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
    internal sealed class UpdateResponse:IDisposable {
        internal readonly int Status;internal readonly Stream Body;internal readonly long Length;internal readonly string Location,RetryAfter;
        readonly Action close;
        internal UpdateResponse(int status,Stream body,long length,string location,string retryAfter,Action dispose=null){Status=status;Body=body;Length=length;Location=location;RetryAfter=retryAfter;close=dispose;}
        public void Dispose(){try{if(Body!=null)Body.Dispose();}finally{if(close!=null)close();}}
    }
    internal sealed class UpdateHttp:IUpdateTransport {
        readonly string userAgent;
        internal UpdateHttp(string version){userAgent="MyFinderDeviceBattery/"+UpdateVersion.Parse(version);}
        public UpdateResponse Get(Uri uri,CancellationToken token){
            // Framework 4.7.2 is the application's existing minimum. Restrict this client to TLS 1.2.
            ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
            ServicePointManager.CheckCertificateRevocationList=true;
            var request=(HttpWebRequest)WebRequest.Create(uri);request.Method="GET";request.UserAgent=userAgent;
            request.AllowAutoRedirect=false;request.Timeout=30000;request.ReadWriteTimeout=30000;request.UseDefaultCredentials=false;request.Credentials=null;
            request.AutomaticDecompression=DecompressionMethods.None;
            var registration=token.Register(request.Abort);
            try {
                token.ThrowIfCancellationRequested();HttpWebResponse response;
                try{response=(HttpWebResponse)request.GetResponse();}catch(WebException error){response=error.Response as HttpWebResponse;if(response==null)throw;}
                return new UpdateResponse((int)response.StatusCode,response.GetResponseStream(),response.ContentLength,response.Headers["Location"],response.Headers["Retry-After"],delegate{response.Dispose();registration.Dispose();});
            }catch{registration.Dispose();throw;}
        }
    }
    internal sealed class UpdateHttpFailure:IOException {
        internal readonly int Status;internal readonly string RetryAfter;
        internal UpdateHttpFailure(int status,string retry):base("Update HTTP request failed."){Status=status;RetryAfter=retry;}
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
                    if(response.Status!=200)throw new UpdateHttpFailure(response.Status,response.RetryAfter);
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
        [OnDeserializing] void Defaults(StreamingContext context){LastCheckUtc=NotBeforeUtc=DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc);}
    }
    internal static class UpdateSchedule {
        internal static bool CanCheck(UpdateState state,DateTime now,DateTime started,bool automatic,bool enabled){
            if(now<state.NotBeforeUtc)return false;if(!automatic)return true;
            return enabled&&now>=started.AddMinutes(2)&&(state.LastCheckUtc==DateTime.MinValue||now>=state.LastCheckUtc.AddHours(24));
        }
    }
    internal sealed class UpdateChecker {
        readonly IUpdateTransport transport;readonly string path,current;readonly DateTime started;
        readonly object gate=new object();readonly UpdateState state;
        internal string ErrorCode {get;private set;}
        internal UpdateChecker(IUpdateTransport http,string statePath,string version,DateTime startedAt){
            transport=http;path=statePath;current=version;started=startedAt;
            if(File.Exists(path)){
                // Corrupt state fails closed for a day instead of hammering the API at each launch.
                try{state=UpdateJson.ReadFile<UpdateState>(path,65536);if(state==null)throw new InvalidDataException();}catch(Exception){state=new UpdateState {LastCheckUtc=DateTime.UtcNow};ErrorCode="update_state_invalid";}
            }else state=new UpdateState();
        }
        void Save(){UpdatePaths.NoLinks(path);AtomicText.Write(path,UpdateJson.Text(state));}
        internal UpdateRelease Check(bool automatic,bool enabled,DateTime now,CancellationToken token=default(CancellationToken)){
            lock(gate){if(!UpdateSchedule.CanCheck(state,now,started,automatic,enabled))return null;state.LastCheckUtc=now;try{Save();}catch(Exception){ErrorCode="update_state_write_failed";return null;}}
            try {
                var bytes=UpdateNetwork.Fetch(transport,new Uri(UpdateTrust.ApiUrl+"?per_page=100"),2097152,token,null);
                var releases=UpdateJson.Read<UpdateRelease[]>(bytes);UpdateRelease candidate;
                lock(gate)candidate=UpdateCatalog.Select(releases,current,state.SkippedVersion);
                if(candidate!=null)UpdateCatalog.ValidateAssets(candidate,candidate.Version);ErrorCode=null;return candidate;
            }catch(UpdateHttpFailure error){
                ErrorCode="update_http_"+error.Status;
                if(error.Status==403||error.Status==429)lock(gate){state.NotBeforeUtc=RetryTime(now,error.RetryAfter);try{Save();}catch(Exception){}}
            }catch(OperationCanceledException){ErrorCode="update_cancelled";}catch(WebException error){ErrorCode=error.Status==WebExceptionStatus.Timeout?"update_timeout":"update_network_failed";}catch(TimeoutException){ErrorCode="update_timeout";}catch(Exception){ErrorCode="update_check_failed";}
            return null;
        }
        internal void Defer(UpdateHttpFailure error,DateTime now){if(error.Status!=403&&error.Status!=429)return;lock(gate){state.NotBeforeUtc=RetryTime(now,error.RetryAfter);Save();}}
        internal bool DownloadsAllowed(DateTime now){lock(gate)return now>=state.NotBeforeUtc;}
        static DateTime RetryTime(DateTime now,string retry){
            DateTime until=now.AddDays(1),date;long seconds;
            if(Int64.TryParse(retry,NumberStyles.None,CultureInfo.InvariantCulture,out seconds)&&seconds>0)date=now.AddSeconds(Math.Min(seconds,TimeSpan.FromDays(3650).TotalSeconds));
            else if(!DateTime.TryParse(retry,CultureInfo.InvariantCulture,DateTimeStyles.AdjustToUniversal|DateTimeStyles.AssumeUniversal,out date))return until;
            return date>until?date:until;
        }
        internal bool MarkNotified(string version){lock(gate){if(state.SkippedVersion==version)return false;UpdateVersion previous;if(UpdateVersion.TryParse(state.NotifiedVersion,out previous)&&UpdateVersion.Parse(version).CompareTo(previous)<=0)return false;state.NotifiedVersion=version;Save();return true;}}
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
            if(targetVersion.CompareTo(installedVersion)<=0||!installedVersion.IsBeta&&(release.Prerelease||targetVersion.IsBeta))throw new InvalidDataException("Update downgrade or channel mismatch refused.");
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
