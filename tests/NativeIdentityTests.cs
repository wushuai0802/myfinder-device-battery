using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

// All device state and every writer are injected. No HID, audio or settings writes.
static class NativeIdentityTests {
    static int checks;
    static readonly string KeyA=MouseBattery.DeviceKey("fixture-receiver-A"),KeyB=MouseBattery.DeviceKey("fixture-receiver-B");
    static void Check(bool value,string name){if(!value)throw new Exception("FAIL "+name);checks++;}
    static void Pump(Func<bool> done){var clock=Stopwatch.StartNew();while(!done()&&clock.ElapsedMilliseconds<5000){Application.DoEvents();Thread.Sleep(2);}Check(done(),"isolated controller settled");}
    static DpiConfig Dpi(string key,int active=0,int middle=800){
        var result=new DpiConfig {DeviceKey=key,ActiveStage=active,MaxStages=3,TimestampUtc=DateTime.UtcNow};
        foreach(int value in new[]{400,middle,1600})result.Stages.Add(new DpiStage {XDpi=value,YDpi=value});return result;
    }
    sealed class Fixture:IDisposable {
        internal string Key=KeyA;
        internal int Stage,Middle=800,Reads,Attempts,Writes,ReadGateAt;
        internal bool GateWrite,GateAfterCommit,FailWrite,FailRead,WrongReadback;
        internal readonly ManualResetEvent ReadEntered=new ManualResetEvent(false),ReadRelease=new ManualResetEvent(false),WriteEntered=new ManualResetEvent(false),WriteRelease=new ManualResetEvent(false);
        internal readonly TrayContext Owner;
        internal Fixture(){
            DeviceSettings.UseTransientConfiguration(new AudioBindings {RememberReadings=false,ReducedMotion=true});
            Owner=new TrayContext(false,Read,delegate(int stage){throw new Exception("Unbound writer must never be called");},null,null,false,null,null,null,delegate{return false;},null,null,null,Write);
            Pump(delegate{return Owner.Dpi!=null&&!Owner.IsRefreshing;});
        }
        MouseSnapshot Read(bool battery){
            int number=Interlocked.Increment(ref Reads);string key=Key;bool fail=FailRead;
            var result=new MouseSnapshot {DeviceKey=key,Dpi=fail?null:Dpi(key,Stage,Middle),DpiError=fail?"fixture unavailable":null,Battery=battery&&!fail?new BatteryReading {DeviceKey=key,Percent=80,TimestampUtc=DateTime.UtcNow}:null,BatteryError=fail?"fixture unavailable":null};
            if(number==ReadGateAt){ReadEntered.Set();ReadRelease.WaitOne(4000);}return result;
        }
        DpiConfig Write(NativeDpiRequest request){
            Interlocked.Increment(ref Attempts);request.CheckCurrent();
            if(GateWrite&&!GateAfterCommit){WriteEntered.Set();WriteRelease.WaitOne(4000);}
            request.Validate(Dpi(Key,Stage,Middle));
            if(FailWrite)throw new IOException("fixture write outcome unavailable");
            Interlocked.Increment(ref Writes);Stage=request.Stage;var result=Dpi(Key,Stage,Middle);
            if(WrongReadback)result.Stages[request.Stage].XDpi++;
            if(GateWrite&&GateAfterCommit){WriteEntered.Set();WriteRelease.WaitOne(4000);}return result;
        }
        public void Dispose(){ReadRelease.Set();WriteRelease.Set();Owner.ExitThread();ReadEntered.Dispose();ReadRelease.Dispose();WriteEntered.Dispose();WriteRelease.Dispose();}
    }
    [DataContract] sealed class Status {
        [DataMember(Name="processId")] public int ProcessId {get;set;}
        [DataMember(Name="processStartedAtUtc")] public string Started {get;set;}
        [DataMember(Name="snapshotGeneratedAtUtc")] public string Generated {get;set;}
        [DataMember(Name="instanceId")] public string Instance {get;set;}
        [DataMember(Name="dpiLiveVerified")] public bool DpiVerified {get;set;}
        [DataMember(Name="persistence")] public Persistence Persistence {get;set;}
    }
    [DataContract] sealed class Persistence {
        [DataMember(Name="lastSuccessfulStatusWriteUtc")] public string LastSuccess {get;set;}
        [DataMember(Name="failureCount")] public long Failures {get;set;}
    }
    static Status ReadStatus(TrayContext owner){using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(owner.BuildStatusJson())))return (Status)new DataContractJsonSerializer(typeof(Status)).ReadObject(stream);}
    static void TopologyChangePreservesFreshBatteryUntilReconciled(){
        using(var f=new Fixture()) {
            var reading=f.Owner.Current;string error=f.Owner.ErrorMessage,token=f.Owner.NativeDpiToken;
            Check(reading!=null&&f.Owner.CanSetNativeDpi,"fixture starts with a fresh battery and confirmed DPI");
            f.Owner.ActivityTopologyChanged();
            Check(Object.ReferenceEquals(f.Owner.Current,reading)&&f.Owner.ErrorMessage==error,"unrelated topology preserves the current battery and its error state");
            var mouse=UiState.Mouse(f.Owner.Current,f.Owner.LastGood,DateTime.UtcNow);
            Check(mouse.Fresh&&UiState.Tray("mouse",mouse,UiState.Headset(null,DateTime.UtcNow)).Battery.Fresh,"the card and tray retain fresh battery evidence after topology changes");
            Check(!f.Owner.CanSetNativeDpi&&!ReadStatus(f.Owner).DpiVerified&&!String.IsNullOrEmpty(f.Owner.DpiError),"topology still invalidates DPI until the receiver is reread");
            Check(!UiState.Mouse(f.Owner.Current,f.Owner.LastGood,reading.TimestampUtc.AddSeconds(91)).Fresh,"retained battery evidence still expires naturally after ninety seconds");
            f.ReadGateAt=f.Reads+1;f.Owner.RefreshBattery(false);Pump(delegate{return f.ReadEntered.WaitOne(0);});
            Check(Object.ReferenceEquals(f.Owner.Current,reading)&&!f.Owner.CanSetNativeDpi,"a pending same-receiver reread preserves battery evidence without enabling DPI");
            f.Owner.SelectDpiStage(1);Check(f.Attempts==0&&f.Owner.GetFeedback().PendingStage==-1,"unconfirmed DPI controls cannot queue a write");
            f.ReadRelease.Set();Pump(delegate{return !f.Owner.IsRefreshing&&f.Owner.CanSetNativeDpi;});
            Check(f.Owner.Current.DeviceKey==KeyA&&f.Owner.NativeDpiToken!=token,"successful same-receiver reread restores DPI with the new connection epoch");
        }
        Console.WriteLine("PASS TopologyChangePreservesFreshBatteryUntilReconciled");
    }
    static void ReceiverIdentityChangeClearsPreviousBattery(){
        using(var f=new Fixture()) {
            var reading=f.Owner.Current;
            f.Key=KeyB;f.FailRead=true;f.Owner.ActivityTopologyChanged();
            Check(Object.ReferenceEquals(f.Owner.Current,reading),"the event alone does not claim that the receiver changed");
            f.ReadGateAt=f.Reads+1;f.Owner.RefreshBattery(false);Pump(delegate{return f.ReadEntered.WaitOne(0);});
            Check(Object.ReferenceEquals(f.Owner.Current,reading)&&!f.Owner.CanSetNativeDpi,"battery evidence remains while the new receiver identity is pending");
            f.ReadRelease.Set();Pump(delegate{return !f.Owner.IsRefreshing;});
            Check(f.Owner.Current==null&&f.Owner.LastGood==null&&f.Owner.Dpi==null,"confirmed receiver identity change clears the old battery, history and DPI");
            Check(!UiState.Mouse(f.Owner.Current,f.Owner.LastGood,DateTime.UtcNow).Percent.HasValue&&!f.Owner.CanSetNativeDpi,"a receiver without a new reading cannot reuse the old receiver's evidence");
            f.FailRead=false;f.Owner.RefreshBattery(false);Pump(delegate{return !f.Owner.IsRefreshing&&f.Owner.CanSetNativeDpi;});
            Check(f.Owner.Current.DeviceKey==KeyB&&Object.ReferenceEquals(f.Owner.Current,f.Owner.LastGood),"new receiver recovery uses only its own confirmed battery");
        }
        Console.WriteLine("PASS ReceiverIdentityChangeClearsPreviousBattery");
    }
    [STAThread] static int Main(){
        try {
            TopologyChangePreservesFreshBatteryUntilReconciled();
            ReceiverIdentityChangeClearsPreviousBattery();
            Check(KeyA!=KeyB&&KeyA==MouseBattery.DeviceKey("FIXTURE-RECEIVER-A"),"receiver keys distinguish instances and normalize path case");
            bool rejected=false;try{new NativeDpiRequest(Dpi(null),1,0,null);}catch(ArgumentException){rejected=true;}Check(rejected,"missing receiver evidence is rejected without HID access");
            var cancelled=new NativeDpiRequest(Dpi(KeyA),1,0,delegate{return false;});rejected=false;
            try{MouseBattery.SetActiveDpiStage(cancelled);}catch(OperationCanceledException){rejected=true;}Check(rejected,"cancelled request is rejected by public writer before enumeration");
            var target=new NativeDpiRequest(Dpi(KeyA),1,0,delegate{return true;});
            foreach(var actual in new[]{Dpi(KeyB),Dpi(KeyA,0,900)}){rejected=false;try{target.Validate(actual);}catch(IOException){rejected=true;}Check(rejected,"receiver swap or changed displayed target cannot pass validation");}

            using(var f=new Fixture()) {
                f.ReadGateAt=f.Reads+1;f.Owner.RefreshBattery(false);Pump(delegate{return f.ReadEntered.WaitOne(0);});
                f.Owner.SelectDpiStage(1);Check(f.Owner.GetFeedback().PendingStage==1,"write intent waits behind the active read");
                f.Key=KeyB;f.Owner.ActivityTopologyChanged();Check(!f.Owner.CanSetNativeDpi,"topology change blocks stale native controls");f.ReadRelease.Set();
                Pump(delegate{return !f.Owner.IsRefreshing&&f.Owner.Dpi!=null&&f.Owner.Dpi.DeviceKey==KeyB;});
                Check(f.Attempts==0&&f.Writes==0,"queued old DPI intent is cancelled and late A read cannot replace B");
            }
            using(var f=new Fixture()) {
                f.GateWrite=true;f.Owner.SelectDpiStage(1);Pump(delegate{return f.WriteEntered.WaitOne(0);});
                f.Key=KeyB;f.Owner.ActivityTopologyChanged();f.WriteRelease.Set();
                Pump(delegate{return !f.Owner.IsRefreshing&&f.Owner.Dpi!=null&&f.Owner.Dpi.DeviceKey==KeyB;});
                Check(f.Attempts==1&&f.Writes==0,"topology change during pre-write work cancels the operation before commit");
            }
            using(var f=new Fixture()) {
                f.GateWrite=true;f.Owner.SelectDpiStage(1);Pump(delegate{return f.WriteEntered.WaitOne(0);});
                f.Key=KeyB;f.WriteRelease.Set();Pump(delegate{return !f.Owner.IsRefreshing&&f.Owner.Dpi!=null&&f.Owner.Dpi.DeviceKey==KeyB;});
                Check(f.Writes==0,"physical revalidation rejects a new receiver even without a topology event");
            }
            using(var f=new Fixture()) {
                f.GateWrite=true;f.GateAfterCommit=true;f.Owner.SelectDpiStage(1);Pump(delegate{return f.WriteEntered.WaitOne(0);});
                Check(f.Writes==1,"fixture committed only to the original receiver before unplug");f.Key=KeyB;f.Stage=0;f.Owner.ActivityTopologyChanged();f.WriteRelease.Set();
                Pump(delegate{return !f.Owner.IsRefreshing&&f.Owner.Dpi!=null&&f.Owner.Dpi.DeviceKey==KeyB;});
                Check(f.Owner.Dpi.ActiveStage==0&&f.Writes==1,"late verified A write result does not become B's state or cause another write");
            }
            using(var f=new Fixture()) {
                var nav=new PopupNavigation();var old=PopupPages.Build(f.Owner,nav,false).Find(delegate(PopupControl value){return value.Id==11;});
                f.Middle=900;f.Owner.RefreshBattery(false);Pump(delegate{return !f.Owner.IsRefreshing&&f.Owner.Dpi.Stages[1].XDpi==900;});
                var fresh=PopupPages.Build(f.Owner,nav,false).Find(delegate(PopupControl value){return value.Id==11;});
                Check(!PopupPages.SameAction(old,fresh),"a changed displayed DPI table changes the press/release action identity");
                f.Owner.SelectDpiStage(1,old.TargetToken);Check(f.Attempts==0,"old UI token cannot select a relabeled target");
                f.Owner.SelectDpiStage(1,fresh.TargetToken);Pump(delegate{return !f.Owner.IsDpiRefreshing;});Check(f.Writes==1&&f.Owner.Dpi.ActiveStage==1,"newly confirmed table remains usable");
            }
            using(var f=new Fixture()) {
                f.GateWrite=true;f.Owner.SelectDpiStage(1);Pump(delegate{return f.WriteEntered.WaitOne(0);});
                f.Owner.SelectDpiStage(2);f.WriteRelease.Set();Pump(delegate{return !f.Owner.IsDpiRefreshing&&f.Owner.Dpi.ActiveStage==2;});
                Check(f.Writes==2,"normal serialized rapid clicks retain the last requested stage");
            }
            using(var f=new Fixture()) {
                f.GateWrite=true;f.FailWrite=true;f.FailRead=true;f.Owner.SelectDpiStage(1);Pump(delegate{return f.WriteEntered.WaitOne(0);});f.Owner.SelectDpiStage(2);f.WriteRelease.Set();
                Pump(delegate{return !f.Owner.IsRefreshing;});Check(f.Attempts==1&&f.Writes==0&&!f.Owner.CanSetNativeDpi,"failed write clears queued intents and blocks controls until a fresh read");
                f.Owner.SelectDpiStage(2);Check(f.Attempts==1,"unknown state cannot accept another write");f.FailRead=false;f.FailWrite=false;f.Owner.RefreshBattery(false);Pump(delegate{return !f.Owner.IsRefreshing&&f.Owner.CanSetNativeDpi;});Check(f.Attempts==1,"reconciliation never automatically resubmits the lost intent");
            }
            using(var f=new Fixture()) {
                f.WrongReadback=true;f.ReadGateAt=f.Reads+1;f.Owner.SelectDpiStage(1);Pump(delegate{return f.ReadEntered.WaitOne(0);});
                Check(!f.Owner.CanSetNativeDpi&&f.Owner.Dpi.ActiveStage==0,"same-index readback with a different DPI value cannot be accepted as the requested result");
                f.ReadRelease.Set();Pump(delegate{return !f.Owner.IsRefreshing;});Check(f.Writes==1,"mismatched readback reconciles without repeating the write");
            }
            using(var f=new Fixture()) {
                var status=ReadStatus(f.Owner);using(var process=Process.GetCurrentProcess()){Check(status.ProcessId==process.Id&&DateTime.Parse(status.Started).ToUniversalTime()==process.StartTime.ToUniversalTime(),"diagnostic snapshot is bound to this exact process instance");}
                Check(!String.IsNullOrEmpty(status.Instance)&&DateTime.Parse(status.Generated).ToUniversalTime()>=DateTime.Parse(status.Started).ToUniversalTime()&&status.Persistence!=null,"snapshot has independent generation and persistence metadata");
                Check(status.DpiVerified,"fresh identity-bound DPI is reported live");f.Owner.Dpi.TimestampUtc=DateTime.UtcNow.AddSeconds(-91);
                Check(!ReadStatus(f.Owner).DpiVerified&&!f.Owner.CanSetNativeDpi,"old DPI evidence cannot remain live or writable");
                Check(!PopupPages.Build(f.Owner,new PopupNavigation(),false).Find(delegate(PopupControl value){return value.Id==11;}).Enabled,"stale DPI control is disabled for pointer and accessibility users");
            }
            var now=DateTime.UtcNow;
            Check(UiState.SyncText(UiState.Mouse(new BatteryReading {Percent=80,TimestampUtc=now},null,now),now)=="刚刚确认","new reading uses immediate confirmation text");
            Check(UiState.SyncText(UiState.Mouse(new BatteryReading {Percent=80,TimestampUtc=now.AddSeconds(-42)},null,now),now)=="40秒前确认","elapsed text follows actual battery sample time");
            Check(UiState.SyncText(UiState.Mouse(new BatteryReading {Percent=80,TimestampUtc=now.AddSeconds(-65)},null,now),now)=="1分钟前确认","minute-old sample is not described as just updated");
            Check(UiState.SyncText(UiState.Mouse(null,new BatteryReading {Percent=80,TimestampUtc=now.AddDays(-1)},now),now).StartsWith("上次确认 "),"cached/failed reads retain their original time qualifier");
            Console.WriteLine("PASS "+checks+" native identity/generation/persistence-metadata/sync-text checks; all hardware and writes are fake");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
