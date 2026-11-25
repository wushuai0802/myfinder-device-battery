using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

static class TaskbarOverlayTests {
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

    static TaskbarEnvironmentSnapshot MakeValidBottomBar() {
        return new TaskbarEnvironmentSnapshot {
            HasTaskbarWindow = true,
            TaskbarVisible = true,
            AutoHideEnabled = false,
            TaskbarEdge = TaskbarEnvironmentProbe.EdgeBottom,
            TaskbarRect = new Rectangle(0, 1032, 1920, 48),
            HasTrayNotifyRect = true,
            TrayNotifyRect = new Rectangle(1680, 1032, 240, 48),
            PrimaryMonitorBounds = new Rectangle(0, 0, 1920, 1080),
            Obstruction = DisplayObstruction.None
        };
    }

    static void SettingsDefaultsAndNormalization() {
        var b = new AudioBindings();
        Check(b.TaskbarOverlay != null, "TaskbarOverlay defaults non-null");
        Check(!b.TaskbarOverlay.Enabled, "TaskbarOverlay disabled by default");
        Check(b.TaskbarOverlay.ShowIcons, "ShowIcons defaults true");
        Check(b.TaskbarOverlay.MaxDevices == 3, "MaxDevices defaults 3");
        Check(b.TaskbarOverlay.OffsetX == 0, "OffsetX defaults 0");

        var cfg = new TaskbarOverlaySettings { Enabled = true, ShowIcons = false, MaxDevices = 99, OffsetX = -9999 };
        cfg.Normalize();
        Check(cfg.MaxDevices == 3, "MaxDevices clamped upper to 3");
        Check(cfg.OffsetX == -400, "OffsetX clamped lower to -400");

        cfg.MaxDevices = 0;
        cfg.OffsetX = 9999;
        cfg.Normalize();
        Check(cfg.MaxDevices == 1, "MaxDevices clamped lower to 1");
        Check(cfg.OffsetX == 400, "OffsetX clamped upper to 400");

        var cloned = b.Clone();
        cloned.TaskbarOverlay.Enabled = true;
        Check(!b.TaskbarOverlay.Enabled, "AudioBindings.Clone deep-copies TaskbarOverlay");
    }

    static void EnvironmentGatingAndDegradationMatrix() {
        Size overlaySize = new Size(220, 28);

        // 1. Missing Shell_TrayWnd (third-party shell)
        var s1 = MakeValidBottomBar();
        s1.HasTaskbarWindow = false;
        var r1 = TaskbarOverlayEvaluator.Evaluate(s1, overlaySize, 0);
        Check(!r1.CanDisplay && r1.StatusCode == "no_explorer_taskbar", "missing Shell_TrayWnd degrades cleanly");

        // 2. Auto-hide taskbar
        var s2 = MakeValidBottomBar();
        s2.AutoHideEnabled = true;
        var r2 = TaskbarOverlayEvaluator.Evaluate(s2, overlaySize, 0);
        Check(!r2.CanDisplay && r2.StatusCode == "auto_hide_taskbar", "auto-hide taskbar degrades cleanly");

        // 3. Non-bottom taskbar (top / left / right)
        var s3 = MakeValidBottomBar();
        s3.TaskbarEdge = TaskbarEnvironmentProbe.EdgeLeft;
        var r3 = TaskbarOverlayEvaluator.Evaluate(s3, overlaySize, 0);
        Check(!r3.CanDisplay && r3.StatusCode == "non_bottom_taskbar", "left taskbar degrades cleanly");

        var s3Top = MakeValidBottomBar();
        s3Top.TaskbarEdge = TaskbarEnvironmentProbe.EdgeTop;
        var r3Top = TaskbarOverlayEvaluator.Evaluate(s3Top, overlaySize, 0);
        Check(!r3Top.CanDisplay && r3Top.StatusCode == "non_bottom_taskbar", "top taskbar degrades cleanly");

        // 4. Fullscreen obstruction
        var s4 = MakeValidBottomBar();
        s4.Obstruction = DisplayObstruction.Fullscreen;
        var r4 = TaskbarOverlayEvaluator.Evaluate(s4, overlaySize, 0);
        Check(!r4.CanDisplay && r4.StatusCode == "fullscreen_hidden", "fullscreen app hides overlay");

        // 5. Insufficient horizontal width
        var s5 = MakeValidBottomBar();
        s5.TaskbarRect = new Rectangle(0, 1032, 640, 48);
        s5.TrayNotifyRect = new Rectangle(350, 1032, 290, 48);
        var r5 = TaskbarOverlayEvaluator.Evaluate(s5, new Size(240, 28), 0);
        Check(!r5.CanDisplay && r5.StatusCode == "insufficient_space", "insufficient taskbar width degrades cleanly");

        // 6. Standard bottom taskbar placement & vertical centering
        var s6 = MakeValidBottomBar();
        var r6 = TaskbarOverlayEvaluator.Evaluate(s6, overlaySize, 0);
        Check(r6.CanDisplay && r6.StatusCode == "ready", "valid bottom taskbar is ready");
        Check(r6.PhysicalLocation.X == 1680 - 10 - 220, "placed 10px left of TrayNotifyWnd");
        Check(r6.PhysicalLocation.Y == 1032 + (48 - 28) / 2, "vertically centered inside taskbar");

        // 7. Offset clamping keeps overlay inside safe zone
        var r7Left = TaskbarOverlayEvaluator.Evaluate(s6, overlaySize, -400);
        Check(r7Left.CanDisplay && r7Left.PhysicalLocation.X == 1680 - 10 - 220 - 400, "negative offset shifts left within safe area");
        var r7Right = TaskbarOverlayEvaluator.Evaluate(s6, overlaySize, 200);
        Check(r7Right.CanDisplay && r7Right.PhysicalLocation.X == 1680 - 10 - 220, "positive offset never overlaps TrayNotifyWnd");
    }

    static void LiveControllerLifecycleAndDegradation() {
        var pref = new AudioBindings { ThemeStyle = "graphite", Theme = "dark", RememberReadings = false };
        DeviceSettings.UseTransientConfiguration(pref);
        string settingsPath = Path.Combine(output, "tb-settings.json");

        using (var owner = new TrayContext(false,
            delegate(bool b) { return new MouseSnapshot { Battery = new BatteryReading { Percent = 84, TimestampUtc = DateTime.UtcNow } }; },
            delegate(int s) { return null; },
            null, null, false, null, null, null, delegate { return false; },
            null, null, null, null,
            delegate(AudioBindings s) { DeviceSettings.SaveBindings(settingsPath, s); DeviceSettings.UseTransientConfiguration(s); })) {
            ((System.Windows.Forms.Timer)Field(owner, "refresh")).Stop();
            owner.Current = new BatteryReading { Percent = 84, TimestampUtc = DateTime.UtcNow };
            owner.NativeMousePresent = true;
            owner.GenericReady = true;
            owner.WirelessDevices = new[] {
                new WirelessDevice { Id = "kb-1", Name = "ATK75", Kind = "keyboard", Transport = "2.4 GHz", Percent = 62, Fresh = true, Online = true, ConnectionState = "connected" },
                new WirelessDevice { Id = "hs-1", Name = "ROG Delta II", Kind = "headset", Transport = "2.4 GHz", Percent = 14, Fresh = true, Online = true, ConnectionState = "connected", Charging = true }
            };

            // Use deterministic injected probe on a standalone controller
            bool autoHide = false;
            using (var ctrl = new TaskbarBatteryOverlayController(owner, delegate {
                var snap = MakeValidBottomBar();
                snap.AutoHideEnabled = autoHide;
                return snap;
            })) {
                ctrl.ApplySettings();
                Check(!ctrl.IsRunning && ctrl.StatusCode == "disabled", "controller stays inactive when disabled");

                var next = DeviceSettings.Current;
                next.TaskbarOverlay.Enabled = true;
                owner.SavePreferences(next);
                ctrl.ApplySettings();
                Check(ctrl.IsRunning && ctrl.StatusCode == "ready" && ctrl.ActiveForm.OverlayVisible, "controller creates and shows overlay when enabled on valid taskbar");

                // Simulate system switching to auto-hide taskbar
                autoHide = true;
                ctrl.NotifyDataChanged();
                Check(ctrl.StatusCode == "auto_hide_taskbar" && !ctrl.ActiveForm.OverlayVisible, "controller automatically hides overlay on auto-hide taskbar");

                // Recover when auto-hide turned off
                autoHide = false;
                ctrl.NotifyTaskbarRecreated();
                Check(ctrl.StatusCode == "ready" && ctrl.ActiveForm.OverlayVisible, "controller recovers on TaskbarCreated");

                // Disable -> closes & disposes form cleanly
                next = DeviceSettings.Current;
                next.TaskbarOverlay.Enabled = false;
                owner.SavePreferences(next);
                ctrl.ApplySettings();
                Check(!ctrl.IsRunning && ctrl.StatusCode == "disabled", "disabling disposes overlay form cleanly");
            }
            owner.ExitThread();
        }
    }

    static void RenderShowcasePng() {
        var items = new List<TaskbarOverlayRenderer.Item> {
            new TaskbarOverlayRenderer.Item { Id = "m1", Name = "ATK A9 Air+", Kind = "mouse", Percent = 84, Fresh = true },
            new TaskbarOverlayRenderer.Item { Id = "k1", Name = "ATK75", Kind = "keyboard", Percent = 62, Fresh = true },
            new TaskbarOverlayRenderer.Item { Id = "h1", Name = "ROG Delta II", Kind = "headset", Percent = 14, Fresh = true, Charging = true, Low = true }
        };

        Size szIcons = TaskbarOverlayRenderer.ComputeLogicalSize(items, true);
        Size szNums = TaskbarOverlayRenderer.ComputeLogicalSize(items, false);
        var darkPal = ThemePalette.For("graphite", true);
        var lightPal = ThemePalette.For("graphite", false);

        using (var bDarkIcons = LayeredSurface.RenderToBitmap(szIcons, 2f, delegate(Graphics g, SizeF s) { TaskbarOverlayRenderer.Paint(g, s, items, true, darkPal, true); }))
        using (var bDarkNums = LayeredSurface.RenderToBitmap(szNums, 2f, delegate(Graphics g, SizeF s) { TaskbarOverlayRenderer.Paint(g, s, items, false, darkPal, true); }))
        using (var bLightIcons = LayeredSurface.RenderToBitmap(szIcons, 2f, delegate(Graphics g, SizeF s) { TaskbarOverlayRenderer.Paint(g, s, items, true, lightPal, false); }))
        using (var bLightNums = LayeredSurface.RenderToBitmap(szNums, 2f, delegate(Graphics g, SizeF s) { TaskbarOverlayRenderer.Paint(g, s, items, false, lightPal, false); }))
        using (var canvas = new Bitmap(960, 320, PixelFormat.Format32bppArgb)) {
            using (var g = Graphics.FromImage(canvas)) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.FromArgb(20, 22, 26));
                using (var titleFont = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var subFont = new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var fg = new SolidBrush(Color.FromArgb(236, 238, 242)))
                using (var muted = new SolidBrush(Color.FromArgb(156, 162, 174))) {
                    g.DrawString("Wave 4 — Clean-Room Taskbar Battery Overlay (Graphite Dark / Light, 200% DPI)", titleFont, fg, 24, 18);

                    // Simulated Dark Win11 Taskbar strip
                    g.DrawString("Dark Taskbar Strip (Icons + Numbers / Numbers Only)", subFont, muted, 24, 52);
                    using (var barBrush = new SolidBrush(Color.FromArgb(30, 32, 36))) g.FillRectangle(barBrush, 24, 74, 912, 88);
                    g.DrawImage(bDarkIcons, 44, 90, szIcons.Width * 2, szIcons.Height * 2);
                    g.DrawImage(bDarkNums, 540, 90, szNums.Width * 2, szNums.Height * 2);

                    // Simulated Light Win11 Taskbar strip
                    g.DrawString("Light Taskbar Strip (Icons + Numbers / Numbers Only)", subFont, muted, 24, 182);
                    using (var barBrush = new SolidBrush(Color.FromArgb(238, 241, 246))) g.FillRectangle(barBrush, 24, 204, 912, 88);
                    g.DrawImage(bLightIcons, 44, 220, szIcons.Width * 2, szIcons.Height * 2);
                    g.DrawImage(bLightNums, 540, 220, szNums.Width * 2, szNums.Height * 2);
                }
            }
            string shotPath = Path.Combine(output, "taskbar-overlay-showcase-graphite.png");
            canvas.Save(shotPath, ImageFormat.Png);
            Check(File.Exists(shotPath) && new FileInfo(shotPath).Length > 4000, "rendered taskbar overlay showcase PNG");
        }
    }

    [STAThread]
    static int Main(string[] args) {
        output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "myfinder-taskbar-tests");
        Directory.CreateDirectory(output);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        NativeDisplay.EnableDpiAwareness();

        Run("TaskbarOverlaySettings defaults and normalization", SettingsDefaultsAndNormalization);
        Run("Taskbar environment gating and degradation matrix", EnvironmentGatingAndDegradationMatrix);
        Run("Taskbar overlay controller lifecycle and recovery", LiveControllerLifecycleAndDegradation);
        Run("Taskbar overlay visual showcase render", RenderShowcasePng);

        Console.WriteLine(checks + " checks, " + failures + " failed groups");
        return failures == 0 ? 0 : 1;
    }
}
