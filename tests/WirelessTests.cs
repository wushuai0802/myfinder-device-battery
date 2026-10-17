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
            foreach(float scale in new[]{1f,1.25f,1.5f,2f})foreach(bool dark in new[]{false,true}) {
                var size=UiState.ScaleSize(layout.Size,scale);using(var bitmap=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppPArgb))using(var graphics=Graphics.FromImage(bitmap)) {
                    graphics.Clear(Color.Transparent);graphics.ScaleTransform(scale,scale);CompactRenderer.PaintWireless(graphics,owner,layout,dark,new PanelFeedback());
                    Check(bitmap.GetPixel((int)(130*scale),(int)(64*scale)).A==0,"wireless cards retain their transparent gap at "+scale);
                    bitmap.Save(Path.Combine(output,"three-cards-"+(dark?"dark-":"light-")+scale.ToString(System.Globalization.CultureInfo.InvariantCulture)+".png"));
                }
            }
            var many=new List<WirelessDevice>();for(int i=1;i<=9;i++)many.Add(Device(i.ToString(),"keyboard",60));owner.WirelessDevices=many.ToArray();layout=WirelessLayout.Build(owner,0,260);
            Check(layout.Paged&&layout.Size.Height<=260,"many devices fit inside a bounded popup");
            var next=WirelessLayout.Build(owner,layout.End,260);Check(next.Start==layout.End&&next.Cards.Count>0,"the next page exposes additional devices");
            owner.WirelessDevices=new WirelessDevice[0];layout=WirelessLayout.Build(owner,0,260);Check(layout.All.Count==1&&layout.All[0].Id=="empty"&&layout.All[0].Name=="无线设备","empty discovery is honest and brand-neutral");
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
        finally{if(owner!=null)owner.ExitThread();current.SetValue(null,previous);}
    }
}
