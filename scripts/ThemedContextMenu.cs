using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AtkBattery;

namespace AtkBatteryTray {
    // Fluent-styled theme-aware ContextMenuStrip renderer unifying Tray, Desktop Widget,
    // and Taskbar Overlay menus with the active ThemePalette, dark/light mode, and Win11 rounded corners.
    internal static class ThemedContextMenu {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        internal static void Apply(ContextMenuStrip menu) {
            if (menu == null) return;
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;
            menu.Padding = new Padding(4, 5, 4, 5);
            menu.DropShadowEnabled = true;
            menu.Renderer = new ThemedMenuRenderer();
            menu.Opening += delegate { SyncMenuTheme(menu); };
            menu.HandleCreated += delegate { ApplyWin11Corners(menu.Handle); };
            SyncMenuTheme(menu);
        }

        static void ApplyWin11Corners(IntPtr hwnd) {
            if (hwnd == IntPtr.Zero || VisualDesign.HighContrast) return;
            try {
                int round = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, 33, ref round, 4);
                var pal = ThemePalette.Current(Style.Dark);
                int border = ColorTranslator.ToWin32(pal.Border);
                DwmSetWindowAttribute(hwnd, 34, ref border, 4);
            } catch (Exception) { }
        }

        internal static void SyncMenuTheme(ToolStripDropDown menu) {
            if (menu == null || menu.IsDisposed) return;
            bool dark = Style.Dark;
            var pal = ThemePalette.Current(dark);
            float fontPx = VisualDesign.LargeFont ? 13.5f : 12.5f;
            menu.BackColor = VisualDesign.HighContrast ? SystemColors.Menu : pal.Card;
            menu.ForeColor = VisualDesign.HighContrast ? SystemColors.MenuText : pal.Foreground;
            if (menu.Font == null || Math.Abs(menu.Font.SizeInPoints - fontPx * 0.75f) > 0.5f || menu.Font.Name != "Microsoft YaHei UI") {
                try { menu.Font = new Font("Microsoft YaHei UI", fontPx, FontStyle.Regular, GraphicsUnit.Pixel); } catch (Exception) { }
            }
            if (menu.IsHandleCreated) ApplyWin11Corners(menu.Handle);
            foreach (ToolStripItem item in menu.Items) {
                SyncItem(item, pal);
            }
        }

        static void SyncItem(ToolStripItem item, ThemePalette pal) {
            if (item == null) return;
            item.ForeColor = VisualDesign.HighContrast ? SystemColors.MenuText : (item.Enabled ? pal.Foreground : pal.Muted);
            var menuItem = item as ToolStripMenuItem;
            if (menuItem != null) {
                menuItem.Padding = new Padding(2, 4, 8, 4);
                if (menuItem.HasDropDownItems) {
                    var drop = menuItem.DropDown as ToolStripDropDownMenu;
                    if (drop != null) {
                        drop.ShowImageMargin = false;
                        drop.ShowCheckMargin = true;
                        drop.Padding = new Padding(4, 5, 4, 5);
                        drop.Renderer = new ThemedMenuRenderer();
                        drop.Opening -= HandleSubMenuOpening;
                        drop.Opening += HandleSubMenuOpening;
                        drop.HandleCreated -= HandleSubMenuHandleCreated;
                        drop.HandleCreated += HandleSubMenuHandleCreated;
                    }
                    foreach (ToolStripItem child in menuItem.DropDownItems) SyncItem(child, pal);
                }
            }
        }

        static void HandleSubMenuOpening(object sender, System.ComponentModel.CancelEventArgs e) {
            var drop = sender as ToolStripDropDown;
            if (drop != null) SyncMenuTheme(drop);
        }

        static void HandleSubMenuHandleCreated(object sender, EventArgs e) {
            var drop = sender as ToolStripDropDown;
            if (drop != null && !drop.IsDisposed) ApplyWin11Corners(drop.Handle);
        }

        sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer {
            internal ThemedMenuRenderer() : base(new ThemedMenuColorTable()) {
                RoundedEdges = false;
            }

            static ThemePalette CurrentPalette(out bool dark) {
                dark = Style.Dark;
                return ThemePalette.Current(dark);
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) {
                bool dark;
                var pal = CurrentPalette(out dark);
                Color bg = VisualDesign.HighContrast ? SystemColors.Menu : pal.Card;
                e.Graphics.Clear(bg);
            }

            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) {
                // Suppress legacy Win9x/Win7 vertical left gutter bar completely.
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) {
                bool dark;
                var pal = CurrentPalette(out dark);
                Color border = VisualDesign.HighContrast ? SystemColors.WindowText : pal.Border;
                using (var pen = new Pen(border, 1f)) {
                    e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
                }
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e) {
                if (!e.Item.Enabled) return;
                bool active = e.Item.Selected || e.Item.Pressed;
                if (!active) return;

                bool dark;
                var pal = CurrentPalette(out dark);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new RectangleF(4f, 1f, Math.Max(4f, e.Item.Width - 8f), Math.Max(4f, e.Item.Height - 2f));
                Color fill = VisualDesign.HighContrast
                    ? SystemColors.Highlight
                    : (dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(16, 0, 0, 0));
                CompactRenderer.Fill(g, rect, fill, 5.5f);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) {
                bool dark;
                var pal = CurrentPalette(out dark);
                bool active = e.Item.Enabled && (e.Item.Selected || e.Item.Pressed);
                e.TextColor = VisualDesign.HighContrast
                    ? (active ? SystemColors.HighlightText : (e.Item.Enabled ? SystemColors.MenuText : SystemColors.GrayText))
                    : (e.Item.Enabled ? pal.Foreground : pal.Muted);
                e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) {
                bool dark;
                var pal = CurrentPalette(out dark);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color fg = VisualDesign.HighContrast
                    ? ((e.Item.Selected || e.Item.Pressed) ? SystemColors.HighlightText : SystemColors.MenuText)
                    : pal.Selected;
                var r = e.ImageRectangle;
                float cx = r.X + r.Width / 2f;
                float cy = r.Y + r.Height / 2f;
                using (var pen = new Pen(fg, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }) {
                    g.DrawLines(pen, new[] {
                        new PointF(cx - 4f, cy),
                        new PointF(cx - 1.2f, cy + 3f),
                        new PointF(cx + 4.5f, cy - 3.2f)
                    });
                }
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) {
                bool dark;
                var pal = CurrentPalette(out dark);
                int y = e.Item.Height / 2;
                Color line = VisualDesign.HighContrast ? SystemColors.GrayText : Color.FromArgb(dark ? 55 : 42, pal.Border);
                using (var pen = new Pen(line, 1f)) {
                    e.Graphics.DrawLine(pen, 10, y, Math.Max(10, e.Item.Width - 10), y);
                }
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) {
                bool dark;
                var pal = CurrentPalette(out dark);
                Color fg = VisualDesign.HighContrast
                    ? ((e.Item.Selected || e.Item.Pressed) ? SystemColors.HighlightText : SystemColors.MenuText)
                    : pal.Muted;
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float cx = e.ArrowRectangle.X + e.ArrowRectangle.Width / 2f;
                float cy = e.ArrowRectangle.Y + e.ArrowRectangle.Height / 2f;
                using (var pen = new Pen(fg, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }) {
                    g.DrawLines(pen, new[] {
                        new PointF(cx - 2.2f, cy - 3.6f),
                        new PointF(cx + 1.8f, cy),
                        new PointF(cx - 2.2f, cy + 3.6f)
                    });
                }
            }
        }

        sealed class ThemedMenuColorTable : ProfessionalColorTable {
            static ThemePalette Pal { get { return ThemePalette.Current(Style.Dark); } }
            public override Color ToolStripDropDownBackground { get { return VisualDesign.HighContrast ? SystemColors.Menu : Pal.Card; } }
            public override Color MenuBorder { get { return VisualDesign.HighContrast ? SystemColors.WindowText : Pal.Border; } }
            public override Color ImageMarginGradientBegin { get { return ToolStripDropDownBackground; } }
            public override Color ImageMarginGradientMiddle { get { return ToolStripDropDownBackground; } }
            public override Color ImageMarginGradientEnd { get { return ToolStripDropDownBackground; } }
        }
    }
}
