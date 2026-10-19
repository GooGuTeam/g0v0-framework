// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace osu.Framework.Graphics.Video
{
    /// <summary>
    /// RTMP publishing transport with certificate-validated TLS for RTMPS. Credentials
    /// remain in memory and exceptions never contain the publishing address or key.
    /// </summary>
    internal sealed class RtmpConnection : IDisposable
    {
        private readonly TcpClient client = new TcpClient();
        private readonly SemaphoreSlim writeLock = new SemaphoreSlim(1);
        private readonly Dictionary<int, ChunkState> chunks = new Dictionary<int, ChunkState>();
        private Stream stream = null!;
        private int inputChunkSize = 128;
        private const int output_chunk_size = 4096;
        private uint streamId;
        private uint acknowledgementWindow = 2500000;
        private long bytesRead;
        private long acknowledged;
        private int bufferedBytes;
        private bool disposed;

        public static void ValidateDestination(string destination)
        {
            if (!Uri.TryCreate(destination, UriKind.Absolute, out var uri)
                || (uri.Scheme != @"rtmp" && uri.Scheme != @"rtmps")
                || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)
                || uri.AbsolutePath.LastIndexOf('/') <= 0 || uri.AbsolutePath.EndsWith('/'))
                throw new ArgumentException(@"Enter an RTMP or RTMPS publishing URL containing an application and stream key.");
        }

        public async Task ConnectAsync(string destination, CancellationToken token)
        {
            ValidateDestination(destination);
            var uri = new Uri(destination);
            int port = uri.IsDefaultPort ? (uri.Scheme == @"rtmps" ? 443 : 1935) : uri.Port;
            await client.ConnectAsync(uri.Host, port, token).ConfigureAwait(false);
            client.NoDelay = true;
            stream = client.GetStream();
            if (uri.Scheme == @"rtmps")
            {
                var tls = new SslStream(stream, false);
                stream = tls;
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = uri.Host }, token).ConfigureAwait(false);
            }
            byte[] hello = new byte[1537];
            hello[0] = 3;
            RandomNumberGenerator.Fill(hello.AsSpan(9));
            await stream.WriteAsync(hello, token).ConfigureAwait(false);
            byte[] response = new byte[3073];
            await stream.ReadExactlyAsync(response, token).ConfigureAwait(false);
            if (response[0] != 3)
                throw new IOException(@"The server selected an unsupported RTMP version.");
            await stream.WriteAsync(response.AsMemory(1, 1536), token).ConfigureAwait(false);

            byte[] chunkSize = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(chunkSize, output_chunk_size);
            // The first control message fits the protocol's initial 128-byte chunk size.
            await sendAsync(1, 0, 0, chunkSize, token).ConfigureAwait(false);
            int separator = uri.AbsolutePath.LastIndexOf('/');
            string app = Uri.UnescapeDataString(uri.AbsolutePath[1..separator]);
            string key = Uri.UnescapeDataString(uri.AbsolutePath[(separator + 1)..]) + uri.Query;
            string tcUrl = uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath[..separator];
            await sendCommandAsync(0, token, @"connect", 1.0, new Dictionary<string, object?>
            {
                [@"app"] = app,
                [@"type"] = @"nonprivate",
                [@"tcUrl"] = tcUrl,
                [@"flashVer"] = @"FMLE/3.0 (compatible; g0v0)",
                [@"fpad"] = false,
                [@"capabilities"] = 15.0,
                [@"audioCodecs"] = 1024.0,
                [@"videoCodecs"] = 128.0,
                [@"videoFunction"] = 1.0,
                [@"objectEncoding"] = 0.0,
            }).ConfigureAwait(false);
            await waitForResultAsync(1, token).ConfigureAwait(false);
            await sendCommandAsync(0, token, @"createStream", 2.0, null).ConfigureAwait(false);
            var result = await waitForResultAsync(2, token).ConfigureAwait(false);
            if (result.Count < 4 || result[3] is not double id || id < 1 || id > uint.MaxValue || id != Math.Truncate(id))
                throw new IOException(@"The server returned an invalid publishing stream.");
            streamId = (uint)id;
            await sendCommandAsync(streamId, token, @"publish", 0.0, null, key, @"live").ConfigureAwait(false);
            while (true)
            {
                var command = await nextCommandAsync(token).ConfigureAwait(false);
                if (command.Count > 3 && command[0] is string name && name == @"onStatus" && command[3] is Dictionary<string, object?> status)
                {
                    if (status.TryGetValue(@"code", out var code) && code is @"NetStream.Publish.Start")
                        break;
                    if (status.TryGetValue(@"level", out var level) && level is @"error")
                        throw new IOException(@"The server rejected the publishing request.");
                }
            }
        }

        public Task SendMediaAsync(bool video, uint timestamp, byte[] data, CancellationToken token)
            => sendAsync(video ? (byte)9 : (byte)8, streamId, timestamp, data, token);

        public async Task ReceiveControlAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var command = await nextCommandAsync(token).ConfigureAwait(false);
                if (command.Count > 3 && command[3] is Dictionary<string, object?> status
                    && status.TryGetValue(@"level", out var level) && level is @"error")
                    throw new IOException(@"The publishing session was rejected by the server.");
            }
        }

        private async Task<List<object?>> waitForResultAsync(double transaction, CancellationToken token)
        {
            while (true)
            {
                var command = await nextCommandAsync(token).ConfigureAwait(false);
                if (command.Count > 1 && command[1] is double id && id == transaction)
                {
                    if (command[0] is @"_error")
                        throw new IOException(@"The server rejected the RTMP command.");
                    if (command[0] is @"_result")
                        return command;
                }
            }
        }

        private async Task<List<object?>> nextCommandAsync(CancellationToken token)
        {
            while (true)
            {
                var (type, data) = await readMessageAsync(token).ConfigureAwait(false);
                switch (type)
                {
                    case 1 when data.Length == 4:
                        int size = BinaryPrimitives.ReadInt32BigEndian(data);
                        if (size < 1 || size > 1024 * 1024)
                            throw new IOException(@"Invalid RTMP chunk size.");
                        inputChunkSize = size;
                        break;
                    case 2 when data.Length == 4:
                        if (chunks.TryGetValue(BinaryPrimitives.ReadInt32BigEndian(data), out var aborted))
                        {
                            bufferedBytes -= aborted.Buffer.Length;
                            aborted.Buffer = Array.Empty<byte>();
                            aborted.Offset = 0;
                        }
                        break;
                    case 5 when data.Length == 4:
                        acknowledgementWindow = BinaryPrimitives.ReadUInt32BigEndian(data);
                        break;
                    case 6 when data.Length == 5:
                        await sendAsync(5, 0, 0, data[..4], token).ConfigureAwait(false);
                        break;
                    case 4 when data.Length >= 6 && BinaryPrimitives.ReadUInt16BigEndian(data) == 6:
                        byte[] pong = data[..6];
                        BinaryPrimitives.WriteUInt16BigEndian(pong, 7);
                        await sendAsync(4, 0, 0, pong, token).ConfigureAwait(false);
                        break;
                    case 17:
                    case 20:
                        using (var input = new MemoryStream(data, false))
                        {
                            if (type == 17 && input.ReadByte() != 0)
                                throw new IOException(@"AMF3 commands are unsupported.");
                            var values = new List<object?>();
                            while (input.Position < input.Length)
                                values.Add(readAmf(input, 0));
                            return values;
                        }
                }
            }
        }

        private async Task<(byte Type, byte[] Data)> readMessageAsync(CancellationToken token)
        {
            byte[] header = new byte[11];
            while (true)
            {
                await readAsync(header.AsMemory(0, 1), token).ConfigureAwait(false);
                int format = header[0] >> 6;
                int channel = header[0] & 63;
                if (channel < 2)
                {
                    int count = channel == 0 ? 1 : 2;
                    await readAsync(header.AsMemory(0, count), token).ConfigureAwait(false);
                    channel = 64 + header[0] + (count == 2 ? header[1] * 256 : 0);
                }
                if (!chunks.TryGetValue(channel, out var chunk))
                {
                    if (format != 0 || chunks.Count >= 64)
                        throw new IOException(@"Invalid RTMP chunk stream.");
                    chunks.Add(channel, chunk = new ChunkState());
                }
                bool continuation = chunk.Buffer.Length != 0 && chunk.Offset < chunk.Buffer.Length;
                if (continuation && format != 3)
                    throw new IOException(@"An RTMP message was interrupted without an abort.");
                int headerLength = format == 0 ? 11 : format == 1 ? 7 : format == 2 ? 3 : 0;
                if (headerLength > 0)
                {
                    await readAsync(header.AsMemory(0, headerLength), token).ConfigureAwait(false);
                    uint time = readUInt24(header, 0);
                    chunk.Extended = time == 0xffffff;
                    if (chunk.Extended)
                    {
                        byte[] extended = new byte[4];
                        await readAsync(extended, token).ConfigureAwait(false);
                        time = BinaryPrimitives.ReadUInt32BigEndian(extended);
                    }
                    if (format == 0) { chunk.Time = time; chunk.Delta = time; }
                    else { chunk.Delta = time; chunk.Time += time; }
                    if (format <= 1)
                    {
                        chunk.Length = (int)readUInt24(header, 3);
                        chunk.Type = header[6];
                    }
                }
                else
                {
                    if (chunk.Extended)
                        await readAsync(header.AsMemory(0, 4), token).ConfigureAwait(false);
                    if (!continuation)
                        chunk.Time += chunk.Delta;
                }
                if (!continuation)
                {
                    if (chunk.Length > 16 * 1024 * 1024 || bufferedBytes + chunk.Length > 32 * 1024 * 1024)
                        throw new IOException(@"RTMP input exceeded the buffering limit.");
                    chunk.Buffer = new byte[chunk.Length];
                    chunk.Offset = 0;
                    bufferedBytes += chunk.Length;
                }
                int take = Math.Min(inputChunkSize, chunk.Length - chunk.Offset);
                await readAsync(chunk.Buffer.AsMemory(chunk.Offset, take), token).ConfigureAwait(false);
                chunk.Offset += take;
                if (chunk.Offset == chunk.Length)
                {
                    byte[] data = chunk.Buffer;
                    bufferedBytes -= data.Length;
                    chunk.Buffer = Array.Empty<byte>();
                    return (chunk.Type, data);
                }
            }
        }

        private async Task readAsync(Memory<byte> data, CancellationToken token)
        {
            await stream.ReadExactlyAsync(data, token).ConfigureAwait(false);
            bytesRead += data.Length;
            if (acknowledgementWindow > 0 && bytesRead - acknowledged >= acknowledgementWindow)
            {
                byte[] ack = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(ack, unchecked((uint)bytesRead));
                await sendAsync(3, 0, 0, ack, token).ConfigureAwait(false);
                acknowledged = bytesRead;
            }
        }

        private async Task sendAsync(byte type, uint messageStream, uint timestamp, byte[] payload, CancellationToken token)
        {
            if (payload.Length > 0xffffff)
                throw new IOException(@"RTMP output packet is too large.");
            await writeLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                byte[] header = new byte[16];
                // Protocol control messages must use chunk stream 2 and message stream 0.
                header[0] = type <= 6 ? (byte)2 : type is 8 or 9 ? (byte)6 : (byte)3;
                writeUInt24(header, 1, Math.Min(timestamp, 0xffffff));
                writeUInt24(header, 4, (uint)payload.Length);
                header[7] = type;
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), messageStream);
                int length = 12;
                if (timestamp >= 0xffffff) { BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), timestamp); length = 16; }
                await stream.WriteAsync(header.AsMemory(0, length), timeout.Token).ConfigureAwait(false);
                for (int offset = 0; offset < payload.Length; offset += output_chunk_size)
                {
                    if (offset > 0)
                    {
                        header[0] |= 0xc0;
                        if (timestamp >= 0xffffff)
                            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1), timestamp);
                        await stream.WriteAsync(header.AsMemory(0, timestamp >= 0xffffff ? 5 : 1), timeout.Token).ConfigureAwait(false);
                    }
                    await stream.WriteAsync(payload.AsMemory(offset, Math.Min(output_chunk_size, payload.Length - offset)), timeout.Token).ConfigureAwait(false);
                }
            }
            finally { writeLock.Release(); }
        }

        private Task sendCommandAsync(uint id, CancellationToken token, params object?[] values)
        {
            using var output = new MemoryStream();
            foreach (var value in values) writeAmf(output, value);
            return sendAsync(20, id, 0, output.ToArray(), token);
        }

        private static void writeAmf(Stream output, object? value)
        {
            switch (value)
            {
                case null: output.WriteByte(5); break;
                case string text:
                    output.WriteByte(2); writeString(output, text); break;
                case double number:
                    output.WriteByte(0);
                    byte[] bytes = new byte[8]; BinaryPrimitives.WriteDoubleBigEndian(bytes, number); output.Write(bytes); break;
                case bool boolean: output.WriteByte(1); output.WriteByte(boolean ? (byte)1 : (byte)0); break;
                case Dictionary<string, object?> obj:
                    output.WriteByte(3);
                    foreach (var field in obj) { writeString(output, field.Key); writeAmf(output, field.Value); }
                    output.Write(new byte[] { 0, 0, 9 }); break;
                default: throw new InvalidOperationException(@"Unsupported AMF value.");
            }
        }

        private static void writeString(Stream output, string value)
        {
            byte[] data = Encoding.UTF8.GetBytes(value);
            if (data.Length > ushort.MaxValue) throw new ArgumentException(@"RTMP string is too long.");
            output.WriteByte((byte)(data.Length >> 8)); output.WriteByte((byte)data.Length); output.Write(data);
        }

        private static object? readAmf(Stream input, int depth)
        {
            if (depth > 16) throw new IOException(@"AMF nesting limit exceeded.");
            byte[] bytes = new byte[8];
            switch (input.ReadByte())
            {
                case 0: input.ReadExactly(bytes); return BinaryPrimitives.ReadDoubleBigEndian(bytes);
                case 1: return input.ReadByte() == 1;
                case 2: return readString(input);
                case 5:
                case 6: return null;
                case 8: input.ReadExactly(bytes.AsSpan(0, 4)); goto case 3;
                case 3:
                    var obj = new Dictionary<string, object?>();
                    while (obj.Count < 1024)
                    {
                        string name = readString(input);
                        if (name.Length == 0)
                        {
                            if (input.ReadByte() != 9) throw new IOException(@"Invalid AMF object terminator.");
                            return obj;
                        }
                        obj[name] = readAmf(input, depth + 1);
                    }
                    throw new IOException(@"AMF object limit exceeded.");
                default: throw new IOException(@"Unsupported AMF command value.");
            }
        }

        private static string readString(Stream input)
        {
            byte[] length = new byte[2]; input.ReadExactly(length);
            byte[] text = new byte[BinaryPrimitives.ReadUInt16BigEndian(length)]; input.ReadExactly(text);
            return Encoding.UTF8.GetString(text);
        }

        private static uint readUInt24(byte[] data, int offset) => (uint)(data[offset] << 16 | data[offset + 1] << 8 | data[offset + 2]);
        private static void writeUInt24(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)(value >> 16); data[offset + 1] = (byte)(value >> 8); data[offset + 2] = (byte)value;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            stream?.Dispose(); client.Dispose();
        }

        private sealed class ChunkState
        {
            public uint Time;
            public uint Delta;
            public bool Extended;
            public byte Type;
            public int Length;
            public int Offset;
            public byte[] Buffer = Array.Empty<byte>();
        }
    }
}
