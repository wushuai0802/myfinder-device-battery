using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

static class ControlCenterTests {
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static int checks, failures;
    static string output;

    static void Check(bool condition, string message) {
        if (!condition) throw new Exception(message);
        checks++;
    }

    static void Run(string name, Action action) {
        try {
            action();
            Console.WriteLine("PASS " + name);
        } catch (Exception e) {
            failures++;
            Console.Error.WriteLine("FAIL " + name + ": " + (e.InnerException ?? e).Message);
        }
    }

    static object Field(object target, string name) {
        return target.GetType().GetField(name, Private).GetValue(target);
    }

    static void SetField(object target, string name, object value) {
        target.GetType().GetField(name, Private).SetValue(target, value);
    }

    static AudioReading SampleAudio() {
        return new AudioReading {
            TimestampUtc = DateTime.UtcNow,
            SpeakerAvailable = true,
            SpeakerId = "speaker",
            SpeakerName = "桌面扬声器",
            HeadsetAvailable = true,
            HeadsetId = "headset",
            HeadsetName = "有线耳机",
            MicrophoneAvailable = true,
            MicrophoneId = "mic",
            MicrophoneMuted = false,
            ActiveOutput = "speaker",
            VolumeEndpointId = "speaker",
            OutputVolumePercent = 62,
            Devices = new[] { new AudioDeviceChoice { Id = "headset", Name = "有线耳机", Flow = 0, FormFactor = 3 } }
        };
    }

    static DpiConfig SampleDpi() {
        var dpi = new DpiConfig { DeviceKey = MouseBattery.DeviceKey("cc-fixture"), TimestampUtc = DateTime.UtcNow, ActiveStage = 2 };
        foreach (int v in new[] { 400, 800, 1600, 3200 }) dpi.Stages.Add(new DpiStage { XDpi = v, YDpi = v });
        return dpi;
    }

    sealed class Fixture : IDisposable {
        internal readonly TrayContext Owner;
        internal int Saves;
        internal string SettingsFile;

        internal Fixture(string style = "graphite", string theme = "dark") {
            SettingsFile = Path.Combine(output, "cc-settings-" + Guid.NewGuid().ToString("N") + ".json");
            var pref = new AudioBindings { ThemeStyle = style, Theme = theme, PanelLayout = "ring", ReducedMotion = true, RememberReadings = false };
            DeviceSettings.UseTransientConfiguration(pref);
            Owner = new TrayContext(false,
                delegate(bool battery) { return new MouseSnapshot { Battery = new BatteryReading { Percent = 100, TimestampUtc = DateTime.UtcNow }, Dpi = SampleDpi() }; },
                delegate(int stage) { return SampleDpi(); },
                null, SampleAudio, false, null, null, null,
                delegate { return false; },
                null, null, null, null,
                delegate(AudioBindings settings) {
                    Saves++;
                    DeviceSettings.SaveBindings(SettingsFile, settings);
                    DeviceSettings.UseTransientConfiguration(settings);
                });
            DateTime until = DateTime.UtcNow.AddSeconds(5);
            while ((Owner.IsRefreshing || Owner.IsAudioBusy || Owner.Audio == null) && DateTime.UtcNow < until) {
                Application.DoEvents();
                Thread.Sleep(2);
            }
            ((System.Windows.Forms.Timer)Field(Owner, "refresh")).Stop();
            Owner.Current = new BatteryReading { Percent = 100, TimestampUtc = DateTime.UtcNow };
            Owner.Dpi = SampleDpi();
            Owner.Audio = SampleAudio();
            Owner.NativeMousePresent = true;
            Owner.GenericReady = true;
            Owner.WirelessDevices = new[] {
                new WirelessDevice {
                    Id = "dev-headset", Name = "ROG Delta II", Kind = "headset", Transport = "2.4 GHz 接收器",
                    Percent = 39, Fresh = true, Online = true, ConnectionState = "connected", MeasurementState = "current",
                    ConfirmedAtUtc = DateTime.UtcNow.ToString("o"), VerificationLevel = "C"
                },
                new WirelessDevice {
                    Id = "dev-kb", Name = "VGN Neon75 Air", Kind = "keyboard", Transport = "2.4 GHz 接收器",
                    Percent = 12, Fresh = true, Online = true, ConnectionState = "connected", MeasurementState = "current",
                    ConfirmedAtUtc = DateTime.UtcNow.ToString("o"), VerificationLevel = "C", FamilyProbe = true, CanDisable = true
                },
                new WirelessDevice {
                    Id = "dev-sleep", Name = "Sleeping Mouse", Kind = "mouse", Transport = "2.4 GHz",
                    Percent = null, Fresh = false, Online = true, ConnectionState = "connected", MeasurementState = "unavailable"
                }
            };
            SetField(Owner, "unadaptedDevices", new[] {
                new UnadaptedDevice { VendorId = 0x0B05, ProductId = 0x1B7E, Product = "ROG FALCHION ACE 75 HE" }
            });
        }

        public void Dispose() {
            Owner.ExitThread();
            Owner.Dispose();
        }
    }

    static void SingleInstanceAndLifecycle() {
        using (var f = new Fixture()) {
            Check(!f.Owner.ControlCenterOpen, "control center starts closed");
            f.Owner.ShowControlCenter(ControlCenterPage.Devices);
            Application.DoEvents();
            Check(f.Owner.ControlCenterOpen, "control center opens");
            var first = f.Owner.ActiveControlCenter;
            Check(first != null && !first.IsDisposed, "active form reference valid");
            Check(first.Icon != null && first.ShowIcon, "control center assigns a crisp top-left window icon");
            f.Owner.ShowControlCenter(ControlCenterPage.Appearance);
            Application.DoEvents();
            Check(ReferenceEquals(first, f.Owner.ActiveControlCenter), "repeated Show reuses single instance");
            Check(first.CurrentPage == ControlCenterPage.Appearance, "repeated Show switches target page");

            first.Close();
            Application.DoEvents();
            Check(!f.Owner.ControlCenterOpen && f.Owner.ActiveControlCenter == null, "closing releases singleton reference");
            Check(first.IsDisposed, "closing disposes form completely");

            // Updating data/theme while closed must never throw or resurrect the form.
            f.Owner.ConfigurationChanged(false);
            Check(!f.Owner.ControlCenterOpen, "updates while closed keep control center closed");

            // Reopening creates a fresh healthy instance.
            f.Owner.ShowControlCenter(ControlCenterPage.Settings);
            Application.DoEvents();
            Check(f.Owner.ControlCenterOpen && f.Owner.ActiveControlCenter != null && !ReferenceEquals(first, f.Owner.ActiveControlCenter), "reopening creates fresh form");
            Check(f.Owner.ActiveControlCenter.CurrentPage == ControlCenterPage.Settings, "reopened form lands on requested page");
            f.Owner.ActiveControlCenter.Close();
            Application.DoEvents();
        }
    }

    static void DeviceCapabilityGrouping() {
        using (var f = new Fixture()) {
            f.Owner.ShowControlCenter(ControlCenterPage.Devices);
            Application.DoEvents();
            var form = f.Owner.ActiveControlCenter;
            var items = form.BuildDeviceItems();
            var sleep = items.Find(delegate(ControlCenterDeviceItem it) { return it.Key == "dev-sleep"; });
            Check(sleep != null && sleep.Capability == DeviceBatteryCapability.Supported, "sleeping/unavailable battery device stays Supported, never misclassified as no-battery placeholder");
            var unadapted = items.Find(delegate(ControlCenterDeviceItem it) { return it.Unadapted != null; });
            Check(unadapted != null && unadapted.Capability == DeviceBatteryCapability.NoBatteryPlaceholder, "unadapted USB device is placed in NoBatteryPlaceholder group");
            var familyKb = items.Find(delegate(ControlCenterDeviceItem it) { return it.Key == "dev-kb"; });
            Check(familyKb != null && familyKb.CanStopFamily && familyKb.VerificationText.Contains("自动识别"), "family probe device exposes stop-reading and experimental badge");
            form.Close();
            Application.DoEvents();
        }
    }

    static void AllPagesAndBoundsClamp() {
        var area = new Rectangle(0, 0, 1280, 720);
        var clamped = ControlCenterForm.ClampToWorkArea(new Rectangle(2000, 1500, 1000, 660), area, new Size(820, 560));
        Check(area.Contains(clamped) && clamped.Width == 1000 && clamped.Height == 660, "off-screen bounds clamp inside work area");
        var smallArea = new Rectangle(0, 0, 800, 500);
        var smallClamped = ControlCenterForm.ClampToWorkArea(new Rectangle(-50, -50, 1000, 660), smallArea, new Size(820, 560));
        Check(smallArea.Contains(smallClamped) && smallClamped.Width == 800 && smallClamped.Height == 500, "small screen bounds never overflow work area");

        // Bounds persistence round-trip
        string boundsPath = Path.Combine(output, "cc-bounds.json");
        var pref = new AudioBindings { ControlCenterBounds = new WindowBoundsState { X = 120, Y = 80, Width = 1040, Height = 680, HasPosition = true } };
        DeviceSettings.SaveBindings(boundsPath, pref);
        var loaded = DeviceSettings.LoadBindings(boundsPath);
        Check(loaded.ControlCenterBounds != null && loaded.ControlCenterBounds.HasPosition && loaded.ControlCenterBounds.Width == 1040 && loaded.ControlCenterBounds.Height == 680 && loaded.ControlCenterBounds.X == 120, "ControlCenterBounds round-trips cleanly");

        using (var f = new Fixture("graphite", "dark")) {
            f.Owner.ShowControlCenter(ControlCenterPage.Devices);
            Application.DoEvents();
            var form = f.Owner.ActiveControlCenter;
            foreach (ControlCenterPage page in Enum.GetValues(typeof(ControlCenterPage))) {
                form.SelectPage(page);
                Application.DoEvents();
                Check(form.CurrentPage == page, "page switches cleanly to " + page);
            }
            form.Close();
            Application.DoEvents();
        }
    }

    static void HighContrastRendering() {
        VisualDesign.HighContrastOverride = true;
        try {
            using (var f = new Fixture("graphite", "dark")) {
                f.Owner.ShowControlCenter(ControlCenterPage.Appearance);
                var form = f.Owner.ActiveControlCenter;
                Application.DoEvents();
                Check(form.BackColor == SystemColors.Window, "high contrast uses system window background");
                using (var bmp = new Bitmap(form.Width, form.Height)) {
                    form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                    Check(bmp.Width >= 820 && bmp.Height >= 560, "high contrast form renders cleanly");
                }
                form.Close();
                Application.DoEvents();
            }
        } finally {
            VisualDesign.HighContrastOverride = false;
        }
    }

    static void RenderVerificationScreenshots() {
        string shotDir = Path.Combine(output, "ui02-screenshots");
        Directory.CreateDirectory(shotDir);
        string[] themes = { "dark", "light" };
        ControlCenterPage[] pages = { ControlCenterPage.Devices, ControlCenterPage.Appearance, ControlCenterPage.Placement, ControlCenterPage.Settings };
        foreach (string theme in themes) {
            using (var f = new Fixture("graphite", theme)) {
                f.Owner.ShowControlCenter(ControlCenterPage.Devices);
                var form = f.Owner.ActiveControlCenter;
                form.Size = new Size(1040, 680);
                Application.DoEvents();
                foreach (var page in pages) {
                    form.SelectPage(page);
                    Application.DoEvents();
                    using (var bmp = new Bitmap(form.Width, form.Height)) {
                        form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                        string file = Path.Combine(shotDir, "cc-graphite-" + theme + "-" + page.ToString().ToLowerInvariant() + ".png");
                        bmp.Save(file, ImageFormat.Png);
                        Check(File.Exists(file) && new FileInfo(file).Length > 1000, "rendered screenshot saved: " + Path.GetFileName(file));
                    }
                }
                form.Close();
                Application.DoEvents();
            }
        }
        // Also verify single-device wide grid card preview in light ocean theme
        using (var f = new Fixture("ocean", "light")) {
            f.Owner.WirelessDevices = new WirelessDevice[0];
            f.Owner.Audio = null;
            var p = DeviceSettings.Current;
            p.PanelLayout = "grid";
            f.Owner.SavePreferences(p);
            f.Owner.ShowControlCenter(ControlCenterPage.Appearance);
            var form = f.Owner.ActiveControlCenter;
            form.Size = new Size(1040, 680);
            Application.DoEvents();
            using (var bmp = new Bitmap(form.Width, form.Height)) {
                form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                string file = Path.Combine(shotDir, "cc-ocean-light-appearance-grid.png");
                bmp.Save(file, ImageFormat.Png);
                Check(File.Exists(file) && new FileInfo(file).Length > 1000, "rendered ocean light wide-grid appearance screenshot");
            }
            form.Close();
            Application.DoEvents();
        }
    }

    [STAThread]
    static int Main(string[] args) {
        output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "myfinder-cc-tests");
        Directory.CreateDirectory(output);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        NativeDisplay.EnableDpiAwareness();
        Application.EnableVisualStyles();
        DialogVisibility.ConsumeStartupShowCommand();
        VisualDesign.HighContrastOverride = false;

        Run("single instance and dispose lifecycle", SingleInstanceAndLifecycle);
        Run("device capability grouping", DeviceCapabilityGrouping);
        Run("all 6 pages navigation and bounds clamp", AllPagesAndBoundsClamp);
        Run("high contrast rendering", HighContrastRendering);
        Run("real WinForms screenshots (graphite dark/light)", RenderVerificationScreenshots);

        Console.WriteLine(checks + " checks, " + failures + " failed groups");
        return failures == 0 ? 0 : 1;
    }
}
