using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using AtkBattery;
using AtkBatteryTray;

// Offline snapshots and injected clipboard/browser only.
static class CardsFeedbackTests {
    static int checks;
    const string A="device-aaaaaaaaaaaaaaaaaaaaaaaa",B="device-bbbbbbbbbbbbbbbbbbbbbbbb";
    static void Check(bool value,string message){if(!value)throw new Exception("FAIL: "+message);checks++;}
    static object Call(object owner,string name,params object[] args){return owner.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).Invoke(owner,args);}
    static WirelessDevice Device(string id,string name){return new WirelessDevice {Id=id,Name=name,Kind="mouse",Provider="logitech",Percent=10,Fresh=true,Online=true,ConnectionState="connected",MeasurementState="current",ConfirmedAtUtc=DateTime.UtcNow.ToString("o"),PhysicalId=id};}
    static void Apply(TrayContext owner,WirelessSnapshot value){Call(owner,"ApplyWireless",value,false);}
    static string Decode(string url){return Uri.UnescapeDataString(url.Substring(url.IndexOf("&diagnostics=",StringComparison.Ordinal)+13));}
    static WirelessSnapshot HealthSnapshot(string entries,string errors="[]"){
        var snapshot=GenericBatteryWorker.Parse("{\"schema\":2,\"nativeMousePresent\":false,\"devices\":[],\"health\":["+entries+"],\"errors\":"+errors+"}");
        Check(String.IsNullOrEmpty(snapshot.Error),"health fixture passes the real worker parser");return snapshot;
    }
    static void CheckFooter(TrayContext owner,string text,bool failure){
        var layout=WirelessLayout.Build(owner,0,1000);
        float textRight=PopupPages.WirelessFooterWidth(layout);
        var surface=VisualDesign.FooterSurfaceRect(layout.Footer);
        foreach(bool dark in new[]{false,true})using(var actual=new Bitmap(layout.Size.Width,layout.Size.Height))using(var expected=new Bitmap(layout.Size.Width,layout.Size.Height))using(var opposite=new Bitmap(layout.Size.Width,layout.Size.Height)){
            using(var g=Graphics.FromImage(actual))CompactRenderer.PaintWireless(g,owner,layout,dark,new PanelFeedback());
            foreach(bool wrongStyle in new[]{false,true})using(var g=Graphics.FromImage(wrongStyle?opposite:expected)){
                g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;g.PixelOffsetMode=System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                typeof(CompactRenderer).GetMethod("DrawFooterCard",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{g,layout.Footer,dark,layout.ReaderNotice});
                typeof(CompactRenderer).GetMethod("DrawSync",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{g,text,layout.Footer,textRight,dark,wrongStyle?!failure:failure,new PanelFeedback()});
            }
            bool matches=true,styleDiffers=false;
            for(int y=(int)surface.Top+2;y<(int)surface.Bottom-2;y++)for(int x=6;x<(int)textRight;x++){
                if(actual.GetPixel(x,y)!=expected.GetPixel(x,y))matches=false;
                if(expected.GetPixel(x,y)!=opposite.GetPixel(x,y))styleDiffers=true;
            }
            Check(matches&&styleDiffers,"wireless footer shows the expected text using "+(failure?"failure":"normal")+" styling, dark="+dark);
        }
    }
    static void CheckHealthPresentation(TrayContext owner,int failures,int idle){
        string failed=failures>0?"部分读取暂不可用（"+failures+"项）":null;
        string sleeping=idle>0&&failures==0?idle+" 台设备暂未取得读数":null;
        Check(owner.GenericFailureCount==failures&&owner.GenericIdleCount==idle,"idle and failed readers are counted separately");
        Check(owner.GenericHealthSummary==failed&&owner.GenericIdleSummary==sleeping,"only failures populate reader health and take precedence over idle");
        var rows=PopupPages.Build(owner,new PopupNavigation {Page=PopupPage.Diagnostics,VisibleChoices=100},false);
        Check(rows.Exists(delegate(PopupControl row){return row.Text=="读取器健康"&&row.Value==(failed??sleeping);})==(failed!=null||sleeping!=null),"status page shows failure first, otherwise idle");
        Check(owner.DiagnosticSummary().Contains("\r\n读取健康："+(failed??sleeping??"正常")+"\r\n"),"diagnostic health text distinguishes idle from failure");
        Check(owner.BuildStatusJson().Contains("\"readerHealth\":\""+(failed??"")+"\",\"readerFailures\":"+failures+",\"readerIdle\":"+idle+","),"status JSON keeps reader health/failures separate from idle");
        string reason=failed!=null?"部分读取器暂不可用":sleeping!=null?"设备暂未取得读数":"尚未连接兼容设备";
        Check(WirelessLayout.EmptyReason(owner,false)==reason,"missing-device explanation distinguishes idle from failure");
        CheckFooter(owner,failed??sleeping??"暂无可读取电量的设备",failures>0);
    }
    static void ReaderHealthStates(TrayContext owner){
        owner.Headset=null;owner.Audio=null;
        var keyboard=Device(A,"ROG keyboard");keyboard.Kind="keyboard";keyboard.Provider="asus_keyboard";
        var unpaired=HealthSnapshot("{\"provider\":\"asus_omni\",\"state\":\"unavailable\",\"reason\":\"no_measurement\",\"deviceCount\":0}");
        unpaired.Devices=new[]{keyboard};Apply(owner,unpaired);
        Check(owner.GenericIdleCount==0&&owner.GenericFailureCount==0,"OMNI with only a keyboard must not count the unpaired mouse as idle");
        var mouse=Device(B,"ROG mouse");mouse.Provider="asus_omni";
        unpaired.Devices=new[]{keyboard,mouse};Apply(owner,unpaired);
        Check(owner.GenericIdleCount==1&&owner.GenericFailureCount==0,"an existing OMNI mouse card still counts as idle");
        unpaired.Health[0].Scope="device";unpaired.Health[0].PhysicalId=A;Apply(owner,unpaired);
        Check(owner.GenericIdleCount==0,"a card from the same reader on a different physical device does not count");
        const string idle="{\"provider\":\"vgn\",\"state\":\"unavailable\",\"reason\":\"no_measurement\",\"deviceCount\":1}";
        var snapshot=HealthSnapshot(idle);Check(snapshot.Health[0].Reason=="no_measurement","worker preserves the no_measurement reason");
        Apply(owner,snapshot);CheckHealthPresentation(owner,0,1);
        Apply(owner,HealthSnapshot(idle+","+idle));CheckHealthPresentation(owner,0,2);
        foreach(string entry in new[]{"{\"state\":\"unavailable\"}","{\"state\":\"unavailable\",\"reason\":\"no_matched_model\"}","{\"state\":\"error\",\"reason\":\"no_measurement\"}","{\"state\":\"timeout\",\"reason\":\"no_measurement\"}","{\"state\":\"backoff\",\"reason\":\"no_measurement\"}"}){
            Apply(owner,HealthSnapshot(entry));CheckHealthPresentation(owner,1,0);
        }
        Apply(owner,HealthSnapshot(idle+",{\"state\":\"timeout\"}"));CheckHealthPresentation(owner,1,1);
        Apply(owner,HealthSnapshot(idle,"[\"vgn: timeout\"]"));CheckHealthPresentation(owner,1,1);
        Apply(owner,HealthSnapshot("{\"state\":\"unavailable\"},{\"state\":\"error\"},{\"state\":\"timeout\"},{\"state\":\"backoff\"},"+idle));CheckHealthPresentation(owner,4,1);
        string longest=new string('a',48);Check(HealthSnapshot("{\"state\":\"unavailable\",\"reason\":\""+longest+"\"}").Health[0].Reason==longest,"reason tokens accept the bounded character set");
        foreach(string reason in new[]{"no_measurement\\n","no_measurement/path","NO_MEASUREMENT","休眠",new string('a',49)}){
            snapshot=HealthSnapshot("{\"state\":\"unavailable\",\"reason\":\""+reason+"\"}");
            Check(snapshot.Health[0].Reason==null,"invalid reason is rejected rather than normalized to idle");Apply(owner,snapshot);CheckHealthPresentation(owner,1,0);
        }
        Apply(owner,HealthSnapshot(idle));
        Apply(owner,GenericBatteryWorker.Parse("{\"schema\":2,\"error\":\"fixture failure\"}"));
        Check(owner.GenericIdleSummary==null&&owner.GenericIdleCount==0&&!String.IsNullOrEmpty(owner.GenericError),"worker failure clears stale idle state");
        CheckFooter(owner,"读取组件暂不可用",true);
        Apply(owner,HealthSnapshot("{\"state\":\"ok\"},{\"state\":\"unsupported\"},{\"state\":\"no_devices\"},{\"state\":\"deferred\",\"reason\":\"round_budget\"}"));CheckHealthPresentation(owner,0,0);
    }
    static void ReaderFooterActions(TrayContext owner){
        var original=DeviceSettings.Current;
        try {
            var failure=HealthSnapshot("{\"provider\":\"reader-a\",\"state\":\"error\",\"errorCode\":\"read_failed\"}");
            foreach(string layoutName in new[]{"ring","bar","grid"}){
                var pref=DeviceSettings.Current;
                pref.PanelLayout=layoutName;
                pref.ReducedMotion=true;
                DeviceSettings.UseTransientConfiguration(pref);
                Apply(owner,failure);
                using(var window=new Details(owner,delegate(string text){},delegate(string url){})){
                    var navigation=(PopupNavigation)typeof(Details).GetField("navigation",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(window);
                    var controls=(List<PopupControl>)Call(window,"ControlsForPage");
                    var banner=controls.Find(delegate(PopupControl control){return control.Text==owner.GenericHealthSummary;});
                    Check(banner!=null&&banner.Enabled&&!banner.Information,layoutName+" exposes the health text as an action");
                    var clicked=PopupPages.Hit(controls,new PointF(banner.Rect.X+2,banner.Rect.Y+2));
                    Call(window,"PerformAction",clicked);
                    Check(navigation.Page==PopupPage.Diagnostics,layoutName+" health text opens diagnostics on click");
                    Call(window,"Back");
                    controls=(List<PopupControl>)Call(window,"ControlsForPage");
                    banner=controls.Find(delegate(PopupControl control){return control.Text==owner.GenericHealthSummary;});
                    Call(window,"FocusControl",banner);
                    Call(window,"ProcessCmdKey",new System.Windows.Forms.Message(),System.Windows.Forms.Keys.Enter);
                    Check(navigation.Page==PopupPage.Diagnostics,layoutName+" keyboard action opens diagnostics");
                    Call(window,"Back");
                    controls=(List<PopupControl>)Call(window,"ControlsForPage");
                    Check(controls.Exists(delegate(PopupControl control){return control.Id==0&&control.Text=="立即同步全部设备";}),layoutName+" retains refresh action 0");
                }
            }
            using(var window=new Details(owner,delegate(string text){},delegate(string url){})){
                var controls=(List<PopupControl>)Call(window,"ControlsForPage");
                var dismiss=controls.Find(delegate(PopupControl control){return control.Text=="知道了";});
                Check(dismiss!=null&&dismiss.Enabled&&!dismiss.Information,"dismiss is an available action");
                Call(window,"PerformAction",dismiss);
                Check(owner.DiagnosticSummary().Contains("read_failed"),"dismiss preserves reader diagnostics");
                Apply(owner,failure);
                controls=(List<PopupControl>)Call(window,"ControlsForPage");
                Check(!controls.Exists(delegate(PopupControl control){return control.Text=="知道了";}),"the same failure group remains dismissed");
                var other=HealthSnapshot("{\"provider\":\"reader-b\",\"state\":\"error\",\"errorCode\":\"read_failed\"}");
                Apply(owner,other);
                controls=(List<PopupControl>)Call(window,"ControlsForPage");
                dismiss=controls.Find(delegate(PopupControl control){return control.Text=="知道了";});
                Check(dismiss!=null,"changed reader identity shows a same-count failure again");
                Call(window,"PerformAction",dismiss);
                Call(owner,"ApplyWireless",HealthSnapshot("{\"provider\":\"reader-b\",\"state\":\"ok\"}"),true);
                Apply(owner,other);
                controls=(List<PopupControl>)Call(window,"ControlsForPage");
                Check(controls.Exists(delegate(PopupControl control){return control.Text=="知道了";}),"a failure following recovery is visible");
            }
            Apply(owner,new WirelessSnapshot());
        }finally{DeviceSettings.UseTransientConfiguration(original);}
    }
    [STAThread] static int Main(string[] args){
        TrayContext owner=null;
        try {
            var settings=new AudioBindings {RememberReadings=false,LowBatteryNotifications=false,TrayDeviceId=B,MouseAlias="PRIVATE-ALIAS",HeadsetAlias="PRIVATE-HEADSET"};
            settings.DeviceAliases[A]="PRIVATE-CARD-ALIAS";DeviceSettings.UseTransientConfiguration(settings);
            owner=new TrayContext(false,delegate(bool battery){return new MouseSnapshot();},delegate(int s){throw new Exception("No DPI writes");});
            owner.NativeMousePresent=false;
            var a=Device(A,"Raw 中文 & # Mouse");var b=Device(B,"Old Mouse");b.Fresh=false;b.MeasurementState="cached";
            Apply(owner,new WirelessSnapshot {Devices=new[]{a,b}});
            var cache=(Dictionary<string,WirelessDevice>)typeof(TrayContext).GetField("wirelessCache",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(owner);
            var snapshot=GenericBatteryWorker.Parse("{\"schema\":2,\"devices\":[],\"suppressed\":[{\"name\":\"Old Mouse\",\"kind\":\"mouse\"}]}");
            snapshot.Devices=new[]{a};Apply(owner,snapshot);
            var pads=GenericBatteryWorker.Parse("{\"schema\":2,\"devices\":[{\"id\":\""+A+"\",\"name\":\"Xbox Wireless Controller\",\"kind\":\"gamepad\"},{\"id\":\""+B+"\",\"name\":\"Odd\",\"kind\":\"toaster\"}],\"suppressed\":[{\"name\":\"Pad\",\"kind\":\"gamepad\"}]}");
            Check(pads.Devices[0].Kind=="gamepad"&&pads.Devices[1].Kind=="device"&&pads.Suppressed[0].Kind=="gamepad","parsed snapshots keep gamepad kind and still reduce unknown kinds to device");
            Check(owner.WirelessDevices.Length==1&&cache.ContainsKey(B),"suppression does not delete cached readings");
            var tray=(TrayView)Call(owner,"WirelessTray","auto");Check(tray.Battery.Percent==10&&tray.Tooltip.Contains("PRIVATE-CARD-ALIAS"),"hidden pinned device falls back to surviving card");
            Check(!owner.CanSetWirelessDpi(B,new string('a',64)),"hidden card cannot target DPI");
            Check(!(bool)Call(owner,"FreshActivitySource",B,false),"hidden connection remains eligible for activity refresh");
            snapshot.Health=new[]{new ReaderHealth {PhysicalId=B,Provider="logitech",State="ok",DeviceCount=1}};
            var withoutMouse=typeof(TrayContext).GetMethod("HealthyPhysicalWithoutMouse",BindingFlags.Static|BindingFlags.NonPublic);
            Check(!(bool)withoutMouse.Invoke(null,new object[]{snapshot,B}),"first seen suppressed mouse is not marked permanently unsupported");
            Check(owner.DiagnosticSummary().Contains("已隐藏的重复连接")&&owner.DiagnosticSummary().Contains("Old Mouse"),"suppressed cards appear in diagnostics");
            Check(!owner.BuildStatusJson().Contains("Old Mouse")&&!owner.BuildStatusJson().Contains("suppressed"),"suppression stays out of status.json");
            var policy=new LowBatteryPolicy();int alerts=0;for(int i=0;i<2;i++)foreach(var d in owner.WirelessDevices)if(policy.Observe(d.Id,WirelessLayout.View(d),true))alerts++;
            Check(alerts==1&&!policy.IsLatched(B),"only visible current card alerts once");
            var rows=PopupPages.Build(owner,new PopupNavigation {Page=PopupPage.Diagnostics,VisibleChoices=100},false);
            int copy=rows.FindIndex(delegate(PopupControl row){return row.Id==250;});
            Check(copy>=0&&rows[copy+1].Text=="反馈问题","feedback form immediately follows copy diagnostics");
            var page=new PopupNavigation {Page=PopupPage.Diagnostics,Offset=100};rows=PopupPages.Build(owner,page,false);
            Check(rows.Exists(delegate(PopupControl row){return row.Id==258;}),"feedback is reachable on final diagnostics page");
            if(args.Length>0){var size=PopupPages.SizeFor(rows);using(var bitmap=new Bitmap(size.Width,size.Height))using(var graphics=Graphics.FromImage(bitmap)){PopupPages.Paint(graphics,owner,page,rows,false,-1,-1,-1);bitmap.Save(Path.Combine(Path.GetFullPath(args[0]),"feedback-page.png"));}}
            var report=owner.CreateFeedbackReport();string full=report.FullText;
            Check(full.Contains("Raw 中文 & # Mouse")&&full.Contains(AppRuntime.Version)&&full.Contains("Windows"),"raw name and versions are included");
            foreach(string secret in new[]{"PRIVATE-ALIAS","PRIVATE-CARD-ALIAS","PRIVATE-HEADSET",A,B,"physicalId","当前实例"})Check(!full.Contains(secret),"private field absent: "+secret);
            string encoded=FeedbackReport.EncodeUrl("中文 & #\nTitle","中文 & #\nBody");
            Check(encoded.StartsWith("https://github.com/wushuai0802/myfinder-device-battery/issues/new?template=device-request.yml&title="),"fixed issue template URL");
            Check(Decode(encoded)=="中文 & #\nBody"&&encoded.Contains("%26")&&encoded.Contains("%23")&&encoded.Contains("%0A"),"values are encoded without query/fragment injection");
            int opened=0;string copied=null;var order=new List<string>();
            report=owner.CreateFeedbackReport();Check(opened==0,"constructing report and page never launches browser");
            string notice=owner.OpenFeedback(delegate(string text){copied=text;order.Add("copy");},delegate(string url){opened++;order.Add("open");Check(url==report.IssueUrl,"browser gets bounded URL");throw new InvalidOperationException("browser unavailable");});
            Check(opened==1&&copied==report.FullText&&String.Join(",",order.ToArray())=="copy,open","full report copied before browser attempt");
            Check(notice.Contains("无法打开")&&notice.Contains("GitHub 账号")&&notice.Contains("检查"),"launch failure is a useful notice");
            notice=owner.OpenFeedback(delegate(string text){throw new InvalidOperationException("clipboard busy");},delegate(string url){opened++;});
            Check(opened==2&&notice.Contains("剪贴板不可用"),"clipboard failure is reported without crashing or blocking browser");
            var hostile=Device(A,@"Mouse C:\private\path SN=PRIVATE-SERIAL HID\VID_373B&PID_120A\PRIVATE-PNP physicalId=PRIVATE-ID pid=987654");
            owner.WirelessDevices=new[]{hostile};full=owner.CreateFeedbackReport().FullText;
            foreach(string secret in new[]{"private","PRIVATE-SERIAL","PRIVATE-PNP","PRIVATE-ID","987654",A})Check(full.IndexOf(secret,StringComparison.OrdinalIgnoreCase)<0,"hostile product sanitized: "+secret);
            owner.Audio=new AudioReading {SpeakerName="PRIVATE-ENDPOINT-NAME",SpeakerId="PRIVATE-ENDPOINT-ID",Devices=new[]{new AudioDeviceChoice {Name="PRIVATE-AUDIO-NAME",Id="PRIVATE-AUDIO-ID"}}};
            foreach(string product in new[]{"SN PRIVATE-SERIAL","serial_number=PRIVATE-SERIAL","序列号：PRIVATE-SERIAL","physicalId="+A,"processId=987654","endpointName=PRIVATE-ENDPOINT-NAME","PRIVATE-ENDPOINT-ID","PRIVATE-AUDIO-NAME","PRIVATE-AUDIO-ID","2026-11-02T10:11:12Z",@"\\host\share\PRIVATE-PATH",@"C:/PRIVATE-PATH/file"}){
                hostile.Name="Mouse "+product;full=owner.CreateFeedbackReport().FullText;
                Check(!full.Contains("PRIVATE-")&&!full.Contains(A)&&!full.Contains("987654")&&!full.Contains("2026-11-02"),"independent hostile name removed: "+product);
            }
            var metadata=GenericBatteryWorker.Parse("{\"schema\":2,\"devices\":[{\"id\":\""+A+"\",\"name\":\"Model\",\"vendorId\":14139,\"productId\":4618}]}");
            Check(metadata.Devices[0].VendorId==0x373B&&metadata.Devices[0].ProductId==0x120A,"optional model IDs are parsed");
            metadata=GenericBatteryWorker.Parse("{\"schema\":2,\"devices\":[{\"id\":\""+A+"\",\"vendorId\":-1,\"productId\":4618}]}");
            Check(!metadata.Devices[0].VendorId.HasValue&&!metadata.Devices[0].ProductId.HasValue,"invalid model identity is omitted as a pair");
            a.VendorId=0x373B;a.ProductId=0x120A;owner.WirelessDevices=new[]{a};Check(owner.CreateFeedbackReport().FullText.Contains("373B:120A"),"model IDs reach report");
            owner.NativeMousePresent=true;owner.Current=new BatteryReading {Product="RAW-NATIVE-ATK",Percent=90};owner.Headset=new HeadsetReading {Product="RAW-NATIVE-HEADSET",ReceiverPresent=true,Connected=true,ConnectionVerified=true,Percent=70,BatteryTimestampUtc=DateTime.UtcNow};
            full=owner.CreateFeedbackReport().FullText;Check(full.Contains("RAW-NATIVE-ATK")&&full.Contains("RAW-NATIVE-HEADSET")&&!full.Contains("PRIVATE-ALIAS")&&!full.Contains("PRIVATE-HEADSET"),"native cards use product names instead of aliases");
            owner.NativeMousePresent=false;owner.Headset=null;
            var devices=new List<WirelessDevice>();for(int i=0;i<64;i++)devices.Add(Device("device-"+i.ToString("x24"),"设备"+i+new string('中',70)));
            var unadapted=new List<UnadaptedDevice>();for(int i=0;i<16;i++)unadapted.Add(new UnadaptedDevice {VendorId=1,ProductId=i,Product="U"+i+new string('中',40)});
            Apply(owner,new WirelessSnapshot {Devices=devices.ToArray(),Unadapted=unadapted.ToArray(),Health=new[]{new ReaderHealth {Provider="health-marker",State="timeout",ErrorCode="timeout"},new ReaderHealth {Provider="healthy-marker",State="unsupported"}}});
            report=owner.CreateFeedbackReport();Check(report.IssueUrl.Length<=6000&&Decode(report.IssueUrl).Contains("已截断"),"worst-case devices still fit URL");
            Check(report.FullText.Contains("health-marker")&&!report.FullText.Contains("healthy-marker")&&report.FullText.Contains("U15"),"clipboard preserves full whitelisted report");
            Check(!Decode(report.IssueUrl).Contains("health-marker"),"health dropped before unadapted");
            owner.WirelessDevices=new[]{a};report=owner.CreateFeedbackReport();string reduced=Decode(report.IssueUrl);
            Check(report.IssueUrl.Length<=6000&&reduced.Contains("U0")&&!reduced.Contains("U15")&&!reduced.Contains("health-marker"),"unadapted removed from tail after health");
            var readers=new List<ReaderHealth>();for(int i=0;i<64;i++)readers.Add(new ReaderHealth {Provider=new string('中',48),State="timeout"});
            report=new FeedbackReport("1","10",new[]{a},new SuppressedDevice[0],new[]{unadapted[0]},readers,new string[0]);reduced=Decode(report.IssueUrl);
            Check(reduced.Contains("U0")&&!reduced.Contains("读取器健康")&&reduced.Contains("已截断"),"health-only overflow retains all unadapted items");
            Apply(owner,new WirelessSnapshot {Devices=new[]{a}});Check(owner.DiagnosticSummary().Contains("Old Mouse"),"omitted optional suppression preserves diagnostics");
            Apply(owner,new WirelessSnapshot {Devices=new[]{a},Suppressed=new SuppressedDevice[0]});Check(!owner.DiagnosticSummary().Contains("Old Mouse"),"explicit empty suppression clears diagnostics");
            ReaderHealthStates(owner);
            var a5v2Readers=new[]{
                new ReaderHealth {Provider="mchose",State="unavailable",ErrorCode="A5V2 no_stable_reply"},
                new ReaderHealth {Provider="mchose",State="ok",ErrorCode="A5V2 topology_unproven"},
                new ReaderHealth {Provider="mchose",State="no_devices",ErrorCode="A5V2 feature_unproven"},
                new ReaderHealth {Provider="mchose",State="ok",ErrorCode="A5V2 stable_reply_C_pending_hardware"},
                new ReaderHealth {Provider="mchose",State="ok",ErrorCode="arbitrary_secret_reason"},
                new ReaderHealth {Provider="other",State="ok",ErrorCode="A5V2 no_stable_reply"}
            };
            var a5v2Report=new FeedbackReport("1","10",new WirelessDevice[0],new SuppressedDevice[0],new UnadaptedDevice[0],a5v2Readers,new string[0]).FullText;
            Check(a5v2Report.Contains("mchose | unavailable | A5V2 no_stable_reply")&&a5v2Report.Contains("mchose | ok | A5V2 topology_unproven")&&a5v2Report.Contains("mchose | no_devices | A5V2 feature_unproven"),"FeedbackReport projects fixed A5V2 failure reasons across states");
            Check(!a5v2Report.Contains("stable_reply_C_pending_hardware")&&!a5v2Report.Contains("arbitrary_secret_reason")&&!a5v2Report.Contains("other | ok"),"FeedbackReport ignores non-whitelisted ok/no_devices reasons");
            ReaderFooterActions(owner);
            Console.WriteLine("PASS "+checks+" cards/feedback checks; no HID or browser");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}finally{if(owner!=null)owner.ExitThread();}
    }
}
