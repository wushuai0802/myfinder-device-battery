using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using AtkBattery;

namespace AtkBatteryTray {
    internal enum PopupPage { Main, More, Audio, Preferences, Mouse, Headset, Diagnostics, Bindings, PickBinding, ChoosePreference, Devices, Device, ManageDevice }
    internal sealed class PopupControl {
        public int Id;
        public int DpiValue;
        public RectangleF Rect;
        public string Text,Value,Detail,Icon,EndpointId,DeviceKey,ActionKey,TargetToken;
        public bool Enabled=true,Selected,Toggle,Checked,Information,Slider,Busy;
        public string StableKey {get {
            string action=ActionKey;
            if(DpiValue>0)action="wireless-dpi:"+DpiValue+":"+(TargetToken??"");
            if(String.IsNullOrEmpty(action))action=Id>=2000&&Id<3000?"refresh":Id>=1000&&Id<2000?"details":Id>=300&&Id<400?"binding:"+(EndpointId??"auto"):"action:"+Id;
            return (DeviceKey??"global")+"|"+action;
        }}
    }
    internal sealed class PopupNavigation {
        internal PopupPage Page=PopupPage.Main;
        internal string BindingRole="speaker",Notice="";
        internal int Offset,VolumePreview=-1,VisibleChoices=7;
        internal int PreferenceAction=210;
        internal int DeviceScroll;
        internal string SelectedDeviceKey,SelectedDeviceName,ReturnedFocusKey;
        internal string AudioDeviceKey,AudioDeviceName;
        readonly Stack<ReturnPoint> history=new Stack<ReturnPoint>();
        sealed class ReturnPoint {internal PopupPage Page;internal int Focus,Offset;internal string FocusKey,AudioDeviceKey,AudioDeviceName;}
        internal void Reset(){history.Clear();Page=PopupPage.Main;Offset=0;VolumePreview=-1;Notice="";DeviceScroll=0;ReturnedFocusKey=null;AudioDeviceKey=null;AudioDeviceName=null;}
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
        internal static Color Foreground(bool dark){return VisualDesign.HighContrast?System.Drawing.SystemColors.WindowText:dark?Color.FromArgb(243,245,248):Color.FromArgb(36,41,50);}
        internal static Color Muted(bool dark){return VisualDesign.HighContrast?System.Drawing.SystemColors.GrayText:dark?Color.FromArgb(167,177,193):Color.FromArgb(100,110,125);}
        internal static Color Track(bool dark){return VisualDesign.HighContrast?System.Drawing.SystemColors.Control:dark?Color.FromArgb(66,72,82):Color.FromArgb(231,235,241);}
        internal static Color Blue(bool dark){return VisualDesign.HighContrast?System.Drawing.SystemColors.Highlight:dark?Color.FromArgb(50,139,250):Color.FromArgb(8,101,214);}
        internal static Color SelectedForeground {get{return VisualDesign.HighContrast?System.Drawing.SystemColors.HighlightText:Color.White;}}
        internal static bool Reduced {get{return DeviceSettings.Current.ReducedMotion||!System.Windows.Forms.SystemInformation.IsMenuAnimationEnabled;}}
        static PopupControl Row(int id,string text,string value=null,string icon=null){return new PopupControl {Id=id,Text=text,Value=value,Icon=icon};}
        static PopupControl Info(string text,string value){var row=Row(-1,text,value);row.Information=true;row.ActionKey="info:"+text;return row;}
        static PopupControl Toggle(int id,string text,bool value){var row=Row(id,text);row.Toggle=true;row.Checked=value;return row;}
        static string OutputName(AudioReading audio){return UiState.CurrentOutputName(audio);}
        internal static List<PopupControl> Build(TrayContext owner,PopupNavigation nav,bool headset,WirelessLayout wireless=null) {
            var rows=new List<PopupControl>();var pref=DeviceSettings.Current;var audio=nav.AudioDeviceKey!=null&&!owner.AudioControlsAvailable?null:owner.Audio;
            var audioPref=DeviceSettings.ForAudioDevice(nav.AudioDeviceKey);
            if(nav.Page==PopupPage.Main&&owner.UseWirelessLayout)return WithIdentity(BuildWireless(owner,wireless??WirelessLayout.Build(owner,nav.DeviceScroll,520),nav),nav,owner);
            if(nav.Page==PopupPage.Main) {
                rows.Add(new PopupControl {Id=2,Rect=VisualDesign.MouseNameRect,Text=pref.MouseAlias});
                rows.Add(new PopupControl {Id=4,Rect=VisualDesign.MouseMeterRect,Text=UiState.Mouse(owner.Current,owner.LastGood,DateTime.UtcNow).Tooltip+"，点击刷新"});
                if(owner.Dpi!=null&&owner.Dpi.Stages!=null)for(int i=0;i<owner.Dpi.Stages.Count;i++) {
                    var hit=VisualDesign.StageRect(i,owner.Dpi.Stages.Count);hit.Inflate(0,owner.Dpi.Stages.Count>4?1:2);
                    rows.Add(new PopupControl {Id=10+i,Rect=hit,Text=owner.Dpi.Stages[i].XDpi+" DPI"});
                }
                if(headset) {
                    rows.Add(new PopupControl {Id=3,Rect=VisualDesign.HeadsetNameRectFor(owner.Dpi),Text=pref.HeadsetAlias});
                    rows.Add(new PopupControl {Id=5,Rect=VisualDesign.HeadsetMeterRectFor(owner.Dpi),Text=UiState.Headset(owner.Headset,DateTime.UtcNow).Tooltip+"，点击刷新"});
                    rows.Add(new PopupControl {Id=100,Rect=VisualDesign.SpeakerOutputRectFor(owner.Dpi),Text="输出到："+(audio==null?"未连接":audio.SpeakerName),Enabled=audio!=null&&audio.SpeakerAvailable});
                    rows.Add(new PopupControl {Id=101,Rect=VisualDesign.HeadsetOutputRectFor(owner.Dpi),Text="输出到："+(audio==null?"未连接":audio.HeadsetName),Enabled=audio!=null&&audio.HeadsetAvailable});
                    rows.Add(new PopupControl {Id=102,Rect=VisualDesign.MicrophoneMuteRectFor(owner.Dpi),Text="Windows麦克风："+(audio==null?"未连接":audio.MicrophoneName),Enabled=audio!=null&&audio.MicrophoneAvailable&&audio.MicrophoneMuted.HasValue});
                    foreach(var control in rows)if(control.Id>=100&&control.Id<=102){var hit=control.Rect;hit.Inflate(0,2);control.Rect=hit;}
                }
                // Keep the first keyboard target compatible with the previous release.
                var feedback=owner.GetFeedback();
                rows.Insert(0,new PopupControl {Id=0,Rect=VisualDesign.RefreshRectFor(headset,owner.Dpi),Text=feedback.DpiFailure??feedback.AudioFailure??"立即同步全部设备"});
                rows.Insert(1,new PopupControl {Id=1,Rect=VisualDesign.MoreRectFor(headset,owner.Dpi),Text="更多选项"});
                rows.Add(MainVolume(owner,nav,VisualDesign.SystemVolumeRectFor(headset,owner.Dpi)));
                return WithIdentity(rows,nav,owner);
            }
            if(nav.Page==PopupPage.More) {
                rows.Add(Row(0,"立即同步全部",null,"refresh"));rows.Add(Row(200,"音源与麦克风",null,"speaker"));
                rows.Add(Row(201,"显示与提醒",null,"settings"));if(owner.NativeMousePresent)rows.Add(Row(2,"鼠标详情",null,"mouse"));
                if(!owner.UseWirelessLayout||owner.Headset!=null&&owner.Headset.ReceiverPresent)rows.Add(Row(3,"耳机详情",null,"headset"));rows.Add(Row(206,"设备管理",null,"keyboard"));rows.Add(Row(202,"状态与诊断",null,"info"));rows.Add(Row(204,"退出",null,"power"));
            }else if(nav.Page==PopupPage.Audio) {
                if(nav.AudioDeviceKey!=null)rows.Add(Info("控制对象",nav.AudioDeviceName??"此耳机"));
                rows.Add(Info("当前系统输出",OutputName(audio)));
                var a=Row(100,"扬声器",null,"speaker");a.Detail=audio==null?"未检测到":audio.SpeakerName;a.Enabled=audio!=null&&audio.SpeakerAvailable;a.Selected=audio!=null&&audio.ActiveOutput=="speaker";rows.Add(a);
                a=Row(101,"耳机",null,"headset");a.Detail=audio==null?"未检测到":audio.HeadsetName;a.Enabled=audio!=null&&audio.HeadsetAvailable;a.Selected=audio!=null&&audio.ActiveOutput=="headset";rows.Add(a);
                a=Row(240,"系统输出音量");a.Slider=true;a.Enabled=audio!=null&&audio.OutputVolumePercent.HasValue&&!String.IsNullOrEmpty(audio.VolumeEndpointId);a.Value=a.Enabled?(nav.VolumePreview>=0?nav.VolumePreview:owner.DesiredVolume??audio.OutputVolumePercent.Value)+"%":"不可用";rows.Add(a);
                a=Toggle(102,"Windows麦克风静音",audio!=null&&audio.MicrophoneMuted==true);a.Enabled=audio!=null&&audio.MicrophoneAvailable&&audio.MicrophoneMuted.HasValue;rows.Add(a);
                rows.Add(Row(203,"更换按钮绑定",null,"settings"));
            }else if(nav.Page==PopupPage.Preferences) {
                rows.Add(Row(210,"外观",pref.Theme=="auto"?"跟随系统":pref.Theme=="dark"?"深色":"浅色"));
                rows.Add(Row(211,"托盘显示",!String.IsNullOrEmpty(pref.TrayDeviceId)?"固定设备":pref.TraySource=="auto"?"自动":pref.TraySource=="mouse"?"鼠标优先":pref.TraySource=="headset"?"耳机优先":pref.TraySource=="keyboard"?"键盘优先":"最低电量"));
                rows.Add(Toggle(212,"低电量提醒",pref.LowBatteryNotifications));rows.Add(Row(213,"提醒阈值",pref.WarningThreshold+"%"));
                rows.Add(Toggle(214,"跨重启保留读数",pref.RememberReadings));rows.Add(Toggle(215,"减弱动效",pref.ReducedMotion));
                rows.Add(Row(216,"后台同步间隔",pref.BackgroundSeconds==60?"1分钟":pref.BackgroundSeconds==120?"2分钟":"5分钟"));
                rows.Add(Row(217,"清除历史读数",null,"refresh"));
            }else if(nav.Page==PopupPage.ChoosePreference) {
                string[] labels;int first,selected;
                if(nav.PreferenceAction==210){labels=new[]{"跟随系统","浅色","深色"};first=400;selected=pref.Theme=="auto"?0:pref.Theme=="light"?1:2;}
                else if(nav.PreferenceAction==211){labels=new[]{"自动选择有数据的设备","鼠标优先","耳机优先","键盘优先","最低电量"};first=410;selected=pref.TraySource=="auto"?0:pref.TraySource=="mouse"?1:pref.TraySource=="headset"?2:pref.TraySource=="keyboard"?3:4;}
                else if(nav.PreferenceAction==213){labels=new[]{"10%","15%","20%"};first=420;selected=pref.WarningThreshold==10?0:pref.WarningThreshold==15?1:2;}
                else {labels=new[]{"1分钟","2分钟","5分钟"};first=430;selected=pref.BackgroundSeconds==60?0:pref.BackgroundSeconds==120?1:2;}
                for(int i=0;i<labels.Length;i++){var choice=Row(first+i,labels[i]);choice.Selected=i==selected&&(nav.PreferenceAction!=211||String.IsNullOrEmpty(pref.TrayDeviceId));rows.Add(choice);}
                if(nav.PreferenceAction==211&&!String.IsNullOrEmpty(pref.TrayDeviceId))rows.Add(Info("当前固定设备","在设备详情中解除"));
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
                rows.Add(Info("耳机无线",headset?"已确认连接":"未确认连接"));rows.Add(Info("后台同步",pref.BackgroundSeconds+"秒"));
                rows.Add(Info("配置状态",DeviceSettings.UsingDefaults?"读取失败，使用默认":"正常"));
                rows.Add(Info("诊断写入",owner.StorageHealthText));
                rows.Add(Info("开机启动",StartupState()));rows.Add(Info("通用设备",owner.GenericReady?owner.WirelessDevices.Length+"个":owner.GenericScanning?"正在识别":"尚未就绪"));
                if(!String.IsNullOrEmpty(owner.GenericHealthSummary))rows.Add(Info("读取器健康",owner.GenericHealthSummary));
                else if(!String.IsNullOrEmpty(owner.GenericError))rows.Add(Info("读取器状态","暂不可用，详情见诊断摘要"));
                rows.Add(Row(250,"复制诊断摘要",null,"info"));
            }else if(nav.Page==PopupPage.Devices) {
                var devices=WirelessLayout.Build(owner,0,100000,true).All;
                nav.Offset=Math.Max(0,Math.Min(nav.Offset,Math.Max(0,devices.Count-nav.VisibleChoices)));
                int count=Math.Min(nav.VisibleChoices,devices.Count-nav.Offset);for(int i=0;i<count;i++){var device=devices[nav.Offset+i];var row=Row(1000+nav.Offset+i,device.Name,device.Hidden?"已隐藏":"显示中",device.Kind);row.DeviceKey=device.Id;row.ActionKey="manage";row.Enabled=device.Id!="empty";rows.Add(row);}
                if(nav.Offset>0)rows.Add(Row(7,"上一页"));if(nav.Offset+count<devices.Count)rows.Add(Row(8,"下一页"));
                rows.Add(Row(0,"重新识别设备",null,"refresh"));
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
                if(rows.Count>visible) {
                    int total=rows.Count;nav.Offset=Math.Max(0,Math.Min(nav.Offset,total-visible));
                    rows=rows.GetRange(nav.Offset,Math.Min(visible,total-nav.Offset));
                    if(nav.Offset>0)rows.Add(Row(7,"上一页"));if(nav.Offset+visible<total)rows.Add(Row(8,"下一页"));
                }else nav.Offset=0;
            }
            float y=44;
            foreach(var row in rows){float height=row.Slider?54:!String.IsNullOrEmpty(row.Detail)?44:34;row.Rect=new RectangleF(10,y,240,height);y+=height+2;}
            rows.Insert(0,new PopupControl {Id=6,Text="返回",Rect=new RectangleF(8,5,34,34)});
            return WithIdentity(rows,nav,owner);
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
        static List<PopupControl> BuildWireless(TrayContext owner,WirelessLayout layout,PopupNavigation nav) {
            var rows=new List<PopupControl>();
            rows.Add(new PopupControl {Id=0,Text="立即同步全部设备",Rect=new RectangleF(8,layout.Footer+1,layout.Paged?146:208,28)});
            rows.Add(new PopupControl {Id=1,Text="更多选项",Rect=new RectangleF(226,layout.Footer+1,28,28)});
            foreach(var card in layout.Cards) {
                if(card.NativeMouse) {
                    var name=VisualDesign.MouseNameRect;name.Y+=card.Top;var meter=VisualDesign.MouseMeterRect;meter.Y+=card.Top;
                    rows.Add(new PopupControl {Id=2,Text=card.Name,Rect=name,DeviceKey=card.Id});rows.Add(new PopupControl {Id=4,Text=card.Battery.Tooltip+"，点击刷新",Rect=meter,DeviceKey=card.Id});
                    if(owner.Dpi!=null)for(int i=0;i<owner.Dpi.Stages.Count;i++){var hit=VisualDesign.StageRect(i,owner.Dpi.Stages.Count);hit.Y+=card.Top;hit.Inflate(0,owner.Dpi.Stages.Count>4?1:2);rows.Add(new PopupControl {Id=10+i,Text=owner.Dpi.Stages[i].XDpi+" DPI",Rect=hit,DeviceKey=card.Id});}
                }else if(card.NativeHeadset) {
                    float delta=card.Top-VisualDesign.MouseCardHeight(owner.Dpi)-8;
                    var name=VisualDesign.HeadsetNameRectFor(owner.Dpi);name.Y+=delta;var meter=VisualDesign.HeadsetMeterRectFor(owner.Dpi);meter.Y+=delta;
                    rows.Add(new PopupControl {Id=3,Text=card.Name,Rect=name,DeviceKey=card.Id});rows.Add(new PopupControl {Id=5,Text=card.Battery.Tooltip+"，点击刷新",Rect=meter,DeviceKey=card.Id});
                    foreach(int id in new[]{100,101,102}) {
                        var hit=id==100?VisualDesign.SpeakerOutputRectFor(owner.Dpi):id==101?VisualDesign.HeadsetOutputRectFor(owner.Dpi):VisualDesign.MicrophoneMuteRectFor(owner.Dpi);hit.Y+=delta;hit.Inflate(0,2);
                        var audio=owner.Audio;bool enabled=audio!=null&&(id==100?audio.SpeakerAvailable:id==101?audio.HeadsetAvailable:audio.MicrophoneAvailable&&audio.MicrophoneMuted.HasValue);
                        rows.Add(new PopupControl {Id=id,Text=id==100?"输出到扬声器":id==101?"输出到耳机":"Windows麦克风静音",Rect=hit,Enabled=enabled,DeviceKey=card.Id});
                    }
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
            if(layout.HasVolume)rows.Add(MainVolume(owner,nav,layout.VolumeRect));
            return rows;
        }
        internal static Size SizeFor(List<PopupControl> controls){float bottom=44;foreach(var c in controls)bottom=Math.Max(bottom,c.Rect.Bottom);return new Size(260,(int)bottom+28);}
        internal static PopupControl Hit(List<PopupControl> controls,PointF point){foreach(var c in controls)if(c.Id>=0&&c.Enabled&&!c.Information&&c.Rect.Contains(point))return c;return null;}
        internal static string StartupState() {
            return StartupDiagnostics.Describe();
        }
        internal static string Title(PopupNavigation nav) {
            if(nav.Page==PopupPage.Devices)return "设备管理";
            if(nav.Page==PopupPage.ManageDevice)return "显示与排序";
            if(nav.Page==PopupPage.Device)return "设备详情";
            if(nav.Page==PopupPage.ChoosePreference)return nav.PreferenceAction==210?"外观":nav.PreferenceAction==211?"托盘显示":nav.PreferenceAction==213?"提醒阈值":"后台同步间隔";
            switch(nav.Page){case PopupPage.More:return "更多选项";case PopupPage.Audio:return "音源与麦克风";case PopupPage.Preferences:return "显示与提醒";case PopupPage.Mouse:return "鼠标详情";case PopupPage.Headset:return "耳机详情";case PopupPage.Bindings:return "按钮绑定";case PopupPage.PickBinding:return nav.BindingRole=="speaker"?"选择扬声器":nav.BindingRole=="headset"?"选择耳机":"选择麦克风";default:return "状态与诊断";}
        }
        internal static RectangleF VolumeTrack(RectangleF row){return new RectangleF(row.X+9,row.Y+33,row.Width-18,6);}
        internal static int VolumeAt(RectangleF row,float x){var track=VolumeTrack(row);return Math.Max(0,Math.Min(100,(int)Math.Round((x-track.Left)/track.Width*100)));}
        internal static void Paint(Graphics g,TrayContext owner,PopupNavigation nav,List<PopupControl> controls,bool dark,int hover,int pressed,int focus) {
            Size size=SizeFor(controls);var fg=Foreground(dark);var muted=Muted(dark);var blue=Blue(dark);var track=Track(dark);
            g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            VisualDesign.DrawSurface(g,new RectangleF(.5f,.5f,size.Width-1,size.Height-1),dark);
            if(hover==6||pressed==6||focus==6)using(var shape=VisualDesign.RoundRect(8,5,34,34,7))using(var brush=new SolidBrush(Color.FromArgb(dark?18:10,fg)))g.FillPath(brush,shape);
            if(focus==6)using(var shape=VisualDesign.RoundRect(8.5f,5.5f,33,33,7))using(var pen=new Pen(blue,1))g.DrawPath(pen,shape);
            VisualDesign.DrawSymbol(g,"back",new RectangleF(17,14,16,16),muted);
            VisualDesign.DrawLabel(g,Title(nav),new RectangleF(45,9,200,27),13,true,fg,StringAlignment.Near);
            foreach(var row in controls) {
                if(row.Id==6)continue;
                var rect=row.Rect;bool highlighted=row.Id>=0&&(row.Id==hover||row.Id==focus||row.Id==pressed);
                if(highlighted||row.Selected)using(var shape=VisualDesign.RoundRect(rect.X,rect.Y,rect.Width,rect.Height,8))using(var brush=new SolidBrush(VisualDesign.HighContrast?SystemColors.Highlight:row.Selected?Color.FromArgb(dark?35:21,blue):Color.FromArgb(dark?18:10,fg)))g.FillPath(brush,shape);
                Color color=row.Enabled?fg:muted;float x=rect.X+6;
                Color detailColor=muted;
                if(VisualDesign.HighContrast&&row.Enabled&&(highlighted||row.Selected)){color=SystemColors.HighlightText;detailColor=color;}
                if(!String.IsNullOrEmpty(row.Icon)){VisualDesign.DrawSymbol(g,row.Icon,new RectangleF(x,rect.Y+(rect.Height-16)/2,16,16),detailColor);x+=24;}
                if(row.Slider) {
                    int? actual=owner.Audio==null?null:owner.Audio.OutputVolumePercent;
                    int? value=nav.VolumePreview>=0?(int?)nav.VolumePreview:owner.DesiredVolume??actual;
                    VisualDesign.DrawLabel(g,nav.VolumePreview>=0||owner.DesiredVolume.HasValue?"系统音量 · 待应用":"系统输出音量",new RectangleF(x,rect.Y+1,175,23),11,false,color,StringAlignment.Near);
                    VisualDesign.DrawLabel(g,value.HasValue?value+"%":"--",new RectangleF(rect.Right-48,rect.Y+1,40,23),11,false,detailColor,StringAlignment.Far);
                    var meter=VolumeTrack(rect);using(var shape=VisualDesign.RoundRect(meter.X,meter.Y,meter.Width,meter.Height,3))using(var brush=new SolidBrush(track))g.FillPath(brush,shape);
                    float fill=value.HasValue?meter.Width*value.Value/100f:0;
                    if(fill>0)using(var shape=VisualDesign.RoundRect(meter.X,meter.Y,fill,meter.Height,Math.Min(3,fill/2)))using(var brush=new SolidBrush(row.Enabled?blue:muted))g.FillPath(brush,shape);
                    if(value.HasValue)using(var brush=new SolidBrush(dark?Color.FromArgb(235,240,247):Color.White))g.FillEllipse(brush,meter.X+fill-6,meter.Y-3,12,12);
                }else if(row.Toggle) {
                    VisualDesign.DrawLabel(g,row.Text,new RectangleF(x,rect.Y,rect.Width-51,rect.Height),11.5f,false,color,StringAlignment.Near);
                    var toggle=new RectangleF(rect.Right-37,rect.Y+(rect.Height-18)/2,30,18);
                    using(var shape=VisualDesign.RoundRect(toggle.X,toggle.Y,toggle.Width,toggle.Height,9))using(var brush=new SolidBrush(!row.Enabled?track:row.Checked?row.Id==102?TrayGlyph.LevelColor(10,dark):blue:track))g.FillPath(brush,shape);
                    using(var brush=new SolidBrush(row.Enabled?Color.White:muted))g.FillEllipse(brush,toggle.X+(row.Checked?14:2),toggle.Y+2,14,14);
                }else {
                    float textWidth=String.IsNullOrEmpty(row.Value)?rect.Right-x-23:Math.Min(105,rect.Right-x-90);
                    VisualDesign.DrawLabel(g,row.Text,new RectangleF(x,rect.Y+(String.IsNullOrEmpty(row.Detail)?0:1),textWidth,String.IsNullOrEmpty(row.Detail)?rect.Height:22),11.5f,false,row.Id==204?TrayGlyph.LevelColor(10,dark):color,StringAlignment.Near);
                    if(!String.IsNullOrEmpty(row.Detail))VisualDesign.DrawLabel(g,row.Detail,new RectangleF(x,rect.Y+22,rect.Right-x-24,18),11,false,detailColor,StringAlignment.Near);
                    if(!String.IsNullOrEmpty(row.Value))VisualDesign.DrawLabel(g,row.Value,new RectangleF(x+textWidth,rect.Y,rect.Right-x-textWidth-19,rect.Height),11,false,detailColor,StringAlignment.Far);
                    if(row.Selected)VisualDesign.DrawSymbol(g,"check",new RectangleF(rect.Right-22,rect.Y+(rect.Height-14)/2,14,14),VisualDesign.HighContrast&&highlighted?SystemColors.HighlightText:blue);
                    else if(row.Id==2||row.Id==3||row.Id>=200&&row.Id<=203||row.Id==206||row.Id==210||row.Id==211||row.Id==213||row.Id==216||row.Id>=220&&row.Id<=222||row.Id>=230&&row.Id<=232||row.Id==7||row.Id==8||row.Id>=1000)VisualDesign.DrawSymbol(g,row.Id==7?"back":"chevron",new RectangleF(rect.Right-18,rect.Y+(rect.Height-12)/2,12,12),detailColor);
                }
                if(row.Id>=0&&row.Id==focus)using(var shape=VisualDesign.RoundRect(rect.X+.5f,rect.Y+.5f,rect.Width-1,rect.Height-1,8))using(var pen=new Pen(blue,1))g.DrawPath(pen,shape);
            }
            string notice=nav.Notice;
            var feedback=owner.GetFeedback();bool failure=false;
            if(String.IsNullOrEmpty(notice)&&nav.Page==PopupPage.Audio&&!String.IsNullOrEmpty(feedback.AudioFailure)){notice=feedback.AudioFailure;failure=true;}
            if(String.IsNullOrEmpty(notice)&&nav.Page==PopupPage.Audio&&feedback.ShowPendingHints&&(!String.IsNullOrEmpty(feedback.PendingOutput)||feedback.MicrophonePending))notice="等待Windows确认…";
            if(String.IsNullOrEmpty(notice))notice=nav.Page==PopupPage.Bindings||nav.Page==PopupPage.PickBinding?"绑定只改变按钮目标，不切换当前音源":nav.Page==PopupPage.Preferences?"关闭保留不删除历史，清除需单独操作":nav.Page==PopupPage.Audio?"静音状态来自Windows麦克风端点":"";
            VisualDesign.DrawLabel(g,notice,new RectangleF(13,size.Height-25,234,20),11,false,failure?TrayGlyph.LevelColor(10,dark):muted,StringAlignment.Near);
        }
    }
}
