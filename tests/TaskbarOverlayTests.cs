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

    static TaskbarEnvironmentSnapshot MakeValidTopBar() {
        return new TaskbarEnvironmentSnapshot {
            HasTaskbarWindow = true,
            TaskbarVisible = true,
            AutoHideEnabled = false,
            TaskbarEdge = TaskbarEnvironmentProbe.EdgeTop,
            TaskbarRect = new Rectangle(0, 0, 1920, 48),
            HasTrayNotifyRect = true,
            TrayNotifyRect = new Rectangle(1680, 0, 240, 48),
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
        Check(cfg.OffsetX == -1200, "OffsetX clamped lower to -1200");

        cfg.MaxDevices = 0;
        cfg.OffsetX = 9999;
        cfg.Normalize();
        Check(cfg.MaxDevices == 1, "MaxDevices clamped lower to 1");
        Check(cfg.OffsetX == 0, "OffsetX clamped upper to 0 (flush against notification area)");

        var cloned = b.Clone();
        cloned.TaskbarOverlay.Enabled = true;
        Check(!b.TaskbarOverlay.Enabled, "AudioBindings.Clone deep-copies TaskbarOverlay");

        // Independent tray icon hiding policy & no-lockout invariant (#1129)
        Check(!b.HideTrayIcon, "HideTrayIcon defaults false");
        string hidePath = Path.Combine(output, "hide-tray-invariant.json");
        var onlyHide = new AudioBindings { HideTrayIcon = true };
        DeviceSettings.SaveBindings(hidePath, onlyHide);
        var loadedOnlyHide = DeviceSettings.LoadBindings(hidePath);
        Check(!loadedOnlyHide.HideTrayIcon, "Validate auto-heals HideTrayIcon=false when neither taskbar overlay nor widget is enabled");

        var withTaskbar = new AudioBindings { HideTrayIcon = true };
        withTaskbar.TaskbarOverlay.Enabled = true;
        DeviceSettings.SaveBindings(hidePath, withTaskbar);
        var loadedWithTaskbar = DeviceSettings.LoadBindings(hidePath);
        Check(loadedWithTaskbar.HideTrayIcon && loadedWithTaskbar.TaskbarOverlay.Enabled, "HideTrayIcon persists when TaskbarOverlay is enabled");
        Check(!TrayVisibilityPolicy.ShouldShowTrayIcon(loadedWithTaskbar, "ready"), "tray icon stays hidden when taskbar overlay is ready");
        Check(!TrayVisibilityPolicy.ShouldShowTrayIcon(loadedWithTaskbar, "ready_avoided"), "tray icon stays hidden when taskbar overlay is ready_avoided");
        Check(!TrayVisibilityPolicy.ShouldShowTrayIcon(loadedWithTaskbar, "fullscreen_hidden"), "tray icon stays hidden during temporary fullscreen game hiding");
        Check(TrayVisibilityPolicy.ShouldShowTrayIcon(loadedWithTaskbar, "auto_hide_taskbar"), "tray icon auto-restores when taskbar overlay persistently degrades on auto-hide taskbar");
        Check(TrayVisibilityPolicy.ShouldShowTrayIcon(loadedWithTaskbar, "non_horizontal_taskbar"), "tray icon auto-restores when taskbar overlay persistently degrades on vertical taskbar");
        Check(TrayVisibilityPolicy.ShouldShowTrayIcon(loadedWithTaskbar, "plugin_conflict"), "tray icon auto-restores when taskbar overlay persistently degrades on plugin conflict");
        Check(TrayVisibilityPolicy.ShouldShowTrayIcon(loadedWithTaskbar, "disabled"), "tray icon stays visible while taskbar overlay is still disabled/uninitialized");
        Check(TrayVisibilityPolicy.ShouldShowTrayIcon(loadedWithTaskbar, null), "tray icon fails safe to visible on null/unknown taskbar status");

        loadedWithTaskbar.DesktopWidget.Enabled = true;
        Check(!TrayVisibilityPolicy.ShouldShowTrayIcon(loadedWithTaskbar, "auto_hide_taskbar"), "tray icon stays hidden when desktop widget remains enabled as alternative surface");
        loadedWithTaskbar.DesktopWidget.Enabled = false;
        loadedWithTaskbar.TaskbarOverlay.Enabled = false;
        DeviceSettings.SaveBindings(hidePath, loadedWithTaskbar);
        Check(!DeviceSettings.LoadBindings(hidePath).HideTrayIcon, "disabling last alternative surface auto-restores HideTrayIcon=false");
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

        // 3. Vertical taskbars (left / right) degrade cleanly; top horizontal taskbar (#1157) is supported
        var s3Left = MakeValidBottomBar();
        s3Left.TaskbarEdge = TaskbarEnvironmentProbe.EdgeLeft;
        var r3Left = TaskbarOverlayEvaluator.Evaluate(s3Left, overlaySize, 0);
        Check(!r3Left.CanDisplay && r3Left.StatusCode == "non_horizontal_taskbar", "left vertical taskbar degrades cleanly");

        var s3Right = MakeValidBottomBar();
        s3Right.TaskbarEdge = TaskbarEnvironmentProbe.EdgeRight;
        var r3Right = TaskbarOverlayEvaluator.Evaluate(s3Right, overlaySize, 0);
        Check(!r3Right.CanDisplay && r3Right.StatusCode == "non_horizontal_taskbar", "right vertical taskbar degrades cleanly");

        var s3Top = MakeValidTopBar();
        var r3Top = TaskbarOverlayEvaluator.Evaluate(s3Top, overlaySize, 0);
        Check(r3Top.CanDisplay && r3Top.StatusCode == "ready", "top horizontal taskbar (#1157) is supported");
        Check(r3Top.PhysicalLocation.X == 1680 - 10 - 220, "top taskbar places overlay 10px left of TrayNotifyWnd");
        Check(r3Top.PhysicalLocation.Y == (48 - 28) / 2, "top taskbar vertically centers overlay inside top bar");
        Check(r3Top.StatusMessage.Contains("顶部"), "top taskbar status message reports top edge");

        // Top taskbar plugin avoidance and Windhawk/ExplorerPatcher edge geometry inference
        var s3TopAvoid = MakeValidTopBar();
        s3TopAvoid.OccupiedRects = new List<Rectangle> { new Rectangle(1500, 2, 160, 44) };
        var r3TopAvoid = TaskbarOverlayEvaluator.Evaluate(s3TopAvoid, overlaySize, 0);
        Check(r3TopAvoid.CanDisplay && r3TopAvoid.StatusCode == "ready_avoided" && r3TopAvoid.PhysicalLocation.X == 1500 - 8 - 220 && r3TopAvoid.PhysicalLocation.Y == 10, "top taskbar avoids third-party plugins cleanly");

        var monBounds = new Rectangle(0, 0, 1920, 1080);
        Check(TaskbarEnvironmentProbe.ResolveTaskbarEdge(TaskbarEnvironmentProbe.EdgeBottom, new Rectangle(0, 0, 1920, 48), monBounds) == TaskbarEnvironmentProbe.EdgeTop, "geometry overrides stale ABM bottom edge when Shell_TrayWnd sits on top (Windhawk/ExplorerPatcher)");
        Check(TaskbarEnvironmentProbe.ResolveTaskbarEdge(-1, new Rectangle(0, 0, 1920, 48), monBounds) == TaskbarEnvironmentProbe.EdgeTop, "infers top edge when ABM_GETTASKBARPOS fails");
        Check(TaskbarEnvironmentProbe.ResolveTaskbarEdge(-1, new Rectangle(0, 1032, 1920, 48), monBounds) == TaskbarEnvironmentProbe.EdgeBottom, "infers bottom edge when ABM_GETTASKBARPOS fails");
        Check(TaskbarEnvironmentProbe.ResolveTaskbarEdge(-1, new Rectangle(0, 0, 62, 1080), monBounds) == TaskbarEnvironmentProbe.EdgeLeft, "infers left vertical edge from geometry");
        Check(!TaskbarEnvironmentProbe.IsFullscreenCoveringTaskbar(new Rectangle(0, 48, 1920, 1032), monBounds, new Rectangle(0, 0, 1920, 48)), "maximized window below top taskbar does NOT count as fullscreen");
        Check(TaskbarEnvironmentProbe.IsFullscreenCoveringTaskbar(new Rectangle(0, 0, 1920, 1080), monBounds, new Rectangle(0, 0, 1920, 48)), "fullscreen game covering top taskbar hides overlay");

        // 4. Fullscreen & game obstruction matrix
        var s4 = MakeValidBottomBar();
        s4.Obstruction = DisplayObstruction.Fullscreen;
        var r4 = TaskbarOverlayEvaluator.Evaluate(s4, overlaySize, 0);
        Check(!r4.CanDisplay && r4.StatusCode == "fullscreen_hidden", "fullscreen app hides overlay");

        var s4Uncertain = MakeValidBottomBar();
        s4Uncertain.Obstruction = DisplayObstruction.UncertainFullscreen;
        var r4Uncertain = TaskbarOverlayEvaluator.Evaluate(s4Uncertain, overlaySize, 0);
        Check(!r4Uncertain.CanDisplay && r4Uncertain.StatusCode == "fullscreen_hidden", "uncertain fullscreen (e.g. scaled/non-native game) hides overlay");

        var s4Covered = MakeValidBottomBar();
        s4Covered.Obstruction = DisplayObstruction.None;
        s4Covered.TaskbarCoveredByFullscreen = true;
        var r4Covered = TaskbarOverlayEvaluator.Evaluate(s4Covered, overlaySize, 0);
        Check(!r4Covered.CanDisplay && r4Covered.StatusCode == "fullscreen_hidden", "unfocused borderless game covering taskbar hides overlay");

        var s4D3d = MakeValidBottomBar();
        s4D3d.Obstruction = DisplayObstruction.None;
        s4D3d.NotificationState = 3; // QUNS_RUNNING_D3D_FULLSCREEN
        var r4D3d = TaskbarOverlayEvaluator.Evaluate(s4D3d, overlaySize, 0);
        Check(!r4D3d.CanDisplay && r4D3d.StatusCode == "fullscreen_hidden", "D3D exclusive fullscreen hides overlay");

        var s4Pres = MakeValidBottomBar();
        s4Pres.Obstruction = DisplayObstruction.None;
        s4Pres.NotificationState = 4; // QUNS_PRESENTATION_MODE
        var r4Pres = TaskbarOverlayEvaluator.Evaluate(s4Pres, overlaySize, 0);
        Check(!r4Pres.CanDisplay && r4Pres.StatusCode == "fullscreen_hidden", "presentation mode hides overlay");

        var s4Max = MakeValidBottomBar();
        s4Max.Obstruction = DisplayObstruction.Maximized;
        s4Max.TaskbarCoveredByFullscreen = false;
        s4Max.NotificationState = 5;
        var r4Max = TaskbarOverlayEvaluator.Evaluate(s4Max, overlaySize, 0);
        Check(r4Max.CanDisplay && r4Max.StatusCode == "ready", "normal maximized window keeps overlay visible");

        // Multi-monitor & taskbar intersection geometry checks
        var mon1 = new Rectangle(0, 0, 1920, 1080);
        var bar1 = new Rectangle(0, 1032, 1920, 48);
        Check(TaskbarEnvironmentProbe.IsFullscreenCoveringTaskbar(new Rectangle(0, 0, 1920, 1080), mon1, bar1), "borderless game covering primary monitor covers taskbar");
        Check(!TaskbarEnvironmentProbe.IsFullscreenCoveringTaskbar(new Rectangle(1920, 0, 1920, 1080), mon1, bar1), "fullscreen window on side-by-side secondary monitor does NOT cover primary taskbar");
        Check(!TaskbarEnvironmentProbe.IsFullscreenCoveringTaskbar(new Rectangle(0, 0, 1920, 1032), mon1, bar1), "normal maximized window ending at taskbar top does NOT cover taskbar");
        Check(!TaskbarEnvironmentProbe.IsFullscreenCoveringTaskbar(new Rectangle(1500, 1034, 160, 44), mon1, bar1), "third-party taskbar plugin does NOT count as fullscreen");

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

        // 7. Offset clamping keeps overlay inside safe zone ([-1200, 0] and safeMinX)
        var r7Left = TaskbarOverlayEvaluator.Evaluate(s6, overlaySize, -400);
        Check(r7Left.CanDisplay && r7Left.PhysicalLocation.X == 1680 - 10 - 220 - 400, "negative offset -400 shifts left cleanly within 1920px safe area");
        var r7SafeMin = TaskbarOverlayEvaluator.Evaluate(s6, overlaySize, -1500);
        Check(r7SafeMin.CanDisplay && r7SafeMin.PhysicalLocation.X == 1920 / 3, "large negative offset clamps at safeMinX");

        var s4k = MakeValidBottomBar();
        s4k.TaskbarRect = new Rectangle(0, 2112, 3840, 48);
        s4k.TrayNotifyRect = new Rectangle(3500, 2112, 340, 48);
        s4k.PrimaryMonitorBounds = new Rectangle(0, 0, 3840, 2160);
        var r7MinClamp = TaskbarOverlayEvaluator.Evaluate(s4k, overlaySize, -1500);
        Check(r7MinClamp.CanDisplay && r7MinClamp.PhysicalLocation.X == 3500 - 10 - 220 - 1200, "4K taskbar clamps negative offset at -1200");

        var r7Right = TaskbarOverlayEvaluator.Evaluate(s6, overlaySize, 200);
        Check(r7Right.CanDisplay && r7Right.PhysicalLocation.X == 1680 - 10 - 220, "positive offset never overlaps TrayNotifyWnd");

        // 8. Third-party plugin collision avoidance (5 scenarios)
        var c1 = MakeValidBottomBar();
        c1.OccupiedRects = new List<Rectangle> { new Rectangle(1500, 1034, 160, 44) };
        var cr1 = TaskbarOverlayEvaluator.Evaluate(c1, overlaySize, 0);
        Check(cr1.CanDisplay && cr1.StatusCode == "ready_avoided" && cr1.PhysicalLocation.X == 1500 - 8 - 220, "single overlapping plugin triggers left avoidance (ready_avoided)");

        var c2 = MakeValidBottomBar();
        c2.OccupiedRects = new List<Rectangle> {
            new Rectangle(1500, 1034, 160, 44),
            new Rectangle(1240, 1034, 180, 44)
        };
        var cr2 = TaskbarOverlayEvaluator.Evaluate(c2, overlaySize, 0);
        Check(cr2.CanDisplay && cr2.StatusCode == "ready_avoided" && cr2.PhysicalLocation.X == 1240 - 8 - 220, "two plugins trigger chained left avoidance");

        var c3 = MakeValidBottomBar();
        c3.OccupiedRects = new List<Rectangle> { new Rectangle(800, 1034, 160, 44) };
        var cr3 = TaskbarOverlayEvaluator.Evaluate(c3, overlaySize, 0);
        Check(cr3.CanDisplay && cr3.StatusCode == "ready" && cr3.PhysicalLocation.X == 1680 - 10 - 220, "distant plugin does not trigger avoidance");

        var c4 = MakeValidBottomBar();
        c4.OccupiedRects = new List<Rectangle> { new Rectangle(750, 1034, 900, 44) };
        var cr4 = TaskbarOverlayEvaluator.Evaluate(c4, overlaySize, 0);
        Check(!cr4.CanDisplay && cr4.StatusCode == "plugin_conflict", "insufficient space after plugin avoidance degrades to plugin_conflict");

        var c5 = MakeValidBottomBar();
        c5.OccupiedRects = new List<Rectangle> { new Rectangle(1250, 1034, 150, 44) };
        var cr5 = TaskbarOverlayEvaluator.Evaluate(c5, overlaySize, -200);
        Check(cr5.CanDisplay && cr5.StatusCode == "ready_avoided" && cr5.PhysicalLocation.X == 1250 - 8 - 220, "manual negative offset still avoids overlapping plugin to the left");
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
            bool gameFullscreen = false;
            using (var ctrl = new TaskbarBatteryOverlayController(owner, delegate {
                var snap = MakeValidBottomBar();
                snap.AutoHideEnabled = autoHide;
                snap.TaskbarCoveredByFullscreen = gameFullscreen;
                return snap;
            })) {
                ctrl.ApplySettings();
                Check(!ctrl.IsRunning && ctrl.StatusCode == "disabled", "controller stays inactive when disabled");

                var next = DeviceSettings.Current;
                next.TaskbarOverlay.Enabled = true;
                owner.SavePreferences(next);
                ctrl.ApplySettings();
                Check(ctrl.IsRunning && ctrl.StatusCode == "ready" && ctrl.ActiveForm.OverlayVisible, "controller creates and shows overlay when enabled on valid taskbar");

                // Simulate entering fullscreen/borderless game covering taskbar
                gameFullscreen = true;
                ctrl.NotifyDataChanged();
                Check(ctrl.StatusCode == "fullscreen_hidden" && !ctrl.ActiveForm.OverlayVisible, "controller automatically hides overlay during fullscreen game");

                // Recover when leaving game
                gameFullscreen = false;
                ctrl.NotifyDataChanged();
                Check(ctrl.StatusCode == "ready" && ctrl.ActiveForm.OverlayVisible, "controller restores overlay after exiting fullscreen game");

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

                // Live TrayContext tray visibility with DesktopWidget + HideTrayIcon (#1129)
                Check(owner.EffectiveTrayIconVisible, "tray icon starts visible");
                next = DeviceSettings.Current;
                next.DesktopWidget.Enabled = true;
                next.HideTrayIcon = true;
                owner.SavePreferences(next);
                owner.ConfigurationChanged(false);
                Check(!owner.EffectiveTrayIconVisible, "tray icon hides when HideTrayIcon=true and alternative surface is active");
                next = DeviceSettings.Current;
                next.DesktopWidget.Enabled = false;
                owner.SavePreferences(next);
                owner.ConfigurationChanged(false);
                Check(!DeviceSettings.Current.HideTrayIcon && owner.EffectiveTrayIconVisible, "disabling last alternative surface automatically restores tray icon");
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
