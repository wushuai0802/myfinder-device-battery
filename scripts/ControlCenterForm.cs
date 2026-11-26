using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AtkBattery;

namespace AtkBatteryTray {
    public enum ControlCenterPage { Devices, Appearance, Placement, Reminders, Diagnostics, Settings }

    internal enum DeviceBatteryCapability { Supported, NoBatteryPlaceholder }

    internal static class ControlCenterTokens {
        internal const float RadiusCard = 10f;
        internal const float RadiusControl = 6f;
        internal const float RadiusBadge = 10f;
        internal const int SidebarWidth = 188;
        internal const int ButtonHeight = 30;
        internal const int InputHeight = 30;
        internal const int DividerInset = 20;

        static double Luma(Color c) {
            Func<int, double> f = delegate(int v) { double x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); };
            return 0.2126 * f(c.R) + 0.7152 * f(c.G) + 0.0722 * f(c.B);
        }

        internal static double Contrast(Color a, Color b) {
            double x = Luma(a), y = Luma(b);
            if (x < y) { double t = x; x = y; y = t; }
            return (x + 0.05) / (y + 0.05);
        }

        // Secondary (caption) text: slightly lifted in dark mode and guaranteed >= 4.5:1 (WCAG AA) on every control-center surface.
        internal static Color SecondaryText(ThemePalette p, bool dark) {
            if (VisualDesign.HighContrast) return p.Muted;
            Color c = dark ? ThemePalette.Blend(p.Muted, p.Foreground, 38) : p.Muted;
            for (int i = 0; i < 14; i++) {
                if (Contrast(c, p.Card) >= 4.6 && Contrast(c, p.WindowBackground) >= 4.6 && Contrast(c, p.RaisedSurface) >= 4.6) break;
                c = ThemePalette.Blend(c, dark ? Color.White : Color.Black, 22);
            }
            return c;
        }

        internal static string SymbolForKind(string kind) {
            if (kind == "mouse" || kind == "headset" || kind == "keyboard" || kind == "gamepad") return kind;
            return "power";
        }
    }

    internal static class ControlCenterIconLoader {
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr hIcon);

        internal static Icon LoadWindowIcon(out bool ownsIcon) {
            ownsIcon = false;
            try {
                string exe = Application.ExecutablePath;
                if (!String.IsNullOrEmpty(exe) && File.Exists(exe)) {
                    string baseName = Path.GetFileName(exe);
                    if (String.Equals(baseName, "AtkMouseBattery.exe", StringComparison.OrdinalIgnoreCase)) {
                        var extracted = Icon.ExtractAssociatedIcon(exe);
                        if (extracted != null) { ownsIcon = true; return extracted; }
                    }
                }
            } catch (Exception) { }

            try {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                for (int i = 0; i < 4 && !String.IsNullOrEmpty(dir); i++) {
                    string candidate = Path.Combine(dir, @"assets\app-icon.ico");
                    if (File.Exists(candidate)) {
                        ownsIcon = true;
                        return new Icon(candidate, new Size(32, 32));
                    }
                    dir = Path.GetDirectoryName(dir);
                }
            } catch (Exception) { }

            // Crisp vector fallback matching assets/app-icon.ico when running inside standalone test EXEs
            ownsIcon = true;
            return RenderBrandIcon(32);
        }

        internal static Icon RenderBrandIcon(int size) {
            using (var bmp = new Bitmap(size, size)) {
                using (var g = Graphics.FromImage(bmp)) {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    DrawBrandGlyph(g, new RectangleF(1, 1, size - 2, size - 2), true);
                }
                IntPtr hIcon = bmp.GetHicon();
                try {
                    using (var temp = Icon.FromHandle(hIcon)) return (Icon)temp.Clone();
                } finally {
                    DestroyIcon(hIcon);
                }
            }
        }

        internal static void DrawBrandGlyph(Graphics g, RectangleF r, bool darkSurface) {
            float rad = Math.Max(4f, r.Width * 0.24f);
            Color bg = darkSurface ? Color.FromArgb(28, 33, 41) : Color.FromArgb(24, 30, 38);
            CompactRenderer.Fill(g, r, bg, rad);
            using (var borderPath = VisualDesign.RoundRect(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f, rad))
            using (var borderPen = new Pen(Color.FromArgb(90, 255, 255, 255), 1f)) {
                g.DrawPath(borderPen, borderPath);
            }
            float pad = r.Width * 0.22f;
            var ring = new RectangleF(r.X + pad, r.Y + pad, r.Width - pad * 2f, r.Height - pad * 2f);
            float stroke = Math.Max(2f, r.Width * 0.11f);
            using (var trackPen = new Pen(Color.FromArgb(70, 255, 255, 255), stroke)) g.DrawEllipse(trackPen, ring);
            using (var arcPen = new Pen(Color.FromArgb(74, 214, 146), stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
                g.DrawArc(arcPen, ring, -90f, 285f);
            }
            float dot = Math.Max(2.5f, r.Width * 0.14f);
            using (var dotBrush = new SolidBrush(Color.FromArgb(238, 244, 248))) {
                g.FillEllipse(dotBrush, r.X + (r.Width - dot) / 2f, r.Y + (r.Height - dot) / 2f, dot, dot);
            }
        }
    }

    internal sealed class ControlCenterDeviceItem {
        internal string Key;
        internal string Name;
        internal string Kind;
        internal string Transport;
        internal string StateText;
        internal string VerificationText;
        internal string NoBatteryReason;
        internal DeviceBatteryCapability Capability;
        internal BatteryView Battery;
        internal WirelessCard Card;
        internal UnadaptedDevice Unadapted;
        internal bool CanRename;
        internal bool CanHide;
        internal bool Hidden;
        internal bool CanPinTray;
        internal bool PinnedTray;
        internal bool CanStopFamily;
        internal bool HasWritableDpi;
        internal int[] DpiStages;
        internal int ActiveDpiIndex;
        internal int? CurrentDpi;
        internal string DpiTargetToken;
        internal bool IsNativeMouse;
        internal bool IsNativeHeadset;
        internal bool IsAudioOnly;
    }

    internal sealed class ControlCenterController : IDisposable {
        readonly TrayContext owner;
        readonly Action<string> copyText;
        readonly Action<string> openLink;
        ControlCenterForm active;
        bool disposed;

        internal ControlCenterController(TrayContext owner, Action<string> copyText = null, Action<string> openLink = null) {
            this.owner = owner;
            this.copyText = copyText ?? delegate(string text) { Clipboard.SetText(text); };
            this.openLink = openLink ?? delegate(string url) { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); };
        }

        internal bool IsOpen { get { return !disposed && active != null && !active.IsDisposed; } }
        internal ControlCenterForm ActiveForm { get { return IsOpen ? active : null; } }

        internal void Show(ControlCenterPage page = ControlCenterPage.Devices) {
            if (disposed) return;
            if (active != null && !active.IsDisposed) {
                active.SelectPage(page);
                DialogVisibility.RestoreAndActivate(active);
                return;
            }
            var form = new ControlCenterForm(owner, page, copyText, openLink);
            form.FormClosed += delegate {
                if (active == form) active = null;
            };
            active = form;
            form.Show();
            DialogVisibility.RestoreAndActivate(form);
        }

        internal void Activate() {
            if (IsOpen) DialogVisibility.RestoreAndActivate(active);
        }

        internal void NotifyDataChanged() {
            if (IsOpen) active.RefreshData();
        }

        internal void NotifyThemeChanged() {
            if (IsOpen) active.RefreshTheme();
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

    // Rounded Fluent/Linear surface card without jagged Region clipping or AntiAlias edge artifacts.
    internal sealed class ControlCenterCard : Panel {
        readonly ThemePalette palette;
        readonly bool dark;

        internal ControlCenterCard(ThemePalette palette, bool dark, Padding padding) {
            this.palette = palette;
            this.dark = dark;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(padding.Left + 2, padding.Top + 2, padding.Right + 2, padding.Bottom + 2);
            BackColor = VisualDesign.HighContrast ? SystemColors.Window : palette.Card;
            ForeColor = palette.Foreground;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e) {
            // Paint entire background cleanly inside OnPaint using g.Clear() first to avoid GDI+ AntiAlias edge bleed.
        }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            Color outerBg = Parent != null ? Parent.BackColor : palette.WindowBackground;
            g.Clear(outerBg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var rect = new RectangleF(1f, 1f, Math.Max(4f, Width - 2f), Math.Max(4f, Height - 2f));
            Color fill = VisualDesign.HighContrast ? SystemColors.Window : palette.Card;
            CompactRenderer.Fill(g, rect, fill, ControlCenterTokens.RadiusCard);

            if (!VisualDesign.HighContrast && dark) {
                using (var rimPen = new Pen(Color.FromArgb(18, 255, 255, 255), 1f)) {
                    g.DrawLine(rimPen, rect.Left + ControlCenterTokens.RadiusCard, rect.Top + 1f, rect.Right - ControlCenterTokens.RadiusCard, rect.Top + 1f);
                }
            }

            Color border = VisualDesign.HighContrast ? palette.Foreground : Color.FromArgb(dark ? 185 : 140, palette.Border);
            using (var path = VisualDesign.RoundRect(rect.X, rect.Y, rect.Width, rect.Height, ControlCenterTokens.RadiusCard))
            using (var pen = new Pen(border, 1f)) {
                g.DrawPath(pen, path);
            }
        }
    }

    internal enum ControlCenterButtonVariant { Secondary, Primary, Danger, Quiet, Segment }

    internal sealed class ControlCenterButton : Control {
        readonly ThemePalette palette;
        readonly bool dark;
        readonly ControlCenterButtonVariant variant;
        readonly string symbolName;
        readonly Font labelFont;
        bool hovered, pressed;
        // Segment variant: the button paints its slice of a shared connected frame (see CreateTaskbarOffsetStepper).
        internal Control FrameOwner;
        internal bool SegmentLast;
        internal bool StaticText;

        internal void MakeStatic() {
            StaticText = true;
            TabStop = false;
            Cursor = Cursors.Default;
            AccessibleRole = AccessibleRole.StaticText;
        }

        internal ControlCenterButton(string text, ThemePalette palette, bool dark, Font font, ControlCenterButtonVariant variant = ControlCenterButtonVariant.Secondary, string symbolName = null) {
            Text = text ?? "";
            this.palette = palette;
            this.dark = dark;
            this.variant = variant;
            this.symbolName = symbolName;
            this.labelFont = font;
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleName = Text;
            AccessibleRole = AccessibleRole.PushButton;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Opaque | ControlStyles.SupportsTransparentBackColor, false);
            BackColor = VisualDesign.HighContrast ? SystemColors.Window : palette.Card;

            var measured = TextRenderer.MeasureText(Text, font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            int iconSpace = String.IsNullOrEmpty(symbolName) ? 0 : 18;
            Size = new Size(Math.Max(68, measured.Width + iconSpace + 24), ControlCenterTokens.ButtonHeight);
        }

        void Trigger() {
            if (!Enabled || StaticText) return;
            OnClick(EventArgs.Empty);
        }

        protected override void OnMouseEnter(EventArgs e) { hovered = !StaticText; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left && Enabled && !StaticText) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) {
            bool fire = pressed && Enabled && ClientRectangle.Contains(e.Location);
            pressed = false;
            Invalidate();
            base.OnMouseUp(e);
            if (fire) Trigger();
        }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }
        protected override void OnKeyDown(KeyEventArgs e) {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { e.Handled = true; Trigger(); }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            Color hostBg = Parent != null ? Parent.BackColor : BackColor;
            g.Clear(hostBg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            if (variant == ControlCenterButtonVariant.Segment) { PaintSegment(g); return; }
            if (variant == ControlCenterButtonVariant.Quiet) { PaintQuiet(g, hostBg); return; }
            var rect = new RectangleF(1f, 1f, Width - 2f, Height - 2f);
            Color fill, fg, border;
            if (VisualDesign.HighContrast) {
                fill = pressed || hovered ? SystemColors.Highlight : SystemColors.Window;
                fg = !Enabled ? SystemColors.GrayText : (pressed || hovered ? SystemColors.HighlightText : SystemColors.WindowText);
                border = SystemColors.WindowText;
            } else if (variant == ControlCenterButtonVariant.Primary) {
                fill = !Enabled ? palette.Track : palette.Selected;
                fg = !Enabled ? palette.Muted : palette.SelectedForeground;
                border = fill;
                if (Enabled && (hovered || pressed)) {
                    var ov = palette.Interaction(pressed);
                    fill = ThemePalette.Blend(fill, ov, ov.A);
                }
            } else if (variant == ControlCenterButtonVariant.Danger) {
                Color red = ThemePalette.LevelColor(10, dark);
                fill = !Enabled ? palette.Card : (pressed ? Color.FromArgb(dark ? 42 : 28, red) : hovered ? Color.FromArgb(dark ? 28 : 18, red) : palette.Card);
                fg = !Enabled ? palette.Muted : red;
                border = !Enabled ? palette.Border : Color.FromArgb(dark ? 95 : 85, red);
            } else {
                fill = !Enabled ? palette.Card : palette.RaisedSurface;
                fg = !Enabled ? palette.Muted : palette.Foreground;
                border = palette.Border;
                if (Enabled && (hovered || pressed)) {
                    var ov = palette.Interaction(pressed);
                    fill = ThemePalette.Blend(fill, ov, ov.A);
                }
            }

            CompactRenderer.Fill(g, rect, fill, ControlCenterTokens.RadiusControl);
            using (var path = VisualDesign.RoundRect(rect.X, rect.Y, rect.Width, rect.Height, ControlCenterTokens.RadiusControl))
            using (var pen = new Pen(border, 1f)) g.DrawPath(pen, path);

            if (!String.IsNullOrEmpty(symbolName)) {
                var textSize = TextRenderer.MeasureText(Text, labelFont, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                float totalW = 13f + 6f + textSize.Width;
                float sx = Math.Max(8f, (Width - totalW) / 2f);
                CompactRenderer.DrawSymbol(g, symbolName, new RectangleF(sx, (Height - 13f) / 2f, 13f, 13f), fg);
                CompactRenderer.DrawLabel(g, Text, new RectangleF(sx + 18f, 0, Math.Max(20f, Width - sx - 24f), Height), labelFont.SizeInPoints * 1.33f, variant == ControlCenterButtonVariant.Primary, fg, StringAlignment.Near);
            } else {
                CompactRenderer.DrawLabel(g, Text, new RectangleF(6, 0, Width - 12, Height), labelFont.SizeInPoints * 1.33f, variant == ControlCenterButtonVariant.Primary, fg, StringAlignment.Center);
            }

            if (Focused && ShowFocusCues) {
                CompactRenderer.Outline(g, rect, palette.Accent, 1f);
            }
        }

        float LabelPx { get { return labelFont.SizeInPoints * 1.33f; } }

        void PaintSegment(Graphics g) {
            Control frame = FrameOwner ?? Parent;
            Color outer = frame != null && frame.Parent != null ? frame.Parent.BackColor : BackColor;
            g.Clear(outer);
            bool hc = VisualDesign.HighContrast;
            bool active = Enabled && !StaticText && (hovered || pressed);
            Color fill = hc ? SystemColors.Window : palette.RaisedSurface;
            Color border = hc ? SystemColors.WindowText : palette.Border;
            var shape = new RectangleF(-Left + 0.5f, -Top + 0.5f, (frame == null ? Width : frame.Width) - 1f, (frame == null ? Height : frame.Height) - 1f);
            using (var path = VisualDesign.RoundRect(shape.X, shape.Y, shape.Width, shape.Height, ControlCenterTokens.RadiusControl)) {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (active) {
                    var state = g.Save();
                    g.SetClip(path, CombineMode.Intersect);
                    Color ov = hc ? SystemColors.Highlight : ThemePalette.Blend(fill, palette.Interaction(pressed), palette.Interaction(pressed).A);
                    using (var b = new SolidBrush(ov)) g.FillRectangle(b, 0, 0, Width, Height);
                    g.Restore(state);
                }
                using (var pen = new Pen(border, 1f)) g.DrawPath(pen, path);
            }
            if (!SegmentLast) {
                using (var pen = new Pen(hc ? SystemColors.WindowText : Color.FromArgb(150, palette.Border), 1f)) g.DrawLine(pen, Width - 0.5f, 6f, Width - 0.5f, Height - 6f);
            }
            Color fg = hc ? (!Enabled ? SystemColors.GrayText : (active ? SystemColors.HighlightText : SystemColors.WindowText))
                : (!Enabled ? Color.FromArgb(125, palette.Muted) : palette.Foreground);
            CompactRenderer.DrawLabel(g, Text, new RectangleF(4, 0, Math.Max(10f, Width - 8f), Height), LabelPx, false, fg, StringAlignment.Center);
            if (Focused && ShowFocusCues && !StaticText) CompactRenderer.Outline(g, new RectangleF(2.5f, 2.5f, Width - 5f, Height - 5f), hc ? SystemColors.Highlight : palette.Accent, 1f);
        }

        void PaintQuiet(Graphics g, Color hostBg) {
            bool hc = VisualDesign.HighContrast;
            bool active = Enabled && (hovered || pressed);
            var rect = new RectangleF(1f, 3f, Width - 2f, Height - 6f);
            if (active) {
                if (hc) CompactRenderer.Fill(g, rect, SystemColors.Highlight, ControlCenterTokens.RadiusControl);
                else CompactRenderer.Fill(g, rect, palette.Interaction(pressed), ControlCenterTokens.RadiusControl);
            }
            Color fg = hc ? (!Enabled ? SystemColors.GrayText : (active ? SystemColors.HighlightText : SystemColors.WindowText))
                : (!Enabled ? Color.FromArgb(125, palette.Muted) : palette.Foreground);
            CompactRenderer.DrawLabel(g, Text, new RectangleF(4, 0, Math.Max(10f, Width - 8f), Height), LabelPx, false, fg, StringAlignment.Center);
            if (Focused && ShowFocusCues) CompactRenderer.Outline(g, rect, hc ? SystemColors.Highlight : palette.Accent, 1f);
        }
    }

    internal enum ControlCenterBadgeTone { Muted, Positive, Accent }

    internal sealed class BufferedPanel : Panel {
        internal BufferedPanel() {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        }
    }

    internal sealed class ControlCenterBadge : Control {
        readonly ThemePalette palette;
        readonly bool dark;
        readonly ControlCenterBadgeTone tone;
        readonly bool showDot;
        readonly bool plain;
        readonly float fontPx;
        readonly float measuredTextWidth;
        internal Color? SurfaceColor;

        internal ControlCenterBadge(string text, ThemePalette palette, bool dark, Font font, ControlCenterBadgeTone tone, bool showDot = false, bool plain = false) {
            Text = text ?? "";
            this.palette = palette;
            this.dark = dark;
            this.tone = tone;
            this.plain = plain;
            this.showDot = showDot && !plain;
            this.fontPx = plain ? (VisualDesign.LargeFont ? 13f : 12f) : (VisualDesign.LargeFont ? 12f : 11f);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Opaque | ControlStyles.SupportsTransparentBackColor, false);
            BackColor = palette.Card;

            bool bold = tone != ControlCenterBadgeTone.Muted;
            using (var bmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(bmp))
            using (var measureFont = new Font("Microsoft YaHei UI", fontPx, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
            using (var sf = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces }) {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                measuredTextWidth = (float)Math.Ceiling(g.MeasureString(Text, measureFont, Int32.MaxValue, sf).Width) + 2f;
            }
            float dotBlock = this.showDot ? 11f : 0f;
            int padX = 11;
            if (plain) Size = new Size((int)Math.Ceiling(measuredTextWidth) + 2, VisualDesign.LargeFont ? 24 : 22);
            else Size = new Size(Math.Max(52, (int)Math.Ceiling(measuredTextWidth + dotBlock) + padX * 2), VisualDesign.LargeFont ? 24 : 22);
        }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            Color hostBg = SurfaceColor.HasValue ? SurfaceColor.Value : (Parent != null ? Parent.BackColor : BackColor);
            g.Clear(hostBg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            var rect = new RectangleF(1f, 1f, Width - 2f, Height - 2f);
            Color fill, fg, dotColor;
            if (VisualDesign.HighContrast) {
                fill = SystemColors.Window;
                fg = SystemColors.WindowText;
                dotColor = SystemColors.Highlight;
            } else if (tone == ControlCenterBadgeTone.Positive) {
                dotColor = ThemePalette.LevelColor(90, dark);
                fill = Color.FromArgb(dark ? 34 : 26, dotColor);
                fg = dark ? Color.FromArgb(148, 232, 184) : Color.FromArgb(20, 118, 68);
            } else if (tone == ControlCenterBadgeTone.Accent) {
                dotColor = ThemePalette.LevelColor(40, dark);
                fill = Color.FromArgb(dark ? 32 : 24, dotColor);
                fg = dark ? Color.FromArgb(245, 206, 128) : Color.FromArgb(148, 92, 12);
            } else {
                dotColor = palette.Muted;
                fill = palette.RaisedSurface;
                fg = palette.Muted;
            }

            if (plain) {
                // Pure typography: weight and colour carry the state, no pill and no dot.
                if (!VisualDesign.HighContrast && tone == ControlCenterBadgeTone.Muted) fg = ControlCenterTokens.SecondaryText(palette, dark);
                bool plainBold = tone != ControlCenterBadgeTone.Muted;
                using (var plainFont = new Font("Microsoft YaHei UI", fontPx, plainBold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
                using (var plainBrush = new SolidBrush(fg))
                using (var plainFormat = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap }) {
                    g.DrawString(Text, plainFont, plainBrush, new RectangleF(0f, 0f, Math.Max(16f, Width), Height), plainFormat);
                }
                return;
            }
            CompactRenderer.Fill(g, rect, fill, rect.Height / 2f);
            if (VisualDesign.HighContrast) {
                using (var path = VisualDesign.RoundRect(rect.X, rect.Y, rect.Width, rect.Height, rect.Height / 2f))
                using (var pen = new Pen(SystemColors.WindowText, 1f)) g.DrawPath(pen, path);
            }

            bool bold = tone != ControlCenterBadgeTone.Muted;
            float dotSize = 6f;
            float gap = 5f;
            float contentW = (showDot ? dotSize + gap : 0f) + measuredTextWidth;
            float startX = Math.Max(6f, (Width - contentW) / 2f);
            if (showDot) {
                using (var b = new SolidBrush(dotColor)) g.FillEllipse(b, startX, (Height - dotSize) / 2f, dotSize, dotSize);
                startX += dotSize + gap;
            }
            using (var drawFont = new Font("Microsoft YaHei UI", fontPx, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(fg))
            using (var sf = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap }) {
                g.DrawString(Text, drawFont, brush, new RectangleF(startX, 0f, Math.Max(16f, Width - startX - 4f), Height), sf);
            }
        }
    }

    // Hybrid Fluent owner-drawn rounded TextBox container wrapping a borderless native TextBox for full IME/accessibility.
    internal sealed class ControlCenterTextBox : Panel {
        readonly ThemePalette palette;
        readonly bool dark;
        readonly TextBox inner;
        bool hovered, focused;

        internal event EventHandler Committed;

        internal ControlCenterTextBox(string initialText, ThemePalette palette, bool dark, Font font, int width = 196) {
            this.palette = palette;
            this.dark = dark;
            Size = new Size(width, 32);
            BackColor = palette.Card;
            Cursor = Cursors.IBeam;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            inner = new TextBox {
                BorderStyle = BorderStyle.None,
                Text = initialText ?? "",
                Font = font,
                BackColor = FieldFill(false),
                ForeColor = VisualDesign.HighContrast ? SystemColors.WindowText : palette.Foreground,
                Location = new Point(10, 7),
                Width = Math.Max(40, width - 20)
            };
            inner.GotFocus += delegate { focused = true; SyncInnerBack(); Invalidate(); };
            inner.LostFocus += delegate { focused = false; SyncInnerBack(); Invalidate(); if (Committed != null) Committed(this, EventArgs.Empty); };
            inner.MouseEnter += delegate { hovered = true; Invalidate(); };
            inner.MouseLeave += delegate { hovered = false; Invalidate(); };
            inner.KeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.KeyCode == Keys.Enter) {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    if (Committed != null) Committed(this, EventArgs.Empty);
                }
            };
            Controls.Add(inner);
            Resize += delegate { PositionInner(); };
            Click += delegate { inner.Focus(); };
            MouseEnter += delegate { hovered = true; Invalidate(); };
            MouseLeave += delegate { hovered = false; Invalidate(); };
            PositionInner();
        }

        Color FieldFill(bool isFocused) {
            if (VisualDesign.HighContrast) return SystemColors.Window;
            if (dark) return isFocused ? ThemePalette.Blend(palette.RaisedSurface, Color.White, 8) : palette.RaisedSurface;
            return isFocused ? Color.White : palette.WindowBackground;
        }

        void SyncInnerBack() {
            Color c = FieldFill(focused);
            if (inner.BackColor != c) inner.BackColor = c;
        }

        internal string ValueText { get { return inner.Text; } }
        internal TextBox InnerBox { get { return inner; } }

        void PositionInner() {
            int ih = Math.Max(14, inner.PreferredHeight);
            int y = Math.Max(4, (Height - ih) / 2);
            inner.SetBounds(11, y, Math.Max(40, Width - 22), ih);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            Color hostBg = Parent != null ? Parent.BackColor : palette.Card;
            g.Clear(hostBg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var rect = new RectangleF(1f, 1f, Width - 2f, Height - 2f);
            Color fill = FieldFill(focused);
            CompactRenderer.Fill(g, rect, fill, ControlCenterTokens.RadiusControl);

            Color border = VisualDesign.HighContrast
                ? (focused ? SystemColors.Highlight : SystemColors.WindowText)
                : (focused ? palette.Selected : hovered ? palette.Muted : Color.FromArgb(dark ? 165 : 140, palette.Border));
            using (var path = VisualDesign.RoundRect(rect.X, rect.Y, rect.Width, rect.Height, ControlCenterTokens.RadiusControl))
            using (var pen = new Pen(border, focused ? 1.4f : 1f)) g.DrawPath(pen, path);

            // Fluent bottom accent bar when focused
            if (focused && !VisualDesign.HighContrast) {
                using (var accentPen = new Pen(palette.Selected, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
                    g.DrawLine(accentPen, rect.Left + 5f, rect.Bottom - 0.5f, rect.Right - 5f, rect.Bottom - 0.5f);
                }
            }
        }
    }

    internal sealed class ControlCenterToggle : Control {
        bool isChecked, hovered, pressed;
        ThemePalette palette;
        bool dark;

        internal event EventHandler Toggled;

        internal ControlCenterToggle(ThemePalette palette, bool dark) {
            this.palette = palette;
            this.dark = dark;
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.CheckButton;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Opaque | ControlStyles.SupportsTransparentBackColor, false);
            Size = new Size(40, 22);
            BackColor = VisualDesign.HighContrast ? SystemColors.Window : palette.Card;
        }

        internal bool Checked {
            get { return isChecked; }
            set {
                if (isChecked == value) return;
                isChecked = value;
                Invalidate();
            }
        }

        internal void ApplyTheme(ThemePalette next, bool isDark, Color surface) {
            palette = next;
            dark = isDark;
            BackColor = VisualDesign.HighContrast ? SystemColors.Window : surface;
            Invalidate();
        }

        void Flip() {
            if (!Enabled) return;
            isChecked = !isChecked;
            Invalidate();
            if (Toggled != null) Toggled(this, EventArgs.Empty);
        }

        protected override void OnClick(EventArgs e) { base.OnClick(e); Flip(); }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }
        protected override void OnKeyDown(KeyEventArgs e) {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { e.Handled = true; Flip(); }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            Color hostBg = Parent != null ? Parent.BackColor : BackColor;
            g.Clear(hostBg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new RectangleF(1, 1, Math.Max(12, Width - 2), Math.Max(12, Height - 2));
            float radius = rect.Height / 2f;
            Color track = !Enabled ? palette.Track : isChecked ? palette.Selected : palette.Track;
            if (Enabled && (hovered || pressed) && !VisualDesign.HighContrast) {
                var overlay = palette.Interaction(pressed);
                track = ThemePalette.Blend(track, overlay, overlay.A);
            }
            CompactRenderer.Fill(g, rect, track, radius);
            if (VisualDesign.HighContrast) {
                using (var path = VisualDesign.RoundRect(rect.X, rect.Y, rect.Width, rect.Height, radius))
                using (var pen = new Pen(palette.Foreground, 1)) g.DrawPath(pen, path);
            }
            float inset = 3f;
            float knobSize = Math.Max(6f, rect.Height - inset * 2f);
            float knobX = isChecked ? rect.Right - knobSize - inset : rect.X + inset;
            var knob = new RectangleF(knobX, rect.Y + inset, knobSize, knobSize);
            Color knobColor = !Enabled ? palette.Muted : isChecked ? palette.SelectedForeground : VisualDesign.HighContrast ? palette.Foreground : dark ? palette.Muted : Color.White;
            using (var brush = new SolidBrush(knobColor)) g.FillEllipse(brush, knob);
            if (Enabled && !isChecked && !dark && !VisualDesign.HighContrast) {
                using (var pen = new Pen(palette.Border, 1)) g.DrawEllipse(pen, knob);
            }
            if (Focused && ShowFocusCues) {
                var focusRect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
                CompactRenderer.Outline(g, focusRect, palette.Accent, 1);
            }
        }
    }

    // Semantic Fluent/Linear navigation sidebar (188px wide) with brand header, icon+label rows, and live status dots.
    internal sealed class ControlCenterRail : Control {
        sealed class RailItem {
            internal ControlCenterPage Page;
            internal string Label;
            internal string Icon;
            internal bool Bottom;
            internal Rectangle Bounds;
        }

        readonly List<RailItem> items = new List<RailItem>();
        ThemePalette palette;
        bool dark;
        bool placementActiveDot, settingsActiveDot;
        ControlCenterPage selected = ControlCenterPage.Devices;
        int hovered = -1;

        internal event EventHandler<ControlCenterPage> PageSelected;

        internal ControlCenterRail(ThemePalette palette, bool dark) {
            this.palette = palette;
            this.dark = dark;
            TabStop = true;
            Width = ControlCenterTokens.SidebarWidth;
            Dock = DockStyle.Left;
            AccessibleName = "控制中心导航";
            AccessibleRole = AccessibleRole.List;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Opaque | ControlStyles.SupportsTransparentBackColor, false);
            items.Add(new RailItem { Page = ControlCenterPage.Devices, Label = "设备", Icon = "devices", Bottom = false });
            items.Add(new RailItem { Page = ControlCenterPage.Appearance, Label = "外观", Icon = "look", Bottom = false });
            items.Add(new RailItem { Page = ControlCenterPage.Placement, Label = "显示位置", Icon = "place", Bottom = false });
            items.Add(new RailItem { Page = ControlCenterPage.Reminders, Label = "提醒", Icon = "remind", Bottom = false });
            items.Add(new RailItem { Page = ControlCenterPage.Diagnostics, Label = "诊断与反馈", Icon = "diag", Bottom = true });
            items.Add(new RailItem { Page = ControlCenterPage.Settings, Label = "设置", Icon = "settings", Bottom = true });
            BackColor = palette.RailBackground;
        }

        internal ControlCenterPage SelectedPage {
            get { return selected; }
            set {
                if (selected == value) return;
                selected = value;
                Invalidate();
            }
        }

        internal void UpdateIndicators(bool placementActive, bool apiRunning) {
            if (placementActiveDot == placementActive && settingsActiveDot == apiRunning) return;
            placementActiveDot = placementActive;
            settingsActiveDot = apiRunning;
            Invalidate();
        }

        internal void ApplyTheme(ThemePalette next, bool isDark) {
            palette = next;
            dark = isDark;
            BackColor = palette.RailBackground;
            Invalidate();
        }

        void LayoutItems() {
            int padX = 12;
            int itemW = Math.Max(40, Width - padX * 2);
            int itemH = 38;
            int gap = 4;
            int yTop = 74;
            for (int i = 0; i < items.Count; i++) {
                if (!items[i].Bottom) {
                    items[i].Bounds = new Rectangle(padX, yTop, itemW, itemH);
                    yTop += itemH + gap;
                }
            }
            int bottomCount = 0;
            for (int i = 0; i < items.Count; i++) if (items[i].Bottom) bottomCount++;
            int yBot = Math.Max(yTop + 22, Height - 14 - bottomCount * itemH - Math.Max(0, bottomCount - 1) * gap);
            for (int i = 0; i < items.Count; i++) {
                if (items[i].Bottom) {
                    items[i].Bounds = new Rectangle(padX, yBot, itemW, itemH);
                    yBot += itemH + gap;
                }
            }
        }

        int HitIndex(Point p) {
            LayoutItems();
            for (int i = 0; i < items.Count; i++) if (items[i].Bounds.Contains(p)) return i;
            return -1;
        }

        void SelectIndex(int index) {
            if (index < 0 || index >= items.Count) return;
            var next = items[index].Page;
            if (selected == next) return;
            selected = next;
            Invalidate();
            if (PageSelected != null) PageSelected(this, next);
        }

        protected override bool IsInputKey(Keys keyData) {
            if (keyData == Keys.Up || keyData == Keys.Down || keyData == Keys.Home || keyData == Keys.End) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e) {
            int current = items.FindIndex(delegate(RailItem item) { return item.Page == selected; });
            if (e.KeyCode == Keys.Down) { e.Handled = true; SelectIndex((current + 1) % items.Count); }
            else if (e.KeyCode == Keys.Up) { e.Handled = true; SelectIndex((current - 1 + items.Count) % items.Count); }
            else if (e.KeyCode == Keys.Home) { e.Handled = true; SelectIndex(0); }
            else if (e.KeyCode == Keys.End) { e.Handled = true; SelectIndex(items.Count - 1); }
            base.OnKeyDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e) {
            int next = HitIndex(e.Location);
            if (next != hovered) {
                hovered = next;
                Cursor = hovered >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) {
            hovered = -1;
            Cursor = Cursors.Default;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e) {
            if (e.Button == MouseButtons.Left) {
                Focus();
                int idx = HitIndex(e.Location);
                if (idx >= 0) SelectIndex(idx);
            }
            base.OnMouseDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e) {
            LayoutItems();
            var g = e.Graphics;
            g.Clear(palette.RailBackground);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            // Subtle inset hairline divider (avoids harsh full-bleed seam against fused Win11 titlebar)
            Color seamColor = VisualDesign.HighContrast ? palette.Foreground : Color.FromArgb(dark ? 22 : 16, palette.Foreground);
            using (var pen = new Pen(seamColor, 1f)) g.DrawLine(pen, Width - 1, 14, Width - 1, Math.Max(14, Height - 14));

            // Brand header (top-left logo + title + version chip)
            ControlCenterIconLoader.DrawBrandGlyph(g, new RectangleF(16, 18, 28, 28), dark);
            CompactRenderer.DrawLabel(g, "MyFinder", new RectangleF(52, 16, Width - 64, 18), 14f, true, palette.Foreground, StringAlignment.Near);
            string ver = AppRuntime.Version ?? "1.4";
            if (ver.Length > 14) ver = ver.Substring(0, 14);
            CompactRenderer.DrawLabel(g, "v" + ver, new RectangleF(52, 34, Width - 64, 14), 10.5f, false, palette.Muted, StringAlignment.Near);
            using (var div = new Pen(Color.FromArgb(dark ? 28 : 22, palette.Foreground), 1)) {
                g.DrawLine(div, 16, 60, Width - 16, 60);
            }

            // Divider before bottom group
            int firstBottomY = -1;
            for (int i = 0; i < items.Count; i++) {
                if (items[i].Bottom) { firstBottomY = items[i].Bounds.Top - 10; break; }
            }
            if (firstBottomY > 0) {
                using (var div = new Pen(Color.FromArgb(dark ? 28 : 22, palette.Foreground), 1)) {
                    g.DrawLine(div, 16, firstBottomY, Width - 16, firstBottomY);
                }
            }

            for (int i = 0; i < items.Count; i++) {
                var item = items[i];
                bool isSel = item.Page == selected;
                bool isHov = i == hovered;
                var rect = new RectangleF(item.Bounds.X, item.Bounds.Y, item.Bounds.Width, item.Bounds.Height);
                if (isSel) {
                    Color fill = VisualDesign.HighContrast ? palette.Selected : palette.RaisedSurface;
                    CompactRenderer.Fill(g, rect, fill, 7f);
                    if (!VisualDesign.HighContrast) {
                        using (var p = VisualDesign.RoundRect(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1f, rect.Height - 1f, 7f))
                        using (var pen = new Pen(Color.FromArgb(dark ? 32 : 42, palette.Border), 1f)) g.DrawPath(pen, p);
                    }
                    var bar = new RectangleF(rect.X, rect.Y + (rect.Height - 16) / 2f, 3f, 16f);
                    CompactRenderer.Fill(g, bar, VisualDesign.HighContrast ? palette.SelectedForeground : palette.Foreground, 1.5f);
                } else if (isHov) {
                    Color hovFill = VisualDesign.HighContrast ? SystemColors.Highlight : Color.FromArgb(dark ? 14 : 12, palette.Foreground);
                    CompactRenderer.Fill(g, rect, hovFill, 7f);
                }
                if (isSel && Focused && ShowFocusCues) {
                    CompactRenderer.Outline(g, rect, palette.Accent, 1f);
                }

                Color fg = isSel
                    ? (VisualDesign.HighContrast ? palette.SelectedForeground : palette.Foreground)
                    : (isHov ? (VisualDesign.HighContrast ? SystemColors.HighlightText : palette.Foreground) : palette.Muted);
                DrawRailIcon(g, item.Icon, new RectangleF(rect.X + 12f, rect.Y + (rect.Height - 16f) / 2f, 16f, 16f), fg);
                CompactRenderer.DrawLabel(g, item.Label, new RectangleF(rect.X + 36f, rect.Y, rect.Width - 52f, rect.Height), 13f, isSel, fg, StringAlignment.Near);

                bool showDot = (item.Page == ControlCenterPage.Placement && placementActiveDot) || (item.Page == ControlCenterPage.Settings && settingsActiveDot);
                if (showDot) {
                    using (var db = new SolidBrush(ThemePalette.LevelColor(90, dark))) {
                        g.FillEllipse(db, rect.Right - 14f, rect.Y + (rect.Height - 6f) / 2f, 6f, 6f);
                    }
                }
            }
        }

        static void DrawRailIcon(Graphics g, string kind, RectangleF r, Color color) {
            if (kind == "settings") {
                CompactRenderer.DrawSymbol(g, "settings", r, color);
                return;
            }
            using (var pen = new Pen(color, 1.5f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var brush = new SolidBrush(color)) {
                float x = r.X, y = r.Y, w = r.Width, h = r.Height;
                if (kind == "devices") {
                    using (var p = VisualDesign.RoundRect(x + 1, y + 2, w - 2, h - 6.5f, 2)) g.DrawPath(pen, p);
                    g.DrawLine(pen, x + w * 0.5f, y + h - 4.5f, x + w * 0.5f, y + h - 1.5f);
                    g.DrawLine(pen, x + 4.5f, y + h - 1.5f, x + w - 4.5f, y + h - 1.5f);
                } else if (kind == "look") {
                    g.DrawEllipse(pen, x + 1.5f, y + 1.5f, w - 3, h - 3);
                    g.FillEllipse(brush, x + 4.5f, y + 6.5f, 2.3f, 2.3f);
                    g.FillEllipse(brush, x + 7.5f, y + 4.2f, 2.3f, 2.3f);
                    g.FillEllipse(brush, x + 10.2f, y + 7.2f, 2.3f, 2.3f);
                } else if (kind == "place") {
                    using (var p = VisualDesign.RoundRect(x + 1, y + 2, w - 2, h - 4, 2)) g.DrawPath(pen, p);
                    g.DrawLine(pen, x + 1, y + h - 5.5f, x + w - 1, y + h - 5.5f);
                    g.DrawLine(pen, x + w - 6.5f, y + h - 3.5f, x + w - 3, y + h - 3.5f);
                } else if (kind == "remind") {
                    g.DrawArc(pen, x + 3.5f, y + 2f, w - 7, 8, 180, 180);
                    g.DrawLine(pen, x + 3.5f, y + 6f, x + 3.5f, y + 11.5f);
                    g.DrawLine(pen, x + w - 3.5f, y + 6f, x + w - 3.5f, y + 11.5f);
                    g.DrawLine(pen, x + 2f, y + 12f, x + w - 2f, y + 12f);
                    g.DrawArc(pen, x + w * 0.5f - 2, y + 12f, 4, 2.8f, 0, 180);
                } else {
                    g.DrawLines(pen, new[] {
                        new PointF(x + 1, y + h * 0.55f),
                        new PointF(x + 4f, y + h * 0.55f),
                        new PointF(x + 6.5f, y + 3.5f),
                        new PointF(x + 9.5f, y + h - 3.5f),
                        new PointF(x + 12f, y + h * 0.55f),
                        new PointF(x + w - 1, y + h * 0.55f)
                    });
                }
            }
        }
    }

    internal sealed class ControlCenterHeroRing : Control {
        ThemePalette palette;
        bool dark;
        int? percent;
        bool fresh;

        internal ControlCenterHeroRing(ThemePalette palette, bool dark) {
            this.palette = palette;
            this.dark = dark;
            Size = new Size(92, 92);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Opaque | ControlStyles.SupportsTransparentBackColor, false);
            BackColor = palette.Card;
        }

        internal void UpdateValue(ThemePalette next, bool isDark, int? pct, bool isFresh) {
            palette = next;
            dark = isDark;
            percent = pct;
            fresh = isFresh;
            BackColor = palette.Card;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            Color hostBg = Parent != null ? Parent.BackColor : BackColor;
            DialogPaint.Background(this, e, hostBg);
            float pad = 7f;
            var ring = new RectangleF(pad, pad, Math.Max(20, Width - pad * 2), Math.Max(20, Height - pad * 2));
            using (var trackPen = new Pen(VisualDesign.HighContrast ? palette.Foreground : palette.Track, VisualDesign.HighContrast ? 2f : 6.5f)) {
                g.DrawEllipse(trackPen, ring);
            }
            Color accent = percent.HasValue && fresh ? ThemePalette.LevelColor(percent, dark) : palette.Muted;
            if (percent.HasValue) {
                float sweep = 360f * Math.Max(0, Math.Min(100, percent.Value)) / 100f;
                if (sweep > 0.5f) {
                    using (var arcPen = new Pen(accent, 6.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
                        g.DrawArc(arcPen, ring, -90f, sweep);
                    }
                }
            }
            string text = percent.HasValue ? percent.Value.ToString() : "—";
            float digitSize = percent.HasValue ? (VisualDesign.LargeFont ? 25f : 23f) : 18f;
            using (var font = new Font("Segoe UI", digitSize, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var pctFont = new Font("Segoe UI", 11.5f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(percent.HasValue ? (fresh ? accent : palette.Muted) : palette.Muted))
            using (var mutedBrush = new SolidBrush(palette.Muted)) {
                var numSize = g.MeasureString(text, font);
                if (percent.HasValue) {
                    var unitSize = g.MeasureString("%", pctFont);
                    float totalW = numSize.Width + unitSize.Width - 6f;
                    float x = (Width - totalW) / 2f;
                    float y = (Height - numSize.Height) / 2f;
                    g.DrawString(text, font, brush, x, y);
                    g.DrawString("%", pctFont, mutedBrush, x + numSize.Width - 6f, y + numSize.Height - unitSize.Height - 4f);
                } else {
                    g.DrawString(text, font, brush, (Width - numSize.Width) / 2f, (Height - numSize.Height) / 2f);
                }
            }
        }
    }

    internal sealed class ControlCenterSwatchCard : Control {
        readonly string styleKey;
        readonly string styleName;
        readonly bool isNew;
        ThemePalette hostPalette;
        bool dark;
        bool selected, hovered;

        internal event EventHandler Chosen;

        internal ControlCenterSwatchCard(string styleKey, string styleName, bool isNew, ThemePalette hostPalette, bool dark) {
            this.styleKey = styleKey;
            this.styleName = styleName;
            this.isNew = isNew;
            this.hostPalette = hostPalette;
            this.dark = dark;
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleName = styleName + "配色";
            AccessibleRole = AccessibleRole.RadioButton;
            Size = new Size(92, 76);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Opaque | ControlStyles.SupportsTransparentBackColor, false);
            BackColor = hostPalette.Card;
        }

        internal string StyleKey { get { return styleKey; } }

        internal void UpdateState(ThemePalette nextHost, bool isDark, bool isSelected) {
            hostPalette = nextHost;
            dark = isDark;
            selected = isSelected;
            BackColor = hostPalette.Card;
            Invalidate();
        }

        void Activate() { if (Chosen != null) Chosen(this, EventArgs.Empty); }

        protected override void OnClick(EventArgs e) { base.OnClick(e); Activate(); }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnKeyDown(KeyEventArgs e) {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { e.Handled = true; Activate(); }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var card = new RectangleF(1f, 1f, Width - 2f, Height - 2f);
            Color fill = hovered && !VisualDesign.HighContrast ? hostPalette.RaisedSurface : hostPalette.Card;
            CompactRenderer.Fill(g, card, fill, 8f);
            // Only the chosen swatch keeps a crisp outline; the others fade to a hairline so five cards do not read as five boxes.
            Color border = selected || VisualDesign.HighContrast ? hostPalette.Foreground : Color.FromArgb(dark ? 70 : 90, hostPalette.Border);
            using (var path = VisualDesign.RoundRect(card.X, card.Y, card.Width, card.Height, 8f))
            using (var pen = new Pen(border, selected ? 1.5f : 1f)) g.DrawPath(pen, path);

            var preview = ThemePalette.For(styleKey, dark);
            var chips = new RectangleF(8, 8, Width - 16, 34);
            if (!VisualDesign.HighContrast) {
                float third = chips.Width / 3f;
                using (var clip = VisualDesign.RoundRect(chips.X, chips.Y, chips.Width, chips.Height, 5f)) {
                    var state = g.Save();
                    g.SetClip(clip);
                    using (var b1 = new SolidBrush(preview.WindowBackground)) g.FillRectangle(b1, chips.X, chips.Y, third + 1, chips.Height);
                    using (var b2 = new SolidBrush(preview.Track)) g.FillRectangle(b2, chips.X + third, chips.Y, third + 1, chips.Height);
                    Color c3 = styleKey == "graphite" ? ThemePalette.LevelColorFor("graphite", 90, dark) : preview.Accent;
                    using (var b3 = new SolidBrush(c3)) g.FillRectangle(b3, chips.X + third * 2, chips.Y, third + 2, chips.Height);
                    g.Restore(state);
                }
                using (var borderPath = VisualDesign.RoundRect(chips.X, chips.Y, chips.Width, chips.Height, 5f))
                using (var pen = new Pen(Color.FromArgb(40, hostPalette.Border), 1f)) g.DrawPath(pen, borderPath);
            } else {
                using (var pen = new Pen(hostPalette.Foreground, 1))
                using (var path = VisualDesign.RoundRect(chips.X, chips.Y, chips.Width, chips.Height, 5f)) g.DrawPath(pen, path);
            }
            string caption = styleName;
            CompactRenderer.DrawLabel(g, caption, new RectangleF(4, 46, Width - 8, 20), 12f, selected, selected ? hostPalette.Foreground : ControlCenterTokens.SecondaryText(hostPalette, dark), StringAlignment.Center);
            if (Focused && ShowFocusCues) CompactRenderer.Outline(g, card, hostPalette.Accent, 1);
        }
    }

    internal sealed class ControlCenterLivePreview : Control {
        readonly TrayContext owner;
        ThemePalette palette;
        bool dark;

        internal ControlCenterLivePreview(TrayContext owner, ThemePalette palette, bool dark) {
            this.owner = owner;
            this.palette = palette;
            this.dark = dark;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Opaque | ControlStyles.SupportsTransparentBackColor, false);
            Size = new Size(300, 340);
            BackColor = palette.WindowBackground;
        }

        internal void ApplyTheme(ThemePalette next, bool isDark) {
            palette = next;
            dark = isDark;
            BackColor = palette.WindowBackground;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            g.Clear(palette.WindowBackground);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var layout = WirelessLayout.Build(owner, 0, Math.Max(220, Height - 24));
            var size = layout.Size;
            float availW = Math.Max(100, Width - 18);
            float availH = Math.Max(100, Height - 18);
            float scale = Math.Min(1f, Math.Min(availW / Math.Max(1, size.Width), availH / Math.Max(1, size.Height)));
            float drawW = size.Width * scale;
            float drawH = size.Height * scale;
            float ox = Math.Max(8, (Width - drawW) / 2f);
            float oy = 8f;
            var frame = new RectangleF(ox - 5, oy - 5, drawW + 10, drawH + 10);
            // Frameless stage: a faint surface tint carries the real panel card, so the preview shows one outline instead of nested strokes.
            if (VisualDesign.HighContrast) {
                CompactRenderer.Fill(g, frame, palette.WindowBackground, ControlCenterTokens.RadiusCard);
                using (var border = VisualDesign.RoundRect(frame.X, frame.Y, frame.Width, frame.Height, ControlCenterTokens.RadiusCard))
                using (var pen = new Pen(palette.Border, 1)) g.DrawPath(pen, border);
            } else {
                CompactRenderer.Fill(g, frame, ThemePalette.Blend(palette.WindowBackground, palette.Foreground, dark ? 16 : 13), ControlCenterTokens.RadiusCard);
            }
            var state = g.Save();
            g.TranslateTransform(ox, oy);
            g.ScaleTransform(scale, scale);
            try {
                CompactRenderer.PaintWireless(g, owner, layout, dark, new PanelFeedback(), 1f);
            } catch (Exception) {
                CompactRenderer.DrawLabel(g, "实时预览暂不可用", new RectangleF(0, 0, size.Width, 40), 12f, false, ControlCenterTokens.SecondaryText(palette, dark), StringAlignment.Center);
            }
            g.Restore(state);
        }
    }

    internal sealed class ControlCenterPlacementPreview : Control {
        ThemePalette palette;
        bool dark;
        int selectedMode;

        internal ControlCenterPlacementPreview(ThemePalette palette, bool dark) {
            this.palette = palette;
            this.dark = dark;
            Size = new Size(600, 148);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Opaque | ControlStyles.SupportsTransparentBackColor, false);
            BackColor = palette.Card;
        }

        internal void UpdatePreview(ThemePalette next, bool isDark, int mode) {
            palette = next;
            dark = isDark;
            selectedMode = mode;
            BackColor = palette.Card;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            g.Clear(palette.Card);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var box = new RectangleF(1f, 1f, Width - 2f, Height - 2f);
            using (var clip = VisualDesign.RoundRect(box.X, box.Y, box.Width, box.Height, ControlCenterTokens.RadiusCard)) {
                var state = g.Save();
                g.SetClip(clip);
                Color top = dark ? Color.FromArgb(42, 54, 70) : Color.FromArgb(204, 218, 232);
                Color bottom = dark ? Color.FromArgb(24, 30, 39) : Color.FromArgb(176, 194, 214);
                using (var bg = new LinearGradientBrush(box, top, bottom, 145f)) g.FillRectangle(bg, box);

                // Subtle abstract wallpaper glow for depth
                using (var glow = new SolidBrush(Color.FromArgb(dark ? 18 : 36, 110, 170, 235))) {
                    g.FillEllipse(glow, Width * 0.12f, -30f, 220f, 150f);
                }

                // Desktop widget illustration (floating capsule + high-contrast edge handle preview)
                if (selectedMode == 1) {
                    var pill = new RectangleF(Width - 208, 18, 184, 36);
                    CompactRenderer.Fill(g, new RectangleF(pill.X, pill.Y + 2, pill.Width, pill.Height), Color.FromArgb(90, 0, 0, 0), 18f);
                    CompactRenderer.Fill(g, pill, palette.Card, 18f);
                    using (var pp = VisualDesign.RoundRect(pill.X, pill.Y, pill.Width, pill.Height, 18f))
                    using (var pen = new Pen(palette.Border, 1f)) g.DrawPath(pen, pp);
                    CompactRenderer.DrawSymbol(g, "mouse", new RectangleF(pill.X + 12, pill.Y + 11, 14, 14), ThemePalette.LevelColor(100, dark));
                    CompactRenderer.DrawLabel(g, "100%", new RectangleF(pill.X + 28, pill.Y, 44, pill.Height), 12f, true, palette.Foreground, StringAlignment.Near);
                    using (var sep = new Pen(palette.Border, 1f)) g.DrawLine(sep, pill.X + 86, pill.Y + 9, pill.X + 86, pill.Bottom - 9);
                    CompactRenderer.DrawSymbol(g, "headset", new RectangleF(pill.X + 98, pill.Y + 11, 14, 14), ThemePalette.LevelColor(39, dark));
                    CompactRenderer.DrawLabel(g, "39%", new RectangleF(pill.X + 115, pill.Y, 44, pill.Height), 12f, true, palette.Foreground, StringAlignment.Near);

                    // Wallpaper-proof edge handle preview on right screen edge
                    var edgeTab = new RectangleF(Width - 10, 62, 10, 38);
                    CompactRenderer.Fill(g, edgeTab, Color.FromArgb(238, 24, 27, 34), 5f);
                    using (var rim = new Pen(Color.FromArgb(90, 255, 255, 255), 1f)) g.DrawLine(rim, edgeTab.X + 1, edgeTab.Y + 4, edgeTab.X + 1, edgeTab.Bottom - 4);
                    CompactRenderer.Fill(g, new RectangleF(edgeTab.X + 4, edgeTab.Y + 8, 3, 22), ThemePalette.LevelColor(90, true), 1.5f);
                }

                // Taskbar strip
                var tb = new RectangleF(0, Height - 40, Width, 40);
                using (var tbBrush = new SolidBrush(dark ? Color.FromArgb(238, 26, 28, 32) : Color.FromArgb(244, 246, 249))) {
                    g.FillRectangle(tbBrush, tb);
                }
                using (var topLine = new Pen(Color.FromArgb(dark ? 28 : 22, dark ? Color.White : Color.Black), 1f)) {
                    g.DrawLine(topLine, 0, Height - 40, Width, Height - 40);
                }
                // Center Win11 placeholder app icons
                float cx = Width / 2f - 42f;
                for (int i = 0; i < 4; i++) {
                    CompactRenderer.Fill(g, new RectangleF(cx + i * 22f, Height - 28f, 16f, 16f), Color.FromArgb(dark ? 45 : 32, dark ? Color.White : Color.Black), 4f);
                }
                Color tbFg = dark ? Color.FromArgb(238, 240, 244) : Color.FromArgb(28, 32, 38);
                CompactRenderer.DrawLabel(g, "11:58\n2026/11/25", new RectangleF(Width - 82, Height - 38, 72, 36), 10.5f, false, tbFg, StringAlignment.Far);

                if (selectedMode == 2) {
                    var bat = new RectangleF(Width - 252, Height - 33, 152, 26);
                    CompactRenderer.Fill(g, bat, dark ? Color.FromArgb(220, 36, 39, 46) : Color.FromArgb(235, 255, 255, 255), 6.5f);
                    using (var bp = VisualDesign.RoundRect(bat.X, bat.Y, bat.Width, bat.Height, 6.5f))
                    using (var pen = new Pen(Color.FromArgb(dark ? 70 : 45, palette.Border), 1f)) g.DrawPath(pen, bp);
                    CompactRenderer.DrawSymbol(g, "mouse", new RectangleF(bat.X + 8, bat.Y + 6, 13, 13), ThemePalette.LevelColor(100, dark));
                    CompactRenderer.DrawLabel(g, "100%", new RectangleF(bat.X + 24, bat.Y, 42, bat.Height), 11f, true, ThemePalette.LevelColor(100, dark), StringAlignment.Near);
                    CompactRenderer.DrawSymbol(g, "headset", new RectangleF(bat.X + 76, bat.Y + 6, 13, 13), ThemePalette.LevelColor(39, dark));
                    CompactRenderer.DrawLabel(g, "39%", new RectangleF(bat.X + 92, bat.Y, 42, bat.Height), 11f, true, ThemePalette.LevelColor(39, dark), StringAlignment.Near);
                } else if (selectedMode == 0) {
                    var badge = new RectangleF(Width - 118, Height - 30, 24, 20);
                    CompactRenderer.Fill(g, badge, ThemePalette.LevelColor(85, dark), 4.5f);
                    CompactRenderer.DrawLabel(g, "85", badge, 11f, true, Color.White, StringAlignment.Center);
                }
                g.Restore(state);
            }
            using (var pen = new Pen(palette.Border, 1))
            using (var path = VisualDesign.RoundRect(box.X, box.Y, box.Width, box.Height, ControlCenterTokens.RadiusCard)) g.DrawPath(pen, path);
        }
    }

    internal sealed class ControlCenterSegments : Control {
        readonly string[] labels;
        int selectedIndex;
        readonly Action<int> onSelect;
        readonly ThemePalette palette;
        readonly Font font, boldFont;
        readonly RectangleF[] rects;
        readonly float[] naturalWidths;
        readonly int naturalTotalWidth;
        int hovered = -1;

        internal ControlCenterSegments(string[] labels, int selectedIndex, Action<int> onSelect, ThemePalette palette, Font font, Font boldFont) {
            this.labels = labels;
            this.selectedIndex = selectedIndex;
            this.onSelect = onSelect;
            this.palette = palette;
            this.font = font;
            this.boldFont = boldFont;
            this.rects = new RectangleF[labels.Length];
            this.naturalWidths = new float[labels.Length];
            TabStop = true;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Opaque | ControlStyles.SupportsTransparentBackColor, false);
            BackColor = palette.Card;

            int h = VisualDesign.LargeFont ? 34 : ControlCenterTokens.ButtonHeight;
            float sum = 3f;
            for (int i = 0; i < labels.Length; i++) {
                var sz = TextRenderer.MeasureText(labels[i], boldFont, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                naturalWidths[i] = Math.Max(40f, sz.Width + 18f);
                sum += naturalWidths[i] + 2f;
            }
            naturalTotalWidth = (int)Math.Ceiling(sum + 2f);
            Size = new Size(naturalTotalWidth, h);
            LayoutSegments();
        }

        internal int SelectedIndex { get { return selectedIndex; } }

        internal void SetSelectedIndex(int idx) {
            if (selectedIndex == idx) return;
            selectedIndex = idx;
            Invalidate();
        }

        internal int NaturalWidth { get { return naturalTotalWidth; } }

        internal void FitMaxWidth(int maxWidth) {
            int target = Math.Max(120, Math.Min(naturalTotalWidth, maxWidth));
            if (Width != target) {
                Width = target;
                LayoutSegments();
                Invalidate();
            }
        }

        void LayoutSegments() {
            float avail = Math.Max(40f, Width - 6f - Math.Max(0, labels.Length - 1) * 2f);
            float totalNat = 0f;
            for (int i = 0; i < naturalWidths.Length; i++) totalNat += naturalWidths[i];
            float scale = totalNat > 0f && avail < totalNat ? (avail / totalNat) : 1f;
            float x = 3f;
            float segH = Math.Max(18f, Height - 6f);
            for (int i = 0; i < labels.Length; i++) {
                float w = (float)Math.Floor(naturalWidths[i] * scale);
                if (i == labels.Length - 1 && scale < 1f) w = Math.Max(22f, Width - 3f - x);
                rects[i] = new RectangleF(x, 3f, Math.Max(22f, w), segH);
                x += rects[i].Width + 2f;
            }
        }

        protected override void OnResize(EventArgs e) {
            base.OnResize(e);
            LayoutSegments();
        }

        int Hit(Point p) {
            for (int i = 0; i < rects.Length; i++) if (rects[i].Contains(p.X, p.Y)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e) {
            int next = Hit(e.Location);
            if (next != hovered) { hovered = next; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hovered = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e) {
            if (e.Button == MouseButtons.Left) {
                int idx = Hit(e.Location);
                if (idx >= 0) {
                    SetSelectedIndex(idx);
                    if (onSelect != null) onSelect(idx);
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            Color hostBg = Parent != null ? Parent.BackColor : BackColor;
            g.Clear(hostBg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var outer = new RectangleF(1f, 1f, Width - 2f, Height - 2f);
            CompactRenderer.Fill(g, outer, palette.RaisedSurface, 7f);
            using (var path = VisualDesign.RoundRect(outer.X, outer.Y, outer.Width, outer.Height, 7f))
            using (var pen = new Pen(palette.Border, 1)) g.DrawPath(pen, path);

            for (int i = 0; i < labels.Length; i++) {
                bool active = i == selectedIndex;
                bool hov = i == hovered && !active;
                var r = rects[i];
                if (active) {
                    CompactRenderer.Fill(g, r, palette.Selected, 5f);
                } else if (hov) {
                    var overlay = palette.Interaction(false);
                    CompactRenderer.Fill(g, r, ThemePalette.Blend(palette.RaisedSurface, overlay, overlay.A), 5f);
                }
                Color fg = active ? palette.SelectedForeground : hov ? palette.Foreground : palette.Muted;
                CompactRenderer.DrawLabel(g, labels[i], r, VisualDesign.LargeFont ? 13f : 12f, active, fg, StringAlignment.Center);
            }
        }
    }

    internal sealed class ControlCenterForm : Form {
        readonly TrayContext owner;
        readonly Action<string> copyText;
        readonly Action<string> openLink;
        readonly ControlCenterRail rail;
        readonly Panel pageHost;
        readonly Panel noticeBar;
        readonly Label noticeLabel;
        readonly Timer noticeTimer = new Timer { Interval = 3600 };
        readonly bool ownsWindowIcon;
        readonly Icon windowIcon;

        ThemePalette palette;
        DialogTheme dialogTheme;
        bool dark;
        ControlCenterPage currentPage;
        string selectedDeviceKey;
        int selectedPlacementCard;
        bool showNoBatteryDevices;
        bool loadingBounds, closing, rebuilding, deferredDataRefresh;
        string lastDataSignature;

        Font titleFont, headingFont, bodyFont, subFont, boldFont, subBoldFont;
        Color secondary;

        internal ControlCenterForm(TrayContext owner, ControlCenterPage initialPage, Action<string> copyText, Action<string> openLink) {
            this.owner = owner;
            this.copyText = copyText;
            this.openLink = openLink;
            currentPage = initialPage;

            Text = "MyFinder 控制中心";
            Name = "MyFinderControlCenterForm";
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = true;
            ShowInTaskbar = true;
            ShowIcon = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);

            windowIcon = ControlCenterIconLoader.LoadWindowIcon(out ownsWindowIcon);
            if (windowIcon != null) Icon = windowIcon;

            LoadTheme();
            RebuildFonts();
            MinimumSize = new Size(860, 580);
            ApplyInitialBounds();

            rail = new ControlCenterRail(palette, dark);
            rail.SelectedPage = currentPage;
            rail.PageSelected += delegate(object sender, ControlCenterPage page) { SelectPage(page); };

            noticeBar = new Panel { Dock = DockStyle.Bottom, Height = 32, Visible = false, Padding = new Padding(28, 5, 28, 5) };
            noticeLabel = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            noticeBar.Controls.Add(noticeLabel);
            noticeTimer.Tick += delegate { noticeTimer.Stop(); if (!IsDisposed) noticeBar.Visible = false; };

            pageHost = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(30, 22, 30, 24) };
            DialogScrollbars.Track(pageHost, dialogTheme);

            Controls.Add(pageHost);
            Controls.Add(noticeBar);
            Controls.Add(rail);

            ApplyChromeColors();
            RebuildCurrentPage();
        }

        internal ControlCenterPage CurrentPage { get { return currentPage; } }
        internal string SelectedDeviceKey { get { return selectedDeviceKey; } }

        void LoadTheme() {
            dark = Style.Dark;
            palette = ThemePalette.Current(dark);
            dialogTheme = new DialogTheme(dark);
            secondary = ControlCenterTokens.SecondaryText(palette, dark);
        }

        void RebuildFonts() {
            if (titleFont != null) titleFont.Dispose();
            if (headingFont != null) headingFont.Dispose();
            if (bodyFont != null) bodyFont.Dispose();
            if (subFont != null) subFont.Dispose();
            if (boldFont != null) boldFont.Dispose();
            if (subBoldFont != null) subBoldFont.Dispose();
            float scale = VisualDesign.LargeFont ? 1.12f : 1f;
            titleFont = new Font("Microsoft YaHei UI", 20f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            headingFont = new Font("Microsoft YaHei UI", 16f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            bodyFont = new Font("Microsoft YaHei UI", 13.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            // Three body tiers: heading 16 bold / body + label 13.5 (regular or bold) / caption 12 (regular or bold).
            boldFont = new Font("Microsoft YaHei UI", 13.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            subFont = new Font("Microsoft YaHei UI", 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            subBoldFont = new Font("Microsoft YaHei UI", 12f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        }

        void ApplyChromeColors() {
            BackColor = palette.WindowBackground;
            ForeColor = palette.Foreground;
            pageHost.BackColor = palette.WindowBackground;
            pageHost.ForeColor = palette.Foreground;
            noticeBar.BackColor = palette.RaisedSurface;
            noticeLabel.BackColor = palette.RaisedSurface;
            noticeLabel.ForeColor = palette.Foreground;
            noticeLabel.Font = subFont;
            rail.ApplyTheme(palette, dark);
            if (IsHandleCreated) ThemedTitleBar.Apply(Handle, dialogTheme, palette.WindowBackground);
        }

        internal static Rectangle ClampToWorkArea(Rectangle requested, Rectangle workArea, Size minSize) {
            int w = Math.Max(Math.Min(minSize.Width, workArea.Width), Math.Min(requested.Width, workArea.Width));
            int h = Math.Max(Math.Min(minSize.Height, workArea.Height), Math.Min(requested.Height, workArea.Height));
            int x = Math.Max(workArea.Left, Math.Min(requested.X, workArea.Right - w));
            int y = Math.Max(workArea.Top, Math.Min(requested.Y, workArea.Bottom - h));
            return new Rectangle(x, y, w, h);
        }

        void ApplyInitialBounds() {
            loadingBounds = true;
            try {
                var saved = DeviceSettings.Current.ControlCenterBounds;
                Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
                Rectangle target;
                if (saved != null && saved.HasPosition && saved.Width >= 600 && saved.Height >= 420) {
                    var savedRect = new Rectangle(saved.X, saved.Y, saved.Width, saved.Height);
                    area = Screen.FromRectangle(savedRect).WorkingArea;
                    target = ClampToWorkArea(savedRect, area, MinimumSize);
                } else {
                    int w = Math.Min(1040, area.Width - 40);
                    int h = Math.Min(680, area.Height - 40);
                    target = ClampToWorkArea(new Rectangle(area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h), area, MinimumSize);
                }
                Bounds = target;
            } finally {
                loadingBounds = false;
            }
        }

        void PersistBounds() {
            if (loadingBounds || WindowState != FormWindowState.Normal || Width < 600 || Height < 420) return;
            bool defaults;
            var pref = DeviceSettings.ReadSnapshot(out defaults);
            if (defaults) return;
            var next = new WindowBoundsState { X = Left, Y = Top, Width = Width, Height = Height, HasPosition = true };
            if (pref.ControlCenterBounds != null && pref.ControlCenterBounds.HasPosition &&
                pref.ControlCenterBounds.X == next.X && pref.ControlCenterBounds.Y == next.Y &&
                pref.ControlCenterBounds.Width == next.Width && pref.ControlCenterBounds.Height == next.Height) return;
            pref.ControlCenterBounds = next;
            owner.SavePreferencesIfLoaded(pref);
        }

        protected override void OnHandleCreated(EventArgs e) {
            base.OnHandleCreated(e);
            ThemedTitleBar.Apply(Handle, dialogTheme, palette.WindowBackground);
        }

        protected override void WndProc(ref Message m) {
            base.WndProc(ref m);
            if (m.Msg == 0x0232) PersistBounds();
        }

        protected override void OnFormClosing(FormClosingEventArgs e) {
            PersistBounds();
            closing = true;
            noticeTimer.Stop();
            base.OnFormClosing(e);
        }

        protected override void OnKeyDown(KeyEventArgs e) {
            if (e.KeyCode == Keys.Escape) {
                e.Handled = true;
                Close();
                return;
            }
            base.OnKeyDown(e);
        }

        internal void SelectPage(ControlCenterPage page) {
            currentPage = page;
            rail.SelectedPage = page;
            RebuildCurrentPage(true);
        }

        internal void RefreshData() {
            if (IsDisposed || Disposing || closing) return;
            if (InvokeRequired) {
                if (!IsHandleCreated) return;
                try { BeginInvoke((MethodInvoker)RefreshData); }
                catch (InvalidOperationException) {}
                return;
            }
            if (rebuilding) return;
            if (ActiveControl is TextBox) {
                deferredDataRefresh = true;
                return;
            }
            string sig = ComputeDataSignature();
            if (sig == lastDataSignature) return;
            RebuildCurrentPage();
        }

        internal void RefreshTheme() {
            if (IsDisposed || Disposing || closing) return;
            if (InvokeRequired) {
                if (!IsHandleCreated) return;
                try { BeginInvoke((MethodInvoker)RefreshTheme); }
                catch (InvalidOperationException) {}
                return;
            }
            if (rebuilding) return;
            LoadTheme();
            RebuildFonts();
            ApplyChromeColors();
            lastDataSignature = null;
            RebuildCurrentPage();
        }

        string ComputeDataSignature() {
            var pref = DeviceSettings.Current;
            var sb = new System.Text.StringBuilder();
            sb.Append(currentPage).Append('|').Append(selectedDeviceKey ?? "").Append('|').Append(selectedPlacementCard).Append('|');
            sb.Append(pref.Theme).Append('|').Append(pref.ThemeStyle).Append('|').Append(pref.PanelLayout).Append('|').Append(pref.FontSize).Append('|');
            sb.Append(pref.ReducedMotion).Append('|').Append(pref.HideTrayIcon).Append('|').Append(pref.TrayIconStyle).Append('|').Append(pref.TraySource).Append('|').Append(pref.TrayDeviceId ?? "").Append('|');
            sb.Append(pref.LowBatteryNotifications).Append('|').Append(pref.WarningThreshold).Append('|').Append(pref.BackgroundSeconds).Append('|');
            sb.Append(pref.RememberReadings).Append('|').Append(pref.AutoDetectMoreDevices).Append('|').Append(pref.AutoCheckUpdates).Append('|');
            sb.Append(pref.ShowUpdateBadge).Append('|').Append(pref.IncludeBetaUpdates).Append('|').Append(pref.CheckFeedbackReplies).Append('|');
            if (pref.DesktopWidget != null) {
                var w = pref.DesktopWidget;
                sb.Append(w.Enabled).Append('|').Append(w.Style).Append('|').Append(w.DockEdge).Append('|').Append(w.AutoHideOnEdge).Append('|').Append(w.TopMost).Append('|').Append(w.FullscreenPolicy).Append('|').Append(w.CompactOnMaximized).Append('|').Append(w.MaxDevices).Append('|');
            }
            sb.Append(pref.LocalApi != null && pref.LocalApi.Enabled).Append('|').Append(owner.LocalApiRunning).Append('|').Append(owner.LocalApiPort).Append('|');
            if (pref.TaskbarOverlay != null) {
                var tb = pref.TaskbarOverlay;
                sb.Append(tb.Enabled).Append('|').Append(tb.ShowIcons).Append('|').Append(tb.MaxDevices).Append('|').Append(tb.OffsetX).Append('|');
            }
            sb.Append(owner.TaskbarOverlayStatusCode).Append('|');
            sb.Append(owner.StoppedFamilyDeviceCount).Append('|').Append(owner.IsProbeToggleBusy).Append('|').Append(owner.IsFamilyOpBusy).Append('|');
            sb.Append(owner.GenericReady).Append('|').Append(owner.GenericError ?? "").Append('|').Append(owner.GenericHealthSummary ?? "").Append('|');
            sb.Append(owner.Current != null ? owner.Current.Percent.ToString() + ":" + owner.Current.Charging : "-").Append('|');
            sb.Append(owner.Dpi != null ? owner.Dpi.ActiveStage.ToString() + ":" + (owner.Dpi.Stages != null ? owner.Dpi.Stages.Count : 0) : "-").Append('|');
            sb.Append(owner.IsDpiRefreshing).Append('|').Append(owner.DpiError ?? "").Append('|');
            if (owner.WirelessDevices != null) {
                foreach (var d in owner.WirelessDevices) {
                    if (d == null) continue;
                    sb.Append(d.Key).Append(':').Append(d.Percent.HasValue ? d.Percent.Value.ToString() : "-").Append(':').Append(d.Fresh).Append(':').Append(d.Charging);
                    if (d.Dpi != null) sb.Append(':').Append(d.Dpi.Current.HasValue ? d.Dpi.Current.Value.ToString() : "-");
                    sb.Append(';');
                }
            }
            if (pref.HiddenDeviceIds != null) {
                foreach (string hid in pref.HiddenDeviceIds) sb.Append("hid:").Append(hid).Append(';');
            }
            return sb.ToString();
        }

        void ShowNotice(string text) {
            if (IsDisposed || String.IsNullOrEmpty(text)) return;
            noticeLabel.Text = text;
            noticeBar.Visible = true;
            noticeTimer.Stop();
            noticeTimer.Start();
        }

        internal List<ControlCenterDeviceItem> BuildDeviceItems() {
            var list = new List<ControlCenterDeviceItem>();
            var pref = DeviceSettings.Current;
            var layout = WirelessLayout.Build(owner, 0, 100000, true);
            foreach (var card in layout.All) {
                if (card == null || card.Id == "empty") continue;
                bool isNoBat = card.AudioOnly;
                var item = new ControlCenterDeviceItem {
                    Key = card.Id,
                    Name = card.Name,
                    Kind = card.Kind ?? "other",
                    Battery = card.Battery,
                    Card = card,
                    IsNativeMouse = card.NativeMouse,
                    IsNativeHeadset = card.NativeHeadset,
                    IsAudioOnly = card.AudioOnly,
                    Hidden = pref.HiddenDeviceIds.Contains(card.Id),
                    PinnedTray = pref.TrayDeviceId == card.Id,
                    CanRename = !card.AudioOnly && (card.NativeMouse || card.NativeHeadset || card.Device != null),
                    CanHide = true,
                    CanPinTray = !card.AudioOnly,
                    Capability = isNoBat ? DeviceBatteryCapability.NoBatteryPlaceholder : DeviceBatteryCapability.Supported
                };
                if (card.AudioOnly) {
                    item.Transport = "仅音频控制";
                    string audioState = card.Subtitle ?? "不提供电池读数";
                    item.StateText = item.Hidden ? audioState + " · 已隐藏" : audioState;
                    item.NoBatteryReason = item.Hidden ? "已隐藏" : "仅音频";
                    item.VerificationText = "系统音频通道";
                } else if (card.NativeMouse) {
                    item.Transport = "2.4 GHz 接收器";
                    item.StateText = card.Battery != null && card.Battery.Fresh ? "在线 · " + card.Battery.TimeText : "暂未取得读数";
                    item.VerificationText = "本项目已实机验证（A 级）";
                    if (owner.Dpi != null && owner.Dpi.Stages != null && owner.Dpi.Stages.Count > 0) {
                        var arr = new int[owner.Dpi.Stages.Count];
                        for (int i = 0; i < owner.Dpi.Stages.Count; i++) arr[i] = owner.Dpi.Stages[i].XDpi;
                        item.DpiStages = arr;
                        item.ActiveDpiIndex = owner.Dpi.ActiveStage;
                        item.CurrentDpi = owner.Dpi.ActiveStage >= 0 && owner.Dpi.ActiveStage < arr.Length ? (int?)arr[owner.Dpi.ActiveStage] : null;
                        item.HasWritableDpi = owner.CanSetNativeDpi;
                        item.DpiTargetToken = owner.NativeDpiToken;
                    }
                } else if (card.NativeHeadset) {
                    item.Transport = "2.4 GHz 接收器";
                    item.StateText = card.Battery != null && card.Battery.Fresh ? "在线 · " + card.Battery.TimeText : "暂未取得读数";
                    item.VerificationText = "本项目已实机验证（A 级）";
                } else if (card.Device != null) {
                    var dev = card.Device;
                    item.Transport = String.IsNullOrEmpty(dev.Transport) ? "无线设备" : dev.Transport;
                    item.StateText = WirelessLayout.Subtitle(dev, card.Battery);
                    item.VerificationText = dev.FamilyProbe ? "自动识别（实验），待确认" :
                        dev.VerificationLevel == "A" ? "本项目已实机验证（A 级）" :
                        dev.VerificationLevel == "B" ? "多方验证（B 级）" : "C 级，待实机验证";
                    item.CanStopFamily = dev.CanDisable;
                    if (dev.Dpi != null && dev.Dpi.Supported && dev.Dpi.Stages != null && dev.Dpi.Stages.Length > 0) {
                        item.DpiStages = dev.Dpi.Stages;
                        item.CurrentDpi = dev.Dpi.Current;
                        item.ActiveDpiIndex = dev.Dpi.Current.HasValue ? Array.IndexOf(dev.Dpi.Stages, dev.Dpi.Current.Value) : -1;
                        item.HasWritableDpi = owner.CanSetWirelessDpi(dev.Id, dev.Dpi.TargetToken);
                        item.DpiTargetToken = dev.Dpi.TargetToken;
                    }
                } else {
                    item.Transport = "已隐藏 / 离线";
                    item.StateText = card.Subtitle ?? "设备未连接";
                    item.VerificationText = "—";
                }
                list.Add(item);
            }
            var unadapted = owner.UnadaptedDevicesSnapshot;
            if (unadapted != null) {
                for (int i = 0; i < unadapted.Length; i++) {
                    var u = unadapted[i];
                    if (u == null) continue;
                    string clean = UnadaptedDevice.CleanProduct(u.Product);
                    if (String.IsNullOrEmpty(clean)) clean = String.Format("USB 设备 ({0:X4}:{1:X4})", u.VendorId, u.ProductId);
                    list.Add(new ControlCenterDeviceItem {
                        Key = String.Format("unadapted:{0:X4}:{1:X4}:{2}", u.VendorId, u.ProductId, i),
                        Name = clean,
                        Kind = "other",
                        Transport = String.Format("USB ({0:X4}:{1:X4})", u.VendorId, u.ProductId),
                        StateText = "已连接（未适配或无电量接口）",
                        NoBatteryReason = "读不到电量",
                        VerificationText = "尚未适配，可在「诊断与反馈」提交资料",
                        Capability = DeviceBatteryCapability.NoBatteryPlaceholder,
                        Unadapted = u
                    });
                }
            }
            return list;
        }

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

        const int WM_SETREDRAW = 0x000B;
        const uint RDW_ERASE = 0x0004;
        const uint RDW_FRAME = 0x0400;
        const uint RDW_INVALIDATE = 0x0001;
        const uint RDW_ALLCHILDREN = 0x0080;

        void RebuildCurrentPage(bool resetScroll = false) {
            if (IsDisposed || closing || rebuilding) return;
            rebuilding = true;
            deferredDataRefresh = false;
            lastDataSignature = ComputeDataSignature();
            var pref = DeviceSettings.Current;
            rail.UpdateIndicators((pref.DesktopWidget != null && pref.DesktopWidget.Enabled) || (pref.TaskbarOverlay != null && pref.TaskbarOverlay.Enabled), owner.LocalApiRunning);

            Point savedScroll = resetScroll ? Point.Empty : pageHost.AutoScrollPosition;
            bool lockRedraw = pageHost.IsHandleCreated && pageHost.Visible;
            if (lockRedraw) SendMessage(pageHost.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            pageHost.SuspendLayout();
            try {
                while (pageHost.Controls.Count > 0) {
                    var old = pageHost.Controls[0];
                    pageHost.Controls.RemoveAt(0);
                    old.Dispose();
                }
                Control content;
                if (currentPage == ControlCenterPage.Devices) content = BuildDevicesPage();
                else if (currentPage == ControlCenterPage.Appearance) content = BuildAppearancePage();
                else if (currentPage == ControlCenterPage.Placement) content = BuildPlacementPage();
                else if (currentPage == ControlCenterPage.Reminders) content = BuildRemindersPage();
                else if (currentPage == ControlCenterPage.Diagnostics) content = BuildDiagnosticsPage();
                else content = BuildSettingsPage();
                content.Dock = DockStyle.Top;
                pageHost.Controls.Add(content);
            } finally {
                pageHost.ResumeLayout(true);
                if (!resetScroll && (savedScroll.X != 0 || savedScroll.Y != 0)) {
                    pageHost.AutoScrollPosition = new Point(-savedScroll.X, -savedScroll.Y);
                }
                rebuilding = false;
                if (lockRedraw && !pageHost.IsDisposed) {
                    SendMessage(pageHost.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
                    RedrawWindow(pageHost.Handle, IntPtr.Zero, IntPtr.Zero, RDW_ERASE | RDW_FRAME | RDW_INVALIDATE | RDW_ALLCHILDREN);
                }
            }
        }

        Panel CreateSpacer(int height, Color? backColor = null) {
            return new Panel { Dock = DockStyle.Top, Height = Math.Max(1, height), BackColor = backColor ?? palette.WindowBackground };
        }

        Panel CreatePageRoot(string title, string subtitle = null, Control headerRight = null) {
            var root = new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = palette.WindowBackground };
            int headH = String.IsNullOrEmpty(subtitle) ? 54 : 72;
            var head = new Panel { Dock = DockStyle.Top, Height = headH, BackColor = palette.WindowBackground };
            var lbl = new Label { Text = title, Font = titleFont, ForeColor = palette.Foreground, BackColor = palette.WindowBackground, AutoSize = true, Location = new Point(0, 2) };
            head.Controls.Add(lbl);
            if (!String.IsNullOrEmpty(subtitle)) {
                var sub = new Label { Text = subtitle, Font = subFont, ForeColor = secondary, BackColor = palette.WindowBackground, AutoSize = true, Location = new Point(1, 34) };
                head.Controls.Add(sub);
            }
            if (headerRight != null) {
                headerRight.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                head.Controls.Add(headerRight);
                Action place = delegate { headerRight.Location = new Point(Math.Max(0, head.ClientSize.Width - headerRight.Width), 6); };
                head.Resize += delegate { place(); };
                place();
            }
            root.Controls.Add(head);
            return root;
        }

        Label CreateSectionHeading(string text, int topSpace = 14) {
            return new Label {
                Text = text,
                Font = subBoldFont,
                ForeColor = secondary,
                BackColor = palette.WindowBackground,
                AutoSize = true,
                Dock = DockStyle.Top,
                Padding = new Padding(4, topSpace, 0, 8)
            };
        }

        ControlCenterCard CreateCard(Padding pad) {
            return new ControlCenterCard(palette, dark, pad);
        }

        Panel CreateSettingRow(string title, string subtitle, Control rightControl, bool topBorder, bool preferStacked = false, int minTextWidth = 185) {
            bool hoveredRow = false;
            int singleRowH = String.IsNullOrEmpty(subtitle) ? 54 : 64;
            var row = new Panel { Dock = DockStyle.Top, Height = singleRowH, BackColor = palette.Card, Padding = new Padding(0, 6, 0, 6) };
            row.Paint += delegate(object sender, PaintEventArgs e) {
                var g = e.Graphics;
                g.Clear(palette.Card);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (hoveredRow && !VisualDesign.HighContrast) {
                    CompactRenderer.Fill(g, new RectangleF(4, 2, Math.Max(10, row.Width - 8), Math.Max(10, row.Height - 4)), Color.FromArgb(dark ? 10 : 8, palette.Foreground), 6f);
                }
                if (topBorder) {
                    // Hairline, inset 10px on both sides: visible enough to group rows, quiet enough not to box them.
                    using (var pen = new Pen(VisualDesign.HighContrast ? palette.Border : Color.FromArgb(dark ? 62 : 78, palette.Border), 1)) {
                        g.DrawLine(pen, 10, 0, Math.Max(10, row.Width - 10), 0);
                    }
                }
            };
            row.MouseEnter += delegate { hoveredRow = true; row.Invalidate(); };
            row.MouseLeave += delegate { hoveredRow = false; row.Invalidate(); };

            var titleLbl = new Label { Text = title, Font = bodyFont, ForeColor = palette.Foreground, BackColor = Color.Transparent, AutoSize = true, Location = new Point(10, String.IsNullOrEmpty(subtitle) ? 17 : 11) };
            row.Controls.Add(titleLbl);
            Label subLbl = null;
            if (!String.IsNullOrEmpty(subtitle)) {
                subLbl = new Label { Text = subtitle, Font = subFont, ForeColor = secondary, BackColor = Color.Transparent, AutoSize = true, AutoEllipsis = true, Location = new Point(10, 34) };
                row.Controls.Add(subLbl);
            }
            if (rightControl != null) {
                row.Controls.Add(rightControl);
                var segs = rightControl as ControlCenterSegments;
                int initialRightW = rightControl.Width;
                Action position = delegate {
                    int cw = row.ClientSize.Width;
                    if (cw <= 0) return;
                    int naturalW = segs != null ? segs.NaturalWidth : initialRightW;
                    bool shouldStack = preferStacked || (cw - naturalW - 28 < minTextWidth && naturalW > 130);
                    if (shouldStack) {
                        int maxW = Math.Max(120, cw - 20);
                        if (segs != null) segs.FitMaxWidth(maxW);
                        else rightControl.Width = Math.Min(initialRightW, maxW);
                        int ctrlTop = String.IsNullOrEmpty(subtitle) ? 40 : 58;
                        int desiredH = ctrlTop + rightControl.Height + 12;
                        if (row.Height != desiredH) row.Height = desiredH;
                        titleLbl.Location = new Point(10, 10);
                        titleLbl.MaximumSize = new Size(maxW, 24);
                        if (subLbl != null) {
                            subLbl.Location = new Point(10, 32);
                            subLbl.MaximumSize = new Size(maxW, 20);
                        }
                        rightControl.Location = new Point(10, ctrlTop);
                    } else {
                        if (segs != null) segs.FitMaxWidth(naturalW);
                        else if (rightControl.Width != initialRightW) rightControl.Width = initialRightW;
                        if (row.Height != singleRowH) row.Height = singleRowH;
                        titleLbl.Location = new Point(10, String.IsNullOrEmpty(subtitle) ? 17 : 11);
                        int rx = Math.Max(160, cw - rightControl.Width - 10);
                        rightControl.Location = new Point(rx, (row.Height - rightControl.Height) / 2);
                        int maxTextW = Math.Max(110, rx - 22);
                        titleLbl.MaximumSize = new Size(maxTextW, 24);
                        if (subLbl != null) {
                            subLbl.Location = new Point(10, 34);
                            subLbl.MaximumSize = new Size(maxTextW, 20);
                        }
                    }
                };
                row.Resize += delegate { position(); };
                position();
            }
            return row;
        }

        Control CreateSegmentedChoices(string[] labels, int selectedIndex, Action<int> onSelect) {
            return new ControlCenterSegments(labels, selectedIndex, onSelect, palette, subFont, subBoldFont);
        }

        ControlCenterButton CreateActionButton(string text, EventHandler onClick, bool danger = false, bool primary = false, string symbolName = null) {
            var variant = danger ? ControlCenterButtonVariant.Danger : primary ? ControlCenterButtonVariant.Primary : ControlCenterButtonVariant.Secondary;
            var btn = new ControlCenterButton(text, palette, dark, subFont, variant, symbolName);
            btn.Click += onClick;
            return btn;
        }

        void UpdatePreference(Action<AudioBindings> mutate, bool refreshAudio = false, bool refreshTheme = false) {
            var pref = DeviceSettings.Current;
            mutate(pref);
            try {
                owner.SavePreferences(pref);
                owner.ConfigurationChanged(refreshAudio);
                if (refreshTheme) RefreshTheme();
                else RebuildCurrentPage();
            } catch (Exception ex) {
                ShowNotice("设置未保存：" + ex.Message);
            }
        }

        // Flicker-free in-place update for controls that do not add/remove page rows.
        void UpdatePreferenceInPlace(Action<AudioBindings> mutate, Action afterSave = null) {
            var pref = DeviceSettings.Current;
            mutate(pref);
            try {
                bool prevRebuilding = rebuilding;
                rebuilding = true;
                try {
                    owner.SavePreferences(pref);
                    owner.ConfigurationChanged(false);
                } finally {
                    rebuilding = prevRebuilding;
                }
                lastDataSignature = ComputeDataSignature();
                var latest = DeviceSettings.Current;
                rail.UpdateIndicators((latest.DesktopWidget != null && latest.DesktopWidget.Enabled) || (latest.TaskbarOverlay != null && latest.TaskbarOverlay.Enabled), owner.LocalApiRunning);
                if (afterSave != null) afterSave();
            } catch (Exception ex) {
                ShowNotice("设置未保存：" + ex.Message);
                RebuildCurrentPage();
            }
        }

        // ==================== PAGE 1: DEVICES ====================
        Control BuildDevicesPage() {
            var refreshBtn = CreateActionButton("刷新电量", delegate {
                owner.RefreshBattery(true);
                ShowNotice("正在刷新全部设备电量…");
            }, false, false, "refresh");
            var root = CreatePageRoot("设备", "查看无线外设实时电量，管理显示名称、板载 DPI 与托盘固定", refreshBtn);

            var items = BuildDeviceItems();
            if (items.Count == 0) {
                var emptyCard = CreateCard(new Padding(24));
                emptyCard.Dock = DockStyle.Top;
                emptyCard.Controls.Add(new Label {
                    Text = "当前尚未检测到外设。连接无线接收器、蓝牙或有线设备后，点右上角“刷新电量”即可显示。",
                    Font = bodyFont,
                    ForeColor = secondary,
                    BackColor = palette.Card,
                    AutoSize = true
                });
                root.Controls.Add(emptyCard);
                root.Controls.SetChildIndex(emptyCard, 0);
                return root;
            }

            if (String.IsNullOrEmpty(selectedDeviceKey) || !items.Exists(delegate(ControlCenterDeviceItem it) { return it.Key == selectedDeviceKey; })) {
                selectedDeviceKey = items[0].Key;
            }
            var selected = items.Find(delegate(ControlCenterDeviceItem it) { return it.Key == selectedDeviceKey; });

            var split = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, AutoSize = true, BackColor = palette.WindowBackground, Margin = Padding.Empty };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 296));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            split.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var left = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = palette.WindowBackground, Padding = new Padding(0, 0, 14, 0) };
            var supported = items.FindAll(delegate(ControlCenterDeviceItem it) { return it.Capability == DeviceBatteryCapability.Supported; });
            var audioCards = items.FindAll(delegate(ControlCenterDeviceItem it) { return it.IsAudioOnly; });
            var noBat = items.FindAll(delegate(ControlCenterDeviceItem it) { return it.Capability == DeviceBatteryCapability.NoBatteryPlaceholder && !it.IsAudioOnly; });

            if (supported.Count > 0) {
                left.Controls.Add(CreateGroupHeading("在线 / 已识别设备 (" + supported.Count + ")"));
                foreach (var it in supported) left.Controls.Add(CreateDeviceListButton(it, it.Key == selected.Key));
            }
            if (audioCards.Count > 0) {
                left.Controls.Add(CreateGroupHeading("有线 / 音频卡片 (" + audioCards.Count + ")"));
                foreach (var it in audioCards) left.Controls.Add(CreateCompactNoBatteryItem(it, it.Key == selected.Key));
            }
            if (noBat.Count > 0) {
                if (selected.Capability == DeviceBatteryCapability.NoBatteryPlaceholder && !selected.IsAudioOnly) showNoBatteryDevices = true;
                left.Controls.Add(CreateNoBatteryExpander(noBat.Count, showNoBatteryDevices));
                if (showNoBatteryDevices) {
                    foreach (var it in noBat) left.Controls.Add(CreateCompactNoBatteryItem(it, it.Key == selected.Key));
                }
            }

            var right = BuildDeviceDetailCard(selected);
            right.Dock = DockStyle.Top;

            split.Controls.Add(left, 0, 0);
            split.Controls.Add(right, 1, 0);
            root.Controls.Add(split);
            root.Controls.SetChildIndex(split, 0);
            return root;
        }

        Control CreateNoBatteryExpander(int count, bool expanded) {
            bool hoveredExp = false;
            var hdr = new Panel {
                Width = 278,
                Height = 32,
                BackColor = palette.WindowBackground,
                Margin = new Padding(0, 10, 0, 2),
                Cursor = Cursors.Hand
            };
            hdr.MouseEnter += delegate { hoveredExp = true; hdr.Invalidate(); };
            hdr.MouseLeave += delegate { hoveredExp = false; hdr.Invalidate(); };
            hdr.Paint += delegate(object sender, PaintEventArgs e) {
                var g = e.Graphics;
                g.Clear(palette.WindowBackground);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                if (hoveredExp && !VisualDesign.HighContrast) {
                    CompactRenderer.Fill(g, new RectangleF(1f, 2f, hdr.Width - 2f, hdr.Height - 4f), Color.FromArgb(dark ? 12 : 10, palette.Foreground), 6f);
                }
                string arrow = expanded ? "▾" : "▸";
                CompactRenderer.DrawLabel(g, arrow + "  其他未读到电量的设备 (" + count + ")", new RectangleF(8, 0, hdr.Width - 16, hdr.Height), 12f, false, hoveredExp ? palette.Foreground : secondary, StringAlignment.Near);
            };
            hdr.Click += delegate {
                showNoBatteryDevices = !showNoBatteryDevices;
                RebuildCurrentPage();
            };
            return hdr;
        }

        Control CreateCompactNoBatteryItem(ControlCenterDeviceItem item, bool isSelected) {
            bool hoveredRow = false;
            var row = new Panel {
                Width = 278,
                Height = 34,
                BackColor = palette.WindowBackground,
                Margin = new Padding(0, 0, 0, 2),
                Cursor = Cursors.Hand
            };
            row.MouseEnter += delegate { hoveredRow = true; row.Invalidate(); };
            row.MouseLeave += delegate { hoveredRow = false; row.Invalidate(); };
            row.Paint += delegate(object sender, PaintEventArgs e) {
                var g = e.Graphics;
                g.Clear(palette.WindowBackground);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var rect = new RectangleF(1f, 1f, row.Width - 2f, row.Height - 2f);
                if (isSelected) {
                    CompactRenderer.Fill(g, rect, palette.Card, 6f);
                    using (var path = VisualDesign.RoundRect(rect.X, rect.Y, rect.Width, rect.Height, 6f))
                    using (var pen = new Pen(palette.Border, 1f)) g.DrawPath(pen, path);
                } else if (hoveredRow && !VisualDesign.HighContrast) {
                    CompactRenderer.Fill(g, rect, Color.FromArgb(dark ? 12 : 10, palette.Foreground), 6f);
                }
                CompactRenderer.DrawSymbol(g, ControlCenterTokens.SymbolForKind(item.Kind), new RectangleF(12, 10, 14, 14), secondary);
                CompactRenderer.DrawLabel(g, item.Name, new RectangleF(32, 0, 154, row.Height), 12f, isSelected, isSelected ? palette.Foreground : secondary, StringAlignment.Near);
                CompactRenderer.DrawLabel(g, item.NoBatteryReason ?? "无电量", new RectangleF(190, 0, 78, row.Height), 12f, false, secondary, StringAlignment.Far);
            };
            row.Click += delegate {
                if (selectedDeviceKey != item.Key) {
                    selectedDeviceKey = item.Key;
                    RebuildCurrentPage();
                }
            };
            return row;
        }

        Label CreateGroupHeading(string text) {
            return new Label {
                Text = text,
                Font = subBoldFont,
                ForeColor = secondary,
                BackColor = palette.WindowBackground,
                AutoSize = true,
                Margin = new Padding(6, 6, 0, 6)
            };
        }

        Control CreateDeviceListButton(ControlCenterDeviceItem item, bool isSelected) {
            bool hoveredRow = false;
            var row = new Panel {
                Width = 278,
                Height = 60,
                BackColor = palette.WindowBackground,
                Margin = new Padding(0, 0, 0, 4),
                Cursor = Cursors.Hand
            };
            row.MouseEnter += delegate { hoveredRow = true; row.Invalidate(); };
            row.MouseLeave += delegate { hoveredRow = false; row.Invalidate(); };
            row.Paint += delegate(object sender, PaintEventArgs e) {
                var g = e.Graphics;
                g.Clear(palette.WindowBackground);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var rect = new RectangleF(1f, 1f, row.Width - 2f, row.Height - 2f);
                if (isSelected) {
                    CompactRenderer.Fill(g, rect, palette.Card, 8f);
                    using (var path = VisualDesign.RoundRect(rect.X, rect.Y, rect.Width, rect.Height, 8f))
                    using (var pen = new Pen(palette.Border, 1f)) g.DrawPath(pen, path);
                    CompactRenderer.Fill(g, new RectangleF(1f, rect.Y + 14f, 3f, rect.Height - 28f), palette.Foreground, 1.5f);
                } else if (hoveredRow && !VisualDesign.HighContrast) {
                    CompactRenderer.Fill(g, rect, Color.FromArgb(dark ? 14 : 12, palette.Foreground), 8f);
                }

                var tile = new RectangleF(12, 12, 36, 36);
                // Light theme, unselected: no grey icon box, the line icon sits directly on the list surface.
                bool frameless = !dark && !isSelected && !VisualDesign.HighContrast;
                if (!frameless) CompactRenderer.Fill(g, tile, palette.RaisedSurface, 8f);
                Color symColor = item.Battery != null && item.Battery.Fresh && ThemePalette.IsLow(item.Battery, DeviceSettings.Current.WarningThreshold)
                    ? ThemePalette.LevelColor(10, dark)
                    : (isSelected ? palette.Foreground : (frameless ? ThemePalette.Blend(secondary, palette.Foreground, 110) : secondary));
                CompactRenderer.DrawSymbol(g, ControlCenterTokens.SymbolForKind(item.Kind), new RectangleF(tile.X + 9, tile.Y + 9, 18, 18), symColor);

                CompactRenderer.DrawLabel(g, item.Name, new RectangleF(58, 11, 134, 20), 13.5f, true, palette.Foreground, StringAlignment.Near);
                string subTransport = item.Hidden ? "已隐藏 · " + item.Transport : item.Transport;
                CompactRenderer.DrawLabel(g, subTransport, new RectangleF(58, 32, 134, 18), 12f, false, secondary, StringAlignment.Near);

                if (item.Capability == DeviceBatteryCapability.NoBatteryPlaceholder) {
                    CompactRenderer.DrawLabel(g, item.NoBatteryReason ?? "无电量", new RectangleF(192, 19, 76, 20), 12f, false, secondary, StringAlignment.Far);
                } else if (item.Battery != null && item.Battery.Percent.HasValue) {
                    Color level = item.Battery.Fresh ? ThemePalette.LevelColor(item.Battery.Percent, dark) : secondary;
                    CompactRenderer.DrawLabel(g, item.Battery.Percent.Value + "%", new RectangleF(196, 10, 70, 22), 16f, true, level, StringAlignment.Far);
                    var bar = new RectangleF(206, 38, 60, 3.5f);
                    CompactRenderer.Fill(g, bar, palette.Track, 1.75f);
                    float fw = bar.Width * Math.Max(0, Math.Min(100, item.Battery.Percent.Value)) / 100f;
                    if (fw > 0) CompactRenderer.Fill(g, new RectangleF(bar.X, bar.Y, fw, bar.Height), level, 1.75f);
                } else {
                    CompactRenderer.DrawLabel(g, "—", new RectangleF(196, 17, 70, 22), 16f, false, secondary, StringAlignment.Far);
                }
            };
            row.Click += delegate {
                if (selectedDeviceKey != item.Key) {
                    selectedDeviceKey = item.Key;
                    RebuildCurrentPage();
                }
            };
            return row;
        }

        Control BuildDeviceDetailCard(ControlCenterDeviceItem item) {
            var card = CreateCard(new Padding(16, 16, 16, 14));

            var hero = new Panel { Dock = DockStyle.Top, Height = 112, BackColor = palette.Card, Padding = new Padding(8, 4, 8, 8) };
            var ring = new ControlCenterHeroRing(palette, dark) { Location = new Point(8, 6) };
            ring.UpdateValue(palette, dark, item.Battery != null ? item.Battery.Percent : (int?)null, item.Battery != null && item.Battery.Fresh);
            hero.Controls.Add(ring);

            var hName = new Label { Text = item.Name, Font = headingFont, ForeColor = palette.Foreground, BackColor = palette.Card, AutoSize = true, Location = new Point(116, 12) };
            string kindCn = item.Kind == "mouse" ? "鼠标" : item.Kind == "headset" ? "耳机" : item.Kind == "keyboard" ? "键盘" : item.Kind == "gamepad" ? "手柄" : "外设";
            var hMeta = new Label { Text = kindCn + " · " + item.Transport, Font = subFont, ForeColor = secondary, BackColor = palette.Card, AutoSize = true, Location = new Point(116, 40) };

            bool isFresh = item.Battery != null && item.Battery.Fresh;
            bool isLow = isFresh && ThemePalette.IsLow(item.Battery, DeviceSettings.Current.WarningThreshold);
            var badgeTone = isLow ? ControlCenterBadgeTone.Accent : isFresh ? ControlCenterBadgeTone.Positive : ControlCenterBadgeTone.Muted;
            var stateBadge = new ControlCenterBadge(item.StateText, palette, dark, subFont, badgeTone, false, true) { Location = new Point(116, 66) };

            hero.Controls.Add(hName);
            hero.Controls.Add(hMeta);
            hero.Controls.Add(stateBadge);

            var rows = new List<Control>();
            if (item.CanRename) {
                var input = new ControlCenterTextBox(item.Name, palette, dark, subFont, 196);
                input.Committed += delegate {
                    if (rebuilding || closing || IsDisposed) return;
                    string trimmed = (input.ValueText ?? "").Trim();
                    if (trimmed == item.Name) {
                        if (deferredDataRefresh) RefreshData();
                        return;
                    }
                    UpdatePreference(delegate(AudioBindings p) {
                        if (item.IsNativeMouse) p.MouseAlias = trimmed;
                        else if (item.IsNativeHeadset) p.HeadsetAlias = trimmed;
                        else if (String.IsNullOrEmpty(trimmed)) p.DeviceAliases.Remove(item.Key);
                        else p.DeviceAliases[item.Key] = trimmed;
                    });
                    ShowNotice("显示名称已保存");
                };
                rows.Add(CreateSettingRow("显示名称", "留空恢复默认名，回车或失焦保存", input, true));
            }

            if (item.DpiStages != null && item.DpiStages.Length > 0) {
                string[] labels = new string[item.DpiStages.Length];
                for (int i = 0; i < item.DpiStages.Length; i++) labels[i] = item.DpiStages[i].ToString();
                ControlCenterSegments dpiCtrl = null;
                dpiCtrl = (ControlCenterSegments)CreateSegmentedChoices(labels, item.ActiveDpiIndex, delegate(int idx) {
                    if (!item.HasWritableDpi) {
                        if (dpiCtrl != null) dpiCtrl.SetSelectedIndex(item.ActiveDpiIndex);
                        ShowNotice("此设备当前为只读 DPI 或状态待确认");
                        return;
                    }
                    if (item.IsNativeMouse) owner.SelectDpiStage(idx, item.DpiTargetToken);
                    else owner.SelectWirelessDpi(item.Key, item.DpiTargetToken, item.DpiStages[idx]);
                    ShowNotice("已请求切换 DPI 至 " + item.DpiStages[idx]);
                });
                rows.Add(CreateSettingRow("DPI 档位", item.HasWritableDpi ? "设备板载档位，点击即时切换" : "当前仅支持读取 DPI", dpiCtrl, true));
            }

            if (item.CanHide) {
                var showToggle = new ControlCenterToggle(palette, dark) { Checked = !item.Hidden };
                showToggle.Toggled += delegate {
                    UpdatePreference(delegate(AudioBindings p) { DeviceSettings.SetDeviceHidden(p, item.Key, !showToggle.Checked); });
                };
                string hideDesc = item.IsAudioOnly
                    ? "关闭后不在托盘弹出面板显示此有线/音源卡片，仍可在音源设置中控制"
                    : "关闭后不在托盘弹出面板展示，后台仍继续读取电量";
                rows.Add(CreateSettingRow("在主面板显示", hideDesc, showToggle, true));
            }

            if (item.CanPinTray) {
                var pinToggle = new ControlCenterToggle(palette, dark) { Checked = item.PinnedTray };
                pinToggle.Toggled += delegate {
                    UpdatePreferenceInPlace(delegate(AudioBindings p) { p.TrayDeviceId = pinToggle.Checked ? item.Key : null; });
                };
                rows.Add(CreateSettingRow("固定为托盘图标", "主托盘图标始终显示这台设备的电量", pinToggle, true));
            }

            var verBadge = new ControlCenterBadge(item.VerificationText, palette, dark, subFont, item.VerificationText.Contains("A 级") ? ControlCenterBadgeTone.Positive : ControlCenterBadgeTone.Muted, false, true);
            rows.Add(CreateSettingRow("适配验证", "设备电量协议的实机校验等级", verBadge, true));

            if (item.CanStopFamily) {
                var stopBtn = CreateActionButton("停止读取…", delegate {
                    var confirm = MessageBox.Show(this, "停止读取后将不再向「" + item.Name + "」发送查询。\n之后可随时在「设置 → 已停止读取的设备」中恢复。", "停止读取此设备", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                    if (confirm != DialogResult.OK) return;
                    owner.StopFamilyDevice(item.Key, item.Name, delegate(bool ok, string msg) {
                        if (!IsDisposed) { ShowNotice(msg); RebuildCurrentPage(); }
                    });
                }, true);
                rows.Add(CreateSettingRow("停止读取此设备", "不再向它发送查询，可在「设置」里随时恢复", stopBtn, true));
            }

            for (int i = rows.Count - 1; i >= 0; i--) card.Controls.Add(rows[i]);
            card.Controls.Add(hero);
            return card;
        }

        // ==================== PAGE 2: APPEARANCE ====================
        Control BuildAppearancePage() {
            var root = CreatePageRoot("外观", "自定义配色主题、深浅模式、面板布局与字体大小，右侧实时预览");
            var pref = DeviceSettings.Current;
            string currentStyle = ThemePalette.Normalize(pref.ThemeStyle);

            var grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, AutoSize = true, BackColor = palette.WindowBackground };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 294));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var leftCard = CreateCard(new Padding(14, 14, 14, 10));
            leftCard.Dock = DockStyle.Top;
            leftCard.Margin = new Padding(0, 0, 14, 0);

            var swatchSection = new Panel { Dock = DockStyle.Top, Height = 112, BackColor = palette.Card, Padding = new Padding(10, 4, 10, 6) };
            var swatchTitle = new Label { Text = "主题配色", Font = bodyFont, ForeColor = palette.Foreground, BackColor = palette.Card, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 8) };
            var swatchGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, BackColor = palette.Card, Margin = Padding.Empty };
            for (int c = 0; c < 5; c++) swatchGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20f));
            swatchGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            string[] displayOrder = { "graphite", "ocean", "native", "amber", "mist" };
            for (int i = 0; i < displayOrder.Length; i++) {
                string k = displayOrder[i];
                var card = new ControlCenterSwatchCard(k, ThemePalette.Name(k), k == "graphite", palette, dark);
                card.Dock = DockStyle.Fill;
                card.UpdateState(palette, dark, currentStyle == k);
                card.Margin = new Padding(0, 0, i < 4 ? 6 : 0, 0);
                card.Chosen += delegate {
                    UpdatePreference(delegate(AudioBindings p) { p.ThemeStyle = k; }, false, true);
                };
                swatchGrid.Controls.Add(card, i, 0);
            }
            swatchSection.Controls.Add(swatchGrid);
            swatchSection.Controls.Add(swatchTitle);

            int themeIdx = pref.Theme == "light" ? 1 : pref.Theme == "dark" ? 2 : 0;
            var themeRow = CreateSettingRow("明暗模式", "跟随系统深浅色或手动锁定", CreateSegmentedChoices(new[] { "跟随系统", "浅色", "深色" }, themeIdx, delegate(int idx) {
                UpdatePreference(delegate(AudioBindings p) { p.Theme = idx == 1 ? "light" : idx == 2 ? "dark" : "auto"; }, false, true);
            }), true);

            var preview = new ControlCenterLivePreview(owner, palette, dark) { Dock = DockStyle.Top, Height = 360 };

            int layoutIdx = Array.IndexOf(PanelLayouts.Keys, PanelLayouts.Normalize(pref.PanelLayout));
            if (layoutIdx < 0) layoutIdx = 0;
            var layoutRow = CreateSettingRow("主面板布局", "环形、电量条或网格三种样式", CreateSegmentedChoices(PanelLayouts.Names, layoutIdx, delegate(int idx) {
                UpdatePreferenceInPlace(delegate(AudioBindings p) { p.PanelLayout = PanelLayouts.Keys[idx]; }, delegate { preview.Invalidate(); });
            }), true);

            int fontIdx = pref.FontSize == "large" ? 1 : 0;
            var fontRow = CreateSettingRow("字体大小", "同步缩放托盘弹出面板与控制中心文字", CreateSegmentedChoices(new[] { "标准", "偏大" }, fontIdx, delegate(int idx) {
                UpdatePreference(delegate(AudioBindings p) { p.FontSize = idx == 1 ? "large" : "normal"; }, false, true);
            }), true);

            var motionToggle = new ControlCenterToggle(palette, dark) { Checked = pref.ReducedMotion };
            motionToggle.Toggled += delegate {
                UpdatePreferenceInPlace(delegate(AudioBindings p) { p.ReducedMotion = motionToggle.Checked; });
            };
            var motionRow = CreateSettingRow("减弱动画效果", "减少面板展开、切换与进度条过渡动画", motionToggle, true);

            leftCard.Controls.Add(motionRow);
            leftCard.Controls.Add(fontRow);
            leftCard.Controls.Add(layoutRow);
            leftCard.Controls.Add(themeRow);
            leftCard.Controls.Add(swatchSection);

            var rightWrap = new Panel { Dock = DockStyle.Top, AutoSize = true, BackColor = palette.WindowBackground };
            var previewLbl = new Label { Text = "实时预览（当前主面板）", Font = subFont, ForeColor = secondary, BackColor = palette.WindowBackground, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(4, 0, 0, 8) };
            rightWrap.Controls.Add(preview);
            rightWrap.Controls.Add(previewLbl);

            grid.Controls.Add(leftCard, 0, 0);
            grid.Controls.Add(rightWrap, 1, 0);
            root.Controls.Add(grid);
            root.Controls.SetChildIndex(grid, 0);
            return root;
        }

        // ==================== PAGE 3: PLACEMENT ====================
        Control BuildPlacementPage() {
            var root = CreatePageRoot("显示位置", "选择在系统托盘、桌面悬浮胶囊或任务栏旁查看外设电量");
            var pref = DeviceSettings.Current;

            var cardsGrid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, RowCount = 1, Height = 124, BackColor = palette.WindowBackground, Margin = new Padding(0, 0, 0, 14) };
            cardsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            cardsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            cardsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));

            bool trayHidden = pref.HideTrayIcon;
            bool trayFallbackVisible = trayHidden && TrayVisibilityPolicy.ShouldShowTrayIcon(pref, owner.TaskbarOverlayStatusCode);
            string trayTag = !trayHidden ? "已启用" : (trayFallbackVisible ? "临时恢复" : "已隐藏");
            var trayTone = !trayHidden ? ControlCenterBadgeTone.Positive : (trayFallbackVisible ? ControlCenterBadgeTone.Accent : ControlCenterBadgeTone.Muted);
            cardsGrid.Controls.Add(CreatePlacementOptionCard(0, "托盘图标", "通知区域显示电量数字，点击打开主面板", trayTag, trayTone), 0, 0);
            cardsGrid.Controls.Add(CreatePlacementOptionCard(1, "桌面小组件", "悬浮电量胶囊、全壁纸贴边隐藏与全屏防打扰", pref.DesktopWidget.Enabled ? "已启用" : "未启用", pref.DesktopWidget.Enabled ? ControlCenterBadgeTone.Positive : ControlCenterBadgeTone.Muted), 1, 0);
            bool tbOn = pref.TaskbarOverlay != null && pref.TaskbarOverlay.Enabled;
            cardsGrid.Controls.Add(CreatePlacementOptionCard(2, "任务栏电量", "在任务栏通知区左侧以无焦点层直接显示电量", tbOn ? "已启用 · 实验" : "未启用 · 实验", tbOn ? ControlCenterBadgeTone.Positive : ControlCenterBadgeTone.Accent), 2, 0);

            var detailCard = CreateCard(new Padding(16, 14, 16, 16));
            detailCard.Dock = DockStyle.Top;

            var preview = new ControlCenterPlacementPreview(palette, dark) { Dock = DockStyle.Top, Height = 148, Margin = new Padding(8, 12, 8, 2) };
            preview.UpdatePreview(palette, dark, selectedPlacementCard);

            var rows = new List<Control>();
            if (selectedPlacementCard == 0) {
                var showTrayToggle = new ControlCenterToggle(palette, dark) { Checked = !pref.HideTrayIcon };
                showTrayToggle.Toggled += delegate {
                    if (!showTrayToggle.Checked && !TrayVisibilityPolicy.HasAlternativeSurface(DeviceSettings.Current)) {
                        showTrayToggle.Checked = true;
                        showTrayToggle.Invalidate();
                        ShowNotice("请先开启「任务栏电量」或「桌面小组件」，再隐藏托盘图标");
                        return;
                    }
                    UpdatePreference(delegate(AudioBindings p) { p.HideTrayIcon = !showTrayToggle.Checked; });
                };
                string trayToggleDesc = trayFallbackVisible
                    ? "任务栏电量当前处于降级隐藏状态，已临时恢复托盘图标以确保可随时打开控制中心"
                    : "开启任务栏电量或桌面小组件后可独立关闭，避免与任务栏电量重复显示";
                rows.Add(CreateSettingRow("显示托盘图标", trayToggleDesc, showTrayToggle, false));

                int iconStyleIdx = pref.TrayIconStyle == "device" ? 1 : 0;
                rows.Add(CreateSettingRow("托盘图标样式", "选择在系统托盘直接显示电量数字或设备图标", CreateSegmentedChoices(new[] { "数字", "设备图标" }, iconStyleIdx, delegate(int idx) {
                    UpdatePreferenceInPlace(delegate(AudioBindings p) { p.TrayIconStyle = idx == 1 ? "device" : "number"; });
                }), true));
                string[] ruleKeys = { "active", "lowest", "auto", "mouse", "headset", "keyboard" };
                string[] ruleLabels = { "当前使用", "最低电量", "自动", "鼠标优先", "耳机优先", "键盘优先" };
                int ruleIdx = Array.IndexOf(ruleKeys, pref.TraySource);
                rows.Add(CreateSettingRow("主托盘显示规则", String.IsNullOrEmpty(pref.TrayDeviceId) ? "当未固定单台设备时生效" : "当前已固定单台设备，点击任意规则可解除固定", CreateSegmentedChoices(ruleLabels, String.IsNullOrEmpty(pref.TrayDeviceId) ? ruleIdx : -1, delegate(int idx) {
                    UpdatePreferenceInPlace(delegate(AudioBindings p) { p.TraySource = ruleKeys[idx]; p.TrayDeviceId = null; });
                }), true));
            } else if (selectedPlacementCard == 1) {
                var w = pref.DesktopWidget;
                var enableToggle = new ControlCenterToggle(palette, dark) { Checked = w.Enabled };
                enableToggle.Toggled += delegate {
                    bool wasHidden = DeviceSettings.Current.HideTrayIcon;
                    UpdatePreference(delegate(AudioBindings p) { p.DesktopWidget.Enabled = enableToggle.Checked; });
                    if (wasHidden && !DeviceSettings.Current.HideTrayIcon) ShowNotice("已自动恢复托盘图标，确保可随时打开控制中心");
                };
                rows.Add(CreateSettingRow("启用桌面小组件", "在桌面显示独立悬浮电量胶囊，支持自由拖动与点击展开", enableToggle, false));

                int styleIdx = Array.IndexOf(DesktopWidgetGeometry.StyleKeys, DesktopWidgetGeometry.NormalizeStyle(w.Style));
                rows.Add(CreateSettingRow("收起形态", "平时停靠在桌面上的紧凑外观", CreateSegmentedChoices(DesktopWidgetGeometry.StyleNames, styleIdx, delegate(int idx) {
                    UpdatePreferenceInPlace(delegate(AudioBindings p) { p.DesktopWidget.Style = DesktopWidgetGeometry.StyleKeys[idx]; });
                }), true));

                int edgeIdx = Array.IndexOf(DesktopWidgetGeometry.EdgeKeys, DesktopWidgetGeometry.NormalizeEdge(w.DockEdge));
                rows.Add(CreateSettingRow("贴边停靠", "拖近或推到屏幕四边会自动吸附并记住所在显示器", CreateSegmentedChoices(DesktopWidgetGeometry.EdgeNames, edgeIdx, delegate(int idx) {
                    UpdatePreferenceInPlace(delegate(AudioBindings p) { p.DesktopWidget.DockEdge = DesktopWidgetGeometry.EdgeKeys[idx]; });
                }), true));

                var autoHideToggle = new ControlCenterToggle(palette, dark) { Checked = w.AutoHideOnEdge };
                autoHideToggle.Toggled += delegate {
                    UpdatePreferenceInPlace(delegate(AudioBindings p) { p.DesktopWidget.AutoHideOnEdge = autoHideToggle.Checked; });
                };
                rows.Add(CreateSettingRow("贴边自动隐藏", "缩为全壁纸高反差边缘样式，鼠标移近弹性滑出", autoHideToggle, true));

                int hiddenStyleIdx = Array.IndexOf(DesktopWidgetGeometry.HiddenStyleKeys, DesktopWidgetGeometry.NormalizeHiddenStyle(w.HiddenStyle));
                rows.Add(CreateSettingRow("贴边隐藏样式", "贴边缩起后的现代琉璃外观：琉璃丝带、凹角灵动耳翼或微光露珠", CreateSegmentedChoices(DesktopWidgetGeometry.HiddenStyleNames, hiddenStyleIdx, delegate(int idx) {
                    UpdatePreferenceInPlace(delegate(AudioBindings p) { p.DesktopWidget.HiddenStyle = DesktopWidgetGeometry.HiddenStyleKeys[idx]; });
                }), true));

                var topToggle = new ControlCenterToggle(palette, dark) { Checked = w.TopMost };
                topToggle.Toggled += delegate {
                    UpdatePreferenceInPlace(delegate(AudioBindings p) { p.DesktopWidget.TopMost = topToggle.Checked; });
                };
                rows.Add(CreateSettingRow("总在最前", "关闭后小组件像普通窗口一样可被其他应用遮挡", topToggle, true));

                string[] fsKeys = { "hide", "edge", "ignore" };
                string[] fsLabels = { "全屏隐藏", "缩为指示条", "不处理" };
                int fsIdx = Array.IndexOf(fsKeys, DesktopDisplayWatch.NormalizePolicy(w.FullscreenPolicy));
                rows.Add(CreateSettingRow("全屏游戏 / 视频防打扰", "不确定全屏信号时不会永久消失，仅缩为指示条", CreateSegmentedChoices(fsLabels, fsIdx, delegate(int idx) {
                    UpdatePreferenceInPlace(delegate(AudioBindings p) { p.DesktopWidget.FullscreenPolicy = fsKeys[idx]; });
                }), true));

                var maxToggle = new ControlCenterToggle(palette, dark) { Checked = w.CompactOnMaximized };
                maxToggle.Toggled += delegate {
                    UpdatePreferenceInPlace(delegate(AudioBindings p) { p.DesktopWidget.CompactOnMaximized = maxToggle.Checked; });
                };
                rows.Add(CreateSettingRow("窗口最大化时缩为指示条", "当前屏幕有窗口最大化时自动收窄，减少遮挡", maxToggle, true));

                int[] maxOpts = { 2, 3, 4 };
                int maxIdx = Array.IndexOf(maxOpts, w.MaxDevices);
                if (maxIdx < 0) maxIdx = 1;
                rows.Add(CreateSettingRow("收起时最多显示设备数", null, CreateSegmentedChoices(new[] { "2 台", "3 台", "4 台" }, maxIdx, delegate(int idx) {
                    UpdatePreferenceInPlace(delegate(AudioBindings p) { p.DesktopWidget.MaxDevices = maxOpts[idx]; });
                }), true));
            } else {
                var tb = pref.TaskbarOverlay ?? new TaskbarOverlaySettings();
                var tbEnable = new ControlCenterToggle(palette, dark) { Checked = tb.Enabled };
                tbEnable.Toggled += delegate {
                    bool wasHidden = DeviceSettings.Current.HideTrayIcon;
                    UpdatePreference(delegate(AudioBindings p) { p.TaskbarOverlay.Enabled = tbEnable.Checked; });
                    if (wasHidden && !DeviceSettings.Current.HideTrayIcon) ShowNotice("已自动恢复托盘图标，确保可随时打开控制中心");
                };
                rows.Add(CreateSettingRow("启用任务栏电量（实验）", "采用独立无焦点贴合层，绝不注入 Explorer；不支持环境自动降级隐藏", tbEnable, false));

                rows.Add(CreateSettingRow("显示样式", "在任务栏通知区左侧紧凑显示设备电量条", CreateSegmentedChoices(new[] { "图标 + 数字", "仅数字" }, tb.ShowIcons ? 0 : 1, delegate(int idx) {
                    UpdatePreferenceInPlace(delegate(AudioBindings p) { p.TaskbarOverlay.ShowIcons = idx == 0; });
                }), true));

                int[] tbMaxOpts = { 1, 2, 3 };
                int tbMaxIdx = Array.IndexOf(tbMaxOpts, tb.MaxDevices);
                if (tbMaxIdx < 0) tbMaxIdx = 2;
                rows.Add(CreateSettingRow("最多显示设备数", null, CreateSegmentedChoices(new[] { "1 台", "2 台", "3 台" }, tbMaxIdx, delegate(int idx) {
                    UpdatePreferenceInPlace(delegate(AudioBindings p) { p.TaskbarOverlay.MaxDevices = tbMaxOpts[idx]; });
                }), true));

                rows.Add(CreateSettingRow("水平偏移微调", "默认紧贴通知区左侧，也可在任务栏上直接水平拖动微调", CreateTaskbarOffsetStepper(tb.OffsetX), true, false, 300));

                bool tbReady = owner.TaskbarOverlayStatusCode == "ready" || owner.TaskbarOverlayStatusCode == "ready_avoided";
                var statusTone = tbReady ? ControlCenterBadgeTone.Positive : ControlCenterBadgeTone.Muted;
                var diagBadge = new ControlCenterBadge(tbReady ? "环境就绪" : (tb.Enabled ? "已自动降级" : "未开启"), palette, dark, subFont, statusTone, false, true);
                rows.Add(CreateSettingRow("当前环境状态", owner.TaskbarOverlayStatusMessage, diagBadge, true));
            }

            detailCard.Controls.Add(preview);
            detailCard.Controls.Add(CreateSpacer(12, palette.Card));
            for (int i = rows.Count - 1; i >= 0; i--) {
                rows[i].Dock = DockStyle.Top;
                detailCard.Controls.Add(rows[i]);
            }

            var gap = CreateSpacer(16);
            root.Controls.Add(detailCard);
            root.Controls.Add(gap);
            root.Controls.Add(cardsGrid);
            root.Controls.SetChildIndex(detailCard, 0);
            root.Controls.SetChildIndex(gap, 1);
            root.Controls.SetChildIndex(cardsGrid, 2);
            return root;
        }

        Control CreateTaskbarOffsetStepper(int initialOffset) {
            int current = Math.Max(-1200, Math.Min(0, initialOffset));
            // One connected control: shared rounded frame with inner dividers (each segment paints its own slice of the frame).
            var frame = new Panel { Height = ControlCenterTokens.ButtonHeight, BackColor = palette.Card };
            var panel = new Panel { Height = ControlCenterTokens.ButtonHeight, BackColor = palette.Card };
            ControlCenterButton leftBtn = null, rightBtn = null, valBtn = null, resetBtn = null;

            Action syncUi = delegate {
                if (valBtn != null) {
                    valBtn.Text = current == 0 ? "0 px (默认)" : ("左移 " + Math.Abs(current) + " px");
                    valBtn.AccessibleName = valBtn.Text;
                    valBtn.Invalidate();
                }
                if (leftBtn != null) { leftBtn.Enabled = current > -1200; leftBtn.Invalidate(); }
                if (rightBtn != null) { rightBtn.Enabled = current < 0; rightBtn.Invalidate(); }
                if (resetBtn != null) { resetBtn.Enabled = current != 0; resetBtn.Invalidate(); }
            };
            Action<int> applyOffset = delegate(int next) {
                int clamped = Math.Max(-1200, Math.Min(0, next));
                if (clamped == current) return;
                current = clamped;
                syncUi();
                UpdatePreferenceInPlace(delegate(AudioBindings p) { p.TaskbarOverlay.OffsetX = current; });
            };

            leftBtn = new ControlCenterButton("← 20px", palette, dark, subFont, ControlCenterButtonVariant.Segment);
            valBtn = new ControlCenterButton("0 px (默认)", palette, dark, subFont, ControlCenterButtonVariant.Segment);
            rightBtn = new ControlCenterButton("20px →", palette, dark, subFont, ControlCenterButtonVariant.Segment);
            resetBtn = new ControlCenterButton("重置", palette, dark, subFont, ControlCenterButtonVariant.Quiet);
            leftBtn.Click += delegate { applyOffset(current - 20); };
            rightBtn.Click += delegate { applyOffset(current + 20); };
            resetBtn.Click += delegate { applyOffset(0); };
            valBtn.MakeStatic();
            leftBtn.Width = 74; valBtn.Width = 100; rightBtn.Width = 74; resetBtn.Width = 48;
            foreach (var seg in new[] { leftBtn, valBtn, rightBtn }) { seg.Height = ControlCenterTokens.ButtonHeight; seg.FrameOwner = frame; }
            rightBtn.SegmentLast = true;
            leftBtn.Location = new Point(0, 0);
            valBtn.Location = new Point(leftBtn.Right, 0);
            rightBtn.Location = new Point(valBtn.Right, 0);
            frame.Width = rightBtn.Right;
            frame.Controls.Add(leftBtn);
            frame.Controls.Add(valBtn);
            frame.Controls.Add(rightBtn);
            frame.Location = new Point(0, 0);
            resetBtn.Location = new Point(frame.Right + 6, 0);
            panel.Width = resetBtn.Right;
            panel.Controls.Add(frame);
            panel.Controls.Add(resetBtn);
            syncUi();
            return panel;
        }

        Control CreatePlacementOptionCard(int mode, string title, string desc, string tag, ControlCenterBadgeTone tone) {
            bool active = selectedPlacementCard == mode;
            bool hoveredCard = false;
            var card = new Panel {
                Dock = DockStyle.Fill,
                Margin = new Padding(mode == 0 ? 0 : 6, 0, mode == 2 ? 0 : 6, 0),
                BackColor = palette.WindowBackground,
                Cursor = Cursors.Hand
            };
            card.MouseEnter += delegate { hoveredCard = true; card.Invalidate(); };
            card.MouseLeave += delegate { hoveredCard = false; card.Invalidate(); };
            card.Paint += delegate(object sender, PaintEventArgs e) {
                var g = e.Graphics;
                g.Clear(palette.WindowBackground);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                var r = new RectangleF(1f, 1f, card.Width - 2f, card.Height - 2f);
                Color fill = hoveredCard && !active && !VisualDesign.HighContrast ? palette.RaisedSurface : palette.Card;
                CompactRenderer.Fill(g, r, fill, ControlCenterTokens.RadiusCard);
                if (!VisualDesign.HighContrast && dark && active) {
                    using (var rim = new Pen(Color.FromArgb(24, 255, 255, 255), 1f)) g.DrawLine(rim, r.Left + 10, r.Top + 1, r.Right - 10, r.Top + 1);
                }
                using (var p = VisualDesign.RoundRect(r.X, r.Y, r.Width, r.Height, ControlCenterTokens.RadiusCard))
                using (var pen = new Pen(active ? palette.Foreground : palette.Border, active ? 1.5f : 1f)) g.DrawPath(pen, p);
            };
            var lblT = new Label { Text = title, Font = boldFont, ForeColor = palette.Foreground, BackColor = Color.Transparent, AutoSize = true, Location = new Point(16, 14), Cursor = Cursors.Hand };
            var lblD = new Label { Text = desc, Font = subFont, ForeColor = secondary, BackColor = Color.Transparent, MaximumSize = new Size(216, 42), AutoSize = true, Location = new Point(16, 38), Cursor = Cursors.Hand };
            var badge = new ControlCenterBadge(tag, palette, dark, subFont, tone, false, true) { Location = new Point(16, 86), Cursor = Cursors.Hand, SurfaceColor = palette.Card };
            card.MouseEnter += delegate { badge.SurfaceColor = !active && !VisualDesign.HighContrast ? palette.RaisedSurface : palette.Card; badge.Invalidate(); };
            card.MouseLeave += delegate { badge.SurfaceColor = palette.Card; badge.Invalidate(); };
            EventHandler click = delegate {
                if (selectedPlacementCard != mode) { selectedPlacementCard = mode; RebuildCurrentPage(); }
            };
            card.Click += click; lblT.Click += click; lblD.Click += click; badge.Click += click;
            card.Controls.Add(lblT); card.Controls.Add(lblD); card.Controls.Add(badge);
            return card;
        }

        public void SelectPlacementCard(int mode) {
            int clamped = mode < 0 ? 0 : (mode > 2 ? 2 : mode);
            if (currentPage != ControlCenterPage.Placement) {
                selectedPlacementCard = clamped;
                SelectPage(ControlCenterPage.Placement);
                return;
            }
            if (selectedPlacementCard != clamped) {
                selectedPlacementCard = clamped;
                RebuildCurrentPage(true);
            }
        }

        // ==================== PAGE 4: REMINDERS ====================
        Control BuildRemindersPage() {
            var root = CreatePageRoot("提醒", "配置低电量系统通知与全设备统一的提醒阈值");
            var pref = DeviceSettings.Current;
            var card = CreateCard(new Padding(16, 8, 16, 8));
            card.Dock = DockStyle.Top;

            var lowToggle = new ControlCenterToggle(palette, dark) { Checked = pref.LowBatteryNotifications };
            lowToggle.Toggled += delegate {
                UpdatePreferenceInPlace(delegate(AudioBindings p) { p.LowBatteryNotifications = lowToggle.Checked; });
            };
            var row1 = CreateSettingRow("低电量提醒", "任意外设电量低于阈值时弹出一次系统通知", lowToggle, false);

            int threshIdx = pref.WarningThreshold == 10 ? 0 : pref.WarningThreshold == 15 ? 1 : 2;
            var row2 = CreateSettingRow("提醒阈值", "所有无线外设共用此低电量提醒与警示色门槛", CreateSegmentedChoices(new[] { "10%", "15%", "20%" }, threshIdx, delegate(int idx) {
                UpdatePreferenceInPlace(delegate(AudioBindings p) { p.WarningThreshold = idx == 0 ? 10 : idx == 1 ? 15 : 20; });
            }), true);

            card.Controls.Add(row2);
            card.Controls.Add(row1);
            root.Controls.Add(card);
            root.Controls.SetChildIndex(card, 0);
            return root;
        }

        // ==================== PAGE 5: DIAGNOSTICS & FEEDBACK ====================
        Control BuildDiagnosticsPage() {
            var root = CreatePageRoot("诊断与反馈", "查看读取组件运行健康度、提交设备适配请求或追踪回复");
            var card = CreateCard(new Padding(16, 8, 16, 8));
            card.Dock = DockStyle.Top;

            string healthText = !String.IsNullOrEmpty(owner.GenericError) ? owner.GenericError :
                !String.IsNullOrEmpty(owner.GenericHealthSummary) ? owner.GenericHealthSummary :
                owner.GenericReady ? "读取组件运行正常" : "读取组件初始化中";

            var r1 = CreateSettingRow("读取组件状态", healthText + " · 型号数据包：" + owner.DevicePackDiagnostic, CreateActionButton("重新刷新", delegate {
                owner.RefreshBattery(true);
                ShowNotice("已触发设备与组件刷新");
            }, false, false, "refresh"), false);

            var r2 = CreateSettingRow("反馈问题", "在程序内提交新设备请求或异常，不需要 GitHub 账号", CreateActionButton("写反馈", delegate {
                owner.OpenFeedbackFromHost(this, false, copyText, openLink);
            }, false, true), true);

            var r3 = CreateSettingRow("我的反馈", owner.FeedbackReplies.Store.MenuText, CreateActionButton("查看", delegate {
                owner.OpenMyFeedback();
            }), true);

            var r4 = CreateSettingRow("复制诊断摘要", "已自动脱敏，不含设备序列号与本机路径", CreateActionButton("复制", delegate {
                copyText(owner.DiagnosticSummary());
                ShowNotice("已复制诊断摘要（不含序列号和路径）");
            }), true);

            var r5 = CreateSettingRow("查看兼容设备清单", "在浏览器打开项目支持的全部设备与验证等级说明", CreateActionButton("打开清单", delegate {
                openLink("https://github.com/wushuai0802/myfinder-device-battery/blob/main/docs/compatibility.md");
            }), true);

            card.Controls.Add(r5);
            card.Controls.Add(r4);
            card.Controls.Add(r3);
            card.Controls.Add(r2);
            card.Controls.Add(r1);
            root.Controls.Add(card);
            root.Controls.SetChildIndex(card, 0);
            return root;
        }

        // ==================== PAGE 6: SETTINGS ====================
        Control BuildSettingsPage() {
            var root = CreatePageRoot("设置", "管理后台同步节奏、自动识别与更新策略，以及本机只读开放接口");
            var pref = DeviceSettings.Current;

            // Group 1: General & Devices
            var generalCard = CreateCard(new Padding(16, 8, 16, 8));
            generalCard.Dock = DockStyle.Top;
            var genRows = new List<Control>();

            var probeToggle = new ControlCenterToggle(palette, dark) { Checked = pref.AutoDetectMoreDevices, Enabled = !owner.IsProbeToggleBusy };
            probeToggle.Toggled += delegate {
                if (owner.IsProbeToggleBusy) return;
                bool next = probeToggle.Checked;
                var p = DeviceSettings.Current;
                p.AutoDetectMoreDevices = next;
                try { owner.SavePreferences(p); }
                catch (Exception) { probeToggle.Checked = !next; ShowNotice("设置未保存"); return; }
                owner.ConfigurationChanged(false);
                ShowNotice("正在重启读取组件…");
                owner.RestartGenericForProbeFamilies(next, delegate(bool ok, string notice) {
                    if (IsDisposed) return;
                    ShowNotice(ok ? (next ? "已开启，正在重新识别设备" : "已关闭，正在重新识别设备") : (notice ?? "已保存，读取组件暂未就绪"));
                    RebuildCurrentPage();
                });
            };
            genRows.Add(CreateSettingRow("自动识别更多设备", "实验功能 · 只发送读取电量的查询；更改后将重启读取组件", probeToggle, false));

            var stopped = owner.StoppedFamilyDevices;
            if (stopped.Count > 0) {
                foreach (var item in stopped) {
                    var target = item;
                    bool busy = owner.IsEnablingFamilyKey(target.MemoryKey);
                    var btn = CreateActionButton(busy ? "恢复中…" : "恢复读取", delegate {
                        owner.EnableFamilyDevice(target.MemoryKey, delegate(bool ok, string msg, bool empty) {
                            if (!IsDisposed) { ShowNotice(msg); RebuildCurrentPage(); }
                        });
                    });
                    btn.Enabled = !owner.IsFamilyOpBusy;
                    genRows.Add(CreateSettingRow("已停止读取：" + target.DisplayName, "点击右侧按钮恢复对此设备的电量查询", btn, true));
                }
            }

            int bgIdx = pref.BackgroundSeconds == 60 ? 0 : pref.BackgroundSeconds == 120 ? 1 : 2;
            genRows.Add(CreateSettingRow("后台同步间隔", "托盘面板收起时的后台轮询频率", CreateSegmentedChoices(new[] { "1 分钟", "2 分钟", "5 分钟" }, bgIdx, delegate(int idx) {
                UpdatePreferenceInPlace(delegate(AudioBindings p) { p.BackgroundSeconds = idx == 0 ? 60 : idx == 1 ? 120 : 300; });
            }), true));

            var rememberToggle = new ControlCenterToggle(palette, dark) { Checked = pref.RememberReadings };
            rememberToggle.Toggled += delegate {
                UpdatePreferenceInPlace(delegate(AudioBindings p) { p.RememberReadings = rememberToggle.Checked; });
            };
            genRows.Add(CreateSettingRow("跨重启保留读数", "关闭保留不删除已有历史，需通过下方按钮单独清除", rememberToggle, true));

            genRows.Add(CreateSettingRow("清除历史读数", "清空本地缓存的上次电量记录", CreateActionButton("清除", delegate {
                owner.ClearReadingHistory();
                ShowNotice("正在清除历史读数…");
            }), true));
            for (int i = genRows.Count - 1; i >= 0; i--) generalCard.Controls.Add(genRows[i]);

            // Group 2: Updates & Local API
            var extCard = CreateCard(new Padding(16, 8, 16, 8));
            extCard.Dock = DockStyle.Top;
            var extRows = new List<Control>();

            var autoUpdate = new ControlCenterToggle(palette, dark) { Checked = pref.AutoCheckUpdates };
            autoUpdate.Toggled += delegate { UpdatePreferenceInPlace(delegate(AudioBindings p) { p.AutoCheckUpdates = autoUpdate.Checked; }); };
            extRows.Add(CreateSettingRow("自动检查更新", "定期检查 GitHub 签名版本更新", autoUpdate, false));

            var badgeToggle = new ControlCenterToggle(palette, dark) { Checked = pref.ShowUpdateBadge };
            badgeToggle.Toggled += delegate { UpdatePreferenceInPlace(delegate(AudioBindings p) { p.ShowUpdateBadge = badgeToggle.Checked; }); };
            extRows.Add(CreateSettingRow("托盘图标显示更新角标", null, badgeToggle, true));

            var betaToggle = new ControlCenterToggle(palette, dark) { Checked = pref.IncludeBetaUpdates };
            betaToggle.Toggled += delegate { UpdatePreferenceInPlace(delegate(AudioBindings p) { p.IncludeBetaUpdates = betaToggle.Checked; }); };
            extRows.Add(CreateSettingRow("提醒测试版更新", "接收 1.4 测试版新设备与功能更新通知", betaToggle, true));

            var replyToggle = new ControlCenterToggle(palette, dark) { Checked = pref.CheckFeedbackReplies };
            replyToggle.Toggled += delegate { UpdatePreferenceInPlace(delegate(AudioBindings p) { p.CheckFeedbackReplies = replyToggle.Checked; }); };
            extRows.Add(CreateSettingRow("自动检查反馈回复", null, replyToggle, true));

            var apiToggle = new ControlCenterToggle(palette, dark) { Checked = pref.LocalApi != null && pref.LocalApi.Enabled };
            apiToggle.Toggled += delegate { UpdatePreference(delegate(AudioBindings p) { p.LocalApi.Enabled = apiToggle.Checked; }); };
            string apiSub = owner.LocalApiRunning
                ? "已监听 127.0.0.1:" + owner.LocalApiPort + "（仅本机只读 JSON-RPC 2.0 / SSE，凭据仅当前用户可读）"
                : !String.IsNullOrEmpty(owner.LocalApiError) ? "启动失败：" + owner.LocalApiError : "默认关闭 · 开启后供 MyDockFinder / Rainmeter / OBS 等读取电量";
            extRows.Add(CreateSettingRow("本机只读开放接口（JSON-RPC 2.0）", apiSub, apiToggle, true));

            if (owner.LocalApiRunning) {
                extRows.Add(CreateSettingRow("接口连接信息", "凭据文件仅当前 Windows 用户可读，不含写入权限", CreateActionButton("复制文件路径", delegate {
                    copyText(owner.LocalApiSessionPath);
                    ShowNotice("已复制 api-session.json 路径（令牌请从受保护文件读取）");
                }), true));
            }
            for (int i = extRows.Count - 1; i >= 0; i--) extCard.Controls.Add(extRows[i]);

            var h1 = CreateSectionHeading("设备与后台同步", 2);
            var h2 = CreateSectionHeading("更新与开放接口", 16);

            // DockStyle.Top stacks bottom-to-top in child order
            root.Controls.Add(extCard);
            root.Controls.Add(h2);
            root.Controls.Add(generalCard);
            root.Controls.Add(h1);
            root.Controls.SetChildIndex(extCard, 0);
            root.Controls.SetChildIndex(h2, 1);
            root.Controls.SetChildIndex(generalCard, 2);
            root.Controls.SetChildIndex(h1, 3);
            return root;
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                noticeTimer.Dispose();
                if (titleFont != null) titleFont.Dispose();
                if (headingFont != null) headingFont.Dispose();
                if (bodyFont != null) bodyFont.Dispose();
                if (subFont != null) subFont.Dispose();
                if (boldFont != null) boldFont.Dispose();
                if (subBoldFont != null) subBoldFont.Dispose();
                if (ownsWindowIcon && windowIcon != null) windowIcon.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
