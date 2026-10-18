using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

static class TrayInteractionTests {
    static int checks,cases,activationChecksUnavailable;
    static bool traceEnabled;
    const BindingFlags PrivateInstance=BindingFlags.Instance|BindingFlags.NonPublic;
    static readonly FieldInfo DetailsField=typeof(TrayContext).GetField("details",PrivateInstance);
    static readonly FieldInfo TrayField=typeof(TrayContext).GetField("tray",PrivateInstance);
    static readonly FieldInfo IgnoreDeactivateField=typeof(Details).GetField("ignoreDeactivate",PrivateInstance);
    static readonly FieldInfo ReleaseTimerField=typeof(Details).GetField("deactivateRelease",PrivateInstance);
    static readonly FieldInfo PanelOpenField=typeof(TrayContext).GetField("panelOpen",PrivateInstance);
    static readonly FieldInfo AnchorField=typeof(Details).GetField("anchor",PrivateInstance);
    static readonly MethodInfo NotifyDown=typeof(NotifyIcon).GetMethod("WmMouseDown",PrivateInstance);
    static readonly MethodInfo NotifyUp=typeof(NotifyIcon).GetMethod("WmMouseUp",PrivateInstance);
    static readonly MethodInfo FormDeactivate=typeof(Form).GetMethod("OnDeactivate",PrivateInstance);
    static readonly MethodInfo ShowDetails=typeof(TrayContext).GetMethod("ShowDetails",PrivateInstance);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    sealed class ActivationProbe:NativeWindow,IDisposable {
        readonly Details popup;
        internal bool NativeFocusLoss;
        internal IntPtr Peer;
        internal ActivationProbe(Details popup){this.popup=popup;AssignHandle(popup.Handle);}
        protected override void WndProc(ref Message message) {
            if(message.Msg==0x0006&&(message.WParam.ToInt64()&0xffff)==0&&!popup.IsClosing&&!popup.Disposing&&!popup.IsDisposed){NativeFocusLoss=true;Peer=message.LParam;}
            base.WndProc(ref message);
        }
        public void Dispose(){ReleaseHandle();}
    }
    static string PopupState(Details popup) {
        if(popup==null)return "null";
        return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(popup)+":disposed="+popup.IsDisposed+",visible="+popup.Visible+",closing="+popup.IsClosing+",deferred="+popup.DeferredDeactivationPending+",ignore="+IgnoreDeactivateField.GetValue(popup)+",capture="+popup.Capture;
    }
    static void Trace(Fixture fixture,string phase,Details popup=null) {
        if(!traceEnabled)return;
        var target=(Details)typeof(TrayContext).GetField("trayPressTarget",PrivateInstance).GetValue(fixture.Context);
        Console.WriteLine("TRACE "+phase+" left="+fixture.LeftDown+" pending="+typeof(TrayContext).GetField("trayPressPending",PrivateInstance).GetValue(fixture.Context)+" closes="+typeof(TrayContext).GetField("trayPressCloses",PrivateInstance).GetValue(fixture.Context)+" target="+PopupState(target)+" current="+PopupState(fixture.Current)+" event="+PopupState(popup)+" active="+PopupState(Form.ActiveForm as Details)+" foreground="+GetForegroundWindow());
    }
    static void Observe(Fixture fixture,Details popup) {
        if(!traceEnabled||popup==null)return;
        popup.Activated+=delegate{Trace(fixture,"Activated",popup);};popup.Deactivate+=delegate{Trace(fixture,"Deactivate",popup);};
        popup.MouseCaptureChanged+=delegate{Trace(fixture,"CaptureChanged",popup);};popup.VisibleChanged+=delegate{Trace(fixture,"VisibleChanged",popup);};popup.Disposed+=delegate{Trace(fixture,"Disposed",popup);};
    }

    static void MouseDown(NotifyIcon notify,MouseButtons button,int clicks) {
        var message=Message.Create(IntPtr.Zero,0x400,IntPtr.Zero,new IntPtr(button==MouseButtons.Left?(clicks==2?0x203:0x201):0x204));
        NotifyDown.Invoke(notify,new object[]{message,button,clicks});
    }
    static void MouseUp(NotifyIcon notify,MouseButtons button) {
        var message=Message.Create(IntPtr.Zero,0x400,IntPtr.Zero,new IntPtr(button==MouseButtons.Left?0x202:0x205));
        NotifyUp.Invoke(notify,new object[]{message,button});
    }

    static void Check(bool value,string name) {
        if(!value)throw new Exception("FAIL "+name);
        checks++;
    }
    static void PumpFor(int milliseconds) {
        var watch=Stopwatch.StartNew();
        do {Application.DoEvents();Thread.Sleep(4);}while(watch.ElapsedMilliseconds<milliseconds);
        Application.DoEvents();
    }
    static void Until(Func<bool> done,string name,int milliseconds=2000) {
        var watch=Stopwatch.StartNew();
        while(!done()&&watch.ElapsedMilliseconds<milliseconds){Application.DoEvents();Thread.Sleep(4);}
        Check(done(),name);
    }
    static void Run(string name,Action test) {
        test();cases++;Console.WriteLine("PASS "+name);
    }
    static void Deactivate(Details details) {
        // Raise Form's real event so this also verifies the production event binding.
        FormDeactivate.Invoke(details,new object[]{EventArgs.Empty});
    }
    static System.Windows.Forms.Timer ReleaseTimer(Details details) {
        return (System.Windows.Forms.Timer)ReleaseTimerField.GetValue(details);
    }

    sealed class Fixture:IDisposable {
        internal readonly TrayContext Context;
        internal readonly NotifyIcon Notify;
        internal bool LeftDown;
        internal int MouseClicks,MouseDowns,MouseUps,Writes;
        internal Fixture(bool reduced=false) {
            typeof(DeviceSettings).GetField("current",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,
                new AudioBindings {Theme="light",ReducedMotion=reduced,RememberReadings=false,LowBatteryNotifications=false});
            Context=new TrayContext(false,
                delegate(bool battery){return new MouseSnapshot {Battery=battery?new BatteryReading {Percent=85,TimestampUtc=DateTime.UtcNow}:null,Dpi=Dpi()};},
                delegate(int stage){Interlocked.Increment(ref Writes);return Dpi();},
                delegate{return new HeadsetReading {ReceiverPresent=false,ConnectionVerified=true,Connected=false};},
                delegate{return Audio();},false,
                delegate(string target){Interlocked.Increment(ref Writes);return Audio();},
                delegate(bool mute){Interlocked.Increment(ref Writes);return Audio();},
                delegate(string endpoint,int volume){Interlocked.Increment(ref Writes);return Audio();},
                delegate{return LeftDown;});
            Notify=(NotifyIcon)TrayField.GetValue(Context);
            Notify.MouseClick+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left)MouseClicks++;};
            Notify.MouseDown+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left)MouseDowns++;};
            Notify.MouseUp+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left)MouseUps++;};
            Until(delegate{return Context.Current!=null&&Context.Audio!=null&&!Context.IsRefreshing&&!Context.IsAudioBusy&&!Context.IsHeadsetRefreshing;},"fake readers settle");
            Check(!Notify.Visible&&!Context.DiagnosticsEnabled,"fixture neither publishes an icon nor enables persistence/hardware diagnostics");
        }
        static DpiConfig Dpi() {
            var result=new DpiConfig {ActiveStage=1,TimestampUtc=DateTime.UtcNow};
            foreach(int value in new[]{400,800,1200,1600})result.Stages.Add(new DpiStage {XDpi=value,YDpi=value});
            return result;
        }
        static AudioReading Audio() {
            return new AudioReading {OperationSucceeded=true,TimestampUtc=DateTime.UtcNow,Devices=new AudioDeviceChoice[0]};
        }
        internal Details Current {get{return (Details)DetailsField.GetValue(Context);}}
        internal void Down(int clicks=1) {
            Trace(this,"before Down");
            LeftDown=true;
            // Framework's actual tray callback path: double-clicks suppress MouseClick,
            // while MouseDown/MouseUp must still reach the production subscribers.
            MouseDown(Notify,MouseButtons.Left,clicks);
            Trace(this,"after Down");
        }
        internal void Up() {
            Trace(this,"before Up");
            LeftDown=false;MouseUp(Notify,MouseButtons.Left);
            Trace(this,"after Up");
        }
        internal void Click(int clicks=1) {Down(clicks);Up();}
        internal Details Open() {
            Click();var details=Current;
            Observe(this,details);
            Check(details!=null&&!details.IsDisposed&&details.Visible&&!details.IsClosing,"release opens a visible popup");
            Until(delegate{return details.IsDisposed||!(bool)IgnoreDeactivateField.GetValue(details);},"opening deactivation guard is released");
            PumpFor(230);
            Check(!details.IsDisposed&&!details.IsClosing,"opening animation settles open");
            Check(!ReleaseTimer(details).Enabled,"deactivation release timer is idle while open");
            return details;
        }
        internal Details OpenHidden(bool createHandle=true) {
            var details=new Details(Context);DetailsField.SetValue(Context,details);
            if(createHandle){var handle=details.Handle;}
            IgnoreDeactivateField.SetValue(details,false);Observe(this,details);return details;
        }
        internal void Reveal() {Trace(this,"before Reveal");ShowDetails.Invoke(Context,new object[]{true});Observe(this,Current);Trace(this,"after Reveal");}
        public void Dispose() {
            LeftDown=false;
            Context.ExitThread();Context.Dispose();
            Check(Writes==0,"tray gestures never issue DPI/audio writes");
            Application.DoEvents();
        }
    }

    static void SlowClicks() {
        using(var f=new Fixture()) {
            f.Down();Check(f.Current==null,"a closed tray press waits for release before opening");
            var releasePosition=Cursor.Position;f.Up();
            var first=f.Current;
            if(Cursor.Position==releasePosition)Check((System.Drawing.Point)AnchorField.GetValue(first)==releasePosition,"tray release anchors the new popup at the unchanged pointer position");
            else Console.WriteLine("UNAVAILABLE tray pointer anchor: the physical cursor moved during the release callback");
            Until(delegate{return !(bool)IgnoreDeactivateField.GetValue(first);},"first opening guard settles");PumpFor(230);
            f.Down();PumpFor(60);
            Check(f.Current==first&&!first.IsClosing&&!first.IsDisposed,"an open tray press waits for release before closing");
            f.Up();Until(delegate{return first.IsDisposed;},"slow second click finishes closed");
            PumpFor(170);Check(f.Current==first&&first.IsDisposed,"close never turns into an automatic reopen");
            f.Click();Check(f.Current!=first&&!f.Current.IsDisposed&&!f.Current.IsClosing,"a later click opens a fresh popup");
        }
    }
    static void FastDoubleClick() {
        using(var f=new Fixture()) {
            f.Click();var first=f.Current;PumpFor(20);f.Click(2);
            Check(f.MouseClicks==1&&f.MouseDowns==2&&f.MouseUps==2,"real NotifyIcon double-click path suppresses only the second MouseClick");
            Until(delegate{return first.IsDisposed;},"fast double-click ends closed despite suppressed MouseClick");
            PumpFor(170);Check(f.Current==first&&first.IsDisposed,"fast double-click does not flash open again");
        }
    }
    static void DeactivateBeforeDown() {
        using(var f=new Fixture()) {
            var details=f.Open();f.LeftDown=true;Deactivate(details);
            Check(details.DeferredDeactivationPending&&!details.IsClosing,"deactivation before tray down defers its close");
            Check(ReleaseTimer(details).Enabled&&ReleaseTimer(details).Interval==16,"pending physical press starts the temporary release timer");
            f.Down();f.Up();
            Until(delegate{return details.IsDisposed;},"deactivation-before-down click ends closed");
            Check(f.Current==details,"release retains the original close target");
        }
    }
    static void DownBeforeDeactivate() {
        using(var f=new Fixture()) {
            var details=f.Open();f.Down();Deactivate(details);
            Check(details.DeferredDeactivationPending&&!details.IsClosing,"tray down before deactivation retains a close intent");
            f.Up();Until(delegate{return details.IsDisposed;},"down-before-deactivation click ends closed");
            Check(f.Current==details,"deactivation cannot replace a captured close with reveal");
        }
    }
    static void LongPress() {
        using(var f=new Fixture()) {
            var details=f.Open();f.LeftDown=true;Deactivate(details);f.Down();PumpFor(240);
            Check(!details.IsDisposed&&!details.IsClosing&&details.DeferredDeactivationPending,"holding longer than the 135ms close animation preserves the popup and close target");
            f.Up();Until(delegate{return details.IsDisposed;},"long-held tray release closes without reopening");
            Check(f.Current==details,"long-held release never constructs another popup");
        }
    }
    static void DisposedTarget() {
        using(var f=new Fixture()) {
            var details=f.Open();f.Down();details.Dispose();f.Up();PumpFor(80);
            Check(f.Current==details&&details.IsDisposed,"release of an already disposed captured target is a no-op");
            f.Click();Check(f.Current!=details&&!f.Current.IsDisposed&&!f.Current.IsClosing,"a new press replaces the consumed disposed target");
        }
    }
    static void ReplacedTarget() {
        using(var f=new Fixture()) {
            var details=f.OpenHidden();f.Down();details.Dispose();var replacement=f.OpenHidden();
            Check(replacement!=details&&!replacement.IsDisposed,"explicit show replaces a disposed captured popup");
            f.Up();PumpFor(80);Trace(f,"hidden replacement after pump",replacement);
            Check(f.Current==replacement&&!replacement.IsDisposed&&!replacement.IsClosing,"old captured close cannot dismiss a later replacement popup");
        }
    }
    static void VisibleReplacementActivation() {
        using(var f=new Fixture()) {
            var details=f.Open();f.Down();details.Dispose();f.Reveal();var replacement=f.Current;
            Check(replacement!=details&&!replacement.IsDisposed,"explicit visible show replaces a disposed captured popup");
            bool wasForeground=Form.ActiveForm==replacement||GetForegroundWindow()==replacement.Handle;
            using(var probe=new ActivationProbe(replacement)) {
                f.Up();Check(!replacement.IsClosing&&!replacement.IsDisposed,"old captured release does not directly close the visible replacement");PumpFor(240);Trace(f,"visible replacement after pump",replacement);
                if(wasForeground&&!probe.NativeFocusLoss)Check(f.Current==replacement&&!replacement.IsDisposed&&!replacement.IsClosing,"visible replacement remains open when native focus is retained");
                else {activationChecksUnavailable++;Console.WriteLine("UNAVAILABLE replacement retention: native foreground was unavailable or an independent WM_ACTIVATE focus loss occurred (initial="+wasForeground+", nativeLoss="+probe.NativeFocusLoss+", peer="+probe.Peer+")");}
            }
        }
    }
    static void PopupHandleLifecycle() {
        using(var f=new Fixture()) {
            var details=f.OpenHidden(false);
            Check(!details.IsHandleCreated,"constructor leaves an unshown popup without a native handle");
            details.HandleDeactivation();details.CheckDeferredDeactivation();
            Check(!details.IsHandleCreated&&!details.DeferredDeactivationPending,"deactivation before handle creation cannot queue native callbacks or create a handle");
            details.Dispose();details.Dispose();Check(details.IsDisposed&&!details.IsHandleCreated,"repeated popup disposal never recreates a tooltip or native handle");
        }
        using(var f=new Fixture()) {
            var details=f.OpenHidden();bool reentered=false;
            details.HandleDestroyed+=delegate {reentered=true;details.HandleDeactivation();details.CheckDeferredDeactivation();};
            details.Dispose();
            Check(reentered&&details.IsDisposed&&!details.DeferredDeactivationPending&&!details.IsHandleCreated,"native handle destruction during Dispose cannot restart deactivation polling or BeginInvoke");
        }
        using(var f=new Fixture()) {
            var details=f.OpenHidden();details.HandleDeactivation();Check(details.DeferredDeactivationPending,"a live hidden owned handle can queue the tested deferred close");
            typeof(Control).GetMethod("DestroyHandle",PrivateInstance).Invoke(details,null);
            Check(!details.IsHandleCreated&&!details.DeferredDeactivationPending,"destroying the handle retires its queued deactivation callback");
            var replacementHandle=details.Handle;PumpFor(80);
            Check(!details.IsDisposed&&!details.IsClosing&&!details.DeferredDeactivationPending,"recreating a handle cannot execute a close queued by its retired predecessor");
            details.HandleDeactivation();Check(details.DeferredDeactivationPending,"a new handle may queue its own deferred close");
            typeof(Details).GetMethod("BeginPresentation",PrivateInstance).Invoke(details,null);PumpFor(80);
            Check(!details.IsDisposed&&!details.IsClosing&&!details.DeferredDeactivationPending,"a later presentation retires callbacks from the earlier display generation");
        }
        using(var f=new Fixture()) {
            var old=f.OpenHidden();old.HandleDeactivation();var replacement=f.OpenHidden();PumpFor(80);
            Check(!old.IsClosing&&!old.IsDisposed&&!replacement.IsDisposed&&!replacement.IsClosing,"an old owned window cannot close after the controller replaces its current window");old.Dispose();
        }
        using(var f=new Fixture()) {
            var details=f.OpenHidden();int epoch=(int)typeof(Details).GetMethod("BeginPresentation",PrivateInstance).Invoke(details,null);
            typeof(Details).GetMethod("QueueOpeningGuardRelease",PrivateInstance).Invoke(details,new object[]{epoch});
            typeof(Control).GetMethod("DestroyHandle",PrivateInstance).Invoke(details,null);var handle=details.Handle;PumpFor(80);
            Check(!(bool)IgnoreDeactivateField.GetValue(details),"handle recreation releases the current opening guard instead of leaving deactivation ignored");
            details.HandleDeactivation();Until(delegate{return details.IsDisposed;},"a recreated popup still dismisses on a later genuine deactivation");
        }
    }
    static void ReopenDuringClose() {
        using(var f=new Fixture()) {
            var details=f.Open();f.Click();
            if(!details.IsDisposed)Check(details.IsClosing,"closing popup exposes its dismissal state");
            using(var other=new Form {Text="Tray interaction test focus target",ShowInTaskbar=false,Width=180,Height=90,StartPosition=FormStartPosition.Manual,Left=24,Top=24}) {
                other.Show();other.Activate();Application.DoEvents();
                bool otherActivated=Form.ActiveForm==other||GetForegroundWindow()==other.Handle;
                bool canReverse=!details.IsDisposed&&details.IsClosing;
                f.Down();f.Up();var reopened=f.Current;
                Check(reopened!=null&&!reopened.IsDisposed&&!reopened.IsClosing&&reopened.Visible,"a new press during dismissal explicitly reveals the popup");
                if(canReverse)Check(reopened==details,"animated dismissal is reversed on the same popup");
                Check((bool)PanelOpenField.GetValue(f.Context),"reveal restores the controller's open-panel state");
                if(otherActivated)Until(delegate{return Form.ActiveForm==reopened||GetForegroundWindow()==reopened.Handle;},"reveal restores activation to the popup");
                else {activationChecksUnavailable++;Console.WriteLine("UNAVAILABLE foreground activation: the same-process control form could not activate in this desktop session (popup foreground="+(GetForegroundWindow()==reopened.Handle)+")");}
                PumpFor(240);Check(!reopened.IsDisposed&&!reopened.IsClosing,"the old close animation cannot dispose the revealed popup");
            }
        }
    }
    static void RightButtonAndMenu() {
        using(var f=new Fixture()) {
            MouseDown(f.Notify,MouseButtons.Right,1);MouseUp(f.Notify,MouseButtons.Right);
            Check(f.Current==null,"right-button callbacks do not open the panel");
            var details=f.Open();
            ((ToolStripMenuItem)f.Notify.ContextMenuStrip.Items[0]).PerformClick();Application.DoEvents();
            Check(f.Current==details&&!details.IsClosing&&!details.IsDisposed,"menu show command reveals an already open popup");
            f.Down();MouseDown(f.Notify,MouseButtons.Right,1);MouseUp(f.Notify,MouseButtons.Right);f.Up();
            Until(delegate{return details.IsDisposed;},"right-button callbacks cannot overwrite a captured left-button close");
            ((ToolStripMenuItem)f.Notify.ContextMenuStrip.Items[0]).PerformClick();
            Check(f.Current!=details&&!f.Current.IsDisposed&&!f.Current.IsClosing,"menu show command opens a fresh popup after dismissal");
        }
    }
    static void ReducedMotion() {
        using(var f=new Fixture(true)) {
            Check(PopupPages.Reduced,"reduced-motion fixture selects the immediate animation path");
            var details=f.Open();f.LeftDown=true;Deactivate(details);f.Down();PumpFor(180);
            Check(!details.IsDisposed&&details.DeferredDeactivationPending,"reduced motion also preserves the target through a physical hold");
            f.Up();Check(details.IsDisposed&&f.Current==details,"reduced-motion release closes synchronously without reopening");
            f.Click();Check(f.Current!=details&&!f.Current.IsDisposed,"reduced-motion next press opens normally");
        }
    }
    static void ExternalClickRelease() {
        using(var f=new Fixture()) {
            var details=f.Open();f.LeftDown=true;Deactivate(details);PumpFor(190);
            Check(!details.IsDisposed&&!details.IsClosing,"external mouse press does not close until physical release");
            f.LeftDown=false;
            Until(delegate{return details.IsDisposed;},"external release closes through the real timer without a tray MouseUp");
            Check(f.MouseUps==1,"external release test sent no extra tray release callback");
        }
    }
    static void KeyboardDeactivation() {
        using(var f=new Fixture()) {
            var details=f.Open();Deactivate(details);
            Check(!ReleaseTimer(details).Enabled,"keyboard focus loss starts no mouse-release polling");
            Until(delegate{return details.IsDisposed;},"keyboard focus loss finishes closing");
        }
    }
    static void DelayedReleasedTrayCallbacks() {
        using(var f=new Fixture()) {
            var details=f.Open();Deactivate(details);
            Check(!f.LeftDown&&!details.IsClosing&&details.DeferredDeactivationPending,"deactivation processed after physical release defers once to the UI queue");
            MouseDown(f.Notify,MouseButtons.Left,1);MouseUp(f.Notify,MouseButtons.Left);
            Until(delegate{return details.IsDisposed;},"late tray down/up callbacks still finish with the original popup closed");
            Check(f.Current==details,"late callbacks never replace the original popup with a reveal");
        }
    }
    static void StaleDeferredClose() {
        using(var f=new Fixture()) {
            var details=f.Open();f.LeftDown=true;Deactivate(details);f.LeftDown=false;
            details.CheckDeferredDeactivation();
            Check(details.DeferredDeactivationPending&&!details.IsClosing,"released deferred close is queued instead of executed inline");
            f.Reveal();
            Check(!details.DeferredDeactivationPending&&!ReleaseTimer(details).Enabled,"explicit reveal cancels the old pending close and polling");
            PumpFor(240);
            Check(f.Current==details&&!details.IsDisposed&&!details.IsClosing&&details.Visible,"an already queued deferred callback cannot dismiss a later reveal");
        }
    }
    static void DragOutWithoutMouseUp() {
        using(var f=new Fixture()) {
            var details=f.Open();f.Down();Deactivate(details);PumpFor(220);
            Check(!details.IsDisposed&&details.DeferredDeactivationPending,"dragging away while held keeps the captured target alive");
            f.LeftDown=false;
            Until(delegate{return details.IsDisposed;},"drag-out closes on physical release when the tray sends no MouseUp");
            Check(f.MouseUps==1,"drag-out fixture actually omitted its second tray MouseUp");
            f.Click();var next=f.Current;PumpFor(240);
            Check(next!=details&&!next.IsDisposed&&!next.IsClosing,"a subsequent press replaces the abandoned capture and opens normally");
        }
    }

    [STAThread]
    static int Main(string[] args) {
        var cached=typeof(DeviceSettings).GetField("current",BindingFlags.Static|BindingFlags.NonPublic);
        object previous=cached.GetValue(null);
        try {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            NativeDisplay.EnableDpiAwareness();Application.EnableVisualStyles();
            traceEnabled=Array.IndexOf(args,"--trace")>=0;
            Check(NotifyDown!=null&&NotifyUp!=null&&FormDeactivate!=null,".NET Framework callback entrypoints exist");
            if(Array.IndexOf(args,"--replacement-only")>=0){Run("captured target replaced before release",ReplacedTarget);Run("visible replacement native activation probe",VisibleReplacementActivation);return 0;}
            if(Array.IndexOf(args,"--lifecycle-only")>=0){Run("popup native handle lifecycle",PopupHandleLifecycle);Run("captured target replaced before release",ReplacedTarget);Console.WriteLine("PASS "+checks+" hidden lifecycle/replacement checks");return 0;}
            Run("popup native handle lifecycle",PopupHandleLifecycle);
            Run("slow tray open/close",SlowClicks);
            Run("actual NotifyIcon fast double-click callback sequence",FastDoubleClick);
            Run("deactivation before tray down",DeactivateBeforeDown);
            Run("tray down before deactivation",DownBeforeDeactivate);
            Run("hold beyond the close animation",LongPress);
            Run("disposed captured close target",DisposedTarget);
            Run("captured target replaced before release",ReplacedTarget);
            Run("visible replacement native activation probe",VisibleReplacementActivation);
            Run("press during closing reveals with activation probe",ReopenDuringClose);
            Run("right-button isolation and menu reveal",RightButtonAndMenu);
            Run("reduced motion",ReducedMotion);
            Run("outside click release",ExternalClickRelease);
            Run("keyboard focus loss",KeyboardDeactivation);
            Run("tray callbacks delayed beyond physical release",DelayedReleasedTrayCallbacks);
            Run("queued deferred close cancellation",StaleDeferredClose);
            Run("drag out without tray mouse-up",DragOutWithoutMouseUp);
            Console.WriteLine("PASS "+checks+" tray interaction checks across "+cases+" scenarios; real NotifyIcon callbacks and owned test forms, fake hardware/audio/physical-button readers, no settings writes");
            Console.WriteLine("Boundary: Explorer delivery and physical taskbar clicks are simulated through Framework callbacks; no installed instance was operated.");
            if(activationChecksUnavailable>0)Console.WriteLine("Boundary: "+activationChecksUnavailable+" actual activation assertion unavailable because the owned control form could not activate in this desktop session.");
            return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally {cached.SetValue(null,previous);}
    }
}
