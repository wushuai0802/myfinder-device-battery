using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AtkBattery {
    internal static class FeedbackProtocol {
        internal static string NewKey(){var bytes=new byte[32];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(bytes);return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');}
        internal static bool ValidKey(string key){return key!=null&&Regex.IsMatch(key,@"\A[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]\z");}
        internal static bool ValidRequestId(string id){return id!=null&&Regex.IsMatch(id,@"\A[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\z");}
        internal static bool ValidId(string id){return id!=null&&Regex.IsMatch(id,@"\A[1-9][0-9]{0,19}\z");}
        internal static byte[] Json<T>(T value){return Encoding.UTF8.GetBytes(UpdateJson.Text(value));}
        internal static byte[] Read(UpdateResponse response,int limit,CancellationToken token){
            if(response.Body==null||response.Length>limit)throw new InvalidDataException("Invalid feedback response.");
            using(var output=new MemoryStream()){
                var buffer=new byte[4096];int total=0;
                while(true){
                    if(total==limit){if(response.Length!=limit)throw new InvalidDataException("Feedback response is too large.");break;}
                    token.ThrowIfCancellationRequested();int count=response.Body.Read(buffer,0,Math.Min(buffer.Length,limit-total));token.ThrowIfCancellationRequested();
                    if(count==0)break;total+=count;output.Write(buffer,0,count);
                }
                if(response.Length>=0&&response.Length!=total)throw new InvalidDataException("Truncated feedback response.");return output.ToArray();
            }
        }
        internal static DateTime? RetryAt(string value,DateTime now){
            long seconds;DateTimeOffset date;
            if(Int64.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out seconds)&&seconds>=0)return now.AddSeconds(Math.Min(seconds,(long)(DateTime.MaxValue-now).TotalSeconds-1));
            if(DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out date)&&date.UtcDateTime>now)return date.UtcDateTime;
            return null;
        }
        internal static void ValidateComment(FeedbackComment comment){
            DateTimeOffset created,updated;
            if(comment==null||!ValidId(comment.Id)||(comment.Author!="maintainer"&&comment.Author!="automation")||comment.Text==null||comment.Text.Length>8000
                ||!DateTimeOffset.TryParse(comment.CreatedAt,CultureInfo.InvariantCulture,DateTimeStyles.None,out created)||!DateTimeOffset.TryParse(comment.UpdatedAt,CultureInfo.InvariantCulture,DateTimeStyles.None,out updated))throw new InvalidDataException("Invalid feedback comment.");
        }
    }
    [DataContract] internal sealed class FeedbackError {
        [DataMember(Name="error",IsRequired=true)] public string Error;
    }
    [DataContract] internal sealed class FeedbackRepliesRequest {
        [DataMember(Name="schema")] public int Schema=1;
        [DataMember(Name="numbers")] public long[] Numbers;
    }
    [DataContract] internal sealed class FeedbackRepliesResponse {
        [DataMember(Name="schema",IsRequired=true)] public int Schema;
        [DataMember(Name="items",IsRequired=true)] public FeedbackReplyItem[] Items;
    }
    [DataContract] internal sealed class FeedbackReplyItem {
        [DataMember(Name="number",IsRequired=true)] public long Number;
        [DataMember(Name="status",IsRequired=true)] public string Status;
        [DataMember(Name="state")] public string State;
        [DataMember(Name="locked")] public bool? Locked;
        [DataMember(Name="canSupplement")] public bool? CanSupplement;
        [DataMember(Name="comments")] public FeedbackComment[] Comments;
        internal void Validate(){
            if(Status=="error"||Status=="unavailable")return;
            if(Status!="ok"||(State!="open"&&State!="closed")||!Locked.HasValue||!CanSupplement.HasValue||Comments==null||Comments.Length>50)throw new InvalidDataException("Invalid feedback item.");
            var ids=new HashSet<string>();foreach(var comment in Comments){FeedbackProtocol.ValidateComment(comment);if(!ids.Add(comment.Id))throw new InvalidDataException("Duplicate comment.");}
        }
    }
    [DataContract] internal sealed class FeedbackSupplementRequest {
        [DataMember(Name="schema")] public int Schema=1;
        [DataMember(Name="number")] public long Number;
        [DataMember(Name="replyKey")] public string ReplyKey;
        [DataMember(Name="requestId")] public string RequestId;
        [DataMember(Name="description")] public string Description;
        [DataMember(Name="diagnostics")] public string Diagnostics;
        [DataMember(Name="appVersion")] public string AppVersion;
    }
    [DataContract] internal sealed class FeedbackSupplementReceipt {
        [DataMember(Name="number",IsRequired=true)] public long Number;
        [DataMember(Name="commentId",IsRequired=true)] public string CommentId;
        [DataMember(Name="duplicate",IsRequired=true)] public bool Duplicate;
    }
    internal enum SupplementKind {Success,Unknown,Denied,Closed,RateLimited,Unavailable,Invalid,TooLarge,LocalFailure,Busy}
    internal sealed class SupplementResult {
        internal readonly SupplementKind Kind;internal readonly DateTime? RetryAt;
        internal SupplementResult(SupplementKind kind,DateTime? retry=null){Kind=kind;RetryAt=retry;}
        internal string Message {get{
            switch(Kind){
                case SupplementKind.Success:return "补充资料已提交。";
                case SupplementKind.Denied:return "无法补充此反馈，请在网页查看或重新提交反馈。";
                case SupplementKind.Closed:return "此反馈已关闭、锁定或无法继续补充。";
                case SupplementKind.RateLimited:return "补充太频繁，请稍后再试。";
                case SupplementKind.Unavailable:return "补充服务暂不可用，请稍后重试。";
                case SupplementKind.Invalid:return "补充内容不符合要求，请检查后重试。";
                case SupplementKind.TooLarge:return "补充内容过长，请缩短描述或取消附带诊断信息。";
                case SupplementKind.LocalFailure:return "无法保存本地记录，请稍后重试。";
                case SupplementKind.Busy:return "正在处理，请稍后重试。";
                default:return "正在确认，请稍后重试";
            }
        }}
    }
    internal static class FeedbackSchedule {
        internal static DateTime First(DateTime now,double random){return now.AddMinutes(1+9*Math.Max(0,Math.Min(1,random)));}
        internal static DateTime? Next(FeedbackRecord record,DateTime now,double random){
            double idle=(now-record.LastActivityAt).TotalDays;
            if(idle>=90)return null;
            if(record.State=="closed")return record.ClosedFollowupDone?(DateTime?)null:(record.ClosedAt??now).AddHours(24);
            return now.AddHours(idle>=30?168:6*(0.8+0.4*Math.Max(0,Math.Min(1,random))));
        }
        internal static void Failed(FeedbackRecord record,DateTime now,DateTime? retry){
            record.Failures=Math.Min(3,record.Failures+1);record.LastCheckedAt=now;
            double idle=(now-record.LastActivityAt).TotalDays;
            if(idle>=90||record.State=="closed"&&record.ClosedFollowupDone){record.NextCheckAt=null;return;}
            var next=now.AddMinutes(record.Failures==1?15:record.Failures==2?60:360);
            var normal=now.AddHours(idle>=30?168:record.State=="closed"?24:6);if(next<normal)next=normal;
            record.NextCheckAt=retry.HasValue&&retry.Value>next?retry:next;
        }
        internal static bool Due(FeedbackRecord record,DateTime now){
            if(record.Number<=0||!record.NextCheckAt.HasValue||record.NextCheckAt.Value>now||(now-record.LastActivityAt).TotalDays>=90)return false;
            return record.State!="closed"||!record.ClosedFollowupDone;
        }
    }
    internal sealed class FeedbackBalloon {
        internal readonly long Number;internal readonly int Count;
        internal FeedbackBalloon(long number,int count){Number=number;Count=count;}
        internal string Title {get{return "反馈有新回复";}}
        internal string Text {get{return Count==1?"#"+Number+" 有维护者回复":Count+" 条反馈有新回复";}}
    }
    internal sealed class FeedbackPollResult {
        internal string Message="已刷新。";internal FeedbackBalloon Balloon;
    }
    internal sealed class FeedbackReplies {
        internal readonly FeedbackStore Store;
        internal event Action<FeedbackBalloon> BalloonReady;
        readonly IFeedbackTransport http;readonly string relay;readonly Func<DateTime> clock;readonly Func<double> random;readonly Func<bool> enabled;
        readonly DateTime firstCheck;readonly int timeoutMilliseconds;
        int polling,supplementing;
        internal FeedbackReplies(FeedbackStore store,IFeedbackTransport transport,string address=null,Func<DateTime> now=null,Func<double> jitter=null,Func<bool> checkEnabled=null,int timeout=20000){
            Store=store;http=transport;relay=address??FeedbackRelay.Url;clock=now??delegate{return DateTime.UtcNow;};var rng=new Random();random=jitter??rng.NextDouble;
            enabled=checkEnabled??delegate{return DeviceSettings.Current.CheckFeedbackReplies&&!DeviceSettings.UsingDefaults;};firstCheck=FeedbackSchedule.First(clock(),random());timeoutMilliseconds=timeout;
        }
        internal static void Merge(FeedbackRecord record,FeedbackReplyItem item,DateTime now,double jitter){
            bool activity=false;var comments=new List<FeedbackComment>();
            foreach(var incoming in item.Comments){if(!record.Comments.Exists(delegate(FeedbackComment c){return c.Id==incoming.Id;}))activity=true;incoming.Text=FeedbackText.Plain(incoming.Text);comments.Add(incoming);}
            record.Comments=comments;if(activity)record.LastActivityAt=now;
            if(item.State=="closed"){
                if(record.State!="closed"||!record.ClosedAt.HasValue){record.ClosedAt=now;record.ClosedFollowupDone=false;}
                else if(now>=record.ClosedAt.Value.AddHours(24))record.ClosedFollowupDone=true;
            }else{record.ClosedAt=null;record.ClosedFollowupDone=false;}
            record.State=item.State;record.Locked=item.Locked.Value;record.CanSupplement=item.CanSupplement.Value&&item.State=="open"&&!item.Locked.Value;
            record.Failures=0;record.LastCheckedAt=now;record.NextCheckAt=FeedbackSchedule.Next(record,now,jitter);
        }
        internal static FeedbackBalloon MarkNotifications(FeedbackData data,DateTime now){
            if(data.LastBalloonAt.HasValue&&now<data.LastBalloonAt.Value.AddHours(6))return null;
            int count=0;long number=0;
            foreach(var record in data.Records){
                bool changed=false;
                foreach(var comment in record.Comments)if(comment.Author=="maintainer"&&!record.NotifiedIds.Contains(comment.Id)&&!record.ReadIds.Contains(comment.Id)){record.NotifiedIds.Add(comment.Id);changed=true;}
                if(changed){count++;if(number==0)number=record.Number;}
            }
            if(count==0)return null;data.LastBalloonAt=now;return new FeedbackBalloon(number,count);
        }
        internal FeedbackPollResult Poll(bool automatic,long? number,CancellationToken token){
            var result=new FeedbackPollResult();var now=clock();
            if(automatic&&(!enabled()||now<firstCheck))return result;
            if(Interlocked.CompareExchange(ref polling,1,0)!=0){result.Message="正在刷新，请稍候。";return result;}
            try{
                var data=Store.Snapshot();if(data.PollNotBefore.HasValue&&now<data.PollNotBefore.Value){result.Message="检查暂缓，请稍后刷新。";return result;}
                var due=data.Records.FindAll(delegate(FeedbackRecord r){return r.Number>0&&(!number.HasValue||r.Number==number.Value)&&(!automatic||FeedbackSchedule.Due(r,now));});
                bool checkedAny=false,failedBatch=false;
                // Each due record occurs once. Dates are advanced from completion, never replayed from an old deadline.
                for(int start=0;start<due.Count;start+=10){
                    token.ThrowIfCancellationRequested();if(automatic&&!enabled())break;
                    var batch=due.GetRange(start,Math.Min(10,due.Count-start));var numbers=batch.ConvertAll(delegate(FeedbackRecord r){return r.Number;}).ToArray();
                    FeedbackRepliesResponse reply=null;DateTime? retry=null;bool pause=false;
                    try{
                        using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token)){
                            timeout.CancelAfter(timeoutMilliseconds);
                            using(var response=http.Post(new Uri(FeedbackRelay.Endpoint(relay).AbsoluteUri+"/replies"),FeedbackProtocol.Json(new FeedbackRepliesRequest {Numbers=numbers}),timeout.Token)){
                                retry=FeedbackProtocol.RetryAt(response.RetryAfter,clock());pause=response.Status==429||response.Status==503;
                                if(pause&&!retry.HasValue)retry=clock().AddMinutes(15);
                                var bytes=FeedbackProtocol.Read(response,512*1024,timeout.Token);
                                if(response.Status==200){
                                    reply=UpdateJson.Read<FeedbackRepliesResponse>(bytes);
                                    if(reply==null||reply.Schema!=1||reply.Items==null||reply.Items.Length!=numbers.Length)throw new InvalidDataException("Invalid replies.");
                                    for(int i=0;i<numbers.Length;i++){if(reply.Items[i]==null||reply.Items[i].Number!=numbers[i])throw new InvalidDataException("Mismatched replies.");reply.Items[i].Validate();}
                                }
                            }
                        }
                    }catch(Exception){token.ThrowIfCancellationRequested();reply=null;}
                    now=clock();checkedAny=true;
                    if(reply==null){failedBatch=true;result.Message="暂时无法检查回复，请稍后重试。";}
                    Store.Change(delegate(FeedbackData next){
                        if(pause)next.PollNotBefore=retry;
                        for(int i=0;i<numbers.Length;i++){
                            var record=next.Records.Find(delegate(FeedbackRecord r){return r.Number==numbers[i];});if(record==null)continue;
                            if(reply!=null&&reply.Items[i].Status=="ok")Merge(record,reply.Items[i],now,random());
                            else{
                                FeedbackSchedule.Failed(record,now,retry);
                                if(reply!=null){if(!failedBatch)result.Message="部分反馈暂时无法读取，请稍后重试。";if(reply.Items[i].Status=="unavailable"){record.CanSupplement=false;if(record.State=="closed"){record.ClosedFollowupDone=true;record.NextCheckAt=null;}}}
                            }
                        }
                    });
                    if(pause)break;
                }
                if(automatic&&checkedAny){
                    Store.Change(delegate(FeedbackData next){result.Balloon=MarkNotifications(next,clock());});
                    var notify=BalloonReady;if(result.Balloon!=null&&notify!=null)notify(result.Balloon);
                }
            }catch(OperationCanceledException){throw;}
            catch(Exception){result.Message="无法读取或保存反馈记录，请稍后重试。";result.Balloon=null;}
            finally{Interlocked.Exchange(ref polling,0);}
            return result;
        }
        internal SupplementResult Supplement(long number,string description,string diagnostics,CancellationToken token){
            if(Interlocked.CompareExchange(ref supplementing,1,0)!=0)return new SupplementResult(SupplementKind.Busy);
            try{
                var record=Store.Find(number);if(record==null||!record.MaySupplement)return new SupplementResult(SupplementKind.Denied);
                if(record.Pending==null){
                    if(!FeedbackInput.Valid(record.Category,description))return new SupplementResult(SupplementKind.Invalid);
                    var pending=new FeedbackSupplement {RequestId=Guid.NewGuid().ToString("D"),Description=description.Trim(),Diagnostics=FeedbackPayload.LimitDiagnostics(diagnostics),AppVersion=AppRuntime.Version};
                    try{Store.Change(delegate(FeedbackData data){var target=data.Records.Find(delegate(FeedbackRecord r){return r.Number==number;});if(target==null||!target.MaySupplement)throw new InvalidDataException("Feedback unavailable.");if(target.Pending==null)target.Pending=pending;});}
                    catch(Exception){return new SupplementResult(SupplementKind.LocalFailure);}
                    record=Store.Find(number);
                }
                if(record==null||record.Pending==null)return new SupplementResult(SupplementKind.LocalFailure);
                var saved=record.Pending;var now=clock();if(saved.RetryAt.HasValue&&now<saved.RetryAt.Value)return new SupplementResult(SupplementKind.RateLimited,saved.RetryAt);
                var request=new FeedbackSupplementRequest {Number=number,ReplyKey=record.ReplyKey,RequestId=saved.RequestId,Description=saved.Description,Diagnostics=saved.Diagnostics,AppVersion=saved.AppVersion};
                var result=new SupplementResult(SupplementKind.Unknown);
                try{
                    using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token)){
                        timeout.CancelAfter(timeoutMilliseconds);
                        using(var response=http.Post(new Uri(FeedbackRelay.Endpoint(relay).AbsoluteUri+"/supplement"),FeedbackProtocol.Json(request),timeout.Token)){
                            var retry=FeedbackProtocol.RetryAt(response.RetryAfter,clock());
                            if((response.Status==429||response.Status==503)&&!retry.HasValue)retry=clock().AddMinutes(15);
                            result=new SupplementResult(SupplementKind.Unknown,retry);
                            var bytes=FeedbackProtocol.Read(response,16384,timeout.Token);
                            if(response.Status==200||response.Status==201){
                                var receipt=UpdateJson.Read<FeedbackSupplementReceipt>(bytes);
                                if(receipt!=null&&receipt.Number==number&&FeedbackProtocol.ValidId(receipt.CommentId)&&receipt.Duplicate==(response.Status==200))result=new SupplementResult(SupplementKind.Success);
                            }else{
                                var error=UpdateJson.Read<FeedbackError>(bytes);string code=error==null?null:error.Error;
                                result=new SupplementResult(response.Status==403&&code=="denied"?SupplementKind.Denied:response.Status==409&&code=="closed"?SupplementKind.Closed:
                                    response.Status==429&&code=="rate_limited"?SupplementKind.RateLimited:response.Status==503&&(code=="disabled"||code=="unavailable")?SupplementKind.Unavailable:
                                    response.Status==400&&code=="invalid"?SupplementKind.Invalid:response.Status==413&&code=="too_large"?SupplementKind.TooLarge:SupplementKind.Unknown,retry);
                            }
                        }
                    }
                }catch(Exception){token.ThrowIfCancellationRequested();}
                try{Store.Change(delegate(FeedbackData data){
                    var target=data.Records.Find(delegate(FeedbackRecord r){return r.Number==number;});if(target==null||target.Pending==null||target.Pending.RequestId!=saved.RequestId)return;
                    if(result.Kind==SupplementKind.Success){target.Pending=null;target.LastActivityAt=clock();target.NextCheckAt=FeedbackSchedule.Next(target,clock(),random());}
                    else if(result.Kind==SupplementKind.Invalid||result.Kind==SupplementKind.TooLarge||result.Kind==SupplementKind.Denied||result.Kind==SupplementKind.Closed){target.Pending=null;if(result.Kind==SupplementKind.Denied||result.Kind==SupplementKind.Closed)target.CanSupplement=false;}
                    else target.Pending.RetryAt=result.RetryAt;
                });}catch(Exception){return new SupplementResult(result.Kind==SupplementKind.Success?SupplementKind.Unknown:SupplementKind.LocalFailure);}
                return result;
            }finally{Interlocked.Exchange(ref supplementing,0);}
        }
    }
}
