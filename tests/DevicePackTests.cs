using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

internal static class DevicePackTests {
    static int checks;static readonly DateTime Now=new DateTime(2026,11,4,10,0,0,DateTimeKind.Utc);
    static void Check(bool value,string name){if(!value)throw new Exception("FAIL "+name);checks++;}
    static void Reject(Action action,string name){bool rejected=false;try{action();}catch(Exception){rejected=true;}Check(rejected,name);}
    static byte[] Bytes(string text){return Encoding.UTF8.GetBytes(text);}
    static string Pack(long version,string key="test"){return "{\"format\":\"myfinder-device-pack\",\"loader\":1,\"packVersion\":"+version+",\"keyId\":\""+key+"\",\"createdAt\":\"2026-11-04T10:00:00Z\",\"entries\":[],\"disabled\":[]}";}
    static byte[] Sign(byte[] raw,RSACryptoServiceProvider key){return Bytes(Convert.ToBase64String(key.SignData(DevicePackTrust.SignedBytes(raw),CryptoConfig.MapNameToOID("SHA256")))+"\n");}
    static string Pointer(long version,byte[] raw,string hash=null){return "{\"format\":\"myfinder-device-pack-pointer\",\"loader\":1,\"packVersion\":"+version+",\"file\":\"device-pack-v1-"+version+".json\",\"sha256\":\""+(hash??DevicePackTrust.Hash(raw))+"\",\"size\":"+raw.Length+"}";}
    sealed class Http:IUpdateTransport {
        internal readonly Dictionary<string,Queue<Func<CancellationToken,UpdateResponse>>> Replies=new Dictionary<string,Queue<Func<CancellationToken,UpdateResponse>>>();
        internal readonly List<Uri> Requests=new List<Uri>();
        internal void Add(string url,byte[] body,int status=200,string location=null,long length=-2){Add(url,delegate(CancellationToken token){return new UpdateResponse(status,new MemoryStream(body),length==-2?body.Length:length,location,null);});}
        internal void Add(string url,Func<CancellationToken,UpdateResponse> reply){Queue<Func<CancellationToken,UpdateResponse>> queue;if(!Replies.TryGetValue(url,out queue)){queue=new Queue<Func<CancellationToken,UpdateResponse>>();Replies.Add(url,queue);}queue.Enqueue(reply);}
        public UpdateResponse Get(Uri uri,CancellationToken token){Requests.Add(uri);Queue<Func<CancellationToken,UpdateResponse>> queue;if(!Replies.TryGetValue(uri.AbsoluteUri,out queue)||queue.Count==0)throw new IOException("fixture_no_response");return queue.Dequeue()(token);}
    }
    sealed class Activation {
        internal int Trials,Commits;internal bool Fail,Timeout;internal Action BeforeCommit;
        internal bool Run(string path,long version,Func<string> commit,CancellationToken token){Trials++;Check(File.Exists(path)&&File.Exists(path+".sig"),"trial sees staged signed pair");if(Timeout)throw new TimeoutException("pack_trial_timeout");if(Fail)return false;if(BeforeCommit!=null)BeforeCommit();string current=commit();Commits++;Check(current.Contains(Path.DirectorySeparatorChar+"current"+Path.DirectorySeparatorChar)&&File.Exists(current+".sig"),"commit publishes signed current pair");return true;}
    }
    static void Feed(Http http,RSACryptoServiceProvider key,long version,string source=DevicePackTrust.Mirror,byte[] raw=null,byte[] sig=null,string hash=null){
        raw=raw??Bytes(Pack(version));string name="device-pack-v1-"+version+".json";
        http.Add(source+"device-pack-v1-latest.json",Bytes(Pointer(version,raw,hash)));http.Add(source+name,raw);http.Add(source+name+".sig",sig??Sign(raw,key));
    }
    static DevicePackManager Manager(string root,Http http,Activation activation,IDictionary<string,string> keys){return DevicePackManager.ForTests(root,http,activation.Run,Now,keys);}
    static void BothPointers(Http http,long version,byte[] raw){foreach(string source in new[]{DevicePackTrust.Mirror,DevicePackTrust.GitHub})http.Add(source+"device-pack-v1-latest.json",Bytes(Pointer(version,raw)));}
    [DataContract] sealed class Corpus {[DataMember(Name="cases")] public Case[] Cases {get;set;}}
    [DataContract] sealed class Case {[DataMember(Name="name")] public string Name {get;set;}[DataMember(Name="json")] public string Json {get;set;}[DataMember(Name="loaded")] public bool Loaded {get;set;}}
    static void Contract(string source){
        var corpus=UpdateJson.Read<Corpus>(File.ReadAllBytes(Path.Combine(source,"tests","fixtures","device-pack-contract.json")));
        foreach(var item in corpus.Cases){bool valid;try{DevicePackValidation.Validate(Bytes(item.Json));valid=true;}catch(Exception){valid=false;}Check(valid==item.Loaded,"shared schema "+item.Name);}
        using(var schema=Assembly.GetExecutingAssembly().GetManifestResourceStream("DevicePackSchema"))using(var memory=new MemoryStream()){schema.CopyTo(memory);Check(DevicePackTrust.Hash(memory.ToArray())==DevicePackTrust.Hash(File.ReadAllBytes(Path.Combine(source,"engine","device_pack_schema.json"))),"embedded schema is the engine schema");}
        Reject(()=>DevicePackValidation.Validate(new byte[]{0xC0,0xAF}),"invalid UTF-8");Reject(()=>DevicePackValidation.Validate(new byte[262145]),"oversize pack");
        var data=Bytes(Pack(1));string pointer=Pointer(1,data);
        foreach(string bad in new[]{pointer.Replace("\"loader\":1","\"loader\":2"),pointer.Replace("\"loader\":1","\"loader\":1,\"loader\":1"),pointer.Replace("\"size\":"+data.Length,"\"size\":262145"),pointer.Replace("device-pack-v1-1.json","../escape.json"),pointer.Replace("\"format\":","\"other\":0,\"format\":"),pointer.Replace("\"loader\":1","\"loader\":1.0")})Reject(()=>DevicePackPointer.Parse(Bytes(bad)),"strict pointer fields and types");
        Check(UpdateCatalog.Select(new[]{new UpdateRelease {Tag="device-pack"}},"1.3.6",null)==null,"program updates ignore device-pack tag");
        Check(UpdateCatalog.Select(new[]{new UpdateRelease {Tag="device-pack"},new UpdateRelease {Tag="v1.3.7"}},"1.3.6",null).Version=="1.3.7","device-pack release cannot mask real program update");
        Reject(()=>UpdateNetwork.ValidateUri(new Uri(DevicePackTrust.Mirror+"device-pack-v1-latest.json"),false),"program update allowlist unchanged");
    }
    static void Network(){
        string name="device-pack-v1-latest.json";var http=new Http();
        http.Add(DevicePackTrust.GitHub+name,new byte[0],302,"https://release-assets.githubusercontent.com/asset?token=fixture");http.Add("https://release-assets.githubusercontent.com/asset?token=fixture",Bytes("ok"));
        Check(Encoding.UTF8.GetString(DevicePackNetwork.Fetch(http,DevicePackTrust.GitHub,name,4,CancellationToken.None))=="ok","GitHub allowlisted download redirect");
        foreach(string uri in new[]{"http://github.com/x","https://evil.example/x","https://github.com.evil.example/x","https://user@github.com/x","https://github.com:444/x","https://github.com/other/repo/file",DevicePackTrust.Mirror+name}){
            http=new Http();http.Add(DevicePackTrust.GitHub+name,new byte[0],302,uri);Reject(()=>DevicePackNetwork.Fetch(http,DevicePackTrust.GitHub,name,4096,CancellationToken.None),"refused redirect "+uri);Check(http.Requests.Count==1,"forbidden host never requested");
        }
        http=new Http();http.Add(DevicePackTrust.Mirror+name,new byte[0],302,DevicePackTrust.GitHub+name);Reject(()=>DevicePackNetwork.Fetch(http,DevicePackTrust.Mirror,name,4096,CancellationToken.None),"mirror redirect cannot change source");
        http=new Http();http.Add(DevicePackTrust.Mirror+name,new byte[0],200,null,4097);Reject(()=>DevicePackNetwork.Fetch(http,DevicePackTrust.Mirror,name,4096,CancellationToken.None),"oversize content-length");
        http=new Http();http.Add(DevicePackTrust.Mirror+name,new byte[4097],200,null,-1);Reject(()=>DevicePackNetwork.Fetch(http,DevicePackTrust.Mirror,name,4096,CancellationToken.None),"oversize streaming body");
        http=new Http();http.Add(DevicePackTrust.Mirror+name,Bytes("short"),200,null,10);Reject(()=>DevicePackNetwork.Fetch(http,DevicePackTrust.Mirror,name,4096,CancellationToken.None),"truncated body");
        http=new Http();for(int i=0;i<6;i++)http.Add(DevicePackTrust.GitHub+name,new byte[0],302,DevicePackTrust.GitHub+name);Reject(()=>DevicePackNetwork.Fetch(http,DevicePackTrust.GitHub,name,4096,CancellationToken.None),"redirect loop cap");
        http=new Http();http.Add(DevicePackTrust.Mirror+name,delegate(CancellationToken token){token.WaitHandle.WaitOne(18000);token.ThrowIfCancellationRequested();throw new Exception("no timeout");});
        var watch=Stopwatch.StartNew();Reject(()=>DevicePackNetwork.Fetch(http,DevicePackTrust.Mirror,name,4096,CancellationToken.None),"request times out");Check(watch.Elapsed.TotalSeconds>=14&&watch.Elapsed.TotalSeconds<18,"15 second total request deadline reaches transport");
    }
    static void SecurityAndLifecycle(string root,RSACryptoServiceProvider key,IDictionary<string,string> keys){
        var http=new Http();var activation=new Activation();string directory=Path.Combine(root,"normal");var manager=Manager(directory,http,activation,keys);
        manager.Check(true,Now.AddSeconds(19),CancellationToken.None);Check(http.Requests.Count==0,"startup check waits 20 seconds");
        Feed(http,key,1);manager.Check(true,Now.AddSeconds(20),CancellationToken.None);var state=manager.StateForTests;
        Check(state.Current.Version==1&&state.HighestAcceptedVersion==1&&state.LastSuccessUtc==Now.AddSeconds(20)&&activation.Commits==1,"normal first upgrade");
        using(var lease=manager.OpenForStart()){Check(lease.Document.Version==1,"startup revalidates accepted pack");Reject(()=>File.WriteAllText(lease.Path,"changed"),"verified pack locked through ready handshake");}
        manager.Check(true,Now.AddHours(11),CancellationToken.None);Check(http.Requests.Count==3,"12 hour throttle");
        Feed(http,key,2);manager.Check(true,Now.AddHours(12).AddSeconds(21),CancellationToken.None);state=manager.StateForTests;
        Check(state.HighestAcceptedVersion==2&&state.Previous.Version==1&&state.Current.Version==2,"upgrade retains previous");
        int before=http.Requests.Count;http.Add(DevicePackTrust.Mirror+"device-pack-v1-latest.json",Bytes(Pointer(2,Bytes(Pack(2)))));manager.Check(false,Now.AddHours(13),CancellationToken.None);
        Check(http.Requests.Count==before+1&&activation.Trials==2&&manager.StateForTests.Result=="pack_unchanged","equal version skips assets");
        BothPointers(http,1,Bytes(Pack(1)));manager.Check(false,Now.AddHours(14),CancellationToken.None);Check(manager.StateForTests.Result=="pack_rollback_refused"&&activation.Trials==2,"old pointer rejected");
        BothPointers(http,2,Bytes(Pack(2)+" "));manager.Check(false,Now.AddHours(15),CancellationToken.None);Check(manager.StateForTests.Result=="pack_version_digest_conflict","same version different content is diagnosed");
        DateTime enabled=state.ActivatedUtc;
        Check(!manager.EngineFailed(2,enabled.AddMinutes(1))&&!manager.EngineFailed(2,enabled.AddMinutes(2))&&manager.EngineFailed(2,enabled.AddMinutes(3)),"three early crashes roll back");state=manager.StateForTests;
        Check(state.Current.Version==1&&state.HighestAcceptedVersion==2&&state.Quarantine.Any(x=>x.Version==2),"rollback keeps high-water mark and quarantine");
        BothPointers(http,2,Bytes(Pack(2)));manager.Check(false,Now.AddHours(16),CancellationToken.None);Check(manager.StateForTests.Result=="pack_quarantined"&&activation.Trials==2,"quarantine prevents repeat trial");
        var reload=Manager(directory,new Http(),new Activation(),keys);using(var lease=reload.OpenForStart())Check(lease.Document.Version==1&&reload.StateForTests.HighestAcceptedVersion==2,"rollback survives restart");
        Check(manager.Diagnostic.Contains("镜像")&&manager.Diagnostic.Contains("隔离版本：2")&&manager.Diagnostic.Contains("上次成功检查"),"diagnostic includes pack provenance and quarantine");

        foreach(string failure in new[]{"signature","digest","key","field","duplicate","size","sig_size","revision"}){
            http=new Http();activation=new Activation();manager=Manager(Path.Combine(root,"bad-"+failure),http,activation,keys);byte[] raw=Bytes(Pack(1)),sig=null;string hash=null;
            if(failure=="key")raw=Bytes(Pack(1,"unknown"));if(failure=="field")raw=Bytes(Pack(1).Replace("\"entries\":[]","\"entries\":[],\"extra\":1"));if(failure=="duplicate")raw=Bytes(Pack(1).Replace("\"loader\":1","\"loader\":1,\"loader\":1"));
            if(failure=="size")raw=new byte[262145];if(failure=="sig_size")sig=new byte[4097];if(failure=="digest")hash=new string('0',64);
            if(failure=="revision")raw=Bytes(Pack(1).Replace("\"entries\":[]","\"entries\":[{\"provider\":\"razer\",\"profile\":\"hyperspeed-mouse-1f\",\"profileRev\":2,\"match\":{\"vendorId\":\"1532\",\"productId\":\"FE01\"},\"name\":\"Mouse\",\"kind\":\"mouse\"}]"));
            using(var wrong=new RSACryptoServiceProvider(2048)){wrong.PersistKeyInCsp=false;if(failure=="signature")sig=Sign(raw,wrong);
                foreach(string source in new[]{DevicePackTrust.Mirror,DevicePackTrust.GitHub})Feed(http,key,1,source,raw,sig??(raw.Length>262144?new byte[1]:Sign(raw,key)),hash);
                manager.Check(false,Now,CancellationToken.None);
            }
            Check(activation.Trials==0&&manager.StateForTests.HighestAcceptedVersion==0&&manager.StateForTests.Current==null,"reject before engine: "+failure);
        }
        foreach(bool timeout in new[]{false,true}){
            http=new Http();activation=new Activation {Fail=!timeout,Timeout=timeout};manager=Manager(Path.Combine(root,timeout?"trial-timeout":"trial-false"),http,activation,keys);Feed(http,key,1);manager.Check(false,Now,CancellationToken.None);state=manager.StateForTests;
            Check(state.Current==null&&state.Quarantine.Count==1&&state.HighestAcceptedVersion==0&&activation.Commits==0,"failed trial never commits "+timeout);
            Check(Directory.GetFiles(Path.Combine(root,timeout?"trial-timeout":"trial-false","staging")).Length==0,"failed staging discarded");
        }
        http=new Http();activation=new Activation();manager=Manager(Path.Combine(root,"fallback"),http,activation,keys);Feed(http,key,1,DevicePackTrust.GitHub);manager.Check(false,Now,CancellationToken.None);
        Check(manager.StateForTests.Current.Source=="github"&&http.Requests[0].AbsoluteUri.StartsWith(DevicePackTrust.Mirror)&&http.Requests[1].AbsoluteUri.StartsWith(DevicePackTrust.GitHub),"mirror failure falls back to GitHub");
        http=new Http();activation=new Activation();directory=Path.Combine(root,"repair");manager=Manager(directory,http,activation,keys);Feed(http,key,1);manager.Check(false,Now,CancellationToken.None);
        File.Delete(Path.Combine(directory,"current","device-pack-v1-1.json"));Feed(http,key,1);manager.Check(false,Now.AddMinutes(1),CancellationToken.None);Check(activation.Commits==2&&manager.StateForTests.Current.Version==1,"equal version repairs missing current file");
        File.WriteAllText(Path.Combine(directory,"state.json"),"broken");manager=Manager(directory,new Http(),new Activation(),keys);Check(manager.OpenForStart()==null&&manager.Diagnostic.Contains("pack_state_invalid"),"corrupt state fails closed instead of resetting rollback floor");
        http=new Http();activation=new Activation();directory=Path.Combine(root,"startup");manager=Manager(directory,http,activation,keys);Feed(http,key,1);manager.Check(false,Now,CancellationToken.None);Feed(http,key,2);manager.Check(false,Now.AddMinutes(1),CancellationToken.None);
        File.WriteAllText(Path.Combine(directory,"current","device-pack-v1-2.json.sig"),"bad");using(var lease=manager.OpenForStart())Check(lease.Document.Version==1,"startup bad signature falls back to previous");Check(manager.StateForTests.HighestAcceptedVersion==2,"startup fallback retains highest version");
        http=new Http();activation=new Activation();directory=Path.Combine(root,"both-broken");manager=Manager(directory,http,activation,keys);Feed(http,key,1);manager.Check(false,Now,CancellationToken.None);Feed(http,key,2);manager.Check(false,Now.AddMinutes(1),CancellationToken.None);
        File.WriteAllText(Path.Combine(directory,"current","device-pack-v1-2.json"),"bad");File.WriteAllText(Path.Combine(directory,"previous","device-pack-v1-1.json"),"bad");Check(manager.OpenForStart()==null&&manager.StateForTests.Current==null&&manager.StateForTests.HighestAcceptedVersion==2,"both damaged falls back to builtin");
        http=new Http();activation=new Activation();manager=Manager(Path.Combine(root,"late-crash"),http,activation,keys);Feed(http,key,1);manager.Check(false,Now,CancellationToken.None);Check(!manager.EngineFailed(1,Now.AddMinutes(11))&&manager.StateForTests.Failures==0,"late crash does not roll back stable pack");
        http=new Http();activation=new Activation();directory=Path.Combine(root,"promotion-io");manager=Manager(directory,http,activation,keys);Feed(http,key,1);manager.Check(false,Now,CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(directory,"previous","device-pack-v1-1.json"));Feed(http,key,2);manager.Check(false,Now.AddMinutes(1),CancellationToken.None);
        Check(manager.StateForTests.Current.Version==1&&manager.StateForTests.HighestAcceptedVersion==1&&manager.StateForTests.Quarantine.Any(x=>x.Version==2),"failed previous write preserves active state and accepted floor");
        using(var lease=manager.OpenForStart())Check(lease.Document.Version==1,"old pack still usable after promotion IO failure");
        http=new Http();activation=new Activation();directory=Path.Combine(root,"stage-tamper");manager=Manager(directory,http,activation,keys);
        activation.BeforeCommit=delegate{File.WriteAllText(Path.Combine(directory,"staging","device-pack-v1-1.json"),Pack(1)+" ");};Feed(http,key,1);manager.Check(false,Now,CancellationToken.None);
        Check(manager.StateForTests.Current==null&&manager.StateForTests.Quarantine.Count==1,"promotion rechecks staged signature");
        http=new Http();activation=new Activation();directory=Path.Combine(root,"fallback-rejection");manager=Manager(directory,http,activation,keys);Feed(http,key,1);manager.Check(false,Now,CancellationToken.None);
        http.Add(DevicePackTrust.Mirror+"device-pack-v1-latest.json",Bytes(Pointer(1,Bytes(Pack(1)+" "))));http.Add(DevicePackTrust.GitHub+"device-pack-v1-latest.json",Bytes(Pointer(1,Bytes(Pack(1)))));manager.Check(false,Now.AddMinutes(1),CancellationToken.None);
        Check(manager.StateForTests.Result=="pack_unchanged"&&manager.Diagnostic.Contains("pack_version_digest_conflict"),"mirror digest conflict stays in diagnostics after GitHub success");
    }
    static void SingleFlight(string root,RSACryptoServiceProvider key,IDictionary<string,string> keys){
        var http=new Http();var activation=new Activation();var first=Manager(Path.Combine(root,"single"),http,activation,keys);var secondHttp=new Http();var second=Manager(Path.Combine(root,"second"),secondHttp,new Activation(),keys);
        using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false)){
            http.Add(DevicePackTrust.Mirror+"device-pack-v1-latest.json",delegate(CancellationToken token){entered.Set();if(!release.WaitOne(5000))throw new TimeoutException();return new UpdateResponse(200,new MemoryStream(Bytes(Pointer(1,Bytes(Pack(1))))),-1,null,null);});
            http.Add(DevicePackTrust.Mirror+"device-pack-v1-1.json",Bytes(Pack(1)));http.Add(DevicePackTrust.Mirror+"device-pack-v1-1.json.sig",Sign(Bytes(Pack(1)),key));
            var running=Task.Factory.StartNew(()=>first.Check(false,Now,CancellationToken.None));try{Check(entered.WaitOne(5000),"first check entered");second.Check(false,Now,CancellationToken.None);Check(secondHttp.Requests.Count==0,"global check single flight");}finally{release.Set();}Check(running.Wait(5000),"single flight releases");
        }
    }
    static string MakeWorker(string root,string name){string directory=Path.Combine(root,name);Directory.CreateDirectory(Path.Combine(directory,"runtime"));File.Copy(Assembly.GetExecutingAssembly().Location,Path.Combine(directory,"runtime","python.exe"));File.WriteAllText(Path.Combine(directory,"server.py"),"# Offline fake; never imports or probes hardware.");return directory;}
    static int FakeWorker(string[] args){
        string root=Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string pack=null;for(int i=0;i<args.Length-1;i++)if(args[i]=="--device-pack")pack=args[i+1];
        File.AppendAllText(Path.Combine(root,"starts.txt"),(pack??"builtin")+"\n");
        string mode=File.Exists(Path.Combine(root,"mode"))?File.ReadAllText(Path.Combine(root,"mode")):"";
        long version=pack==null?0:PackJson.Integer(PackJson.Object(PackJson.Parse(File.ReadAllBytes(pack)))["packVersion"]);
        // Match server.py's UTF-8 input with an optional initial BOM, independently
        // of the console code page. The signed pack parser remains strict.
        using(var input=new StreamReader(Console.OpenStandardInput(),Encoding.UTF8))using(var output=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false)){AutoFlush=true}){
        string line;while((line=input.ReadLine())!=null){var request=PackJson.Object(PackJson.Parse(Bytes(line)));string command=(string)request["command"];
            if((mode=="hang"&&command=="ready")||(mode=="hang-poll"&&command=="poll")){Thread.Sleep(30000);return 0;}if(mode=="crash"&&command=="poll")return 0;
            bool loaded=pack!=null&&mode!="false"&&!(mode=="formal-false"&&version==4&&pack.Contains(Path.DirectorySeparatorChar+"current"+Path.DirectorySeparatorChar));
            output.WriteLine("{\"schema\":2,\"requestId\":"+PackJson.Integer(request["requestId"])+",\"generation\":"+PackJson.Integer(request["generation"])+",\"complete\":true,\"ready\":true,\"devices\":[],\"devicePack\":{\"loaded\":"+(loaded?"true":"false")+",\"version\":"+(pack==null?"null":version.ToString())+",\"disabled\":[]}}");
        }}return 0;
    }
    static void WorkerInputEncodings(string root,RSACryptoServiceProvider key,IDictionary<string,string> keys){
        var previous=Console.InputEncoding;
        try{
            foreach(bool bom in new[]{false,true}){
                // Framework Process.Start inherits this encoding for the child's stdin.
                // Both forms must match the real server's utf-8-sig input contract.
                Console.InputEncoding=new UTF8Encoding(bom);
                string engine=MakeWorker(root,"encoding-"+bom);var http=new Http();
                using(var worker=new GenericBatteryWorker(engine,false,2000)){
                    var manager=DevicePackManager.ForTests(Path.Combine(root,"encoding-packs-"+bom),http,worker.ActivatePack,DateTime.UtcNow,keys);worker.UseDevicePacks(manager);
                    var before=worker.Poll();Check(before.Error==null&&before.Complete&&!before.DevicePack.Loaded,"builtin worker accepts UTF-8 input, BOM="+bom);
                    File.WriteAllText(Path.Combine(engine,"mode"),"false");Feed(http,key,1);manager.Check(false,DateTime.UtcNow,CancellationToken.None);
                    Check(manager.StateForTests.Current==null&&manager.StateForTests.HighestAcceptedVersion==0&&manager.StateForTests.Quarantine.Any(x=>x.Version==1),"rejected trial never publishes a pack, BOM="+bom);
                    var after=worker.Poll();Check(after.Error==null&&after.Complete&&!after.DevicePack.Loaded&&after.Generation==before.Generation,"rejected trial keeps the same builtin engine responsive, BOM="+bom);
                    var starts=File.ReadAllLines(Path.Combine(engine,"starts.txt"));Check(starts.Length==2&&starts[0]=="builtin"&&starts[1].Contains(Path.DirectorySeparatorChar+"staging"+Path.DirectorySeparatorChar),"rejected trial neither stops nor replaces builtin engine, BOM="+bom);
                }
            }
        }finally{Console.InputEncoding=previous;}
    }
    static void Workers(string root,string source,RSACryptoServiceProvider key,IDictionary<string,string> keys){
        string engine=MakeWorker(root,"engine");var http=new Http();string directory=Path.Combine(root,"worker-packs");
        using(var worker=new GenericBatteryWorker(engine,false,2000)){
            var manager=DevicePackManager.ForTests(directory,http,worker.ActivatePack,DateTime.UtcNow,keys);worker.UseDevicePacks(manager);
            Check(worker.Poll().Error==null,"original builtin engine alive");
            Feed(http,key,1);manager.Check(false,DateTime.UtcNow,CancellationToken.None);Check(manager.StateForTests.Current.Version==1&&worker.Poll().DevicePack.Version==1,"real process trial then formal engine replacement");
            var starts=File.ReadAllLines(Path.Combine(engine,"starts.txt"));Check(starts.Length==3&&starts[1].Contains("staging")&&starts[2].Contains("current"),"trial and formal process use expected paths");
            Feed(http,key,2);manager.Check(false,DateTime.UtcNow,CancellationToken.None);File.WriteAllText(Path.Combine(engine,"mode"),"crash");worker.Reset();
            for(int i=0;i<3;i++)Check(worker.Poll().Error!=null,"count real exited worker "+i);
            Check(manager.StateForTests.Current.Version==1&&manager.StateForTests.HighestAcceptedVersion==2,"real worker crash triggers rollback");
            File.WriteAllText(Path.Combine(engine,"mode"),"");Check(worker.Poll().DevicePack.Version==1,"worker restarts using previous pack");
            var beforeTrial=worker.Poll();int beforeTrialStarts=File.ReadAllLines(Path.Combine(engine,"starts.txt")).Length;
            File.WriteAllText(Path.Combine(engine,"mode"),"false");Feed(http,key,3);manager.Check(false,DateTime.UtcNow,CancellationToken.None);
            var afterTrial=worker.Poll();Check(manager.StateForTests.Current.Version==1&&manager.StateForTests.Quarantine.Any(x=>x.Version==3)&&afterTrial.Error==null&&afterTrial.DevicePack.Version==1&&afterTrial.Generation==beforeTrial.Generation&&File.ReadAllLines(Path.Combine(engine,"starts.txt")).Length==beforeTrialStarts+1,"loaded=false trial keeps existing engine without restart");
            File.WriteAllText(Path.Combine(engine,"mode"),"formal-false");Feed(http,key,4);manager.Check(false,DateTime.UtcNow,CancellationToken.None);
            Check(manager.StateForTests.Quarantine.Any(x=>x.Version==4)&&!manager.StateForTests.Quarantine.Any(x=>x.Version==1),"formal startup failure does not quarantine healthy previous");
        }
        engine=MakeWorker(root,"broken-engine");http=new Http();directory=Path.Combine(root,"broken-worker-packs");
        using(var worker=new GenericBatteryWorker(engine,false,2000)){
            var manager=DevicePackManager.ForTests(directory,http,worker.ActivatePack,DateTime.UtcNow,keys);worker.UseDevicePacks(manager);Feed(http,key,1);manager.Check(false,DateTime.UtcNow,CancellationToken.None);Feed(http,key,2);manager.Check(false,DateTime.UtcNow,CancellationToken.None);
            File.WriteAllText(Path.Combine(directory,"current","device-pack-v1-2.json.sig"),"bad");File.WriteAllText(Path.Combine(directory,"previous","device-pack-v1-1.json.sig"),"bad");worker.Reset();
            Check(worker.Poll().Error==null&&File.ReadAllLines(Path.Combine(engine,"starts.txt")).Last()=="builtin","damaged previous starts without --device-pack");
        }
        engine=MakeWorker(root,"unresponsive-engine");http=new Http();directory=Path.Combine(root,"unresponsive-packs");
        using(var worker=new GenericBatteryWorker(engine,false,150)){
            var manager=DevicePackManager.ForTests(directory,http,worker.ActivatePack,DateTime.UtcNow,keys);worker.UseDevicePacks(manager);Feed(http,key,1);manager.Check(false,DateTime.UtcNow,CancellationToken.None);
            File.WriteAllText(Path.Combine(engine,"mode"),"hang-poll");worker.Reset();
            for(int i=0;i<3;i++)Check(worker.Poll().Error!=null,"count timed-out engine "+i);
            Check(manager.StateForTests.Current==null&&manager.StateForTests.Quarantine.Any(x=>x.Version==1)&&manager.StateForTests.HighestAcceptedVersion==1,"three unresponsive engines fall back to builtin without lowering floor");
        }
        engine=MakeWorker(root,"timeout-engine");File.WriteAllText(Path.Combine(engine,"mode"),"hang");string pack=Path.Combine(root,"probe.json");File.WriteAllText(pack,Pack(1),new UTF8Encoding(false));
        using(var worker=new GenericBatteryWorker(engine,false,2000,null,pack,1)){var watch=Stopwatch.StartNew();Reject(()=>worker.Ready(1,150,CancellationToken.None),"ready timeout stops trial");Check(watch.ElapsedMilliseconds<4000,"ready probe budget bounded");}
        http=new Http();directory=Path.Combine(root,"timed-trial");
        using(var worker=new GenericBatteryWorker(engine,false,2000)){
            var manager=DevicePackManager.ForTests(directory,http,worker.ActivatePack,DateTime.UtcNow,keys);worker.UseDevicePacks(manager);Feed(http,key,1);
            var beforeTrial=worker.Poll();Check(beforeTrial.Error==null&&!beforeTrial.DevicePack.Loaded,"builtin engine alive before timed-out trial");int beforeTrialStarts=File.ReadAllLines(Path.Combine(engine,"starts.txt")).Length;
            var watch=Stopwatch.StartNew();manager.Check(false,DateTime.UtcNow,CancellationToken.None);Check(watch.Elapsed.TotalSeconds>=19&&watch.Elapsed.TotalSeconds<24&&manager.StateForTests.Current==null&&manager.StateForTests.Quarantine.Count==1,"production trial stops at 20 seconds and quarantines");
            var afterTrial=worker.Poll();Check(afterTrial.Error==null&&!afterTrial.DevicePack.Loaded&&afterTrial.Generation==beforeTrial.Generation&&File.ReadAllLines(Path.Combine(engine,"starts.txt")).Length==beforeTrialStarts+1,"timed-out trial keeps the same builtin engine responsive without restart");
        }
        if(File.Exists(Path.Combine(source,"engine","runtime","python.exe"))){
            string realPack=Path.Combine(root,"real-ready.json");File.WriteAllText(realPack,Pack(1).Replace("\"disabled\":[]","\"disabled\":[{\"provider\":\"razer\",\"vendorId\":\"1532\",\"productId\":\"FE01\",\"reason\":\"fixture\"}]"),new UTF8Encoding(false));
            using(var worker=new GenericBatteryWorker(Path.Combine(source,"engine"),true,2000,null,realPack,1)){var ready=worker.Ready(1,20000,CancellationToken.None);Check(ready.DevicePack.Disabled.Length==1,"real Python ready contract parsed by C# without polling hardware");}
        }else Console.WriteLine("UNAVAILABLE real Python ready: portable runtime absent; fake process lifecycle tested");
    }
    static void LinkedPaths(string root,RSACryptoServiceProvider key,IDictionary<string,string> keys){
        string directory=Path.Combine(root,"linked"),target=Path.Combine(root,"link-target");Directory.CreateDirectory(directory);Directory.CreateDirectory(target);File.WriteAllText(Path.Combine(target,"sentinel"),"preserve");
        var info=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe"));
        string command="New-Item -ItemType Junction -Path '"+Path.Combine(directory,"current").Replace("'","''")+"' -Target '"+target.Replace("'","''")+"' -ErrorAction Stop | Out-Null";
        info.Arguments="-NoProfile -Command "+UpdateProcess.Quote(command);info.UseShellExecute=false;info.CreateNoWindow=true;info.WindowStyle=ProcessWindowStyle.Hidden;
        using(var process=Process.Start(info)){Check(process.WaitForExit(10000)&&process.ExitCode==0,"create isolated reparse fixture");}
        Reject(()=>DevicePackManager.Write(Path.Combine(directory,"current","escape"),Bytes("bad")),"write refuses linked parent");
        var http=new Http();var manager=Manager(directory,http,new Activation(),keys);manager.Check(false,Now,CancellationToken.None);
        Check(manager.OpenForStart()==null&&http.Requests.Count==0&&Directory.GetFiles(target).Length==1&&File.ReadAllText(Path.Combine(target,"sentinel"))=="preserve","linked store fails closed without touching target");
        // The fixture is retained in tmp. Do not recursively delete a junction.
    }
    static void Signer(string root,string source,RSACryptoServiceProvider key){
        string pack=Path.Combine(root,"sign.json"),privateKey=Path.Combine(root,"temporary-test-key.xml");File.WriteAllText(pack,Pack(1,"pack-2026a"),new UTF8Encoding(false));File.WriteAllText(privateKey,key.ToXmlString(true));string digest=UpdatePackageVerifier.HashFile(privateKey);File.WriteAllText(pack+".sig","stale signature");
        var info=UpdateProcess.HelperStartInfo(Path.Combine(source,"scripts","Sign-DevicePack.ps1"),new[]{"-PackPath",pack,"-PrivateKeyPath",privateKey});info.RedirectStandardOutput=true;info.RedirectStandardError=true;
        using(var process=Process.Start(info)){string output=process.StandardOutput.ReadToEnd()+process.StandardError.ReadToEnd();Check(process.WaitForExit(20000)&&process.ExitCode!=0&&!File.Exists(pack+".sig")&&output.Contains("built-in public-key verification failed"),"manual signer rejects temporary private key against built-in public key and removes sig");Check(!output.Contains("<D>")&&!output.Contains("<RSAKeyValue>"),"signer does not print private key");}
        Check(UpdatePackageVerifier.HashFile(privateKey)==digest,"signer preserves private key input");File.Delete(privateKey);
    }
    static void DiagnosticLayout(){
        NativeDisplay.EnableDpiAwareness();DeviceSettings.UseTransientConfiguration(new AudioBindings {RememberReadings=false,ReducedMotion=true});
        var previousDpi=DialogDpi.DeviceDpiReader;var previousArea=ThemedDialog.WorkingAreaProvider;
        using(var watchdog=new System.Threading.Timer(delegate{Console.Error.WriteLine("FAIL: pack diagnostics triggered a layout loop");Environment.Exit(124);},null,15000,Timeout.Infinite))try{
            ThemedDialog.WorkingAreaProvider=delegate{return new Rectangle(0,0,3200,2400);};
            foreach(float scale in new[]{1f,2f})foreach(string pack in new[]{"未使用型号数据包","型号数据包：2026110401 | 来源：镜像 | 上次成功检查：2026-11-04 10:00:00 UTC | 禁用条目：2 | 隔离版本：2026110301"}){
                DialogDpi.DeviceDpiReader=delegate{return (int)(96*scale);};
                var report=new FeedbackReport("fixture","fixture-windows",new WirelessDevice[0],new SuppressedDevice[0],new UnadaptedDevice[0],new ReaderHealth[0],new string[0],pack);
                using(var feedback=new FeedbackForm(report,false,false,delegate{},delegate{},FeedbackRelay.Url))using(var dialog=feedback.CreateDiagnosticsDialog()){
                    dialog.Scale(new SizeF(scale,scale));dialog.Font=new Font("Microsoft YaHei UI",VisualDesign.BodyFontSize*scale,FontStyle.Regular,GraphicsUnit.Pixel);dialog.Opacity=0;dialog.ShowInTaskbar=false;dialog.Show();Application.DoEvents();
                    var text=(TextBox)dialog.Controls.Find("DiagnosticText",true)[0];int handles=0;text.HandleCreated+=delegate{handles++;};
                    foreach(var size in new[]{new Size(512,384),new Size(400,300),new Size(400,240),new Size(512,384)}){
                        var limit=new Size((int)(size.Width*scale),(int)(size.Height*scale));dialog.MaximumSize=new Size(limit.Width+dialog.Width-dialog.ClientSize.Width,limit.Height+dialog.Height-dialog.ClientSize.Height);dialog.ClientSize=limit;Application.DoEvents();
                        var close=(Control)dialog.AcceptButton;Check(close.Visible&&dialog.RectangleToScreen(dialog.ClientRectangle).Contains(close.RectangleToScreen(close.ClientRectangle)),"pack diagnostic close button remains reachable");
                    }
                    Check(handles<32&&text.Text.Contains(pack),"pack text remains visible without repeated scrollbar handle recreation");
                }
            }
        }finally{DialogDpi.DeviceDpiReader=previousDpi;ThemedDialog.WorkingAreaProvider=previousArea;}
    }
    [STAThread] static int Main(string[] args){
        if(args.Length>0&&args[0]=="-I")return FakeWorker(args);
        if(args.Length==1&&args[0]=="--layout-only"){try{DiagnosticLayout();Console.WriteLine("PASS "+checks+" pack diagnostic layout checks");return 0;}catch(Exception error){Console.Error.WriteLine(error);return 1;}}
        string root=Path.Combine(Path.GetFullPath(args[0]),"device-pack-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try{
            Contract(args[1]);Network();
            using(var key=new RSACryptoServiceProvider(2048)){
                key.PersistKeyInCsp=false;var keys=new Dictionary<string,string>{{"test",key.ToXmlString(false)}};
                byte[] raw=Bytes(Pack(1)),sig=Sign(raw,key);DevicePackTrust.Verify(raw,sig,keys);Check(true,"temporary key verifies exact prefixed bytes");
                Reject(()=>DevicePackTrust.Verify(Bytes(Pack(1)+"\n"),sig,keys),"raw byte normalization forbidden");
                Reject(()=>DevicePackTrust.Verify(raw,Bytes(Convert.ToBase64String(key.SignData(raw,CryptoConfig.MapNameToOID("SHA256")))),keys),"unprefixed signature rejected");
                Reject(()=>DevicePackTrust.Verify(raw,Bytes(" "+Encoding.UTF8.GetString(sig)),keys),"signature is exactly one base64 line");
                Reject(()=>DevicePackSigning.Verify(raw,sig),"production keyring cannot use injected test key");
                SecurityAndLifecycle(root,key,keys);SingleFlight(root,key,keys);WorkerInputEncodings(root,key,keys);Workers(root,args[1],key,keys);LinkedPaths(root,key,keys);Signer(root,args[1],key);
            }
            var card=new WirelessDevice {Name="Pack mouse",Kind="mouse",Provider="razer",ModelSource="pack"};var report=new FeedbackReport("1.3.6","Windows",new[]{card},new SuppressedDevice[0],new UnadaptedDevice[0],new ReaderHealth[0],new string[0],"型号数据包：1 | 来源：镜像");
            Check(report.FullText.Contains("型号数据包：1")&&report.FullText.Contains("来自数据包，待验证"),"feedback includes pack and card provenance");
            DiagnosticLayout();
            Console.WriteLine("PASS "+checks+" device-pack checks; fake HTTP, ephemeral keys, isolated fake engines; no hardware or production private key");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);Console.Error.WriteLine("Fixture retained: "+root);return 1;}
    }
}
