using System;
using System.Collections.Generic;
using System.Drawing;
using AtkBattery;

namespace AtkBatteryTray {
    public sealed class BatteryView {
        public string Name;
        public int? Percent;
        public bool Fresh, Charging;
        public DateTime ConfirmedAt;
        public string TimeText {get {var local=ConfirmedAt.ToLocalTime();return ConfirmedAt==DateTime.MinValue?"":local.ToString(local.Date==DateTime.Now.Date?"HH:mm":"M/d HH:mm");}}
        public string Key {get {return Name+"|"+Percent+"|"+Fresh+"|"+Charging+"|"+(!Fresh?TimeText:"");}}
        public string Tooltip {get {return Name+" "+(Percent.HasValue?Percent+"%":"--")+
            (Percent.HasValue&&!Fresh?"（上次 "+TimeText+"）":Charging?" · 充电中":"");}}
    }
    public sealed class TrayView {
        public BatteryView Battery;
        public string Tooltip;
    }
    public sealed class PanelFeedback {
        public int PendingStage=-1;
        public string PendingOutput, DpiFailure, AudioFailure;
        public bool MicrophonePending, ManualRefreshing;
        public int KeyboardFocus=-1;
        public string Key {get {return PendingStage+"|"+PendingOutput+"|"+MicrophonePending+"|"+ManualRefreshing+"|"+DpiFailure+"|"+AudioFailure+"|"+KeyboardFocus;}}
    }
    public static class UiState {
        public static bool Recent(DateTime timestamp,DateTime now,double seconds) {
            return timestamp!=DateTime.MinValue&&timestamp<=now.AddSeconds(5)&&(now-timestamp).TotalSeconds<=seconds;
        }
        public static BatteryView Mouse(BatteryReading current,BatteryReading last,DateTime now) {
            var value=current??last;
            return new BatteryView {Name="鼠标",Percent=value==null?(int?)null:value.Percent,
                Fresh=current!=null&&Recent(current.TimestampUtc,now,90),
                Charging=current!=null&&current.Charging&&Recent(current.TimestampUtc,now,90),
                ConfirmedAt=value==null?DateTime.MinValue:value.TimestampUtc};
        }
        public static BatteryView Headset(HeadsetReading value,DateTime now) {
            bool connected=value!=null&&value.Connected;
            bool radio=connected&&value.ConnectionVerified;
            return new BatteryView {Name="耳机",Percent=connected&&value.Percent.HasValue&&value.Percent>=0&&value.Percent<=100?value.Percent:null,
                Fresh=radio&&value.Percent.HasValue&&Recent(value.BatteryTimestampUtc,now,90),
                Charging=radio&&value.Charging==true&&Recent(value.ChargingTimestampUtc,now,90),
                ConfirmedAt=connected?value.BatteryTimestampUtc:DateTime.MinValue};
        }
        public static TrayView Tray(string source,BatteryView mouse,BatteryView headset) {
            BatteryView chosen=mouse;
            if(source=="headset")chosen=headset;
            else if(source=="lowest") {
                if(headset.Percent.HasValue&&(!mouse.Percent.HasValue||
                    (headset.Fresh==mouse.Fresh?headset.Percent<mouse.Percent:headset.Fresh)))chosen=headset;
            }
            string tooltip=(source=="lowest"?"最低电量 · ":"")+chosen.Name+"\n"+mouse.Tooltip+" | "+headset.Tooltip;
            if(tooltip.Length>63)tooltip=tooltip.Substring(0,62)+"…";
            return new TrayView {Battery=chosen,Tooltip=tooltip};
        }
        public static bool SkipConfirmedStage(DpiConfig dpi,string error,int requested,bool switching,DateTime now) {
            return !switching&&dpi!=null&&dpi.ActiveStage==requested&&String.IsNullOrEmpty(error)&&Recent(dpi.TimestampUtc,now,5);
        }
        public static Size ScaleSize(Size logical,float scale) {
            return new Size((int)Math.Round(logical.Width*scale),(int)Math.Round(logical.Height*scale));
        }
        public static PointF LogicalPoint(Point physical,float scale) {return new PointF(physical.X/scale,physical.Y/scale);}
        public static string EndpointName(AudioReading audio,string id) {
            if(audio!=null&&audio.Devices!=null)foreach(var device in audio.Devices)
                if(String.Equals(id,device.Id,StringComparison.OrdinalIgnoreCase))return device.Name;
            return String.IsNullOrEmpty(id)?"未检测到":"设备未连接";
        }
    }
    public sealed class LowBatteryPolicy {
        readonly Dictionary<string,bool> notified=new Dictionary<string,bool>();
        public bool IsLatched(string device) {bool value;return notified.TryGetValue(device,out value)&&value;}
        public void Restore(string device,bool value) {notified[device]=value;}
        public bool Observe(string device,BatteryView battery,bool mayNotify) {
            if(!battery.Fresh||!battery.Percent.HasValue)return false;
            if(battery.Charging||battery.Percent>=30){notified[device]=false;return false;}
            if(battery.Percent>20||IsLatched(device)||!mayNotify)return false;
            notified[device]=true;return true;
        }
    }
}
