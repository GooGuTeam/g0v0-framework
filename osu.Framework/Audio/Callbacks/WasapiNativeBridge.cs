// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using osu.Framework.Logging;

namespace osu.Framework.Audio.Callbacks
{
    /// <summary>
    /// Manages an inlined unmanaged WASAPI callback procedure that executes entirely in native machine code,
    /// directly invoking BASS stream mixing (<c>BASS_ChannelGetData</c>) without entering the .NET CLR.
    /// This eliminates Garbage Collection Stop-The-World (STW) pauses on the real-time audio thread,
    /// preventing buffer underruns, crackles, and stuttering.
    /// </summary>
    public static class WasapiNativeBridge
    {
        private static readonly object sync_lock = new object();

        private static IntPtr userContext = IntPtr.Zero;
        private static IntPtr nativeProcedure = IntPtr.Zero;

        private static string providerName = "Uninitialized";
        private static bool isInitialized;

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
        /// Whether an inlined native WASAPI callback procedure is currently active and available.
        /// </summary>
        public static bool IsNativeActive
        {
            get
            {
                lock (sync_lock)
                    return nativeProcedure != IntPtr.Zero;
            }
        }

        /// <summary>
        /// Attempts to obtain an inlined native WASAPI callback procedure pointer and its user context pointer.
        /// </summary>
        /// <param name="procedure">The native function pointer to pass as WASAPIPROC to <c>BASS_WASAPI_Init</c>.</param>
        /// <param name="context">The unmanaged context pointer to pass as user data.</param>
        /// <returns>True if a native procedure was successfully prepared; false if fallback to managed delegate is required.</returns>
        public static bool TryGetNativeProcedure(out IntPtr procedure, out IntPtr context)
        {
            lock (sync_lock)
            {
                if (isInitialized && nativeProcedure != IntPtr.Zero)
                {
                    procedure = nativeProcedure;
                    context = userContext;
                    return true;
                }

                if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                {
                    procedure = IntPtr.Zero;
                    context = IntPtr.Zero;
                    providerName = "Unsupported OS";
                    return false;
                }

                if (userContext == IntPtr.Zero)
                {
                    userContext = Marshal.AllocHGlobal(sizeof(int));
                    Marshal.WriteInt32(userContext, 0);
                }

                if (tryCreateInlinedNativeThunk(out nativeProcedure, out string archName))
                {
                    isInitialized = true;
                    procedure = nativeProcedure;
                    context = userContext;
                    providerName = $"Inlined Native Machine Code ({archName})";
                    Logger.Log($"WASAPI native callback bridge activated ({providerName})");
                    return true;
                }

                providerName = "Managed Delegate Fallback";
                procedure = IntPtr.Zero;
                context = IntPtr.Zero;
                isInitialized = true;
                Logger.Log("WASAPI inlined native bridge unavailable; falling back to managed delegate", level: LogLevel.Important);
                return false;
            }
        }

        /// <summary>
        /// Sets the active BASS mixer channel handle. The native audio callback reads this atomically.
        /// </summary>
        /// <param name="handle">The BASS mixer stream channel handle, or 0 if inactive.</param>
        public static void SetMixerHandle(int handle)
        {
            IntPtr ctx = userContext;
            if (ctx != IntPtr.Zero)
            {
                Marshal.WriteInt32(ctx, handle);
                Thread.MemoryBarrier();
            }
        }

        /// <summary>
        /// Atomically clears the mixer channel handle to 0, causing the native callback to output silence immediately.
        /// Must be called before freeing the BASS stream handle.
        /// </summary>
        public static void Reset()
        {
            SetMixerHandle(0);
        }

        /// <summary>
        /// Releases all unmanaged resources and executable memory allocations.
        /// </summary>
        public static void Free()
        {
            lock (sync_lock)
            {
                Reset();

                if (nativeProcedure != IntPtr.Zero)
                {
                    VirtualFree(nativeProcedure, UIntPtr.Zero, MEM_RELEASE);
                    nativeProcedure = IntPtr.Zero;
                }

                if (userContext != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(userContext);
                    userContext = IntPtr.Zero;
                }

                providerName = "Uninitialized";
                isInitialized = false;
            }
        }

        private static bool tryCreateInlinedNativeThunk(out IntPtr proc, out string archName)
        {
            proc = IntPtr.Zero;
            archName = string.Empty;

            if (!tryGetBassChannelGetData(out IntPtr bassChannelGetData))
                return false;

            byte[]? machineCode = null;

            switch (RuntimeInformation.ProcessArchitecture)
            {
                case Architecture.X64:
                    archName = "x64";
                    machineCode = generateX64Thunk(bassChannelGetData);
                    break;

                case Architecture.Arm64:
                    archName = "arm64";
                    machineCode = generateArm64Thunk(bassChannelGetData);
                    break;

                case Architecture.X86:
                    archName = "x86";
                    machineCode = generateX86Thunk(bassChannelGetData);
                    break;

                default:
                    return false;
            }

            if (machineCode == null || machineCode.Length == 0)
                return false;

            // Allocate readable/writable page, copy inlined machine code, then mark as executable/read-only (W^X security)
            IntPtr mem = VirtualAlloc(IntPtr.Zero, (UIntPtr)machineCode.Length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (mem == IntPtr.Zero)
                return false;

            Marshal.Copy(machineCode, 0, mem, machineCode.Length);

            if (!VirtualProtect(mem, (UIntPtr)machineCode.Length, PAGE_EXECUTE_READ, out _))
            {
                VirtualFree(mem, UIntPtr.Zero, MEM_RELEASE);
                return false;
            }

            FlushInstructionCache(GetCurrentProcess(), mem, (UIntPtr)machineCode.Length);

            proc = mem;
            return true;
        }

        private static byte[] generateX64Thunk(IntPtr targetProc)
        {
            // Windows x64 ABI:
            // WasapiProc(void *buffer [RCX], DWORD length [RDX], void *user [R8]) -> DWORD [EAX]
            // BASS_ChannelGetData(DWORD handle [RCX], void *buffer [RDX], DWORD length [R8]) -> DWORD [EAX]
            byte[] code = new byte[]
            {
                0x48, 0x83, 0xEC, 0x28,                         // 0:  sub rsp, 40
                0x4D, 0x85, 0xC0,                               // 4:  test r8, r8
                0x74, 0x2A,                                     // 7:  jz +42 (.zero @ 51)
                0x45, 0x8B, 0x08,                               // 9:  mov r9d, dword ptr [r8]
                0x45, 0x85, 0xC9,                               // 12: test r9d, r9d
                0x74, 0x22,                                     // 15: jz +34 (.zero @ 51)
                0x44, 0x8B, 0xC2,                               // 17: mov r8d, edx
                0x48, 0x8B, 0xD1,                               // 20: mov rdx, rcx
                0x41, 0x8B, 0xC9,                               // 23: mov ecx, r9d
                0x48, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0,             // 26: mov rax, <imm64 targetProc>
                0xFF, 0xD0,                                     // 36: call rax
                0x83, 0xF8, 0xFF,                               // 38: cmp eax, -1
                0x75, 0x02,                                     // 41: jne +2 (.done @ 45)
                0x31, 0xC0,                                     // 43: xor eax, eax
                // 45: .done
                0x48, 0x83, 0xC4, 0x28,                         // 45: add rsp, 40
                0xC3,                                           // 49: ret
                0xCC,                                           // 50: int3 padding
                // 51: .zero
                0x31, 0xC0,                                     // 51: xor eax, eax
                0x48, 0x83, 0xC4, 0x28,                         // 53: add rsp, 40
                0xC3                                            // 57: ret
            };

            Array.Copy(BitConverter.GetBytes((long)targetProc), 0, code, 28, 8);
            return code;
        }

        private static byte[] generateArm64Thunk(IntPtr targetProc)
        {
            // Windows ARM64 ABI:
            // WasapiProc(void *buffer [X0], DWORD length [W1], void *user [X2]) -> DWORD [W0]
            // BASS_ChannelGetData(DWORD handle [W0], void *buffer [X1], DWORD length [W2]) -> DWORD [W0]
            byte[] code = new byte[]
            {
                0x02, 0x01, 0x00, 0xB4,                         // 0:  cbz x2, +32 (.zero @ 32 bytes forward)
                0x43, 0x00, 0x40, 0xB9,                         // 4:  ldr w3, [x2]
                0xC3, 0x00, 0x00, 0x34,                         // 8:  cbz w3, +24 (.zero @ 24 bytes forward)
                0xFD, 0x7B, 0xBF, 0xA9,                         // 12: stp x29, x30, [sp, #-16]!
                0xFD, 0x03, 0x00, 0x91,                         // 16: mov x29, sp
                0xE2, 0x03, 0x01, 0x2A,                         // 20: mov w2, w1 (length)
                0xE1, 0x03, 0x00, 0xAA,                         // 24: mov x1, x0 (buffer)
                0xE0, 0x03, 0x03, 0x2A,                         // 28: mov w0, w3 (handle)
                0x10, 0x01, 0x00, 0x58,                         // 32: ldr x16, +32 (.target_addr @ 64)
                0x00, 0x02, 0x3F, 0xD6,                         // 36: blr x16
                0x1F, 0x04, 0x00, 0x31,                         // 40: cmn w0, #1
                0xE0, 0x03, 0x80, 0x1A,                         // 44: csel w0, wzr, w0, eq
                0xFD, 0x7B, 0xC1, 0xA8,                         // 48: ldp x29, x30, [sp], #16
                0xC0, 0x03, 0x5F, 0xD6,                         // 52: ret
                // 56: .zero
                0xE0, 0x03, 0x1F, 0x2A,                         // 56: mov w0, wzr
                0xC0, 0x03, 0x5F, 0xD6,                         // 60: ret
                // 64: .target_addr (8 bytes imm64)
                0, 0, 0, 0, 0, 0, 0, 0
            };

            Array.Copy(BitConverter.GetBytes((long)targetProc), 0, code, 64, 8);
            return code;
        }

        private static byte[] generateX86Thunk(IntPtr targetProc)
        {
            // Windows x86 stdcall ABI:
            // WasapiProc(void *buffer, DWORD length, void *user) -> ret 12
            // BASS_ChannelGetData(handle, buffer, length) -> stdcall
            byte[] code = new byte[]
            {
                0x8B, 0x44, 0x24, 0x0C,                         // 0:  mov eax, [esp + 12] (user)
                0x85, 0xC0,                                     // 4:  test eax, eax
                0x74, 0x1E,                                     // 6:  jz .zero (+30 -> 38)
                0x8B, 0x08,                                     // 8:  mov ecx, [eax] (handle)
                0x85, 0xC9,                                     // 10: test ecx, ecx
                0x74, 0x18,                                     // 12: jz .zero (+24 -> 38)
                0xFF, 0x74, 0x24, 0x08,                         // 14: push [esp + 8] (length)
                0xFF, 0x74, 0x24, 0x08,                         // 18: push [esp + 8] (buffer)
                0x51,                                           // 22: push ecx (handle)
                0xB8, 0, 0, 0, 0,                               // 23: mov eax, <targetProc imm32>
                0xFF, 0xD0,                                     // 28: call eax
                0x83, 0xF8, 0xFF,                               // 30: cmp eax, -1
                0x75, 0x02,                                     // 33: jne +2 (.done @ 37)
                0x31, 0xC0,                                     // 35: xor eax, eax
                // 37: .done
                0xC2, 0x0C, 0x00,                               // 37: ret 12
                // 40: .zero
                0x31, 0xC0,                                     // 40: xor eax, eax
                0xC2, 0x0C, 0x00                                // 42: ret 12
            };

            Array.Copy(BitConverter.GetBytes(targetProc.ToInt32()), 0, code, 24, 4);
            return code;
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
                if (NativeLibrary.TryLoad(p, typeof(WasapiNativeBridge).Assembly, DllImportSearchPath.SafeDirectories, out IntPtr h) ||
                    NativeLibrary.TryLoad(p, out h))
                {
                    if (NativeLibrary.TryGetExport(h, "BASS_ChannelGetData", out address))
                        return true;
                }
            }

            return false;
        }

        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint MEM_RELEASE = 0x8000;
        private const uint PAGE_READWRITE = 0x04;
        private const uint PAGE_EXECUTE_READ = 0x20;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualFree(IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, UIntPtr dwSize);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}
