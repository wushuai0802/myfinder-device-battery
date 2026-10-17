using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace AtkBattery {
    [DataContract] internal sealed class SavedReadings {
        [DataMember] public int? MousePercent;
        [DataMember(EmitDefaultValue=false)] public DateTime MouseTime;
        [DataMember] public int? HeadsetPercent;
        [DataMember(EmitDefaultValue=false)] public DateTime HeadsetTime;
    }
    internal static class ReadingCache {
        static string lastKey;
        internal static WirelessDevice[] LoadWireless(string path) {
            try {if(File.Exists(path)&&new FileInfo(path).Length<=131072){var snapshot=GenericBatteryWorker.Parse(File.ReadAllText(path));if(String.IsNullOrEmpty(snapshot.Error)){foreach(var device in snapshot.Devices){device.Fresh=false;device.Online=false;device.Charging=false;}return snapshot.Devices;}}}
            catch(IOException){}catch(UnauthorizedAccessException){}
            return new WirelessDevice[0];
        }
        internal static void SaveWireless(string path,IEnumerable<WirelessDevice> devices) {
            var saved=new System.Collections.Generic.List<WirelessDevice>();
            foreach(var device in devices)if(device.Percent.HasValue&&device.ConfirmedTime!=DateTime.MinValue&&(DateTime.UtcNow-device.ConfirmedTime).TotalDays<=7&&saved.Count<64)saved.Add(device);
            string temporary=path+".tmp";
            try {Directory.CreateDirectory(Path.GetDirectoryName(path));using(var output=File.Create(temporary))new DataContractJsonSerializer(typeof(WirelessSnapshot)).WriteObject(output,new WirelessSnapshot {Schema=1,Devices=saved.ToArray()});
                if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);}
            catch(IOException){}catch(UnauthorizedAccessException){}catch(SerializationException){}
            finally{try{if(File.Exists(temporary))File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}}
        }
        static bool Valid(int? percent,DateTime time) {
            var age=DateTime.UtcNow-time;
            return percent>=0&&percent<=100&&time!=DateTime.MinValue&&age.TotalSeconds>=-5&&age.TotalDays<=7;
        }
        internal static void Load(string path,out BatteryReading mouse,out HeadsetReading headset) {
            mouse=null;headset=null;
            try {
                if(!File.Exists(path)||new FileInfo(path).Length>16384)return;
                SavedReadings value;
                using(var stream=File.OpenRead(path))value=(SavedReadings)new DataContractJsonSerializer(typeof(SavedReadings)).ReadObject(stream);
                if(value==null)return;
                if(Valid(value.MousePercent,value.MouseTime))mouse=new BatteryReading {Percent=value.MousePercent.Value,TimestampUtc=value.MouseTime};
                // Saved headset data is only merged after a new radio-connected response.
                if(Valid(value.HeadsetPercent,value.HeadsetTime))headset=new HeadsetReading {Connected=true,Percent=value.HeadsetPercent,BatteryTimestampUtc=value.HeadsetTime};
            }catch(IOException){}catch(UnauthorizedAccessException){}catch(SerializationException){}catch(ArgumentException){}
        }
        internal static void Save(string path,BatteryReading mouse,HeadsetReading headset) {
            var value=new SavedReadings();
            if(mouse!=null&&Valid(mouse.Percent,mouse.TimestampUtc)){value.MousePercent=mouse.Percent;value.MouseTime=mouse.TimestampUtc;}
            if(headset!=null&&Valid(headset.Percent,headset.BatteryTimestampUtc)){value.HeadsetPercent=headset.Percent;value.HeadsetTime=headset.BatteryTimestampUtc;}
            string key=path+"|"+value.MousePercent+"|"+value.MouseTime.Ticks+"|"+value.HeadsetPercent+"|"+value.HeadsetTime.Ticks;
            if(key==lastKey)return;
            string temp=path+".tmp";
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using(var stream=File.Create(temp))new DataContractJsonSerializer(typeof(SavedReadings)).WriteObject(stream,value);
                if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
                lastKey=key;
            }catch(IOException){}catch(UnauthorizedAccessException){}catch(SerializationException){}
            finally{try{if(File.Exists(temp))File.Delete(temp);}catch(IOException){}catch(UnauthorizedAccessException){}}
        }
    }
}
