using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AtkBattery {
    internal static class DevicePackFiles {
        internal static void NoLinks(string path){
            for(string part=Path.GetFullPath(path);!String.IsNullOrEmpty(part);part=Path.GetDirectoryName(part)){
                FileAttributes attributes;
                try{attributes=File.GetAttributes(part);}catch(FileNotFoundException){continue;}catch(DirectoryNotFoundException){continue;}
                if((attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("pack_linked_path");
            }
        }
    }
    [DataContract] internal sealed class DevicePackRecord {
        [DataMember] public long Version;
        [DataMember] public string Sha256,Source,Reason;
        [DataMember] public int Disabled;
        internal string FileName {get{return "device-pack-v1-"+Version.ToString(CultureInfo.InvariantCulture)+".json";}}
    }
    [DataContract] internal sealed class DevicePackState {
        [DataMember] public int Schema=1;
        [DataMember] public long HighestAcceptedVersion;
        [DataMember] public List<DevicePackRecord> Versions=new List<DevicePackRecord>();
        [DataMember] public List<DevicePackRecord> Quarantine=new List<DevicePackRecord>();
        [DataMember] public DevicePackRecord Current,Previous;
        [DataMember] public DateTime LastCheckUtc=DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc),LastSuccessUtc=DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc),ActivatedUtc=DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc);
        [DataMember] public string LastSource,Result,LastRejection,LastFailure;
        [DataMember] public int Failures;
    }
    // A signed pair remains write/delete locked until the engine's ready reply.
    // The worker then owns an immutable in-memory snapshot (including children).
    internal sealed class DevicePackLease:IDisposable {
        internal string Path;internal byte[] Raw,Signature;internal DevicePackRecord Record;internal DevicePackDocument Document;
        FileStream json,sig;
        internal static DevicePackLease Open(string path,IDictionary<string,string> keys){
            var lease=new DevicePackLease {Path=path};
            try{
                DevicePackFiles.NoLinks(path);DevicePackFiles.NoLinks(path+".sig");
                lease.json=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
                lease.sig=new FileStream(path+".sig",FileMode.Open,FileAccess.Read,FileShare.Read);
                lease.Raw=Read(lease.json,DevicePackTrust.MaxPackBytes);lease.Signature=Read(lease.sig,DevicePackTrust.MaxMetadataBytes);
                DevicePackTrust.Verify(lease.Raw,lease.Signature,keys);lease.Document=DevicePackValidation.Validate(lease.Raw);return lease;
            }catch{lease.Dispose();throw;}
        }
        static byte[] Read(FileStream stream,int limit){if(stream.Length<1||stream.Length>limit)throw new InvalidDataException("pack_file_size");using(var memory=new MemoryStream()){stream.CopyTo(memory);return memory.ToArray();}}
        public void Dispose(){if(json!=null)json.Dispose();if(sig!=null)sig.Dispose();}
    }
    internal sealed class DevicePackManager {
        internal const string NotUsedDiagnostic="未使用型号数据包";
        static readonly HashSet<string> RejectionCodes=new HashSet<string>((
            "pack_const pack_datetime pack_digest_mismatch pack_download_size pack_duplicate_key pack_entry_limit pack_enum pack_file_size pack_integer pack_integer_range "+
            "pack_json pack_json_escape pack_linked_path pack_local_identity pack_missing_field pack_non_printable pack_previous_invalid pack_profile_revision pack_quarantined "+
            "pack_ready_rejected pack_redirect pack_rollback_refused pack_schema_missing pack_schema_type pack_signature_format pack_signature_invalid pack_signature_size pack_size "+
            "pack_source pack_string_format pack_string_length pack_trial_failed pack_trial_path pack_trial_timeout pack_unknown_field pack_unknown_key pack_url pack_version_digest_conflict pack_version_mismatch "+
            "pointer_fields pointer_format pointer_loader pointer_size pointer_value").Split(' '),StringComparer.Ordinal);
        static readonly HashSet<string> ResultCodes=new HashSet<string>((
            "pack_cancelled pack_check_failed pack_checking pack_crash_rollback pack_download_truncated pack_enabled pack_engine_stop_failed pack_network_failed pack_not_found "+
            "pack_startup_failed pack_startup_rollback pack_state pack_state_active pack_state_duplicate pack_state_field pack_state_floor pack_state_full pack_state_invalid pack_state_missing "+
            "pack_state_quarantine pack_state_record pack_status_write_failed pack_storage_failed pack_timeout pack_trial_stop_failed pack_unchanged").Split(' '),StringComparer.Ordinal);
        readonly object gate=new object();readonly string root;readonly IUpdateTransport http;readonly IDictionary<string,string> keys;
        readonly Func<string,long,Func<string>,CancellationToken,bool> activate;readonly DateTime started;
        DevicePackState state=new DevicePackState();bool valid=true,checkedThisSession;static int checking;
        internal DevicePackManager(string directory,IUpdateTransport transport,Func<string,long,Func<string>,CancellationToken,bool> activation,DateTime start)
            :this(directory,transport,activation,start,DevicePackTrust.Keys()){}
        DevicePackManager(string directory,IUpdateTransport transport,Func<string,long,Func<string>,CancellationToken,bool> activation,DateTime start,IDictionary<string,string> trust){
            root=Path.GetFullPath(directory);http=transport;activate=activation;started=start;keys=trust;
            try{
                DevicePackFiles.NoLinks(root);foreach(string slot in new[]{"current","previous","staging"})DevicePackFiles.NoLinks(Path.Combine(root,slot));
                string path=Path.Combine(root,"state.json");DevicePackFiles.NoLinks(path);
                if(File.Exists(path)){
                    byte[] bytes=UpdateJson.ReadBytes(path,4*1024*1024);var data=PackJson.Object(PackJson.Parse(bytes));
                    foreach(string field in new[]{"Schema","HighestAcceptedVersion","Versions","Quarantine","Current","Previous","LastCheckUtc","LastSuccessUtc","ActivatedUtc","Failures"})if(!data.ContainsKey(field))throw new InvalidDataException("pack_state_field");
                    state=UpdateJson.Read<DevicePackState>(bytes);ValidateState();
                    // Older clients also stored transport failures as rejections.
                    if(!String.IsNullOrEmpty(state.LastRejection)&&!RejectionCodes.Contains(state.LastRejection)){
                        if(state.LastRejection=="pack_http_404"){if(state.Result=="pack_http_404")state.Result="pack_not_found";}
                        else state.LastFailure=SafeResult(state.LastRejection);
                        state.LastRejection=null;
                    }
                }else if(Directory.Exists(Path.Combine(root,"current"))&&Directory.GetFileSystemEntries(Path.Combine(root,"current")).Length!=0||Directory.Exists(Path.Combine(root,"previous"))&&Directory.GetFileSystemEntries(Path.Combine(root,"previous")).Length!=0)throw new InvalidDataException("pack_state_missing");
            }catch(Exception){state=new DevicePackState {Result="pack_state_invalid"};valid=false;}
        }
#if DEVICE_PACK_TESTS
        internal static DevicePackManager ForTests(string directory,IUpdateTransport transport,Func<string,long,Func<string>,CancellationToken,bool> activation,DateTime start,IDictionary<string,string> trust){return new DevicePackManager(directory,transport,activation,start,trust);}
        internal DevicePackState StateForTests {get{lock(gate)return UpdateJson.Read<DevicePackState>(Encoding.UTF8.GetBytes(UpdateJson.Text(state)));}}
#endif
        void ValidateRecord(DevicePackRecord record){if(record==null||record.Version<1||record.Version>9999999999L||!Regex.IsMatch(record.Sha256??"",@"\A[a-f0-9]{64}\z")||(record.Source!="mirror"&&record.Source!="github")||record.Disabled<0||record.Disabled>2000)throw new InvalidDataException("pack_state_record");}
        void ValidateState(){
            if(state==null||state.Schema!=1||state.HighestAcceptedVersion<0||state.HighestAcceptedVersion>9999999999L||state.Versions==null||state.Quarantine==null||state.Failures<0||state.Failures>3)throw new InvalidDataException("pack_state");
            var seen=new HashSet<long>();foreach(var record in state.Versions){ValidateRecord(record);if(!seen.Add(record.Version))throw new InvalidDataException("pack_state_duplicate");}
            if(state.HighestAcceptedVersion>0&&!seen.Contains(state.HighestAcceptedVersion))throw new InvalidDataException("pack_state_floor");
            foreach(var record in state.Quarantine){ValidateRecord(record);if(!Known(record))throw new InvalidDataException("pack_state_quarantine");}
            foreach(var record in new[]{state.Current,state.Previous})if(record!=null){ValidateRecord(record);if(!Known(record)||record.Version>state.HighestAcceptedVersion)throw new InvalidDataException("pack_state_active");}
        }
        bool Known(DevicePackRecord record){return state.Versions.Exists(delegate(DevicePackRecord value){return value.Version==record.Version&&value.Sha256==record.Sha256;});}
        bool Quarantined(DevicePackRecord record){return state.Quarantine.Exists(delegate(DevicePackRecord value){return value.Version==record.Version||value.Sha256==record.Sha256;});}
        string Slot(string slot,DevicePackRecord record){string path=UpdatePaths.Resolve(root,slot+"/"+record.FileName);DevicePackFiles.NoLinks(path);return path;}
        void Save(){
            if(!valid)throw new InvalidDataException("pack_state_invalid");
            byte[] bytes=Encoding.UTF8.GetBytes(UpdateJson.Text(state));if(bytes.Length>4*1024*1024)throw new InvalidDataException("pack_state_full");Write(Path.Combine(root,"state.json"),bytes);
        }
        internal static void Write(string path,byte[] bytes){
            DevicePackFiles.NoLinks(path);string directory=Path.GetDirectoryName(path);Directory.CreateDirectory(directory);DevicePackFiles.NoLinks(directory);
            string temp=Path.Combine(directory,".pack-"+Guid.NewGuid().ToString("N")+".tmp");
            try{using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(bytes,0,bytes.Length);file.Flush(true);}
                DevicePackFiles.NoLinks(path);if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
            }finally{DevicePackFiles.NoLinks(temp);if(File.Exists(temp))File.Delete(temp);}
        }
        void Pair(string slot,DevicePackRecord record,byte[] raw,byte[] sig){Write(Slot(slot,record),raw);Write(Slot(slot,record)+".sig",sig);}
        DevicePackLease Open(string slot,DevicePackRecord record){
            if(record==null||Quarantined(record))return null;
            var lease=DevicePackLease.Open(Slot(slot,record),keys);
            if(lease.Document.Version!=record.Version||DevicePackTrust.Hash(lease.Raw)!=record.Sha256){lease.Dispose();throw new InvalidDataException("pack_local_identity");}
            lease.Record=record;return lease;
        }
        void Quarantine(DevicePackRecord record,string reason){
            if(record!=null&&!Quarantined(record))state.Quarantine.Add(new DevicePackRecord {Version=record.Version,Sha256=record.Sha256,Source=record.Source,Disabled=record.Disabled,Reason=reason});
        }
        void Rollback(string reason){
            Quarantine(state.Current,reason);DevicePackRecord next=null;
            try{using(var previous=Open("previous",state.Previous)){if(previous!=null){Pair("current",previous.Record,previous.Raw,previous.Signature);next=previous.Record;}}}
            catch(Exception){Quarantine(state.Previous,"pack_previous_invalid");}
            state.Current=next;state.Previous=null;state.Failures=0;state.ActivatedUtc=DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc);state.Result=reason;Save();
        }
        internal DevicePackLease OpenForStart(){lock(gate){
            if(!valid)return null;
            try{var lease=Open("current",state.Current);if(lease!=null)return lease;if(state.Current==null&&state.Previous==null)return null;}
            catch(Exception){}
            try{Rollback("pack_startup_rollback");return Open("current",state.Current);}catch(Exception){valid=false;state.Result="pack_storage_failed";return null;}
        }}
        internal DevicePackLease LockTrial(string path){lock(gate){
            if(!valid||!String.Equals(Path.GetDirectoryName(Path.GetFullPath(path)),Path.Combine(root,"staging"),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("pack_trial_path");
            return DevicePackLease.Open(path,keys);
        }}
        internal void StartupRejected(long version){lock(gate){if(valid&&state.Current!=null&&state.Current.Version==version)try{Rollback("pack_ready_rejected");}catch(Exception){valid=false;state.Result="pack_storage_failed";}}}
        internal bool EngineFailed(long version,DateTime now){lock(gate){
            if(!valid||state.Current==null||state.Current.Version!=version||state.ActivatedUtc==DateTime.MinValue||now<state.ActivatedUtc||now>state.ActivatedUtc.AddMinutes(10))return false;
            try{state.Failures++;if(state.Failures>=3){Rollback("pack_crash_rollback");return true;}Save();}catch(Exception){valid=false;state.Result="pack_storage_failed";}return false;
        }}
        internal void SetLoaded(long version,int disabled){lock(gate){if(state.Current!=null&&state.Current.Version==version)state.Current.Disabled=Math.Max(0,Math.Min(2000,disabled));}}
        internal string Diagnostic {get{lock(gate){
            var text=new StringBuilder(state.Current==null||!valid?NotUsedDiagnostic:"型号数据包："+state.Current.Version+" | 来源："+(state.Current.Source=="mirror"?"镜像":"GitHub")+" | 禁用条目："+state.Current.Disabled);
            text.Append(" | 上次成功检查：").Append(state.LastSuccessUtc==DateTime.MinValue?"未成功":state.LastSuccessUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'",CultureInfo.InvariantCulture));
            text.Append(" | 隔离版本：");if(state.Quarantine.Count==0)text.Append("无");else{int count=0;foreach(var item in state.Quarantine){if(count++==6){text.Append(" 等").Append(state.Quarantine.Count).Append("版");break;}if(count>1)text.Append(",");text.Append(item.Version);}}
            if(!String.IsNullOrEmpty(state.Result))text.Append(" | 结果：").Append(state.Result=="pack_not_found"?"远端暂无数据包":SafeResult(state.Result));
            if(!String.IsNullOrEmpty(state.LastFailure))text.Append(" | 上次失败：").Append(SafeResult(state.LastFailure));
            if(!String.IsNullOrEmpty(state.LastRejection))text.Append(" | 上次拒绝：").Append(SafeResult(state.LastRejection));return text.ToString();
        }}}
        static string SafeResult(string value){return value!=null&&(RejectionCodes.Contains(value)||ResultCodes.Contains(value)||Regex.IsMatch(value,@"\Apack_http_[1-5][0-9]{2}\z"))?value:"pack_check_failed";}
        static string FailureCode(Exception error){
            var network=error as WebException;if(network!=null)return network.Status==WebExceptionStatus.Timeout?"pack_timeout":"pack_network_failed";
            if(error is OperationCanceledException)return "pack_cancelled";
            if(error is TimeoutException&&!RejectionCodes.Contains(error.Message))return "pack_timeout";
            return SafeResult(error.Message);
        }
        void RememberFailure(string reason){
            if(RejectionCodes.Contains(reason))state.LastRejection=reason;
            else if(reason!="pack_not_found"&&reason!="pack_cancelled")state.LastFailure=reason;
        }
        bool Candidate(DevicePackPointer pointer){
            var known=state.Versions.Find(delegate(DevicePackRecord item){return item.Version==pointer.Version;});
            if(known!=null&&known.Sha256!=pointer.Hash)throw new InvalidDataException("pack_version_digest_conflict");
            if(pointer.Version<state.HighestAcceptedVersion)throw new InvalidDataException("pack_rollback_refused");
            if(known!=null&&Quarantined(known))throw new InvalidDataException("pack_quarantined");
            if(pointer.Version==state.HighestAcceptedVersion){try{using(var usable=Open("current",state.Current)){if(usable!=null)return false;}}catch(Exception){}}
            return true;
        }
        internal void Check(bool automatic,DateTime now,CancellationToken token){
            if(Interlocked.CompareExchange(ref checking,1,0)!=0)return;
            DevicePackRecord candidate=null;bool promoted=false;string stage=null;
            try{
                lock(gate){if(!valid||automatic&&(!checkedThisSession?now<started.AddSeconds(20):now<state.LastCheckUtc.AddHours(12)))return;
                    checkedThisSession=true;state.LastCheckUtc=now;state.Result="pack_checking";Save();}
                byte[] raw=null,sig=null;string lastError="pack_check_failed";
                foreach(string source in new[]{DevicePackTrust.Mirror,DevicePackTrust.GitHub}){
                    token.ThrowIfCancellationRequested();
                    bool pointerFetched=false;
                    try{
                        lock(gate)state.LastSource=source==DevicePackTrust.Mirror?"mirror":"github";
                        var pointer=DevicePackPointer.Parse(DevicePackNetwork.Fetch(http,source,"device-pack-v1-latest.json",4096,token));
                        pointerFetched=true;
                        lock(gate){state.LastSource=source==DevicePackTrust.Mirror?"mirror":"github";if(!Candidate(pointer)){state.LastSuccessUtc=now;state.Result="pack_unchanged";Save();return;}}
                        raw=DevicePackNetwork.Fetch(http,source,pointer.FileName,DevicePackTrust.MaxPackBytes,token);
                        sig=DevicePackNetwork.Fetch(http,source,pointer.FileName+".sig",4096,token);
                        if(raw.Length!=pointer.Size||DevicePackTrust.Hash(raw)!=pointer.Hash)throw new InvalidDataException("pack_digest_mismatch");
                        DevicePackTrust.Verify(raw,sig,keys);var document=DevicePackValidation.Validate(raw);
                        if(document.Version!=pointer.Version)throw new InvalidDataException("pack_version_mismatch");
                        candidate=new DevicePackRecord {Version=document.Version,Sha256=pointer.Hash,Source=source==DevicePackTrust.Mirror?"mirror":"github",Disabled=document.Disabled};break;
                    }catch(OperationCanceledException){token.ThrowIfCancellationRequested();lastError="pack_timeout";lock(gate)RememberFailure(lastError);}
                    catch(Exception error){lastError=FailureCode(error);if(!pointerFetched&&lastError=="pack_http_404")lastError="pack_not_found";lock(gate)RememberFailure(lastError);}
                }
                if(candidate==null){lock(gate){state.Result=lastError;Save();}return;}
                lock(gate){
                    if(!Known(candidate))state.Versions.Add(candidate);Save(); // pin signed bytes even if the trial fails
                    stage=Slot("staging",candidate);Pair("staging",candidate,raw,sig);
                }
                Func<string> promote=delegate{lock(gate){
                    token.ThrowIfCancellationRequested();
                    // Verify from disk again and retain the staged handles during the copy.
                    var before=UpdateJson.Read<DevicePackState>(Encoding.UTF8.GetBytes(UpdateJson.Text(state)));
                    try{using(var staged=Open("staging",candidate)){
                        if(staged==null)throw new InvalidDataException("pack_quarantined");
                        DevicePackRecord previous=null;DevicePackLease old=null;
                        try{old=Open("current",state.Current);}catch(Exception){Quarantine(state.Current,"pack_previous_invalid");}
                        using(old){if(old!=null){Pair("previous",old.Record,old.Raw,old.Signature);previous=old.Record;}}
                        Pair("current",candidate,staged.Raw,staged.Signature);
                        state.Previous=previous;state.Current=candidate;state.HighestAcceptedVersion=Math.Max(state.HighestAcceptedVersion,candidate.Version);
                        state.ActivatedUtc=now;state.Failures=0;Save();promoted=true;return Slot("current",candidate);
                    }}catch{state=before;throw;}
                }};
                token.ThrowIfCancellationRequested();
                if(!activate(stage,candidate.Version,promote,token))throw new InvalidDataException("pack_trial_failed");
                lock(gate){state.LastSuccessUtc=now;state.Result="pack_enabled";try{Save();}catch(Exception){state.Result="pack_status_write_failed";}}
            }catch(Exception error){lock(gate){
                string reason=FailureCode(error);RememberFailure(reason);
                try{if(candidate!=null){if(promoted&&state.Current!=null&&state.Current.Version==candidate.Version)Rollback(reason);else Quarantine(candidate,reason);}state.Result=reason;Save();}
                catch(Exception){valid=false;state.Result="pack_storage_failed";}
            }}finally{
                if(stage!=null)try{foreach(string path in new[]{stage,stage+".sig"}){DevicePackFiles.NoLinks(path);if(File.Exists(path))File.Delete(path);}}catch(Exception){}
                lock(gate)if(valid)try{CleanSlot("current",state.Current);CleanSlot("previous",state.Previous);}catch(Exception){}
                Interlocked.Exchange(ref checking,0);
            }
        }
        void CleanSlot(string slot,DevicePackRecord keep){
            string directory=Path.Combine(root,slot);DevicePackFiles.NoLinks(directory);if(!Directory.Exists(directory))return;
            foreach(string path in Directory.GetFiles(directory)){
                string name=Path.GetFileName(path);if(!Regex.IsMatch(name,@"\Adevice-pack-v1-[0-9]{1,10}\.json(?:\.sig)?\z"))continue;
                if(keep!=null&&(name==keep.FileName||name==keep.FileName+".sig"))continue;
                DevicePackFiles.NoLinks(path);File.Delete(path);
            }
        }
    }
}
