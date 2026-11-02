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
    sealed class Http:IUpdateTransport {
        internal readonly Queue<UpdateResponse> Responses=new Queue<UpdateResponse>();
        internal readonly List<Uri> Requests=new List<Uri>();
        internal bool Timeout;
        public UpdateResponse Get(Uri uri,CancellationToken token){token.ThrowIfCancellationRequested();Requests.Add(uri);if(Timeout)throw new WebException("fixture timeout",WebExceptionStatus.Timeout);return Responses.Dequeue();}
        internal void Reply(string text,int status=200,string location=null,long length=-1,string retry=null){Responses.Enqueue(new UpdateResponse(status,new MemoryStream(Encoding.UTF8.GetBytes(text)),length,location,retry));}
        internal void Bytes(byte[] bytes){Responses.Enqueue(new UpdateResponse(200,new MemoryStream(bytes),bytes.Length,null,null));}
    }
    sealed class Installer:IUpdateInstaller {
        internal bool Stable=true;internal int Starts;
        internal string Root,Package;
        public bool IsStable(CancellationToken token){return Stable;}
        public void Start(PreparedUpdate update){Starts++;Root=update.InstallDirectory;Package=update.PackageDirectory;}
    }
    static UpdateAsset Asset(string version,string name){return new UpdateAsset {Name=name,Size=100,Url="https://github.com/wushuai0802/myfinder-device-battery/releases/download/v"+version+"/"+name};}
    static UpdateRelease Release(string version,bool pre=false){return new UpdateRelease {Tag="v"+version,Prerelease=pre,Body="<script>bad()</script>\r\n[click](file:///C:/x)",Assets=new[]{Asset(version,UpdateTrust.ZipName(version)),Asset(version,"SHA256SUMS.txt"),Asset(version,"SHA256SUMS.txt.sig")}};}
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
    static void Signed(Http http,RSACryptoServiceProvider key,byte[] zip,string version,string sumName=null,bool corruptSums=false,bool corruptZip=false,bool absentSignature=false){
        byte[] sums=Encoding.UTF8.GetBytes(Hash(zip)+"  "+(sumName??UpdateTrust.ZipName(version))+"\n");
        byte[] sig=key.SignData(sums,CryptoConfig.MapNameToOID("SHA256"));
        if(corruptSums)sums[0]=(byte)(sums[0]=='a'?'b':'a');if(corruptZip)zip=new byte[]{1,2,3};
        http.Bytes(sums);if(absentSignature)http.Reply("missing",404);else http.Bytes(sig);http.Bytes(zip);
    }
    [STAThread] static int Main(string[] args){
        string root=Path.Combine(Path.GetFullPath(args[0]),"update-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try {
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
            var foreign=Release("1.3.5");foreign.Assets[0].Url="https://github.com/other/repo/releases/download/v1.3.5/"+UpdateTrust.ZipName("1.3.5");
            Reject(()=>UpdateCatalog.ValidateAssets(foreign,"1.3.5"),"foreign repository asset");
            var renamed=Release("1.3.5");renamed.Assets[0].Name="MyFinder-device-battery-v1.3.5-windows-x64.zip";Reject(()=>UpdateCatalog.ValidateAssets(renamed,"1.3.5"),"asset filename is case exact");
            var now=new DateTime(2026,1,2,0,0,0,DateTimeKind.Utc);
            var state=new UpdateState();Check(!UpdateSchedule.CanCheck(state,now,now.AddMinutes(-5),true,false),"disabled sends no request");
            Check(!UpdateSchedule.CanCheck(state,now,now.AddSeconds(-119),true,true),"two minute startup delay");
            Check(UpdateSchedule.CanCheck(state,now,now.AddMinutes(-2),true,true),"startup delay expires");
            state.LastCheckUtc=now.AddHours(-23);Check(!UpdateSchedule.CanCheck(state,now,now.AddDays(-1),true,true),"24 hour throttle");
            state.LastCheckUtc=now.AddDays(-1);Check(UpdateSchedule.CanCheck(state,now,now.AddDays(-2),true,true),"daily check allowed");
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
                Check(!checker.DownloadsAllowed(now.AddDays(1))&&checker.DownloadsAllowed(now.AddDays(2)),"download also respects Retry-After "+status);
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
            using(var dialog=new UpdateDialog(Release("1.3.6"),true)){
                var notes=dialog.Controls.Find("ReleaseNotes",true).Single() as TextBox;Check(notes!=null&&notes.ReadOnly&&notes.Multiline&&notes.Text.Contains("<script>bad()</script>"),"malicious release notes shown by plain TextBox");
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
