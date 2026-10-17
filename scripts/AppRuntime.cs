using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AtkBattery {
    public static class AppRuntime {
        public static string Version {get{var values=Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute),false);return values.Length>0?((AssemblyInformationalVersionAttribute)values[0]).InformationalVersion:"unknown";}}
        internal static string ReadLineBounded(StreamReader reader,int limit){var result=new StringBuilder();for(;;){int c=reader.Read();if(c<0)return result.Length==0?null:result.ToString();if(c=='\n')return result.ToString();if(c=='\r')continue;if(result.Length>=limit)throw new InvalidDataException("Reader response exceeds its limit.");result.Append((char)c);}}
    }
    internal sealed class ChildProcessJob:IDisposable {
        IntPtr handle;
        [StructLayout(LayoutKind.Sequential)] struct BasicLimits {public long ProcessTime,JobTime;public uint Flags;public UIntPtr MinWorking,MaxWorking;public uint ActiveLimit;public UIntPtr Affinity;public uint Priority,Scheduling;}
        [StructLayout(LayoutKind.Sequential)] struct Counters {public ulong Read,Write,Other,ReadBytes,WriteBytes,OtherBytes;}
        [StructLayout(LayoutKind.Sequential)] struct ExtendedLimits {public BasicLimits Basic;public Counters Io;public UIntPtr ProcessMemory,JobMemory,PeakProcess,PeakJob;}
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateJobObject(IntPtr security,string name);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetInformationJobObject(IntPtr job,int kind,ref ExtendedLimits limits,uint length);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        internal ChildProcessJob(Process process){handle=CreateJobObject(IntPtr.Zero,null);var limits=new ExtendedLimits();limits.Basic.Flags=0x2000;if(handle==IntPtr.Zero||!SetInformationJobObject(handle,9,ref limits,(uint)Marshal.SizeOf(typeof(ExtendedLimits)))||!AssignProcessToJobObject(handle,process.Handle)){Dispose();try{if(!process.HasExited)process.Kill();}catch(InvalidOperationException){}throw new IOException("Unable to contain the device reader process.");}}
        public void Dispose(){IntPtr owned=Interlocked.Exchange(ref handle,IntPtr.Zero);if(owned!=IntPtr.Zero)CloseHandle(owned);}
    }
}
