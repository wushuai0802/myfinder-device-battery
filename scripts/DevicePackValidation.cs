using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace AtkBattery {
    // DataContractJsonSerializer accepts duplicate keys and loses numeric token
    // types. This bounded reader preserves the Python loader's JSON contract.
    internal sealed class PackJson {
        internal sealed class Number {internal string Text;}
        static readonly Regex NumberToken=new Regex(@"\G-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?",RegexOptions.CultureInvariant);
        readonly string text;int position;
        PackJson(byte[] bytes){text=new UTF8Encoding(false,true).GetString(bytes);}
        internal static object Parse(byte[] bytes){var parser=new PackJson(bytes);var value=parser.Value(0);parser.Space();if(parser.position!=parser.text.Length)Bad();return value;}
        internal static Dictionary<string,object> Object(object value){var result=value as Dictionary<string,object>;if(result==null)Bad();return result;}
        internal static List<object> Array(object value){var result=value as List<object>;if(result==null)Bad();return result;}
        internal static string String(object value){var result=value as string;if(result==null)Bad();return result;}
        internal static long Integer(object value){var number=value as Number;long result;if(number==null||!Regex.IsMatch(number.Text,@"\A-?(?:0|[1-9][0-9]*)\z")||!Int64.TryParse(number.Text,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out result))throw new InvalidDataException("pack_integer");return result;}
        static void Bad(){throw new InvalidDataException("pack_json");}
        void Space(){while(position<text.Length&&(text[position]==' '||text[position]=='\t'||text[position]=='\r'||text[position]=='\n'))position++;}
        bool Take(char c){Space();if(position<text.Length&&text[position]==c){position++;return true;}return false;}
        void Need(char c){if(!Take(c))Bad();}
        object Value(int depth){
            if(depth>32)Bad();Space();if(position==text.Length)Bad();char c=text[position];
            if(c=='"')return ReadString();
            if(c=='{'){
                position++;var result=new Dictionary<string,object>(StringComparer.Ordinal);if(Take('}'))return result;
                do{Space();if(position==text.Length||text[position]!='"')Bad();string key=ReadString();Need(':');if(result.ContainsKey(key))throw new InvalidDataException("pack_duplicate_key");result.Add(key,Value(depth+1));}while(Take(','));Need('}');return result;
            }
            if(c=='['){position++;var result=new List<object>();if(Take(']'))return result;do{result.Add(Value(depth+1));}while(Take(','));Need(']');return result;}
            foreach(string literal in new[]{"true","false","null"})if(position+literal.Length<=text.Length&&System.String.CompareOrdinal(text,position,literal,0,literal.Length)==0){position+=literal.Length;return literal=="null"?null:(object)(literal=="true");}
            var match=NumberToken.Match(text,position);
            if(!match.Success)Bad();position+=match.Length;return new Number {Text=match.Value};
        }
        string ReadString(){
            position++;var value=new StringBuilder();
            while(position<text.Length){
                char c=text[position++];if(c=='"')return value.ToString();if(c<32)Bad();
                if(c!='\\'){value.Append(c);continue;}if(position==text.Length)Bad();c=text[position++];
                switch(c){
                    case '"':case '\\':case '/':value.Append(c);break;
                    case 'b':value.Append('\b');break;case 'f':value.Append('\f');break;case 'n':value.Append('\n');break;case 'r':value.Append('\r');break;case 't':value.Append('\t');break;
                    case 'u':int code;if(position+4>text.Length||!Int32.TryParse(text.Substring(position,4),NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out code))throw new InvalidDataException("pack_json_escape");value.Append((char)code);position+=4;break;
                    default:Bad();break;
                }
            }Bad();return null;
        }
    }
    internal sealed class DevicePackDocument {
        internal long Version;internal string KeyId;internal int Disabled;
    }
    internal static class DevicePackValidation {
        static readonly Lazy<Dictionary<string,object>> Schema=new Lazy<Dictionary<string,object>>(delegate{
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("DevicePackSchema")){
                if(stream==null)throw new InvalidDataException("pack_schema_missing");
                using(var memory=new MemoryStream()){stream.CopyTo(memory);return PackJson.Object(PackJson.Parse(memory.ToArray()));}
            }
        });
        // These are the only whole-pack semantic checks outside the schema in
        // engine/device_pack.py. Unknown profiles are row rejections, not errors.
        static readonly HashSet<string> RazerProfiles=new HashSet<string>(StringComparer.Ordinal){"hyperspeed-mouse-1f","hyperspeed-mouse-3f","hyperspeed-mouse-ff","headset-3f","keyboard-1f","keyboard-3f","keyboard-9f"};
        internal static DevicePackDocument Validate(byte[] raw){
            if(raw.Length>DevicePackTrust.MaxPackBytes)throw new InvalidDataException("pack_size");
            var data=PackJson.Object(PackJson.Parse(raw));ValidateValue(data,Schema.Value,Schema.Value);
            var entries=PackJson.Array(data["entries"]);var disabled=PackJson.Array(data["disabled"]);
            if(entries.Count+disabled.Count>2000)throw new InvalidDataException("pack_entry_limit");
            foreach(var item in entries){var row=PackJson.Object(item);if((string)row["provider"]=="razer"&&RazerProfiles.Contains((string)row["profile"])&&PackJson.Integer(row["profileRev"])!=1)throw new InvalidDataException("pack_profile_revision");}
            return new DevicePackDocument {Version=PackJson.Integer(data["packVersion"]),KeyId=(string)data["keyId"],Disabled=disabled.Count};
        }
        static bool Equal(object a,object b){if(a is PackJson.Number&&b is PackJson.Number)return PackJson.Integer(a)==PackJson.Integer(b);return Object.Equals(a,b);}
        static void ValidateValue(object value,Dictionary<string,object> rule,Dictionary<string,object> root){
            object field;if(rule.TryGetValue("$ref",out field))rule=PackJson.Object(PackJson.Object(root["$defs"])[((string)field).Substring("#/$defs/".Length)]);
            string type=(string)rule["type"];
            if(rule.TryGetValue("const",out field)&&!Equal(value,field))throw new InvalidDataException("pack_const");
            if(rule.TryGetValue("enum",out field)&&!PackJson.Array(field).Exists(delegate(object item){return Equal(value,item);}))throw new InvalidDataException("pack_enum");
            if(type=="object"){
                var obj=PackJson.Object(value);var properties=PackJson.Object(rule["properties"]);
                if(rule.TryGetValue("required",out field))foreach(string key in PackJson.Array(field))if(!obj.ContainsKey(key))throw new InvalidDataException("pack_missing_field");
                foreach(var pair in obj){if(!properties.TryGetValue(pair.Key,out field))throw new InvalidDataException("pack_unknown_field");ValidateValue(pair.Value,PackJson.Object(field),root);}
            }else if(type=="array"){
                var items=PackJson.Array(value);if(items.Count>PackJson.Integer(rule["maxItems"]))throw new InvalidDataException("pack_entry_limit");foreach(var item in items)ValidateValue(item,PackJson.Object(rule["items"]),root);
            }else if(type=="integer"){
                long number=PackJson.Integer(value);if(number<(rule.TryGetValue("minimum",out field)?PackJson.Integer(field):0)||number>(rule.TryGetValue("maximum",out field)?PackJson.Integer(field):10000000000L))throw new InvalidDataException("pack_integer_range");
            }else if(type=="string"){
                string str=PackJson.String(value);int length=0;
                for(int i=0;i<str.Length;i++){length++;if(Char.IsSurrogatePair(str,i))i++;}
                if(length<(rule.TryGetValue("minLength",out field)?PackJson.Integer(field):0)||length>(rule.TryGetValue("maxLength",out field)?PackJson.Integer(field):DevicePackTrust.MaxPackBytes))throw new InvalidDataException("pack_string_length");
                if(rule.TryGetValue("pattern",out field)&&!Regex.IsMatch(str,"\\A(?:"+field+")\\z",RegexOptions.CultureInvariant))throw new InvalidDataException("pack_string_format");
                if(rule.TryGetValue("x-printable",out field)&&(bool)field)for(int i=0;i<str.Length;i++){
                    int code=Char.IsSurrogatePair(str,i)?Char.ConvertToUtf32(str,i):str[i];
                    if(!DevicePackUnicode.Printable(code))throw new InvalidDataException("pack_non_printable");
                    if(Char.IsSurrogatePair(str,i))i++;
                }
                if(rule.TryGetValue("format",out field)&&(string)field=="date-time"){
                    DateTime time;if(!DateTime.TryParseExact(str,"yyyy-MM-dd'T'HH:mm:ss'Z'",CultureInfo.InvariantCulture,DateTimeStyles.None,out time))throw new InvalidDataException("pack_datetime");
                }
            }else throw new InvalidDataException("pack_schema_type");
        }
    }
    internal sealed class DevicePackPointer {
        internal long Version,Size;internal string FileName,Hash;
        internal static DevicePackPointer Parse(byte[] bytes){
            if(bytes.Length>DevicePackTrust.MaxMetadataBytes)throw new InvalidDataException("pointer_size");
            var data=PackJson.Object(PackJson.Parse(bytes));
            var fields=new HashSet<string>(new[]{"format","loader","packVersion","file","sha256","size"},StringComparer.Ordinal);
            if(data.Count!=fields.Count)throw new InvalidDataException("pointer_fields");foreach(string key in data.Keys)if(!fields.Contains(key))throw new InvalidDataException("pointer_fields");
            if(PackJson.String(data["format"])!="myfinder-device-pack-pointer")throw new InvalidDataException("pointer_format");
            if(PackJson.Integer(data["loader"])!=1)throw new InvalidDataException("pointer_loader");
            long version=PackJson.Integer(data["packVersion"]),size=PackJson.Integer(data["size"]);string name=PackJson.String(data["file"]),hash=PackJson.String(data["sha256"]);
            if(version<1||version>9999999999L||name!="device-pack-v1-"+version.ToString(CultureInfo.InvariantCulture)+".json"||size<1||size>DevicePackTrust.MaxPackBytes||!Regex.IsMatch(hash,@"\A[a-fA-F0-9]{64}\z"))throw new InvalidDataException("pointer_value");
            return new DevicePackPointer {Version=version,Size=size,FileName=name,Hash=hash.ToLowerInvariant()};
        }
    }
}
