using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

// Compile this single suite under TEMP. Readers and configuration writes are fake.
static class TrayGroupsTests {
    static int checks,failures;
    static string output;
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
    static void Run(string name,Action action){try{action();Console.WriteLine("PASS "+name);}catch(Exception error){failures++;Console.Error.WriteLine("FAIL "+name+": "+(error.InnerException??error).Message);}}
    static void Contracts(){
        Check(typeof(AudioBindings).GetField("TrayGroups")!=null,"additional tray groups are persisted");
        Check(typeof(AudioBindings).GetField("TrayIconStyle")!=null,"global tray icon style is persisted");
    }
    static void SelectionContract(){Check(typeof(TrayContext).Assembly.GetType("AtkBatteryTray.TraySelection")!=null,"tray selection has a pure, independently testable entry point");}
    static void LifecycleContract(){Check(typeof(TrayContext).Assembly.GetType("AtkBatteryTray.TrayIconSet")!=null,"tray icon lifecycle accepts a fake icon factory");}
    static void DeviceStyleContract(){Check(typeof(TrayGlyph).GetMethod("RenderDevice")!=null,"device glyph and battery bar renderer exists");}
    static void PreferencesEntry(){
        DeviceSettings.UseTransientConfiguration(new AudioBindings {RememberReadings=false,ReducedMotion=true});
        var owner=new TrayContext(false,delegate(bool battery){return new MouseSnapshot();},delegate(int stage){throw new Exception("No device writes");});
        try {
            var rows=PopupPages.Build(owner,new PopupNavigation {Page=PopupPage.Preferences,VisibleChoices=100},false);
            var row=rows.Find(delegate(PopupControl item){return item.Id==211;});
            Check(row!=null&&row.Text=="托盘图标"&&row.Value=="1 个","preferences opens the tray icon count page");
        }finally{owner.ExitThread();owner.Dispose();}
    }
    static AudioBindings Load(string fields){string path=Path.Combine(output,"tray-settings.json");File.WriteAllText(path,"{\"SchemaVersion\":1,\"Theme\":\"dark\",\"MouseAlias\":\"保留名字\",\"WarningThreshold\":15,\"TraySource\":\"keyboard\",\"TrayDeviceId\":\"legacy-pin\""+fields+"}");return DeviceSettings.LoadBindings(path);}
    static void Kept(AudioBindings pref){Check(pref.Theme=="dark"&&pref.MouseAlias=="保留名字"&&pref.WarningThreshold==15&&pref.TraySource=="keyboard"&&pref.TrayDeviceId=="legacy-pin","new field validation preserves unrelated and legacy first-group settings");}
    static void Configuration(){
        var old=Load("");Kept(old);Check(old.TrayGroups.Count==0&&old.TrayIconStyle=="number","old configuration means one numeric icon");
        var pref=Load(",\"TrayGroups\":[{\"Id\":\"g2\",\"Name\":\" A\\u0001B \",\"Source\":\"wrong\",\"DeviceId\":\"bad\\n\",\"Members\":[\"one\",\"one\",null,42,\"two\",\"bad\\n\"]},{\"Id\":\"g3\",\"Name\":\"123456789012345\",\"Source\":\"active\",\"Members\":[\"one\",\"three\"]},{\"Id\":\"g4\",\"Name\":\"\\u0001\",\"Members\":[\"four\"]},{\"Id\":\"g5\",\"Members\":[\"five\"]}],\"TrayIconStyle\":\"future\"");
        Kept(pref);Check(pref.TrayGroups.Count==3&&pref.TrayIconStyle=="number","excess groups and unknown style are handled locally");
        Check(pref.TrayGroups[0].Name=="AB"&&pref.TrayGroups[0].Source=="lowest"&&pref.TrayGroups[0].DeviceId==null,"control characters, invalid rule and invalid fixed ID are sanitized");
        Check(String.Join(",",pref.TrayGroups[0].Members.ToArray())=="one,two"&&String.Join(",",pref.TrayGroups[1].Members.ToArray())=="three","earlier groups own duplicate members");
        Check(pref.TrayGroups[1].Name.Length==12&&pref.TrayGroups[2].Name=="托盘图标 4","long and empty group names remain valid");
        foreach(string field in new[]{",\"TrayGroups\":true,\"TrayIconStyle\":{}",",\"TrayGroups\":[null,4,[],{\"Id\":4},{\"Id\":\"g1\"},{\"Id\":\"ok\",\"Name\":[],\"Source\":{},\"Members\":false,\"DeviceId\":[]},{\"Id\":\"ok\"},{\"Id\":\"later\",\"Members\":[\"valid\",{}]}],\"TrayIconStyle\":false"}){
            var malformed=Load(field);Kept(malformed);Check(malformed.TrayIconStyle=="number","wrong JSON style type defaults locally");
            if(malformed.TrayGroups.Count>0)Check(malformed.TrayGroups.Count==2&&malformed.TrayGroups[0].Members.Count==0&&malformed.TrayGroups[1].Members[0]=="valid","invalid groups and fields do not discard later valid groups");
        }
        var clone=pref.Clone();clone.TrayGroups[0].Members.Add("copy-only");Check(!pref.TrayGroups[0].Members.Contains("copy-only"),"group clones do not share member lists");
        for(int i=0;i<80;i++)pref.TrayGroups[0].Members.Add("member-"+i);
        pref.TrayIconStyle="device";pref.TrayGroupHintShown=true;string path=Path.Combine(output,"tray-roundtrip.json");DeviceSettings.SaveBindings(path,pref);var loaded=DeviceSettings.LoadBindings(path);Kept(loaded);
        Check(loaded.TrayGroups[0].Members.Count==64&&loaded.TrayIconStyle=="device"&&loaded.TrayGroupHintShown,"member cap, style and one-time hint survive a round trip");
        Check(File.ReadAllText(path).Contains("\"TraySource\":\"keyboard\"")&&File.ReadAllText(path).Contains("\"TrayDeviceId\":\"legacy-pin\""),"first group still writes downgrade-compatible legacy fields");
        var member=(DataMemberAttribute)Attribute.GetCustomAttribute(typeof(AudioBindings).GetField("TrayGroups"),typeof(DataMemberAttribute));Check(member.Order==22,"TrayGroups uses the next DataMember order");
    }
    static void GroupEditing(){
        var pref=new AudioBindings();var a=TrayConfiguration.Add(pref);var b=TrayConfiguration.Add(pref);var c=TrayConfiguration.Add(pref);
        Check(a.Name=="托盘图标 2"&&b.Name=="托盘图标 3"&&c.Name=="托盘图标 4","new groups have numbered default names");
        bool rejected=false;try{TrayConfiguration.Add(pref);}catch(InvalidOperationException){rejected=true;}Check(rejected&&pref.TrayGroups.Count==3,"at most four icons can be added");
        TrayConfiguration.Move(pref,"mouse",a.Id);a.DeviceId="mouse";TrayConfiguration.Move(pref,"mouse",b.Id);
        Check(a.Members.Count==0&&a.DeviceId==null&&b.Members.Contains("mouse")&&TrayConfiguration.Owner(pref,"mouse")==b.Id,"moving a device is exclusive and clears a stale pin in the old group");
        TrayConfiguration.Move(pref,"mouse",TrayConfiguration.FirstId);Check(b.Members.Count==0&&TrayConfiguration.Owner(pref,"mouse")==TrayConfiguration.FirstId,"removing a member returns it to the first group");
        TrayConfiguration.Rename(pref,b.Id," 名\n字\t123456789012345 ");Check(b.Name=="名字1234567890"&&b.Name.Length==12,"rename strips controls and enforces twelve characters");
        TrayConfiguration.Rename(pref,b.Id," \n ");Check(b.Name=="名字1234567890","empty rename retains a valid name");
        TrayConfiguration.Move(pref,"headset",b.Id);string stable=c.Id;TrayConfiguration.Remove(pref,b.Id);Check(TrayConfiguration.Owner(pref,"headset")==TrayConfiguration.FirstId&&c.Id==stable,"deleting a group returns its members without renumbering other IDs");
        var replacement=TrayConfiguration.Add(pref);Check(replacement.Id!=b.Id&&replacement.Id!=c.Id,"new IDs are not recycled after deletion");Check(replacement.Name=="托盘图标 3","replacement groups use an available default name without renaming survivors");
        for(int i=0;i<64;i++)TrayConfiguration.Move(pref,"full-"+i,a.Id);
        TrayConfiguration.Move(pref,"held",c.Id);rejected=false;try{TrayConfiguration.Move(pref,"held",a.Id);}catch(InvalidOperationException){rejected=true;}
        Check(rejected&&c.Members.Contains("held"),"a full target group cannot steal a device from its current group");
        rejected=false;try{TrayConfiguration.Remove(pref,TrayConfiguration.FirstId);}catch(InvalidOperationException){rejected=true;}Check(rejected,"the first group cannot be deleted");
    }
    static WirelessCard Card(string id,string kind,int? percent,bool fresh=true){return new WirelessCard {Id=id,Name=id,Kind=kind,Battery=new BatteryView {Name=id,Percent=percent,Fresh=fresh},Device=new WirelessDevice {Id=id,Kind=kind,PhysicalId="physical-"+id,Online=true,ConnectionState="connected"}};}
    static void Selection(){
        var mouse=Card("mouse","mouse",80);var keyboard=Card("keyboard","keyboard",20);var headset=Card("headset","headset",46);var unknown=Card("unknown","device",null,false);var history=Card("history","mouse",1,false);
        var cards=new List<WirelessCard>{history,mouse,keyboard,headset,unknown};
        foreach(string source in new[]{"mouse","keyboard","headset","lowest","auto","active"}){
            var selected=TraySelection.Select(cards,source,null,null,false,null);
            Check(selected==(source=="keyboard"||source=="lowest"?keyboard:source=="headset"?headset:mouse),"legacy rule "+source+" keeps current-reading priority");
        }
        Check(TraySelection.Select(cards,"lowest","mouse",null,false,null)==mouse,"a fixed device with a current reading wins");
        Check(TraySelection.Select(cards,"lowest","history",null,false,null)==keyboard&&TraySelection.Select(cards,"lowest","absent",null,false,null)==keyboard,"stale and disconnected pins fall back to the rule");
        Check(TraySelection.Select(cards,"auto",null,null,false,"headset")==headset,"auto preserves a current previous device");
        Check(TraySelection.Select(cards,"active",null,"physical-mouse",false,"keyboard",true)==mouse,"an active mouse in the group wins");
        Check(TraySelection.Select(cards,"active",null,"other-group",false,"mouse",true)==keyboard,"an active mouse outside the group falls back to lowest");
        Check(TraySelection.Select(cards,"active",null,"physical-history",false,null,true)==keyboard,"a historical active mouse cannot replace a current reading in an extra group");
        Check(TraySelection.Select(cards,"active",null,"physical-history",false,null)==history,"legacy active selection behavior remains unchanged");
        var native=Card("native:mouse","mouse",90);native.NativeMouse=true;cards.Add(native);
        Check(TraySelection.Select(cards,"active",null,"native",true,null,true)==native,"native active mouse selection is supported");
        headset.Battery.Percent=20;Check(TraySelection.Select(cards,"lowest",null,null,false,"headset",true)==headset,"equal lowest values retain the previous device");
        Check(TraySelection.Select(new WirelessCard[0],"active",null,"mouse",false,"mouse",true)==null,"empty groups return no card");
        Check(TraySelection.Select(new[]{unknown,history},"lowest",null,null,false,null,true)==history,"historical readings beat absent readings when no current reading exists");
        Check(TraySelection.Select(new[]{keyboard},"mouse",null,null,false,null)==keyboard,"a missing legacy device type retains the existing fallback");
    }
    static void Tooltips(){
        var card=Card("device","mouse",46);card.Name="Logitech G Pro";var cards=new List<WirelessCard>{card};
        Check(TraySelection.Present(cards,card,"工作设备").Tooltip=="工作设备：Logitech G Pro 46%","multiple icons use group and device names");
        card.Name=new String('设',90)+"😀";var view=TraySelection.Present(cards,card,"托盘图标 2");
        Check(view.Tooltip.Length==63&&view.Tooltip.EndsWith("… 46%"),"only the device name is truncated at the 63-character limit");
        card.Name=new String('a',48)+"😀😀😀😀😀";view=TraySelection.Present(cards,card,"托盘图标 2");for(int i=0;i<view.Tooltip.Length;i++)if(Char.IsHighSurrogate(view.Tooltip[i]))Check(i+1<view.Tooltip.Length&&Char.IsLowSurrogate(view.Tooltip[++i]),"tooltip truncation preserves surrogate pairs");
        card.Name="鼠标";Check(TraySelection.Present(cards,card).Tooltip==card.Battery.TooltipFor(card.Name),"one-group tooltip format remains unchanged");
        view=TraySelection.Present(new List<WirelessCard>(),null,"游戏设备");Check(view.Battery.Percent==null&&view.Tooltip=="游戏设备：没有已连接的设备","empty icons remain present with the required message");
    }
    sealed class FakeIcon:ITrayIcon {
        internal int Disposals,Recreations;public bool Update(TrayView view,string style,bool dark,int size){return false;}public void Recreate(){Recreations++;}public void Dispose(){Disposals++;}
    }
    static void Lifecycle(){
        var first=new FakeIcon();var made=new Dictionary<string,FakeIcon>();int creations=0;
        var icons=new TrayIconSet(first,delegate(string id){creations++;var icon=new FakeIcon();made.Add(id,icon);return icon;});
        var a=new TrayGroup {Id="g2"};var b=new TrayGroup {Id="g3"};icons.Sync(new[]{a,b});var second=made[a.Id];var third=made[b.Id];
        a.Name="改名";icons.Sync(new[]{b,a});Check(creations==2&&icons.First==first&&icons.For(a.Id)==second&&icons.For(b.Id)==third,"renaming and reordering preserve all icon instances");
        icons.Sync(new[]{b});Check(second.Disposals==1&&third.Disposals==0&&first.Disposals==0,"removing a group disposes only its icon");
        icons.Sync(new[]{b,new TrayGroup {Id="g4"}});Check(creations==3&&icons.For(b.Id)==third,"adding a group creates only its icon");
        using(var window=new DeviceEventsWindow(delegate{},null,null,icons.RecreateAll)){
            var message=Message.Create(window.Handle,DeviceEventsWindow.TaskbarCreated,IntPtr.Zero,IntPtr.Zero);typeof(DeviceEventsWindow).GetMethod("WndProc",Private).Invoke(window,new object[]{message});
        }
        Check(first.Recreations==1&&third.Recreations==1&&made["g4"].Recreations==1&&second.Recreations==0,"Explorer's taskbar message re-adds every surviving icon");
        icons.Dispose();icons.Dispose();Check(first.Disposals==1&&third.Disposals==1&&made["g4"].Disposals==1,"exit disposes every icon exactly once");icons.RecreateAll();Check(first.Recreations==1,"disposed icons cannot reappear");
    }
    static object Field(object target,string name){return target.GetType().GetField(name,Private).GetValue(target);}
    static object Call(object target,string name,params object[] args){return target.GetType().GetMethod(name,Private|BindingFlags.Public).Invoke(target,args);}
    static WirelessDevice Device(string id,string kind="mouse"){return new WirelessDevice {Id=id,Name=id,Kind=kind,PhysicalId="physical-"+id,Percent=65,Online=true,Fresh=true,ConnectionState="connected",MeasurementState="current",ConfirmedAtUtc=DateTime.UtcNow.ToString("o")};}
    sealed class Fixture:IDisposable {
        internal readonly TrayContext Owner;internal readonly Details Popup;internal readonly PopupNavigation Nav;internal int Saves;
        internal Fixture(){
            DeviceSettings.UseTransientConfiguration(new AudioBindings {RememberReadings=false,ReducedMotion=true});
            Owner=new TrayContext(false,delegate(bool battery){return new MouseSnapshot();},delegate(int stage){throw new Exception("No device writes");},null,null,configurationWriter:delegate(AudioBindings pref){Saves++;DeviceSettings.UseTransientConfiguration(pref);});
            DateTime until=DateTime.UtcNow.AddSeconds(4);while(Owner.IsRefreshing&&DateTime.UtcNow<until){Application.DoEvents();Thread.Sleep(2);}Check(!Owner.IsRefreshing,"fake reader settles");
            ((System.Windows.Forms.Timer)Field(Owner,"refresh")).Stop();Owner.GenericReady=true;Owner.NativeMousePresent=false;Owner.WirelessDevices=new[]{Device("mouse"),Device("keyboard","keyboard"),Device("headset","headset")};
            Popup=new Details(Owner);typeof(TrayContext).GetField("details",Private).SetValue(Owner,Popup);Nav=(PopupNavigation)Field(Popup,"navigation");
        }
        internal List<PopupControl> Rows(){return (List<PopupControl>)Call(Popup,"ControlsForPage");}
        internal PopupControl Find(string action,string group=null,string device=null){
            for(int i=0;i<50;i++){var rows=Rows();var row=rows.Find(delegate(PopupControl item){return item.ActionKey==action&&(group==null||item.GroupKey==group)&&(device==null||item.DeviceKey==device);});if(row!=null)return row;var next=rows.Find(delegate(PopupControl item){return item.Id==8;});if(next==null)break;Click(next);}
            throw new Exception("missing action "+action+" "+device);
        }
        internal void Click(PopupControl row){Call(Popup,"PerformAction",row);}
        internal void Page(PopupPage page){Nav.Page=page;Nav.Offset=0;}
        public void Dispose(){Owner.ExitThread();Owner.Dispose();}
    }
    static void SettingsUi(){
        using(var f=new Fixture()){
            f.Page(PopupPage.Preferences);f.Click(f.Rows().Find(delegate(PopupControl row){return row.Id==211;}));Check(f.Nav.Page==PopupPage.TrayIcons,"preferences opens icon management");
            f.Click(f.Find("tray:add"));var a=DeviceSettings.Current.TrayGroups[0];
            Check(f.Nav.TrayHint&&f.Rows().Exists(delegate(PopupControl row){return row.Text==TrayConfiguration.HiddenIconHint&&row.WrapText;}),"first addition displays the complete hidden-area hint in the panel");
            f.Nav.TrayHint=false;f.Click(f.Find("tray:add"));f.Click(f.Find("tray:add"));var pref=DeviceSettings.Current;var b=pref.TrayGroups[1];
            Check(!f.Find("tray:add").Enabled&&!f.Nav.TrayHint,"adding four icons disables addition without repeating the hint");int saves=f.Saves;f.Click(f.Find("tray:add"));Check(f.Saves==saves,"disabled add cannot mutate settings");
            f.Click(f.Find("tray:group",a.Id));Check(PopupPages.Title(f.Nav)==a.Name,"group page title is its name");f.Click(f.Find("tray:members"));
            Check(f.Find("tray:member",a.Id,"mouse").Detail=="当前在：托盘图标 1","unassigned devices identify the first group");f.Click(f.Find("tray:member",a.Id,"mouse"));
            Check(f.Find("tray:member",a.Id,"mouse").Selected,"membership is checked after joining");
            f.Page(PopupPage.TrayIcons);f.Click(f.Find("tray:group",b.Id));f.Click(f.Find("tray:members"));Check(f.Find("tray:member",b.Id,"mouse").Detail=="当前在："+a.Name,"other groups are identified in membership details");f.Click(f.Find("tray:member",b.Id,"mouse"));
            pref=DeviceSettings.Current;Check(!pref.TrayGroups[0].Members.Contains("mouse")&&pref.TrayGroups[1].Members.Contains("mouse"),"UI moves a device exclusively between groups");
            f.Click(f.Find("tray:member",b.Id,"mouse"));Check(TrayConfiguration.Owner(DeviceSettings.Current,"mouse")==TrayConfiguration.FirstId,"UI unchecking returns a device to group one");
            pref=DeviceSettings.Current;pref.HiddenDeviceIds.Add("headset");TrayConfiguration.Move(pref,"headset",a.Id);DeviceSettings.UseTransientConfiguration(pref);
            f.Page(PopupPage.TrayIcons);f.Click(f.Find("tray:group",a.Id));f.Click(f.Find("tray:rules"));var active=f.Find("tray:rule");Check(active.Detail=="只识别鼠标，其他设备按最低电量","active rule explains its mouse-only scope");
            var pins=f.Rows().FindAll(delegate(PopupControl row){return row.ActionKey=="tray:group-pin";});Check(pins.Count==1&&pins[0].DeviceKey=="headset","pin choices contain only group members, including hidden devices");f.Click(pins[0]);
            Check(TrayConfiguration.Find(DeviceSettings.Current,a.Id).DeviceId=="headset"&&DeviceSettings.Current.TrayDeviceId==null,"extra-group pin is separate from the legacy first-group pin");
            f.Click(f.Find("tray:delete"));Check(f.Nav.Page==PopupPage.ConfirmTrayDelete&&DeviceSettings.Current.TrayGroups.Count==3,"deletion requires a confirmation page");f.Click(f.Rows()[0]);Check(f.Nav.Page==PopupPage.TrayGroup&&DeviceSettings.Current.TrayGroups.Count==3,"back cancels deletion");
            f.Click(f.Find("tray:delete"));f.Click(f.Find("tray:confirm-delete"));Check(f.Nav.Page==PopupPage.TrayIcons&&TrayConfiguration.Owner(DeviceSettings.Current,"headset")==TrayConfiguration.FirstId,"confirmed deletion returns members and navigates back to the list");
            f.Click(f.Find("tray:group",TrayConfiguration.FirstId));Check(!f.Rows().Exists(delegate(PopupControl row){return row.ActionKey=="tray:rename"||row.ActionKey=="tray:delete";})&&f.Rows().Exists(delegate(PopupControl row){return row.Text=="包含其余所有设备";}),"first group cannot be renamed or deleted");
            f.Click(f.Find("tray:rules"));Check(f.Nav.Page==PopupPage.ChoosePreference&&f.Nav.PreferenceAction==211,"first group opens the existing six-rule selector");
            var all=PopupPages.Build(f.Owner,new PopupNavigation {Page=PopupPage.ChoosePreference,PreferenceAction=211,VisibleChoices=100},false);foreach(int id in new[]{410,411,412,413,414,415})Check(all.Exists(delegate(PopupControl row){return row.Id==id;}),"legacy tray rule remains available: "+id);
            f.Page(PopupPage.TrayIcons);f.Click(f.Rows().Find(delegate(PopupControl row){return row.Id==224;}));f.Click(f.Rows().Find(delegate(PopupControl row){return row.Id==451;}));Check(DeviceSettings.Current.TrayIconStyle=="device"&&f.Nav.Page==PopupPage.TrayIcons,"style selection saves and returns to the icon list");
            f.Nav.TrayGroupKey=b.Id;f.Page(PopupPage.TrayMembers);pref=DeviceSettings.Current;for(int i=0;i<18;i++)f.Owner.WirelessDevices=Append(f.Owner.WirelessDevices,Device("paged-"+i));
            Check(f.Rows().Exists(delegate(PopupControl row){return row.Id==8;}),"member selection uses existing paging");f.Click(f.Find("tray:member",b.Id,"paged-17"));Check(TrayConfiguration.Owner(DeviceSettings.Current,"paged-17")==b.Id,"paged device actions retain their identity");
        }
    }
    static WirelessDevice[] Append(WirelessDevice[] values,WirelessDevice value){var list=new List<WirelessDevice>(values);list.Add(value);return list.ToArray();}
    static void Integration(){
        using(var f=new Fixture()){
            var first=(NotifyIcon)Field(f.Owner,"tray");var icons=(TrayIconSet)Field(f.Owner,"trayIcons");var original=icons.First;var pref=DeviceSettings.Current;var group=TrayConfiguration.Add(pref);group.Name="耳机组";TrayConfiguration.Move(pref,"headset",group.Id);pref.TraySource="lowest";pref.HiddenDeviceIds.Add("headset");DeviceSettings.UseTransientConfiguration(pref);f.Owner.ConfigurationChanged(false);
            var extra=(NativeTrayIcon)icons.For(group.Id);var extraNotify=(NotifyIcon)Field(extra,"tray");Check(Field(f.Owner,"tray")==first&&icons.First==original,"the original NotifyIcon is preserved when groups are added");
            Check(extraNotify.Text=="耳机组：headset 65%"&&first.Text.StartsWith("托盘图标 1：")&&!first.Text.Contains("headset"),"group presentation includes hidden members and excludes them from the first group");
            Check(extraNotify.ContextMenuStrip==first.ContextMenuStrip&&!extraNotify.Visible&&!first.Visible,"all icons share the existing menu and test icons stay hidden");
            f.Owner.WirelessDevices=new WirelessDevice[0];f.Owner.ConfigurationChanged(false);Check(extraNotify.Text=="耳机组：没有已连接的设备"&&icons.For(group.Id)==extra,"an empty group keeps its icon and connection message");
            var status=f.Owner.BuildStatusJson();Check(!status.Contains("耳机组")&&!status.Contains("TrayGroups"),"extra groups do not leak into the MyDockFinder state schema");
            var click=new Point(Screen.PrimaryScreen.Bounds.Left+200,Screen.PrimaryScreen.Bounds.Top+12);f.Owner.CursorPosition=delegate{return click;};
            f.Popup.Dispose();NotifyClick(extraNotify,1);var panel=(Details)Field(f.Owner,"details");Check(panel!=null&&!panel.IsDisposed,"an additional icon opens the shared panel");
            Check(DeviceSettings.Current.LastTrayIconPosition.Point==click,"an additional tray icon records the same injected click position");
            NotifyClick(first,2);Check(panel.IsDisposed,"a double-click release on the first icon uses the same panel toggle");
            var menu=first.ContextMenuStrip;pref=DeviceSettings.Current;TrayConfiguration.Remove(pref,group.Id);DeviceSettings.UseTransientConfiguration(pref);f.Owner.ConfigurationChanged(false);Check(first.ContextMenuStrip==menu&&!menu.IsDisposed&&icons.First==original,"removing a real extra NotifyIcon preserves the original icon and shared menu");
        }
    }
    static void HiddenCandidates(bool extraGroups){
        using(var f=new Fixture()){
            var hidden=Device("hidden");hidden.Percent=1;
            f.Owner.WirelessDevices=Append(f.Owner.WirelessDevices,hidden);
            var pref=DeviceSettings.Current;pref.HiddenDeviceIds.Add(hidden.Id);pref.TraySource="lowest";
            TrayGroup group=null;if(extraGroups){group=TrayConfiguration.Add(pref);TrayConfiguration.Move(pref,"headset",group.Id);}
            DeviceSettings.UseTransientConfiguration(pref);
            foreach(string source in new[]{"lowest","auto","active","mouse","keyboard","headset"}){
                var view=(TrayView)Call(f.Owner,"WirelessTray",source);
                Check(!view.Tooltip.Contains("hidden")&&view.Battery.Percent==65,"hidden device is excluded from first-group automatic rule "+source+" with extra groups="+extraGroups);
            }
            pref.TrayDeviceId=hidden.Id;DeviceSettings.UseTransientConfiguration(pref);f.Owner.ConfigurationChanged(false);
            var first=(NotifyIcon)Field(f.Owner,"tray");
            Check(first.Text.Contains("hidden 1%"),"a first-group pin displays a hidden device with extra groups="+extraGroups);
            pref.TrayDeviceId=null;DeviceSettings.UseTransientConfiguration(pref);f.Owner.ConfigurationChanged(false);
            Check(!first.Text.Contains("hidden"),"clearing the pin excludes the hidden device again with extra groups="+extraGroups);
            pref.HiddenDeviceIds.Add("mouse");pref.HiddenDeviceIds.Add("keyboard");pref.HiddenDeviceIds.Add("headset");DeviceSettings.UseTransientConfiguration(pref);f.Owner.ConfigurationChanged(false);
            Check(((TrayView)Call(f.Owner,"WirelessTray","lowest")).Battery.Percent==null,"all hidden devices leave first-group automatic selection empty with extra groups="+extraGroups);
            pref.TrayDeviceId=hidden.Id;DeviceSettings.UseTransientConfiguration(pref);f.Owner.ConfigurationChanged(false);
            Check(first.Text.Contains("hidden 1%"),"the pin remains visible when every device is hidden with extra groups="+extraGroups);
            if(group!=null){
                var icons=(TrayIconSet)Field(f.Owner,"trayIcons");var notify=(NotifyIcon)Field(icons.For(group.Id),"tray");
                Check(notify.Text.EndsWith("headset 65%"),"hidden members remain automatic candidates in an extra group");
                TrayConfiguration.Move(pref,hidden.Id,group.Id);group.DeviceId=hidden.Id;DeviceSettings.UseTransientConfiguration(pref);f.Owner.ConfigurationChanged(false);
                Check(notify.Text.EndsWith("hidden 1%")&&!first.Text.Contains("hidden"),"an extra group owns its hidden pin even if the first group still pins it");
                foreach(string id in new[]{"mouse","keyboard"}){
                    var next=TrayConfiguration.Add(pref);TrayConfiguration.Move(pref,id,next.Id);DeviceSettings.UseTransientConfiguration(pref);f.Owner.ConfigurationChanged(false);
                    Check(((NotifyIcon)Field(icons.For(next.Id),"tray")).Text.EndsWith(id+" 65%"),"hidden members also display in group "+pref.TrayGroups.Count);
                }
            }
        }
    }
    static void NotifyClick(NotifyIcon icon,int clicks){
        var down=Message.Create(IntPtr.Zero,0x400,IntPtr.Zero,new IntPtr(clicks==2?0x203:0x201));var up=Message.Create(IntPtr.Zero,0x400,IntPtr.Zero,new IntPtr(0x202));
        typeof(NotifyIcon).GetMethod("WmMouseDown",Private).Invoke(icon,new object[]{down,MouseButtons.Left,clicks});typeof(NotifyIcon).GetMethod("WmMouseUp",Private).Invoke(icon,new object[]{up,MouseButtons.Left});
    }
    static void ActiveGroupAndRename(){
        using(var f=new Fixture()){
            var pref=DeviceSettings.Current;pref.TraySource="lowest";var group=TrayConfiguration.Add(pref);group.Source="active";TrayConfiguration.Move(pref,"mouse",group.Id);TrayConfiguration.Move(pref,"keyboard",group.Id);
            f.Owner.WirelessDevices[1].Percent=5;f.Owner.WirelessDevices[0].PhysicalId="device-aaaaaaaaaaaaaaaaaaaaaaaa";DeviceSettings.UseTransientConfiguration(pref);f.Owner.ConfigurationChanged(false);var icons=(TrayIconSet)Field(f.Owner,"trayIcons");var extra=icons.For(group.Id);var notify=(NotifyIcon)Field(extra,"tray");
            Check(notify.Text.EndsWith("keyboard 5%"),"extra active group initially falls back to its lowest reading");
            f.Owner.HandleMouseActivity(new MouseActivitySource("device-aaaaaaaaaaaaaaaaaaaaaaaa",false,0x046D,0xC547));Check(notify.Text.EndsWith("mouse 65%"),"extra active icons refresh even when the first group's rule is not active");
            f.Page(PopupPage.TrayIcons);f.Click(f.Find("tray:group",group.Id));Exception failure=null;bool shown=false;
            using(var timer=new System.Windows.Forms.Timer {Interval=10}){
                timer.Tick+=delegate {
                    foreach(Form dialog in Application.OpenForms)if(dialog.Text=="托盘图标名称"){
                        timer.Stop();shown=true;
                        try{var inputs=dialog.Controls.Find("AliasInput",true);var input=inputs.Length==1?inputs[0] as TextBox:null;Check(input!=null&&input.MaxLength==12,"group rename reuses the name dialog with a twelve-character input");input.Text="  改名分组  ";dialog.DialogResult=DialogResult.OK;}
                        catch(Exception error){failure=error;dialog.DialogResult=DialogResult.Cancel;}break;
                    }
                };
                timer.Start();f.Click(f.Find("tray:rename"));
            }
            if(failure!=null)throw failure;
            Check(shown&&TrayConfiguration.Find(DeviceSettings.Current,group.Id).Name=="改名分组"&&PopupPages.Title(f.Nav)=="改名分组","rename saves a sanitized name through the injected writer");
            Check(icons.For(group.Id)==extra&&notify.Text.StartsWith("改名分组："),"renaming updates the tooltip without rebuilding the icon");
        }
    }
    static void SettingsRendering(){
        using(var f=new Fixture()){
            var pref=DeviceSettings.Current;var group=TrayConfiguration.Add(pref);group.Name="十二字符的托盘分组名称测试";TrayConfiguration.Add(pref);TrayConfiguration.Add(pref);TrayConfiguration.Move(pref,"mouse",group.Id);TrayConfiguration.Move(pref,"headset",group.Id);pref.HiddenDeviceIds.Add("headset");DeviceSettings.UseTransientConfiguration(pref);
            foreach(bool dark in new[]{false,true})foreach(var page in new[]{PopupPage.TrayIcons,PopupPage.TrayGroup,PopupPage.TrayRules,PopupPage.TrayMembers,PopupPage.ConfirmTrayDelete}){
                var nav=new PopupNavigation {Page=page,TrayGroupKey=group.Id,TrayHint=true,VisibleChoices=7};var rows=PopupPages.Build(f.Owner,nav,false);var size=PopupPages.SizeFor(rows);var ids=new HashSet<int>();var keys=new HashSet<string>();
                foreach(var row in rows){Check(row.Rect.Left>=0&&row.Rect.Right<=size.Width&&row.Rect.Bottom<=size.Height-28,"new settings rows fit within their panel");Check(keys.Add(row.StableKey),"new settings rows have unique stable keys: "+page+" "+row.StableKey);if(row.Id>=0)Check(ids.Add(row.Id),"new settings actions have unique focus IDs");}
                using(var image=new Bitmap(size.Width*2,size.Height*2,PixelFormat.Format32bppPArgb)){
                    using(var g=Graphics.FromImage(image)){g.Clear(Color.Transparent);g.ScaleTransform(2,2);PopupPages.Paint(g,f.Owner,nav,rows,dark,-1,-1,-1);}
                    image.Save(Path.Combine(output,"tray-settings-"+page+"-"+(dark?"dark":"light")+".png"),ImageFormat.Png);
                }
            }
        }
    }
    static void DeviceRendering(){
        VisualDesign.HighContrastOverride=false;
        foreach(int size in new[]{16,20,24,32})foreach(bool dark in new[]{false,true})foreach(string kind in new[]{"mouse","keyboard","headset","gamepad","device"}){
            foreach(int? percent in new int?[]{0,1,15,46,80,100,null})using(var bitmap=TrayGlyph.RenderDevice(percent,false,dark,false,size,kind)){
                Check(bitmap.Width==size&&bitmap.Height==size,"device icon has the requested native size");
                Check(bitmap.GetPixel(0,0).A==0&&bitmap.GetPixel(size-1,size-1).A==0,"glyphs preserve transparent corners");
                for(int x=0;x<size;x++)for(int z=0;z<size;z++)if(bitmap.GetPixel(x,z).A>24)Check(x>0&&x<size-1&&z>0&&z<size-1,"device strokes fit inside the canvas: "+size+" "+kind+" "+percent+" at "+x+","+z+" alpha "+bitmap.GetPixel(x,z).A);
            }
            using(var charge=TrayGlyph.RenderDevice(46,true,dark,false,size,kind))using(var normal=TrayGlyph.RenderDevice(46,false,dark,false,size,kind)){
                bool different=false;for(int x=size*3/4;x<size;x++)for(int y=0;y<size/2;y++)different|=charge.GetPixel(x,y)!=normal.GetPixel(x,y);Check(different,"charging renders the existing lightning marker");
            }
        }
    }
    static void BatteryBars(int size){
        int minimum=size==16?4:size==20?5:size==24?6:8;
        foreach(bool dark in new[]{false,true})foreach(string kind in new[]{"mouse","keyboard","headset","device"})using(var full=TrayGlyph.RenderDevice(100,false,dark,false,size,kind)){
            int top=size,bottom=-1,rows=0;
            for(int y=size-1;y>=(int)Math.Ceiling(size*.62);y--){
                if(full.GetPixel(size/2,y).A>150){if(bottom<0)bottom=y;top=y;rows++;}else if(bottom>=0)break;
            }
            Check(rows>=minimum,"battery bar height at "+size+"px is at least "+minimum+"px; got "+rows);
            Check(rows<=Math.Ceiling(size*.3),"battery bar stays in the lower portion of the icon");int middle=(top+bottom)/2;
            for(int value=0;value<=100;value++)using(var bitmap=TrayGlyph.RenderDevice(value,false,dark,false,size,kind)){
                int filled=0;for(int x=0;x<size;x++)if(bitmap.GetPixel(x,middle).A>150)filled++;
                float expected=value==0?0:Math.Max(1,(size-2)*value/100f);
                Check(Math.Abs(filled-expected)<=1,"proportional fill at "+size+"px / "+value+"%; expected "+expected+", got "+filled);
                Check(value==0?filled==0:filled>=1,"zero is empty and every positive reading has a visible pixel");
            }
            using(var empty=TrayGlyph.RenderDevice(null,false,dark,false,size,kind))using(var zero=TrayGlyph.RenderDevice(0,false,dark,false,size,kind))using(var stale=TrayGlyph.RenderDevice(46,false,dark,true,size,kind))using(var charge=TrayGlyph.RenderDevice(46,true,dark,false,size,kind))using(var normal=TrayGlyph.RenderDevice(46,false,dark,false,size,kind)){
                Check(empty.GetPixel(1,middle).A>24&&empty.GetPixel(size-2,middle).A>24&&empty.GetPixel(0,middle).A==0&&empty.GetPixel(size-1,middle).A==0,"track spans the width with a one-pixel margin");
                Color track=empty.GetPixel(size/2,middle);Check(track.A>24&&track.A<150&&Math.Abs(track.R-track.B)<35,"unknown reading has a visible translucent gray track");
                Check(full.GetPixel(1,top).A<full.GetPixel(size/2,top).A,"battery track and full fill have rounded corners");
                Color gray=stale.GetPixel(2,middle),expectedGray=TrayGlyph.LevelColor(null,dark);Check(Math.Abs(gray.R-expectedGray.R)<=4&&Math.Abs(gray.G-expectedGray.G)<=4&&Math.Abs(gray.B-expectedGray.B)<=4,"historical fill uses the existing gray");
                Color level=normal.GetPixel(2,middle),expectedLevel=TrayGlyph.LevelColor(46,dark);Check(Math.Abs(level.R-expectedLevel.R)<=4&&Math.Abs(level.G-expectedLevel.G)<=4&&Math.Abs(level.B-expectedLevel.B)<=4,"fresh fill uses the existing level color");
                for(int y=top;y<=bottom;y++)for(int x=0;x<size;x++){
                    Check(empty.GetPixel(x,y)==zero.GetPixel(x,y),"unknown and zero readings have the same empty track");
                    Check(charge.GetPixel(x,y)==normal.GetPixel(x,y),"charging marker never covers the bar");
                }
            }
        }
    }
    static void ApproximateTrayBadgePolish(){
        // #1139: approximate readings omit the tiny "~" prefix in 16-32px tray number badges so digits stay full-sized
        // and never resemble a minus sign ("-"), while tooltips still disclose approximation ("约80%").
        foreach(int size in new[]{16,20,24,32})foreach(int level in new[]{80,100}){
            using(var exact=TrayGlyph.Render(level,false,true,false,size,false))
            using(var approx=TrayGlyph.Render(level,false,true,false,size,true)){
                bool same=exact.Width==approx.Width&&exact.Height==approx.Height;
                for(int y=0;same&&y<exact.Height;y++)for(int x=0;same&&x<exact.Width;x++)if(exact.GetPixel(x,y)!=approx.GetPixel(x,y))same=false;
                Check(same,"approximate tray number glyph matches exact digit size without leading ~/- at "+size+"px ("+level+"%)");
            }
        }
        var approxCard=Card("approx","mouse",80);approxCard.Battery.Approximate=true;approxCard.Name="Logitech G";
        Check(TraySelection.Present(new List<WirelessCard>{approxCard},approxCard,"设备").Tooltip.Contains("约80%"),"tray tooltip still discloses approximate battery reading");
    }
    static void TrayIconStyleOptions(){
        // #1184: support rounded white-digit badge ("badge") and transparent minimal digits ("minimal") alongside "number" and "device".
        foreach(string style in new[]{"badge","minimal"}){
            string path=Path.Combine(output,"tray-style-"+style+".json");
            var pref=new AudioBindings {TrayIconStyle=style};
            DeviceSettings.SaveBindings(path,pref);
            var loaded=DeviceSettings.LoadBindings(path);
            Check(loaded.TrayIconStyle==style,"tray icon style "+style+" persists cleanly");
        }
        foreach(int size in new[]{16,20,24,32})foreach(bool dark in new[]{false,true}){
            using(var circle=TrayGlyph.RenderStyle("number",78,false,dark,false,size,"mouse"))
            using(var badge=TrayGlyph.RenderStyle("badge",78,false,dark,false,size,"mouse"))
            using(var minimal=TrayGlyph.RenderStyle("minimal",78,false,dark,false,size,"mouse"))
            using(var badgeCharge=TrayGlyph.RenderStyle("badge",42,true,dark,false,size,"mouse",false,UpdateBadge.Blue,dark))
            using(var minimalCharge=TrayGlyph.RenderStyle("minimal",15,true,dark,false,size,"mouse",false,UpdateBadge.Blue,dark)){
                Check(badge.GetPixel(0,0).A==0&&minimal.GetPixel(0,0).A==0,"corner stays transparent for new tray styles at "+size+"px");
                Check(minimal.GetPixel(1,1).A==0,"minimal style keeps background transparent");
                bool badgeDiffers=false,minimalDiffers=false;
                int badgeOpaque=0,minimalOpaque=0;
                for(int y=0;y<size;y++)for(int x=0;x<size;x++){
                    if(badge.GetPixel(x,y).A>20)badgeOpaque++;
                    if(minimal.GetPixel(x,y).A>20)minimalOpaque++;
                    if(badge.GetPixel(x,y)!=circle.GetPixel(x,y))badgeDiffers=true;
                    if(minimal.GetPixel(x,y)!=badge.GetPixel(x,y))minimalDiffers=true;
                }
                Check(badgeOpaque>size*size/2&&badgeDiffers,"rounded white-digit badge renders distinct filled badge at "+size+"px");
                Check(minimalOpaque>10&&minimalOpaque<badgeOpaque&&minimalDiffers,"minimal style renders transparent-background digits at "+size+"px");
                Check(badgeCharge.Width==size&&minimalCharge.Width==size,"charging + update badge variants render cleanly at "+size+"px");
            }
        }
        using(var f=new Fixture()){
            f.Page(PopupPage.TrayIcons);
            f.Click(f.Rows().Find(delegate(PopupControl row){return row.Id==224;}));
            f.Click(f.Rows().Find(delegate(PopupControl row){return row.Id==452;}));
            Check(DeviceSettings.Current.TrayIconStyle=="badge"&&f.Nav.Page==PopupPage.TrayIcons,"popup menu selects badge style (452)");
            f.Click(f.Rows().Find(delegate(PopupControl row){return row.Id==224;}));
            f.Click(f.Rows().Find(delegate(PopupControl row){return row.Id==453;}));
            Check(DeviceSettings.Current.TrayIconStyle=="minimal"&&f.Nav.Page==PopupPage.TrayIcons,"popup menu selects minimal style (453)");
            f.Click(f.Rows().Find(delegate(PopupControl row){return row.Id==224;}));
            f.Click(f.Rows().Find(delegate(PopupControl row){return row.Id==454;}));
            Check(DeviceSettings.Current.TrayIconStyle=="device_number"&&f.Nav.Page==PopupPage.TrayIcons,"popup menu selects device_number combo style (454)");
        }
    }
    static void DeviceNumberComboAndPerGroupStyles(){
        // #1112: combo "device_number" style + per-group IconStyle override + tooltip safety.
        string path=Path.Combine(output,"tray-1112-roundtrip.json");
        var pref=new AudioBindings {TrayIconStyle="device_number"};
        var headsetGroup=TrayConfiguration.Add(pref);headsetGroup.Name="耳机组";headsetGroup.IconStyle="badge";
        var kbGroup=TrayConfiguration.Add(pref);kbGroup.Name="键盘组";kbGroup.IconStyle=null;
        DeviceSettings.SaveBindings(path,pref);
        string rawJson=File.ReadAllText(path);
        Check(rawJson.Contains("\"IconStyle\":\"badge\""),"explicit group IconStyle persists to JSON");
        var loaded=DeviceSettings.LoadBindings(path);
        Check(loaded.TrayIconStyle=="device_number","global device_number style survives round trip");
        Check(loaded.TrayGroups.Count==2&&loaded.TrayGroups[0].IconStyle=="badge"&&loaded.TrayGroups[1].IconStyle==null,"per-group IconStyle and null inheritance survive round trip");
        Check(TrayConfiguration.EffectiveGroupTrayIconStyle(loaded,loaded.TrayGroups[0])=="badge","group override beats global style");
        Check(TrayConfiguration.EffectiveGroupTrayIconStyle(loaded,loaded.TrayGroups[1])=="device_number","null group style inherits global style");

        // Malformed JSON IconStyle must degrade cleanly to null without dropping the group or legacy settings.
        var malformed=Load(",\"TrayGroups\":[{\"Id\":\"g2\",\"Name\":\"耳机\",\"IconStyle\":\"unknown_future\",\"Members\":[\"h1\"]},{\"Id\":\"g3\",\"Name\":\"键盘\",\"IconStyle\":123,\"Members\":[\"k1\"]}],\"TrayIconStyle\":\"device_number\"");
        Kept(malformed);
        Check(malformed.TrayIconStyle=="device_number"&&malformed.TrayGroups.Count==2&&malformed.TrayGroups[0].IconStyle==null&&malformed.TrayGroups[1].IconStyle==null,"invalid or non-string group IconStyle degrades cleanly to inherit");

        // Verify distinct top device silhouettes and bottom numeric pill across sizes and themes.
        VisualDesign.HighContrastOverride=false;
        foreach(int size in new[]{16,20,24,32})foreach(bool dark in new[]{false,true}){
            using(var mouse79=TrayGlyph.RenderStyle("device_number",79,false,dark,false,size,"mouse",false,null,dark))
            using(var headset79=TrayGlyph.RenderStyle("device_number",79,false,dark,false,size,"headset",false,null,dark))
            using(var keyboard79=TrayGlyph.RenderStyle("device_number",79,false,dark,false,size,"keyboard",false,null,dark))
            using(var gamepad79=TrayGlyph.RenderStyle("device_number",79,false,dark,false,size,"gamepad",false,null,dark))
            using(var mouse100=TrayGlyph.RenderStyle("device_number",100,false,dark,false,size,"mouse",false,null,dark))
            using(var badge79=TrayGlyph.RenderStyle("badge",79,false,dark,false,size,"mouse",false,null,dark))
            using(var device79=TrayGlyph.RenderStyle("device",79,false,dark,false,size,"mouse",false,null,dark))
            using(var chargeUpdate=TrayGlyph.RenderStyle("device_number",42,true,dark,false,size,"keyboard",false,UpdateBadge.Blue,dark)){
                Check(mouse79.Width==size&&mouse79.Height==size&&chargeUpdate.Width==size,"device_number renders exact native size "+size+"px");
                Check(mouse79.GetPixel(0,0).A==0&&mouse79.GetPixel(size-1,size-1).A==0,"device_number keeps transparent corners at "+size+"px");
                Check(chargeUpdate.GetPixel(size-2,1).A>200,"device_number preserves update badge dot when charging + update badge coexist at "+size+"px");
                int splitY=(int)Math.Floor(size*0.44);
                bool mouseVsHeadsetTop=false,mouseVsKbTop=false,mouseVsPadTop=false,pct79Vs100Bottom=false,diffBadge=false,diffDevice=false;
                for(int y=0;y<size;y++)for(int x=0;x<size;x++){
                    Color m79=mouse79.GetPixel(x,y);
                    if(y<splitY){
                        if(m79!=headset79.GetPixel(x,y))mouseVsHeadsetTop=true;
                        if(m79!=keyboard79.GetPixel(x,y))mouseVsKbTop=true;
                        if(m79!=gamepad79.GetPixel(x,y))mouseVsPadTop=true;
                    }else{
                        if(m79!=mouse100.GetPixel(x,y))pct79Vs100Bottom=true;
                    }
                    if(m79!=badge79.GetPixel(x,y))diffBadge=true;
                    if(m79!=device79.GetPixel(x,y))diffDevice=true;
                }
                Check(mouseVsHeadsetTop&&mouseVsKbTop&&mouseVsPadTop,"top silhouette distinguishes mouse/headset/keyboard/gamepad at "+size+"px");
                Check(pct79Vs100Bottom,"bottom numeric pill reflects battery value change (79 vs 100) at "+size+"px");
                Check(diffBadge&&diffDevice,"device_number is visually distinct from badge and device styles at "+size+"px");
            }
        }

        // Render showcase sheet for visual pixel inspection across 16/20/24/32px dark & light taskbars.
        using(var sheet=new Bitmap(760,340,PixelFormat.Format32bppArgb)){
            using(var g=Graphics.FromImage(sheet)){
                g.Clear(Color.FromArgb(24,26,30));
                g.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode=System.Drawing.Drawing2D.PixelOffsetMode.Half;
                string[] kinds={"mouse","headset","keyboard","gamepad","device"};
                int?[] levels={79,100,42,15,null};
                bool[] charges={false,false,true,false,false};
                int[] sizes={16,20,24,32};
                using(var font=new Font("Segoe UI",11f,FontStyle.Regular,GraphicsUnit.Pixel))
                using(var darkBar=new SolidBrush(Color.FromArgb(28,30,36)))
                using(var lightBar=new SolidBrush(Color.FromArgb(240,242,246)))
                using(var white=new SolidBrush(Color.White))
                using(var ink=new SolidBrush(Color.FromArgb(24,28,34))){
                    for(int row=0;row<2;row++){
                        bool dark=row==0;
                        int baseY=14+row*160;
                        g.FillRectangle(dark?darkBar:lightBar,12,baseY,736,148);
                        g.DrawString(dark?"Dark Taskbar (1:1 + 3x zoom)":"Light Taskbar (1:1 + 3x zoom)",font,dark?white:ink,20,baseY+6);
                        for(int s=0;s<sizes.Length;s++){
                            int px=sizes[s];
                            int x0=20+s*182;
                            g.DrawString(px+"px",font,dark?white:ink,x0,baseY+24);
                            for(int k=0;k<kinds.Length;k++){
                                using(var bmp=TrayGlyph.RenderDeviceNumber(levels[k],charges[k],dark,false,px,kinds[k],false,k==0&&s==3?(Color?)UpdateBadge.Blue:null,dark)){
                                    g.DrawImage(bmp,x0+k*34,baseY+42,px,px);
                                    g.DrawImage(bmp,x0+k*34,baseY+82,32,32);
                                }
                            }
                        }
                    }
                }
            }
            string shot=Path.Combine(output,"tray-device-number-showcase.png");
            sheet.Save(shot,ImageFormat.Png);
            Check(File.Exists(shot)&&new FileInfo(shot).Length>2000,"rendered device_number visual showcase PNG");
        }

        // Popup navigation + live multi-icon style differentiation + tooltip 63-char guard.
        using(var f=new Fixture()){
            var p=DeviceSettings.Current;
            p.TrayIconStyle="badge";
            var grp=TrayConfiguration.Add(p);grp.Name="耳机组";
            TrayConfiguration.Move(p,"headset",grp.Id);
            DeviceSettings.UseTransientConfiguration(p);
            f.Owner.ConfigurationChanged(false);

            f.Page(PopupPage.TrayIcons);
            f.Click(f.Find("tray:group",grp.Id));
            var styleRow=f.Find("tray:style",grp.Id);
            Check(styleRow.Value.StartsWith("跟随全局"),"group page shows inherited icon style by default");
            f.Click(styleRow);
            Check(f.Nav.Page==PopupPage.TrayGroupStyle&&PopupPages.Title(f.Nav)=="分组图标样式","opens per-group icon style picker");

            // Check layout bounds & unique keys on TrayGroupStyle page.
            var styleRows=PopupPages.Build(f.Owner,new PopupNavigation {Page=PopupPage.TrayGroupStyle,TrayGroupKey=grp.Id,VisibleChoices=10},false);
            var styleSize=PopupPages.SizeFor(styleRows);
            var keys=new HashSet<string>();var ids=new HashSet<int>();
            foreach(var r in styleRows){
                Check(r.Rect.Left>=0&&r.Rect.Right<=styleSize.Width&&r.Rect.Bottom<=styleSize.Height-28,"TrayGroupStyle row fits panel");
                Check(keys.Add(r.StableKey),"TrayGroupStyle row has unique stable key: "+r.StableKey);
                if(r.Id>=0)Check(ids.Add(r.Id),"TrayGroupStyle row has unique focus ID");
            }

            // Pick "device_number" specifically for grp.
            var comboChoice=f.Rows().Find(delegate(PopupControl row){return row.ActionKey=="tray:group-style"&&row.TargetToken=="device_number";});
            Check(comboChoice!=null,"TrayGroupStyle contains device_number option");
            f.Click(comboChoice);
            Check(f.Nav.Page==PopupPage.TrayGroup&&TrayConfiguration.Find(DeviceSettings.Current,grp.Id).IconStyle=="device_number","per-group style persists device_number and navigates back");

            // Verify live TrayIconSet passed different styles to First ("badge") vs grp ("device_number").
            var icons=(TrayIconSet)Field(f.Owner,"trayIcons");
            string firstKey=(string)Field(icons.First,"lastKey");
            string groupKey=(string)Field(icons.For(grp.Id),"lastKey");
            Check(firstKey.Contains("|badge|")&&groupKey.Contains("|device_number|"),"primary and extra tray icons render with independent styles");

            // Reset back to inherit ("跟随全局").
            f.Click(f.Find("tray:style",grp.Id));
            var inheritChoice=f.Rows().Find(delegate(PopupControl row){return row.ActionKey=="tray:group-style"&&row.TargetToken=="";});
            f.Click(inheritChoice);
            Check(TrayConfiguration.Find(DeviceSettings.Current,grp.Id).IconStyle==null,"selecting inherit clears per-group IconStyle");
            string groupKeyAfter=(string)Field(icons.For(grp.Id),"lastKey");
            Check(groupKeyAfter.Contains("|badge|"),"clearing per-group override immediately reverts extra icon to global style");
        }

        // Tooltip safety guard under extreme long group name + stale suffix + blank device name.
        var longCard=Card("blank-name","mouse",88,false);longCard.Name="   ";
        var safeTip=TraySelection.Present(new List<WirelessCard>{longCard},longCard,"十二字符的托盘分组名称测试");
        Check(safeTip.Tooltip.Length<=63&&safeTip.Tooltip.Contains("无线设备"),"blank device name falls back cleanly within 63-char tooltip limit");
    }
    [STAThread] static int Main(string[] args){
        output=args.Length>0?args[0]:Path.GetDirectoryName(Application.ExecutablePath);
        var previous=DeviceSettings.Current;
        try {
            Run("configuration contract",Contracts);Run("selection contract",SelectionContract);Run("lifecycle contract",LifecycleContract);Run("device style contract",DeviceStyleContract);Run("preferences entry",PreferencesEntry);
            Run("tolerant configuration and downgrade compatibility",Configuration);Run("exclusive group editing",GroupEditing);Run("pure selection rules",Selection);Run("group tooltip formatting",Tooltips);Run("incremental icon lifecycle and Explorer recovery",Lifecycle);Run("settings navigation and actions",SettingsUi);Run("tray integration",Integration);Run("hidden candidates without extra groups",delegate{HiddenCandidates(false);});Run("hidden candidates with extra groups",delegate{HiddenCandidates(true);});Run("extra-group activity and rename dialog",ActiveGroupAndRename);Run("settings bounds, identity and previews",SettingsRendering);Run("native-size device glyphs and bars",DeviceRendering);
            Run("approximate tray badge omits tiny tilde prefix (#1139)",ApproximateTrayBadgePolish);
            Run("tray icon style options badge & minimal (#1184)",TrayIconStyleOptions);
            Run("device+number combo style and per-group tray styles (#1112)",DeviceNumberComboAndPerGroupStyles);
            foreach(int size in new[]{16,20,24,32}){int pixels=size;Run("battery bars at "+pixels+"px",delegate{BatteryBars(pixels);});}
            Console.WriteLine(checks+" checks, "+failures+" failures");return failures==0?0:1;
        }finally{VisualDesign.HighContrastOverride=null;DeviceSettings.UseTransientConfiguration(previous);}
    }
}
