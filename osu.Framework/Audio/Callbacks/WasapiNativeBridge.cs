// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using osu.Framework.Logging;

namespace osu.Framework.Audio.Callbacks
{
    /// <summary>
    /// Supplies the native WASAPI callback from <c>g0v0.Audio.dll</c>, which pulls the mixer's output with
    /// <c>BASS_ChannelGetData</c> without entering the CLR (so garbage collection cannot stall the audio thread)
    /// and taps the played PCM for recording (<see cref="NativeWasapiCapture"/>).
    /// Without that library, <see cref="TryGetNativeProcedure"/> fails and the audio thread uses a managed callback.
    /// </summary>
    public static class WasapiNativeBridge
    {
        private static readonly Lock sync_lock = new Lock();

        private static IntPtr activeProcedure = IntPtr.Zero;

        private static string providerName = "Uninitialized";

        /// <summary>
        /// A human-readable description of the active native callback provider.
        /// </summary>
        public static string ProviderName
        {
            get
            {
                lock (sync_lock)
                    return providerName;
            }
        }

        /// <summary>
        /// Whether the native WASAPI callback procedure is currently handed out.
        /// </summary>
        public static bool IsNativeActive
        {
            get
            {
                lock (sync_lock)
                    return activeProcedure != IntPtr.Zero;
            }
        }

        /// <summary>
        /// Attempts to obtain the native WASAPI callback procedure pointer and its user context pointer.
        /// </summary>
        /// <param name="procedure">The native function pointer to pass as WASAPIPROC to <c>BASS_WASAPI_Init</c>.</param>
        /// <param name="context">The unmanaged context pointer to pass as user data.</param>
        /// <returns>True if the native procedure is available; false if the managed callback is required.</returns>
        public static bool TryGetNativeProcedure(out IntPtr procedure, out IntPtr context)
        {
            lock (sync_lock)
            {
                procedure = context = IntPtr.Zero;

                if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                {
                    providerName = "Unsupported OS";
                    return false;
                }

                try
                {
                    if (tryGetBassChannelGetData(out IntPtr getData) && NativeWasapiCapture.TryCreate(getData, out procedure, out context))
                    {
                        providerName = "Native PCM capture bridge";
                        activeProcedure = procedure;
                        return true;
                    }
                }
                catch (Exception)
                {
                    // An optional native library must never prevent normal audio initialisation.
                    procedure = context = IntPtr.Zero;
                    Logger.Log("Native WASAPI callback unavailable; using the managed callback.", level: LogLevel.Important);
                }

                providerName = "Managed callback";
                return false;
            }
        }

        /// <summary>
        /// Sets the active BASS mixer channel handle. The native audio callback reads this atomically.
        /// </summary>
        /// <param name="handle">The BASS mixer stream channel handle, or 0 if inactive.</param>
        public static void SetMixerHandle(int handle) => NativeWasapiCapture.SetMixer(handle);

        /// <summary>
        /// Clears the mixer channel handle to 0, causing the native callback to output silence immediately.
        /// Must be called before freeing the BASS stream handle.
        /// </summary>
        public static void Reset() => SetMixerHandle(0);

        /// <summary>
        /// Releases the native callback. Only call once WASAPI has stopped.
        /// </summary>
        public static void Free()
        {
            lock (sync_lock)
            {
                Reset();
                NativeWasapiCapture.FreeAfterWasapiStopped();
                activeProcedure = IntPtr.Zero;
                providerName = "Uninitialized";
            }
        }

        private static bool tryGetBassChannelGetData(out IntPtr address)
        {
            address = IntPtr.Zero;

            // 1. If bass.dll is already loaded in the process
            IntPtr hBassModule = GetModuleHandle("bass.dll");
            if (hBassModule != IntPtr.Zero && NativeLibrary.TryGetExport(hBassModule, "BASS_ChannelGetData", out address))
                return true;

            // 2. Touch ManagedBass to trigger runtime dependency loading
            try
            {
                _ = ManagedBass.Bass.Version;
                hBassModule = GetModuleHandle("bass.dll");
                if (hBassModule != IntPtr.Zero && NativeLibrary.TryGetExport(hBassModule, "BASS_ChannelGetData", out address))
                    return true;
            }
            catch
            {
            }

            // 3. Search common runtime candidate paths
            string rid = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "win-x64",
                Architecture.Arm64 => "win-arm64",
                Architecture.X86 => "win-x86",
                _ => "win-x64"
            };

            string[] paths =
            {
                "bass",
                "bass.dll",
                System.IO.Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", "bass.dll"),
                System.IO.Path.Combine(AppContext.BaseDirectory, "bass.dll")
            };

            foreach (string p in paths)
            {
                if (NativeLibrary.TryLoad(p, typeof(WasapiNativeBridge).Assembly, DllImportSearchPath.SafeDirectories, out IntPtr h) || NativeLibrary.TryLoad(p, out h))
                {
                    if (NativeLibrary.TryGetExport(h, "BASS_ChannelGetData", out address))
                        return true;
                }
            }

            return false;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}
