// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace osu.Framework.Platform
{
    public static class ProcessorDiagnostics
    {
        /// <summary>
        /// Reads native CPU diagnostics independently of image codec initialisation.
        /// </summary>
        public static string GetDescription()
        {
            try
            {
                byte[] buffer = new byte[16384];
                if (g0_cpu_diagnostics(buffer, (nuint)buffer.Length) == 0)
                    return @"cpu_features: diagnostic query failed.";
                int length = Array.IndexOf(buffer, (byte)0);
                return length < 0 ? @"cpu_features: invalid diagnostic response." : Encoding.UTF8.GetString(buffer, 0, length);
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                return $@"cpu_features: unavailable ({exception.GetType().Name}).";
            }
        }

        [DllImport("g0v0-image-codecs", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int g0_cpu_diagnostics([Out] byte[] buffer, nuint capacity);
    }
}
