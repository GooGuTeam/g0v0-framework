// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using OpenTabletDriver;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Platform.Pointer;
using OpenTabletDriver.Plugin.Tablet;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Input.StateChanges;
using osu.Framework.Platform;
using osu.Framework.Statistics;
using osuTK;

namespace osu.Framework.Input.Handlers.Tablet
{
    public class OpenTabletDriverHandler : InputHandler, IAbsolutePointer, IRelativePointer, IPressureHandler, ITabletHandler
    {
        private static readonly GlobalStatistic<ulong> statistic_total_events = GlobalStatistics.Get<ulong>(StatisticGroupFor<OpenTabletDriverHandler>(), "Total events");

        private TabletDriver? tabletDriver;

        private InputDeviceTree? device;

        private AbsoluteOutputMode outputMode = null!;

        private GameHost host = null!;

        public override string Description => "Tablet";

        public override bool IsActive => tabletDriver != null;

        public Bindable<Vector2> AreaOffset { get; } = new Bindable<Vector2>();

        public Bindable<Vector2> AreaSize { get; } = new Bindable<Vector2>();

        public Bindable<Vector2> OutputAreaOffset { get; } = new Bindable<Vector2>(new Vector2(0.5f, 0.5f));

        public Bindable<Vector2> OutputAreaSize { get; } = new Bindable<Vector2>(new Vector2(1f, 1f));

        public Bindable<float> Rotation { get; } = new Bindable<float>();

        public BindableFloat PressureThreshold { get; } = new BindableFloat
        {
            MinValue = 0f,
            MaxValue = 1f,
            Precision = 0.005f,
        };

        public IBindable<TabletInfo?> Tablet => tablet;

        private readonly Bindable<TabletInfo?> tablet = new Bindable<TabletInfo?>();

        private Task? lastInitTask;

        private IBindable<bool> windowActive = null!;

        public override bool Initialize(GameHost host)
        {
            this.host = host;

            outputMode = new AbsoluteTabletMode(this);

            host.Window.Resized += () => updateOutputArea(host.Window);
            windowActive = host.Window.IsActive.GetBoundCopy();
            windowActive.BindValueChanged(_ => resetFilters());
            PositionSmoothing.BindValueChanged(_ => resetFilters());
            ButtonDebounce.BindValueChanged(_ => resetFilters());
            RadialSmoothingStrength.BindValueChanged(_ => resetFilters());

            AreaOffset.BindValueChanged(_ => updateTabletAndInputArea(device));
            AreaSize.BindValueChanged(_ => updateTabletAndInputArea(device));
            Rotation.BindValueChanged(_ => updateTabletAndInputArea(device), true);

            OutputAreaOffset.BindValueChanged(_ => updateOutputArea(host.Window));
            OutputAreaSize.BindValueChanged(_ => updateOutputArea(host.Window), true);

            Enabled.BindValueChanged(enabled =>
            {
                resetFilters();
                reportRate.Reset();
                if (enabled.NewValue)
                {
                    lastInitTask = Task.Run(() =>
                    {
                        tabletDriver = TabletDriver.Create();
                        tabletDriver.PostLog = Log;
                        tabletDriver.TabletsChanged += handleTabletsChanged;
                        tabletDriver.DeviceReported += handleDeviceReported;
                        tabletDriver.Detect();
                    });
                }
                else
                {
                    lastInitTask?.WaitSafely();

                    if (tabletDriver != null)
                    {
                        tabletDriver.DeviceReported -= handleDeviceReported;
                        tabletDriver.TabletsChanged -= handleTabletsChanged;
                        tabletDriver.Dispose();
                        tabletDriver = null;
                    }
                }
            }, true);

            return true;
        }

        private TabletPenDeviceType lastTabletDeviceType = TabletPenDeviceType.Unknown;

        void IAbsolutePointer.SetPosition(System.Numerics.Vector2 pos)
        {
            lastTabletDeviceType = TabletPenDeviceType.Unknown;
            if (!float.IsFinite(pos.X) || !float.IsFinite(pos.Y))
                return;
            lock (filterSync)
            {
                if (!Enabled.Value || !windowActive.Value)
                    return;
                var position = filter.FilterPosition(new Vector2(pos.X, pos.Y), reportTime, PositionSmoothing.Value,
                    millimetresPerPixel.X > 0 && millimetresPerPixel.Y > 0 ? RadialSmoothingStrength.Value : 0,
                    millimetresPerPixel);
                enqueueInput(new MousePositionAbsoluteInputFromPen { Position = position, DeviceType = lastTabletDeviceType });
            }
        }

        void IRelativePointer.SetPosition(System.Numerics.Vector2 delta)
        {
            lastTabletDeviceType = TabletPenDeviceType.Indirect;
            enqueueInput(new MousePositionRelativeInputFromPen { Delta = new Vector2(delta.X, delta.Y), DeviceType = lastTabletDeviceType });
        }

        public BindableDouble PositionSmoothing { get; } = new BindableDouble { MinValue = 0, MaxValue = 20, Precision = 0.1 };
        public BindableDouble ButtonDebounce { get; } = new BindableDouble { MinValue = 0, MaxValue = 30, Precision = 0.1 };

        public BindableDouble RadialSmoothingStrength { get; } = new BindableDouble { MinValue = 0, MaxValue = 1, Precision = 0.01 };

        private readonly TabletReportRate reportRate = new TabletReportRate();

        /// <summary>
        /// Pen reports received during the last second, with 10 ms bucket resolution.
        /// </summary>
        public int GetReportRate() => Enabled.Value && IsActive ? reportRate.Read() : 0;

        private Vector2 millimetresPerPixel;

        private readonly object filterSync = new object();
        private readonly TabletInputFilter filter = new TabletInputFilter();
        private static double reportTime => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

        public override void CollectPendingInputs(List<IInput> inputs)
        {
            lock (filterSync)
            {
                if (!Enabled.Value || !windowActive.Value)
                {
                    filter.Reset(emitButton);
                    penPressed = false;
                }
                else
                    filter.Flush(reportTime, ButtonDebounce.Value, emitButton);
                base.CollectPendingInputs(inputs);
            }
        }

        private void resetFilters()
        {
            lock (filterSync)
            {
                filter.Reset(emitButton);
                penPressed = false;
            }
        }

        private void emitButton(int key, bool pressed)
        {
            IInput input = key == -1
                ? new MouseButtonInputFromPen(pressed) { DeviceType = lastTabletDeviceType }
                : key < 256
                    ? new TabletPenButtonInput((TabletPenButton)key, pressed)
                    : new TabletAuxiliaryButtonInput((TabletAuxiliaryButton)(key - 256), pressed);
            // Releases must reach the input manager even after focus loss or disconnection.
            PendingInputs.Enqueue(input);
        }

        private void filterButton(int key, bool pressed)
        {
            lock (filterSync)
            {
                if (!Enabled.Value || !windowActive.Value)
                    return;
                double now = reportTime;
                filter.SetButton(key, pressed, now);
                filter.Flush(now, ButtonDebounce.Value, emitButton);
            }
        }

        private bool penPressed;

        void IPressureHandler.SetPressure(float pressure)
        {
            lock (filterSync)
            {
                if (Enabled.Value && windowActive.Value)
                    handlePressure(pressure);
            }
        }

        private void handlePressure(float pressure)
        {
            // Most important for edge cases where users have pressure set to 0 or 1 and tablets can report fuzzy data.
            const float hysteresis_half = 0.02f;

            if (!float.IsFinite(pressure))
                return;
            pressure = Math.Clamp(pressure, 0f, 1f);

            float releaseThreshold = PressureThreshold.Value - hysteresis_half;
            float pressThreshold = PressureThreshold.Value + hysteresis_half;

            // keep press..release threshold range constant for edge cases.
            if (releaseThreshold < 0f)
            {
                pressThreshold = hysteresis_half * 2;
                releaseThreshold = 0f;
            }
            else if (pressThreshold > 1f)
            {
                releaseThreshold = 1 - (hysteresis_half * 2);
                pressThreshold = 1f;
            }

            setPressed(penPressed
                ? pressure > releaseThreshold
                : pressure > pressThreshold);
        }

        private void handleTabletsChanged(object? sender, IEnumerable<TabletReference> tablets)
        {
            lock (filterSync)
            {
                filter.Reset(emitButton);
                penPressed = false;
            }
            reportRate.Reset();
            device = tablets.Any() ? tabletDriver?.InputDevices.First() : null;

            if (device != null)
            {
                // Important to reinitialise outputMode here as the previous one is likely disposed.
                device.OutputMode = outputMode = new AbsoluteTabletMode(this);
                outputMode.Tablet = device.CreateReference();

                updateTabletAndInputArea(device);
                updateOutputArea(host.Window);
            }
            else
            {
                // Ensure we don't leave the simulated mouse button pressed if the tablet disappears.
                setPressed(false);
                tablet.Value = null;
            }
        }

        private void setPressed(bool pressed)
        {
            // Importantly, only fire input when the state changes.
            // If we fire more often, this may intefere with users that click with mouse but use tablet for positional input (hovering).
            if (pressed == penPressed)
                return;

            filterButton(-1, pressed);
            penPressed = pressed;
        }

        private void handleDeviceReported(object? sender, IDeviceReport report)
        {
            if (report is ITabletReport tabletReport)
            {
                if (Enabled.Value)
                    reportRate.Record();
                handleTabletReport(tabletReport);
            }

            if (report is IAuxReport auxiliaryReport)
                handleAuxiliaryReport(auxiliaryReport);
        }

        private void updateOutputArea(IWindow window)
        {
            updateFilterMapping(window);
            if (device == null)
                return;

            switch (device.OutputMode)
            {
                case AbsoluteOutputMode absoluteOutputMode:
                {
                    Vector2 windowSize = new Vector2(window.ClientSize.Width, window.ClientSize.Height);
                    Vector2 scaledSize = windowSize * OutputAreaSize.Value;
                    Vector2 offsetFromCenter = (OutputAreaOffset.Value - new Vector2(0.5f, 0.5f)) * (windowSize - scaledSize);
                    Vector2 position = (windowSize / 2) + offsetFromCenter;

                    absoluteOutputMode.Output = new Area
                    {
                        Width = scaledSize.X,
                        Height = scaledSize.Y,
                        Position = position.ToSystemNumerics()
                    };
                    break;
                }
            }
        }

        private void updateTabletAndInputArea(InputDeviceTree? inputDevice)
        {
            if (inputDevice == null)
                return;

            var digitizer = inputDevice.Properties.Specifications.Digitizer;
            float inputWidth = digitizer.Width;
            float inputHeight = digitizer.Height;

            AreaSize.Default = new Vector2(inputWidth, inputHeight);

            // if it's clear the user has not configured the area, take the full area from the tablet that was just found.
            if (AreaSize.Value == Vector2.Zero)
                AreaSize.SetDefault();

            AreaOffset.Default = new Vector2(inputWidth / 2, inputHeight / 2);

            // likewise with the position, use the centre point if it has not been configured.
            // it's safe to assume no user would set their centre point to 0,0 for now.
            if (AreaOffset.Value == Vector2.Zero)
                AreaOffset.SetDefault();

            tablet.Value = new TabletInfo(inputDevice.Properties.Name, AreaSize.Default);
            updateFilterMapping(host.Window);

            switch (inputDevice.OutputMode)
            {
                case AbsoluteOutputMode absoluteOutputMode:
                {
                    // Set input area in millimeters
                    absoluteOutputMode.Input = new Area
                    {
                        Width = AreaSize.Value.X,
                        Height = AreaSize.Value.Y,
                        Position = new System.Numerics.Vector2(AreaOffset.Value.X, AreaOffset.Value.Y),
                        Rotation = Rotation.Value
                    };
                    break;
                }
            }
        }

        private void updateFilterMapping(IWindow window)
        {
            lock (filterSync)
            {
                float width = window.ClientSize.Width * OutputAreaSize.Value.X;
                float height = window.ClientSize.Height * OutputAreaSize.Value.Y;
                millimetresPerPixel = width > 0 && height > 0
                    ? new Vector2(AreaSize.Value.X / width, AreaSize.Value.Y / height)
                    : Vector2.Zero;
                if (!float.IsFinite(millimetresPerPixel.X) || !float.IsFinite(millimetresPerPixel.Y))
                    millimetresPerPixel = Vector2.Zero;
                filter.ResetPosition();
            }
        }

        private void handleTabletReport(ITabletReport tabletReport)
        {
            for (int i = 0; i < Math.Min(256, tabletReport.PenButtons.Length); i++)
                filterButton(i, tabletReport.PenButtons[i]);
        }

        private void handleAuxiliaryReport(IAuxReport auxiliaryReport)
        {
            for (int i = 0; i < auxiliaryReport.AuxButtons.Length; i++)
                filterButton(256 + i, auxiliaryReport.AuxButtons[i]);
        }

        private void enqueueInput(IInput input)
        {
            if (!windowActive.Value)
                return;

            PendingInputs.Enqueue(input);
            FrameStatistics.Increment(StatisticsCounterType.TabletEvents);
            statistic_total_events.Value++;
        }
    }
}
