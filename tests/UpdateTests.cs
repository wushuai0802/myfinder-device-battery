using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

static class UpdateTests {
    static int passed;
    static void Check(bool value,string name){if(!value)throw new Exception("FAIL "+name);passed++;}
    static void Reject(Action action,string name){bool rejected=false;try{action();}catch(Exception){rejected=true;}Check(rejected,name);}
    static void Throws<T>(Action action,string name) where T:Exception {bool rejected=false;try{action();}catch(T){rejected=true;}Check(rejected,name);}
    static void HelperEnvironment(string root){
        const string sentinel="MYFINDER_UPDATE_TEST_ENV";
        string modules=Environment.GetEnvironmentVariable("PSModulePath"),previous=Environment.GetEnvironmentVariable(sentinel);
        try {
            string inherited=Path.Combine(root,"PowerShell 7 modules");
            Environment.SetEnvironmentVariable("PSModulePath",inherited);Environment.SetEnvironmentVariable(sentinel,"preserved");
            var start=UpdateProcess.HelperStartInfo(Path.Combine(root,"Apply-Update.ps1"),new string[0]);
            Check(!start.EnvironmentVariables.ContainsKey("PSModulePath"),"helper drops inherited PSModulePath");
            Check(Environment.GetEnvironmentVariable("PSModulePath")==inherited,"helper leaves parent PSModulePath unchanged");
            foreach(System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
                if(!((string)variable.Key).Equals("PSModulePath",StringComparison.OrdinalIgnoreCase))
                    Check(start.EnvironmentVariables[(string)variable.Key]==(string)variable.Value,"helper preserves environment variable "+variable.Key);
        }finally{Environment.SetEnvironmentVariable("PSModulePath",modules);Environment.SetEnvironmentVariable(sentinel,previous);}
    }
    static void FailureNotification(string root){
        string data=Path.Combine(root,"failure notification");Directory.CreateDirectory(data);
        string path=Path.Combine(data,"update-result.json"),reason="fixture hash validation failed";
        using(var dispatch=new Control()){
            dispatch.CreateControl();var handle=dispatch.Handle;
            foreach(string status in new[]{"failed","failed","success"}){
                bool notified=status=="failed"&&File.Exists(path);int changes=0,balloons=0;
                var result=new UpdateResult {Schema=1,Status=status,Version="1.3.6",Reason=reason,Notified=notified};
                string original=UpdateJson.Text(result);File.WriteAllText(path,original);
                var controller=(UpdateController)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(UpdateController));
                var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                typeof(UpdateController).GetField("data",flags).SetValue(controller,data);
                typeof(UpdateController).GetField("dispatch",flags).SetValue(controller,dispatch);
                typeof(UpdateController).GetField("changed",flags).SetValue(controller,(Action)delegate{changes++;});
                typeof(UpdateController).GetField("balloon",flags).SetValue(controller,(Action<string,string,bool>)delegate(string title,string text,bool update){
                    balloons++;Check(title=="上次更新失败"&&text==reason&&!update,"failure notification shows the persisted reason");
                });
                typeof(UpdateController).GetMethod("Initialize",flags).Invoke(controller,null);Application.DoEvents();
                Check(controller.LastFailure==(status=="failed"?reason:null),"next launch exposes only failed update reasons");
                Check(changes==(status=="failed"?1:0)&&balloons==(status=="failed"&&!notified?1:0),"failure notification is shown once across launches");
                if(status=="success")Check(File.ReadAllText(path)==original,"success result path remains unchanged");
                else Check(UpdateJson.ReadFile<UpdateResult>(path,16384).Notified,"failure notification acknowledgement is persisted");
            }
        }
    }
    sealed class Http:IUpdateTransport {
        internal readonly Queue<UpdateResponse> Responses=new Queue<UpdateResponse>();
        internal readonly List<Uri> Requests=new List<Uri>();
        internal bool Timeout;
        public UpdateResponse Get(Uri uri,CancellationToken token){token.ThrowIfCancellationRequested();Requests.Add(uri);if(Timeout)throw new WebException("fixture timeout",WebExceptionStatus.Timeout);return Responses.Dequeue();}
        internal void Reply(string text,int status=200,string location=null,long length=-1,string retry=null,string remaining=null,string reset=null){Responses.Enqueue(new UpdateResponse(status,new MemoryStream(Encoding.UTF8.GetBytes(text)),length,location,retry,remaining,reset));}
        internal void Bytes(byte[] bytes){Responses.Enqueue(new UpdateResponse(200,new MemoryStream(bytes),bytes.Length,null,null));}
    }
    sealed class Installer:IUpdateInstaller {
        internal bool Stable=true;internal int Starts;
        internal string Root,Package;
        public bool IsStable(CancellationToken token){return Stable;}
        public void Start(PreparedUpdate update){Starts++;Root=update.InstallDirectory;Package=update.PackageDirectory;}
    }
    sealed class Clock {
        internal double Seconds;
        internal TimeSpan Now(){return TimeSpan.FromSeconds(Seconds);}
    }
    sealed class SlowStream:MemoryStream {
        readonly Clock clock;readonly double[] delays;
        internal int Reads;internal Action OnRead;
        internal SlowStream(Clock timer,int bytes,params double[] waits):base(new byte[bytes]){clock=timer;delays=waits;}
        public override int Read(byte[] buffer,int offset,int count){
            if(Reads<delays.Length)clock.Seconds+=delays[Reads];Reads++;
            if(OnRead!=null)OnRead();return base.Read(buffer,offset,Math.Min(count,1));
        }
    }
    static Http SlowHttp(SlowStream body,long length){var http=new Http();http.Responses.Enqueue(new UpdateResponse(200,body,length,null,null));return http;}
    static void NetworkTimeouts(){
        var uri=new Uri("https://release-assets.githubusercontent.com/package.zip");
        var clock=new Clock();var body=new SlowStream(clock,8,Enumerable.Repeat(20.0,8).ToArray());var http=SlowHttp(body,8);
        using(var output=new MemoryStream()){
            int reports=0;
            UpdateNetwork.Download(http,uri,output,100,CancellationToken.None,delegate(long count,long total){reports++;Check(count==reports&&total==8,"slow transfer reports cumulative progress");},clock.Now);
            Check(clock.Seconds==160&&output.Length==8&&reports==8,"continuous slow download survives more than 120 seconds");
        }
        foreach(bool eof in new[]{false,true}){
            clock=new Clock();body=new SlowStream(clock,eof?1:2,1,30);http=SlowHttp(body,-1);
            using(var output=new MemoryStream()){
                Throws<TimeoutException>(()=>UpdateNetwork.Download(http,uri,output,100,CancellationToken.None,null,clock.Now),"30 second stall fails, including EOF="+eof);
                Check(output.Length==1&&clock.Seconds==31,"stalled transfer stops before writing late data");
            }
        }
        clock=new Clock();body=new SlowStream(clock,90,Enumerable.Repeat(20.0,90).ToArray());http=SlowHttp(body,90);
        using(var output=new MemoryStream()){
            Throws<TimeoutException>(()=>UpdateNetwork.Download(http,uri,output,100,CancellationToken.None,null,clock.Now),"active download still stops at the 30 minute total limit");
            Check(clock.Seconds==1800&&output.Length==89,"total limit is independent of continuing progress");
        }
        // Release metadata, checksums and signatures all use Fetch and retain its 120 second cap.
        foreach(var small in new[]{new Uri(UpdateTrust.ApiUrl),new Uri("https://github.com/SHA256SUMS.txt"),new Uri("https://github.com/SHA256SUMS.txt.sig")}){
            clock=new Clock();body=new SlowStream(clock,8,Enumerable.Repeat(20.0,8).ToArray());http=SlowHttp(body,8);
            Throws<TimeoutException>(()=>UpdateNetwork.Fetch(http,small,100,CancellationToken.None,null,clock.Now),"small request retains its total timeout: "+small.AbsolutePath);
            Check(clock.Seconds==140,"small request is not extended to the package deadline");
        }
        clock=new Clock();body=new SlowStream(clock,3);http=SlowHttp(body,3);
        using(var token=new CancellationTokenSource())using(var output=new MemoryStream()){
            Throws<OperationCanceledException>(()=>UpdateNetwork.Download(http,uri,output,100,token.Token,delegate(long count,long total){token.Cancel();},clock.Now),"cancel from progress is observed before another read");
            Check(body.Reads==1&&output.Length==1,"cancel does not attempt another potentially blocking read");
        }
        clock=new Clock();body=new SlowStream(clock,3);http=SlowHttp(body,3);
        using(var token=new CancellationTokenSource())using(var output=new MemoryStream()){
            body.OnRead=delegate{token.Cancel();throw new IOException("fixture aborted read");};
            Throws<OperationCanceledException>(()=>UpdateNetwork.Download(http,uri,output,100,token.Token,null,clock.Now),"aborted read is reported as cancellation");
            Check(body.Reads==1&&output.Length==0,"cancelled read writes no data");
        }
        clock=new Clock();body=new SlowStream(clock,3);http=SlowHttp(body,3);
        using(var token=new CancellationTokenSource())using(var output=new MemoryStream()){
            token.Cancel();Throws<OperationCanceledException>(()=>UpdateNetwork.Download(http,uri,output,100,token.Token,null,clock.Now),"pre-cancelled transfer is refused");
            Check(http.Requests.Count==0&&body.Reads==0,"pre-cancelled transfer sends no HTTP");
        }
    }
    static UpdateAsset Asset(string version,string name){return new UpdateAsset {Name=name,Size=100,Url="https://github.com/wushuai0802/myfinder-device-battery/releases/download/v"+version+"/"+name};}
    static UpdateRelease Release(string version,bool pre=false){return new UpdateRelease {Tag="v"+version,Prerelease=pre,Body="<script>bad()</script>\r\n[click](file:///C:/x)",Assets=new[]{Asset(version,UpdateTrust.ZipName(version)),Asset(version,"SHA256SUMS.txt"),Asset(version,"SHA256SUMS.txt.sig")}};}
    static void PlainReleaseNotes(){
        foreach(string text in new[]{"<script>bad()</script>\r\n[click](file:///C:/x)",@"{\rtf1\ansi\b This must remain literal RTF text\b0}","https://example.invalid/\r\nfile:///C:/x\r\n[click](javascript:bad())"}){
            var release=Release("1.3.6");release.Body=text;
            using(var dialog=new UpdateDialog(release,true)){
                // The visual brief requires 1.4x line spacing. Both native edit
                // types must preserve the same read-only, plain-text contract.
                var notes=dialog.Controls.Find("ReleaseNotes",true).Single() as TextBoxBase;
                Check(notes!=null&&notes.ReadOnly&&notes.Multiline,"release notes use a read-only native multiline text control");
                dialog.Opacity=0;dialog.ShowInTaskbar=false;dialog.Show();Application.DoEvents();
                Check(notes.Text.Replace("\r\n","\n")==text.Replace("\r\n","\n"),"HTML, RTF and URL release notes remain literal text after native handle creation");
                var rich=notes as RichTextBox;
                Check(rich==null||!rich.DetectUrls,"release notes do not generate active links");
            }
        }
    }
    static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
    static byte[] Zip(string version,string app="myfinder-device-battery",string extra=null,bool badHash=false,int attributes=0){
        var files=new Dictionary<string,byte[]>();
        files["release.json"]=Encoding.UTF8.GetBytes("{\"schema\":1,\"appId\":\""+app+"\",\"version\":\""+version+"\",\"assemblyVersion\":\"1.3.6.0\",\"configSchema\":1,\"engineSchema\":2}");
        foreach(string path in UpdatePackageVerifier.RequiredFiles)if(!files.ContainsKey(path))files[path]=Encoding.UTF8.GetBytes("fixture "+path);
        var rows=files.Select(p=>"{\"path\":\""+p.Key+"\",\"length\":"+p.Value.Length+",\"sha256\":\""+(badHash?new string('0',64):Hash(p.Value))+"\"}");
        files["package-manifest.json"]=Encoding.UTF8.GetBytes("{\"schema\":1,\"appId\":\"myfinder-device-battery\",\"version\":\""+version+"\",\"configSchema\":1,\"engineSchema\":2,\"files\":["+String.Join(",",rows)+"]}");
        using(var output=new MemoryStream()){
            using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true)){
                foreach(var file in files)using(var stream=zip.CreateEntry(file.Key).Open())stream.Write(file.Value,0,file.Value.Length);
                if(extra!=null){var entry=zip.CreateEntry(extra);entry.ExternalAttributes=attributes;using(var stream=entry.Open())stream.WriteByte(1);}
            }return output.ToArray();
        }
    }
    static void UpdateBackoffPolicy(string root){
        var now=new DateTime(2026,1,2,0,0,0,DateTimeKind.Utc);string path;UpdateBackoffInfo wait;Http http;UpdateChecker checker;
        path=Path.Combine(root,"backoff-seconds.json");http=new Http();http.Reply("limited",403,null,-1,"120");checker=new UpdateChecker(http,path,"1.3.4",now.AddDays(-1));Check(checker.Check(true,true,now)==null,"120-second rate limit is quiet");wait=checker.Backoff(UpdateBackoffScope.Catalog,now);Check(wait.Source=="server"&&wait.UntilUtc==now.AddMinutes(2)&&wait.FailureCount==1,"valid Retry-After seconds set a two-minute catalog wait");Check(checker.DownloadsAllowed(now.AddMinutes(3)),"catalog wait does not block the asset channel");
        path=Path.Combine(root,"backoff-date.json");http=new Http();http.Reply("limited",403,null,-1,now.AddMinutes(7).ToString("R"));checker=new UpdateChecker(http,path,"1.3.4",now.AddDays(-1));checker.Check(true,true,now);wait=checker.Backoff(UpdateBackoffScope.Catalog,now);Check(wait.Source=="server"&&wait.UntilUtc==now.AddMinutes(7),"HTTP-date Retry-After is parsed as UTC");
        path=Path.Combine(root,"backoff-reset.json");http=new Http();http.Reply("limited",429,null,-1,null,"0",((long)(now.AddMinutes(15)-new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalSeconds).ToString());checker=new UpdateChecker(http,path,"1.3.4",now.AddDays(-1));checker.Check(true,true,now);wait=checker.Backoff(UpdateBackoffScope.Catalog,now);Check(wait.Source=="server"&&wait.UntilUtc==now.AddMinutes(15),"remaining zero plus reset epoch sets the catalog wait");
        path=Path.Combine(root,"backoff-combined.json");http=new Http();http.Reply("limited",403,null,-1,"120","0",((long)(now.AddMinutes(10)-new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalSeconds).ToString());checker=new UpdateChecker(http,path,"1.3.4",now.AddDays(-1));checker.Check(true,true,now);wait=checker.Backoff(UpdateBackoffScope.Catalog,now);Check(wait.UntilUtc==now.AddMinutes(10),"multiple valid server deadlines use the latest");
        path=Path.Combine(root,"backoff-fallback.json");checker=new UpdateChecker(new Http(),path,"1.3.4",now.AddDays(-1));checker.Defer(new UpdateHttpFailure(403,"-1","bad","-1"),now,UpdateBackoffScope.Catalog);wait=checker.Backoff(UpdateBackoffScope.Catalog,now);Check(wait.Source=="fallback"&&wait.UntilUtc==now.AddMinutes(5)&&wait.FailureCount==1,"invalid headers use a five-minute fallback");checker.Defer(new UpdateHttpFailure(403,"overflow"),now.AddMinutes(5),UpdateBackoffScope.Catalog);wait=checker.Backoff(UpdateBackoffScope.Catalog,now.AddMinutes(5));Check(wait.UntilUtc==now.AddMinutes(15)&&wait.FailureCount==2,"fallback grows exponentially after a consecutive failure");checker.Defer(new UpdateHttpFailure(403,"bad"),now.AddMinutes(15),UpdateBackoffScope.Catalog);checker.Defer(new UpdateHttpFailure(403,"bad"),now.AddMinutes(35),UpdateBackoffScope.Catalog);checker.Defer(new UpdateHttpFailure(403,"bad"),now.AddMinutes(75),UpdateBackoffScope.Catalog);wait=checker.Backoff(UpdateBackoffScope.Catalog,now.AddMinutes(75));Check(wait.UntilUtc==now.AddMinutes(135)&&wait.FailureCount==5,"fallback caps at one hour");checker.Complete(UpdateBackoffScope.Catalog);wait=checker.Backoff(UpdateBackoffScope.Catalog,now.AddMinutes(75));Check(wait.UntilUtc==DateTime.MinValue&&wait.FailureCount==0,"successful catalog completion clears its wait and count");
        path=Path.Combine(root,"backoff-legacy.json");var legacy=new UpdateState {NotBeforeUtc=now.AddDays(1),SkippedVersion="1.3.5",SeenVersion="1.3.4",NotifiedVersion="1.3.3",AvailableRelease=Release("1.3.6")};File.WriteAllText(path,UpdateJson.Text(legacy));checker=new UpdateChecker(new Http(),path,"1.3.4",now.AddDays(-1));wait=checker.Backoff(UpdateBackoffScope.Catalog,now);Check(wait.Source=="legacy"&&wait.UntilUtc==now.AddDays(1),"legacy unknown wait remains conservative and identifies its source");Check(!checker.DownloadsAllowed(now.AddHours(1)),"legacy wait still protects downloads");var restored=UpdateJson.ReadFile<UpdateState>(path,65536);Check(restored.SkippedVersion=="1.3.5"&&restored.SeenVersion=="1.3.4"&&restored.NotifiedVersion=="1.3.3"&&restored.AvailableRelease!=null,"legacy cache and four notification markers survive migration");
        path=Path.Combine(root,"backoff-asset.json");checker=new UpdateChecker(new Http(),path,"1.3.4",now.AddDays(-1));checker.Defer(new UpdateHttpFailure(429,"60"),now,UpdateBackoffScope.Asset);Check(checker.Backoff(UpdateBackoffScope.Asset,now).UntilUtc==now.AddMinutes(1)&&checker.Backoff(UpdateBackoffScope.Catalog,now).UntilUtc==DateTime.MinValue,"asset wait is recorded independently");checker.Complete(UpdateBackoffScope.Asset);Check(checker.Backoff(UpdateBackoffScope.Asset,now).UntilUtc==DateTime.MinValue,"successful asset completion clears only its wait");
        path=Path.Combine(root,"backoff-cached.json");legacy=new UpdateState {CatalogNotBeforeUtc=now.AddMinutes(10),CatalogBackoffSource="server",AvailableRelease=Release("1.3.6")};File.WriteAllText(path,UpdateJson.Text(legacy));http=new Http();checker=new UpdateChecker(http,path,"1.3.4",now.AddDays(-1));Check(checker.CachedRelease!=null&&checker.Check(false,true,now)==null&&http.Requests.Count==0&&checker.DownloadsAllowed(now),"catalog wait preserves a known signed cached release and permits its asset channel");
    }
    static void UpdateUiStatusPolicy(string root){
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;var now=new DateTime(2026,1,2,0,0,0,DateTimeKind.Utc);var http=new Http();http.Reply("limited",403,null,-1,"120");var checker=new UpdateChecker(http,Path.Combine(root,"ui-server.json"),"1.3.4",now.AddDays(-1));checker.Check(false,true,now);var controller=(UpdateController)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(UpdateController));typeof(UpdateController).GetField("checker",flags).SetValue(controller,checker);string text=(string)typeof(UpdateController).GetMethod("CheckStatus",flags).Invoke(controller,new object[]{null,now});Check(text.StartsWith("刚收到服务端限流",StringComparison.Ordinal),"UI names an explicit server limit");
        http=new Http();http.Reply("denied",403);checker=new UpdateChecker(http,Path.Combine(root,"ui-unknown.json"),"1.3.4",now.AddDays(-1));checker.Check(false,true,now);typeof(UpdateController).GetField("checker",flags).SetValue(controller,checker);text=(string)typeof(UpdateController).GetMethod("CheckStatus",flags).Invoke(controller,new object[]{null,now});Check(text.StartsWith("访问被拒绝，无法确认限流",StringComparison.Ordinal),"UI keeps an unclassified 403 distinct from a server limit");
        typeof(UpdateController).GetField("available",flags).SetValue(controller,Release("1.3.6"));typeof(UpdateController).GetField("availableCached",flags).SetValue(controller,true);Check(controller.Status=="已缓存版本 1.3.6","UI labels a cached release instead of claiming a fresh check");
    }
    static void Signed(Http http,RSACryptoServiceProvider key,byte[] zip,string version,string sumName=null,bool corruptSums=false,bool corruptZip=false,bool absentSignature=false){
        byte[] sums=Encoding.UTF8.GetBytes(Hash(zip)+"  "+(sumName??UpdateTrust.ZipName(version))+"\n");
        byte[] sig=key.SignData(sums,CryptoConfig.MapNameToOID("SHA256"));
        if(corruptSums)sums[0]=(byte)(sums[0]=='a'?'b':'a');if(corruptZip)zip=new byte[]{1,2,3};
        http.Bytes(sums);if(absentSignature)http.Reply("missing",404);else http.Bytes(sig);http.Bytes(zip);
    }
    [STAThread] static int Main(string[] args){
        string root=Path.Combine(Path.GetFullPath(args[0]),"update-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try {
            HelperEnvironment(root);FailureNotification(root);
            NetworkTimeouts();
            UpdateVersion a,b;Check(UpdateVersion.TryParse("1.3.5-beta.1",out a)&&UpdateVersion.TryParse("1.3.5",out b)&&a.CompareTo(b)<0,"beta before stable");
            Check(UpdateVersion.Parse("1.3.5").CompareTo(UpdateVersion.Parse("1.3.6-beta.1"))<0,"stable before next beta");
            Check(UpdateVersion.Parse("1.3.5-beta.9").CompareTo(UpdateVersion.Parse("1.3.5-beta.10"))<0,"numeric beta ordering");
            foreach(string bad in new[]{"",null,"1.3","1.3.5-beta","1.3.5-beta.x","01.3.5","1.3.5-rc.1","1.3.5+foo","1.3.5\n","999999999999.0.0"})Check(!UpdateVersion.TryParse(bad,out a),"invalid version "+bad);
            var stable=Release("1.3.5");var beta=Release("1.3.6-beta.1",true);var draft=Release("9.0.0");draft.Draft=true;
            Check(UpdateCatalog.Select(new[]{stable,beta,draft,new UpdateRelease {Tag="nonsense"}},"1.3.4",null)==stable,"stable channel and draft/invalid filtering");
            Check(UpdateCatalog.Select(new[]{stable,beta,draft},"1.3.5-beta.1",null)==beta,"beta channel selects greatest");
            Check(UpdateCatalog.Select(new[]{stable},"1.3.4","1.3.5")==null,"skip version");
            Check(UpdateCatalog.Select(new[]{beta},"1.3.5-beta.1","1.3.5")==beta,"higher version restores notification");
            Check(UpdateCatalog.Select(new[]{Release("1.3.3")},"1.3.4",null)==null,"no downgrade");
            var disguised=Release("1.3.5-beta.2",false);Check(UpdateCatalog.Select(new[]{disguised},"1.3.4",null)==null,"beta tag cannot enter stable channel");
            Check(UpdateCatalog.Select(new[]{Release("1.3.5",true)},"1.3.4",null)==null,"prerelease flag excludes even a stable tag");
            Check(UpdateCatalog.Select(new[]{stable},"1.3.5-beta.9",null)==stable,"beta upgrades to same stable version");
            Check(UpdateCatalog.Select(new[]{stable,beta},"1.3.4",null,true)==beta,"stable install that opts in to betas sees the greatest beta");
            Check(UpdateCatalog.Select(new[]{Release("1.3.5",true)},"1.3.4",null,true)!=null,"opted-in stable install accepts a prerelease");
            Check(UpdateCatalog.Select(new[]{stable,beta},"1.3.5-beta.1",null,false)==stable,"beta install that opts out follows stable releases only");
            Check(UpdateCatalog.Select(new[]{beta},"1.3.4",null,false)==null,"opted-out stable install never sees betas");
            Check(UpdateCatalog.Select(new[]{stable,beta},"1.3.4","1.3.6-beta.1",true)==stable,"skipping a beta still offers the lower stable release");
            Check(UpdateDialog.Notes("**测试版**：说明\n## 新增支持的硬件\n- 迈从 `3554:FA09`\n  - 子项")=="测试版：说明"+Environment.NewLine+"【新增支持的硬件】"+Environment.NewLine+"• 迈从 3554:FA09"+Environment.NewLine+"  • 子项","release-note Markdown marks render as plain readable text");
            Check(UpdateDialog.Notes("#123 和 C# 保持原样")=="#123 和 C# 保持原样","issue numbers are not mistaken for headings");
            var foreign=Release("1.3.5");foreign.Assets[0].Url="https://github.com/other/repo/releases/download/v1.3.5/"+UpdateTrust.ZipName("1.3.5");
            Reject(()=>UpdateCatalog.ValidateAssets(foreign,"1.3.5"),"foreign repository asset");
            var renamed=Release("1.3.5");renamed.Assets[0].Name="MyFinder-device-battery-v1.3.5-windows-x64.zip";Reject(()=>UpdateCatalog.ValidateAssets(renamed,"1.3.5"),"asset filename is case exact");
            var now=new DateTime(2026,1,2,0,0,0,DateTimeKind.Utc);
            UpdateBackoffPolicy(root);
            UpdateUiStatusPolicy(root);
            var state=new UpdateState();Check(!UpdateSchedule.CanCheck(state,now,now.AddMinutes(-5),true,false),"disabled sends no request");
            Check(!UpdateSchedule.CanCheck(state,now,now.AddSeconds(-119),true,true),"two minute startup delay");
            Check(UpdateSchedule.CanCheck(state,now,now.AddMinutes(-2),true,true),"startup delay expires");
            state.LastCheckUtc=now.AddHours(-5).AddMinutes(-59);Check(!UpdateSchedule.CanCheck(state,now,now.AddDays(-1),true,true),"no automatic check after 5 hours 59 minutes");
            state.LastCheckUtc=now.AddHours(-6);Check(UpdateSchedule.CanCheck(state,now,now.AddDays(-1),true,true),"automatic check allowed at 6 hours");
            state.LastCheckUtc=now;Check(UpdateSchedule.CanCheck(state,now,now,false,true),"manual check bypasses automatic interval and startup delay");
            Check(UpdateSchedule.CanCheck(state,now,now,true,false)==false&&UpdateSchedule.CanCheck(state,now,now,false,false),"manual works when disabled");
            var http=new Http();http.Reply("redirect",302,"http://github.com/x");Reject(()=>UpdateNetwork.Fetch(http,new Uri(UpdateTrust.ApiUrl),100,CancellationToken.None,null),"HTTP redirect refused");
            http=new Http();http.Reply("redirect",302,"https://example.com/x");Reject(()=>UpdateNetwork.Fetch(http,new Uri(UpdateTrust.ApiUrl),100,CancellationToken.None,null),"foreign redirect refused");
            http=new Http();http.Reply("redirect",302,"https://release-assets.githubusercontent.com/x");http.Reply("ok");Check(Encoding.UTF8.GetString(UpdateNetwork.Fetch(http,new Uri(UpdateTrust.ApiUrl),100,CancellationToken.None,null))=="ok","allowlisted GitHub redirect");
            http=new Http();http.Reply("x",200,null,UpdateTrust.MaxDownloadBytes+1);Reject(()=>UpdateNetwork.Fetch(http,new Uri(UpdateTrust.ApiUrl),UpdateTrust.MaxDownloadBytes,CancellationToken.None,null),"oversized content length");
            http=new Http();http.Reply("12345");Reject(()=>UpdateNetwork.Fetch(http,new Uri(UpdateTrust.ApiUrl),4,CancellationToken.None,null),"streamed size cap");
            foreach(string url in new[]{"http://github.com/x","https://github.com.evil.example/x","https://github.com:444/x","https://user@github.com/x","https://api.github.com/repos/other/repo"})Reject(()=>UpdateNetwork.Fetch(new Http(),new Uri(url),100,CancellationToken.None,null),"unsafe request URL "+url);
            http=new Http();for(int i=0;i<7;i++)http.Reply("",302,"https://github.com/loop");Reject(()=>UpdateNetwork.Fetch(http,new Uri(UpdateTrust.ApiUrl),100,CancellationToken.None,null),"redirect loops bounded");
            foreach(int status in new[]{403,429}){
                http=new Http();http.Reply("limited",status,null,-1,"172800");var checker=new UpdateChecker(http,Path.Combine(root,"state-"+status+".json"),"1.3.4",now.AddDays(-1));
                Check(checker.Check(true,true,now)==null,"rate limit quiet "+status);checker.Check(false,true,now.AddDays(1));Check(http.Requests.Count==1,"Retry-After respected by manual checks "+status+" (requests="+http.Requests.Count+", error="+checker.ErrorCode+")");
                checker.Check(true,true,now.AddHours(6));Check(http.Requests.Count==1,"six hour interval does not bypass Retry-After "+status);
                Check(checker.DownloadsAllowed(now.AddDays(1))&&checker.DownloadsAllowed(now.AddDays(2)),"catalog Retry-After does not block signed downloads "+status);
            }
            http=new Http {Timeout=true};var timeout=new UpdateChecker(http,Path.Combine(root,"timeout.json"),"1.3.4",now.AddDays(-1));Check(timeout.Check(true,true,now)==null,"timeout quiet");timeout.Check(true,true,now.AddHours(1));Check(http.Requests.Count==1,"failure throttled");
            http=new Http();var off=new UpdateChecker(http,Path.Combine(root,"off.json"),"1.3.4",now.AddDays(-1));Check(off.Check(true,false,now)==null&&http.Requests.Count==0,"disabled checker has zero HTTP");
            http.Reply(UpdateJson.Text(new[]{Release("1.3.6")}));Check(off.Check(false,false,now).Version=="1.3.6"&&http.Requests.Count==1&&http.Requests[0].AbsoluteUri.StartsWith(UpdateTrust.ApiUrl),"manual discovery requests only API, never assets");
            Check(off.MarkNotified("1.3.6")&&!off.MarkNotified("1.3.6"),"notification at most once");
            var reloaded=new UpdateChecker(http,Path.Combine(root,"off.json"),"1.3.4",now);Check(!reloaded.MarkNotified("1.3.6"),"notification deduplication survives restart");
            off.Skip("1.3.6");http.Reply(UpdateJson.Text(new[]{Release("1.3.6")}));Check(off.Check(false,false,now.AddHours(1))==null,"skip persisted in checker");
            var pref=new AudioBindings();Check(pref.AutoCheckUpdates&&pref.Clone().AutoCheckUpdates,"auto checks default enabled and cloned");
            string settings=Path.Combine(root,"old-settings.json");File.WriteAllText(settings,"{\"SchemaVersion\":1}");Check(DeviceSettings.LoadBindings(settings).AutoCheckUpdates,"old configuration defaults enabled");
            pref.AutoCheckUpdates=false;DeviceSettings.SaveBindings(settings,pref);Check(!DeviceSettings.LoadBindings(settings).AutoCheckUpdates,"disabled configuration persists");
            using(var key=new RSACryptoServiceProvider(2048))using(var other=new RSACryptoServiceProvider(2048)){
                key.PersistKeyInCsp=false;other.PersistKeyInCsp=false;
                byte[] zip=Zip("1.3.6"),sums=Encoding.UTF8.GetBytes(Hash(zip)+"  "+UpdateTrust.ZipName("1.3.6")+"\r\n"),sig=key.SignData(sums,CryptoConfig.MapNameToOID("SHA256"));
                UpdatePackageVerifier.VerifySignature(sums,sig,key.ToXmlString(false));Check(true,"valid signature over raw bytes");
                Reject(()=>UpdatePackageVerifier.VerifySignature(sums,new byte[0],key.ToXmlString(false)),"missing signature");
                Reject(()=>UpdatePackageVerifier.VerifySignature(sums,sig,other.ToXmlString(false)),"wrong signing key");
                Reject(()=>UpdatePackageVerifier.VerifySignature(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(sums).Replace("\r\n","\n")),sig,key.ToXmlString(false)),"raw byte tampering refused");
                string extracted=Path.Combine(root,"extracted");string file=Path.Combine(root,"package.zip");File.WriteAllBytes(file,zip);
                UpdatePackageVerifier.ExtractAndValidate(file,extracted,"1.3.6",CancellationToken.None);Check(File.Exists(Path.Combine(extracted,"release.json")),"safe package validates");
                foreach(string path in new[]{"../outside.txt","/absolute.txt","C:/drive.txt","engine/a:stream","engine/../outside","engine\\..\\outside","engine/NUL.txt","engine/alias.","engine/link"}){
                    File.WriteAllBytes(file,Zip("1.3.6","myfinder-device-battery",path,false,path=="engine/link"?unchecked((int)0xA1FF0000):0));
                    Reject(()=>UpdatePackageVerifier.ExtractAndValidate(file,Path.Combine(root,Guid.NewGuid().ToString("N")),"1.3.6",CancellationToken.None),"unsafe ZIP "+path);
                }
                foreach(byte[] badZip in new[]{Zip("1.3.5"),Zip("1.3.6","another-app"),Zip("1.3.6",badHash:true)}){
                    File.WriteAllBytes(file,badZip);Reject(()=>UpdatePackageVerifier.ExtractAndValidate(file,Path.Combine(root,Guid.NewGuid().ToString("N")),"1.3.6",CancellationToken.None),"package metadata/hash mismatch");
                }
                {var optedOut=new Installer();var channelPipe=new UpdateDownload(new Http(),optedOut,"1.3.4",Path.Combine(root,"channel-install"),Path.Combine(root,"channel")) {AllowBeta=false};
                Reject(()=>channelPipe.InstallForTests(Release("1.3.6-beta.1",true),CancellationToken.None,null,key.ToXmlString(false)),"opted-out stable install refuses a beta package");Check(optedOut.Starts==0,"no helper for a refused beta");}
                var release=Release("1.3.6");string installRoot=Path.Combine(root,"中文 稳定安装");
                for(int failure=0;failure<5;failure++){
                    http=new Http();Signed(http,key,zip,"1.3.6",failure==4?"wrong.zip":null,failure==0,failure==1,failure==3);
                    var installer=new Installer();var pipeline=new UpdateDownload(http,installer,"1.3.4",installRoot,Path.Combine(root,"updates-"+failure));
                    int f=failure;Reject(()=>pipeline.InstallForTests(release,CancellationToken.None,null,f==2?other.ToXmlString(false):key.ToXmlString(false)),"reject bad candidate before helper "+failure);Check(installer.Starts==0,"no helper for rejected candidate "+failure);
                    Check(!Directory.Exists(pipeline.UpdatesDirectory)||Directory.GetDirectories(pipeline.UpdatesDirectory).Length==0,"rejected download cleaned "+failure);
                }
                http=new Http();var portable=new Installer {Stable=false};var portablePipe=new UpdateDownload(http,portable,"1.3.4",installRoot,Path.Combine(root,"portable"));
                Reject(()=>portablePipe.InstallForTests(release,CancellationToken.None,null,key.ToXmlString(false)),"portable does not install");Check(portable.Starts==0&&http.Requests.Count==0,"portable has no download/helper");
                http=new Http();Signed(http,key,zip,"1.3.6");var good=new Installer();var goodPipe=new UpdateDownload(http,good,"1.3.4",installRoot,Path.Combine(root,"good"));
                goodPipe.InstallForTests(release,CancellationToken.None,null,key.ToXmlString(false));Check(good.Starts==1&&good.Root==installRoot&&Directory.Exists(good.Package),"verified stable update passes exact Unicode directory");
                http=new Http();Signed(http,key,zip,"1.3.6");var production=new Installer();var productionPipe=new UpdateDownload(http,production,"1.3.4",installRoot,Path.Combine(root,"production-key"));
                Reject(()=>productionPipe.Install(release,CancellationToken.None,null),"production entry always uses built-in key");Check(production.Starts==0,"test key cannot launch production helper");
                foreach(byte[] badZip in new[]{Zip("1.3.5"),Zip("1.3.6","another-app"),Zip("1.3.6",badHash:true),Zip("1.3.6",extra:"../escape")}){
                    http=new Http();Signed(http,key,badZip,"1.3.6");var rejected=new Installer();var badPipe=new UpdateDownload(http,rejected,"1.3.4",installRoot,Path.Combine(root,Guid.NewGuid().ToString("N")));
                    Reject(()=>badPipe.InstallForTests(release,CancellationToken.None,null,key.ToXmlString(false)),"signed but unsafe package refused");Check(rejected.Starts==0,"no helper for unsafe signed package");
                }
                http=new Http();Signed(http,key,zip,"1.3.6");var lateCancel=new Installer();var latePipe=new UpdateDownload(http,lateCancel,"1.3.4",installRoot,Path.Combine(root,"late-cancel"));
                using(var token=new CancellationTokenSource()){Reject(()=>latePipe.InstallForTests(release,token.Token,null,key.ToXmlString(false),token.Cancel),"cancel at handoff boundary");}Check(lateCancel.Starts==0,"handoff cancellation never starts helper");
                http=new Http();Signed(http,key,zip,"1.3.6");var streamingCancel=new Installer();var streamPipe=new UpdateDownload(http,streamingCancel,"1.3.4",installRoot,Path.Combine(root,"stream-cancel"));
                using(var token=new CancellationTokenSource()){Reject(()=>streamPipe.InstallForTests(release,token.Token,delegate(long count,long total){token.Cancel();},key.ToXmlString(false)),"cancel during ZIP stream");}Check(streamingCancel.Starts==0,"stream cancellation never starts helper");
                http=new Http();Signed(http,key,zip,"1.3.6");var cancelled=new Installer();var cancelPipe=new UpdateDownload(http,cancelled,"1.3.4",installRoot,Path.Combine(root,"cancelled"));using(var token=new CancellationTokenSource()){token.Cancel();Reject(()=>cancelPipe.InstallForTests(release,token.Token,null,key.ToXmlString(false)),"download cancellation");}Check(cancelled.Starts==0,"cancel never starts helper");
            }
            var start=UpdateProcess.HelperStartInfo(Path.Combine(root,"中文 helper","Apply-Update.ps1"),new[]{"-InstallDirectory",Path.Combine(root,"中文 稳定目录"),"-ParentId","123"});
            Check(Path.IsPathRooted(start.FileName)&&start.FileName.EndsWith("\\WindowsPowerShell\\v1.0\\powershell.exe",StringComparison.OrdinalIgnoreCase)&&!start.UseShellExecute&&start.CreateNoWindow&&start.WindowStyle==ProcessWindowStyle.Hidden,"absolute hidden system PowerShell");
            Check(start.Arguments.Contains("-NoProfile -ExecutionPolicy Bypass -File ")&&start.Arguments.Contains("\""+Path.Combine(root,"中文 稳定目录")+"\""),"Unicode and spaces are quoted");
            string portableRoot=Path.Combine(root,"portable 实际绿色目录"),portableData=Path.Combine(root,"portable fake data");
            Directory.CreateDirectory(Path.Combine(portableRoot,"outputs"));Directory.CreateDirectory(Path.Combine(portableRoot,"scripts"));Directory.CreateDirectory(portableData);
            string portableExe=Path.Combine(portableRoot,"outputs","AtkMouseBattery.exe");File.WriteAllText(portableExe,"fixture");
            string marker="{\"AppId\":\"myfinder-device-battery\",\"Schema\":1,\"iNsTaLlDiReCtOrY\":\""+portableRoot.Replace("\\","\\\\")+"\"}";
            File.WriteAllText(Path.Combine(portableRoot,".myfinder-device-battery-install.json"),marker);
            File.WriteAllText(Path.Combine(portableRoot,"scripts","Apply-Update.ps1"),"param([switch]$Probe,[string]$ExecutablePath,[string]$DataDirectory)\r\n[IO.File]::WriteAllText([IO.Path]::Combine($DataDirectory,'probe-started'),'started')\r\n[Console]::Write('portable')");
            File.WriteAllText(Path.Combine(portableData,"install.json"),"{\"InstallDirectory\":\""+Path.Combine(root,"other stable installation").Replace("\\","\\\\")+"\"}");
            Check(!new UpdateProcess(portableExe,portableData).IsStable(CancellationToken.None)&&!File.Exists(Path.Combine(portableData,"probe-started")),"portable receipt mismatch starts no PowerShell helper, including probe");
            // A positive decision always runs the real installer rules, including its case-insensitive
            // JSON property handling. Only fixture folders and a non-executable placeholder are used.
            string sourceRoot=args.Length>1?args[1]:Environment.CurrentDirectory;
            foreach(string name in new[]{"Apply-Update.ps1","Update.Common.ps1","Install.Common.ps1","Manifest.Common.ps1"})File.Copy(Path.Combine(sourceRoot,"scripts",name),Path.Combine(portableRoot,"scripts",name),true);
            string receiptV2=marker.Replace("\"Schema\":1","\"Schema\":2");receiptV2=receiptV2.Substring(0,receiptV2.Length-1)+",\"Files\":[{\"path\":\"outputs/AtkMouseBattery.exe\",\"sha256\":\""+new string('0',64)+"\"}]}";
            File.WriteAllText(Path.Combine(portableData,"install.json"),receiptV2,new UTF8Encoding(true));
            Check(new UpdateProcess(portableExe,portableData).IsStable(CancellationToken.None),"stable decision reuses real installer with BOM and case-insensitive receipt");
            PlainReleaseNotes();
            using(var dialog=new UpdateDialog(Release("1.3.6"),true)){
                var notes=dialog.Controls.Find("ReleaseNotes",true).Single() as TextBoxBase;Check(notes!=null&&notes.ReadOnly&&notes.Multiline&&notes.Text.Contains("<script>bad()</script>"),"malicious release notes shown as literal text by a native edit control");
                dialog.Opacity=0;dialog.ShowInTaskbar=false;dialog.Show();Application.DoEvents();
                using(var image=new System.Drawing.Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(image,new System.Drawing.Rectangle(0,0,image.Width,image.Height));image.Save(Path.Combine(Path.GetFullPath(args[0]),"update-dialog.png"));}
                dialog.Close();
            }
            Check(UpdateText.Notes(new string('x',20000)).Length<=UpdateText.MaxNotesLength,"bounded release notes");
            Console.WriteLine("PASS "+passed+" offline updater checks; no network, hardware or user configuration");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
