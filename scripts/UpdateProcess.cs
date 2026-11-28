using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
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
            info.Arguments=command.ToString();info.UseShellExecute=false;info.CreateNoWindow=true;info.WindowStyle=ProcessWindowStyle.Hidden;
            // Windows PowerShell must discover its own modules, even when the app came from pwsh.
            info.EnvironmentVariables.Remove("PSModulePath");return info;
        }
        internal static bool HasMatchingStableReceipt(string executablePath,string dataDirectory){
            try{
                if(String.IsNullOrWhiteSpace(executablePath)||String.IsNullOrWhiteSpace(dataDirectory))return false;
                string exe=Path.GetFullPath(executablePath);
                string outputs=Path.GetDirectoryName(exe);
                if(String.IsNullOrEmpty(outputs))return false;
                string installRoot=Path.GetDirectoryName(outputs);
                if(String.IsNullOrEmpty(installRoot))return false;
                if(!File.Exists(Path.Combine(installRoot,@"scripts\Apply-Update.ps1")))return false;
                string receipt=Path.Combine(dataDirectory,"install.json");
                if(!File.Exists(receipt)||!File.Exists(Path.Combine(installRoot,".myfinder-device-battery-install.json")))return false;
                UpdatePaths.NoLinks(receipt);
                var properties=UpdateJson.Read<Dictionary<string,object>>(UpdateJson.ReadBytes(receipt,2097152),true);
                string location=null;int matches=0;
                foreach(var property in properties)if(property.Key.Equals("InstallDirectory",StringComparison.OrdinalIgnoreCase)){location=property.Value as string;matches++;}
                return matches==1&&!String.IsNullOrWhiteSpace(location)&&Path.IsPathRooted(location)&&Path.GetFullPath(location).TrimEnd('\\').Equals(installRoot.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase);
            }catch(Exception){return false;}
        }
        public bool IsStable(CancellationToken token){
            token.ThrowIfCancellationRequested();string script=Path.Combine(root,@"scripts\Apply-Update.ps1");if(!File.Exists(script))return false;
            // Negative prefilter only: ordinary portable copies never start even a probe process.
            // A matching location still requires the installer's complete receipt/marker decision.
            if(!HasMatchingStableReceipt(executable,dataDirectory))return false;
            var info=HelperStartInfo(script,new[]{"-Probe","-ExecutablePath",executable,"-DataDirectory",dataDirectory});info.RedirectStandardOutput=true;
            using(var process=Process.Start(info)){
                var read=process.StandardOutput.ReadToEndAsync();var clock=Stopwatch.StartNew();
                while(!process.WaitForExit(100)){if(token.IsCancellationRequested||clock.Elapsed.TotalSeconds>30){try{process.Kill();}catch(InvalidOperationException){}token.ThrowIfCancellationRequested();return false;}}
                return read.Wait(1000)&&process.ExitCode==0&&read.Result=="stable";
            }
        }
        static readonly string[] HelperScriptNames={"Apply-Update.ps1","Update.Common.ps1","Install.Common.ps1","Manifest.Common.ps1"};
        internal static void VerifyInstalledHelperScripts(string installRoot,string scriptsDirectory=null){
            string manifestPath=Path.Combine(installRoot,"package-manifest.json");
            if(!File.Exists(manifestPath))return;
            UpdatePaths.NoLinks(manifestPath);
            var manifest=UpdateJson.ReadFile<UpdateManifest>(manifestPath,2097152);
            if(manifest==null||manifest.Files==null)throw new InvalidDataException("Installed package manifest is invalid.");
            var byPath=new Dictionary<string,UpdateManifestFile>(StringComparer.OrdinalIgnoreCase);
            foreach(var entry in manifest.Files)if(entry!=null&&!String.IsNullOrEmpty(entry.Path))byPath[entry.Path]=entry;
            string verifyDir=String.IsNullOrWhiteSpace(scriptsDirectory)?Path.Combine(installRoot,"scripts"):scriptsDirectory;
            UpdatePaths.NoLinks(verifyDir);
            foreach(string name in HelperScriptNames){
                string relative="scripts/"+name;
                UpdateManifestFile expected;
                if(!byPath.TryGetValue(relative,out expected))throw new InvalidDataException("Installed manifest omits required update helper script.");
                string target=Path.Combine(verifyDir,name);
                UpdatePaths.NoLinks(target);
                if(!File.Exists(target)||new FileInfo(target).Length!=expected.Length||!UpdatePackageVerifier.HashFile(target).Equals(expected.Sha256,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Installed update helper script failed integrity check.");
            }
        }
        static void CreateProtectedDirectory(string directory){
            UpdatePaths.NoLinks(directory);
            var security=new DirectorySecurity();
            security.SetAccessRuleProtection(true,false);
            var user=WindowsIdentity.GetCurrent().User;
            security.AddAccessRule(new FileSystemAccessRule(user,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            Directory.CreateDirectory(directory,security);
        }
        public void Start(PreparedUpdate update){
            string target=String.IsNullOrWhiteSpace(update.InstallDirectory)?root:UpdateInstallPaths.ValidateTargetOrThrow(update.InstallDirectory,root,dataDirectory,true);
            UpdatePaths.NoLinks(update.WorkDirectory);
            string helper=Path.Combine(update.WorkDirectory,"helper");
            CreateProtectedDirectory(helper);
            // The currently installed helper survives the replacement. Copy into the
            // user-only ACL helper directory first, then verify hashes of the exact
            // copied scripts before launching PowerShell (eliminating copy->verify TOCTOU).
            foreach(string name in HelperScriptNames){
                string source=Path.Combine(root,"scripts",name);UpdatePaths.NoLinks(source);File.Copy(source,Path.Combine(helper,name),false);
            }
            VerifyInstalledHelperScripts(root,helper);
            using(var parent=Process.GetCurrentProcess()){
                var args=new List<string>{"-ExecutablePath",executable,"-DataDirectory",dataDirectory,"-Version",update.Version,"-WorkDirectory",update.WorkDirectory,"-ParentId",parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),"-ParentStartUtc",parent.StartTime.ToUniversalTime().ToString("o")};
                if(!UpdateInstallPaths.IsSameDirectory(target,root)){args.Add("-TargetInstallDirectory");args.Add(target);}
                var info=HelperStartInfo(Path.Combine(helper,"Apply-Update.ps1"),args);
                using(var process=Process.Start(info)){if(process==null)throw new IOException("Update helper could not start.");}
            }
        }
        internal void RequestExit(){
            using(var process=Process.Start(new ProcessStartInfo(executable,"--exit") {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden}))if(process==null)throw new IOException("The normal exit request could not start.");
        }
    }
}
