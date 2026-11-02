using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AtkBattery {
    // Shared by the application, offline validation harness and local signing tool.
    // The production download path always supplies this constant, never settings or environment data.
    internal static class UpdateTrust {
        internal const string PublicKeyXml = @"<RSAKeyValue><Modulus>5Rp+cdwPDCkEarIn8skfCaDyZ/wpPWKgeWyLNTDWrd3kkpOW9rcsLWrvyYObwQ8NNfnsGlqMYsc6BzOb6ZnUxTkDbwZAn+6QB9MEh8PrkG9PL4ZbcSco4dmr2hi9D5EurnIHpDCQd+75u4yMiMd93VprToE7w/sSOXXmHk602ny5TXtLdNTIWSRMT9RAZ5hxwFnRekflG1fmrxXyuO72gzRy7XVvNrHRa/TwrDVGMxg1djpYs7OFtZXTX6dszekah5OSg9d4or1fUK8ll71xBFTswiD4JQ/3tQ3y66MIjoKJM9KQp41Q4+WiXgKCnWe1vpZiM/IJlMB2agdjSmYfQoxBiWuL2dc0PAJrAeyuLBq348NnBw3ik7J7h9MbFMHsHG5oFyAZR10X+GPzsSAvLSiPvaNn3bnYE9NhKm6FMGJjRS1ecsQ+YUhRBatMjSwRDtbkGFmnadynp0GV5r2lyjZXeaF7PeLAXotPwUrXMriIj+1W4BhPVoe4X9M+BKqZ</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
        internal const string AppId="myfinder-device-battery";
        internal const string Repository="https://github.com/wushuai0802/myfinder-device-battery";
        internal const string ApiUrl="https://api.github.com/repos/wushuai0802/myfinder-device-battery/releases";
        internal const long MaxDownloadBytes=100L*1024*1024;
        internal const long MaxExpandedBytes=512L*1024*1024;
        internal static string ZipName(string version){return "myfinder-device-battery-v"+UpdateVersion.Parse(version)+"-windows-x64.zip";}
    }

    internal sealed class UpdateVersion:IComparable<UpdateVersion> {
        readonly int major,minor,patch;readonly int? beta;readonly string text;
        internal bool IsBeta {get{return beta.HasValue;}}
        UpdateVersion(string value,int a,int b,int c,int? d){text=value;major=a;minor=b;patch=c;beta=d;}
        internal static bool TryParse(string value,out UpdateVersion version){
            version=null;if(value==null||value.Length>80)return false;
            var match=Regex.Match(value,@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-beta\.(0|[1-9][0-9]*))?\z",RegexOptions.CultureInvariant);
            int a,b,c,d=0;
            if(!match.Success||!Int32.TryParse(match.Groups[1].Value,out a)||!Int32.TryParse(match.Groups[2].Value,out b)||!Int32.TryParse(match.Groups[3].Value,out c)||match.Groups[4].Success&&!Int32.TryParse(match.Groups[4].Value,out d))return false;
            version=new UpdateVersion(value,a,b,c,match.Groups[4].Success?(int?)d:null);return true;
        }
        internal static UpdateVersion Parse(string value){UpdateVersion result;if(!TryParse(value,out result))throw new InvalidDataException("Unrecognized update version.");return result;}
        public int CompareTo(UpdateVersion other){if(other==null)return 1;int c=major.CompareTo(other.major);if(c!=0)return c;c=minor.CompareTo(other.minor);if(c!=0)return c;c=patch.CompareTo(other.patch);if(c!=0)return c;if(!beta.HasValue)return other.beta.HasValue?1:0;if(!other.beta.HasValue)return -1;return beta.Value.CompareTo(other.beta.Value);}
        public override string ToString(){return text;}
    }

    [DataContract] internal sealed class UpdateAsset {
        [DataMember(Name="name")] public string Name {get;set;}
        [DataMember(Name="browser_download_url")] public string Url {get;set;}
        [DataMember(Name="size")] public long Size {get;set;}
    }
    [DataContract] internal sealed class UpdateRelease {
        [DataMember(Name="tag_name")] public string Tag {get;set;}
        [DataMember(Name="draft")] public bool Draft {get;set;}
        [DataMember(Name="prerelease")] public bool Prerelease {get;set;}
        [DataMember(Name="body")] public string Body {get;set;}
        [DataMember(Name="assets")] public UpdateAsset[] Assets {get;set;}
        internal string Version {get{return Tag!=null&&Tag.StartsWith("v",StringComparison.Ordinal)?Tag.Substring(1):Tag;}}
        internal string PageUrl {get{UpdateVersion.Parse(Version);return UpdateTrust.Repository+"/releases/tag/"+Uri.EscapeDataString(Tag);}}
    }
    internal static class UpdateCatalog {
        internal static UpdateRelease Select(IEnumerable<UpdateRelease> releases,string current,string skipped){
            UpdateVersion installed;if(!UpdateVersion.TryParse(current,out installed))return null;
            UpdateRelease best=null;UpdateVersion bestVersion=installed;
            foreach(var item in releases??new UpdateRelease[0]){
                UpdateVersion candidate;if(item==null||item.Draft||!UpdateVersion.TryParse(item.Version,out candidate)||!installed.IsBeta&&(item.Prerelease||candidate.IsBeta)||candidate.CompareTo(bestVersion)<=0)continue;
                best=item;bestVersion=candidate;
            }
            return best!=null&&best.Version!=skipped?best:null;
        }
        internal static Dictionary<string,UpdateAsset> ValidateAssets(UpdateRelease release,string version){
            if(release==null||release.Draft||release.Version!=version)throw new InvalidDataException("Release identity mismatch.");
            UpdateVersion.Parse(version);var result=new Dictionary<string,UpdateAsset>(StringComparer.Ordinal);
            foreach(string name in new[]{UpdateTrust.ZipName(version),"SHA256SUMS.txt","SHA256SUMS.txt.sig"}){
                var matches=(release.Assets??new UpdateAsset[0]).Where(a=>a!=null&&a.Name==name).ToArray();
                if(matches.Length!=1)throw new InvalidDataException("A required signed release attachment is missing or duplicated.");
                var asset=matches[0];string expected=UpdateTrust.Repository+"/releases/download/"+Uri.EscapeDataString(release.Tag)+"/"+Uri.EscapeDataString(name);
                Uri uri;if(asset.Size<1||asset.Size>UpdateTrust.MaxDownloadBytes||!Uri.TryCreate(asset.Url,UriKind.Absolute,out uri)||uri.AbsoluteUri!=expected||asset.Url!=expected)throw new InvalidDataException("Release attachment does not belong to this repository.");
                result.Add(name,asset);
            }return result;
        }
    }
    internal static class UpdateText {
        internal const int MaxNotesLength=12000;
        internal static string Notes(string value){var text=new StringBuilder();foreach(char c in value??""){if(text.Length>=MaxNotesLength)break;if(c=='\r'||c=='\n'||c=='\t'||!Char.IsControl(c))text.Append(c);}return text.ToString();}
    }
    internal static class UpdateJson {
        internal static T Read<T>(byte[] value,bool dictionary=false){int start=value.Length>=3&&value[0]==0xEF&&value[1]==0xBB&&value[2]==0xBF?3:0;using(var stream=new MemoryStream(value,start,value.Length-start,false))return (T)new DataContractJsonSerializer(typeof(T),new DataContractJsonSerializerSettings {UseSimpleDictionaryFormat=dictionary}).ReadObject(stream);}
        internal static byte[] ReadBytes(string path,int limit){
            using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))using(var output=new MemoryStream()){
                if(input.Length>limit)throw new InvalidDataException("Update metadata exceeds its limit.");byte[] buffer=new byte[8192];int count;
                while((count=input.Read(buffer,0,buffer.Length))!=0){if(output.Length+count>limit)throw new InvalidDataException("Update metadata exceeds its limit.");output.Write(buffer,0,count);}return output.ToArray();
            }
        }
        internal static T ReadFile<T>(string path,int limit){return Read<T>(ReadBytes(path,limit));}
        internal static string Text<T>(T value){using(var stream=new MemoryStream()){new DataContractJsonSerializer(typeof(T)).WriteObject(stream,value);return Encoding.UTF8.GetString(stream.ToArray());}}
    }
    [DataContract] internal sealed class UpdateReleaseIdentity {
        [DataMember(Name="schema")] public int Schema {get;set;}
        [DataMember(Name="appId")] public string AppId {get;set;}
        [DataMember(Name="version")] public string Version {get;set;}
        [DataMember(Name="configSchema")] public int ConfigSchema {get;set;}
        [DataMember(Name="engineSchema")] public int EngineSchema {get;set;}
    }
    [DataContract] internal sealed class UpdateManifest {
        [DataMember(Name="schema")] public int Schema {get;set;}
        [DataMember(Name="appId")] public string AppId {get;set;}
        [DataMember(Name="version")] public string Version {get;set;}
        [DataMember(Name="configSchema")] public int ConfigSchema {get;set;}
        [DataMember(Name="engineSchema")] public int EngineSchema {get;set;}
        [DataMember(Name="files")] public UpdateManifestFile[] Files {get;set;}
    }
    [DataContract] internal sealed class UpdateManifestFile {
        [DataMember(Name="path")] public string Path {get;set;}
        [DataMember(Name="length")] public long Length {get;set;}
        [DataMember(Name="sha256")] public string Sha256 {get;set;}
    }
    internal static class UpdatePaths {
        internal static void NoLinks(string path){
            for(string part=Path.GetFullPath(path);!String.IsNullOrEmpty(part);part=Path.GetDirectoryName(part)){
                if((Directory.Exists(part)||File.Exists(part))&&(File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked update paths are not supported.");
            }
        }
        internal static string Resolve(string root,string relative){
            if(String.IsNullOrWhiteSpace(relative)||relative.Length>240||relative.Contains("\\")||Regex.IsMatch(relative,@"[:*?""<>|\x00-\x1f]")||relative.StartsWith("/",StringComparison.Ordinal))throw new InvalidDataException("Unsafe update path.");
            foreach(string segment in relative.Split('/'))if(segment.Length==0||segment=="."||segment==".."||segment.EndsWith(".",StringComparison.Ordinal)||segment.EndsWith(" ",StringComparison.Ordinal)||Regex.IsMatch(segment,@"\A(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|\z)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant))throw new InvalidDataException("Unsafe update path segment.");
            string full=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
            if(!full.StartsWith(Path.GetFullPath(root).TrimEnd('\\','/')+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Update path escapes its directory.");
            NoLinks(full);return full;
        }
        internal static void DeleteTree(string path){
            NoLinks(path);if(!Directory.Exists(path))return;
            foreach(string file in Directory.GetFiles(path)){NoLinks(file);File.Delete(file);}
            foreach(string directory in Directory.GetDirectories(path))DeleteTree(directory);
            Directory.Delete(path);
        }
    }

    // This entry point is also used with ephemeral test keys. Production callers always use PublicKeyXml.
    internal static class UpdatePackageVerifier {
        internal static readonly string[] RequiredFiles={"release.json","LICENSE","THIRD_PARTY_NOTICES.md","outputs/AtkMouseBattery.exe","scripts/Manifest.Common.ps1","scripts/Install.Common.ps1","scripts/Install-Startup.ps1","scripts/Uninstall-Startup.ps1","engine/server.py","engine/runtime/python.exe"};
        internal static void VerifySignature(byte[] sums,byte[] signature,string publicKey){
            if(sums==null||sums.Length==0||sums.Length>262144||signature==null||signature.Length==0||signature.Length>16384)throw new InvalidDataException("Release signature is missing or invalid.");
            using(var rsa=new RSACryptoServiceProvider()){
                rsa.PersistKeyInCsp=false;rsa.FromXmlString(publicKey);
                if(!rsa.VerifyData(sums,CryptoConfig.MapNameToOID("SHA256"),signature))throw new InvalidDataException("Release signature verification failed.");
            }
        }
        internal static string ExpectedHash(byte[] sums,string name){
            string value=new UTF8Encoding(false,true).GetString(sums);string result=null;
            foreach(string line in value.Split('\n')){
                string clean=line.TrimEnd('\r');if(clean.Length==0)continue;
                var match=Regex.Match(clean,@"\A([a-fA-F0-9]{64})  ([^\r\n]+)\z",RegexOptions.CultureInvariant);
                if(!match.Success)throw new InvalidDataException("Malformed signed checksums.");
                if(match.Groups[2].Value==name){if(result!=null)throw new InvalidDataException("Duplicate signed checksum.");result=match.Groups[1].Value;}
            }
            if(result==null)throw new InvalidDataException("The exact package filename is absent from signed checksums.");return result;
        }
        internal static string HashFile(string file){using(var stream=File.OpenRead(file))using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
        internal static void VerifyAndExtract(string zipPath,byte[] sums,byte[] signature,string publicKey,string directory,string version,CancellationToken token){
            VerifySignature(sums,signature,publicKey);string name=UpdateTrust.ZipName(version);
            if(Path.GetFileName(zipPath)!=name)throw new InvalidDataException("The archive filename is not exact.");
            string expected=ExpectedHash(sums,name);UpdatePaths.NoLinks(zipPath);
            // Hash and unzip the same open handle. Another process cannot replace/write this ZIP
            // between authentication and extraction on Windows.
            using(var input=new FileStream(zipPath,FileMode.Open,FileAccess.Read,FileShare.Read)){
                if(input.Length<1||input.Length>UpdateTrust.MaxDownloadBytes)throw new InvalidDataException("Invalid update archive size.");
                using(var hash=SHA256.Create())if(!BitConverter.ToString(hash.ComputeHash(input)).Replace("-","").Equals(expected,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Signed package SHA256 mismatch.");
                token.ThrowIfCancellationRequested();input.Position=0;ExtractAndValidateStream(input,directory,version,token);
            }
        }
        internal static void ExtractAndValidate(string zipPath,string directory,string version,CancellationToken token){
            using(var input=new FileStream(zipPath,FileMode.Open,FileAccess.Read,FileShare.Read))ExtractAndValidateStream(input,directory,version,token);
        }
        static void ExtractAndValidateStream(Stream input,string directory,string version,CancellationToken token){
            UpdateVersion.Parse(version);UpdatePaths.NoLinks(directory);if(Directory.Exists(directory)||File.Exists(directory))throw new InvalidDataException("Extraction requires a fresh directory.");
            try {
                using(var zip=new ZipArchive(input,ZipArchiveMode.Read,true)){
                    if(zip.Entries.Count==0||zip.Entries.Count>10000)throw new InvalidDataException("Invalid archive entry count.");
                    var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long expanded=0;
                    // Inspect the entire archive before writing the first entry.
                    foreach(var entry in zip.Entries){
                        token.ThrowIfCancellationRequested();bool dir=entry.FullName.EndsWith("/",StringComparison.Ordinal);string relative=dir?entry.FullName.Substring(0,entry.FullName.Length-1):entry.FullName;
                        UpdatePaths.Resolve(directory,relative);int type=(entry.ExternalAttributes>>16)&0xF000;
                        if(!paths.Add(relative)||(entry.ExternalAttributes&0x400)!=0||type!=0&&type!=(dir?0x4000:0x8000)||dir&&entry.Length!=0)throw new InvalidDataException("Linked, special or duplicate ZIP entry.");
                        if(entry.Length<0||entry.Length>UpdateTrust.MaxExpandedBytes-expanded)throw new InvalidDataException("Expanded update exceeds its limit.");expanded+=entry.Length;
                    }
                    Directory.CreateDirectory(directory);
                    foreach(var entry in zip.Entries){
                        token.ThrowIfCancellationRequested();bool dir=entry.FullName.EndsWith("/",StringComparison.Ordinal);string file=UpdatePaths.Resolve(directory,dir?entry.FullName.Substring(0,entry.FullName.Length-1):entry.FullName);
                        if(dir){Directory.CreateDirectory(file);continue;}Directory.CreateDirectory(Path.GetDirectoryName(file));
                        using(var source=entry.Open())using(var output=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
                            byte[] buffer=new byte[65536];long total=0;int count;
                            while((count=source.Read(buffer,0,buffer.Length))!=0){token.ThrowIfCancellationRequested();total+=count;if(total>entry.Length)throw new InvalidDataException("Archive entry length mismatch.");output.Write(buffer,0,count);}if(total!=entry.Length)throw new InvalidDataException("Truncated archive entry.");
                        }
                    }
                }
                ValidateDirectory(directory,version,token);
            }catch{UpdatePaths.DeleteTree(directory);throw;}
        }
        internal static void ValidateDirectory(string directory,string version,CancellationToken token){
            var release=UpdateJson.ReadFile<UpdateReleaseIdentity>(UpdatePaths.Resolve(directory,"release.json"),65536);
            var manifest=UpdateJson.ReadFile<UpdateManifest>(UpdatePaths.Resolve(directory,"package-manifest.json"),2097152);
            if(release.Schema!=1||release.AppId!=UpdateTrust.AppId||release.Version!=version||release.ConfigSchema!=1||release.EngineSchema!=1&&release.EngineSchema!=2||manifest.Schema!=1||manifest.AppId!=UpdateTrust.AppId||manifest.Version!=version||manifest.ConfigSchema!=release.ConfigSchema||manifest.EngineSchema!=release.EngineSchema||manifest.Files==null||manifest.Files.Length==0||manifest.Files.Length>5000)throw new InvalidDataException("Update package identity, version or schema mismatch.");
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var entry in manifest.Files){
                token.ThrowIfCancellationRequested();if(entry==null)throw new InvalidDataException("Invalid manifest entry.");string file=UpdatePaths.Resolve(directory,entry.Path);
                if(!seen.Add(entry.Path)||entry.Path.Equals("package-manifest.json",StringComparison.OrdinalIgnoreCase)||entry.Path.Equals(".myfinder-device-battery-install.json",StringComparison.OrdinalIgnoreCase)||entry.Length<0||!Regex.IsMatch(entry.Sha256??"",@"\A[a-fA-F0-9]{64}\z")||!File.Exists(file)||new FileInfo(file).Length!=entry.Length||!HashFile(file).Equals(entry.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Update package file is missing or changed.");
            }
            foreach(string file in RequiredFiles)if(!seen.Contains(file))throw new InvalidDataException("Required installer payload is absent.");
            // Unlike a manual installation, the signed update must not carry unlisted executables/scripts.
            seen.Add("package-manifest.json");foreach(string file in Directory.GetFiles(directory,"*",SearchOption.AllDirectories))if(!seen.Contains(file.Substring(directory.TrimEnd('\\','/').Length+1).Replace('\\','/')))throw new InvalidDataException("Unlisted update payload.");
        }
    }
}
