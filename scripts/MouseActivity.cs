using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace AtkBatteryTray {
    public sealed class MouseActivitySource {
        readonly string physicalId;
        readonly bool nativeAtk;
        readonly ushort vendorId,productId;
        public string PhysicalId {get{return physicalId;}}
        public bool IsNativeAtk {get{return nativeAtk;}}
        public ushort VendorId {get{return vendorId;}}
        public ushort ProductId {get{return productId;}}
        internal MouseActivitySource(string physicalId,bool isNativeAtk,ushort vendorId,ushort productId) {
            if(!Regex.IsMatch(physicalId??"",@"^device-[a-f0-9]{24}$")||vendorId==0||productId==0||isNativeAtk!=(vendorId==0x373b&&productId==0x1278))throw new ArgumentException("Unresolved mouse activity source.");
            this.physicalId=physicalId;nativeAtk=isNativeAtk;this.vendorId=vendorId;this.productId=productId;
        }
        internal static MouseActivitySource FromUsbParent(string rawPath,string parentInstance) {
            if(String.IsNullOrEmpty(rawPath)||String.IsNullOrEmpty(parentInstance)||rawPath.Length>32768||parentInstance.Length>4096)return null;
            string path=rawPath.ToLowerInvariant(),parent=parentInstance.ToLowerInvariant();
            if(!path.StartsWith(@"\\?\hid#",StringComparison.Ordinal)&&!path.StartsWith(@"\??\hid#",StringComparison.Ordinal))return null;
            if(!parent.StartsWith(@"usb\vid_",StringComparison.Ordinal))return null;
            var parts=parent.Split('\\');if(parts.Length!=3||parts[2].Length==0||parts[1].Contains("&mi_"))return null;
            foreach(char value in parent)if(Char.IsControl(value))return null;
            var raw=Regex.Match(path,@"(?:#|\\)vid_([0-9a-f]{4})&pid_([0-9a-f]{4})(?:&|#|\\)");
            var usb=Regex.Match(parent,@"^usb\\vid_([0-9a-f]{4})&pid_([0-9a-f]{4})(?:&[^\\]*)?\\");
            if(!raw.Success||!usb.Success||raw.Groups[1].Value!=usb.Groups[1].Value||raw.Groups[2].Value!=usb.Groups[2].Value)return null;
            ushort vendor=UInt16.Parse(usb.Groups[1].Value,NumberStyles.HexNumber,CultureInfo.InvariantCulture),product=UInt16.Parse(usb.Groups[2].Value,NumberStyles.HexNumber,CultureInfo.InvariantCulture);
            if(vendor==0||product==0)return null;
            string id;using(var sha=SHA256.Create())id="device-"+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes("pnp:"+parent))).Replace("-","").ToLowerInvariant().Substring(0,24);
            return new MouseActivitySource(id,vendor==0x373b&&product==0x1278,vendor,product);
        }
    }

    internal delegate void MouseActivityDefaultProcedure(ref Message message);
    internal sealed class MouseHandleComparer:IEqualityComparer<IntPtr> {
        internal static readonly MouseHandleComparer Instance=new MouseHandleComparer();
        public bool Equals(IntPtr left,IntPtr right){return left==right;}
        public int GetHashCode(IntPtr value){long bits=value.ToInt64();return (int)bits^(int)(bits>>32);}
    }
    internal interface IMouseActivityNative:IDisposable {
        bool ReadHeader(IntPtr rawInput,out uint type,out IntPtr device);
        MouseActivitySource Resolve(IntPtr device);
        bool Register(IntPtr window);
        void Unregister();
    }

    // Only the real TrayContext constructor creates the native registration.
    // The packet path uses a fixed header buffer, two bounded lookups and a
    // monotonic guard. It neither reads mouse coordinates nor keyboard data.
    internal sealed class MouseActivityListener:IDisposable {
        internal const int InputMessage=0x00ff,DeviceChangeMessage=0x00fe;
        internal const long CoalesceMilliseconds=500;
        const int MaximumHandles=256;
        readonly object gate=new object();
        readonly Action<MouseActivitySource> callback;
        readonly Action<Action> dispatch,queueWork;
        readonly Func<long> clock;
        readonly IMouseActivityNative native;
        readonly Action resolveLoop;
        readonly Dictionary<IntPtr,HandleEntry> handles=new Dictionary<IntPtr,HandleEntry>(MouseHandleComparer.Instance);
        readonly Dictionary<string,PhysicalEntry> physical=new Dictionary<string,PhysicalEntry>(StringComparer.Ordinal);
        readonly Queue<HandleEntry> unresolved=new Queue<HandleEntry>();
        ActivityWindow window;
        long epoch;
        long rawInputMessages,mousePackets,headerReadFailures,resolvedSourceCount,unresolvedSourceCount;
        bool disposed,resolverRunning,registered;
        internal bool Registered {get{lock(gate)return registered&&!disposed;}}
        internal long RawInputMessages {get{return Interlocked.Read(ref rawInputMessages);}}
        internal long MousePackets {get{return Interlocked.Read(ref mousePackets);}}
        internal long HeaderReadFailures {get{return Interlocked.Read(ref headerReadFailures);}}
        internal long ResolvedSourceCount {get{return Interlocked.Read(ref resolvedSourceCount);}}
        internal long UnresolvedSourceCount {get{return Interlocked.Read(ref unresolvedSourceCount);}}
        sealed class HandleEntry {
            internal readonly IntPtr Handle;internal readonly long Epoch;
            internal MouseActivitySource Source;internal PhysicalEntry Physical;
            internal HandleEntry(IntPtr handle,long epoch){Handle=handle;Epoch=epoch;}
        }
        sealed class PhysicalEntry {
            internal readonly MouseActivitySource Source;internal readonly long Epoch;internal readonly Action Deliver;
            internal bool Pending,HasAttempt;internal long LastAttempt;
            internal PhysicalEntry(MouseActivityListener owner,MouseActivitySource source,long epoch){Source=source;Epoch=epoch;Deliver=delegate{owner.Deliver(this);};}
        }
        static readonly WaitCallback RunWork=delegate(object state){((Action)state)();};
        static void Background(Action action){ThreadPool.QueueUserWorkItem(RunWork,action);}
        static long MonotonicMilliseconds(){return Stopwatch.GetTimestamp()/Math.Max(1,Stopwatch.Frequency/1000);}
        internal MouseActivityListener(Action<MouseActivitySource> callback,Action<Action> dispatch):this(callback,dispatch,new MouseActivityNative(),MonotonicMilliseconds,Background,true,IntPtr.Zero){}
        internal MouseActivityListener(Action<MouseActivitySource> callback,Action<Action> dispatch,IMouseActivityNative native,Func<long> clock,Action<Action> queueWork,IntPtr registrationTarget):this(callback,dispatch,native,clock,queueWork,false,registrationTarget){}
        MouseActivityListener(Action<MouseActivitySource> callback,Action<Action> dispatch,IMouseActivityNative native,Func<long> clock,Action<Action> queueWork,bool createWindow,IntPtr registrationTarget) {
            if(callback==null||dispatch==null||native==null||clock==null||queueWork==null)throw new ArgumentNullException("Mouse activity dependencies");
            this.callback=callback;this.dispatch=dispatch;this.native=native;this.clock=clock;this.queueWork=queueWork;resolveLoop=ResolveQueued;
            try {
                if(createWindow){window=new ActivityWindow(this);registrationTarget=window.Handle;}
                if(registrationTarget!=IntPtr.Zero){if(!native.Register(registrationTarget))throw new Win32Exception("Mouse activity registration unavailable.");registered=true;}
            }catch{Dispose();throw;}
        }
        sealed class ActivityWindow:NativeWindow {
            readonly MouseActivityListener owner;readonly MouseActivityDefaultProcedure forwardDefault;
            internal ActivityWindow(MouseActivityListener owner){this.owner=owner;forwardDefault=ForwardDefault;CreateHandle(new CreateParams {Caption="MyFinder Mouse Activity",Parent=new IntPtr(-3)});}
            void ForwardDefault(ref Message message){base.WndProc(ref message);}
            protected override void WndProc(ref Message message){owner.ProcessWindowMessage(ref message,forwardDefault);}
            internal void Close(){DestroyHandle();}
        }
        internal void ProcessWindowMessage(ref Message message,MouseActivityDefaultProcedure defaultProcedure) {
            try {
                if(message.Msg==DeviceChangeMessage){InvalidateTopology();return;}
                if(message.Msg!=InputMessage)return;
                lock(gate)if(disposed)return;
                Interlocked.Increment(ref rawInputMessages);
                uint type;IntPtr device;
                bool valid;try{valid=native.ReadHeader(message.LParam,out type,out device);}catch{Interlocked.Increment(ref headerReadFailures);throw;}
                if(!valid){Interlocked.Increment(ref headerReadFailures);return;}
                if(type==0&&device!=IntPtr.Zero){Interlocked.Increment(ref mousePackets);Observe(device);}
            }finally {defaultProcedure(ref message);}
        }
        void Observe(IntPtr device) {
            Action notification=null;bool startResolver=false;
            lock(gate) {
                if(disposed)return;HandleEntry entry;
                if(!handles.TryGetValue(device,out entry)) {
                    if(handles.Count>=MaximumHandles)return;
                    entry=new HandleEntry(device,epoch);handles.Add(device,entry);unresolved.Enqueue(entry);
                    if(!resolverRunning){resolverRunning=true;startResolver=true;}
                }else if(entry.Physical!=null)notification=Prepare(entry.Physical);
            }
            if(startResolver)queueWork(resolveLoop);
            if(notification!=null)Post(notification);
        }
        Action Prepare(PhysicalEntry entry) {
            long now=clock();if(now<0||entry.Pending||entry.HasAttempt&&(now<entry.LastAttempt||now-entry.LastAttempt<CoalesceMilliseconds))return null;
            entry.Pending=true;entry.HasAttempt=true;entry.LastAttempt=now;return entry.Deliver;
        }
        void Post(Action action) {
            lock(gate)if(disposed)return;
            try{dispatch(action);}catch(ObjectDisposedException){CancelPost(action);}catch(InvalidOperationException){CancelPost(action);}
        }
        void CancelPost(Action action){lock(gate)foreach(var entry in physical.Values)if(entry.Deliver==action){entry.Pending=false;break;}}
        void ResolveQueued() {
            for(;;) {
                HandleEntry entry;lock(gate){if(disposed||unresolved.Count==0){resolverRunning=false;return;}entry=unresolved.Dequeue();}
                MouseActivitySource source=null;try{source=native.Resolve(entry.Handle);}catch(Exception){}
                if(source==null)Interlocked.Increment(ref unresolvedSourceCount);else Interlocked.Increment(ref resolvedSourceCount);
                Action notification=null;
                lock(gate) {
                    HandleEntry current;
                    if(disposed||entry.Epoch!=epoch||!handles.TryGetValue(entry.Handle,out current)||!Object.ReferenceEquals(entry,current))continue;
                    entry.Source=source;if(source==null)continue;
                    PhysicalEntry group;
                    if(!physical.TryGetValue(source.PhysicalId,out group)){group=new PhysicalEntry(this,source,epoch);physical.Add(source.PhysicalId,group);}
                    else if(group.Source.VendorId!=source.VendorId||group.Source.ProductId!=source.ProductId)continue;
                    entry.Physical=group;notification=Prepare(group);
                }
                if(notification!=null)Post(notification);
            }
        }
        void Deliver(PhysicalEntry entry) {
            MouseActivitySource source;
            lock(gate) {
                PhysicalEntry current;
                if(disposed||entry.Epoch!=epoch||!physical.TryGetValue(entry.Source.PhysicalId,out current)||!Object.ReferenceEquals(entry,current))return;
                entry.Pending=false;long now=clock();if(now<entry.LastAttempt)return;entry.LastAttempt=now;source=entry.Source;
            }
            callback(source);
        }
        internal void InvalidateTopology(){lock(gate){if(disposed)return;epoch++;handles.Clear();physical.Clear();unresolved.Clear();}}
        public void Dispose() {
            lock(gate){if(disposed)return;disposed=true;epoch++;handles.Clear();physical.Clear();unresolved.Clear();}
            try{if(registered){registered=false;native.Unregister();}}finally{try{if(window!=null){window.Close();window=null;}}finally{native.Dispose();}}
        }
    }

    internal sealed class MouseActivityNative:IMouseActivityNative {
        internal const uint HeaderCommand=0x10000005,DeviceNameCommand=0x20000007,InputSink=0x100,DeviceNotify=0x2000,Remove=1;
        [StructLayout(LayoutKind.Sequential)] internal struct RawHeader {internal uint Type,Size;internal IntPtr Device,WParam;}
        [StructLayout(LayoutKind.Sequential)] internal struct Registration {internal ushort UsagePage,Usage;internal uint Flags;internal IntPtr Target;}
        [StructLayout(LayoutKind.Sequential)] struct InterfaceData {internal int Size;internal Guid Guid;internal uint Flags;internal IntPtr Reserved;}
        [StructLayout(LayoutKind.Sequential)] struct DeviceInfo {internal int Size;internal Guid Guid;internal uint Instance;internal IntPtr Reserved;}
        static readonly object registrationGate=new object();static MouseActivityNative registrationOwner;
        static readonly uint HeaderSize=(uint)Marshal.SizeOf(typeof(RawHeader));
        IntPtr header=Marshal.AllocHGlobal((int)HeaderSize);
        [DllImport("user32.dll",SetLastError=true)] static extern uint GetRawInputData(IntPtr input,uint command,IntPtr data,ref uint size,uint headerSize);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)] static extern uint GetRawInputDeviceInfoW(IntPtr device,uint command,IntPtr data,ref uint size);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)] static extern uint GetRawInputDeviceInfoW(IntPtr device,uint command,StringBuilder data,ref uint size);
        [DllImport("user32.dll",SetLastError=true)] static extern bool RegisterRawInputDevices([In] Registration[] devices,uint count,uint size);
        [DllImport("setupapi.dll",SetLastError=true)] static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr guid,IntPtr window);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)] static extern bool SetupDiOpenDeviceInterfaceW(IntPtr set,string path,uint flags,ref InterfaceData data);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)] static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set,ref InterfaceData data,IntPtr detail,uint size,out uint required,IntPtr device);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)] static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set,ref InterfaceData data,IntPtr detail,uint size,out uint required,ref DeviceInfo device);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("cfgmgr32.dll")] static extern uint CM_Get_Parent(out uint parent,uint instance,uint flags);
        [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] static extern uint CM_Get_Device_IDW(uint instance,StringBuilder buffer,uint count,uint flags);
        internal static Registration RegistrationFor(IntPtr target,bool remove){return new Registration {UsagePage=1,Usage=2,Flags=remove?Remove:InputSink|DeviceNotify,Target=remove?IntPtr.Zero:target};}
        public bool Register(IntPtr target) {
            lock(registrationGate){if(registrationOwner!=null||target==IntPtr.Zero)return false;var value=RegistrationFor(target,false);if(!RegisterRawInputDevices(new[]{value},1,(uint)Marshal.SizeOf(typeof(Registration))))return false;registrationOwner=this;return true;}
        }
        public void Unregister(){lock(registrationGate){if(!Object.ReferenceEquals(registrationOwner,this))return;var value=RegistrationFor(IntPtr.Zero,true);RegisterRawInputDevices(new[]{value},1,(uint)Marshal.SizeOf(typeof(Registration)));registrationOwner=null;}}
        public bool ReadHeader(IntPtr input,out uint type,out IntPtr device) {
            type=UInt32.MaxValue;device=IntPtr.Zero;if(header==IntPtr.Zero||input==IntPtr.Zero)return false;
            uint size=HeaderSize;uint copied=GetRawInputData(input,HeaderCommand,header,ref size,HeaderSize);
            if(copied!=HeaderSize||size<HeaderSize||(uint)Marshal.ReadInt32(header,4)<HeaderSize)return false;
            type=(uint)Marshal.ReadInt32(header);device=Marshal.ReadIntPtr(header,8);return true;
        }
        public MouseActivitySource Resolve(IntPtr device) {
            if(device==IntPtr.Zero)return null;uint count=0;
            if(GetRawInputDeviceInfoW(device,DeviceNameCommand,IntPtr.Zero,ref count)==UInt32.MaxValue||count<1||count>32768)return null;
            var path=new StringBuilder((int)count+1);uint capacity=(uint)path.Capacity;
            if(GetRawInputDeviceInfoW(device,DeviceNameCommand,path,ref capacity)==UInt32.MaxValue||capacity>path.Capacity)return null;
            string name=path.ToString();if(name.StartsWith(@"\??\",StringComparison.Ordinal))name=@"\\?\"+name.Substring(4);
            if(!name.StartsWith(@"\\?\HID#",StringComparison.OrdinalIgnoreCase))return null;
            string parent=UsbParent(name);return parent==null?null:MouseActivitySource.FromUsbParent(name,parent);
        }
        static string UsbParent(string path) {
            IntPtr set=SetupDiCreateDeviceInfoList(IntPtr.Zero,IntPtr.Zero);if(set==IntPtr.Zero||set==new IntPtr(-1))return null;
            try {
                var data=new InterfaceData {Size=Marshal.SizeOf(typeof(InterfaceData))};if(!SetupDiOpenDeviceInterfaceW(set,path,0,ref data))return null;
                uint required;SetupDiGetDeviceInterfaceDetailW(set,ref data,IntPtr.Zero,0,out required,IntPtr.Zero);if(required<8||required>65536)return null;
                IntPtr detail=Marshal.AllocHGlobal((int)required);
                try {
                    Marshal.WriteInt32(detail,IntPtr.Size==8?8:6);var info=new DeviceInfo {Size=Marshal.SizeOf(typeof(DeviceInfo))};
                    if(!SetupDiGetDeviceInterfaceDetailW(set,ref data,detail,required,out required,ref info))return null;
                    uint node=info.Instance;
                    for(int i=0;i<12;i++) {
                        var text=new StringBuilder(4096);if(CM_Get_Device_IDW(node,text,(uint)text.Capacity,0)!=0)return null;
                        string value=text.ToString().ToLowerInvariant();var parts=value.Split('\\');
                        if(value.StartsWith(@"usb\vid_",StringComparison.Ordinal)&&parts.Length>1&&!parts[1].Contains("&mi_"))return value;
                        uint parent;if(CM_Get_Parent(out parent,node,0)!=0)break;node=parent;
                    }
                }finally{Marshal.FreeHGlobal(detail);}
            }finally{SetupDiDestroyDeviceInfoList(set);}return null;
        }
        public void Dispose(){Unregister();if(header!=IntPtr.Zero){Marshal.FreeHGlobal(header);header=IntPtr.Zero;}}
    }
}
