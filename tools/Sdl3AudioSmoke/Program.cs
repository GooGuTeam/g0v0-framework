using System.Runtime.InteropServices;

// Standalone control-plane probe. All mixing runs in SDL's native device thread.
// Run on the main thread; all control calls are serialized here.
if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: Sdl3AudioSmoke <audio-file> [sample-frames=256] [driver=wasapi|default] [gc-stress]");
    return 2;
}
int frames = args.Length > 1 ? int.Parse(args[1]) : 256;
IntPtr engine = Native.g0_audio_create(frames, args.Length > 2 && args[2] == "default" ? 0 : 1);
if (engine == IntPtr.Zero) throw new InvalidOperationException(Native.Error);
try
{
    int voice = Native.g0_audio_load(engine, Path.GetFullPath(args[0]));
    if (voice < 0) throw new InvalidOperationException(Native.Error);
    if (Native.g0_audio_gain(engine, voice, 0.05f) == 0 || Native.g0_audio_play(engine, voice) == 0)
        throw new InvalidOperationException(Native.Error);
    Console.WriteLine($"Driver={Marshal.PtrToStringUTF8(Native.g0_audio_driver())}; requestedFrames={frames}; actualFrames={Native.g0_audio_device_frames(engine)}");
    // Deliberately block the managed control thread. Native callbacks must continue.
    Thread.Sleep(500);
    int before = Native.g0_audio_callbacks(engine);
    if (args.Contains("gc-stress"))
    {
        for (int i = 0; i < 20; ++i)
        {
            _ = new byte[8 * 1024 * 1024];
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
    }
    Thread.Sleep(1000);
    int after = Native.g0_audio_callbacks(engine);
    Console.WriteLine($"Native callbacks: {before} -> {after}; source position={Native.g0_audio_position(engine, voice):F3}s (not a DAC clock)");
    if (after <= before) throw new InvalidOperationException("Native audio callbacks did not advance.");
    if (Native.g0_audio_seek(engine, voice, 0.25) == 0 || Native.g0_audio_frequency(engine, voice, 1.1f) == 0)
        throw new InvalidOperationException(Native.Error);
    Thread.Sleep(250);
    if (Native.g0_audio_stop(engine, voice) == 0) throw new InvalidOperationException(Native.Error);
    return 0;
}
finally
{
    Native.g0_audio_destroy(engine);
}

internal static class Native
{
    private const string library = "g0_audio";
    internal static string Error => Marshal.PtrToStringUTF8(g0_audio_error()) ?? "Native audio failure";
    [DllImport(library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g0_audio_create(int frames, int wasapi);
    [DllImport(library, CallingConvention = CallingConvention.Cdecl)] internal static extern void g0_audio_destroy(IntPtr engine);
    [DllImport(library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g0_audio_error();
    [DllImport(library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g0_audio_driver();
    [DllImport(library, CallingConvention = CallingConvention.Cdecl)] internal static extern int g0_audio_load(IntPtr engine, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(library, CallingConvention = CallingConvention.Cdecl)] internal static extern int g0_audio_play(IntPtr engine, int voice);
    [DllImport(library, CallingConvention = CallingConvention.Cdecl)] internal static extern int g0_audio_stop(IntPtr engine, int voice);
    [DllImport(library, CallingConvention = CallingConvention.Cdecl)] internal static extern int g0_audio_gain(IntPtr engine, int voice, float gain);
    [DllImport(library, CallingConvention = CallingConvention.Cdecl)] internal static extern int g0_audio_frequency(IntPtr engine, int voice, float ratio);
    [DllImport(library, CallingConvention = CallingConvention.Cdecl)] internal static extern int g0_audio_seek(IntPtr engine, int voice, double seconds);
    [DllImport(library, CallingConvention = CallingConvention.Cdecl)] internal static extern double g0_audio_position(IntPtr engine, int voice);
    [DllImport(library, CallingConvention = CallingConvention.Cdecl)] internal static extern int g0_audio_callbacks(IntPtr engine);
    [DllImport(library, CallingConvention = CallingConvention.Cdecl)] internal static extern int g0_audio_device_frames(IntPtr engine);
}
