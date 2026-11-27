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
    //   positioned adjacent to the notification tray on the primary taskbar (bottom/top/left/right).
    // - Strict environment gating & self-recovery: auto-hide taskbar, missing/third-party shell,
    //   fullscreen obstruction, insufficient free space, or persistent commit/Z-order failures degrade
    //   cleanly by hiding the overlay, auto-restoring the tray icon, and surfacing explicit diagnostics.
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
        internal bool TaskbarCoveredByFullscreen;
        internal int NotificationState; // SHQueryUserNotificationState: 2=BUSY, 3=D3D_FULLSCREEN, 4=PRESENTATION, 5=ACCEPTS
        internal bool SelfBehindTaskbar; // True when selfHandle sits below Shell_TrayWnd in EnumWindows top-to-bottom Z-order
        internal bool SelfCloaked; // True when DWMWA_CLOAKED reports non-zero on selfHandle
        internal List<Rectangle> OccupiedRects;
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

        const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        const int DWMWA_CLOAKED = 14;
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x00000020;
        const int WS_EX_LAYERED = 0x00080000;
        const int WS_EX_NOACTIVATE = 0x08000000;

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        static extern IntPtr GetDesktopWindow();

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentProcessId();

        [DllImport("dwmapi.dll", PreserveSig = true)]
        static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out NativeRect pvAttribute, int cbAttribute);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        static readonly HashSet<string> ShellWindowClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW",
            "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
            "Windows.UI.Core.CoreWindow"
        };

        static readonly HashSet<string> ExplorerNativeChildClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "Shell_TrayWnd", "ReBarWindow32", "MSTaskSwWClass", "MSTaskListWClass",
            "TrayNotifyWnd", "Start", "TrayButton", "TrayShowDesktopButtonWClass",
            "TrayDummySearchControl", "InputIndicatorButton",
            "Windows.UI.Composition.DesktopWindowContentBridge", "DesktopWindowXamlSource",
            "SysPager", "ToolbarWindow32", "Button", "ClockButton", "TrayClockWClass", "CiceroUIWndFrame"
        };

        static string ReadClassName(IntPtr hwnd) {
            var sb = new System.Text.StringBuilder(256);
            int len = GetClassName(hwnd, sb, sb.Capacity);
            return len > 0 ? sb.ToString() : "";
        }

        static bool IsWindowCloaked(IntPtr hwnd) {
            try {
                int cloaked = 0;
                return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0;
            } catch (DllNotFoundException) {
                return false;
            } catch (EntryPointNotFoundException) {
                return false;
            }
        }

        static bool TryReadVisibleRect(IntPtr hwnd, out Rectangle rect) {
            rect = Rectangle.Empty;
            NativeRect nr = new NativeRect();
            try {
                if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out nr, Marshal.SizeOf(typeof(NativeRect))) == 0 &&
                    nr.Right > nr.Left && nr.Bottom > nr.Top) {
                    rect = nr.ToRectangle();
                    return true;
                }
            } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
            if (!GetWindowRect(hwnd, out nr)) return false;
            rect = nr.ToRectangle();
            return rect.Width > 0 && rect.Height > 0;
        }

        static bool IsPassiveOverlayStyle(IntPtr hwnd) {
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((ex & WS_EX_NOACTIVATE) != 0) return true;
            if ((ex & WS_EX_TRANSPARENT) != 0 && (ex & WS_EX_LAYERED) != 0) return true;
            return false;
        }

        internal static bool IsVerticalEdge(int edge) {
            return edge == EdgeLeft || edge == EdgeRight;
        }

        internal static bool IsHorizontalEdge(int edge) {
            return edge == EdgeBottom || edge == EdgeTop;
        }

        internal static int ResolveTaskbarEdge(int appBarEdge, Rectangle taskbarRect, Rectangle monitorBounds) {
            if (taskbarRect.Width > 0 && taskbarRect.Height > 0 && monitorBounds.Width > 0 && monitorBounds.Height > 0) {
                if (taskbarRect.Width > taskbarRect.Height) {
                    bool nearTop = taskbarRect.Top <= monitorBounds.Top + 4 && taskbarRect.Bottom < monitorBounds.Bottom - 4;
                    bool nearBottom = taskbarRect.Bottom >= monitorBounds.Bottom - 4 && taskbarRect.Top > monitorBounds.Top + 4;
                    if (nearTop && !nearBottom) return EdgeTop;
                    if (nearBottom && !nearTop) return EdgeBottom;
                } else if (taskbarRect.Height > taskbarRect.Width) {
                    bool nearLeft = taskbarRect.Left <= monitorBounds.Left + 4 && taskbarRect.Right < monitorBounds.Right - 4;
                    bool nearRight = taskbarRect.Right >= monitorBounds.Right - 4 && taskbarRect.Left > monitorBounds.Left + 4;
                    if (nearLeft && !nearRight) return EdgeLeft;
                    if (nearRight && !nearLeft) return EdgeRight;
                }
            }
            return appBarEdge;
        }

        internal static bool IsFullscreenCoveringTaskbar(Rectangle windowRect, Rectangle monitorBounds, Rectangle taskbarRect) {
            if (monitorBounds.Width <= 0 || monitorBounds.Height <= 0 || taskbarRect.Width <= 0 || taskbarRect.Height <= 0) return false;
            bool coversFullMonitor =
                windowRect.Left <= monitorBounds.Left + 4 &&
                windowRect.Top <= monitorBounds.Top + 4 &&
                windowRect.Right >= monitorBounds.Right - 4 &&
                windowRect.Bottom >= monitorBounds.Bottom - 4;
            if (coversFullMonitor) return true;

            Rectangle barOverlap = Rectangle.Intersect(windowRect, taskbarRect);
            Rectangle monOverlap = Rectangle.Intersect(windowRect, monitorBounds);
            if (taskbarRect.Height > taskbarRect.Width) {
                return barOverlap.Height >= (taskbarRect.Height * 3) / 4 &&
                       barOverlap.Width >= Math.Max(1, taskbarRect.Width - 8) &&
                       monOverlap.Width >= (monitorBounds.Width * 3) / 4;
            }
            return barOverlap.Width >= (taskbarRect.Width * 3) / 4 &&
                   barOverlap.Height >= Math.Max(1, taskbarRect.Height - 8) &&
                   monOverlap.Height >= (monitorBounds.Height * 3) / 4;
        }

        static void ProbeTaskbarWindows(IntPtr trayWnd, IntPtr selfHandle, Rectangle monitorBounds, Rectangle bar, bool hasTrayNotify, Rectangle trayNotifyRect, List<Rectangle> occupied, out bool taskbarCoveredByFullscreen, out bool selfBehindTaskbar) {
            taskbarCoveredByFullscreen = false;
            selfBehindTaskbar = false;
            if (trayWnd == IntPtr.Zero || bar.Width <= 0 || bar.Height <= 0 || occupied == null) return;
            bool verticalBar = bar.Height > bar.Width;
            uint currentPid = GetCurrentProcessId();
            IntPtr shellWnd = GetShellWindow();
            IntPtr desktopWnd = GetDesktopWindow();
            bool reachedTaskbar = false;
            bool covered = false;
            bool behind = false;
            try {
                EnumWindows(delegate(IntPtr hwnd, IntPtr lParam) {
                    if (hwnd == IntPtr.Zero) return true;
                    if (selfHandle != IntPtr.Zero && hwnd == selfHandle) {
                        if (reachedTaskbar) behind = true;
                        return true;
                    }
                    if (hwnd == trayWnd) {
                        reachedTaskbar = true;
                        return true;
                    }
                    if (hwnd == shellWnd || hwnd == desktopWnd) return true;
                    uint pid = 0;
                    GetWindowThreadProcessId(hwnd, out pid);
                    if (pid == currentPid) return true;
                    if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsWindowCloaked(hwnd)) return true;
                    string cls = ReadClassName(hwnd);
                    if (ShellWindowClasses.Contains(cls) || DesktopDisplayWatch.IsShellClassName(cls)) return true;
                    Rectangle r;
                    if (!TryReadVisibleRect(hwnd, out r)) return true;

                    // Any interactive top-level window sitting above Shell_TrayWnd in Z-order that covers
                    // the primary monitor (or covers the taskbar while spanning most of the screen) is a
                    // fullscreen game/video/presentation and must hide our topmost taskbar overlay.
                    if (!reachedTaskbar && !covered && !IsPassiveOverlayStyle(hwnd) && IsFullscreenCoveringTaskbar(r, monitorBounds, bar)) {
                        covered = true;
                    }

                    if (!reachedTaskbar &&
                        r.Bottom > bar.Top && r.Top < bar.Bottom &&
                        r.Right > bar.Left && r.Left < bar.Right) {
                        bool isPluginBand = verticalBar
                            ? (r.Width > 0 && r.Width <= bar.Width + 16 && r.Height >= 16 && r.Height <= (bar.Height * 2) / 3)
                            : (r.Height > 0 && r.Height <= bar.Height + 16 && r.Width >= 16 && r.Width <= (bar.Width * 2) / 3);
                        if (isPluginBand) occupied.Add(r);
                    }
                    return true;
                }, IntPtr.Zero);
            } catch (Exception) { }
            taskbarCoveredByFullscreen = reachedTaskbar && covered;
            selfBehindTaskbar = reachedTaskbar && behind;

            try {
                int horizontalGuardStart = bar.Left + bar.Width / 3;
                int verticalGuardStart = bar.Top + bar.Height / 3;
                EnumChildWindows(trayWnd, delegate(IntPtr hwnd, IntPtr lParam) {
                    if (hwnd == IntPtr.Zero || hwnd == selfHandle) return true;
                    if (!IsWindowVisible(hwnd)) return true;
                    string cls = ReadClassName(hwnd);
                    if (ExplorerNativeChildClasses.Contains(cls)) return true;
                    NativeRect nr;
                    if (!GetWindowRect(hwnd, out nr)) return true;
                    Rectangle r = nr.ToRectangle();
                    if (verticalBar) {
                        if (hasTrayNotify && r.Top >= trayNotifyRect.Top - 4) return true;
                        if (r.Width > 0 && r.Width <= bar.Width + 4 &&
                            r.Height >= 16 && r.Height <= (bar.Height * 2) / 3 &&
                            r.Bottom > verticalGuardStart && r.Top < bar.Bottom) {
                            occupied.Add(r);
                        }
                    } else {
                        if (hasTrayNotify && r.Left >= trayNotifyRect.Left - 4) return true;
                        if (r.Height > 0 && r.Height <= bar.Height + 4 &&
                            r.Width >= 16 && r.Width <= (bar.Width * 2) / 3 &&
                            r.Right > horizontalGuardStart && r.Left < bar.Right) {
                            occupied.Add(r);
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            } catch (Exception) { }
        }

        internal static TaskbarEnvironmentSnapshot Capture() {
            return Capture(IntPtr.Zero);
        }

        internal static TaskbarEnvironmentSnapshot Capture(IntPtr selfHandle) {
            var snap = new TaskbarEnvironmentSnapshot {
                TaskbarEdge = -1,
                PrimaryMonitorBounds = Screen.PrimaryScreen != null ? Screen.PrimaryScreen.Bounds : new Rectangle(0, 0, 1920, 1080),
                Obstruction = DisplayObstruction.None,
                NotificationState = 5,
                OccupiedRects = new List<Rectangle>()
            };

            IntPtr trayWnd = FindWindow("Shell_TrayWnd", null);
            if (trayWnd == IntPtr.Zero) return snap;
            snap.HasTaskbarWindow = true;
            snap.TaskbarVisible = IsWindowVisible(trayWnd);

            Screen taskbarScreen = Screen.FromHandle(trayWnd) ?? Screen.PrimaryScreen;
            IntPtr trayMon;
            Rectangle liveMonBounds, liveMonWork;
            if (DesktopDisplayWatch.TryGetMonitorRects(trayWnd, out trayMon, out liveMonBounds, out liveMonWork)) {
                snap.PrimaryMonitorBounds = liveMonBounds;
            } else if (taskbarScreen != null) {
                snap.PrimaryMonitorBounds = taskbarScreen.Bounds;
            }

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

            if (snap.TaskbarRect.Width > 0 && snap.TaskbarRect.Height > 0) {
                snap.TaskbarEdge = ResolveTaskbarEdge(snap.TaskbarEdge, snap.TaskbarRect, snap.PrimaryMonitorBounds);
            }

            try {
                var fg = DesktopDisplayWatch.Capture(taskbarScreen);
                snap.NotificationState = fg.NotificationState;
                snap.Obstruction = DesktopDisplayWatch.Evaluate(fg);
            } catch (Exception) { }

            bool coveredByFullscreen;
            bool selfBehindTaskbar;
            ProbeTaskbarWindows(trayWnd, selfHandle, snap.PrimaryMonitorBounds, snap.TaskbarRect, snap.HasTrayNotifyRect, snap.TrayNotifyRect, snap.OccupiedRects, out coveredByFullscreen, out selfBehindTaskbar);
            snap.TaskbarCoveredByFullscreen = coveredByFullscreen;
            snap.SelfBehindTaskbar = selfBehindTaskbar;
            snap.SelfCloaked = selfHandle != IntPtr.Zero && IsWindowCloaked(selfHandle);

            return snap;
        }
    }

    internal static class TaskbarOverlayEvaluator {
        internal const int MinOffsetX = -1200;
        const int GapBeforeNotifyPx = 10;
        const int GapBetweenPluginsPx = 8;
        const int MaxAvoidanceIterations = 16;
        const int MinLeftGuardRatioDenominator = 3; // Reserve left/top 1/3 for Start/search/pinned apps

        internal static bool TryComputeHorizontalBounds(TaskbarEnvironmentSnapshot snap, Size overlayPhysicalSize, out int safeMinX, out int maxAllowedX, out int y, out TaskbarOverlayPlacement failure) {
            bool isVertical;
            if (!TryComputeBounds(snap, overlayPhysicalSize, out isVertical, out safeMinX, out maxAllowedX, out y, out failure)) {
                return false;
            }
            if (isVertical) {
                safeMinX = 0;
                maxAllowedX = 0;
                y = 0;
                failure = Fail("non_horizontal_taskbar", "当前任务栏为垂直侧边模式。");
                return false;
            }
            return true;
        }

        internal static bool TryComputeBounds(TaskbarEnvironmentSnapshot snap, Size overlayPhysicalSize, out bool isVertical, out int safeMinAxis, out int maxAllowedAxis, out int crossPos, out TaskbarOverlayPlacement failure) {
            isVertical = false;
            safeMinAxis = 0;
            maxAllowedAxis = 0;
            crossPos = 0;
            if (snap == null || !snap.HasTaskbarWindow) {
                failure = Fail("no_explorer_taskbar", "未检测到 Windows 任务栏（可能使用第三方 Shell），已自动隐藏。");
                return false;
            }
            if (!snap.TaskbarVisible) {
                failure = Fail("taskbar_hidden", "当前任务栏不可见，叠加层已自动隐藏。");
                return false;
            }
            if (snap.AutoHideEnabled) {
                failure = Fail("auto_hide_taskbar", "当前系统启用了自动隐藏任务栏，为避免悬空遮挡已自动降级隐藏。");
                return false;
            }
            bool horizontal = TaskbarEnvironmentProbe.IsHorizontalEdge(snap.TaskbarEdge);
            bool vertical = TaskbarEnvironmentProbe.IsVerticalEdge(snap.TaskbarEdge);
            if (!horizontal && !vertical) {
                failure = Fail("unsupported_taskbar_edge", "未识别的任务栏停靠边缘，已自动降级隐藏。");
                return false;
            }
            isVertical = vertical;
            Rectangle bar = snap.TaskbarRect;
            if (vertical) {
                if (bar.Height < 320 || bar.Width < 40 || bar.Width > 160) {
                    failure = Fail("invalid_taskbar_bounds", "当前侧边任务栏尺寸异常，已自动隐藏以防止错位。");
                    return false;
                }
            } else {
                if (bar.Width < 320 || bar.Height < 24 || bar.Height > 120) {
                    failure = Fail("invalid_taskbar_bounds", "当前任务栏尺寸异常，已自动隐藏以防止错位。");
                    return false;
                }
            }
            if (snap.Obstruction == DisplayObstruction.Fullscreen ||
                snap.Obstruction == DisplayObstruction.UncertainFullscreen ||
                snap.TaskbarCoveredByFullscreen ||
                snap.NotificationState == 3 ||
                snap.NotificationState == 4) {
                failure = Fail("fullscreen_hidden", "检测到全屏应用或游戏，任务栏电量已自动隐藏。");
                return false;
            }

            int w = Math.Max(1, overlayPhysicalSize.Width);
            int h = Math.Max(1, overlayPhysicalSize.Height);
            if (vertical) {
                if (w > bar.Width) {
                    failure = Fail("invalid_taskbar_bounds", "侧边任务栏宽度不足以容纳电量条，已自动隐藏。");
                    return false;
                }
                int bottomAnchor = (snap.HasTrayNotifyRect && snap.TrayNotifyRect.Top > bar.Top + 80 && snap.TrayNotifyRect.Top < bar.Bottom)
                    ? snap.TrayNotifyRect.Top - GapBeforeNotifyPx
                    : bar.Bottom - Math.Max(120, bar.Height / 6);
                safeMinAxis = bar.Top + Math.Max(120, bar.Height / MinLeftGuardRatioDenominator);
                maxAllowedAxis = bottomAnchor - h;
                if (maxAllowedAxis < safeMinAxis) {
                    failure = Fail("insufficient_space", "侧边任务栏通知区上方空间不足，已自动隐藏以避免遮挡图标。");
                    return false;
                }
                crossPos = bar.Left + (bar.Width - w) / 2;
            } else {
                if (h > bar.Height) {
                    failure = Fail("invalid_taskbar_bounds", "任务栏高度不足以容纳电量条，已自动隐藏。");
                    return false;
                }
                int rightAnchor = (snap.HasTrayNotifyRect && snap.TrayNotifyRect.Left > bar.Left + 80 && snap.TrayNotifyRect.Left < bar.Right)
                    ? snap.TrayNotifyRect.Left - GapBeforeNotifyPx
                    : bar.Right - Math.Max(140, bar.Width / 8);
                safeMinAxis = bar.Left + Math.Max(120, bar.Width / MinLeftGuardRatioDenominator);
                maxAllowedAxis = rightAnchor - w;
                if (maxAllowedAxis < safeMinAxis) {
                    failure = Fail("insufficient_space", "任务栏通知区左侧空间不足，已自动隐藏以避免遮挡图标。");
                    return false;
                }
                crossPos = bar.Top + (bar.Height - h) / 2;
            }

            failure = null;
            return true;
        }

        internal static TaskbarOverlayPlacement Evaluate(TaskbarEnvironmentSnapshot snap, Size overlayPhysicalSize, int offsetX) {
            bool isVertical;
            int safeMinAxis, maxAllowedAxis, crossPos;
            TaskbarOverlayPlacement failure;
            if (!TryComputeBounds(snap, overlayPhysicalSize, out isVertical, out safeMinAxis, out maxAllowedAxis, out crossPos, out failure)) {
                return failure;
            }

            int span = Math.Max(1, isVertical ? overlayPhysicalSize.Height : overlayPhysicalSize.Width);
            int clampedOffset = Math.Max(MinOffsetX, Math.Min(0, offsetX));
            int pos = maxAllowedAxis + clampedOffset;
            pos = Math.Max(safeMinAxis, Math.Min(maxAllowedAxis, pos));

            bool avoided = false;
            if (snap.OccupiedRects != null && snap.OccupiedRects.Count > 0) {
                int maxIters = Math.Max(MaxAvoidanceIterations, snap.OccupiedRects.Count + 1);
                for (int iter = 0; iter < maxIters; iter++) {
                    bool hit = false;
                    for (int i = 0; i < snap.OccupiedRects.Count; i++) {
                        Rectangle occ = snap.OccupiedRects[i];
                        if (occ.Width <= 0 || occ.Height <= 0) continue;
                        int startBound = (isVertical ? occ.Top : occ.Left) - GapBetweenPluginsPx;
                        int endBound = (isVertical ? occ.Bottom : occ.Right) + GapBetweenPluginsPx;
                        if (pos < endBound && (pos + span) > startBound) {
                            pos = (isVertical ? occ.Top : occ.Left) - GapBetweenPluginsPx - span;
                            avoided = true;
                            hit = true;
                            break;
                        }
                    }
                    if (!hit || pos < safeMinAxis) break;
                }
                if (pos < safeMinAxis) {
                    return Fail("plugin_conflict", isVertical
                        ? "检测到侧边任务栏其他插件占用空间且上方余量不足，已自动避让隐藏。"
                        : "检测到任务栏其他插件占用空间且左侧余量不足，已自动避让隐藏。");
                }
            }

            string edgeText = snap.TaskbarEdge == TaskbarEnvironmentProbe.EdgeTop ? "顶部"
                : snap.TaskbarEdge == TaskbarEnvironmentProbe.EdgeLeft ? "左侧"
                : snap.TaskbarEdge == TaskbarEnvironmentProbe.EdgeRight ? "右侧"
                : "底部";
            string anchorText = isVertical ? "通知区上方" : "通知区左侧";
            return new TaskbarOverlayPlacement {
                CanDisplay = true,
                StatusCode = avoided ? "ready_avoided" : "ready",
                StatusMessage = avoided
                    ? "运行正常 · 已自动避让" + edgeText + "任务栏其他插件"
                    : "运行正常 · 已贴合在主屏" + edgeText + "任务栏" + anchorText + "（独立无焦点层）",
                PhysicalLocation = isVertical ? new Point(crossPos, pos) : new Point(pos, crossPos)
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
            return ComputeLogicalSize(items, showIcons, false, 56f);
        }

        internal static Size ComputeLogicalSize(List<Item> items, bool showIcons, bool vertical, float taskbarLogicalThickness) {
            int count = items == null || items.Count == 0 ? 1 : items.Count;
            if (vertical) {
                int maxAllowed = Math.Max(26, (int)Math.Floor(taskbarLogicalThickness) - 4);
                int width = Math.Min(64, Math.Max(26, Math.Min(maxAllowed, (int)Math.Floor(Math.Max(26f, taskbarLogicalThickness)) - 6)));
                if (width > maxAllowed) width = maxAllowed;
                int itemH = showIcons ? 42 : 26;
                int padY = 6;
                int gap = 4;
                int height = padY * 2 + count * itemH + Math.Max(0, count - 1) * gap;
                return new Size(width, height);
            }
            int itemW = showIcons ? 66 : 46;
            int padX = 8;
            int hGap = 6;
            int totalW = padX * 2 + count * itemW + Math.Max(0, count - 1) * hGap;
            return new Size(totalW, 28);
        }

        internal static void Paint(Graphics g, SizeF size, List<Item> items, bool showIcons, ThemePalette palette, bool dark) {
            Paint(g, size, items, showIcons, palette, dark, false);
        }

        internal static void Paint(Graphics g, SizeF size, List<Item> items, bool showIcons, ThemePalette palette, bool dark, bool vertical) {
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
                CompactRenderer.DrawLabel(g, vertical ? "—" : "外设 —", new RectangleF(4, 0, size.Width - 8, size.Height), vertical ? 11f : 11.5f, false, palette.Muted, StringAlignment.Center);
                return;
            }

            if (vertical) {
                float itemH = showIcons ? 42f : 26f;
                float gap = 4f;
                float y = 6f;
                float fontSize = size.Width < 40f ? 9.5f : (size.Width < 46f ? 10f : 11f);
                float sideMargin = size.Width < 40f ? 3f : 6f;
                for (int i = 0; i < items.Count; i++) {
                    var it = items[i];
                    Color fg = it.Percent.HasValue
                        ? (it.Fresh ? ThemePalette.LevelColor(it.Percent, dark) : palette.Muted)
                        : palette.Muted;
                    if (VisualDesign.HighContrast) fg = SystemColors.WindowText;

                    float textTop = y;
                    if (showIcons) {
                        Color iconFg = VisualDesign.HighContrast ? SystemColors.WindowText : (it.Low ? fg : palette.Foreground);
                        float iconSize = size.Width < 40f ? 11.5f : 13f;
                        CompactRenderer.DrawSymbol(g, ControlCenterTokens.SymbolForKind(it.Kind), new RectangleF((size.Width - iconSize) / 2f, y + 2f, iconSize, iconSize), iconFg);
                        textTop = y + 17f;
                    }

                    string pct = it.Percent.HasValue ? (it.Charging ? it.Percent.Value + "%+" : it.Percent.Value + "%") : "--";
                    CompactRenderer.DrawLabel(g, pct, new RectangleF(1f, textTop, size.Width - 2f, 16f), fontSize, true, fg, StringAlignment.Center);

                    float barY = showIcons ? (y + itemH - 5.5f) : (y + itemH - 5f);
                    var barRect = new RectangleF(sideMargin, barY, Math.Max(14f, size.Width - sideMargin * 2f), 2.5f);
                    CompactRenderer.Fill(g, barRect, VisualDesign.HighContrast ? SystemColors.ControlDark : palette.Track, 1.25f);
                    if (it.Percent.HasValue && it.Percent.Value > 0) {
                        float fillW = barRect.Width * Math.Max(0, Math.Min(100, it.Percent.Value)) / 100f;
                        if (fillW > 0.5f) CompactRenderer.Fill(g, new RectangleF(barRect.X, barRect.Y, fillW, barRect.Height), fg, 1.25f);
                    }

                    if (i < items.Count - 1) {
                        float sepY = y + itemH + gap / 2f;
                        using (var pen = new Pen(Color.FromArgb(dark ? 70 : 60, palette.Border), 1f)) {
                            g.DrawLine(pen, 7f, sepY, size.Width - 7f, sepY);
                        }
                    }
                    y += itemH + gap;
                }
                return;
            }

            float itemW = showIcons ? 66f : 46f;
            float hGap = 6f;
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
                    // Frameless line icon: no rounded badge box, extra breathing room before the percent.
                    Color iconFg = VisualDesign.HighContrast ? SystemColors.WindowText : (it.Low ? fg : palette.Foreground);
                    CompactRenderer.DrawSymbol(g, ControlCenterTokens.SymbolForKind(it.Kind), new RectangleF(x + 1f, 6f, 14f, 14f), iconFg);
                    textX = x + 22f;
                    textW = itemW - 22f;
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
                    float sepX = x + itemW + hGap / 2f;
                    using (var pen = new Pen(Color.FromArgb(dark ? 70 : 60, palette.Border), 1f)) {
                        g.DrawLine(pen, sepX, 6f, sepX, size.Height - 6f);
                    }
                }
                x += itemW + hGap;
            }
        }
    }

    internal enum TaskbarOverlayRecoveryAction {
        None,
        PromoteZOrder,
        RecreateHandle,
        DegradeCommitFailed,
        DegradeOccluded
    }

    internal delegate bool TaskbarOverlayCommitFunc(
        IntPtr hwnd,
        Point physicalLocation,
        Size logicalSize,
        float dpiScale,
        byte opacity,
        Action<Graphics, SizeF> paint,
        out int win32Error);

    // Pure deterministic state machine governing layered-window commit failure retries,
    // Z-order re-promotion above Shell_TrayWnd, bounded handle recreation, and fail-safe degradation.
    internal sealed class TaskbarOverlayRecoveryPolicy {
        internal const int CommitFailuresBeforeRecreate = 2;
        internal const int BehindTicksBeforeRecreate = 3;
        internal const int DefaultMaxRecreates = 3;
        internal const int HealthyFramesBeforeBudgetReset = 150; // ~60s of continuous healthy 400ms ticks

        readonly int maxRecreates;
        int consecutiveCommitFailures;
        int consecutiveBehindTaskbar;
        int consecutiveHealthyFrames;
        int recreateCount;
        int lastWin32Error;
        bool degradedCommitFailed;
        bool degradedOccluded;

        internal TaskbarOverlayRecoveryPolicy(int maxRecreates = DefaultMaxRecreates) {
            this.maxRecreates = Math.Max(0, maxRecreates);
        }

        internal int ConsecutiveCommitFailures { get { return consecutiveCommitFailures; } }
        internal int ConsecutiveBehindTaskbar { get { return consecutiveBehindTaskbar; } }
        internal int RecreateCount { get { return recreateCount; } }
        internal int LastWin32Error { get { return lastWin32Error; } }
        internal bool IsDegradedCommitFailed { get { return degradedCommitFailed; } }
        internal bool IsDegradedOccluded { get { return degradedOccluded; } }
        internal bool IsPersistentlyDegraded { get { return degradedCommitFailed || degradedOccluded; } }

        internal TaskbarOverlayRecoveryAction ObserveCommitResult(bool succeeded, int win32Error) {
            if (succeeded) {
                consecutiveCommitFailures = 0;
                lastWin32Error = 0;
                degradedCommitFailed = false;
                return TaskbarOverlayRecoveryAction.None;
            }
            consecutiveHealthyFrames = 0;
            consecutiveCommitFailures++;
            lastWin32Error = win32Error;
            if (consecutiveCommitFailures >= CommitFailuresBeforeRecreate) {
                if (recreateCount < maxRecreates) {
                    recreateCount++;
                    consecutiveCommitFailures = 0;
                    return TaskbarOverlayRecoveryAction.RecreateHandle;
                }
                degradedCommitFailed = true;
            }
            return TaskbarOverlayRecoveryAction.DegradeCommitFailed;
        }

        internal TaskbarOverlayRecoveryAction ObserveVisibility(bool committedAndVisible, bool behindTaskbar) {
            if (!committedAndVisible) return TaskbarOverlayRecoveryAction.None;
            if (!behindTaskbar) {
                consecutiveBehindTaskbar = 0;
                degradedOccluded = false;
                return TaskbarOverlayRecoveryAction.None;
            }
            consecutiveHealthyFrames = 0;
            consecutiveBehindTaskbar++;
            if (consecutiveBehindTaskbar >= BehindTicksBeforeRecreate) {
                if (recreateCount < maxRecreates) {
                    recreateCount++;
                    consecutiveBehindTaskbar = 0;
                    return TaskbarOverlayRecoveryAction.RecreateHandle;
                }
                degradedOccluded = true;
                return TaskbarOverlayRecoveryAction.DegradeOccluded;
            }
            return TaskbarOverlayRecoveryAction.PromoteZOrder;
        }

        internal void RecordHealthyFrame() {
            consecutiveCommitFailures = 0;
            consecutiveBehindTaskbar = 0;
            lastWin32Error = 0;
            degradedCommitFailed = false;
            degradedOccluded = false;
            consecutiveHealthyFrames++;
            if (consecutiveHealthyFrames >= HealthyFramesBeforeBudgetReset) {
                recreateCount = 0;
            }
        }

        internal void ResetForManualRecreate() {
            consecutiveCommitFailures = 0;
            consecutiveBehindTaskbar = 0;
            consecutiveHealthyFrames = 0;
            recreateCount = 0;
            lastWin32Error = 0;
            degradedCommitFailed = false;
            degradedOccluded = false;
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
        static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOACTIVATE = 0x0010;
        const uint SWP_NOOWNERZORDER = 0x0200;

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        const int SW_HIDE = 0;
        const int SW_SHOWNOACTIVATE = 4;

        readonly TrayContext owner;
        readonly LayeredSurface surface = new LayeredSurface();
        readonly System.Windows.Forms.Timer watchTimer = new System.Windows.Forms.Timer { Interval = 400 };
        readonly System.Windows.Forms.Timer startupVerifyTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        readonly Func<TaskbarEnvironmentSnapshot> probeFunc;
        readonly TaskbarOverlayCommitFunc commitFunc;
        readonly TaskbarOverlayRecoveryPolicy recoveryPolicy;
        readonly Action<string> recreateRequested;
        readonly ContextMenuStrip contextMenu = new ContextMenuStrip();

        List<TaskbarOverlayRenderer.Item> items = new List<TaskbarOverlayRenderer.Item>();
        TaskbarOverlayPlacement lastPlacement = new TaskbarOverlayPlacement { CanDisplay = false, StatusCode = "disabled", StatusMessage = "任务栏电量未启用" };
        float dpiScale = 1f;
        Point physicalLocation;
        bool overlayVisible;
        bool isVerticalLayout;
        bool evaluating;
        int lastCommitError;
        int zPromoteCount;
        IntPtr lastCommittedHandle;
        string lastRenderKey;
        bool pointerDown, dragging;
        Point dragStartScreen;
        int dragStartAxis, dragMinAxis, dragMaxAxis, dragAnchorMaxAxis;

        internal TaskbarBatteryOverlayForm(
            TrayContext owner,
            Func<TaskbarEnvironmentSnapshot> probe = null,
            TaskbarOverlayCommitFunc commitOverride = null,
            TaskbarOverlayRecoveryPolicy recoveryPolicy = null,
            Action<string> recreateRequested = null) {
            this.owner = owner;
            probeFunc = probe ?? delegate { return TaskbarEnvironmentProbe.Capture(IsHandleCreated ? Handle : IntPtr.Zero); };
            commitFunc = commitOverride;
            this.recoveryPolicy = recoveryPolicy ?? new TaskbarOverlayRecoveryPolicy();
            this.recreateRequested = recreateRequested;

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
            startupVerifyTimer.Tick += delegate {
                startupVerifyTimer.Stop();
                if (!IsDisposed) RefreshNow(true);
            };
            MouseDown += HandleMouseDown;
            MouseMove += HandleMouseMove;
            MouseUp += HandleMouseUp;
            MouseCaptureChanged += HandleMouseCaptureChanged;
        }

        internal TaskbarOverlayPlacement LastPlacement { get { return lastPlacement; } }
        internal bool OverlayVisible { get { return overlayVisible; } }
        internal int LastCommitError { get { return lastCommitError; } }
        internal int ZPromoteCount { get { return zPromoteCount; } }
        internal IntPtr LastCommittedHandle { get { return lastCommittedHandle; } }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e) {
            base.OnHandleCreated(e);
            lastCommittedHandle = IntPtr.Zero;
            lastRenderKey = null;
        }

        protected override void OnHandleDestroyed(EventArgs e) {
            lastCommittedHandle = IntPtr.Zero;
            lastRenderKey = null;
            overlayVisible = false;
            base.OnHandleDestroyed(e);
        }

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
                if (!IsDisposed) {
                    if (m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_DPICHANGED) {
                        recoveryPolicy.ResetForManualRecreate();
                    }
                    EvaluateAndRender();
                }
            }
        }

        void BuildContextMenu() {
            ThemedContextMenu.Apply(contextMenu);
            contextMenu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e) {
                contextMenu.Items.Clear();
                var pref = DeviceSettings.Current;
                var openPanel = new ToolStripMenuItem("打开设备电量面板");
                openPanel.Click += delegate { if (owner != null) owner.ShowDetailsFromOverlay(); };
                var openCc = new ToolStripMenuItem("打开控制中心 → 显示位置");
                openCc.Click += delegate { if (owner != null) owner.ShowControlCenter(ControlCenterPage.Placement); };
                var nudgeLeft = new ToolStripMenuItem(isVerticalLayout ? "向上避让（-40px）" : "向左避让（-40px）");
                nudgeLeft.Click += delegate {
                    if (owner == null) return;
                    var p = DeviceSettings.Current;
                    p.TaskbarOverlay.OffsetX = Math.Max(TaskbarOverlayEvaluator.MinOffsetX, Math.Min(0, p.TaskbarOverlay.OffsetX - 40));
                    try { owner.SavePreferences(p); owner.ConfigurationChanged(false); } catch (Exception) { }
                };
                var resetOffset = new ToolStripMenuItem("重置默认位置");
                resetOffset.Click += delegate {
                    if (owner == null) return;
                    var p = DeviceSettings.Current;
                    p.TaskbarOverlay.OffsetX = 0;
                    try { owner.SavePreferences(p); owner.ConfigurationChanged(false); } catch (Exception) { }
                };
                var toggleIcons = new ToolStripMenuItem("切换图标显示");
                toggleIcons.Click += delegate {
                    if (owner == null) return;
                    var p = DeviceSettings.Current;
                    p.TaskbarOverlay.ShowIcons = !p.TaskbarOverlay.ShowIcons;
                    try { owner.SavePreferences(p); owner.ConfigurationChanged(false); } catch (Exception) { }
                };
                var toggleTray = new ToolStripMenuItem("显示托盘图标") { Checked = !pref.HideTrayIcon };
                toggleTray.Click += delegate {
                    if (owner == null) return;
                    var p = DeviceSettings.Current;
                    p.HideTrayIcon = !p.HideTrayIcon;
                    try { owner.SavePreferences(p); owner.ConfigurationChanged(false); } catch (Exception) { }
                };
                var disableItem = new ToolStripMenuItem("关闭任务栏电量");
                disableItem.Click += delegate {
                    if (owner == null) return;
                    var p = DeviceSettings.Current;
                    p.TaskbarOverlay.Enabled = false;
                    try { owner.SavePreferences(p); owner.ConfigurationChanged(false); } catch (Exception) { }
                };
                var exitItem = new ToolStripMenuItem("退出");
                exitItem.Click += delegate { if (owner != null) owner.ExitThread(); };

                contextMenu.Items.Add(openPanel);
                contextMenu.Items.Add(openCc);
                contextMenu.Items.Add(new ToolStripSeparator());
                contextMenu.Items.Add(nudgeLeft);
                contextMenu.Items.Add(resetOffset);
                contextMenu.Items.Add(toggleIcons);
                contextMenu.Items.Add(toggleTray);
                contextMenu.Items.Add(disableItem);
                contextMenu.Items.Add(new ToolStripSeparator());
                contextMenu.Items.Add(exitItem);
                e.Cancel = false;
            };
        }

        void HandleMouseDown(object sender, MouseEventArgs e) {
            if (e.Button != MouseButtons.Left || IsDisposed) return;
            pointerDown = true;
            dragging = false;
            dragStartScreen = Cursor.Position;
            var pref = DeviceSettings.Current;
            var cfg = pref.TaskbarOverlay ?? new TaskbarOverlaySettings();
            TaskbarEnvironmentSnapshot snap = probeFunc();
            dpiScale = NativeDisplay.ScaleAt(snap != null && snap.TaskbarRect.Width > 0 ? snap.TaskbarRect.Location : Point.Empty);
            bool vertical = snap != null && TaskbarEnvironmentProbe.IsVerticalEdge(snap.TaskbarEdge);
            isVerticalLayout = vertical;
            float thickness = snap != null && snap.TaskbarRect.Width > 0 ? (snap.TaskbarRect.Width / Math.Max(1f, dpiScale)) : 56f;
            Size logicalSize = TaskbarOverlayRenderer.ComputeLogicalSize(items, cfg.ShowIcons, vertical, thickness);
            Size physicalSize = LayeredSurface.ComputePhysicalSize(logicalSize, dpiScale);
            if (vertical && snap != null && snap.TaskbarRect.Width > 0 && physicalSize.Width > snap.TaskbarRect.Width - 2) {
                int clampedLogicalW = Math.Max(26, (int)Math.Floor((snap.TaskbarRect.Width - 2) / Math.Max(1f, dpiScale)));
                logicalSize = new Size(clampedLogicalW, logicalSize.Height);
                physicalSize = LayeredSurface.ComputePhysicalSize(logicalSize, dpiScale);
            }
            dragStartAxis = vertical ? physicalLocation.Y : physicalLocation.X;
            bool computedVertical;
            int safeMin, maxAllowed, unusedCross;
            TaskbarOverlayPlacement unusedFail;
            if (TaskbarOverlayEvaluator.TryComputeBounds(snap, physicalSize, out computedVertical, out safeMin, out maxAllowed, out unusedCross, out unusedFail)) {
                isVerticalLayout = computedVertical;
                dragStartAxis = computedVertical ? physicalLocation.Y : physicalLocation.X;
                dragMinAxis = Math.Max(safeMin, maxAllowed + TaskbarOverlayEvaluator.MinOffsetX);
                dragMaxAxis = maxAllowed;
                dragAnchorMaxAxis = maxAllowed;
            } else {
                dragMinAxis = dragStartAxis;
                dragMaxAxis = dragStartAxis;
                dragAnchorMaxAxis = dragStartAxis;
            }
            Capture = true;
        }

        void HandleMouseMove(object sender, MouseEventArgs e) {
            if (!pointerDown || IsDisposed) return;
            int delta = isVerticalLayout ? (Cursor.Position.Y - dragStartScreen.Y) : (Cursor.Position.X - dragStartScreen.X);
            if (!dragging && Math.Abs(delta) >= 6) dragging = true;
            if (!dragging) return;
            int nextAxis = Math.Max(dragMinAxis, Math.Min(dragMaxAxis, dragStartAxis + delta));
            int curAxis = isVerticalLayout ? physicalLocation.Y : physicalLocation.X;
            if (nextAxis == curAxis) return;
            physicalLocation = isVerticalLayout ? new Point(physicalLocation.X, nextAxis) : new Point(nextAxis, physicalLocation.Y);
            lastRenderKey = null;
            SetWindowPos(Handle, HWND_TOPMOST, physicalLocation.X, physicalLocation.Y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        void HandleMouseUp(object sender, MouseEventArgs e) {
            if (e.Button == MouseButtons.Left) {
                bool wasDown = pointerDown;
                bool wasDragging = dragging;
                pointerDown = false;
                dragging = false;
                if (Capture) Capture = false;
                if (wasDragging) {
                    int currentAxis = isVerticalLayout ? physicalLocation.Y : physicalLocation.X;
                    int newOffset = Math.Max(TaskbarOverlayEvaluator.MinOffsetX, Math.Min(0, currentAxis - dragAnchorMaxAxis));
                    if (owner != null) {
                        var p = DeviceSettings.Current;
                        p.TaskbarOverlay.OffsetX = newOffset;
                        try { owner.SavePreferences(p); owner.ConfigurationChanged(false); } catch (Exception) { }
                    }
                    RefreshNow(true);
                    return;
                }
                if (wasDown && owner != null) owner.ShowDetailsFromOverlay();
            } else if (e.Button == MouseButtons.Right) {
                contextMenu.Show(Cursor.Position);
            }
        }

        void HandleMouseCaptureChanged(object sender, EventArgs e) {
            if (!Capture && (pointerDown || dragging)) {
                pointerDown = false;
                dragging = false;
                if (!IsDisposed) RefreshNow(true);
            }
        }

        internal void StartWatching() {
            if (IsDisposed) return;
            var unused = Handle;
            EvaluateAndRender();
            watchTimer.Start();
            startupVerifyTimer.Stop();
            startupVerifyTimer.Start();
        }

        internal void RefreshNow(bool forceRepaint = true) {
            if (IsDisposed) return;
            if (forceRepaint) lastRenderKey = null;
            EvaluateAndRender();
        }

        bool CommitPixels(IntPtr hwnd, Point loc, Size logicalSize, float scale, byte opacity, Action<Graphics, SizeF> paint, out int win32Error) {
            if (commitFunc != null) {
                return commitFunc(hwnd, loc, logicalSize, scale, opacity, paint, out win32Error);
            }
            bool ok = surface.Commit(hwnd, loc, logicalSize, scale, opacity, paint);
            win32Error = ok ? 0 : surface.LastWin32Error;
            return ok;
        }

        void PromoteAboveTaskbar(bool bounce) {
            if (!IsHandleCreated || IsDisposed) return;
            uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
            if (bounce) {
                // Re-inserting through NOTOPMOST -> TOPMOST reliably lifts an already-topmost
                // overlay back above Shell_TrayWnd on Win11 without activating it.
                SetWindowPos(Handle, HWND_NOTOPMOST, 0, 0, 0, 0, flags);
                zPromoteCount++;
            }
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, flags);
        }

        void HideOverlayWindow() {
            overlayVisible = false;
            lastCommittedHandle = IntPtr.Zero;
            lastRenderKey = null;
            if (IsDisposed || Disposing) return;
            if (Visible) Hide();
            if (!IsDisposed && !Disposing && IsHandleCreated && IsWindowVisible(Handle)) ShowWindow(Handle, SW_HIDE);
        }

        void UpdatePlacement(TaskbarOverlayPlacement next) {
            string prevStatus = lastPlacement != null ? lastPlacement.StatusCode : null;
            string prevMessage = lastPlacement != null ? lastPlacement.StatusMessage : null;
            lastPlacement = next;
            if (prevStatus != next.StatusCode) {
                SafeDiagnostic.Transition(
                    "taskbar_overlay",
                    next.StatusCode +
                    (lastCommitError != 0 ? " err=" + lastCommitError : "") +
                    (recoveryPolicy.RecreateCount > 0 ? " recreates=" + recoveryPolicy.RecreateCount : ""));
            }
            if ((prevStatus != next.StatusCode || prevMessage != next.StatusMessage) && owner != null) {
                owner.NotifyTaskbarOverlayStatusChanged();
            }
        }

        static string BuildRenderKey(Point loc, Size logicalSize, float scale, bool dark, string themeStyle, bool showIcons, bool highContrast, bool vertical, List<TaskbarOverlayRenderer.Item> list) {
            var sb = new System.Text.StringBuilder();
            sb.Append(loc.X).Append(',').Append(loc.Y).Append('|')
              .Append(logicalSize.Width).Append('x').Append(logicalSize.Height).Append('|')
              .Append(Math.Round(scale * 100)).Append('|')
              .Append(dark).Append('|').Append(themeStyle ?? "").Append('|')
              .Append(showIcons).Append('|').Append(highContrast).Append('|').Append(vertical).Append('|');
            if (list != null) {
                for (int i = 0; i < list.Count; i++) {
                    var it = list[i];
                    sb.Append(it.Kind).Append(':').Append(it.Percent.HasValue ? it.Percent.Value.ToString() : "-").Append(':').Append(it.Fresh).Append(':').Append(it.Charging).Append(';');
                }
            }
            return sb.ToString();
        }

        void EvaluateAndRender() {
            if (IsDisposed || Disposing || dragging || evaluating) return;
            evaluating = true;
            try {
                EvaluateAndRenderCore();
            } finally {
                evaluating = false;
            }
        }

        void EvaluateAndRenderCore() {
            if (IsDisposed || Disposing || dragging) return;
            var pref = DeviceSettings.Current;
            var cfg = pref.TaskbarOverlay ?? new TaskbarOverlaySettings();
            cfg.Normalize();
            items = TaskbarOverlayRenderer.CollectItems(owner, cfg.MaxDevices);

            TaskbarEnvironmentSnapshot snap = probeFunc();
            dpiScale = NativeDisplay.ScaleAt(snap != null && snap.TaskbarRect.Width > 0 ? snap.TaskbarRect.Location : Point.Empty);
            bool vertical = snap != null && TaskbarEnvironmentProbe.IsVerticalEdge(snap.TaskbarEdge);
            isVerticalLayout = vertical;
            float thickness = snap != null && snap.TaskbarRect.Width > 0 ? (snap.TaskbarRect.Width / Math.Max(1f, dpiScale)) : 56f;
            Size logicalSize = TaskbarOverlayRenderer.ComputeLogicalSize(items, cfg.ShowIcons, vertical, thickness);
            Size physicalSize = LayeredSurface.ComputePhysicalSize(logicalSize, dpiScale);
            if (vertical && snap != null && snap.TaskbarRect.Width > 0 && physicalSize.Width > snap.TaskbarRect.Width - 2) {
                int clampedLogicalW = Math.Max(26, (int)Math.Floor((snap.TaskbarRect.Width - 2) / Math.Max(1f, dpiScale)));
                logicalSize = new Size(clampedLogicalW, logicalSize.Height);
                physicalSize = LayeredSurface.ComputePhysicalSize(logicalSize, dpiScale);
            }

            var placement = TaskbarOverlayEvaluator.Evaluate(snap, physicalSize, cfg.OffsetX);
            if (!placement.CanDisplay) {
                HideOverlayWindow();
                UpdatePlacement(placement);
                return;
            }

            if (recoveryPolicy.IsPersistentlyDegraded) {
                HideOverlayWindow();
                if (recoveryPolicy.IsDegradedOccluded) {
                    UpdatePlacement(new TaskbarOverlayPlacement {
                        CanDisplay = false,
                        StatusCode = "zorder_occluded",
                        StatusMessage = "任务栏浮层多次被系统任务栏遮挡，已自动恢复托盘图标；可在「诊断与反馈」点击「重建浮层」重试。"
                    });
                } else {
                    lastCommitError = recoveryPolicy.LastWin32Error;
                    UpdatePlacement(new TaskbarOverlayPlacement {
                        CanDisplay = false,
                        StatusCode = "commit_failed",
                        StatusMessage = "任务栏浮层像素提交失败（Win32 错误 " + lastCommitError + "），已自动恢复托盘图标；可在「诊断与反馈」点击「重建浮层」重试。"
                    });
                }
                return;
            }

            if (overlayVisible && snap != null && snap.SelfCloaked) {
                // DWM cloaked the window (e.g. shell/virtual-desktop transition). Do not treat as healthy
                // or spin recreations; restore tray icon via fail-safe status and force repaint once uncloaked.
                lastRenderKey = null;
                UpdatePlacement(new TaskbarOverlayPlacement {
                    CanDisplay = false,
                    StatusCode = "dwm_cloaked",
                    StatusMessage = "任务栏浮层当前被系统桌面合成器隐藏（Cloaked），已自动恢复托盘图标并等待恢复。"
                });
                return;
            }

            bool behindTaskbar = overlayVisible && snap != null && snap.SelfBehindTaskbar;
            bool forceZBounce = !overlayVisible || (lastPlacement != null && lastPlacement.StatusCode == "dwm_cloaked");
            if (behindTaskbar) {
                var zAction = recoveryPolicy.ObserveVisibility(true, true);
                if (zAction == TaskbarOverlayRecoveryAction.RecreateHandle && recreateRequested != null) {
                    SafeDiagnostic.Transition("taskbar_overlay", "recreate_zorder_occluded#" + recoveryPolicy.RecreateCount);
                    recreateRequested("zorder_occluded");
                    return;
                }
                if (zAction == TaskbarOverlayRecoveryAction.DegradeOccluded) {
                    HideOverlayWindow();
                    UpdatePlacement(new TaskbarOverlayPlacement {
                        CanDisplay = false,
                        StatusCode = "zorder_occluded",
                        StatusMessage = "任务栏浮层多次被系统任务栏遮挡，已自动恢复托盘图标；可在「诊断与反馈」点击「重建浮层」重试。"
                    });
                    return;
                }
                forceZBounce = true;
                lastRenderKey = null;
            } else if (overlayVisible) {
                recoveryPolicy.ObserveVisibility(true, false);
            }

            physicalLocation = placement.PhysicalLocation;
            bool dark = Style.TaskbarDark;
            ThemePalette palette = ThemePalette.Current(dark);
            string renderKey = BuildRenderKey(physicalLocation, logicalSize, dpiScale, dark, pref.ThemeStyle, cfg.ShowIcons, VisualDesign.HighContrast, vertical, items);

            IntPtr currentHandle = IsHandleCreated ? Handle : IntPtr.Zero;
            bool nativeVisible = currentHandle != IntPtr.Zero && IsWindowVisible(currentHandle);
            if (!behindTaskbar && !forceZBounce && overlayVisible && Visible && nativeVisible &&
                currentHandle == lastCommittedHandle && renderKey == lastRenderKey) {
                recoveryPolicy.RecordHealthyFrame();
                UpdatePlacement(placement);
                return;
            }

            // Show via WinForms (ShowWithoutActivation=true + WS_EX_LAYERED has zero flash) so
            // managed Form.Visible stays synced with native HWND visibility, then commit pixels atomically.
            if (!Visible) Show();
            if (IsDisposed || Disposing) return;
            IntPtr commitHandle = Handle;
            int commitErr;
            bool committed = CommitPixels(commitHandle, physicalLocation, logicalSize, dpiScale, 255, delegate(Graphics g, SizeF sz) {
                TaskbarOverlayRenderer.Paint(g, sz, items, cfg.ShowIcons, palette, dark, vertical);
            }, out commitErr);
            if (IsDisposed || Disposing) return;

            if (!committed) {
                HideOverlayWindow();
                lastCommitError = commitErr;
                var cAction = recoveryPolicy.ObserveCommitResult(false, commitErr);
                if (cAction == TaskbarOverlayRecoveryAction.RecreateHandle && recreateRequested != null) {
                    SafeDiagnostic.Transition("taskbar_overlay", "recreate_commit_failed_err=" + commitErr + "#" + recoveryPolicy.RecreateCount);
                    recreateRequested("commit_failed");
                    return;
                }
                UpdatePlacement(new TaskbarOverlayPlacement {
                    CanDisplay = false,
                    StatusCode = "commit_failed",
                    StatusMessage = "任务栏浮层像素提交失败（Win32 错误 " + commitErr + "），已自动恢复托盘图标；可在「诊断与反馈」点击「重建浮层」重试。"
                });
                return;
            }

            lastCommitError = 0;
            recoveryPolicy.ObserveCommitResult(true, 0);
            if (!IsWindowVisible(commitHandle)) ShowWindow(commitHandle, SW_SHOWNOACTIVATE);
            PromoteAboveTaskbar(forceZBounce);
            overlayVisible = true;
            lastCommittedHandle = commitHandle;
            lastRenderKey = renderKey;
            if (!behindTaskbar) recoveryPolicy.RecordHealthyFrame();
            UpdatePlacement(placement);
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                watchTimer.Stop();
                watchTimer.Dispose();
                startupVerifyTimer.Stop();
                startupVerifyTimer.Dispose();
                contextMenu.Dispose();
                surface.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class TaskbarBatteryOverlayController : IDisposable {
        readonly TrayContext owner;
        readonly Func<TaskbarEnvironmentSnapshot> probeOverride;
        readonly TaskbarOverlayCommitFunc commitOverride;
        readonly TaskbarOverlayRecoveryPolicy recoveryPolicy = new TaskbarOverlayRecoveryPolicy();
        System.Windows.Forms.Timer postUpdateTimer;
        TaskbarBatteryOverlayForm form;
        bool disposed;
        bool recreating;
        int recreateCount;
        string lastRecoveryReason;

        internal TaskbarBatteryOverlayController(
            TrayContext owner,
            Func<TaskbarEnvironmentSnapshot> probeOverride = null,
            TaskbarOverlayCommitFunc commitOverride = null) {
            this.owner = owner;
            this.probeOverride = probeOverride;
            this.commitOverride = commitOverride;
        }

        internal bool IsRunning { get { return !disposed && form != null && !form.IsDisposed; } }
        internal TaskbarBatteryOverlayForm ActiveForm { get { return IsRunning ? form : null; } }
        internal int RecreateCount { get { return recreateCount; } }
        internal string LastRecoveryReason { get { return lastRecoveryReason; } }

        internal string StatusCode {
            get {
                if (disposed || form == null || form.IsDisposed) return "disabled";
                return form.LastPlacement != null ? form.LastPlacement.StatusCode : "disabled";
            }
        }

        internal string StatusMessage {
            get {
                if (disposed || form == null || form.IsDisposed) return "默认关闭 · 开启后在主屏任务栏通知区旁以独立无焦点浮层显示（支持底部/顶部/左/右侧）";
                return form.LastPlacement != null ? form.LastPlacement.StatusMessage : "初始化中";
            }
        }

        internal string ShortStatusLabel {
            get {
                string code = StatusCode;
                if (code == "disabled") return "未开启";
                if (code == "ready") return "正常绘制";
                if (code == "ready_avoided") return "正常绘制（已避让）";
                if (code == "fullscreen_hidden") return "全屏自动隐藏";
                if (code == "commit_failed") return "像素提交失败";
                if (code == "zorder_occluded") return "被任务栏遮挡";
                if (code == "dwm_cloaked") return "被系统合成隐藏";
                return "已自动降级";
            }
        }

        internal string DiagnosticText {
            get {
                var cfg = DeviceSettings.Current.TaskbarOverlay;
                bool enabled = cfg != null && cfg.Enabled;
                if (!enabled && StatusCode == "disabled") return "未启用";
                var sb = new System.Text.StringBuilder();
                sb.Append(enabled ? "已启用" : "已关闭")
                  .Append(" | 状态=").Append(StatusCode)
                  .Append(" | 可见=").Append(ActiveForm != null && ActiveForm.OverlayVisible ? "是" : "否");
                if (ActiveForm != null && ActiveForm.LastCommitError != 0) {
                    sb.Append(" | Win32错误=").Append(ActiveForm.LastCommitError);
                }
                if (recreateCount > 0) {
                    sb.Append(" | 重建=").Append(recreateCount);
                    if (!String.IsNullOrEmpty(lastRecoveryReason)) sb.Append("(").Append(lastRecoveryReason).Append(")");
                }
                sb.Append(" | ").Append(StatusMessage);
                return sb.ToString();
            }
        }

        internal void ApplySettings() {
            if (disposed) return;
            var cfg = DeviceSettings.Current.TaskbarOverlay;
            if (cfg == null || !cfg.Enabled) {
                StopPostUpdateTimer();
                recoveryPolicy.ResetForManualRecreate();
                CloseForm();
                return;
            }
            if (form == null || form.IsDisposed) {
                CreateAndStartForm();
            } else {
                form.RefreshNow();
            }
        }

        internal bool RecreateOverlay(string reason, bool resetBudget = false) {
            if (disposed || recreating) return false;
            var cfg = DeviceSettings.Current.TaskbarOverlay;
            if (cfg == null || !cfg.Enabled) {
                CloseForm();
                return false;
            }
            recreating = true;
            try {
                if (resetBudget) recoveryPolicy.ResetForManualRecreate();
                recreateCount++;
                lastRecoveryReason = String.IsNullOrEmpty(reason) ? "manual" : reason;
                SafeDiagnostic.Transition("taskbar_overlay", "recreate:" + lastRecoveryReason + "#" + recreateCount);
                CloseForm();
                CreateAndStartForm();
            } finally {
                recreating = false;
            }
            if (owner != null) owner.NotifyTaskbarOverlayStatusChanged();
            return IsRunning;
        }

        internal void SchedulePostUpdateRecovery(int delayMs = 1200) {
            if (disposed) return;
            var cfg = DeviceSettings.Current.TaskbarOverlay;
            if (cfg == null || !cfg.Enabled) return;
            StopPostUpdateTimer();
            int interval = Math.Max(50, delayMs);
            postUpdateTimer = new System.Windows.Forms.Timer { Interval = interval };
            postUpdateTimer.Tick += delegate {
                StopPostUpdateTimer();
                if (!disposed) RecreateOverlay("post_update", true);
            };
            postUpdateTimer.Start();
        }

        internal void NotifyDataChanged() {
            if (disposed || form == null || form.IsDisposed) return;
            form.RefreshNow();
        }

        internal void NotifyThemeChanged() {
            if (disposed || form == null || form.IsDisposed) return;
            form.RefreshNow();
        }

        internal void NotifyPopupStateChanged() {
            if (disposed || form == null || form.IsDisposed) return;
            form.RefreshNow(false);
        }

        internal void NotifyTaskbarRecreated() {
            if (disposed) return;
            var cfg = DeviceSettings.Current.TaskbarOverlay;
            if (cfg == null || !cfg.Enabled) return;
            RecreateOverlay("taskbar_created", true);
        }

        void CreateAndStartForm() {
            form = new TaskbarBatteryOverlayForm(owner, probeOverride, commitOverride, recoveryPolicy, delegate(string reason) {
                RecreateOverlay(reason, false);
            });
            form.StartWatching();
        }

        void StopPostUpdateTimer() {
            var timer = postUpdateTimer;
            postUpdateTimer = null;
            if (timer != null) {
                try { timer.Stop(); } catch (Exception) { }
                try { timer.Dispose(); } catch (Exception) { }
            }
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
            StopPostUpdateTimer();
            CloseForm();
        }
    }
}
