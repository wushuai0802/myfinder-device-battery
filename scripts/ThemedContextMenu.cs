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
        static readonly ThemedMenuRenderer SharedRenderer = new ThemedMenuRenderer();
        static Font normalMenuFont;
        static Font largeMenuFont;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        static Font MenuFontFor(bool large) {
            if (large) {
                if (largeMenuFont == null) largeMenuFont = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point);
                return largeMenuFont;
            }
            if (normalMenuFont == null) normalMenuFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            return normalMenuFont;
        }

        static bool HasCheckableItems(ToolStripItemCollection items) {
            if (items == null) return false;
            foreach (ToolStripItem item in items) {
                var mi = item as ToolStripMenuItem;
                if (mi != null && (mi.Checked || mi.CheckOnClick)) return true;
            }
            return false;
        }

        internal static void Apply(ContextMenuStrip menu) {
            if (menu == null) return;
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = HasCheckableItems(menu.Items);
            menu.Padding = new Padding(0, 5, 0, 5);
            menu.DropShadowEnabled = true;
            menu.Renderer = SharedRenderer;
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
            var dropMenu = menu as ToolStripDropDownMenu;
            bool hasChecks = HasCheckableItems(menu.Items);
            if (dropMenu != null) {
                dropMenu.ShowImageMargin = false;
                dropMenu.ShowCheckMargin = hasChecks;
                dropMenu.Padding = new Padding(0, 5, 0, 5);
            }
            menu.BackColor = VisualDesign.HighContrast ? SystemColors.Menu : pal.Card;
            menu.ForeColor = VisualDesign.HighContrast ? SystemColors.MenuText : pal.Foreground;
            var targetFont = MenuFontFor(VisualDesign.LargeFont);
            if (!ReferenceEquals(menu.Font, targetFont)) {
                try { menu.Font = targetFont; } catch (Exception) { }
            }
            if (menu.IsHandleCreated) ApplyWin11Corners(menu.Handle);
            foreach (ToolStripItem item in menu.Items) {
                SyncItem(item, pal, hasChecks);
            }
        }

        static void SyncItem(ToolStripItem item, ThemePalette pal, bool parentHasChecks) {
            if (item == null) return;
            item.ForeColor = VisualDesign.HighContrast ? SystemColors.MenuText : (item.Enabled ? pal.Foreground : pal.Muted);
            var menuItem = item as ToolStripMenuItem;
            if (menuItem != null) {
                menuItem.Margin = new Padding(0, 1, 0, 1);
                menuItem.Padding = new Padding(parentHasChecks ? 2 : 8, 5, 14, 5);
                if (menuItem.HasDropDownItems) {
                    var drop = menuItem.DropDown as ToolStripDropDownMenu;
                    bool childChecks = HasCheckableItems(menuItem.DropDownItems);
                    if (drop != null) {
                        drop.ShowImageMargin = false;
                        drop.ShowCheckMargin = childChecks;
                        drop.Padding = new Padding(0, 5, 0, 5);
                        drop.Renderer = SharedRenderer;
                        drop.Opening -= HandleSubMenuOpening;
                        drop.Opening += HandleSubMenuOpening;
                        drop.HandleCreated -= HandleSubMenuHandleCreated;
                        drop.HandleCreated += HandleSubMenuHandleCreated;
                    }
                    foreach (ToolStripItem child in menuItem.DropDownItems) SyncItem(child, pal, childChecks);
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

            static bool OwnerShowsCheckMargin(ToolStripItem item) {
                var owner = item != null ? item.Owner as ToolStripDropDownMenu : null;
                return owner != null && owner.ShowCheckMargin;
            }

            static int MenuClientWidth(ToolStripItem item) {
                if (item != null && item.Owner != null && item.Owner.ClientSize.Width > 0) return item.Owner.ClientSize.Width;
                return item != null ? item.Width : 0;
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
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                // Strictly symmetric [6px .. menuWidth - 6px] Fluent pill in menu coordinates regardless of item.Bounds.X
                int menuW = MenuClientWidth(e.Item);
                int itemX = e.Item.Bounds.X;
                float left = Math.Max(1f, 6f - itemX);
                float right = Math.Max(left + 12f, menuW - 6f - itemX);
                var rect = new RectangleF(left, 1.5f, right - left, Math.Max(8f, e.Item.Height - 3f));
                Color fill = VisualDesign.HighContrast
                    ? SystemColors.Highlight
                    : (dark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0));
                CompactRenderer.Fill(g, rect, fill, 6f);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) {
                bool dark;
                var pal = CurrentPalette(out dark);
                bool active = e.Item.Enabled && (e.Item.Selected || e.Item.Pressed);
                Color fg = VisualDesign.HighContrast
                    ? (active ? SystemColors.HighlightText : (e.Item.Enabled ? SystemColors.MenuText : SystemColors.GrayText))
                    : (e.Item.Enabled ? pal.Foreground : pal.Muted);

                bool hasCheck = OwnerShowsCheckMargin(e.Item);
                var mi = e.Item as ToolStripMenuItem;
                bool hasArrow = mi != null && mi.HasDropDownItems;
                int menuW = MenuClientWidth(e.Item);
                int itemX = e.Item.Bounds.X;
                int left = hasCheck ? Math.Max(28 - itemX, e.TextRectangle.X) : Math.Max(4, 14 - itemX);
                int rightEdge = menuW - (hasArrow ? 26 : 14) - itemX;
                int w = Math.Max(20, rightEdge - left);
                // Align text vertically to exact item center so text and pill highlight share the exact midline
                var centeredRect = new Rectangle(left, 0, w, e.Item.Height);
                TextRenderer.DrawText(
                    e.Graphics,
                    e.Text,
                    e.TextFont,
                    centeredRect,
                    fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping);
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) {
                bool dark;
                var pal = CurrentPalette(out dark);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color fg = VisualDesign.HighContrast
                    ? ((e.Item.Selected || e.Item.Pressed) ? SystemColors.HighlightText : SystemColors.MenuText)
                    : pal.Selected;
                float cx = Math.Max(10f, 16f - e.Item.Bounds.X);
                float cy = e.Item.Height / 2f;
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
                int menuW = MenuClientWidth(e.Item);
                int itemX = e.Item.Bounds.X;
                int x1 = Math.Max(4, 12 - itemX);
                int x2 = Math.Max(x1 + 8, menuW - 12 - itemX);
                Color line = VisualDesign.HighContrast ? SystemColors.GrayText : Color.FromArgb(dark ? 110 : 60, pal.Border);
                using (var pen = new Pen(line, 1f)) {
                    e.Graphics.DrawLine(pen, x1, y, x2, y);
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
                int menuW = MenuClientWidth(e.Item);
                float cx = Math.Max(16f, menuW - 16f - e.Item.Bounds.X);
                float cy = e.Item.Height / 2f;
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
