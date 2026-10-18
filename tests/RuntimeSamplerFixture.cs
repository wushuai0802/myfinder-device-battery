using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
#if FIXTURE_VERSION_TWO
[assembly:AssemblyInformationalVersion("runtime-sampler-fixture-v2")]
#else
[assembly:AssemblyInformationalVersion("runtime-sampler-fixture-v1")]
#endif
// Finite, invisible and inert. Only test-owned coordination files are written;
// no application source, device API or settings are accessed.
static class RuntimeSamplerFixture {
    static Process Start(string arguments){return Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,arguments) {
        UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});}
    static string Quote(string value){return "\""+value+"\"";}
    static int Main(string[] args) {
        int seconds=args.Length>1?Int32.Parse(args[1]):5;
        if(args.Length>3&&args[0]=="--orphan-root") {
            using(var parent=Start("--orphan-parent "+seconds+" "+Quote(args[2])+" "+Quote(args[3])))Thread.Sleep(seconds*1000);
            return 0;
        }
        if(args.Length>3&&args[0]=="--orphan-parent") {
            using(var child=Start("--runtime-fixture "+seconds)) {
                File.WriteAllText(args[3],Process.GetCurrentProcess().Id+"\n"+child.Id+"\n"+child.StartTime.ToUniversalTime().Ticks);
                var clock=Stopwatch.StartNew();while(clock.Elapsed.TotalSeconds<seconds&&!File.Exists(args[2]))Thread.Sleep(10);
            }
            return 0;
        }
        using(var child=args.Length>2&&args[2]=="--child"?Start("--runtime-fixture "+seconds):null)Thread.Sleep(seconds*1000);
        return 0;
    }
}
