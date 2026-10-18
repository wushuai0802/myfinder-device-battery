using System;
using System.Collections.Generic;
using System.Drawing;
using AtkBattery;

namespace AtkBatteryTray {
    internal sealed class WirelessCard {
        internal string Id,Name,Kind,Subtitle;
        internal BatteryView Battery;
        internal WirelessDevice Device;
        internal bool NativeMouse,NativeHeadset,Hidden;
        internal int Height {get{return NativeMouse?VisualDesign.MouseCardHeight(Dpi):NativeHeadset?96:WirelessLayout.DpiStageCount(Device)>0?WirelessLayout.DpiStageCount(Device)>4?126:96:VisualDesign.GenericCardHeight;}}
        internal DpiConfig Dpi;
        internal float Top;
    }
    internal sealed class WirelessLayout {
        internal readonly List<WirelessCard> Cards=new List<WirelessCard>();
        internal readonly List<WirelessCard> All=new List<WirelessCard>();
        internal int Start,End;
        internal float Footer;
        internal bool HasVolume=true;
        internal float VolumeTop;
        internal RectangleF VolumeRect {get{return new RectangleF(0,VolumeTop,260,VisualDesign.SystemVolumeHeight);}}
        internal bool Paged {get{return Start>0||End<All.Count;}}
        internal Size Size {get{return new Size(260,(int)Footer+30);}}
        internal static WirelessLayout Build(TrayContext owner,int offset,int maximumHeight,bool includeHidden=false) {
            var layout=new WirelessLayout();var pref=DeviceSettings.Current;var now=DateTime.UtcNow;
            if(owner.NativeMousePresent)layout.All.Add(new WirelessCard {Id="native:mouse",Name=pref.MouseAlias,Kind="mouse",Battery=UiState.Mouse(owner.Current,owner.LastGood,now),NativeMouse=true,Dpi=owner.Dpi});
            foreach(var device in owner.WirelessDevices) {
                string alias;string name=pref.DeviceAliases!=null&&pref.DeviceAliases.TryGetValue(device.Id,out alias)?alias:device.Name;
                layout.All.Add(new WirelessCard {Id=device.Id,Name=name,Kind=device.Kind,Device=device,Battery=View(device)});
            }
            if(VisualDesign.ShouldShowHeadset(owner.Headset))layout.All.Add(new WirelessCard {Id="native:headset",Name=pref.HeadsetAlias,Kind="headset",Battery=UiState.Headset(owner.Headset,now),NativeHeadset=true});
            var hidden=new HashSet<string>(pref.HiddenDeviceIds,StringComparer.Ordinal);
            if(includeHidden)foreach(string id in pref.HiddenDeviceIds)if(!layout.All.Exists(delegate(WirelessCard card){return card.Id==id;})) {
                string alias;string name=id=="native:mouse"?pref.MouseAlias:id=="native:headset"?pref.HeadsetAlias:pref.DeviceAliases.TryGetValue(id,out alias)?alias:"已隐藏设备";
                layout.All.Add(new WirelessCard {Id=id,Name=name,Kind="device",Battery=new BatteryView {Name=name},Subtitle="设备未连接"});
            }
            var discovered=new List<WirelessCard>(layout.All);layout.All.Clear();var added=new HashSet<string>(StringComparer.Ordinal);
            Action<WirelessCard> add=delegate(WirelessCard card){if(card==null||!added.Add(card.Id))return;card.Hidden=hidden.Contains(card.Id);if(includeHidden||!card.Hidden)layout.All.Add(card);};
            foreach(string id in pref.DeviceOrder)add(discovered.Find(delegate(WirelessCard card){return card.Id==id;}));
            foreach(var card in discovered)add(card);
            if(layout.All.Count==0)layout.All.Add(new WirelessCard {Id="empty",Name="无线设备",Kind="device",Battery=new BatteryView {Name="无线设备"},Subtitle=discovered.Count>0?"设备已隐藏，可在设备管理中恢复":owner.GenericScanning?"正在识别设备…":!String.IsNullOrEmpty(owner.GenericError)?"设备读取组件暂不可用":owner.GenericFailureCount>0||!String.IsNullOrEmpty(owner.GenericHealthSummary)?"部分读取器暂不可用":!owner.GenericReady?"设备读取组件尚未就绪":"尚未连接兼容设备"});
            layout.Start=Math.Max(0,Math.Min(offset,layout.All.Count-1));
            float y=0;int end=layout.Start;
            foreach(var card in layout.All.GetRange(layout.Start,layout.All.Count-layout.Start)) {
                if(layout.Cards.Count>0&&y+card.Height+VisualDesign.CardGap+VisualDesign.SystemVolumeHeight+VisualDesign.FooterHeight>maximumHeight)break;
                card.Top=y;layout.Cards.Add(card);y+=card.Height+VisualDesign.CardGap;end++;
            }
            layout.End=end;layout.VolumeTop=Math.Max(0,y);layout.Footer=layout.VolumeTop+VisualDesign.SystemVolumeHeight;return layout;
        }
        internal static BatteryView View(WirelessDevice device) {
            bool system=device.SourceQuality=="system_property";
            bool fresh=device.Fresh&&device.Online&&device.ConnectionState!="unconfirmed"&&device.ConnectionState!="disconnected"&&!system&&device.MeasurementState!="cached"&&UiState.Recent(device.ConfirmedTime,DateTime.UtcNow,90);
            DateTime chargeTime;bool chargeKnown=DateTime.TryParse(device.ChargingCheckedAtUtc,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out chargeTime);
            return new BatteryView {Name=device.Name,Percent=device.Percent,Fresh=fresh,Charging=fresh&&device.Charging==true&&chargeKnown&&UiState.Recent(chargeTime.ToUniversalTime(),DateTime.UtcNow,90),ConfirmedAt=device.ConfirmedTime,Approximate=device.Approximate,SystemProvided=system,QueriedAt=device.QueryTime};
        }
        internal static string Subtitle(WirelessDevice device,BatteryView value) {
            if(!value.Fresh&&device.ConnectionState=="unconfirmed")return "连接待确认";
            if(device.ConnectionState=="disconnected")return value.Percent.HasValue&&!String.IsNullOrEmpty(value.TimeText)?"已断开 · 上次 "+value.TimeText:"设备已断开";
            if(value.SystemProvided&&value.Percent.HasValue)return "系统提供"+(String.IsNullOrEmpty(value.QueryText)?"":" · 最近查询 "+value.QueryText);
            if(value.Percent.HasValue&&!value.Fresh)return String.IsNullOrEmpty(value.TimeText)?"历史读数 · 时间未知":"上次确认 "+value.TimeText;
            return value.Fresh?device.Transport+" · 已连接":device.Online?"电量暂不可读":"连接待确认";
        }
        internal static string RefreshText(WirelessDevice device){return device!=null&&device.RefreshScope=="provider"?"同步同组设备":"同步此设备";}
        internal static int DpiStageCount(WirelessDevice device){return device!=null&&device.Kind=="mouse"&&device.Dpi!=null&&device.Dpi.Supported&&device.Dpi.Stages!=null?Math.Min(8,device.Dpi.Stages.Length):0;}
        internal static string DpiLabel(WirelessDevice device){return device!=null&&device.Dpi!=null&&device.Dpi.StageSource=="quick"?"快捷DPI":"DPI";}
        internal static bool DpiFresh(WirelessDevice device) {
            return device!=null&&device.Dpi!=null&&device.Dpi.Supported&&device.Dpi.Current.HasValue&&WirelessDpi.ValidValue(device.Dpi.Current.Value)&&String.IsNullOrEmpty(device.Dpi.Error)&&WirelessDpi.ValidToken(device.Dpi.TargetToken)&&device.Online&&device.ConnectionState=="connected"&&UiState.Recent(device.Dpi.CheckedTime,DateTime.UtcNow,90);
        }
        internal static string DpiSubtitle(WirelessDevice device,int? pending,string failure) {
            string label=DpiLabel(device);if(pending.HasValue)return label+" · 等待 "+pending.Value;
            if(!String.IsNullOrEmpty(failure))return label+" · 状态待确认";
            if(device.Dpi.Current.HasValue)return label+" · "+(DpiFresh(device)?"":"上次 ")+device.Dpi.Current.Value;
            return label+" · 未读取";
        }
        internal static RectangleF DpiRect(WirelessCard card,int stage) {var rect=VisualDesign.StageRect(stage,DpiStageCount(card.Device));rect.Y+=card.Top;return rect;}
        internal static int DpiActionId(int cardIndex,int stage){return 3000+cardIndex*8+stage;}
    }
}
