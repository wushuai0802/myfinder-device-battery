using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AtkBattery {
    // An explicit public-data projection, independent of local diagnostics.
    // Construction is offline. Only the click handler copies/opens anything.
    internal sealed class FeedbackReport {
        const string IssueBase="https://github.com/wushuai0802/myfinder-device-battery/issues/new?template=device-request.yml";
        const string Title="[设备反馈] 电量与连接";
        const int UrlLimit=6000;
        readonly List<string> devices=new List<string>(),suppressed=new List<string>(),unadapted=new List<string>(),health=new List<string>();
        readonly string header;
        internal FeedbackReport(string version,string windows,IEnumerable<WirelessDevice> cards,IEnumerable<SuppressedDevice> hidden,IEnumerable<UnadaptedDevice> unknown,IEnumerable<ReaderHealth> readers,IEnumerable<string> privateValues,string devicePack="未使用型号数据包"){
            var secrets=new List<string>();foreach(string value in privateValues)if(!String.IsNullOrEmpty(value))secrets.Add(value);
            secrets.Sort(delegate(string a,string b){return b.Length.CompareTo(a.Length);});
            Func<string,string> clean=delegate(string value){
                foreach(string secret in secrets)value=Regex.Replace(value??"",Regex.Escape(secret),"[redacted]",RegexOptions.IgnoreCase);
                return CleanField(value);
            };
            header="MyFinder Device Battery "+CleanField(version)+"\r\nWindows "+CleanField(windows)+"\r\n"+devicePack;
            foreach(var d in cards){
                // The worker permits 64 cards, plus the two native readers.
                if(d==null||devices.Count>=66)continue;
                string model=d.VendorId.HasValue&&d.ProductId.HasValue&&UnadaptedDevice.Word(d.VendorId.Value)&&UnadaptedDevice.Word(d.ProductId.Value)?" | "+d.VendorId.Value.ToString("X4")+":"+d.ProductId.Value.ToString("X4"):"";
                devices.Add(clean(d.Name)+" | "+clean(d.Kind)+" | "+clean(d.Provider)+" | "+(d.Percent.HasValue?(d.Approximate?"约":"")+d.Percent+"%":"未知")+" | 当前读数："+(d.MeasurementState=="current"?"是":"否")+model+(d.ModelSource=="pack"?" | 来自数据包，待验证":""));
            }
            foreach(var d in hidden)if(d!=null&&suppressed.Count<16)suppressed.Add(clean(d.Name)+" | "+clean(d.Kind));
            foreach(var d in unknown)if(d!=null&&unadapted.Count<16&&UnadaptedDevice.Word(d.VendorId)&&UnadaptedDevice.Word(d.ProductId)){
                var line=new StringBuilder(d.VendorId.ToString("X4")+":"+d.ProductId.ToString("X4")+" \""+clean(d.Product)+"\"");
                int count=0;foreach(var c in d.Collections??new UnadaptedCollection[0])if(c!=null&&UnadaptedDevice.Word(c.UsagePage)&&UnadaptedDevice.Word(c.Usage)&&count++<8)line.Append(" "+c.UsagePage.ToString("X4")+":"+c.Usage.ToString("X4"));
                unadapted.Add(line.ToString());
            }
            foreach(var r in readers)if(r!=null&&health.Count<64&&r.State!="ok"&&r.State!="no_devices"&&r.State!="unsupported")health.Add(clean(r.Provider)+" | "+clean(r.State)+" | "+clean(r.ErrorCode));
        }
        internal static string CleanField(string value){
            value=Regex.Replace(value??"",@"(?i)(?:[a-z]:[\\/]|\\\\|(?:hid|usb|bthenum|bthledevice)[\\#]|(?<!\w)/(?:[^\s/]+/))[^\r\n""']*","[path]");
            value=Regex.Replace(value,@"(?i)(?:\b(?:serial(?:[_ -]?number)?|s/n|sn|physical[_ -]?id|process[_ -]?id|pid|instance[_ -]?(?:id|time)|endpoint[_ -]?(?:id|name))\b|序列号|进程号)\s*[:=#：]?\s*(?:""[^""]*""|'[^']*'|[^\s]+)","[private]");
            value=Regex.Replace(value,@"(?i)\bdevice-[a-f0-9]{24}\b|\b[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\b|\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b|\b\d{4}-\d{2}-\d{2}T\S+","[device]");
            var text=new StringBuilder();foreach(char c in value)if(text.Length<80)text.Append(Char.IsControl(c)?' ':c);
            return text.ToString().Trim();
        }
        static void Section(StringBuilder text,string title,List<string> items,int count){
            text.Append("\r\n\r\n").Append(title);if(count==0)text.Append("\r\n无记录");
            for(int i=0;i<count;i++)text.Append("\r\n").Append(items[i]);
        }
        string Render(int dc,int sc,int uc,int hc,bool truncated){
            var text=new StringBuilder(header);Section(text,"有卡片的设备",devices,dc);Section(text,"已隐藏的重复连接",suppressed,sc);Section(text,"未取得电量的设备",unadapted,uc);
            if(hc>0)Section(text,"读取器健康",health,hc);
            if(truncated)text.Append("\r\n\r\n已截断");return text.ToString();
        }
        internal string FullText {get{return Render(devices.Count,suppressed.Count,unadapted.Count,health.Count,false);}}
        internal static string EncodeUrl(string title,string diagnostics){return IssueBase+"&title="+Uri.EscapeDataString(title)+"&diagnostics="+Uri.EscapeDataString(diagnostics);}
        internal string IssueUrl {get{
            int dc=devices.Count,sc=suppressed.Count,uc=unadapted.Count,hc=health.Count;bool truncated=false;
            while(true){
                string url=EncodeUrl(Title,Render(dc,sc,uc,hc,truncated));if(url.Length<=UrlLimit)return url;
                truncated=true;
                if(hc>0)hc=0;else if(uc>0)uc--;else if(sc>0)sc--;else if(dc>0)dc--;
                else throw new InvalidOperationException("Feedback header exceeds URL budget.");
            }
        }}
        internal FeedbackBrowserSubmission BrowserSubmission(string category,string description,bool includeDiagnostics){
            if(!FeedbackInput.Valid(category,description))throw new ArgumentException("Invalid feedback input.");
            description=description.Trim();string prefix="类型："+FeedbackInput.CategoryLabel(category)+"\r\n描述：\r\n";
            string full=prefix+description+(includeDiagnostics?"\r\n\r\n"+FullText:"");
            int dc=devices.Count,sc=suppressed.Count,uc=unadapted.Count,hc=health.Count;bool truncated=false;
            while(true){
                string tail=includeDiagnostics?"\r\n\r\n"+Render(dc,sc,uc,hc,truncated):"";
                string marker=truncated?"\r\n[已截断；完整内容已复制]":"";
                string url=EncodeUrl(Title,prefix+description+tail+marker);
                if(url.Length<=UrlLimit)return new FeedbackBrowserSubmission(full,url);
                truncated=true;
                if(includeDiagnostics&&(hc>0||uc>0||sc>0||dc>0)){
                    if(hc>0)hc=0;else if(uc>0)uc--;else if(sc>0)sc--;else dc--;
                }else{
                    // The full text is already retained for the clipboard. Bound only the prefill,
                    // preserving UTF-16 pairs and the existing diagnostic reduction order.
                    tail=includeDiagnostics?"\r\n\r\n"+Render(dc,sc,uc,hc,true):"";
                    marker="\r\n[已截断；完整内容已复制]";
                    int low=0,high=description.Length;
                    while(low<high){int mid=(low+high+1)/2;if(EncodeUrl(Title,prefix+FeedbackInput.Prefix(description,mid)+tail+marker).Length<=UrlLimit)low=mid;else high=mid-1;}
                    return new FeedbackBrowserSubmission(full,EncodeUrl(Title,prefix+FeedbackInput.Prefix(description,low)+tail+marker));
                }
            }
        }
    }

    [DataContract] public sealed class WirelessDpi {
        [DataMember(Name="supported")] public bool Supported;
        public bool CanSet;
        [DataMember(Name="canSet")] bool WireCanSet {get{return false;}set{CanSet=value;}}
        [DataMember(Name="current")] public int? Current;
        [DataMember(Name="stages")] public int[] Stages=new int[0];
        [DataMember(Name="stageSource")] public string StageSource;
        [DataMember(Name="checkedAtUtc")] public string CheckedAtUtc;
        public string TargetToken;
        // Action leases are accepted from the live worker only. Serializing a
        // device for a historical cache must never preserve an actionable token.
        [DataMember(Name="targetToken",EmitDefaultValue=false)] string WireTargetToken {get{return null;}set{TargetToken=value;}}
        [DataMember(Name="error")] public string Error;
        public DateTime CheckedTime {get{DateTime value;return DateTime.TryParse(CheckedAtUtc,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out value)?value.ToUniversalTime():DateTime.MinValue;}}
        internal static bool ValidValue(int value){return value>=50&&value<=65535;}
        internal static bool ValidToken(string value){return Regex.IsMatch(value??"",@"^[a-f0-9]{64}$");}
        internal bool HasStage(int value){return Stages!=null&&Array.IndexOf(Stages,value)>=0;}
        internal bool CanWrite(WirelessDevice device,string token){
            DateTime time=CheckedTime,now=DateTime.UtcNow;
            if(Stages==null||Stages.Length==0||Stages.Length>8)return false;
            var seen=new HashSet<int>();foreach(int stage in Stages)if(!ValidValue(stage)||!seen.Add(stage))return false;
            return device!=null&&device.Kind=="mouse"&&device.Provider=="logitech"&&device.Online&&device.ConnectionState=="connected"&&Supported&&CanSet&&Current.HasValue&&ValidValue(Current.Value)&&String.IsNullOrEmpty(Error)&&ValidToken(token)&&String.Equals(TargetToken,token,StringComparison.Ordinal)&&(StageSource=="onboard"||StageSource=="quick")&&time!=DateTime.MinValue&&time<=now.AddSeconds(5)&&(now-time).TotalSeconds<=90;
        }
        public string Key {get{return Supported+"|"+CanSet+"|"+Current+"|"+String.Join(",",Stages??new int[0])+"|"+StageSource+"|"+CheckedAtUtc+"|"+TargetToken+"|"+Error;}}
    }
    [DataContract] public sealed class WirelessDpiResult {
        [DataMember(Name="deviceId")] public string DeviceId;
        [DataMember(Name="targetToken")] public string TargetToken;
        [DataMember(Name="requestedDpi")] public int RequestedDpi;
        [DataMember(Name="confirmedDpi")] public int? ConfirmedDpi;
        [DataMember(Name="success")] public bool Success;
        [DataMember(Name="outcomeUnknown")] public bool OutcomeUnknown;
        [DataMember(Name="error")] public string Error;
        [DataMember(Name="checkedAtUtc")] public string CheckedAtUtc;
    }
    internal sealed class WirelessDpiRequest {
        internal readonly string DeviceId,TargetToken,Provider,Kind;
        internal readonly int Dpi,ExpectedDpi,Epoch;
        internal WirelessDpiRequest(string deviceId,string targetToken,int dpi,int expectedDpi,string provider,string kind,int epoch){DeviceId=deviceId;TargetToken=targetToken;Dpi=dpi;ExpectedDpi=expectedDpi;Provider=provider;Kind=kind;Epoch=epoch;}
    }
    [DataContract] public sealed class WirelessDevice {
        [DataMember(Name="id")] public string Id;
        [DataMember(Name="legacyId")] public string LegacyId;
        [DataMember(Name="physicalId",EmitDefaultValue=false)] public string PhysicalId;
        [DataMember(Name="name")] public string Name;
        [DataMember(Name="vendorId",EmitDefaultValue=false)] public int? VendorId;
        [DataMember(Name="productId",EmitDefaultValue=false)] public int? ProductId;
        [DataMember(Name="kind")] public string Kind;
        [DataMember(Name="percent")] public int? Percent;
        [DataMember(Name="charging")] public bool? Charging;
        [DataMember(Name="online")] public bool Online;
        [DataMember(Name="fresh")] public bool Fresh;
        [DataMember(Name="approximate")] public bool Approximate;
        [DataMember(Name="provider")] public string Provider;
        [DataMember(Name="transport")] public string Transport;
        [DataMember(Name="modelSource",EmitDefaultValue=false)] public string ModelSource;
        [DataMember(Name="confirmedAtUtc")] public string ConfirmedAtUtc;
        [DataMember(Name="measuredAtUtc")] public string MeasuredAtUtc;
        [DataMember(Name="connectionCheckedAtUtc")] public string ConnectionCheckedAtUtc;
        [DataMember(Name="chargingCheckedAtUtc")] public string ChargingCheckedAtUtc;
        [DataMember(Name="queriedAtUtc")] public string QueriedAtUtc;
        [DataMember(Name="sourceQuality")] public string SourceQuality;
        [DataMember(Name="connectionState")] public string ConnectionState;
        [DataMember(Name="verificationLevel")] public string VerificationLevel;
        [DataMember(Name="verificationLabel")] public string VerificationLabel;
        [DataMember(Name="measurementState")] public string MeasurementState;
        [DataMember(Name="refreshScope")] public string RefreshScope;
        [DataMember(Name="dpi",EmitDefaultValue=false)] public WirelessDpi Dpi;
        public DateTime ConfirmedTime {get{DateTime value;return DateTime.TryParse(ConfirmedAtUtc,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out value)?value.ToUniversalTime():DateTime.MinValue;}}
        public DateTime QueryTime {get{DateTime value;return DateTime.TryParse(QueriedAtUtc,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out value)?value.ToUniversalTime():DateTime.MinValue;}}
        internal bool SameCacheIdentity(WirelessDevice other){return other!=null&&Id==other.Id&&Kind==other.Kind&&Provider==other.Provider;}
        internal WirelessDevice BatteryCacheCopy(){var copy=(WirelessDevice)MemberwiseClone();copy.Dpi=null;return copy;}
        public string Key {get{return Id+"|"+PhysicalId+"|"+Name+"|"+Percent+"|"+Charging+"|"+Online+"|"+Fresh+"|"+Approximate+"|"+ConfirmedAtUtc+"|"+SourceQuality+"|"+QueriedAtUtc+"|"+(Dpi==null?"":Dpi.Key);}}
    }
    [DataContract] public sealed class ReaderHealth {
        [DataMember(Name="physicalId",EmitDefaultValue=false)] public string PhysicalId;
        [DataMember(Name="provider")] public string Provider;
        [DataMember(Name="state")] public string State;
        [DataMember(Name="scope")] public string Scope;
        [DataMember(Name="errorCode")] public string ErrorCode;
        [DataMember(Name="deviceCount")] public int DeviceCount;
        [DataMember(Name="nextRetryAtUtc")] public string NextRetryAtUtc;
    }
    [DataContract] public sealed class UnadaptedCollection {
        [DataMember(Name="usagePage")] public int UsagePage;
        [DataMember(Name="usage")] public int Usage;
    }
    [DataContract] public sealed class UnadaptedDevice {
        [DataMember(Name="vendorId")] public int VendorId;
        [DataMember(Name="productId")] public int ProductId;
        [DataMember(Name="product")] public string Product;
        [DataMember(Name="collections")] public UnadaptedCollection[] Collections;
        internal static bool Word(int value){return value>=0&&value<=65535;}
        internal static string CleanProduct(string value){
            // This extra cleanup is restricted to the new inventory section.
            // Model IDs are formatted from bounded integers, never from names.
            value=Regex.Replace(value??"",@"(?i)(?:[a-z]:\\|\\\\|(?:hid|usb|bthenum|bthledevice)[\\#])[^\s""']+","[path]");
            value=Regex.Replace(value,@"(?i)(?:\b(?:serial(?:[_ -]?number)?|s/n|sn)|序列号)\s*[:=#：]?\s*[^\s""']+","[serial]");
            value=SafeDiagnostic.Clean(value).Replace('"','\'');
            var result=new StringBuilder();foreach(char c in value)if(!Char.IsControl(c)&&result.Length<48)result.Append(c);
            return result.ToString().Trim();
        }
        internal string DiagnosticLine(){
            var line=new StringBuilder(VendorId.ToString("X4")+":"+ProductId.ToString("X4")+" \""+CleanProduct(Product)+"\"");
            int count=0;foreach(var item in Collections??new UnadaptedCollection[0])if(item!=null&&Word(item.UsagePage)&&Word(item.Usage)&&count++<8)line.Append(" "+item.UsagePage.ToString("X4")+":"+item.Usage.ToString("X4"));
            return line.ToString();
        }
    }
    [DataContract] public sealed class SuppressedDevice {
        [DataMember(Name="name")] public string Name;
        [DataMember(Name="kind")] public string Kind;
    }
    [DataContract] public sealed class WirelessDevicePack {
        [DataMember(Name="version")] public long? Version;
        [DataMember(Name="loaded")] public bool Loaded;
        [DataMember(Name="disabled")] public object[] Disabled;
    }
    [DataContract] public sealed class WirelessSnapshot {
        [DataMember(Name="schema")] public int Schema;
        [DataMember(Name="requestId")] public long RequestId;
        [DataMember(Name="generation")] public int Generation;
        [DataMember(Name="complete")] public bool Complete;
        [DataMember(Name="ready")] public bool Ready;
        [DataMember(Name="devicePack",EmitDefaultValue=false)] public WirelessDevicePack DevicePack;
        [DataMember(Name="devices")] public WirelessDevice[] Devices;
        [DataMember(Name="unadapted",EmitDefaultValue=false)] public UnadaptedDevice[] Unadapted;
        [DataMember(Name="suppressed",EmitDefaultValue=false)] public SuppressedDevice[] Suppressed;
        [DataMember(Name="nativeMousePresent")] public bool? NativeMousePresent;
        [DataMember(Name="providerCount")] public int ProviderCount;
        [DataMember(Name="health")] public ReaderHealth[] Health;
        [DataMember(Name="errors")] public string[] Errors;
        [DataMember(Name="error")] public string Error;
        [DataMember(Name="checkedAtUtc")] public string CheckedAtUtc;
        [DataMember(Name="dpiResult",EmitDefaultValue=false)] public WirelessDpiResult DpiResult;
    }
    internal sealed class GenericBatteryWorker:IDisposable {
        readonly string root;
        readonly bool smoke;
        readonly int queryBudget;
        readonly Func<Process,bool> terminate;
        readonly object lifecycle=new object();
        readonly object query=new object();
        int generation;long sequence;
        Process process;
        IntPtr job;
        bool disposed,quarantined;
        DevicePackManager packManager;
        readonly string explicitPackPath;readonly long explicitPackVersion;
        long loadedPackVersion;int failedPackGeneration=-1;
        sealed class DpiLease {internal string Token;internal int Current;internal int[] Stages;internal DateTime Checked;internal int Generation;}
        readonly Dictionary<string,DpiLease> dpiLeases=new Dictionary<string,DpiLease>(StringComparer.Ordinal);
        internal GenericBatteryWorker(string engineRoot=null,bool packageSmoke=false,int timeoutMilliseconds=45000,Func<Process,bool> terminateProcess=null,string devicePackPath=null,long devicePackVersion=0){root=engineRoot??Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","engine"));smoke=packageSmoke;queryBudget=Math.Max(100,Math.Min(45000,timeoutMilliseconds));terminate=terminateProcess??TryTerminate;explicitPackPath=devicePackPath;explicitPackVersion=devicePackVersion;}
        internal void UseDevicePacks(DevicePackManager manager){lock(query){if(process!=null)throw new InvalidOperationException("Attach device packs before starting the worker.");packManager=manager;}}
        internal bool Available {get{return File.Exists(Path.Combine(root,"runtime","python.exe"))&&File.Exists(Path.Combine(root,"server.py"));}}
        [StructLayout(LayoutKind.Sequential)] struct BasicLimits {
            public long PerProcess,PerJob;public uint Flags;public UIntPtr MinWorking,MaxWorking;public uint ActiveLimit;public UIntPtr Affinity;public uint Priority,Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)] struct Counters {public ulong Read,Write,Other,ReadBytes,WriteBytes,OtherBytes;}
        [StructLayout(LayoutKind.Sequential)] struct ExtendedLimits {public BasicLimits Basic;public Counters Io;public UIntPtr ProcessMemory,JobMemory,PeakProcess,PeakJob;}
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr security,string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job,int kind,ref ExtendedLimits limits,uint length);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        Process Start() {lock(lifecycle){
            if(disposed)throw new ObjectDisposedException("GenericBatteryWorker");
            if(quarantined&&!Stop())throw new IOException("上次设备读取进程仍未退出，控制暂时停用。");
            if(process!=null&&!process.HasExited)return process;
            if(process!=null)ReportPackFailure();
            if(!Stop())throw new IOException("上次设备读取进程仍未退出，控制暂时停用。");
            if(!Available)throw new IOException("请使用带设备读取组件的完整发行包。");
            for(int attempt=0;attempt<3;attempt++)using(var lease=packManager==null?null:packManager.OpenForStart()){
            string packPath=lease==null?explicitPackPath:lease.Path;
            loadedPackVersion=lease==null?explicitPackVersion:lease.Record.Version;
            var info=new ProcessStartInfo(Path.Combine(root,"runtime","python.exe"),"-I -B -u \""+Path.Combine(root,"server.py")+"\""+(smoke?" --package-smoke":"")+(packPath==null?"":" --device-pack "+UpdateProcess.Quote(packPath))) {
                WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
                RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8
            };
            process=Process.Start(info);process.ErrorDataReceived+=delegate{};process.BeginErrorReadLine();
            job=CreateJobObject(IntPtr.Zero,null);
            if(job!=IntPtr.Zero){var limits=new ExtendedLimits();limits.Basic.Flags=0x2000;
                if(!SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf(typeof(ExtendedLimits)))||!AssignProcessToJobObject(job,process.Handle)){Stop();throw new IOException("Reader process lifetime protection failed.");}}
            else{Stop();throw new IOException("Reader process lifetime protection failed.");}
            generation++;
            if(lease!=null){
                try{var ready=ReadyCore(process,loadedPackVersion,20000,CancellationToken.None);packManager.SetLoaded(loadedPackVersion,ready.DevicePack.Disabled==null?0:ready.DevicePack.Disabled.Length);}
                catch(Exception){long rejected=loadedPackVersion;if(!Stop())throw new IOException("pack_engine_stop_failed");packManager.StartupRejected(rejected);continue;}
            }
            return process;
            }
            throw new IOException("pack_startup_failed");
        }}
        internal WirelessSnapshot Ready(long version,int timeoutMilliseconds,CancellationToken token){lock(query){try{return ReadyCore(Start(),version,Math.Min(20000,timeoutMilliseconds),token);}catch{Stop();throw;}}}
        WirelessSnapshot ReadyCore(Process child,long version,int timeoutMilliseconds,CancellationToken token){
            token.ThrowIfCancellationRequested();long request=Interlocked.Increment(ref sequence);int currentGeneration=generation;
            child.StandardInput.WriteLine("{\"command\":\"ready\",\"requestId\":"+request+",\"generation\":"+currentGeneration+"}");child.StandardInput.Flush();
            var result=Receive(child,request,currentGeneration,null,null,timeoutMilliseconds,token);
            if(!String.IsNullOrEmpty(result.Error)||!result.Ready||result.Schema!=2||result.DevicePack==null||!result.DevicePack.Loaded||result.DevicePack.Version!=version)throw new InvalidDataException("pack_ready_rejected");
            return result;
        }
        internal bool ActivatePack(string staging,long version,Func<string> promote,CancellationToken token){lock(query){
            token.ThrowIfCancellationRequested();if(disposed)throw new ObjectDisposedException("GenericBatteryWorker");
            using(var lease=packManager==null?null:packManager.LockTrial(staging))
            using(var trial=new GenericBatteryWorker(root,smoke,20000,null,staging,version)){
                trial.Ready(version,20000,token);if(!trial.Stop())throw new IOException("pack_trial_stop_failed");
                token.ThrowIfCancellationRequested();promote();
            }
            if(!Stop())throw new IOException("pack_engine_stop_failed");
            Start();return loadedPackVersion==version;
        }}
        void ReportPackFailure(){if(!disposed&&packManager!=null&&loadedPackVersion>0&&failedPackGeneration!=generation){failedPackGeneration=generation;packManager.EngineFailed(loadedPackVersion,DateTime.UtcNow);}}
        internal WirelessSnapshot Poll(bool force=false,string deviceId=null,Action<WirelessSnapshot> progress=null,bool parkEmptyReaders=false,string physicalId=null) {lock(query){
            try {
                if(deviceId!=null&&physicalId!=null||deviceId!=null&&!Regex.IsMatch(deviceId,@"^device-[a-f0-9]{24}$")||physicalId!=null&&!Regex.IsMatch(physicalId,@"^device-[a-f0-9]{24}$"))return new WirelessSnapshot {Error="无效或冲突的设备刷新目标。"};
                var child=Start();int currentGeneration=generation;long request=Interlocked.Increment(ref sequence);
                child.StandardInput.WriteLine("{\"command\":\"poll\",\"requestId\":"+request+",\"generation\":"+currentGeneration+",\"force\":"+(force?"true":"false")+",\"parkEmptyReaders\":"+(parkEmptyReaders?"true":"false")+(deviceId==null?"":",\"deviceId\":\""+deviceId+"\"")+(physicalId==null?"":",\"physicalId\":\""+physicalId+"\"")+"}");child.StandardInput.Flush();
                var result=Receive(child,request,currentGeneration,progress,null);if(!String.IsNullOrEmpty(result.Error))ReportPackFailure();return result;
            }catch(Exception){Stop();ReportPackFailure();return new WirelessSnapshot {Error="设备读取组件暂不可用，保留最近数据。"};}
        }}
        internal WirelessSnapshot SetDpi(string deviceId,string targetToken,int dpi,int expectedDpi) {
            int submittedGeneration;lock(lifecycle)submittedGeneration=generation;
            lock(query){
            var write=new WirelessDpiResult {DeviceId=deviceId,TargetToken=targetToken,RequestedDpi=dpi};bool attempted=false;
            if(!Regex.IsMatch(deviceId??"",@"^device-[a-f0-9]{24}$")||!WirelessDpi.ValidToken(targetToken)||!WirelessDpi.ValidValue(dpi)||!WirelessDpi.ValidValue(expectedDpi))return DpiFailure(write,"无效的DPI控制目标。",false);
            try {
                Process child;int currentGeneration;
                lock(lifecycle){
                    DpiLease lease;child=process;currentGeneration=generation;
                    if(disposed||quarantined||child==null||child.HasExited||generation!=submittedGeneration||!dpiLeases.TryGetValue(deviceId,out lease)||lease.Generation!=submittedGeneration||lease.Token!=targetToken||lease.Current!=expectedDpi||Array.IndexOf(lease.Stages,dpi)<0||(DateTime.UtcNow-lease.Checked).TotalSeconds>90||lease.Checked>DateTime.UtcNow.AddSeconds(5))return DpiFailure(write,"DPI状态或设备已改变，请先同步。",false);
                    dpiLeases.Remove(deviceId);
                }
                long request=Interlocked.Increment(ref sequence);
                attempted=true;
                child.StandardInput.WriteLine("{\"command\":\"set_dpi\",\"requestId\":"+request+",\"generation\":"+currentGeneration+",\"deviceId\":\""+deviceId+"\",\"dpi\":"+dpi+",\"targetToken\":\""+targetToken+"\",\"expectedDpi\":"+expectedDpi+"}");child.StandardInput.Flush();
                var result=Receive(child,request,currentGeneration,null,write);if(!String.IsNullOrEmpty(result.Error))ReportPackFailure();return result;
            }catch(Exception){Stop();if(attempted)ReportPackFailure();return DpiFailure(write,attempted?"DPI操作结果未确认，请先同步；未自动重试。":"DPI控制暂不可用，请先同步。",attempted);}
        }}
        WirelessSnapshot Receive(Process child,long request,int currentGeneration,Action<WirelessSnapshot> progress,WirelessDpiResult write,int timeoutMilliseconds=0,CancellationToken token=default(CancellationToken)){
            int budget=timeoutMilliseconds>0?timeoutMilliseconds:queryBudget;
            var clock=Stopwatch.StartNew();var reader=child.StandardOutput;
            for(int frames=0;frames<128;frames++){
                var line=Task.Factory.StartNew(delegate{return AppRuntime.ReadLineBounded(reader,524288);});
                if(!line.Wait(Math.Max(1,budget-(int)clock.ElapsedMilliseconds),token)){Stop();return write==null?new WirelessSnapshot {Error="设备读取超时，保留最近数据。"}:DpiFailure(write,"DPI响应超时，结果未确认；未自动重试。",true);}
                var snapshot=Parse(line.Result);
                if(!String.IsNullOrEmpty(snapshot.Error)){Stop();return write==null?snapshot:DpiFailure(write,"DPI返回结果无效，结果未确认。",true);}
                if(snapshot.Schema==1){if(write!=null){Stop();return DpiFailure(write,"DPI返回结果不能核对，结果未确认。",true);}snapshot.Complete=true;RememberDpi(snapshot,child,currentGeneration);return snapshot;}
                if(snapshot.RequestId!=request||snapshot.Generation!=currentGeneration)continue;
                lock(lifecycle)if(disposed||process!=child||generation!=currentGeneration)return write==null?new WirelessSnapshot {Error="读取组件已重置，请重新同步。"}:DpiFailure(write,"读取组件已重置，DPI结果未确认。",true);
                if(snapshot.Complete){
                    if(write!=null){
                        var result=snapshot.DpiResult;
                        if(result==null||result.DeviceId!=write.DeviceId||result.TargetToken!=write.TargetToken||result.RequestedDpi!=write.RequestedDpi){Stop();return DpiFailure(write,"DPI返回目标不一致，结果未确认。",true);}
                        if(result.Success){
                            var device=Array.Find(snapshot.Devices,delegate(WirelessDevice value){return value.Id==write.DeviceId;});
                            DateTime checkedTime;
                            bool resultFresh=DateTime.TryParse(result.CheckedAtUtc,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out checkedTime)&&FreshDpiTime(checkedTime.ToUniversalTime());
                            if(result.OutcomeUnknown||!String.IsNullOrEmpty(result.Error)||!resultFresh||result.ConfirmedDpi!=write.RequestedDpi||device==null||device.Provider!="logitech"||device.Kind!="mouse"||device.Dpi==null||device.Dpi.Current!=write.RequestedDpi||device.Dpi.TargetToken!=write.TargetToken||!device.Dpi.CanWrite(device,write.TargetToken)||!device.Dpi.HasStage(write.RequestedDpi)){Stop();return DpiFailure(write,"DPI重新读取未能确认目标值。",true);}
                        }else foreach(var device in snapshot.Devices)if(device.Id==write.DeviceId)DisableDpi(device.Dpi);
                    }
                    RememberDpi(snapshot,child,currentGeneration);return snapshot;
                }
                if(write==null&&progress!=null)progress(snapshot);
                if(clock.ElapsedMilliseconds>=budget)break;
            }
            Stop();return write==null?new WirelessSnapshot {Error="设备读取未能在预算内完成。"}:DpiFailure(write,"DPI操作未能在预算内确认；未自动重试。",true);
        }
        void RememberDpi(WirelessSnapshot snapshot,Process child,int currentGeneration){lock(lifecycle){
            dpiLeases.Clear();if(disposed||process!=child||generation!=currentGeneration||snapshot.Schema!=2)return;
            foreach(var device in snapshot.Devices??new WirelessDevice[0])if(device.Dpi!=null&&device.Dpi.CanWrite(device,device.Dpi.TargetToken))dpiLeases[device.Id]=new DpiLease {Token=device.Dpi.TargetToken,Current=device.Dpi.Current.Value,Stages=(int[])device.Dpi.Stages.Clone(),Checked=device.Dpi.CheckedTime,Generation=currentGeneration};
        }}
        static WirelessSnapshot DpiFailure(WirelessDpiResult write,string message,bool unknown){return new WirelessSnapshot {Schema=2,Complete=true,Error=message,DpiResult=new WirelessDpiResult {DeviceId=write.DeviceId,TargetToken=write.TargetToken,RequestedDpi=write.RequestedDpi,Success=false,OutcomeUnknown=unknown,Error=message,CheckedAtUtc=DateTime.UtcNow.ToString("o")}};}
        static bool FreshDpiTime(DateTime time){var now=DateTime.UtcNow;return time!=DateTime.MinValue&&time<=now.AddSeconds(5)&&(now-time).TotalSeconds<=90;}
        internal static void DisableDpi(WirelessDpi dpi){if(dpi!=null){dpi.CanSet=false;dpi.TargetToken=null;}}
        internal static WirelessSnapshot Parse(string json) {
            if(String.IsNullOrEmpty(json)||json.Length>524288)return new WirelessSnapshot {Error="无效的设备读取结果。"};
            WirelessSnapshot snapshot;
            try {using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(json)))snapshot=(WirelessSnapshot)new DataContractJsonSerializer(typeof(WirelessSnapshot),new DataContractJsonSerializerSettings {MaxItemsInObjectGraph=32768}).ReadObject(stream);}
            catch(SerializationException){return new WirelessSnapshot {Error="设备读取结果格式无效。"};}
            if(snapshot==null||(snapshot.Schema!=1&&snapshot.Schema!=2)||!String.IsNullOrEmpty(snapshot.Error))return new WirelessSnapshot {Error="设备读取尚未完成。"};
            var devices=new List<WirelessDevice>();var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var value in snapshot.Devices??new WirelessDevice[0]) {
                if(devices.Count>=64)break;
                if(value==null||!Regex.IsMatch(value.Id??"",@"^device-[a-f0-9]{24}$")||!seen.Add(value.Id))continue;
                if(!Regex.IsMatch(value.LegacyId??"",@"^device-[a-f0-9]{24}$"))value.LegacyId=null;
                if(!Regex.IsMatch(value.PhysicalId??"",@"^device-[a-f0-9]{24}$"))value.PhysicalId=null;
                value.Name=Clean(value.Name,80,"无线设备");value.Provider=Clean(value.Provider,48,"");value.Transport=Clean(value.Transport,32,"");
                value.ModelSource=value.ModelSource=="pack"?"pack":null;
                if(!value.VendorId.HasValue||!value.ProductId.HasValue||!UnadaptedDevice.Word(value.VendorId.Value)||!UnadaptedDevice.Word(value.ProductId.Value)){value.VendorId=null;value.ProductId=null;}
                if(value.Kind!="mouse"&&value.Kind!="headset"&&value.Kind!="keyboard")value.Kind="device";
                if(value.Percent<0||value.Percent>100)value.Percent=null;
                DateTime time=value.ConfirmedTime;
                if(time==DateTime.MinValue||time>DateTime.UtcNow.AddSeconds(5)){value.Percent=null;value.Fresh=false;value.ConfirmedAtUtc=null;}
                if(!value.Online||!value.Percent.HasValue||(DateTime.UtcNow-time).TotalSeconds>90||value.SourceQuality=="system_property"||value.SourceQuality=="cached")value.Fresh=false;
                if(!value.Fresh)value.Charging=null;
                value.SourceQuality=Clean(value.SourceQuality,24,snapshot.Schema==1?"legacy":"unknown");value.MeasurementState=Clean(value.MeasurementState,16,"unknown");value.RefreshScope=value.RefreshScope=="device"?"device":"provider";
                value.ConnectionState=Clean(value.ConnectionState,24,snapshot.Schema==1&&value.Online?"connected":"unconfirmed");value.VerificationLevel=Clean(value.VerificationLevel,1,"C");value.VerificationLabel=Clean(value.VerificationLabel,64,"协议已接入，待型号实测");
                if(value.Dpi!=null){
                    var dpi=value.Dpi;bool validStages=dpi.Stages!=null&&dpi.Stages.Length>0&&dpi.Stages.Length<=8;var stages=new List<int>();
                    foreach(int stage in dpi.Stages??new int[0]){if(!WirelessDpi.ValidValue(stage)||stages.Contains(stage)){validStages=false;continue;}if(stages.Count<8)stages.Add(stage);}
                    dpi.Stages=stages.ToArray();if(dpi.Current.HasValue&&!WirelessDpi.ValidValue(dpi.Current.Value))dpi.Current=null;
                    dpi.Error=Clean(dpi.Error,160,"");if(dpi.StageSource!="onboard"&&dpi.StageSource!="quick")dpi.StageSource=null;
                    if(snapshot.Schema!=2||value.Kind!="mouse"||value.ModelSource=="pack")dpi.Supported=false;
                    dpi.CanSet=dpi.CanSet&&snapshot.RequestId>0&&snapshot.Generation>0&&validStages&&dpi.CanWrite(value,dpi.TargetToken);
                    if(!dpi.CanSet)DisableDpi(dpi);
                }
                devices.Add(value);
            }
            var errors=new List<string>();foreach(var error in snapshot.Errors??new string[0])if(errors.Count<32)errors.Add(Clean(error,96,"read_failed"));snapshot.Errors=errors.ToArray();
            if(snapshot.Suppressed!=null){
                var suppressed=new List<SuppressedDevice>();
                foreach(var item in snapshot.Suppressed){
                    if(suppressed.Count>=16)break;if(item==null)continue;
                    item.Name=FeedbackReport.CleanField(item.Name);
                    if(item.Kind!="mouse"&&item.Kind!="keyboard"&&item.Kind!="headset")item.Kind="device";
                    suppressed.Add(item);
                }
                snapshot.Suppressed=suppressed.ToArray();
            }
            if(snapshot.Unadapted!=null){
                var unadapted=new List<UnadaptedDevice>();
                foreach(var item in snapshot.Unadapted){
                    if(unadapted.Count>=16)break;
                    if(item==null||!UnadaptedDevice.Word(item.VendorId)||!UnadaptedDevice.Word(item.ProductId))continue;
                    item.Product=UnadaptedDevice.CleanProduct(item.Product);
                    var collections=new List<UnadaptedCollection>();
                    foreach(var collection in item.Collections??new UnadaptedCollection[0])if(collection!=null&&UnadaptedDevice.Word(collection.UsagePage)&&UnadaptedDevice.Word(collection.Usage)&&collections.Count<8)collections.Add(collection);
                    item.Collections=collections.ToArray();unadapted.Add(item);
                }
                snapshot.Unadapted=unadapted.ToArray();
            }
            var health=new List<ReaderHealth>();foreach(var item in snapshot.Health??new ReaderHealth[0])if(item!=null&&health.Count<64){if(!Regex.IsMatch(item.PhysicalId??"",@"^device-[a-f0-9]{24}$"))item.PhysicalId=null;item.Provider=Clean(item.Provider,48,"unknown");item.State=Clean(item.State,24,"unknown");item.ErrorCode=Clean(item.ErrorCode,48,"");item.Scope=item.Scope=="device"?"device":"provider";item.DeviceCount=Math.Max(0,Math.Min(64,item.DeviceCount));health.Add(item);}snapshot.Health=health.ToArray();
            if(snapshot.DpiResult!=null){
                var result=snapshot.DpiResult;result.Error=Clean(result.Error,160,"");
                if(!Regex.IsMatch(result.DeviceId??"",@"^device-[a-f0-9]{24}$"))result.DeviceId=null;
                if(!WirelessDpi.ValidToken(result.TargetToken))result.TargetToken=null;
                if(result.ConfirmedDpi.HasValue&&!WirelessDpi.ValidValue(result.ConfirmedDpi.Value))result.ConfirmedDpi=null;
                if(result.DeviceId==null||result.TargetToken==null||!WirelessDpi.ValidValue(result.RequestedDpi)||result.Success&&(result.OutcomeUnknown||!String.IsNullOrEmpty(result.Error)||result.ConfirmedDpi!=result.RequestedDpi)){result.Success=false;result.OutcomeUnknown=true;result.Error="DPI操作返回内容未通过核对。";}
            }
            snapshot.Devices=devices.ToArray();return snapshot;
        }
        static string Clean(string value,int max,string fallback){var result=new StringBuilder();foreach(char c in value??"")if(!Char.IsControl(c)&&result.Length<max)result.Append(c);return result.Length==0?fallback:result.ToString();}
        internal void Reset(){lock(lifecycle){failedPackGeneration=generation;Stop();}}
        static bool TryTerminate(Process value){
            try{if(value.HasExited)return true;value.Kill();return value.WaitForExit(2000)||value.HasExited;}
            catch(InvalidOperationException){try{return value.HasExited;}catch(InvalidOperationException){return false;}catch(System.ComponentModel.Win32Exception){return false;}}
            catch(System.ComponentModel.Win32Exception){try{return value.HasExited;}catch(InvalidOperationException){return false;}catch(System.ComponentModel.Win32Exception){return false;}}
        }
        bool Stop() {lock(lifecycle){
            dpiLeases.Clear();
            if(process!=null){try{process.StandardInput.Close();}catch(InvalidOperationException){}catch(IOException){} }
            if(job!=IntPtr.Zero){CloseHandle(job);job=IntPtr.Zero;}
            if(process!=null){bool confirmed=false;try{confirmed=terminate(process);}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
                if(!confirmed){quarantined=true;return false;}process.Dispose();process=null;}
            quarantined=false;return true;
        }}
        public void Dispose(){lock(lifecycle){disposed=true;Stop();}}
    }
}
