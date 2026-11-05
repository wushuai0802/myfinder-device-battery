using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

// Entirely offline. DPAPI files and settings fixtures stay under the supplied test output.
static class FeedbackRepliesTests {
    const string Relay="https://feedback.invalid/service";
    static readonly DateTime Start=new DateTime(2026,11,5,0,0,0,DateTimeKind.Utc);
    static int checks,failures;static string output;
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    static void Check(bool value,string text){if(!value)throw new Exception(text);checks++;}
    static void Run(string name,Action action){try{action();Console.WriteLine("PASS "+name);}catch(Exception e){failures++;Console.Error.WriteLine("FAIL "+name+": "+e);}}
    static UpdateResponse Response(int code,string body,string retry=null,long? length=null){var bytes=Encoding.UTF8.GetBytes(body);return new UpdateResponse(code,new MemoryStream(bytes),length??bytes.Length,null,retry);}
    static UpdateResponse Created(bool key=true){return Response(201,"{\"number\":42,\"url\":\""+FeedbackClient.IssueUrl(42)+"\""+(key?",\"replyAccess\":true":"")+"}");}
    sealed class Http:IFeedbackTransport {
        internal readonly List<byte[]> Bodies=new List<byte[]>();internal readonly List<Uri> Uris=new List<Uri>();
        internal Func<Uri,byte[],CancellationToken,UpdateResponse> Reply=delegate{return Created();};
        public UpdateResponse Post(Uri uri,byte[] body,CancellationToken token){Uris.Add(uri);Bodies.Add(body);return Reply(uri,body,token);}
    }
    static FeedbackComment Comment(string id,string author="maintainer",string text="请提供复现步骤。",bool truncated=false){return new FeedbackComment {Id=id,Author=author,Text=text,CreatedAt=Start.ToString("o"),UpdatedAt=Start.ToString("o"),Truncated=truncated};}
    static FeedbackRecord Record(long number=42,bool key=true){return new FeedbackRecord {Number=number,Url=FeedbackClient.IssueUrl(number),Category="device",Summary="连接后不显示电量",ReplyKey=key?FeedbackProtocol.NewKey():null,CanSupplement=key,CreatedAt=Start,LastActivityAt=Start,NextCheckAt=Start};}
    static FeedbackStore Store(params FeedbackRecord[] records){var store=new FeedbackStore(null);store.Change(delegate(FeedbackData d){d.Records.AddRange(records);});return store;}
    static FeedbackReplyItem Item(long number=42,params FeedbackComment[] comments){return new FeedbackReplyItem {Number=number,Status="ok",State="open",Locked=false,CanSupplement=true,Comments=comments};}
    static UpdateResponse Replies(params FeedbackReplyItem[] items){return Response(200,UpdateJson.Text(new FeedbackRepliesResponse {Schema=1,Items=items}));}
    static FeedbackReplies Service(FeedbackStore store,Http http,Func<DateTime> clock=null,Func<bool> enabled=null,int timeout=20000){return new FeedbackReplies(store,http,Relay,clock??delegate{return Start;},delegate{return 0.5;},enabled??delegate{return true;},timeout);}
    static FeedbackReport Report(){return new FeedbackReport("fixture-version","fixture-windows",new[]{new WirelessDevice {Name="fixture-mouse",Kind="mouse",Percent=62}},new SuppressedDevice[0],new UnadaptedDevice[0],new ReaderHealth[0],new string[0]);}
    static Dictionary<string,object> Fields(byte[] bytes){return UpdateJson.Read<Dictionary<string,object>>(bytes,true);}
    static void Creation(){
        var store=Store();var http=new Http();string captured=null;
        http.Reply=delegate(Uri uri,byte[] body,CancellationToken token){
            var fields=Fields(body);captured=(string)fields["replyKey"];
            Check(Convert.ToInt32(fields["schema"])==2&&fields.Count==6&&FeedbackProtocol.ValidKey(captured),"schema 2 has exactly the six agreed fields and a canonical 32-byte key");
            Check(store.Snapshot().Records.Count==1&&store.Snapshot().Records[0].ReplyKey==captured&&store.Snapshot().Records[0].Number==0,"pending creation saved before first transport call");return Created();
        };
        var result=new FeedbackClient(Relay,http,new FeedbackSession(),20000,store,delegate{return Start;}).Submit("device"," 设备连接后不显示电量 ",Report().FullText,CancellationToken.None);
        Check(result.Kind==FeedbackResultKind.Success&&result.ReplyAccess&&result.Saved&&store.Find(42).ReplyKey==captured,"receipt replaces the pending record and retains only the private credential");
        Check(store.Find(42).CreationId==null&&store.Snapshot().Records.Count==1,"no duplicate pending record after success");
        var keys=new HashSet<string>();for(int i=0;i<100;i++){string key=FeedbackProtocol.NewKey();Check(FeedbackProtocol.ValidKey(key)&&key.Length==43&&keys.Add(key),"fresh CSPRNG key");}
        Check(!Report().FullText.Contains(captured)&&!Report().BrowserSubmission("other","test description",true).Text.Contains(captured),"neither diagnostic nor browser report contains the private credential");
        store=Store();http=new Http();http.Reply=delegate{return http.Bodies.Count==1?Response(400,"{\"error\":\"invalid\"}"):Created(false);};
        result=new FeedbackClient(Relay,http,new FeedbackSession(),20000,store).Submit("other","valid description",null,CancellationToken.None);
        Check(result.Kind==FeedbackResultKind.Success&&http.Bodies.Count==2,"definite old-relay rejection falls back exactly once");
        Check(Convert.ToInt32(Fields(http.Bodies[1])["schema"])==1&&!Fields(http.Bodies[1]).ContainsKey("replyKey")&&store.Find(42).ReplyKey==null&&!store.Find(42).MaySupplement,"fallback is the original five-field payload and web-only record");
        foreach(int status in new[]{400,500,502,503}){
            store=Store();http=new Http();http.Reply=delegate{return Response(status,status==400?"{\"error\":\"different\"}":"{\"error\":\"invalid\"}");};
            result=new FeedbackClient(Relay,http,new FeedbackSession(),20000,store).Submit("other","valid description",null,CancellationToken.None);
            Check(result.Kind==FeedbackResultKind.Unavailable&&http.Bodies.Count==1,"no unsafe schema fallback for "+status);
        }
        http=new Http {Reply=delegate{throw new WebException("timeout",WebExceptionStatus.Timeout);}};
        Check(new FeedbackClient(Relay,http,new FeedbackSession()).Submit("other","valid description",null,CancellationToken.None).Kind==FeedbackResultKind.Unavailable&&http.Bodies.Count==1,"timeout never resends creation");
        http=new Http {Reply=delegate{return Response(400,"{\"error\":\"invalid\"}");}};
        Check(new FeedbackClient(Relay,http,new FeedbackSession()).Submit("other","valid description",null,CancellationToken.None).Kind==FeedbackResultKind.Unavailable&&http.Bodies.Count==2,"second invalid does not loop");
    }
    static FeedbackStore FullStore(){
        var store=Store();store.Change(delegate(FeedbackData data){for(int i=1;i<=50;i++){var record=Record(i);record.CreatedAt=Start.AddMinutes(i);if(i==1)record.State="closed";data.Records.Add(record);}});return store;
    }
    static void CreationRetention(){
        foreach(int status in new[]{400,413,429,503}){
            var store=FullStore();string key=store.Find(1).ReplyKey;var http=new Http();
            http.Reply=delegate{
                Check(store.Snapshot().Records.Count==51&&store.Find(1).ReplyKey==key,"no real record trimmed before rejection "+status);
                return Response(status,"{\"error\":\"invalid\"}");
            };
            var result=new FeedbackClient(Relay,http,new FeedbackSession(),20000,store).Submit("other","valid description",null,CancellationToken.None);
            Check(result.Kind==(status==429?FeedbackResultKind.RateLimited:FeedbackResultKind.Unavailable)&&store.Snapshot().Records.Count==50&&store.Find(1).ReplyKey==key,"definite rejection removes only its placeholder "+status);
        }
        foreach(bool timeout in new[]{false,true}){
            var store=FullStore();var http=new Http {Reply=delegate{if(timeout)throw new WebException("timeout",WebExceptionStatus.Timeout);throw new FeedbackNotSentException(new IOException("before sending"));}};
            new FeedbackClient(Relay,http,new FeedbackSession(),20000,store).Submit("other","valid description",null,CancellationToken.None);
            Check(store.Find(1)!=null&&store.Snapshot().Records.Count==(timeout?51:50),"only an unknown result retains its placeholder");
        }
        var full=FullStore();var sent=new Http {Reply=delegate{Check(full.Snapshot().Records.Count==51&&full.Find(1)!=null,"success also waits for the network result before trimming");return Response(201,"{\"number\":51,\"url\":\""+FeedbackClient.IssueUrl(51)+"\",\"replyAccess\":true}");}};
        new FeedbackClient(Relay,sent,new FeedbackSession(),20000,full).Submit("other","valid description",null,CancellationToken.None);
        Check(full.Snapshot().Records.Count==50&&full.Find(1)==null&&full.Find(51).ReplyKey!=null,"only a completed creation consumes a real-record slot");
        var pending=FullStore();string id=pending.BeginCreation("other","pending description",FeedbackProtocol.NewKey(),Start);
        pending.Change(delegate(FeedbackData data){data.Records.Add(data.Records[data.Records.Count-1]);});
        Check(pending.Snapshot().Records.FindAll(delegate(FeedbackRecord r){return r.CreationId==id;}).Count==1&&pending.Find(1)!=null,"one placeholder per CreationId never consumes the 50-record cap");
        string path=Path.Combine(output,"pending-creation-"+Guid.NewGuid().ToString("N")+".dat");var disk=new FeedbackStore(path,delegate{return Start;});
        string expired=disk.BeginCreation("other","expired description",FeedbackProtocol.NewKey(),Start.AddHours(-24).AddTicks(-1));
        string boundary=disk.BeginCreation("other","boundary description",FeedbackProtocol.NewKey(),Start.AddHours(-24));
        string fresh=disk.BeginCreation("other","fresh description",FeedbackProtocol.NewKey(),Start);
        var loaded=new FeedbackStore(path,delegate{return Start;});var records=loaded.Snapshot().Records;
        Check(records.Count==2&&!records.Exists(delegate(FeedbackRecord r){return r.CreationId==expired;})&&records.Exists(delegate(FeedbackRecord r){return r.CreationId==boundary;})&&records.Exists(delegate(FeedbackRecord r){return r.CreationId==fresh;}),"load expires only placeholders older than 24 hours");
        var noResend=new Http();Service(loaded,noResend).Poll(false,null,CancellationToken.None);Check(noResend.Bodies.Count==0,"unknown creations are never resent or polled automatically");
        using(var cancellation=new CancellationTokenSource()){
            var store=Store();var http=new Http();
            var client=new FeedbackClient(Relay,http,new FeedbackSession(),20000,store,delegate{cancellation.Cancel();return Start;});
            bool cancelled=false;try{client.Submit("other","valid description",null,cancellation.Token);}catch(OperationCanceledException){cancelled=true;}
            Check(cancelled&&http.Bodies.Count==0&&store.Snapshot().Records.Count==0,"cancellation before transport removes the saved placeholder");
        }
        bool notSent=false;using(var cancellation=new CancellationTokenSource()){
            cancellation.Cancel();try{new FeedbackHttp().Post(new Uri(Relay+"/feedback"),new byte[0],cancellation.Token);}catch(FeedbackNotSentException){notSent=true;}
        }Check(notSent,"concrete transport identifies cancellation before any network I/O");
    }
    static void ConfirmedReceiptCancellation(){
        foreach(bool fallback in new[]{false,true})using(var cancellation=new CancellationTokenSource()){
            var store=Store();var http=new Http();var session=new FeedbackSession(delegate{return Start;});
            http.Reply=delegate{return fallback&&http.Bodies.Count==1?Response(400,"{\"error\":\"invalid\"}"):Created(!fallback);};
            var client=new FeedbackClient(Relay,http,session,20000,store,delegate{return Start;},delegate(byte[] bytes){
                var receipt=UpdateJson.Read<FeedbackReceipt>(bytes);Check(FeedbackClient.IsIssueUrl(receipt.Number,receipt.Url),"race fixture fully reads and validates the 201 receipt");cancellation.Cancel();return receipt;
            });
            var result=client.Submit("other","valid description",null,cancellation.Token);
            Check(cancellation.IsCancellationRequested&&result.Kind==FeedbackResultKind.Success&&result.Saved&&session.NeedsConfirmation&&store.Find(42)!=null,"confirmed creation survives cancellation at the receipt boundary");
            Check(store.Snapshot().Records.Count==1&&store.Find(42).Number==42&&store.Find(42).CreationId==null&&http.Bodies.Count==(fallback?2:1),"confirmed receipt is bound exactly once without resending");
        }
    }
    static void Persistence(){
        string path=Path.Combine(output,"feedback-"+Guid.NewGuid().ToString("N")+".dat");var store=new FeedbackStore(path);var record=Record();record.Comments.Add(Comment("123"));record.ReadIds.Add("123");record.NotifiedIds.Add("123");
        record.Pending=new FeedbackSupplement {RequestId=Guid.NewGuid().ToString("D"),Description="exact pending description",Diagnostics=Report().FullText,AppVersion=AppRuntime.Version};
        store.Change(delegate(FeedbackData d){d.Records.Add(record);d.LastBalloonAt=Start;});
        var restored=new FeedbackStore(path).Find(42);
        Check(restored.ReplyKey==record.ReplyKey&&restored.Pending.RequestId==record.Pending.RequestId&&restored.Comments[0].Text==record.Comments[0].Text&&restored.ReadIds.Contains("123")&&restored.NotifiedIds.Contains("123"),"whole DPAPI record round trip");
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(record.ReplyKey)&&!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(record.Pending.Description),"credential and content are not plaintext on disk");
        store.MarkRead(42);Check(new FeedbackStore(path).Snapshot().LastBalloonAt==Start&&Directory.GetFiles(output,Path.GetFileName(path)+".*.tmp").Length==0,"atomic replacement leaves a loadable document and no temp file");
        byte[] corrupt={1,2,3,4,5};File.WriteAllBytes(path,corrupt);store=new FeedbackStore(path);
        Check(store.Snapshot().Records.Count==0&&!File.Exists(path)&&File.ReadAllBytes(path+".bad").Length==5,"corruption starts empty and moves the bad bytes out of the load path");
        new FeedbackStore(path);new FeedbackStore(path);Check(Directory.GetFiles(output,Path.GetFileName(path)+"*.bad").Length==1,"later starts do not create repeated bad files");
        store.Change(delegate(FeedbackData d){d.Records.Add(Record(99));});Check(new FeedbackStore(path).Find(99)!=null,"recovered store remains writable");
        var capped=Store();capped.Change(delegate(FeedbackData d){for(int i=1;i<=50;i++){var r=Record(i);r.CreatedAt=Start.AddMinutes(i);if(i==10||i==20)r.State="closed";d.Records.Add(r);}});
        capped.Change(delegate(FeedbackData d){d.Records.Add(Record(51));});Check(capped.Find(10)==null&&capped.Find(1)!=null&&capped.Snapshot().Records.Count==50,"oldest closed goes first even when open records are older");
        capped.Change(delegate(FeedbackData d){d.Records.Add(Record(52));});Check(capped.Find(20)==null,"next closed record removed");
        capped.Change(delegate(FeedbackData d){var r=Record(53);r.CreatedAt=Start.AddDays(1);d.Records.Add(r);});Check(capped.Find(51)==null&&capped.Snapshot().Records.Count==50,"oldest open removed once no closed record remains");
        bool rejected=false;try{capped.Change(delegate(FeedbackData d){d.Records[0].Url="https://evil.invalid";});}catch(InvalidDataException){rejected=true;}Check(rejected&&capped.Find(1)!=null,"invalid URL is rejected transactionally");
        string directory=Path.Combine(output,"blocked-store");Directory.CreateDirectory(directory);store=new FeedbackStore(directory);var http=new Http();
        Check(new FeedbackClient(Relay,http,new FeedbackSession(),20000,store).Submit("other","valid description",null,CancellationToken.None).Kind==FeedbackResultKind.Unavailable&&http.Bodies.Count==0,"no HTTP when pending record cannot be saved");
    }
    static void Scheduler(){
        var record=Record();
        Check(FeedbackSchedule.First(Start,0)==Start.AddMinutes(1)&&FeedbackSchedule.First(Start,1)==Start.AddMinutes(10),"startup jitter 1 to 10 minutes");
        Check(FeedbackSchedule.Next(record,Start,0)==Start.AddHours(4.8)&&FeedbackSchedule.Next(record,Start,1)==Start.AddHours(7.2),"six-hour jitter has the exact 20 percent bounds");
        DateTime now=Start.AddDays(30);Check(FeedbackSchedule.Next(record,now,0.5)==now.AddDays(7),"30 days idle becomes weekly");
        Check(!FeedbackSchedule.Next(record,Start.AddDays(90),0.5).HasValue&&!FeedbackSchedule.Due(record,Start.AddDays(90)),"90 days idle is manual only");
        now=Start;var closed=Item();closed.State="closed";closed.CanSupplement=false;FeedbackReplies.Merge(record,closed,now,0.5);
        Check(record.NextCheckAt==Start.AddHours(24)&&!record.ClosedFollowupDone,"first closed observation schedules a final check");
        FeedbackReplies.Merge(record,closed,Start.AddHours(1),0.5);Check(record.NextCheckAt==Start.AddHours(24)&&!record.ClosedFollowupDone,"early manual refresh does not consume closed follow-up");
        FeedbackSchedule.Failed(record,Start.AddHours(24),null);Check(record.NextCheckAt.HasValue&&!record.ClosedFollowupDone,"failed final closed check still retries");
        FeedbackReplies.Merge(record,closed,Start.AddHours(25),0.5);Check(record.ClosedFollowupDone&&!record.NextCheckAt.HasValue,"successful final check stops polling");
        FeedbackReplies.Merge(record,Item(),Start.AddHours(26),0.5);Check(!record.ClosedFollowupDone&&!record.ClosedAt.HasValue&&record.NextCheckAt.HasValue,"manual observation of reopening resumes schedule");
        record=Record();FeedbackSchedule.Failed(record,Start,null);Check(record.NextCheckAt==Start.AddHours(6),"first failure respects normal period");FeedbackSchedule.Failed(record,Start,null);Check(record.NextCheckAt==Start.AddHours(6),"second failure respects normal period");FeedbackSchedule.Failed(record,Start,null);Check(record.NextCheckAt==Start.AddHours(6),"third failure");
        FeedbackSchedule.Failed(record,Start,Start.AddDays(1));Check(record.NextCheckAt==Start.AddDays(1),"Retry-After overrides shorter failure delay");
        Check(FeedbackProtocol.RetryAt("7200",Start)==Start.AddHours(2)&&FeedbackProtocol.RetryAt(Start.AddHours(3).ToString("R"),Start)==Start.AddHours(3)&&FeedbackProtocol.RetryAt("bad",Start)==null,"seconds and HTTP-date Retry-After");
        bool enabled=false;var store=Store(Record());var http=new Http {Reply=delegate{return Replies(Item(42,Comment("10")));}};var service=Service(store,http,delegate{return now;},delegate{return enabled;});now=Start.AddHours(1);
        service.Poll(true,null,CancellationToken.None);Check(http.Bodies.Count==0,"setting off prevents all background requests");
        service.Poll(false,null,CancellationToken.None);Check(http.Bodies.Count==1&&store.Find(42).Comments.Count==1,"manual refresh works while disabled");
        enabled=true;now=now.AddDays(31);service.Poll(true,null,CancellationToken.None);int calls=http.Bodies.Count;service.Poll(true,null,CancellationToken.None);Check(calls==2&&http.Bodies.Count==calls&&store.Find(42).NextCheckAt==now.AddDays(7),"waking from sleep runs one round and schedules from now, with no catch-up burst");
        now=Start;store=Store(Record());http=new Http {Reply=delegate{return Replies(Item());}};service=Service(store,http,delegate{return now;});service.Poll(true,null,CancellationToken.None);Check(http.Bodies.Count==0,"due records still wait for startup jitter");now=Start.AddMinutes(6);service.Poll(true,null,CancellationToken.None);Check(http.Bodies.Count==1,"startup due record polls after jitter");
        now=Start;store=Store(Record());http=new Http {Reply=delegate{return Response(503,"{\"error\":\"unavailable\"}","7200");}};service=Service(store,http,delegate{return now;});service.Poll(false,null,CancellationToken.None);
        Check(store.Find(42).NextCheckAt==Start.AddHours(6)&&store.Snapshot().PollNotBefore==Start.AddHours(2),"503 upstream pause persists Retry-After without shortening the normal record period");
        service.Poll(false,null,CancellationToken.None);Check(http.Bodies.Count==1,"manual refresh also respects upstream pause");
        now=Start.AddHours(2);service.Poll(false,null,CancellationToken.None);Check(http.Bodies.Count==2,"pause eventually expires");
        store=Store(Record());http=new Http {Reply=delegate{return Response(429,"invalid JSON","7200");}};service=Service(store,http);service.Poll(false,null,CancellationToken.None);service.Poll(false,null,CancellationToken.None);
        Check(http.Bodies.Count==1&&store.Snapshot().PollNotBefore==Start.AddHours(2),"malformed limit body cannot bypass Retry-After");
    }
    static void ScheduleRegressions(){
        foreach(string state in new[]{"open","closed"}){
            var record=Record();record.State=state;record.ClosedAt=Start;
            Check(FeedbackSchedule.Due(record,Start.AddDays(90).AddTicks(-1))&&!FeedbackSchedule.Due(record,Start.AddDays(90)),"90-day boundary applies to "+state);
            Check(!FeedbackSchedule.Next(record,Start.AddDays(90),0.5).HasValue,"all states stop scheduling after 90 idle days");
            FeedbackSchedule.Failed(record,Start.AddDays(90),null);Check(!record.NextCheckAt.HasValue,"failure cannot resume an expired record");
        }
        foreach(int days in new[]{30,60,89}){
            var record=Record();DateTime now=Start.AddDays(days);
            for(int i=0;i<4;i++){FeedbackSchedule.Failed(record,now,now.AddHours(12));Check(record.NextCheckAt==now.AddDays(7),"weekly idle record never retries early, attempt "+i);}
            FeedbackSchedule.Failed(record,now,now.AddDays(8));Check(record.NextCheckAt==now.AddDays(8),"longer Retry-After still wins over weekly period");
        }
        foreach(string status in new[]{"unavailable","error"}){
            var record=Record();record.State="closed";record.ClosedAt=Start;record.NextCheckAt=Start.AddHours(24);var store=Store(record);DateTime now=Start.AddHours(24);
            var http=new Http {Reply=delegate{return Replies(new FeedbackReplyItem {Number=42,Status=status});}};var service=Service(store,http,delegate{return now;});
            service.Poll(false,null,CancellationToken.None);var saved=store.Find(42);
            Check(saved.LastCheckedAt==now&&saved.ClosedFollowupDone==(status=="unavailable")&&saved.NextCheckAt.HasValue==(status!="unavailable"),"closed unavailable completes follow-up while transient error remains retryable");
            now=Start.AddDays(90);service.Poll(true,null,CancellationToken.None);Check(http.Bodies.Count==1,"closed failures cannot poll past the 90-day limit");
        }
    }
    static void Merging(){
        var record=Record();var auto=Comment("1","automation");FeedbackReplies.Merge(record,Item(42,auto),Start,0.5);var data=new FeedbackData();data.Records.Add(record);
        Check(record.Unread==1&&FeedbackReplies.MarkNotifications(data,Start)==null,"automation is unread but never balloons");
        FeedbackReplies.Merge(record,Item(42,auto,Comment("2")),Start.AddMinutes(1),0.5);var balloon=FeedbackReplies.MarkNotifications(data,Start.AddMinutes(1));
        Check(balloon!=null&&balloon.Title=="反馈有新回复"&&balloon.Text=="#42 有维护者回复"&&record.NotifiedIds.Contains("2"),"maintainer IDs marked before one balloon");
        record.ReadIds.Add("2");var edited=Comment("2","maintainer","edited plain text");edited.UpdatedAt=Start.AddHours(1).ToString("o");
        FeedbackReplies.Merge(record,Item(42,auto,edited),Start.AddHours(1),0.5);
        Check(record.Comments.Count==2&&record.Comments[1].Text=="edited plain text"&&record.Unread==1&&FeedbackReplies.MarkNotifications(data,Start.AddHours(7))==null,"edits update text without duplicate or repeat notifications and retain read state");
        FeedbackReplies.Merge(record,Item(42,auto,edited,Comment("3")),Start.AddHours(2),0.5);Check(FeedbackReplies.MarkNotifications(data,Start.AddHours(2))==null&&!record.NotifiedIds.Contains("3"),"six-hour cap retains unsent IDs");
        var other=Record(43);other.Comments.Add(Comment("4"));data.Records.Add(other);balloon=FeedbackReplies.MarkNotifications(data,Start.AddHours(7));
        Check(balloon!=null&&balloon.Count==2&&balloon.Text=="2 条反馈有新回复"&&other.NotifiedIds.Contains("4"),"one merged balloon for the poll round");
        Check(FeedbackReplies.MarkNotifications(data,Start.AddHours(14))==null,"repeated identical replies do not notify");
        string controls="a\0\u0001\r\n\t\u202aX\u202e\u2066Y\u2069\u007fb";
        Check(FeedbackText.Plain(controls)=="a\n\t\ufffdX\ufffd\ufffdY\ufffdb","control and bidi sanitising");
        record.Comments=new List<FeedbackComment> {Comment("99","automation","[link](https://evil.invalid) <script>evil</script>\0\u202e",true)};record.ReplyKey=null;
        string display=FeedbackText.Comments(record);Check(display.Contains("自动初筛")&&display.Contains("（内容过长，已截断）")&&display.Contains("此反馈只能在网页查看")&&display.Contains("<script>")&&!display.Contains("\0")&&!display.Contains("\u202e"),"labels, fallback notice and literal plain-text rendering");
    }
    static void NotificationRegressions(){
        var record=Record();record.Comments.Add(Comment("20"));record.ReadIds.Add("20");var data=new FeedbackData();data.Records.Add(record);
        Check(FeedbackReplies.MarkNotifications(data,Start)==null&&!data.LastBalloonAt.HasValue,"a previously read but unnotified maintainer comment never balloons");
        record.Comments.Add(Comment("21","automation"));var store=Store(record);store.MarkRead(42);store.MarkRead(42);
        Check(store.Find(42).NotifiedIds.Count==2&&store.Find(42).NotifiedIds.Contains("20")&&store.Find(42).NotifiedIds.Contains("21"),"marking read acknowledges all IDs once without using the balloon cap");
        store=Store(Record());store.Change(delegate(FeedbackData d){d.LastBalloonAt=Start;});DateTime now=Start.AddHours(1);
        var http=new Http {Reply=delegate{return Replies(Item(42,Comment("22")));}};var service=Service(store,http,delegate{return now;});int balloons=0;service.BalloonReady+=delegate{balloons++;};
        var result=service.Poll(false,null,CancellationToken.None);
        Check(result.Balloon==null&&balloons==0&&store.Snapshot().LastBalloonAt==Start&&store.Find(42).NotifiedIds.Count==0,"manual refresh never notifies or consumes the six-hour cap");
        store.MarkRead(42);now=Start.AddHours(7);service.Poll(true,null,CancellationToken.None);
        Check(balloons==0&&store.Snapshot().LastBalloonAt==Start&&store.Find(42).NotifiedIds.Contains("22"),"reply read during cooldown never creates a stale balloon later");
        http.Reply=delegate{return Replies(Item(42,Comment("22"),Comment("23")));};now=Start.AddHours(13);service.Poll(true,null,CancellationToken.None);
        Check(balloons==1&&store.Snapshot().LastBalloonAt==now&&store.Find(42).NotifiedIds.Contains("23"),"only an automatic poll with an unread new maintainer comment consumes the cap");
        store=Store(Record());http=new Http {Reply=delegate{return Replies(Item(42,Comment("24")));}};service=Service(store,http);balloons=0;service.BalloonReady+=delegate{balloons++;};
        using(var dialog=new MyFeedbackDialog(service,Report(),false,delegate{},42)){
            dialog.Opacity=0;dialog.Show();Find<Button>(dialog,"RefreshFeedbackReplies").PerformClick();Pump(delegate{return Find<Label>(dialog,"FeedbackHistoryStatus").Text=="已刷新。";});
            Check(balloons==0&&!store.Snapshot().LastBalloonAt.HasValue&&store.Find(42).ReadIds.Contains("24")&&store.Find(42).NotifiedIds.Contains("24"),"dialog refresh displays and acknowledges the reply without a balloon");dialog.Close();
        }
    }
    sealed class CountStream:MemoryStream {internal int Count;internal CountStream(byte[] bytes):base(bytes){}public override int Read(byte[] buffer,int offset,int count){int n=base.Read(buffer,offset,count);Count+=n;return n;}}
    static void ReplyBoundaries(){
        var store=Store();store.Change(delegate(FeedbackData d){for(int i=1;i<=23;i++)d.Records.Add(Record(i));});var http=new Http();
        http.Reply=delegate(Uri uri,byte[] body,CancellationToken token){var request=UpdateJson.Read<FeedbackRepliesRequest>(body);Check(uri.AbsoluteUri==Relay+"/feedback/replies"&&request.Numbers.Length<=10&&request.Schema==1&&Fields(body).Count==2,"batch endpoint and exact public request shape");var items=new List<FeedbackReplyItem>();foreach(long n in request.Numbers)items.Add(Item(n,Comment(n.ToString())));return Replies(items.ToArray());};
        DateTime now=Start;var service=Service(store,http,delegate{return now;});int balloons=0;service.BalloonReady+=delegate(FeedbackBalloon b){balloons++;Check(store.Find(1).NotifiedIds.Count==1&&b.Count==23,"all IDs durably marked before notification event");};
        now=Start.AddHours(1);service.Poll(true,null,CancellationToken.None);Check(http.Bodies.Count==3&&balloons==1,"23 records use three automatic batches and one balloon");
        foreach(string invalid in new[]{"{}","not JSON","{\"schema\":1,\"items\":[]}","{\"schema\":1,\"items\":[{\"number\":42,\"status\":\"ok\",\"state\":\"open\",\"comments\":[]}]}",UpdateJson.Text(new FeedbackRepliesResponse {Schema=1,Items=new[]{Item(99)}})}){
            store=Store(Record());http=new Http {Reply=delegate{return Response(200,invalid);}};Service(store,http).Poll(false,null,CancellationToken.None);Check(store.Find(42).Failures==1&&store.Find(42).Comments.Count==0,"malformed batch fails closed");
        }
        store=Store(Record());http=new Http {Reply=delegate{return Replies(Item(42,Comment("5"),Comment("5")));}};Service(store,http).Poll(false,null,CancellationToken.None);Check(store.Find(42).Failures==1,"duplicate IDs in an untrusted batch are rejected");
        store=Store(Record());var counted=new CountStream(new byte[512*1024+1]);http=new Http {Reply=delegate{return new UpdateResponse(200,counted,-1,null,null);}};
        Service(store,http).Poll(false,null,CancellationToken.None);Check(counted.Count==512*1024&&store.Find(42).Failures==1,"unknown-length replies never read beyond 512 KB");
        store=Store(Record(42),Record(43));http=new Http {Reply=delegate{return Replies(new FeedbackReplyItem {Number=42,Status="unavailable"},Item(43,Comment("6")));}};
        Service(store,http).Poll(false,null,CancellationToken.None);Check(!store.Find(42).CanSupplement&&store.Find(42).Failures==1&&store.Find(43).Comments.Count==1,"per-item unavailability does not discard other replies");
        store=Store(Record(42),Record(43));http=new Http {Reply=delegate{return Response(429,"{\"error\":\"rate_limited\"}","3600");}};service=Service(store,http);service.Poll(false,null,CancellationToken.None);service.Poll(false,null,CancellationToken.None);Check(http.Bodies.Count==1&&store.Snapshot().PollNotBefore==Start.AddHours(1),"429 pauses the whole batch round");
    }
    static void DefaultsAndBatchFailures(){
        var previous=DeviceSettings.Current;
        try{
            typeof(DeviceSettings).GetMethod("UseDefaults",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
            DateTime now=Start;var store=Store(Record());var http=new Http {Reply=delegate{return Replies(Item());}};
            var service=new FeedbackReplies(store,http,Relay,delegate{return now;},delegate{return 0.5;});now=Start.AddHours(1);
            service.Poll(true,null,CancellationToken.None);Check(DeviceSettings.UsingDefaults&&http.Bodies.Count==0,"default service blocks automatic polling on unreadable-settings fallback");
            service.Poll(false,null,CancellationToken.None);Check(http.Bodies.Count==1,"manual polling still works while settings use defaults");
            var injected=Service(Store(Record()),http,delegate{return now;});
            using(var dispatch=new Control())using(var controller=new FeedbackReplyController(injected,dispatch,delegate{})){
                now=now.AddHours(1);typeof(FeedbackReplyController).GetMethod("Check",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(controller,null);
                Check(http.Bodies.Count==1,"controller independently blocks fallback defaults even with an injected enabled service");
                DeviceSettings.UseTransientConfiguration(new AudioBindings {CheckFeedbackReplies=true});
                typeof(FeedbackReplyController).GetMethod("Check",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(controller,null);
                Check(http.Bodies.Count==2,"controller resumes after an explicit valid configuration");
            }
        }finally{DeviceSettings.UseTransientConfiguration(previous);}
        foreach(int code in new[]{500,503,200}){
            var store=Store(Record());var http=new Http {Reply=delegate{return Response(code,"invalid JSON");}};
            var result=Service(store,http).Poll(false,null,CancellationToken.None);
            Check(result.Message=="暂时无法检查回复，请稍后重试。","whole-batch failure is never overwritten by the per-record loop "+code);
        }
        var partial=new Http {Reply=delegate{return Replies(new FeedbackReplyItem {Number=42,Status="unavailable"});}};
        Check(Service(Store(Record()),partial).Poll(false,null,CancellationToken.None).Message=="部分反馈暂时无法读取，请稍后重试。","valid per-item unavailable keeps the partial-failure message");
        var many=Store();many.Change(delegate(FeedbackData d){for(int i=1;i<=11;i++)d.Records.Add(Record(i));});var batches=new Http();
        batches.Reply=delegate{return batches.Bodies.Count==1?Response(500,"{}"):Replies(new FeedbackReplyItem {Number=11,Status="unavailable"});};
        Check(Service(many,batches).Poll(false,null,CancellationToken.None).Message=="暂时无法检查回复，请稍后重试。"&&batches.Bodies.Count==2,"later partial batch cannot overwrite an earlier whole-batch failure");
    }
    static void Supplements(){
        var record=Record();var store=Store(record);var http=new Http();string requestId=null;string exact=null;
        http.Reply=delegate(Uri uri,byte[] body,CancellationToken token){
            var request=UpdateJson.Read<FeedbackSupplementRequest>(body);Check(uri.AbsoluteUri==Relay+"/feedback/supplement"&&request.Schema==1&&Fields(body).Count==7,"supplement exact endpoint and fields");
            Check(store.Find(42).Pending.RequestId==request.RequestId&&store.Find(42).Pending.Description==request.Description&&FeedbackProtocol.ValidRequestId(request.RequestId),"UUID v4 and exact text saved before sending");
            requestId=request.RequestId;exact=Encoding.UTF8.GetString(body);throw new WebException("timeout",WebExceptionStatus.Timeout);
        };
        var service=Service(store,http);var result=service.Supplement(42," exact original description ",Report().FullText,CancellationToken.None);
        Check(result.Kind==SupplementKind.Unknown&&result.Message=="正在确认，请稍后重试"&&store.Find(42).Pending.RequestId==requestId,"timeout retains the pending supplement");
        http.Reply=delegate(Uri uri,byte[] body,CancellationToken token){Check(Encoding.UTF8.GetString(body)==exact,"retry reuses byte-for-byte content, version and request ID despite edited arguments");return Response(200,"{\"number\":42,\"commentId\":\"123\",\"duplicate\":true}");};
        result=service.Supplement(42,"changed user description",null,CancellationToken.None);
        Check(result.Kind==SupplementKind.Success&&store.Find(42).Pending==null&&store.Find(42).LastActivityAt==Start,"duplicate success clears pending and refreshes activity");
        var cases=new[]{new {Code=403,Error="denied",Kind=SupplementKind.Denied},new {Code=409,Error="closed",Kind=SupplementKind.Closed},new {Code=429,Error="rate_limited",Kind=SupplementKind.RateLimited},new {Code=503,Error="disabled",Kind=SupplementKind.Unavailable},new {Code=503,Error="unavailable",Kind=SupplementKind.Unavailable},new {Code=400,Error="invalid",Kind=SupplementKind.Invalid},new {Code=413,Error="too_large",Kind=SupplementKind.TooLarge}};
        foreach(var failure in cases){
            store=Store(Record());http=new Http {Reply=delegate{return Response(failure.Code,"{\"error\":\""+failure.Error+"\",\"detail\":\"SECRET_RAW_SERVER_TEXT\"}","3600");}};service=Service(store,http);
            result=service.Supplement(42,"valid supplement",null,CancellationToken.None);Check(result.Kind==failure.Kind&&!result.Message.Contains("SECRET_RAW")&&result.Message.Length>5,"Chinese error mapping "+failure.Error);
            if(failure.Code==429){service.Supplement(42,"another supplement",null,CancellationToken.None);Check(http.Bodies.Count==1&&store.Find(42).Pending.RetryAt==Start.AddHours(1),"supplement respects Retry-After and keeps content");}
        }
        foreach(bool noKey in new[]{false,true}){
            record=Record();if(noKey)record.ReplyKey=null;else record.CanSupplement=false;store=Store(record);http=new Http();
            Check(Service(store,http).Supplement(42,"valid supplement",null,CancellationToken.None).Kind==SupplementKind.Denied&&http.Bodies.Count==0&&store.Find(42).Pending==null,"supplement disabled without key or relay permission");
        }
        store=Store(Record());var counted=new CountStream(new byte[16385]);http=new Http {Reply=delegate{return new UpdateResponse(201,counted,-1,null,null);}};
        result=Service(store,http).Supplement(42,"valid supplement",null,CancellationToken.None);Check(result.Kind==SupplementKind.Unknown&&counted.Count==16384&&store.Find(42).Pending!=null,"oversized supplement receipt remains uncertain with bounded read");
        store=Store(Record());http=new Http {Reply=delegate(Uri uri,byte[] body,CancellationToken token){token.WaitHandle.WaitOne();token.ThrowIfCancellationRequested();return null;}};
        result=Service(store,http,null,null,30).Supplement(42,"valid supplement",null,CancellationToken.None);Check(result.Kind==SupplementKind.Unknown&&store.Find(42).Pending!=null,"whole-operation timeout keeps the request ID");
    }
    static T Find<T>(Control owner,string name) where T:Control{return (T)owner.Controls.Find(name,true)[0];}
    static void Pump(Func<bool> ready){var end=DateTime.UtcNow.AddSeconds(5);while(!ready()&&DateTime.UtcNow<end){Application.DoEvents();Thread.Sleep(2);}Check(ready(),"UI async work settles");}
    static void SettingsAndUi(){
        string path=Path.Combine(output,"feedback-settings.json");File.WriteAllText(path,"{\"SchemaVersion\":1}");Check(DeviceSettings.LoadBindings(path).CheckFeedbackReplies,"old settings default to enabled");
        var settings=new AudioBindings {CheckFeedbackReplies=false};DeviceSettings.SaveBindings(path,settings);Check(!DeviceSettings.LoadBindings(path).Clone().CheckFeedbackReplies,"disabled preference survives save/load/clone");
        var record=Record();record.Comments.Add(Comment("5","maintainer","literal https://example.invalid <b>hello</b>\u202e",true));var store=Store(record,Record(43,false));var http=new Http {Reply=delegate{return Replies(Item(42,Comment("6")),Item(43));}};string opened=null;bool confirmed=false;
        using(var dialog=new MyFeedbackDialog(Service(store,http),Report(),false,delegate(string url){opened=url;},42,delegate(string question){confirmed=question.Contains("不会删除 GitHub");return true;})){
            dialog.Opacity=0;dialog.Show();Application.DoEvents();var comments=Find<TextBox>(dialog,"FeedbackComments");Check(comments.ReadOnly&&comments.Multiline&&comments.GetType()==typeof(TextBox)&&comments.Text.Contains("<b>hello</b>")&&comments.Text.Contains("（内容过长，已截断）"),"history detail uses only a read-only plain TextBox");
            Check(store.Find(42).Unread==0&&store.Unread==0,"opening marks the selected comments read");Find<Button>(dialog,"OpenFeedbackWeb").PerformClick();Check(opened==FeedbackClient.IssueUrl(42),"web URL is rebuilt from the stored number");
            dialog.Reload(43);Check(!Find<Button>(dialog,"SupplementFeedback").Enabled&&comments.Text.Contains("此反馈只能在网页查看"),"keyless row is web-only");
            Find<Button>(dialog,"DeleteFeedbackRecord").PerformClick();Check(confirmed&&store.Find(43)==null&&http.Bodies.Count==0,"local deletion explicitly confirms and never touches the relay");dialog.Close();
        }
        store=Store(Record());http=new Http {Reply=delegate{throw new WebException("timeout");}};var service=Service(store,http);
        using(var dialog=new FeedbackSupplementDialog(service,42,Report(),true)){
            dialog.Opacity=0;dialog.Show();Application.DoEvents();var input=Find<TextBox>(dialog,"SupplementDescription");var submit=Find<Button>(dialog,"SubmitSupplement");
            Check(!submit.Enabled&&Find<CheckBox>(dialog,"IncludeDiagnostics").Checked&&Find<Label>(dialog,"SupplementNotice").Text=="补充内容会公开显示在 GitHub，请勿填写密码、账号等个人信息。","supplement default validation and public warning");
            input.Text="1234";Check(!submit.Enabled,"too short disabled");input.Text=new string('中',2001);Check(!submit.Enabled&&Find<Label>(dialog,"SupplementRemaining").Text.Contains("超出 1"),"too long disabled");
            input.Text="valid supplement";Find<CheckBox>(dialog,"IncludeDiagnostics").Checked=false;submit.PerformClick();Pump(delegate{return Find<Label>(dialog,"SupplementStatus").Text=="正在确认，请稍后重试";});
            Check(input.ReadOnly&&!Find<CheckBox>(dialog,"IncludeDiagnostics").Enabled&&store.Find(42).Pending.Diagnostics==null,"uncertain content is frozen for exact retry");dialog.Close();
        }
        using(var dialog=new FeedbackSupplementDialog(service,42,Report(),false)){
            Check(Find<TextBox>(dialog,"SupplementDescription").Text=="valid supplement"&&Find<TextBox>(dialog,"SupplementDescription").ReadOnly&&Find<Button>(dialog,"SubmitSupplement").Text=="重试确认","pending content restored after reopening");
        }
        var route=new TrayBalloonRoute();int update=0,low=0;long feedback=0;Action click=delegate{route.Click(delegate{update++;},delegate{low++;},delegate(long number){feedback=number;});};
        route.Set(TrayBalloonKind.Update);click();Check(update==1&&low==0&&feedback==0,"update balloon routes to update");
        route.Set(TrayBalloonKind.Feedback,42);click();Check(update==1&&low==0&&feedback==42,"feedback balloon routes to its record");
        route.Set(TrayBalloonKind.LowBattery);click();Check(update==1&&low==1&&feedback==42,"low battery still routes to device details");
        route.Set(TrayBalloonKind.Feedback,43);route.Set(TrayBalloonKind.Update);click();Check(update==2&&feedback==42,"latest displayed balloon replaces the previous route");
    }
    static void DrawRecord(ListBox list,Bitmap bitmap,int index,DrawItemState state){
        using(var graphics=Graphics.FromImage(bitmap))list.GetType().GetMethod("OnDrawItem",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(list,new object[]{new DrawItemEventArgs(graphics,list.Font,new Rectangle(Point.Empty,bitmap.Size),index,state)});
    }
    // #87: two real maintainer replies made the reply box toggle its vertical
    // scrollbar forever at 1366x728 / YaHei / 96 DPI, freezing My Feedback.
    static void HistoryLongReplies() {
        string[] texts=new[]{@"感谢反馈！VGN S99 的接收器（320F:5088）在 rainy75（#23）和 VGN N75 PRO 的反馈里也出现过：同一个 ID 下有多款不同设备，我们需要先确认读取命令不会误发给别的设备，会单独研究，有进展在这里回复。

如果 VGN 官方驱动能显示 S99 的电量，请告诉我们驱动名称和版本。在程序里打开“我的反馈”，选中这条后点“补充资料”就能回复，不需要 GitHub 账号。",@"测试版 1.3.8-beta.2 已支持 VGN 2.4G 键盘接收器（320F:5088）：https://github.com/wushuai0802/myfinder-device-battery/releases/tag/v1.3.8-beta.2

这是按 VGN 官方驱动的读电量方式实现的，维护者用自己的 VGN 键盘实测读到 93%（VGN HUB 显示 94%）。键盘休眠时会保留上次读数，按任意键后刷新。欢迎试用并对照 VGN HUB 的读数，在程序“我的反馈”里选中这条点“补充资料”告诉我们是否一致。

正式版用户的程序内更新只推送正式版，试用测试版请下载 zip 解压后运行“一键安装自启.bat”，设置会保留。",@"感谢反馈和截图！很高兴 VGN S99 的电量已经能读到了。

“我的反馈”窗口卡死（Not Responding）我们正在排查。截图显示窗口在第一次排版时就卡住了，但在维护者的测试环境里还没有复现。想请你帮忙确认几点：

1. 是不是每次点“我的反馈”都会卡？卡住后等一两分钟会不会恢复？
2. 你当前用的版本（状态与诊断页顶部，例如 1.3.8-beta.2 或 beta.3）。
3. Windows 的显示缩放比例（设置 → 显示 → 缩放，例如 100% / 125% / 150%）和屏幕分辨率。
4. 大概在程序里提交过几条反馈。

卡住时可以在任务栏托盘图标上右键退出，或在任务管理器里结束 MyFinder 设备电量。谢谢！
"};
        var previousArea=ThemedDialog.WorkingAreaProvider;var previousDpi=DialogDpi.DeviceDpiReader;
        Exception failure=null;bool finished=false;string current="";DateTime caseStarted=DateTime.UtcNow;int done=0;
        var worker=new Thread(delegate(){
            try{
                foreach(Size area in new[]{new Size(1366,728),new Size(1920,1040)})foreach(string face in new[]{"Microsoft YaHei UI","Segoe UI","Microsoft Sans Serif"})foreach(int dpi in new[]{96,120,144})foreach(int count in new[]{1,2})foreach(int replies in new[]{1,2,3}){
                    ThemedDialog.WorkingAreaProvider=delegate{return new Rectangle(Point.Empty,area);};DialogDpi.DeviceDpiReader=delegate{return dpi;};
                    current=area.Width+"x"+area.Height+" "+face+" "+dpi+"dpi records="+count+" replies="+replies;caseStarted=DateTime.UtcNow;done++;
                    var store=Store();store.Change(delegate(FeedbackData data){for(int i=1;i<=count;i++){var record=Record(80+i);record.Summary="设备不识别：设备不识别，设备名称 vgn s99 ,使用2.4g无线连接。".Substring(0,30);record.CreatedAt=Start.AddMinutes(-i);for(int r=0;r<replies;r++)record.Comments.Add(Comment((i*100+r+1).ToString(),r%2==0?"maintainer":"automation",texts[r%texts.Length]));data.Records.Add(record);}});
                    using(var dialog=new MyFeedbackDialog(Service(store,new Http()),Report(),false,delegate{}))using(var systemFont=new Font(face,9f*dpi/96f,FontStyle.Regular,GraphicsUnit.Point)){
                        dialog.Font=systemFont;dialog.Opacity=0;dialog.Show();for(int pump=0;pump<8;pump++)Application.DoEvents();dialog.Close();
                    }
                }
                finished=true;
            }catch(Exception error){failure=error;}
        });
        worker.SetApartmentState(ApartmentState.STA);worker.IsBackground=true;worker.Start();
        while(!worker.Join(1000)){if((DateTime.UtcNow-caseStarted).TotalSeconds>15)throw new Exception("FAIL history dialog hangs: "+current+" (after "+done+" cases)");}
        ThemedDialog.WorkingAreaProvider=previousArea;DialogDpi.DeviceDpiReader=previousDpi;
        if(failure!=null)throw failure;Check(finished,"long-reply history sweep completed");
    }
    static void HistoryAppearance(){
        var previousArea=ThemedDialog.WorkingAreaProvider;var previousDpi=DialogDpi.DeviceDpiReader;var previousSettings=DeviceSettings.Current.Clone();var previousContrast=VisualDesign.HighContrastOverride;
        try{
            VisualDesign.HighContrastOverride=false;ThemedDialog.WorkingAreaProvider=delegate{return new Rectangle(0,0,3200,2400);};
            foreach(string style in ThemePalette.Keys)foreach(bool dark in new[]{false,true})foreach(int dpi in new[]{96,120,144,192})foreach(int count in new[]{0,1,10}){
                DeviceSettings.UseTransientConfiguration(new AudioBindings {ThemeStyle=style,Theme=dark?"dark":"light",RememberReadings=false,ReducedMotion=true});DialogDpi.DeviceDpiReader=delegate{return dpi;};
                var store=Store();store.Change(delegate(FeedbackData data){for(int i=1;i<=count;i++){var record=Record(i);record.CreatedAt=Start.AddMinutes(-i);record.Comments.Add(Comment(i.ToString()));data.Records.Add(record);}});
                float scale=dpi/96f;
                using(var font=new Font("Microsoft YaHei UI",VisualDesign.BodyFontSize*scale,FontStyle.Regular,GraphicsUnit.Pixel))
                using(var dialog=new MyFeedbackDialog(Service(store,new Http()),Report(),dark,delegate{})){
                    Check(dialog.FormBorderStyle==FormBorderStyle.FixedDialog&&!dialog.MaximizeBox&&!dialog.MinimizeBox&&dialog.StartPosition==FormStartPosition.CenterParent,"history uses the fixed themed dialog frame");
                    Check(dialog.ClientSize.Width>=540&&dialog.ClientSize.Width<=620,"history starts at feedback-form width");
                    dialog.Scale(new SizeF(scale,scale));dialog.Font=font;dialog.Opacity=0;dialog.Show();Application.DoEvents();
                    var list=Find<ListBox>(dialog,"FeedbackRecords");var close=Find<ThemedButton>(dialog,"CloseFeedbackHistory");var supplement=Find<ThemedButton>(dialog,"SupplementFeedback");var delete=Find<ThemedButton>(dialog,"DeleteFeedbackRecord");
                    Check(list.DrawMode==DrawMode.OwnerDrawFixed&&list.AccessibleName=="我的反馈列表","native list keeps accessible selection with owner-drawn rows");
                    int scrollbar=list.HorizontalExtent>list.ClientSize.Width?SystemInformation.HorizontalScrollBarHeight:0;
                    Check(list.Height-scrollbar==Math.Max(2,Math.Min(6,count))*list.ItemHeight,"history reserves two to six complete rows at "+dpi+" DPI for "+count+" records");
                    Check(Math.Abs(list.ItemHeight-list.Font.Height*1.8)<=1,"history row spacing follows the scaled font");
                    Check(Find<Label>(dialog,"FeedbackHistoryTitle").Text=="我的反馈"&&Find<Label>(dialog,"FeedbackHistoryNotice").Text.Contains("公开显示"),"history has a title and public supplement notice");
                    Check(Find<Label>(dialog,"FeedbackHistoryEmpty").Visible==(count==0),"empty history has a visible explanation");
                    Check(close.FillFor(false,false)==dialog.Theme.Track&&delete.FillFor(false,false)==dialog.Theme.Card,"close is ordinary and local deletion is a separate text action");
                    Check(supplement.Enabled==(count>0)&&supplement.FillFor(false,false)==(count>0?dialog.Theme.Selected:dialog.Theme.Track),"only the available supplement uses primary styling");
                    var deleteBounds=delete.RectangleToScreen(delete.ClientRectangle);Check(deleteBounds.Right<=supplement.Parent.RectangleToScreen(supplement.Parent.ClientRectangle).Left,"local deletion is separated on the left");
                    int lastButtonBottom=0;
                    foreach(string name in new[]{"CloseFeedbackHistory","RefreshFeedbackReplies","OpenFeedbackWeb","SupplementFeedback","DeleteFeedbackRecord"}){
                        var button=Find<Button>(dialog,name);var bounds=button.RectangleToScreen(button.ClientRectangle);
                        lastButtonBottom=Math.Max(lastButtonBottom,bounds.Bottom);
                        for(Control parent=button.Parent;parent!=null;parent=parent.Parent)Check(parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds),name+" stays inside its ancestors at "+dpi+" DPI");
                    }
                    Check(delete.Parent.RectangleToScreen(delete.Parent.ClientRectangle).Bottom-lastButtonBottom<=4*scale,"button bar does not reserve unused rows: bar="+delete.Parent.Size+", preferred="+delete.Parent.GetPreferredSize(new Size(delete.Parent.Width,0))+", actions="+supplement.Parent.Size+", action preferred="+supplement.Parent.GetPreferredSize(new Size(supplement.Parent.Width,0))+", gap="+(delete.Parent.RectangleToScreen(delete.Parent.ClientRectangle).Bottom-lastButtonBottom));
                    if(count>0){
                        Check(list.GetItemText(list.Items[0])==store.Find(1).ListText,"owner drawing preserves ListText exactly");close.Focus();
                        using(var bitmap=new Bitmap(list.Width,list.ItemHeight)){
                            DrawRecord(list,bitmap,0,DrawItemState.Selected|DrawItemState.Focus);
                            Color selected=bitmap.GetPixel(bitmap.Width-8,(int)(bitmap.Height/2));
                            Check(selected.ToArgb()==dialog.Theme.Interaction(dialog.Theme.Card,false).ToArgb()&&selected.ToArgb()!=SystemColors.Highlight.ToArgb(),"selected history uses the palette's soft panel fill in "+style);
                            Check(bitmap.GetPixel(0,0).ToArgb()==dialog.Theme.Card.ToArgb(),"selection leaves rounded card-colored corners");
                            if(count==10){
                                DrawRecord(list,bitmap,1,DrawItemState.None);Check(bitmap.GetPixel((int)(10*scale),bitmap.Height/2).ToArgb()==dialog.Theme.Accent.ToArgb(),"unread row has a palette accent dot");
                            }
                        }
                    }
                    if(count==10&&dpi==96){
                        int outlines=0;var observer=CompactRenderer.InteractionOutlineObserver;
                        try{
                            CompactRenderer.InteractionOutlineObserver=delegate{outlines++;};
                            using(var bitmap=new Bitmap(list.Width,list.ItemHeight)){
                                DrawRecord(list,bitmap,0,DrawItemState.Selected|DrawItemState.Focus);Check(outlines==0,"unfocused history does not draw a focus ring");
                                list.Focus();SendMessage(list.Handle,0x0127,(IntPtr)(1|(1<<16)),IntPtr.Zero);outlines=0;
                                DrawRecord(list,bitmap,0,DrawItemState.Selected|DrawItemState.Focus);Check(outlines==0,"mouse focus keeps keyboard cues hidden");
                                SendMessage(list.Handle,0x0127,(IntPtr)(2|(1<<16)),IntPtr.Zero);outlines=0;
                                DrawRecord(list,bitmap,0,DrawItemState.Selected|DrawItemState.Focus);Check(list.Focused&&outlines==1,"keyboard focus draws the panel focus ring");
                            }
                        }finally{CompactRenderer.InteractionOutlineObserver=observer;}
                        SendMessage(list.Handle,0x0100,(IntPtr)Keys.Down,IntPtr.Zero);SendMessage(list.Handle,0x0101,(IntPtr)Keys.Down,IntPtr.Zero);Application.DoEvents();
                        Check(list.SelectedIndex==1&&store.Find(2).Unread==0,"Down selects the next record and retains read marking");
                        SendMessage(list.Handle,0x0100,(IntPtr)Keys.Up,IntPtr.Zero);SendMessage(list.Handle,0x0101,(IntPtr)Keys.Up,IntPtr.Zero);Application.DoEvents();Check(list.SelectedIndex==0,"Up selects the previous record");
                        store.Change(delegate(FeedbackData data){data.Records.RemoveAll(delegate(FeedbackRecord record){return record.Number>1;});});dialog.Reload();Application.DoEvents();
                        scrollbar=list.HorizontalExtent>list.ClientSize.Width?SystemInformation.HorizontalScrollBarHeight:0;Check(list.Height-scrollbar==2*list.ItemHeight,"reload shrinks the list after records are deleted");
                        dialog.ClientSize=new Size(390,580);Application.DoEvents();Check(close.Top!=supplement.Top,"actions wrap when the available width narrows");
                        foreach(string name in new[]{"CloseFeedbackHistory","RefreshFeedbackReplies","OpenFeedbackWeb","SupplementFeedback","DeleteFeedbackRecord"}){
                            var button=Find<Button>(dialog,name);var bounds=button.RectangleToScreen(button.ClientRectangle);
                            for(Control parent=button.Parent;parent!=null;parent=parent.Parent)Check(parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds),"wrapped "+name+" stays reachable");
                        }
                    }
                    dialog.Close();
                }
            }
        }finally{ThemedDialog.WorkingAreaProvider=previousArea;DialogDpi.DeviceDpiReader=previousDpi;DeviceSettings.UseTransientConfiguration(previousSettings);VisualDesign.HighContrastOverride=previousContrast;}
    }
    static void Layouts(){
        var previous=ThemedDialog.WorkingAreaProvider;
        try{
            foreach(bool dark in new[]{false,true})foreach(float scale in new[]{1f,1.25f,1.5f,2f})foreach(bool small in new[]{false,true}){
                var area=new Rectangle(0,0,small?1024:1920,small?560:1080);ThemedDialog.WorkingAreaProvider=delegate{return area;};
                var record=Record();record.Comments.Add(Comment("1","maintainer","请提供设备型号、连接方式和复现步骤。\nhttps://example.invalid 仅为纯文本。",true));record.Comments.Add(Comment("2","automation","请检查接收器连接。"));
                var store=Store(record,Record(43,false));var service=Service(store,new Http());
                using(var history=new MyFeedbackDialog(service,Report(),dark,delegate{},42))using(var supplement=new FeedbackSupplementDialog(service,42,Report(),dark)){
                    foreach(var dialog in new ThemedDialog[]{history,supplement}){
                        dialog.Scale(new SizeF(scale,scale));dialog.Font=new Font("Microsoft YaHei UI",12*scale,FontStyle.Regular,GraphicsUnit.Pixel);dialog.Opacity=0;dialog.Show();Application.DoEvents();
                        Check(area.Contains(dialog.Bounds),"new dialog fits small/large work area at scale "+scale);
                        string[] actions=dialog==history?new[]{"RefreshFeedbackReplies","OpenFeedbackWeb","SupplementFeedback","DeleteFeedbackRecord"}:new[]{"SubmitSupplement"};
                        foreach(string name in actions){var button=Find<Button>(dialog,name);var bounds=button.RectangleToScreen(button.ClientRectangle);for(Control parent=button.Parent;parent!=null;parent=parent.Parent)Check(parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds),name+" is not clipped at scale "+scale);}
                        var text=Find<TextBox>(dialog,dialog==history?"FeedbackComments":"SupplementDescription");Check(text.Height>=3*text.Font.Height,"new dialog keeps three readable text lines");
                        if(!small&&scale==1f){using(var bitmap=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(output,(dialog==history?"feedback-history-":"feedback-supplement-")+(dark?"dark":"light")+".png"));}}
                        dialog.Close();
                    }
                }
            }
        }finally{ThemedDialog.WorkingAreaProvider=previous;}
    }
    static object Field(object owner,string name){return owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(owner);}
    static int HistoryWindows(){int count=0;foreach(Form form in Application.OpenForms)if(form is MyFeedbackDialog)count++;return count;}
    static void HistoryEntry(Action entry,MyFeedbackDialog expected,bool submission=false){
        int duplicates=0;bool clicked=false;string marker="entry "+Guid.NewGuid().ToString("N");var replies=(FeedbackReplies)Field(expected,"replies");
        replies.Store.Change(delegate(FeedbackData data){data.Records.Find(delegate(FeedbackRecord r){return r.Number==42;}).Comments.Add(Comment((9000+checks).ToString(),"maintainer",marker));});
        // Close a regressed modal duplicate so a failure reports instead of hanging the harness.
        using(var guard=new System.Windows.Forms.Timer {Interval=20}){
            guard.Tick+=delegate{
                var forms=new List<Form>();foreach(Form form in Application.OpenForms)forms.Add(form);
                foreach(var form in forms)if(form is MyFeedbackDialog&&!Object.ReferenceEquals(form,expected)){duplicates++;form.Close();}
                if(submission&&!clicked)foreach(var form in forms)if(form is FeedbackForm){clicked=true;Find<Button>(form,"MyFeedback").PerformClick();form.Close();}
            };
            guard.Start();entry();guard.Stop();
        }
        Check(duplicates==0&&HistoryWindows()==1&&!expected.IsDisposed&&Find<TextBox>(expected,"FeedbackComments").Text.Contains(marker),"entry reloads the existing history window");
        if(submission)Check(clicked,"production submission form button was exercised");
    }
    static void NativeBalloonRouting(){
        DeviceSettings.UseTransientConfiguration(new AudioBindings {RememberReadings=false,ReducedMotion=true});
        using(var owner=new TrayContext(false,delegate(bool battery){return new MouseSnapshot();},delegate(int stage){throw new Exception("No device writes");},null,null)){
            Pump(delegate{return !owner.IsRefreshing;});((System.Windows.Forms.Timer)Field(owner,"refresh")).Stop();
            var first=Record();first.Comments.Add(Comment("7","maintainer","selected record reply"));var second=Record(43);second.Comments.Add(Comment("8"));
            owner.FeedbackReplies=Service(Store(first,second),new Http());
            using(var form=new FeedbackForm(Report(),false,false,delegate{},delegate{},Relay,new Http(),feedbackReplies:owner.FeedbackReplies))Check(Find<Button>(form,"MyFeedback").Text=="我的反馈（2）","submission form shows unread reply count");
            var tray=(NotifyIcon)Field(owner,"tray");var route=(TrayBalloonRoute)Field(owner,"balloonRoute");
            typeof(ContextMenuStrip).GetMethod("OnOpening",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(tray.ContextMenuStrip,new object[]{new System.ComponentModel.CancelEventArgs()});
            ToolStripMenuItem menu=null;foreach(ToolStripItem item in tray.ContextMenuStrip.Items)if(item.Text=="我的反馈（2）")menu=(ToolStripMenuItem)item;Check(menu!=null,"tray menu shows unread reply count");
            route.Set(TrayBalloonKind.Feedback,42);typeof(NotifyIcon).GetMethod("OnBalloonTipClicked",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(tray,null);Application.DoEvents();
            var history=(MyFeedbackDialog)Field(owner,"feedbackDialog");Check(history!=null&&history.Visible&&Find<TextBox>(history,"FeedbackComments").Text.Contains("selected record reply")&&owner.FeedbackReplies.Store.Find(43).Unread==1,"native feedback balloon opens and marks only its target record");
            HistoryEntry(delegate{menu.PerformClick();},history);
            route.Set(TrayBalloonKind.LowBattery);typeof(NotifyIcon).GetMethod("OnBalloonTipClicked",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(tray,null);Application.DoEvents();
            var details=Field(owner,"details");Check(details!=null,"native low-battery balloon still opens device panel");
            HistoryEntry(delegate{details.GetType().GetMethod("ShowMyFeedback",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(details,null);},history);
            HistoryEntry(delegate{details.GetType().GetMethod("ShowFeedback",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(details,null);},history,true);
            route.Set(TrayBalloonKind.Feedback,43);typeof(NotifyIcon).GetMethod("OnBalloonTipClicked",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(tray,null);Application.DoEvents();
            Check(Object.ReferenceEquals(Field(owner,"feedbackDialog"),history)&&HistoryWindows()==1&&Find<TextBox>(history,"FeedbackComments").Text.Contains("请提供复现步骤")&&owner.FeedbackReplies.Store.Find(43).Unread==0,"a second balloon reloads its requested number in the same instance");
            history.Close();owner.OpenMyFeedback(42);Check(!Object.ReferenceEquals(Field(owner,"feedbackDialog"),history)&&HistoryWindows()==1,"closing history allows a fresh single instance");owner.ExitThread();
        }
    }
    [STAThread] static int Main(string[] args){
        output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);NativeDisplay.EnableDpiAwareness();Application.EnableVisualStyles();
        Run("schema 2 creation and safe fallback",Creation);Run("creation placeholder retention",CreationRetention);Run("confirmed receipt cancellation",ConfirmedReceiptCancellation);Run("DPAPI persistence and recovery",Persistence);Run("scheduling and pause policy",Scheduler);Run("closed and idle scheduling regressions",ScheduleRegressions);Run("merge and notifications",Merging);Run("read and manual refresh notifications",NotificationRegressions);Run("reply validation and batching",ReplyBoundaries);Run("defaults and batch failure messages",DefaultsAndBatchFailures);Run("supplement idempotency and errors",Supplements);Run("settings, dialogs and balloon routing",SettingsAndUi);Run("history themes, adaptive rows and DPI",HistoryAppearance);Run("history and supplement layout",Layouts);Run("history with long real replies settles",HistoryLongReplies);Run("native balloon and unread routing",NativeBalloonRouting);
        Console.WriteLine(checks+" feedback reply checks, "+failures+" failed groups; fake transport only");return failures==0?0:1;
    }
}
