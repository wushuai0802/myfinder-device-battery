using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using AtkBattery;
using AtkBatteryTray;

// In-memory snapshots and fake native readers only; no HID or settings writes.
static class UnadaptedDiagnosticTests {
    static int checks;
    static void Check(bool value,string message){if(!value)throw new Exception("FAIL: "+message);checks++;}
    static void Apply(TrayContext owner,string json){
        var snapshot=GenericBatteryWorker.Parse(json);
        Check(snapshot.Error==null,"diagnostic snapshot parses");
        typeof(TrayContext).GetMethod("ApplyWireless",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(owner,new object[]{snapshot,false});
    }
    [STAThread] static int Main(string[] args){
        TrayContext owner=null;
        try {
            DeviceSettings.UseTransientConfiguration(new AudioBindings {RememberReadings=false});
            owner=new TrayContext(false,delegate(bool battery){return new MouseSnapshot();},delegate(int stage){throw new Exception("Unexpected DPI write");});
            const string old="{\"schema\":2,\"complete\":true,\"devices\":[]}";
            Check(GenericBatteryWorker.Parse(old).Error==null,"old snapshot without unadapted is accepted");
            Check(GenericBatteryWorker.Parse("{\"schema\":1,\"devices\":[]}").Error==null,"schema 1 still parses");
            const string frame="{\"schema\":2,\"complete\":true,\"devices\":[],\"unadapted\":[{\"vendorId\":14139,\"productId\":4618,\"product\":\"ATK A9 Air\",\"collections\":[{\"usagePage\":65282,\"usage\":2},{\"usagePage\":65284,\"usage\":2}]}]}";
            Apply(owner,frame);
            string summary=owner.DiagnosticSummary();
            Check(summary.Contains("未取得电量的设备（可能未适配，也可能正在休眠）"),"summary explains missing battery without claiming incompatibility");
            Check(summary.Contains("373B:120A \"ATK A9 Air\" FF02:0002 FF04:0002"),"fixed model and collection IDs survive sanitizing");
            string status=owner.BuildStatusJson();
            Check(!status.Contains("unadapted")&&!status.Contains("373B:120A")&&!status.Contains("ATK A9 Air"),"status.json excludes the diagnostic list");
            Apply(owner,old);
            Check(owner.DiagnosticSummary().Contains("373B:120A"),"omitted optional field preserves the previous list");
            string path="C:\\private\\mouse",serial="SN=PRIVATE-SERIAL",pnp=@"HID\VID_373B&PID_120A\PRIVATE-PNP";
            string hostile=frame.Replace("ATK A9 Air","ATK "+(path+" "+serial+" "+pnp).Replace("\\","\\\\"));
            Apply(owner,hostile);
            summary=owner.DiagnosticSummary();
            Check(summary.Contains("373B:120A"),"trusted model IDs remain in the hostile-name fixture");
            Check(!summary.Contains("private")&&!summary.Contains("PRIVATE-SERIAL")&&!summary.Contains("PRIVATE-PNP"),"product paths and labelled serials are removed");
            Check(SafeDiagnostic.Clean(@"C:\private\mouse AA:BB:CC:DD:EE:FF")=="[path] [device]","existing path and device-ID sanitizer is unchanged");
            Apply(owner,frame.Replace("14139","-1"));
            Check(!owner.DiagnosticSummary().Contains("373B:120A"),"out-of-range model IDs are rejected");
            Apply(owner,"{\"schema\":2,\"devices\":[],\"unadapted\":[]}");
            Check(!owner.DiagnosticSummary().Contains("373B:120A"),"explicit empty list clears previous diagnostics");
            var rows=PopupPages.Build(owner,new PopupNavigation {Page=PopupPage.Diagnostics,VisibleChoices=100},false);
            Check(rows.Exists(delegate(PopupControl row){return row.Information&&(row.Text+row.Value).Contains("AGPL-3.0-only");}),"diagnostics page names the license");
            Check(rows.Exists(delegate(PopupControl row){return row.Information&&(row.Text+row.Value+row.Detail).Contains("无担保");}),"diagnostics page displays no warranty");
            Check(rows.Exists(delegate(PopupControl row){return row.Information&&(row.Text+row.Value+row.Detail).Replace("\n","").Contains("https://github.com/wushuai0802/myfinder-device-battery");}),"diagnostics page exposes the complete source address without a network action");
            var navigation=new PopupNavigation {Page=PopupPage.Diagnostics,Offset=7};
            rows=PopupPages.Build(owner,navigation,false);
            var source=rows.Find(delegate(PopupControl row){return row.Text=="源码";});
            Check(source!=null&&rows.Exists(delegate(PopupControl row){return row.Id==250;}),"license and copy action are reachable through existing pagination");
            var size=PopupPages.SizeFor(rows);
            using(var bitmap=new Bitmap(size.Width,size.Height))using(var graphics=Graphics.FromImage(bitmap))using(var font=new Font("Segoe UI",11,FontStyle.Regular,GraphicsUnit.Pixel)){
                foreach(string line in source.Detail.Split('\n'))Check(graphics.MeasureString(line,font).Width<source.Rect.Width-33,"source address line fits without truncation");
                PopupPages.Paint(graphics,owner,navigation,rows,false,-1,-1,-1);
                if(args.Length>0)bitmap.Save(Path.Combine(Path.GetFullPath(args[0]),"unadapted-license-page.png"));
            }
            Console.WriteLine("PASS "+checks+" unadapted diagnostics/license checks; fake devices only");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{if(owner!=null)owner.ExitThread();}
    }
}
