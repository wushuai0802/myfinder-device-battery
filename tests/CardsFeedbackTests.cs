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
            Check(copy>=0&&rows[copy+1].Text=="反馈到 GitHub","feedback immediately follows copy diagnostics");
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
            Console.WriteLine("PASS "+checks+" cards/feedback checks; no HID or browser");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}finally{if(owner!=null)owner.ExitThread();}
    }
}
