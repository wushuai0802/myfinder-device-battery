using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
#if FIXTURE_VERSION_TWO
[assembly:AssemblyInformationalVersion("runtime-sampler-fixture-v2")]
#else
[assembly:AssemblyInformationalVersion("runtime-sampler-fixture-v1")]
#endif
// Finite, invisible and inert. No application source, device API or settings.
static class RuntimeSamplerFixture {
    static int Main(string[] args) {
        int seconds=args.Length>1?Int32.Parse(args[1]):5;
        using(var child=args.Length>2&&args[2]=="--child"?Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--runtime-fixture "+seconds) {
            UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden}):null)Thread.Sleep(seconds*1000);
        return 0;
    }
}
