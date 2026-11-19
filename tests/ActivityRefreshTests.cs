using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

// All snapshots are fabricated. The child "python.exe" is this executable's
// JSON fixture, not Python/the engine; no listener or HID device is opened.
static class ActivityRefreshTests {
    const string PhysA="device-000000000000000000000001",PhysB="device-000000000000000000000002",PhysNative="device-000000000000000000000003";
    const string IdA="device-111111111111111111111111",IdB="device-222222222222222222222222";
    static readonly string Token=new string('a',64);
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value,string label){if(!value)throw new Exception("FAIL "+label);checks++;Console.WriteLine("PASS "+label);}
    static void Pump(Func<bool> done){var end=DateTime.UtcNow.AddSeconds(7);while(!done()&&DateTime.UtcNow<end){Application.DoEvents();Thread.Sleep(5);}Check(done(),"fake operation completes within its deadline");}
    static object Field(object value,string name){return value.GetType().GetField(name,Private).GetValue(value);}
    static void Set(object value,string name,object field){value.GetType().GetField(name,Private).SetValue(value,field);}
    static object Call(object value,string name,params object[] args){return value.GetType().GetMethod(name,Private).Invoke(value,args);}
    static MouseActivitySource Source(string id,bool native=false){return new MouseActivitySource(id,native,native?(ushort)0x373B:(ushort)0x046D,native?(ushort)0x1278:(ushort)0xC547);}
    static void Policy(){
        var now=DateTime.UtcNow;var policy=new ActivityRefreshPolicy();
        for(int i=0;i<100;i++)policy.Record(PhysA,false,true,now);
        Check(policy.Reads==0&&policy.FreshSkipped==100&&policy.Pending==0,"fresh activity records memory counters without scheduling reads");
        policy.Record(PhysA,false,false,now);for(int i=0;i<100;i++)policy.Record(PhysA,false,false,now);
        Check(policy.Pending==1&&policy.Coalesced==100,"a packet burst coalesces into one source request");
        Check(policy.Take(now,delegate{return false;},delegate{return false;})==null&&policy.Deferred==1&&!policy.NextDue(delegate{return false;}).HasValue,"busy work retains the request without a spinning timer");
        var first=policy.Take(now,delegate{return false;},delegate{return true;});Check(first!=null&&first.PhysicalId==PhysA&&!first.Retry,"the deferred source resumes when its own channel becomes free");
        policy.Complete(first,false,false,now);Check(policy.Take(now.AddSeconds(1),delegate{return false;},delegate{return true;})==null,"wake retry waits a short bounded delay");
        var retry=policy.Take(now.AddSeconds(1.5),delegate{return false;},delegate{return true;});Check(retry!=null&&retry.Retry&&policy.Retries==1,"one delayed wake retry is allowed");
        policy.Complete(retry,false,false,now.AddSeconds(1.5));
        Check(policy.Pending==0&&!policy.NextDue(delegate{return true;}).HasValue,"exhausted retry stops its timer rather than polling indefinitely");
        for(int i=0;i<100;i++)policy.Record(PhysA,false,false,now.AddSeconds(2));Check(policy.Pending==0&&policy.CooldownSkipped==100,"continued activity cannot bypass failed-source backoff");
        policy.Record(PhysB,false,false,now.AddSeconds(2));var second=policy.Take(now.AddSeconds(2),delegate{return false;},delegate{return true;});
        Check(second!=null&&second.PhysicalId==PhysB,"one source's cooldown never suppresses another receiver");
        policy.Complete(second,false,true,now.AddSeconds(2));policy.Record(PhysB,false,false,now.AddDays(1));Check(policy.Pending==0&&policy.UnsupportedSkipped==1,"healthy unsupported sources stay suppressed until topology changes");
        policy.TopologyChanged();policy.ObserveMouse(PhysA);policy.Record(PhysA,false,false,now);first=policy.Take(now,delegate{return false;},delegate{return true;});policy.Complete(first,false,true,now);
        Check(policy.Pending==1,"a receiver previously seen with a mouse is not made unsupported when a sleeping slot disappears");
        policy.TopologyChanged();Check(policy.Pending==0,"topology invalidates old pending work and negative knowledge");
        policy.Record(PhysB,false,false,now);first=policy.Take(now,delegate{return true;},delegate{return true;});Check(first==null&&policy.Pending==0,"a timer or an existing read can satisfy queued activity without duplicate I/O");
        policy.Dispose();long events=policy.Events;policy.Record(PhysA,false,false,now);Check(policy.Events==events&&!policy.NextDue(delegate{return true;}).HasValue,"disposed policy ignores late callbacks and retry work");
    }
    static string Q(string value){return "\""+value+"\"";}
    static string Device(string id,string physical,bool fresh,string kind="mouse",int dpi=400){
        string stamp=DateTime.UtcNow.AddSeconds(fresh?0:-180).ToString("o");
        return "{\"id\":"+Q(id)+",\"physicalId\":"+Q(physical)+",\"kind\":"+Q(kind)+",\"name\":\"Fixture\",\"provider\":\"logitech\",\"percent\":80,\"online\":true,\"fresh\":"+fresh.ToString().ToLower()+",\"connectionState\":\"connected\",\"sourceQuality\":\"device\",\"confirmedAtUtc\":"+Q(stamp)+",\"dpi\":{\"supported\":true,\"canSet\":true,\"current\":"+dpi+",\"stages\":[400,800,1600],\"stageSource\":\"quick\",\"checkedAtUtc\":"+Q(DateTime.UtcNow.ToString("o"))+",\"targetToken\":"+Q(Token)+"}}";
    }
    static string Frame(long request,int generation,string devices,string health="",string result=null){return "{\"schema\":2,\"requestId\":"+request+",\"generation\":"+generation+",\"complete\":true,\"nativeMousePresent\":true,\"providerCount\":26,\"devices\":["+devices+"],\"health\":["+health+"],\"errors\":[]"+(result==null?"":",\"dpiResult\":"+result)+"}";}
    static string Health(string physical,string state){return "{\"physicalId\":"+Q(physical)+",\"provider\":\"logitech\",\"state\":"+Q(state)+",\"scope\":\"device\",\"deviceCount\":0}";}
    static string MakeWorker(string root,string mode){string path=Path.Combine(root,mode+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(path,"runtime"));File.Copy(Assembly.GetExecutingAssembly().Location,Path.Combine(path,"runtime","python.exe"));File.WriteAllText(Path.Combine(path,"server.py"),"# fake JSON reader; no HID");return path;}
    static string[] Requests(string root){string path=Path.Combine(root,"requests.log");return File.Exists(path)?File.ReadAllLines(path):new string[0];}
    static int PhysicalRequests(string root,string physical=null){return Array.FindAll(Requests(root),delegate(string line){return line.Contains("\"physicalId\":")&&(physical==null||line.Contains(Q(physical)));}).Length;}
    static void Transport(string root){
        string folder=MakeWorker(root,"stale");using(var worker=new GenericBatteryWorker(folder,false,2000)){
            Check(worker.Poll(true,IdA,null,false,PhysA).Error!=null&&Field(worker,"process")==null,"mutually exclusive refresh targets are rejected without starting a reader");
            Check(worker.Poll(true,null,null,false,"USB raw path").Error!=null&&Field(worker,"process")==null,"raw physical paths are never accepted as IPC targets");
            var snapshot=worker.Poll(true,null,null,false,PhysA);string request=Requests(folder)[0];
            Check(snapshot.Error==null&&snapshot.Devices[0].PhysicalId==PhysA&&snapshot.Health[0].PhysicalId==PhysA&&request.Contains("\"physicalId\":"+Q(PhysA))&&!request.Contains("\"deviceId\""),"typed physical poll transports only its opaque receiver target and parses matching health");
            var invalid=GenericBatteryWorker.Parse(Frame(1,1,Device(IdA,"raw-path",false),Health("invalid","ok")));
            Check(invalid.Devices[0].PhysicalId==null&&invalid.Health[0].PhysicalId==null,"invalid device and health physical identifiers are removed");
        }
    }
    static DpiConfig NativeDpi(){var dpi=new DpiConfig {DeviceKey=MouseBattery.DeviceKey("fixture-native"),ActiveStage=0,TimestampUtc=DateTime.UtcNow};dpi.Stages.Add(new DpiStage {XDpi=1200,YDpi=1200});return dpi;}
    static TrayContext Context(GenericBatteryWorker worker,Func<bool,MouseSnapshot> read,Func<DateTime> clock){
        var context=new TrayContext(false,read,delegate(int stage){throw new Exception("Native DPI must never be written");},null,null,false,null,null,null,delegate{return false;},null,worker,clock,null,null,false,delegate{throw new Exception("diagnostics=false must not dispatch wireless diagnostics");});
        Check(((System.Windows.Forms.Timer)Field(context,"refresh")).Enabled&&Field(context,"mouseActivity")==null,"test constructor preserves timer fallback without registering a live input listener");
        ((System.Windows.Forms.Timer)Field(context,"refresh")).Stop();return context;
    }
    static ActivityRefreshPolicy Policy(TrayContext context){return (ActivityRefreshPolicy)Field(context,"activityRefresh");}
    static void DiagnosticIsolation(){
        TrayContext context=null;int dispatched=0;string category=null,state=null;
        try{
            // Never construct diagnostics=true: that constructor enumerates real HID.
            // The per-context fake dispatch is installed before the initial refresh.
            context=new TrayContext(false,delegate{return new MouseSnapshot();},delegate{throw new Exception("No DPI writes");},null,null,false,null,null,null,delegate{return false;},null,null,null,null,null,false,delegate(string c,string s){dispatched++;category=c;state=s;});
            ((System.Windows.Forms.Timer)Field(context,"refresh")).Stop();Pump(delegate{return !context.IsRefreshing;});
            foreach(bool complete in new[]{true,false})foreach(bool error in new[]{false,true}){
                int before=dispatched;Call(context,"ApplyWireless",new WirelessSnapshot {Complete=complete,Error=error?"fixture_error":null},complete);
                Check(dispatched==before,"diagnostics=false "+(complete?"complete":"partial")+" "+(error?"error":"success")+" dispatches zero diagnostic writes");
            }
            // Only flip the gate on the already isolated instance. SaveStatus uses
            // an in-memory writer; notifications/cache are disabled in Main.
            var original=(SnapshotWriter)Field(context,"fileWriter");
            using(var fake=new SnapshotWriter(delegate(string path,string text){})){
                Set(context,"fileWriter",fake);
                try{
                    Set(context,"persistStatus",true);
                    foreach(bool complete in new[]{true,false})foreach(bool error in new[]{false,true}){
                        int before=dispatched;Call(context,"ApplyWireless",new WirelessSnapshot {Complete=complete,Error=error?"fixture_error":null},complete);
                        Check(dispatched==before+(complete?1:0)&&(!complete||category=="device_readers"&&state==(error?"fixture_error":"ready")),"diagnostics=true "+(complete?"complete":"partial")+" "+(error?"error":"success")+" preserves category/state and dispatch count");
                    }
                    Check(fake.Flush(2000),"diagnostics gate fixture drains only its in-memory status writer");
                }finally{Set(context,"persistStatus",false);Set(context,"fileWriter",original);}
            }
        }finally{if(context!=null)context.ExitThread();}
    }
    static void Controller(string root){
        string folder=MakeWorker(root,"stale");using(var worker=new GenericBatteryWorker(folder,false,2500)){
            TrayContext context=null;int nativeReads=0;DateTime now=DateTime.UtcNow;
            try{
                context=Context(worker,delegate(bool battery){Interlocked.Increment(ref nativeReads);return new MouseSnapshot {Dpi=NativeDpi(),Battery=new BatteryReading {Percent=60,TimestampUtc=now}};},delegate{return now;});
                Pump(delegate{return !context.IsRefreshing&&!context.GenericScanning&&context.WirelessDevices.Length==2;});
                context.WirelessDevices[0].Fresh=true;context.WirelessDevices[0].ConfirmedAtUtc=now.ToString("o");int reads=nativeReads,total=Requests(folder).Length;
                for(int i=0;i<100;i++){context.HandleMouseActivity(Source(PhysNative,true));context.HandleMouseActivity(Source(PhysA));}
                Check(nativeReads==reads&&Requests(folder).Length==total&&Policy(context).FreshSkipped==200,"fresh native and receiver activity performs no read, UI refresh or global scan");
                context.Current.TimestampUtc=now.AddMinutes(-3);context.NativeMousePresent=false;var previous=context.Current;Set(context,"busy",1);
                for(int i=0;i<100;i++)context.HandleMouseActivity(Source(PhysNative,true));
                Check(context.NativeMousePresent&&Object.ReferenceEquals(previous,context.Current)&&previous.TimestampUtc<now.AddMinutes(-2),"exact ATK activity restores receiver presence while leaving the old battery timestamp unchanged");
                Check(nativeReads==reads&&Policy(context).Pending==1,"native activity remains queued behind an existing native operation");
                Call(context,"ApplyWireless",new WirelessSnapshot {Schema=2,Generation=(int)Field(context,"wirelessDpiGeneration"),Complete=true,NativeMousePresent=false,Devices=context.WirelessDevices},true);
                Set(context,"busy",0);Call(context,"DrainActivityRefresh");Check(context.NativeMousePresent,"dispatch reasserts exact-source presence after an intervening full snapshot hides the receiver");
                Pump(delegate{return !context.IsRefreshing&&Policy(context).Pending==0;});
                Check(nativeReads==reads+1&&Requests(folder).Length==total&&context.Current.TimestampUtc==now,"stale ATK activity coalesces into exactly one real native read without generic traffic");
                context.WirelessDevices[0].Fresh=false;context.WirelessDevices[1].Fresh=false;var nativeDpi=context.Dpi;reads=nativeReads;context.NativeMousePresent=true;
                context.HandleMouseActivity(Source(PhysA));context.HandleMouseActivity(Source(PhysB));for(int i=0;i<100;i++)context.HandleMouseActivity(Source(PhysA));
                Pump(delegate{return !context.GenericScanning&&Policy(context).Pending==0;});
                Check(PhysicalRequests(folder,PhysA)==1&&PhysicalRequests(folder,PhysB)==1&&nativeReads==reads,"two active sources serialize receiver-specific reads without reading the other/native receiver");
                Check(context.NativeMousePresent,"a receiver-scoped response cannot overwrite native presence using its old global metadata");
                Check(Object.ReferenceEquals(nativeDpi,context.Dpi)&&context.WirelessDevices[0].Fresh&&context.WirelessDevices[1].Fresh,"activity never selects DPI or changes the native DPI model");
                now=now.AddSeconds(6);var first=context.WirelessDevices[0];var stale=GenericBatteryWorker.Parse(Frame(9,(int)Field(context,"wirelessDpiGeneration"),Device(IdB,PhysA,false))).Devices[0];context.WirelessDevices=new[]{first,stale};
                Check(!(bool)Call(context,"FreshActivitySource",PhysA,false),"one fresh mouse never hides another stale slot on the same receiver");
                int before=PhysicalRequests(folder);Set(context,"sessionLocked",true);context.HandleMouseActivity(Source(PhysA));Call(context,"DrainActivityRefresh");
                Check(PhysicalRequests(folder)==before&&!((System.Windows.Forms.Timer)Field(context,"activityTimer")).Enabled,"session lock suppresses new activity and retry scheduling");
                Set(context,"sessionLocked",false);context.HandleMouseActivity(Source(PhysA));Pump(delegate{return !context.GenericScanning&&Policy(context).Pending==0;});
                Check(PhysicalRequests(folder)==before+1,"activity can refresh the stale receiver after unlocking");
                reads=nativeReads;total=Requests(folder).Length;Set(context,"genericChecked",DateTime.MinValue);var timer=(System.Windows.Forms.Timer)Field(context,"refresh");Call(timer,"OnTick",EventArgs.Empty);
                Pump(delegate{return !context.IsRefreshing&&!context.GenericScanning;});Check(nativeReads==reads+1&&Requests(folder).Length==total+1,"the original background timer remains an independent read fallback");
                reads=nativeReads;total=Requests(folder).Length;context.ExitThread();context.HandleMouseActivity(Source(PhysNative,true));context.HandleMouseActivity(Source(PhysA));Call(context,"DrainActivityRefresh");
                Check(nativeReads==reads&&Requests(folder).Length==total,"disposed context rejects late activity and deferred work");context=null;
            }finally{if(context!=null)context.ExitThread();}
        }
    }
    static void BoundedFailures(string root){
        foreach(string mode in new[]{"unsupported","unavailable","forget"}){
            string folder=MakeWorker(root,mode);using(var worker=new GenericBatteryWorker(folder,false,2500)){
                TrayContext context=null;DateTime now=DateTime.UtcNow;
                try{
                    context=Context(worker,delegate{return new MouseSnapshot {Dpi=NativeDpi(),Battery=new BatteryReading {Percent=60,TimestampUtc=now}};},delegate{return now;});Pump(delegate{return !context.GenericScanning&&!context.IsRefreshing;});
                    context.HandleMouseActivity(Source(PhysA));Pump(delegate{return !context.GenericScanning;});
                    Check(PhysicalRequests(folder)==1,"first stale/unknown activity targets one receiver for "+mode);
                    if(mode=="unsupported"){
                        for(int i=0;i<100;i++)context.HandleMouseActivity(Source(PhysA));Check(PhysicalRequests(folder)==1&&Policy(context).UnsupportedSkipped==100,"healthy unknown non-mouse receiver is negatively cached");
                        context.ActivityTopologyChanged();context.HandleMouseActivity(Source(PhysA));Pump(delegate{return !context.GenericScanning;});Check(PhysicalRequests(folder)==2,"topology change re-enables discovery of an unsupported source");
                    }else{
                        now=now.AddMilliseconds(1600);Call(context,"DrainActivityRefresh");Pump(delegate{return !context.GenericScanning;});
                        Check(PhysicalRequests(folder)==2&&Policy(context).Retries==1&&Policy(context).Pending==0,"only one delayed wake retry runs for "+mode);
                        for(int i=0;i<100;i++)context.HandleMouseActivity(Source(PhysA));Check(PhysicalRequests(folder)==2&&Policy(context).CooldownSkipped==100,"failed wake reads back off without a continuing timer for "+mode);
                    }
                }finally{if(context!=null)context.ExitThread();}
            }
        }
    }
    static void DpiBusy(string root){
        string folder=MakeWorker(root,"dpi");using(var worker=new GenericBatteryWorker(folder,false,3000)){
            TrayContext context=null;DateTime now=DateTime.UtcNow;int nativeReads=0;
            try{
                context=Context(worker,delegate{nativeReads++;return new MouseSnapshot {Dpi=NativeDpi(),Battery=new BatteryReading {Percent=60,TimestampUtc=now}};},delegate{return now;});Pump(delegate{return !context.GenericScanning&&!context.IsRefreshing;});var native=context.Dpi;int baseline=nativeReads;
                context.SelectWirelessDpi(IdA,Token,800);Pump(delegate{return File.Exists(Path.Combine(folder,"write-entered"));});context.HandleMouseActivity(Source(PhysA));
                Check(context.DesiredWirelessDpi(IdA,Token)==800&&Policy(context).Pending==1&&PhysicalRequests(folder)==0,"activity stays deferred behind the exact in-flight DPI action without cancelling it");
                File.WriteAllText(Path.Combine(folder,"release-write"),"go");Pump(delegate{return !context.GenericScanning&&Policy(context).Pending==0;});
                Check(Array.FindAll(Requests(folder),delegate(string line){return line.Contains("\"command\":\"set_dpi\"");}).Length==1&&PhysicalRequests(folder)==1&&context.WirelessDevices[0].Dpi.Current==800,"deferred activity issues a read after DPI completes and never replays the write");
                Check(nativeReads==baseline&&Object.ReferenceEquals(context.Dpi,native),"receiver activity behind DPI does not touch native ATK reads or settings");
            }finally{File.WriteAllText(Path.Combine(folder,"release-write"),"go");if(context!=null)context.ExitThread();}
        }
    }
    // Each budget request waits on a numbered temp-file barrier. No real clocks
    // are advanced, and no 8/60/300-second wait is needed for the interval gates.
    static void Entered(string folder,int request){Pump(delegate{return File.Exists(Path.Combine(folder,"entered-"+request));});}
    static void Release(string folder,int request,bool fresh){if(fresh)File.WriteAllText(Path.Combine(folder,"fresh-"+request),"yes");File.WriteAllText(Path.Combine(folder,"release-"+request),"go");}
    static void Settled(TrayContext context){Pump(delegate{return !context.GenericScanning&&!context.IsRefreshing;});}
    static void CountBudget(TrayContext context,string folder,string label,int total,int physical,int forced,int queued,int pending){
        // Exclude only the constructor's completed request, never a scenario read.
        string[] lines=Requests(folder);int p=0,f=0;for(int i=1;i<lines.Length;i++){if(lines[i].Contains("\"physicalId\":"))p++;if(lines[i].Contains("\"force\":true"))f++;}
        int t=lines.Length-1;long a=Policy(context).Reads;
        Check(t==total&&p==physical&&f==forced&&Policy(context).Pending==pending&&!((bool)Field(context,"genericQueued")),label+" exact total/physical/force/pending and empty forced queue");
        // Q is verified by requests after the blocked completion, not inferred
        // from an invented controller counter: all reads here are G, A, or Q.
        Check(t-physical-(label.StartsWith("panel-target")?0:1)==queued,label+" forced follow-up count");
        Console.WriteLine("BUDGET "+label+" T/P/G/F/A/Q/N="+t+"/"+p+"/"+(t-p)+"/"+f+"/"+a+"/"+queued+"/"+Policy(context).Pending);
    }
    static void ControllerInterleavings(string root){
        foreach(bool firstFresh in new[]{true,false}){
            string folder=MakeWorker(root,"budget-merge");using(var worker=new GenericBatteryWorker(folder,false,5000)){
                TrayContext context=null;DateTime now=DateTime.UtcNow;
                try{
                    context=Context(worker,delegate{return new MouseSnapshot();},delegate{return now;});Entered(folder,1);Release(folder,1,false);Settled(context);
                    Call(context,"RequestWirelessRead",true,null,null);Entered(folder,2);
                    for(int i=0;i<5;i++){Call(context,"RequestWirelessRead",true,null,null);Call(context,"RequestWirelessRead",false,null,null);}
                    for(int i=0;i<100;i++)context.HandleMouseActivity(Source(PhysA));
                    Check(Requests(folder).Length==2&&(bool)Field(context,"genericQueued")&&Policy(context).Pending==1&&Policy(context).Coalesced==99,"full busy merge "+firstFresh+" keeps one forced queue and one coalesced activity source");
                    Release(folder,2,firstFresh);Entered(folder,3);
                    Check(Requests(folder).Length==3&&PhysicalRequests(folder)==0&&Policy(context).Reads==0,"full busy merge "+firstFresh+" dispatches forced full follow-up before activity");
                    Release(folder,3,true);Settled(context);
                    CountBudget(context,folder,"full-merge-first-"+(firstFresh?"fresh":"stale"),2,0,2,1,0);
                }finally{if(context!=null)context.ExitThread();}
            }
        }
        foreach(string outcome in new[]{"full-fresh","target-fresh","target-stale"}){
            string folder=MakeWorker(root,"budget-activity");using(var worker=new GenericBatteryWorker(folder,false,5000)){
                TrayContext context=null;DateTime now=DateTime.UtcNow;
                try{
                    context=Context(worker,delegate{return new MouseSnapshot();},delegate{return now;});Entered(folder,1);Release(folder,1,false);Settled(context);
                    Call(context,"RequestWirelessRead",true,null,null);Entered(folder,2);
                    for(int i=0;i<100;i++)context.HandleMouseActivity(Source(PhysA));
                    Check(Policy(context).Pending==1&&Policy(context).Reads==0&&Policy(context).Coalesced==99&&!(bool)Field(context,"genericQueued"),outcome+" activity defers behind a full read without setting the forced queue");
                    Release(folder,2,outcome=="full-fresh");
                    if(outcome!="full-fresh"){
                        Entered(folder,3);Check(Policy(context).Pending==1&&PhysicalRequests(folder)==1,outcome+" stale full completion dispatches one physical activity read");
                        for(int i=0;i<100;i++)context.HandleMouseActivity(Source(PhysA));
                        Check(Policy(context).Coalesced==199&&Requests(folder).Length==3,outcome+" activity during targeted flight coalesces without a second request");
                        Release(folder,3,outcome=="target-fresh");Settled(context);
                        if(outcome=="target-stale"){
                            Check(Policy(context).Pending==1&&Policy(context).Retries==0,"stale targeted completion retains one delayed activity retry");
                            now=now.AddSeconds(1);Call(context,"DrainActivityRefresh");Check(Requests(folder).Length==3,"controller does not dispatch activity retry before its due time");
                            now=now.AddMilliseconds(600);Call(context,"DrainActivityRefresh");Entered(folder,4);Release(folder,4,false);Settled(context);
                            for(int i=0;i<100;i++)context.HandleMouseActivity(Source(PhysA));
                            Check(Policy(context).Retries==1&&Policy(context).CooldownSkipped==100,"stale activity retry exhausts its budget and continued packets respect backoff");
                        }else Check(Policy(context).Pending==0&&Policy(context).Retries==0,"fresh targeted completion clears activity pending without retry");
                    }
                    Settled(context);int p=outcome=="full-fresh"?0:outcome=="target-fresh"?1:2;
                    CountBudget(context,folder,outcome,1+p,p,1+p,0,0);
                }finally{if(context!=null)context.ExitThread();}
            }
        }
        foreach(bool targetFresh in new[]{true,false}){
            string folder=MakeWorker(root,"budget-panel");using(var worker=new GenericBatteryWorker(folder,false,5000)){
                TrayContext context=null;DateTime now=DateTime.UtcNow;
                try{
                    context=Context(worker,delegate{return new MouseSnapshot();},delegate{return now;});Entered(folder,1);Release(folder,1,false);Settled(context);
                    context.HandleMouseActivity(Source(PhysA));Entered(folder,2);context.PanelOpened();
                    Check((bool)Field(context,"genericQueued")&&Requests(folder).Length==2,"panel request behind targeted "+targetFresh+" queues one independent forced full read");
                    Release(folder,2,targetFresh);Entered(folder,3);
                    Check(Policy(context).Pending==(targetFresh?0:1)&&Policy(context).Reads==1&&Policy(context).Retries==0,"targeted "+targetFresh+" completion updates activity pending while panel full read is in flight");
                    Release(folder,3,true);Settled(context);
                    CountBudget(context,folder,"panel-target-"+(targetFresh?"fresh":"stale"),2,1,2,1,0);
                    context.PanelClosed();
                }finally{if(context!=null)context.ExitThread();}
            }
        }
    }
    static void ControllerTimeGates(string root){
        foreach(int seconds in new[]{60,300}){
            DeviceSettings.UseTransientConfiguration(new AudioBindings {RememberReadings=false,LowBatteryNotifications=false,BackgroundSeconds=seconds});
            string folder=MakeWorker(root,"budget-gates");using(var worker=new GenericBatteryWorker(folder,false,5000)){
                TrayContext context=null;DateTime now=DateTime.UtcNow;
                try{
                    context=Context(worker,delegate{return new MouseSnapshot();},delegate{return now;});Entered(folder,1);Release(folder,1,true);Settled(context);
                    context.PanelOpened();Entered(folder,2);Release(folder,2,true);Settled(context);
                    int before=Requests(folder).Length;
                    Set(context,"genericChecked",DateTime.UtcNow.AddSeconds(-7));Call(context,"RequestWirelessRead",false,null,null);
                    Check(Requests(folder).Length==before&&!context.GenericScanning,"open panel suppresses nonforce before eight seconds (background "+seconds+")");
                    Set(context,"genericChecked",DateTime.UtcNow.AddSeconds(-9));Call(context,"RequestWirelessRead",false,null,null);Entered(folder,3);Release(folder,3,true);Settled(context);
                    Check(!(bool)Field(context,"genericQueued"),"open panel nonforce due read creates no forced follow-up (background "+seconds+")");
                    Set(context,"genericChecked",DateTime.MinValue);var timer=(System.Windows.Forms.Timer)Field(context,"refresh");before=Requests(folder).Length;Call(timer,"OnTick",EventArgs.Empty);
                    Check(!context.GenericScanning&&Requests(folder).Length==before,"background timer is suppressed while panel is open (background "+seconds+")");
                    context.PanelClosed();Check(!((System.Windows.Forms.Timer)Field(context,"liveSync")).Enabled,"closing panel stops liveSync (background "+seconds+")");
                    Set(context,"genericChecked",DateTime.UtcNow.AddSeconds(-(seconds-2)));Call(timer,"OnTick",EventArgs.Empty);Settled(context);
                    Check(!context.GenericScanning&&Requests(folder).Length==before,"closed panel background tick skips nonforce before "+seconds+" seconds");
                    Set(context,"genericChecked",DateTime.UtcNow.AddSeconds(-(seconds+2)));Call(timer,"OnTick",EventArgs.Empty);Entered(folder,4);Release(folder,4,true);Settled(context);
                    string[] lines=Requests(folder);
                    Check(lines.Length==4&&lines[1].Contains("\"force\":true")&&lines[2].Contains("\"force\":false")&&lines[2].Contains("\"parkEmptyReaders\":false")&&lines[3].Contains("\"force\":false")&&lines[3].Contains("\"parkEmptyReaders\":true"),"panel/background "+seconds+" sends exactly one due nonforce read per mode with original park flags");
                    Check((DateTime.UtcNow-(DateTime)Field(context,"genericChecked")).TotalSeconds<2,"completed background read updates the real interval gate for "+seconds);
                    Console.WriteLine("GATES background="+seconds+" scenario T/P/G/F=3/0/3/1; open nonforce=1 closed nonforce=1; no long waits");
                }finally{if(context!=null)context.ExitThread();}
            }
        }
        DeviceSettings.UseTransientConfiguration(new AudioBindings {RememberReadings=false,LowBatteryNotifications=false});
    }
    static long Number(string json,string field){return Int64.Parse(Regex.Match(json,"\""+field+"\":([0-9]+)").Groups[1].Value);}
    static string Text(string json,string field){return Regex.Match(json,"\""+field+"\":\"([^\"]*)\"").Groups[1].Value;}
    static void FakeWorker(){
        string root=Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).FullName;string mode=Path.GetFileName(root);bool a=false,b=false;int dpi=400,sequence=0;
        using(var input=new StreamReader(Console.OpenStandardInput(),Encoding.UTF8))using(var output=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false)){AutoFlush=true}){
            string line;while((line=input.ReadLine())!=null){File.AppendAllText(Path.Combine(root,"requests.log"),line+Environment.NewLine);long id=Number(line,"requestId");int generation=(int)Number(line,"generation");string physical=Text(line,"physicalId"),result=null;
                sequence++;
                if(mode.StartsWith("budget")){
                    File.WriteAllText(Path.Combine(root,"entered-"+sequence),"ready");var deadline=DateTime.UtcNow.AddSeconds(6);
                    while(!File.Exists(Path.Combine(root,"release-"+sequence))&&DateTime.UtcNow<deadline)Thread.Sleep(5);
                    if(!File.Exists(Path.Combine(root,"release-"+sequence)))throw new Exception("Fake request barrier timed out");
                }
                if(Text(line,"command")=="set_dpi"){
                    File.WriteAllText(Path.Combine(root,"write-entered"),"ready");var end=DateTime.UtcNow.AddSeconds(6);while(!File.Exists(Path.Combine(root,"release-write"))&&DateTime.UtcNow<end)Thread.Sleep(5);
                    dpi=(int)Number(line,"dpi");result="{\"deviceId\":"+Q(IdA)+",\"targetToken\":"+Q(Token)+",\"requestedDpi\":"+dpi+",\"confirmedDpi\":"+dpi+",\"success\":true,\"outcomeUnknown\":false,\"error\":null,\"checkedAtUtc\":"+Q(DateTime.UtcNow.ToString("o"))+"}";
                }
                if(physical==PhysA)a=true;if(physical==PhysB)b=true;
                bool unsupported=mode.StartsWith("unsupported"),unavailable=mode.StartsWith("unavailable"),forget=mode.StartsWith("forget")&&physical==PhysA;
                string devices=(unsupported||unavailable?"":Device(IdA,PhysA,mode.StartsWith("stale")?a:false,"mouse",dpi)+",")+Device(IdB,PhysB,b);
                if(forget)devices=Device(IdA,PhysA,false,"keyboard")+","+Device(IdB,PhysB,b);
                if(mode.StartsWith("dpi"))devices=Device(IdA,PhysA,a,"mouse",dpi)+","+Device(IdB,PhysB,b);
                if(mode.StartsWith("budget")){bool fresh=File.Exists(Path.Combine(root,"fresh-"+sequence));devices=Device(IdA,PhysA,fresh)+","+Device(IdB,PhysB,fresh);}
                string state=unavailable?"unavailable":unsupported?"unsupported":"ok";string health=physical.Length==0?"":Health(physical,state);
                string frame=Frame(id,generation,devices,health,result);if(physical.Length>0)frame=frame.Replace("\"nativeMousePresent\":true","\"nativeMousePresent\":false");output.WriteLine(frame);
            }
        }
    }
    [STAThread] static int Main(string[] args){
        if(args.Length>0&&args[0]=="-I"){FakeWorker();return 0;}
        var cached=typeof(DeviceSettings).GetField("current",BindingFlags.NonPublic|BindingFlags.Static);object previous=cached.GetValue(null);
        try{
            DeviceSettings.UseTransientConfiguration(new AudioBindings {RememberReadings=false,LowBatteryNotifications=false});string root=Path.Combine(Path.GetFullPath(args.Length>0?args[0]:"tmp"),"activity-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            DiagnosticIsolation();if(args.Length<2||args[1]!="--isolation-only"){Policy();Transport(root);Controller(root);BoundedFailures(root);DpiBusy(root);ControllerInterleavings(root);ControllerTimeGates(root);}
            Console.WriteLine("PASS "+checks+" activity refresh policy/physical IPC/deferred/DPI-safety checks; no live input or HID access");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{cached.SetValue(null,previous);}
    }
}
