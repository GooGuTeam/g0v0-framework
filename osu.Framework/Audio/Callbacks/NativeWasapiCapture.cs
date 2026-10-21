// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace osu.Framework.Audio.Callbacks
{
    /// <summary>
    /// Native PCM tap enabled for recording sessions. The audio callback never enters this class or the CLR.
    /// Control and reader calls are serialised; the native producer never takes this lock.
    /// </summary>
    public static unsafe class NativeWasapiCapture
    {
        private static readonly object sync = new object();
        private static IntPtr library;
        private static IntPtr context;
        private static IntPtr procedure;
        private static bool configured;
        private static bool captureRequested;
        private static long generation;

        internal static bool IsConfigured
        {
            get
            {
                lock (sync)
                    return configured;
            }
        }
        private static delegate* unmanaged[Cdecl]<IntPtr, uint, void> setMixer;
        private static delegate* unmanaged[Cdecl]<IntPtr, uint, void> enable;
        private static delegate* unmanaged[Cdecl]<IntPtr, uint, uint, int> configure;
        private static delegate* unmanaged[Cdecl]<IntPtr, void> destroy;
        private static delegate* unmanaged[Cdecl]<IntPtr, void*, uint, WasapiCaptureBlock*, int> read;

        internal static bool TryCreate(IntPtr getData, out IntPtr callback, out IntPtr user)
        {
            lock (sync)
            {
                callback = user = IntPtr.Zero;
                // Only load an explicit absolute path or the deployed application-local library.
                string? path = Environment.GetEnvironmentVariable(@"G0V0_WASAPI_CAPTURE_LIBRARY");
                if (string.IsNullOrEmpty(path))
                    path = Path.Combine(AppContext.BaseDirectory, @"g0v0-wasapi-capture.dll");
                if (context == IntPtr.Zero)
                {
                    if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
                        return false;
                    library = NativeLibrary.Load(path);
                    try
                    {
                        var abi = (delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(library, "g0_capture_abi");
                        if (abi() != 1)
                            throw new NotSupportedException("Unsupported native audio capture ABI.");
                        var create = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)NativeLibrary.GetExport(library, "g0_capture_create");
                        setMixer = (delegate* unmanaged[Cdecl]<IntPtr, uint, void>)NativeLibrary.GetExport(library, "g0_capture_set_mixer");
                        enable = (delegate* unmanaged[Cdecl]<IntPtr, uint, void>)NativeLibrary.GetExport(library, "g0_capture_enable");
                        configure = (delegate* unmanaged[Cdecl]<IntPtr, uint, uint, int>)NativeLibrary.GetExport(library, "g0_capture_configure");
                        destroy = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(library, "g0_capture_destroy");
                        read = (delegate* unmanaged[Cdecl]<IntPtr, void*, uint, WasapiCaptureBlock*, int>)NativeLibrary.GetExport(library, "g0_capture_read");
                        procedure = NativeLibrary.GetExport(library, "g0_capture_callback");
                        context = create(getData);
                        if (context == IntPtr.Zero)
                            throw new InvalidOperationException("Could not allocate the native audio capture ring.");
                    }
                    catch
                    {
                        NativeLibrary.Free(library);
                        library = IntPtr.Zero;
                        throw;
                    }
                }

                callback = procedure;
                user = context;
                return true;
            }
        }

        internal static void SetMixer(int mixer)
        {
            lock (sync)
            {
                if (context != IntPtr.Zero)
                    setMixer(context, (uint)mixer);
            }
        }

        // Must only run before WASAPI starts, or after WASAPI has stopped and joined callbacks.
        internal static void Configure(int frequency, int channels)
        {
            lock (sync)
            {
                if (context == IntPtr.Zero)
                    return;
                if (configure(context, (uint)frequency, (uint)channels) == 0)
                    throw new ArgumentOutOfRangeException(nameof(channels));
                generation++;
                configured = true;
                enable(context, captureRequested ? 1u : 0u);
            }
        }

        internal static void Stopped()
        {
            lock (sync)
            {
                configured = false;
                if (context != IntPtr.Zero)
                    enable(context, 0);
            }
        }

        /// <summary>
        /// Enables capture only on a configured native WASAPI bridge. Playback remains unchanged.
        /// Capture intent survives device changes and is reapplied before the new producer starts.
        /// </summary>
        public static bool SetEnabled(bool enabled)
        {
            lock (sync)
            {
                captureRequested = enabled;
                if (context == IntPtr.Zero || !configured)
                    return false;
                enable(context, enabled ? 1u : 0u);
                return true;
            }
        }

        /// <summary>
        /// Copies one interleaved float PCM block. Use a buffer of at least 65536 bytes.
        /// A generation change identifies a device reset; sample positions can have gaps on overflow.
        /// QPC is the block's mixing completion timestamp, not the device's presentation timestamp.
        /// </summary>
        public static bool TryRead(Span<byte> destination, out WasapiCaptureBlock block, out long deviceGeneration)
        {
            lock (sync)
            {
                block = default;
                deviceGeneration = generation;
                if (context == IntPtr.Zero || !configured)
                    return false;
                WasapiCaptureBlock info;
                fixed (byte* bytes = destination)
                {
                    int result = read(context, bytes, (uint)destination.Length, &info);
                    if (result < 0)
                        throw new ArgumentException("PCM destination is too small.", nameof(destination));
                    if (result == 0)
                        return false;
                }

                block = info;
                return true;
            }
        }

        internal static void FreeAfterWasapiStopped()
        {
            lock (sync)
            {
                configured = false;
                if (context != IntPtr.Zero)
                {
                    destroy(context);
                    context = IntPtr.Zero;
                    NativeLibrary.Free(library);
                    library = procedure = IntPtr.Zero;
                }
            }
        }
    }
}
