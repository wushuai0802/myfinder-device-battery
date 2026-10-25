using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

// No Show/Activate, input injection, real readers, settings save or visible icon.
// Performance observations are reported separately from deterministic lifecycle checks.
static class InteractionPerformanceTests {
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static int checks,renderCount,maximumFramesPerCycle;
    static readonly Dictionary<string,List<double>> times=new Dictionary<string,List<double>>();
    static readonly List<string> raw=new List<string>();
    static readonly List<WeakReference> popups=new List<WeakReference>();
    static readonly List<ResourceSample> resources=new List<ResourceSample>();
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr process,uint flags);
    static object Field(object target,string name){return target.GetType().GetField(name,Private).GetValue(target);}
    static void Set(object target,string name,object value){target.GetType().GetField(name,Private).SetValue(target,value);}
    static object Call(object target,string name,params object[] values){return target.GetType().GetMethod(name,Private).Invoke(target,values);}
    static void Check(bool condition,string name){if(!condition)throw new Exception("FAIL "+name);checks++;}
    static void Record(string name,int index,double milliseconds,bool measured) {
        if(!measured)return;
        List<double> values;if(!times.TryGetValue(name,out values)){values=new List<double>();times[name]=values;}
        if(raw.Count>=32768)throw new Exception("Performance fixture sample bound exceeded");
        values.Add(milliseconds);raw.Add(name+","+index+","+milliseconds.ToString("F6",CultureInfo.InvariantCulture));
    }
    static void Settle(TrayContext owner) {
        var clock=Stopwatch.StartNew();
        while((owner.IsRefreshing||owner.IsAudioBusy||owner.IsHeadsetRefreshing)&&clock.ElapsedMilliseconds<5000){Application.DoEvents();Thread.Sleep(2);}
        Check(!owner.IsRefreshing&&!owner.IsAudioBusy&&!owner.IsHeadsetRefreshing,"fake reads settle");
    }
    static DpiConfig Dpi() {
        var value=new DpiConfig {DeviceKey=MouseBattery.DeviceKey("fixture-native"),ActiveStage=1,TimestampUtc=DateTime.UtcNow};
        foreach(int dpi in new[]{400,800,1200,1600})value.Stages.Add(new DpiStage {XDpi=dpi,YDpi=dpi});
        return value;
    }
    static AudioReading Audio() {
        return new AudioReading {OperationSucceeded=true,TimestampUtc=DateTime.UtcNow,ConsoleOutputId="fixture-speaker",ActiveOutput="speaker",
            SpeakerId="fixture-speaker",HeadsetId="fixture-headset",MicrophoneId="fixture-mic",SpeakerAvailable=true,HeadsetAvailable=true,
            MicrophoneAvailable=true,MicrophoneMuted=false,OutputVolumePercent=50,VolumeEndpointId="fixture-speaker",Devices=new AudioDeviceChoice[0]};
    }
    static WirelessDevice[] Devices(int count) {
        var values=new List<WirelessDevice>();
        for(int i=0;i<count;i++)values.Add(new WirelessDevice {Id="performance-fixture-"+i,Name="Simulated "+new[]{"mouse","headset","keyboard"}[i%3],
            Kind=new[]{"mouse","headset","keyboard"}[i%3],Percent=25+i*20,Fresh=true,Online=true,Provider="fixture",Transport="USB / 2.4G",
            ConnectionState="connected",MeasurementState="current",SourceQuality="device_reply",ConfirmedAtUtc=DateTime.UtcNow.ToString("o")});
        return values.ToArray();
    }
    static double Draw(Details popup,int index,bool measured,string metric="draw_ms") {
        // A null key forces the full production GDI+/ARGB/UpdateLayeredWindow
        // path; repeated cache hits are never counted as completed painting.
        Set(popup,"lastPresentedKey",null);
        var clock=Stopwatch.StartNew();bool presented=(bool)Call(popup,"RenderLayeredFrame");clock.Stop();
        Check(presented&&!popup.Visible&&GetForegroundWindow()!=popup.Handle,"real layered rendering stays hidden and never owns foreground");
        Record(metric,index,clock.Elapsed.TotalMilliseconds,measured);
        if(measured){renderCount++;if(metric!="draw_ms")Record("draw_ms",index,clock.Elapsed.TotalMilliseconds,true);}
        return clock.Elapsed.TotalMilliseconds;
    }
    static void DrainMotion(Details popup,int index,bool measured,bool closing) {
        var clock=Stopwatch.StartNew();int frames=0;string previous=null;
        while(!popup.IsDisposed) {
            var timer=(System.Windows.Forms.Timer)Field(popup,"animation");
            var page=(System.Windows.Forms.Timer)Field(popup,"pageAnimation");
            if(!timer.Enabled&&!page.Enabled)break;
            Application.DoEvents();if(popup.IsDisposed)break;
            string state=Field(popup,"frameAlpha")+"|"+Field(popup,"animationProgress")+"|"+Field(popup,"pageProgress");
            if(state!=previous){Draw(popup,index,measured,"animation_draw_ms");previous=state;frames++;}
            if(clock.ElapsedMilliseconds>15000)throw new Exception("Fixture animation failed to settle; this is a lifecycle timeout, not a performance threshold.");
            Thread.Sleep(2);
        }
        maximumFramesPerCycle=Math.Max(maximumFramesPerCycle,frames);
        if(closing)Check(popup.IsDisposed,"close animation disposes its owned popup");
    }
    static void Click(Details popup,int id,int index,bool measured) {
        var controls=(List<PopupControl>)Call(popup,"ControlsForPage");
        var control=controls.Find(delegate(PopupControl value){return value.Id==id;});
        Check(control!=null&&control.Enabled,"fixture navigation action exists");
        float scale=(float)Field(popup,"dpiScale");
        var args=new MouseEventArgs(MouseButtons.Left,1,(int)((control.Rect.Left+control.Rect.Width/2)*scale),(int)((control.Rect.Top+control.Rect.Height/2)*scale),0);
        var clock=Stopwatch.StartNew();
        typeof(Control).GetMethod("OnMouseDown",Private).Invoke(popup,new object[]{args});
        typeof(Control).GetMethod("OnMouseUp",Private).Invoke(popup,new object[]{args});clock.Stop();
        Record("click_logic_ms",index,clock.Elapsed.TotalMilliseconds,measured);
        DrainMotion(popup,index,measured,false);Draw(popup,index,measured,"navigation_draw_ms");
    }
    static void Cycle(TrayContext owner,int index,bool measured,bool animate) {
        Details popup=null;
        try {
            var clock=Stopwatch.StartNew();popup=new Details(owner);Set(owner,"details",popup);
            var handle=popup.Handle;Call(popup,"ResetPage");owner.PanelOpened();Call(popup,"AnimateTo",1f,190f);
            Record("construct_open_logic_ms",index,clock.Elapsed.TotalMilliseconds,measured);
            Draw(popup,index,measured,"initial_draw_ms");
            Record("first_frame_from_construction_ms",index,clock.Elapsed.TotalMilliseconds,measured);
            DrainMotion(popup,index,measured,false);
            // Twenty full navigation round trips give forty real handler samples.
            if(measured&&index<20){Click(popup,1,index,true);Click(popup,6,index,true);Check(popup.CurrentPage=="Main","navigation returns to main");}
            clock.Restart();popup.CloseAnimated();clock.Stop();Record("close_logic_ms",index,clock.Elapsed.TotalMilliseconds,measured);
            DrainMotion(popup,index,measured,true);
            Check(popup.IsDisposed&&!popup.IsHandleCreated,"closed popup releases its HWND");
            popups.Add(new WeakReference(popup));
        }finally{if(popup!=null&&!popup.IsDisposed)popup.Dispose();Set(owner,"details",null);}
        Settle(owner);
    }
    static void TopologyChanges(TrayContext owner) {
        Details popup=null;
        try {
            popup=new Details(owner);Set(owner,"details",popup);var handle=popup.Handle;Call(popup,"ResetPage");
            for(int i=0;i<20;i++) {
                int count=i%4;var snapshot=new WirelessSnapshot {Schema=2,Complete=false,NativeMousePresent=false,Devices=Devices(count),Health=new ReaderHealth[0],Errors=new string[0]};
                var clock=Stopwatch.StartNew();Call(owner,"ApplyWireless",snapshot,false);popup.SyncDevices();clock.Stop();
                Record("topology_logic_ms",i,clock.Elapsed.TotalMilliseconds,true);
                DrainMotion(popup,i,true,false);Draw(popup,i,true,"topology_draw_ms");
                Check(owner.WirelessDevices.Length==count,"simulated topology replaces the reader snapshot, including empty states");
            }
        }finally{if(popup!=null){popup.Dispose();popups.Add(new WeakReference(popup));}Set(owner,"details",null);}
    }
    static void Collect() {Application.DoEvents();GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();Application.DoEvents();}
    static void Resources(int completed) {
        Collect();using(var process=Process.GetCurrentProcess()) {
            resources.Add(new ResourceSample {CompletedCycles=completed,PrivateBytes=process.PrivateMemorySize64,WorkingSetBytes=process.WorkingSet64,
                Handles=process.HandleCount,Threads=process.Threads.Count,ManagedBytes=GC.GetTotalMemory(false),GdiObjects=GetGuiResources(process.Handle,0),UserObjects=GetGuiResources(process.Handle,1)});
        }
    }
    [DataContract] sealed class ResourceSample {
        [DataMember] public int CompletedCycles,Handles,Threads;
        [DataMember] public long PrivateBytes,WorkingSetBytes,ManagedBytes;
        [DataMember] public uint GdiObjects,UserObjects;
    }
    [DataContract] sealed class Summary {
        [DataMember] public int Count,Over50Milliseconds;
        [DataMember] public double P50Milliseconds,P95Milliseconds,MaximumMilliseconds,MeanMilliseconds;
        internal static Summary From(List<double> values) {
            var sorted=new List<double>(values);sorted.Sort();double total=0;int slow=0;foreach(double value in sorted){total+=value;if(value>50)slow++;}
            return new Summary {Count=sorted.Count,P50Milliseconds=sorted[(int)Math.Ceiling(sorted.Count*.5)-1],P95Milliseconds=sorted[(int)Math.Ceiling(sorted.Count*.95)-1],
                MaximumMilliseconds=sorted[sorted.Count-1],MeanMilliseconds=total/sorted.Count,Over50Milliseconds=slow};
        }
    }
    [DataContract] sealed class Report {
        [DataMember] public int Schema=1,OpenCloseCycles=100,TopologySwitches=20,WarmupCycles=5,ActualRenderCount,Checks,CollectedPopups,MaximumFramesInOneMotion;
        [DataMember] public string ProductVersion,CreatedAtUtc;
        [DataMember] public bool AnimatedRequested,MotionEnabledByEnvironment;
        [DataMember] public Dictionary<string,Summary> Measurements;
        [DataMember] public ResourceSample[] Resources;
        [DataMember] public string[] Boundaries;
    }
    [STAThread] static int Main(string[] args) {
        if(args.Length>0&&args[0]=="--runtime-fixture")return RuntimeFixture(args);
        var cached=typeof(DeviceSettings).GetField("current",BindingFlags.NonPublic|BindingFlags.Static);object previous=cached.GetValue(null);TrayContext owner=null;
        try {
            string output=Path.GetFullPath(args.Length>0?args[0]:Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"performance"));Directory.CreateDirectory(output);
            bool animate=Array.IndexOf(args,"--animate")>=0;
            NativeDisplay.EnableDpiAwareness();Application.EnableVisualStyles();
            DeviceSettings.UseTransientConfiguration(new AudioBindings {Theme="light",ReducedMotion=!animate,RememberReadings=false,LowBatteryNotifications=false});
            owner=new TrayContext(false,delegate(bool battery){return new MouseSnapshot {Battery=battery?new BatteryReading {Percent=85,TimestampUtc=DateTime.UtcNow}:null,Dpi=Dpi()};},
                delegate(int stage){throw new Exception("Unexpected DPI write");},delegate{return new HeadsetReading {ConnectionVerified=true,Connected=false};},delegate{return Audio();},false,
                delegate(string endpoint){throw new Exception("Unexpected output write");},delegate(bool mute){throw new Exception("Unexpected microphone write");},
                delegate(string endpoint,int volume){throw new Exception("Unexpected volume write");},delegate{return false;});
            Settle(owner);owner.NativeMousePresent=false;owner.GenericReady=true;owner.WirelessDevices=Devices(3);
            bool motion=!PopupPages.Reduced;
            for(int i=0;i<5;i++)Cycle(owner,i,false,animate);Resources(0);
            for(int i=0;i<100;i++){Cycle(owner,i,true,animate);if((i+1)%25==0)Resources(i+1);}
            TopologyChanges(owner);Resources(100);
            // Owned-window lifetime is deterministic. Process-wide private bytes,
            // CLR/JIT/font caches, handle totals and wall time are observations;
            // they do not create machine-dependent absolute CI limits.
            Collect();int alive=0;foreach(var popup in popups)if(popup.IsAlive)alive++;
            Check(alive==0,"all disposed popup instances are collectible after repeated lifecycle/topology work");
            Check(!((NotifyIcon)Field(owner,"tray")).Visible&&!owner.DiagnosticsEnabled,"fixture never publishes an icon or enables persistence/hardware diagnostics");
            var measurements=new Dictionary<string,Summary>();foreach(var pair in times)measurements[pair.Key]=Summary.From(pair.Value);
            var report=new Report {ProductVersion=AppRuntime.Version,CreatedAtUtc=DateTime.UtcNow.ToString("o"),AnimatedRequested=animate,MotionEnabledByEnvironment=motion,
                ActualRenderCount=renderCount,Checks=checks,CollectedPopups=popups.Count,MaximumFramesInOneMotion=maximumFramesPerCycle,Measurements=measurements,Resources=resources.ToArray(),
                Boundaries=new[]{"All devices/topology and readers/writers are fixtures. No physical HID/audio request or settings save is performed.",
                    "Production Details/ARGB/GDI+/UpdateLayeredWindow code renders into hidden owned windows. Show/Activate and desktop input injection are not called.",
                    "Open/close uses production reset, controller-open and animation/close logic with visibility and activation omitted. Reduced motion is the default; --animate requests timed production animation when Windows permits it.",
                    "First frame means construction-to-hidden-surface presentation. Click latency is the local handler duration, not device I/O or input-to-photon latency.",
                    "Draw P50/P95 and over-50ms counts measure actual drawing/presentation work. They cannot prove 60fps, compositor cadence or visible desktop smoothness.",
                    "Resource samples include the fixture's bounded measurement lists/weak references, CLR/JIT and font caches. They are process-local observations after warmup/GC, not standalone application memory growth measurements.",
                    "Regression gates require destroyed HWNDs and collectible owned popup instances; memory/handle/CPU growth requires the separate long runtime sampler."}};
            var serializer=new DataContractJsonSerializer(typeof(Report),new DataContractJsonSerializerSettings {UseSimpleDictionaryFormat=true});
            using(var file=File.Create(Path.Combine(output,"interaction-performance.json")))serializer.WriteObject(file,report);
            File.WriteAllText(Path.Combine(output,"interaction-performance.csv"),"metric,iteration,milliseconds\r\n"+String.Join("\r\n",raw.ToArray())+"\r\n",new UTF8Encoding(false));
            Console.WriteLine("PASS "+checks+" lifecycle/resource checks; 100 hidden open/close cycles, 20 simulated topology switches; "+renderCount+" real draws");
            Console.WriteLine("PERFORMANCE "+Path.Combine(output,"interaction-performance.json"));return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{if(owner!=null){owner.ExitThread();owner.Dispose();}cached.SetValue(null,previous);}
    }
    static int RuntimeFixture(string[] args) {
        // Sampler self-tests use only this inert process and its inert child.
        int seconds=args.Length>1?Int32.Parse(args[1],CultureInfo.InvariantCulture):5;
        using(var child=args.Length>2&&args[2]=="--child"?Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--runtime-fixture "+seconds) {
            UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden}):null)Thread.Sleep(seconds*1000);
        return 0;
    }
}
