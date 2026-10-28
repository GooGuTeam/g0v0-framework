// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using osu.Framework.Logging;

namespace osu.Framework.Testing.Benchmarking
{
    internal static partial class BenchmarkProcessPriority
    {
        private const int process_power_throttling = 4;
        private const uint power_throttling_execution_speed = 0x1;
        private const uint power_throttling_ignore_timer_resolution = 0x4;

        /// <summary>
        /// Asks the system to run this process at full speed and on the same kind of core every time.
        /// Besides a higher priority, on Windows this opts out of the power throttling (EcoQoS) applied to processes without a visible window,
        /// and keeps the process on the fastest cores of hybrid processors, where runs otherwise differ by which cores they happen to get.
        /// </summary>
        public static void Raise()
        {
            try
            {
                using (var process = Process.GetCurrentProcess())
                    process.PriorityClass = ProcessPriorityClass.High;
            }
            catch (Exception e)
            {
                Logger.Log($"The benchmark's priority could not be raised: {e.Message}");
            }

            if (!OperatingSystem.IsWindows())
                return;

            var state = new PowerThrottlingState
            {
                Version = 1,
                ControlMask = power_throttling_execution_speed | power_throttling_ignore_timer_resolution,
                StateMask = 0,
            };

            if (!SetProcessInformation(GetCurrentProcess(), process_power_throttling, ref state, Marshal.SizeOf<PowerThrottlingState>()))
                Logger.Log($"Power throttling could not be turned off for the benchmark (error {Marshal.GetLastPInvokeError()}).");

            useFastestCores();
        }

        private static unsafe void useFastestCores()
        {
            GetSystemCpuSetInformation(null, 0, out uint length, GetCurrentProcess(), 0);

            if (length == 0)
                return;

            byte[] buffer = new byte[length];
            var sets = new List<(uint Id, byte EfficiencyClass)>();

            fixed (byte* data = buffer)
            {
                if (!GetSystemCpuSetInformation(data, length, out length, GetCurrentProcess(), 0))
                    return;

                // SYSTEM_CPU_SET_INFORMATION: Size, Type, then for CPU sets Id at 8 and EfficiencyClass at 18.
                for (uint offset = 0; offset + 19 <= length;)
                {
                    uint size = *(uint*)(data + offset);

                    if (size == 0)
                        break;

                    if (*(uint*)(data + offset + 4) == 0)
                        sets.Add((*(uint*)(data + offset + 8), data[offset + 18]));

                    offset += size;
                }
            }

            if (sets.Count == 0)
                return;

            byte fastest = sets.Max(s => s.EfficiencyClass);

            // Processors with one kind of core have nothing to choose.
            if (sets.All(s => s.EfficiencyClass == fastest))
                return;

            uint[] ids = sets.Where(s => s.EfficiencyClass == fastest).Select(s => s.Id).ToArray();

            fixed (uint* idData = ids)
            {
                if (!SetProcessDefaultCpuSets(GetCurrentProcess(), idData, (uint)ids.Length)
                    || !SetThreadSelectedCpuSets(GetCurrentThread(), idData, (uint)ids.Length))
                    Logger.Log($"The benchmark could not be kept to the fastest cores (error {Marshal.GetLastPInvokeError()}).");
                else
                    Logger.Log($"The benchmark runs on the {ids.Length} fastest of {sets.Count} logical processors.");
            }
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState information, int size);

        [LibraryImport("kernel32.dll")]
        private static partial IntPtr GetCurrentProcess();

        [LibraryImport("kernel32.dll")]
        private static partial IntPtr GetCurrentThread();

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool GetSystemCpuSetInformation(byte* information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool SetProcessDefaultCpuSets(IntPtr process, uint* cpuSetIds, uint cpuSetIdCount);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool SetThreadSelectedCpuSets(IntPtr thread, uint* cpuSetIds, uint cpuSetIdCount);

        [StructLayout(LayoutKind.Sequential)]
        private struct PowerThrottlingState
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }
    }
}
