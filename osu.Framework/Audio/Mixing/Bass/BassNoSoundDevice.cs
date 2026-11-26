// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using ManagedBass;

namespace osu.Framework.Audio.Mixing.Bass
{
    /// <summary>
    /// Coordinates ownership of BASS's process-wide no-sound device between offline mixers and live audio.
    /// </summary>
    internal static class BassNoSoundDevice
    {
        private static readonly Lock sync = new Lock();
        private static int offlineUsers;
        private static bool releaseWhenUnused;

        public static void AcquireForOfflineMixing()
        {
            lock (sync)
            {
                if (offlineUsers == 0)
                {
                    releaseWhenUnused = ManagedBass.Bass.Init(ManagedBass.Bass.NoSoundDevice);
                    if (!releaseWhenUnused && ManagedBass.Bass.LastError != Errors.Already)
                        throw new InvalidOperationException($"Unable to initialise the no-sound audio device: {ManagedBass.Bass.LastError}");
                }

                ManagedBass.Bass.CurrentDevice = ManagedBass.Bass.NoSoundDevice;
                offlineUsers++;
            }
        }

        public static void ReleaseFromOfflineMixing()
        {
            lock (sync)
            {
                if (--offlineUsers == 0 && releaseWhenUnused)
                {
                    free();
                    releaseWhenUnused = false;
                }
            }
        }

        public static bool InitialiseForPlayback(DeviceInitFlags flags)
        {
            lock (sync)
            {
                if (!ManagedBass.Bass.Init(ManagedBass.Bass.NoSoundDevice, Flags: flags) && ManagedBass.Bass.LastError != Errors.Already)
                    return false;

                releaseWhenUnused = false;
                return true;
            }
        }

        public static void ReleaseFromPlayback()
        {
            lock (sync)
            {
                if (offlineUsers == 0)
                    free();
                else
                    releaseWhenUnused = true;
            }
        }

        private static void free()
        {
            if (!ManagedBass.Bass.GetDeviceInfo(ManagedBass.Bass.NoSoundDevice, out var device) || !device.IsInitialized)
                return;

            ManagedBass.Bass.CurrentDevice = ManagedBass.Bass.NoSoundDevice;
            ManagedBass.Bass.Free();
        }
    }
}
