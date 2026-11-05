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
    // The updated-panel paths deliberately wait about two seconds for a tray icon
    // before falling back, so a 3 s budget left <1 s of slack under a busy CI/test
    // host. The budget only bounds waiting; completion is still asserted.
    static void Pump(Func<bool> done,int timeout=10000){var clock=Stopwatch.StartNew();while(!done()&&clock.ElapsedMilliseconds<timeout){Application.DoEvents();Thread.Sleep(5);}Check(done(),"asynchronous request completes");}
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
        var mdf=TrayPanelPlacement.Fallback(new Rectangle(0,0,2560,1440),new Rectangle(0,24,2560,1392),TaskbarEdge.Bottom);
        Check(mdf.Point==new Point(2559,24)&&mdf.Edge==TaskbarEdge.Top,"auto-hidden bottom taskbar with MDF uses the upper-right work area");
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
    static void AnchorPriority(){
        var screen=new Rectangle(0,0,2560,1440);var area=new Rectangle(0,24,2560,1392);
        var taskbar=new Rectangle(0,1392,2560,48);var icon=new Rectangle(2480,1400,24,24);var click=new Point(2100,12);
        var screens=new[]{screen,new Rectangle(-1920,0,1920,1080)};var areas=new[]{area,new Rectangle(-1920,0,1920,1040)};
        var chosen=TrayPanelPlacement.FindAnchor(click,screen,icon,true,screens,areas,taskbar,TaskbarEdge.Bottom);
        Check(chosen.HasValue&&chosen.Value.Point==click&&chosen.Value.WorkArea==new Rectangle(0,24,2560,1368)&&chosen.Value.Edge==TaskbarEdge.Top,"MDF click wins over the hidden bottom tray while reserving its overlap");
        var location=TrayPanelPlacement.Position(chosen.Value.Point,area,new Size(260,300),chosen.Value.Edge);
        Check(location==new Point(1970,24),"MDF remembered click opens below the top icon");
        Check(TrayPanelPlacement.FindAnchor(click,screen,icon,false,screens,areas,taskbar,TaskbarEdge.Bottom).Value.Point==click,"remembered click also wins over a visible shell icon");
        Check(!TrayPanelPlacement.FindAnchor(null,null,icon,true,screens,areas,taskbar,TaskbarEdge.Bottom).HasValue,"auto-hidden taskbar rectangle is ignored without a last click");
        var stale=new Rectangle(0,0,2560,1600);
        Check(!TrayPanelPlacement.FindAnchor(click,stale,icon,true,screens,areas,taskbar,TaskbarEdge.Bottom).HasValue,"screen bounds must match exactly");
        Check(!TrayPanelPlacement.FindAnchor(new Point(3000,10),screen,null,false,screens,areas,null,TaskbarEdge.Bottom).HasValue,"saved point outside its screen is ignored");
        chosen=TrayPanelPlacement.FindAnchor(click,stale,icon,false,screens,areas,taskbar,TaskbarEdge.Bottom);
        Check(chosen.HasValue&&chosen.Value.Point==new Point(2492,1412)&&chosen.Value.Edge==TaskbarEdge.Bottom,"valid visible shell icon follows stale remembered screen");
        foreach(var hidden in new[]{new Rectangle(2570,1400,24,24),new Rectangle(2550,1400,24,24),Rectangle.Empty})
            Check(!TrayPanelPlacement.FindAnchor(null,null,hidden,false,screens,areas,taskbar,TaskbarEdge.Bottom).HasValue,"shell icon must be fully inside a current screen");
        chosen=TrayPanelPlacement.FindAnchor(new Point(-100,20),screens[1],icon,true,screens,areas,taskbar,TaskbarEdge.Bottom);
        Check(chosen.Value.WorkArea==areas[1]&&chosen.Value.Edge==TaskbarEdge.Top,"remembered click selects its negative-coordinate monitor");
        chosen=TrayPanelPlacement.FindAnchor(null,null,new Rectangle(-150,2,20,20),false,screens,areas,taskbar,TaskbarEdge.Bottom);
        Check(chosen.Value.WorkArea==areas[1]&&chosen.Value.Edge==TaskbarEdge.Top,"secondary shell icon uses its own screen edge");
        foreach(TaskbarEdge edge in Enum.GetValues(typeof(TaskbarEdge))){
            var fallback=TrayPanelPlacement.Fallback(screens[1],areas[1],edge);
            Check(fallback.Edge==edge&&fallback.Point==TrayPanelPlacement.Anchor(null,areas[1],edge).Point,"no top reservation preserves taskbar corner: "+edge);
        }
    }
    static void AutoHiddenTaskbarAreas(){
        var screen=new Rectangle(0,0,1600,900);
        var edges=new[]{TaskbarEdge.Left,TaskbarEdge.Top,TaskbarEdge.Right,TaskbarEdge.Bottom};
        var bars=new[]{new Rectangle(0,0,60,900),new Rectangle(0,0,1600,60),new Rectangle(1540,0,60,900),new Rectangle(0,840,1600,60)};
        var expected=new[]{new Rectangle(60,0,1540,900),new Rectangle(0,60,1600,840),new Rectangle(0,0,1540,900),new Rectangle(0,0,1600,840)};
        var partial=new[]{new Rectangle(2,0,1598,900),new Rectangle(0,2,1600,898),new Rectangle(0,0,1598,900),new Rectangle(0,0,1600,898)};
        var screens=new[]{screen,new Rectangle(-1600,0,1600,900),new Rectangle(0,-900,1600,900)};
        for(int i=0;i<edges.Length;i++){
            var edge=edges[i];var bar=bars[i];
            var reserved=TrayPanelPlacement.Reserved(screen,screen,bar,edge,true);
            Check(reserved==expected[i],"reserve full auto-hidden taskbar thickness: "+edge);
            Check(TrayPanelPlacement.Reserved(screen,screen,bar,edge,false)==screen,"visible state never reserves an overlapping bar: "+edge);
            Check(TrayPanelPlacement.Reserved(screen,reserved,bar,edge,false)==reserved,"visible taskbar is not deducted twice: "+edge);
            Check(TrayPanelPlacement.Reserved(screen,reserved,bar,edge,true)==reserved,"reservation is idempotent: "+edge);
            Check(TrayPanelPlacement.Reserved(screen,partial[i],bar,edge,true)==reserved,"only remaining overlap is deducted: "+edge);
            var floating=bar;floating.Inflate(-1,-1);
            Check(TrayPanelPlacement.Reserved(screen,screen,floating,edge,true)==screen,"floating appbar is ignored: "+edge);
            var inset=bar;inset.Offset(edge==TaskbarEdge.Left?10:edge==TaskbarEdge.Right?-10:0,edge==TaskbarEdge.Top?10:edge==TaskbarEdge.Bottom?-10:0);
            Check(TrayPanelPlacement.Reserved(screen,screen,inset,edge,true)==screen,"full-span bar away from screen edge is ignored: "+edge);
            var shortBar=bar;if(edge==TaskbarEdge.Left||edge==TaskbarEdge.Right)shortBar.Height--;else shortBar.Width--;
            Check(TrayPanelPlacement.Reserved(screen,screen,shortBar,edge,true)==screen,"partial-edge bar is ignored: "+edge);
            for(int monitor=1;monitor<screens.Length;monitor++)
                Check(TrayPanelPlacement.Reserved(screens[monitor],screens[monitor],bar,edge,true)==screens[monitor],"other monitor is unaffected: "+edge+"/"+monitor);
            var shiftedBar=bar;shiftedBar.Offset(-1600,0);var shiftedArea=reserved;shiftedArea.Offset(-1600,0);
            Check(TrayPanelPlacement.Reserved(screens[1],screens[1],shiftedBar,edge,true)==shiftedArea,"taskbar on negative-coordinate monitor: "+edge);
            var click=new Point(bar.Left+bar.Width/2,bar.Top+bar.Height/2);
            var remembered=TrayPanelPlacement.FindAnchor(click,screen,null,true,screens,screens,bar,edge).Value;
            Check(remembered.Point==click&&remembered.WorkArea==reserved&&remembered.Edge==edge,"saved tray click reserves work area: "+edge);
            var icon=new Rectangle(click.X-1,click.Y-1,2,2);
            var visible=TrayPanelPlacement.FindAnchor(null,null,icon,false,new[]{screen},new[]{reserved},bar,edge).Value;
            Check(visible.WorkArea==reserved&&visible.Edge==edge,"shell icon keeps already-reserved visible work area: "+edge);
            var fallback=TrayPanelPlacement.Fallback(screen,screen,edge,bar,true);
            Check(fallback.WorkArea==reserved&&fallback.Edge==edge,"missing icon fallback reserves work area: "+edge);
            foreach(var anchor in new[]{remembered,fallback}){
                var size=new Size(260,300);var bounds=new Rectangle(TrayPanelPlacement.Position(anchor.Point,anchor.WorkArea,size,anchor.Edge),size);
                Check(reserved.Contains(bounds)&&!bar.IntersectsWith(bounds),"panel clears revealed taskbar: "+edge);
                Check(TrayPanelPlacement.Position(anchor.Point,anchor.WorkArea,reserved.Size,anchor.Edge)==reserved.Location,"maximum-size panel fits reserved area: "+edge);
            }
        }
        Check(TrayPanelPlacement.Reserved(screen,screen,null,TaskbarEdge.Bottom,true)==screen,"missing taskbar leaves area unchanged");
        Check(TrayPanelPlacement.Reserved(screen,screen,Rectangle.Empty,TaskbarEdge.Bottom,true)==screen,"empty taskbar leaves area unchanged");
        var mdfScreen=new Rectangle(0,0,2560,1440);var mdfArea=new Rectangle(0,24,2560,1416);var bottom=new Rectangle(0,1368,2560,72);
        var mdfExpected=new Rectangle(0,24,2560,1344);
        Check(TrayPanelPlacement.Reserved(mdfScreen,mdfArea,bottom,TaskbarEdge.Bottom,true)==mdfExpected,"MDF top reservation survives measured 72px bottom taskbar");
        var mdf=TrayPanelPlacement.Fallback(mdfScreen,mdfArea,TaskbarEdge.Bottom,bottom,true);
        Check(mdf.WorkArea==mdfExpected&&mdf.Point==new Point(2559,24)&&mdf.Edge==TaskbarEdge.Top,"MDF fallback keeps upper-right placement");
        var saved=TrayPanelPlacement.FindAnchor(new Point(2100,12),mdfScreen,null,true,new[]{mdfScreen},new[]{mdfArea},bottom,TaskbarEdge.Bottom).Value;
        Check(saved.WorkArea==mdfExpected&&saved.Edge==TaskbarEdge.Top&&saved.Point==new Point(2100,12),"MDF saved top icon keeps priority with bottom reservation");
    }
    static void ReservedPanelLimits(){
        DeviceSettings.UseTransientConfiguration(new AudioBindings {ReducedMotion=true,RememberReadings=false});
        using(var owner=new TrayContext(false,delegate(bool battery){return new MouseSnapshot();},delegate(int stage){throw new Exception("unexpected hardware write");})){
            owner.NativeMousePresent=false;owner.GenericReady=true;owner.WirelessDevices=new WirelessDevice[30];
            for(int i=0;i<owner.WirelessDevices.Length;i++)owner.WirelessDevices[i]=new WirelessDevice {Id="fixture-"+i,Name="Fixture keyboard",Kind="keyboard"};
            using(var popup=new Details(owner))foreach(float scale in new[]{1f,1.5f,2f}){
                var screen=new Rectangle(0,0,1600,900);var bar=new Rectangle(0,828,1600,72);
                var placement=TrayPanelPlacement.Fallback(screen,screen,TaskbarEdge.Bottom,bar,true);
                typeof(Details).GetField("updatedAnchor",Private).SetValue(popup,placement);
                typeof(Details).GetField("anchor",Private).SetValue(popup,placement.Point);
                typeof(Details).GetField("dpiScale",Private).SetValue(popup,scale);
                typeof(Details).GetMethod("UpdateLayoutSize",Private).Invoke(popup,null);
                var layout=(WirelessLayout)typeof(Details).GetMethod("MainWireless",Private).Invoke(popup,null);
                Check(layout.Paged&&layout.Size.Height<=(int)(placement.WorkArea.Height/scale)-24,"wireless cards paginate within reserved height at "+scale);
                Check(popup.ClientSize==UiState.ScaleSize(layout.Size,scale),"footer is laid out rather than clipped by the size cap at "+scale);
                Check(placement.WorkArea.Contains(popup.Bounds)&&!bar.IntersectsWith(popup.Bounds),"panel fits above taskbar at "+scale);
                typeof(Details).GetMethod("ControlsForPage",Private).Invoke(popup,null);
                var navigation=(PopupNavigation)Field(popup,"navigation");
                Check(navigation.MaximumHeight==(int)(placement.WorkArea.Height/scale)-24,"menu size limit uses the same reserved area at "+scale);
            }
            owner.ExitThread();
        }
    }
    static void PositionSettings(){
        string path=System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tray-position-settings.json");
        var settings=new AudioBindings {Theme="dark",MouseAlias="保留名称"};
        DeviceSettings.SaveBindings(path,settings);
        Check(!System.IO.File.ReadAllText(path).Contains("LastTrayIconPosition")&&DeviceSettings.LoadBindings(path).LastTrayIconPosition==null,"old configuration omits the optional position and loads empty");
        settings.LastTrayIconPosition=TrayIconPosition.Capture(new Point(-100,12),new Rectangle(-1920,0,1920,1080));
        DeviceSettings.SaveBindings(path,settings);var restored=DeviceSettings.LoadBindings(path);
        Check(restored.LastTrayIconPosition.Point==new Point(-100,12)&&restored.LastTrayIconPosition.Bounds==new Rectangle(-1920,0,1920,1080),"last click and complete screen bounds survive config reload");
        var copy=restored.Clone();copy.LastTrayIconPosition.X=999;
        Check(restored.LastTrayIconPosition.X==-100&&copy.Theme=="dark"&&copy.MouseAlias=="保留名称","configuration clones isolate the position and preserve preferences");
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
        int saves=0,reads=0;var fixedPoint=new Point(Screen.PrimaryScreen.Bounds.Left+150,Screen.PrimaryScreen.Bounds.Top+12);
        using(var owner=new TrayContext(false,delegate(bool battery){return new MouseSnapshot();},delegate(int stage){throw new Exception("unexpected hardware write");},null,null,configurationWriter:delegate(AudioBindings settings){saves++;DeviceSettings.UseTransientConfiguration(settings);})) {
            owner.CursorPosition=delegate{reads++;return fixedPoint;};
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
            var cursorScreen=TrayPanelPlacement.WorkingArea(Screen.FromPoint(fixedPoint));
            Check((Point)Field(popup,"anchor")==new Point(cursorScreen.Right-24,cursorScreen.Top+16),"launcher keeps its existing upper-right anchor");
            Check(saves==0,"updated and legacy launcher paths do not record an icon click");
            popup.Close();reads=0;owner.HandleTrayMouseUp(MouseButtons.Left);popup=(Details)Field(owner,"details");
            Check((Point)Field(popup,"anchor")==fixedPoint&&reads==1,"tray click samples the injected cursor once and keeps that anchor");
            var stored=DeviceSettings.Current.LastTrayIconPosition;
            Check(saves==1&&stored.Point==fixedPoint&&stored.Bounds==Screen.FromPoint(fixedPoint).Bounds,"up-only MDF click saves the point and screen bounds");
            owner.HandleTrayMouseUp(MouseButtons.Right);Check(saves==1,"right click does not overwrite the last opening");
            popup.Close();fixedPoint.Offset(50,0);owner.HandleTrayMouseDown(MouseButtons.Left);owner.HandleTrayMouseUp(MouseButtons.Left);popup=(Details)Field(owner,"details");
            Check(saves==2&&DeviceSettings.Current.LastTrayIconPosition.Point==fixedPoint,"paired tray clicks update the saved position");
            owner.HandleTrayMouseDown(MouseButtons.Left);owner.HandleTrayMouseUp(MouseButtons.Left);Check(saves==2,"closing a panel does not record another opening");
            popup.Close();owner.RequestUpdatedPanel();popup=(Details)Field(owner,"details");
            Check(((TrayPanelAnchor)Field(popup,"updatedAnchor")).Point==fixedPoint&&saves==2,"updated request reads the saved click without rewriting it");
            }
            owner.ExitThread();
        }
    }
    [STAThread] static int Main() {
        try{Application.EnableVisualStyles();ArgumentsAndForwarding();Positions();AnchorPriority();AutoHiddenTaskbarAreas();ReservedPanelLimits();PositionSettings();Retries();PopupRouting();Console.WriteLine("PASS "+checks+" update anchor checks; shell visibility/overflow behavior still requires Explorer verification");return 0;}
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
