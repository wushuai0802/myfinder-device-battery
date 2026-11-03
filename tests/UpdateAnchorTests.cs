using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

// Fake hardware, transient settings and uniquely named events only.
static class UpdateAnchorTests {
    static int checks;
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Check(bool value,string name){if(!value)throw new Exception("FAIL "+name);checks++;}
    static object Field(object target,string name){return target.GetType().GetField(name,Private).GetValue(target);}
    static void Pump(Func<bool> done,int timeout=3000){var clock=Stopwatch.StartNew();while(!done()&&clock.ElapsedMilliseconds<timeout){Application.DoEvents();Thread.Sleep(5);}Check(done(),"asynchronous request completes");}
    static void ArgumentsAndForwarding() {
        Check(PanelStartup.Parse(new[]{"--show"})==PanelOpenRequest.Launcher,"legacy show uses launcher placement");
        Check(PanelStartup.Parse(new[]{"--show-updated"})==PanelOpenRequest.Updated,"updated show uses tray placement");
        Check(PanelStartup.Parse(new string[0])==PanelOpenRequest.None,"ordinary startup stays quiet");
        Check(PanelStartup.Parse(new[]{"--show-updated","extra"})==PanelOpenRequest.None,"extra arguments do not open a panel");
        string name="Local\\UpdateAnchorTests-"+Guid.NewGuid().ToString("N");int launcher=0,updated=0;
        using(var legacy=new ShowPanelSignal(name))using(var newer=new ShowPanelSignal(name+"-updated"))
        using(var legacySender=new ShowPanelSignal(name))using(var updatedSender=new ShowPanelSignal(name+"-updated")) {
            legacy.Listen(delegate{Interlocked.Increment(ref launcher);});newer.Listen(delegate{Interlocked.Increment(ref updated);});
            PanelStartup.Forward(PanelStartup.Parse(new[]{"--show"}),legacySender,updatedSender);
            Pump(delegate{return Volatile.Read(ref launcher)==1;});Check(updated==0,"legacy forwarding never signals updated mode");
            PanelStartup.Forward(PanelStartup.Parse(new[]{"--show-updated"}),legacySender,updatedSender);
            Pump(delegate{return Volatile.Read(ref updated)==1;});Check(launcher==1,"updated forwarding never signals launcher mode");
            PanelStartup.Forward(PanelOpenRequest.None,legacySender,updatedSender);
            Check(launcher==1&&updated==1,"quiet startup has no show forwarding");
        }
    }
    static void Positions() {
        var area=new Rectangle(0,0,1600,900);var size=new Size(260,300);
        var anchors=new[]{new Point(900,920),new Point(900,-20),new Point(-20,600),new Point(1620,600)};
        var edges=new[]{TaskbarEdge.Bottom,TaskbarEdge.Top,TaskbarEdge.Left,TaskbarEdge.Right};
        var expected=new[]{new Point(770,600),new Point(770,0),new Point(0,300),new Point(1340,300)};
        var fallbackExpected=new[]{new Point(1340,591),new Point(1340,8),new Point(8,599),new Point(1331,599)};
        for(int i=0;i<edges.Length;i++) {
            var icon=new Rectangle(anchors[i].X-10,anchors[i].Y-10,20,20);
            var resolved=TrayPanelPlacement.Anchor(icon,area,edges[i]);
            Check(resolved.Point==anchors[i],"icon center anchors "+edges[i]);
            var location=TrayPanelPlacement.Position(resolved.Point,area,size,edges[i]);
            Check(location==expected[i]&&area.Contains(new Rectangle(location,size)),"icon placement stays close and inside "+edges[i]);
            var fallback=TrayPanelPlacement.Anchor(null,area,edges[i]);
            location=TrayPanelPlacement.Position(fallback.Point,area,size,edges[i]);
            Check(location==fallbackExpected[i]&&area.Contains(new Rectangle(location,size)),"notification corner fallback "+edges[i]);
            var offset=new Point(-1920,-1080);var second=new Rectangle(offset,area.Size);
            icon.Offset(offset);var secondary=TrayPanelPlacement.Anchor(icon,second,edges[i]);
            location=TrayPanelPlacement.Position(secondary.Point,second,size,edges[i]);
            Check(location==new Point(expected[i].X+offset.X,expected[i].Y+offset.Y)&&second.Contains(new Rectangle(location,size)),"negative-coordinate monitor "+edges[i]);
            Check(TrayPanelPlacement.Position(secondary.Point,second,second.Size,edges[i])==second.Location,"exact-fit panel stays inside "+edges[i]);
            foreach(var extreme in new[]{new Point(-5000,-5000),new Point(5000,5000)})
                Check(area.Contains(new Rectangle(TrayPanelPlacement.Position(extreme,area,size,edges[i]),size)),"outlying anchor clamped "+edges[i]);
        }
        var primary=new Rectangle(0,0,1920,1040);var secondaryArea=new Rectangle(-1920,40,1920,1040);
        var iconRect=new Rectangle(-120,8,20,24);Point selected=Point.Empty;
        var monitor=TrayPanelPlacement.ForIcon(iconRect,TaskbarEdge.Top,delegate(Point point){selected=point;return point.X<0?secondaryArea:primary;});
        Check(selected==new Point(-110,20)&&monitor.WorkArea==secondaryArea,"monitor selected from the icon, not cursor or primary screen");
    }
    static void Retries() {
        var anchor=TrayPanelPlacement.Anchor(null,new Rectangle(0,0,800,600),TaskbarEdge.Bottom);
        int probes=0,opens=0,fallbacks=0;
        using(var request=new UpdatedPanelRequest(delegate{probes++;return probes<3?(TrayPanelAnchor?)null:anchor;},delegate{fallbacks++;return anchor;},delegate{opens++;})) {
            request.Start();request.Start();Pump(delegate{return opens==1;});
            Check(probes==3&&fallbacks==0,"startup retries missing icon and coalesces duplicate requests");
        }
        probes=opens=fallbacks=0;var clock=Stopwatch.StartNew();
        using(var request=new UpdatedPanelRequest(delegate{probes++;return null;},delegate{fallbacks++;return anchor;},delegate{opens++;})) {
            request.Start();Pump(delegate{return opens==1;});
            Check(clock.ElapsedMilliseconds>=1900&&clock.ElapsedMilliseconds<2800&&probes<=23&&fallbacks==1,"missing icon falls back once after about two seconds");
        }
        opens=0;using(var request=new UpdatedPanelRequest(delegate{return null;},delegate{return anchor;},delegate{opens++;})){request.Start();}
        var until=Stopwatch.StartNew();while(until.ElapsedMilliseconds<150){Application.DoEvents();Thread.Sleep(5);}
        Check(opens==0,"disposal cancels a pending opening");
        Rectangle rect;Check(!TrayPanelPlacement.TryIconRect(null,out rect),"missing icon fails safely");
        using(var icon=new NotifyIcon()) {
            IntPtr handle;uint id;Check(!TrayPanelPlacement.TryIconIdentity(icon,out handle,out id),"not-yet-created NotifyIcon handle fails safely");
            // Create only this fixture's hidden callback window; never register a
            // tray icon with Explorer or touch the installed application.
            var window=(NativeWindow)typeof(NotifyIcon).GetField("window",Private).GetValue(icon);window.CreateHandle(new CreateParams());
            Check(TrayPanelPlacement.TryIconIdentity(icon,out handle,out id)&&handle==window.Handle&&id==unchecked((uint)(int)typeof(NotifyIcon).GetField("id",Private).GetValue(icon)),"Framework reflection obtains this NotifyIcon's window and ID");
            Check(!TrayPanelPlacement.TryIconRect(icon,out rect),"unregistered icon rectangle fails safely");
        }
    }
    static void PopupRouting() {
        DeviceSettings.UseTransientConfiguration(new AudioBindings {ReducedMotion=true,RememberReadings=false,Theme="light"});
        using(var owner=new TrayContext(false,delegate(bool battery){return new MouseSnapshot();},delegate(int stage){throw new Exception("unexpected hardware write");})) {
            var screen=Screen.PrimaryScreen.WorkingArea;
            var anchor=TrayPanelPlacement.Anchor(null,screen,TaskbarEdge.Bottom);
            string name="Local\\UpdateAnchorPopup-"+Guid.NewGuid().ToString("N");
            using(var launcher=new ShowPanelSignal(name))using(var updated=new ShowPanelSignal(name+"-updated")) {
            owner.ListenForShowRequest(launcher);owner.ListenForShowRequest(updated,true);
            PanelStartup.Forward(PanelOpenRequest.Updated,launcher,updated);
            Pump(delegate{return Field(owner,"details")!=null;});var popup=(Details)Field(owner,"details");
            var placement=(TrayPanelAnchor)Field(popup,"updatedAnchor");
            Check(popup.Visible&&placement.WorkArea.Contains(popup.Bounds),"forwarded updated request opens inside tray fallback work area");
            var feedback=(PanelFeedback)typeof(Details).GetMethod("Feedback",Private).Invoke(popup,null);
            Check(feedback.UpdatedNotice=="已更新到 "+AppRuntime.Version,"updated version is supplied to the main-panel renderer");
            using(var bitmap=new Bitmap(popup.ClientSize.Width,popup.ClientSize.Height))using(var graphics=Graphics.FromImage(bitmap)) {
                graphics.Clear(Color.White);typeof(Details).GetMethod("PaintContent",Private).Invoke(popup,new object[]{graphics});
                bitmap.Save(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"update-panel.png"));
            }
            var noticeTimer=(System.Windows.Forms.Timer)Field(popup,"updatedNoticeTimer");
            typeof(System.Windows.Forms.Timer).GetMethod("OnTick",Private).Invoke(noticeTimer,new object[]{EventArgs.Empty});
            Check(String.IsNullOrEmpty(((PanelFeedback)typeof(Details).GetMethod("Feedback",Private).Invoke(popup,null)).UpdatedNotice)&&!noticeTimer.Enabled,"version notice expires without repeating");
            owner.OpenUpdatedPanel(anchor);
            feedback=(PanelFeedback)typeof(Details).GetMethod("Feedback",Private).Invoke(popup,null);
            Check(String.IsNullOrEmpty(feedback.UpdatedNotice),"repeated updated requests do not repeat the notice");
            popup.Close();
            PanelStartup.Forward(PanelOpenRequest.Launcher,launcher,updated);
            Pump(delegate{var current=(Details)Field(owner,"details");return current!=null&&current!=popup&&!current.IsDisposed;});popup=(Details)Field(owner,"details");
            var cursorScreen=Screen.FromPoint(Cursor.Position).WorkingArea;
            Check((Point)Field(popup,"anchor")==new Point(cursorScreen.Right-24,cursorScreen.Top+16),"launcher keeps its existing upper-right anchor");
            popup.Close();owner.HandleTrayMouseUp(MouseButtons.Left);popup=(Details)Field(owner,"details");
            Check((Point)Field(popup,"anchor")==Cursor.Position,"tray click keeps its existing cursor anchor");
            }
            owner.ExitThread();
        }
    }
    [STAThread] static int Main() {
        try{Application.EnableVisualStyles();ArgumentsAndForwarding();Positions();Retries();PopupRouting();Console.WriteLine("PASS "+checks+" update anchor checks; shell visibility/overflow behavior still requires Explorer verification");return 0;}
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
