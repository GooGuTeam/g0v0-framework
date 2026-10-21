// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Runtime.InteropServices;

namespace osu.Framework.Audio.Callbacks
{
    /// <summary>
    /// Metadata for an interleaved float PCM block from the native WASAPI tap.
    /// SamplePosition counts sample frames, not individual channel samples.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WasapiCaptureBlock
    {
        public ulong SamplePosition;
        public long Qpc;
        public uint Length;
        public uint Frequency;
        public uint Channels;
        private uint reserved;
    }
}
