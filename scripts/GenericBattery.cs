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
    [DataContract] public sealed class WirelessDevice {
        [DataMember(Name="id")] public string Id;
        [DataMember(Name="legacyId")] public string LegacyId;
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
        public DateTime ConfirmedTime {get{DateTime value;return DateTime.TryParse(ConfirmedAtUtc,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out value)?value.ToUniversalTime():DateTime.MinValue;}}
        public DateTime QueryTime {get{DateTime value;return DateTime.TryParse(QueriedAtUtc,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out value)?value.ToUniversalTime():DateTime.MinValue;}}
        internal bool SameCacheIdentity(WirelessDevice other){return other!=null&&Id==other.Id&&Kind==other.Kind&&Provider==other.Provider;}
        public string Key {get{return Id+"|"+Name+"|"+Percent+"|"+Charging+"|"+Online+"|"+Fresh+"|"+Approximate+"|"+ConfirmedAtUtc+"|"+SourceQuality+"|"+QueriedAtUtc;}}
    }
    [DataContract] public sealed class ReaderHealth {
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
    }
    internal sealed class GenericBatteryWorker:IDisposable {
        readonly string root;
        readonly bool smoke;
        readonly int queryBudget;
        readonly object lifecycle=new object();
        readonly object query=new object();
        int generation;long sequence;
        Process process;
        IntPtr job;
        bool disposed;
        internal GenericBatteryWorker(string engineRoot=null,bool packageSmoke=false,int timeoutMilliseconds=45000){root=engineRoot??Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","engine"));smoke=packageSmoke;queryBudget=Math.Max(100,Math.Min(45000,timeoutMilliseconds));}
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
            if(process!=null&&!process.HasExited)return process;
            Stop();
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
        internal WirelessSnapshot Poll(bool force=false,string deviceId=null,Action<WirelessSnapshot> progress=null) {lock(query){
            try {
                var child=Start();int currentGeneration=generation;long request=Interlocked.Increment(ref sequence);
                if(deviceId!=null&&!Regex.IsMatch(deviceId,@"^device-[a-f0-9]{24}$"))return new WirelessSnapshot {Error="无效的设备刷新目标。"};
                child.StandardInput.WriteLine("{\"command\":\"poll\",\"requestId\":"+request+",\"generation\":"+currentGeneration+",\"force\":"+(force?"true":"false")+(deviceId==null?"":",\"deviceId\":\""+deviceId+"\"")+"}");child.StandardInput.Flush();
                var clock=Stopwatch.StartNew();var reader=child.StandardOutput;
                for(int frames=0;frames<128;frames++){
                    var line=Task.Factory.StartNew(delegate{return AppRuntime.ReadLineBounded(reader,524288);});
                    if(!line.Wait(Math.Max(1,queryBudget-(int)clock.ElapsedMilliseconds))){Stop();return new WirelessSnapshot {Error="设备读取超时，保留最近数据。"};}
                    var snapshot=Parse(line.Result);if(!String.IsNullOrEmpty(snapshot.Error)){Stop();return snapshot;}
                    if(snapshot.Schema==1){snapshot.Complete=true;return snapshot;}
                    if(snapshot.RequestId!=request||snapshot.Generation!=currentGeneration)continue;
                    if(snapshot.Complete)return snapshot;if(progress!=null)progress(snapshot);
                    if(clock.ElapsedMilliseconds>=queryBudget)break;
                }
                Stop();return new WirelessSnapshot {Error="设备读取未能在预算内完成。"};
            }catch(Exception){Stop();return new WirelessSnapshot {Error="设备读取组件暂不可用，保留最近数据。"};}
        }}
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
                value.Name=Clean(value.Name,80,"无线设备");value.Provider=Clean(value.Provider,48,"");value.Transport=Clean(value.Transport,32,"");
                if(value.Kind!="mouse"&&value.Kind!="headset"&&value.Kind!="keyboard")value.Kind="device";
                if(value.Percent<0||value.Percent>100)value.Percent=null;
                DateTime time=value.ConfirmedTime;
                if(time==DateTime.MinValue||time>DateTime.UtcNow.AddSeconds(5)){value.Percent=null;value.Fresh=false;value.ConfirmedAtUtc=null;}
                if(!value.Online||!value.Percent.HasValue||(DateTime.UtcNow-time).TotalSeconds>90||value.SourceQuality=="system_property"||value.SourceQuality=="cached")value.Fresh=false;
                if(!value.Fresh)value.Charging=null;
                value.SourceQuality=Clean(value.SourceQuality,24,snapshot.Schema==1?"legacy":"unknown");value.MeasurementState=Clean(value.MeasurementState,16,"unknown");value.RefreshScope=value.RefreshScope=="device"?"device":"provider";
                value.ConnectionState=Clean(value.ConnectionState,24,snapshot.Schema==1&&value.Online?"connected":"unconfirmed");value.VerificationLevel=Clean(value.VerificationLevel,1,"C");value.VerificationLabel=Clean(value.VerificationLabel,64,"协议已接入，待型号实测");
                devices.Add(value);
            }
            var errors=new List<string>();foreach(var error in snapshot.Errors??new string[0])if(errors.Count<32)errors.Add(Clean(error,96,"read_failed"));snapshot.Errors=errors.ToArray();
            var health=new List<ReaderHealth>();foreach(var item in snapshot.Health??new ReaderHealth[0])if(item!=null&&health.Count<64){item.Provider=Clean(item.Provider,48,"unknown");item.State=Clean(item.State,24,"unknown");item.ErrorCode=Clean(item.ErrorCode,48,"");item.Scope=item.Scope=="device"?"device":"provider";item.DeviceCount=Math.Max(0,Math.Min(64,item.DeviceCount));health.Add(item);}snapshot.Health=health.ToArray();
            snapshot.Devices=devices.ToArray();return snapshot;
        }
        static string Clean(string value,int max,string fallback){var result=new StringBuilder();foreach(char c in value??"")if(!Char.IsControl(c)&&result.Length<max)result.Append(c);return result.Length==0?fallback:result.ToString();}
        internal void Reset(){Stop();}
        void Stop() {lock(lifecycle){
            if(process!=null){try{process.StandardInput.Close();}catch(InvalidOperationException){}catch(IOException){} }
            if(job!=IntPtr.Zero){CloseHandle(job);job=IntPtr.Zero;}
            if(process!=null){try{if(!process.HasExited)process.Kill();process.WaitForExit(2000);}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}process.Dispose();process=null;}
        }}
        public void Dispose(){lock(lifecycle){disposed=true;Stop();}}
    }
}
