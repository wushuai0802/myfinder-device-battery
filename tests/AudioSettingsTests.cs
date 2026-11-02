using System;
using System.Collections.Generic;
using System.IO;
using AtkBattery;
using AtkBatteryTray;

static class AudioSettingsTests {
    static int passed;
    static void Check(bool value,string name){if(!value)throw new Exception("FAIL "+name);passed++;}
    static void Reject(Action operation,string name){bool rejected=false;try{operation();}catch{rejected=true;}Check(rejected,name);}
    static AudioEndpointInfo Output(string id,int? form,int flow=0){return new AudioEndpointInfo {Id=id,Flow=flow,FormFactor=form,Name="Unrelated name",InterfaceName="Other vendor"};}
    static void SpeakerFallbacks(){
        bool ambiguous;var automatic=new AudioBindings();
        var speaker=Output("usb",1);var headphones=Output("headphones",3);var hdmi=Output("hdmi",9);var spdif=Output("spdif",8);
        var endpoints=new List<AudioEndpointInfo> {speaker,headphones};
        Check(AudioControl.FindEndpoint(endpoints,"speaker",out ambiguous)==speaker&&!ambiguous,"a unique non-Realtek speaker resolves without a binding");
        endpoints.Add(hdmi);endpoints.Add(spdif);endpoints.Add(Output("second-speaker",1));
        Check(AudioControl.FindEndpoint(endpoints,"speaker",automatic,"HDMI","usb",out ambiguous)==hdmi&&!ambiguous,"current known non-headphone default wins over memory and multiple speakers");
        Check(AudioControl.FindEndpoint(endpoints,"speaker",automatic,"headphones","hdmi",out ambiguous)==hdmi&&!ambiguous,"headphone default returns to the remembered HDMI output");
        Check(AudioControl.FindEndpoint(endpoints,"speaker",automatic,"headphones",null,out ambiguous)==null&&ambiguous,"multiple speakers without a usable default or memory are ambiguous");
        endpoints.RemoveAt(4);
        Check(AudioControl.FindEndpoint(endpoints,"speaker",automatic,"headphones","unplugged",out ambiguous)==speaker&&!ambiguous,"unplugged memory falls back to the unique speaker before other output types");
        endpoints.Remove(speaker);endpoints.Remove(spdif);
        Check(AudioControl.FindEndpoint(endpoints,"speaker",automatic,"headphones","unplugged",out ambiguous)==hdmi&&!ambiguous,"unplugged memory falls back to the unique known other output");
        endpoints.Add(spdif);
        Check(AudioControl.FindEndpoint(endpoints,"speaker",automatic,"headphones",null,out ambiguous)==null&&ambiguous,"multiple other outputs without a speaker are ambiguous");
        foreach(int form in new[]{0,2,4,6,7,8,9}){
            var known=Output("known",form);endpoints=new List<AudioEndpointInfo> {headphones,known,Output("capture",1,1)};
            Check(AudioControl.FindEndpoint(endpoints,"speaker",automatic,"headphones",null,out ambiguous)==known&&!ambiguous,"known render form factor "+form+" is eligible without name guessing");
        }
        foreach(int? form in new int?[]{null,-1,10,11,99}){
            var unknown=Output("unknown",form);unknown.Name="Speakers";
            endpoints=new List<AudioEndpointInfo> {headphones,Output("headset",5),unknown,Output("capture",1,1)};
            Check(AudioControl.FindEndpoint(endpoints,"speaker",automatic,"unknown","unknown",out ambiguous)==null&&!ambiguous,"unknown form factor "+form+", headphones and capture endpoints never qualify");
            endpoints.Add(speaker);
            Check(AudioControl.FindEndpoint(endpoints,"speaker",automatic,"unknown","unknown",out ambiguous)==speaker&&!ambiguous,"unknown endpoints do not make a unique speaker ambiguous");
        }
        var realtek=Output("realtek",1);realtek.InterfaceName="Realtek(R) Audio";
        endpoints=new List<AudioEndpointInfo> {speaker,headphones,hdmi,spdif,realtek};
        Check(AudioControl.FindEndpoint(endpoints,"speaker",automatic,"hdmi","spdif",out ambiguous)==realtek&&!ambiguous,"existing Realtek rule keeps priority over the generic fallback");
        var explicitBinding=new AudioBindings {SpeakerEndpointId="headphones"};
        Check(AudioControl.FindEndpoint(endpoints,"speaker",explicitBinding,"hdmi","usb",out ambiguous)==headphones&&!ambiguous,"explicit bindings retain the user-selected output regardless of endpoint type");
        explicitBinding.SpeakerEndpointId="unplugged";
        Check(AudioControl.FindEndpoint(endpoints,"speaker",explicitBinding,"hdmi","usb",out ambiguous)==null&&!ambiguous,"an absent explicit speaker binding cannot fall through to automatic selection");
        endpoints.Remove(realtek);
        var memory=new SpeakerEndpointMemory();
        Check(memory.FindSpeaker(endpoints,automatic,"hdmi",out ambiguous)==hdmi,"a global known default seeds session memory");
        var scoped=new AudioBindings {ExplicitAudioBindingsOnly=true};
        Check(memory.FindSpeaker(endpoints,scoped,"spdif",out ambiguous)==null&&!ambiguous,"device-only scopes do not select an automatic speaker");
        scoped.SpeakerEndpointId="spdif";
        Check(memory.FindSpeaker(endpoints,scoped,"spdif",out ambiguous)==spdif,"device-only scopes keep explicit bindings");
        Check(memory.FindSpeaker(endpoints,automatic,"headphones",out ambiguous)==hdmi,"device-only reads and explicit device bindings do not overwrite global session memory");
        Check(new SpeakerEndpointMemory().FindSpeaker(endpoints,automatic,"headphones",out ambiguous)==speaker,"session memory is isolated from a fresh resolver");
        Check(memory.FindSpeaker(endpoints,automatic,"spdif",out ambiguous)==spdif&&memory.FindSpeaker(endpoints,automatic,"headphones",out ambiguous)==spdif,"the most recently observed eligible default replaces older memory");
        endpoints.Remove(spdif);
        Check(memory.FindSpeaker(endpoints,automatic,"headphones",out ambiguous)==speaker,"removed remembered outputs are revalidated against the current inventory");
        Check(automatic.SpeakerEndpointId==null&&automatic.HeadsetEndpointId==null,"automatic selection leaves configuration bindings untouched");
        int errors=0;var threads=new System.Threading.Thread[6];
        memory.FindSpeaker(endpoints,automatic,"hdmi",out ambiguous);
        for(int i=0;i<threads.Length;i++){
            threads[i]=new System.Threading.Thread(delegate(){
                for(int j=0;j<100;j++){
                    bool multiple;
                    if(memory.FindSpeaker(endpoints,scoped,"usb",out multiple)!=null||multiple||memory.FindSpeaker(endpoints,automatic,"headphones",out multiple)!=hdmi||multiple)System.Threading.Interlocked.Increment(ref errors);
                }
            });threads[i].Start();
        }
        foreach(var thread in threads)Check(thread.Join(5000),"concurrent fake endpoint resolver completes");
        Check(errors==0,"concurrent scoped reads cannot overwrite remembered global output");
    }
    static AudioBindings HeadsetBindings(AudioEndpointInfo receiver,bool hyperx){
        if(hyperx){receiver.InterfaceName="HyperX Cloud Alpha Wireless";return new AudioBindings();}
        return new AudioBindings {HeadsetEndpointId=receiver.Id.ToUpperInvariant()};
    }
    static void SpeakerExcludesHeadset(){
        foreach(bool hyperx in new[]{false,true}){
            bool ambiguous;var receiver=Output("receiver",1);var realSpeaker=Output("speaker",1);
            var bindings=HeadsetBindings(receiver,hyperx);var endpoints=new List<AudioEndpointInfo> {receiver,realSpeaker};
            var headset=AudioControl.FindEndpoint(endpoints,"headset",bindings,out ambiguous);
            Check(headset==receiver&&!ambiguous,"speaker-form-factor receiver resolves as the bound or HyperX headset");
            var speaker=new SpeakerEndpointMemory().FindSpeaker(endpoints,bindings,"RECEIVER",out ambiguous);
            Check(speaker==realSpeaker&&!ambiguous&&speaker.Id!=headset.Id,"default speaker-form-factor headset cannot also become the automatic speaker");
        }
    }
    static void HeadsetPreservesSpeakerMemory(){
        foreach(bool hyperx in new[]{false,true}){
            bool ambiguous;var receiver=Output("receiver",1);var hdmi=Output("hdmi",9);
            var bindings=HeadsetBindings(receiver,hyperx);var endpoints=new List<AudioEndpointInfo> {receiver,Output("speaker",1),hdmi,Output("spdif",8)};
            var memory=new SpeakerEndpointMemory();
            Check(memory.FindSpeaker(endpoints,bindings,"HDMI",out ambiguous)==hdmi&&!ambiguous,"a real HDMI default seeds memory before switching to the headset");
            Check(memory.FindSpeaker(endpoints,bindings,"RECEIVER",out ambiguous)==hdmi&&!ambiguous,"switching to the speaker-form-factor headset retains the remembered HDMI target");
            Check(memory.FindSpeaker(endpoints,bindings,null,out ambiguous)==hdmi&&!ambiguous,"the headset default does not overwrite HDMI memory");
        }
    }
    static void HeadsetOnlyHasNoSpeaker(){
        foreach(bool hyperx in new[]{false,true}){
            bool ambiguous;var receiver=Output("receiver",1);var bindings=HeadsetBindings(receiver,hyperx);
            var endpoints=new List<AudioEndpointInfo> {receiver};
            var headset=AudioControl.FindEndpoint(endpoints,"headset",bindings,out ambiguous);
            Check(headset==receiver&&!ambiguous,"the only output remains available as the headset");
            Check(new SpeakerEndpointMemory().FindSpeaker(endpoints,bindings,"RECEIVER",out ambiguous)==null&&!ambiguous,"a lone speaker-form-factor headset leaves the speaker unavailable");
        }
    }
    static void HeadsetSpeakerFallbackRules(){
        bool ambiguous;var receiver=Output("receiver",1);var realSpeaker=Output("speaker",1);var hdmi=Output("hdmi",9);
        var endpoints=new List<AudioEndpointInfo> {receiver,realSpeaker,hdmi};var bindings=HeadsetBindings(receiver,false);
        Check(AudioControl.FindEndpoint(endpoints,"speaker",bindings,null,"RECEIVER",out ambiguous)==realSpeaker&&!ambiguous,"headset memory is skipped and the headset is excluded from unique-speaker counting");
        var memory=new SpeakerEndpointMemory();
        Check(memory.FindSpeaker(endpoints,new AudioBindings(),"receiver",out ambiguous)==receiver,"an output can be remembered before it is assigned to the headset button");
        Check(memory.FindSpeaker(endpoints,bindings,null,out ambiguous)==realSpeaker&&!ambiguous,"new headset bindings invalidate a previously remembered speaker target");
        endpoints.Remove(realSpeaker);receiver.FormFactor=9;
        Check(AudioControl.FindEndpoint(endpoints,"speaker",bindings,null,"RECEIVER",out ambiguous)==hdmi&&!ambiguous,"the bound headset is excluded from unique-other-output counting");
        endpoints.Add(Output("spdif",8));
        Check(AudioControl.FindEndpoint(endpoints,"speaker",bindings,null,"RECEIVER",out ambiguous)==null&&ambiguous,"multiple remaining other outputs stay ambiguous");
        endpoints.Add(realSpeaker);endpoints.Add(Output("second-speaker",1));receiver.FormFactor=1;
        Check(AudioControl.FindEndpoint(endpoints,"speaker",bindings,null,"RECEIVER",out ambiguous)==null&&ambiguous,"multiple remaining speakers stay ambiguous");
        bindings.SpeakerEndpointId="receiver";
        Check(memory.FindSpeaker(endpoints,bindings,"receiver",out ambiguous)==receiver&&!ambiguous,"explicit speaker bindings retain priority even when shared with the headset");
        bindings.SpeakerEndpointId=null;receiver.InterfaceName="Realtek(R) Audio";
        Check(memory.FindSpeaker(endpoints,bindings,"receiver",out ambiguous)==receiver&&!ambiguous,"the legacy Realtek rule retains priority even when bound as the headset");
        bindings.ExplicitAudioBindingsOnly=true;
        Check(memory.FindSpeaker(endpoints,bindings,"receiver",out ambiguous)==null&&!ambiguous,"device-only scopes still require an explicit speaker binding");
        bindings.SpeakerEndpointId="receiver";
        Check(memory.FindSpeaker(endpoints,bindings,"receiver",out ambiguous)==receiver&&!ambiguous,"device-only scopes preserve explicit shared endpoint bindings");
    }
    static int Main(string[] args) {
        try {
            SpeakerFallbacks();
            SpeakerExcludesHeadset();
            HeadsetPreservesSpeakerMemory();
            HeadsetOnlyHasNoSpeaker();
            HeadsetSpeakerFallbackRules();
            string root=Path.Combine(Path.GetFullPath(args[0]),"audio-settings-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string path=Path.Combine(root,"audio-settings.json");
            var defaults=DeviceSettings.LoadBindings(path);
            Check(defaults.EndpointFor("speaker")==null&&defaults.EndpointFor("headset")==null&&defaults.EndpointFor("microphone")==null,"missing file uses automatic bindings");
            Check(defaults.TraySource=="active"&&!defaults.LowBatteryNotifications,"new installations follow active USB mice with automatic battery fallback");
            Check(defaults.Theme=="auto"&&defaults.RememberReadings&&defaults.WarningThreshold==20&&defaults.BackgroundSeconds==60,"display preferences have conservative defaults");
            var settings=new AudioBindings();
            settings.SetEndpoint("speaker","{render}.USB-\u626c\u58f0\u5668");
            settings.SetEndpoint("headset","{render}.HDMI");
            settings.SetEndpoint("microphone","{capture}.USB");
            settings.TraySource="lowest";settings.LowBatteryNotifications=true;
            DeviceSettings.SaveBindings(path,settings);
            var loaded=DeviceSettings.LoadBindings(path);
            Check(loaded.TraySource=="lowest"&&loaded.LowBatteryNotifications,"tray and notification preferences roundtrip with audio bindings");
            foreach(string role in new[]{"speaker","headset","microphone"})Check(loaded.EndpointFor(role)==settings.EndpointFor(role),"roundtrip "+role);
            var mapped=loaded.Clone();const string mappedId="device-000000000000000000000001";
            mapped.AudioDeviceBindings[mappedId]=new AudioEndpointBindings {HeadsetEndpointId="mapped-render",MicrophoneEndpointId="mapped-capture"};
            DeviceSettings.SaveBindings(path,mapped);var mappedLoaded=DeviceSettings.LoadBindings(path);
            Check(mappedLoaded.HeadsetEndpointId==loaded.HeadsetEndpointId&&mappedLoaded.AudioDeviceBindings[mappedId].HeadsetEndpointId=="mapped-render"&&mappedLoaded.AudioDeviceBindings[mappedId].MicrophoneEndpointId=="mapped-capture","stable-device audio mapping roundtrips without replacing global endpoints");
            var mappedClone=mappedLoaded.Clone();mappedClone.AudioDeviceBindings[mappedId].HeadsetEndpointId="changed";
            Check(mappedLoaded.AudioDeviceBindings[mappedId].HeadsetEndpointId=="mapped-render","editable device mapping copies cannot mutate saved binding objects");
            var clone=loaded.Clone();clone.SetEndpoint("speaker",null);
            Check(loaded.SpeakerEndpointId!=null,"editing a copy does not change saved bindings");
            DeviceSettings.SaveBindings(path,clone);
            loaded=DeviceSettings.LoadBindings(path);
            Check(loaded.SpeakerEndpointId==null&&loaded.HeadsetEndpointId==settings.HeadsetEndpointId,"reset one role preserves the other roles");
            var endpoints=new List<AudioEndpointInfo> {
                new AudioEndpointInfo {Id="realtek",Name="Speakers",InterfaceName="Realtek(R) Audio",Flow=0,FormFactor=1},
                new AudioEndpointInfo {Id="usb",Name="USB DAC",InterfaceName="USB Audio",Flow=0,FormFactor=1},
                new AudioEndpointInfo {Id="hyperx",Name="Headphones",InterfaceName="HyperX Cloud Alpha Wireless",Flow=0},
                new AudioEndpointInfo {Id="mic",Name="USB Mic",InterfaceName="USB Audio",Flow=1}
            };
            bool ambiguous;
            Check(AudioControl.FindEndpoint(endpoints,"speaker",out ambiguous).Id=="realtek"&&!ambiguous,"default speaker detection is preserved");
            Check(AudioControl.FindEndpoint(endpoints,"headset",out ambiguous).Id=="hyperx"&&!ambiguous,"default headset detection is preserved");
            var namedOutput=new AudioReading {ConsoleOutputId="speaker",SpeakerId="speaker",SpeakerName="Fallback speaker",HeadsetId="headset",HeadsetName="Fallback headset"};
            Check(UiState.CurrentOutputName(namedOutput)=="Fallback speaker"&&UiState.EndpointName(namedOutput,"speaker")=="设备未连接","current output retains its known speaker name while absent binding-list entries keep their original qualifier");
            namedOutput.Devices=new[]{new AudioDeviceChoice {Id="speaker",Name="Enumerated speaker",Flow=0}};
            bool listPreferred=UiState.CurrentOutputName(namedOutput)=="Enumerated speaker";namedOutput.ConsoleOutputId="headset";
            Check(listPreferred&&UiState.CurrentOutputName(namedOutput)=="Fallback headset","current output prefers device-list names and falls back to the matching headset name");
            namedOutput.ConsoleOutputId="other-live-output";bool unknownNamed=UiState.CurrentOutputName(namedOutput)=="当前系统输出（名称未确认）";namedOutput.ConsoleOutputId=null;
            Check(unknownNamed&&UiState.CurrentOutputName(namedOutput)=="未检测到系统输出","an existing unnamed current endpoint is distinct from having no system output");
            var manual=new AudioBindings {SpeakerEndpointId="USB",HeadsetEndpointId="realtek",MicrophoneEndpointId="mic"};
            Check(AudioControl.FindEndpoint(endpoints,"speaker",manual,out ambiguous).Id=="usb","explicit output ID overrides vendor detection, case-insensitively");
            Check(AudioControl.FindEndpoint(endpoints,"headset",manual,out ambiguous).Id=="realtek","headset button accepts a user-selected output");
            Check(AudioControl.FindEndpoint(endpoints,"microphone",manual,out ambiguous).Id=="mic","microphone uses the selected recording endpoint");
            var explicitOnly=new AudioBindings {ExplicitAudioBindingsOnly=true};
            Check(AudioControl.FindEndpoint(endpoints,"speaker",explicitOnly,out ambiguous)==null&&AudioControl.FindEndpoint(endpoints,"headset",explicitOnly,out ambiguous)==null&&AudioControl.FindEndpoint(endpoints,"microphone",explicitOnly,out ambiguous)==null,"unmapped device scopes never inherit global vendor detection");
            explicitOnly.HeadsetEndpointId="usb";explicitOnly.MicrophoneEndpointId="mic";
            Check(AudioControl.FindEndpoint(endpoints,"headset",explicitOnly,out ambiguous).Id=="usb"&&AudioControl.FindEndpoint(endpoints,"microphone",explicitOnly,out ambiguous).Id=="mic","explicit device scopes resolve only their selected endpoints");
            manual.SpeakerEndpointId="unplugged";
            Check(AudioControl.FindEndpoint(endpoints,"speaker",manual,out ambiguous)==null,"missing explicit output never falls back to a different device");
            manual.SpeakerEndpointId="mic";
            Check(AudioControl.FindEndpoint(endpoints,"speaker",manual,out ambiguous)==null,"a recording endpoint cannot become an output");
            manual.MicrophoneEndpointId="usb";
            Check(AudioControl.FindEndpoint(endpoints,"microphone",manual,out ambiguous)==null,"an output cannot become a microphone");
            manual.SpeakerEndpointId="usb";
            endpoints.Add(new AudioEndpointInfo {Id="usb",Name="Duplicate",Flow=0});
            Check(AudioControl.FindEndpoint(endpoints,"speaker",manual,out ambiguous)==null&&ambiguous,"duplicate explicit IDs are rejected");
            Check(AudioControl.FindEndpoint(endpoints,"unknown",manual,out ambiguous)==null,"unknown roles are rejected");
            Reject(delegate {manual.SetEndpoint("other","usb");},"unknown setting role is rejected");
            Reject(delegate {manual.SetEndpoint("speaker",new string('x',4097));},"oversized endpoint IDs are rejected");
            var old=File.ReadAllBytes(path);
            using(var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None)) {
                Reject(delegate {DeviceSettings.SaveBindings(path,new AudioBindings());},"failed atomic replace is surfaced");
            }
            Check(Convert.ToBase64String(old)==Convert.ToBase64String(File.ReadAllBytes(path)),"failed save preserves the old settings");
            Check(Directory.GetFiles(root,"*.tmp").Length==0,"temporary settings are cleaned up after failure");
            File.WriteAllText(path,"{\"SchemaVersion\":2}");
            Reject(delegate {DeviceSettings.LoadBindings(path);},"unknown settings schema is rejected");
            File.WriteAllText(path,"{broken");
            Reject(delegate {DeviceSettings.LoadBindings(path);},"invalid JSON is rejected");
            File.WriteAllText(path,"{\"SchemaVersion\":1,\"SpeakerEndpointId\":\"legacy\"}");
            loaded=DeviceSettings.LoadBindings(path);
            Check(loaded.TraySource=="mouse"&&!loaded.LowBatteryNotifications&&loaded.SpeakerEndpointId=="legacy","old settings migrate without losing endpoint bindings");
            Check(loaded.Theme=="auto"&&loaded.RememberReadings&&loaded.WarningThreshold==20&&loaded.MouseAlias=="ATK A9 Air+","missing new properties migrate without losing defaults");
            loaded.Theme="dark";loaded.RememberReadings=false;loaded.ReducedMotion=true;loaded.WarningThreshold=15;loaded.BackgroundSeconds=120;loaded.MouseAlias="办公鼠标";
            DeviceSettings.SaveBindings(path,loaded);loaded=DeviceSettings.LoadBindings(path);
            Check(loaded.Theme=="dark"&&!loaded.RememberReadings&&loaded.ReducedMotion&&loaded.WarningThreshold==15&&loaded.BackgroundSeconds==120&&loaded.MouseAlias=="办公鼠标","new preferences roundtrip including explicit false");
            loaded.TrayDeviceId="device-000000000000000000000001";DeviceSettings.SaveBindings(path,loaded);loaded=DeviceSettings.LoadBindings(path);
            Check(loaded.TrayDeviceId=="device-000000000000000000000001"&&loaded.Clone().TrayDeviceId==loaded.TrayDeviceId,"a fixed tray device survives save and editable cloning");
            var invalidPin=loaded.Clone();invalidPin.TrayDeviceId="device\ninvalid";Reject(delegate{DeviceSettings.SaveBindings(path,invalidPin);},"tray identifiers cannot contain control characters");
            invalidPin=loaded.Clone();invalidPin.TrayDeviceId=new string('x',65);Reject(delegate{DeviceSettings.SaveBindings(path,invalidPin);},"oversized tray identifiers cannot overwrite valid settings");
            var invalid=loaded.Clone();invalid.WarningThreshold=0;Reject(delegate{DeviceSettings.SaveBindings(path,invalid);},"invalid warning threshold cannot overwrite settings");
            invalid=loaded.Clone();invalid.MouseAlias="bad\nname";Reject(delegate{DeviceSettings.SaveBindings(path,invalid);},"alias cannot inject control characters");
            string cache=Path.Combine(root,"readings-cache.json");var confirmed=DateTime.UtcNow.AddMinutes(-3);
            ReadingCache.Save(cache,new BatteryReading {Percent=37,TimestampUtc=confirmed},new HeadsetReading {Percent=48,BatteryTimestampUtc=confirmed});
            BatteryReading savedMouse;HeadsetReading savedHeadset;ReadingCache.Load(cache,out savedMouse,out savedHeadset);
            Check(savedMouse!=null&&savedMouse.Percent==37&&Math.Abs((savedMouse.TimestampUtc-confirmed).TotalMilliseconds)<1,"cached mouse retains its original confirmation time");
            Check(savedHeadset!=null&&savedHeadset.Percent==48&&!savedHeadset.ConnectionVerified&&!savedHeadset.ReceiverPresent,"saved battery cannot claim a live headset radio connection");
            ReadingCache.Save(cache,new BatteryReading {Percent=100,TimestampUtc=DateTime.UtcNow.AddHours(1)},null);ReadingCache.Load(cache,out savedMouse,out savedHeadset);
            Check(savedMouse==null&&savedHeadset==null,"future-dated readings are not trusted");
            File.WriteAllText(cache,"{broken");ReadingCache.Load(cache,out savedMouse,out savedHeadset);Check(savedMouse==null&&savedHeadset==null,"corrupt cache does not prevent startup");
            loaded.TraySource="unknown";Reject(delegate {DeviceSettings.SaveBindings(path,loaded);},"invalid tray mode is rejected before saving");
            Console.WriteLine("PASS "+passed+" audio/settings tests; no audio endpoint opened or changed");
            return 0;
        } catch(Exception error){Console.Error.WriteLine(error.ToString());return 1;}
    }
}
