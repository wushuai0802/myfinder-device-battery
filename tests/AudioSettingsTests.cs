using System;
using System.Collections.Generic;
using System.IO;
using AtkBattery;

static class AudioSettingsTests {
    static int passed;
    static void Check(bool value,string name){if(!value)throw new Exception("FAIL "+name);passed++;}
    static void Reject(Action operation,string name){bool rejected=false;try{operation();}catch{rejected=true;}Check(rejected,name);}
    static int Main(string[] args) {
        try {
            string root=Path.Combine(Path.GetFullPath(args[0]),"audio-settings-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string path=Path.Combine(root,"audio-settings.json");
            var defaults=DeviceSettings.LoadBindings(path);
            Check(defaults.EndpointFor("speaker")==null&&defaults.EndpointFor("headset")==null&&defaults.EndpointFor("microphone")==null,"missing file uses automatic bindings");
            Check(defaults.TraySource=="mouse"&&!defaults.LowBatteryNotifications,"new optional features do not change existing defaults");
            var settings=new AudioBindings();
            settings.SetEndpoint("speaker","{render}.USB-\u626c\u58f0\u5668");
            settings.SetEndpoint("headset","{render}.HDMI");
            settings.SetEndpoint("microphone","{capture}.USB");
            settings.TraySource="lowest";settings.LowBatteryNotifications=true;
            DeviceSettings.SaveBindings(path,settings);
            var loaded=DeviceSettings.LoadBindings(path);
            Check(loaded.TraySource=="lowest"&&loaded.LowBatteryNotifications,"tray and notification preferences roundtrip with audio bindings");
            foreach(string role in new[]{"speaker","headset","microphone"})Check(loaded.EndpointFor(role)==settings.EndpointFor(role),"roundtrip "+role);
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
            var manual=new AudioBindings {SpeakerEndpointId="USB",HeadsetEndpointId="realtek",MicrophoneEndpointId="mic"};
            Check(AudioControl.FindEndpoint(endpoints,"speaker",manual,out ambiguous).Id=="usb","explicit output ID overrides vendor detection, case-insensitively");
            Check(AudioControl.FindEndpoint(endpoints,"headset",manual,out ambiguous).Id=="realtek","headset button accepts a user-selected output");
            Check(AudioControl.FindEndpoint(endpoints,"microphone",manual,out ambiguous).Id=="mic","microphone uses the selected recording endpoint");
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
            loaded.TraySource="unknown";Reject(delegate {DeviceSettings.SaveBindings(path,loaded);},"invalid tray mode is rejected before saving");
            Console.WriteLine("PASS "+passed+" audio/settings tests; no audio endpoint opened or changed");
            return 0;
        } catch(Exception error){Console.Error.WriteLine(error.ToString());return 1;}
    }
}
