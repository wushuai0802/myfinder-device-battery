using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using AtkBattery;

static class RuntimeTests {
    static int checks;
    static void Check(bool value,string name){if(!value)throw new Exception("FAIL "+name);checks++;}
    static bool Gone(int pid){try{using(var p=Process.GetProcessById(pid))return p.HasExited;}catch(ArgumentException){return true;}}
    static int Field(string line,string field){return Int32.Parse(Regex.Match(line,"\""+field+"\":([0-9]+)").Groups[1].Value);}
    static void FakeWorker(){string line;while((line=Console.ReadLine())!=null){if(line.Contains("exit"))return;int id=Field(line,"requestId"),generation=Field(line,"generation");
        if(AppDomain.CurrentDomain.BaseDirectory.Contains("hung")){Thread.Sleep(30000);return;}
        int child=0;if(AppDomain.CurrentDomain.BaseDirectory.Contains("descendant")){var p=Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--sleep") {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});child=p.Id;p.Dispose();}
        string body="\"schema\":2,\"generation\":"+generation+",\"devices\":[],\"providerCount\":26,\"errors\":[\"childpid:"+child+"\"]";
        Console.WriteLine("{"+body+",\"requestId\":"+(id+100)+",\"complete\":true}");Console.WriteLine("{"+body+",\"requestId\":"+id+",\"complete\":false}");Console.WriteLine("{"+body+",\"requestId\":"+id+",\"complete\":true}");Console.Out.Flush();
    }}
    static void FakeAudio(){using(var input=new StreamReader(Console.OpenStandardInput(),Encoding.UTF8))using(var output=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false)) {AutoFlush=true}){string line;while((line=input.ReadLine())!=null){var command=AudioBrokerClient.Decode<AudioCommand>(line);if(command.Target=="hang"){Thread.Sleep(30000);return;}output.WriteLine(AudioBrokerClient.Encode(new AudioResponse {Id=command.Id,Reading=new AudioReading {OperationSucceeded=true,TimestampUtc=DateTime.UtcNow,ActiveOutput="speaker"}}));}}}
    [STAThread] static int Main(string[] args){
        if(args.Length>0&&args[0]=="-I"){FakeWorker();return 0;}if(args.Length>0&&args[0]=="--audio-broker"){FakeAudio();return 0;}if(args.Length>0&&args[0]=="--sleep"){Thread.Sleep(30000);return 0;}
        try{
            string root=Path.Combine(Path.GetFullPath(args[0]),"runtime-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);Check(AppRuntime.Version!="unknown","test binaries use the product version source");
            string fake=Path.Combine(root,"reader with spaces");MakeWorker(fake);int partial=0;
            using(var worker=new GenericBatteryWorker(fake,false,3000)){var result=worker.Poll(true,null,delegate(WirelessSnapshot value){partial++;Check(!value.Complete,"only partial frames reach progress");});Check(result.Complete&&result.RequestId==1&&partial==1,"stale request IDs cannot replace the matching final result");Check(worker.Poll().RequestId==2,"requests have monotonic IDs");}
            string hung=Path.Combine(root,"hung reader");MakeWorker(hung);var watch=Stopwatch.StartNew();
            using(var worker=new GenericBatteryWorker(hung,false,300)){Check(worker.Poll().Error!=null&&watch.ElapsedMilliseconds<4000,"blocked reader is terminated within its bounded budget");Check(typeof(GenericBatteryWorker).GetField("process",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(worker)==null,"timed-out process reference is cleared");}
            string descendant=Path.Combine(root,"descendant reader");MakeWorker(descendant);int childId;
            using(var worker=new GenericBatteryWorker(descendant,false,3000)){var result=worker.Poll();childId=Int32.Parse(result.Errors[0].Split(':')[1]);Check(!Gone(childId),"fixture descendant exists before disposal");}for(int i=0;i<50&&!Gone(childId);i++)Thread.Sleep(20);Check(Gone(childId),"reader disposal kills its entire child tree");
            string binary=Path.Combine(root,"audio broker.exe");File.Copy(Assembly.GetExecutingAssembly().Location,binary);
            using(var broker=new AudioBrokerClient(binary,1200)){var initial=broker.Query(new AudioCommand {Operation="read"});Check(initial.OperationSucceeded,"audio broker uses local JSON with fake endpoints: "+initial.Error);watch.Restart();var timeout=broker.Query(new AudioCommand {Operation="output",Target="hang"});Check(timeout.OutcomeUnknown&&!timeout.OperationSucceeded&&watch.ElapsedMilliseconds<4500,"hanging write cannot be reported successful");Check(broker.Query(new AudioCommand {Operation="read"}).OperationSucceeded,"audio state can be reread after contained timeout");}
            string path=Path.Combine(root,"atomic.json");using(var writer=new SnapshotWriter()){for(int i=0;i<100;i++)writer.Enqueue(path,"{\"sequence\":"+i+"}");Check(writer.Flush(5000),"coalesced writer completes");Check(File.ReadAllText(path)=="{\"sequence\":99}","atomic writer retains newest pending snapshot");}Check(Directory.GetFiles(root,"*.tmp").Length==0,"atomic writes leave no staging files");
            Check(StartupDiagnostics.Evaluate(binary,"",binary,null)=="已启用","missing approval follows Windows default");Check(StartupDiagnostics.Evaluate(binary,"",binary,new byte[]{3})=="Windows 已禁用","disabled startup is visible");Check(StartupDiagnostics.Evaluate(binary,"",binary,new byte[0])=="无法确认","empty approval stays unknown");Check(StartupDiagnostics.Evaluate(binary,"",binary,new byte[]{9})=="无法确认","unrecognized approval stays unknown");Check(StartupDiagnostics.Evaluate(binary,"--bad",binary,null)=="目标失效","invalid startup arguments are detected");
            bool limited=false;try{AppRuntime.ReadLineBounded(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("12345\n"))),4);}catch(InvalidDataException){limited=true;}Check(limited,"IPC line length is bounded before parsing");
            Console.WriteLine("PASS "+checks+" runtime isolation/IPC/atomic-write checks; no hardware accessed");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    static void MakeWorker(string path){Directory.CreateDirectory(Path.Combine(path,"runtime"));File.Copy(Assembly.GetExecutingAssembly().Location,Path.Combine(path,"runtime","python.exe"));File.WriteAllText(Path.Combine(path,"server.py"),"# fixture: no hardware");}
}
