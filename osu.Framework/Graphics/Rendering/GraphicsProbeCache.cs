// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using osu.Framework.Logging;
using osu.Framework.Platform;

namespace osu.Framework.Graphics.Rendering
{
    /// <summary>
    /// Runs device probes once per device and driver, and caches their results.
    /// A probe that crashes the process leaves a marker, so it is not retried automatically.
    /// </summary>
    internal sealed class GraphicsProbeCache
    {
        private const int version = 1;
        private readonly Storage storage;

        public GraphicsProbeCache(Storage storage)
        {
            this.storage = storage;
        }

        public BindlessTextureProbeResult GetOrProbe(string fingerprint, Func<BindlessTextureProbeResult> probe)
            => GetOrProbe("bindless", fingerprint, probe,
                saved => Enum.IsDefined(saved.Status) && !string.IsNullOrEmpty(saved.Details),
                () => new BindlessTextureProbeResult(BindlessTextureProbeStatus.Interrupted, "Previous probe did not finish; clear graphics probe cache to retry."),
                reason => new BindlessTextureProbeResult(BindlessTextureProbeStatus.Unknown, reason),
                e => new BindlessTextureProbeResult(BindlessTextureProbeStatus.Failed, e.Message));

        /// <param name="kind">Distinguishes probes in file names.</param>
        /// <param name="fingerprint">Identifies the device and driver. A different fingerprint runs the probe again.</param>
        /// <param name="probe">The probe.</param>
        /// <param name="isValid">Whether a saved result can be used.</param>
        /// <param name="interrupted">The result when a previous run of the probe did not finish.</param>
        /// <param name="skipped">The result when the probe cannot run safely, given the reason.</param>
        /// <param name="failed">The result when the probe throws.</param>
        public T GetOrProbe<T>(string kind, string fingerprint, Func<T> probe, Func<T, bool> isValid, Func<T> interrupted, Func<string, T> skipped, Func<Exception, T> failed)
            where T : class
        {
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{version}|{Environment.OSVersion}|{IntPtr.Size}|{fingerprint}")));
            string filename = $"{kind}-{key}.json";
            string pending = $"{kind}-{key}.pending";

            try
            {
                if (storage.Exists(filename))
                {
                    using var reader = new StreamReader(storage.GetStream(filename));
                    var saved = JsonConvert.DeserializeObject<T>(reader.ReadToEnd());
                    if (saved != null && isValid(saved))
                        return saved;
                }
            }
            catch (Exception e)
            {
                Logger.Log($"Graphics probe cache could not be read: {e.Message}");
            }

            // Do not retry an interrupted driver call automatically. Clear the cache to retry.
            if (storage.Exists(pending))
                return interrupted();

            try
            {
                using var marker = storage.CreateFileSafely(pending);
                marker.WriteByte(1);
            }
            catch (Exception e)
            {
                // Running without a durable marker could crash on every launch.
                return skipped($"Probe skipped because its recovery marker could not be saved: {e.Message}");
            }

            T result;

            try
            {
                result = probe();
            }
            catch (Exception e)
            {
                result = failed(e);
            }

            try
            {
                using (var writer = new StreamWriter(storage.CreateFileSafely(filename)))
                    writer.Write(JsonConvert.SerializeObject(result));
                storage.Delete(pending);
            }
            catch (Exception e)
            {
                Logger.Log($"Graphics probe result could not be saved: {e.Message}");
            }

            return result;
        }

        /// <summary>
        /// The probe kinds used with <see cref="GetOrProbe{T}"/>, for <see cref="Clear"/>.
        /// </summary>
        private static readonly string[] kinds = { "bindless", "texture-size" };

        public void Clear()
        {
            foreach (string kind in kinds)
            {
                foreach (string file in storage.GetFiles(string.Empty, $"{kind}-*"))
                    storage.Delete(file);
            }
        }
    }
}
