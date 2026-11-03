using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using System.Xml;

namespace AtkBattery {
    [DataContract]
    public sealed class TrayGroup {
        [DataMember(Order=0)] public string Id;
        [DataMember(Order=1)] public string Name;
        [DataMember(Order=2)] public string Source="lowest";
        [DataMember(Order=3,EmitDefaultValue=false)] public string DeviceId;
        [DataMember(Order=4)] public List<string> Members=new List<string>();
        public TrayGroup Clone(){return new TrayGroup {Id=Id,Name=Name,Source=Source,DeviceId=DeviceId,Members=Members==null?new List<string>():new List<string>(Members)};}
    }
    internal static class TrayConfiguration {
        internal const string FirstId="g1",FirstName="托盘图标 1";
        internal const string HiddenIconHint="Windows 可能把新图标收进隐藏区，可在 设置 → 个性化 → 任务栏 → 其他系统托盘图标 中打开";
        internal static bool ValidId(string id){
            if(String.IsNullOrWhiteSpace(id)||id.Length>64||id!=id.Trim())return false;
            foreach(char c in id)if(Char.IsControl(c))return false;return true;
        }
        internal static string CleanName(string name,string fallback){
            var clean=new StringBuilder();foreach(char c in name??"")if(!Char.IsControl(c))clean.Append(c);
            string value=clean.ToString().Trim();if(value.Length==0)return fallback;
            if(value.Length>12)value=value.Substring(0,Char.IsHighSurrogate(value[11])?11:12);
            return value;
        }
        internal static void Normalize(AudioBindings pref){
            pref.TrayIconStyle=pref.TrayIconStyle=="device"?"device":"number";
            var groups=new List<TrayGroup>();var ids=new HashSet<string>(StringComparer.Ordinal);var members=new HashSet<string>(StringComparer.Ordinal);
            foreach(var group in pref.TrayGroups??new List<TrayGroup>()){
                if(groups.Count==3)break;
                if(group==null||!ValidId(group.Id)||group.Id==FirstId||!ids.Add(group.Id))continue;
                var next=group.Clone();next.Name=CleanName(next.Name,"托盘图标 "+(groups.Count+2));
                next.Source=next.Source=="active"?"active":"lowest";if(!ValidId(next.DeviceId))next.DeviceId=null;
                next.Members.Clear();foreach(string id in group.Members??new List<string>()){
                    if(next.Members.Count==64)break;
                    if(ValidId(id)&&members.Add(id))next.Members.Add(id);
                }
                groups.Add(next);
            }
            pref.TrayGroups=groups;
        }
        // Parse only the new fields independently. A wrong JSON type inside one group
        // must not make DataContractJsonSerializer discard unrelated legacy settings.
        internal static void ReadFields(XmlElement root,AudioBindings pref){
            pref.TrayGroups=new List<TrayGroup>();
            var groups=root["TrayGroups"];
            if(groups!=null&&groups.GetAttribute("type")=="array")foreach(XmlNode node in groups.ChildNodes){
                var item=node as XmlElement;if(item==null||item.GetAttribute("type")!="object")continue;
                var group=new TrayGroup {Id=ReadString(item["Id"]),Name=ReadString(item["Name"]),Source=ReadString(item["Source"]),DeviceId=ReadString(item["DeviceId"])};
                var list=item["Members"];if(list!=null&&list.GetAttribute("type")=="array")foreach(XmlNode member in list.ChildNodes){string id=ReadString(member as XmlElement);if(id!=null)group.Members.Add(id);}
                pref.TrayGroups.Add(group);
            }
            pref.TrayIconStyle=ReadString(root["TrayIconStyle"]);
            var hint=root["TrayGroupHintShown"];pref.TrayGroupHintShown=hint!=null&&hint.GetAttribute("type")=="boolean"&&hint.InnerText=="true";
            foreach(string field in new[]{"TrayGroups","TrayIconStyle","TrayGroupHintShown"})while(root[field]!=null)root.RemoveChild(root[field]);
            Normalize(pref);
        }
        static string ReadString(XmlElement node){return node!=null&&node.GetAttribute("type")=="string"?node.InnerText:null;}
        internal static TrayGroup Find(AudioBindings pref,string id){return pref.TrayGroups.Find(delegate(TrayGroup group){return group.Id==id;});}
        internal static string Name(AudioBindings pref,string id){var group=Find(pref,id);return group==null?FirstName:group.Name;}
        internal static string Owner(AudioBindings pref,string deviceId){foreach(var group in pref.TrayGroups)if(group.Members.Contains(deviceId))return group.Id;return FirstId;}
        internal static TrayGroup Add(AudioBindings pref){
            if(pref.TrayGroups.Count>=3)throw new InvalidOperationException("最多显示 4 个托盘图标");
            int ordinal=pref.TrayGroups.Count+2;
            if(pref.TrayGroups.Exists(delegate(TrayGroup item){return item.Name=="托盘图标 "+ordinal;})){ordinal=2;while(pref.TrayGroups.Exists(delegate(TrayGroup item){return item.Name=="托盘图标 "+ordinal;}))ordinal++;}
            var group=new TrayGroup {Id="g"+Guid.NewGuid().ToString("N"),Name="托盘图标 "+ordinal};pref.TrayGroups.Add(group);return group;
        }
        internal static void Rename(AudioBindings pref,string id,string name){
            var group=Find(pref,id);if(group==null)throw new InvalidOperationException("此图标不能改名");
            group.Name=CleanName(name,group.Name);
        }
        internal static void Move(AudioBindings pref,string deviceId,string groupId){
            if(!ValidId(deviceId))throw new InvalidDataException("Invalid device identifier");
            var target=Find(pref,groupId);if(groupId!=FirstId&&target==null)throw new InvalidOperationException("分组不存在");
            if(target!=null&&!target.Members.Contains(deviceId)&&target.Members.Count>=64)throw new InvalidOperationException("每组最多包含 64 台设备");
            foreach(var group in pref.TrayGroups)if(group.Id!=groupId){group.Members.Remove(deviceId);if(group.DeviceId==deviceId)group.DeviceId=null;}
            if(target!=null&&!target.Members.Contains(deviceId))target.Members.Add(deviceId);
        }
        internal static void Remove(AudioBindings pref,string id){var group=Find(pref,id);if(group==null)throw new InvalidOperationException("此图标不能删除");pref.TrayGroups.Remove(group);}
    }
}
