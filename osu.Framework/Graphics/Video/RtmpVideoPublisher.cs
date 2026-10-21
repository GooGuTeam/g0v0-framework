// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace osu.Framework.Graphics.Video
{
    /// <summary>
    /// Bounded compressed-packet publisher. Congestion discards the dependent GOP and
    /// resumes at an IDR. No network operation runs on the encoder callback thread.
    /// </summary>
    public sealed class RtmpVideoPublisher : IAsyncDisposable
    {
        private readonly object gate = new object();
        private readonly Queue<EncodedVideoPacket> queue = new Queue<EncodedVideoPacket>();
        private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly Task worker;
        private readonly Action requestKeyFrame;
        private readonly TaskCompletionSource started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[]? videoConfig;
        private byte[]? audioConfig;
        private long queuedBytes;
        private bool waitingForKeyFrame = true;
        private int generation;
        private bool stopped;
        private int state;
        private int disposed;

        /// <summary>0: connecting, 1: publishing, 2: reconnecting, 3: stopped.</summary>
        public int State => Volatile.Read(ref state);
        public Task Started => started.Task;
        public Task Completion => worker;
        public long DroppedPackets { get; private set; }

        public RtmpVideoPublisher(string destination, Action requestKeyFrame)
        {
            RtmpConnection.ValidateDestination(destination);
            ArgumentNullException.ThrowIfNull(requestKeyFrame);
            this.requestKeyFrame = requestKeyFrame;
            worker = Task.Run(() => runAsync(destination));
        }

        public void Enqueue(EncodedVideoPacket packet)
        {
            bool request = false;
            lock (gate)
            {
                if (stopped) return;
                if (packet.IsConfiguration)
                {
                    if (packet.Data.Length > 65536)
                        throw new InvalidOperationException(@"Codec configuration exceeds the size limit.");
                    byte[] next = packet.Data.ToArray();
                    byte[]? previous = packet.IsVideo ? videoConfig : audioConfig;
                    if (previous != null && !previous.AsSpan().SequenceEqual(next))
                    {
                        resetQueue();
                        request = true;
                    }
                    if (packet.IsVideo) videoConfig = next;
                    else audioConfig = next;
                }
                else
                {
                    if (packet.Data.Length > 8 * 1024 * 1024)
                    {
                        resetQueue();
                        DroppedPackets++;
                        requestKeyFrame();
                        return;
                    }
                    if (queuedBytes + packet.Data.Length > 8 * 1024 * 1024 || queue.Count >= 512
                        || (queue.TryPeek(out var first) && packet.DecodeTime - first.DecodeTime > TimeSpan.FromSeconds(2).Ticks))
                    {
                        resetQueue();
                        request = true;
                    }
                    if (waitingForKeyFrame)
                    {
                        if (packet.IsVideo && packet.IsKeyFrame && audioConfig != null)
                            waitingForKeyFrame = false;
                        else
                        {
                            DroppedPackets++;
                            if (request) requestKeyFrame();
                            return;
                        }
                    }
                    queue.Enqueue(packet);
                    queuedBytes += packet.Data.Length;
                    wake.Writer.TryWrite(true);
                }
            }
            if (request) requestKeyFrame();
        }

        private void resetQueue()
        {
            DroppedPackets += queue.Count;
            queue.Clear();
            queuedBytes = 0;
            waitingForKeyFrame = true;
            generation++;
        }

        private async Task runAsync(string destination)
        {
            CancellationToken token = cancellation.Token;
            int failures = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    using var connection = new RtmpConnection();
                    using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
                    Task? control = null;
                    try
                    {
                        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                            await connection.ConnectAsync(destination, connectTimeout.Token).ConfigureAwait(false);
                        }
                        control = connection.ReceiveControlAsync(session.Token);
                        lock (gate) resetQueue();
                        requestKeyFrame();
                        long epoch = -1;
                        long waitingSince = Environment.TickCount64;
                        long lastVideo = -1, lastAudio = -1;
                        int currentGeneration = -1;
                        while (!token.IsCancellationRequested)
                        {
                            EncodedVideoPacket? packet = null;
                            byte[]? vconfig;
                            byte[]? aconfig;
                            int nextGeneration;
                            lock (gate)
                            {
                                if (queue.TryDequeue(out packet)) queuedBytes -= packet.Data.Length;
                                vconfig = videoConfig; aconfig = audioConfig; nextGeneration = generation;
                            }
                            if (control.IsCompleted)
                                await control.ConfigureAwait(false);
                            if (packet == null)
                            {
                                if (Environment.TickCount64 - waitingSince > 15000)
                                    throw new IOException(@"No decodable media arrived from the encoder.");
                                using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                                idleTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                                var available = wake.Reader.ReadAsync(idleTimeout.Token).AsTask();
                                Task completed = await Task.WhenAny(available, control).ConfigureAwait(false);
                                if (completed == control)
                                {
                                    session.Cancel();
                                    try { await available.ConfigureAwait(false); } catch (OperationCanceledException) { }
                                    await control.ConfigureAwait(false);
                                    throw new IOException(@"The streaming control channel closed.");
                                }
                                await available.ConfigureAwait(false);
                                continue;
                            }
                            waitingSince = Environment.TickCount64;
                            if (nextGeneration != currentGeneration)
                            {
                                if (!packet.IsVideo || !packet.IsKeyFrame || aconfig == null) continue;
                                byte[] avcc = createAvcConfiguration(vconfig, packet.Data.Span);
                                epoch = epoch < 0 ? Math.Max(0, packet.DecodeTime) : epoch;
                                uint timestamp = timestampFor(packet.DecodeTime, epoch);
                                await connection.SendMediaAsync(true, timestamp, videoTag(avcc, true, 0, 0), token).ConfigureAwait(false);
                                await connection.SendMediaAsync(false, timestamp, audioTag(aconfig, true), token).ConfigureAwait(false);
                                currentGeneration = nextGeneration;
                            }
                            if (packet.DecodeTime < epoch) continue;
                            if (packet.IsVideo)
                            {
                                if (packet.DecodeTime < lastVideo) throw new IOException(@"Video decode timestamps moved backwards.");
                                lastVideo = packet.DecodeTime;
                                long composition = (packet.PresentationTime - packet.DecodeTime) / 10000;
                                if (composition < -8388608 || composition > 8388607) throw new IOException(@"Video composition offset is out of range.");
                                byte[] payload = convertNals(packet.Data.Span);
                                await connection.SendMediaAsync(true, timestampFor(packet.DecodeTime, epoch),
                                    videoTag(payload, packet.IsKeyFrame, 1, (int)composition), token).ConfigureAwait(false);
                                Volatile.Write(ref state, 1);
                                started.TrySetResult();
                                failures = 0;
                            }
                            else
                            {
                                if (packet.DecodeTime < lastAudio) throw new IOException(@"Audio decode timestamps moved backwards.");
                                lastAudio = packet.DecodeTime;
                                await connection.SendMediaAsync(false, timestampFor(packet.DecodeTime, epoch), audioTag(packet.Data.ToArray(), false), token).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (Exception) when (!token.IsCancellationRequested)
                    {
                        // Never forward transport exceptions: providers may include credentials.
                        Volatile.Write(ref state, 2);
                        lock (gate) resetQueue();
                        if (++failures >= 5)
                            throw new IOException(@"Unable to publish after five connection attempts. Check the server, key and network connection.");
                    }
                    finally
                    {
                        session.Cancel();
                        connection.Dispose();
                        if (control != null)
                        {
                            try { await control.ConfigureAwait(false); }
                            catch (Exception) { }
                        }
                    }
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, failures * 2)), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                started.TrySetCanceled(token);
            }
            catch (Exception)
            {
                var error = new IOException(@"Live streaming stopped. Check the server, stream key and network connection.");
                started.TrySetException(error);
                throw error;
            }
            finally
            {
                Volatile.Write(ref state, 3);
                lock (gate) { stopped = true; resetQueue(); }
            }
        }

        private static uint timestampFor(long time, long epoch) => unchecked((uint)((time - epoch) / 10000));

        private static byte[] audioTag(byte[] packet, bool configuration)
        {
            byte[] tag = new byte[packet.Length + 2];
            tag[0] = 0xaf; tag[1] = configuration ? (byte)0 : (byte)1;
            packet.CopyTo(tag, 2); return tag;
        }

        private static byte[] videoTag(byte[] packet, bool key, byte type, int composition)
        {
            byte[] tag = new byte[packet.Length + 5];
            tag[0] = key ? (byte)0x17 : (byte)0x27; tag[1] = type;
            tag[2] = (byte)(composition >> 16); tag[3] = (byte)(composition >> 8); tag[4] = (byte)composition;
            packet.CopyTo(tag, 5); return tag;
        }

        private static byte[] createAvcConfiguration(byte[]? configuration, ReadOnlySpan<byte> keyFrame)
        {
            if (configuration is { Length: >= 7 } && configuration[0] == 1)
            {
                if ((configuration[4] & 3) != 3) throw new IOException(@"Unsupported AVC NAL length size.");
                return configuration;
            }
            var nals = splitNals(configuration ?? Array.Empty<byte>());
            nals.AddRange(splitNals(keyFrame));
            byte[]? sps = nals.FirstOrDefault(n => (n[0] & 31) == 7);
            byte[]? pps = nals.FirstOrDefault(n => (n[0] & 31) == 8);
            if (sps == null || pps == null || sps.Length < 4 || sps.Length > ushort.MaxValue || pps.Length > ushort.MaxValue)
                throw new IOException(@"The encoder did not supply AVC decoder configuration.");
            using var result = new MemoryStream();
            result.Write(new byte[] { 1, sps[1], sps[2], sps[3], 0xff, 0xe1, (byte)(sps.Length >> 8), (byte)sps.Length });
            result.Write(sps);
            result.Write(new byte[] { 1, (byte)(pps.Length >> 8), (byte)pps.Length });
            result.Write(pps); return result.ToArray();
        }

        private static byte[] convertNals(ReadOnlySpan<byte> data)
        {
            using var output = new MemoryStream();
            byte[] length = new byte[4];
            foreach (byte[] nal in splitNals(data))
            {
                BinaryPrimitives.WriteInt32BigEndian(length, nal.Length);
                output.Write(length); output.Write(nal);
            }
            return output.ToArray();
        }

        private static List<byte[]> splitNals(ReadOnlySpan<byte> data)
        {
            var result = new List<byte[]>();
            if (data.Length == 0) return result;
            if (startCode(data, 0) != 0)
            {
                int start = 0;
                while (start < data.Length)
                {
                    int code = startCode(data, start);
                    int end = start + code;
                    while (end < data.Length && startCode(data, end) == 0) end++;
                    int nalEnd = end;
                    while (nalEnd > start + code && data[nalEnd - 1] == 0) nalEnd--;
                    if (nalEnd > start + code) result.Add(data.Slice(start + code, nalEnd - start - code).ToArray());
                    start = end;
                }
            }
            else
            {
                int offset = 0;
                while (offset < data.Length)
                {
                    if (data.Length - offset < 4) throw new IOException(@"Truncated AVC packet.");
                    int size = BinaryPrimitives.ReadInt32BigEndian(data[offset..]); offset += 4;
                    if (size <= 0 || size > data.Length - offset) throw new IOException(@"Invalid AVC NAL length.");
                    result.Add(data.Slice(offset, size).ToArray()); offset += size;
                }
            }
            return result;
        }

        private static int startCode(ReadOnlySpan<byte> data, int offset)
        {
            if (offset + 3 <= data.Length && data[offset] == 0 && data[offset + 1] == 0)
            {
                if (data[offset + 2] == 1) return 3;
                if (offset + 4 <= data.Length && data[offset + 2] == 0 && data[offset + 3] == 1) return 4;
            }
            return 0;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            cancellation.Cancel();
            try { await worker.ConfigureAwait(false); }
            finally { cancellation.Dispose(); }
        }
    }
}
