// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Statistics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ManagedBass;
using ManagedBass.Mix;
using ManagedBass.Wasapi;
using osu.Framework.Audio;
using osu.Framework.Bindables;
using osu.Framework.Development;
using osu.Framework.Logging;
using osu.Framework.Platform.Linux.Native;
using ManagedBass.Fx;

namespace osu.Framework.Threading
{
    public class AudioThread : GameThread
    {
        public AudioThread()
            : base(name: "Audio")
        {
            OnNewFrame += onNewFrame;
            PreloadBass();
        }

        public override bool IsCurrent => ThreadSafety.IsAudioThread;

        internal sealed override void MakeCurrent()
        {
            base.MakeCurrent();

            ThreadSafety.IsAudioThread = true;
        }

        internal override IEnumerable<StatisticsCounterType> StatisticsCounters => new[]
        {
            StatisticsCounterType.TasksRun,
            StatisticsCounterType.Tracks,
            StatisticsCounterType.Samples,
            StatisticsCounterType.SChannels,
            StatisticsCounterType.Components,
            StatisticsCounterType.MixChannels,
        };

        private readonly List<AudioManager> managers = new List<AudioManager>();

        private static readonly HashSet<int> initialised_devices = new HashSet<int>();

        private static readonly GlobalStatistic<double> cpu_usage = GlobalStatistics.Get<double>("Audio", "Bass CPU%");

        private long frameCount;

        private void onNewFrame()
        {
            if (frameCount++ % 1000 == 0)
                cpu_usage.Value = Bass.CPUUsage;

            lock (managers)
            {
                for (int i = 0; i < managers.Count; i++)
                {
                    var m = managers[i];
                    m.Update();
                }
            }
        }

        internal void RegisterManager(AudioManager manager)
        {
            lock (managers)
            {
                if (managers.Contains(manager))
                    throw new InvalidOperationException($"{manager} was already registered");

                managers.Add(manager);
            }

            manager.GlobalMixerHandle.BindTo(globalMixerHandle);
        }

        internal void UnregisterManager(AudioManager manager)
        {
            lock (managers)
                managers.Remove(manager);

            manager.GlobalMixerHandle.UnbindFrom(globalMixerHandle);
        }

        protected override void OnExit()
        {
            base.OnExit();

            lock (managers)
            {
                // AudioManagers are iterated over backwards since disposal will unregister and remove them from the list.
                for (int i = managers.Count - 1; i >= 0; i--)
                {
                    var m = managers[i];

                    m.Dispose();

                    // Audio component disposal (including the AudioManager itself) is scheduled and only runs when the AudioThread updates.
                    // But the AudioThread won't run another update since it's exiting, so an update must be performed manually in order to finish the disposal.
                    m.Update();
                }

                managers.Clear();
            }

            // Safety net to ensure we have freed all devices before exiting.
            // This is mainly required for device-lost scenarios.
            // See https://github.com/ppy/osu-framework/pull/3378 for further discussion.
            foreach (int d in initialised_devices.ToArray())
                FreeDevice(d);
        }

        #region BASS Initialisation

        // TODO: All this bass init stuff should probably not be in this class.

        private WasapiProcedure? wasapiProcedure;
        private WasapiNotifyProcedure? wasapiNotifyProcedure;

        /// <summary>
        /// If a global mixer is being used, this will be the BASS handle for it.
        /// If non-null, all game mixers should be added to this mixer.
        /// </summary>
        private readonly Bindable<int?> globalMixerHandle = new Bindable<int?>();

        internal bool InitDevice(int deviceId, bool useWasapi, bool isExclusive, double buffer, double period)
        {
            Debug.Assert(ThreadSafety.IsAudioThread);
            Trace.Assert(deviceId != -1); // The real device ID should always be used, as the -1 device has special cases which are hard to work with.

            // Always free any active WASAPI session before reinitialising BASS.
            // If WASAPI was running in exclusive mode, holding the device prevents BASS from opening or re-initialising it.
            freeWasapi();

            // Try to initialise the device, or request a re-initialise.
            if (!Bass.Init(deviceId, Flags: (DeviceInitFlags)128) && Bass.LastError != Errors.Already) // 128 == BASS_DEVICE_REINIT
                return false;

            if (useWasapi)
            {
                if (!attemptWasapiInitialisation(deviceId, isExclusive, buffer, period))
                    return false;
            }

            initialised_devices.Add(deviceId);
            return true;
        }

        internal void FreeDevice(int deviceId)
        {
            Debug.Assert(ThreadSafety.IsAudioThread);

            int selectedDevice = Bass.CurrentDevice;

            if (canSelectDevice(deviceId))
            {
                Bass.CurrentDevice = deviceId;
                Bass.Free();
            }

            freeWasapi();

            if (selectedDevice != deviceId && canSelectDevice(selectedDevice))
                Bass.CurrentDevice = selectedDevice;

            initialised_devices.Remove(deviceId);

            static bool canSelectDevice(int deviceId) => Bass.GetDeviceInfo(deviceId, out var deviceInfo) && deviceInfo.IsInitialized;
        }

        /// <summary>
        /// Makes BASS available to be consumed.
        /// </summary>
        internal static void PreloadBass()
        {
            if (RuntimeInfo.OS == RuntimeInfo.Platform.Linux)
            {
                // required for the time being to address libbass_fx.so load failures (see https://github.com/ppy/osu/issues/2852)
                Library.Load("libbass.so", Library.LoadFlags.RTLD_LAZY | Library.LoadFlags.RTLD_GLOBAL);
            }
        }

        private bool attemptWasapiInitialisation(int deviceId, bool isExclusive, double buffer, double period)
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                return false;

            Logger.Log("Attempting local BassWasapi initialisation");

            int wasapiDevice = -1;

            // WASAPI device indices don't match normal BASS devices.
            // Each device is listed multiple times with each supported channel/frequency pair.
            //
            // Working backwards to find the correct device is how bass does things internally (see BassWasapi.GetBassDevice).
            if (deviceId > 0)
            {
                string driver = Bass.GetDeviceInfo(deviceId).Driver;

                if (!string.IsNullOrEmpty(driver))
                {
                    // In the normal execution case, BassWasapi.GetDeviceInfo will return false as soon as we reach the end of devices.
                    // This while condition is just a safety to avoid looping forever.
                    // It's intentionally quite high because if a user has many audio devices, this list can get long.
                    //
                    // Retrieving device info here isn't free. In the future we may want to investigate a better method.
                    int dev = -1;
                    while (dev < 16384)
                    {
                        if (!BassWasapi.GetDeviceInfo(++dev, out WasapiDeviceInfo info))
                            break;

                        if (info.ID == driver)
                        {
                            wasapiDevice = dev;
                            break;
                        }
                    }
                }
            }

            // To keep things in a sane state let's only keep one device initialised via wasapi.
            freeWasapi();
            return initWasapi(wasapiDevice, isExclusive, buffer, period);
        }

        private bool initWasapi(int wasapiDevice, bool isExclusive, double buffer, double period)
        {
            // This is intentionally initialised inline and stored to a field.
            // If we don't do this, it gets GC'd away.
            wasapiProcedure = (buffer, length, _) =>
            {
                if (globalMixerHandle.Value == null)
                    return 0;

                return Bass.ChannelGetData(globalMixerHandle.Value!.Value, buffer, length);
            };
            wasapiNotifyProcedure = (notify, device, _) => Scheduler.Add(() =>
            {
                if (notify == WasapiNotificationType.DefaultOutput)
                {
                    freeWasapi();
                    initWasapi(device, isExclusive, buffer, period);
                }
            });
            float v = (float)period;

            // In shared mode, period should be float.Epsilon when 0 to request the minimum period.
            // In exclusive mode, 0 requests the minimum supported period by the driver.
            if (period < 1e-5)
                v = isExclusive ? 0f : float.Epsilon;

            bool initialised = BassWasapi.Init(
                wasapiDevice,
                Procedure: wasapiProcedure,
                Flags: WasapiInitFlags.EventDriven | WasapiInitFlags.AutoFormat | (isExclusive ? WasapiInitFlags.Exclusive : 0),
                Buffer: (float)buffer,
                Period: v
            );

            Logger.Log($"Initialising BassWasapi for device {wasapiDevice}...{(initialised ? "success!" : $"FAILED ({Bass.LastError})")}");

            if (!initialised && isExclusive)
            {
                Logger.Log($"Initialising BassWasapi in exclusive mode failed ({Bass.LastError}), retrying in shared mode...", level: LogLevel.Important);
                freeWasapi();

                initialised = BassWasapi.Init(
                    wasapiDevice,
                    Procedure: wasapiProcedure,
                    Flags: WasapiInitFlags.EventDriven | WasapiInitFlags.AutoFormat,
                    Buffer: 0f,
                    Period: float.Epsilon
                );
            }

            if (!initialised)
                return false;

            BassWasapi.GetInfo(out var wasapiInfo);
            globalMixerHandle.Value = BassMix.CreateMixerStream(wasapiInfo.Frequency, wasapiInfo.Channels, BassFlags.MixerNonStop | BassFlags.Decode | BassFlags.Float);
            BassWasapi.Start();

            BassWasapi.SetNotify(wasapiNotifyProcedure);
            return true;
        }

        private void freeWasapi()
        {
            if (globalMixerHandle.Value != null)
            {
                // The mixer probably doesn't need to be recycled. Just keeping things sane for now.
                Bass.StreamFree(globalMixerHandle.Value.Value);
                globalMixerHandle.Value = null;
            }

            BassWasapi.Stop();
            BassWasapi.Free();
        }

        #endregion
    }
}
