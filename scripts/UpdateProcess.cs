using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace AtkBattery {
    internal sealed class UpdateProcess:IUpdateInstaller {
        readonly string executable,dataDirectory,root;
        internal UpdateProcess(string exe,string data){executable=Path.GetFullPath(exe);dataDirectory=Path.GetFullPath(data);root=Path.GetDirectoryName(Path.GetDirectoryName(executable));}
        internal string InstallDirectory {get{return root;}}
        internal static string Quote(string value){
            if(value==null||value.IndexOf('\0')>=0)throw new ArgumentException("Invalid update argument.");
            var result=new StringBuilder("\"");int slashes=0;
            foreach(char c in value){if(c=='\\'){slashes++;continue;}if(c=='"')result.Append('\\',slashes*2+1);else result.Append('\\',slashes);result.Append(c);slashes=0;}result.Append('\\',slashes*2);return result.Append('"').ToString();
        }
        internal static ProcessStartInfo HelperStartInfo(string script,IEnumerable<string> arguments){
            string system=Environment.GetFolderPath(Environment.SpecialFolder.System);
            var info=new ProcessStartInfo(Path.Combine(system,@"WindowsPowerShell\v1.0\powershell.exe"));
            var command=new StringBuilder("-NoProfile -ExecutionPolicy Bypass -File "+Quote(Path.GetFullPath(script)));
            foreach(string arg in arguments)command.Append(' ').Append(Quote(arg));
            info.Arguments=command.ToString();info.UseShellExecute=false;info.CreateNoWindow=true;info.WindowStyle=ProcessWindowStyle.Hidden;return info;
        }
        public bool IsStable(CancellationToken token){
            token.ThrowIfCancellationRequested();string script=Path.Combine(root,@"scripts\Apply-Update.ps1");if(!File.Exists(script))return false;
            // Negative prefilter only: ordinary portable copies never start even a probe process.
            // A matching location still requires the installer's complete receipt/marker decision.
            try{
                string receipt=Path.Combine(dataDirectory,"install.json");if(!File.Exists(receipt)||!File.Exists(Path.Combine(root,".myfinder-device-battery-install.json")))return false;
                UpdatePaths.NoLinks(receipt);var properties=UpdateJson.Read<Dictionary<string,object>>(UpdateJson.ReadBytes(receipt,2097152),true);
                string location=null;int matches=0;foreach(var property in properties)if(property.Key.Equals("InstallDirectory",StringComparison.OrdinalIgnoreCase)){location=property.Value as string;matches++;}
                if(matches!=1||String.IsNullOrWhiteSpace(location)||!Path.IsPathRooted(location)||!Path.GetFullPath(location).TrimEnd('\\').Equals(root.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))return false;
            }catch(Exception){return false;}
            var info=HelperStartInfo(script,new[]{"-Probe","-ExecutablePath",executable,"-DataDirectory",dataDirectory});info.RedirectStandardOutput=true;
            using(var process=Process.Start(info)){
                var read=process.StandardOutput.ReadToEndAsync();var clock=Stopwatch.StartNew();
                while(!process.WaitForExit(100)){if(token.IsCancellationRequested||clock.Elapsed.TotalSeconds>30){try{process.Kill();}catch(InvalidOperationException){}token.ThrowIfCancellationRequested();return false;}}
                return read.Wait(1000)&&process.ExitCode==0&&read.Result=="stable";
            }
        }
        public void Start(PreparedUpdate update){
            if(update.InstallDirectory!=root)throw new InvalidDataException("Update target changed.");
            string helper=Path.Combine(update.WorkDirectory,"helper");Directory.CreateDirectory(helper);
            // The currently installed helper survives the replacement. The downloaded installer is
            // executed only after the old binary independently re-verifies the signature and ZIP.
            foreach(string name in new[]{"Apply-Update.ps1","Update.Common.ps1","Install.Common.ps1","Manifest.Common.ps1"}){
                string source=Path.Combine(root,"scripts",name);UpdatePaths.NoLinks(source);File.Copy(source,Path.Combine(helper,name),false);
            }
            using(var parent=Process.GetCurrentProcess()){
                var info=HelperStartInfo(Path.Combine(helper,"Apply-Update.ps1"),new[]{"-ExecutablePath",executable,"-DataDirectory",dataDirectory,"-Version",update.Version,"-WorkDirectory",update.WorkDirectory,"-ParentId",parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),"-ParentStartUtc",parent.StartTime.ToUniversalTime().ToString("o")});
                using(var process=Process.Start(info)){if(process==null)throw new IOException("Update helper could not start.");}
            }
        }
        internal void RequestExit(){
            using(var process=Process.Start(new ProcessStartInfo(executable,"--exit") {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden}))if(process==null)throw new IOException("The normal exit request could not start.");
        }
    }
}
