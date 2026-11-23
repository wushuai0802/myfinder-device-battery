using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace AtkBattery {
    [DataContract] internal sealed class FeedbackComment {
        [DataMember(Name="id",IsRequired=true)] public string Id;
        [DataMember(Name="createdAt",IsRequired=true)] public string CreatedAt;
        [DataMember(Name="updatedAt",IsRequired=true)] public string UpdatedAt;
        [DataMember(Name="author",IsRequired=true)] public string Author;
        [DataMember(Name="text",IsRequired=true)] public string Text;
        [DataMember(Name="truncated",IsRequired=true)] public bool Truncated;
    }
    [DataContract] internal sealed class FeedbackSupplement {
        [DataMember] public string RequestId;
        [DataMember] public string Description;
        [DataMember] public string Diagnostics;
        [DataMember] public string AppVersion;
        [DataMember] public DateTime? RetryAt;
    }
    [DataContract] internal sealed class FeedbackRecord {
        [DataMember] public long Number;
        [DataMember] public string Url;
        [DataMember] public DateTime CreatedAt;
        [DataMember] public string Category;
        [DataMember] public string Summary;
        [DataMember] public string ReplyKey;
        // A creation is saved before HTTP. Without a receipt it is never retried automatically.
        [DataMember] public string CreationId;
        [DataMember] public string State="open";
        [DataMember] public bool Locked;
        [DataMember] public bool CanSupplement;
        [DataMember] public DateTime? LastCheckedAt;
        [DataMember] public DateTime? NextCheckAt;
        [DataMember] public DateTime LastActivityAt;
        [DataMember] public DateTime? ClosedAt;
        [DataMember] public bool ClosedFollowupDone;
        [DataMember] public int Failures;
        [DataMember] public List<FeedbackComment> Comments=new List<FeedbackComment>();
        [DataMember] public List<string> ReadIds=new List<string>();
        [DataMember] public List<string> NotifiedIds=new List<string>();
        [DataMember] public FeedbackSupplement Pending;
        internal bool MaySupplement {get{return ReplyKey!=null&&CanSupplement&&!Locked&&State=="open";}}
        internal int Unread {get{return Comments.FindAll(delegate(FeedbackComment c){return !ReadIds.Contains(c.Id);}).Count;}}
        internal string ListText {get{return "#"+Number+" · "+FeedbackInput.CategoryLabel(Category)+" · "+Summary+" · "+(State=="closed"?"已关闭":"处理中")+(Unread>0?" · ●":"");}}
    }
    [DataContract] internal sealed class FeedbackData {
        [DataMember] public int Schema=1;
        [DataMember] public List<FeedbackRecord> Records=new List<FeedbackRecord>();
        [DataMember] public DateTime? LastBalloonAt;
        [DataMember] public DateTime? PollNotBefore;
    }
    internal static class FeedbackText {
        internal static string Plain(string value){
            var output=new StringBuilder();
            foreach(char c in (value??"").Replace("\r\n","\n")){
                if((c>='\u202a'&&c<='\u202e')||(c>='\u2066'&&c<='\u2069'))output.Append('\ufffd');
                else if(c=='\n'||c=='\t'||!Char.IsControl(c))output.Append(c);
            }
            return output.ToString();
        }
        internal static string Comments(FeedbackRecord record){
            var text=new StringBuilder();
            if(record.ReplyKey==null)text.AppendLine("此反馈只能在网页查看，补充请重新提交反馈并在描述里写上 #"+record.Number).AppendLine();
            if(record.Comments.Count==0)text.AppendLine("暂无回复。");
            foreach(var comment in record.Comments){
                DateTimeOffset time;
                string local=DateTimeOffset.TryParse(comment.CreatedAt,CultureInfo.InvariantCulture,DateTimeStyles.None,out time)?time.ToLocalTime().ToString("yyyy-MM-dd HH:mm"):"时间未知";
                text.AppendLine((comment.Author=="maintainer"?"维护者":"自动初筛")+" · "+local);
                text.AppendLine(Plain(comment.Text).Replace("\n",Environment.NewLine));
                if(comment.Truncated)text.AppendLine("（内容过长，已截断）");
                text.AppendLine();
            }
            return text.ToString();
        }
    }
    internal sealed class FeedbackStore {
        static readonly Lazy<FeedbackStore> current=new Lazy<FeedbackStore>(delegate{return new FeedbackStore(Path.Combine(DeviceSettings.DataDirectory,"feedback-v1.dat"));});
        static readonly byte[] entropy=Encoding.UTF8.GetBytes("MyFinderDeviceBattery/feedback-v1");
        readonly object gate=new object();readonly string path;FeedbackData data;bool writable=true,unreadable,backedUp;string resetReason;
        internal event EventHandler Changed;
        internal static FeedbackStore Current {get{return current.Value;}}
        // A null path is an isolated, in-memory store for injected/offline callers.
        internal FeedbackStore(string file,Func<DateTime> now=null){path=file;data=Load((now??delegate{return DateTime.UtcNow;})());}
        static T Clone<T>(T value){return UpdateJson.Read<T>(Encoding.UTF8.GetBytes(UpdateJson.Text(value)));}
        internal FeedbackData Snapshot(){lock(gate)return Clone(data);}
        internal FeedbackRecord Find(long number){return Snapshot().Records.Find(delegate(FeedbackRecord r){return r.Number==number&&number>0;});}
        internal int Unread {get{lock(gate){int count=0;foreach(var r in data.Records)if(r.Number>0)count+=r.Unread;return count;}}}
        internal string MenuText {get{int count=Unread;return "我的反馈"+(count>0?"（"+count+"）":"");}}
        internal bool Unreadable {get{lock(gate)return unreadable;}}
        internal bool Writable {get{lock(gate)return writable;}}
        internal bool BackedUp {get{lock(gate)return backedUp;}}
        internal string ResetReason {get{lock(gate)return backedUp?resetReason:null;}}
        internal string LoadFailureKind {get{lock(gate)return resetReason;}}
        internal string LoadWarning {get{lock(gate){
            if(!unreadable)return "";
            return backedUp?"之前的反馈记录无法读取，已另存为备份。已提交的反馈仍可在网页查看。":"之前的反馈记录无法读取，未能另存为备份。暂时无法保存本地记录。已提交的反馈仍可在网页查看。";
        }}}
        internal string Diagnostic {get{lock(gate){
            int count=0;foreach(var record in data.Records)if(record.Number>0)count++;
            string state=!writable?"不可写":resetReason==null?"正常":"已重置（"+resetReason+"）";
            return "反馈记录："+count.ToString(CultureInfo.InvariantCulture)+" 条 | 存储："+state;
        }}}
        internal void Change(Action<FeedbackData> change){lock(gate){
            if(!writable)throw new IOException("Feedback storage is unavailable.");
            var next=Clone(data);change(next);Commit(next);
        }NotifyChanged();}
        void Commit(FeedbackData next){
            Validate(next);Trim(next);
            try{Save(next);}catch(Exception){writable=false;throw new IOException("Feedback storage is unavailable.");}
            data=next;
        }
        void NotifyChanged(){
            var handlers=Changed;if(handlers==null)return;
            foreach(EventHandler handler in handlers.GetInvocationList())try{handler(this,EventArgs.Empty);}catch(Exception){}
        }
        internal void MarkRead(long number){
            lock(gate){
                var record=data.Records.Find(delegate(FeedbackRecord r){return r.Number==number;});
                if(record==null||!record.Comments.Exists(delegate(FeedbackComment c){return !record.ReadIds.Contains(c.Id)||!record.NotifiedIds.Contains(c.Id);}))return;
                if(!writable)throw new IOException("Feedback storage is unavailable.");
                var next=Clone(data);record=next.Records.Find(delegate(FeedbackRecord r){return r.Number==number;});
                foreach(var c in record.Comments){
                    if(!record.ReadIds.Contains(c.Id))record.ReadIds.Add(c.Id);
                    if(!record.NotifiedIds.Contains(c.Id))record.NotifiedIds.Add(c.Id);
                }
                Commit(next);
            }
            NotifyChanged();
        }
        internal void Delete(long number){Change(delegate(FeedbackData next){next.Records.RemoveAll(delegate(FeedbackRecord r){return r.Number==number;});});}
        internal string BeginCreation(string category,string description,string key,DateTime now){
            string id=Guid.NewGuid().ToString("D");string summary=FeedbackText.Plain(description.Trim()).Replace('\n',' ').Replace('\t',' ');
            Change(delegate(FeedbackData next){next.Records.Add(new FeedbackRecord {CreationId=id,Category=category,Summary=FeedbackInput.Prefix(summary,Math.Min(60,summary.Length)),ReplyKey=key,CreatedAt=now,LastActivityAt=now,NextCheckAt=now});});return id;
        }
        internal void CancelCreation(string id){Change(delegate(FeedbackData next){next.Records.RemoveAll(delegate(FeedbackRecord r){return r.Number==0&&r.CreationId==id;});});}
        internal void CompleteCreation(string id,FeedbackResult result,DateTime now){Change(delegate(FeedbackData next){
            var record=next.Records.Find(delegate(FeedbackRecord r){return r.CreationId==id;});if(record==null)throw new InvalidDataException("Missing feedback creation.");
            next.Records.RemoveAll(delegate(FeedbackRecord r){return r!=record&&r.Number==result.Number;});
            record.Number=result.Number;record.Url=result.Url;record.CreationId=null;if(!result.ReplyAccess)record.ReplyKey=null;
            record.CanSupplement=result.ReplyAccess;record.NextCheckAt=now.AddHours(6);
        });}
        FeedbackData Load(DateTime now){
            if(path==null)return new FeedbackData();
            byte[] encrypted;
            try{
                encrypted=UpdateJson.ReadBytes(path,128*1024*1024);
            }catch(FileNotFoundException){return new FeedbackData();}
            catch(DirectoryNotFoundException){return new FeedbackData();}
            catch(InvalidDataException){return Recover("format");}
            catch(Exception){return Recover("io");}
            byte[] serialized;
            try{serialized=ProtectedData.Unprotect(encrypted,entropy,DataProtectionScope.CurrentUser);}
            catch(Exception){return Recover("decrypt");}
            try{
                var value=UpdateJson.Read<FeedbackData>(serialized);Validate(value);
                value.Records.RemoveAll(delegate(FeedbackRecord r){return r.Number==0&&now-r.CreatedAt>TimeSpan.FromHours(24);});Trim(value);return value;
            }catch(Exception){return Recover("format");}
        }
        FeedbackData Recover(string reason){
            unreadable=true;resetReason=reason;
            // Only a completed move establishes a backup and a reset. An input
            // that could not be preserved must never be overwritten.
            try{
                string bad=path+".bad";if(File.Exists(bad))bad=path+"."+Guid.NewGuid().ToString("N")+".bad";
                File.Move(path,bad);backedUp=true;
            }catch(Exception){writable=false;}
            return new FeedbackData();
        }
        void Save(FeedbackData value){
            if(path==null)return;
            byte[] bytes=ProtectedData.Protect(Encoding.UTF8.GetBytes(UpdateJson.Text(value)),entropy,DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{
                using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(bytes,0,bytes.Length);file.Flush(true);}
                if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
            }finally{if(File.Exists(temp))File.Delete(temp);}
        }
        static void Trim(FeedbackData value){
            var creations=new HashSet<string>();
            for(int i=value.Records.Count-1;i>=0;i--)if(value.Records[i].Number==0&&!creations.Add(value.Records[i].CreationId))value.Records.RemoveAt(i);
            // Pending creations never use a completed record's slot or discard its credential.
            var records=value.Records.FindAll(delegate(FeedbackRecord r){return r.Number>0;});
            while(records.Count>50){
                FeedbackRecord oldest=null;
                foreach(var r in records)if(oldest==null||(r.State=="closed"&&oldest.State!="closed")||(r.State==oldest.State&&r.CreatedAt<oldest.CreatedAt))oldest=r;
                records.Remove(oldest);value.Records.Remove(oldest);
            }
        }
        static void Validate(FeedbackData value){
            if(value==null||value.Schema!=1||value.Records==null||value.Records.Count>1000)throw new InvalidDataException("Invalid feedback store.");
            var numbers=new HashSet<long>();
            foreach(var r in value.Records){
                if(r==null||r.Number<0||r.Number>9007199254740991L||(r.Number>0&&(!FeedbackClient.IsIssueUrl(r.Number,r.Url)||!numbers.Add(r.Number)))||(r.Number==0&&String.IsNullOrEmpty(r.CreationId)))throw new InvalidDataException("Invalid feedback record.");
                FeedbackInput.CategoryLabel(r.Category);
                if(r.Summary==null||r.Summary.Length>60||(r.ReplyKey!=null&&!FeedbackProtocol.ValidKey(r.ReplyKey))||(r.State!="open"&&r.State!="closed")||r.Comments==null||r.Comments.Count>50||r.ReadIds==null||r.NotifiedIds==null)throw new InvalidDataException("Invalid feedback record.");
                r.Summary=FeedbackText.Plain(r.Summary);
                var ids=new HashSet<string>();foreach(var c in r.Comments){FeedbackProtocol.ValidateComment(c);if(!ids.Add(c.Id))throw new InvalidDataException("Duplicate comment.");c.Text=FeedbackText.Plain(c.Text);}
                if(r.Pending!=null&&(!FeedbackProtocol.ValidRequestId(r.Pending.RequestId)||!FeedbackInput.Valid(r.Category,r.Pending.Description)||r.Pending.Diagnostics!=null&&r.Pending.Diagnostics.Length>20000))throw new InvalidDataException("Invalid pending supplement.");
            }
        }
    }
}
