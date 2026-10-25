using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace AtkBattery {
    internal static class AtomicText {
        internal static void Write(string path,string text){
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{File.WriteAllText(temp,text,new UTF8Encoding(false));if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}
            finally{try{if(File.Exists(temp))File.Delete(temp);}catch(IOException){}catch(UnauthorizedAccessException){}}
        }
    }
    internal sealed class SnapshotHealth {
        internal DateTime LastAttemptUtc,LastSuccessUtc,LastFailureUtc;
        internal long FailureCount;
        internal string ErrorCode;
        internal bool InFlight,Pending;
        internal string Describe(DateTime now){
            if(InFlight&&LastAttemptUtc!=DateTime.MinValue&&(now-LastAttemptUtc).TotalSeconds>=5)return "写入响应较慢";
            if(!String.IsNullOrEmpty(ErrorCode))return ErrorCode=="access_denied"?"保存失败：访问被拒绝":"保存失败：文件写入异常";
            return LastSuccessUtc!=DateTime.MinValue?"正常":InFlight||Pending?"正在保存":"尚未保存";
        }
    }
    internal sealed class SnapshotWriter:IDisposable {
        sealed class Item {internal string Text;internal int Retries;internal Item(string text){Text=text;}}
        readonly object gate=new object();
        readonly Dictionary<string,Item> pending=new Dictionary<string,Item>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,string> written=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,SnapshotHealth> health=new Dictionary<string,SnapshotHealth>(StringComparer.OrdinalIgnoreCase);
        readonly Action<string,string> write;
        readonly ManualResetEvent idle=new ManualResetEvent(true);
        string writingPath;bool running,disposed;
        internal SnapshotWriter(Action<string,string> writer=null){write=writer??AtomicText.Write;}
        internal void Enqueue(string path,string text){lock(gate){
            if(disposed||!health.ContainsKey(path)&&health.Count>=16)return;
            if(!health.ContainsKey(path))health[path]=new SnapshotHealth();
            string old;
            // A previous committed A cannot suppress a new A while B is in flight.
            if(!String.Equals(writingPath,path,StringComparison.OrdinalIgnoreCase)&&written.TryGetValue(path,out old)&&old==text&&!pending.ContainsKey(path)&&String.IsNullOrEmpty(health[path].ErrorCode))return;
            pending[path]=new Item(text);
            if(!running){running=true;idle.Reset();ThreadPool.QueueUserWorkItem(delegate{Run();});}
        }}
        internal SnapshotHealth GetHealth(string path){lock(gate){
            SnapshotHealth value;if(!health.TryGetValue(path,out value))return new SnapshotHealth();
            return new SnapshotHealth {LastAttemptUtc=value.LastAttemptUtc,LastSuccessUtc=value.LastSuccessUtc,LastFailureUtc=value.LastFailureUtc,FailureCount=value.FailureCount,ErrorCode=value.ErrorCode,InFlight=String.Equals(path,writingPath,StringComparison.OrdinalIgnoreCase),Pending=pending.ContainsKey(path)};
        }}
        void Run(){Thread.Sleep(100);for(;;){
            string path=null;Item item=null;
            lock(gate){foreach(var entry in pending){path=entry.Key;item=entry.Value;break;}if(path==null){writingPath=null;running=false;idle.Set();return;}pending.Remove(path);writingPath=path;health[path].LastAttemptUtc=DateTime.UtcNow;}
            string failure=null;
            try{write(path,item.Text);}catch(UnauthorizedAccessException){failure="access_denied";}catch(System.Security.SecurityException){failure="access_denied";}catch(IOException){failure="io_error";}catch(ArgumentException){failure="invalid_path";}catch(NotSupportedException){failure="invalid_path";}catch(Exception){failure="write_error";}
            int retryDelay=0;
            lock(gate){
                var state=health[path];writingPath=null;
                if(failure==null){written[path]=item.Text;state.LastSuccessUtc=DateTime.UtcNow;state.ErrorCode=null;}
                else {state.FailureCount++;state.LastFailureUtc=DateTime.UtcNow;state.ErrorCode=failure;
                    // Do not overwrite a newer queued value, or spawn unbounded retries.
                    if(!disposed&&!pending.ContainsKey(path)&&item.Retries<2){item.Retries++;pending[path]=item;retryDelay=100*item.Retries;}
                }
            }
            if(retryDelay>0)Thread.Sleep(retryDelay);
        }}
        internal bool Flush(int milliseconds){return idle.WaitOne(milliseconds);}
        internal void Forget(string path){lock(gate){if(String.Equals(writingPath,path,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Flush pending writes before clearing history.");pending.Remove(path);written.Remove(path);health.Remove(path);}}
        public void Dispose(){lock(gate){if(disposed)return;disposed=true;}if(Flush(2000))idle.Dispose();}
    }
    internal static class SafeDiagnostic {
        static readonly object gate=new object();static readonly Dictionary<string,string> last=new Dictionary<string,string>();
        internal static string Clean(string value){value=Regex.Replace(value??"",@"(?i)(?:[a-z]:\\|\\\\\?\\)[^\r\n ]+","[path]");value=Regex.Replace(value,@"(?i)\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b","[device]");return value.Replace("\r"," ").Replace("\n"," ");}
        internal static void Transition(string category,string state){state=Clean(state);lock(gate){string old;if(last.TryGetValue(category,out old)&&old==state)return;last[category]=state;
            try{string path=Path.Combine(DeviceSettings.DataDirectory,"diagnostics.log");Directory.CreateDirectory(DeviceSettings.DataDirectory);if(File.Exists(path)&&new FileInfo(path).Length>1048576){string two=path+".2",one=path+".1";if(File.Exists(two))File.Delete(two);if(File.Exists(one))File.Move(one,two);File.Move(path,one);}File.AppendAllText(path,DateTime.UtcNow.ToString("o")+" "+category+" "+state.Substring(0,Math.Min(320,state.Length))+Environment.NewLine,new UTF8Encoding(false));}catch(IOException){}catch(UnauthorizedAccessException){}
        }}
    }
    public static class StartupDiagnostics {
        static DateTime checkedAt;static string cached;
        public static string Describe(){if(cached!=null&&(DateTime.UtcNow-checkedAt).TotalSeconds<5)return cached;checkedAt=DateTime.UtcNow;
            string name="MyFinder 设备电量.lnk",path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),name);if(!File.Exists(path))return cached="未设置";
            object shell=null,link=null;
            try{var type=Type.GetTypeFromProgID("WScript.Shell");if(type==null)return cached="无法确认";shell=Activator.CreateInstance(type);link=type.InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[]{path});var t=link.GetType();string target=Convert.ToString(t.InvokeMember("TargetPath",BindingFlags.GetProperty,null,link,null)),args=Convert.ToString(t.InvokeMember("Arguments",BindingFlags.GetProperty,null,link,null));
                byte[] approval=null;using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder"))if(key!=null){object raw=key.GetValue(name);if(raw!=null&&!(raw is byte[]))return cached="无法确认";approval=raw as byte[];}
                return cached=Evaluate(target,args,Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"AtkMouseBattery.exe"),approval);
            }catch(Exception){return cached="无法确认";}finally{if(link!=null&&Marshal.IsComObject(link))Marshal.FinalReleaseComObject(link);if(shell!=null&&Marshal.IsComObject(shell))Marshal.FinalReleaseComObject(shell);}
        }
        internal static string Evaluate(string target,string args,string expected,byte[] approval){
            if(String.IsNullOrWhiteSpace(target)||!File.Exists(target)||!String.Equals(Path.GetFullPath(target),Path.GetFullPath(expected),StringComparison.OrdinalIgnoreCase)||!String.IsNullOrEmpty(args))return "目标失效";
            if(approval!=null){if(approval.Length==0||approval[0]!=2&&approval[0]!=3&&approval[0]!=6&&approval[0]!=7)return "无法确认";if(approval[0]==3||approval[0]==7)return "Windows 已禁用";}return "已启用";
        }
    }
}
