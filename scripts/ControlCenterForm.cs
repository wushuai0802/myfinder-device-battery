using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AtkBattery;

namespace AtkBatteryTray {
    public enum ControlCenterPage { Devices, Appearance, Placement, Reminders, Diagnostics, Settings }

    internal enum DeviceBatteryCapability { Supported, NoBatteryPlaceholder }

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
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DialogPaint.Background(this, e, BackColor);
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

    internal sealed class ControlCenterRail : Control {
        sealed class RailItem {
            internal ControlCenterPage Page;
            internal string Label;
            internal string Icon;
            internal bool Bottom;
            internal Rectangle Bounds;
        }

        readonly List<RailItem> items = new List<RailItem>();
        readonly ToolTip tip = new ToolTip { InitialDelay = 250, ReshowDelay = 100 };
        ThemePalette palette;
        bool dark;
        ControlCenterPage selected = ControlCenterPage.Devices;
        int hovered = -1;

        internal event EventHandler<ControlCenterPage> PageSelected;

        internal ControlCenterRail(ThemePalette palette, bool dark) {
            this.palette = palette;
            this.dark = dark;
            TabStop = true;
            Width = 60;
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

        internal void ApplyTheme(ThemePalette next, bool isDark) {
            palette = next;
            dark = isDark;
            BackColor = palette.RailBackground;
            Invalidate();
        }

        void LayoutItems() {
            int btnSize = Math.Max(36, Math.Min(44, Width - 16));
            int x = (Width - btnSize) / 2;
            int yTop = 12;
            int gap = 6;
            for (int i = 0; i < items.Count; i++) {
                if (!items[i].Bottom) {
                    items[i].Bounds = new Rectangle(x, yTop, btnSize, btnSize);
                    yTop += btnSize + gap;
                }
            }
            int bottomCount = 0;
            for (int i = 0; i < items.Count; i++) if (items[i].Bottom) bottomCount++;
            int yBot = Math.Max(yTop + 12, Height - 12 - bottomCount * btnSize - Math.Max(0, bottomCount - 1) * gap);
            for (int i = 0; i < items.Count; i++) {
                if (items[i].Bottom) {
                    items[i].Bounds = new Rectangle(x, yBot, btnSize, btnSize);
                    yBot += btnSize + gap;
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
                tip.SetToolTip(this, hovered >= 0 ? items[hovered].Label : null);
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) {
            hovered = -1;
            Cursor = Cursors.Default;
            tip.SetToolTip(this, null);
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
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DialogPaint.Background(this, e, palette.RailBackground);
            using (var pen = new Pen(palette.Border, 1)) g.DrawLine(pen, Width - 1, 0, Width - 1, Height);
            for (int i = 0; i < items.Count; i++) {
                var item = items[i];
                bool isSel = item.Page == selected;
                bool isHov = i == hovered;
                var rect = new RectangleF(item.Bounds.X, item.Bounds.Y, item.Bounds.Width, item.Bounds.Height);
                if (isSel || isHov) {
                    Color fill = VisualDesign.HighContrast && isSel ? palette.Selected : palette.RaisedSurface;
                    CompactRenderer.Fill(g, rect, fill, 8);
                }
                if (isSel) {
                    var bar = new RectangleF(0, rect.Y + (rect.Height - 16) / 2f, 3, 16);
                    CompactRenderer.Fill(g, bar, VisualDesign.HighContrast ? palette.Selected : palette.Foreground, 1.5f);
                }
                if (isSel && Focused && ShowFocusCues) {
                    CompactRenderer.Outline(g, rect, palette.Accent, 1);
                }
                Color iconColor = isSel ? (VisualDesign.HighContrast ? palette.SelectedForeground : palette.Foreground) : palette.Muted;
                DrawRailIcon(g, item.Icon, new RectangleF(rect.X + (rect.Width - 18) / 2f, rect.Y + (rect.Height - 18) / 2f, 18, 18), iconColor);
            }
        }

        static void DrawRailIcon(Graphics g, string kind, RectangleF r, Color color) {
            using (var pen = new Pen(color, 1.6f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var brush = new SolidBrush(color)) {
                float x = r.X, y = r.Y, w = r.Width, h = r.Height;
                if (kind == "devices") {
                    using (var p = VisualDesign.RoundRect(x + 1, y + 2, w - 2, h - 7, 2)) g.DrawPath(pen, p);
                    g.DrawLine(pen, x + w * 0.5f, y + h - 5, x + w * 0.5f, y + h - 2);
                    g.DrawLine(pen, x + 5, y + h - 2, x + w - 5, y + h - 2);
                } else if (kind == "look") {
                    g.DrawEllipse(pen, x + 1.5f, y + 1.5f, w - 3, h - 3);
                    g.FillEllipse(brush, x + 5, y + 7, 2.6f, 2.6f);
                    g.FillEllipse(brush, x + 8.5f, y + 4.5f, 2.6f, 2.6f);
                    g.FillEllipse(brush, x + 11.5f, y + 8, 2.6f, 2.6f);
                } else if (kind == "place") {
                    using (var p = VisualDesign.RoundRect(x + 1, y + 2, w - 2, h - 4, 2)) g.DrawPath(pen, p);
                    g.DrawLine(pen, x + 1, y + h - 6, x + w - 1, y + h - 6);
                    g.DrawLine(pen, x + w - 7, y + h - 4, x + w - 3, y + h - 4);
                } else if (kind == "remind") {
                    g.DrawArc(pen, x + 4, y + 2.5f, w - 8, 9, 180, 180);
                    g.DrawLine(pen, x + 4, y + 7, x + 4, y + 12.5f);
                    g.DrawLine(pen, x + w - 4, y + 7, x + w - 4, y + 12.5f);
                    g.DrawLine(pen, x + 2.5f, y + 13.5f, x + w - 2.5f, y + 13.5f);
                    g.DrawArc(pen, x + w * 0.5f - 2, y + 13.5f, 4, 3, 0, 180);
                } else if (kind == "diag") {
                    g.DrawLines(pen, new[] {
                        new PointF(x + 1, y + h * 0.55f),
                        new PointF(x + 4.5f, y + h * 0.55f),
                        new PointF(x + 7, y + 4),
                        new PointF(x + 10.5f, y + h - 4),
                        new PointF(x + 13.5f, y + h * 0.55f),
                        new PointF(x + w - 1, y + h * 0.55f)
                    });
                } else {
                    g.DrawEllipse(pen, x + 6, y + 6, w - 12, h - 12);
                    g.DrawEllipse(pen, x + 2.5f, y + 2.5f, w - 5, h - 5);
                }
            }
        }

        protected override void Dispose(bool disposing) {
            if (disposing) tip.Dispose();
            base.Dispose(disposing);
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
            Size = new Size(96, 96);
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
            DialogPaint.Background(this, e, BackColor);
            float pad = 7f;
            var ring = new RectangleF(pad, pad, Math.Max(20, Width - pad * 2), Math.Max(20, Height - pad * 2));
            using (var trackPen = new Pen(VisualDesign.HighContrast ? palette.Foreground : palette.Track, VisualDesign.HighContrast ? 2f : 7f)) {
                g.DrawEllipse(trackPen, ring);
            }
            Color accent = percent.HasValue && fresh ? ThemePalette.LevelColor(percent, dark) : palette.Muted;
            if (percent.HasValue) {
                float sweep = 360f * Math.Max(0, Math.Min(100, percent.Value)) / 100f;
                if (sweep > 0.5f) {
                    using (var arcPen = new Pen(accent, 7f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
                        g.DrawArc(arcPen, ring, -90f, sweep);
                    }
                }
            }
            string text = percent.HasValue ? percent.Value.ToString() : "—";
            float digitSize = percent.HasValue ? (VisualDesign.LargeFont ? 26f : 24f) : 18f;
            using (var font = new Font("Microsoft YaHei UI", digitSize, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var pctFont = new Font("Microsoft YaHei UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel))
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
            Size = new Size(92, 74);
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
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            DialogPaint.Background(this, e, BackColor);
            var card = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Color fill = hovered && !VisualDesign.HighContrast ? hostPalette.RaisedSurface : hostPalette.Card;
            CompactRenderer.Fill(g, card, fill, 8);
            Color border = selected ? hostPalette.Foreground : hostPalette.Border;
            using (var path = VisualDesign.RoundRect(card.X, card.Y, card.Width, card.Height, 8))
            using (var pen = new Pen(border, selected ? 1.5f : 1f)) g.DrawPath(pen, path);

            var preview = ThemePalette.For(styleKey, dark);
            var chips = new RectangleF(8, 8, Width - 16, 34);
            if (!VisualDesign.HighContrast) {
                float third = chips.Width / 3f;
                using (var clip = VisualDesign.RoundRect(chips.X, chips.Y, chips.Width, chips.Height, 5)) {
                    var state = g.Save();
                    g.SetClip(clip);
                    using (var b1 = new SolidBrush(preview.WindowBackground)) g.FillRectangle(b1, chips.X, chips.Y, third + 1, chips.Height);
                    using (var b2 = new SolidBrush(preview.Track)) g.FillRectangle(b2, chips.X + third, chips.Y, third + 1, chips.Height);
                    Color c3 = styleKey == "graphite" ? ThemePalette.LevelColorFor("graphite", 90, dark) : preview.Accent;
                    using (var b3 = new SolidBrush(c3)) g.FillRectangle(b3, chips.X + third * 2, chips.Y, third + 2, chips.Height);
                    g.Restore(state);
                }
            } else {
                using (var pen = new Pen(hostPalette.Foreground, 1))
                using (var path = VisualDesign.RoundRect(chips.X, chips.Y, chips.Width, chips.Height, 5)) g.DrawPath(pen, path);
            }
            string caption = isNew ? styleName + " · 新" : styleName;
            CompactRenderer.DrawLabel(g, caption, new RectangleF(8, 46, Width - 16, 20), 12f, selected, selected ? hostPalette.Foreground : hostPalette.Muted, StringAlignment.Near);
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
            Size = new Size(310, 340);
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
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            DialogPaint.Background(this, e, palette.WindowBackground);
            var layout = WirelessLayout.Build(owner, 0, Math.Max(220, Height - 24));
            var size = layout.Size;
            float availW = Math.Max(100, Width - 16);
            float availH = Math.Max(100, Height - 16);
            float scale = Math.Min(1f, Math.Min(availW / Math.Max(1, size.Width), availH / Math.Max(1, size.Height)));
            float drawW = size.Width * scale;
            float drawH = size.Height * scale;
            float ox = Math.Max(8, (Width - drawW) / 2f);
            float oy = 8f;
            var frame = new RectangleF(ox - 4, oy - 4, drawW + 8, drawH + 8);
            CompactRenderer.Fill(g, frame, palette.WindowBackground, 10);
            using (var border = VisualDesign.RoundRect(frame.X, frame.Y, frame.Width, frame.Height, 10))
            using (var pen = new Pen(palette.Border, 1)) g.DrawPath(pen, border);
            var state = g.Save();
            g.TranslateTransform(ox, oy);
            g.ScaleTransform(scale, scale);
            try {
                CompactRenderer.PaintWireless(g, owner, layout, dark, new PanelFeedback(), 1f);
            } catch (Exception) {
                CompactRenderer.DrawLabel(g, "实时预览暂不可用", new RectangleF(0, 0, size.Width, 40), 12f, false, palette.Muted, StringAlignment.Center);
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
            Size = new Size(600, 152);
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
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            DialogPaint.Background(this, e, palette.Card);
            var box = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using (var clip = VisualDesign.RoundRect(box.X, box.Y, box.Width, box.Height, 10)) {
                var state = g.Save();
                g.SetClip(clip);
                Color top = dark ? Color.FromArgb(46, 58, 72) : Color.FromArgb(207, 217, 228);
                Color bottom = dark ? Color.FromArgb(27, 34, 42) : Color.FromArgb(182, 197, 212);
                using (var bg = new LinearGradientBrush(box, top, bottom, 145f)) g.FillRectangle(bg, box);

                // Desktop widget illustration (top-right edge capsule)
                if (selectedMode == 1) {
                    var pill = new RectangleF(Width - 186, 18, 166, 38);
                    CompactRenderer.Fill(g, pill, palette.Card, 19);
                    using (var pp = VisualDesign.RoundRect(pill.X, pill.Y, pill.Width, pill.Height, 19))
                    using (var pen = new Pen(palette.Border, 1)) g.DrawPath(pen, pp);
                    CompactRenderer.DrawLabel(g, "鼠标 100%  ·  耳机 39%", pill, 12f, true, palette.Foreground, StringAlignment.Center);
                }

                // Taskbar strip
                var tb = new RectangleF(0, Height - 40, Width, 40);
                using (var tbBrush = new SolidBrush(dark ? Color.FromArgb(236, 28, 28, 28) : Color.FromArgb(242, 243, 243, 243))) {
                    g.FillRectangle(tbBrush, tb);
                }
                Color tbFg = dark ? Color.FromArgb(242, 242, 242) : Color.FromArgb(26, 26, 26);
                CompactRenderer.DrawLabel(g, "11:58\n2026/11/25", new RectangleF(Width - 82, Height - 38, 72, 36), 11f, false, tbFg, StringAlignment.Far);

                if (selectedMode == 2) {
                    var bat = new RectangleF(Width - 246, Height - 32, 144, 24);
                    CompactRenderer.Fill(g, bat, dark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(16, 0, 0, 0), 5);
                    CompactRenderer.DrawLabel(g, "鼠 100%   耳 39%", bat, 11.5f, true, tbFg, StringAlignment.Center);
                } else if (selectedMode == 0) {
                    var badge = new RectangleF(Width - 122, Height - 30, 24, 20);
                    CompactRenderer.Fill(g, badge, ThemePalette.LevelColor(85, dark), 4);
                    CompactRenderer.DrawLabel(g, "85", badge, 11f, true, Color.White, StringAlignment.Center);
                }
                g.Restore(state);
            }
            using (var pen = new Pen(palette.Border, 1))
            using (var path = VisualDesign.RoundRect(box.X, box.Y, box.Width, box.Height, 10)) g.DrawPath(pen, path);
        }
    }

    internal sealed class ControlCenterSegments : Control {
        readonly string[] labels;
        readonly int selectedIndex;
        readonly Action<int> onSelect;
        readonly ThemePalette palette;
        readonly Font font, boldFont;
        readonly RectangleF[] rects;
        int hovered = -1;

        internal ControlCenterSegments(string[] labels, int selectedIndex, Action<int> onSelect, ThemePalette palette, Font font, Font boldFont) {
            this.labels = labels;
            this.selectedIndex = selectedIndex;
            this.onSelect = onSelect;
            this.palette = palette;
            this.font = font;
            this.boldFont = boldFont;
            this.rects = new RectangleF[labels.Length];
            TabStop = true;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Opaque | ControlStyles.SupportsTransparentBackColor, false);
            BackColor = palette.Card;

            float x = 3f;
            int h = VisualDesign.LargeFont ? 34 : 30;
            using (var bmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(bmp)) {
                for (int i = 0; i < labels.Length; i++) {
                    var sz = TextRenderer.MeasureText(labels[i], boldFont, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                    float w = Math.Max(42f, sz.Width + 20f);
                    rects[i] = new RectangleF(x, 3f, w, h - 6f);
                    x += w + 2f;
                }
            }
            Size = new Size((int)Math.Ceiling(x + 1f), h);
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
                if (idx >= 0 && onSelect != null) onSelect(idx);
            }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            DialogPaint.Background(this, e, BackColor);
            var outer = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
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

        ThemePalette palette;
        DialogTheme dialogTheme;
        bool dark;
        ControlCenterPage currentPage;
        string selectedDeviceKey;
        int selectedPlacementCard;
        bool loadingBounds, closing, rebuilding, deferredDataRefresh;
        string lastDataSignature;

        Font titleFont, headingFont, bodyFont, subFont, boldFont;

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
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);

            LoadTheme();
            RebuildFonts();
            MinimumSize = new Size(820, 560);
            ApplyInitialBounds();

            rail = new ControlCenterRail(palette, dark);
            rail.SelectedPage = currentPage;
            rail.PageSelected += delegate(object sender, ControlCenterPage page) { SelectPage(page); };

            noticeBar = new Panel { Dock = DockStyle.Bottom, Height = 30, Visible = false, Padding = new Padding(24, 5, 24, 5) };
            noticeLabel = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            noticeBar.Controls.Add(noticeLabel);
            noticeTimer.Tick += delegate { noticeTimer.Stop(); if (!IsDisposed) noticeBar.Visible = false; };

            pageHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(28, 22, 28, 22) };
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
        }

        void RebuildFonts() {
            if (titleFont != null) titleFont.Dispose();
            if (headingFont != null) headingFont.Dispose();
            if (bodyFont != null) bodyFont.Dispose();
            if (subFont != null) subFont.Dispose();
            if (boldFont != null) boldFont.Dispose();
            float scale = VisualDesign.LargeFont ? 1.12f : 1f;
            titleFont = new Font("Microsoft YaHei UI", 22f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            headingFont = new Font("Microsoft YaHei UI", 16f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            bodyFont = new Font("Microsoft YaHei UI", 13.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            boldFont = new Font("Microsoft YaHei UI", 13.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            subFont = new Font("Microsoft YaHei UI", 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            Font = bodyFont;
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
            if (IsHandleCreated) ThemedTitleBar.Apply(Handle, dialogTheme);
        }

        void ApplyInitialBounds() {
            loadingBounds = true;
            try {
                var area = ThemedDialog.WorkingAreaProvider(this);
                if (area.Width <= 0 || area.Height <= 0) area = Screen.PrimaryScreen.WorkingArea;
                var saved = DeviceSettings.Current.ControlCenterBounds;
                int w = saved != null && saved.Width >= 820 ? saved.Width : 1000;
                int h = saved != null && saved.Height >= 560 ? saved.Height : 660;
                w = Math.Min(w, Math.Max(MinimumSize.Width, area.Width));
                h = Math.Min(h, Math.Max(MinimumSize.Height, area.Height));
                int x = saved != null && saved.HasPosition ? saved.X : area.Left + (area.Width - w) / 2;
                int y = saved != null && saved.HasPosition ? saved.Y : area.Top + (area.Height - h) / 2;
                var clamped = ClampToWorkArea(new Rectangle(x, y, w, h), area, MinimumSize);
                Bounds = clamped;
            } finally {
                loadingBounds = false;
            }
        }

        internal static Rectangle ClampToWorkArea(Rectangle bounds, Rectangle workArea, Size minSize) {
            if (workArea.Width <= 0 || workArea.Height <= 0) return bounds;
            int maxW = Math.Max(1, workArea.Width);
            int maxH = Math.Max(1, workArea.Height);
            int w = Math.Min(Math.Max(bounds.Width, Math.Min(minSize.Width, maxW)), maxW);
            int h = Math.Min(Math.Max(bounds.Height, Math.Min(minSize.Height, maxH)), maxH);
            int x = Math.Max(workArea.Left, Math.Min(bounds.X, workArea.Right - w));
            int y = Math.Max(workArea.Top, Math.Min(bounds.Y, workArea.Bottom - h));
            return new Rectangle(x, y, w, h);
        }

        void PersistBounds() {
            if (loadingBounds || closing || IsDisposed || WindowState != FormWindowState.Normal) return;
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
            ThemedTitleBar.Apply(Handle, dialogTheme);
        }

        protected override void WndProc(ref Message m) {
            base.WndProc(ref m);
            if (m.Msg == 0x0232) { // WM_EXITSIZEMOVE
                PersistBounds();
            }
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
            RebuildCurrentPage();
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
            sb.Append(pref.ReducedMotion).Append('|').Append(pref.TrayIconStyle).Append('|').Append(pref.TraySource).Append('|').Append(pref.TrayDeviceId ?? "").Append('|');
            sb.Append(pref.LowBatteryNotifications).Append('|').Append(pref.WarningThreshold).Append('|').Append(pref.BackgroundSeconds).Append('|');
            sb.Append(pref.RememberReadings).Append('|').Append(pref.AutoDetectMoreDevices).Append('|').Append(pref.AutoCheckUpdates).Append('|');
            sb.Append(pref.ShowUpdateBadge).Append('|').Append(pref.IncludeBetaUpdates).Append('|').Append(pref.CheckFeedbackReplies).Append('|');
            if (pref.DesktopWidget != null) {
                var w = pref.DesktopWidget;
                sb.Append(w.Enabled).Append('|').Append(w.Style).Append('|').Append(w.DockEdge).Append('|').Append(w.AutoHideOnEdge).Append('|').Append(w.TopMost).Append('|').Append(w.FullscreenPolicy).Append('|').Append(w.CompactOnMaximized).Append('|').Append(w.MaxDevices).Append('|');
            }
            sb.Append(pref.LocalApi != null && pref.LocalApi.Enabled).Append('|').Append(owner.LocalApiRunning).Append('|').Append(owner.LocalApiPort).Append('|');
            sb.Append(owner.StoppedFamilyDeviceCount).Append('|').Append(owner.IsProbeToggleBusy).Append('|').Append(owner.IsFamilyOpBusy).Append('|');
            sb.Append(owner.GenericReady).Append('|').Append(owner.GenericError ?? "").Append('|').Append(owner.GenericHealthSummary ?? "").Append('|');
            sb.Append(owner.Current != null ? owner.Current.Percent.ToString() : "-").Append('|');
            if (owner.WirelessDevices != null) {
                foreach (var d in owner.WirelessDevices) if (d != null) sb.Append(d.Key).Append(';');
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
                    Kind = card.Kind ?? "device",
                    Capability = isNoBat ? DeviceBatteryCapability.NoBatteryPlaceholder : DeviceBatteryCapability.Supported,
                    Battery = card.Battery,
                    Card = card,
                    IsNativeMouse = card.NativeMouse,
                    IsNativeHeadset = card.NativeHeadset,
                    Hidden = card.Hidden,
                    CanRename = !card.AudioOnly && (card.NativeMouse || card.NativeHeadset || card.Device != null),
                    CanHide = !card.AudioOnly,
                    CanPinTray = !card.AudioOnly && (card.NativeMouse || card.NativeHeadset || card.Device != null),
                    PinnedTray = pref.TrayDeviceId == card.Id
                };
                if (card.AudioOnly) {
                    item.Transport = "仅音频控制";
                    item.StateText = card.Subtitle ?? "已连接";
                    item.NoBatteryReason = "无电量读取";
                    item.VerificationText = "系统音频端点";
                } else if (card.NativeMouse) {
                    item.Transport = "2.4 GHz 接收器";
                    item.StateText = card.Battery != null && card.Battery.Fresh ? "在线 · " + card.Battery.TimeText : (card.Battery != null && card.Battery.Percent.HasValue ? "上次确认 " + card.Battery.TimeText : "暂未取得读数");
                    item.VerificationText = "本项目已实机验证（A 级）";
                    if (owner.Dpi != null && owner.Dpi.Stages != null && owner.Dpi.Stages.Count > 0) {
                        item.HasWritableDpi = true;
                        item.DpiStages = new int[owner.Dpi.Stages.Count];
                        for (int i = 0; i < owner.Dpi.Stages.Count; i++) item.DpiStages[i] = owner.Dpi.Stages[i].XDpi;
                        item.ActiveDpiIndex = owner.Dpi.ActiveStage;
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

        void RebuildCurrentPage() {
            if (IsDisposed || closing || rebuilding) return;
            rebuilding = true;
            deferredDataRefresh = false;
            lastDataSignature = ComputeDataSignature();
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
                rebuilding = false;
            }
        }

        Panel CreatePageRoot(string title, Control headerRight = null) {
            var root = new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = palette.WindowBackground };
            var head = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = palette.WindowBackground, Margin = new Padding(0, 0, 0, 12) };
            var lbl = new Label { Text = title, Font = titleFont, ForeColor = palette.Foreground, BackColor = palette.WindowBackground, AutoSize = true, Location = new Point(0, 4) };
            head.Controls.Add(lbl);
            if (headerRight != null) {
                headerRight.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                head.Controls.Add(headerRight);
                head.Resize += delegate { headerRight.Location = new Point(Math.Max(0, head.ClientSize.Width - headerRight.Width), 4); };
                headerRight.Location = new Point(Math.Max(0, head.ClientSize.Width - headerRight.Width), 4);
            }
            root.Controls.Add(head);
            return root;
        }

        Panel CreateCard(Padding pad) {
            var card = new Panel { BackColor = palette.Card, ForeColor = palette.Foreground, Padding = pad, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            card.Paint += delegate(object sender, PaintEventArgs e) {
                var p = (Panel)sender;
                using (var pen = new Pen(palette.Border, 1)) e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
            };
            return card;
        }

        Panel CreateSettingRow(string title, string subtitle, Control rightControl, bool topBorder) {
            var row = new Panel { Dock = DockStyle.Top, Height = String.IsNullOrEmpty(subtitle) ? 50 : 62, BackColor = palette.Card, Padding = new Padding(0, 8, 0, 8) };
            if (topBorder) {
                row.Paint += delegate(object sender, PaintEventArgs e) {
                    using (var pen = new Pen(palette.Border, 1)) e.Graphics.DrawLine(pen, 0, 0, row.Width, 0);
                };
            }
            var titleLbl = new Label { Text = title, Font = bodyFont, ForeColor = palette.Foreground, BackColor = palette.Card, AutoSize = true, Location = new Point(0, String.IsNullOrEmpty(subtitle) ? 15 : 10) };
            row.Controls.Add(titleLbl);
            if (!String.IsNullOrEmpty(subtitle)) {
                var subLbl = new Label { Text = subtitle, Font = subFont, ForeColor = palette.Muted, BackColor = palette.Card, AutoSize = true, Location = new Point(0, 32) };
                row.Controls.Add(subLbl);
            }
            if (rightControl != null) {
                rightControl.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                row.Controls.Add(rightControl);
                Action position = delegate {
                    rightControl.Location = new Point(Math.Max(180, row.ClientSize.Width - rightControl.Width), (row.Height - rightControl.Height) / 2);
                };
                row.Resize += delegate { position(); };
                position();
            }
            return row;
        }

        Control CreateSegmentedChoices(string[] labels, int selectedIndex, Action<int> onSelect) {
            return new ControlCenterSegments(labels, selectedIndex, onSelect, palette, subFont, boldFont);
        }

        Button CreateActionButton(string text, EventHandler onClick, bool danger = false) {
            var btn = new Button {
                Text = text,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                Font = subFont,
                BackColor = danger ? palette.Card : palette.RaisedSurface,
                ForeColor = danger ? ThemePalette.LevelColor(10, dark) : palette.Foreground,
                Padding = new Padding(12, 5, 12, 5),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = danger ? 0 : 1;
            btn.FlatAppearance.BorderColor = palette.Border;
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

        // ==================== PAGE 1: DEVICES ====================
        Control BuildDevicesPage() {
            var refreshBtn = CreateActionButton("刷新电量", delegate {
                owner.RefreshBattery(true);
                ShowNotice("正在刷新全部设备电量…");
            });
            var root = CreatePageRoot("设备", refreshBtn);

            var items = BuildDeviceItems();
            if (items.Count == 0) {
                var emptyCard = CreateCard(new Padding(24));
                emptyCard.Dock = DockStyle.Top;
                emptyCard.Controls.Add(new Label {
                    Text = "当前尚未检测到外设。连接无线接收器、蓝牙或有线设备后，点右上角“刷新电量”即可显示。",
                    Font = bodyFont,
                    ForeColor = palette.Muted,
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
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            split.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // Left list grouped by capability
            var left = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = palette.WindowBackground, Padding = new Padding(0, 0, 16, 0) };
            var supported = items.FindAll(delegate(ControlCenterDeviceItem it) { return it.Capability == DeviceBatteryCapability.Supported; });
            var noBat = items.FindAll(delegate(ControlCenterDeviceItem it) { return it.Capability == DeviceBatteryCapability.NoBatteryPlaceholder; });

            if (supported.Count > 0) {
                left.Controls.Add(CreateGroupHeading("在线 / 已识别设备"));
                foreach (var it in supported) left.Controls.Add(CreateDeviceListButton(it, it.Key == selected.Key));
            }
            if (noBat.Count > 0) {
                left.Controls.Add(CreateGroupHeading("读不到电量"));
                foreach (var it in noBat) left.Controls.Add(CreateDeviceListButton(it, it.Key == selected.Key));
            }

            // Right detail panel
            var right = BuildDeviceDetailCard(selected);
            right.Dock = DockStyle.Top;

            split.Controls.Add(left, 0, 0);
            split.Controls.Add(right, 1, 0);
            root.Controls.Add(split);
            root.Controls.SetChildIndex(split, 0);
            return root;
        }

        Label CreateGroupHeading(string text) {
            return new Label {
                Text = text,
                Font = subFont,
                ForeColor = palette.Muted,
                BackColor = palette.WindowBackground,
                AutoSize = true,
                Margin = new Padding(4, 6, 0, 6)
            };
        }

        Control CreateDeviceListButton(ControlCenterDeviceItem item, bool isSelected) {
            var row = new Panel {
                Width = 300,
                Height = 58,
                BackColor = isSelected ? palette.Card : palette.WindowBackground,
                Margin = new Padding(0, 0, 0, 4),
                Cursor = Cursors.Hand
            };
            row.Paint += delegate(object sender, PaintEventArgs e) {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var rect = new RectangleF(0.5f, 0.5f, row.Width - 1, row.Height - 1);
                if (isSelected) {
                    CompactRenderer.Fill(g, rect, palette.Card, 8);
                    using (var path = VisualDesign.RoundRect(rect.X, rect.Y, rect.Width, rect.Height, 8))
                    using (var pen = new Pen(palette.Border, 1)) g.DrawPath(pen, path);
                }
                var tile = new RectangleF(10, 11, 36, 36);
                CompactRenderer.Fill(g, tile, palette.RaisedSurface, 8);
                string kindGlyph = item.Kind == "mouse" ? "鼠" : item.Kind == "headset" ? "耳" : item.Kind == "keyboard" ? "键" : item.Kind == "gamepad" ? "柄" : "设";
                CompactRenderer.DrawLabel(g, kindGlyph, tile, 14f, true, palette.Foreground, StringAlignment.Center);

                CompactRenderer.DrawLabel(g, item.Name, new RectangleF(56, 10, 152, 20), 13.5f, true, palette.Foreground, StringAlignment.Near);
                CompactRenderer.DrawLabel(g, item.Transport, new RectangleF(56, 31, 152, 18), 11.5f, false, palette.Muted, StringAlignment.Near);

                if (item.Capability == DeviceBatteryCapability.NoBatteryPlaceholder) {
                    CompactRenderer.DrawLabel(g, item.NoBatteryReason ?? "无电量", new RectangleF(208, 18, 82, 20), 11.5f, false, palette.Muted, StringAlignment.Far);
                } else if (item.Battery != null && item.Battery.Percent.HasValue) {
                    Color level = item.Battery.Fresh ? ThemePalette.LevelColor(item.Battery.Percent, dark) : palette.Muted;
                    CompactRenderer.DrawLabel(g, item.Battery.Percent.Value + "%", new RectangleF(214, 10, 76, 22), 16f, true, level, StringAlignment.Far);
                    var bar = new RectangleF(226, 38, 64, 3);
                    CompactRenderer.Fill(g, bar, palette.Track, 1.5f);
                    float fw = bar.Width * Math.Max(0, Math.Min(100, item.Battery.Percent.Value)) / 100f;
                    if (fw > 0) CompactRenderer.Fill(g, new RectangleF(bar.X, bar.Y, fw, bar.Height), level, 1.5f);
                } else {
                    CompactRenderer.DrawLabel(g, "—", new RectangleF(214, 16, 76, 22), 14f, false, palette.Muted, StringAlignment.Far);
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
            var card = CreateCard(new Padding(22, 18, 22, 18));

            var hero = new Panel { Dock = DockStyle.Top, Height = 108, BackColor = palette.Card };
            var ring = new ControlCenterHeroRing(palette, dark) { Location = new Point(0, 4) };
            ring.UpdateValue(palette, dark, item.Battery != null ? item.Battery.Percent : (int?)null, item.Battery != null && item.Battery.Fresh);
            hero.Controls.Add(ring);

            var hName = new Label { Text = item.Name, Font = headingFont, ForeColor = palette.Foreground, BackColor = palette.Card, AutoSize = true, Location = new Point(116, 12) };
            string kindCn = item.Kind == "mouse" ? "鼠标" : item.Kind == "headset" ? "耳机" : item.Kind == "keyboard" ? "键盘" : item.Kind == "gamepad" ? "手柄" : "外设";
            var hMeta = new Label { Text = kindCn + "，" + item.Transport, Font = bodyFont, ForeColor = palette.Muted, BackColor = palette.Card, AutoSize = true, Location = new Point(116, 40) };
            var hState = new Label { Text = "●  " + item.StateText, Font = subFont, ForeColor = item.Battery != null && item.Battery.Fresh && ThemePalette.IsLow(item.Battery, DeviceSettings.Current.WarningThreshold) ? ThemePalette.LevelColor(10, dark) : palette.Muted, BackColor = palette.Card, AutoSize = true, Location = new Point(116, 68) };
            hero.Controls.Add(hName);
            hero.Controls.Add(hMeta);
            hero.Controls.Add(hState);

            var rows = new List<Control>();
            if (item.CanRename) {
                var box = new TextBox { Width = 200, Text = item.Name, BackColor = palette.RaisedSurface, ForeColor = palette.Foreground, BorderStyle = BorderStyle.FixedSingle, Font = subFont };
                Action saveAlias = delegate {
                    if (rebuilding || closing || IsDisposed) return;
                    string trimmed = (box.Text ?? "").Trim();
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
                box.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.Handled = true; e.SuppressKeyPress = true; saveAlias(); } };
                box.Leave += delegate { saveAlias(); };
                rows.Add(CreateSettingRow("显示名称", "留空时恢复设备默认名称（按回车或失焦保存）", box, true));
            }

            if (item.DpiStages != null && item.DpiStages.Length > 0) {
                string[] labels = new string[item.DpiStages.Length];
                for (int i = 0; i < item.DpiStages.Length; i++) labels[i] = item.DpiStages[i].ToString();
                var dpiCtrl = CreateSegmentedChoices(labels, item.ActiveDpiIndex, delegate(int idx) {
                    if (!item.HasWritableDpi) { ShowNotice("此设备当前为只读 DPI 或状态待确认"); return; }
                    if (item.IsNativeMouse) owner.SelectDpiStage(idx, item.DpiTargetToken);
                    else owner.SelectWirelessDpi(item.Key, item.DpiTargetToken, item.DpiStages[idx]);
                    ShowNotice("已请求切换 DPI 至 " + item.DpiStages[idx]);
                });
                rows.Add(CreateSettingRow("DPI 档位", item.HasWritableDpi ? "设备板载档位，点击即切换" : "当前仅支持读取 DPI", dpiCtrl, true));
            }

            if (item.CanHide) {
                var showToggle = new ControlCenterToggle(palette, dark) { Checked = !item.Hidden };
                showToggle.Toggled += delegate {
                    UpdatePreference(delegate(AudioBindings p) { DeviceSettings.SetDeviceHidden(p, item.Key, !showToggle.Checked); });
                };
                rows.Add(CreateSettingRow("在主面板显示", "关闭后不在托盘弹出面板展示，后台仍继续读取电量", showToggle, true));
            }

            if (item.CanPinTray) {
                var pinToggle = new ControlCenterToggle(palette, dark) { Checked = item.PinnedTray };
                pinToggle.Toggled += delegate {
                    UpdatePreference(delegate(AudioBindings p) { p.TrayDeviceId = pinToggle.Checked ? item.Key : null; });
                };
                rows.Add(CreateSettingRow("固定为托盘图标", "主托盘图标始终显示这台设备的电量", pinToggle, true));
            }

            var verLbl = new Label { Text = item.VerificationText, Font = subFont, ForeColor = palette.Muted, BackColor = palette.Card, AutoSize = true };
            rows.Add(CreateSettingRow("适配验证", null, verLbl, true));

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

            // WinForms DockStyle.Top stacks bottom-to-top by control order
            for (int i = rows.Count - 1; i >= 0; i--) card.Controls.Add(rows[i]);
            card.Controls.Add(hero);
            return card;
        }

        // ==================== PAGE 2: APPEARANCE ====================
        Control BuildAppearancePage() {
            var root = CreatePageRoot("外观");
            var pref = DeviceSettings.Current;
            string currentStyle = ThemePalette.Normalize(pref.ThemeStyle);

            var grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, AutoSize = true, BackColor = palette.WindowBackground };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 326));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var leftCard = CreateCard(new Padding(22, 16, 22, 16));
            leftCard.Dock = DockStyle.Top;
            leftCard.Margin = new Padding(0, 0, 18, 0);

            // Swatches header + 5 equal-width swatch cards (graphite first with "新" badge)
            var swatchSection = new Panel { Dock = DockStyle.Top, Height = 114, BackColor = palette.Card };
            swatchSection.Controls.Add(new Label { Text = "配色", Font = bodyFont, ForeColor = palette.Foreground, BackColor = palette.Card, AutoSize = true, Location = new Point(0, 4) });
            var swatchGrid = new TableLayoutPanel { Location = new Point(0, 30), Height = 76, Width = 460, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, ColumnCount = 5, RowCount = 1, BackColor = palette.Card, Margin = Padding.Empty };
            for (int c = 0; c < 5; c++) swatchGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20f));
            swatchGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            string[] displayOrder = { "graphite", "ocean", "native", "amber", "mist" };
            for (int i = 0; i < displayOrder.Length; i++) {
                string k = displayOrder[i];
                var card = new ControlCenterSwatchCard(k, ThemePalette.Name(k), k == "graphite", palette, dark);
                card.Dock = DockStyle.Fill;
                card.UpdateState(palette, dark, currentStyle == k);
                card.Margin = new Padding(0, 0, i < 4 ? 8 : 0, 0);
                card.Chosen += delegate {
                    UpdatePreference(delegate(AudioBindings p) { p.ThemeStyle = k; }, false, true);
                };
                swatchGrid.Controls.Add(card, i, 0);
            }
            swatchSection.Resize += delegate { swatchGrid.Width = Math.Max(320, swatchSection.ClientSize.Width); };
            swatchSection.Controls.Add(swatchGrid);

            int themeIdx = pref.Theme == "auto" ? 0 : pref.Theme == "light" ? 1 : 2;
            var themeRow = CreateSettingRow("浅色 / 深色", null, CreateSegmentedChoices(new[] { "跟随系统", "浅色", "深色" }, themeIdx, delegate(int idx) {
                UpdatePreference(delegate(AudioBindings p) { p.Theme = idx == 0 ? "auto" : idx == 1 ? "light" : "dark"; }, false, true);
            }), true);

            int layoutIdx = Array.IndexOf(PanelLayouts.Keys, PanelLayouts.Normalize(pref.PanelLayout));
            var layoutRow = CreateSettingRow("主面板布局", null, CreateSegmentedChoices(PanelLayouts.Names, layoutIdx, delegate(int idx) {
                UpdatePreference(delegate(AudioBindings p) { p.PanelLayout = PanelLayouts.Keys[idx]; }, false, false);
            }), true);

            int fontIdx = pref.FontSize == "large" ? 1 : 0;
            var fontRow = CreateSettingRow("字号", null, CreateSegmentedChoices(new[] { "标准", "较大" }, fontIdx, delegate(int idx) {
                UpdatePreference(delegate(AudioBindings p) { p.FontSize = idx == 1 ? "large" : "normal"; }, false, true);
            }), true);

            var motionToggle = new ControlCenterToggle(palette, dark) { Checked = pref.ReducedMotion };
            motionToggle.Toggled += delegate {
                UpdatePreference(delegate(AudioBindings p) { p.ReducedMotion = motionToggle.Checked; }, false, false);
            };
            var motionRow = CreateSettingRow("减弱动效", "关闭面板展开与电量填充过渡动画", motionToggle, true);

            leftCard.Controls.Add(motionRow);
            leftCard.Controls.Add(fontRow);
            leftCard.Controls.Add(layoutRow);
            leftCard.Controls.Add(themeRow);
            leftCard.Controls.Add(swatchSection);

            // Right live preview
            var rightWrap = new Panel { Dock = DockStyle.Top, AutoSize = true, BackColor = palette.WindowBackground };
            var previewLbl = new Label { Text = "实时预览（当前主面板）", Font = subFont, ForeColor = palette.Muted, BackColor = palette.WindowBackground, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(2, 0, 0, 8) };
            var preview = new ControlCenterLivePreview(owner, palette, dark) { Dock = DockStyle.Top, Height = 360 };
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
            var root = CreatePageRoot("显示位置");
            var pref = DeviceSettings.Current;

            var cardsGrid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, RowCount = 1, Height = 122, BackColor = palette.WindowBackground, Margin = new Padding(0, 0, 0, 16) };
            cardsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            cardsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            cardsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));

            cardsGrid.Controls.Add(CreatePlacementOptionCard(0, "托盘图标", "通知区域显示电量数字，点击打开主面板", "已启用"), 0, 0);
            cardsGrid.Controls.Add(CreatePlacementOptionCard(1, "桌面小组件", "常驻桌面、胶囊收起、贴边隐藏、全屏防打扰", pref.DesktopWidget.Enabled ? "已启用" : "未启用"), 1, 0);
            cardsGrid.Controls.Add(CreatePlacementOptionCard(2, "任务栏电量", "在任务栏旁直接显示几台设备的电量", "实验 · Wave 4"), 2, 0);

            var detailCard = CreateCard(new Padding(22, 16, 22, 18));
            detailCard.Dock = DockStyle.Top;

            var preview = new ControlCenterPlacementPreview(palette, dark) { Dock = DockStyle.Top, Height = 152, Margin = new Padding(0, 12, 0, 0) };
            preview.UpdatePreview(palette, dark, selectedPlacementCard);

            var rows = new List<Control>();
            if (selectedPlacementCard == 0) {
                int iconStyleIdx = pref.TrayIconStyle == "device" ? 1 : 0;
                rows.Add(CreateSettingRow("托盘图标样式", "选择在系统托盘直接显示数字或设备图标", CreateSegmentedChoices(new[] { "数字", "设备图标" }, iconStyleIdx, delegate(int idx) {
                    UpdatePreference(delegate(AudioBindings p) { p.TrayIconStyle = idx == 1 ? "device" : "number"; });
                }), false));
                string[] ruleKeys = { "active", "lowest", "auto", "mouse", "headset", "keyboard" };
                string[] ruleLabels = { "当前使用", "最低电量", "自动", "鼠标优先", "耳机优先", "键盘优先" };
                int ruleIdx = Array.IndexOf(ruleKeys, pref.TraySource);
                rows.Add(CreateSettingRow("主托盘显示规则", String.IsNullOrEmpty(pref.TrayDeviceId) ? "当未固定单台设备时生效" : "当前已固定单台设备，点击任意规则可解除固定", CreateSegmentedChoices(ruleLabels, String.IsNullOrEmpty(pref.TrayDeviceId) ? ruleIdx : -1, delegate(int idx) {
                    UpdatePreference(delegate(AudioBindings p) { p.TraySource = ruleKeys[idx]; p.TrayDeviceId = null; });
                }), true));
            } else if (selectedPlacementCard == 1) {
                var w = pref.DesktopWidget;
                var enableToggle = new ControlCenterToggle(palette, dark) { Checked = w.Enabled };
                enableToggle.Toggled += delegate {
                    UpdatePreference(delegate(AudioBindings p) { p.DesktopWidget.Enabled = enableToggle.Checked; });
                };
                rows.Add(CreateSettingRow("启用桌面小组件", "在桌面显示独立悬浮电量胶囊，支持自由拖动与点击展开", enableToggle, false));

                int styleIdx = Array.IndexOf(DesktopWidgetGeometry.StyleKeys, DesktopWidgetGeometry.NormalizeStyle(w.Style));
                rows.Add(CreateSettingRow("收起形态", "平时停靠在桌面上的紧凑外观", CreateSegmentedChoices(DesktopWidgetGeometry.StyleNames, styleIdx, delegate(int idx) {
                    UpdatePreference(delegate(AudioBindings p) { p.DesktopWidget.Style = DesktopWidgetGeometry.StyleKeys[idx]; });
                }), true));

                int edgeIdx = Array.IndexOf(DesktopWidgetGeometry.EdgeKeys, DesktopWidgetGeometry.NormalizeEdge(w.DockEdge));
                rows.Add(CreateSettingRow("贴边停靠", "拖近屏幕四边也会自动吸附并记住所在显示器", CreateSegmentedChoices(DesktopWidgetGeometry.EdgeNames, edgeIdx, delegate(int idx) {
                    UpdatePreference(delegate(AudioBindings p) { p.DesktopWidget.DockEdge = DesktopWidgetGeometry.EdgeKeys[idx]; });
                }), true));

                var autoHideToggle = new ControlCenterToggle(palette, dark) { Checked = w.AutoHideOnEdge };
                autoHideToggle.Toggled += delegate {
                    UpdatePreference(delegate(AudioBindings p) { p.DesktopWidget.AutoHideOnEdge = autoHideToggle.Checked; });
                };
                rows.Add(CreateSettingRow("贴边自动隐藏", "贴靠边缘时缩为细指示条，鼠标移到边缘立即滑出", autoHideToggle, true));

                var topToggle = new ControlCenterToggle(palette, dark) { Checked = w.TopMost };
                topToggle.Toggled += delegate {
                    UpdatePreference(delegate(AudioBindings p) { p.DesktopWidget.TopMost = topToggle.Checked; });
                };
                rows.Add(CreateSettingRow("总在最前", "关闭后小组件像普通窗口一样可被其他应用遮挡", topToggle, true));

                string[] fsKeys = { "hide", "edge", "ignore" };
                string[] fsLabels = { "全屏隐藏", "缩为指示条", "不处理" };
                int fsIdx = Array.IndexOf(fsKeys, DesktopDisplayWatch.NormalizePolicy(w.FullscreenPolicy));
                rows.Add(CreateSettingRow("全屏游戏 / 视频防打扰", "不确定全屏信号时不会永久消失，仅缩为指示条", CreateSegmentedChoices(fsLabels, fsIdx, delegate(int idx) {
                    UpdatePreference(delegate(AudioBindings p) { p.DesktopWidget.FullscreenPolicy = fsKeys[idx]; });
                }), true));

                var maxToggle = new ControlCenterToggle(palette, dark) { Checked = w.CompactOnMaximized };
                maxToggle.Toggled += delegate {
                    UpdatePreference(delegate(AudioBindings p) { p.DesktopWidget.CompactOnMaximized = maxToggle.Checked; });
                };
                rows.Add(CreateSettingRow("窗口最大化时缩为指示条", "当前屏幕有窗口最大化时自动收窄，减少遮挡", maxToggle, true));

                int[] maxOpts = { 2, 3, 4 };
                int maxIdx = Array.IndexOf(maxOpts, w.MaxDevices);
                if (maxIdx < 0) maxIdx = 1;
                rows.Add(CreateSettingRow("收起时最多显示设备数", null, CreateSegmentedChoices(new[] { "2 台", "3 台", "4 台" }, maxIdx, delegate(int idx) {
                    UpdatePreference(delegate(AudioBindings p) { p.DesktopWidget.MaxDevices = maxOpts[idx]; });
                }), true));
            } else {
                var info = new Label { Text = "将在 Wave 4 作为实验功能提供：采用纯净室独立无焦点贴合浮层，不支持环境（如自动隐藏/侧边任务栏）将自动降级隐藏并提示原因。", Font = subFont, ForeColor = palette.Muted, BackColor = palette.Card, AutoSize = true, MaximumSize = new Size(760, 0), Padding = new Padding(0, 4, 0, 12) };
                rows.Add(info);
            }

            detailCard.Controls.Add(preview);
            for (int i = rows.Count - 1; i >= 0; i--) {
                rows[i].Dock = DockStyle.Top;
                detailCard.Controls.Add(rows[i]);
            }

            root.Controls.Add(detailCard);
            root.Controls.Add(cardsGrid);
            root.Controls.SetChildIndex(detailCard, 0);
            root.Controls.SetChildIndex(cardsGrid, 1);
            return root;
        }

        Control CreatePlacementOptionCard(int mode, string title, string desc, string tag) {
            bool active = selectedPlacementCard == mode;
            var card = new Panel {
                Dock = DockStyle.Fill,
                Margin = new Padding(mode == 0 ? 0 : 6, 0, mode == 2 ? 0 : 6, 0),
                BackColor = palette.Card,
                Cursor = Cursors.Hand,
                Padding = new Padding(14, 12, 14, 12)
            };
            card.Paint += delegate(object sender, PaintEventArgs e) {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new RectangleF(0.5f, 0.5f, card.Width - 1, card.Height - 1);
                CompactRenderer.Fill(g, r, palette.Card, 10);
                using (var p = VisualDesign.RoundRect(r.X, r.Y, r.Width, r.Height, 10))
                using (var pen = new Pen(active ? palette.Foreground : palette.Border, active ? 1.5f : 1f)) g.DrawPath(pen, p);
            };
            var lblT = new Label { Text = title, Font = boldFont, ForeColor = palette.Foreground, BackColor = palette.Card, AutoSize = true, Location = new Point(14, 12), Cursor = Cursors.Hand };
            var lblD = new Label { Text = desc, Font = subFont, ForeColor = palette.Muted, BackColor = palette.Card, MaximumSize = new Size(220, 44), AutoSize = true, Location = new Point(14, 36), Cursor = Cursors.Hand };
            var lblTag = new Label { Text = tag, Font = subFont, ForeColor = active ? palette.Foreground : palette.Muted, BackColor = palette.RaisedSurface, AutoSize = true, Padding = new Padding(6, 2, 6, 2), Location = new Point(14, 84), Cursor = Cursors.Hand };
            EventHandler click = delegate {
                if (selectedPlacementCard != mode) { selectedPlacementCard = mode; RebuildCurrentPage(); }
            };
            card.Click += click; lblT.Click += click; lblD.Click += click; lblTag.Click += click;
            card.Controls.Add(lblT); card.Controls.Add(lblD); card.Controls.Add(lblTag);
            return card;
        }

        // ==================== PAGE 4: REMINDERS ====================
        Control BuildRemindersPage() {
            var root = CreatePageRoot("提醒");
            var pref = DeviceSettings.Current;
            var card = CreateCard(new Padding(22, 8, 22, 8));
            card.Dock = DockStyle.Top;

            var lowToggle = new ControlCenterToggle(palette, dark) { Checked = pref.LowBatteryNotifications };
            lowToggle.Toggled += delegate {
                UpdatePreference(delegate(AudioBindings p) { p.LowBatteryNotifications = lowToggle.Checked; });
            };
            var row1 = CreateSettingRow("低电量提醒", "电量低于阈值时弹出一次系统通知", lowToggle, false);

            int threshIdx = pref.WarningThreshold == 10 ? 0 : pref.WarningThreshold == 15 ? 1 : 2;
            var row2 = CreateSettingRow("提醒阈值", "所有无线外设共用此低电量提醒门槛", CreateSegmentedChoices(new[] { "10%", "15%", "20%" }, threshIdx, delegate(int idx) {
                UpdatePreference(delegate(AudioBindings p) { p.WarningThreshold = idx == 0 ? 10 : idx == 1 ? 15 : 20; });
            }), true);

            card.Controls.Add(row2);
            card.Controls.Add(row1);
            root.Controls.Add(card);
            root.Controls.SetChildIndex(card, 0);
            return root;
        }

        // ==================== PAGE 5: DIAGNOSTICS & FEEDBACK ====================
        Control BuildDiagnosticsPage() {
            var root = CreatePageRoot("诊断与反馈");
            var card = CreateCard(new Padding(22, 8, 22, 8));
            card.Dock = DockStyle.Top;

            string healthText = !String.IsNullOrEmpty(owner.GenericError) ? owner.GenericError :
                !String.IsNullOrEmpty(owner.GenericHealthSummary) ? owner.GenericHealthSummary :
                owner.GenericReady ? "读取组件运行正常" : "读取组件初始化中";

            var r1 = CreateSettingRow("读取组件状态", healthText + " · 型号数据包：" + owner.DevicePackDiagnostic, CreateActionButton("重新刷新", delegate {
                owner.RefreshBattery(true);
                ShowNotice("已触发设备与组件刷新");
            }), false);

            var r2 = CreateSettingRow("反馈问题", "在程序内提交新设备请求或异常，不需要 GitHub 账号", CreateActionButton("写反馈", delegate {
                owner.OpenFeedbackFromHost(this, false, copyText, openLink);
            }), true);

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
            var root = CreatePageRoot("设置");
            var pref = DeviceSettings.Current;
            var card = CreateCard(new Padding(22, 8, 22, 8));
            card.Dock = DockStyle.Top;

            var rows = new List<Control>();

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
            rows.Add(CreateSettingRow("自动识别更多设备", "实验功能 · 只发送读取电量的查询；更改后将重启读取组件", probeToggle, false));

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
                    rows.Add(CreateSettingRow("已停止读取：" + target.DisplayName, "点击右侧按钮恢复对此设备的电量查询", btn, true));
                }
            }

            int bgIdx = pref.BackgroundSeconds == 60 ? 0 : pref.BackgroundSeconds == 120 ? 1 : 2;
            rows.Add(CreateSettingRow("后台同步间隔", null, CreateSegmentedChoices(new[] { "1 分钟", "2 分钟", "5 分钟" }, bgIdx, delegate(int idx) {
                UpdatePreference(delegate(AudioBindings p) { p.BackgroundSeconds = idx == 0 ? 60 : idx == 1 ? 120 : 300; });
            }), true));

            var rememberToggle = new ControlCenterToggle(palette, dark) { Checked = pref.RememberReadings };
            rememberToggle.Toggled += delegate {
                UpdatePreference(delegate(AudioBindings p) { p.RememberReadings = rememberToggle.Checked; });
            };
            rows.Add(CreateSettingRow("跨重启保留读数", "关闭保留不删除已有历史，需通过下方按钮单独清除", rememberToggle, true));

            rows.Add(CreateSettingRow("清除历史读数", "清空本地缓存的上次电量记录", CreateActionButton("清除", delegate {
                owner.ClearReadingHistory();
                ShowNotice("正在清除历史读数…");
            }), true));

            var autoUpdate = new ControlCenterToggle(palette, dark) { Checked = pref.AutoCheckUpdates };
            autoUpdate.Toggled += delegate { UpdatePreference(delegate(AudioBindings p) { p.AutoCheckUpdates = autoUpdate.Checked; }); };
            rows.Add(CreateSettingRow("自动检查更新", null, autoUpdate, true));

            var badgeToggle = new ControlCenterToggle(palette, dark) { Checked = pref.ShowUpdateBadge };
            badgeToggle.Toggled += delegate { UpdatePreference(delegate(AudioBindings p) { p.ShowUpdateBadge = badgeToggle.Checked; }); };
            rows.Add(CreateSettingRow("托盘图标显示更新角标", null, badgeToggle, true));

            var betaToggle = new ControlCenterToggle(palette, dark) { Checked = pref.IncludeBetaUpdates };
            betaToggle.Toggled += delegate { UpdatePreference(delegate(AudioBindings p) { p.IncludeBetaUpdates = betaToggle.Checked; }); };
            rows.Add(CreateSettingRow("提醒测试版更新", "接收 1.4 测试版新设备与功能更新通知", betaToggle, true));

            var replyToggle = new ControlCenterToggle(palette, dark) { Checked = pref.CheckFeedbackReplies };
            replyToggle.Toggled += delegate { UpdatePreference(delegate(AudioBindings p) { p.CheckFeedbackReplies = replyToggle.Checked; }); };
            rows.Add(CreateSettingRow("自动检查反馈回复", null, replyToggle, true));

            var apiToggle = new ControlCenterToggle(palette, dark) { Checked = pref.LocalApi != null && pref.LocalApi.Enabled };
            apiToggle.Toggled += delegate { UpdatePreference(delegate(AudioBindings p) { p.LocalApi.Enabled = apiToggle.Checked; }); };
            string apiSub = owner.LocalApiRunning
                ? "已监听 127.0.0.1:" + owner.LocalApiPort + "（仅本机只读 JSON-RPC 2.0 / SSE，凭据仅当前用户可读）"
                : !String.IsNullOrEmpty(owner.LocalApiError) ? "启动失败：" + owner.LocalApiError : "默认关闭 · 开启后供 MyDockFinder / Rainmeter / OBS 等读取电量";
            rows.Add(CreateSettingRow("本机只读开放接口（JSON-RPC 2.0）", apiSub, apiToggle, true));

            if (owner.LocalApiRunning) {
                rows.Add(CreateSettingRow("接口连接信息", "凭据文件仅当前 Windows 用户可读，不含写入权限", CreateActionButton("复制文件路径", delegate {
                    copyText(owner.LocalApiSessionPath);
                    ShowNotice("已复制 api-session.json 路径（令牌请从受保护文件读取）");
                }), true));
            }

            for (int i = rows.Count - 1; i >= 0; i--) card.Controls.Add(rows[i]);
            root.Controls.Add(card);
            root.Controls.SetChildIndex(card, 0);
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
            }
            base.Dispose(disposing);
        }
    }
}
