using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AtkBattery {
    [DataContract] internal sealed class AudioCommand {
        [DataMember] internal long Id;
        [DataMember] internal string Operation;
        [DataMember] internal string Target;
        [DataMember] internal string Endpoint;
        [DataMember] internal int Percent;
        [DataMember] internal bool Mute;
        [DataMember] internal AudioBindings Settings;
    }
    [DataContract] internal sealed class AudioResponse {
        [DataMember] internal long Id;
        [DataMember] internal AudioReading Reading;
    }
    internal sealed class AudioBrokerClient:IDisposable {
        static readonly AudioBrokerClient shared=new AudioBrokerClient();
        readonly object gate=new object();readonly string executable;
        readonly int testBudget;
        Process process;ChildProcessJob job;long sequence;volatile bool disposed;bool quarantined;
        internal AudioBrokerClient(string executablePath=null,int timeoutMilliseconds=0){executable=executablePath??System.Reflection.Assembly.GetExecutingAssembly().Location;testBudget=timeoutMilliseconds;}
        internal static AudioReading Read(){return shared.Query(new AudioCommand {Operation="read"});}
        internal static AudioReading Switch(string target){return shared.Query(new AudioCommand {Operation="output",Target=target});}
        internal static AudioReading Mute(bool value){return shared.Query(new AudioCommand {Operation="mute",Mute=value});}
        internal static AudioReading Volume(string endpoint,int value){return shared.Query(new AudioCommand {Operation="volume",Endpoint=endpoint,Percent=value});}
        internal static void Shutdown(){shared.disposed=true;var owned=Interlocked.Exchange(ref shared.job,null);if(owned!=null)owned.Dispose();ThreadPool.QueueUserWorkItem(delegate{shared.Dispose();});}
        internal AudioReading Query(AudioCommand command){lock(gate){
            if(disposed)return Unknown("音频组件已退出。",false);
            try{
                if(quarantined&&!Stop())return Unknown("上次音频进程仍未退出，控制继续停用。",true);
                if(process!=null&&process.HasExited){process.Dispose();process=null;if(job!=null){job.Dispose();job=null;}}
                if(process==null){
                    var info=new ProcessStartInfo(executable,"--audio-broker") {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
                    process=Process.Start(info);process.ErrorDataReceived+=delegate{};process.BeginErrorReadLine();job=new ChildProcessJob(process);
                }
                command.Id=++sequence;command.Settings=DeviceSettings.Current;process.StandardInput.WriteLine(Encode(command));process.StandardInput.Flush();
                var input=process.StandardOutput;var line=Task.Factory.StartNew(delegate{return AppRuntime.ReadLineBounded(input,262144);});
                if(!line.Wait(testBudget>0?testBudget:command.Operation=="read"?3500:6000)){bool stopped=Stop();return Unknown(stopped?"音频响应超时，结果未确认；正在重新核对。":"音频操作仍未退出，暂停新的控制请求。",command.Operation!="read");}
                var response=Decode<AudioResponse>(line.Result);if(response==null||response.Id!=command.Id||response.Reading==null){Stop();return Unknown("音频返回结果无效，正在重新核对。",command.Operation!="read");}
                return response.Reading;
            }catch(Exception){Stop();return Unknown("音频组件暂不可用，结果未确认。",command.Operation!="read");}
        }}
        static AudioReading Unknown(string message,bool outcome){return new AudioReading {Error=message,OutcomeUnknown=outcome,OperationSucceeded=false,TimestampUtc=DateTime.UtcNow};}
        bool Stop(){
            if(job!=null){job.Dispose();job=null;}
            if(process==null){quarantined=false;return true;}
            try{if(!process.HasExited)process.Kill();if(!process.WaitForExit(2500)){quarantined=true;return false;}}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){quarantined=true;return false;}
            process.Dispose();process=null;quarantined=false;return true;
        }
        internal static string Encode<T>(T value){using(var output=new MemoryStream()){new DataContractJsonSerializer(typeof(T)).WriteObject(output,value);return Encoding.UTF8.GetString(output.ToArray());}}
        internal static T Decode<T>(string text){if(String.IsNullOrEmpty(text)||text.Length>262144)return default(T);using(var input=new MemoryStream(Encoding.UTF8.GetBytes(text)))return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(input);}
        internal static void RunServer(){
            using(var input=new StreamReader(Console.OpenStandardInput(),Encoding.UTF8))using(var output=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false)) {AutoFlush=true}){
                string line;while((line=AppRuntime.ReadLineBounded(input,131072))!=null){
                    var command=Decode<AudioCommand>(line);if(command==null)break;if(command.Operation=="exit")break;
                    AudioReading result;
                    try{
                        DeviceSettings.UseTransientConfiguration(command.Settings??new AudioBindings());
                        if(command.Operation=="read")result=AudioControl.Read();
                        else if(command.Operation=="output")result=AudioControl.SwitchOutput(command.Target);
                        else if(command.Operation=="mute")result=AudioControl.SetHeadsetMicrophoneMute(command.Mute);
                        else if(command.Operation=="volume")result=AudioControl.SetOutputVolume(command.Endpoint,command.Percent);
                        else result=Unknown("无效的音频请求。",false);
                    }catch(Exception){result=Unknown("音频请求失败。",command.Operation!="read");}
                    output.WriteLine(Encode(new AudioResponse {Id=command.Id,Reading=result}));
                }
            }
        }
        public void Dispose(){lock(gate){disposed=true;Stop();}}
    }
}
