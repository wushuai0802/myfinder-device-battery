using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Collections.Generic;

namespace AtkBattery {
    [DataContract]
    public sealed class AudioEndpointBindings {
        [DataMember(Order=0, EmitDefaultValue=false)] public string SpeakerEndpointId;
        [DataMember(Order=1, EmitDefaultValue=false)] public string HeadsetEndpointId;
        [DataMember(Order=2, EmitDefaultValue=false)] public string MicrophoneEndpointId;
        public AudioEndpointBindings Clone(){return new AudioEndpointBindings {SpeakerEndpointId=SpeakerEndpointId,HeadsetEndpointId=HeadsetEndpointId,MicrophoneEndpointId=MicrophoneEndpointId};}
        public string EndpointFor(string role) {
            if(role=="speaker")return SpeakerEndpointId;if(role=="headset")return HeadsetEndpointId;if(role=="microphone")return MicrophoneEndpointId;
            throw new ArgumentException("Unknown audio role","role");
        }
        public void SetEndpoint(string role,string id) {
            id=String.IsNullOrWhiteSpace(id)?null:id;
            if(id!=null&&id.Length>4096)throw new ArgumentException("Audio endpoint ID is too long","id");
            if(role=="speaker")SpeakerEndpointId=id;else if(role=="headset")HeadsetEndpointId=id;else if(role=="microphone")MicrophoneEndpointId=id;
            else throw new ArgumentException("Unknown audio role","role");
        }
    }
    [DataContract]
    public sealed class AudioBindings {
        [DataMember(Order=0)] public int SchemaVersion = 1;
        [DataMember(Order=1, EmitDefaultValue=false)] public string SpeakerEndpointId;
        [DataMember(Order=2, EmitDefaultValue=false)] public string HeadsetEndpointId;
        [DataMember(Order=3, EmitDefaultValue=false)] public string MicrophoneEndpointId;
        [DataMember(Order=4, EmitDefaultValue=false)] public string TraySource = "auto";
        [DataMember(Order=5, EmitDefaultValue=false)] public bool LowBatteryNotifications;
        [DataMember(Order=6)] public string Theme = "auto";
        [DataMember(Order=7)] public bool ReducedMotion;
        [DataMember(Order=8)] public bool RememberReadings = true;
        [DataMember(Order=9)] public int WarningThreshold = 20;
        [DataMember(Order=10)] public string MouseAlias = "ATK A9 Air+";
        [DataMember(Order=11)] public string HeadsetAlias = "HyperX Cloud Alpha";
        [DataMember(Order=12)] public int BackgroundSeconds = 60;
        [DataMember(Order=13)] public Dictionary<string,string> DeviceAliases = new Dictionary<string,string>();
        [DataMember(Order=14, EmitDefaultValue=false)] public string TrayDeviceId;
        [DataMember(Order=15, EmitDefaultValue=false)] public List<string> HiddenDeviceIds = new List<string>();
        [DataMember(Order=16, EmitDefaultValue=false)] public List<string> DeviceOrder = new List<string>();
        [DataMember(Order=17, EmitDefaultValue=false)] public Dictionary<string,AudioEndpointBindings> AudioDeviceBindings = new Dictionary<string,AudioEndpointBindings>();
        [DataMember(Order=18, EmitDefaultValue=false)] public bool ExplicitAudioBindingsOnly;
        [OnDeserializing] void Defaults(StreamingContext context) {
            Theme="auto";TraySource="mouse";RememberReadings=true;WarningThreshold=20;
            MouseAlias="ATK A9 Air+";HeadsetAlias="HyperX Cloud Alpha";BackgroundSeconds=60;
            DeviceAliases=new Dictionary<string,string>();
            HiddenDeviceIds=new List<string>();DeviceOrder=new List<string>();AudioDeviceBindings=new Dictionary<string,AudioEndpointBindings>();
        }

        public AudioBindings Clone() {
            var copy=new AudioBindings { SchemaVersion=SchemaVersion,
                SpeakerEndpointId=SpeakerEndpointId, HeadsetEndpointId=HeadsetEndpointId,
                MicrophoneEndpointId=MicrophoneEndpointId, TraySource=TraySource,TrayDeviceId=TrayDeviceId,
                LowBatteryNotifications=LowBatteryNotifications,Theme=Theme,ReducedMotion=ReducedMotion,
                RememberReadings=RememberReadings,WarningThreshold=WarningThreshold,MouseAlias=MouseAlias,
                HeadsetAlias=HeadsetAlias,BackgroundSeconds=BackgroundSeconds,DeviceAliases=DeviceAliases==null?new Dictionary<string,string>():new Dictionary<string,string>(DeviceAliases),
                HiddenDeviceIds=HiddenDeviceIds==null?new List<string>():new List<string>(HiddenDeviceIds),DeviceOrder=DeviceOrder==null?new List<string>():new List<string>(DeviceOrder),ExplicitAudioBindingsOnly=ExplicitAudioBindingsOnly };
            if(AudioDeviceBindings!=null)foreach(var pair in AudioDeviceBindings)copy.AudioDeviceBindings[pair.Key]=pair.Value==null?null:pair.Value.Clone();
            return copy;
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
        static bool usingDefaults;
        public static bool UsingDefaults {get{lock(Gate)return usingDefaults;}}
        public static string DataDirectory {
            get {return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MyFinderDeviceBattery");}
        }
        public static string SettingsPath {get{return Path.Combine(DataDirectory,"audio-settings.json");}}
        public static AudioBindings Current {
            get {lock(Gate){if(current==null){try{current=LoadBindings(SettingsPath);}
                catch(InvalidDataException){UseDefaults();}catch(IOException){UseDefaults();}catch(UnauthorizedAccessException){UseDefaults();}catch(SerializationException){UseDefaults();}catch(ArgumentException){UseDefaults();}}
                return current.Clone();}}
        }
        static void UseDefaults(){current=new AudioBindings();usingDefaults=true;}
        static void PreserveUnreadSettings(){if(usingDefaults&&File.Exists(SettingsPath)&&!File.Exists(SettingsPath+".unread.bak"))File.Copy(SettingsPath,SettingsPath+".unread.bak");}
        public static void SetEndpoint(string role,string id) {
            lock(Gate) {
                var next=Current;next.SetEndpoint(role,id);
                PreserveUnreadSettings();SaveBindings(SettingsPath,next);current=next;usingDefaults=false;
            }
        }
        public static void ResetAudio() {
            lock(Gate){var next=Current;next.SpeakerEndpointId=null;next.HeadsetEndpointId=null;
                next.MicrophoneEndpointId=null;PreserveUnreadSettings();SaveBindings(SettingsPath,next);current=next;usingDefaults=false;}
        }
        public static AudioBindings ForAudioDevice(string deviceId) {
            var settings=Current;if(String.IsNullOrEmpty(deviceId))return settings;
            ValidateDeviceId(deviceId);settings.SpeakerEndpointId=null;settings.HeadsetEndpointId=null;settings.MicrophoneEndpointId=null;settings.ExplicitAudioBindingsOnly=true;
            AudioEndpointBindings bindings;
            if(settings.AudioDeviceBindings.TryGetValue(deviceId,out bindings)&&bindings!=null) {
                settings.SpeakerEndpointId=bindings.SpeakerEndpointId;settings.HeadsetEndpointId=bindings.HeadsetEndpointId;settings.MicrophoneEndpointId=bindings.MicrophoneEndpointId;
            }
            return settings;
        }
        public static void SetEndpointForDevice(string deviceId,string role,string id) {
            if(String.IsNullOrEmpty(deviceId)){SetEndpoint(role,id);return;}
            lock(Gate) {
                ValidateDeviceId(deviceId);var next=Current;AudioEndpointBindings bindings;
                if(!next.AudioDeviceBindings.TryGetValue(deviceId,out bindings))bindings=new AudioEndpointBindings();
                bindings.SetEndpoint(role,id);
                if(bindings.SpeakerEndpointId==null&&bindings.HeadsetEndpointId==null&&bindings.MicrophoneEndpointId==null)next.AudioDeviceBindings.Remove(deviceId);
                else next.AudioDeviceBindings[deviceId]=bindings;
                Validate(next);PreserveUnreadSettings();SaveBindings(SettingsPath,next);current=next;usingDefaults=false;
            }
        }
        public static void ResetAudioForDevice(string deviceId) {
            if(String.IsNullOrEmpty(deviceId)){ResetAudio();return;}
            lock(Gate){ValidateDeviceId(deviceId);var next=Current;next.AudioDeviceBindings.Remove(deviceId);PreserveUnreadSettings();SaveBindings(SettingsPath,next);current=next;usingDefaults=false;}
        }
        public static void SaveConfiguration(AudioBindings settings) {
            lock(Gate){var next=settings.Clone();Validate(next);PreserveUnreadSettings();SaveBindings(SettingsPath,next);current=next;usingDefaults=false;}
        }
        internal static void SetDeviceHidden(AudioBindings settings,string deviceId,bool hidden) {
            ValidateDeviceId(deviceId);if(settings.HiddenDeviceIds==null)settings.HiddenDeviceIds=new List<string>();
            if(hidden){if(!settings.HiddenDeviceIds.Contains(deviceId))settings.HiddenDeviceIds.Add(deviceId);}else settings.HiddenDeviceIds.Remove(deviceId);
            ValidateDeviceIds(settings.HiddenDeviceIds);
        }
        internal static void MoveDevice(AudioBindings settings,string deviceId,int newIndex,IEnumerable<string> currentOrder) {
            ValidateDeviceId(deviceId);var order=new List<string>();var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(string id in currentOrder){ValidateDeviceId(id);if(seen.Add(id))order.Add(id);}
            if(settings.DeviceOrder!=null)foreach(string id in settings.DeviceOrder)if(seen.Add(id))order.Add(id);
            int oldIndex=order.IndexOf(deviceId);if(oldIndex<0)return;
            order.RemoveAt(oldIndex);order.Insert(Math.Max(0,Math.Min(newIndex,order.Count)),deviceId);ValidateDeviceIds(order);settings.DeviceOrder=order;
        }
        internal static void UseTransientConfiguration(AudioBindings settings) {
            lock(Gate){var next=settings.Clone();Validate(next);current=next;usingDefaults=false;}
        }
        static void Validate(AudioBindings settings) {
            if(settings==null||settings.SchemaVersion!=1)throw new InvalidDataException("Unsupported audio settings format");
            foreach(string role in new[]{"speaker","headset","microphone"})settings.SetEndpoint(role,settings.EndpointFor(role));
            if(String.IsNullOrEmpty(settings.TraySource))settings.TraySource="mouse";
            if(settings.TraySource!="auto"&&settings.TraySource!="mouse"&&settings.TraySource!="headset"&&settings.TraySource!="keyboard"&&settings.TraySource!="lowest")
                throw new InvalidDataException("Unknown tray battery source");
            settings.TrayDeviceId=String.IsNullOrWhiteSpace(settings.TrayDeviceId)?null:settings.TrayDeviceId.Trim();
            if(settings.TrayDeviceId!=null) {
                if(settings.TrayDeviceId.Length>64)throw new InvalidDataException("Invalid tray device identifier");
                foreach(char c in settings.TrayDeviceId)if(Char.IsControl(c))throw new InvalidDataException("Invalid tray device identifier");
            }
            if(settings.Theme!="auto"&&settings.Theme!="light"&&settings.Theme!="dark")throw new InvalidDataException("Unknown theme");
            if(settings.WarningThreshold!=10&&settings.WarningThreshold!=15&&settings.WarningThreshold!=20)throw new InvalidDataException("Invalid notification threshold");
            if(settings.BackgroundSeconds!=60&&settings.BackgroundSeconds!=120&&settings.BackgroundSeconds!=300)throw new InvalidDataException("Invalid background interval");
            settings.MouseAlias=CleanAlias(settings.MouseAlias,"ATK A9 Air+");
            settings.HeadsetAlias=CleanAlias(settings.HeadsetAlias,"HyperX Cloud Alpha");
            if(settings.DeviceAliases==null)settings.DeviceAliases=new Dictionary<string,string>();
            if(settings.DeviceAliases.Count>128)throw new InvalidDataException("Too many device aliases");
            foreach(var id in new List<string>(settings.DeviceAliases.Keys)){if(id.Length>64)throw new InvalidDataException("Invalid device identifier");settings.DeviceAliases[id]=CleanAlias(settings.DeviceAliases[id],"无线设备");}
            if(settings.HiddenDeviceIds==null)settings.HiddenDeviceIds=new List<string>();
            if(settings.DeviceOrder==null)settings.DeviceOrder=new List<string>();
            ValidateDeviceIds(settings.HiddenDeviceIds);ValidateDeviceIds(settings.DeviceOrder);
            if(settings.AudioDeviceBindings==null)settings.AudioDeviceBindings=new Dictionary<string,AudioEndpointBindings>();
            if(settings.AudioDeviceBindings.Count>128)throw new InvalidDataException("Too many device audio bindings");
            foreach(var pair in settings.AudioDeviceBindings){ValidateDeviceId(pair.Key);if(pair.Value==null)throw new InvalidDataException("Invalid device audio bindings");foreach(string role in new[]{"speaker","headset","microphone"})pair.Value.SetEndpoint(role,pair.Value.EndpointFor(role));}
        }
        static void ValidateDeviceIds(List<string> ids) {
            if(ids.Count>128)throw new InvalidDataException("Too many device identifiers");
            var seen=new HashSet<string>(StringComparer.Ordinal);foreach(string id in ids){ValidateDeviceId(id);if(!seen.Add(id))throw new InvalidDataException("Duplicate device identifier");}
        }
        static void ValidateDeviceId(string id) {
            if(String.IsNullOrWhiteSpace(id)||id.Length>64||id!=id.Trim())throw new InvalidDataException("Invalid device identifier");
            foreach(char character in id)if(Char.IsControl(character))throw new InvalidDataException("Invalid device identifier");
        }
        static string CleanAlias(string value,string fallback) {
            value=(value??"").Trim();if(value.Length==0)return fallback;
            if(value.Length>32)throw new InvalidDataException("Device name exceeds 32 characters");
            foreach(char c in value)if(Char.IsControl(c))throw new InvalidDataException("Device name contains control characters");
            return value;
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
                using(var output=File.Create(temporary)) {
                    new DataContractJsonSerializer(typeof(AudioBindings)).WriteObject(output,bindings);
                    if(output.Length>65536)throw new InvalidDataException("Audio settings file is too large");
                }
                if(File.Exists(path))File.Replace(temporary,path,null);
                else File.Move(temporary,path);
            } finally {if(File.Exists(temporary))File.Delete(temporary);}
        }
    }
}
