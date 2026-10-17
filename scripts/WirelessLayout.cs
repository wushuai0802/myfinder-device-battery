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
        internal int Height {get{return NativeMouse?VisualDesign.MouseCardHeight(Dpi):NativeHeadset?96:60;}}
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
            if(layout.All.Count==0)layout.All.Add(new WirelessCard {Id="empty",Name="无线设备",Kind="device",Battery=new BatteryView {Name="无线设备"},Subtitle=owner.GenericScanning?"正在识别设备…":String.IsNullOrEmpty(owner.GenericError)?"尚未检测到兼容设备":"设备读取组件暂不可用"});
            layout.Start=Math.Max(0,Math.Min(offset,layout.All.Count-1));
            float y=0;int end=layout.Start;
            foreach(var card in layout.All.GetRange(layout.Start,layout.All.Count-layout.Start)) {
                if(layout.Cards.Count>0&&y+card.Height+30>maximumHeight)break;
                card.Top=y;layout.Cards.Add(card);y+=card.Height+8;end++;
            }
            layout.End=end;layout.Footer=Math.Max(0,y-8);return layout;
        }
        internal static BatteryView View(WirelessDevice device) {
            bool fresh=device.Fresh&&device.Online&&UiState.Recent(device.ConfirmedTime,DateTime.UtcNow,90);
            return new BatteryView {Name=device.Name,Percent=device.Percent,Fresh=fresh,Charging=fresh&&device.Charging,ConfirmedAt=device.ConfirmedTime,Approximate=device.Approximate};
        }
    }
}
