using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace AtkBattery {
    public static partial class AudioControl {
        [StructLayout(LayoutKind.Sequential)] public struct NotificationKey {public Guid Format;public uint Id;}
        [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown),ComVisible(true)]
        public interface IEndpointNotifications {
            [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id,uint state);
            [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int OnDefaultDeviceChanged(int flow,int role,[MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id,NotificationKey key);
        }
        [Guid("657804FA-D6AD-4496-8A60-352752AF4F89"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown),ComVisible(true)]
        public interface IVolumeNotifications {[PreserveSig] int OnNotify(IntPtr data);}
        [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
        internal sealed class NotificationListener:IDisposable,IEndpointNotifications,IVolumeNotifications {
            readonly object gate=new object();readonly Action changed;IMMDeviceEnumerator enumerator;IntPtr endpointPointer,volumePointer;
            readonly List<object> volumes=new List<object>();volatile bool disposed;int queued,pendingTopology;string microphoneTarget;
            internal bool Ready {get;private set;}
            internal NotificationListener(Action callback){changed=callback;enumerator=(IMMDeviceEnumerator)new MMDeviceEnumerator();endpointPointer=Marshal.GetComInterfaceForObject(this,typeof(IEndpointNotifications));volumePointer=Marshal.GetComInterfaceForObject(this,typeof(IVolumeNotifications));
                try{Check(enumerator.RegisterEndpointNotificationCallback(endpointPointer),"注册音频变化通知");Ready=true;Refresh();}catch{Dispose();throw;}}
            void Signal(bool topology){if(disposed)return;if(topology)Interlocked.Exchange(ref pendingTopology,1);if(Interlocked.Exchange(ref queued,1)!=0)return;ThreadPool.QueueUserWorkItem(delegate{try{do{bool rebind=Interlocked.Exchange(ref pendingTopology,0)!=0;if(disposed)break;if(rebind)Refresh();changed();}while(!disposed&&Interlocked.CompareExchange(ref pendingTopology,0,0)!=0);}catch(Exception){}finally{Interlocked.Exchange(ref queued,0);if(!disposed&&Interlocked.CompareExchange(ref pendingTopology,0,0)!=0)Signal(false);}});}
            internal void SetMicrophoneTarget(string id){string previous=Interlocked.Exchange(ref microphoneTarget,id);if(!String.Equals(previous,id,StringComparison.OrdinalIgnoreCase))Signal(true);}
            void Refresh(){lock(gate){if(disposed)return;ClearVolumes();
                foreach(int flow in new[]{0,1}){IMMDevice device=null;object volume=null;try{string selected=flow==1?(microphoneTarget??DeviceSettings.Current.MicrophoneEndpointId):null;int result=String.IsNullOrEmpty(selected)?enumerator.GetDefaultAudioEndpoint(flow,Role.Console,out device):enumerator.GetDevice(selected,out device);if(result<0||device==null)continue;Guid iid=VolumeInterfaceId;if(device.Activate(ref iid,23,IntPtr.Zero,out volume)<0||volume==null)continue;if(((IAudioEndpointVolume)volume).RegisterControlChangeNotify(volumePointer)>=0){volumes.Add(volume);volume=null;}}finally{ReleaseCom(volume);ReleaseCom(device);}}
            }}
            void ClearVolumes(){foreach(var value in volumes){try{((IAudioEndpointVolume)value).UnregisterControlChangeNotify(volumePointer);}catch(COMException){}ReleaseCom(value);}volumes.Clear();}
            public int OnDeviceStateChanged(string id,uint state){Signal(true);return 0;}public int OnDeviceAdded(string id){Signal(true);return 0;}public int OnDeviceRemoved(string id){Signal(true);return 0;}public int OnDefaultDeviceChanged(int flow,int role,string id){Signal(true);return 0;}public int OnPropertyValueChanged(string id,NotificationKey key){Signal(false);return 0;}public int OnNotify(IntPtr data){Signal(false);return 0;}
            public void Dispose(){disposed=true;Ready=false;if(!Monitor.TryEnter(gate,20))return;try{ClearVolumes();if(enumerator!=null&&endpointPointer!=IntPtr.Zero)try{enumerator.UnregisterEndpointNotificationCallback(endpointPointer);}catch(COMException){}ReleaseCom(enumerator);enumerator=null;if(endpointPointer!=IntPtr.Zero){Marshal.Release(endpointPointer);endpointPointer=IntPtr.Zero;}if(volumePointer!=IntPtr.Zero){Marshal.Release(volumePointer);volumePointer=IntPtr.Zero;}}finally{Monitor.Exit(gate);}}
        }
        internal static NotificationListener Listen(Action changed){return new NotificationListener(changed);}
    }
}
