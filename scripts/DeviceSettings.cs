using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace AtkBattery {
    [DataContract]
    public sealed class AudioBindings {
        [DataMember(Order=0)] public int SchemaVersion = 1;
        [DataMember(Order=1, EmitDefaultValue=false)] public string SpeakerEndpointId;
        [DataMember(Order=2, EmitDefaultValue=false)] public string HeadsetEndpointId;
        [DataMember(Order=3, EmitDefaultValue=false)] public string MicrophoneEndpointId;
        [DataMember(Order=4, EmitDefaultValue=false)] public string TraySource = "mouse";
        [DataMember(Order=5, EmitDefaultValue=false)] public bool LowBatteryNotifications;

        public AudioBindings Clone() {
            return new AudioBindings { SchemaVersion=SchemaVersion,
                SpeakerEndpointId=SpeakerEndpointId, HeadsetEndpointId=HeadsetEndpointId,
                MicrophoneEndpointId=MicrophoneEndpointId, TraySource=TraySource,
                LowBatteryNotifications=LowBatteryNotifications };
        }
        public string EndpointFor(string role) {
            if(role=="speaker")return SpeakerEndpointId;
            if(role=="headset")return HeadsetEndpointId;
            if(role=="microphone")return MicrophoneEndpointId;
            throw new ArgumentException("Unknown audio role", "role");
        }
        public void SetEndpoint(string role,string id) {
            id=String.IsNullOrWhiteSpace(id)?null:id;
            if(id!=null&&id.Length>4096)throw new ArgumentException("Audio endpoint ID is too long", "id");
            if(role=="speaker")SpeakerEndpointId=id;
            else if(role=="headset")HeadsetEndpointId=id;
            else if(role=="microphone")MicrophoneEndpointId=id;
            else throw new ArgumentException("Unknown audio role", "role");
        }
    }

    public static class DeviceSettings {
        static readonly object Gate=new object();
        static AudioBindings current;
        public static string DataDirectory {
            get {return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MyFinderDeviceBattery");}
        }
        public static string SettingsPath {get{return Path.Combine(DataDirectory,"audio-settings.json");}}
        public static AudioBindings Current {
            get {lock(Gate){if(current==null)current=LoadBindings(SettingsPath);return current.Clone();}}
        }
        public static void SetEndpoint(string role,string id) {
            lock(Gate) {
                var next=Current;next.SetEndpoint(role,id);
                SaveBindings(SettingsPath,next);current=next;
            }
        }
        public static void ResetAudio() {
            lock(Gate){var next=Current;next.SpeakerEndpointId=null;next.HeadsetEndpointId=null;
                next.MicrophoneEndpointId=null;SaveBindings(SettingsPath,next);current=next;}
        }
        public static void SaveConfiguration(AudioBindings settings) {
            lock(Gate){var next=settings.Clone();Validate(next);SaveBindings(SettingsPath,next);current=next;}
        }
        static void Validate(AudioBindings settings) {
            if(settings==null||settings.SchemaVersion!=1)throw new InvalidDataException("Unsupported audio settings format");
            foreach(string role in new[]{"speaker","headset","microphone"})settings.SetEndpoint(role,settings.EndpointFor(role));
            if(String.IsNullOrEmpty(settings.TraySource))settings.TraySource="mouse";
            if(settings.TraySource!="mouse"&&settings.TraySource!="headset"&&settings.TraySource!="lowest")
                throw new InvalidDataException("Unknown tray battery source");
        }
        internal static AudioBindings LoadBindings(string path) {
            if(!File.Exists(path))return new AudioBindings();
            var info=new FileInfo(path);
            if(info.Length>65536)throw new InvalidDataException("Audio settings file is too large");
            using(var input=File.OpenRead(path)) {
                var serializer=new DataContractJsonSerializer(typeof(AudioBindings));
                var settings=serializer.ReadObject(input) as AudioBindings;
                Validate(settings);
                return settings;
            }
        }
        internal static void SaveBindings(string path,AudioBindings bindings) {
            if(bindings==null)throw new InvalidDataException("Invalid audio settings");
            bindings=bindings.Clone();Validate(bindings);
            string directory=Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(directory);
            string temporary=Path.Combine(directory,".audio-"+Guid.NewGuid().ToString("N")+".tmp");
            try {
                using(var output=File.Create(temporary))new DataContractJsonSerializer(typeof(AudioBindings)).WriteObject(output,bindings);
                if(File.Exists(path))File.Replace(temporary,path,null);
                else File.Move(temporary,path);
            } finally {if(File.Exists(temporary))File.Delete(temporary);}
        }
    }
}
