using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace AtkBatteryTray {
    public static class NativeDisplay {
        [StructLayout(LayoutKind.Sequential)] struct NativePoint {public int X,Y;public NativePoint(Point point){X=point.X;Y=point.Y;}}
        [StructLayout(LayoutKind.Sequential)] struct NativeRect {public int Left,Top,Right,Bottom;}
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int awareness);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(NativePoint point,uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor,int kind,out uint x,out uint y);
        [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window,out NativeRect rect);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        internal static bool IsLeftButtonPressed(){return (GetAsyncKeyState(SystemInformation.MouseButtonsSwapped?2:1)&0x8000)!=0;}
        public static void EnableDpiAwareness() {
            try{if(SetProcessDpiAwarenessContext(new IntPtr(-4)))return;}catch(EntryPointNotFoundException){}
            try{if(SetProcessDpiAwareness(2)==0)return;}catch(DllNotFoundException){}catch(EntryPointNotFoundException){}
            SetProcessDPIAware();
        }
        public static float ScaleAt(Point point) {
            try {uint x,y;if(GetDpiForMonitor(MonitorFromPoint(new NativePoint(point),2),0,out x,out y)==0)return Math.Max(1f,x/96f);}
            catch(DllNotFoundException){}catch(EntryPointNotFoundException){}
            using(var graphics=Graphics.FromHwnd(IntPtr.Zero))return Math.Max(1f,graphics.DpiX/96f);
        }
        public static bool MayNotify() {
            try{int state;if(SHQueryUserNotificationState(out state)!=0||state!=5)return false;}
            catch(EntryPointNotFoundException){return false;}
            var window=GetForegroundWindow();
            if(window==IntPtr.Zero||window==GetShellWindow())return true;
            NativeRect rect;if(!GetWindowRect(window,out rect))return false;
            var bounds=Screen.FromHandle(window).Bounds;
            return !(rect.Left<=bounds.Left&&rect.Top<=bounds.Top&&rect.Right>=bounds.Right&&rect.Bottom>=bounds.Bottom);
        }
    }
    // A hidden broadcast window, not a polling service or input hook.
    internal sealed class DeviceEventsWindow:NativeWindow,IDisposable {
        readonly Action changed;
        readonly Action appearance;readonly Action<bool> session;
        [DllImport("wtsapi32.dll")] static extern bool WTSRegisterSessionNotification(IntPtr hwnd,int flags);
        [DllImport("wtsapi32.dll")] static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);
        public DeviceEventsWindow(Action callback,Action appearanceChanged=null,Action<bool> sessionChanged=null) {
            changed=callback;appearance=appearanceChanged;session=sessionChanged;CreateHandle(new CreateParams {Caption="MyFinder Device Battery Events"});
            if(session!=null)try{WTSRegisterSessionNotification(Handle,0);}catch(DllNotFoundException){}
        }
        protected override void WndProc(ref Message message) {
            int value=message.WParam.ToInt32();
            if(message.Msg==0x219&&(value==7||value==0x8000||value==0x8004)||
                message.Msg==0x218&&(value==7||value==18))changed();
            if(message.Msg==0x001A&&appearance!=null)appearance();
            if(message.Msg==0x02B1&&session!=null&&(value==7||value==8))session(value==7);
            base.WndProc(ref message);
        }
        public void Dispose(){if(session!=null)try{WTSUnRegisterSessionNotification(Handle);}catch(DllNotFoundException){}DestroyHandle();}
    }
    internal sealed class ShowPanelSignal:IDisposable {
        readonly EventWaitHandle signal;
        RegisteredWaitHandle registration;
        public ShowPanelSignal(string name=null) {signal=new EventWaitHandle(false,EventResetMode.AutoReset,name??"Local\\MyFinderDeviceBattery.ShowPanel");}
        public void Request(){signal.Set();}
        public void Listen(Action callback) {
            registration=ThreadPool.RegisterWaitForSingleObject(signal,delegate {callback();},null,Timeout.Infinite,false);
        }
        public void Dispose(){if(registration!=null)registration.Unregister(null);signal.Dispose();}
    }
}
