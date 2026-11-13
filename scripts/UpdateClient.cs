using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;

namespace AtkBattery {
    internal interface IUpdateTransport {UpdateResponse Get(Uri uri,CancellationToken token);}
    internal enum UpdateBackoffScope {Catalog,Asset}
    internal sealed class UpdateBackoffInfo {
        internal readonly DateTime UntilUtc;internal readonly string Source;internal readonly int FailureCount;
        internal UpdateBackoffInfo(DateTime until,string source,int failures){UntilUtc=until;Source=source;FailureCount=failures;}
        internal bool Waiting(DateTime now){return UntilUtc>now;}
    }
    internal sealed class UpdateResponse:IDisposable {
        internal readonly int Status;internal readonly Stream Body;internal readonly long Length;internal readonly string Location,RetryAfter,RateLimitRemaining,RateLimitReset;
        readonly Action close;
        internal UpdateResponse(int status,Stream body,long length,string location,string retryAfter,Action dispose=null):this(status,body,length,location,retryAfter,null,null,dispose){}
        internal UpdateResponse(int status,Stream body,long length,string location,string retryAfter,string remaining,string reset,Action dispose=null){Status=status;Body=body;Length=length;Location=location;RetryAfter=retryAfter;RateLimitRemaining=remaining;RateLimitReset=reset;close=dispose;}
        public void Dispose(){try{if(Body!=null)Body.Dispose();}finally{if(close!=null)close();}}
    }
    internal sealed class UpdateHttp:IUpdateTransport {
        readonly string userAgent;readonly int timeout;
        internal UpdateHttp(string version,int timeoutMilliseconds=30000){userAgent="MyFinderDeviceBattery/"+UpdateVersion.Parse(version);timeout=timeoutMilliseconds;}
        public UpdateResponse Get(Uri uri,CancellationToken token){
            // Framework 4.7.2 is the application's existing minimum. Restrict this client to TLS 1.2.
            ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
            ServicePointManager.CheckCertificateRevocationList=true;
            var request=(HttpWebRequest)WebRequest.Create(uri);request.Method="GET";request.UserAgent=userAgent;
            request.AllowAutoRedirect=false;request.Timeout=timeout;request.ReadWriteTimeout=timeout;request.UseDefaultCredentials=false;request.Credentials=null;
            request.AutomaticDecompression=DecompressionMethods.None;
            var registration=token.Register(request.Abort);
            try {
                token.ThrowIfCancellationRequested();HttpWebResponse response;
                try{response=(HttpWebResponse)request.GetResponse();}catch(WebException error){response=error.Response as HttpWebResponse;if(response==null)throw;}
                return new UpdateResponse((int)response.StatusCode,response.GetResponseStream(),response.ContentLength,response.Headers["Location"],response.Headers["Retry-After"],response.Headers["X-RateLimit-Remaining"],response.Headers["X-RateLimit-Reset"],delegate{response.Dispose();registration.Dispose();});
            }catch{registration.Dispose();throw;}
        }
    }
    internal sealed class UpdateHttpFailure:IOException {
        internal readonly int Status;internal readonly string RetryAfter,RateLimitRemaining,RateLimitReset;
        internal UpdateHttpFailure(int status,string retry):this(status,retry,null,null){}
        internal UpdateHttpFailure(int status,string retry,string remaining,string reset):base("Update HTTP request failed."){Status=status;RetryAfter=retry;RateLimitRemaining=remaining;RateLimitReset=reset;}
    }
    internal static class UpdateNetwork {
        static readonly HashSet<string> DownloadHosts=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"github.com","objects.githubusercontent.com","release-assets.githubusercontent.com"};
        internal static void ValidateUri(Uri uri,bool redirect){
            if(uri==null||!uri.IsAbsoluteUri||uri.Scheme!=Uri.UriSchemeHttps||uri.Port!=443||uri.UserInfo.Length!=0||uri.Fragment.Length!=0||!(DownloadHosts.Contains(uri.IdnHost)||!redirect&&uri.AbsoluteUri.StartsWith(UpdateTrust.ApiUrl,StringComparison.Ordinal)&&uri.Host=="api.github.com"))throw new InvalidDataException("Update URL or redirect is not allowed.");
        }
        internal static byte[] Fetch(IUpdateTransport transport,Uri uri,long limit,CancellationToken token,Action<long,long> progress,Func<TimeSpan> clock=null){using(var output=new MemoryStream()){Transfer(transport,uri,output,limit,token,progress,false,clock);return output.ToArray();}}
        internal static void Download(IUpdateTransport transport,Uri uri,Stream output,long limit,CancellationToken token,Action<long,long> progress,Func<TimeSpan> clock=null){
            Transfer(transport,uri,output,limit,token,progress,true,clock);
        }
        static void Transfer(IUpdateTransport transport,Uri uri,Stream output,long limit,CancellationToken token,Action<long,long> progress,bool download,Func<TimeSpan> clock){
            try{DownloadCore(transport,uri,output,limit,token,progress,download,clock);}catch(Exception){token.ThrowIfCancellationRequested();throw;}
        }
        static void CheckTimeout(TimeSpan elapsed,TimeSpan idle,bool download){
            if(download?elapsed.TotalMinutes>=30:elapsed.TotalSeconds>120)throw new TimeoutException("Update download timed out.");
            if(download&&idle.TotalSeconds>=30)throw new TimeoutException("Update download stalled.");
        }
        static void DownloadCore(IUpdateTransport transport,Uri uri,Stream output,long limit,CancellationToken token,Action<long,long> progress,bool download,Func<TimeSpan> clock){
            var stopwatch=Stopwatch.StartNew();if(clock==null)clock=delegate{return stopwatch.Elapsed;};
            var started=clock();var lastData=started;
            Action checkTimeout=delegate{var now=clock();CheckTimeout(now-started,now-lastData,download);};
            for(int redirects=0;;redirects++){
                token.ThrowIfCancellationRequested();ValidateUri(uri,redirects!=0);checkTimeout();
                using(var response=transport.Get(uri,token)){
                    token.ThrowIfCancellationRequested();checkTimeout();
                    if(response.Status==301||response.Status==302||response.Status==303||response.Status==307||response.Status==308){
                        Uri next;if(redirects>=5||String.IsNullOrEmpty(response.Location)||!Uri.TryCreate(uri,response.Location,out next))throw new InvalidDataException("Invalid update redirect.");ValidateUri(next,true);uri=next;continue;
                    }
                    if(response.Status!=200)throw new UpdateHttpFailure(response.Status,response.RetryAfter,response.RateLimitRemaining,response.RateLimitReset);
                    if(response.Length>limit)throw new InvalidDataException("Update download exceeds its limit.");
                    // UpdateHttp keeps its 30 second ReadWriteTimeout to interrupt a blocked read.
                    // This clock also bounds the whole transfer and is injectable for offline tests.
                    byte[] buffer=new byte[65536];long count=0;
                    while(true){
                        token.ThrowIfCancellationRequested();checkTimeout();
                        int read=response.Body.Read(buffer,0,buffer.Length);
                        token.ThrowIfCancellationRequested();checkTimeout();if(read==0)break;lastData=clock();
                        count+=read;if(count>limit)throw new InvalidDataException("Update download exceeds its limit.");output.Write(buffer,0,read);if(progress!=null)progress(count,response.Length);
                    }
                    token.ThrowIfCancellationRequested();if(response.Length>=0&&response.Length!=count)throw new InvalidDataException("Truncated update download.");return;
                }
            }
        }
    }
    [DataContract] internal sealed class UpdateState {
        [DataMember] public DateTime LastCheckUtc=DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc);
        [DataMember] public DateTime NotBeforeUtc=DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc);
        [DataMember] public string SkippedVersion;
        [DataMember] public string NotifiedVersion;
        [DataMember(EmitDefaultValue=false)] public string SeenVersion;
        [DataMember(EmitDefaultValue=false)] public UpdateRelease AvailableRelease;
        [DataMember(EmitDefaultValue=false)] public DateTime CatalogNotBeforeUtc=DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc);
        [DataMember(EmitDefaultValue=false)] public DateTime AssetNotBeforeUtc=DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc);
        [DataMember(EmitDefaultValue=false)] public int CatalogFailureCount;
        [DataMember(EmitDefaultValue=false)] public int AssetFailureCount;
        [DataMember(EmitDefaultValue=false)] public string CatalogBackoffSource;
        [DataMember(EmitDefaultValue=false)] public string AssetBackoffSource;
        [OnDeserializing] void Defaults(StreamingContext context){LastCheckUtc=NotBeforeUtc=CatalogNotBeforeUtc=AssetNotBeforeUtc=DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc);CatalogFailureCount=AssetFailureCount=0;CatalogBackoffSource=AssetBackoffSource=null;}
    }
    internal static class UpdateSchedule {
        const int AutomaticCheckIntervalHours=6;
        internal static bool CanCheck(UpdateState state,DateTime now,DateTime started,bool automatic,bool enabled){
            if(now<UpdateChecker.EffectiveNotBefore(state,UpdateBackoffScope.Catalog))return false;if(!automatic)return true;
            return enabled&&now>=started.AddMinutes(2)&&(state.LastCheckUtc==DateTime.MinValue||now>=state.LastCheckUtc.AddHours(AutomaticCheckIntervalHours));
        }
    }
    internal sealed class UpdateChecker {
        readonly IUpdateTransport transport;readonly string path,current;readonly DateTime started;
        readonly object gate=new object();readonly UpdateState state;
        static readonly DateTime UtcEpoch=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc);
        internal string ErrorCode {get;private set;}
        internal Func<bool> IncludeBeta;
        bool? Betas {get{var include=IncludeBeta;return include==null?(bool?)null:include();}}
        internal UpdateChecker(IUpdateTransport http,string statePath,string version,DateTime startedAt){
            transport=http;path=statePath;current=version;started=startedAt;
            if(File.Exists(path)){
                // Corrupt state waits for the automatic check interval instead of hammering the API at each launch.
                try{state=UpdateJson.ReadFile<UpdateState>(path,65536);if(state==null)throw new InvalidDataException();}catch(Exception){state=new UpdateState {LastCheckUtc=DateTime.UtcNow};ErrorCode="update_state_invalid";}
            }else state=new UpdateState();
        }
        void Save(){UpdatePaths.NoLinks(path);AtomicText.Write(path,UpdateJson.Text(state));}
        internal static DateTime EffectiveNotBefore(UpdateState value,UpdateBackoffScope scope){
            if(value==null)return DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc);DateTime legacy=Utc(value.NotBeforeUtc);DateTime scoped=Utc(scope==UpdateBackoffScope.Catalog?value.CatalogNotBeforeUtc:value.AssetNotBeforeUtc);return legacy>scoped?legacy:scoped;
        }
        static DateTime Utc(DateTime value){return value==DateTime.MinValue?DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc):value.Kind==DateTimeKind.Utc?value:value.Kind==DateTimeKind.Unspecified?DateTime.SpecifyKind(value,DateTimeKind.Utc):value.ToUniversalTime();}
        internal UpdateBackoffInfo Backoff(UpdateBackoffScope scope,DateTime now){lock(gate){
            DateTime scoped=Utc(scope==UpdateBackoffScope.Catalog?state.CatalogNotBeforeUtc:state.AssetNotBeforeUtc);DateTime until=EffectiveNotBefore(state,scope);string source;
            if(until>now&&Utc(state.NotBeforeUtc)>=scoped&&Utc(state.NotBeforeUtc)>now)source="legacy";
            else source=scope==UpdateBackoffScope.Catalog?state.CatalogBackoffSource:state.AssetBackoffSource;
            return new UpdateBackoffInfo(until,source,scope==UpdateBackoffScope.Catalog?state.CatalogFailureCount:state.AssetFailureCount);
        }}
        internal UpdateRelease CachedRelease {get{lock(gate){
            try{
                var release=UpdateCatalog.Select(new[]{state.AvailableRelease},current,state.SkippedVersion,Betas);
                if(release!=null)UpdateCatalog.ValidateAssets(release,release.Version);return release;
            }catch(Exception){return null;}
        }}}
        internal bool BadgeVisible(bool enabled,bool automatic){lock(gate){
            return enabled&&automatic&&CachedRelease!=null;
        }}
        internal void MarkSeen(string version,bool userInitiated){
            if(!userInitiated)return;var next=UpdateVersion.Parse(version);
            lock(gate){UpdateVersion seen;if(UpdateVersion.TryParse(state.SeenVersion,out seen)&&next.CompareTo(seen)<=0)return;string previous=state.SeenVersion;state.SeenVersion=version;try{Save();}catch{state.SeenVersion=previous;throw;}}
        }
        internal UpdateRelease Check(bool automatic,bool enabled,DateTime now,CancellationToken token=default(CancellationToken)){
            lock(gate){if(!UpdateSchedule.CanCheck(state,now,started,automatic,enabled)){ErrorCode=now<EffectiveNotBefore(state,UpdateBackoffScope.Catalog)?"update_waiting_catalog":null;return null;}ErrorCode=null;state.LastCheckUtc=now;try{Save();}catch(Exception){ErrorCode="update_state_write_failed";return null;}}
            try {
                var bytes=UpdateNetwork.Fetch(transport,new Uri(UpdateTrust.ApiUrl+"?per_page=100"),2097152,token,null);
                var releases=UpdateJson.Read<UpdateRelease[]>(bytes);UpdateRelease candidate;
                bool? betas=Betas;lock(gate)candidate=UpdateCatalog.Select(releases,current,state.SkippedVersion,betas);
                if(candidate!=null){
                    var assets=UpdateCatalog.ValidateAssets(candidate,candidate.Version);
                    // Bound the cached metadata just like the dialog notes; retain only required assets.
                    candidate=new UpdateRelease {Tag=candidate.Tag,Prerelease=candidate.Prerelease,Body=UpdateText.Notes(candidate.Body),Assets=new List<UpdateAsset>(assets.Values).ToArray()};
                }
                lock(gate){state.AvailableRelease=candidate;Clear(UpdateBackoffScope.Catalog);Save();}ErrorCode=null;return candidate;
            }catch(UpdateHttpFailure error){
                ErrorCode="update_http_"+error.Status;
                if(error.Status==403||error.Status==429)lock(gate){ApplyBackoff(UpdateBackoffScope.Catalog,error,now);try{Save();}catch(Exception){}}
            }catch(OperationCanceledException){ErrorCode="update_cancelled";}catch(WebException error){ErrorCode=error.Status==WebExceptionStatus.Timeout?"update_timeout":"update_network_failed";}catch(TimeoutException){ErrorCode="update_timeout";}catch(Exception){ErrorCode="update_check_failed";}
            return null;
        }
        internal void Defer(UpdateHttpFailure error,DateTime now){Defer(error,now,UpdateBackoffScope.Asset);}
        internal void Defer(UpdateHttpFailure error,DateTime now,UpdateBackoffScope scope){if(error==null||error.Status!=403&&error.Status!=429)return;lock(gate){ApplyBackoff(scope,error,now);Save();}}
        internal void Complete(UpdateBackoffScope scope){lock(gate){Clear(scope);try{Save();}catch(Exception){}}}
        internal bool DownloadsAllowed(DateTime now){return !Backoff(UpdateBackoffScope.Asset,now).Waiting(now);}
        static DateTime Scoped(UpdateState value,UpdateBackoffScope scope){return Utc(scope==UpdateBackoffScope.Catalog?value.CatalogNotBeforeUtc:value.AssetNotBeforeUtc);}
        static int Failures(UpdateState value,UpdateBackoffScope scope){return scope==UpdateBackoffScope.Catalog?value.CatalogFailureCount:value.AssetFailureCount;}
        static void SetFailure(UpdateState value,UpdateBackoffScope scope,int count){if(scope==UpdateBackoffScope.Catalog)value.CatalogFailureCount=count;else value.AssetFailureCount=count;}
        static void SetDeadline(UpdateState value,UpdateBackoffScope scope,DateTime until,string source){until=Utc(until);if(scope==UpdateBackoffScope.Catalog){value.CatalogNotBeforeUtc=until;value.CatalogBackoffSource=source;}else{value.AssetNotBeforeUtc=until;value.AssetBackoffSource=source;}}
        void Clear(UpdateBackoffScope scope){if(scope==UpdateBackoffScope.Catalog){state.CatalogNotBeforeUtc=DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc);state.CatalogFailureCount=0;state.CatalogBackoffSource=null;}else{state.AssetNotBeforeUtc=DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc);state.AssetFailureCount=0;state.AssetBackoffSource=null;}}
        void ApplyBackoff(UpdateBackoffScope scope,UpdateHttpFailure error,DateTime now){
            int previous=Failures(state,scope);int count=previous==Int32.MaxValue?Int32.MaxValue:previous+1;SetFailure(state,scope,count);DateTime deadline;string source;
            if(TryServerDeadline(now,error.RetryAfter,error.RateLimitRemaining,error.RateLimitReset,out deadline))source="server";else{deadline=FallbackDeadline(now,count);source="fallback";}
            DateTime old=Scoped(state,scope);if(old>deadline){deadline=old;string oldSource=scope==UpdateBackoffScope.Catalog?state.CatalogBackoffSource:state.AssetBackoffSource;if(!String.IsNullOrEmpty(oldSource))source=oldSource;}SetDeadline(state,scope,deadline,source);
        }
        static DateTime FallbackDeadline(DateTime now,int failures){double minutes=failures<=1?5:failures==2?10:failures==3?20:failures==4?40:60;try{return now.AddMinutes(minutes);}catch(ArgumentOutOfRangeException){return DateTime.MaxValue;}}
        static DateTime Max(DateTime first,DateTime second){return first>second?first:second;}
        static bool TryServerDeadline(DateTime now,string retry,string remaining,string reset,out DateTime deadline){
            deadline=DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc);bool found=false;DateTime candidate;long seconds;
            if(!String.IsNullOrWhiteSpace(retry)&&Int64.TryParse(retry.Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out seconds)&&seconds>=0&&TryAdd(now,seconds,out candidate)){if(candidate>=now){deadline=candidate;found=true;}}
            DateTime date;string httpDate=String.IsNullOrWhiteSpace(retry)?null:retry.Trim();
            if(httpDate!=null&&DateTime.TryParseExact(httpDate,"R",CultureInfo.InvariantCulture,DateTimeStyles.AdjustToUniversal|DateTimeStyles.AssumeUniversal,out date)&&Utc(date)>now){deadline=found?Max(deadline,Utc(date)):Utc(date);found=true;}
            long left,epoch;if(Int64.TryParse((remaining??"").Trim(),NumberStyles.Integer,CultureInfo.InvariantCulture,out left)&&left==0&&Int64.TryParse((reset??"").Trim(),NumberStyles.Integer,CultureInfo.InvariantCulture,out epoch)&&TryEpoch(epoch,out date)&&date>now){deadline=found?Max(deadline,date):date;found=true;}
            return found;
        }
        static bool TryAdd(DateTime now,long seconds,out DateTime result){try{result=now.AddSeconds(seconds);return true;}catch(ArgumentOutOfRangeException){result=DateTime.MinValue;return false;}}
        static bool TryEpoch(long seconds,out DateTime result){try{if(seconds<0){result=DateTime.MinValue;return false;}result=UtcEpoch.AddSeconds(seconds);return true;}catch(ArgumentOutOfRangeException){result=DateTime.MinValue;return false;}}
        internal bool MarkNotified(string version){lock(gate){if(state.SkippedVersion==version)return false;UpdateVersion previous;if(UpdateVersion.TryParse(state.NotifiedVersion,out previous)&&UpdateVersion.Parse(version).CompareTo(previous)<=0)return false;state.NotifiedVersion=version;Save();return true;}}
        // The cache holds one release chosen under the previous beta choice; let the next
        // automatic check run now (still honouring any server back-off).
        internal void ForgetLastCheck(){lock(gate){state.LastCheckUtc=DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc);try{Save();}catch(Exception){}}}
        internal void Skip(string version){UpdateVersion.Parse(version);lock(gate){state.SkippedVersion=version;Save();}}
    }
    internal interface IUpdateInstaller {
        bool IsStable(CancellationToken token);
        void Start(PreparedUpdate update);
    }
    internal sealed class PreparedUpdate {
        internal string WorkDirectory,PackageDirectory,InstallDirectory,Version;
    }
    internal sealed class UpdateDownload {
        readonly IUpdateTransport transport;readonly IUpdateInstaller installer;readonly string current,installDirectory;
        internal readonly string UpdatesDirectory;
        internal bool? AllowBeta;
        internal UpdateDownload(IUpdateTransport http,IUpdateInstaller runner,string version,string target,string updatesDirectory){transport=http;installer=runner;current=version;installDirectory=target;UpdatesDirectory=Path.GetFullPath(updatesDirectory);}
        internal void Install(UpdateRelease release,CancellationToken token,Action<long,long> progress,Action commit=null){InstallSigned(release,token,progress,UpdateTrust.PublicKeyXml,commit);}
#if UPDATE_TESTS
        // This seam does not exist in the shipped binary. Tests never need the release private key.
        internal void InstallForTests(UpdateRelease release,CancellationToken token,Action<long,long> progress,string key,Action commit=null){InstallSigned(release,token,progress,key,commit);}
#endif
        void CheckSpace(){
            var required=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
            Action<string,long> reserve=delegate(string directory,long bytes){UpdatePaths.NoLinks(directory);string drive=Path.GetPathRoot(Path.GetFullPath(directory));long old;required.TryGetValue(drive,out old);required[drive]=old+bytes;};
            // ZIP + two independent extractions; installer candidate + rollback copies in TEMP;
            // and destination growth. Sum reservations when these directories share a volume.
            reserve(UpdatesDirectory,UpdateTrust.MaxDownloadBytes+UpdateTrust.MaxExpandedBytes*2);
            reserve(Path.GetTempPath(),UpdateTrust.MaxExpandedBytes*2);reserve(installDirectory,UpdateTrust.MaxExpandedBytes);
            foreach(var entry in required)if(new DriveInfo(entry.Key).AvailableFreeSpace<entry.Value)throw new IOException("Insufficient free space for a transactional update.");
        }
        void InstallSigned(UpdateRelease release,CancellationToken token,Action<long,long> progress,string key,Action commit){
            token.ThrowIfCancellationRequested();if(!installer.IsStable(token))throw new InvalidOperationException("Portable copies cannot be updated in place.");
            string version=release.Version;var targetVersion=UpdateVersion.Parse(version);var installedVersion=UpdateVersion.Parse(current);
            if(targetVersion.CompareTo(installedVersion)<=0||!(AllowBeta??installedVersion.IsBeta)&&(release.Prerelease||targetVersion.IsBeta))throw new InvalidDataException("Update downgrade or channel mismatch refused.");
            var assets=UpdateCatalog.ValidateAssets(release,version);
            CheckSpace();
            string versionDirectory=UpdatePaths.Resolve(UpdatesDirectory,version);UpdatePaths.NoLinks(versionDirectory);
            Directory.CreateDirectory(versionDirectory);string work=Path.Combine(versionDirectory,"run-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);bool handedOff=false;
            try {
                byte[] sums=UpdateNetwork.Fetch(transport,new Uri(assets["SHA256SUMS.txt"].Url),262144,token,null);
                byte[] sig=UpdateNetwork.Fetch(transport,new Uri(assets["SHA256SUMS.txt.sig"].Url),16384,token,null);
                UpdatePackageVerifier.VerifySignature(sums,sig,key);string name=UpdateTrust.ZipName(version);UpdatePackageVerifier.ExpectedHash(sums,name);
                string zip=Path.Combine(work,name);using(var output=new FileStream(zip,FileMode.CreateNew,FileAccess.Write,FileShare.None))UpdateNetwork.Download(transport,new Uri(assets[name].Url),output,UpdateTrust.MaxDownloadBytes,token,progress);
                string package=Path.Combine(work,"package");UpdatePackageVerifier.VerifyAndExtract(zip,sums,sig,key,package,version,token);
                token.ThrowIfCancellationRequested();if(!installer.IsStable(token))throw new InvalidOperationException("The installation changed during download.");
                // Retain signed input for an independent recheck by the helper after this process exits.
                File.WriteAllBytes(Path.Combine(work,"SHA256SUMS.txt"),sums);File.WriteAllBytes(Path.Combine(work,"SHA256SUMS.txt.sig"),sig);
                if(commit!=null)commit();token.ThrowIfCancellationRequested();
                installer.Start(new PreparedUpdate {WorkDirectory=work,PackageDirectory=package,InstallDirectory=installDirectory,Version=version});handedOff=true;
            }finally{if(!handedOff){UpdatePaths.DeleteTree(work);if(Directory.Exists(versionDirectory)&&Directory.GetFileSystemEntries(versionDirectory).Length==0)Directory.Delete(versionDirectory);}}
        }
        internal static void CleanStale(string updatesDirectory,DateTime now){
            UpdatePaths.NoLinks(updatesDirectory);if(!Directory.Exists(updatesDirectory))return;
            foreach(string version in Directory.GetDirectories(updatesDirectory)){
                UpdateVersion parsed;if(!UpdateVersion.TryParse(Path.GetFileName(version),out parsed))continue;UpdatePaths.NoLinks(version);
                foreach(string run in Directory.GetDirectories(version))if(System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(run),@"\Arun-[0-9a-f]{32}\z")&&Directory.GetLastWriteTimeUtc(run)<now.AddDays(-1))UpdatePaths.DeleteTree(run);
                if(Directory.GetFileSystemEntries(version).Length==0)Directory.Delete(version);
            }
        }
    }
}
