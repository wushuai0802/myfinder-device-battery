using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace AtkBatteryTray {
    internal enum DisplayObstruction { None, Maximized, Fullscreen, UncertainFullscreen }

    internal enum WidgetVisibilityMode { Normal, EdgeStrip, Hidden }

    internal sealed class ForegroundSnapshot {
        internal bool HasWindow;
        internal bool IsOwnProcess;
        internal bool IsShellOrDesktop;
        internal bool IsVisible;
        internal bool IsMinimized;
        internal bool IsZoomed;
        internal bool SameMonitor;
        internal Rectangle WindowRect;
        internal Rectangle MonitorBounds;
        internal Rectangle MonitorWorkArea;
        internal int NotificationState; // SHQueryUserNotificationState: 2=BUSY, 3=D3D_FULLSCREEN, 4=PRESENTATION, 5=ACCEPTS
    }

    // Layered fullscreen and maximized window detection.
    // Conceptually follows BrimDeck's DisplayWatch user-facing behavior (Apache-2.0);
    // implemented cleanly in Win32/WinForms with pure testable decision logic.
    internal static class DesktopDisplayWatch {
        [StructLayout(LayoutKind.Sequential)]
        struct NativeRect {
            internal int Left, Top, Right, Bottom;
            internal Rectangle ToRectangle() { return Rectangle.FromLTRB(Left, Top, Right, Bottom); }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NativePoint {
            internal int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MonitorInfo {
            internal int Size;
            internal NativeRect Monitor;
            internal NativeRect Work;
            internal uint Flags;
        }

        const uint MONITOR_DEFAULTTOPRIMARY = 1;
        const uint MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(NativePoint pt, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect rect, int size);
        [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

        internal static bool TryGetMonitorRects(IntPtr hwnd, out IntPtr hMonitor, out Rectangle monitorBounds, out Rectangle workArea) {
            hMonitor = IntPtr.Zero;
            monitorBounds = Rectangle.Empty;
            workArea = Rectangle.Empty;
            if (hwnd == IntPtr.Zero) return false;
            try {
                hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (hMonitor == IntPtr.Zero) return false;
                var mi = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                if (!GetMonitorInfo(hMonitor, ref mi)) return false;
                monitorBounds = mi.Monitor.ToRectangle();
                workArea = mi.Work.ToRectangle();
                return monitorBounds.Width > 0 && monitorBounds.Height > 0;
            } catch (Exception) {
                return false;
            }
        }

        internal static bool TryGetMonitorRectsForScreen(Screen screen, out IntPtr hMonitor, out Rectangle monitorBounds, out Rectangle workArea) {
            hMonitor = IntPtr.Zero;
            monitorBounds = Rectangle.Empty;
            workArea = Rectangle.Empty;
            try {
                NativePoint pt = screen != null
                    ? new NativePoint { X = screen.Bounds.Left + screen.Bounds.Width / 2, Y = screen.Bounds.Top + screen.Bounds.Height / 2 }
                    : new NativePoint { X = 0, Y = 0 };
                hMonitor = MonitorFromPoint(pt, screen != null ? MONITOR_DEFAULTTONEAREST : MONITOR_DEFAULTTOPRIMARY);
                if (hMonitor == IntPtr.Zero) return false;
                var mi = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                if (!GetMonitorInfo(hMonitor, ref mi)) return false;
                monitorBounds = mi.Monitor.ToRectangle();
                workArea = mi.Work.ToRectangle();
                return monitorBounds.Width > 0 && monitorBounds.Height > 0;
            } catch (Exception) {
                return false;
            }
        }

        static readonly int OwnProcessId = Process.GetCurrentProcess().Id;

        internal static bool IsShellClassName(string className) {
            if (String.IsNullOrEmpty(className)) return false;
            return className == "Progman" || className == "WorkerW" ||
                className == "Shell_TrayWnd" || className == "Shell_SecondaryTrayWnd" ||
                className == "Windows.UI.Core.CoreWindow" || className == "NotifyIconOverflowWindow";
        }

        internal static DisplayObstruction Evaluate(ForegroundSnapshot snap) {
            if (snap == null || !snap.HasWindow) return DisplayObstruction.None;
            if (snap.IsOwnProcess || snap.IsShellOrDesktop || !snap.IsVisible || snap.IsMinimized) return DisplayObstruction.None;
            if (!snap.SameMonitor) return DisplayObstruction.None;

            bool coversFullMonitor = snap.MonitorBounds.Width > 0 && snap.MonitorBounds.Height > 0 &&
                snap.WindowRect.Left <= snap.MonitorBounds.Left + 4 &&
                snap.WindowRect.Top <= snap.MonitorBounds.Top + 4 &&
                snap.WindowRect.Right >= snap.MonitorBounds.Right - 4 &&
                snap.WindowRect.Bottom >= snap.MonitorBounds.Bottom - 4;

            if (coversFullMonitor) return DisplayObstruction.Fullscreen;

            bool systemFullscreenHint = snap.NotificationState == 2 || snap.NotificationState == 3 || snap.NotificationState == 4;
            if (systemFullscreenHint) return DisplayObstruction.UncertainFullscreen;

            bool coversWorkArea = snap.MonitorWorkArea.Width > 0 && snap.MonitorWorkArea.Height > 0 &&
                snap.WindowRect.Left <= snap.MonitorWorkArea.Left + 4 &&
                snap.WindowRect.Top <= snap.MonitorWorkArea.Top + 4 &&
                snap.WindowRect.Right >= snap.MonitorWorkArea.Right - 4 &&
                snap.WindowRect.Bottom >= snap.MonitorWorkArea.Bottom - 4;

            if (snap.IsZoomed || coversWorkArea) return DisplayObstruction.Maximized;
            return DisplayObstruction.None;
        }

        internal static WidgetVisibilityMode ResolveMode(DisplayObstruction obstruction, string fullscreenPolicy, bool compactOnMaximized) {
            string policy = NormalizePolicy(fullscreenPolicy);
            if (obstruction == DisplayObstruction.Fullscreen) {
                if (policy == "hide") return WidgetVisibilityMode.Hidden;
                if (policy == "edge") return WidgetVisibilityMode.EdgeStrip;
                return WidgetVisibilityMode.Normal;
            }
            if (obstruction == DisplayObstruction.UncertainFullscreen) {
                // Uncertain fullscreen signal never hides permanently; falls back to slim edge strip.
                if (policy == "hide" || policy == "edge") return WidgetVisibilityMode.EdgeStrip;
                return WidgetVisibilityMode.Normal;
            }
            if (obstruction == DisplayObstruction.Maximized && compactOnMaximized) {
                return WidgetVisibilityMode.EdgeStrip;
            }
            return WidgetVisibilityMode.Normal;
        }

        internal static string NormalizePolicy(string policy) {
            if (policy == "edge" || policy == "ignore") return policy;
            return "hide";
        }

        internal static ForegroundSnapshot Capture(Screen widgetScreen) {
            var snap = new ForegroundSnapshot { NotificationState = 5 };
            try {
                int ns;
                if (SHQueryUserNotificationState(out ns) == 0) snap.NotificationState = ns;
            } catch (EntryPointNotFoundException) { } catch (DllNotFoundException) { }

            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return snap;
            snap.HasWindow = true;
            if (fg == GetShellWindow() || fg == GetDesktopWindow()) {
                snap.IsShellOrDesktop = true;
                return snap;
            }

            uint pid;
            GetWindowThreadProcessId(fg, out pid);
            snap.IsOwnProcess = (int)pid == OwnProcessId;
            if (snap.IsOwnProcess) return snap;

            var cls = new StringBuilder(128);
            if (GetClassName(fg, cls, cls.Capacity) > 0 && IsShellClassName(cls.ToString())) {
                snap.IsShellOrDesktop = true;
                return snap;
            }

            snap.IsVisible = IsWindowVisible(fg);
            snap.IsMinimized = IsIconic(fg);
            snap.IsZoomed = IsZoomed(fg);
            if (!snap.IsVisible || snap.IsMinimized) return snap;

            NativeRect rect = new NativeRect();
            bool gotRect = false;
            try {
                if (DwmGetWindowAttribute(fg, 9, out rect, Marshal.SizeOf(typeof(NativeRect))) == 0 &&
                    rect.Right > rect.Left && rect.Bottom > rect.Top) {
                    gotRect = true;
                }
            } catch (EntryPointNotFoundException) { } catch (DllNotFoundException) { }
            if (!gotRect && !GetWindowRect(fg, out rect)) return snap;

            snap.WindowRect = rect.ToRectangle();
            var targetScreen = widgetScreen ?? Screen.PrimaryScreen;
            IntPtr fgMon, targetMon;
            Rectangle fgMonBounds, fgMonWork, targetBounds, targetWork;
            bool gotFgMon = TryGetMonitorRects(fg, out fgMon, out fgMonBounds, out fgMonWork);
            bool gotTargetMon = TryGetMonitorRectsForScreen(targetScreen, out targetMon, out targetBounds, out targetWork);
            if (gotTargetMon) {
                snap.MonitorBounds = targetBounds;
                snap.MonitorWorkArea = targetWork;
            } else if (targetScreen != null) {
                snap.MonitorBounds = targetScreen.Bounds;
                snap.MonitorWorkArea = targetScreen.WorkingArea;
            }
            if (gotFgMon && gotTargetMon) {
                snap.SameMonitor = fgMon == targetMon;
            } else {
                var fgScreen = Screen.FromHandle(fg);
                snap.SameMonitor = fgScreen != null && targetScreen != null && fgScreen.Bounds == targetScreen.Bounds;
            }
            return snap;
        }
    }
}
