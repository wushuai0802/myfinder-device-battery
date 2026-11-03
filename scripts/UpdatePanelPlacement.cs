using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AtkBatteryTray {
    internal enum PanelOpenRequest {None,Launcher,Updated}
    internal static class PanelStartup {
        internal const string UpdatedSignalName="Local\\MyFinderDeviceBattery.ShowUpdatedPanel";
        internal static PanelOpenRequest Parse(string[] args) {
            if(args.Length!=1)return PanelOpenRequest.None;
            return args[0]=="--show"?PanelOpenRequest.Launcher:args[0]=="--show-updated"?PanelOpenRequest.Updated:PanelOpenRequest.None;
        }
        internal static void Forward(PanelOpenRequest request,ShowPanelSignal launcher,ShowPanelSignal updated) {
            if(request==PanelOpenRequest.Launcher)launcher.Request();
            else if(request==PanelOpenRequest.Updated)updated.Request();
        }
    }
    // Matches ABE_LEFT/TOP/RIGHT/BOTTOM in APPBARDATA.
    internal enum TaskbarEdge {Left,Top,Right,Bottom}
    internal struct TrayPanelAnchor {
        internal readonly Point Point;
        internal readonly Rectangle WorkArea;
        internal readonly TaskbarEdge Edge;
        internal TrayPanelAnchor(Point point,Rectangle workArea,TaskbarEdge edge){Point=point;WorkArea=workArea;Edge=edge;}
    }
    internal static class TrayPanelPlacement {
        [StructLayout(LayoutKind.Sequential)] struct NativeRect {internal int Left,Top,Right,Bottom;internal Rectangle Rectangle {get{return Rectangle.FromLTRB(Left,Top,Right,Bottom);}}}
        [StructLayout(LayoutKind.Sequential)] struct NotifyIconIdentifier {internal uint Size;internal IntPtr Window;internal uint Id;internal Guid Guid;}
        [StructLayout(LayoutKind.Sequential)] struct AppBarData {internal uint Size;internal IntPtr Window;internal uint CallbackMessage,Edge;internal NativeRect Rect;internal IntPtr Param;}
        [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier,out NativeRect rect);
        [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint message,ref AppBarData data);
        internal static TrayPanelAnchor Anchor(Rectangle? icon,Rectangle workArea,TaskbarEdge edge) {
            var point=icon.HasValue?Center(icon.Value):new Point(edge==TaskbarEdge.Left?workArea.Left:workArea.Right-1,edge==TaskbarEdge.Top?workArea.Top:workArea.Bottom-1);
            return new TrayPanelAnchor(point,workArea,edge);
        }
        static Point Center(Rectangle rect){return new Point(rect.Left+rect.Width/2,rect.Top+rect.Height/2);}
        internal static TrayPanelAnchor ForIcon(Rectangle icon,TaskbarEdge edge,Func<Point,Rectangle> workingArea) {
            return Anchor(icon,workingArea(Center(icon)),edge);
        }
        // Physical coordinates throughout. The caller caps panel size to the work area.
        internal static Point Position(Point anchor,Rectangle workArea,Size panel,TaskbarEdge edge) {
            if(panel.Width<0||panel.Height<0||panel.Width>workArea.Width||panel.Height>workArea.Height)throw new ArgumentOutOfRangeException("panel");
            int x=anchor.X-panel.Width/2,y=anchor.Y-panel.Height-8;
            if(edge==TaskbarEdge.Top)y=anchor.Y+8;
            else if(edge==TaskbarEdge.Left){x=anchor.X+8;y=anchor.Y-panel.Height;}
            else if(edge==TaskbarEdge.Right){x=anchor.X-panel.Width-8;y=anchor.Y-panel.Height;}
            return new Point(Math.Max(workArea.Left,Math.Min(x,workArea.Right-panel.Width)),Math.Max(workArea.Top,Math.Min(y,workArea.Bottom-panel.Height)));
        }
        internal static bool TryIconIdentity(NotifyIcon icon,out IntPtr handle,out uint id) {
            handle=IntPtr.Zero;id=0;if(icon==null)return false;
            try {
                const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
                var type=typeof(NotifyIcon);
                var windowField=type.GetField("window",flags)??type.GetField("_window",flags);
                var idField=type.GetField("id",flags)??type.GetField("_id",flags);
                if(windowField==null||idField==null)return false;
                var window=windowField.GetValue(icon) as NativeWindow;
                var value=idField.GetValue(icon);
                if(window==null||window.Handle==IntPtr.Zero||!(value is int)&&!(value is uint))return false;
                handle=window.Handle;id=value is int?unchecked((uint)(int)value):(uint)value;return true;
            }catch(MemberAccessException){return false;}catch(System.Security.SecurityException){return false;}catch(InvalidOperationException){return false;}
        }
        internal static bool TryIconRect(NotifyIcon icon,out Rectangle rect) {
            rect=Rectangle.Empty;IntPtr handle;uint id;if(!TryIconIdentity(icon,out handle,out id))return false;
            var identifier=new NotifyIconIdentifier {Size=(uint)Marshal.SizeOf(typeof(NotifyIconIdentifier)),Window=handle,Id=id};
            try {
                NativeRect native;if(Shell_NotifyIconGetRect(ref identifier,out native)!=0||native.Right<=native.Left||native.Bottom<=native.Top)return false;
                rect=native.Rectangle;return true;
            }catch(DllNotFoundException){return false;}catch(EntryPointNotFoundException){return false;}
        }
        static bool TryTaskbar(out Rectangle rect,out TaskbarEdge edge) {
            rect=Rectangle.Empty;edge=TaskbarEdge.Bottom;var data=new AppBarData {Size=(uint)Marshal.SizeOf(typeof(AppBarData))};
            try {
                if(SHAppBarMessage(5,ref data)==UIntPtr.Zero||data.Edge>3||data.Rect.Right<=data.Rect.Left||data.Rect.Bottom<=data.Rect.Top)return false;
                rect=data.Rect.Rectangle;edge=(TaskbarEdge)data.Edge;return true;
            }catch(DllNotFoundException){return false;}catch(EntryPointNotFoundException){return false;}
        }
        internal static TrayPanelAnchor? FindIcon(NotifyIcon icon) {
            Rectangle rect;if(!TryIconRect(icon,out rect))return null;
            var point=Center(rect);var screen=Screen.FromPoint(point);Rectangle taskbar;TaskbarEdge edge;
            if(!TryTaskbar(out taskbar,out edge)||Screen.FromRectangle(taskbar).DeviceName!=screen.DeviceName) {
                // ABM_GETTASKBARPOS describes the primary taskbar. A secondary
                // monitor's icon must keep its own screen and nearest edge.
                var bounds=screen.Bounds;int distance=Math.Abs(point.Y-bounds.Bottom);edge=TaskbarEdge.Bottom;
                int next=Math.Abs(point.Y-bounds.Top);if(next<distance){distance=next;edge=TaskbarEdge.Top;}
                next=Math.Abs(point.X-bounds.Left);if(next<distance){distance=next;edge=TaskbarEdge.Left;}
                next=Math.Abs(point.X-bounds.Right);if(next<distance)edge=TaskbarEdge.Right;
            }
            return ForIcon(rect,edge,delegate(Point location){return Screen.FromPoint(location).WorkingArea;});
        }
        internal static TrayPanelAnchor Fallback() {
            Rectangle taskbar;TaskbarEdge edge;
            if(TryTaskbar(out taskbar,out edge))return Anchor(null,Screen.FromRectangle(taskbar).WorkingArea,edge);
            return Anchor(null,Screen.PrimaryScreen.WorkingArea,TaskbarEdge.Bottom);
        }
    }
    // UI-thread retries allow Explorer to register a newly-created NotifyIcon
    // without blocking the message loop. Only one opening may be pending.
    internal sealed class UpdatedPanelRequest:IDisposable {
        readonly Timer timer=new Timer {Interval=100};
        readonly Stopwatch clock=new Stopwatch();
        readonly Func<TrayPanelAnchor?> find;
        readonly Func<TrayPanelAnchor> fallback;
        readonly Action<TrayPanelAnchor> open;
        bool pending,disposed;
        internal UpdatedPanelRequest(Func<TrayPanelAnchor?> find,Func<TrayPanelAnchor> fallback,Action<TrayPanelAnchor> open) {
            this.find=find;this.fallback=fallback;this.open=open;timer.Tick+=delegate{TryOpen();};
        }
        internal void Start(){if(disposed||pending)return;pending=true;clock.Restart();TryOpen();if(pending)timer.Start();}
        void TryOpen() {
            if(disposed||!pending)return;
            var anchor=find();if(!anchor.HasValue&&clock.ElapsedMilliseconds<2000)return;
            pending=false;timer.Stop();clock.Stop();open(anchor??fallback());
        }
        public void Dispose(){disposed=true;pending=false;timer.Dispose();clock.Stop();}
    }
}
