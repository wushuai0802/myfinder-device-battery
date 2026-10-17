using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

static class WirelessTests {
    static int checks;
    static void Check(bool value,string name){if(!value)throw new Exception("FAIL "+name);checks++;}
    static WirelessDevice Device(string suffix,string kind,int level,bool approximate=false) {
        return new WirelessDevice {Id="device-"+suffix.PadLeft(24,'0'),Name=kind=="keyboard"?"Lofree Hyzen":kind=="headset"?"Razer Headset":"Logitech Mouse",Kind=kind,Percent=level,Fresh=true,Online=true,Provider="fixture",Transport="USB / 2.4G",ConfirmedAtUtc=DateTime.UtcNow.ToString("o"),Approximate=approximate};
    }
    static Bitmap Scene(TrayContext owner,WirelessLayout layout,PanelFeedback feedback) {
        var image=new Bitmap(layout.Size.Width,layout.Size.Height,PixelFormat.Format32bppPArgb);
        using(var drawing=Graphics.FromImage(image)){drawing.Clear(Color.Transparent);CompactRenderer.PaintWireless(drawing,owner,layout,false,feedback);}return image;
    }
    static bool Changed(Bitmap left,Bitmap right,RectangleF rect) {
        for(int y=(int)rect.Top;y<Math.Min(left.Height,rect.Bottom);y++)for(int x=(int)rect.Left;x<Math.Min(left.Width,rect.Right);x++)if(left.GetPixel(x,y)!=right.GetPixel(x,y))return true;return false;
    }
    [STAThread] static int Main(string[] args) {
        if(args.Length>0&&args[0]=="-I") {
            string line;while((line=Console.ReadLine())!=null){if(line.Contains("exit"))break;Console.WriteLine("{\"schema\":1,\"devices\":[],\"nativeMousePresent\":false,\"providerCount\":26}");Console.Out.Flush();}return 0;
        }
        var current=typeof(DeviceSettings).GetField("current",BindingFlags.Static|BindingFlags.NonPublic);object previous=current.GetValue(null);TrayContext owner=null;
        try {
            NativeDisplay.EnableDpiAwareness();Application.EnableVisualStyles();current.SetValue(null,new AudioBindings());
            string output=Path.Combine(Path.GetFullPath(args[0]),"wireless-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
            Check(GenericBatteryWorker.Parse("{broken").Error!=null,"invalid worker output cannot replace verified data");
            string json="{\"schema\":1,\"devices\":[{\"id\":\"device-000000000000000000000001\",\"name\":\"Keyboard\\nInjected\",\"kind\":\"keyboard\",\"percent\":42,\"online\":true,\"fresh\":true,\"confirmedAtUtc\":\""+DateTime.UtcNow.ToString("o")+"\"}]}";
            var snapshot=GenericBatteryWorker.Parse(json);Check(snapshot.Devices.Length==1&&snapshot.Devices[0].Percent==42&&snapshot.Devices[0].Fresh,"valid integer battery reports are accepted");
            Check(!snapshot.Devices[0].Name.Contains("\n"),"untrusted device names do not inject controls");
            var sameModelA=Device("a","keyboard",42);var sameModelB=Device("b","keyboard",42);
            Check(!sameModelA.SameCacheIdentity(sameModelB),"same-model history cannot migrate between different physical identities");
            Check(sameModelA.SameCacheIdentity(Device("a","keyboard",42)),"exact device/provider/type identity can retain history");
            Check(GenericBatteryWorker.Parse(json.Replace("\"percent\":42","\"percent\":200")).Devices[0].Percent==null,"invalid levels are not clamped into fake percentages");
            var cache=Path.Combine(output,"wireless-cache.json");ReadingCache.SaveWireless(cache,snapshot.Devices);var restored=ReadingCache.LoadWireless(cache);
            Check(restored.Length==1&&restored[0].Percent==42&&!restored[0].Fresh&&!restored[0].Online,"disk cache cannot claim a connected device");
            owner=new TrayContext(false,delegate(bool include){return new MouseSnapshot();},delegate(int stage){return null;});
            owner.NativeMousePresent=false;owner.GenericReady=true;owner.WirelessDevices=new[]{Device("1","mouse",75),Device("2","keyboard",42,true),Device("3","headset",19)};
            var navigation=new PopupNavigation();var layout=WirelessLayout.Build(owner,0,500);
            Check(layout.Cards.Count==3&&layout.Size==new Size(260,226),"three generic cards stay compact");
            Check(layout.All.TrueForAll(delegate(WirelessCard c){return c.Name!="ATK A9 Air+";}),"another PC does not get a phantom ATK card");
            Check(layout.Cards[1].Kind=="keyboard"&&layout.Cards[1].Battery.Approximate,"keyboard and coarse-level capabilities reach the UI");
            var controls=PopupPages.Build(owner,navigation,false,layout);bool disjoint=true;for(int a=0;a<controls.Count;a++)for(int b=a+1;b<controls.Count;b++)disjoint&=!controls[a].Rect.IntersectsWith(controls[b].Rect);Check(disjoint,"dynamic card hit areas do not overlap");
            foreach(float scale in new[]{1f,1.25f,1.5f,1.75f,2f})foreach(bool dark in new[]{false,true})foreach(bool contrast in new[]{false,true}) {
                VisualDesign.HighContrastOverride=contrast;
                var size=UiState.ScaleSize(layout.Size,scale);using(var bitmap=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppPArgb))using(var graphics=Graphics.FromImage(bitmap)) {
                    graphics.Clear(Color.Transparent);graphics.ScaleTransform(scale,scale);CompactRenderer.PaintWireless(graphics,owner,layout,dark,new PanelFeedback());
                    Check(bitmap.GetPixel((int)(130*scale),(int)(64*scale)).A==0,"wireless cards retain their transparent gap at "+scale);
                    bitmap.Save(Path.Combine(output,"three-cards-"+(contrast?"contrast-":"")+(dark?"dark-":"light-")+scale.ToString(System.Globalization.CultureInfo.InvariantCulture)+".png"));
                }
            }
            VisualDesign.HighContrastOverride=null;
            var system=Device("4","keyboard",70);system.SourceQuality="system_property";system.QueriedAtUtc=DateTime.UtcNow.ToString("o");
            var systemView=WirelessLayout.View(system);
            Check(!systemView.Fresh&&systemView.SystemProvided&&systemView.Tooltip.Contains("系统提供")&&WirelessLayout.Subtitle(system,systemView).Contains("最近查询"),"Windows-provided battery is distinguished from a device measurement");
            system.Charging=true;system.ChargingCheckedAtUtc=DateTime.UtcNow.AddMinutes(-4).ToString("o");
            Check(!WirelessLayout.View(system).Charging,"expired charging evidence cannot revive on a system property query");
            var stale=Device("5","keyboard",33);stale.Fresh=false;stale.ConfirmedAtUtc=null;
            Check(WirelessLayout.View(stale).Tooltip.Contains("时间未知"),"history without an original timestamp does not invent a confirmation time");
            stale.ConnectionState="unconfirmed";Check(WirelessLayout.Subtitle(stale,WirelessLayout.View(stale))=="连接待确认","cached presence does not claim a confirmed wireless link");
            stale.RefreshScope="provider";Check(WirelessLayout.RefreshText(stale).Contains("同组"),"provider-wide refresh scope is disclosed");
            var many=new List<WirelessDevice>();for(int i=1;i<=9;i++)many.Add(Device(i.ToString(),"keyboard",60));owner.WirelessDevices=many.ToArray();layout=WirelessLayout.Build(owner,0,260);
            Check(layout.Paged&&layout.Size.Height<=260,"many devices fit inside a bounded popup");
            controls=PopupPages.Build(owner,navigation,false,layout);
            using(var baseline=Scene(owner,layout,new PanelFeedback()))foreach(int id in new[]{0,1,903}) {
                var control=controls.Find(delegate(PopupControl c){return c.Id==id;});
                using(var hover=Scene(owner,layout,new PanelFeedback {HoverAction=id}))Check(Changed(baseline,hover,control.Rect),"footer hover is visible for action "+id);
                using(var pressed=Scene(owner,layout,new PanelFeedback {PressedAction=id}))Check(Changed(baseline,pressed,control.Rect),"footer pressed feedback is visible for action "+id);
                using(var focus=Scene(owner,layout,new PanelFeedback {KeyboardFocus=id}))Check(Changed(baseline,focus,control.Rect),"footer keyboard focus is visible for action "+id);
            }
            var oldTarget=controls.Find(delegate(PopupControl c){return c.Id==2001;});string identity=oldTarget.StableKey;
            var reordered=(WirelessDevice[])owner.WirelessDevices.Clone();var firstDevice=reordered[0];reordered[0]=reordered[1];reordered[1]=firstDevice;owner.WirelessDevices=reordered;
            var moved=PopupPages.Build(owner,navigation,false,WirelessLayout.Build(owner,0,260));var same=PopupPages.FindStable(moved,identity);
            Check(same!=null&&same.Id!=oldTarget.Id&&PopupPages.SameAction(oldTarget,same),"device insertion or reorder preserves the original action target");
            Check(!PopupPages.SameAction(oldTarget,moved.Find(delegate(PopupControl c){return c.Id==oldTarget.Id;})),"an old numeric slot cannot transfer an action to a different device");
            Check(PopupPages.ReconcileFocus(moved,identity,controls.IndexOf(oldTarget))==same,"keyboard focus follows stable device identity");
            navigation.Go(PopupPage.Device,oldTarget.Id,identity);navigation.Back();
            Check(navigation.ReturnedFocusKey==identity&&PopupPages.FindStable(moved,navigation.ReturnedFocusKey)==same,"return navigation preserves the original device across hotplug");
            moved.Remove(same);Check(PopupPages.FindStable(moved,identity)==null&&PopupPages.ReconcileFocus(moved,identity,moved.Count-1)!=null,"removed focus falls back to a remaining enabled target");
            var next=WirelessLayout.Build(owner,layout.End,260);Check(next.Start==layout.End&&next.Cards.Count>0,"the next page exposes additional devices");
            AccessibilityChecks();
            owner.WirelessDevices=new WirelessDevice[0];layout=WirelessLayout.Build(owner,0,260);Check(layout.All.Count==1&&layout.All[0].Id=="empty"&&layout.All[0].Name=="无线设备","empty discovery is honest and brand-neutral");
            owner.GenericHealthSummary="部分读取暂不可用（1项）";owner.GenericFailureCount=1;layout=WirelessLayout.Build(owner,0,260);
            Check(layout.All[0].Subtitle=="部分读取器暂不可用","empty partial failure is distinct from having no compatible device");
            owner.GenericHealthSummary=null;owner.GenericFailureCount=0;owner.WirelessDevices=new[]{system};
            var detailRows=PopupPages.Build(owner,new PopupNavigation {Page=PopupPage.Device,SelectedDeviceKey=system.Id,SelectedDeviceName=system.Name},false);
            Check(detailRows.Exists(delegate(PopupControl row){return row.Information&&row.Text=="适配验证"&&row.Value=="待型号实测";}),"a protocol-backed device is not presented as a locally verified model");
            owner.WirelessDevices=new WirelessDevice[0];
            string fake=Path.Combine(output,"worker with spaces");Directory.CreateDirectory(Path.Combine(fake,"runtime"));File.Copy(Assembly.GetExecutingAssembly().Location,Path.Combine(fake,"runtime","python.exe"));File.WriteAllText(Path.Combine(fake,"server.py"),"# fake worker");
            int pid;
            using(var worker=new GenericBatteryWorker(fake)) {
                Check(worker.Available,"a complete portable worker is detected without consulting PATH");var value=worker.Poll();Check(value.Error==null&&value.ProviderCount==26,"local pipe IPC works from a path containing spaces");
                var process=(Process)typeof(GenericBatteryWorker).GetField("process",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(worker);pid=process.Id;
            }
            bool exited=false;for(int i=0;i<30&&!exited;i++){try{using(var process=Process.GetProcessById(pid))exited=process.HasExited;}catch(ArgumentException){exited=true;}if(!exited)Thread.Sleep(20);}
            Check(exited,"closing the widget worker leaves no background process");
            Console.WriteLine("PASS "+checks+" universal battery checks; hardware access replaced with fixtures");Console.WriteLine("Universal UI previews: "+output);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{VisualDesign.HighContrastOverride=null;if(owner!=null)owner.ExitThread();current.SetValue(null,previous);}
    }
    static void AccessibilityChecks() {
        using(var host=new Form {Location=new Point(42,57),ClientSize=new Size(520,300),AccessibleName="设备电量"}) {
            var handle=host.Handle;int invoked=0,focus=10,volume=-1;
            var button=new PopupControl {Id=10,DeviceKey="device-a",ActionKey="refresh",Text="同步键盘",Rect=new RectangleF(10,20,100,30),Busy=true};
            var toggle=new PopupControl {Id=20,Text="Windows麦克风静音",Toggle=true,Checked=true,Rect=new RectangleF(10,55,100,30)};
            var slider=new PopupControl {Id=240,Text="系统输出音量",Slider=true,Value="40%",Rect=new RectangleF(10,90,200,50)};
            var information=new PopupControl {Id=-1,Text="最近确认",Value="12:30",Information=true,Rect=new RectangleF(10,145,200,30)};
            var rows=new List<PopupControl> {button,toggle,slider,information};
            var tree=new PopupAccessibility(host,delegate{return rows;},delegate{return focus;},delegate(PopupControl row){invoked=row.Id;},delegate{return 1.75f;},delegate(PopupControl row){focus=row.Id;},delegate(PopupControl row,int value){volume=value;});
            var accessible=tree.GetChild(0);
            Check(tree.GetChildCount()==rows.Count&&accessible.Role==AccessibleRole.PushButton,"accessibility exposes the same control list used for hit testing");
            var expected=host.RectangleToScreen(Rectangle.FromLTRB(17,35,193,88));
            Check(accessible.Bounds==expected&&tree.HitTest(expected.X+2,expected.Y+2)==accessible,"accessible bounds use actual screen coordinates at fractional scale");
            Check((accessible.State&AccessibleStates.Busy)!=0&&(accessible.State&AccessibleStates.Focused)!=0,"assistive clients receive busy and focused states");
            Check(tree.GetChild(1).Role==AccessibleRole.CheckButton&&(tree.GetChild(1).State&AccessibleStates.Checked)!=0&&tree.GetChild(1).Value=="开启","toggle state and value are available without relying on color");
            tree.GetChild(2).Value="65%";Check(volume==65&&tree.GetChild(2).Role==AccessibleRole.Slider,"accessible slider accepts bounded percentages");
            tree.GetChild(2).Value="101";Check(volume==65,"invalid accessible slider writes are ignored");
            tree.GetChild(3).DoDefaultAction();Check(invoked==0&&(tree.GetChild(3).State&AccessibleStates.ReadOnly)!=0,"informational rows never invoke an operation");
            rows[0]=new PopupControl {Id=55,DeviceKey="device-b",ActionKey="refresh",Text="另一台键盘"};accessible.DoDefaultAction();Check(invoked==0&&(accessible.State&AccessibleStates.Unavailable)!=0,"removed accessible targets cannot operate a new device in the same slot");
            rows.Add(new PopupControl {Id=77,DeviceKey="device-a",ActionKey="refresh",Text="同步键盘"});accessible.DoDefaultAction();Check(invoked==77,"accessible actions resolve the same stable device after a reorder");
            tree.GetChild(1).Select(AccessibleSelection.TakeFocus);Check(focus==20,"accessible focus requests select their visual control");
            rows[1].Enabled=false;tree.GetChild(1).DoDefaultAction();Check(invoked==77&&(tree.GetChild(1).State&AccessibleStates.Unavailable)!=0,"disabled controls reject accessible actions");
        }
    }
}
