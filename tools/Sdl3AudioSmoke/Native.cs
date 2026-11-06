using System.Runtime.InteropServices;

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
