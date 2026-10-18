using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

// Every endpoint, reader and writer is fake. The child process implements only
// this fixture's JSON echo protocol; no Windows audio/HID API or user file runs.
static class AudioStateRegressionTests {
    static int checks;
    static void Check(bool value,string name){if(!value)throw new Exception("FAIL "+name);checks++;}
    static void Pump(Func<bool> done){
        DateTime deadline=DateTime.UtcNow.AddSeconds(6);
        while(!done()&&DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(5);}
        Check(done(),"fake operation completes within its deadline");
    }
    static void Wait(WaitHandle signal){if(!signal.WaitOne(5000))throw new Exception("Fake gate timed out");}
    static DpiConfig Dpi(){var result=new DpiConfig {ActiveStage=0,TimestampUtc=DateTime.UtcNow};result.Stages.Add(new DpiStage {XDpi=800,YDpi=800});return result;}
    static MouseSnapshot Mouse(bool battery){return new MouseSnapshot {Dpi=Dpi(),Battery=battery?new BatteryReading {Percent=80,TimestampUtc=DateTime.UtcNow}:null};}
    static AudioBindings Bindings(string suffix){return new AudioBindings {SpeakerEndpointId="speaker-"+suffix,HeadsetEndpointId="headset-"+suffix,MicrophoneEndpointId="mic-"+suffix};}
    static AudioReading Audio(AudioBindings settings,bool muted=false){
        return new AudioReading {
            ConsoleOutputId=settings.SpeakerEndpointId,MultimediaOutputId=settings.SpeakerEndpointId,CommunicationsOutputId=settings.SpeakerEndpointId,
            ActiveOutput="speaker",SpeakerId=settings.SpeakerEndpointId,HeadsetId=settings.HeadsetEndpointId,MicrophoneId=settings.MicrophoneEndpointId,
            SpeakerName="Fake speaker",HeadsetName="Fake headset",MicrophoneName="Fake microphone",SpeakerAvailable=!String.IsNullOrEmpty(settings.SpeakerEndpointId),HeadsetAvailable=!String.IsNullOrEmpty(settings.HeadsetEndpointId),MicrophoneAvailable=!String.IsNullOrEmpty(settings.MicrophoneEndpointId),
            MicrophoneMuted=String.IsNullOrEmpty(settings.MicrophoneEndpointId)?null:(bool?)muted,OutputVolumePercent=String.IsNullOrEmpty(settings.SpeakerEndpointId)?null:(int?)50,VolumeEndpointId=settings.SpeakerEndpointId,OperationSucceeded=true,TimestampUtc=DateTime.UtcNow,
            Devices=new[]{new AudioDeviceChoice {Id=settings.SpeakerEndpointId,Name="Fake speaker",Flow=0},new AudioDeviceChoice {Id=settings.HeadsetEndpointId,Name="Fake headset",Flow=0},new AudioDeviceChoice {Id=settings.MicrophoneEndpointId,Name="Fake microphone",Flow=1}}
        };
    }
    static TrayContext Context(Func<AudioCommand,AudioReading> runner){
        return new TrayContext(false,Mouse,delegate(int stage){return Dpi();},null,
            delegate{throw new Exception("Legacy reader must not run");},false,
            delegate(string target){throw new Exception("Legacy output writer must not run");},
            delegate(bool muted){throw new Exception("Legacy mute writer must not run");},
            delegate(string endpoint,int value){throw new Exception("Legacy volume writer must not run");},delegate{return false;},runner);
    }
    static bool ControlsDisabled(TrayContext context){
        return PopupPages.Build(context,new PopupNavigation {Page=PopupPage.Audio},true).TrueForAll(delegate(PopupControl row){return row.Id!=100&&row.Id!=101&&row.Id!=102&&row.Id!=240||!row.Enabled;});
    }
    static void EmptyAndFailedSnapshots(){
        DeviceSettings.UseTransientConfiguration(Bindings("A"));
        AudioReading next=Audio(Bindings("A"));int reads=0,writes=0;TrayContext context=null;
        try {
            context=Context(delegate(AudioCommand command){if(command.Operation!="read")Interlocked.Increment(ref writes);Interlocked.Increment(ref reads);return next;});
            Pump(delegate{return reads==1&&!context.IsAudioBusy;});
            next=new AudioReading {OperationSucceeded=true,TimestampUtc=DateTime.UtcNow,MicrophoneId="mic-new",MicrophoneName="Recording only",MicrophoneAvailable=true,MicrophoneMuted=true,
                Devices=new[]{new AudioDeviceChoice {Id="mic-new",Name="Recording only",Flow=1}}};
            context.RefreshAudioDevices();Pump(delegate{return reads==2&&!context.IsAudioBusy;});
            var rows=PopupPages.Build(context,new PopupNavigation {Page=PopupPage.Audio},true);
            Check(Object.ReferenceEquals(context.Audio,next)&&context.Audio.ConsoleOutputId==null&&context.Audio.MicrophoneId=="mic-new"&&context.Audio.MicrophoneMuted==true,"successful no-output snapshot replaces every old field");
            Check(!rows.Find(delegate(PopupControl row){return row.Id==100;}).Enabled&&!rows.Find(delegate(PopupControl row){return row.Id==240;}).Enabled&&rows.Find(delegate(PopupControl row){return row.Id==102;}).Enabled,"recording-only state disables missing playback controls and keeps the new microphone");
            next=new AudioReading {OperationSucceeded=true,TimestampUtc=DateTime.UtcNow};context.RefreshAudioDevices();Pump(delegate{return reads==3&&!context.IsAudioBusy;});
            Check(context.Audio.MicrophoneId==null&&context.Audio.Devices.Length==0&&context.AudioControlsAvailable&&ControlsDisabled(context),"successful fully empty topology clears microphone, choices and control availability");
            next=Audio(Bindings("A"));context.RefreshAudioDevices();Pump(delegate{return reads==4&&!context.IsAudioBusy;});
            next=new AudioReading {Error="Fake read failed",TimestampUtc=DateTime.UtcNow};context.RefreshAudioDevices();Pump(delegate{return reads==5&&!context.IsAudioBusy;});
            Check(Object.ReferenceEquals(context.Audio,next)&&context.Audio.Error=="Fake read failed"&&!context.AudioControlsAvailable&&ControlsDisabled(context),"failed read keeps its explicit error and blocks old controls");
            context.ToggleMicrophoneMute();context.SelectAudioOutput("headset");context.SelectOutputVolume("speaker-A",80);
            Check(writes==0,"failed-state actions cannot issue writes to an old endpoint");
            next=null;context.RefreshAudioDevices();Pump(delegate{return reads==6&&!context.IsAudioBusy;});
            Check(context.Audio!=null&&!String.IsNullOrEmpty(context.Audio.Error)&&!context.AudioControlsAvailable,"missing read result becomes an explicit unavailable state");
            next=Audio(Bindings("B"));context.RefreshAudioDevices();Pump(delegate{return reads==7&&!context.IsAudioBusy;});
            Check(context.Audio.MicrophoneId=="mic-B"&&context.AudioControlsAvailable,"subsequent successful read restores current controls");
        } finally {if(context!=null)context.ExitThread();}
    }
    static void QueuedActionsAreCancelled(){
        DeviceSettings.UseTransientConfiguration(Bindings("A"));
        using(var oldReadEntered=new ManualResetEvent(false))using(var releaseOldRead=new ManualResetEvent(false))
        using(var newReadEntered=new ManualResetEvent(false))using(var releaseNewRead=new ManualResetEvent(false)){
            TrayContext context=null;int reads=0,writes=0;AudioCommand lastWrite=null;
            try {
                context=Context(delegate(AudioCommand command){
                    if(command.Operation!="read"){lastWrite=command;Interlocked.Increment(ref writes);return Audio(command.Settings,command.Mute);}
                    int count=Interlocked.Increment(ref reads);
                    if(count==2){oldReadEntered.Set();Wait(releaseOldRead);return Audio(Bindings("stale"),true);}
                    if(count==3){newReadEntered.Set();Wait(releaseNewRead);}
                    return Audio(command.Settings);
                });
                Pump(delegate{return reads==1&&!context.IsAudioBusy;});var initial=context.Audio;
                context.RefreshAudioDevices();Pump(delegate{return oldReadEntered.WaitOne(0);});
                context.ToggleMicrophoneMute();context.SelectAudioOutput("headset");context.SelectOutputVolume("speaker-A",80);
                DeviceSettings.UseTransientConfiguration(Bindings("B"));context.ConfigurationChanged();
                Check(!context.AudioControlsAvailable&&!context.DesiredVolume.HasValue,"rebinding immediately blocks controls and clears queued volume");
                releaseOldRead.Set();Pump(delegate{return newReadEntered.WaitOne(0);});
                Check(Object.ReferenceEquals(context.Audio,initial)&&!context.AudioControlsAvailable,"old in-flight read cannot replace the snapshot while new bindings are being checked");
                Check(writes==0&&context.GetFeedback().PendingOutput==null&&!context.GetFeedback().MicrophonePending,"rebinding cancels all queued old output, mute and volume operations");
                releaseNewRead.Set();Pump(delegate{return reads==3&&!context.IsAudioBusy;});
                Check(context.Audio.MicrophoneId=="mic-B"&&context.AudioControlsAvailable,"new binding is confirmed before enabling controls");
                context.ToggleMicrophoneMute();Pump(delegate{return writes==1&&!context.IsAudioBusy;});
                Check(lastWrite.Endpoint=="mic-B"&&lastWrite.Settings.MicrophoneEndpointId=="mic-B"&&context.Audio.MicrophoneMuted==true,"next user action uses the newly confirmed microphone");
            } finally {releaseOldRead.Set();releaseNewRead.Set();if(context!=null)context.ExitThread();}
        }
    }
    static void StartedActionKeepsItsTarget(string operation){
        DeviceSettings.UseTransientConfiguration(Bindings("A"));
        using(var broker=new AudioBrokerClient(Assembly.GetExecutingAssembly().Location,2000))
        using(var writeEntered=new ManualResetEvent(false))using(var releaseWrite=new ManualResetEvent(false))
        using(var newReadEntered=new ManualResetEvent(false))using(var releaseNewRead=new ManualResetEvent(false)){
            TrayContext context=null;int reads=0;AudioReading writeResult=null;AudioCommand submitted=null;
            try {
                context=Context(delegate(AudioCommand command){
                    if(command.Operation=="read"){
                        if(Interlocked.Increment(ref reads)==2){newReadEntered.Set();Wait(releaseNewRead);}
                        return Audio(command.Settings);
                    }
                    submitted=command;writeEntered.Set();Wait(releaseWrite);
                    writeResult=broker.Query(command);return writeResult;
                });
                Pump(delegate{return reads==1&&!context.IsAudioBusy;});var initial=context.Audio;
                if(operation=="mute")context.ToggleMicrophoneMute();else if(operation=="output")context.SelectAudioOutput("headset");else context.SelectOutputVolume("speaker-A",75);
                Pump(delegate{return writeEntered.WaitOne(0);});
                DeviceSettings.UseTransientConfiguration(Bindings("B"));context.ConfigurationChanged();releaseWrite.Set();
                Pump(delegate{return newReadEntered.WaitOne(0);});
                string expected=operation=="mute"?"mic-A":operation=="output"?"headset-A":"speaker-A";
                Check(submitted.Endpoint==expected&&submitted.Settings.MicrophoneEndpointId=="mic-A"&&writeResult.Endpoints[0]==expected&&writeResult.MicrophoneId=="mic-A",operation+" already on a worker keeps its submitted target when the broker starts after rebinding");
                Check(Object.ReferenceEquals(context.Audio,initial)&&!context.AudioControlsAvailable,operation+" old readback cannot overwrite the new binding generation");
                releaseNewRead.Set();Pump(delegate{return reads==2&&!context.IsAudioBusy;});
                Check(context.Audio.MicrophoneId=="mic-B"&&context.AudioControlsAvailable,operation+" reconciles the new generation after completing the original operation");
            } finally {releaseWrite.Set();releaseNewRead.Set();if(context!=null)context.ExitThread();}
        }
    }
    static void BrokerCapturesBeforeWaiting(bool suppliedSettings){
        DeviceSettings.UseTransientConfiguration(Bindings("A"));
        using(var broker=new AudioBrokerClient(Assembly.GetExecutingAssembly().Location,2000)){
            var command=new AudioCommand {Operation="mute",Endpoint="mic-A",Mute=true,Settings=suppliedSettings?Bindings("A"):null};
            object gate=typeof(AudioBrokerClient).GetField("gate",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(broker);
            AudioReading result=null;Exception failure=null;
            var worker=new Thread(delegate(){try{result=broker.Query(command);}catch(Exception error){failure=error;}});
            worker.IsBackground=true;
            Monitor.Enter(gate);
            try {
                worker.Start();Pump(delegate{return (worker.ThreadState&ThreadState.WaitSleepJoin)!=0;});
                DeviceSettings.UseTransientConfiguration(Bindings("B"));command.Endpoint="mic-B";
                if(command.Settings!=null)command.Settings.MicrophoneEndpointId="mic-B";
            } finally {Monitor.Exit(gate);}
            Pump(delegate{return worker.Join(0);});
            Check(failure==null&&result!=null&&result.MicrophoneId=="mic-A"&&result.Endpoints[0]=="mic-A",suppliedSettings?"broker copies supplied command/settings before waiting for its lock":"broker captures current bindings before waiting for its lock or starting its child");
        }
    }
    static void DeviceScopesStayIndependent(){
        const string first="device-000000000000000000000001",second="device-000000000000000000000002",unbound="device-000000000000000000000003";
        var global=Bindings("global");
        global.AudioDeviceBindings[first]=new AudioEndpointBindings {SpeakerEndpointId="speaker-first",HeadsetEndpointId="headset-first",MicrophoneEndpointId="mic-first"};
        global.AudioDeviceBindings[second]=new AudioEndpointBindings {SpeakerEndpointId="speaker-second",HeadsetEndpointId="headset-second",MicrophoneEndpointId="mic-second"};
        DeviceSettings.UseTransientConfiguration(global);TrayContext context=null;int reads=0,writes=0;AudioCommand lastWrite=null;
        try {
            context=Context(delegate(AudioCommand command){
                if(command.Operation=="read")Interlocked.Increment(ref reads);else{lastWrite=command;Interlocked.Increment(ref writes);}
                return Audio(command.Settings,command.Mute);
            });
            Pump(delegate{return reads==1&&!context.IsAudioBusy;});
            context.SetAudioScope(first);Check(!context.AudioControlsAvailable,"entering a device scope blocks controls until its own read completes");
            Pump(delegate{return reads==2&&!context.IsAudioBusy;});
            Check(context.AudioScopeDeviceId==first&&context.AudioScopeSettings.ExplicitAudioBindingsOnly&&context.Audio.HeadsetId=="headset-first"&&context.Audio.MicrophoneId=="mic-first","first stable device ID resolves its own explicit output and microphone");
            context.ToggleMicrophoneMute();Pump(delegate{return writes==1&&!context.IsAudioBusy;});
            Check(lastWrite.Endpoint=="mic-first"&&lastWrite.Settings.ExplicitAudioBindingsOnly&&lastWrite.Settings.HeadsetEndpointId=="headset-first","scoped write carries the device binding snapshot through the controller");
            context.SetAudioScope(second);Pump(delegate{return reads==3&&!context.IsAudioBusy;});
            Check(context.Audio.HeadsetId=="headset-second"&&context.Audio.MicrophoneId=="mic-second","changing device scope does not reuse the first device's bindings");
            context.SetAudioScope(unbound);Pump(delegate{return reads==4&&!context.IsAudioBusy;});
            Check(context.AudioScopeSettings.ExplicitAudioBindingsOnly&&context.Audio.HeadsetId==null&&context.Audio.MicrophoneId==null&&ControlsDisabled(context),"unmapped generic headset has no automatic global endpoint fallback");
            context.ToggleMicrophoneMute();context.SelectAudioOutput("headset");context.SelectOutputVolume("speaker-global",80);
            Check(writes==1,"unmapped scope cannot write to a global microphone or output");
            context.SetAudioScope(null);Pump(delegate{return reads==5&&!context.IsAudioBusy;});
            Check(context.AudioScopeDeviceId==null&&!context.AudioScopeSettings.ExplicitAudioBindingsOnly&&context.Audio.HeadsetId=="headset-global"&&context.Audio.MicrophoneId=="mic-global","leaving the device audio pages restores the original global scope");
            Check(DeviceSettings.Current.HeadsetEndpointId=="headset-global"&&DeviceSettings.Current.AudioDeviceBindings[first].HeadsetEndpointId=="headset-first","scope reads and writes preserve global and other device configuration");
        } finally {if(context!=null)context.ExitThread();}
    }
    static void FakeBroker(){
        using(var input=new StreamReader(Console.OpenStandardInput(),Encoding.UTF8))
        using(var output=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false)) {AutoFlush=true}){
            string line;while((line=input.ReadLine())!=null){
                var command=AudioBrokerClient.Decode<AudioCommand>(line);var result=Audio(command.Settings,command.Operation=="mute"&&command.Mute);
                result.Endpoints=new[]{command.Endpoint};
                output.WriteLine(AudioBrokerClient.Encode(new AudioResponse {Id=command.Id,Reading=result}));
            }
        }
    }
    [STAThread] static int Main(string[] args){
        if(args.Length==1&&args[0]=="--audio-broker"){FakeBroker();return 0;}
        var current=typeof(DeviceSettings).GetField("current",BindingFlags.NonPublic|BindingFlags.Static);object previous=current.GetValue(null);
        try {
            EmptyAndFailedSnapshots();QueuedActionsAreCancelled();
            foreach(string operation in new[]{"mute","output","volume"})StartedActionKeepsItsTarget(operation);
            BrokerCapturesBeforeWaiting(false);BrokerCapturesBeforeWaiting(true);
            DeviceScopesStayIndependent();
            Console.WriteLine("PASS "+checks+" audio snapshot/target-generation regression checks; no live hardware/audio/settings accessed");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally {current.SetValue(null,previous);}
    }
}
