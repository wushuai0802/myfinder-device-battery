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
        readonly object gate=new object();readonly string path;FeedbackData data;bool writable=true;
        internal static FeedbackStore Current {get{return current.Value;}}
        // A null path is an isolated, in-memory store for injected/offline callers.
        internal FeedbackStore(string file){path=file;data=Load();}
        static T Clone<T>(T value){return UpdateJson.Read<T>(Encoding.UTF8.GetBytes(UpdateJson.Text(value)));}
        internal FeedbackData Snapshot(){lock(gate)return Clone(data);}
        internal FeedbackRecord Find(long number){return Snapshot().Records.Find(delegate(FeedbackRecord r){return r.Number==number&&number>0;});}
        internal int Unread {get{lock(gate){int count=0;foreach(var r in data.Records)if(r.Number>0)count+=r.Unread;return count;}}}
        internal string MenuText {get{int count=Unread;return "我的反馈"+(count>0?"（"+count+"）":"");}}
        internal void Change(Action<FeedbackData> change){lock(gate){
            if(!writable)throw new IOException("Feedback storage is unavailable.");
            var next=Clone(data);change(next);Validate(next);Trim(next);Save(next);data=next;
        }}
        internal void MarkRead(long number){Change(delegate(FeedbackData next){var record=next.Records.Find(delegate(FeedbackRecord r){return r.Number==number;});if(record!=null)foreach(var c in record.Comments)if(!record.ReadIds.Contains(c.Id))record.ReadIds.Add(c.Id);});}
        internal void Delete(long number){Change(delegate(FeedbackData next){next.Records.RemoveAll(delegate(FeedbackRecord r){return r.Number==number;});});}
        internal string BeginCreation(string category,string description,string key,DateTime now){
            string id=Guid.NewGuid().ToString("D");string summary=FeedbackText.Plain(description.Trim()).Replace('\n',' ').Replace('\t',' ');
            Change(delegate(FeedbackData next){next.Records.Add(new FeedbackRecord {CreationId=id,Category=category,Summary=FeedbackInput.Prefix(summary,Math.Min(60,summary.Length)),ReplyKey=key,CreatedAt=now,LastActivityAt=now,NextCheckAt=now});});return id;
        }
        internal void CompleteCreation(string id,FeedbackResult result,DateTime now){Change(delegate(FeedbackData next){
            var record=next.Records.Find(delegate(FeedbackRecord r){return r.CreationId==id;});if(record==null)throw new InvalidDataException("Missing feedback creation.");
            next.Records.RemoveAll(delegate(FeedbackRecord r){return r!=record&&r.Number==result.Number;});
            record.Number=result.Number;record.Url=result.Url;record.CreationId=null;if(!result.ReplyAccess)record.ReplyKey=null;
            record.CanSupplement=result.ReplyAccess;record.NextCheckAt=now.AddHours(6);
        });}
        FeedbackData Load(){
            if(path==null)return new FeedbackData();
            try{
                if(!File.Exists(path))return new FeedbackData();
                byte[] encrypted=UpdateJson.ReadBytes(path,128*1024*1024);
                var value=UpdateJson.Read<FeedbackData>(ProtectedData.Unprotect(encrypted,entropy,DataProtectionScope.CurrentUser));Validate(value);Trim(value);return value;
            }catch(Exception){
                // Do not overwrite unreadable data if preserving it also failed.
                try{if(File.Exists(path)){string bad=path+".bad";if(File.Exists(bad))bad=path+"."+Guid.NewGuid().ToString("N")+".bad";File.Copy(path,bad,false);}}
                catch(Exception){writable=false;}
                return new FeedbackData();
            }
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
            while(value.Records.Count>50){
                FeedbackRecord oldest=null;
                foreach(var r in value.Records)if(oldest==null||(r.State=="closed"&&oldest.State!="closed")||(r.State==oldest.State&&r.CreatedAt<oldest.CreatedAt))oldest=r;
                value.Records.Remove(oldest);
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
