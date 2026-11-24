using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AtkBattery {
    internal sealed class StoppedFamilyDevice {
        internal string MemoryKey;
        internal string DisplayName;
    }

    internal static class FamilyMemoryJson {
        internal const int MaxEntries=256;
        internal const int MaxFileBytes=262144;
        internal const int MaxDisplayNameLength=32;
        internal const string DefaultDisplayName="未命名设备";
        static readonly Regex MemoryKeyPattern=new Regex(@"\A[a-z0-9_-]{1,48}\|unit-[a-f0-9]{24}\z",RegexOptions.CultureInvariant);
        static readonly UTF8Encoding Utf8NoBom=new UTF8Encoding(false,true);

        internal static bool IsValidMemoryKey(string key){
            return !String.IsNullOrEmpty(key)&&MemoryKeyPattern.IsMatch(key);
        }

        internal static string CleanDisplayName(string value){
            if(String.IsNullOrWhiteSpace(value))return null;
            var cleaned=new StringBuilder();
            foreach(char c in value.Trim()){
                if(Char.IsControl(c))continue;
                if(cleaned.Length>=MaxDisplayNameLength)break;
                cleaned.Append(c);
            }
            if(cleaned.Length>0&&Char.IsHighSurrogate(cleaned[cleaned.Length-1]))cleaned.Length--;
            string text=cleaned.ToString().Trim();
            return text.Length==0?null:text;
        }

        internal static object ParseBytes(byte[] raw){
            if(raw==null||raw.Length==0||raw.Length>MaxFileBytes)throw new InvalidDataException("family_memory_size");
            int start=raw.Length>=3&&raw[0]==0xEF&&raw[1]==0xBB&&raw[2]==0xBF?3:0;
            if(start>0){
                var trimmed=new byte[raw.Length-start];
                Buffer.BlockCopy(raw,start,trimmed,0,trimmed.Length);
                raw=trimmed;
            }
            return PackJson.Parse(raw);
        }

        internal static object ParseText(string json){
            if(String.IsNullOrEmpty(json))throw new InvalidDataException("family_memory_empty");
            return ParseBytes(Utf8NoBom.GetBytes(json));
        }

        internal static Dictionary<string,Dictionary<string,object>> ParseMemoryMapObject(object value){
            var map=PackJson.Object(value);
            if(map.Count>MaxEntries)throw new InvalidDataException("family_memory_limit");
            var result=new Dictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
            foreach(var pair in map){
                if(!IsValidMemoryKey(pair.Key))throw new InvalidDataException("family_memory_key");
                var entry=PackJson.Object(pair.Value);
                result[pair.Key]=CloneEntry(entry);
            }
            return result;
        }

        internal static Dictionary<string,Dictionary<string,object>> ParseMemoryMapJson(string json){
            return ParseMemoryMapObject(ParseText(json));
        }

        internal static Dictionary<string,object> CloneEntry(Dictionary<string,object> entry){
            var copy=new Dictionary<string,object>(StringComparer.Ordinal);
            foreach(var pair in entry)copy[pair.Key]=CloneValue(pair.Value);
            return copy;
        }

        static object CloneValue(object value){
            var dict=value as Dictionary<string,object>;
            if(dict!=null){
                var copy=new Dictionary<string,object>(StringComparer.Ordinal);
                foreach(var pair in dict)copy[pair.Key]=CloneValue(pair.Value);
                return copy;
            }
            var list=value as List<object>;
            if(list!=null){
                var copy=new List<object>(list.Count);
                foreach(object item in list)copy.Add(CloneValue(item));
                return copy;
            }
            var number=value as PackJson.Number;
            if(number!=null)return new PackJson.Number {Text=number.Text};
            return value;
        }

        internal static bool EntryDisabled(Dictionary<string,object> entry){
            object disabled;
            return entry!=null&&entry.TryGetValue("disabled",out disabled)&&disabled is bool&&(bool)disabled;
        }

        internal static string SerializeMemoryMap(Dictionary<string,Dictionary<string,object>> map){
            var raw=new Dictionary<string,object>(StringComparer.Ordinal);
            if(map!=null)foreach(var pair in map)raw[pair.Key]=pair.Value;
            return Serialize(raw);
        }

        internal static string SerializeEnvelope(Dictionary<string,Dictionary<string,object>> memory,Dictionary<string,string> stoppedNames){
            var memoryObj=new Dictionary<string,object>(StringComparer.Ordinal);
            if(memory!=null)foreach(var pair in memory)memoryObj[pair.Key]=pair.Value;
            var namesObj=new Dictionary<string,object>(StringComparer.Ordinal);
            if(stoppedNames!=null)foreach(var pair in stoppedNames)namesObj[pair.Key]=pair.Value;
            var root=new Dictionary<string,object>(StringComparer.Ordinal){
                {"schema",new PackJson.Number {Text="1"}},
                {"familyMemory",memoryObj},
                {"stoppedNames",namesObj}
            };
            return Serialize(root);
        }

        internal static string Serialize(object value){
            var builder=new StringBuilder();
            WriteValue(builder,value);
            return builder.ToString();
        }

        static void WriteValue(StringBuilder builder,object value){
            if(value==null){builder.Append("null");return;}
            if(value is bool){builder.Append((bool)value?"true":"false");return;}
            var text=value as string;
            if(text!=null){WriteString(builder,text);return;}
            var number=value as PackJson.Number;
            if(number!=null){builder.Append(number.Text);return;}
            if(value is int||value is long){builder.Append(Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture));return;}
            var dict=value as Dictionary<string,object>;
            if(dict!=null){
                builder.Append('{');
                var keys=new List<string>(dict.Keys);
                keys.Sort(StringComparer.Ordinal);
                for(int i=0;i<keys.Count;i++){
                    if(i>0)builder.Append(',');
                    WriteString(builder,keys[i]);
                    builder.Append(':');
                    WriteValue(builder,dict[keys[i]]);
                }
                builder.Append('}');
                return;
            }
            var list=value as List<object>;
            if(list!=null){
                builder.Append('[');
                for(int i=0;i<list.Count;i++){
                    if(i>0)builder.Append(',');
                    WriteValue(builder,list[i]);
                }
                builder.Append(']');
                return;
            }
            throw new InvalidDataException("family_memory_unsupported_type");
        }

        internal static void WriteString(StringBuilder builder,string value){
            builder.Append('"');
            foreach(char c in value){
                switch(c){
                    case '"':builder.Append("\\\"");break;
                    case '\\':builder.Append("\\\\");break;
                    case '\b':builder.Append("\\b");break;
                    case '\f':builder.Append("\\f");break;
                    case '\n':builder.Append("\\n");break;
                    case '\r':builder.Append("\\r");break;
                    case '\t':builder.Append("\\t");break;
                    default:
                        if(c<0x20){builder.Append("\\u");builder.Append(((int)c).ToString("x4"));}
                        else builder.Append(c);
                        break;
                }
            }
            builder.Append('"');
        }

        internal static string ExtractTopLevelField(string json,string fieldName){
            if(String.IsNullOrEmpty(json)||String.IsNullOrEmpty(fieldName))return null;
            int i=0,n=json.Length;
            SkipWs(json,ref i);
            if(i>=n||json[i]!='{')return null;
            i++;
            SkipWs(json,ref i);
            if(i<n&&json[i]=='}')return null;
            while(i<n){
                SkipWs(json,ref i);
                if(i>=n||json[i]!='"')return null;
                string key=ReadJsonString(json,ref i);
                if(key==null)return null;
                SkipWs(json,ref i);
                if(i>=n||json[i]!=':')return null;
                i++;
                SkipWs(json,ref i);
                int start=i;
                if(!SkipJsonValue(json,ref i))return null;
                if(String.Equals(key,fieldName,StringComparison.Ordinal)){
                    string slice=json.Substring(start,i-start);
                    return slice=="null"?null:slice;
                }
                SkipWs(json,ref i);
                if(i<n&&json[i]==','){i++;continue;}
                if(i<n&&json[i]=='}')return null;
                return null;
            }
            return null;
        }

        static void SkipWs(string s,ref int i){
            while(i<s.Length&&(s[i]==' '||s[i]=='\t'||s[i]=='\r'||s[i]=='\n'))i++;
        }

        static string ReadJsonString(string s,ref int i){
            if(i>=s.Length||s[i]!='"')return null;
            i++;
            var sb=new StringBuilder();
            while(i<s.Length){
                char c=s[i++];
                if(c=='"')return sb.ToString();
                if(c<32)return null;
                if(c!='\\'){sb.Append(c);continue;}
                if(i>=s.Length)return null;
                char esc=s[i++];
                switch(esc){
                    case '"':case '\\':case '/':sb.Append(esc);break;
                    case 'b':sb.Append('\b');break;
                    case 'f':sb.Append('\f');break;
                    case 'n':sb.Append('\n');break;
                    case 'r':sb.Append('\r');break;
                    case 't':sb.Append('\t');break;
                    case 'u':
                        int code;
                        if(i+4>s.Length||!Int32.TryParse(s.Substring(i,4),System.Globalization.NumberStyles.AllowHexSpecifier,System.Globalization.CultureInfo.InvariantCulture,out code))return null;
                        sb.Append((char)code);
                        i+=4;
                        break;
                    default:return null;
                }
            }
            return null;
        }

        static bool SkipJsonValue(string s,ref int i){
            if(i>=s.Length)return false;
            char c=s[i];
            if(c=='"')return ReadJsonString(s,ref i)!=null;
            if(c=='{'||c=='['){
                int depth=0;
                while(i<s.Length){
                    char ch=s[i];
                    if(ch=='"'){if(ReadJsonString(s,ref i)==null)return false;continue;}
                    if(ch=='{'||ch=='[')depth++;
                    else if(ch=='}'||ch==']'){depth--;if(depth==0){i++;return true;}}
                    i++;
                }
                return false;
            }
            while(i<s.Length){
                char ch=s[i];
                if(ch==','||ch=='}'||ch==']'||ch==' '||ch=='\t'||ch=='\r'||ch=='\n')return true;
                i++;
            }
            return true;
        }
    }

    internal sealed class FamilyMemoryStore {
        readonly object gate=new object();
        readonly string path;
        Dictionary<string,Dictionary<string,object>> familyMemory=new Dictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
        Dictionary<string,string> stoppedNames=new Dictionary<string,string>(StringComparer.Ordinal);
        string canonicalMemoryJson="{}";
        string canonicalEnvelopeJson="{\"familyMemory\":{},\"schema\":1,\"stoppedNames\":{}}";

        internal FamilyMemoryStore():this(Path.Combine(DeviceSettings.DataDirectory,"family-memory.json")){}

        internal FamilyMemoryStore(string filePath){
            path=String.IsNullOrWhiteSpace(filePath)?null:filePath;
            Load();
        }

        internal string FilePath {get{return path;}}

        internal bool HasEntries {get{lock(gate)return familyMemory.Count>0;}}

        internal int StoppedCount {get{lock(gate){
            int count=0;
            foreach(var pair in familyMemory)if(FamilyMemoryJson.EntryDisabled(pair.Value))count++;
            return count;
        }}}

        internal string GetFamilyMemoryJson(){
            lock(gate)return canonicalMemoryJson;
        }

        internal List<StoppedFamilyDevice> GetStoppedDevices(){
            lock(gate){
                var list=new List<StoppedFamilyDevice>();
                foreach(var pair in familyMemory){
                    if(!FamilyMemoryJson.EntryDisabled(pair.Value))continue;
                    string name;
                    stoppedNames.TryGetValue(pair.Key,out name);
                    list.Add(new StoppedFamilyDevice {
                        MemoryKey=pair.Key,
                        DisplayName=String.IsNullOrWhiteSpace(name)?FamilyMemoryJson.DefaultDisplayName:name
                    });
                }
                list.Sort(delegate(StoppedFamilyDevice a,StoppedFamilyDevice b){
                    int cmp=StringComparer.OrdinalIgnoreCase.Compare(a.DisplayName,b.DisplayName);
                    return cmp!=0?cmp:StringComparer.Ordinal.Compare(a.MemoryKey,b.MemoryKey);
                });
                return list;
            }
        }

        internal void Reload(){Load();}

        void Load(){
            lock(gate){
                if(path==null||!File.Exists(path)){ResetEmptyLocked();return;}
                try{
                    var info=new FileInfo(path);
                    if(info.Length>FamilyMemoryJson.MaxFileBytes)throw new InvalidDataException("family_memory_oversize");
                    byte[] raw=File.ReadAllBytes(path);
                    var root=PackJson.Object(FamilyMemoryJson.ParseBytes(raw));
                    object schemaObj;
                    if(!root.TryGetValue("schema",out schemaObj)||PackJson.Integer(schemaObj)!=1)throw new InvalidDataException("family_memory_schema");
                    object memObj;
                    if(!root.TryGetValue("familyMemory",out memObj))throw new InvalidDataException("family_memory_missing");
                    var parsedMemory=FamilyMemoryJson.ParseMemoryMapObject(memObj);
                    var parsedNames=new Dictionary<string,string>(StringComparer.Ordinal);
                    object namesObj;
                    if(root.TryGetValue("stoppedNames",out namesObj)&&namesObj!=null){
                        var rawNames=PackJson.Object(namesObj);
                        foreach(var pair in rawNames){
                            string rawName=pair.Value as string;
                            string cleaned=FamilyMemoryJson.CleanDisplayName(rawName);
                            if(cleaned!=null)parsedNames[pair.Key]=cleaned;
                        }
                    }
                    PruneNames(parsedMemory,parsedNames);
                    familyMemory=parsedMemory;
                    stoppedNames=parsedNames;
                    UpdateCanonicalsLocked();
                }catch(Exception error){
                    if(error is IOException||error is UnauthorizedAccessException||error is InvalidDataException){
                        BackupCorruptOnceLocked();
                        ResetEmptyLocked();
                        SafeDiagnostic.Transition("family_memory","corrupt_reset");
                    }else throw;
                }
            }
        }

        void ResetEmptyLocked(){
            familyMemory=new Dictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
            stoppedNames=new Dictionary<string,string>(StringComparer.Ordinal);
            UpdateCanonicalsLocked();
        }

        void BackupCorruptOnceLocked(){
            if(path==null)return;
            string backup=path+".corrupt";
            try{
                if(File.Exists(path)&&!File.Exists(backup))File.Copy(path,backup);
            }catch(IOException){}catch(UnauthorizedAccessException){}
        }

        static void PruneNames(Dictionary<string,Dictionary<string,object>> memory,Dictionary<string,string> names){
            foreach(string key in new List<string>(names.Keys)){
                Dictionary<string,object> entry;
                string cleaned=FamilyMemoryJson.CleanDisplayName(names[key]);
                if(!memory.TryGetValue(key,out entry)||!FamilyMemoryJson.EntryDisabled(entry)||cleaned==null)names.Remove(key);
                else names[key]=cleaned;
            }
        }

        void UpdateCanonicalsLocked(){
            canonicalMemoryJson=FamilyMemoryJson.SerializeMemoryMap(familyMemory);
            canonicalEnvelopeJson=FamilyMemoryJson.SerializeEnvelope(familyMemory,stoppedNames);
        }

        internal bool MergeFromSnapshot(string snapshotMemoryJson){
            if(String.IsNullOrEmpty(snapshotMemoryJson))return false;
            Dictionary<string,Dictionary<string,object>> parsed;
            try{parsed=FamilyMemoryJson.ParseMemoryMapJson(snapshotMemoryJson);}
            catch(InvalidDataException){return false;}
            lock(gate){
                var prevMemory=familyMemory;
                var prevNames=stoppedNames;
                var prevMemJson=canonicalMemoryJson;
                var prevEnvJson=canonicalEnvelopeJson;
                var nextNames=new Dictionary<string,string>(stoppedNames,StringComparer.Ordinal);
                PruneNames(parsed,nextNames);
                string nextEnvJson=FamilyMemoryJson.SerializeEnvelope(parsed,nextNames);
                if(String.Equals(nextEnvJson,canonicalEnvelopeJson,StringComparison.Ordinal))return false;
                try{
                    familyMemory=parsed;
                    stoppedNames=nextNames;
                    UpdateCanonicalsLocked();
                    SaveLocked();
                    return true;
                }catch(IOException){
                    familyMemory=prevMemory;stoppedNames=prevNames;canonicalMemoryJson=prevMemJson;canonicalEnvelopeJson=prevEnvJson;
                    SafeDiagnostic.Transition("family_memory","save_failed");
                    return false;
                }catch(UnauthorizedAccessException){
                    familyMemory=prevMemory;stoppedNames=prevNames;canonicalMemoryJson=prevMemJson;canonicalEnvelopeJson=prevEnvJson;
                    SafeDiagnostic.Transition("family_memory","save_failed");
                    return false;
                }catch(InvalidDataException){
                    familyMemory=prevMemory;stoppedNames=prevNames;canonicalMemoryJson=prevMemJson;canonicalEnvelopeJson=prevEnvJson;
                    return false;
                }
            }
        }

        internal void RecordDisable(string engineMemoryJson,string memoryKey,string displayName){
            if(!FamilyMemoryJson.IsValidMemoryKey(memoryKey))throw new InvalidDataException("family_memory_key");
            lock(gate){
                var prevMemory=familyMemory;
                var prevNames=stoppedNames;
                var prevMemJson=canonicalMemoryJson;
                var prevEnvJson=canonicalEnvelopeJson;
                try{
                    var nextMemory=String.IsNullOrEmpty(engineMemoryJson)?CloneMemoryMap(familyMemory):FamilyMemoryJson.ParseMemoryMapJson(engineMemoryJson);
                    Dictionary<string,object> entry;
                    if(!nextMemory.TryGetValue(memoryKey,out entry)){
                        if(nextMemory.Count>=FamilyMemoryJson.MaxEntries)throw new InvalidDataException("family_memory_limit");
                        entry=new Dictionary<string,object>(StringComparer.Ordinal){{"working",null},{"rounds",new PackJson.Number {Text="0"}},{"retired",new List<object>()},{"silentPolls",new PackJson.Number {Text="0"}},{"relearned",false},{"disabled",true}};
                        nextMemory[memoryKey]=entry;
                    }else{
                        entry["disabled"]=true;
                    }
                    var nextNames=new Dictionary<string,string>(stoppedNames,StringComparer.Ordinal);
                    string cleaned=FamilyMemoryJson.CleanDisplayName(displayName);
                    if(cleaned!=null)nextNames[memoryKey]=cleaned;else nextNames.Remove(memoryKey);
                    PruneNames(nextMemory,nextNames);
                    familyMemory=nextMemory;
                    stoppedNames=nextNames;
                    UpdateCanonicalsLocked();
                    SaveLocked();
                }catch{
                    familyMemory=prevMemory;stoppedNames=prevNames;canonicalMemoryJson=prevMemJson;canonicalEnvelopeJson=prevEnvJson;
                    throw;
                }
            }
        }

        internal void RecordEnable(string engineMemoryJson,string memoryKey){
            if(!FamilyMemoryJson.IsValidMemoryKey(memoryKey))throw new InvalidDataException("family_memory_key");
            lock(gate){
                var prevMemory=familyMemory;
                var prevNames=stoppedNames;
                var prevMemJson=canonicalMemoryJson;
                var prevEnvJson=canonicalEnvelopeJson;
                try{
                    var nextMemory=String.IsNullOrEmpty(engineMemoryJson)?CloneMemoryMap(familyMemory):FamilyMemoryJson.ParseMemoryMapJson(engineMemoryJson);
                    Dictionary<string,object> entry;
                    if(nextMemory.TryGetValue(memoryKey,out entry))entry["disabled"]=false;
                    var nextNames=new Dictionary<string,string>(stoppedNames,StringComparer.Ordinal);
                    nextNames.Remove(memoryKey);
                    PruneNames(nextMemory,nextNames);
                    familyMemory=nextMemory;
                    stoppedNames=nextNames;
                    UpdateCanonicalsLocked();
                    SaveLocked();
                }catch{
                    familyMemory=prevMemory;stoppedNames=prevNames;canonicalMemoryJson=prevMemJson;canonicalEnvelopeJson=prevEnvJson;
                    throw;
                }
            }
        }

        static Dictionary<string,Dictionary<string,object>> CloneMemoryMap(Dictionary<string,Dictionary<string,object>> source){
            var copy=new Dictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
            foreach(var pair in source)copy[pair.Key]=FamilyMemoryJson.CloneEntry(pair.Value);
            return copy;
        }

        void SaveLocked(){
            byte[] bytes=new UTF8Encoding(false).GetBytes(canonicalEnvelopeJson);
            if(bytes.Length>FamilyMemoryJson.MaxFileBytes)throw new InvalidDataException("family_memory_size");
            if(path==null)return;
            string fullPath=Path.GetFullPath(path);
            string directory=Path.GetDirectoryName(fullPath);
            Directory.CreateDirectory(directory);
            string temporary=Path.Combine(directory,".family-memory-"+Guid.NewGuid().ToString("N")+".tmp");
            try{
                using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
                    stream.Write(bytes,0,bytes.Length);
                    stream.Flush(true);
                }
                if(File.Exists(fullPath))File.Replace(temporary,fullPath,null);
                else File.Move(temporary,fullPath);
            }finally{
                try{if(File.Exists(temporary))File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}
            }
        }
    }
}
