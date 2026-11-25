using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace AtkBatteryTray {
    // Reusable premultiplied-BGRA layered window surface with strict GDI lifetime
    // and per-monitor DPI scaling. Used by DesktopWidgetForm (T50) and future overlays.
    internal sealed class LayeredSurface : IDisposable {
        [StructLayout(LayoutKind.Sequential)]
        struct NativePoint {
            internal int X, Y;
            internal NativePoint(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NativeSize {
            internal int Width, Height;
            internal NativeSize(int w, int h) { Width = w; Height = h; }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct NativeBlend {
            internal byte Op, Flags, Alpha, Format;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

        [DllImport("gdi32.dll", SetLastError = true)]
        static extern bool DeleteObject(IntPtr obj);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(
            IntPtr hwnd,
            IntPtr dstDc,
            ref NativePoint dstPos,
            ref NativeSize size,
            IntPtr srcDc,
            ref NativePoint srcPos,
            int colorKey,
            ref NativeBlend blend,
            int flags);

        Bitmap buffer;
        Size physicalSize;
        bool disposed;

        internal Size PhysicalSize { get { return physicalSize; } }

        internal static Size ComputePhysicalSize(Size logicalSize, float dpiScale) {
            float scale = Math.Max(1f, Math.Min(4f, dpiScale));
            int w = Math.Max(1, (int)Math.Ceiling(logicalSize.Width * scale));
            int h = Math.Max(1, (int)Math.Ceiling(logicalSize.Height * scale));
            return new Size(w, h);
        }

        Bitmap EnsureBuffer(Size target) {
            if (buffer != null && buffer.Width == target.Width && buffer.Height == target.Height) return buffer;
            if (buffer != null) {
                buffer.Dispose();
                buffer = null;
            }
            buffer = new Bitmap(target.Width, target.Height, PixelFormat.Format32bppPArgb);
            physicalSize = target;
            return buffer;
        }

        internal static Bitmap RenderToBitmap(Size logicalSize, float dpiScale, Action<Graphics, SizeF> paint) {
            float scale = Math.Max(1f, Math.Min(4f, dpiScale));
            var phys = ComputePhysicalSize(logicalSize, scale);
            var bmp = new Bitmap(phys.Width, phys.Height, PixelFormat.Format32bppPArgb);
            try {
                using (var g = Graphics.FromImage(bmp)) {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.ScaleTransform(scale, scale);
                    if (paint != null) paint(g, new SizeF(logicalSize.Width, logicalSize.Height));
                }
                return bmp;
            } catch {
                bmp.Dispose();
                throw;
            }
        }

        internal bool Commit(IntPtr hwnd, Point physicalLocation, Size logicalSize, float dpiScale, byte opacity, Action<Graphics, SizeF> paint) {
            if (disposed || hwnd == IntPtr.Zero) return false;
            float scale = Math.Max(1f, Math.Min(4f, dpiScale));
            var target = ComputePhysicalSize(logicalSize, scale);
            var bmp = EnsureBuffer(target);
            using (var g = Graphics.FromImage(bmp)) {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.ScaleTransform(scale, scale);
                if (paint != null) paint(g, new SizeF(logicalSize.Width, logicalSize.Height));
            }

            IntPtr screenDc = IntPtr.Zero;
            IntPtr memDc = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try {
                screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero) return false;
                memDc = CreateCompatibleDC(screenDc);
                if (memDc == IntPtr.Zero) return false;
                hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
                if (hBitmap == IntPtr.Zero) return false;
                oldBitmap = SelectObject(memDc, hBitmap);
                var dstPos = new NativePoint(physicalLocation.X, physicalLocation.Y);
                var size = new NativeSize(target.Width, target.Height);
                var srcPos = new NativePoint(0, 0);
                var blend = new NativeBlend { Op = 0, Flags = 0, Alpha = opacity, Format = 1 };
                return UpdateLayeredWindow(hwnd, screenDc, ref dstPos, ref size, memDc, ref srcPos, 0, ref blend, 2);
            } finally {
                if (oldBitmap != IntPtr.Zero && memDc != IntPtr.Zero) SelectObject(memDc, oldBitmap);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        public void Dispose() {
            if (disposed) return;
            disposed = true;
            if (buffer != null) {
                buffer.Dispose();
                buffer = null;
            }
            physicalSize = Size.Empty;
        }
    }
}
