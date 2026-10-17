using System;
using System.Collections.Generic;
using System.Drawing;
using AtkBattery;

namespace AtkBatteryTray {
    internal sealed class WirelessCard {
        internal string Id,Name,Kind,Subtitle;
        internal BatteryView Battery;
        internal WirelessDevice Device;
        internal bool NativeMouse,NativeHeadset;
        internal int Height {get{return NativeMouse?VisualDesign.MouseCardHeight(Dpi):NativeHeadset?96:VisualDesign.GenericCardHeight;}}
        internal DpiConfig Dpi;
        internal float Top;
    }
    internal sealed class WirelessLayout {
        internal readonly List<WirelessCard> Cards=new List<WirelessCard>();
        internal readonly List<WirelessCard> All=new List<WirelessCard>();
        internal int Start,End;
        internal float Footer;
        internal bool Paged {get{return Start>0||End<All.Count;}}
        internal Size Size {get{return new Size(260,(int)Footer+30);}}
        internal static WirelessLayout Build(TrayContext owner,int offset,int maximumHeight) {
            var layout=new WirelessLayout();var pref=DeviceSettings.Current;var now=DateTime.UtcNow;
            if(owner.NativeMousePresent)layout.All.Add(new WirelessCard {Id="native:mouse",Name=pref.MouseAlias,Kind="mouse",Battery=UiState.Mouse(owner.Current,owner.LastGood,now),NativeMouse=true,Dpi=owner.Dpi});
            foreach(var device in owner.WirelessDevices) {
                string alias;string name=pref.DeviceAliases!=null&&pref.DeviceAliases.TryGetValue(device.Id,out alias)?alias:device.Name;
                layout.All.Add(new WirelessCard {Id=device.Id,Name=name,Kind=device.Kind,Device=device,Battery=View(device)});
            }
            if(VisualDesign.ShouldShowHeadset(owner.Headset))layout.All.Add(new WirelessCard {Id="native:headset",Name=pref.HeadsetAlias,Kind="headset",Battery=UiState.Headset(owner.Headset,now),NativeHeadset=true});
            if(layout.All.Count==0)layout.All.Add(new WirelessCard {Id="empty",Name="无线设备",Kind="device",Battery=new BatteryView {Name="无线设备"},Subtitle=owner.GenericScanning?"正在识别设备…":!String.IsNullOrEmpty(owner.GenericError)?"设备读取组件暂不可用":owner.GenericFailureCount>0||!String.IsNullOrEmpty(owner.GenericHealthSummary)?"部分读取器暂不可用":!owner.GenericReady?"设备读取组件尚未就绪":"尚未连接兼容设备"});
            layout.Start=Math.Max(0,Math.Min(offset,layout.All.Count-1));
            float y=0;int end=layout.Start;
            foreach(var card in layout.All.GetRange(layout.Start,layout.All.Count-layout.Start)) {
                if(layout.Cards.Count>0&&y+card.Height+30>maximumHeight)break;
                card.Top=y;layout.Cards.Add(card);y+=card.Height+VisualDesign.CardGap;end++;
            }
            layout.End=end;layout.Footer=Math.Max(0,y-VisualDesign.CardGap);return layout;
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
    }
}
