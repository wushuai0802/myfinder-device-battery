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

static class DesktopWidgetTests {
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

    static void SettingsDefaultsAndNormalization() {
        var fresh = new AudioBindings();
        Check(fresh.DesktopWidget != null && !fresh.DesktopWidget.Enabled, "desktop widget defaults to disabled");
        Check(fresh.DesktopWidget.Style == "capsule" && fresh.DesktopWidget.DockEdge == "none" && fresh.DesktopWidget.TopMost && fresh.DesktopWidget.FullscreenPolicy == "hide", "desktop widget defaults match T50 spec");

        string path = Path.Combine(output, "widget-settings.json");
        var custom = fresh.Clone();
        custom.DesktopWidget.Enabled = true;
        custom.DesktopWidget.Style = "rings";
        custom.DesktopWidget.DockEdge = "right";
        custom.DesktopWidget.AutoHideOnEdge = true;
        custom.DesktopWidget.TopMost = false;
        custom.DesktopWidget.FullscreenPolicy = "edge";
        custom.DesktopWidget.CompactOnMaximized = false;
        custom.DesktopWidget.OpacityPercent = 88;
        custom.DesktopWidget.MaxDevices = 4;
        custom.DesktopWidget.AnchorXRatio = 0.25;
        custom.DesktopWidget.AnchorYRatio = 0.75;
        custom.DesktopWidget.MonitorDeviceName = @"\\.\DISPLAY1";

        DeviceSettings.SaveBindings(path, custom);
        var loaded = DeviceSettings.LoadBindings(path);
        Check(loaded.DesktopWidget.Enabled && loaded.DesktopWidget.Style == "rings" && loaded.DesktopWidget.DockEdge == "right" && !loaded.DesktopWidget.TopMost && loaded.DesktopWidget.FullscreenPolicy == "edge" && loaded.DesktopWidget.MaxDevices == 4 && loaded.DesktopWidget.OpacityPercent == 88, "desktop widget settings round-trip cleanly");

        // Out-of-range / NaN / unknown values normalize without throwing
        var weird = new AudioBindings();
        weird.DesktopWidget.Style = "invalid";
        weird.DesktopWidget.DockEdge = "diagonal";
        weird.DesktopWidget.FullscreenPolicy = "bogus";
        weird.DesktopWidget.OpacityPercent = 12;
        weird.DesktopWidget.MaxDevices = 99;
        weird.DesktopWidget.AnchorXRatio = Double.NaN;
        weird.DesktopWidget.AnchorYRatio = 4.2;
        DeviceSettings.SaveBindings(path, weird);
        var normalized = DeviceSettings.LoadBindings(path);
        Check(normalized.DesktopWidget.Style == "capsule" && normalized.DesktopWidget.DockEdge == "none" && normalized.DesktopWidget.FullscreenPolicy == "hide" && normalized.DesktopWidget.OpacityPercent == 70 && normalized.DesktopWidget.MaxDevices == 4 && normalized.DesktopWidget.AnchorXRatio >= 0 && normalized.DesktopWidget.AnchorYRatio == 1.0, "malformed widget fields normalize safely");
    }

    static void GeometryAndEdgeSnap() {
        var workArea = new Rectangle(0, 0, 1920, 1040);
        Check(DesktopWidgetGeometry.DetectSnapEdge(new Rectangle(400, 10, 200, 36), workArea) == "top", "snaps to top edge");
        Check(DesktopWidgetGeometry.DetectSnapEdge(new Rectangle(400, 1000, 200, 36), workArea) == "bottom", "snaps to bottom edge");
        Check(DesktopWidgetGeometry.DetectSnapEdge(new Rectangle(8, 300, 200, 36), workArea) == "left", "snaps to left edge");
        Check(DesktopWidgetGeometry.DetectSnapEdge(new Rectangle(1715, 300, 200, 36), workArea) == "right", "snaps to right edge");
        Check(DesktopWidgetGeometry.DetectSnapEdge(new Rectangle(500, 500, 200, 36), workArea) == "none", "center stays undocked");

        var hiddenRight = DesktopWidgetGeometry.CollapsedLogicalSize("capsule", 3, false, "right", true);
        Check(hiddenRight.Width == 8 && hiddenRight.Height == 68, "vertical hidden edge tab is slim 8x68");
        var hiddenTop = DesktopWidgetGeometry.CollapsedLogicalSize("capsule", 3, false, "top", true);
        Check(hiddenTop.Width == 68 && hiddenTop.Height == 8, "horizontal hidden edge tab is slim 68x8");

        var locRight = DesktopWidgetGeometry.ComputePhysicalLocation("right", new Size(200, 36), workArea, 0.5, 0.5, false);
        Check(locRight.X == workArea.Right - 200 - 6 && locRight.Y >= workArea.Top && locRight.Y + 36 <= workArea.Bottom, "right docked widget hugs right edge within workArea");
        var locHiddenRight = DesktopWidgetGeometry.ComputePhysicalLocation("right", new Size(8, 68), workArea, 0.5, 0.5, true);
        Check(locHiddenRight.X == workArea.Right - 8, "hidden right edge tab sits flush against screen edge");

        double rx, ry;
        DesktopWidgetGeometry.ComputeAnchorRatios(new Point(960, 520), new Size(200, 36), workArea, out rx, out ry);
        Check(rx > 0.4 && rx < 0.6 && ry > 0.4 && ry < 0.6, "relative ratios stay bounded in [0,1]");
    }

    static void DisplayWatchStateMachine() {
        var mon = new Rectangle(0, 0, 1920, 1080);
        var wa = new Rectangle(0, 0, 1920, 1040);

        Check(DesktopDisplayWatch.Evaluate(new ForegroundSnapshot { HasWindow = false }) == DisplayObstruction.None, "no window -> None");
        Check(DesktopDisplayWatch.Evaluate(new ForegroundSnapshot { HasWindow = true, IsOwnProcess = true, IsVisible = true, SameMonitor = true, WindowRect = mon, MonitorBounds = mon, MonitorWorkArea = wa }) == DisplayObstruction.None, "own process never triggers fullscreen");
        Check(DesktopDisplayWatch.Evaluate(new ForegroundSnapshot { HasWindow = true, IsShellOrDesktop = true, IsVisible = true, SameMonitor = true, WindowRect = mon, MonitorBounds = mon, MonitorWorkArea = wa }) == DisplayObstruction.None, "desktop/shell never triggers fullscreen");
        Check(DesktopDisplayWatch.Evaluate(new ForegroundSnapshot { HasWindow = true, IsVisible = true, SameMonitor = false, WindowRect = mon, MonitorBounds = mon, MonitorWorkArea = wa }) == DisplayObstruction.None, "fullscreen on another monitor does not hide this monitor's widget");

        var full = DesktopDisplayWatch.Evaluate(new ForegroundSnapshot { HasWindow = true, IsVisible = true, SameMonitor = true, WindowRect = mon, MonitorBounds = mon, MonitorWorkArea = wa, NotificationState = 3 });
        Check(full == DisplayObstruction.Fullscreen, "full monitor rect -> Fullscreen");
        Check(DesktopDisplayWatch.ResolveMode(full, "hide", true) == WidgetVisibilityMode.Hidden, "hide policy hides on confirmed fullscreen");
        Check(DesktopDisplayWatch.ResolveMode(full, "edge", true) == WidgetVisibilityMode.EdgeStrip, "edge policy shrinks on confirmed fullscreen");
        Check(DesktopDisplayWatch.ResolveMode(full, "ignore", true) == WidgetVisibilityMode.Normal, "ignore policy ignores fullscreen");

        var uncertain = DesktopDisplayWatch.Evaluate(new ForegroundSnapshot { HasWindow = true, IsVisible = true, SameMonitor = true, WindowRect = new Rectangle(100, 100, 800, 600), MonitorBounds = mon, MonitorWorkArea = wa, NotificationState = 2 });
        Check(uncertain == DisplayObstruction.UncertainFullscreen, "busy notification state without full rect -> UncertainFullscreen");
        Check(DesktopDisplayWatch.ResolveMode(uncertain, "hide", true) == WidgetVisibilityMode.EdgeStrip, "uncertain fullscreen shrinks to edge strip instead of vanishing");

        var max = DesktopDisplayWatch.Evaluate(new ForegroundSnapshot { HasWindow = true, IsVisible = true, IsZoomed = true, SameMonitor = true, WindowRect = wa, MonitorBounds = mon, MonitorWorkArea = wa, NotificationState = 5 });
        Check(max == DisplayObstruction.Maximized, "zoomed window -> Maximized");
        Check(DesktopDisplayWatch.ResolveMode(max, "hide", true) == WidgetVisibilityMode.EdgeStrip, "compactOnMaximized shrinks maximized window obstruction");
        Check(DesktopDisplayWatch.ResolveMode(max, "hide", false) == WidgetVisibilityMode.Normal, "compactOnMaximized=false keeps Normal");
    }

    static void LiveWidgetLifecycleAndSurface() {
        var pref = new AudioBindings { ThemeStyle = "graphite", Theme = "dark", ReducedMotion = true, RememberReadings = false };
        DeviceSettings.UseTransientConfiguration(pref);
        string settingsPath = Path.Combine(output, "widget-live.json");
        using (var owner = new TrayContext(false,
            delegate(bool b) { return new MouseSnapshot { Battery = new BatteryReading { Percent = 94, TimestampUtc = DateTime.UtcNow } }; },
            delegate(int s) { return null; },
            null, null, false, null, null, null, delegate { return false; },
            null, null, null, null,
            delegate(AudioBindings s) { DeviceSettings.SaveBindings(settingsPath, s); DeviceSettings.UseTransientConfiguration(s); })) {
            ((System.Windows.Forms.Timer)Field(owner, "refresh")).Stop();
            owner.Current = new BatteryReading { Percent = 94, TimestampUtc = DateTime.UtcNow };
            owner.NativeMousePresent = true;
            owner.GenericReady = true;
            owner.WirelessDevices = new[] {
                new WirelessDevice { Id = "w-hs", Name = "ROG Delta II", Kind = "headset", Transport = "2.4 GHz", Percent = 42, Fresh = true, Online = true, ConnectionState = "connected", ConfirmedAtUtc = DateTime.UtcNow.ToString("o") },
                new WirelessDevice { Id = "w-kb", Name = "VGN Neon75 Air", Kind = "keyboard", Transport = "2.4 GHz", Percent = 14, Fresh = true, Online = true, ConnectionState = "connected", ConfirmedAtUtc = DateTime.UtcNow.ToString("o") }
            };

            Check(!owner.DesktopWidgetRunning, "widget is not running when disabled by default");

            // Enable widget
            var next = DeviceSettings.Current;
            next.DesktopWidget.Enabled = true;
            owner.SavePreferences(next);
            owner.ConfigurationChanged(false);
            Application.DoEvents();
            Check(owner.DesktopWidgetRunning && owner.ActiveDesktopWidget != null, "enabling spawns DesktopWidgetForm");

            // Repeated data/theme updates must remain healthy
            var form = owner.ActiveDesktopWidget;
            for (int i = 0; i < 6; i++) form.RenderNow(true);
            Check(!form.IsDisposed && form.LogicalSize.Width > 0 && form.LogicalSize.Height > 0, "repeated LayeredSurface commits succeed cleanly");

            // Disable widget -> disposes completely
            next = DeviceSettings.Current;
            next.DesktopWidget.Enabled = false;
            owner.SavePreferences(next);
            owner.ConfigurationChanged(false);
            Application.DoEvents();
            Check(!owner.DesktopWidgetRunning && form.IsDisposed, "disabling disposes DesktopWidgetForm completely");
            owner.ExitThread();
        }
    }

    static void RenderWidgetShowcase() {
        string dir = Path.Combine(output, "t50-screenshots");
        Directory.CreateDirectory(dir);
        var entries = new List<DesktopWidgetEntry> {
            new DesktopWidgetEntry { Id = "m1", Name = "ATK A9 Air+", Kind = "mouse", Subtitle = "2.4 GHz · 在线", Percent = 96, Fresh = true },
            new DesktopWidgetEntry { Id = "h1", Name = "ROG Delta II", Kind = "headset", Subtitle = "2.4 GHz · 在线", Percent = 42, Fresh = true },
            new DesktopWidgetEntry { Id = "k1", Name = "VGN Neon75 Air", Kind = "keyboard", Subtitle = "2.4 GHz · 电量低", Percent = 14, Fresh = true, Low = true }
        };
        DeviceSettings.UseTransientConfiguration(new AudioBindings { ThemeStyle = "graphite", Theme = "dark" });

        // Render composite sheet (capsule / rings / strip / expanded in dark & light graphite at 2x DPI)
        using (var sheet = new Bitmap(920, 560, PixelFormat.Format32bppPArgb))
        using (var sg = Graphics.FromImage(sheet)) {
            sg.Clear(Color.FromArgb(24, 28, 34));
            var cfg = new DesktopWidgetSettings { Style = "capsule", MaxDevices = 3 };
            foreach (bool isDark in new[] { true, false }) {
                int ox = isDark ? 24 : 472;
                var pal = ThemePalette.For("graphite", isDark);
                var capSize = DesktopWidgetGeometry.CollapsedLogicalSize("capsule", 3, false, "none", false);
                using (var b = LayeredSurface.RenderToBitmap(capSize, 1.5f, delegate(Graphics g, SizeF sz) { DesktopWidgetRenderer.Paint(g, sz, entries, pal, isDark, cfg, false, false, false, -1); }))
                    sg.DrawImageUnscaled(b, ox, 24);

                var ringSize = DesktopWidgetGeometry.CollapsedLogicalSize("rings", 3, false, "none", false);
                var cfgR = new DesktopWidgetSettings { Style = "rings" };
                using (var b = LayeredSurface.RenderToBitmap(ringSize, 1.5f, delegate(Graphics g, SizeF sz) { DesktopWidgetRenderer.Paint(g, sz, entries, pal, isDark, cfgR, false, false, false, -1); }))
                    sg.DrawImageUnscaled(b, ox, 98);

                var stripSize = DesktopWidgetGeometry.CollapsedLogicalSize("strip", 3, false, "none", false);
                var cfgS = new DesktopWidgetSettings { Style = "strip" };
                using (var b = LayeredSurface.RenderToBitmap(stripSize, 1.5f, delegate(Graphics g, SizeF sz) { DesktopWidgetRenderer.Paint(g, sz, entries, pal, isDark, cfgS, false, false, false, -1); }))
                    sg.DrawImageUnscaled(b, ox, 184);

                var expSize = DesktopWidgetGeometry.ExpandedLogicalSize(3, false);
                using (var b = LayeredSurface.RenderToBitmap(expSize, 1.5f, delegate(Graphics g, SizeF sz) { DesktopWidgetRenderer.Paint(g, sz, entries, pal, isDark, cfg, true, false, false, 0); }))
                    sg.DrawImageUnscaled(b, ox, 244);
            }
            string file = Path.Combine(dir, "t50-widget-showcase-graphite.png");
            sheet.Save(file, ImageFormat.Png);
            Check(File.Exists(file) && new FileInfo(file).Length > 2000, "widget showcase screenshot rendered");
        }
    }

    [STAThread]
    static int Main(string[] args) {
        output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "myfinder-widget-tests");
        Directory.CreateDirectory(output);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        NativeDisplay.EnableDpiAwareness();
        Application.EnableVisualStyles();
        VisualDesign.HighContrastOverride = false;

        Run("widget settings defaults and normalization", SettingsDefaultsAndNormalization);
        Run("widget geometry and 4-edge magnetic snap", GeometryAndEdgeSnap);
        Run("layered display watch fullscreen/maximized state machine", DisplayWatchStateMachine);
        Run("live widget lifecycle and LayeredSurface commits", LiveWidgetLifecycleAndSurface);
        Run("rendered widget showcase PNG", RenderWidgetShowcase);

        Console.WriteLine(checks + " checks, " + failures + " failed groups");
        return failures == 0 ? 0 : 1;
    }
}
