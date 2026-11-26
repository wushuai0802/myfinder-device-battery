using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        internal static readonly string[] HiddenStyleKeys = { "handle", "peek", "dot" };
        internal static readonly string[] HiddenStyleNames = { "琉璃丝带", "灵动耳翼", "微光露珠" };

        internal static string NormalizeStyle(string style) {
            return Array.IndexOf(StyleKeys, style) >= 0 ? style : "capsule";
        }

        internal static string NormalizeEdge(string edge) {
            return Array.IndexOf(EdgeKeys, edge) >= 0 ? edge : "none";
        }

        internal static string NormalizeHiddenStyle(string hiddenStyle) {
            return Array.IndexOf(HiddenStyleKeys, hiddenStyle) >= 0 ? hiddenStyle : "handle";
        }

        internal static Size CollapsedLogicalSize(string style, int deviceCount, bool largeFont, string dockEdge, bool hiddenEdgeTab, string hiddenStyle = "handle") {
            string edge = NormalizeEdge(dockEdge);
            if (hiddenEdgeTab && edge != "none") {
                bool vertical = edge == "left" || edge == "right";
                string hs = NormalizeHiddenStyle(hiddenStyle);
                if (hs == "peek") {
                    return vertical
                        ? new Size(largeFont ? 46 : 42, largeFont ? 90 : 84)
                        : new Size(largeFont ? 92 : 86, largeFont ? 42 : 38);
                }
                if (hs == "dot") {
                    return vertical ? new Size(24, 38) : new Size(38, 24);
                }
                // handle: Glass Ribbon floating softly 3px off the screen edge with generous soft-shadow canvas
                return vertical ? new Size(18, 72) : new Size(72, 18);
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

    // Pure spring/easing math for desktop widget edge-reveal, auto-hide, magnetic snap, and expand/collapse animations.
    internal static class DesktopWidgetSpring {
        internal static double Clamp01(double t) {
            return t <= 0.0 ? 0.0 : (t >= 1.0 ? 1.0 : t);
        }

        // Underdamped overshoot curve: brisk launch, gentle ~7-9% spring overshoot, smooth settle.
        internal static double EaseOutBack(double t, double overshoot = 1.45) {
            double c = Clamp01(t);
            if (c <= 0.0) return 0.0;
            if (c >= 1.0) return 1.0;
            double u = c - 1.0;
            return 1.0 + (overshoot + 1.0) * u * u * u + overshoot * u * u;
        }

        internal static double EaseOutCubic(double t) {
            double c = Clamp01(t);
            double u = 1.0 - c;
            return 1.0 - u * u * u;
        }

        internal static double EaseInOutCubic(double t) {
            double c = Clamp01(t);
            return c < 0.5 ? 4.0 * c * c * c : 1.0 - Math.Pow(-2.0 * c + 2.0, 3.0) / 2.0;
        }

        internal static double Evaluate(double t, bool bouncy, double overshoot = 1.45) {
            return bouncy ? EaseOutBack(t, overshoot) : EaseInOutCubic(t);
        }

        internal static void InterpolateFrame(
            Point startPos, Point targetPos,
            Size startLogicalSize, Size targetLogicalSize,
            byte startAlpha, byte targetAlpha,
            Rectangle workArea, float dpiScale,
            double t, bool bouncy, double overshoot,
            out Point framePos, out Size frameLogicalSize, out byte frameAlpha) {

            double c = Clamp01(t);
            if (c >= 1.0) {
                framePos = targetPos;
                frameLogicalSize = targetLogicalSize;
                frameAlpha = targetAlpha;
                return;
            }
            double posFactor = Evaluate(c, bouncy, overshoot);
            double sizeFactor = bouncy ? EaseOutBack(c, Math.Min(1.10, overshoot * 0.75)) : EaseInOutCubic(c);
            double alphaFactor = EaseOutCubic(c);

            int lw = Math.Max(10, (int)Math.Round(startLogicalSize.Width + (targetLogicalSize.Width - startLogicalSize.Width) * sizeFactor));
            int lh = Math.Max(10, (int)Math.Round(startLogicalSize.Height + (targetLogicalSize.Height - startLogicalSize.Height) * sizeFactor));
            frameLogicalSize = new Size(lw, lh);

            Size phys = LayeredSurface.ComputePhysicalSize(frameLogicalSize, dpiScale);
            int px = (int)Math.Round(startPos.X + (targetPos.X - startPos.X) * posFactor);
            int py = (int)Math.Round(startPos.Y + (targetPos.Y - startPos.Y) * posFactor);

            if (workArea.Width > 0 && workArea.Height > 0) {
                int maxX = Math.Max(workArea.Left, workArea.Right - phys.Width);
                int maxY = Math.Max(workArea.Top, workArea.Bottom - phys.Height);
                px = Math.Max(workArea.Left, Math.Min(px, maxX));
                py = Math.Max(workArea.Top, Math.Min(py, maxY));
            }
            framePos = new Point(px, py);

            int a = (int)Math.Round(startAlpha + (targetAlpha - startAlpha) * alphaFactor);
            frameAlpha = (byte)Math.Max(120, Math.Min(255, a));
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
                string edge = DesktopWidgetGeometry.NormalizeEdge(cfg != null ? cfg.DockEdge : "right");
                string hiddenStyle = DesktopWidgetGeometry.NormalizeHiddenStyle(cfg != null ? cfg.HiddenStyle : "handle");
                PaintHiddenTab(g, logicalSize, entries, palette, dark, edge, hiddenStyle);
                return;
            }
            // Clip smoothly inside the rounded card surface during morph/spring frames so text never bleeds outside.
            float clipRadius = expanded ? 12f : (edgeStripMode || DesktopWidgetGeometry.NormalizeStyle(cfg.Style) != "rings" ? Math.Min(logicalSize.Height / 2f, 22f) : 12f);
            var clipRect = new RectangleF(0.5f, 0.5f, Math.Max(4f, logicalSize.Width - 1f), Math.Max(4f, logicalSize.Height - 1f));
            var state = g.Save();
            try {
                using (var clipPath = VisualDesign.RoundRect(clipRect.X, clipRect.Y, clipRect.Width, clipRect.Height, Math.Max(2f, clipRadius))) {
                    g.SetClip(clipPath, CombineMode.Intersect);
                    if (expanded) {
                        PaintExpanded(g, logicalSize, entries, palette, dark, hoveredRow);
                    } else {
                        string style = edgeStripMode ? "strip" : DesktopWidgetGeometry.NormalizeStyle(cfg.Style);
                        if (style == "rings") PaintRings(g, logicalSize, entries, palette, dark);
                        else if (style == "strip") PaintStrip(g, logicalSize, entries, palette, dark);
                        else PaintCapsule(g, logicalSize, entries, palette, dark);
                    }
                }
            } finally {
                g.Restore(state);
            }
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

        // Builds an organic S-curve concave-fillet notch path anchored flush to the screen edge
        // (inspired by macOS Dynamic Island / Boring.Notch & Samsung Edge Panel).
        static GraphicsPath CreateConcaveNotchPath(RectangleF bounds, string edge, float flare, float tipRadius) {
            var p = new GraphicsPath();
            float l = bounds.Left, t = bounds.Top, r = bounds.Right, b = bounds.Bottom;
            float w = Math.Max(8f, bounds.Width), h = Math.Max(8f, bounds.Height);
            if (edge == "right") {
                float fl = Math.Max(1f, Math.Min(flare, h * 0.24f));
                float rad = Math.Max(1.5f, Math.Min(tipRadius, Math.Min(w * 0.6f, (h - fl * 2f) * 0.45f)));
                float bodyTop = t + fl;
                float bodyBot = Math.Max(bodyTop + rad * 2f, b - fl);
                float bodyLeft = l;
                p.AddBezier(r, t, r, t + fl * 0.62f, r - Math.Min(w * 0.48f, fl * 0.9f), bodyTop, bodyLeft + rad, bodyTop);
                p.AddArc(bodyLeft, bodyTop, rad * 2f, rad * 2f, 270, -90);
                p.AddLine(bodyLeft, bodyTop + rad, bodyLeft, bodyBot - rad);
                p.AddArc(bodyLeft, bodyBot - rad * 2f, rad * 2f, rad * 2f, 180, -90);
                p.AddBezier(bodyLeft + rad, bodyBot, r - Math.Min(w * 0.48f, fl * 0.9f), bodyBot, r, b - fl * 0.62f, r, b);
                p.AddLine(r, b, r, t);
            } else if (edge == "left") {
                float fl = Math.Max(1f, Math.Min(flare, h * 0.24f));
                float rad = Math.Max(1.5f, Math.Min(tipRadius, Math.Min(w * 0.6f, (h - fl * 2f) * 0.45f)));
                float bodyTop = t + fl;
                float bodyBot = Math.Max(bodyTop + rad * 2f, b - fl);
                float bodyRight = r;
                p.AddBezier(l, t, l, t + fl * 0.62f, l + Math.Min(w * 0.48f, fl * 0.9f), bodyTop, bodyRight - rad, bodyTop);
                p.AddArc(bodyRight - rad * 2f, bodyTop, rad * 2f, rad * 2f, 270, 90);
                p.AddLine(bodyRight, bodyTop + rad, bodyRight, bodyBot - rad);
                p.AddArc(bodyRight - rad * 2f, bodyBot - rad * 2f, rad * 2f, rad * 2f, 0, 90);
                p.AddBezier(bodyRight - rad, bodyBot, l + Math.Min(w * 0.48f, fl * 0.9f), bodyBot, l, b - fl * 0.62f, l, b);
                p.AddLine(l, b, l, t);
            } else if (edge == "top") {
                float fl = Math.Max(1f, Math.Min(flare, w * 0.22f));
                float rad = Math.Max(1.5f, Math.Min(tipRadius, Math.Min(h * 0.6f, (w - fl * 2f) * 0.45f)));
                float bodyLeft = l + fl;
                float bodyRight = Math.Max(bodyLeft + rad * 2f, r - fl);
                float bodyBot = b;
                p.AddBezier(l, t, l + fl * 0.62f, t, bodyLeft, t + Math.Min(h * 0.48f, fl * 0.9f), bodyLeft, bodyBot - rad);
                p.AddArc(bodyLeft, bodyBot - rad * 2f, rad * 2f, rad * 2f, 180, -90);
                p.AddLine(bodyLeft + rad, bodyBot, bodyRight - rad, bodyBot);
                p.AddArc(bodyRight - rad * 2f, bodyBot - rad * 2f, rad * 2f, rad * 2f, 90, -90);
                p.AddBezier(bodyRight, bodyBot - rad, bodyRight, t + Math.Min(h * 0.48f, fl * 0.9f), r - fl * 0.62f, t, r, t);
                p.AddLine(r, t, l, t);
            } else {
                float fl = Math.Max(1f, Math.Min(flare, w * 0.22f));
                float rad = Math.Max(1.5f, Math.Min(tipRadius, Math.Min(h * 0.6f, (w - fl * 2f) * 0.45f)));
                float bodyLeft = l + fl;
                float bodyRight = Math.Max(bodyLeft + rad * 2f, r - fl);
                float bodyTop = t;
                p.AddBezier(l, b, l + fl * 0.62f, b, bodyLeft, b - Math.Min(h * 0.48f, fl * 0.9f), bodyLeft, bodyTop + rad);
                p.AddArc(bodyLeft, bodyTop, rad * 2f, rad * 2f, 180, 90);
                p.AddLine(bodyLeft + rad, bodyTop, bodyRight - rad, bodyTop);
                p.AddArc(bodyRight - rad * 2f, bodyTop, rad * 2f, rad * 2f, 270, 90);
                p.AddBezier(bodyRight, bodyTop + rad, bodyRight, b - Math.Min(h * 0.48f, fl * 0.9f), r - fl * 0.62f, b, r, b);
                p.AddLine(r, b, l, b);
            }
            p.CloseFigure();
            return p;
        }

        // Draws multi-layer soft ambient drop shadow + subtle light halo so translucent glass floats crisply
        // on pure white, pure black, and vivid wallpapers without heavy black borders.
        static void PaintSoftCapsuleShadow(Graphics g, RectangleF body, float radius, bool dark) {
            if (VisualDesign.HighContrast) return;
            // Subtle outer pale aura keeps dark glass legible on pure black wallpapers
            if (dark) {
                var aura = RectangleF.Inflate(body, 2.2f, 2.2f);
                CompactRenderer.Fill(g, aura, Color.FromArgb(16, 255, 255, 255), radius + 2.2f);
            }
            float[] spreads = { 4.2f, 2.8f, 1.6f, 0.7f };
            int[] alphas = dark ? new[] { 8, 15, 24, 34 } : new[] { 6, 12, 20, 30 };
            for (int i = 0; i < spreads.Length; i++) {
                var sr = RectangleF.Inflate(body, spreads[i], spreads[i]);
                sr.Offset(0f, 0.6f);
                CompactRenderer.Fill(g, sr, Color.FromArgb(alphas[i], 10, 14, 22), radius + spreads[i]);
            }
        }

        static void ResolveEdgeBatterySummary(List<DesktopWidgetEntry> entries, out int? primaryPct, out bool anyLow, out bool anyCharging, out Color statusColor) {
            primaryPct = null;
            anyLow = false;
            anyCharging = false;
            if (entries != null) {
                foreach (var e in entries) {
                    if (e.Percent.HasValue && e.Fresh) {
                        if (!primaryPct.HasValue || e.Percent.Value < primaryPct.Value) primaryPct = e.Percent.Value;
                        if (e.Low) anyLow = true;
                        if (e.Charging) anyCharging = true;
                    }
                }
            }
            statusColor = anyLow
                ? Color.FromArgb(244, 96, 102)
                : primaryPct.HasValue
                    ? (primaryPct.Value <= 30 ? Color.FromArgb(245, 182, 66) : Color.FromArgb(56, 212, 136))
                    : Color.FromArgb(156, 168, 184);
        }

        // Modern glassmorphic edge-hidden tabs in 3 selectable styles:
        // - handle ("琉璃丝带"): floating frosted glass ribbon 3px off screen edge with feathered status glow core
        // - peek ("灵动耳翼"): S-curve concave-fillet notch anchored to edge with crisp embedded mini battery ring
        // - dot ("微光露珠"): floating frosted micro-capsule with radial-glow liquid battery jewel
        static void PaintHiddenTab(Graphics g, SizeF size, List<DesktopWidgetEntry> entries, ThemePalette palette, bool dark, string edge, string hiddenStyle) {
            // Paint an invisible alpha=1 hit-test film across the edge-anchored canvas so flinging the cursor
            // right onto the 0px screen boundary always triggers WM_MOUSEENTER (never passes through alpha=0 gaps).
            using (var hitFilm = new SolidBrush(Color.FromArgb(1, 0, 0, 0))) {
                g.FillRectangle(hitFilm, 0f, 0f, size.Width, size.Height);
            }
            int? primaryPct; bool anyLow, anyCharging; Color coreColor;
            ResolveEdgeBatterySummary(entries, out primaryPct, out anyLow, out anyCharging, out coreColor);
            string style = DesktopWidgetGeometry.NormalizeHiddenStyle(hiddenStyle);
            if (style == "peek") {
                PaintHiddenPeek(g, size, primaryPct, anyLow, anyCharging, coreColor, palette, dark, edge);
                return;
            }
            if (style == "dot") {
                PaintHiddenDot(g, size, anyLow, anyCharging, coreColor, dark, edge);
                return;
            }
            PaintHiddenHandle(g, size, anyLow, anyCharging, coreColor, dark, edge);
        }

        static void PaintHiddenHandle(Graphics g, SizeF size, bool anyLow, bool anyCharging, Color coreColor, bool dark, string edge) {
            bool vertical = edge == "left" || edge == "right" || size.Height > size.Width;
            RectangleF body;
            if (vertical) {
                float bw = 6.5f;
                float bh = Math.Max(22f, size.Height - 16f);
                float bx = edge == "left" ? 3.5f : edge == "right" ? Math.Max(2f, size.Width - bw - 3.5f) : (size.Width - bw) / 2f;
                body = new RectangleF(bx, (size.Height - bh) / 2f, bw, bh);
            } else {
                float bh = 6.5f;
                float bw = Math.Max(22f, size.Width - 16f);
                float by = edge == "top" ? 3.5f : edge == "bottom" ? Math.Max(2f, size.Height - bh - 3.5f) : (size.Height - bh) / 2f;
                body = new RectangleF((size.Width - bw) / 2f, by, bw, bh);
            }
            float radius = Math.Min(body.Width, body.Height) / 2f;
            PaintSoftCapsuleShadow(g, body, radius, dark);

            using (var path = VisualDesign.RoundRect(body.X, body.Y, body.Width, body.Height, radius)) {
                if (VisualDesign.HighContrast) {
                    using (var b = new SolidBrush(SystemColors.Window)) g.FillPath(b, path);
                    using (var p = new Pen(SystemColors.WindowText, 1f)) g.DrawPath(p, path);
                } else {
                    var gradRect = RectangleF.Inflate(body, 0.5f, 0.5f);
                    Color c1 = dark ? Color.FromArgb(228, 46, 52, 64) : Color.FromArgb(236, 255, 255, 255);
                    Color c2 = dark ? Color.FromArgb(216, 24, 28, 36) : Color.FromArgb(220, 236, 241, 248);
                    using (var lgb = new LinearGradientBrush(gradRect, c1, c2, vertical ? LinearGradientMode.Vertical : LinearGradientMode.Horizontal)) {
                        g.FillPath(lgb, path);
                    }
                    // Delicate dual glass hairline (soft dark edge + crisp inner highlight)
                    using (var outerPen = new Pen(dark ? Color.FromArgb(88, 0, 0, 0) : Color.FromArgb(62, 18, 26, 38), 0.85f)) {
                        g.DrawPath(outerPen, path);
                    }
                    var inner = RectangleF.Inflate(body, -0.75f, -0.75f);
                    if (inner.Width > 2f && inner.Height > 2f) {
                        using (var innerPath = VisualDesign.RoundRect(inner.X, inner.Y, inner.Width, inner.Height, Math.Max(1f, radius - 0.75f)))
                        using (var rimPen = new Pen(dark ? Color.FromArgb(72, 255, 255, 255) : Color.FromArgb(210, 255, 255, 255), 0.75f)) {
                            g.DrawPath(rimPen, innerPath);
                        }
                    }
                }
            }

            // Feathered luminous status ribbon core (tapers softly at both ends)
            Color ribbonColor = VisualDesign.HighContrast
                ? SystemColors.Highlight
                : (!dark && !anyLow ? Color.FromArgb(24, 174, 102) : coreColor);
            RectangleF coreRect = vertical
                ? new RectangleF(body.X + (body.Width - 2.6f) / 2f, body.Y + 8f, 2.6f, Math.Max(10f, body.Height - 16f))
                : new RectangleF(body.X + 8f, body.Y + (body.Height - 2.6f) / 2f, Math.Max(10f, body.Width - 16f), 2.6f);
            using (var corePath = VisualDesign.RoundRect(coreRect.X, coreRect.Y, coreRect.Width, coreRect.Height, 1.3f))
            using (var lgb = new LinearGradientBrush(RectangleF.Inflate(coreRect, 0.5f, 0.5f), Color.Transparent, Color.Transparent, vertical ? LinearGradientMode.Vertical : LinearGradientMode.Horizontal)) {
                var cb = new ColorBlend(4);
                cb.Positions = new[] { 0f, 0.24f, 0.76f, 1f };
                cb.Colors = new[] {
                    Color.FromArgb(25, ribbonColor),
                    Color.FromArgb(245, ribbonColor),
                    Color.FromArgb(245, ribbonColor),
                    Color.FromArgb(25, ribbonColor)
                };
                lgb.InterpolationColors = cb;
                g.FillPath(lgb, corePath);
            }

            if (anyCharging) {
                float dot = 2.8f;
                using (var wb = new SolidBrush(Color.White)) {
                    g.FillEllipse(wb, coreRect.X + (coreRect.Width - dot) / 2f, coreRect.Y + (coreRect.Height - dot) / 2f, dot, dot);
                }
            }
        }

        static void PaintHiddenPeek(Graphics g, SizeF size, int? primaryPct, bool anyLow, bool anyCharging, Color coreColor, ThemePalette palette, bool dark, string edge) {
            bool vertical = edge == "left" || edge == "right";
            float padShadow = 7f;
            RectangleF notchBounds;
            if (edge == "right") notchBounds = new RectangleF(padShadow, padShadow, Math.Max(14f, size.Width - padShadow), Math.Max(28f, size.Height - padShadow * 2f));
            else if (edge == "left") notchBounds = new RectangleF(0f, padShadow, Math.Max(14f, size.Width - padShadow), Math.Max(28f, size.Height - padShadow * 2f));
            else if (edge == "top") notchBounds = new RectangleF(padShadow, 0f, Math.Max(28f, size.Width - padShadow * 2f), Math.Max(14f, size.Height - padShadow));
            else notchBounds = new RectangleF(padShadow, padShadow, Math.Max(28f, size.Width - padShadow * 2f), Math.Max(14f, size.Height - padShadow));

            float flare = vertical ? 11f : 10f;
            float tipRadius = vertical ? 12f : 11f;

            // Multi-layer soft shadow hugging the S-curve concave notch silhouette
            if (!VisualDesign.HighContrast) {
                if (dark) {
                    var auraRect = RectangleF.Inflate(notchBounds, 1.8f, 1.8f);
                    using (var ap = CreateConcaveNotchPath(auraRect, edge, flare + 1f, tipRadius + 1.5f))
                    using (var ab = new SolidBrush(Color.FromArgb(14, 255, 255, 255))) g.FillPath(ab, ap);
                }
                float[] spreads = { 4.2f, 2.7f, 1.4f, 0.5f };
                int[] alphas = dark ? new[] { 7, 13, 21, 30 } : new[] { 5, 10, 17, 26 };
                for (int i = 0; i < spreads.Length; i++) {
                    var sr = notchBounds;
                    if (edge == "right") sr = new RectangleF(notchBounds.X - spreads[i], notchBounds.Y - spreads[i], notchBounds.Width + spreads[i], notchBounds.Height + spreads[i] * 2f);
                    else if (edge == "left") sr = new RectangleF(notchBounds.X, notchBounds.Y - spreads[i], notchBounds.Width + spreads[i], notchBounds.Height + spreads[i] * 2f);
                    else if (edge == "top") sr = new RectangleF(notchBounds.X - spreads[i], notchBounds.Y, notchBounds.Width + spreads[i] * 2f, notchBounds.Height + spreads[i]);
                    else sr = new RectangleF(notchBounds.X - spreads[i], notchBounds.Y - spreads[i], notchBounds.Width + spreads[i] * 2f, notchBounds.Height + spreads[i]);
                    using (var sp = CreateConcaveNotchPath(sr, edge, flare + spreads[i] * 0.4f, tipRadius + spreads[i] * 0.8f))
                    using (var sb = new SolidBrush(Color.FromArgb(alphas[i], 10, 14, 22))) g.FillPath(sb, sp);
                }
            }

            using (var notchPath = CreateConcaveNotchPath(notchBounds, edge, flare, tipRadius)) {
                if (VisualDesign.HighContrast) {
                    using (var b = new SolidBrush(SystemColors.Window)) g.FillPath(b, notchPath);
                    using (var p = new Pen(SystemColors.WindowText, 1f)) g.DrawPath(p, notchPath);
                } else {
                    Color c1 = dark ? Color.FromArgb(236, 36, 41, 52) : Color.FromArgb(242, 255, 255, 255);
                    Color c2 = dark ? Color.FromArgb(228, 20, 24, 32) : Color.FromArgb(230, 238, 243, 250);
                    using (var lgb = new LinearGradientBrush(RectangleF.Inflate(notchBounds, 1f, 1f), c1, c2, vertical ? LinearGradientMode.Horizontal : LinearGradientMode.Vertical)) {
                        g.FillPath(lgb, notchPath);
                    }
                    using (var borderPen = new Pen(dark ? Color.FromArgb(62, 255, 255, 255) : Color.FromArgb(58, 18, 26, 38), 0.85f)) {
                        g.DrawPath(borderPen, notchPath);
                    }
                }
            }

            Color accent = VisualDesign.HighContrast
                ? SystemColors.Highlight
                : (dark ? coreColor : (anyLow ? Color.FromArgb(214, 52, 58) : (primaryPct.HasValue && primaryPct.Value <= 30 ? Color.FromArgb(204, 134, 16) : Color.FromArgb(22, 162, 92))));
            Color trackColor = VisualDesign.HighContrast
                ? SystemColors.ControlDark
                : (dark ? Color.FromArgb(44, 255, 255, 255) : Color.FromArgb(30, 15, 23, 42));
            Color fgColor = VisualDesign.HighContrast
                ? SystemColors.WindowText
                : (anyLow ? accent : (dark ? Color.FromArgb(244, 247, 252) : Color.FromArgb(24, 30, 40)));

            if (vertical) {
                // Crisp embedded mini battery ring centered inside the organic concave ear
                float ringSize = Math.Min(VisualDesign.LargeFont ? 25f : 23f, Math.Max(14f, notchBounds.Width - 10f));
                float rx = edge == "right" ? notchBounds.Left + (notchBounds.Width - ringSize) / 2f + 1.5f : notchBounds.Left + (notchBounds.Width - ringSize) / 2f - 1.5f;
                float ry = notchBounds.Top + (notchBounds.Height - ringSize) / 2f;
                var ring = new RectangleF(rx, ry, ringSize, ringSize);
                using (var tp = new Pen(trackColor, 2.4f)) g.DrawEllipse(tp, ring);
                if (primaryPct.HasValue) {
                    float sweep = 360f * Math.Max(0, Math.Min(100, primaryPct.Value)) / 100f;
                    if (sweep > 0.5f) {
                        using (var ap = new Pen(accent, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(ap, ring, -90f, sweep);
                    }
                }
                string digits = primaryPct.HasValue ? primaryPct.Value.ToString() : "--";
                float digitPx = digits.Length >= 3 ? (VisualDesign.LargeFont ? 8.5f : 8f) : (VisualDesign.LargeFont ? 10.5f : 9.5f);
                CompactRenderer.DrawLabel(g, digits, ring, digitPx, true, fgColor, StringAlignment.Center);
                if (anyCharging) {
                    float dot = 3f;
                    using (var cb = new SolidBrush(accent)) g.FillEllipse(cb, ring.X + (ring.Width - dot) / 2f, ring.Bottom + 3f, dot, dot);
                }
            } else {
                // Horizontal notch: compact 16px ring + single-line crisp percentage
                float ringSize = 15f;
                float cy = notchBounds.Top + (notchBounds.Height - ringSize) / 2f + (edge == "top" ? -0.5f : 0.5f);
                float startX = notchBounds.Left + flare + 2f;
                var ring = new RectangleF(startX, cy, ringSize, ringSize);
                using (var tp = new Pen(trackColor, 2.2f)) g.DrawEllipse(tp, ring);
                if (primaryPct.HasValue) {
                    float sweep = 360f * Math.Max(0, Math.Min(100, primaryPct.Value)) / 100f;
                    if (sweep > 0.5f) {
                        using (var ap = new Pen(accent, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(ap, ring, -90f, sweep);
                    }
                }
                if (anyCharging) {
                    using (var wb = new SolidBrush(accent)) g.FillEllipse(wb, ring.X + (ring.Width - 3.6f) / 2f, ring.Y + (ring.Height - 3.6f) / 2f, 3.6f, 3.6f);
                }
                string label = primaryPct.HasValue ? primaryPct.Value + "%" : "--";
                float textX = ring.Right + 4f;
                float textW = Math.Max(20f, notchBounds.Right - flare - textX);
                CompactRenderer.DrawLabel(g, label, new RectangleF(textX, notchBounds.Top + (edge == "top" ? -0.5f : 0.5f), textW, notchBounds.Height), VisualDesign.LargeFont ? 11.5f : 10.5f, true, fgColor, StringAlignment.Near);
            }
        }

        static void PaintHiddenDot(Graphics g, SizeF size, bool anyLow, bool anyCharging, Color coreColor, bool dark, string edge) {
            bool vertical = edge == "left" || edge == "right" || size.Height > size.Width;
            RectangleF body;
            if (vertical) {
                float bw = 12f, bh = 22f;
                float bx = edge == "left" ? 4f : edge == "right" ? Math.Max(3f, size.Width - bw - 4f) : (size.Width - bw) / 2f;
                body = new RectangleF(bx, (size.Height - bh) / 2f, bw, bh);
            } else {
                float bw = 22f, bh = 12f;
                float by = edge == "top" ? 4f : edge == "bottom" ? Math.Max(3f, size.Height - bh - 4f) : (size.Height - bh) / 2f;
                body = new RectangleF((size.Width - bw) / 2f, by, bw, bh);
            }
            float radius = Math.Min(body.Width, body.Height) / 2f;
            PaintSoftCapsuleShadow(g, body, radius, dark);

            using (var path = VisualDesign.RoundRect(body.X, body.Y, body.Width, body.Height, radius)) {
                if (VisualDesign.HighContrast) {
                    using (var b = new SolidBrush(SystemColors.Window)) g.FillPath(b, path);
                    using (var p = new Pen(SystemColors.WindowText, 1f)) g.DrawPath(p, path);
                } else {
                    Color c1 = dark ? Color.FromArgb(228, 44, 50, 62) : Color.FromArgb(238, 255, 255, 255);
                    Color c2 = dark ? Color.FromArgb(218, 22, 26, 34) : Color.FromArgb(224, 236, 241, 248);
                    using (var lgb = new LinearGradientBrush(RectangleF.Inflate(body, 0.5f, 0.5f), c1, c2, LinearGradientMode.Vertical)) {
                        g.FillPath(lgb, path);
                    }
                    using (var outerPen = new Pen(dark ? Color.FromArgb(84, 0, 0, 0) : Color.FromArgb(58, 18, 26, 38), 0.85f)) {
                        g.DrawPath(outerPen, path);
                    }
                    var inner = RectangleF.Inflate(body, -0.75f, -0.75f);
                    if (inner.Width > 2f && inner.Height > 2f) {
                        using (var innerPath = VisualDesign.RoundRect(inner.X, inner.Y, inner.Width, inner.Height, Math.Max(1f, radius - 0.75f)))
                        using (var rimPen = new Pen(dark ? Color.FromArgb(72, 255, 255, 255) : Color.FromArgb(215, 255, 255, 255), 0.75f)) {
                            g.DrawPath(rimPen, innerPath);
                        }
                    }
                }
            }

            float cx = body.X + body.Width / 2f;
            float cy = body.Y + body.Height / 2f;
            Color jewelColor = VisualDesign.HighContrast
                ? SystemColors.Highlight
                : (!dark && !anyLow ? Color.FromArgb(24, 174, 102) : coreColor);

            // Soft radial glow around the liquid jewel
            if (!VisualDesign.HighContrast) {
                float halo = 11.5f;
                using (var gp = new GraphicsPath()) {
                    gp.AddEllipse(cx - halo / 2f, cy - halo / 2f, halo, halo);
                    using (var pgb = new PathGradientBrush(gp)) {
                        pgb.CenterPoint = new PointF(cx, cy);
                        pgb.CenterColor = Color.FromArgb(dark ? 115 : 82, jewelColor);
                        pgb.SurroundColors = new[] { Color.FromArgb(0, jewelColor) };
                        g.FillPath(pgb, gp);
                    }
                }
            }
            float dot = 5.6f;
            using (var db = new SolidBrush(jewelColor)) {
                g.FillEllipse(db, cx - dot / 2f, cy - dot / 2f, dot, dot);
            }
            if (anyCharging) {
                float inner = 2.4f;
                using (var wb = new SolidBrush(Color.White)) g.FillEllipse(wb, cx - inner / 2f, cy - inner / 2f, inner, inner);
            } else if (!VisualDesign.HighContrast) {
                // Subtle specular highlight for liquid-drop finish
                using (var sp = new SolidBrush(Color.FromArgb(145, 255, 255, 255))) g.FillEllipse(sp, cx - 1.5f, cy - 1.7f, 1.8f, 1.8f);
            }
        }

        static void PaintCapsule(Graphics g, SizeF size, List<DesktopWidgetEntry> entries, ThemePalette palette, bool dark) {
            PaintSurface(g, size, Math.Max(2f, size.Height / 2f), palette, dark);
            if (size.Width < 36f || size.Height < 16f) return;
            if (entries.Count == 0) {
                CompactRenderer.DrawLabel(g, "无设备", new RectangleF(6, 0, Math.Max(4f, size.Width - 12), size.Height), 12f, false, palette.Muted, StringAlignment.Center);
                return;
            }
            float padX = 10f;
            float itemW = (size.Width - padX * 2f) / entries.Count;
            if (itemW < 12f) return;
            for (int i = 0; i < entries.Count; i++) {
                var e = entries[i];
                float x = padX + i * itemW;
                if (i > 0) {
                    using (var pen = new Pen(Color.FromArgb(dark ? 55 : 45, palette.Border), 1f)) g.DrawLine(pen, x, 8f, x, Math.Max(9f, size.Height - 8f));
                }
                Color level = e.Percent.HasValue && e.Fresh ? ThemePalette.LevelColor(e.Percent, dark) : palette.Muted;
                float ringSize = Math.Min(VisualDesign.LargeFont ? 22f : 20f, Math.Max(6f, size.Height - 8f));
                var ring = new RectangleF(x + 4f, (size.Height - ringSize) / 2f, ringSize, ringSize);
                using (var trackPen = new Pen(palette.Track, 2.2f)) g.DrawEllipse(trackPen, ring);
                if (e.Percent.HasValue) {
                    float sweep = 360f * Math.Max(0, Math.Min(100, e.Percent.Value)) / 100f;
                    if (sweep > 0.5f) {
                        using (var arcPen = new Pen(level, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(arcPen, ring, -90f, sweep);
                    }
                }
                float symSize = Math.Min(VisualDesign.LargeFont ? 11f : 10f, Math.Max(4f, ringSize * 0.5f));
                CompactRenderer.DrawSymbol(g, ControlCenterTokens.SymbolForKind(e.Kind), new RectangleF(ring.X + (ring.Width - symSize) / 2f, ring.Y + (ring.Height - symSize) / 2f, symSize, symSize), e.Low ? level : palette.Foreground);

                string txt = e.Percent.HasValue ? (e.Charging ? e.Percent.Value + "%+" : e.Percent.Value + "%") : "--";
                CompactRenderer.DrawLabel(g, txt, new RectangleF(ring.Right + 3f, 0, Math.Max(12f, itemW - ringSize - 8f), size.Height), VisualDesign.LargeFont ? 13f : 12f, true, e.Low ? level : palette.Foreground, StringAlignment.Center);
            }
        }

        static void PaintRings(Graphics g, SizeF size, List<DesktopWidgetEntry> entries, ThemePalette palette, bool dark) {
            PaintSurface(g, size, 12f, palette, dark);
            if (size.Width < 28f || size.Height < 20f) return;
            if (entries.Count == 0) {
                CompactRenderer.DrawLabel(g, "—", new RectangleF(0, 0, size.Width, size.Height), 13f, false, palette.Muted, StringAlignment.Center);
                return;
            }
            float pad = 8f;
            float cell = (size.Width - pad * 2f) / entries.Count;
            float ringSize = Math.Min(size.Height - 10f, cell - 4f);
            // Guard mid-animation morph frames where width/height is temporarily narrower than ring diameter
            if (cell <= 6f || ringSize < 8f) return;
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
            PaintSurface(g, size, Math.Max(2f, size.Height / 2f), palette, dark);
            if (size.Width < 36f || size.Height < 14f) return;
            if (entries.Count == 0) {
                CompactRenderer.DrawLabel(g, "MyFinder", new RectangleF(6, 0, Math.Max(4f, size.Width - 12), size.Height), 11f, false, palette.Muted, StringAlignment.Center);
                return;
            }
            float padX = entries.Count == 1 ? 3f : 4f;
            float padY = 3f;
            float gap = entries.Count == 1 ? 0f : 4f;
            float totalGap = gap * Math.Max(0, entries.Count - 1);
            float slotW = (size.Width - padX * 2f - totalGap) / entries.Count;
            float slotH = Math.Max(8f, size.Height - padY * 2f);
            if (slotW < 18f) return;
            float slotRadius = Math.Max(2f, slotH / 2f);

            for (int i = 0; i < entries.Count; i++) {
                var e = entries[i];
                float sx = padX + i * (slotW + gap);
                var slot = new RectangleF(sx, padY, slotW, slotH);

                // Subtle recessed battery track slot inside the outer pill
                Color trackFill = VisualDesign.HighContrast
                    ? SystemColors.Window
                    : (dark ? Color.FromArgb(46, 255, 255, 255) : Color.FromArgb(22, 15, 23, 42));
                CompactRenderer.Fill(g, slot, trackFill, slotRadius);

                int pctVal = e.Percent.HasValue ? Math.Max(0, Math.Min(100, e.Percent.Value)) : 0;
                float fillW = e.Percent.HasValue ? (slot.Width * pctVal / 100f) : 0f;
                if (e.Percent.HasValue && pctVal > 0 && fillW < 4f) fillW = 4f;

                // Saturated, high-contrast fill colors tuned so white inverted glyphs stay crisp (WCAG AA+)
                Color fillStart, fillEnd, accentColor;
                if (VisualDesign.HighContrast) {
                    fillStart = SystemColors.Highlight;
                    fillEnd = SystemColors.Highlight;
                    accentColor = SystemColors.Highlight;
                } else if (!e.Percent.HasValue || !e.Fresh) {
                    fillStart = palette.Muted;
                    fillEnd = palette.Muted;
                    accentColor = palette.Muted;
                } else if (e.Low) {
                    fillStart = Color.FromArgb(228, 66, 74);
                    fillEnd = Color.FromArgb(204, 46, 56);
                    accentColor = fillStart;
                } else if (pctVal <= 50) {
                    fillStart = Color.FromArgb(224, 146, 24);
                    fillEnd = Color.FromArgb(196, 118, 14);
                    accentColor = fillStart;
                } else {
                    fillStart = Color.FromArgb(34, 178, 108);
                    fillEnd = Color.FromArgb(22, 148, 88);
                    accentColor = fillStart;
                }

                string txt = e.Percent.HasValue ? (e.Charging ? e.Percent.Value + "%+" : e.Percent.Value + "%") : "--";
                float fontPx = VisualDesign.LargeFont ? 12f : 11f;
                float iconSz = 11.5f;
                float contentPad = entries.Count == 1 ? 9f : 6f;
                var iconRect = new RectangleF(slot.X + contentPad, slot.Y + (slot.Height - iconSz) / 2f, iconSz, iconSz);
                var textRect = new RectangleF(iconRect.Right + 3.5f, slot.Y, Math.Max(14f, slot.Right - (iconRect.Right + 3.5f) - 4f), slot.Height);

                using (var slotPath = VisualDesign.RoundRect(slot.X, slot.Y, slot.Width, slot.Height, slotRadius)) {
                    // 1. Paint liquid battery fill clipped strictly inside the rounded slot
                    if (fillW > 0.5f) {
                        var stFill = g.Save();
                        try {
                            g.SetClip(slotPath, CombineMode.Intersect);
                            var fillRect = new RectangleF(slot.X, slot.Y, fillW, slot.Height);
                            using (var lgb = new LinearGradientBrush(RectangleF.Inflate(slot, 0.5f, 0.5f), fillStart, fillEnd, LinearGradientMode.Vertical)) {
                                g.FillRectangle(lgb, fillRect);
                            }
                            if (!VisualDesign.HighContrast) {
                                // Subtle top glass sheen along filled bar
                                using (var sheen = new SolidBrush(Color.FromArgb(32, 255, 255, 255))) {
                                    g.FillRectangle(sheen, slot.X, slot.Y, fillW, slot.Height * 0.42f);
                                }
                            }
                        } finally {
                            g.Restore(stFill);
                        }
                    }

                    // 2. Unfilled region pass: standard theme foreground / low-battery accent
                    Color unfilledIcon = e.Low ? accentColor : (e.Percent.HasValue && e.Fresh ? palette.Foreground : palette.Muted);
                    Color unfilledText = e.Low ? accentColor : palette.Foreground;
                    var stUnfilled = g.Save();
                    try {
                        g.SetClip(slotPath, CombineMode.Intersect);
                        g.SetClip(new RectangleF(slot.X + fillW, slot.Y, Math.Max(0f, slot.Width - fillW + 1f), slot.Height), CombineMode.Intersect);
                        CompactRenderer.DrawSymbol(g, ControlCenterTokens.SymbolForKind(e.Kind), iconRect, unfilledIcon);
                        CompactRenderer.DrawLabel(g, txt, textRect, fontPx, true, unfilledText, StringAlignment.Near);
                    } finally {
                        g.Restore(stUnfilled);
                    }

                    // 3. Filled region pass: crisp inverted white contrast glyphs cleanly split at the exact fill boundary
                    if (fillW > 0.5f) {
                        Color filledFg = VisualDesign.HighContrast ? SystemColors.HighlightText : Color.White;
                        var stInverted = g.Save();
                        try {
                            g.SetClip(slotPath, CombineMode.Intersect);
                            g.SetClip(new RectangleF(slot.X, slot.Y, fillW, slot.Height), CombineMode.Intersect);
                            CompactRenderer.DrawSymbol(g, ControlCenterTokens.SymbolForKind(e.Kind), iconRect, filledFg);
                            CompactRenderer.DrawLabel(g, txt, textRect, fontPx, true, filledFg, StringAlignment.Near);
                        } finally {
                            g.Restore(stInverted);
                        }
                    }
                }
            }
        }

        static void PaintExpanded(Graphics g, SizeF size, List<DesktopWidgetEntry> entries, ThemePalette palette, bool dark, int hoveredRow) {
            PaintSurface(g, size, 12f, palette, dark);
            if (size.Width < 140f || size.Height < 64f) return;
            int headerH = VisualDesign.LargeFont ? 32 : 28;
            int rowH = VisualDesign.LargeFont ? 50 : 44;
            int footerH = VisualDesign.LargeFont ? 34 : 30;

            CompactRenderer.DrawLabel(g, "外设电量", new RectangleF(14, 6, Math.Max(20f, size.Width - 80), headerH - 4), 13f, true, palette.Foreground, StringAlignment.Near);
            CompactRenderer.DrawLabel(g, "点击收起", new RectangleF(Math.Max(14f, size.Width - 78), 6, 66, headerH - 4), 11f, false, palette.Muted, StringAlignment.Far);

            float y = headerH + 2f;
            if (entries.Count == 0) {
                CompactRenderer.DrawLabel(g, "暂未检测到兼容设备", new RectangleF(14, y, Math.Max(20f, size.Width - 28), rowH), 12.5f, false, palette.Muted, StringAlignment.Center);
                y += rowH;
            } else {
                for (int i = 0; i < entries.Count; i++) {
                    var e = entries[i];
                    var rowRect = new RectangleF(8, y, Math.Max(20f, size.Width - 16), rowH - 2);
                    if (hoveredRow == i && !VisualDesign.HighContrast) {
                        CompactRenderer.Fill(g, rowRect, palette.RaisedSurface, 8f);
                    }
                    Color level = e.Percent.HasValue && e.Fresh ? ThemePalette.LevelColor(e.Percent, dark) : palette.Muted;
                    var tile = new RectangleF(rowRect.X + 6, rowRect.Y + (rowRect.Height - 28) / 2f, 28, 28);
                    CompactRenderer.Fill(g, tile, palette.RaisedSurface, 7f);
                    CompactRenderer.DrawSymbol(g, ControlCenterTokens.SymbolForKind(e.Kind), new RectangleF(tile.X + 6, tile.Y + 6, 16, 16), e.Low ? level : palette.Foreground);

                    float nameW = Math.Max(16f, rowRect.Width - 104);
                    CompactRenderer.DrawLabel(g, e.Name, new RectangleF(tile.Right + 8, rowRect.Y + 5, nameW, 18), 12.5f, true, palette.Foreground, StringAlignment.Near);
                    CompactRenderer.DrawLabel(g, e.Subtitle ?? "", new RectangleF(tile.Right + 8, rowRect.Y + 23, nameW, 16), 10.5f, false, palette.Muted, StringAlignment.Near);

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

            float footY = Math.Max(headerH + 4f, size.Height - footerH - 4);
            using (var pen = new Pen(palette.Border, 1f)) g.DrawLine(pen, 12, footY, Math.Max(13f, size.Width - 12), footY);
            float halfW = Math.Max(20f, (size.Width - 24) / 2f);
            Color footerInk = VisualDesign.HighContrast ? palette.Foreground : Color.FromArgb(235, palette.Foreground);
            CompactRenderer.DrawLabel(g, "刷新", new RectangleF(12, footY + 2, halfW, footerH - 2), 11.5f, false, footerInk, StringAlignment.Center);
            CompactRenderer.DrawLabel(g, "控制中心", new RectangleF(12 + halfW, footY + 2, halfW, footerH - 2), 11.5f, false, footerInk, StringAlignment.Center);
            using (var midPen = new Pen(VisualDesign.HighContrast ? palette.Border : Color.FromArgb(dark ? 70 : 60, palette.Border), 1f)) {
                float midX = 12 + halfW;
                g.DrawLine(midPen, midX, footY + 8f, midX, footY + footerH - 6f);
            }
        }
    }

    internal sealed class DesktopWidgetForm : Form {
        readonly TrayContext owner;
        readonly LayeredSurface surface = new LayeredSurface();
        readonly Timer watchTimer = new Timer { Interval = 650 };
        readonly Timer hoverWatchTimer = new Timer { Interval = 100 };
        readonly Timer animTimer = new Timer { Interval = 15 };
        readonly Stopwatch animClock = new Stopwatch();
        readonly ContextMenuStrip menu = new ContextMenuStrip();

        bool expanded, hovered, dragging, dragMoved, disposed;
        bool autoHiddenOnEdge;
        bool hasCommittedFrame;
        DateTime lastCursorInsideUtc = DateTime.UtcNow;
        WidgetVisibilityMode visibilityMode = WidgetVisibilityMode.Normal;
        Point dragStartCursor, dragStartLocation, dragPhysicalLocation;
        Size logicalSize;
        Rectangle currentPhysicalBounds;
        Point currentPhysicalPos;
        byte currentAlpha = 255;
        float currentDpiScale = 1f;
        int hoveredRow = -1;
        string lastRenderSignature;
        List<DesktopWidgetEntry> cachedEntries = new List<DesktopWidgetEntry>();

        // Spring animation state
        bool animating;
        Point animStartPos, animTargetPos;
        Size animStartLogicalSize, animTargetLogicalSize;
        byte animStartAlpha = 255, animTargetAlpha = 255;
        int animDurationMs = 260;
        bool animBouncy = true;
        double animOvershoot = 1.45;
        bool animTargetHiddenTab;
        bool animTargetExpanded;
        bool animTargetStripMode;
        Rectangle animWorkArea;

        internal DesktopWidgetForm(TrayContext owner) {
            this.owner = owner;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = DeviceSettings.Current.DesktopWidget.TopMost;
            Name = "MyFinderDesktopWidgetForm";

            BuildContextMenu();
            watchTimer.Tick += delegate { EvaluateDisplayState(); };
            hoverWatchTimer.Tick += delegate { EvaluateEdgeAutoHideTick(Cursor.Position, DateTime.UtcNow); };
            animTimer.Tick += delegate { StepAnimation(); };
            menu.Closed += delegate {
                lastCursorInsideUtc = DateTime.UtcNow;
                hovered = IsPointNearWidget(Cursor.Position, 8);
                SyncHoverWatch();
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
        internal bool IsAnimating { get { return animating; } }
        internal WidgetVisibilityMode CurrentVisibilityMode { get { return visibilityMode; } }
        internal Size LogicalSize { get { return logicalSize; } }

        bool IsPointNearWidget(Point screenPoint, int inflatePx) {
            if (currentPhysicalBounds.IsEmpty) return false;
            var hit = Rectangle.Inflate(currentPhysicalBounds, inflatePx, inflatePx);
            return hit.Contains(screenPoint);
        }

        void SyncHoverWatch() {
            if (disposed || IsDisposed) return;
            var cfg = DeviceSettings.Current.DesktopWidget;
            bool shouldWatch = cfg != null && cfg.AutoHideOnEdge && DesktopWidgetGeometry.NormalizeEdge(cfg.DockEdge) != "none" && !autoHiddenOnEdge && !dragging;
            if (shouldWatch) {
                if (!hoverWatchTimer.Enabled) hoverWatchTimer.Start();
            } else {
                if (hoverWatchTimer.Enabled) hoverWatchTimer.Stop();
            }
        }

        // Exposed internally so deterministic tests can verify auto-hide even when Windows drops WM_MOUSELEAVE.
        internal void EvaluateEdgeAutoHideTick(Point cursorPos, DateTime utcNow) {
            if (disposed || IsDisposed || dragging) return;
            var cfg = DeviceSettings.Current.DesktopWidget;
            if (cfg == null || !cfg.AutoHideOnEdge || DesktopWidgetGeometry.NormalizeEdge(cfg.DockEdge) == "none") {
                if (hoverWatchTimer.Enabled) hoverWatchTimer.Stop();
                return;
            }
            if (autoHiddenOnEdge) {
                if (hoverWatchTimer.Enabled) hoverWatchTimer.Stop();
                return;
            }
            if (menu.Visible) {
                lastCursorInsideUtc = utcNow;
                return;
            }
            bool inside = IsPointNearWidget(cursorPos, 8);
            if (inside) {
                hovered = true;
                lastCursorInsideUtc = utcNow;
                return;
            }
            if (hovered) {
                hovered = false;
                hoveredRow = -1;
                lastCursorInsideUtc = utcNow;
                if (expanded) RenderNow(false);
                return;
            }
            if (expanded) return;
            // After cursor stays outside for 380ms, smoothly retract back to the edge handle.
            if ((utcNow - lastCursorInsideUtc).TotalMilliseconds >= 380.0) {
                autoHiddenOnEdge = true;
                if (hoverWatchTimer.Enabled) hoverWatchTimer.Stop();
                TransitionToCurrentState(230, false, 1.0);
            }
        }

        void BuildContextMenu() {
            ThemedContextMenu.Apply(menu);
            menu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e) {
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

                var hiddenStyleMenu = new ToolStripMenuItem("贴边隐藏样式");
                for (int i = 0; i < DesktopWidgetGeometry.HiddenStyleKeys.Length; i++) {
                    string key = DesktopWidgetGeometry.HiddenStyleKeys[i];
                    var item = new ToolStripMenuItem(DesktopWidgetGeometry.HiddenStyleNames[i]) { Checked = DesktopWidgetGeometry.NormalizeHiddenStyle(cfg.HiddenStyle) == key };
                    item.Click += delegate { UpdateWidgetConfig(delegate(DesktopWidgetSettings w) { w.HiddenStyle = key; }); };
                    hiddenStyleMenu.DropDownItems.Add(item);
                }
                menu.Items.Add(hiddenStyleMenu);

                var topMost = new ToolStripMenuItem("总在最前") { Checked = cfg.TopMost };
                topMost.Click += delegate { UpdateWidgetConfig(delegate(DesktopWidgetSettings w) { w.TopMost = !w.TopMost; }); };
                menu.Items.Add(topMost);

                menu.Items.Add(new ToolStripSeparator());
                var openCc = new ToolStripMenuItem("打开控制中心");
                openCc.Click += delegate { owner.ShowControlCenter(ControlCenterPage.Placement); };
                menu.Items.Add(openCc);

                var toggleTray = new ToolStripMenuItem("显示托盘图标") { Checked = !DeviceSettings.Current.HideTrayIcon };
                toggleTray.Click += delegate {
                    var pref = DeviceSettings.Current;
                    pref.HideTrayIcon = !pref.HideTrayIcon;
                    try { owner.SavePreferences(pref); owner.ConfigurationChanged(false); } catch (Exception) { }
                };
                menu.Items.Add(toggleTray);

                var closeItem = new ToolStripMenuItem("关闭桌面小组件");
                closeItem.Click += delegate { UpdateWidgetConfig(delegate(DesktopWidgetSettings w) { w.Enabled = false; }); };
                menu.Items.Add(closeItem);

                menu.Items.Add(new ToolStripSeparator());
                var exitItem = new ToolStripMenuItem("退出");
                exitItem.Click += delegate { owner.ExitThread(); };
                menu.Items.Add(exitItem);
                e.Cancel = false;
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
            SyncHoverWatch();
            if (!watchTimer.Enabled) watchTimer.Start();
        }

        void StopAnimation() {
            animating = false;
            if (animTimer.Enabled) animTimer.Stop();
            if (animClock.IsRunning) animClock.Stop();
        }

        void ComputeTargetState(out DesktopWidgetSettings cfg, out bool dark, out ThemePalette palette, out bool hiddenTab, out bool stripMode, out Rectangle workArea, out Size targetLogical, out Point targetPos, out byte targetAlpha) {
            cfg = DeviceSettings.Current.DesktopWidget;
            int maxDev = expanded ? 6 : Math.Max(1, Math.Min(4, cfg.MaxDevices));
            cachedEntries = CollectEntries(maxDev);

            dark = Style.Dark;
            palette = ThemePalette.Current(dark);
            hiddenTab = !expanded && autoHiddenOnEdge && DesktopWidgetGeometry.NormalizeEdge(cfg.DockEdge) != "none";
            stripMode = visibilityMode == WidgetVisibilityMode.EdgeStrip;

            var screen = ResolveTargetScreen(cfg);
            workArea = screen.WorkingArea;
            currentDpiScale = NativeDisplay.ScaleAt(new Point(workArea.Left + workArea.Width / 2, workArea.Top + workArea.Height / 2));

            targetLogical = expanded
                ? DesktopWidgetGeometry.ExpandedLogicalSize(cachedEntries.Count, VisualDesign.LargeFont)
                : DesktopWidgetGeometry.CollapsedLogicalSize(stripMode ? "strip" : cfg.Style, cachedEntries.Count, VisualDesign.LargeFont, cfg.DockEdge, hiddenTab, cfg.HiddenStyle);

            var physSize = LayeredSurface.ComputePhysicalSize(targetLogical, currentDpiScale);
            targetPos = dragging ? dragPhysicalLocation : DesktopWidgetGeometry.ComputePhysicalLocation(cfg.DockEdge, physSize, workArea, cfg.AnchorXRatio, cfg.AnchorYRatio, hiddenTab);

            targetAlpha = (byte)Math.Max(140, Math.Min(255, (int)Math.Round(cfg.OpacityPercent * 2.55)));
            if (hiddenTab) targetAlpha = 255;
        }

        void TransitionToCurrentState(int durationMs, bool bouncy, double overshoot) {
            if (disposed || IsDisposed || !IsHandleCreated) return;
            if (visibilityMode == WidgetVisibilityMode.Hidden) return;

            DesktopWidgetSettings cfg; bool dark, hiddenTab, stripMode; ThemePalette palette; Rectangle workArea; Size targetLogical; Point targetPos; byte targetAlpha;
            ComputeTargetState(out cfg, out dark, out palette, out hiddenTab, out stripMode, out workArea, out targetLogical, out targetPos, out targetAlpha);

            if (!hasCommittedFrame || dragging || DeviceSettings.Current.ReducedMotion || durationMs <= 0 ||
                (currentPhysicalPos == targetPos && logicalSize == targetLogical && currentAlpha == targetAlpha)) {
                StopAnimation();
                RenderNow(true);
                SyncHoverWatch();
                return;
            }

            animStartPos = currentPhysicalPos;
            animTargetPos = targetPos;
            animStartLogicalSize = logicalSize.Width > 0 && logicalSize.Height > 0 ? logicalSize : targetLogical;
            animTargetLogicalSize = targetLogical;
            animStartAlpha = currentAlpha;
            animTargetAlpha = targetAlpha;
            animDurationMs = durationMs;
            animBouncy = bouncy;
            animOvershoot = overshoot;
            animTargetHiddenTab = hiddenTab;
            animTargetExpanded = expanded;
            animTargetStripMode = stripMode;
            animWorkArea = workArea;
            animating = true;
            lastRenderSignature = null;
            animClock.Reset();
            animClock.Start();
            if (!animTimer.Enabled) animTimer.Start();
            StepAnimation();
        }

        void StepAnimation() {
            if (!animating || disposed || IsDisposed || !IsHandleCreated) {
                StopAnimation();
                return;
            }
            double t = animDurationMs <= 0 ? 1.0 : animClock.ElapsedMilliseconds / (double)animDurationMs;
            if (t >= 1.0) {
                StopAnimation();
                RenderNow(true);
                SyncHoverWatch();
                return;
            }

            Point framePos; Size frameLogical; byte frameAlpha;
            DesktopWidgetSpring.InterpolateFrame(
                animStartPos, animTargetPos,
                animStartLogicalSize, animTargetLogicalSize,
                animStartAlpha, animTargetAlpha,
                animWorkArea, currentDpiScale,
                t, animBouncy, animOvershoot,
                out framePos, out frameLogical, out frameAlpha);

            var cfg = DeviceSettings.Current.DesktopWidget;
            bool dark = Style.Dark;
            var palette = ThemePalette.Current(dark);
            bool drawHiddenTab = animTargetHiddenTab
                ? (t >= 0.72 || Math.Min(frameLogical.Width, frameLogical.Height) <= 16)
                : (t < 0.22 && Math.Min(frameLogical.Width, frameLogical.Height) <= 18);

            var physSize = LayeredSurface.ComputePhysicalSize(frameLogical, currentDpiScale);
            try {
                surface.Commit(Handle, framePos, frameLogical, currentDpiScale, frameAlpha, delegate(Graphics g, SizeF sz) {
                    DesktopWidgetRenderer.Paint(g, sz, cachedEntries, palette, dark, cfg, animTargetExpanded && !drawHiddenTab, animTargetStripMode, drawHiddenTab, hoveredRow);
                });
                logicalSize = frameLogical;
                currentPhysicalPos = framePos;
                currentAlpha = frameAlpha;
                currentPhysicalBounds = new Rectangle(framePos, physSize);
                hasCommittedFrame = true;
            } catch (Exception) {
                StopAnimation();
                RenderNow(true);
            }
        }

        bool pendingSnapAnim;

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
            bool wasHidden = autoHiddenOnEdge;
            if (!docked || !cfg.AutoHideOnEdge) {
                autoHiddenOnEdge = false;
            } else if (!expanded && !dragging && !autoHiddenOnEdge) {
                if (IsPointNearWidget(Cursor.Position, 8)) lastCursorInsideUtc = DateTime.UtcNow;
            }
            SyncHoverWatch();

            if (pendingSnapAnim) {
                pendingSnapAnim = false;
                TransitionToCurrentState(260, true, 1.35);
                return;
            }
            if (wasHidden && !autoHiddenOnEdge) {
                TransitionToCurrentState(270, true, 1.40);
                return;
            }
            if (animating) {
                int maxDev = expanded ? 6 : Math.Max(1, Math.Min(4, cfg.MaxDevices));
                cachedEntries = CollectEntries(maxDev);
                return;
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
                    StopAnimation();
                    expanded = false;
                    if (Visible) Hide();
                    return;
                }
                if (visibilityMode == WidgetVisibilityMode.EdgeStrip) expanded = false;
                if (!Visible) Show();
                TransitionToCurrentState(220, false, 1.0);
            }
        }

        internal void RenderNow(bool force) {
            if (disposed || IsDisposed || !IsHandleCreated) return;
            if (visibilityMode == WidgetVisibilityMode.Hidden) return;

            DesktopWidgetSettings cfg; bool dark, hiddenTab, stripMode; ThemePalette palette; Rectangle workArea; Size targetLogical; Point physPos; byte alpha;
            ComputeTargetState(out cfg, out dark, out palette, out hiddenTab, out stripMode, out workArea, out targetLogical, out physPos, out alpha);
            var physSize = LayeredSurface.ComputePhysicalSize(targetLogical, currentDpiScale);

            string sig = BuildRenderSignature(cfg, dark, hiddenTab, stripMode, physPos, physSize);
            if (!force && sig == lastRenderSignature) return;
            lastRenderSignature = sig;

            // UpdateLayeredWindow atomically commits physical position, size and alpha.
            // Avoid redundant WinForms Location/Size writes that can trigger double-DPI scaling.
            surface.Commit(Handle, physPos, targetLogical, currentDpiScale, alpha, delegate(Graphics g, SizeF sz) {
                DesktopWidgetRenderer.Paint(g, sz, cachedEntries, palette, dark, cfg, expanded, stripMode, hiddenTab, hoveredRow);
            });
            logicalSize = targetLogical;
            currentPhysicalPos = physPos;
            currentAlpha = alpha;
            currentPhysicalBounds = new Rectangle(physPos, physSize);
            hasCommittedFrame = true;
        }

        string BuildRenderSignature(DesktopWidgetSettings cfg, bool dark, bool hiddenTab, bool stripMode, Point pos, Size size) {
            var sb = new StringBuilder();
            sb.Append(dark).Append('|').Append(DeviceSettings.Current.ThemeStyle).Append('|').Append(VisualDesign.LargeFont).Append('|').Append(VisualDesign.HighContrast).Append('|');
            sb.Append(cfg.Style).Append('|').Append(cfg.DockEdge).Append('|').Append(cfg.HiddenStyle).Append('|').Append(cfg.OpacityPercent).Append('|').Append(expanded).Append('|').Append(hiddenTab).Append('|').Append(stripMode).Append('|');
            sb.Append(hoveredRow).Append('|').Append(pos.X).Append(',').Append(pos.Y).Append('|').Append(size.Width).Append('x').Append(size.Height).Append('|');
            foreach (var e in cachedEntries) sb.Append(e.Id).Append(':').Append(e.Name).Append(':').Append(e.Percent).Append(':').Append(e.Fresh).Append(':').Append(e.Charging).Append(';');
            return sb.ToString();
        }

        protected override void WndProc(ref Message m) {
            if (m.Msg == 0x0021) { // WM_MOUSEACTIVATE
                m.Result = new IntPtr(3); // MA_NOACTIVATE
                return;
            }
            if (m.Msg == 0x007E || m.Msg == 0x02E0 || m.Msg == 0x0218) { // WM_DISPLAYCHANGE / WM_DPICHANGED / WM_POWERBROADCAST
                if (m.Msg == 0x02E0) {
                    m.Result = IntPtr.Zero;
                    if (!dragging) { StopAnimation(); lastRenderSignature = null; RenderNow(true); SyncHoverWatch(); }
                    return;
                }
                base.WndProc(ref m);
                if (!dragging) { StopAnimation(); lastRenderSignature = null; RenderNow(true); SyncHoverWatch(); }
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnMouseEnter(EventArgs e) {
            base.OnMouseEnter(e);
            hovered = true;
            lastCursorInsideUtc = DateTime.UtcNow;
            if (autoHiddenOnEdge) {
                autoHiddenOnEdge = false;
                TransitionToCurrentState(280, true, 1.45);
            }
            SyncHoverWatch();
        }

        protected override void OnMouseLeave(EventArgs e) {
            base.OnMouseLeave(e);
            if (dragging) return;
            if (!IsPointNearWidget(Cursor.Position, 6)) {
                hovered = false;
                hoveredRow = -1;
                lastCursorInsideUtc = DateTime.UtcNow;
                if (expanded && !animating) RenderNow(false);
            }
            // Always keep hoverWatchTimer active while revealed on a docked edge so dropped WM_MOUSELEAVE never stalls auto-hide.
            SyncHoverWatch();
        }

        protected override void OnMouseDown(MouseEventArgs e) {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) {
                menu.Show(Cursor.Position);
                return;
            }
            if (e.Button == MouseButtons.Left) {
                StopAnimation();
                dragging = true;
                dragMoved = false;
                dragStartCursor = Cursor.Position;
                dragStartLocation = currentPhysicalBounds.IsEmpty ? Location : currentPhysicalBounds.Location;
                dragPhysicalLocation = dragStartLocation;
                Capture = true;
                SyncHoverWatch();
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
            lastCursorInsideUtc = DateTime.UtcNow;
            if (expanded && !animating) {
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
                pendingSnapAnim = snapped != "none";
                UpdateWidgetConfig(delegate(DesktopWidgetSettings w) {
                    w.DockEdge = snapped;
                    w.AnchorXRatio = rx;
                    w.AnchorYRatio = ry;
                    w.MonitorDeviceName = screen.DeviceName;
                });
                lastCursorInsideUtc = DateTime.UtcNow;
                hovered = IsPointNearWidget(Cursor.Position, 8);
                SyncHoverWatch();
                return;
            }
            HandleClick(e.Location);
        }

        void HandleClick(Point physicalPoint) {
            float lx = physicalPoint.X / Math.Max(1f, currentDpiScale);
            float ly = physicalPoint.Y / Math.Max(1f, currentDpiScale);
            if (!expanded) {
                expanded = true;
                autoHiddenOnEdge = false;
                TransitionToCurrentState(270, true, 1.35);
                return;
            }
            int headerH = VisualDesign.LargeFont ? 32 : 28;
            int rowH = VisualDesign.LargeFont ? 50 : 44;
            int footerH = VisualDesign.LargeFont ? 34 : 30;
            if (ly < headerH) {
                expanded = false;
                hoveredRow = -1;
                lastCursorInsideUtc = DateTime.UtcNow;
                TransitionToCurrentState(210, false, 1.0);
                SyncHoverWatch();
                return;
            }
            if (ly >= logicalSize.Height - footerH - 4) {
                if (lx < logicalSize.Width / 2f) {
                    owner.RefreshBattery(true);
                } else {
                    expanded = false;
                    hoveredRow = -1;
                    lastCursorInsideUtc = DateTime.UtcNow;
                    TransitionToCurrentState(210, false, 1.0);
                    SyncHoverWatch();
                    owner.ShowControlCenter(ControlCenterPage.Devices);
                }
                return;
            }
            int idx = (int)((ly - headerH - 2) / rowH);
            if (idx >= 0 && idx < cachedEntries.Count) {
                expanded = false;
                hoveredRow = -1;
                lastCursorInsideUtc = DateTime.UtcNow;
                TransitionToCurrentState(210, false, 1.0);
                SyncHoverWatch();
                owner.ShowControlCenter(ControlCenterPage.Devices);
            } else {
                expanded = false;
                hoveredRow = -1;
                lastCursorInsideUtc = DateTime.UtcNow;
                TransitionToCurrentState(210, false, 1.0);
                SyncHoverWatch();
            }
        }

        protected override void Dispose(bool disposing) {
            if (disposed) return;
            disposed = true;
            if (disposing) {
                StopAnimation();
                watchTimer.Stop();
                watchTimer.Dispose();
                hoverWatchTimer.Stop();
                hoverWatchTimer.Dispose();
                animTimer.Dispose();
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
