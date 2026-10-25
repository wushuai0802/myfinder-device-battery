using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

// UI-only fixtures: no real generic worker, HID, audio write or settings save.
static class WirelessDpiUiTests {
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value,string name){if(!value)throw new Exception("FAIL: "+name);checks++;}
    static void Pump(Func<bool> done) {
        var watch=Stopwatch.StartNew();while(!done()&&watch.ElapsedMilliseconds<3000){Application.DoEvents();Thread.Sleep(2);}Check(done(),"fake readers settle");
    }
    static DpiConfig NativeDpi() {
        var result=new DpiConfig {DeviceKey=MouseBattery.DeviceKey("fixture-native"),ActiveStage=7,TimestampUtc=DateTime.UtcNow};
        foreach(int value in new[]{400,800,1200,1600,2400,3200,4800,6400})result.Stages.Add(new DpiStage {XDpi=value,YDpi=value});return result;
    }
    static WirelessDevice Device(string suffix,string source="onboard",int[] stages=null) {
        return new WirelessDevice {Id="device-"+suffix.PadLeft(24,'0'),Name="Fixture GPW "+suffix,Kind="mouse",Percent=78,Provider="logitech",Transport="2.4G",Online=true,Fresh=true,ConnectionState="connected",SourceQuality="verified",MeasurementState="measured",ConfirmedAtUtc=DateTime.UtcNow.ToString("o"),Dpi=new WirelessDpi {Supported=true,CanSet=true,Current=800,Stages=stages??new[]{400,800,1600,3200},StageSource=source,CheckedAtUtc=DateTime.UtcNow.ToString("o"),TargetToken=new string(suffix[0],64)}};
    }
    static TrayContext Owner() {
        DeviceSettings.UseTransientConfiguration(new AudioBindings {RememberReadings=false,Theme="light",ReducedMotion=true,LowBatteryNotifications=false});
        var owner=new TrayContext(false,delegate(bool battery){return new MouseSnapshot {Battery=battery?new BatteryReading {Percent=80,TimestampUtc=DateTime.UtcNow}:null,Dpi=NativeDpi()};},
            delegate(int stage){throw new Exception("Unexpected native DPI write");},delegate{return new HeadsetReading {Connected=false,ConnectionVerified=true};},delegate{return new AudioReading {OperationSucceeded=true,Devices=new AudioDeviceChoice[0]};},false,
            delegate(string id){throw new Exception("Unexpected audio write");},delegate(bool mute){throw new Exception("Unexpected microphone write");},delegate(string id,int value){throw new Exception("Unexpected volume write");},delegate{return false;});
        Pump(delegate{return owner.Audio!=null&&!owner.IsRefreshing&&!owner.IsAudioBusy&&!owner.IsHeadsetRefreshing;});owner.NativeMousePresent=false;owner.GenericReady=true;owner.Dpi=NativeDpi();return owner;
    }
    static List<PopupControl> Controls(TrayContext owner,int height=1000){return PopupPages.Build(owner,new PopupNavigation(),false,WirelessLayout.Build(owner,0,height));}
    static PopupControl Stage(List<PopupControl> rows,string id,int value){return rows.Find(delegate(PopupControl row){return row.DeviceKey==id&&row.DpiValue==value;});}
    static bool Changed(Bitmap left,Bitmap right,RectangleF rectangle,float scale) {
        for(int y=Math.Max(0,(int)Math.Floor(rectangle.Top*scale));y<Math.Min(left.Height,(int)Math.Ceiling(rectangle.Bottom*scale));y++)for(int x=Math.Max(0,(int)Math.Floor(rectangle.Left*scale));x<Math.Min(left.Width,(int)Math.Ceiling(rectangle.Right*scale));x++)if(left.GetPixel(x,y)!=right.GetPixel(x,y))return true;return false;
    }
    static Bitmap Render(TrayContext owner,WirelessLayout layout,bool dark,float scale,PanelFeedback feedback) {
        var size=UiState.ScaleSize(layout.Size,scale);var image=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppPArgb);
        using(var graphics=Graphics.FromImage(image)){graphics.Clear(Color.Transparent);graphics.ScaleTransform(scale,scale);CompactRenderer.PaintWireless(graphics,owner,layout,dark,feedback);}return image;
    }
    static void CapabilityAndGeometry(TrayContext owner) {
        var gpw=Device("a");var ordinary=Device("b");ordinary.Dpi=null;var keyboard=Device("c");keyboard.Kind="keyboard";owner.WirelessDevices=new[]{gpw,ordinary,keyboard};
        var layout=WirelessLayout.Build(owner,0,1000);var rows=Controls(owner);
        Check(layout.All[0].Height==96&&layout.All[1].Height==60&&layout.All[2].Height==60,"only a supported mouse with its own stages expands the generic card");
        Check(rows.FindAll(delegate(PopupControl row){return row.DpiValue>0;}).Count==4&&Stage(rows,gpw.Id,800).Selected,"generic confirmed value is selected using the device model");
        Check(Stage(rows,gpw.Id,800).Enabled&&!Stage(rows,gpw.Id,1600).Selected&&Stage(rows,ordinary.Id,800)==null,"ordinary devices never inherit the native ATK DPI model or controls");
        Check(rows.TrueForAll(delegate(PopupControl row){return row.DpiValue<=0||row.Id>=3000&&row.TargetToken==gpw.Dpi.TargetToken;}),"generic DPI actions carry their own token and use a separate action range");
        bool disjoint=true;for(int i=0;i<rows.Count;i++)for(int j=i+1;j<rows.Count;j++)disjoint&=!rows[i].Rect.IntersectsWith(rows[j].Rect);Check(disjoint,"DPI stages, device header, refresh, volume and footer have disjoint hit areas");
        foreach(int count in new[]{1,4,5,8}) {
            var values=new[]{400,800,1200,1600,2400,3200,4800,6400};var stages=new int[count];Array.Copy(values,stages,count);gpw.Dpi.Stages=stages;gpw.Dpi.Current=stages[0];owner.WirelessDevices=new[]{gpw};layout=WirelessLayout.Build(owner,0,260);rows=Controls(owner,260);
            Check(layout.All[0].Height==(count>4?126:96)&&rows.FindAll(delegate(PopupControl row){return row.DpiValue>0;}).Count==count,"one to eight device stages reserve the correct compact DPI rows");
            Check(layout.Size.Height<=260&&rows.TrueForAll(delegate(PopupControl row){return row.Rect.Left>=0&&row.Rect.Right<=260&&row.Rect.Bottom<=layout.Size.Height;}),"expanded DPI geometry fits the bounded 260-pixel popup");
        }
        gpw.Dpi.Stages=new int[0];Check(WirelessLayout.Build(owner,0,1000).All[0].Height==60,"a supported read without a stage list does not create an empty DPI row");
        gpw.Dpi.Stages=new[]{400,800,1600,3200};gpw.Dpi.Current=800;gpw.Dpi.Supported=false;Check(WirelessLayout.Build(owner,0,1000).All[0].Height==60&&Controls(owner).FindAll(delegate(PopupControl row){return row.DpiValue>0;}).Count==0,"unsupported DPI metadata leaves the original compact card unchanged");gpw.Dpi.Supported=true;
        owner.NativeMousePresent=true;owner.WirelessDevices=new[]{gpw};layout=WirelessLayout.Build(owner,0,1000);rows=Controls(owner);
        Check(layout.All[0].NativeMouse&&layout.All[0].Height==126&&layout.All[1].Height==96,"native ATK and generic GPW card sizes use independent stage lists");
        Check(rows.FindAll(delegate(PopupControl row){return row.Id>=10&&row.Id<18&&row.DeviceKey=="native:mouse";}).Count==8&&Stage(rows,gpw.Id,800).Selected,"adding generic DPI preserves all native ATK actions and confirmed state");owner.NativeMousePresent=false;
    }
    static void FreshnessAndSource(TrayContext owner) {
        var gpw=Device("a","quick");owner.WirelessDevices=new[]{gpw};var rows=Controls(owner);
        Check(Stage(rows,gpw.Id,800).Text.Contains("快捷DPI")&&Stage(rows,gpw.Id,800).Detail.Contains("不代表设备板载"),"quick values are described as software shortcuts rather than stored device presets");
        Check(WirelessLayout.DpiSubtitle(gpw,null,null).StartsWith("快捷DPI"),"the card subtitle distinguishes quick DPI");
        gpw.Dpi.StageSource="onboard";Check(WirelessLayout.DpiSubtitle(gpw,null,null).StartsWith("DPI ·"),"actual onboard presets retain the DPI label");
        var states=new Action[]{delegate{gpw.Dpi.CheckedAtUtc=DateTime.UtcNow.AddSeconds(-91).ToString("o");},delegate{gpw.ConnectionState="unconfirmed";},delegate{gpw.Online=false;},delegate{gpw.Dpi.TargetToken=null;},delegate{gpw.Dpi.Current=null;},delegate{gpw.Dpi.Error="DPI读取待确认";},delegate{gpw.Dpi.CanSet=false;}};
        foreach(var change in states) {
            gpw=Device("a");owner.WirelessDevices=new[]{gpw};change();rows=Controls(owner);var stage=Stage(rows,gpw.Id,1600);var refresh=rows.Find(delegate(PopupControl row){return row.Id>=2000&&row.Id<3000;});
            Check(stage!=null&&!stage.Enabled&&refresh!=null&&refresh.Enabled,"unconfirmed or read-only DPI disables writes while keeping read refresh available");
            Check(PopupPages.Hit(rows,new PointF(stage.Rect.Left+stage.Rect.Width/2,stage.Rect.Top+stage.Rect.Height/2))==null,"disabled DPI cannot be activated by pointer hit testing");
        }
        gpw=Device("a");gpw.Fresh=false;gpw.ConfirmedAtUtc=DateTime.UtcNow.AddMinutes(-4).ToString("o");owner.WirelessDevices=new[]{gpw};
        Check(Stage(Controls(owner),gpw.Id,1600).Enabled,"battery history does not replace independent fresh DPI evidence");
        gpw.Dpi.StageSource="quick";gpw.Dpi.CheckedAtUtc=DateTime.UtcNow.AddMinutes(-4).ToString("o");
        var navigation=new PopupNavigation {Page=PopupPage.Device,SelectedDeviceKey=gpw.Id,SelectedDeviceName=gpw.Name,VisibleChoices=7};var details=new List<PopupControl>();
        for(int page=0;page<8;page++){var part=PopupPages.Build(owner,navigation,false);details.AddRange(part);if(!part.Exists(delegate(PopupControl row){return row.Id==8;}))break;navigation.Offset+=navigation.VisibleChoices;}
        Check(details.Exists(delegate(PopupControl row){return row.Information&&row.Text=="快捷DPI"&&row.Value=="上次 800";})&&details.Exists(delegate(PopupControl row){return row.Information&&row.Text=="DPI来源"&&row.Value=="快捷值，非板载";}),"device details expose stale current value and the true shortcut source");
        Check(details.Exists(delegate(PopupControl row){return row.Information&&row.Text=="DPI最近确认"&&!String.IsNullOrEmpty(row.Value);}),"DPI confirmation time is distinct from the battery timestamp");
    }
    static void IdentityAndAccessibility(TrayContext owner) {
        var a=Device("a");var b=Device("b");owner.WirelessDevices=new[]{a,b};var rows=Controls(owner);var pressed=Stage(rows,a.Id,1600);string identity=pressed.StableKey;
        using(var host=new Form {ClientSize=new Size(520,800)}) {
            var handle=host.Handle;PopupControl invoked=null;int calls=0;
            var tree=new PopupAccessibility(host,delegate{return rows;},delegate{return pressed.Id;},delegate(PopupControl row){invoked=row;calls++;},delegate{return 1.75f;},delegate(PopupControl row){},null);
            var accessible=tree.GetChild(rows.IndexOf(pressed));var confirmed=tree.GetChild(rows.IndexOf(Stage(rows,a.Id,800)));
            Check((confirmed.State&AccessibleStates.Selected)!=0&&(accessible.State&AccessibleStates.Selected)==0&&accessible.Role==AccessibleRole.PushButton&&accessible.Name.Contains(a.Name),"MSAA identifies the mouse and actual confirmed stage without selecting a different target");
            owner.WirelessDevices=new[]{b,a};rows=Controls(owner);var moved=Stage(rows,a.Id,1600);
            Check(moved.Id!=pressed.Id&&moved.StableKey==identity&&PopupPages.SameAction(pressed,moved)&&!PopupPages.SameAction(pressed,rows.Find(delegate(PopupControl row){return row.Id==pressed.Id;})),"a device reorder cannot redirect a captured DPI action to the new occupant of its numeric slot");
            Check(PopupPages.ReconcileFocus(rows,identity,1)==moved,"keyboard focus follows device, value and token after a reorder");accessible.DoDefaultAction();
            Check(calls==1&&invoked.DeviceKey==a.Id&&invoked.DpiValue==1600&&invoked.TargetToken==a.Dpi.TargetToken,"accessible activation resolves the original device DPI action after a reorder");
            a.Dpi.Stages=new[]{3200,1600,800,400};rows=Controls(owner);Check(SameStageIdentity(rows,identity,a.Id,1600),"reordered stage values retain their logical action identity");
            a.Dpi.TargetToken=new string('c',64);rows=Controls(owner);accessible.DoDefaultAction();
            Check(calls==1&&PopupPages.FindStable(rows,identity)==null&&(accessible.State&AccessibleStates.Unavailable)!=0&&!PopupPages.SameAction(pressed,Stage(rows,a.Id,1600)),"a changed device target token retires old pointer and accessibility actions");
            owner.WirelessDevices=new[]{b};rows=Controls(owner);accessible.DoDefaultAction();Check(calls==1,"removing the original device cannot transfer its accessible action to another mouse");
            owner.WirelessDevices=new[]{a};a.Dpi.CheckedAtUtc=DateTime.UtcNow.AddMinutes(-4).ToString("o");rows=Controls(owner);var disabled=tree.GetChild(rows.IndexOf(Stage(rows,a.Id,1600)));disabled.DoDefaultAction();
            Check(calls==1&&(disabled.State&AccessibleStates.Unavailable)!=0,"stale DPI rejects accessible invocation as well as pointer hits");
        }
    }
    static bool SameStageIdentity(List<PopupControl> rows,string identity,string id,int value){var row=Stage(rows,id,value);return row!=null&&row.StableKey==identity;}
    static void PendingPresentation(TrayContext owner,string output) {
        var a=Device("a");var b=Device("b");owner.WirelessDevices=new[]{a,b};var layout=WirelessLayout.Build(owner,0,1000);var original=Controls(owner);
        var queue=(Queue<WirelessDpiRequest>)typeof(TrayContext).GetField("pendingWirelessDpi",Private).GetValue(owner);
        var blocked=typeof(TrayContext).GetField("wirelessDpiNeedsReconcile",Private);object previousBlocked=blocked.GetValue(owner);
        int epoch=(int)typeof(TrayContext).GetField("historyGeneration",Private).GetValue(owner);
        try {
            using(var before=Render(owner,layout,false,1.75f,new PanelFeedback())) {
                queue.Enqueue(new WirelessDpiRequest(a.Id,a.Dpi.TargetToken,1600,800,a.Provider,a.Kind,epoch));var rows=Controls(owner);var current=Stage(rows,a.Id,800);var pending=Stage(rows,a.Id,1600);
                Check(current.Selected&&pending.Busy&&!pending.Selected&&!pending.Enabled&&!current.Enabled&&a.Dpi.Current==800,"pending disables duplicate writes but preserves the actual confirmed value and separate target state");
                Check(Stage(rows,b.Id,1600).Enabled&&!Stage(rows,b.Id,1600).Busy,"pending DPI feedback remains isolated to the requested device");
                using(var after=Render(owner,layout,false,1.75f,new PanelFeedback())) {
                    Check(Changed(before,after,pending.Rect,1.75f)&&!Changed(before,after,current.Rect,1.75f),"pending outline is distinct while the last confirmed selection keeps its visual highlight");
                    after.Save(Path.Combine(output,"pending-onboard-1.75-light.png"));
                }
                using(var after=Render(owner,layout,true,1.75f,new PanelFeedback()))after.Save(Path.Combine(output,"pending-onboard-1.75-dark.png"));
                Check(WirelessLayout.DpiSubtitle(a,owner.DesiredWirelessDpi(a.Id,a.Dpi.TargetToken),null).Contains("等待 1600"),"the subtitle names the pending target without replacing current DPI");
                a.Dpi.TargetToken=new string('c',64);rows=Controls(owner);
                Check(!owner.DesiredWirelessDpi(a.Id,a.Dpi.TargetToken).HasValue&&!Stage(rows,a.Id,1600).Busy,"a replacement target token cannot inherit the former token's pending state");
            }
            queue.Clear();blocked.SetValue(owner,true);var blockedRows=Controls(owner);
            Check(blockedRows.TrueForAll(delegate(PopupControl row){return row.DpiValue<=0||!row.Enabled&&!row.Selected;})&&owner.WirelessDpiFailure(b.Id,b.Dpi.TargetToken).Contains("待确认"),"unknown-result reconciliation suppresses current selection and offers a clear temporary status");
        }finally {queue.Clear();blocked.SetValue(owner,previousBlocked);}
    }
    static void Rendering(TrayContext owner,string output) {
        var gpw=Device("a","quick",new[]{400,800,1200,1600,2400,3200,4800,6400});gpw.Name="Fixture GPW 设备名称与长名称布局检查";owner.WirelessDevices=new[]{gpw};var layout=WirelessLayout.Build(owner,0,260);var rows=Controls(owner,260);var target=Stage(rows,gpw.Id,1600);
        foreach(float scale in new[]{1f,1.25f,1.5f,1.75f,2f})foreach(bool dark in new[]{false,true})foreach(bool contrast in new[]{false,true}) {
            VisualDesign.HighContrastOverride=contrast;
            using(var baseline=Render(owner,layout,dark,scale,new PanelFeedback()))
            using(var hover=Render(owner,layout,dark,scale,new PanelFeedback {HoverAction=target.Id}))
            using(var pressed=Render(owner,layout,dark,scale,new PanelFeedback {PressedAction=target.Id}))
            using(var focused=Render(owner,layout,dark,scale,new PanelFeedback {KeyboardFocus=target.Id})) {
                Check(Changed(baseline,hover,target.Rect,scale)&&Changed(baseline,pressed,target.Rect,scale)&&Changed(baseline,focused,target.Rect,scale),"generic DPI has visible hover, press and keyboard focus at "+scale+" dark="+dark+" contrast="+contrast);
                Check(baseline.GetPixel((int)(130*scale),(int)((layout.VolumeTop-4)*scale)).A==0,"expanded generic DPI preserves the transparent gap above global volume at "+scale);
                string suffix=scale.ToString(System.Globalization.CultureInfo.InvariantCulture)+(dark?"-dark":"-light")+(contrast?"-contrast":"");baseline.Save(Path.Combine(output,"quick-eight-"+suffix+".png"));
            }
        }
        VisualDesign.HighContrastOverride=null;gpw.Dpi.StageSource="onboard";gpw.Dpi.Stages=new[]{400,800,1600,3200};layout=WirelessLayout.Build(owner,0,260);
        foreach(bool dark in new[]{false,true})using(var image=Render(owner,layout,dark,1.75f,new PanelFeedback()))image.Save(Path.Combine(output,"onboard-four-1.75"+(dark?"-dark":"-light")+".png"));
    }
    [STAThread] static int Main(string[] args) {
        var cached=typeof(DeviceSettings).GetField("current",BindingFlags.NonPublic|BindingFlags.Static);object previous=cached.GetValue(null);TrayContext owner=null;
        try {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);NativeDisplay.EnableDpiAwareness();Application.EnableVisualStyles();owner=Owner();
            string root=args.Length==0?AppDomain.CurrentDomain.BaseDirectory:Path.GetFullPath(args[0]);string output=Path.Combine(root,"wireless-dpi-ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
            CapabilityAndGeometry(owner);FreshnessAndSource(owner);IdentityAndAccessibility(owner);PendingPresentation(owner,output);Rendering(owner,output);
            Console.WriteLine("PASS "+checks+" generic DPI UI checks; isolated fake snapshots, hidden owned accessibility host, no hardware or settings writes");Console.WriteLine("Generic DPI previews: "+output);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally {VisualDesign.HighContrastOverride=null;if(owner!=null){owner.ExitThread();owner.Dispose();}cached.SetValue(null,previous);}
    }
}
