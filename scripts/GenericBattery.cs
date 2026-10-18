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
        [DataMember(Name="kind")] public string Kind;
        [DataMember(Name="percent")] public int? Percent;
        [DataMember(Name="charging")] public bool? Charging;
        [DataMember(Name="online")] public bool Online;
        [DataMember(Name="fresh")] public bool Fresh;
        [DataMember(Name="approximate")] public bool Approximate;
        [DataMember(Name="provider")] public string Provider;
        [DataMember(Name="transport")] public string Transport;
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
    [DataContract] public sealed class WirelessSnapshot {
        [DataMember(Name="schema")] public int Schema;
        [DataMember(Name="requestId")] public long RequestId;
        [DataMember(Name="generation")] public int Generation;
        [DataMember(Name="complete")] public bool Complete;
        [DataMember(Name="devices")] public WirelessDevice[] Devices;
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
        sealed class DpiLease {internal string Token;internal int Current;internal int[] Stages;internal DateTime Checked;internal int Generation;}
        readonly Dictionary<string,DpiLease> dpiLeases=new Dictionary<string,DpiLease>(StringComparer.Ordinal);
        internal GenericBatteryWorker(string engineRoot=null,bool packageSmoke=false,int timeoutMilliseconds=45000,Func<Process,bool> terminateProcess=null){root=engineRoot??Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","engine"));smoke=packageSmoke;queryBudget=Math.Max(100,Math.Min(45000,timeoutMilliseconds));terminate=terminateProcess??TryTerminate;}
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
            if(!Stop())throw new IOException("上次设备读取进程仍未退出，控制暂时停用。");
            if(!Available)throw new IOException("请使用带设备读取组件的完整发行包。");
            var info=new ProcessStartInfo(Path.Combine(root,"runtime","python.exe"),"-I -B -u \""+Path.Combine(root,"server.py")+"\""+(smoke?" --package-smoke":"")) {
                WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
                RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8
            };
            process=Process.Start(info);process.ErrorDataReceived+=delegate{};process.BeginErrorReadLine();
            job=CreateJobObject(IntPtr.Zero,null);
            if(job!=IntPtr.Zero){var limits=new ExtendedLimits();limits.Basic.Flags=0x2000;
                if(!SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf(typeof(ExtendedLimits)))||!AssignProcessToJobObject(job,process.Handle)){Stop();throw new IOException("Reader process lifetime protection failed.");}}
            else{Stop();throw new IOException("Reader process lifetime protection failed.");}
            generation++;
            return process;
        }}
        internal WirelessSnapshot Poll(bool force=false,string deviceId=null,Action<WirelessSnapshot> progress=null,bool parkEmptyReaders=false,string physicalId=null) {lock(query){
            try {
                if(deviceId!=null&&physicalId!=null||deviceId!=null&&!Regex.IsMatch(deviceId,@"^device-[a-f0-9]{24}$")||physicalId!=null&&!Regex.IsMatch(physicalId,@"^device-[a-f0-9]{24}$"))return new WirelessSnapshot {Error="无效或冲突的设备刷新目标。"};
                var child=Start();int currentGeneration=generation;long request=Interlocked.Increment(ref sequence);
                child.StandardInput.WriteLine("{\"command\":\"poll\",\"requestId\":"+request+",\"generation\":"+currentGeneration+",\"force\":"+(force?"true":"false")+",\"parkEmptyReaders\":"+(parkEmptyReaders?"true":"false")+(deviceId==null?"":",\"deviceId\":\""+deviceId+"\"")+(physicalId==null?"":",\"physicalId\":\""+physicalId+"\"")+"}");child.StandardInput.Flush();
                return Receive(child,request,currentGeneration,progress,null);
            }catch(Exception){Stop();return new WirelessSnapshot {Error="设备读取组件暂不可用，保留最近数据。"};}
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
                return Receive(child,request,currentGeneration,null,write);
            }catch(Exception){Stop();return DpiFailure(write,attempted?"DPI操作结果未确认，请先同步；未自动重试。":"DPI控制暂不可用，请先同步。",attempted);}
        }}
        WirelessSnapshot Receive(Process child,long request,int currentGeneration,Action<WirelessSnapshot> progress,WirelessDpiResult write){
            var clock=Stopwatch.StartNew();var reader=child.StandardOutput;
            for(int frames=0;frames<128;frames++){
                var line=Task.Factory.StartNew(delegate{return AppRuntime.ReadLineBounded(reader,524288);});
                if(!line.Wait(Math.Max(1,queryBudget-(int)clock.ElapsedMilliseconds))){Stop();return write==null?new WirelessSnapshot {Error="设备读取超时，保留最近数据。"}:DpiFailure(write,"DPI响应超时，结果未确认；未自动重试。",true);}
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
                if(clock.ElapsedMilliseconds>=queryBudget)break;
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
            try {using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(json)))snapshot=(WirelessSnapshot)new DataContractJsonSerializer(typeof(WirelessSnapshot),new DataContractJsonSerializerSettings {MaxItemsInObjectGraph=4096}).ReadObject(stream);}
            catch(SerializationException){return new WirelessSnapshot {Error="设备读取结果格式无效。"};}
            if(snapshot==null||(snapshot.Schema!=1&&snapshot.Schema!=2)||!String.IsNullOrEmpty(snapshot.Error))return new WirelessSnapshot {Error="设备读取尚未完成。"};
            var devices=new List<WirelessDevice>();var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var value in snapshot.Devices??new WirelessDevice[0]) {
                if(devices.Count>=64)break;
                if(value==null||!Regex.IsMatch(value.Id??"",@"^device-[a-f0-9]{24}$")||!seen.Add(value.Id))continue;
                if(!Regex.IsMatch(value.LegacyId??"",@"^device-[a-f0-9]{24}$"))value.LegacyId=null;
                if(!Regex.IsMatch(value.PhysicalId??"",@"^device-[a-f0-9]{24}$"))value.PhysicalId=null;
                value.Name=Clean(value.Name,80,"无线设备");value.Provider=Clean(value.Provider,48,"");value.Transport=Clean(value.Transport,32,"");
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
                    if(snapshot.Schema!=2||value.Kind!="mouse")dpi.Supported=false;
                    dpi.CanSet=dpi.CanSet&&snapshot.RequestId>0&&snapshot.Generation>0&&validStages&&dpi.CanWrite(value,dpi.TargetToken);
                    if(!dpi.CanSet)DisableDpi(dpi);
                }
                devices.Add(value);
            }
            var errors=new List<string>();foreach(var error in snapshot.Errors??new string[0])if(errors.Count<32)errors.Add(Clean(error,96,"read_failed"));snapshot.Errors=errors.ToArray();
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
        internal void Reset(){Stop();}
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
