using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using AtkBattery;

namespace AtkBatteryTray {
    internal static class TraySelection {
        internal static WirelessCard Select(IEnumerable<WirelessCard> candidates,string source,string fixedId,string activePhysicalId,bool activeNative,string previousId,bool groupRules=false){
            var available=new List<WirelessCard>();foreach(var card in candidates)if(card!=null&&card.Battery!=null&&!card.AudioOnly&&card.Id!="empty")available.Add(card);
            WirelessCard selected=null;
            if(!String.IsNullOrEmpty(fixedId))selected=available.Find(delegate(WirelessCard card){return card.Id==fixedId&&card.Battery.Fresh&&card.Battery.Percent.HasValue;});
            bool fixedSelected=selected!=null;
            if(source=="active"){
                bool current=available.Exists(delegate(WirelessCard card){return card.Battery.Fresh&&card.Battery.Percent.HasValue;});
                if(selected==null&&activePhysicalId!=null)selected=available.Find(delegate(WirelessCard card){return card.Battery.Percent.HasValue&&(!groupRules||card.Battery.Fresh||!current)&&(activeNative?card.NativeMouse:card.Kind=="mouse"&&card.Device!=null&&card.Device.PhysicalId==activePhysicalId&&card.Device.Online&&card.Device.ConnectionState!="disconnected");});
                if(selected!=null)fixedSelected=true;
                source=groupRules?"lowest":"auto";
            }
            if(selected==null&&source=="auto")selected=available.Find(delegate(WirelessCard card){return card.Id==previousId&&card.Battery.Fresh&&card.Battery.Percent.HasValue;});
            foreach(var card in available){
                if(fixedSelected||selected!=null&&source=="auto"&&selected.Id==previousId)break;
                if(source!="auto"&&source!="lowest"&&card.Kind!=source)continue;
                if(selected==null||card.Battery.Fresh&&!selected.Battery.Fresh||card.Battery.Fresh==selected.Battery.Fresh&&(source=="lowest"?card.Battery.Percent.HasValue&&(!selected.Battery.Percent.HasValue||card.Battery.Percent<selected.Battery.Percent):!selected.Battery.Percent.HasValue&&card.Battery.Percent.HasValue))selected=card;
            }
            if(source=="lowest"&&!fixedSelected&&selected!=null){var previous=available.Find(delegate(WirelessCard card){return card.Id==previousId&&card.Battery.Fresh==selected.Battery.Fresh&&card.Battery.Percent==selected.Battery.Percent;});if(previous!=null)selected=previous;}
            if(selected==null&&available.Count>0){selected=available[0];foreach(var card in available)if(card.Battery.Fresh&&card.Battery.Percent.HasValue){selected=card;break;}}
            return selected;
        }
        internal static TrayView Present(List<WirelessCard> candidates,WirelessCard selected,string groupName=null){
            var battery=selected==null?new BatteryView {Name="无线设备"}:selected.Battery;
            string tooltip;
            if(groupName!=null){
                if(selected==null)tooltip=groupName+"：没有已连接的设备";
                else {
                    string prefix=groupName+"：",suffix=" "+(battery.Percent.HasValue?(battery.Approximate?"约":"")+battery.Percent+"%":"--")+(battery.Percent.HasValue&&!battery.Fresh?"（历史读数）":battery.Charging?" · 充电中":"");
                    tooltip=prefix+Fit(selected.Name,63-prefix.Length-suffix.Length)+suffix;
                }
            }else {
                var text=new StringBuilder(selected==null?"无线设备":battery.TooltipFor(selected.Name));bool extra=false;
                foreach(var card in candidates){if(card==selected)continue;text.Append(extra?" | ":"\n").Append(card.Battery.TooltipFor(card.Name));extra=true;}
                tooltip=text.ToString();if(tooltip.Length>63)tooltip=Fit(tooltip,63);
            }
            return new TrayView {Battery=battery,Tooltip=tooltip,DeviceKind=selected==null?"device":selected.Kind};
        }
        static string Fit(string value,int length){value=value??"无线设备";if(value.Length<=length)return value;int take=Math.Max(0,length-1);if(take>0&&Char.IsHighSurrogate(value[take-1]))take--;return value.Substring(0,take)+"…";}
    }
    internal interface ITrayIcon:IDisposable {
        bool Update(TrayView view,string style,bool dark,int size);
        void Recreate();
    }
    internal sealed class NativeTrayIcon:ITrayIcon {
        readonly NotifyIcon tray;readonly bool visible;
        Icon icon;string lastKey,lastTooltip;
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
        internal NativeTrayIcon(NotifyIcon tray,bool visible){this.tray=tray;this.visible=visible;}
        public bool Update(TrayView view,string style,bool dark,int size){
            var battery=view.Battery;bool taskbarDark=Style.TaskbarDark;
            string key=battery.Key+"|"+style+"|"+view.DeviceKind+"|"+dark+"|"+size+"|"+view.UpdateBadge+"|"+(view.UpdateBadge?view.UpdateColor.ToArgb():0)+"|"+taskbarDark;bool changed=key!=lastKey;
            if(changed){
                Color? badge=view.UpdateBadge?(Color?)view.UpdateColor:null;
                using(var bitmap=style=="device"?TrayGlyph.RenderDevice(battery.Percent,battery.Charging,dark,battery.Percent.HasValue&&!battery.Fresh,size,view.DeviceKind,badge,taskbarDark):TrayGlyph.Render(battery.Percent,battery.Charging,dark,battery.Percent.HasValue&&!battery.Fresh,size,battery.Approximate,badge,taskbarDark)){
                    IntPtr handle=bitmap.GetHicon();Icon next;try{next=(Icon)Icon.FromHandle(handle).Clone();}finally{DestroyIcon(handle);}
                    var old=icon;icon=next;tray.Icon=icon;if(old!=null)old.Dispose();
                }
                lastKey=key;
            }
            if(view.Tooltip!=lastTooltip){tray.Text=view.Tooltip;lastTooltip=view.Tooltip;}
            tray.Visible=visible;return changed;
        }
        public void Recreate(){tray.Visible=false;tray.Visible=visible&&icon!=null;}
        public void Dispose(){tray.Visible=false;tray.Dispose();if(icon!=null){icon.Dispose();icon=null;}}
    }
    // Owns the original icon for its entire lifetime. Only changed IDs are created
    // or disposed; Explorer recovery re-adds each existing instance to the shell.
    internal sealed class TrayIconSet:IDisposable {
        internal readonly ITrayIcon First;
        readonly Func<string,ITrayIcon> factory;
        readonly Dictionary<string,ITrayIcon> extras=new Dictionary<string,ITrayIcon>(StringComparer.Ordinal);
        bool disposed;
        internal TrayIconSet(ITrayIcon first,Func<string,ITrayIcon> create){First=first;factory=create;}
        internal void Sync(IEnumerable<TrayGroup> groups){
            if(disposed)return;var ids=new HashSet<string>(StringComparer.Ordinal);foreach(var group in groups)ids.Add(group.Id);
            foreach(string id in new List<string>(extras.Keys))if(!ids.Contains(id)){extras[id].Dispose();extras.Remove(id);}
            foreach(var group in groups)if(!extras.ContainsKey(group.Id))extras.Add(group.Id,factory(group.Id));
        }
        internal ITrayIcon For(string id){return id==TrayConfiguration.FirstId?First:extras[id];}
        internal void RecreateAll(){if(disposed)return;First.Recreate();foreach(var icon in extras.Values)icon.Recreate();}
        public void Dispose(){if(disposed)return;disposed=true;First.Dispose();foreach(var icon in extras.Values)icon.Dispose();extras.Clear();}
    }
}
