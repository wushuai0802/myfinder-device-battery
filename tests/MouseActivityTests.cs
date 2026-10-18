using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AtkBatteryTray;

static class MouseActivityTests {
    static int checks;
    static void Check(bool value,string name){if(!value)throw new Exception("FAIL: "+name);checks++;}
    static MouseActivitySource Logitech(){return MouseActivitySource.FromUsbParent(@"\\?\HID#VID_046D&PID_C547&MI_00#7&demo&0&0000#{mouse}",@"USB\VID_046D&PID_C547\5&DEMO&0&2");}
    static MouseActivitySource Atk(){return MouseActivitySource.FromUsbParent(@"\\?\HID#VID_373B&PID_1278&MI_01#7&demo&0&0001#{mouse}",@"USB\VID_373B&PID_1278\SERIAL-A");}
    sealed class ManualQueue {
        internal readonly Queue<Action> Actions=new Queue<Action>();
        internal int Posts;
        internal void Post(Action action){Posts++;Actions.Enqueue(action);}
        internal void Drain(){int limit=10000;while(Actions.Count>0){if(--limit==0)throw new Exception("Unbounded fixture work");Actions.Dequeue()();}}
    }
    sealed class FakeNative:IMouseActivityNative {
        internal struct Header {internal uint Type;internal IntPtr Device;}
        internal readonly Dictionary<IntPtr,Header> Headers=new Dictionary<IntPtr,Header>(MouseHandleComparer.Instance);
        internal readonly Dictionary<IntPtr,MouseActivitySource> Sources=new Dictionary<IntPtr,MouseActivitySource>(MouseHandleComparer.Instance);
        internal Func<IntPtr,MouseActivitySource> Resolver;
        internal int HeaderReads,Resolutions,Registrations,Unregistrations,Disposals;
        internal bool EchoHandle,ThrowHeader,ThrowResolver;
        internal IntPtr Target;
        public bool ReadHeader(IntPtr input,out uint type,out IntPtr device) {
            HeaderReads++;if(ThrowHeader)throw new InvalidOperationException("Injected header failure");Header header;
            if(Headers.TryGetValue(input,out header)){type=header.Type;device=header.Device;return true;}
            type=0;device=EchoHandle?input:IntPtr.Zero;return EchoHandle;
        }
        public MouseActivitySource Resolve(IntPtr device){Resolutions++;if(ThrowResolver)throw new InvalidOperationException("Injected metadata failure");if(Resolver!=null)return Resolver(device);MouseActivitySource value;return Sources.TryGetValue(device,out value)?value:null;}
        public bool Register(IntPtr target){Registrations++;Target=target;return true;}
        public void Unregister(){Unregistrations++;}
        public void Dispose(){Disposals++;}
    }
    sealed class Fixture:IDisposable {
        internal long Now;
        internal readonly FakeNative Native=new FakeNative();
        internal readonly ManualQueue Work=new ManualQueue(),Ui=new ManualQueue();
        internal readonly List<MouseActivitySource> Delivered=new List<MouseActivitySource>();
        internal readonly List<long> DeliveredAt=new List<long>();
        internal readonly MouseActivityListener Listener;
        internal int Defaults;
        internal readonly MouseActivityDefaultProcedure Default;
        internal Fixture(bool registered=false,Action<Action> customDispatch=null) {
            Default=delegate(ref Message message){Defaults++;message.Result=new IntPtr(123);};
            Listener=new MouseActivityListener(delegate(MouseActivitySource source){Delivered.Add(source);DeliveredAt.Add(Now);},customDispatch??Ui.Post,Native,delegate{return Now;},Work.Post,registered?new IntPtr(42):IntPtr.Zero);
        }
        internal void Map(long packet,long device,uint type,MouseActivitySource source){Native.Headers[new IntPtr(packet)]=new FakeNative.Header {Type=type,Device=new IntPtr(device)};Native.Sources[new IntPtr(device)]=source;}
        internal void Message(int id,long parameter){var message=System.Windows.Forms.Message.Create(IntPtr.Zero,id,IntPtr.Zero,new IntPtr(parameter));Listener.ProcessWindowMessage(ref message,Default);Check(message.Result==new IntPtr(123),"raw message retains the normal default procedure result");}
        internal void Packet(long packet){var message=System.Windows.Forms.Message.Create(IntPtr.Zero,MouseActivityListener.InputMessage,IntPtr.Zero,new IntPtr(packet));Listener.ProcessWindowMessage(ref message,Default);}
        internal void ResolveAndDeliver(){Work.Drain();Ui.Drain();}
        public void Dispose(){Listener.Dispose();}
    }
    static void PhysicalIdentity() {
        var logitech=Logitech();var atk=Atk();
        Check(logitech!=null&&logitech.PhysicalId=="device-f9780bfc49ea1d2ab8197b8e"&&logitech.VendorId==0x046d&&logitech.ProductId==0xc547&&!logitech.IsNativeAtk,"USB parent hashing agrees with the Python engine public_id fixture");
        Check(atk!=null&&atk.PhysicalId=="device-e3d8e3423938a0d26bb71cab"&&atk.IsNativeAtk,"the exact verified native ATK pair is classified independently");
        var secondCollection=MouseActivitySource.FromUsbParent(@"\\?\HID#VID_046D&PID_C547&MI_01&Col02#different-collection#{mouse}",@"usb\vid_046d&pid_c547\5&demo&0&2");
        Check(secondCollection!=null&&secondCollection.PhysicalId==logitech.PhysicalId,"multiple HID collections or receiver slots share only proven USB physical evidence");
        var another=MouseActivitySource.FromUsbParent(@"\\?\HID#VID_046D&PID_C547&MI_00#other#{mouse}",@"USB\VID_046D&PID_C547\5&OTHER&0&2");
        Check(another!=null&&another.PhysicalId!=logitech.PhysicalId,"same-model receivers at different parent instances remain separate");
        Check(MouseActivitySource.FromUsbParent(@"\\?\HID#VID_046D&PID_C547&MI_00#bt#{mouse}",@"USB\VID_8087&PID_0033\adapter")==null,"Bluetooth activity cannot borrow an unrelated USB radio ancestor");
        Check(MouseActivitySource.FromUsbParent(@"\\?\HID#VID_046D&PID_C547&MI_00#x#{mouse}",@"USB\VID_046D&PID_C548\receiver")==null,"raw interface and USB parent product IDs must match");
        Check(MouseActivitySource.FromUsbParent(@"\\?\HID#VID_046D&PID_C547&MI_00#x#{mouse}",@"USB\VID_046D&PID_C547&MI_00\collection")==null,"a composite interface node cannot substitute for the USB parent");
        Check(MouseActivitySource.FromUsbParent(@"\\?\ROOT#RDP_MOU#0000#{mouse}",@"USB\VID_046D&PID_C547\receiver")==null&&MouseActivitySource.FromUsbParent(null,null)==null,"synthetic and unresolved source names are not guessed");
        Check(MouseActivitySource.FromUsbParent(@"\\?\HID#VID_046D&PID_C547&MI_00#x#{mouse}","USB\\VID_046D&PID_C547\\bad\ninstance")==null,"invalid parent identity text is rejected");
        bool setters=false;foreach(var property in typeof(MouseActivitySource).GetProperties())setters|=property.CanWrite;
        Check(!setters,"the public activity source exposes immutable opaque metadata only");
    }
    static void NativeContract() {
        var registration=MouseActivityNative.RegistrationFor(new IntPtr(42),false);var remove=MouseActivityNative.RegistrationFor(new IntPtr(99),true);
        Check(registration.UsagePage==1&&registration.Usage==2&&registration.Target==new IntPtr(42)&&registration.Flags==0x2100,"only the mouse TLC is registered for background input and device notifications");
        Check((registration.Flags&0x30)==0&&(registration.Flags&0x200)==0,"registration never suppresses legacy mouse messages or captures other windows");
        Check(remove.Flags==1&&remove.Target==IntPtr.Zero,"unregistration follows the required REMOVE and null-target contract");
        Check(Marshal.SizeOf(typeof(MouseActivityNative.RawHeader))==(IntPtr.Size==8?24:16)&&Marshal.OffsetOf(typeof(MouseActivityNative.RawHeader),"Device").ToInt32()==8,"RAWINPUTHEADER size and device offset follow native pointer width");
        Check(Marshal.SizeOf(typeof(MouseActivityNative.Registration))==(IntPtr.Size==8?16:12)&&Marshal.OffsetOf(typeof(MouseActivityNative.Registration),"Target").ToInt32()==8,"RAWINPUTDEVICE has the native ABI layout");
        Check(MouseActivityNative.HeaderCommand==0x10000005&&MouseActivityNative.DeviceNameCommand==0x20000007,"the native adapter asks for header and descriptor name rather than input payload");
        using(var f=new Fixture(true)) {
            Check(f.Listener.Registered,"registration state is observable without native diagnostics I/O");
            f.Message(0x400,0);f.Map(1,10,1,Logitech());f.Message(MouseActivityListener.InputMessage,1);f.Map(2,0,0,Logitech());f.Message(MouseActivityListener.InputMessage,2);f.Message(MouseActivityListener.DeviceChangeMessage,10);
            Check(f.Defaults==4&&f.Native.HeaderReads==2&&f.Native.Resolutions==0,"keyboard, zero-device and topology messages all reach normal default processing without a lookup");
            Check(f.Listener.RawInputMessages==2&&f.Listener.MousePackets==0&&f.Listener.HeaderReadFailures==0,"diagnostics distinguish received messages from eligible mouse headers");
            f.Native.ThrowHeader=true;bool threw=false;try{f.Packet(1);}catch(InvalidOperationException){threw=true;}Check(threw&&f.Defaults==5,"default processing still runs if a packet reader fails");
            Check(f.Listener.RawInputMessages==3&&f.Listener.HeaderReadFailures==1&&f.Listener.MousePackets==0,"a failed header is counted without becoming mouse activity");
            f.Listener.Dispose();f.Listener.Dispose();Check(f.Native.Registrations==1&&f.Native.Target==new IntPtr(42)&&f.Native.Unregistrations==1&&f.Native.Disposals==1,"registered disposal unregisters exactly once and is idempotent");
            Check(!f.Listener.Registered,"registered state becomes false on disposal");
            int reads=f.Native.HeaderReads;f.Packet(1);Check(f.Native.HeaderReads==reads&&f.Defaults==6,"after disposal the listener ignores packets while preserving default processing");
        }
    }
    static void CoalescingAndIsolation() {
        using(var f=new Fixture()) {
            f.Map(1,10,0,Logitech());for(int i=0;i<8000;i++)f.Packet(1);
            Check(f.Work.Posts==1&&f.Native.Resolutions==0&&f.Ui.Posts==0&&f.Delivered.Count==0,"an 8 kHz-sized cold burst schedules one background metadata job and no UI work in the packet path");
            Check(f.Listener.RawInputMessages==8000&&f.Listener.MousePackets==8000&&f.Listener.ResolvedSourceCount==0&&f.Listener.UnresolvedSourceCount==0,"high-rate packet counters are independent from deferred source resolution");
            f.Work.Drain();Check(f.Native.Resolutions==1&&f.Ui.Posts==1&&f.Delivered.Count==0,"resolution publishes one deferred UI notification");f.Ui.Drain();
            Check(f.Listener.ResolvedSourceCount==1&&f.Listener.UnresolvedSourceCount==0,"resolved metadata is counted once instead of once per packet");
            Check(f.Delivered.Count==1&&f.Delivered[0].PhysicalId==Logitech().PhysicalId,"the resolved source reaches the caller only through the UI dispatcher");
            f.Now=499;for(int i=0;i<8000;i++)f.Packet(1);Check(f.Ui.Posts==1&&f.Native.Resolutions==1,"warm high-frequency packets reuse source metadata and remain coalesced");
            f.Now=500;f.Packet(1);f.Ui.Drain();Check(f.Delivered.Count==2&&f.DeliveredAt[1]-f.DeliveredAt[0]>=500,"a physical source is delivered at most once per 500 ms");
            f.Map(2,20,0,Atk());f.Packet(2);f.ResolveAndDeliver();Check(f.Delivered.Count==3&&f.Delivered[2].IsNativeAtk,"a second physical mouse is not throttled by the first source");
        }
        using(var f=new Fixture()) {
            f.Map(1,10,0,Logitech());f.Map(2,11,0,Logitech());f.Packet(1);f.Packet(2);f.ResolveAndDeliver();
            Check(f.Native.Resolutions==2&&f.Delivered.Count==1,"same-receiver collections coalesce at physical level");f.Now=500;f.Packet(1);f.Packet(2);f.Ui.Drain();Check(f.Delivered.Count==2,"alternating collections cannot multiply the physical callback rate");
        }
        using(var f=new Fixture()) {
            f.Map(1,10,0,Logitech());f.Packet(1);f.Work.Drain();f.Now=5000;for(int i=0;i<16000;i++)f.Packet(1);Check(f.Ui.Posts==1,"a delayed UI post does not accumulate one callback per throttle interval");f.Ui.Drain();
            f.Now=5499;f.Packet(1);Check(f.Ui.Posts==1,"coalescing is anchored to delivery time after a slow UI queue");f.Now=5500;f.Packet(1);f.Ui.Drain();Check(f.DeliveredAt.Count==2&&f.DeliveredAt[0]==5000&&f.DeliveredAt[1]==5500,"deferred callbacks retain actual minimum delivery spacing");
            f.Now=4000;for(int i=0;i<1000;i++)f.Packet(1);Check(f.Delivered.Count==2&&f.Ui.Posts==2,"a backwards injected clock cannot bypass the monotonic guard");
        }
    }
    static void EpochAndDisposal() {
        using(var f=new Fixture()) {
            f.Map(1,10,0,Logitech());f.Packet(1);f.Work.Drain();f.Listener.InvalidateTopology();f.Native.Sources[new IntPtr(10)]=Atk();f.Packet(1);f.Work.Drain();f.Ui.Drain();
            Check(f.Delivered.Count==1&&f.Delivered[0].IsNativeAtk&&f.Native.Resolutions==2,"topology invalidation retires a queued callback before a raw handle is reused");
        }
        using(var f=new Fixture()) {
            var first=Logitech();var second=Atk();bool changed=false;f.Map(1,10,0,first);
            f.Native.Resolver=delegate(IntPtr device){if(!changed){changed=true;f.Listener.InvalidateTopology();f.Packet(1);return first;}return second;};
            f.Packet(1);f.ResolveAndDeliver();Check(f.Native.Resolutions==2&&f.Delivered.Count==1&&f.Delivered[0].IsNativeAtk,"an in-progress old metadata resolution cannot publish into the new topology epoch");
        }
        using(var f=new Fixture(true)) {
            f.Map(1,10,0,Logitech());f.Packet(1);f.Listener.Dispose();f.ResolveAndDeliver();Check(f.Native.Resolutions==0&&f.Delivered.Count==0,"disposal cancels metadata work before it begins");
        }
        using(var f=new Fixture(true)) {
            f.Map(1,10,0,Logitech());f.Packet(1);f.Work.Drain();f.Listener.Dispose();f.Ui.Drain();Check(f.Delivered.Count==0,"disposal invalidates an already posted UI callback");
        }
        using(var f=new Fixture()) {
            f.Map(1,10,0,Logitech());f.Native.Resolver=delegate(IntPtr device){f.Listener.Dispose();return Logitech();};f.Packet(1);f.ResolveAndDeliver();Check(f.Delivered.Count==0&&f.Ui.Posts==0,"a resolution completing during disposal cannot post or deliver activity");
        }
    }
    static void FailureAndBounds() {
        using(var f=new Fixture()) {
            f.Map(1,10,0,null);for(int i=0;i<1000;i++)f.Packet(1);f.ResolveAndDeliver();f.Now=10000;for(int i=0;i<1000;i++)f.Packet(1);f.ResolveAndDeliver();
            Check(f.Native.Resolutions==1&&f.Delivered.Count==0&&f.Listener.UnresolvedSourceCount==1,"unresolved metadata is negatively cached and counted once without guessed targets");f.Listener.InvalidateTopology();f.Native.Sources[new IntPtr(10)]=Logitech();f.Packet(1);f.ResolveAndDeliver();Check(f.Delivered.Count==1&&f.Native.Resolutions==2,"topology change permits a formerly unresolved source to be resolved again");
            Check(f.Listener.ResolvedSourceCount==1&&f.Listener.UnresolvedSourceCount==1&&f.Listener.MousePackets==2001,"topology invalidation preserves cumulative diagnostic counters while clearing source caches");
        }
        using(var f=new Fixture()) {f.Map(1,10,0,null);f.Native.ThrowResolver=true;f.Packet(1);f.ResolveAndDeliver();f.Packet(1);f.ResolveAndDeliver();Check(f.Native.Resolutions==1&&f.Delivered.Count==0,"metadata failures do not create a retry or notification storm");}
        using(var f=new Fixture()) {f.Native.EchoHandle=true;for(int i=1;i<=2000;i++)f.Packet(i);f.ResolveAndDeliver();Check(f.Work.Posts==1&&f.Native.Resolutions==256&&f.Delivered.Count==0,"untrusted handle churn stays within the bounded cache and one background worker");}
        int attempts=0;using(var f=new Fixture(false,delegate(Action action){attempts++;throw new InvalidOperationException("UI handle unavailable");})) {
            f.Map(1,10,0,Logitech());f.Packet(1);f.Work.Drain();for(int i=0;i<8000;i++)f.Packet(1);Check(attempts==1,"failed UI dispatch retains a throttle instead of allocating one post per packet");f.Now=500;f.Packet(1);Check(attempts==2,"a failed UI dispatch can recover on a later bounded attempt");
        }
    }
    static void HotPathBenchmark() {
        using(var f=new Fixture()) {
            f.Map(1,10,0,Logitech());f.Packet(1);f.ResolveAndDeliver();for(int i=0;i<1000;i++)f.Packet(1);
            var allocationMethod=typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread",BindingFlags.Static|BindingFlags.Public);Func<long> allocation=allocationMethod==null?null:(Func<long>)Delegate.CreateDelegate(typeof(Func<long>),allocationMethod);
            var timer=new Stopwatch();GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();int gen0=GC.CollectionCount(0);long bytes=allocation==null?0:allocation();timer.Start();
            for(int i=0;i<80000;i++)f.Packet(1);timer.Stop();long allocated=allocation==null?-1:allocation()-bytes;int collections=GC.CollectionCount(0)-gen0;
            Console.WriteLine("Fixture benchmark: 80000 cached header packets in "+timer.Elapsed.TotalMilliseconds.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)+" ms; observed allocated bytes="+allocated+". Native Windows delivery and HID/CPU behavior are not measured.");
            Check(f.Native.Resolutions==1&&f.Ui.Posts==1&&f.Delivered.Count==1,"80k warm packets cause no additional metadata, posts or controller callbacks");
            Check(collections==0&&(allocated<0||allocated<4096),"warm packet handling has no per-packet managed allocation pressure");
        }
    }
    [STAThread] static int Main() {
        try {Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);PhysicalIdentity();NativeContract();CoalescingAndIsolation();EpochAndDisposal();FailureAndBounds();HotPathBenchmark();Console.WriteLine("PASS "+checks+" mouse activity checks; injected raw headers, clock, metadata and dispatcher only, no native input registration or hardware access");return 0;}
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
