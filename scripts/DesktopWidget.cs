using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;
using AtkBattery;

namespace AtkBatteryTray {
    // T50 Desktop Widget: pure WinForms WS_EX_LAYERED / WS_EX_NOACTIVATE floating battery widget.
    // Supports capsule/rings/strip collapsed modes, hover/click expand, 4-edge magnetic dock &
    // auto-hide, per-monitor relative positioning, and layered fullscreen/maximized protection.
    // Interaction design references BrimDeck (Apache-2.0) without copying WPF code.
    internal static class DesktopWidgetGeometry {
        internal static readonly string[] StyleKeys = { "capsule", "rings", "strip" };
        internal static readonly string[] StyleNames = { "胶囊", "迷你环", "指示条" };
        internal static readonly string[] EdgeKeys = { "none", "top", "bottom", "left", "right" };
        internal static readonly string[] EdgeNames = { "自由拖动", "顶部贴边", "底部贴边", "左侧贴边", "右侧贴边" };

        internal static string NormalizeStyle(string style) {
            return Array.IndexOf(StyleKeys, style) >= 0 ? style : "capsule";
        }

        internal static string NormalizeEdge(string edge) {
            return Array.IndexOf(EdgeKeys, edge) >= 0 ? edge : "none";
        }

        internal static Size CollapsedLogicalSize(string style, int deviceCount, bool largeFont, string dockEdge, bool hiddenEdgeTab) {
            string edge = NormalizeEdge(dockEdge);
            if (hiddenEdgeTab && edge != "none") {
                bool vertical = edge == "left" || edge == "right";
                return vertical ? new Size(10, 72) : new Size(72, 10);
            }
            string s = NormalizeStyle(style);
            int count = Math.Max(1, Math.Min(4, deviceCount));
            if (s == "rings") {
                int cell = largeFont ? 46 : 40;
                int pad = 8;
                return new Size(pad * 2 + count * cell + Math.Max(0, count - 1) * 4, largeFont ? 50 : 44);
            }
            if (s == "strip") {
                int itemW = largeFont ? 72 : 64;
                return new Size(14 + count * itemW + Math.Max(0, count - 1) * 6, largeFont ? 30 : 26);
            }
            // capsule (default)
            int chipW = largeFont ? 78 : 68;
            return new Size(16 + count * chipW + Math.Max(0, count - 1) * 8, largeFont ? 42 : 36);
        }

        internal static Size ExpandedLogicalSize(int deviceCount, bool largeFont) {
            int count = Math.Max(1, Math.Min(6, deviceCount));
            int width = largeFont ? 284 : 254;
            int rowH = largeFont ? 50 : 44;
            int headerH = largeFont ? 32 : 28;
            int footerH = largeFont ? 34 : 30;
            return new Size(width, headerH + count * rowH + footerH + 14);
        }

        internal static string DetectSnapEdge(Rectangle physicalRect, Rectangle workArea, int threshold = 32) {
            if (workArea.Width <= 0 || workArea.Height <= 0) return "none";
            // Treat dragging slightly past a screen edge as distance 0 so pushing against an edge always snaps.
            int dTop = physicalRect.Top <= workArea.Top ? 0 : physicalRect.Top - workArea.Top;
            int dBottom = physicalRect.Bottom >= workArea.Bottom ? 0 : workArea.Bottom - physicalRect.Bottom;
            int dLeft = physicalRect.Left <= workArea.Left ? 0 : physicalRect.Left - workArea.Left;
            int dRight = physicalRect.Right >= workArea.Right ? 0 : workArea.Right - physicalRect.Right;
            int best = Math.Min(Math.Min(dTop, dBottom), Math.Min(dLeft, dRight));
            if (best > threshold) return "none";
            if (best == dLeft) return "left";
            if (best == dRight) return "right";
            if (best == dTop) return "top";
            return "bottom";
        }

        internal static void ComputeAnchorRatios(Point physicalLocation, Size physicalSize, Rectangle workArea, out double xRatio, out double yRatio) {
            int spanX = Math.Max(1, workArea.Width - physicalSize.Width);
            int spanY = Math.Max(1, workArea.Height - physicalSize.Height);
            xRatio = Math.Max(0.0, Math.Min(1.0, (physicalLocation.X - workArea.Left) / (double)spanX));
            yRatio = Math.Max(0.0, Math.Min(1.0, (physicalLocation.Y - workArea.Top) / (double)spanY));
        }

        internal static Point ComputePhysicalLocation(string dockEdge, Size physicalSize, Rectangle workArea, double xRatio, double yRatio, bool hiddenEdgeTab) {
            if (workArea.Width <= 0 || workArea.Height <= 0) return Point.Empty;
            double rx = Math.Max(0.0, Math.Min(1.0, xRatio));
            double ry = Math.Max(0.0, Math.Min(1.0, yRatio));
            int maxX = Math.Max(workArea.Left, workArea.Right - physicalSize.Width);
            int maxY = Math.Max(workArea.Top, workArea.Bottom - physicalSize.Height);
            int x = workArea.Left + (int)Math.Round((maxX - workArea.Left) * rx);
            int y = workArea.Top + (int)Math.Round((maxY - workArea.Top) * ry);

            string edge = NormalizeEdge(dockEdge);
            int margin = hiddenEdgeTab ? 0 : 6;
            if (edge == "top") y = workArea.Top + margin;
            else if (edge == "bottom") y = workArea.Bottom - physicalSize.Height - margin;
            else if (edge == "left") x = workArea.Left + margin;
            else if (edge == "right") x = workArea.Right - physicalSize.Width - margin;

            x = Math.Max(workArea.Left, Math.Min(x, maxX));
            y = Math.Max(workArea.Top, Math.Min(y, maxY));
            return new Point(x, y);
        }
    }

    internal sealed class DesktopWidgetEntry {
        internal string Id;
        internal string Name;
        internal string Kind;
        internal string Subtitle;
        internal int? Percent;
        internal bool Fresh;
        internal bool Charging;
        internal bool Low;
    }

    internal sealed class DesktopWidgetRenderer {
        internal static void Paint(Graphics g, SizeF logicalSize, List<DesktopWidgetEntry> entries, ThemePalette palette, bool dark, DesktopWidgetSettings cfg, bool expanded, bool edgeStripMode, bool hiddenEdgeTab, int hoveredRow) {
            if (hiddenEdgeTab) {
                PaintHiddenTab(g, logicalSize, entries, palette, dark, DesktopWidgetGeometry.NormalizeEdge(cfg != null ? cfg.DockEdge : "right"));
                return;
            }
            if (expanded) {
                PaintExpanded(g, logicalSize, entries, palette, dark, hoveredRow);
                return;
            }
            string style = edgeStripMode ? "strip" : DesktopWidgetGeometry.NormalizeStyle(cfg.Style);
            if (style == "rings") PaintRings(g, logicalSize, entries, palette, dark);
            else if (style == "strip") PaintStrip(g, logicalSize, entries, palette, dark);
            else PaintCapsule(g, logicalSize, entries, palette, dark);
        }

        static void PaintSurface(Graphics g, SizeF size, float radius, ThemePalette palette, bool dark) {
            var rect = new RectangleF(1.5f, 1.5f, Math.Max(4, size.Width - 3f), Math.Max(4, size.Height - 3f));
            if (!VisualDesign.HighContrast) {
                // Dual-layer halo ensures legibility on both light and dark wallpapers
                var shadow = new RectangleF(rect.X, rect.Y + 1f, rect.Width, rect.Height);
                CompactRenderer.Fill(g, shadow, Color.FromArgb(dark ? 115 : 68, 0, 0, 0), radius);
            }
            CompactRenderer.Fill(g, rect, palette.Card, radius);
            if (!VisualDesign.HighContrast && dark) {
                using (var inner = VisualDesign.RoundRect(rect.X + 1f, rect.Y + 1f, rect.Width - 2f, rect.Height - 2f, Math.Max(2f, radius - 1f)))
                using (var rim = new Pen(Color.FromArgb(26, 255, 255, 255), 1f)) {
                    g.DrawPath(rim, inner);
                }
            }
            Color borderColor = VisualDesign.HighContrast
                ? palette.Foreground
                : (dark ? Color.FromArgb(145, 0, 0, 0) : Color.FromArgb(130, palette.Border));
            using (var path = VisualDesign.RoundRect(rect.X, rect.Y, rect.Width, rect.Height, radius))
            using (var pen = new Pen(borderColor, 1f)) {
                g.DrawPath(pen, path);
            }
        }

        static GraphicsPath CreateEdgeTabPath(RectangleF r, string edge, float radius) {
            float rad = Math.Max(1.5f, Math.Min(radius, Math.Min(r.Width, r.Height) * 0.5f));
            float d = rad * 2f;
            bool roundTL = edge != "left" && edge != "top";
            bool roundTR = edge != "right" && edge != "top";
            bool roundBR = edge != "right" && edge != "bottom";
            bool roundBL = edge != "left" && edge != "bottom";

            var path = new GraphicsPath();
            if (roundTL) path.AddArc(r.Left, r.Top, d, d, 180, 90);
            else path.AddLine(r.Left, r.Top, r.Left, r.Top);

            if (roundTR) path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            else path.AddLine(r.Right, r.Top, r.Right, r.Top);

            if (roundBR) path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            else path.AddLine(r.Right, r.Bottom, r.Right, r.Bottom);

            if (roundBL) path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            else path.AddLine(r.Left, r.Bottom, r.Left, r.Bottom);

            path.CloseFigure();
            return path;
        }

        // Wallpaper-proof 3-layer edge handle:
        // 1. Dark outer shadow outline (visible on pure white/bright wallpapers)
        // 2. High-opacity neutral charcoal pill + luminous inner rim (visible on pure black/vivid wallpapers)
        // 3. Centered battery-level status core bar (+ bright white center dot when charging)
        static void PaintHiddenTab(Graphics g, SizeF size, List<DesktopWidgetEntry> entries, ThemePalette palette, bool dark, string edge) {
            bool vertical = edge == "left" || edge == "right" || size.Height > size.Width;
            var outerRect = new RectangleF(0.5f, 0.5f, Math.Max(4, size.Width - 1f), Math.Max(4, size.Height - 1f));
            float outerRadius = vertical ? Math.Min(5f, outerRect.Width * 0.65f) : Math.Min(5f, outerRect.Height * 0.65f);

            int? minPct = null;
            bool anyLow = false;
            bool anyCharging = false;
            foreach (var e in entries) {
                if (e.Percent.HasValue && e.Fresh) {
                    if (!minPct.HasValue || e.Percent.Value < minPct.Value) minPct = e.Percent.Value;
                    if (e.Low) anyLow = true;
                    if (e.Charging) anyCharging = true;
                }
            }
            Color coreColor = anyLow
                ? Color.FromArgb(248, 108, 108)
                : minPct.HasValue
                    ? (minPct.Value <= 30 ? Color.FromArgb(245, 192, 78) : Color.FromArgb(74, 218, 148))
                    : Color.FromArgb(164, 174, 188);

            Color shellFill = VisualDesign.HighContrast ? SystemColors.Window : Color.FromArgb(242, 24, 27, 34);
            Color outerBorder = VisualDesign.HighContrast ? SystemColors.WindowText : Color.FromArgb(175, 0, 0, 0);
            Color innerRim = VisualDesign.HighContrast ? SystemColors.Highlight : Color.FromArgb(95, 255, 255, 255);

            using (var shellPath = CreateEdgeTabPath(outerRect, edge, outerRadius)) {
                using (var fillBrush = new SolidBrush(shellFill)) g.FillPath(fillBrush, shellPath);
                using (var outerPen = new Pen(outerBorder, 1f)) g.DrawPath(outerPen, shellPath);
            }

            var rimRect = new RectangleF(outerRect.X + 1f, outerRect.Y + 1f, Math.Max(2f, outerRect.Width - 2f), Math.Max(2f, outerRect.Height - 2f));
            using (var rimPath = CreateEdgeTabPath(rimRect, edge, Math.Max(1.5f, outerRadius - 1f)))
            using (var rimPen = new Pen(innerRim, 1f)) {
                g.DrawPath(rimPen, rimPath);
            }

            // Centered vibrant battery status core pill
            RectangleF coreRect;
            if (vertical) {
                float coreW = 3f;
                float coreH = Math.Max(14f, outerRect.Height - 20f);
                float coreX = edge == "left" ? outerRect.Right - coreW - 2.5f : edge == "right" ? outerRect.Left + 2.5f : outerRect.X + (outerRect.Width - coreW) / 2f;
                coreRect = new RectangleF(coreX, outerRect.Y + (outerRect.Height - coreH) / 2f, coreW, coreH);
            } else {
                float coreH = 3f;
                float coreW = Math.Max(14f, outerRect.Width - 20f);
                float coreY = edge == "top" ? outerRect.Bottom - coreH - 2.5f : edge == "bottom" ? outerRect.Top + 2.5f : outerRect.Y + (outerRect.Height - coreH) / 2f;
                coreRect = new RectangleF(outerRect.X + (outerRect.Width - coreW) / 2f, coreY, coreW, coreH);
            }
            CompactRenderer.Fill(g, coreRect, coreColor, 1.5f);

            if (anyCharging) {
                float dot = 3f;
                using (var white = new SolidBrush(Color.White)) {
                    g.FillEllipse(white, coreRect.X + (coreRect.Width - dot) / 2f, coreRect.Y + (coreRect.Height - dot) / 2f, dot, dot);
                }
            }
        }

        static void PaintCapsule(Graphics g, SizeF size, List<DesktopWidgetEntry> entries, ThemePalette palette, bool dark) {
            PaintSurface(g, size, size.Height / 2f, palette, dark);
            if (entries.Count == 0) {
                CompactRenderer.DrawLabel(g, "无设备", new RectangleF(6, 0, size.Width - 12, size.Height), 12f, false, palette.Muted, StringAlignment.Center);
                return;
            }
            float padX = 10f;
            float itemW = (size.Width - padX * 2f) / entries.Count;
            for (int i = 0; i < entries.Count; i++) {
                var e = entries[i];
                float x = padX + i * itemW;
                if (i > 0) {
                    using (var pen = new Pen(Color.FromArgb(dark ? 55 : 45, palette.Border), 1f)) g.DrawLine(pen, x, 8f, x, size.Height - 8f);
                }
                Color level = e.Percent.HasValue && e.Fresh ? ThemePalette.LevelColor(e.Percent, dark) : palette.Muted;
                float ringSize = VisualDesign.LargeFont ? 22f : 20f;
                var ring = new RectangleF(x + 4f, (size.Height - ringSize) / 2f, ringSize, ringSize);
                using (var trackPen = new Pen(palette.Track, 2.2f)) g.DrawEllipse(trackPen, ring);
                if (e.Percent.HasValue) {
                    float sweep = 360f * Math.Max(0, Math.Min(100, e.Percent.Value)) / 100f;
                    if (sweep > 0.5f) {
                        using (var arcPen = new Pen(level, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(arcPen, ring, -90f, sweep);
                    }
                }
                float symSize = VisualDesign.LargeFont ? 11f : 10f;
                CompactRenderer.DrawSymbol(g, ControlCenterTokens.SymbolForKind(e.Kind), new RectangleF(ring.X + (ring.Width - symSize) / 2f, ring.Y + (ring.Height - symSize) / 2f, symSize, symSize), e.Low ? level : palette.Foreground);

                string txt = e.Percent.HasValue ? (e.Charging ? e.Percent.Value + "%+" : e.Percent.Value + "%") : "--";
                CompactRenderer.DrawLabel(g, txt, new RectangleF(ring.Right + 3f, 0, Math.Max(22f, itemW - ringSize - 8f), size.Height), VisualDesign.LargeFont ? 13f : 12f, true, e.Low ? level : palette.Foreground, StringAlignment.Center);
            }
        }

        static void PaintRings(Graphics g, SizeF size, List<DesktopWidgetEntry> entries, ThemePalette palette, bool dark) {
            PaintSurface(g, size, 12f, palette, dark);
            if (entries.Count == 0) {
                CompactRenderer.DrawLabel(g, "—", new RectangleF(0, 0, size.Width, size.Height), 13f, false, palette.Muted, StringAlignment.Center);
                return;
            }
            float pad = 8f;
            float cell = (size.Width - pad * 2f) / entries.Count;
            float ringSize = Math.Min(size.Height - 10f, cell - 4f);
            for (int i = 0; i < entries.Count; i++) {
                var e = entries[i];
                float cx = pad + i * cell + (cell - ringSize) / 2f;
                float cy = (size.Height - ringSize) / 2f;
                var ring = new RectangleF(cx, cy, ringSize, ringSize);
                Color level = e.Percent.HasValue && e.Fresh ? ThemePalette.LevelColor(e.Percent, dark) : palette.Muted;
                using (var trackPen = new Pen(palette.Track, 3f)) g.DrawEllipse(trackPen, ring);
                if (e.Percent.HasValue) {
                    float sweep = 360f * Math.Max(0, Math.Min(100, e.Percent.Value)) / 100f;
                    if (sweep > 0.5f) {
                        using (var arcPen = new Pen(level, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(arcPen, ring, -90f, sweep);
                    }
                }
                string digits = e.Percent.HasValue ? e.Percent.Value.ToString() : "--";
                CompactRenderer.DrawLabel(g, digits, ring, VisualDesign.LargeFont ? 12f : 10.5f, true, e.Low ? level : palette.Foreground, StringAlignment.Center);
            }
        }

        static void PaintStrip(Graphics g, SizeF size, List<DesktopWidgetEntry> entries, ThemePalette palette, bool dark) {
            PaintSurface(g, size, size.Height / 2f, palette, dark);
            if (entries.Count == 0) {
                CompactRenderer.DrawLabel(g, "MyFinder", new RectangleF(6, 0, size.Width - 12, size.Height), 11f, false, palette.Muted, StringAlignment.Center);
                return;
            }
            float padX = 8f;
            float itemW = (size.Width - padX * 2f) / entries.Count;
            for (int i = 0; i < entries.Count; i++) {
                var e = entries[i];
                float x = padX + i * itemW;
                if (i > 0) {
                    using (var sep = new Pen(Color.FromArgb(dark ? 50 : 40, palette.Border), 1f)) g.DrawLine(sep, x - 2f, 6f, x - 2f, size.Height - 6f);
                }
                Color level = e.Percent.HasValue && e.Fresh ? ThemePalette.LevelColor(e.Percent, dark) : palette.Muted;
                CompactRenderer.DrawSymbol(g, ControlCenterTokens.SymbolForKind(e.Kind), new RectangleF(x + 2f, (size.Height - 12f) / 2f, 12f, 12f), level);
                string txt = e.Percent.HasValue ? (e.Charging ? e.Percent.Value + "%+" : e.Percent.Value + "%") : "--";
                CompactRenderer.DrawLabel(g, txt, new RectangleF(x + 16f, 0, Math.Max(22f, itemW - 18f), size.Height), VisualDesign.LargeFont ? 12f : 11f, true, e.Low ? level : palette.Foreground, StringAlignment.Near);
            }
        }

        static void PaintExpanded(Graphics g, SizeF size, List<DesktopWidgetEntry> entries, ThemePalette palette, bool dark, int hoveredRow) {
            PaintSurface(g, size, 12f, palette, dark);
            int headerH = VisualDesign.LargeFont ? 32 : 28;
            int rowH = VisualDesign.LargeFont ? 50 : 44;
            int footerH = VisualDesign.LargeFont ? 34 : 30;

            CompactRenderer.DrawLabel(g, "外设电量", new RectangleF(14, 6, size.Width - 80, headerH - 4), 12.5f, true, palette.Muted, StringAlignment.Near);
            CompactRenderer.DrawLabel(g, "点击收起", new RectangleF(size.Width - 78, 6, 66, headerH - 4), 11f, false, palette.Muted, StringAlignment.Far);

            float y = headerH + 2f;
            if (entries.Count == 0) {
                CompactRenderer.DrawLabel(g, "暂未检测到兼容设备", new RectangleF(14, y, size.Width - 28, rowH), 12.5f, false, palette.Muted, StringAlignment.Center);
                y += rowH;
            } else {
                for (int i = 0; i < entries.Count; i++) {
                    var e = entries[i];
                    var rowRect = new RectangleF(8, y, size.Width - 16, rowH - 2);
                    if (hoveredRow == i && !VisualDesign.HighContrast) {
                        CompactRenderer.Fill(g, rowRect, palette.RaisedSurface, 8f);
                    }
                    Color level = e.Percent.HasValue && e.Fresh ? ThemePalette.LevelColor(e.Percent, dark) : palette.Muted;
                    var tile = new RectangleF(rowRect.X + 6, rowRect.Y + (rowRect.Height - 28) / 2f, 28, 28);
                    CompactRenderer.Fill(g, tile, palette.RaisedSurface, 7f);
                    CompactRenderer.DrawSymbol(g, ControlCenterTokens.SymbolForKind(e.Kind), new RectangleF(tile.X + 6, tile.Y + 6, 16, 16), e.Low ? level : palette.Foreground);

                    CompactRenderer.DrawLabel(g, e.Name, new RectangleF(tile.Right + 8, rowRect.Y + 5, rowRect.Width - 104, 18), 12.5f, true, palette.Foreground, StringAlignment.Near);
                    CompactRenderer.DrawLabel(g, e.Subtitle ?? "", new RectangleF(tile.Right + 8, rowRect.Y + 23, rowRect.Width - 104, 16), 10.5f, false, palette.Muted, StringAlignment.Near);

                    string pct = e.Percent.HasValue ? e.Percent.Value + "%" : "—";
                    CompactRenderer.DrawLabel(g, pct, new RectangleF(rowRect.Right - 62, rowRect.Y + 5, 54, 19), 14f, true, level, StringAlignment.Far);
                    var bar = new RectangleF(rowRect.Right - 54, rowRect.Y + 29, 46, 3);
                    CompactRenderer.Fill(g, bar, palette.Track, 1.5f);
                    if (e.Percent.HasValue) {
                        float fw = bar.Width * Math.Max(0, Math.Min(100, e.Percent.Value)) / 100f;
                        if (fw > 0) CompactRenderer.Fill(g, new RectangleF(bar.X, bar.Y, fw, bar.Height), level, 1.5f);
                    }
                    y += rowH;
                }
            }

            float footY = size.Height - footerH - 4;
            using (var pen = new Pen(palette.Border, 1f)) g.DrawLine(pen, 12, footY, size.Width - 12, footY);
            CompactRenderer.DrawLabel(g, "刷新", new RectangleF(12, footY + 2, (size.Width - 24) / 2f, footerH - 2), 11.5f, false, palette.Muted, StringAlignment.Center);
            CompactRenderer.DrawLabel(g, "控制中心", new RectangleF(12 + (size.Width - 24) / 2f, footY + 2, (size.Width - 24) / 2f, footerH - 2), 11.5f, false, palette.Foreground, StringAlignment.Center);
        }
    }

    internal sealed class DesktopWidgetForm : Form {
        readonly TrayContext owner;
        readonly LayeredSurface surface = new LayeredSurface();
        readonly Timer watchTimer = new Timer { Interval = 650 };
        readonly Timer hideDelayTimer = new Timer { Interval = 450 };
        readonly ContextMenuStrip menu = new ContextMenuStrip();

        bool expanded, hovered, dragging, dragMoved, disposed;
        bool autoHiddenOnEdge;
        WidgetVisibilityMode visibilityMode = WidgetVisibilityMode.Normal;
        Point dragStartCursor, dragStartLocation, dragPhysicalLocation;
        Size logicalSize;
        Rectangle currentPhysicalBounds;
        float currentDpiScale = 1f;
        int hoveredRow = -1;
        string lastRenderSignature;
        List<DesktopWidgetEntry> cachedEntries = new List<DesktopWidgetEntry>();

        internal DesktopWidgetForm(TrayContext owner) {
            this.owner = owner;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = DeviceSettings.Current.DesktopWidget.TopMost;
            Name = "MyFinderDesktopWidgetForm";

            BuildContextMenu();
            watchTimer.Tick += delegate { EvaluateDisplayState(); };
            hideDelayTimer.Tick += delegate {
                hideDelayTimer.Stop();
                if (!hovered && !dragging && !expanded && !menu.Visible) {
                    var cfg = DeviceSettings.Current.DesktopWidget;
                    if (cfg.AutoHideOnEdge && DesktopWidgetGeometry.NormalizeEdge(cfg.DockEdge) != "none") {
                        autoHiddenOnEdge = true;
                        RenderNow(true);
                    }
                }
            };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams {
            get {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                if (DeviceSettings.Current.DesktopWidget.TopMost) cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
                return cp;
            }
        }

        internal bool IsExpanded { get { return expanded; } }
        internal bool IsAutoHiddenOnEdge { get { return autoHiddenOnEdge; } }
        internal WidgetVisibilityMode CurrentVisibilityMode { get { return visibilityMode; } }
        internal Size LogicalSize { get { return logicalSize; } }

        void BuildContextMenu() {
            ThemedContextMenu.Apply(menu);
            menu.Opening += delegate {
                menu.Items.Clear();
                var cfg = DeviceSettings.Current.DesktopWidget;
                var styleMenu = new ToolStripMenuItem("收起形态");
                for (int i = 0; i < DesktopWidgetGeometry.StyleKeys.Length; i++) {
                    string key = DesktopWidgetGeometry.StyleKeys[i];
                    var item = new ToolStripMenuItem(DesktopWidgetGeometry.StyleNames[i]) { Checked = DesktopWidgetGeometry.NormalizeStyle(cfg.Style) == key };
                    item.Click += delegate { UpdateWidgetConfig(delegate(DesktopWidgetSettings w) { w.Style = key; }); };
                    styleMenu.DropDownItems.Add(item);
                }
                menu.Items.Add(styleMenu);

                var edgeMenu = new ToolStripMenuItem("贴边位置");
                for (int i = 0; i < DesktopWidgetGeometry.EdgeKeys.Length; i++) {
                    string key = DesktopWidgetGeometry.EdgeKeys[i];
                    var item = new ToolStripMenuItem(DesktopWidgetGeometry.EdgeNames[i]) { Checked = DesktopWidgetGeometry.NormalizeEdge(cfg.DockEdge) == key };
                    item.Click += delegate { UpdateWidgetConfig(delegate(DesktopWidgetSettings w) { w.DockEdge = key; }); };
                    edgeMenu.DropDownItems.Add(item);
                }
                menu.Items.Add(edgeMenu);

                var autoHide = new ToolStripMenuItem("贴边时自动隐藏") { Checked = cfg.AutoHideOnEdge };
                autoHide.Click += delegate { UpdateWidgetConfig(delegate(DesktopWidgetSettings w) { w.AutoHideOnEdge = !w.AutoHideOnEdge; }); };
                menu.Items.Add(autoHide);

                var topMost = new ToolStripMenuItem("总在最前") { Checked = cfg.TopMost };
                topMost.Click += delegate { UpdateWidgetConfig(delegate(DesktopWidgetSettings w) { w.TopMost = !w.TopMost; }); };
                menu.Items.Add(topMost);

                menu.Items.Add(new ToolStripSeparator());
                var openCc = new ToolStripMenuItem("打开控制中心…");
                openCc.Click += delegate { owner.ShowControlCenter(ControlCenterPage.Placement); };
                menu.Items.Add(openCc);

                var closeItem = new ToolStripMenuItem("关闭桌面小组件");
                closeItem.Click += delegate { UpdateWidgetConfig(delegate(DesktopWidgetSettings w) { w.Enabled = false; }); };
                menu.Items.Add(closeItem);
            };
        }

        void UpdateWidgetConfig(Action<DesktopWidgetSettings> mutate) {
            var pref = DeviceSettings.Current;
            mutate(pref.DesktopWidget);
            try {
                owner.SavePreferences(pref);
                owner.ConfigurationChanged(false);
            } catch (Exception) { }
        }

        internal List<DesktopWidgetEntry> CollectEntries(int maxCount) {
            var list = new List<DesktopWidgetEntry>();
            var layout = WirelessLayout.Build(owner, 0, 100000, false);
            int threshold = DeviceSettings.Current.WarningThreshold;
            foreach (var card in layout.All) {
                if (card == null || card.Id == "empty" || card.AudioOnly) continue;
                var bv = card.Battery;
                list.Add(new DesktopWidgetEntry {
                    Id = card.Id,
                    Name = card.Name,
                    Kind = card.Kind ?? "device",
                    Subtitle = card.Device != null ? WirelessLayout.Subtitle(card.Device, bv) : (bv != null && bv.Fresh ? "2.4 GHz · 在线" : "暂未取得读数"),
                    Percent = bv != null ? bv.Percent : (int?)null,
                    Fresh = bv != null && bv.Fresh,
                    Charging = bv != null && bv.Charging,
                    Low = ThemePalette.IsLow(bv, threshold)
                });
                if (list.Count >= maxCount) break;
            }
            return list;
        }

        Screen ResolveTargetScreen(DesktopWidgetSettings cfg) {
            if (!String.IsNullOrEmpty(cfg.MonitorDeviceName)) {
                foreach (var s in Screen.AllScreens) {
                    if (String.Equals(s.DeviceName, cfg.MonitorDeviceName, StringComparison.OrdinalIgnoreCase)) return s;
                }
            }
            return IsHandleCreated ? Screen.FromHandle(Handle) : Screen.PrimaryScreen;
        }

        internal void StartWatching() {
            if (disposed) return;
            var cfg = DeviceSettings.Current.DesktopWidget;
            autoHiddenOnEdge = cfg.AutoHideOnEdge && DesktopWidgetGeometry.NormalizeEdge(cfg.DockEdge) != "none";
            RenderNow(true);
            if (!watchTimer.Enabled) watchTimer.Start();
        }

        internal void RefreshFromSettingsOrData(bool force) {
            if (disposed || IsDisposed || Disposing) return;
            if (InvokeRequired) {
                if (!IsHandleCreated) return;
                try { BeginInvoke((MethodInvoker)delegate { RefreshFromSettingsOrData(force); }); }
                catch (InvalidOperationException) { }
                return;
            }
            var cfg = DeviceSettings.Current.DesktopWidget;
            if (TopMost != cfg.TopMost) TopMost = cfg.TopMost;
            bool docked = DesktopWidgetGeometry.NormalizeEdge(cfg.DockEdge) != "none";
            if (!docked || !cfg.AutoHideOnEdge) {
                autoHiddenOnEdge = false;
                hideDelayTimer.Stop();
            } else if (!expanded && !dragging && !hovered && !autoHiddenOnEdge) {
                hideDelayTimer.Stop();
                hideDelayTimer.Start();
            }
            RenderNow(force);
        }

        void EvaluateDisplayState() {
            if (disposed || IsDisposed || dragging) return;
            var cfg = DeviceSettings.Current.DesktopWidget;
            var screen = ResolveTargetScreen(cfg);
            var snap = DesktopDisplayWatch.Capture(screen);
            var obs = DesktopDisplayWatch.Evaluate(snap);
            var nextMode = DesktopDisplayWatch.ResolveMode(obs, cfg.FullscreenPolicy, cfg.CompactOnMaximized);
            if (nextMode != visibilityMode) {
                visibilityMode = nextMode;
                if (visibilityMode == WidgetVisibilityMode.Hidden) {
                    expanded = false;
                    if (Visible) Hide();
                    return;
                }
                if (visibilityMode == WidgetVisibilityMode.EdgeStrip) expanded = false;
                if (!Visible) Show();
                RenderNow(true);
            }
        }

        internal void RenderNow(bool force) {
            if (disposed || IsDisposed || !IsHandleCreated) return;
            if (visibilityMode == WidgetVisibilityMode.Hidden) return;

            var cfg = DeviceSettings.Current.DesktopWidget;
            int maxDev = expanded ? 6 : Math.Max(1, Math.Min(4, cfg.MaxDevices));
            cachedEntries = CollectEntries(maxDev);

            bool dark = Style.Dark;
            var palette = ThemePalette.Current(dark);
            bool hiddenTab = !expanded && !hovered && autoHiddenOnEdge && DesktopWidgetGeometry.NormalizeEdge(cfg.DockEdge) != "none";
            bool stripMode = visibilityMode == WidgetVisibilityMode.EdgeStrip;

            var screen = ResolveTargetScreen(cfg);
            var workArea = screen.WorkingArea;
            currentDpiScale = NativeDisplay.ScaleAt(new Point(workArea.Left + workArea.Width / 2, workArea.Top + workArea.Height / 2));

            logicalSize = expanded
                ? DesktopWidgetGeometry.ExpandedLogicalSize(cachedEntries.Count, VisualDesign.LargeFont)
                : DesktopWidgetGeometry.CollapsedLogicalSize(stripMode ? "strip" : cfg.Style, cachedEntries.Count, VisualDesign.LargeFont, cfg.DockEdge, hiddenTab);

            var physSize = LayeredSurface.ComputePhysicalSize(logicalSize, currentDpiScale);
            Point physPos = dragging ? dragPhysicalLocation : DesktopWidgetGeometry.ComputePhysicalLocation(cfg.DockEdge, physSize, workArea, cfg.AnchorXRatio, cfg.AnchorYRatio, hiddenTab);

            string sig = BuildRenderSignature(cfg, dark, hiddenTab, stripMode, physPos, physSize);
            if (!force && sig == lastRenderSignature) return;
            lastRenderSignature = sig;

            byte alpha = (byte)Math.Max(140, Math.Min(255, (int)Math.Round(cfg.OpacityPercent * 2.55)));
            if (hiddenTab) alpha = 210;

            // UpdateLayeredWindow atomically commits physical position, size and alpha.
            // Avoid redundant WinForms Location/Size writes that can trigger double-DPI scaling.
            surface.Commit(Handle, physPos, logicalSize, currentDpiScale, alpha, delegate(Graphics g, SizeF sz) {
                DesktopWidgetRenderer.Paint(g, sz, cachedEntries, palette, dark, cfg, expanded, stripMode, hiddenTab, hoveredRow);
            });
            currentPhysicalBounds = new Rectangle(physPos, physSize);
        }

        string BuildRenderSignature(DesktopWidgetSettings cfg, bool dark, bool hiddenTab, bool stripMode, Point pos, Size size) {
            var sb = new StringBuilder();
            sb.Append(dark).Append('|').Append(DeviceSettings.Current.ThemeStyle).Append('|').Append(VisualDesign.LargeFont).Append('|').Append(VisualDesign.HighContrast).Append('|');
            sb.Append(cfg.Style).Append('|').Append(cfg.DockEdge).Append('|').Append(cfg.OpacityPercent).Append('|').Append(expanded).Append('|').Append(hiddenTab).Append('|').Append(stripMode).Append('|');
            sb.Append(hoveredRow).Append('|').Append(pos.X).Append(',').Append(pos.Y).Append('|').Append(size.Width).Append('x').Append(size.Height).Append('|');
            foreach (var e in cachedEntries) sb.Append(e.Id).Append(':').Append(e.Name).Append(':').Append(e.Percent).Append(':').Append(e.Fresh).Append(':').Append(e.Charging).Append(';');
            return sb.ToString();
        }

        protected override void WndProc(ref Message m) {
            if (m.Msg == 0x0021) { // WM_MOUSEACTIVATE
                m.Result = new IntPtr(3); // MA_NOACTIVATE
                return;
            }
            if (m.Msg == 0x007E || m.Msg == 0x02E0) { // WM_DISPLAYCHANGE / WM_DPICHANGED
                if (m.Msg == 0x02E0) {
                    m.Result = IntPtr.Zero;
                    if (!dragging) RenderNow(true);
                    return;
                }
                base.WndProc(ref m);
                if (!dragging) RenderNow(true);
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnMouseEnter(EventArgs e) {
            base.OnMouseEnter(e);
            hovered = true;
            hideDelayTimer.Stop();
            if (autoHiddenOnEdge) {
                autoHiddenOnEdge = false;
                RenderNow(true);
            }
        }

        protected override void OnMouseLeave(EventArgs e) {
            base.OnMouseLeave(e);
            if (!dragging && !currentPhysicalBounds.IsEmpty && currentPhysicalBounds.Contains(Cursor.Position)) {
                return;
            }
            hovered = false;
            hoveredRow = -1;
            if (expanded) RenderNow(false);
            var cfg = DeviceSettings.Current.DesktopWidget;
            if (!expanded && cfg.AutoHideOnEdge && DesktopWidgetGeometry.NormalizeEdge(cfg.DockEdge) != "none") {
                hideDelayTimer.Stop();
                hideDelayTimer.Start();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e) {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) {
                menu.Show(Cursor.Position);
                return;
            }
            if (e.Button == MouseButtons.Left) {
                dragging = true;
                dragMoved = false;
                dragStartCursor = Cursor.Position;
                dragStartLocation = currentPhysicalBounds.IsEmpty ? Location : currentPhysicalBounds.Location;
                dragPhysicalLocation = dragStartLocation;
                Capture = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e) {
            base.OnMouseMove(e);
            if (dragging) {
                var now = Cursor.Position;
                int dx = now.X - dragStartCursor.X;
                int dy = now.Y - dragStartCursor.Y;
                if (!dragMoved && (Math.Abs(dx) > 4 || Math.Abs(dy) > 4)) dragMoved = true;
                if (dragMoved) {
                    dragPhysicalLocation = new Point(dragStartLocation.X + dx, dragStartLocation.Y + dy);
                    RenderNow(true);
                }
                return;
            }
            if (expanded) {
                float ly = e.Y / Math.Max(1f, currentDpiScale);
                int headerH = VisualDesign.LargeFont ? 32 : 28;
                int rowH = VisualDesign.LargeFont ? 50 : 44;
                int nextRow = ly >= headerH + 2 && ly < headerH + 2 + cachedEntries.Count * rowH ? (int)((ly - headerH - 2) / rowH) : -1;
                if (nextRow != hoveredRow) {
                    hoveredRow = nextRow;
                    Cursor = hoveredRow >= 0 ? Cursors.Hand : Cursors.Default;
                    RenderNow(false);
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e) {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !dragging) return;
            dragging = false;
            Capture = false;
            if (dragMoved) {
                var physRect = currentPhysicalBounds.IsEmpty ? new Rectangle(dragPhysicalLocation, Size) : currentPhysicalBounds;
                var cursor = Cursor.Position;
                var screen = Screen.FromPoint(cursor);
                var workArea = screen.WorkingArea;
                float scale = Math.Max(1f, NativeDisplay.ScaleAt(cursor));
                int snapThreshold = Math.Max(28, (int)Math.Round(36f * scale));
                string snapped = DesktopWidgetGeometry.DetectSnapEdge(physRect, workArea, snapThreshold);
                // Also snap if cursor itself was dragged right onto the screen edge
                if (snapped == "none") {
                    int edgeCursorThreshold = Math.Max(12, (int)Math.Round(16f * scale));
                    if (cursor.X <= workArea.Left + edgeCursorThreshold) snapped = "left";
                    else if (cursor.X >= workArea.Right - edgeCursorThreshold) snapped = "right";
                    else if (cursor.Y <= workArea.Top + edgeCursorThreshold) snapped = "top";
                    else if (cursor.Y >= workArea.Bottom - edgeCursorThreshold) snapped = "bottom";
                }
                double rx, ry;
                DesktopWidgetGeometry.ComputeAnchorRatios(physRect.Location, physRect.Size, workArea, out rx, out ry);
                UpdateWidgetConfig(delegate(DesktopWidgetSettings w) {
                    w.DockEdge = snapped;
                    w.AnchorXRatio = rx;
                    w.AnchorYRatio = ry;
                    w.MonitorDeviceName = screen.DeviceName;
                });
                var latest = DeviceSettings.Current.DesktopWidget;
                if (snapped != "none" && latest.AutoHideOnEdge && !expanded) {
                    hovered = !currentPhysicalBounds.IsEmpty && currentPhysicalBounds.Contains(Cursor.Position);
                    if (!hovered) {
                        hideDelayTimer.Stop();
                        hideDelayTimer.Start();
                    }
                }
                return;
            }
            HandleClick(e.Location);
        }

        void HandleClick(Point physicalPoint) {
            float lx = physicalPoint.X / Math.Max(1f, currentDpiScale);
            float ly = physicalPoint.Y / Math.Max(1f, currentDpiScale);
            if (!expanded) {
                expanded = true;
                RenderNow(true);
                return;
            }
            int headerH = VisualDesign.LargeFont ? 32 : 28;
            int rowH = VisualDesign.LargeFont ? 50 : 44;
            int footerH = VisualDesign.LargeFont ? 34 : 30;
            if (ly < headerH) {
                expanded = false;
                RenderNow(true);
                return;
            }
            if (ly >= logicalSize.Height - footerH - 4) {
                if (lx < logicalSize.Width / 2f) owner.RefreshBattery(true);
                else owner.ShowControlCenter(ControlCenterPage.Devices);
                return;
            }
            int idx = (int)((ly - headerH - 2) / rowH);
            if (idx >= 0 && idx < cachedEntries.Count) {
                owner.ShowControlCenter(ControlCenterPage.Devices);
            } else {
                expanded = false;
                RenderNow(true);
            }
        }

        protected override void Dispose(bool disposing) {
            if (disposed) return;
            disposed = true;
            if (disposing) {
                watchTimer.Stop();
                watchTimer.Dispose();
                hideDelayTimer.Stop();
                hideDelayTimer.Dispose();
                menu.Dispose();
                surface.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class DesktopWidgetController : IDisposable {
        readonly TrayContext owner;
        DesktopWidgetForm active;
        bool disposed;

        internal DesktopWidgetController(TrayContext owner) {
            this.owner = owner;
        }

        internal bool IsRunning { get { return !disposed && active != null && !active.IsDisposed; } }
        internal DesktopWidgetForm ActiveForm { get { return IsRunning ? active : null; } }

        internal void ApplySettings() {
            if (disposed) return;
            var cfg = DeviceSettings.Current.DesktopWidget;
            if (cfg == null || !cfg.Enabled) {
                CloseAndDispose();
                return;
            }
            if (active == null || active.IsDisposed) {
                var form = new DesktopWidgetForm(owner);
                form.FormClosed += delegate { if (active == form) active = null; };
                active = form;
                form.Show();
                form.StartWatching();
            } else {
                active.RefreshFromSettingsOrData(true);
            }
        }

        internal void NotifyDataChanged() {
            var form = active;
            if (!disposed && form != null && !form.IsDisposed && !form.Disposing) {
                try { form.RefreshFromSettingsOrData(false); }
                catch (InvalidOperationException) { }
            }
        }

        internal void NotifyThemeChanged() {
            var form = active;
            if (!disposed && form != null && !form.IsDisposed && !form.Disposing) {
                try { form.RefreshFromSettingsOrData(true); }
                catch (InvalidOperationException) { }
            }
        }

        internal void CloseAndDispose() {
            var form = active;
            active = null;
            if (form != null && !form.IsDisposed) {
                form.Close();
                if (!form.IsDisposed) form.Dispose();
            }
        }

        public void Dispose() {
            if (disposed) return;
            disposed = true;
            CloseAndDispose();
        }
    }
}
