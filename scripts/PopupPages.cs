using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using AtkBattery;

namespace AtkBatteryTray {
    internal enum PopupPage { Main, More, Audio, Preferences, Mouse, Headset, Diagnostics, Bindings, PickBinding, ChoosePreference, Devices, Device, ManageDevice, DeviceMissing, TrayIcons, TrayGroup, TrayRules, TrayMembers, ConfirmTrayDelete }
    internal sealed class PopupControl {
        public int Id;
        public int DpiValue;
        public int PageOffset=-1;
        public RectangleF Rect;
        public string Text,Value,Detail,Icon,EndpointId,DeviceKey,ActionKey,TargetToken,PaletteKey;
        public string GroupKey;
        public bool Enabled=true,Selected,Toggle,Checked,Information,Slider,Busy,WrapText,Section;
        public string StableKey {get {
            string action=ActionKey;
            if(action=="tray:rule")action+=":"+TargetToken;
            if(DpiValue>0)action="wireless-dpi:"+DpiValue+":"+(TargetToken??"");
            if(String.IsNullOrEmpty(action))action=Id>=2000&&Id<3000?"refresh":Id>=1000&&Id<2000?"details":Id>=300&&Id<400?"binding:"+(EndpointId??"auto"):"action:"+Id;
            return (GroupKey==null?"":GroupKey+"|")+(DeviceKey??"global")+"|"+action;
        }}
    }
    internal sealed class PopupNavigation {
        internal PopupPage Page=PopupPage.Main;
        internal string BindingRole="speaker",Notice="";
        internal int Offset,VolumePreview=-1,VisibleChoices=7;
        internal int MaximumHeight;
        internal int PreferenceAction=210;
        internal int DeviceScroll;
        internal string SelectedDeviceKey,SelectedDeviceName,ReturnedFocusKey;
        internal string AudioDeviceKey,AudioDeviceName;
        internal string TrayGroupKey=TrayConfiguration.FirstId;
        internal bool TrayHint;
        readonly Stack<ReturnPoint> history=new Stack<ReturnPoint>();
        sealed class ReturnPoint {internal PopupPage Page;internal int Focus,Offset;internal string FocusKey,AudioDeviceKey,AudioDeviceName;}
        internal void Reset(){history.Clear();Page=PopupPage.Main;Offset=0;VolumePreview=-1;Notice="";DeviceScroll=0;ReturnedFocusKey=null;AudioDeviceKey=null;AudioDeviceName=null;TrayGroupKey=TrayConfiguration.FirstId;TrayHint=false;}
        internal void Go(PopupPage page,int focus,string stableFocus=null) {
            if(page==Page)return;
            history.Push(new ReturnPoint {Page=Page,Focus=focus,Offset=Offset,FocusKey=stableFocus,AudioDeviceKey=AudioDeviceKey,AudioDeviceName=AudioDeviceName});Page=page;Offset=0;Notice="";
        }
        internal int Back() {
            Notice="";VolumePreview=-1;
            if(history.Count==0){Page=PopupPage.Main;ReturnedFocusKey=null;return 1;}
            var value=history.Pop();Page=value.Page;Offset=value.Offset;ReturnedFocusKey=value.FocusKey;AudioDeviceKey=value.AudioDeviceKey;AudioDeviceName=value.AudioDeviceName;return value.Focus;
        }
    }
    internal static class PopupPages {
        internal static Color Foreground(bool dark){return ThemePalette.Current(dark).Foreground;}
        internal static Color Muted(bool dark){return ThemePalette.Current(dark).Muted;}
        internal static Color Track(bool dark){return ThemePalette.Current(dark).Track;}
        internal static Color Blue(bool dark){return ThemePalette.Current(dark).Accent;}
        internal static Color SelectedForeground(bool dark){return ThemePalette.Current(dark).SelectedForeground;}
        internal static bool Reduced {get{return DeviceSettings.Current.ReducedMotion||!System.Windows.Forms.SystemInformation.IsMenuAnimationEnabled;}}
        static PopupControl Row(int id,string text,string value=null,string icon=null){return new PopupControl {Id=id,Text=text,Value=value,Icon=icon};}
        static PopupControl Info(string text,string value){var row=Row(-1,text,value);row.Information=true;row.ActionKey="info:"+text;return row;}
        static PopupControl Section(string text){var row=Info(text,null);row.Section=true;return row;}
        static string WrappedText(PopupControl row){return String.IsNullOrEmpty(row.Value)?row.Text:row.Text+"\n"+row.Value;}
        static PopupControl Toggle(int id,string text,bool value){var row=Row(id,text);row.Toggle=true;row.Checked=value;return row;}
        static string OutputName(AudioReading audio){return UiState.CurrentOutputName(audio);}
        static List<WirelessCard> TrayDevices(TrayContext owner){return WirelessLayout.Build(owner,0,100000,true).All.FindAll(delegate(WirelessCard card){return card.Id!="empty"&&!card.AudioOnly&&(card.Device!=null||card.NativeMouse||card.NativeHeadset);});}
        static string FixedTrayName(TrayContext owner,string id){var device=TrayDevices(owner).Find(delegate(WirelessCard card){return card.Id==id;});return "固定："+(device==null?"已断开的设备":device.Name);}
        static string TrayRule(TrayContext owner,string source,string deviceId){return !String.IsNullOrEmpty(deviceId)?FixedTrayName(owner,deviceId):source=="active"?"当前使用的设备":source=="auto"?"自动":source=="mouse"?"鼠标优先":source=="headset"?"耳机优先":source=="keyboard"?"键盘优先":"最低电量";}
        static PopupControl GroupRow(string action,string text,string groupId,string value=null){var row=Row(610+Array.IndexOf(new[]{"add","rename","rules","members","delete","confirm-delete","rule","member","group-pin","group"},action),text,value);row.ActionKey="tray:"+action;row.GroupKey=groupId;return row;}
        static List<WirelessCard> MemberDevices(TrayContext owner,AudioBindings pref){
            var cards=WirelessLayout.Build(owner,0,100000,true).All.FindAll(delegate(WirelessCard card){return card.Id!="empty"&&!card.AudioOnly&&card.Id!="native:audio";});
            foreach(var group in pref.TrayGroups)foreach(string id in group.Members)if(!cards.Exists(delegate(WirelessCard card){return card.Id==id;})){
                string alias;string name=id=="native:mouse"?pref.MouseAlias:id=="native:headset"?pref.HeadsetAlias:pref.DeviceAliases.TryGetValue(id,out alias)?alias:"已断开的设备";
                cards.Add(new WirelessCard {Id=id,Name=name,Kind="device"});
            }
            return cards;
        }
        internal static List<PopupControl> Build(TrayContext owner,PopupNavigation nav,bool headset,WirelessLayout wireless=null) {
            var rows=new List<PopupControl>();int preferenceSplit=0;var pref=DeviceSettings.Current;var audio=nav.AudioDeviceKey!=null&&!owner.AudioControlsAvailable?null:owner.Audio;
            var audioPref=DeviceSettings.ForAudioDevice(nav.AudioDeviceKey);
            if(nav.Page==PopupPage.Main&&owner.UseWirelessLayout)return WithIdentity(BuildWireless(owner,wireless??WirelessLayout.Build(owner,nav.DeviceScroll,520),nav),nav,owner);
            if(nav.Page==PopupPage.Main) {
                var audioCard=VisualDesign.AudioCardFor(owner.Headset,audio);bool secondCard=headset||audioCard!=null;
                rows.Add(new PopupControl {Id=2,Rect=VisualDesign.MouseNameRect,Text=pref.MouseAlias});
                rows.Add(new PopupControl {Id=4,Rect=VisualDesign.MouseMeterRect,Text=UiState.Mouse(owner.Current,owner.LastGood,DateTime.UtcNow).Tooltip+"，点击刷新"});
                if(owner.Dpi!=null&&owner.Dpi.Stages!=null)for(int i=0;i<owner.Dpi.Stages.Count;i++) {
                    var hit=VisualDesign.StageRect(i,owner.Dpi.Stages.Count);hit.Inflate(0,owner.Dpi.Stages.Count>4?1:2);
                    rows.Add(new PopupControl {Id=10+i,Rect=hit,Text=owner.Dpi.Stages[i].XDpi+" DPI"});
                }
                if(secondCard) {
                    if(audioCard!=null)rows.Add(new PopupControl {Id=200,Rect=new RectangleF(10,VisualDesign.MouseCardHeight(owner.Dpi)+14,240,44),Text="音源与麦克风",Value=audioCard.Subtitle,DeviceKey="native:audio"});
                    else {
                        rows.Add(new PopupControl {Id=3,Rect=VisualDesign.HeadsetNameRectFor(owner.Dpi),Text=pref.HeadsetAlias});
                        rows.Add(new PopupControl {Id=5,Rect=VisualDesign.HeadsetMeterRectFor(owner.Dpi),Text=UiState.Headset(owner.Headset,DateTime.UtcNow).Tooltip+"，点击刷新"});
                    }
                    rows.Add(new PopupControl {Id=100,Rect=VisualDesign.SpeakerOutputRectFor(owner.Dpi),Text="输出到："+(audio==null?"未连接":audio.SpeakerName),Enabled=audio!=null&&audio.SpeakerAvailable});
                    rows.Add(new PopupControl {Id=101,Rect=VisualDesign.HeadsetOutputRectFor(owner.Dpi),Text="输出到："+(audio==null?"未连接":audio.HeadsetName),Enabled=audio!=null&&audio.HeadsetAvailable});
                    rows.Add(new PopupControl {Id=102,Rect=VisualDesign.MicrophoneMuteRectFor(owner.Dpi),Text="Windows麦克风："+(audio==null?"未连接":audio.MicrophoneName),Enabled=audio!=null&&audio.MicrophoneAvailable&&audio.MicrophoneMuted.HasValue});
                    foreach(var control in rows)if(control.Id>=100&&control.Id<=102){var hit=control.Rect;hit.Inflate(0,2);control.Rect=hit;}
                    QualifyAudioCard(rows,audioCard);
                }
                // Keep the first keyboard target compatible with the previous release.
                var feedback=owner.GetFeedback();
                rows.Insert(0,new PopupControl {Id=0,Rect=VisualDesign.RefreshRectFor(secondCard,owner.Dpi),Text=feedback.DpiFailure??feedback.AudioFailure??"立即同步全部设备"});
                rows.Insert(1,new PopupControl {Id=1,Rect=VisualDesign.MoreRectFor(secondCard,owner.Dpi),Text="更多选项"});
                AddMainVolume(rows,owner,nav,VisualDesign.SystemVolumeRectFor(secondCard,owner.Dpi),pref.VolumeCollapsed);
                return WithIdentity(rows,nav,owner);
            }
            if(nav.Page==PopupPage.More) {
                if(owner.UpdateAvailable)rows.Add(Row(260,owner.UpdateStatus,null,"update"));
                rows.Add(Row(200,"音源与麦克风",null,"speaker"));rows.Add(Row(201,"显示与提醒",null,"settings"));
                rows.Add(Row(206,"设备管理",null,"keyboard"));rows.Add(Row(207,"设备没显示？",null,"info"));
                rows.Add(Row(202,"状态与诊断",null,"info"));rows.Add(Row(204,"退出",null,"power"));
            }else if(nav.Page==PopupPage.Audio) {
                if(nav.AudioDeviceKey!=null)rows.Add(Info("控制对象",nav.AudioDeviceName??"此耳机"));
                rows.Add(Info("当前系统输出",OutputName(audio)));
                var a=Row(100,"扬声器",null,"speaker");a.Detail=audio==null?"未检测到":audio.SpeakerAvailable?audio.SpeakerName:"请到音频设置中绑定扬声器端点";a.Enabled=audio!=null&&audio.SpeakerAvailable;a.Selected=audio!=null&&audio.ActiveOutput=="speaker";rows.Add(a);
                a=Row(101,"耳机",null,"headset");a.Detail=audio==null?"未检测到":audio.HeadsetName;a.Enabled=audio!=null&&audio.HeadsetAvailable;a.Selected=audio!=null&&audio.ActiveOutput=="headset";rows.Add(a);
                a=Row(240,"系统输出音量");a.Slider=true;a.Enabled=audio!=null&&audio.OutputVolumePercent.HasValue&&!String.IsNullOrEmpty(audio.VolumeEndpointId);a.Value=a.Enabled?(nav.VolumePreview>=0?nav.VolumePreview:owner.DesiredVolume??audio.OutputVolumePercent.Value)+"%":"不可用";rows.Add(a);
                a=Toggle(102,"Windows麦克风静音",audio!=null&&audio.MicrophoneMuted==true);a.Enabled=audio!=null&&audio.MicrophoneAvailable&&audio.MicrophoneMuted.HasValue;rows.Add(a);
                rows.Add(Row(203,"更换按钮绑定",null,"settings"));
            }else if(nav.Page==PopupPage.Preferences) {
                rows.Add(Section("显示"));
                rows.Add(Row(210,"外观",pref.Theme=="auto"?"跟随系统":pref.Theme=="dark"?"深色":"浅色"));
                rows.Add(Row(223,"配色",ThemePalette.Name(pref.ThemeStyle)));
                rows.Add(Row(211,"托盘图标",(pref.TrayGroups.Count+1)+" 个"));
                rows.Add(Toggle(215,"减弱动效",pref.ReducedMotion));
                rows.Add(Section("提醒"));rows.Add(Toggle(212,"低电量提醒",pref.LowBatteryNotifications));rows.Add(Row(213,"提醒阈值",pref.WarningThreshold+"%"));
                preferenceSplit=rows.Count;
                rows.Add(Section("数据"));
                rows.Add(Row(216,"后台同步间隔",pref.BackgroundSeconds==60?"1分钟":pref.BackgroundSeconds==120?"2分钟":"5分钟"));
                rows.Add(Toggle(214,"跨重启保留读数",pref.RememberReadings));
                var clear=Row(217,"清除历史读数",null,"refresh");clear.Detail="关闭保留不删除历史，清除需单独操作";rows.Add(clear);
                rows.Add(Section("更新"));rows.Add(Toggle(219,"自动检查更新",pref.AutoCheckUpdates));
                rows.Add(Toggle(225,"托盘图标显示更新角标",pref.ShowUpdateBadge));
                rows.Add(Toggle(226,"自动检查反馈回复",pref.CheckFeedbackReplies));
            }else if(nav.Page==PopupPage.TrayIcons){
                var firstGroup=GroupRow("group",TrayConfiguration.FirstName,TrayConfiguration.FirstId,TrayRule(owner,pref.TraySource,pref.TrayDeviceId));firstGroup.Id=600;rows.Add(firstGroup);
                foreach(var group in pref.TrayGroups){var row=GroupRow("group",group.Name,group.Id,TrayRule(owner,group.Source,group.DeviceId));row.Id=600+rows.Count;rows.Add(row);}
                var add=GroupRow("add","添加托盘图标",null);add.Enabled=pref.TrayGroups.Count<3;rows.Add(add);
                rows.Add(Row(224,"图标样式",pref.TrayIconStyle=="device"?"设备图标":"数字"));
                if(nav.TrayHint){var hint=Info(TrayConfiguration.HiddenIconHint,null);hint.WrapText=true;rows.Add(hint);}
            }else if(nav.Page==PopupPage.TrayGroup){
                var group=TrayConfiguration.Find(pref,nav.TrayGroupKey);
                if(group==null)rows.Add(Info("包含其余所有设备",null));else rows.Add(GroupRow("rename","名称",group.Id,group.Name));
                rows.Add(GroupRow("rules","显示规则",nav.TrayGroupKey,TrayRule(owner,group==null?pref.TraySource:group.Source,group==null?pref.TrayDeviceId:group.DeviceId)));
                if(group!=null){rows.Add(GroupRow("members","包含的设备",group.Id,group.Members.Count+" 台"));rows.Add(GroupRow("delete","删除此图标",group.Id));}
            }else if(nav.Page==PopupPage.TrayRules){
                var group=TrayConfiguration.Find(pref,nav.TrayGroupKey);
                if(group!=null){
                    foreach(string source in new[]{"active","lowest"}){var rule=GroupRow("rule",source=="active"?"当前使用的设备":"最低电量",group.Id);rule.Id=620+rows.Count;rule.TargetToken=source;rule.Selected=String.IsNullOrEmpty(group.DeviceId)&&group.Source==source;if(source=="active")rule.Detail="只识别鼠标，其他设备按最低电量";rows.Add(rule);}
                    foreach(var device in MemberDevices(owner,pref))if(group.Members.Contains(device.Id)){var pin=GroupRow("group-pin","固定："+device.Name,group.Id);pin.Id=4000+rows.Count;pin.DeviceKey=device.Id;pin.Selected=group.DeviceId==device.Id;rows.Add(pin);}
                }
            }else if(nav.Page==PopupPage.TrayMembers){
                var group=TrayConfiguration.Find(pref,nav.TrayGroupKey);
                if(group!=null)foreach(var device in MemberDevices(owner,pref)){
                    var member=GroupRow("member",device.Name,group.Id);member.Id=5000+rows.Count;member.DeviceKey=device.Id;member.Selected=group.Members.Contains(device.Id);
                    string current=TrayConfiguration.Owner(pref,device.Id);if(current!=group.Id)member.Detail="当前在："+TrayConfiguration.Name(pref,current);rows.Add(member);
                }
            }else if(nav.Page==PopupPage.ConfirmTrayDelete){
                var note=Info("删除此图标后，包含的设备将回到托盘图标 1",null);note.WrapText=true;rows.Add(note);
                rows.Add(GroupRow("confirm-delete","确认删除",nav.TrayGroupKey));
            }else if(nav.Page==PopupPage.ChoosePreference) {
                string[] labels;int first,selected;
                if(nav.PreferenceAction==210){labels=new[]{"跟随系统","浅色","深色"};first=400;selected=pref.Theme=="auto"?0:pref.Theme=="light"?1:2;}
                else if(nav.PreferenceAction==223){labels=new[]{"海蓝","原生","琥珀","青雾"};first=440;selected=Array.IndexOf(ThemePalette.Keys,ThemePalette.Normalize(pref.ThemeStyle));}
                else if(nav.PreferenceAction==224){labels=new[]{"数字","设备图标"};first=450;selected=pref.TrayIconStyle=="device"?1:0;}
                else if(nav.PreferenceAction==211){labels=new[]{"当前使用的设备","最低电量","自动选择有数据的设备","鼠标优先","耳机优先","键盘优先"};first=410;selected=pref.TraySource=="active"?0:pref.TraySource=="lowest"?1:pref.TraySource=="auto"?2:pref.TraySource=="mouse"?3:pref.TraySource=="headset"?4:5;}
                else if(nav.PreferenceAction==213){labels=new[]{"10%","15%","20%"};first=420;selected=pref.WarningThreshold==10?0:pref.WarningThreshold==15?1:2;}
                else {labels=new[]{"1分钟","2分钟","5分钟"};first=430;selected=pref.BackgroundSeconds==60?0:pref.BackgroundSeconds==120?1:2;}
                int[] trayRuleIds={415,414,410,411,412,413};
                for(int i=0;i<labels.Length;i++){var choice=Row(nav.PreferenceAction==211?trayRuleIds[i]:first+i,labels[i]);choice.Selected=i==selected&&(nav.PreferenceAction!=211||String.IsNullOrEmpty(pref.TrayDeviceId));if(nav.PreferenceAction==223)choice.PaletteKey=ThemePalette.Keys[i];rows.Add(choice);}
                if(nav.PreferenceAction==211) {
                    rows[0].Detail="仅跟随 USB 鼠标活动";
                    foreach(var device in TrayDevices(owner)) {
                        var choice=Row(500+rows.Count-6,"固定："+device.Name);choice.DeviceKey=device.Id;choice.ActionKey="tray:pin";choice.Selected=pref.TrayDeviceId==device.Id;rows.Add(choice);
                    }
                    if(!String.IsNullOrEmpty(pref.TrayDeviceId)&&!rows.Exists(delegate(PopupControl row){return row.Selected;})) {
                        var missing=Row(500+rows.Count-6,"固定：已断开的设备");missing.DeviceKey=pref.TrayDeviceId;missing.ActionKey="tray:pin";missing.Selected=true;rows.Add(missing);
                    }
                }
            }else if(nav.Page==PopupPage.Mouse) {
                rows.Add(Row(220,"显示名称",pref.MouseAlias));rows.Add(Info("连接方式","2.4 GHz"));rows.Add(Info("适配验证","本项目已实机验证"));
                string dpi=owner.Dpi==null||owner.Dpi.Stages==null||owner.Dpi.ActiveStage<0||owner.Dpi.ActiveStage>=owner.Dpi.Stages.Count?"未确认":owner.Dpi.Stages[owner.Dpi.ActiveStage].XDpi+" DPI";
                rows.Add(Info("当前档位",String.IsNullOrEmpty(owner.DpiError)?dpi:"上次 · "+dpi));
                var value=UiState.Mouse(owner.Current,owner.LastGood,DateTime.UtcNow);rows.Add(Info("电量",value.Percent.HasValue?value.Percent+"%":"未读取"));
                rows.Add(Info("最近确认",value.TimeText));rows.Add(Toggle(218,"固定为托盘主设备",pref.TrayDeviceId=="native:mouse"));rows.Add(Row(4,"同步鼠标",null,"refresh"));
                rows.Add(Row(257,"显示与排序",null,"settings"));
            }else if(nav.Page==PopupPage.Headset) {
                var value=UiState.Headset(owner.Headset,DateTime.UtcNow);rows.Add(Row(221,"显示名称",pref.HeadsetAlias));
                rows.Add(Info("适配验证","本项目已实机验证"));
                rows.Add(Info("无线连接",headset?"已连接":"未确认连接"));rows.Add(Info("电量",value.Percent.HasValue?value.Percent+"%":"未读取"));
                rows.Add(Info("最近确认",value.TimeText));rows.Add(Toggle(218,"固定为托盘主设备",pref.TrayDeviceId=="native:headset"));rows.Add(Row(200,"音源与麦克风",null,"speaker"));rows.Add(Row(5,"同步耳机",null,"refresh"));
                rows.Add(Row(257,"显示与排序",null,"settings"));
            }else if(nav.Page==PopupPage.Bindings) {
                if(nav.AudioDeviceKey!=null)rows.Add(Info("控制对象",nav.AudioDeviceName??"此耳机"));
                rows.Add(Row(230,"扬声器按钮",UiState.EndpointName(audio,audioPref.SpeakerEndpointId)));rows.Add(Row(231,"耳机按钮",UiState.EndpointName(audio,audioPref.HeadsetEndpointId)));
                rows.Add(Row(232,"麦克风按钮",UiState.EndpointName(audio,audioPref.MicrophoneEndpointId)));rows.Add(Row(205,"刷新设备列表",null,"refresh"));rows.Add(Row(233,nav.AudioDeviceKey==null?"恢复自动识别":"清除此设备绑定"));
                foreach(var row in rows)if(row.Id>=230&&row.Id<=232&&row.Value=="未检测到")row.Value=nav.AudioDeviceKey==null?"自动识别":"未绑定";
            }else if(nav.Page==PopupPage.PickBinding) {
                string selected=audioPref.EndpointFor(nav.BindingRole);var choices=new List<PopupControl>();
                var first=Row(300,nav.AudioDeviceKey==null?"自动识别":"未绑定");first.Selected=String.IsNullOrEmpty(selected);choices.Add(first);
                if(audio!=null&&audio.Devices!=null)foreach(var device in audio.Devices)if(device.Flow==(nav.BindingRole=="microphone"?1:0)) {
                    var row=Row(300+choices.Count,device.Name);row.EndpointId=device.Id;row.Selected=AudioControl.SameId(device.Id,selected);choices.Add(row);
                }
                if(!String.IsNullOrEmpty(selected)&&!choices.Exists(delegate(PopupControl c){return c.Selected;})) {
                    var missing=Row(300+choices.Count,"已绑定设备未连接");missing.EndpointId=selected;missing.Selected=true;choices.Add(missing);
                }
                int visible=Math.Max(2,nav.VisibleChoices);nav.Offset=Math.Max(0,Math.Min(nav.Offset,Math.Max(0,choices.Count-visible)));
                for(int i=nav.Offset;i<Math.Min(choices.Count,nav.Offset+visible);i++)rows.Add(choices[i]);
                if(nav.Offset>0)rows.Add(Row(7,"上一页"));if(nav.Offset+visible<choices.Count)rows.Add(Row(8,"下一页"));
            }else if(nav.Page==PopupPage.Diagnostics) {
                rows.Add(Info("版本",AppRuntime.Version));rows.Add(Info("鼠标读取",UiState.Mouse(owner.Current,owner.LastGood,DateTime.UtcNow).Fresh?"已确认":"历史 / 未知"));
                rows.Insert(1,Info("软件更新",owner.UpdateStatus));
                rows.Insert(2,Row(owner.UpdateAvailable?260:261,owner.UpdateAvailable?owner.UpdateStatus:"检查更新",null,owner.UpdateAvailable?"update":"info"));
                if(!String.IsNullOrEmpty(owner.UpdateFailure))rows.Insert(3,Info("上次更新失败",owner.UpdateFailure));
                rows.Add(Info("耳机无线",headset?"已确认连接":"未确认连接"));rows.Add(Info("后台同步",pref.BackgroundSeconds+"秒"));
                rows.Add(Info("配置状态",DeviceSettings.UsingDefaults?"读取失败，使用默认":"正常"));
                rows.Add(Info("诊断写入",owner.StorageHealthText));
                rows.Add(Info("开机启动",StartupState()));rows.Add(Info("通用设备",owner.GenericReady?owner.WirelessDevices.Length+"个":owner.GenericScanning?"正在识别":"尚未就绪"));
                if(!String.IsNullOrEmpty(owner.GenericHealthSummary))rows.Add(Info("读取器健康",owner.GenericHealthSummary));
                else if(!String.IsNullOrEmpty(owner.GenericError))rows.Add(Info("读取器状态","暂不可用，详情见诊断摘要"));
                rows.Add(Info("许可证","AGPL-3.0-only"));rows.Add(Info("无担保","按现状提供"));
                var source=Info("源码",null);source.Detail="https://github.com/\nwushuai0802/\nmyfinder-device-battery";rows.Add(source);
                rows.Add(Row(250,"复制诊断摘要",null,"info"));
                var feedbackRow=Row(258,"反馈问题",null,"info");feedbackRow.Detail=String.IsNullOrEmpty(FeedbackRelay.Url)?"提交需要 GitHub 账号":"在程序内提交，不需要 GitHub 账号";rows.Add(feedbackRow);
                rows.Add(Row(262,owner.FeedbackReplies.Store.MenuText,null,"info"));
                var pack=Info("型号数据包",owner.DevicePackDiagnostic);pack.WrapText=true;rows.Add(pack);
            }else if(nav.Page==PopupPage.DeviceMissing) {
                var layout=WirelessLayout.Build(owner,0,100000);
                if(layout.All[0].Id=="empty"){var status=Info("当前状态",WirelessLayout.EmptyReason(owner,layout.AllHidden));status.WrapText=true;rows.Add(status);}
                var explanation=Info("请确认接收器已插好，设备已开机并唤醒",null);explanation.WrapText=true;rows.Add(explanation);
                rows.Add(Row(0,"重新识别设备",null,"refresh"));rows.Add(Row(259,"查看支持清单",null,"info"));rows.Add(Row(250,"复制诊断摘要",null,"info"));
                var feedbackRow=Row(258,"反馈问题",null,"info");feedbackRow.Detail=String.IsNullOrEmpty(FeedbackRelay.Url)?"提交需要 GitHub 账号":"在程序内提交，不需要 GitHub 账号";rows.Add(feedbackRow);
                rows.Add(Row(262,owner.FeedbackReplies.Store.MenuText,null,"info"));
                rows.Add(Info("反馈不代表一定能适配",null));
            }else if(nav.Page==PopupPage.Devices) {
                var devices=WirelessLayout.Build(owner,0,100000,true).All;
                nav.Offset=Math.Max(0,Math.Min(nav.Offset,Math.Max(0,devices.Count-nav.VisibleChoices)));
                int count=Math.Min(nav.VisibleChoices,devices.Count-nav.Offset);for(int i=0;i<count;i++){var device=devices[nav.Offset+i];var row=Row(1000+nav.Offset+i,device.Name,device.Hidden?"已隐藏":"显示中",device.Kind);row.DeviceKey=device.Id;row.ActionKey="manage";row.Enabled=device.Id!="empty";rows.Add(row);}
                if(nav.Offset>0)rows.Add(Row(7,"上一页"));if(nav.Offset+count<devices.Count)rows.Add(Row(8,"下一页"));
                rows.Add(Row(0,"重新识别设备",null,"refresh"));
                rows.Add(Row(207,"设备没显示？",null,"info"));
            }else if(nav.Page==PopupPage.ManageDevice) {
                var devices=WirelessLayout.Build(owner,0,100000,true).All;var device=devices.Find(delegate(WirelessCard card){return card.Id==nav.SelectedDeviceKey;});
                string name=device==null?nav.SelectedDeviceName:device.Name;bool hidden=pref.HiddenDeviceIds.Contains(nav.SelectedDeviceKey);
                rows.Add(Info("设备",name??"无线设备"));
                var show=Toggle(252,"在主界面显示",!hidden);show.Enabled=!String.IsNullOrEmpty(nav.SelectedDeviceKey);rows.Add(show);
                rows.Add(Info("显示状态",hidden?"已隐藏，仍继续读取":"显示中，隐藏不停止读取"));
                int index=devices.FindIndex(delegate(WirelessCard card){return card.Id==nav.SelectedDeviceKey;});
                var top=Row(253,"置顶");top.Enabled=index>0;rows.Add(top);var up=Row(254,"上移一位");up.Enabled=index>0;rows.Add(up);var down=Row(255,"下移一位");down.Enabled=index>=0&&index<devices.Count-1;rows.Add(down);
                var detail=Row(256,"设备详情",null,"info");detail.Enabled=device!=null;rows.Add(detail);
            }else if(nav.Page==PopupPage.Device) {
                var device=Array.Find(owner.WirelessDevices,delegate(WirelessDevice d){return d.Id==nav.SelectedDeviceKey;});
                rows.Add(Row(222,"显示名称",nav.SelectedDeviceName));
                if(device!=null){var reading=WirelessLayout.View(device);rows.Add(Info("类型",device.Kind=="keyboard"?"键盘":device.Kind=="mouse"?"鼠标":device.Kind=="headset"?"耳机":"无线设备"));rows.Add(Info("连接",device.Transport));rows.Add(Info("适配验证",String.IsNullOrEmpty(device.VerificationLabel)?"待型号实测":device.VerificationLabel));rows.Add(Info("电量",device.Percent.HasValue?(device.Approximate?"约":"")+device.Percent+"%":"暂不可读"));rows.Add(Info("数据",reading.SystemProvided?"系统提供":reading.Fresh?device.Approximate?"估算 / 档位":"设备上报":"历史读数"));rows.Add(Info(reading.SystemProvided?"最近查询":"最近确认",reading.SystemProvided?reading.QueryText:reading.TimeText));}
                else rows.Add(Info("状态","设备已断开"));
                if(device!=null&&device.Kind=="mouse"&&device.Dpi!=null&&device.Dpi.Supported) {
                    var dpi=device.Dpi;string failure=owner.WirelessDpiFailure(device.Id,dpi.TargetToken)??dpi.Error;bool fresh=WirelessLayout.DpiFresh(device)&&String.IsNullOrEmpty(failure);
                    rows.Insert(3,Info(WirelessLayout.DpiLabel(device),dpi.Current.HasValue?(fresh?"":"上次 ")+dpi.Current.Value:"未读取"));
                    rows.Insert(4,Info("DPI来源",dpi.StageSource=="onboard"?"设备板载档位":dpi.StageSource=="quick"?"快捷值，非板载":"未确认"));
                    rows.Add(Info("DPI最近确认",dpi.CheckedTime==DateTime.MinValue?"时间未知":BatteryView.FormatTime(dpi.CheckedTime)));
                    rows.Add(Info("DPI状态",!String.IsNullOrEmpty(failure)?failure:!fresh?"连接或读数待确认":owner.CanSetWirelessDpi(device.Id,dpi.TargetToken)?"可切换":"只读"));
                }
                var pin=Toggle(218,"固定为托盘主设备",!String.IsNullOrEmpty(nav.SelectedDeviceKey)&&pref.TrayDeviceId==nav.SelectedDeviceKey);pin.Enabled=device!=null;rows.Add(pin);
                var sync=Row(251,WirelessLayout.RefreshText(device),null,"refresh");sync.Enabled=device!=null;rows.Add(sync);
                if(device!=null&&device.Kind=="headset")rows.Add(Row(200,"音源与麦克风",null,"speaker"));
                rows.Add(Row(257,"显示与排序",null,"settings"));
            }
            if(nav.Page!=PopupPage.Devices&&nav.Page!=PopupPage.PickBinding) {
                int visible=Math.Max(2,nav.VisibleChoices);
                var preferences=preferenceSplit>0?PreferenceRows(rows,nav,visible,preferenceSplit):null;
                if(preferences!=null)rows=preferences;
                else if(rows.Count>visible) {
                    int total=rows.Count;nav.Offset=Math.Max(0,Math.Min(nav.Offset,total-visible));
                    rows=rows.GetRange(nav.Offset,Math.Min(visible,total-nav.Offset));
                    if(nav.Offset>0)rows.Add(Row(7,"上一页"));if(nav.Offset+visible<total)rows.Add(Row(8,"下一页"));
                }else nav.Offset=0;
            }
            float y=44;
            foreach(var row in rows){float height=RowHeight(row);
                row.Rect=new RectangleF(10,y,240,height);y+=height+2;}
            rows.Insert(0,new PopupControl {Id=6,Text="返回",Rect=new RectangleF(8,5,34,34)});
            return WithIdentity(rows,nav,owner);
        }
        static float RowHeight(PopupControl row) {
            float height=row.Section?24:row.Slider?54:!String.IsNullOrEmpty(row.Detail)?26+18*row.Detail.Split('\n').Length:34;
            if(row.WrapText)using(var bitmap=new Bitmap(1,1))using(var g=Graphics.FromImage(bitmap))using(var font=new Font("Microsoft YaHei UI",11.5f,FontStyle.Regular,GraphicsUnit.Pixel))height=Math.Max(height,(float)Math.Ceiling(g.MeasureString(WrappedText(row),font,new SizeF(211,1000)).Height)+12);
            return height;
        }
        static int PageHeight(List<PopupControl> rows,int start,int count,int buttons) {
            float bottom=44;for(int i=start;i<start+count;i++)bottom+=RowHeight(rows[i])+2;
            return (int)bottom+buttons*36-2+28;
        }
        static PopupControl PageButton(int id,int offset){var row=Row(id,id==7?"上一页":"下一页");row.PageOffset=offset;return row;}
        static List<PopupControl> PreferenceRows(List<PopupControl> rows,PopupNavigation nav,int visible,int split) {
            if(rows.Count<=visible)return null;
            int height=Math.Max(PageHeight(rows,0,split,1),PageHeight(rows,split,rows.Count-split,1));
            if(nav.MaximumHeight>0?height<=nav.MaximumHeight:visible>=7) {
                bool first=nav.Offset<=0;nav.Offset=first?0:split;
                var page=rows.GetRange(nav.Offset,first?split:rows.Count-split);page.Add(PageButton(first?8:7,first?split:0));return page;
            }
            if(nav.MaximumHeight<=0)return null;
            // Keep row paging on short work areas, including the taller data note.
            // Explicit offsets let both arrows follow the same measured page boundaries.
            var starts=new List<int>();var counts=new List<int>();
            for(int start=0;start<rows.Count;) {
                int count=Math.Min(visible,rows.Count-start);
                while(count>1&&PageHeight(rows,start,count,(start>0?1:0)+(start+count<rows.Count?1:0))>nav.MaximumHeight)count--;
                starts.Add(start);counts.Add(count);start+=count;
            }
            int index=0;while(index+1<starts.Count&&starts[index+1]<=nav.Offset)index++;
            nav.Offset=starts[index];var result=rows.GetRange(nav.Offset,counts[index]);
            if(index>0)result.Add(PageButton(7,starts[index-1]));if(index+1<starts.Count)result.Add(PageButton(8,starts[index+1]));return result;
        }
        static List<PopupControl> WithIdentity(List<PopupControl> rows,PopupNavigation nav,TrayContext owner) {
            string context=nav.Page==PopupPage.Mouse?"native:mouse":nav.Page==PopupPage.Headset?"native:headset":nav.Page==PopupPage.Device||nav.Page==PopupPage.ManageDevice?nav.SelectedDeviceKey:nav.Page==PopupPage.Audio||nav.Page==PopupPage.Bindings||nav.Page==PopupPage.PickBinding?nav.AudioDeviceKey:null;
            var feedback=owner.GetFeedback();
            foreach(var row in rows) {
                if(String.IsNullOrEmpty(row.DeviceKey)) {
                    if(row.Id==2||row.Id==4||row.Id>=10&&row.Id<18)row.DeviceKey="native:mouse";
                    else if(row.Id==3||row.Id==5||row.Id>=100&&row.Id<=102)row.DeviceKey=nav.AudioDeviceKey??"native:headset";
                    else if(context!=null&&row.Id!=6)row.DeviceKey=context;
                }
                if(row.Id>=10&&row.Id<18){row.TargetToken=owner.NativeDpiToken;row.ActionKey="dpi:"+(row.Id-10)+":"+(row.TargetToken??"unconfirmed");row.Enabled&=owner.CanSetNativeDpi;}
                if(nav.Page==PopupPage.PickBinding&&row.Id>=300&&row.Id<400)row.ActionKey="binding:"+nav.BindingRole+":"+(row.EndpointId??"auto");
                if(row.Id>=10&&row.Id<18){row.Selected=owner.Dpi!=null&&owner.Dpi.ActiveStage==row.Id-10;row.Busy=feedback.PendingStage==row.Id-10;}
                if(row.DpiValue>0) {
                    var device=Array.Find(owner.WirelessDevices,delegate(WirelessDevice item){return item.Id==row.DeviceKey&&item.Dpi!=null&&item.Dpi.TargetToken==row.TargetToken;});
                    row.Enabled&=device!=null&&owner.CanSetWirelessDpi(row.DeviceKey,row.TargetToken);
                    row.Selected=device!=null&&WirelessLayout.DpiFresh(device)&&String.IsNullOrEmpty(owner.WirelessDpiFailure(row.DeviceKey,row.TargetToken))&&device.Dpi.Current==row.DpiValue;row.Busy=owner.DesiredWirelessDpi(row.DeviceKey,row.TargetToken)==row.DpiValue;
                }
                if(row.Id==100||row.Id==101){row.Selected=owner.AudioControlsAvailable&&owner.Audio!=null&&owner.Audio.ActiveOutput==(row.Id==100?"speaker":"headset");row.Busy=feedback.PendingOutput==(row.Id==100?"speaker":"headset");}
                if(row.Id==102){row.Toggle=true;row.Checked=owner.AudioControlsAvailable&&owner.Audio!=null&&owner.Audio.MicrophoneMuted==true;row.Busy=feedback.MicrophonePending;}
                if(row.Id==100||row.Id==101||row.Id==102||row.Id==240)row.Enabled&=owner.AudioControlsAvailable;
                if(row.Id==0)row.Busy=feedback.ManualRefreshing;
                if(row.Id==4)row.Busy=owner.IsRefreshing;
                if(row.Id==5)row.Busy=owner.IsHeadsetRefreshing;
                if(row.Id==251||row.Id>=2000&&row.Id<3000)row.Busy=owner.IsDeviceRefreshing(row.DeviceKey??nav.SelectedDeviceKey);
            }
            return rows;
        }
        internal static PopupControl FindStable(List<PopupControl> controls,string key) {
            if(String.IsNullOrEmpty(key))return null;
            return controls.Find(delegate(PopupControl c){return c.Enabled&&!c.Information&&c.Id>=0&&c.StableKey==key;});
        }
        internal static PopupControl ReconcileFocus(List<PopupControl> controls,string key,int previousIndex) {
            var same=FindStable(controls,key);if(same!=null)return same;
            for(int i=Math.Max(0,Math.Min(previousIndex,controls.Count-1));i<controls.Count;i++)if(controls[i].Enabled&&!controls[i].Information&&controls[i].Id>=0)return controls[i];
            for(int i=controls.Count-1;i>=0;i--)if(controls[i].Enabled&&!controls[i].Information&&controls[i].Id>=0)return controls[i];return null;
        }
        internal static bool SameAction(PopupControl left,PopupControl right) {return left!=null&&right!=null&&left.Id>=0&&right.Id>=0&&!left.Information&&!right.Information&&left.Enabled&&right.Enabled&&left.StableKey==right.StableKey;}
        static PopupControl MainVolume(TrayContext owner,PopupNavigation nav,RectangleF rectangle) {
            var audio=owner.Audio;bool enabled=owner.AudioScopeDeviceId==null&&owner.AudioControlsAvailable&&audio!=null&&audio.OutputVolumePercent.HasValue&&!String.IsNullOrEmpty(audio.VolumeEndpointId);
            return new PopupControl {Id=240,Text="系统输出音量",Slider=true,Rect=rectangle,Enabled=enabled,Value=enabled?(nav.VolumePreview>=0?nav.VolumePreview:owner.DesiredVolume??audio.OutputVolumePercent.Value)+"%":"不可用",Detail=UiState.CurrentOutputName(audio)};
        }
        static void AddMainVolume(List<PopupControl> rows,TrayContext owner,PopupNavigation nav,RectangleF rectangle,bool collapsed) {
            var volume=MainVolume(owner,nav,VisualDesign.VolumeSliderRect(rectangle));
            if(!collapsed)rows.Add(volume);
            rows.Add(new PopupControl {Id=241,Text=collapsed?"展开系统音量":"收起系统音量",Value=collapsed?volume.Value:null,Detail=volume.Detail,Rect=VisualDesign.VolumeToggleRect(rectangle,collapsed),ActionKey=collapsed?"volume:expand":"volume:collapse"});
        }
        static void QualifyAudioCard(List<PopupControl> rows,AudioCardView card) {
            if(card==null)return;
            foreach(var row in rows)if(row.Id>=100&&row.Id<=102){row.DeviceKey="native:audio";if(row.Id==101){row.Enabled&=card.HasTarget;row.Detail=card.HasTarget?card.Subtitle:"请到音频设置中绑定耳机端点";}}
        }
        static List<PopupControl> BuildWireless(TrayContext owner,WirelessLayout layout,PopupNavigation nav) {
            var rows=new List<PopupControl>();
            rows.Add(new PopupControl {Id=0,Text="立即同步全部设备",Rect=new RectangleF(8,layout.Footer+1,layout.Paged?146:208,28)});
            rows.Add(new PopupControl {Id=1,Text="更多选项",Rect=new RectangleF(226,layout.Footer+1,28,28)});
            foreach(var card in layout.Cards) {
                if(card.Id=="empty") {
                    rows.Add(new PopupControl {Id=1000,Text=layout.AllHidden?"设备管理":"设备没显示？",Detail=card.Subtitle,Rect=new RectangleF(0,card.Top,260,card.Height),DeviceKey=card.Id,ActionKey=layout.AllHidden?"devices":"device-missing",Enabled=!owner.GenericScanning});
                }else if(card.NativeMouse) {
                    var name=VisualDesign.MouseNameRect;name.Y+=card.Top;var meter=VisualDesign.MouseMeterRect;meter.Y+=card.Top;
                    rows.Add(new PopupControl {Id=2,Text=card.Name,Rect=name,DeviceKey=card.Id});rows.Add(new PopupControl {Id=4,Text=card.Battery.Tooltip+"，点击刷新",Rect=meter,DeviceKey=card.Id});
                    if(owner.Dpi!=null)for(int i=0;i<owner.Dpi.Stages.Count;i++){var hit=VisualDesign.StageRect(i,owner.Dpi.Stages.Count);hit.Y+=card.Top;hit.Inflate(0,owner.Dpi.Stages.Count>4?1:2);rows.Add(new PopupControl {Id=10+i,Text=owner.Dpi.Stages[i].XDpi+" DPI",Rect=hit,DeviceKey=card.Id});}
                }else if(card.NativeHeadset||card.AudioOnly) {
                    float delta=card.Top-VisualDesign.MouseCardHeight(owner.Dpi)-8;
                    var name=VisualDesign.HeadsetNameRectFor(owner.Dpi);name.Y+=delta;var meter=VisualDesign.HeadsetMeterRectFor(owner.Dpi);meter.Y+=delta;
                    if(card.AudioOnly)rows.Add(new PopupControl {Id=200,Text="音源与麦克风",Value=card.Subtitle,Rect=new RectangleF(10,card.Top+6,240,44),DeviceKey=card.Id});
                    else {rows.Add(new PopupControl {Id=3,Text=card.Name,Rect=name,DeviceKey=card.Id});rows.Add(new PopupControl {Id=5,Text=card.Battery.Tooltip+"，点击刷新",Rect=meter,DeviceKey=card.Id});}
                    foreach(int id in new[]{100,101,102}) {
                        var hit=id==100?VisualDesign.SpeakerOutputRectFor(owner.Dpi):id==101?VisualDesign.HeadsetOutputRectFor(owner.Dpi):VisualDesign.MicrophoneMuteRectFor(owner.Dpi);hit.Y+=delta;hit.Inflate(0,2);
                        var audio=owner.Audio;bool enabled=audio!=null&&(id==100?audio.SpeakerAvailable:id==101?audio.HeadsetAvailable:audio.MicrophoneAvailable&&audio.MicrophoneMuted.HasValue);
                        rows.Add(new PopupControl {Id=id,Text=id==100?"输出到扬声器":id==101?"输出到耳机":"Windows麦克风静音",Rect=hit,Enabled=enabled,DeviceKey=card.Id});
                    }
                    if(card.AudioOnly)QualifyAudioCard(rows,UiState.AudioCard(owner.Audio,DeviceSettings.Current,owner.Headset,owner.WirelessDevices));
                }else {
                    int index=layout.All.IndexOf(card);
                    rows.Add(new PopupControl {Id=1000+index,Text=card.Name,Rect=new RectangleF(10,card.Top+6,150,44),DeviceKey=card.Id,Enabled=card.Id!="empty"});
                    rows.Add(new PopupControl {Id=2000+index,Text=card.Battery.Tooltip+"，"+WirelessLayout.RefreshText(card.Device),Rect=new RectangleF(168,card.Top+6,84,44),DeviceKey=card.Id,Enabled=card.Id!="empty"});
                    int count=WirelessLayout.DpiStageCount(card.Device);
                    for(int i=0;i<count;i++) {
                        var dpi=card.Device.Dpi;int value=dpi.Stages[i];var rect=WirelessLayout.DpiRect(card,i);rect.Inflate(0,count>4?1:2);
                        rows.Add(new PopupControl {Id=WirelessLayout.DpiActionId(index,i),DpiValue=value,TargetToken=dpi.TargetToken,DeviceKey=card.Id,Rect=rect,Text=card.Name+"，设置"+WirelessLayout.DpiLabel(card.Device)+"为 "+value,Value=value+" DPI",Detail=dpi.StageSource=="quick"?"软件快捷值，不代表设备板载预设":"设备板载DPI档位",Enabled=owner.CanSetWirelessDpi(card.Id,dpi.TargetToken)});
                    }
                }
            }
            if(layout.Paged){rows.Add(new PopupControl {Id=902,Text="上一组设备",Rect=new RectangleF(158,layout.Footer+1,28,28),Enabled=layout.Start>0});rows.Add(new PopupControl {Id=903,Text="下一组设备",Rect=new RectangleF(192,layout.Footer+1,28,28),Enabled=layout.End<layout.All.Count});}
            if(layout.HasVolume)AddMainVolume(rows,owner,nav,layout.VolumeRect,layout.VolumeCollapsed);
            return rows;
        }
        internal static Size SizeFor(List<PopupControl> controls){float bottom=44;foreach(var c in controls)bottom=Math.Max(bottom,c.Rect.Bottom);return new Size(260,(int)bottom+28);}
        internal static PopupControl Hit(List<PopupControl> controls,PointF point){foreach(var c in controls)if(c.Id>=0&&c.Enabled&&!c.Information&&c.Rect.Contains(point))return c;return null;}
        internal static string StartupState() {
            return StartupDiagnostics.Describe();
        }
        internal static string Title(PopupNavigation nav) {
            if(nav.Page==PopupPage.TrayIcons)return "托盘图标";
            if(nav.Page==PopupPage.TrayGroup)return TrayConfiguration.Name(DeviceSettings.Current,nav.TrayGroupKey);
            if(nav.Page==PopupPage.TrayRules)return "显示规则";
            if(nav.Page==PopupPage.TrayMembers)return "包含的设备";
            if(nav.Page==PopupPage.ConfirmTrayDelete)return "删除此图标？";
            if(nav.Page==PopupPage.Devices)return "设备管理";
            if(nav.Page==PopupPage.DeviceMissing)return "设备没显示？";
            if(nav.Page==PopupPage.ManageDevice)return "显示与排序";
            if(nav.Page==PopupPage.Device)return "设备详情";
            if(nav.Page==PopupPage.ChoosePreference)return nav.PreferenceAction==210?"外观":nav.PreferenceAction==223?"配色":nav.PreferenceAction==224?"图标样式":nav.PreferenceAction==211?"显示规则":nav.PreferenceAction==213?"提醒阈值":"后台同步间隔";
            switch(nav.Page){case PopupPage.More:return "更多选项";case PopupPage.Audio:return "音源与麦克风";case PopupPage.Preferences:return "显示与提醒";case PopupPage.Mouse:return "鼠标详情";case PopupPage.Headset:return "耳机详情";case PopupPage.Bindings:return "按钮绑定";case PopupPage.PickBinding:return nav.BindingRole=="speaker"?"选择扬声器":nav.BindingRole=="headset"?"选择耳机":"选择麦克风";default:return "状态与诊断";}
        }
        internal static RectangleF VolumeTrack(RectangleF row){return new RectangleF(row.X+17,row.Y+(row.Height<=VisualDesign.SystemVolumeHeight?27:33),row.Width-34,VisualDesign.VolumeTrackHeight);}
        internal static int VolumeAt(RectangleF row,float x){var track=VolumeTrack(row);return Math.Max(0,Math.Min(100,(int)Math.Round((x-track.Left)/track.Width*100)));}
        internal static RectangleF ToggleBounds(RectangleF row){return new RectangleF(row.Right-37,row.Y+(row.Height-VisualDesign.ToggleHeight)/2,VisualDesign.ToggleWidth,VisualDesign.ToggleHeight);}
        internal static void DrawToggle(Graphics g,RectangleF row,bool isChecked,bool enabled,bool dark,bool warning=false) {
            var palette=ThemePalette.Current(dark);var toggle=ToggleBounds(row);
            if(InteractionLayout.Measuring){InteractionLayout.Include(toggle);return;}
            CompactRenderer.Fill(g,toggle,!enabled?palette.Track:isChecked?warning&&!VisualDesign.HighContrast?ThemePalette.LevelColor(10,dark):palette.Selected:palette.Track,VisualDesign.ToggleHeight/2);
            var knob=new RectangleF(toggle.X+(isChecked?VisualDesign.ToggleWidth-VisualDesign.ToggleKnobSize-VisualDesign.ToggleInset:VisualDesign.ToggleInset),toggle.Y+VisualDesign.ToggleInset,VisualDesign.ToggleKnobSize,VisualDesign.ToggleKnobSize);
            using(var brush=new SolidBrush(!enabled?palette.Muted:isChecked?palette.SelectedForeground:VisualDesign.HighContrast?palette.Knob:dark?palette.Muted:Color.White))g.FillEllipse(brush,knob);
            if(enabled&&!isChecked&&!dark&&!VisualDesign.HighContrast)using(var pen=new Pen(palette.Muted,1))g.DrawEllipse(pen,knob);
        }
        internal static void Paint(Graphics g,TrayContext owner,PopupNavigation nav,List<PopupControl> controls,bool dark,int hover,int pressed,int focus) {
            Size size=SizeFor(controls);var palette=ThemePalette.Current(dark);var fg=palette.Foreground;var muted=palette.Muted;var blue=palette.Accent;var track=palette.Track;
            g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            VisualDesign.DrawSurface(g,new RectangleF(.5f,.5f,size.Width-1,size.Height-1),dark);
            var card=new RectangleF(.5f,.5f,size.Width-1,size.Height-1);
            var feedbackState=new PanelFeedback {HoverAction=hover,PressedAction=pressed,KeyboardFocus=focus};
            bool backActive=hover==6||pressed==6||focus==6;
            InteractionLayout.Paint(g,card,new RectangleF(8,5,34,34),6,feedbackState,dark,false,delegate{VisualDesign.DrawSymbol(g,"back",new RectangleF(17,14,16,16),VisualDesign.HighContrast&&backActive?palette.SelectedForeground:muted);});
            VisualDesign.DrawLabel(g,Title(nav),new RectangleF(45,9,200,27),13,true,fg,StringAlignment.Near);
            foreach(var row in controls) {
                if(row.Id==6)continue;
                if(row.Section){VisualDesign.DrawLabel(g,row.Text,new RectangleF(row.Rect.X+6,row.Rect.Y,row.Rect.Width-12,row.Rect.Height),11,false,muted,StringAlignment.Near);continue;}
                var rect=row.Rect;bool highlighted=row.Id>=0&&(row.Id==hover||row.Id==focus||row.Id==pressed),themeChoice=!String.IsNullOrEmpty(row.PaletteKey);
                if(!themeChoice&&row.Selected)CompactRenderer.Fill(g,rect,palette.Selected,VisualDesign.SelectionRadius);
                InteractionLayout.Paint(g,card,rect,row.Id,row.Enabled&&!row.Information&&row.Id>=0?feedbackState:null,dark,row.Selected,delegate{
                Color color=row.Enabled?fg:muted;float x=rect.X+6;
                Color detailColor=muted;
                if(row.Enabled&&themeChoice&&row.Selected)color=blue;
                else if(row.Enabled&&(row.Selected||VisualDesign.HighContrast&&highlighted)&&!themeChoice){color=palette.SelectedForeground;detailColor=color;}
                if(!String.IsNullOrEmpty(row.Icon)){
                    bool titleAligned=!String.IsNullOrEmpty(row.Detail)&&!row.Slider&&!row.Toggle&&!row.WrapText&&!themeChoice;
                    float iconY=titleAligned?rect.Y+4:rect.Y+(rect.Height-16)/2;
                    if(row.Icon=="update"){using(var brush=new SolidBrush(owner.UpdateDotColor))g.FillEllipse(brush,x+4,iconY+4,8,8);}
                    else VisualDesign.DrawSymbol(g,row.Icon,new RectangleF(x,iconY,16,16),detailColor);x+=24;
                }
                if(themeChoice) {
                    if(!VisualDesign.HighContrast) {
                        var swatch=ThemePalette.For(row.PaletteKey,dark);float y=rect.Y+(rect.Height-18)/2;
                        using(var shape=VisualDesign.RoundRect(x,y,30,18,6)) {
                            using(var brush=new SolidBrush(swatch.Card))g.FillPath(brush,shape);
                            using(var pen=new Pen(swatch.Border,1))g.DrawPath(pen,shape);
                        }
                        using(var brush=new SolidBrush(swatch.Accent))g.FillEllipse(brush,x+18,y+5,8,8);
                        InteractionLayout.Include(new RectangleF(x-.5f,y-.5f,31,19));
                    }
                    x+=38;
                }
                if(row.Slider) {
                    int? actual=owner.Audio==null?null:owner.Audio.OutputVolumePercent;
                    int? value=nav.VolumePreview>=0?(int?)nav.VolumePreview:owner.DesiredVolume??actual;
                    VisualDesign.DrawLabel(g,nav.VolumePreview>=0||owner.DesiredVolume.HasValue?"系统音量 · 待应用":"系统输出音量",new RectangleF(x,rect.Y+1,175,23),11,false,color,StringAlignment.Near);
                    VisualDesign.DrawLabel(g,value.HasValue?value+"%":"--",new RectangleF(rect.Right-48,rect.Y+1,40,23),11,false,detailColor,StringAlignment.Far);
                    var meter=VolumeTrack(rect);CompactRenderer.DrawVolumeTrack(g,meter,dark,value.GetValueOrDefault()/100f,0,row.Enabled?blue:muted);
                    float fill=value.HasValue?meter.Width*value.Value/100f:0;
                    if(value.HasValue) {
                        float diameter=row.Id==hover||row.Id==pressed?14:12;
                        var knob=new RectangleF(meter.X+fill-diameter/2,meter.Y+meter.Height/2-diameter/2,diameter,diameter);
                        var ink=knob;ink.Inflate(.5f,.5f);InteractionLayout.Include(ink);
                        using(var brush=new SolidBrush(palette.Knob))g.FillEllipse(brush,knob);
                        using(var pen=new Pen(blue,1))g.DrawEllipse(pen,knob);
                    }
                }else if(row.Toggle) {
                    VisualDesign.DrawLabel(g,row.Text,new RectangleF(x,rect.Y,rect.Width-51,rect.Height),11.5f,false,color,StringAlignment.Near);
                    DrawToggle(g,rect,row.Checked,row.Enabled,dark,row.Id==102);
                }else if(row.WrapText) {
                    InteractionLayout.Include(InteractionLayout.LabelBounds(g,WrappedText(row),new RectangleF(x,rect.Y+6,rect.Right-x-23,rect.Height-12),11.5f,false,StringAlignment.Near,StringTrimming.None,true));
                    using(var font=new Font("Microsoft YaHei UI",11.5f,FontStyle.Regular,GraphicsUnit.Pixel))using(var brush=new SolidBrush(color))using(var format=new StringFormat {LineAlignment=StringAlignment.Center})g.DrawString(WrappedText(row),font,brush,new RectangleF(x,rect.Y+6,rect.Right-x-23,rect.Height-12),format);
                }else {
                    float textWidth=String.IsNullOrEmpty(row.Value)?rect.Right-x-23:Math.Min(105,rect.Right-x-90);
                    VisualDesign.DrawLabel(g,row.Text,new RectangleF(x,rect.Y+(String.IsNullOrEmpty(row.Detail)?0:1),textWidth,String.IsNullOrEmpty(row.Detail)?rect.Height:22),11.5f,false,row.Id==204&&!(VisualDesign.HighContrast&&highlighted)?ThemePalette.LevelColor(10,dark):color,StringAlignment.Near);
                    if(!String.IsNullOrEmpty(row.Detail)){var lines=row.Detail.Split('\n');for(int i=0;i<lines.Length;i++)VisualDesign.DrawLabel(g,lines[i],new RectangleF(x,rect.Y+22+18*i,rect.Right-x-(row.Id==217?12:24),18),11,false,detailColor,StringAlignment.Near);}
                    if(!String.IsNullOrEmpty(row.Value))VisualDesign.DrawLabel(g,row.Value,new RectangleF(x+textWidth,rect.Y,rect.Right-x-textWidth-19,rect.Height),11,false,detailColor,StringAlignment.Far);
                    if(row.Selected)VisualDesign.DrawSymbol(g,"check",new RectangleF(rect.Right-22,rect.Y+(rect.Height-14)/2,14,14),themeChoice?blue:palette.SelectedForeground);
                    else if(row.Id==2||row.Id==3||row.Id>=200&&row.Id<=203||row.Id==206||row.Id==207||row.Id==210||row.Id==211||row.Id==213||row.Id==216||row.Id>=220&&row.Id<=224||row.Id>=230&&row.Id<=232||row.Id==7||row.Id==8||row.Id>=1000&&row.ActionKey!="tray:member"&&row.ActionKey!="tray:group-pin"||row.ActionKey=="tray:group"||row.ActionKey=="tray:rename"||row.ActionKey=="tray:rules"||row.ActionKey=="tray:members")VisualDesign.DrawSymbol(g,row.Id==7?"back":"chevron",new RectangleF(rect.Right-18,rect.Y+(rect.Height-12)/2,12,12),detailColor);
                }
                });
            }
            string notice=nav.Notice;
            var feedback=owner.GetFeedback();bool failure=false;
            if(String.IsNullOrEmpty(notice)&&nav.Page==PopupPage.Audio&&!String.IsNullOrEmpty(feedback.AudioFailure)){notice=feedback.AudioFailure;failure=true;}
            if(String.IsNullOrEmpty(notice)&&nav.Page==PopupPage.Audio&&feedback.ShowPendingHints&&(!String.IsNullOrEmpty(feedback.PendingOutput)||feedback.MicrophonePending))notice="等待Windows确认…";
            if(String.IsNullOrEmpty(notice))notice=nav.Page==PopupPage.Bindings||nav.Page==PopupPage.PickBinding?"绑定只改变按钮目标，不切换当前音源":nav.Page==PopupPage.Audio?"静音状态来自Windows麦克风端点":"";
            VisualDesign.DrawLabel(g,notice,new RectangleF(13,size.Height-25,234,20),11,false,failure?ThemePalette.LevelColor(10,dark):muted,StringAlignment.Near);
        }
    }
}
