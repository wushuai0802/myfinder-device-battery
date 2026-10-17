using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AtkBattery {
    [DataContract] public sealed class WirelessDevice {
        [DataMember(Name="id")] public string Id;
        [DataMember(Name="name")] public string Name;
        [DataMember(Name="kind")] public string Kind;
        [DataMember(Name="percent")] public int? Percent;
        [DataMember(Name="charging")] public bool Charging;
        [DataMember(Name="online")] public bool Online;
        [DataMember(Name="fresh")] public bool Fresh;
        [DataMember(Name="approximate")] public bool Approximate;
        [DataMember(Name="provider")] public string Provider;
        [DataMember(Name="transport")] public string Transport;
        [DataMember(Name="confirmedAtUtc")] public string ConfirmedAtUtc;
        public DateTime ConfirmedTime {get{DateTime value;return DateTime.TryParse(ConfirmedAtUtc,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out value)?value.ToUniversalTime():DateTime.MinValue;}}
        public string Key {get{return Id+"|"+Name+"|"+Percent+"|"+Charging+"|"+Online+"|"+Fresh+"|"+Approximate+"|"+ConfirmedAtUtc;}}
    }
    [DataContract] public sealed class WirelessSnapshot {
        [DataMember(Name="schema")] public int Schema;
        [DataMember(Name="devices")] public WirelessDevice[] Devices;
        [DataMember(Name="nativeMousePresent")] public bool? NativeMousePresent;
        [DataMember(Name="providerCount")] public int ProviderCount;
        [DataMember(Name="errors")] public string[] Errors;
        [DataMember(Name="error")] public string Error;
    }
    internal sealed class GenericBatteryWorker:IDisposable {
        readonly string root;
        readonly object lifecycle=new object();
        Process process;
        IntPtr job;
        bool disposed;
        internal GenericBatteryWorker(string engineRoot=null){root=engineRoot??Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","engine"));}
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
            var info=new ProcessStartInfo(Path.Combine(root,"runtime","python.exe"),"-I -B -u \""+Path.Combine(root,"server.py")+"\"") {
                WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
                RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8
            };
            process=Process.Start(info);process.ErrorDataReceived+=delegate{};process.BeginErrorReadLine();
            job=CreateJobObject(IntPtr.Zero,null);
            if(job!=IntPtr.Zero){var limits=new ExtendedLimits();limits.Basic.Flags=0x2000;
                if(!SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf(typeof(ExtendedLimits)))||!AssignProcessToJobObject(job,process.Handle)){CloseHandle(job);job=IntPtr.Zero;}}
            return process;
        }}
        internal WirelessSnapshot Poll(bool force=false) {
            try {
                var child=Start();child.StandardInput.WriteLine("{\"command\":\"poll\",\"force\":"+(force?"true":"false")+"}");child.StandardInput.Flush();
                var line=Task.Factory.StartNew(delegate{return child.StandardOutput.ReadLine();});
                if(!line.Wait(80000)){Stop();return new WirelessSnapshot {Error="设备读取超时，保留最近数据。"};}
                return Parse(line.Result);
            }catch(Exception){Stop();return new WirelessSnapshot {Error="设备读取组件暂不可用，保留最近数据。"};}
        }
        internal static WirelessSnapshot Parse(string json) {
            if(String.IsNullOrEmpty(json)||json.Length>524288)return new WirelessSnapshot {Error="无效的设备读取结果。"};
            WirelessSnapshot snapshot;
            try {using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(json)))snapshot=(WirelessSnapshot)new DataContractJsonSerializer(typeof(WirelessSnapshot),new DataContractJsonSerializerSettings {MaxItemsInObjectGraph=4096}).ReadObject(stream);}
            catch(SerializationException){return new WirelessSnapshot {Error="设备读取结果格式无效。"};}
            if(snapshot==null||snapshot.Schema!=1||!String.IsNullOrEmpty(snapshot.Error))return new WirelessSnapshot {Error="设备读取尚未完成。"};
            var devices=new List<WirelessDevice>();var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var value in snapshot.Devices??new WirelessDevice[0]) {
                if(devices.Count>=64)break;
                if(value==null||!Regex.IsMatch(value.Id??"",@"^device-[a-f0-9]{24}$")||!seen.Add(value.Id))continue;
                value.Name=Clean(value.Name,80,"无线设备");value.Provider=Clean(value.Provider,48,"");value.Transport=Clean(value.Transport,32,"");
                if(value.Kind!="mouse"&&value.Kind!="headset"&&value.Kind!="keyboard")value.Kind="device";
                if(value.Percent<0||value.Percent>100)value.Percent=null;
                DateTime time=value.ConfirmedTime;
                if(time==DateTime.MinValue||time>DateTime.UtcNow.AddSeconds(5)){value.Percent=null;value.Fresh=false;value.ConfirmedAtUtc=null;}
                if(!value.Online||!value.Percent.HasValue||(DateTime.UtcNow-time).TotalSeconds>90)value.Fresh=false;
                if(!value.Fresh)value.Charging=false;
                devices.Add(value);
            }
            snapshot.Devices=devices.ToArray();return snapshot;
        }
        static string Clean(string value,int max,string fallback){var result=new StringBuilder();foreach(char c in value??"")if(!Char.IsControl(c)&&result.Length<max)result.Append(c);return result.Length==0?fallback:result.ToString();}
        void Stop() {lock(lifecycle){
            if(process!=null){try{process.StandardInput.Close();}catch(InvalidOperationException){}catch(IOException){} }
            if(job!=IntPtr.Zero){CloseHandle(job);job=IntPtr.Zero;}
            if(process!=null){try{if(!process.HasExited)process.Kill();}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}process.Dispose();process=null;}
        }}
        public void Dispose(){lock(lifecycle){disposed=true;Stop();}}
    }
}
