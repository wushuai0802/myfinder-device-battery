using System;
using System.Drawing;
using System.Windows.Forms;
using AtkBattery;

namespace AtkBatteryTray {
    public sealed class SettingsForm:Form {
        sealed class Choice {
            public string Id,Name;
            public override string ToString(){return Name;}
        }
        readonly TrayContext owner;
        readonly ComboBox speaker=Picker(),headset=Picker(),microphone=Picker(),traySource=Picker();
        readonly Label actual=new Label {AutoSize=true,MaximumSize=new Size(420,0),Margin=new Padding(0,4,0,12)};
        readonly Label status=new Label {AutoSize=true,MaximumSize=new Size(420,0),Margin=new Padding(0,8,0,8)};
        readonly CheckBox notify=new CheckBox {Text="低电量提醒（20%及以下，每轮放电一次）",AutoSize=true};
        readonly Button refreshDevices=new Button {Text="刷新设备",AutoSize=true};
        AudioBindings settings;
        bool pendingList;
        public SettingsForm(TrayContext context) {
            owner=context;Text="设备电量 · 设置";ShowInTaskbar=false;StartPosition=FormStartPosition.CenterScreen;
            AutoScaleMode=AutoScaleMode.Dpi;AutoScaleDimensions=new SizeF(96,96);
            Font=new Font("Microsoft YaHei UI",9f);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
            ClientSize=new Size(484,480);Padding=new Padding(20);AutoSize=true;AutoSizeMode=AutoSizeMode.GrowAndShrink;MinimumSize=new Size(500,490);
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=9,AutoSize=true};
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,94));layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            Controls.Add(layout);
            AddRow(layout,0,"扬声器按钮",speaker);AddRow(layout,1,"耳机按钮",headset);AddRow(layout,2,"麦克风按钮",microphone);
            layout.Controls.Add(actual,0,3);layout.SetColumnSpan(actual,2);
            AddRow(layout,4,"托盘电量",traySource);
            traySource.Items.AddRange(new object[]{"鼠标（默认）","耳机","两者中较低的电量"});
            layout.Controls.Add(notify,0,5);layout.SetColumnSpan(notify,2);
            var help=new Label {Text="保存只改变按钮绑定，不会切换音源或改变静音。\n全屏、演示或系统不允许通知时延后提醒。",AutoSize=true,ForeColor=SystemColors.GrayText,Margin=new Padding(0,8,0,4)};
            layout.Controls.Add(help,0,6);layout.SetColumnSpan(help,2);
            layout.Controls.Add(status,0,7);layout.SetColumnSpan(status,2);
            var actions=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false};
            var save=new Button {Text="保存",AutoSize=true};var close=new Button {Text="关闭",AutoSize=true,DialogResult=DialogResult.Cancel};
            var reset=new Button {Text="重置音源",AutoSize=true};
            actions.Controls.Add(save);actions.Controls.Add(close);actions.Controls.Add(reset);actions.Controls.Add(refreshDevices);
            layout.Controls.Add(actions,0,8);layout.SetColumnSpan(actions,2);AcceptButton=save;CancelButton=close;
            save.Click+=delegate {Save();};close.Click+=delegate {Close();};
            reset.Click+=delegate {speaker.SelectedIndex=0;headset.SelectedIndex=0;microphone.SelectedIndex=0;status.Text="默认音源识别将在保存后生效。";};
            refreshDevices.Click+=delegate {refreshDevices.Enabled=false;status.Text="正在读取音频设备…";owner.RefreshAudioDevices();};
            foreach(var picker in new[]{speaker,headset,microphone})picker.DropDownClosed+=delegate {if(pendingList)AudioUpdated();};
            try {settings=DeviceSettings.Current;traySource.SelectedIndex=settings.TraySource=="headset"?1:settings.TraySource=="lowest"?2:0;notify.Checked=settings.LowBatteryNotifications;Populate(false);}
            catch(Exception error){settings=new AudioBindings();traySource.SelectedIndex=0;status.Text="设置读取失败："+error.Message;}
        }
        static ComboBox Picker(){return new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Top,Margin=new Padding(0,4,0,10)};}
        static void AddRow(TableLayoutPanel layout,int row,string text,Control control){layout.Controls.Add(new Label {Text=text,AutoSize=true,Margin=new Padding(0,8,8,8)},0,row);layout.Controls.Add(control,1,row);}
        static string SelectedId(ComboBox picker){var choice=picker.SelectedItem as Choice;return choice==null?null:choice.Id;}
        void Fill(ComboBox picker,string id,int flow) {
            picker.Items.Clear();picker.Items.Add(new Choice {Name="自动识别（默认）"});int selected=0;
            if(owner.Audio!=null&&owner.Audio.Devices!=null)foreach(var device in owner.Audio.Devices) {
                if(device.Flow!=flow)continue;
                picker.Items.Add(new Choice {Id=device.Id,Name=device.Name});
                if(String.Equals(device.Id,id,StringComparison.OrdinalIgnoreCase))selected=picker.Items.Count-1;
            }
            if(id!=null&&selected==0){picker.Items.Add(new Choice {Id=id,Name="已绑定设备未连接（保留）"});selected=picker.Items.Count-1;}
            picker.SelectedIndex=selected;
        }
        public void Populate(bool preserveSelection) {
            string a=preserveSelection?SelectedId(speaker):settings.SpeakerEndpointId;
            string b=preserveSelection?SelectedId(headset):settings.HeadsetEndpointId;
            string c=preserveSelection?SelectedId(microphone):settings.MicrophoneEndpointId;
            Fill(speaker,a,0);Fill(headset,b,0);Fill(microphone,c,1);
            AudioReading audio=owner.Audio;
            actual.Text="当前系统输出："+UiState.EndpointName(audio,audio==null?null:audio.ConsoleOutputId)+
                (audio!=null&&audio.OutputRolesDiffer?"\n媒体输出："+UiState.EndpointName(audio,audio.MultimediaOutputId):"");
            refreshDevices.Enabled=true;
            status.ForeColor=audio!=null&&!String.IsNullOrEmpty(audio.Error)?Color.Firebrick:SystemColors.ControlText;
            status.Text=audio!=null&&!String.IsNullOrEmpty(audio.Error)?"读取失败："+audio.Error:"绑定设备与当前输出可以不同；点击卡片按钮才切换。";
        }
        public void AudioUpdated() {
            if(speaker.DroppedDown||headset.DroppedDown||microphone.DroppedDown){pendingList=true;return;}
            pendingList=false;Populate(true);
        }
        void Save() {
            try {
                var next=settings.Clone();next.SetEndpoint("speaker",SelectedId(speaker));next.SetEndpoint("headset",SelectedId(headset));next.SetEndpoint("microphone",SelectedId(microphone));
                next.TraySource=traySource.SelectedIndex==1?"headset":traySource.SelectedIndex==2?"lowest":"mouse";next.LowBatteryNotifications=notify.Checked;
                DeviceSettings.SaveConfiguration(next);settings=next;owner.ConfigurationChanged();Close();
            } catch(Exception error){status.ForeColor=Color.Firebrick;status.Text="保存失败，未切换音源："+error.Message;}
        }
    }
}
