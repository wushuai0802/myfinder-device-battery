using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AtkBattery;

namespace AtkBatteryTray {
    // Clean-room experimental taskbar battery overlay.
    // Design constraints:
    // - NEVER calls SetParent into Shell_TrayWnd or injects DLLs/hooks into explorer.exe.
    // - Renders as an independent WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE overlay
    //   positioned adjacent to the notification tray on the primary bottom taskbar.
    // - Strict environment gating: auto-hide taskbar, non-bottom taskbar, missing/third-party shell,
    //   fullscreen obstruction, or insufficient free width degrade cleanly by hiding the overlay and
    //   surfacing an explicit diagnostic reason in Control Center.
    internal sealed class TaskbarEnvironmentSnapshot {
        internal bool HasTaskbarWindow;
        internal bool TaskbarVisible;
        internal bool AutoHideEnabled;
        internal int TaskbarEdge; // Win32 ABE_LEFT=0, ABE_TOP=1, ABE_RIGHT=2, ABE_BOTTOM=3, -1=unknown
        internal Rectangle TaskbarRect;
        internal bool HasTrayNotifyRect;
        internal Rectangle TrayNotifyRect;
        internal Rectangle PrimaryMonitorBounds;
        internal DisplayObstruction Obstruction;
    }

    internal sealed class TaskbarOverlayPlacement {
        internal bool CanDisplay;
        internal string StatusCode;
        internal string StatusMessage;
        internal Point PhysicalLocation;
    }

    internal static class TaskbarEnvironmentProbe {
        const uint ABM_GETSTATE = 0x00000004;
        const uint ABM_GETTASKBARPOS = 0x00000005;
        const int ABS_AUTOHIDE = 0x0000001;
        internal const int EdgeLeft = 0;
        internal const int EdgeTop = 1;
        internal const int EdgeRight = 2;
        internal const int EdgeBottom = 3;

        [StructLayout(LayoutKind.Sequential)]
        struct NativeRect {
            internal int Left, Top, Right, Bottom;
            internal Rectangle ToRectangle() { return Rectangle.FromLTRB(Left, Top, Right, Bottom); }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct APPBARDATA {
            internal int cbSize;
            internal IntPtr hWnd;
            internal uint uCallbackMessage;
            internal uint uEdge;
            internal NativeRect rc;
            internal IntPtr lParam;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        internal static TaskbarEnvironmentSnapshot Capture() {
            var snap = new TaskbarEnvironmentSnapshot {
                TaskbarEdge = -1,
                PrimaryMonitorBounds = Screen.PrimaryScreen != null ? Screen.PrimaryScreen.Bounds : new Rectangle(0, 0, 1920, 1080),
                Obstruction = DisplayObstruction.None
            };

            IntPtr trayWnd = FindWindow("Shell_TrayWnd", null);
            if (trayWnd == IntPtr.Zero) return snap;
            snap.HasTaskbarWindow = true;
            snap.TaskbarVisible = IsWindowVisible(trayWnd);

            Screen taskbarScreen = Screen.FromHandle(trayWnd) ?? Screen.PrimaryScreen;
            if (taskbarScreen != null) snap.PrimaryMonitorBounds = taskbarScreen.Bounds;

            NativeRect barRect = new NativeRect();
            if (GetWindowRect(trayWnd, out barRect)) {
                snap.TaskbarRect = barRect.ToRectangle();
            }

            IntPtr notifyWnd = FindWindowEx(trayWnd, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notifyWnd != IntPtr.Zero) {
                NativeRect notifyRect = new NativeRect();
                if (GetWindowRect(notifyWnd, out notifyRect)) {
                    snap.HasTrayNotifyRect = true;
                    snap.TrayNotifyRect = notifyRect.ToRectangle();
                }
            }

            var abd = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)), hWnd = trayWnd };
            try {
                ulong state = SHAppBarMessage(ABM_GETSTATE, ref abd).ToUInt64();
                snap.AutoHideEnabled = (state & ABS_AUTOHIDE) != 0;
            } catch (Exception) { }

            var posData = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)), hWnd = trayWnd };
            try {
                if (SHAppBarMessage(ABM_GETTASKBARPOS, ref posData) != UIntPtr.Zero) {
                    snap.TaskbarEdge = (int)posData.uEdge;
                    if (snap.TaskbarRect.Width <= 0 || snap.TaskbarRect.Height <= 0) {
                        snap.TaskbarRect = posData.rc.ToRectangle();
                    }
                }
            } catch (Exception) { }

            if (snap.TaskbarEdge < 0 && snap.TaskbarRect.Width > 0 && snap.TaskbarRect.Height > 0) {
                if (snap.TaskbarRect.Width > snap.TaskbarRect.Height && snap.TaskbarRect.Bottom >= snap.PrimaryMonitorBounds.Bottom - 4) {
                    snap.TaskbarEdge = EdgeBottom;
                }
            }

            try {
                var fg = DesktopDisplayWatch.Capture(taskbarScreen);
                snap.Obstruction = DesktopDisplayWatch.Evaluate(fg);
            } catch (Exception) { }

            return snap;
        }
    }

    internal static class TaskbarOverlayEvaluator {
        const int GapBeforeNotifyPx = 10;
        const int MinLeftGuardRatioDenominator = 3; // Reserve left 1/3 for Start/search/pinned apps

        internal static TaskbarOverlayPlacement Evaluate(TaskbarEnvironmentSnapshot snap, Size overlayPhysicalSize, int offsetX) {
            if (snap == null || !snap.HasTaskbarWindow) {
                return Fail("no_explorer_taskbar", "未检测到 Windows 任务栏（可能使用第三方 Shell），已自动隐藏。");
            }
            if (!snap.TaskbarVisible) {
                return Fail("taskbar_hidden", "当前任务栏不可见，叠加层已自动隐藏。");
            }
            if (snap.AutoHideEnabled) {
                return Fail("auto_hide_taskbar", "当前系统启用了自动隐藏任务栏，为避免悬空遮挡已自动降级隐藏。");
            }
            if (snap.TaskbarEdge != TaskbarEnvironmentProbe.EdgeBottom) {
                return Fail("non_bottom_taskbar", "实验版仅支持底部水平任务栏，当前位置已自动降级隐藏。");
            }
            Rectangle bar = snap.TaskbarRect;
            if (bar.Width < 320 || bar.Height < 24 || bar.Height > 120) {
                return Fail("invalid_taskbar_bounds", "当前任务栏尺寸异常，已自动隐藏以防止错位。");
            }
            if (snap.Obstruction == DisplayObstruction.Fullscreen) {
                return Fail("fullscreen_hidden", "检测到主屏全屏应用，任务栏电量已临时隐藏。");
            }

            int w = Math.Max(1, overlayPhysicalSize.Width);
            int h = Math.Max(1, overlayPhysicalSize.Height);
            if (h > bar.Height) {
                return Fail("invalid_taskbar_bounds", "任务栏高度不足以容纳电量条，已自动隐藏。");
            }

            int rightAnchor = (snap.HasTrayNotifyRect && snap.TrayNotifyRect.Left > bar.Left + 80 && snap.TrayNotifyRect.Left < bar.Right)
                ? snap.TrayNotifyRect.Left - GapBeforeNotifyPx
                : bar.Right - Math.Max(140, bar.Width / 8);

            int safeMinX = bar.Left + Math.Max(120, bar.Width / MinLeftGuardRatioDenominator);
            int maxAllowedX = rightAnchor - w;
            if (maxAllowedX < safeMinX) {
                return Fail("insufficient_space", "任务栏通知区左侧空间不足，已自动隐藏以避免遮挡图标。");
            }

            int clampedOffset = Math.Max(-400, Math.Min(400, offsetX));
            int x = maxAllowedX + clampedOffset;
            x = Math.Max(safeMinX, Math.Min(maxAllowedX, x));
            int y = bar.Top + (bar.Height - h) / 2;

            return new TaskbarOverlayPlacement {
                CanDisplay = true,
                StatusCode = "ready",
                StatusMessage = "运行正常 · 已贴合在主屏底部任务栏通知区左侧（独立无焦点层）",
                PhysicalLocation = new Point(x, y)
            };
        }

        static TaskbarOverlayPlacement Fail(string code, string msg) {
            return new TaskbarOverlayPlacement { CanDisplay = false, StatusCode = code, StatusMessage = msg, PhysicalLocation = Point.Empty };
        }
    }

    internal static class TaskbarOverlayRenderer {
        internal sealed class Item {
            internal string Id;
            internal string Name;
            internal string Kind;
            internal int? Percent;
            internal bool Fresh;
            internal bool Charging;
            internal bool Low;
        }

        internal static List<Item> CollectItems(TrayContext owner, int maxDevices) {
            var list = new List<Item>();
            if (owner == null) return list;
            int limit = Math.Max(1, Math.Min(3, maxDevices));
            var pref = DeviceSettings.Current;
            var layout = WirelessLayout.Build(owner, 0, 100000, false);
            foreach (var card in layout.All) {
                if (card == null || card.Id == "empty" || card.AudioOnly) continue;
                var bv = card.Battery;
                list.Add(new Item {
                    Id = card.Id,
                    Name = card.Name ?? "设备",
                    Kind = card.Kind ?? "device",
                    Percent = bv != null ? bv.Percent : (int?)null,
                    Fresh = bv != null && bv.Fresh,
                    Charging = bv != null && bv.Charging,
                    Low = ThemePalette.IsLow(bv, pref.WarningThreshold)
                });
                if (list.Count >= limit) break;
            }
            return list;
        }

        internal static Size ComputeLogicalSize(List<Item> items, bool showIcons) {
            int count = items == null || items.Count == 0 ? 1 : items.Count;
            int itemW = showIcons ? 66 : 46;
            int padX = 8;
            int gap = 6;
            int width = padX * 2 + count * itemW + Math.Max(0, count - 1) * gap;
            return new Size(width, 28);
        }

        internal static void Paint(Graphics g, SizeF size, List<Item> items, bool showIcons, ThemePalette palette, bool dark) {
            var pill = new RectangleF(0.5f, 0.5f, size.Width - 1f, size.Height - 1f);
            Color bg = VisualDesign.HighContrast
                ? SystemColors.Window
                : Color.FromArgb(dark ? 215 : 228, palette.Card);
            Color border = VisualDesign.HighContrast
                ? SystemColors.WindowText
                : Color.FromArgb(dark ? 95 : 85, palette.Border);
            CompactRenderer.Fill(g, pill, bg, 7f);
            using (var path = VisualDesign.RoundRect(pill.X, pill.Y, pill.Width, pill.Height, 7f))
            using (var pen = new Pen(border, 1f)) g.DrawPath(pen, path);

            if (items == null || items.Count == 0) {
                CompactRenderer.DrawLabel(g, "外设 —", new RectangleF(6, 0, size.Width - 12, size.Height), 11.5f, false, palette.Muted, StringAlignment.Center);
                return;
            }

            float itemW = showIcons ? 66f : 46f;
            float gap = 6f;
            float x = 8f;
            for (int i = 0; i < items.Count; i++) {
                var it = items[i];
                Color fg = it.Percent.HasValue
                    ? (it.Fresh ? ThemePalette.LevelColor(it.Percent, dark) : palette.Muted)
                    : palette.Muted;
                if (VisualDesign.HighContrast) fg = SystemColors.WindowText;

                float textX = x;
                float textW = itemW;
                if (showIcons) {
                    var badge = new RectangleF(x, 5f, 18f, 18f);
                    Color badgeBg = VisualDesign.HighContrast ? SystemColors.Highlight : palette.RaisedSurface;
                    Color badgeFg = VisualDesign.HighContrast ? SystemColors.HighlightText : (it.Low ? fg : palette.Foreground);
                    CompactRenderer.Fill(g, badge, badgeBg, 4.5f);
                    CompactRenderer.DrawSymbol(g, ControlCenterTokens.SymbolForKind(it.Kind), new RectangleF(badge.X + 3f, badge.Y + 3f, 12f, 12f), badgeFg);
                    textX = x + 20f;
                    textW = itemW - 20f;
                }

                string pct = it.Percent.HasValue ? (it.Charging ? it.Percent.Value + "%+" : it.Percent.Value + "%") : "--";
                CompactRenderer.DrawLabel(g, pct, new RectangleF(textX, 2f, textW, 18f), 11.5f, true, fg, StringAlignment.Center);

                var barRect = new RectangleF(textX + 4f, 22f, Math.Max(16f, textW - 8f), 2.5f);
                CompactRenderer.Fill(g, barRect, VisualDesign.HighContrast ? SystemColors.ControlDark : palette.Track, 1.25f);
                if (it.Percent.HasValue && it.Percent.Value > 0) {
                    float fillW = barRect.Width * Math.Max(0, Math.Min(100, it.Percent.Value)) / 100f;
                    if (fillW > 0.5f) CompactRenderer.Fill(g, new RectangleF(barRect.X, barRect.Y, fillW, barRect.Height), fg, 1.25f);
                }

                if (i < items.Count - 1) {
                    float sepX = x + itemW + gap / 2f;
                    using (var pen = new Pen(Color.FromArgb(dark ? 70 : 60, palette.Border), 1f)) {
                        g.DrawLine(pen, sepX, 6f, sepX, size.Height - 6f);
                    }
                }
                x += itemW + gap;
            }
        }
    }

    internal sealed class TaskbarBatteryOverlayForm : Form {
        const int WS_EX_LAYERED = 0x00080000;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOPMOST = 0x00000008;
        const int WM_MOUSEACTIVATE = 0x0021;
        const int MA_NOACTIVATE = 3;
        const int WM_DISPLAYCHANGE = 0x007E;
        const int WM_SETTINGCHANGE = 0x001A;
        const int WM_DPICHANGED = 0x02E0;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        const int SW_SHOWNOACTIVATE = 4;

        readonly TrayContext owner;
        readonly LayeredSurface surface = new LayeredSurface();
        readonly System.Windows.Forms.Timer watchTimer = new System.Windows.Forms.Timer { Interval = 800 };
        readonly Func<TaskbarEnvironmentSnapshot> probeFunc;
        readonly ContextMenuStrip contextMenu = new ContextMenuStrip();

        List<TaskbarOverlayRenderer.Item> items = new List<TaskbarOverlayRenderer.Item>();
        TaskbarOverlayPlacement lastPlacement = new TaskbarOverlayPlacement { CanDisplay = false, StatusCode = "disabled", StatusMessage = "任务栏电量未启用" };
        float dpiScale = 1f;
        Point physicalLocation;
        bool overlayVisible;
        string lastRenderKey;

        internal TaskbarBatteryOverlayForm(TrayContext owner, Func<TaskbarEnvironmentSnapshot> probe = null) {
            this.owner = owner;
            probeFunc = probe ?? TaskbarEnvironmentProbe.Capture;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            TopMost = true;
            Text = "MyFinder 任务栏电量";
            AccessibleName = Text;
            Cursor = Cursors.Hand;

            BuildContextMenu();
            watchTimer.Tick += delegate { EvaluateAndRender(); };
            MouseUp += HandleMouseUp;
        }

        internal TaskbarOverlayPlacement LastPlacement { get { return lastPlacement; } }
        internal bool OverlayVisible { get { return overlayVisible; } }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams {
            get {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void WndProc(ref Message m) {
            if (m.Msg == WM_MOUSEACTIVATE) {
                m.Result = (IntPtr)MA_NOACTIVATE;
                return;
            }
            base.WndProc(ref m);
            if (m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_SETTINGCHANGE || m.Msg == WM_DPICHANGED) {
                if (!IsDisposed) EvaluateAndRender();
            }
        }

        void BuildContextMenu() {
            var openPanel = new ToolStripMenuItem("打开设备电量面板");
            openPanel.Click += delegate { if (owner != null) owner.ShowDetailsFromOverlay(); };
            var openCc = new ToolStripMenuItem("打开控制中心 → 显示位置");
            openCc.Click += delegate { if (owner != null) owner.ShowControlCenter(ControlCenterPage.Placement); };
            var toggleIcons = new ToolStripMenuItem("切换图标显示");
            toggleIcons.Click += delegate {
                if (owner == null) return;
                var p = DeviceSettings.Current;
                p.TaskbarOverlay.ShowIcons = !p.TaskbarOverlay.ShowIcons;
                try { owner.SavePreferences(p); owner.ConfigurationChanged(false); } catch (Exception) { }
            };
            var disableItem = new ToolStripMenuItem("关闭任务栏电量");
            disableItem.Click += delegate {
                if (owner == null) return;
                var p = DeviceSettings.Current;
                p.TaskbarOverlay.Enabled = false;
                try { owner.SavePreferences(p); owner.ConfigurationChanged(false); } catch (Exception) { }
            };
            contextMenu.Items.Add(openPanel);
            contextMenu.Items.Add(openCc);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(toggleIcons);
            contextMenu.Items.Add(disableItem);
        }

        void HandleMouseUp(object sender, MouseEventArgs e) {
            if (e.Button == MouseButtons.Left) {
                if (owner != null) owner.ShowDetailsFromOverlay();
            } else if (e.Button == MouseButtons.Right) {
                contextMenu.Show(Cursor.Position);
            }
        }

        internal void StartWatching() {
            if (IsDisposed) return;
            var unused = Handle;
            EvaluateAndRender();
            watchTimer.Start();
        }

        internal void RefreshNow(bool forceRepaint = true) {
            if (IsDisposed) return;
            if (forceRepaint) lastRenderKey = null;
            EvaluateAndRender();
        }

        static string BuildRenderKey(Point loc, Size logicalSize, float scale, bool dark, string themeStyle, bool showIcons, bool highContrast, List<TaskbarOverlayRenderer.Item> list) {
            var sb = new System.Text.StringBuilder();
            sb.Append(loc.X).Append(',').Append(loc.Y).Append('|')
              .Append(logicalSize.Width).Append('x').Append(logicalSize.Height).Append('|')
              .Append(Math.Round(scale * 100)).Append('|')
              .Append(dark).Append('|').Append(themeStyle ?? "").Append('|')
              .Append(showIcons).Append('|').Append(highContrast).Append('|');
            if (list != null) {
                for (int i = 0; i < list.Count; i++) {
                    var it = list[i];
                    sb.Append(it.Kind).Append(':').Append(it.Percent.HasValue ? it.Percent.Value.ToString() : "-").Append(':').Append(it.Fresh).Append(':').Append(it.Charging).Append(';');
                }
            }
            return sb.ToString();
        }

        void EvaluateAndRender() {
            if (IsDisposed) return;
            var pref = DeviceSettings.Current;
            var cfg = pref.TaskbarOverlay ?? new TaskbarOverlaySettings();
            cfg.Normalize();
            items = TaskbarOverlayRenderer.CollectItems(owner, cfg.MaxDevices);
            Size logicalSize = TaskbarOverlayRenderer.ComputeLogicalSize(items, cfg.ShowIcons);

            TaskbarEnvironmentSnapshot snap = probeFunc();
            dpiScale = NativeDisplay.ScaleAt(snap != null && snap.TaskbarRect.Width > 0 ? snap.TaskbarRect.Location : Point.Empty);
            Size physicalSize = LayeredSurface.ComputePhysicalSize(logicalSize, dpiScale);

            var placement = TaskbarOverlayEvaluator.Evaluate(snap, physicalSize, cfg.OffsetX);
            lastPlacement = placement;

            if (!placement.CanDisplay) {
                if (Visible || overlayVisible) Hide();
                overlayVisible = false;
                lastRenderKey = null;
                return;
            }

            physicalLocation = placement.PhysicalLocation;
            bool dark = Style.TaskbarDark;
            ThemePalette palette = ThemePalette.Current(dark);
            string renderKey = BuildRenderKey(physicalLocation, logicalSize, dpiScale, dark, pref.ThemeStyle, cfg.ShowIcons, VisualDesign.HighContrast, items);

            if (overlayVisible && Visible && renderKey == lastRenderKey) {
                return;
            }

            // Commit premultiplied pixels & position BEFORE showing to avoid blank flash.
            surface.Commit(Handle, physicalLocation, logicalSize, dpiScale, 255, delegate(Graphics g, SizeF sz) {
                TaskbarOverlayRenderer.Paint(g, sz, items, cfg.ShowIcons, palette, dark);
            });
            if (!Visible) ShowWindow(Handle, SW_SHOWNOACTIVATE);
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            overlayVisible = true;
            lastRenderKey = renderKey;
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                watchTimer.Stop();
                watchTimer.Dispose();
                contextMenu.Dispose();
                surface.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class TaskbarBatteryOverlayController : IDisposable {
        readonly TrayContext owner;
        readonly Func<TaskbarEnvironmentSnapshot> probeOverride;
        TaskbarBatteryOverlayForm form;
        bool disposed;

        internal TaskbarBatteryOverlayController(TrayContext owner, Func<TaskbarEnvironmentSnapshot> probeOverride = null) {
            this.owner = owner;
            this.probeOverride = probeOverride;
        }

        internal bool IsRunning { get { return !disposed && form != null && !form.IsDisposed; } }
        internal TaskbarBatteryOverlayForm ActiveForm { get { return IsRunning ? form : null; } }

        internal string StatusCode {
            get {
                if (disposed || form == null || form.IsDisposed) return "disabled";
                return form.LastPlacement != null ? form.LastPlacement.StatusCode : "disabled";
            }
        }

        internal string StatusMessage {
            get {
                if (disposed || form == null || form.IsDisposed) return "默认关闭 · 开启后在主屏底部任务栏通知区左侧以独立无焦点浮层显示";
                return form.LastPlacement != null ? form.LastPlacement.StatusMessage : "初始化中";
            }
        }

        internal void ApplySettings() {
            if (disposed) return;
            var cfg = DeviceSettings.Current.TaskbarOverlay;
            if (cfg == null || !cfg.Enabled) {
                CloseForm();
                return;
            }
            if (form == null || form.IsDisposed) {
                form = new TaskbarBatteryOverlayForm(owner, probeOverride);
                form.StartWatching();
            } else {
                form.RefreshNow();
            }
        }

        internal void NotifyDataChanged() {
            if (disposed || form == null || form.IsDisposed) return;
            form.RefreshNow();
        }

        internal void NotifyThemeChanged() {
            if (disposed || form == null || form.IsDisposed) return;
            form.RefreshNow();
        }

        internal void NotifyTaskbarRecreated() {
            if (disposed || form == null || form.IsDisposed) return;
            form.RefreshNow();
        }

        void CloseForm() {
            var current = form;
            form = null;
            if (current != null && !current.IsDisposed) {
                try { current.Close(); } catch (Exception) { }
                try { current.Dispose(); } catch (Exception) { }
            }
        }

        public void Dispose() {
            if (disposed) return;
            disposed = true;
            CloseForm();
        }
    }
}
