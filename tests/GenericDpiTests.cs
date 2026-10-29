using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

// The portable "python.exe" below is a copy of this fixture. It speaks JSON
// only, never loads the engine, imports a provider, or opens a HID endpoint.
static class GenericDpiTests {
    const string IdA="device-000000000000000000000001",IdB="device-000000000000000000000002";
    static readonly string TokenA=new string('a',64),TokenB=new string('b',64);
    const BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
    static int checks;
    static void Check(bool value,string name){if(!value)throw new Exception("FAIL "+name);checks++;}
    static void Pump(Func<bool> done){var deadline=DateTime.UtcNow.AddSeconds(7);while(!done()&&DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(5);}Check(done(),"fake operation finishes within its deadline");}
    static string Q(string value){return "\""+value+"\"";}
    static string Device(string id,string token,int current=400,bool canSet=true,string provider="logitech",string kind="mouse",string stages="400,800,1600",string stamp=null){
        stamp=stamp??DateTime.UtcNow.ToString("o");
        return "{\"id\":"+Q(id)+",\"name\":\"Fixture GPW\",\"kind\":"+Q(kind)+",\"provider\":"+Q(provider)+",\"percent\":80,\"fresh\":true,\"online\":true,\"sourceQuality\":\"device\",\"connectionState\":\"connected\",\"confirmedAtUtc\":"+Q(stamp)+",\"dpi\":{\"supported\":true,\"canSet\":"+canSet.ToString().ToLower()+",\"current\":"+current+",\"stages\":["+stages+"],\"stageSource\":\"quick\",\"checkedAtUtc\":"+Q(stamp)+",\"targetToken\":"+Q(token)+",\"error\":null}}";
    }
    static string Frame(long request=1,int generation=1,string devices=null,string result=null){return "{\"schema\":2,\"requestId\":"+request+",\"generation\":"+generation+",\"complete\":true,\"nativeMousePresent\":true,\"providerCount\":26,\"devices\":["+(devices??Device(IdA,TokenA))+"],\"health\":[],\"errors\":[]"+(result==null?"":",\"dpiResult\":"+result)+"}";}
    static object Field(object value,string name){return value.GetType().GetField(name,Private).GetValue(value);}
    static void SetField(object value,string name,object field){value.GetType().GetField(name,Private).SetValue(value,field);}
    static object Call(object value,string name,params object[] args){return value.GetType().GetMethod(name,Private).Invoke(value,args);}
    static string MakeWorker(string root,string mode){
        string folder=Path.Combine(root,mode+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(folder,"runtime"));
        File.Copy(Assembly.GetExecutingAssembly().Location,Path.Combine(folder,"runtime","python.exe"));File.WriteAllText(Path.Combine(folder,"server.py"),"# fixture only: no hardware");return folder;
    }
    static int WriteCount(string root){string path=Path.Combine(root,"writes.log");return File.Exists(path)?File.ReadAllLines(path).Length:0;}
    static void Release(string root,string gate){File.WriteAllText(Path.Combine(root,"release-"+gate),"go");}
    static void ParserAndCache(string root){
        var device=GenericBatteryWorker.Parse(Frame()).Devices[0];
        Check(device.Dpi.Supported&&device.Dpi.CanSet&&device.Dpi.Current==400&&device.Dpi.TargetToken==TokenA&&device.Dpi.Stages.Length==3,"valid live DPI capability is parsed independently of native ATK state");
        string original=device.Key;device.Dpi.Current=800;Check(device.Key!=original,"DPI changes participate in the wireless presentation key");
        foreach(string body in new[]{Device(IdA,TokenA,400,true,"logitech","mouse","400,400,800"),Device(IdA,TokenA,400,true,"logitech","mouse","0,800"),Device(IdA,TokenA,400,true,"logitech","mouse","50,100,200,400,800,1600,3200,6400,12800"),Device(IdA,"bad"),Device(IdA,TokenA,400,true,"other"),Device(IdA,TokenA,400,true,"logitech","keyboard"),Device(IdA,TokenA,1),Device(IdA,TokenA,400,true,"logitech","mouse","400,800",DateTime.UtcNow.AddMinutes(-2).ToString("o")),Device(IdA,TokenA,400,true,"logitech","mouse","400,800",DateTime.UtcNow.AddMinutes(1).ToString("o"))}){
            var invalid=GenericBatteryWorker.Parse(Frame(1,1,body)).Devices[0];Check(!invalid.Dpi.CanSet&&invalid.Dpi.TargetToken==null,"invalid, stale or unsupported DPI capability loses its write lease");
        }
        var offline=GenericBatteryWorker.Parse(Frame().Replace("\"online\":true","\"online\":false")).Devices[0];Check(!offline.Dpi.CanSet&&offline.Dpi.TargetToken==null,"offline snapshots cannot preserve DPI write capability");
        var failed=GenericBatteryWorker.Parse(Frame().Replace("\"error\":null","\"error\":\"read_failed\"")).Devices[0];Check(!failed.Dpi.CanSet&&failed.Dpi.TargetToken==null,"failed DPI reads cannot preserve their token");
        var live=GenericBatteryWorker.Parse(Frame()).Devices[0];string cache=ReadingCache.EncodeWireless(new[]{live});
        Check(!cache.Contains(TokenA)&&cache.Contains("\"canSet\":false"),"serializing historical readings never exports an actionable DPI token or capability");
        string path=Path.Combine(root,"cache.json");File.WriteAllText(path,cache);var restored=ReadingCache.LoadWireless(path);
        Check(restored.Length==1&&restored[0].Dpi!=null&&!restored[0].Dpi.CanSet&&restored[0].Dpi.TargetToken==null&&!restored[0].Online,"reading cache reload remains read-only even with a recent DPI timestamp");
        Check(live.BatteryCacheCopy().Dpi==null&&live.Dpi.CanSet,"in-memory battery history also strips DPI without mutating the live model");
    }
    static void Transport(string root){
        string folder=MakeWorker(root,"normal");
        using(var worker=new GenericBatteryWorker(folder,false,2000)){
            Check(worker.SetDpi(IdA,TokenA,800,400).DpiResult.Success==false&&Field(worker,"process")==null,"writing cannot start a worker or invent a lease before a poll");
            var initial=worker.Poll();Check(initial.Error==null&&initial.Devices[0].Dpi.CanSet,"live poll grants a lease for the current worker lifetime");
            Check(!worker.SetDpi(IdA,TokenA,900,400).DpiResult.Success&&WriteCount(folder)==0,"unsupported values never reach the fake child");
            Check(!worker.SetDpi(IdA,TokenB,800,400).DpiResult.Success&&WriteCount(folder)==0,"mismatched target tokens cannot reach the child");
            Check(!worker.SetDpi(IdA,TokenA,800,800).DpiResult.Success&&WriteCount(folder)==0,"mismatched expected current DPI is rejected before dispatch");
            var changed=worker.SetDpi(IdA,TokenA,800,400);Check(changed.DpiResult.Success&&changed.DpiResult.ConfirmedDpi==800&&changed.Devices[0].Dpi.Current==800&&WriteCount(folder)==1,"one typed set_dpi command succeeds only with matching confirmed full readback");
            var restore=worker.SetDpi(IdA,TokenA,400,800);Check(restore.DpiResult.Success&&restore.Devices[0].Dpi.Current==400&&WriteCount(folder)==2,"a subsequent user command uses the newly confirmed current value");
            worker.Reset();Check(!worker.SetDpi(IdA,TokenA,800,400).DpiResult.Success&&Field(worker,"process")==null,"reset drops leases and forbids writes before another poll");
        }
        foreach(string mode in new[]{"bad-token","bad-provider","bad-kind","bad-cap","bad-stage"}){
            folder=MakeWorker(root,mode);using(var worker=new GenericBatteryWorker(folder,false,2000)){
                worker.Poll();var result=worker.SetDpi(IdA,TokenA,800,400);
                Check(!result.DpiResult.Success&&result.DpiResult.OutcomeUnknown&&WriteCount(folder)==1,mode+" success claim cannot bypass snapshot target, capability or stage validation");
                Check(!worker.SetDpi(IdA,TokenA,800,400).DpiResult.Success&&WriteCount(folder)==1,mode+" unconfirmed result never retries its write");
            }
        }
        folder=MakeWorker(root,"timeout");using(var worker=new GenericBatteryWorker(folder,false,600)){
            worker.Poll();var result=worker.SetDpi(IdA,TokenA,800,400);Check(result.DpiResult.OutcomeUnknown&&!result.DpiResult.Success&&WriteCount(folder)==1,"timed-out write stays unknown and is issued only once");
            Check(!worker.SetDpi(IdA,TokenA,800,400).DpiResult.Success&&WriteCount(folder)==1,"timeout requires a fresh poll before any later user write");
        }
    }
    static void QueryWaitDoesNotCrossLifetime(string root){
        string folder=MakeWorker(root,"normal");using(var worker=new GenericBatteryWorker(folder,false,2000)){
            int first=worker.Poll().Generation;object gate=Field(worker,"query");WirelessSnapshot result=null;
            var caller=new Thread(delegate(){result=worker.SetDpi(IdA,TokenA,800,400);});caller.IsBackground=true;
            Monitor.Enter(gate);
            try{
                caller.Start();Pump(delegate{return (caller.ThreadState&System.Threading.ThreadState.WaitSleepJoin)!=0;});
                worker.Reset();Check(worker.Poll().Generation!=first,"fixture restarts its child with the same stable target token and current DPI");
            }finally{Monitor.Exit(gate);}
            Pump(delegate{return caller.Join(0);});Check(result!=null&&!result.DpiResult.Success&&WriteCount(folder)==0,"a queued worker call cannot use a new lifetime's matching-looking lease");
        }
    }
    static void UnconfirmedTerminationQuarantines(string root){
        string folder=MakeWorker(root,"normal");bool confirm=false;
        using(var worker=new GenericBatteryWorker(folder,false,2000,delegate(Process child){if(!confirm)return false;try{if(!child.HasExited)child.Kill();return child.WaitForExit(1000);}catch(InvalidOperationException){return true;}})){
            worker.Poll();var old=(Process)Field(worker,"process");worker.Reset();
            Check(Object.ReferenceEquals(Field(worker,"process"),old)&&(bool)Field(worker,"quarantined"),"unconfirmed termination retains and quarantines the old process reference");
            Check(worker.Poll().Error!=null&&Object.ReferenceEquals(Field(worker,"process"),old),"quarantine prevents creating a replacement worker while termination is unconfirmed");
            Check(!worker.SetDpi(IdA,TokenA,800,400).DpiResult.Success&&WriteCount(folder)==0,"quarantine forbids DPI writes even with a matching old token");
            confirm=true;Check(worker.Poll().Error==null&&!Object.ReferenceEquals(Field(worker,"process"),old),"a confirmed exit permits a fresh read and a new worker lifetime");
        }
    }
    static DpiConfig NativeDpi(){var dpi=new DpiConfig {DeviceKey=MouseBattery.DeviceKey("fixture-native"),ActiveStage=0,TimestampUtc=DateTime.UtcNow};dpi.Stages.Add(new DpiStage {XDpi=1200,YDpi=1200});return dpi;}
    static TrayContext Context(GenericBatteryWorker worker,Action nativeWrite){return new TrayContext(false,delegate(bool battery){return new MouseSnapshot {Dpi=NativeDpi(),Battery=battery?new BatteryReading {Percent=60,TimestampUtc=DateTime.UtcNow}:null};},delegate(int stage){nativeWrite();return NativeDpi();},null,null,false,null,null,null,delegate{return false;},null,worker);}
    static void Controller(string root){
        string folder=MakeWorker(root,"wait-write");using(var worker=new GenericBatteryWorker(folder,false,2000)){
            TrayContext context=null;int nativeWrites=0;
            try{
                context=Context(worker,delegate{nativeWrites++;});Pump(delegate{return !context.GenericScanning&&context.WirelessDevices.Length==2&&context.Dpi!=null;});var native=context.Dpi;
                context.SelectWirelessDpi(IdA,TokenA,800);Pump(delegate{return File.Exists(Path.Combine(folder,"write-entered"));});
                Check(context.DesiredWirelessDpi(IdA,TokenA)==800&&!context.CanSetWirelessDpi(IdA,TokenA)&&context.WirelessDevices[0].Dpi.Current==400,"pending feedback preserves the old confirmed DPI and disables repeated writes to the same device");
                context.SelectWirelessDpi(IdA,TokenA,1600);context.SelectWirelessDpi(IdB,TokenB,1600);
                Check(context.DesiredWirelessDpi(IdA,TokenA)==800&&context.DesiredWirelessDpi(IdB,TokenB)==1600,"another device has its own immutable pending target without rewriting the active action");
                Release(folder,"write");Pump(delegate{return !context.GenericScanning&&!context.DesiredWirelessDpi(IdB,TokenB).HasValue;});
                Check(WriteCount(folder)==2&&context.WirelessDevices[0].Dpi.Current==800&&context.WirelessDevices[1].Dpi.Current==1600,"two independent generic devices serialize their own value and target through one worker");
                Check(nativeWrites==0&&Object.ReferenceEquals(context.Dpi,native)&&context.Dpi.Stages[0].XDpi==1200,"generic DPI never calls the native ATK selector or replaces owner.Dpi");
            }finally{Release(folder,"write");if(context!=null)context.ExitThread();}
        }
        folder=MakeWorker(root,"unknown");using(var worker=new GenericBatteryWorker(folder,false,2000)){
            TrayContext context=null;
            try{
                context=Context(worker,delegate{throw new Exception("Native DPI writer must not run");});Pump(delegate{return !context.GenericScanning&&context.WirelessDevices.Length==2;});
                context.SelectWirelessDpi(IdA,TokenA,800);Pump(delegate{return File.Exists(Path.Combine(folder,"write-entered"));});context.SelectWirelessDpi(IdB,TokenB,1600);Release(folder,"write");
                Pump(delegate{return !context.GenericScanning&&context.CanSetWirelessDpi(IdA,TokenA);});
                Check(WriteCount(folder)==1&&context.WirelessDevices[0].Dpi.Current==800&&context.WirelessDevices[1].Dpi.Current==400&&!context.DesiredWirelessDpi(IdB,TokenB).HasValue,"unknown result clears every queued write and performs only a read reconciliation");
                var last=(WirelessDpiResult)Field(context,"lastWirelessDpiResult");Check(last!=null&&last.OutcomeUnknown&&!last.Success,"read reconciliation does not rewrite the original unknown action as success");
            }finally{Release(folder,"write");if(context!=null)context.ExitThread();}
        }
        foreach(bool restart in new[]{false,true}){
            folder=MakeWorker(root,"normal");using(var worker=new GenericBatteryWorker(folder,false,2000)){
                TrayContext context=null;
                try{
                    context=Context(worker,delegate{throw new Exception("Native DPI writer must not run");});Pump(delegate{return !context.GenericScanning&&context.WirelessDevices.Length==2;});
                    SetField(context,"genericBusy",1);context.SelectWirelessDpi(IdA,TokenA,800);Check(context.DesiredWirelessDpi(IdA,TokenA)==800,"fixture queues a click behind an active generic operation");
                    WirelessSnapshot next;if(restart){worker.Reset();next=worker.Poll();}else next=GenericBatteryWorker.Parse(Frame(9,context.WirelessDevices.Length>0?(int)Field(context,"wirelessDpiGeneration"):1,Device(IdA,TokenB)+","+Device(IdB,TokenB)));
                    Call(context,"ApplyWireless",next,true);SetField(context,"genericBusy",0);Call(context,"ContinueWirelessQueue");
                    Check(WriteCount(folder)==0&&!context.DesiredWirelessDpi(IdA,TokenA).HasValue,restart?"a new snapshot generation cancels old pending actions even when token/current are unchanged":"a changed physical token cancels a queued action before dispatch");
                }finally{if(context!=null)context.ExitThread();}
            }
        }
    }
    static long Number(string json,string field){return Int64.Parse(Regex.Match(json,"\""+field+"\":([0-9]+)").Groups[1].Value);}
    static string Text(string json,string field){return Regex.Match(json,"\""+field+"\":\"([^\"]*)\"").Groups[1].Value;}
    static void Gate(string root,string gate){File.WriteAllText(Path.Combine(root,gate+"-entered"),"ready");var until=DateTime.UtcNow.AddSeconds(6);while(!File.Exists(Path.Combine(root,"release-"+gate))&&DateTime.UtcNow<until)Thread.Sleep(5);}
    static void FakeWorker(){
        string folder=Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).FullName;string mode=Path.GetFileName(folder);int a=400,b=400;
        using(var input=new StreamReader(Console.OpenStandardInput(),Encoding.UTF8))using(var output=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false)) {AutoFlush=true}){
            string line;while((line=input.ReadLine())!=null){
                long request=Number(line,"requestId");int generation=(int)Number(line,"generation");string command=Text(line,"command");string result=null,token=TokenA,provider="logitech",kind="mouse",stages="400,800,1600";bool canSet=true;
                if(command=="set_dpi"){
                    File.AppendAllText(Path.Combine(folder,"writes.log"),line+Environment.NewLine);
                    if(mode.StartsWith("timeout")){Thread.Sleep(30000);return;}
                    if(mode.StartsWith("wait-write")||mode.StartsWith("unknown"))Gate(folder,"write");
                    int dpi=(int)Number(line,"dpi");string id=Text(line,"deviceId"),submitted=Text(line,"targetToken");if(id==IdA)a=dpi;else b=dpi;
                    bool unknown=mode.StartsWith("unknown");result="{\"deviceId\":"+Q(id)+",\"targetToken\":"+Q(submitted)+",\"requestedDpi\":"+dpi+",\"confirmedDpi\":"+dpi+",\"success\":"+(!unknown).ToString().ToLower()+",\"outcomeUnknown\":"+unknown.ToString().ToLower()+",\"error\":"+(unknown?"\"outcome_unknown\"":"null")+",\"checkedAtUtc\":"+Q(DateTime.UtcNow.ToString("o"))+"}";
                    if(mode.StartsWith("bad-token"))token=TokenB;if(mode.StartsWith("bad-provider"))provider="other";if(mode.StartsWith("bad-kind"))kind="keyboard";if(mode.StartsWith("bad-cap")||unknown)canSet=false;if(mode.StartsWith("bad-stage"))stages="400,1600";
                }
                string devices=Device(IdA,token,a,canSet,provider,kind,stages)+","+Device(IdB,TokenB,b);
                output.WriteLine(Frame(request+100,generation,devices,result));output.WriteLine(Frame(request,generation,devices,result));
            }
        }
    }
    [STAThread] static int Main(string[] args){
        if(args.Length>0&&args[0]=="-I"){FakeWorker();return 0;}
        var current=typeof(DeviceSettings).GetField("current",BindingFlags.NonPublic|BindingFlags.Static);object previous=current.GetValue(null);
        try{
            var settings=new AudioBindings {RememberReadings=false};DeviceSettings.UseTransientConfiguration(settings);
            string root=Path.Combine(Path.GetFullPath(args.Length>0?args[0]:"tmp"),"generic-dpi-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            ParserAndCache(root);Transport(root);QueryWaitDoesNotCrossLifetime(root);UnconfirmedTerminationQuarantines(root);Controller(root);
            Console.WriteLine("PASS "+checks+" generic DPI parser/IPC/lifetime/queue regressions; all hardware and writers are fake");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{current.SetValue(null,previous);}
    }
}
