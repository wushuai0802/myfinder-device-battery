using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Windows.Forms;
using Microsoft.Win32;
using AtkBattery;

namespace AtkBatteryTray {
    public static class Style {
        static bool? systemDark;
        public static void InvalidateTheme(){systemDark=null;}
        public static bool Dark {
            get {
                var appearance=DeviceSettings.Current.Theme;
                if(appearance=="dark")return true;if(appearance=="light")return false;
                if(!systemDark.HasValue)using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))systemDark=key!=null&&Convert.ToInt32(key.GetValue("SystemUsesLightTheme",1))==0;
                return systemDark.Value;
            }
        }
        public static GraphicsPath Round(RectangleF rect,float radius) {
            var p=new GraphicsPath();float d=radius*2;
            p.AddArc(rect.Left,rect.Top,d,d,180,90);p.AddArc(rect.Right-d,rect.Top,d,d,270,90);
            p.AddArc(rect.Right-d,rect.Bottom-d,d,d,0,90);p.AddArc(rect.Left,rect.Bottom-d,d,d,90,90);p.CloseFigure();return p;
        }
        public static void Text(Graphics g,string s,float size,FontStyle fontStyle,Color color,RectangleF rect,StringAlignment align) {
            using(var f=new Font("Segoe UI",size,fontStyle,GraphicsUnit.Pixel))
            using(var b=new SolidBrush(color))
            using(var sf=new StringFormat {Alignment=align,LineAlignment=StringAlignment.Center,Trimming=StringTrimming.EllipsisCharacter})
                g.DrawString(s,f,b,rect,sf);
        }
        public static void Pill(Graphics g,RectangleF rect,string label,bool selected,bool enabled,Color fg,Color muted,Color card) {
            Color fill=selected?Color.FromArgb(32,fg):card;
            Color text=enabled?fg:muted;
            using(var path=Round(rect,9))
            using(var b=new SolidBrush(fill))g.FillPath(b,path);
            using(var p=new Pen(Color.FromArgb(selected?90:35,text),1))
            using(var path=Round(new RectangleF(rect.X+.5f,rect.Y+.5f,rect.Width-1,rect.Height-1),9))g.DrawPath(p,path);
            Text(g,label,10,selected?FontStyle.Bold:FontStyle.Regular,text,rect,StringAlignment.Center);
        }
        public static void Ring(Graphics g,RectangleF rect,int? percent,bool charge,Color color) {
            using(var track=new Pen(Color.FromArgb(70,color),3.0f))
            using(var value=new Pen(color,3.0f)) {
                track.StartCap=LineCap.Round;track.EndCap=LineCap.Round;
                value.StartCap=LineCap.Round;value.EndCap=LineCap.Round;
                g.DrawArc(track,rect,-90,360);
                if(percent.HasValue && percent.Value>0)g.DrawArc(value,rect,-90,Math.Max(4,percent.Value*3.6f));
            }
            Text(g,percent.HasValue?percent.Value.ToString():"--",rect.Height*.27f,FontStyle.Bold,color,rect,StringAlignment.Center);
            if(charge) {
                using(var p=new Pen(color,1.1f))
                    g.DrawLines(p,new PointF[]{new PointF(rect.X+rect.Width*.58f,rect.Y+rect.Height*.16f),new PointF(rect.X+rect.Width*.42f,rect.Y+rect.Height*.53f),new PointF(rect.X+rect.Width*.61f,rect.Y+rect.Height*.53f),new PointF(rect.X+rect.Width*.45f,rect.Y+rect.Height*.88f)});
            }
        }
        public static Bitmap Tray(int? percent,bool charge,bool dark,bool stale,bool approximate=false) {
            return TrayGlyph.Render(percent,charge,dark,stale,SystemInformation.SmallIconSize.Width,approximate);
        }
    }
    public sealed class Details:Form {
        readonly TrayContext owner;
        readonly System.Windows.Forms.Timer animation=new System.Windows.Forms.Timer();
        Size targetSize=VisualDesign.PanelSize;
        readonly System.Windows.Forms.Timer layoutAnimation=new System.Windows.Forms.Timer();
        readonly Stopwatch layoutClock=new Stopwatch();
        float headsetProgress,layoutStart,layoutTarget;
        HeadsetReading displayHeadset;
        AudioReading displayAudio;
        Point anchor;
        float animationProgress;
        readonly Stopwatch motionClock=new Stopwatch();
        float motionStart,motionTarget,motionDurationMs;
        bool closing;
        bool ignoreDeactivate;
        int hoverStage=-1;
        bool hoverRefresh;
        bool hoverSettings;
        float dpiScale=1f;
        int keyboardFocus=-1;
        string keyboardFocusKey;
        int keyboardFocusIndex;
        int hoverAudio=-1;
        readonly PopupNavigation navigation=new PopupNavigation();
        int hoverAction=-1,pressedAction=-1;
        PopupControl pressedControl;
        PopupPage pressedPage;
        bool draggingVolume;
        string volumeEndpoint;
        string lastHoverTip;
        readonly System.Windows.Forms.Timer feedbackTimer=new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer pageAnimation=new System.Windows.Forms.Timer();
        readonly Stopwatch pageClock=new Stopwatch();
        float pageProgress=1f;
        int pageStartHeight;
        readonly ToolTip controlTip=new ToolTip {InitialDelay=350,ReshowDelay=100,AutoPopDelay=2000,ShowAlways=true};
        byte frameAlpha=255;
        bool layeredRendering=true;
        bool renderPreviewSaved;
        string lastPresentedKey;
        [StructLayout(LayoutKind.Sequential)] struct NativePoint {public int X,Y;public NativePoint(int x,int y){X=x;Y=y;}}
        [StructLayout(LayoutKind.Sequential)] struct NativeSize {public int Width,Height;public NativeSize(int w,int h){Width=w;Height=h;}}
        [StructLayout(LayoutKind.Sequential,Pack=1)] struct NativeBlend {public byte Operation,Flags,Alpha,Format;}
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd,IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll",SetLastError=true)] static extern bool UpdateLayeredWindow(IntPtr hwnd,IntPtr destination,ref NativePoint position,ref NativeSize size,IntPtr source,ref NativePoint origin,int colorKey,ref NativeBlend blend,int flags);
        [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd,int index);
        [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hwnd,int index,IntPtr value);
        public Details(TrayContext context) {
            owner=context;FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;StartPosition=FormStartPosition.Manual;AutoScaleMode=AutoScaleMode.None;
            dpiScale=NativeDisplay.ScaleAt(Cursor.Position);
            Text="设备电量与音频控制";AccessibleName=Text;
            displayHeadset=owner.Headset;displayAudio=owner.Audio;
            headsetProgress=TrayContext.ShowHeadset(displayHeadset,displayAudio)?1f:0f;
            layoutTarget=headsetProgress;
            targetSize=VisualDesign.PanelSizeFor(headsetProgress>0f,owner.Dpi);
            ClientSize=UiState.ScaleSize(targetSize,dpiScale);DoubleBuffered=true;Font=new Font("Segoe UI",9);
            animation.Interval=16;animation.Tick+=delegate {AdvanceAnimation();};
            layoutAnimation.Interval=16;layoutAnimation.Tick+=delegate {AdvanceLayout();};
            feedbackTimer.Interval=285;feedbackTimer.Tick+=delegate {feedbackTimer.Stop();Invalidate();};
            pageAnimation.Interval=16;pageAnimation.Tick+=delegate {
                float t=Math.Min(1f,(float)pageClock.Elapsed.TotalMilliseconds/160f);pageProgress=1f-(float)Math.Pow(1-t,3);
                if(t>=1f){pageProgress=1;pageAnimation.Stop();}UpdateLayoutSize();Invalidate();
            };
            Deactivate+=delegate {if(!ignoreDeactivate&&!closing)CloseAnimated();};
            MouseDown+=delegate(object sender,MouseEventArgs e) {
                if(e.Button!=MouseButtons.Left||closing)return;
                pressedControl=PopupPages.Hit(ControlsForPage(),PanelPoint(e.Location));pressedPage=navigation.Page;
                pressedAction=pressedControl==null?-1:pressedControl.Id;
                if(pressedAction==240&&displayAudio!=null){draggingVolume=true;volumeEndpoint=displayAudio.VolumeEndpointId;navigation.VolumePreview=PopupPages.VolumeAt(pressedControl.Rect,PanelPoint(e.Location).X);Capture=true;}
                Invalidate();
            };
            MouseUp+=delegate(object sender,MouseEventArgs e) {
                if(e.Button!=MouseButtons.Left||closing)return;
                var target=PopupPages.Hit(ControlsForPage(),PanelPoint(e.Location));
                int selected=pressedAction;pressedAction=-1;keyboardFocus=-1;keyboardFocusKey=null;
                if(draggingVolume){draggingVolume=false;Capture=false;int value=navigation.VolumePreview;navigation.VolumePreview=-1;owner.SelectOutputVolume(volumeEndpoint,value);}
                else if(target!=null&&pressedControl!=null&&PopupPages.SameAction(target,pressedControl)&&pressedPage==navigation.Page&&target.Rect==pressedControl.Rect)PerformAction(target);
                pressedControl=null;
                Invalidate();
            };
            MouseMove+=delegate(object sender,MouseEventArgs e) {
                var point=PanelPoint(e.Location);
                if(draggingVolume&&pressedControl!=null){navigation.VolumePreview=PopupPages.VolumeAt(pressedControl.Rect,point.X);Invalidate();return;}
                var controls=ControlsForPage();var target=PopupPages.Hit(controls,point);int next=target==null?-1:target.Id;
                var tipTarget=target??controls.Find(delegate(PopupControl c){return c.Rect.Contains(point);});
                string tip=tipTarget==null?null:tipTarget.Text+(String.IsNullOrEmpty(tipTarget.Value)?"":"："+tipTarget.Value)+(String.IsNullOrEmpty(tipTarget.Detail)?"":"\n"+tipTarget.Detail);
                if(navigation.Page!=PopupPage.Main&&point.Y>targetSize.Height-28){var feedback=owner.GetFeedback();tip=feedback.AudioFailure??feedback.DpiFailure??navigation.Notice;}
                if(next!=hoverAction||tip!=lastHoverTip){
                    hoverAction=next;hoverStage=next>=10&&next<18?next-10:-1;hoverAudio=next>=100&&next<=102?next-100:-1;hoverRefresh=next==0;hoverSettings=next==1;
                    lastHoverTip=tip;controlTip.SetToolTip(this,tip);Invalidate();
                }
                Cursor=target==null?Cursors.Default:Cursors.Hand;
            };
            MouseLeave+=delegate {lastHoverTip=null;hoverAction=-1;hoverStage=-1;hoverRefresh=false;hoverSettings=false;hoverAudio=-1;controlTip.SetToolTip(this,null);Cursor=Cursors.Default;Invalidate();};
            MouseWheel+=delegate(object sender,MouseEventArgs e) {
                if(navigation.Page!=PopupPage.Main){navigation.Offset=Math.Max(0,navigation.Offset+(e.Delta<0?1:-1));UpdateLayoutSize();Invalidate();}
                else if(navigation.Page==PopupPage.Main&&owner.UseWirelessLayout){navigation.DeviceScroll=Math.Max(0,navigation.DeviceScroll+(e.Delta<0?1:-1));UpdateLayoutSize();Invalidate();}
            };
        }
        WirelessLayout MainWireless(){return WirelessLayout.Build(owner,navigation.DeviceScroll,(int)(Screen.FromPoint(anchor).WorkingArea.Height/dpiScale)-24);}
        List<PopupControl> ControlsForPage(){navigation.VisibleChoices=Math.Max(2,Math.Min(7,(int)((Screen.FromPoint(anchor).WorkingArea.Height/dpiScale-88)/36)-2));return PopupPages.Build(owner,navigation,headsetProgress>.98f,owner.UseWirelessLayout?MainWireless():null);}
        void Go(PopupPage page) {
            pageStartHeight=targetSize.Height;string stable=keyboardFocusKey;int focus=keyboardFocus>=0?keyboardFocus:hoverAction;if(stable==null){var hovered=ControlsForPage().Find(delegate(PopupControl c){return c.Id==focus;});if(hovered!=null)stable=hovered.StableKey;}navigation.Go(page,focus,stable);keyboardFocus=-1;keyboardFocusKey=null;StartPageMotion();
        }
        public void ShowPreferences(){Go(PopupPage.Preferences);}
        internal string CurrentPage {get{return navigation.Page.ToString();}}
        void Back() {pageStartHeight=targetSize.Height;keyboardFocus=navigation.Back();keyboardFocusKey=navigation.ReturnedFocusKey;StartPageMotion();ReconcileKeyboardFocus(ControlsForPage());}
        void StartPageMotion(){draggingVolume=false;Capture=false;navigation.VolumePreview=-1;volumeEndpoint=null;pressedControl=null;hoverAction=-1;pressedAction=-1;hoverStage=-1;hoverAudio=-1;hoverRefresh=false;hoverSettings=false;lastHoverTip=null;controlTip.SetToolTip(this,null);pageProgress=PopupPages.Reduced?1:0;pageClock.Restart();if(pageProgress==0)pageAnimation.Start();UpdateLayoutSize();Invalidate();}
        void PerformAction(PopupControl control) {
            int id=control.Id;
            if(id==902||id==903){var layout=MainWireless();navigation.DeviceScroll=id==902?Math.Max(0,layout.Start-layout.Cards.Count):layout.End;UpdateLayoutSize();Invalidate();return;}
            if(id>=1000&&id<3000) {
                if(id>=2000){owner.RefreshWireless(control.DeviceKey);return;}
                if(control.DeviceKey=="native:mouse"){Go(PopupPage.Mouse);return;}if(control.DeviceKey=="native:headset"){Go(PopupPage.Headset);return;}
                navigation.SelectedDeviceKey=control.DeviceKey;navigation.SelectedDeviceName=control.Text;Go(PopupPage.Device);return;
            }
            if(id==0||id==4||id==5){owner.RefreshDevice(id==4?"mouse":id==5?"headset":"all");return;}
            if(id==1){Go(PopupPage.More);return;}if(id==6){Back();return;}
            if(id>=10&&id<18){owner.SelectDpiStage(id-10);SchedulePendingHint();return;}
            if(id==100||id==101){owner.SelectAudioOutput(id==100?"speaker":"headset");SchedulePendingHint();return;}
            if(id==102){owner.ToggleMicrophoneMute();SchedulePendingHint();return;}
            if(id==2){Go(PopupPage.Mouse);return;}if(id==3){Go(PopupPage.Headset);return;}
            if(id==200){Go(PopupPage.Audio);owner.RefreshAudioDevices();return;}if(id==201){Go(PopupPage.Preferences);return;}
            if(id==202){Go(PopupPage.Diagnostics);return;}if(id==203){Go(PopupPage.Bindings);return;}if(id==204){owner.ExitThread();return;}
            if(id==206){Go(PopupPage.Devices);return;}if(id==251){owner.RefreshWireless(navigation.SelectedDeviceKey);return;}
            if(id==205){owner.RefreshAudioDevices();return;}
            try {
                var pref=DeviceSettings.Current;
                if(id==210||id==211||id==213||id==216){navigation.PreferenceAction=id;Go(PopupPage.ChoosePreference);return;}
                if(id>=400&&id<=432) {
                    if(id<=402)pref.Theme=id==400?"auto":id==401?"light":"dark";
                    else if(id>=410&&id<=414){pref.TraySource=id==410?"auto":id==411?"mouse":id==412?"headset":id==413?"keyboard":"lowest";pref.TrayDeviceId=null;}
                    else if(id>=420&&id<=422)pref.WarningThreshold=id==420?10:id==421?15:20;
                    else if(id>=430)pref.BackgroundSeconds=id==430?60:id==431?120:300;else return;
                    DeviceSettings.SaveConfiguration(pref);owner.ConfigurationChanged(false);Back();return;
                }
                else if(id==212)pref.LowBatteryNotifications=!pref.LowBatteryNotifications;
                else if(id==214)pref.RememberReadings=!pref.RememberReadings;
                else if(id==215)pref.ReducedMotion=!pref.ReducedMotion;
                else if(id==217){owner.ClearReadingHistory();navigation.Notice="正在清除历史读数…";Invalidate();return;}
                else if(id==218){pref.TrayDeviceId=pref.TrayDeviceId==control.DeviceKey?null:control.DeviceKey;}
                else if(id==220||id==221||id==222){EditAlias(id==220,id==222?navigation.SelectedDeviceKey:null);return;}
                else if(id>=230&&id<=232){navigation.BindingRole=id==230?"speaker":id==231?"headset":"microphone";Go(PopupPage.PickBinding);return;}
                else if(id==233){DeviceSettings.ResetAudio();owner.ConfigurationChanged();navigation.Notice="已恢复自动识别";Invalidate();return;}
                else if(id==7||id==8){navigation.Offset=Math.Max(0,navigation.Offset+(id==7?-navigation.VisibleChoices:navigation.VisibleChoices));UpdateLayoutSize();Invalidate();return;}
                else if(id>=300){DeviceSettings.SetEndpoint(navigation.BindingRole,control.EndpointId);owner.ConfigurationChanged();Back();navigation.Notice="绑定已保存，当前音源未更改";Invalidate();return;}
                else if(id==250){Clipboard.SetText(owner.DiagnosticSummary());navigation.Notice="已复制不含设备ID的诊断摘要";Invalidate();return;}
                else return;
                DeviceSettings.SaveConfiguration(pref);owner.ConfigurationChanged(false);UpdateLayoutSize();Invalidate();
            }catch(Exception error){navigation.Notice="未保存："+error.Message;Invalidate();}
        }
        void SchedulePendingHint(){feedbackTimer.Stop();feedbackTimer.Start();}
        internal void Notice(string text){navigation.Notice=text;Invalidate();}
        void EditAlias(bool mouse,string deviceKey=null) {
            bool dark=Style.Dark;var pref=DeviceSettings.Current;
            using(var dialog=new Form {Text=deviceKey!=null?"设备显示名称":mouse?"鼠标显示名称":"耳机显示名称",ShowInTaskbar=false,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,StartPosition=FormStartPosition.CenterParent,ClientSize=new Size(280,106),BackColor=dark?Color.FromArgb(41,45,52):Color.FromArgb(250,251,253),ForeColor=PopupPages.Foreground(dark),Font=new Font("Microsoft YaHei UI",9)})
            using(var input=new TextBox {Text=deviceKey!=null?navigation.SelectedDeviceName:mouse?pref.MouseAlias:pref.HeadsetAlias,MaxLength=32,Location=new Point(14,15),Width=252,BackColor=PopupPages.Track(dark),ForeColor=PopupPages.Foreground(dark),BorderStyle=BorderStyle.FixedSingle})
            using(var save=new Button {Text="保存",DialogResult=DialogResult.OK,Location=new Point(191,62),Size=new Size(75,28)}) {
                dialog.Controls.Add(input);dialog.Controls.Add(save);dialog.AcceptButton=save;ignoreDeactivate=true;
                try {if(dialog.ShowDialog(this)==DialogResult.OK){pref=DeviceSettings.Current;if(deviceKey!=null){pref.DeviceAliases[deviceKey]=input.Text;navigation.SelectedDeviceName=input.Text;}else if(mouse)pref.MouseAlias=input.Text;else pref.HeadsetAlias=input.Text;DeviceSettings.SaveConfiguration(pref);owner.ConfigurationChanged(false);}}
                finally{ignoreDeactivate=false;}
            }
        }
        PointF PanelPoint(Point point) {return UiState.LogicalPoint(point,dpiScale);}
        protected override bool ProcessCmdKey(ref Message message,Keys keys) {
            if(keys==Keys.Escape){if(navigation.Page==PopupPage.Main)CloseAnimated();else Back();return true;}
            var controls=ControlsForPage();ReconcileKeyboardFocus(controls);var targets=new List<PopupControl>();foreach(var control in controls)if(control.Id>=0&&control.Enabled&&!control.Information)targets.Add(control);
            if(keyboardFocus==240&&(keys==Keys.Left||keys==Keys.Right)&&displayAudio!=null&&displayAudio.OutputVolumePercent.HasValue){owner.SelectOutputVolume(displayAudio.VolumeEndpointId,Math.Max(0,Math.Min(100,(owner.DesiredVolume??displayAudio.OutputVolumePercent.Value)+(keys==Keys.Left?-2:2))));return true;}
            if(keys==Keys.Tab||keys==(Keys.Tab|Keys.Shift)||keys==Keys.Right||keys==Keys.Left) {
                if(targets.Count==0)return true;int direction=keys==(Keys.Tab|Keys.Shift)||keys==Keys.Left?-1:1,index=targets.FindIndex(delegate(PopupControl c){return c.StableKey==keyboardFocusKey;});
                FocusControl(targets[(index<0?(direction>0?0:targets.Count-1):(index+direction+targets.Count)%targets.Count)]);return true;
            }
            if(keys==Keys.Enter||keys==Keys.Space) {
                var target=PopupPages.FindStable(controls,keyboardFocusKey);if(target!=null&&target.Enabled&&!target.Information)PerformAction(target);
                return true;
            }
            return base.ProcessCmdKey(ref message,keys);
        }
        void FocusControl(PopupControl control){keyboardFocus=control==null?-1:control.Id;keyboardFocusKey=control==null?null:control.StableKey;keyboardFocusIndex=control==null?0:ControlsForPage().FindIndex(delegate(PopupControl c){return c.StableKey==control.StableKey;});AccessibilityNotifyClients(AccessibleEvents.Focus,-1);Invalidate();}
        void ReconcileKeyboardFocus(List<PopupControl> controls){if(keyboardFocus<0&&keyboardFocusKey==null)return;if(keyboardFocusKey==null){var previous=controls.Find(delegate(PopupControl c){return c.Id==keyboardFocus;});if(previous!=null)keyboardFocusKey=previous.StableKey;}
            var next=PopupPages.ReconcileFocus(controls,keyboardFocusKey,keyboardFocusIndex);keyboardFocus=next==null?-1:next.Id;keyboardFocusKey=next==null?null:next.StableKey;}
        protected override AccessibleObject CreateAccessibilityInstance(){return new PopupAccessibility(this,ControlsForPage,delegate{return keyboardFocus;},PerformAction,delegate{return dpiScale;},FocusControl,delegate(PopupControl control,int value){if(displayAudio!=null)owner.SelectOutputVolume(displayAudio.VolumeEndpointId,value);});}
        protected override void WndProc(ref Message message) {
            if(message.Msg==0x02E0){dpiScale=Math.Max(1f,(message.WParam.ToInt64()&0xffff)/96f);UpdateLayoutSize();Invalidate();}
            base.WndProc(ref message);
        }
        int AudioAtPoint(PointF point) {
            if(headsetProgress<.98f||displayAudio==null)return -1;
            if(displayAudio.SpeakerAvailable&&VisualDesign.SpeakerOutputRectFor(owner.Dpi).Contains(point))return 0;
            if(displayAudio.HeadsetAvailable&&VisualDesign.HeadsetOutputRectFor(owner.Dpi).Contains(point))return 1;
            if(displayAudio.MicrophoneAvailable&&displayAudio.MicrophoneMuted.HasValue&&VisualDesign.MicrophoneMuteRectFor(owner.Dpi).Contains(point))return 2;
            return -1;
        }
        public void SyncDevices() {
            if(owner.UseWirelessLayout&&navigation.Page==PopupPage.Main&&!pageAnimation.Enabled&&!PopupPages.Reduced&&MainWireless().Size.Height!=targetSize.Height){pageStartHeight=targetSize.Height;pageProgress=0;pageClock.Restart();pageAnimation.Start();}
            bool visible=TrayContext.ShowHeadset(owner.Headset,owner.Audio);
            if(visible||headsetProgress==0f){displayHeadset=owner.Headset;displayAudio=owner.Audio;}
            float next=visible?1f:0f;
            if(next!=layoutTarget) {
                layoutStart=headsetProgress;layoutTarget=next;
                if(PopupPages.Reduced){headsetProgress=next;layoutAnimation.Stop();UpdateLayoutSize();}
                else{layoutClock.Restart();layoutAnimation.Start();}
            } else UpdateLayoutSize();
            ReconcileKeyboardFocus(ControlsForPage());AccessibilityNotifyClients(AccessibleEvents.Reorder,-1);
            Invalidate();
        }
        void UpdateLayoutSize() {
            Size mouse=VisualDesign.PanelSizeFor(false,owner.Dpi),both=VisualDesign.PanelSizeFor(true,owner.Dpi);
            targetSize=new Size(mouse.Width,mouse.Height+(int)Math.Round((both.Height-mouse.Height)*headsetProgress));
            if(navigation.Page==PopupPage.Main&&owner.UseWirelessLayout)targetSize=MainWireless().Size;
            if(navigation.Page!=PopupPage.Main)targetSize=PopupPages.SizeFor(ControlsForPage());
            if(pageProgress<1f)targetSize.Height=(int)Math.Round(pageStartHeight+(targetSize.Height-pageStartHeight)*pageProgress);
            Size physical=UiState.ScaleSize(targetSize,dpiScale);
            if(ClientSize!=physical) {
                ClientSize=physical;
                if(!layeredRendering)UpdateFallbackRegion();
            }
            PositionForSize((int)Math.Round((1-animationProgress)*6));
        }
        void UpdateFallbackRegion() {
            Region mask;
            if(navigation.Page==PopupPage.Main&&owner.UseWirelessLayout) {
                mask=new Region();mask.MakeEmpty();var layout=MainWireless();foreach(var card in layout.Cards)using(var shape=Style.Round(new RectangleF(0,card.Top,260,card.Height),16))mask.Union(shape);
                mask.Union(new RectangleF(0,layout.Footer,260,30));
            }else{
            int mouse=VisualDesign.MouseCardHeight(owner.Dpi),headsetTop=mouse+8;
            using(var shape=Style.Round(new RectangleF(0,0,targetSize.Width,navigation.Page==PopupPage.Main?mouse:targetSize.Height),VisualDesign.PanelRadius))mask=new Region(shape);
            if(navigation.Page==PopupPage.Main) {
                float height=Math.Max(1,96*headsetProgress);
                if(headsetProgress>0f)using(var shape=Style.Round(new RectangleF(0,headsetTop,targetSize.Width,height),Math.Min(VisualDesign.PanelRadius,height/2)))mask.Union(shape);
                mask.Union(new RectangleF(0,mouse+104*headsetProgress,targetSize.Width,30));
            }
            }
            using(var transform=new Matrix()){transform.Scale(dpiScale,dpiScale);mask.Transform(transform);}
            var previous=Region;Region=mask;if(previous!=null)previous.Dispose();
        }
        void AdvanceLayout() {
            float elapsed=Math.Min(1f,(float)layoutClock.Elapsed.TotalMilliseconds/160f);
            float eased=1f-(float)Math.Pow(1f-elapsed,3);
            headsetProgress=layoutStart+(layoutTarget-layoutStart)*eased;
            if(elapsed>=1f){headsetProgress=layoutTarget;layoutAnimation.Stop();layoutClock.Stop();if(headsetProgress==0f){displayHeadset=owner.Headset;displayAudio=owner.Audio;}}
            UpdateLayoutSize();Invalidate();
        }
        protected override bool ShowWithoutActivation {get{return false;}}
        protected override CreateParams CreateParams {
            get {var p=base.CreateParams;p.ExStyle|=0x80|0x80000;return p;}
        }
        public void OpenAnimated(bool fromLauncher=false) {
            anchor=Cursor.Position;
            if(fromLauncher){var screen=Screen.FromPoint(anchor).WorkingArea;anchor=new Point(screen.Right-24,screen.Top+16);}
            dpiScale=NativeDisplay.ScaleAt(anchor);ResetPage();UpdateLayoutSize();closing=false;ignoreDeactivate=true;animationProgress=0;
            frameAlpha=3;PositionForSize(6);Show();Activate();owner.PanelOpened();
            BeginInvoke((MethodInvoker)delegate {ignoreDeactivate=false;});AnimateTo(1f,190f);
        }
        void PositionForSize(int slideY) {
            var screen=Screen.FromPoint(anchor).WorkingArea;
            int margin=(int)Math.Round(8*dpiScale);
            int x=Math.Max(screen.Left+margin,Math.Min(anchor.X-Width/2,screen.Right-Width-margin));
            int y=anchor.Y<screen.Top+100*dpiScale?screen.Top+margin:anchor.Y-Height-(int)(14*dpiScale);
            y+=(int)Math.Round(slideY*dpiScale);
            y=Math.Max(screen.Top+margin,Math.Min(y,screen.Bottom-Height-margin));Location=new Point(x,y);
        }
        public void Reveal() {
            ResetPage();
            if(closing){closing=false;owner.PanelOpened();AnimateTo(1f,190f);}
            Show();Activate();
        }
        public void CloseAnimated() {
            if(closing)return;closing=true;owner.PanelClosed();AnimateTo(0f,135f);
        }
        public void ToggleAnimated() {
            if(closing) { ResetPage();closing=false;owner.PanelOpened();AnimateTo(1f,190f); }
            else CloseAnimated();
        }
        void ResetPage(){navigation.Reset();keyboardFocus=-1;keyboardFocusKey=null;hoverAction=-1;pressedAction=-1;hoverStage=-1;hoverAudio=-1;hoverRefresh=false;hoverSettings=false;lastHoverTip=null;controlTip.SetToolTip(this,null);pressedControl=null;draggingVolume=false;Capture=false;pageAnimation.Stop();pageProgress=1;UpdateLayoutSize();}
        void AnimateTo(float target,float durationMs) {
            if(PopupPages.Reduced){animationProgress=target;frameAlpha=target>0? (byte)255:(byte)0;animation.Stop();if(target==0&&closing){Hide();Dispose();return;}if(!layeredRendering)Opacity=1;Invalidate();return;}
            motionStart=animationProgress;motionTarget=target;
            motionDurationMs=Math.Max(60f,durationMs*Math.Abs(target-animationProgress));
            motionClock.Restart();animation.Start();
        }
        void AdvanceAnimation() {
            float progress=Math.Min(1f,(float)motionClock.Elapsed.TotalMilliseconds/motionDurationMs);
            float eased=1f-(float)Math.Pow(1f-progress,3);
            animationProgress=motionStart+(motionTarget-motionStart)*eased;
            if(progress>=1f){animationProgress=motionTarget;animation.Stop();motionClock.Stop();}
            if(animationProgress<=0f&&closing){Hide();Dispose();return;}
            PositionForSize((int)Math.Round((1-animationProgress)*6));frameAlpha=(byte)Math.Round(3+252*animationProgress);
            if(!layeredRendering)Opacity=.01+.99*animationProgress;
            Invalidate();
        }
        protected override void Dispose(bool disposing) {
            if(disposing){owner.PanelClosed();animation.Stop();animation.Dispose();layoutAnimation.Stop();layoutAnimation.Dispose();feedbackTimer.Stop();feedbackTimer.Dispose();pageAnimation.Stop();pageAnimation.Dispose();controlTip.Dispose();}
            base.Dispose(disposing);
        }
        protected override void OnPaintBackground(PaintEventArgs e) {if(!layeredRendering)base.OnPaintBackground(e);}
        public new void Invalidate() {
            // Per-pixel layered windows do not rely on WM_PAINT to publish
            // their initial surface. Present each visual update explicitly.
            if(layeredRendering&&IsHandleCreated&&!IsDisposed&&Visible) {
                if(RenderLayeredFrame())return;
                layeredRendering=false;
                UpdateFallbackRegion();
                Opacity=Math.Max(.01,frameAlpha/255d);
            }
            base.Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e);
            if(layeredRendering&&RenderLayeredFrame())return;
            if(layeredRendering) {
                layeredRendering=false;
                UpdateFallbackRegion();
                Opacity=Math.Max(.01,frameAlpha/255d);
            }
            var saved=e.Graphics.Save();e.Graphics.ScaleTransform(dpiScale,dpiScale);
            PaintContent(e.Graphics);
            e.Graphics.Restore(saved);
        }
        void PaintContent(Graphics g) {
            var saved=g.Save();
            if(navigation.Page!=PopupPage.Main){g.TranslateTransform(8*(1-pageProgress),0);PopupPages.Paint(g,owner,navigation,ControlsForPage(),Style.Dark,hoverAction,pressedAction,keyboardFocus);}
            else if(owner.UseWirelessLayout)CompactRenderer.PaintWireless(g,owner,MainWireless(),Style.Dark,Feedback());
            else VisualDesign.PaintDevicesPanel(g,owner.Current,owner.LastGood,owner.ErrorMessage,owner.IsRefreshing,owner.Dpi,owner.DpiError,owner.IsDpiRefreshing,Style.Dark,displayHeadset,displayAudio,animationProgress,hoverStage,hoverRefresh,headsetProgress,hoverAudio,Feedback(),hoverSettings);
            g.Restore(saved);
        }
        bool RenderLayeredFrame() {
            var feedback=Feedback();DateTime now=DateTime.UtcNow;
            var shown=owner.Current??owner.LastGood;
            var visible=new StringBuilder();
            visible.Append(ClientSize).Append('|').Append(Left).Append('|').Append(Top).Append('|').Append(frameAlpha).Append('|').Append(animationProgress.ToString("F4",System.Globalization.CultureInfo.InvariantCulture));
            visible.Append('|').Append(Style.Dark).Append('|').Append(hoverStage).Append('|').Append(hoverRefresh);
            visible.Append('|').Append(owner.Current!=null).Append('|').Append(shown==null?-1:shown.Percent).Append('|').Append(owner.Current!=null&&owner.Current.Charging);
            if(owner.Current==null&&owner.LastGood!=null)visible.Append('|').Append(owner.LastGood.TimestampUtc.ToLocalTime().ToString("HH:mm"));
            visible.Append('|').Append(owner.IsRefreshing&&owner.Current==null&&owner.LastGood==null).Append('|').Append(owner.IsDpiRefreshing).Append('|').Append(!String.IsNullOrEmpty(owner.DpiError));
            if(owner.Dpi!=null){visible.Append('|').Append(owner.Dpi.ActiveStage);foreach(var stage in owner.Dpi.Stages)visible.Append('|').Append(stage.XDpi).Append('/').Append(stage.YDpi);}
            visible.Append('|').Append(headsetProgress.ToString("F4",System.Globalization.CultureInfo.InvariantCulture));
            if(displayHeadset!=null)visible.Append('|').Append(displayHeadset.Connected).Append('|').Append(displayHeadset.ConnectionVerified).Append('|').Append(displayHeadset.Percent).Append('|').Append(displayHeadset.Charging);
            if(displayAudio!=null)visible.Append('|').Append(displayAudio.ActiveOutput).Append('|').Append(displayAudio.MicrophoneMuted).Append('|').Append(displayAudio.SpeakerAvailable).Append('|').Append(displayAudio.HeadsetAvailable).Append('|').Append(displayAudio.Error);
            visible.Append('|').Append(hoverAudio);
            visible.Append('|').Append(hoverSettings).Append('|').Append(feedback.Key);
            var preferences=DeviceSettings.Current;
            visible.Append('|').Append(navigation.Page).Append('|').Append(navigation.Offset).Append('|').Append(navigation.Notice).Append('|').Append(navigation.VolumePreview).Append('|').Append(pageProgress);
            visible.Append('|').Append(preferences.MouseAlias).Append('|').Append(preferences.HeadsetAlias).Append('|').Append(preferences.Theme).Append('|').Append(preferences.TraySource).Append('|').Append(preferences.LowBatteryNotifications).Append('|').Append(preferences.WarningThreshold).Append('|').Append(preferences.RememberReadings).Append('|').Append(preferences.ReducedMotion).Append('|').Append(preferences.BackgroundSeconds);
            if(owner.Audio!=null)visible.Append('|').Append(owner.Audio.OutputVolumePercent).Append('|').Append(owner.Audio.VolumeEndpointId);
            if(owner.Audio!=null&&owner.Audio.Devices!=null)foreach(var device in owner.Audio.Devices)visible.Append('|').Append(device.Id).Append('|').Append(device.Name);
            visible.Append('|').Append(owner.DesiredVolume).Append('|').Append(hoverAction).Append('|').Append(pressedAction);
            visible.Append('|').Append(owner.NativeMousePresent).Append('|').Append(owner.GenericReady).Append('|').Append(owner.GenericScanning).Append('|').Append(navigation.DeviceScroll).Append('|').Append(navigation.SelectedDeviceKey).Append('|').Append(navigation.SelectedDeviceName);
            foreach(var device in owner.WirelessDevices){visible.Append('|').Append(device.Key);string alias;if(preferences.DeviceAliases.TryGetValue(device.Id,out alias))visible.Append('|').Append(alias);}
            visible.Append('|').Append(UiState.Mouse(owner.Current,owner.LastGood,now).Key).Append('|').Append(UiState.Headset(displayHeadset,now).Key);
            if(displayAudio!=null)visible.Append('|').Append(displayAudio.MicrophoneAvailable);
            string key=visible.ToString();
            if(key==lastPresentedKey)return true;
            using(var bitmap=new Bitmap(ClientSize.Width,ClientSize.Height,PixelFormat.Format32bppPArgb)) {
                using(var g=Graphics.FromImage(bitmap)) {
                    g.Clear(Color.Transparent);g.ScaleTransform(dpiScale,dpiScale);
                    PaintContent(g);
                }
                IntPtr screen=GetDC(IntPtr.Zero),memory=IntPtr.Zero,image=IntPtr.Zero,previous=IntPtr.Zero;
                try {
                    memory=CreateCompatibleDC(screen);image=bitmap.GetHbitmap(Color.FromArgb(0));previous=SelectObject(memory,image);
                    var position=new NativePoint(Left,Top);var size=new NativeSize(ClientSize.Width,ClientSize.Height);var origin=new NativePoint(0,0);
                    var blend=new NativeBlend {Operation=0,Flags=0,Alpha=frameAlpha,Format=1};
                    bool presented=UpdateLayeredWindow(Handle,screen,ref position,ref size,memory,ref origin,0,ref blend,2);
                    if(!presented&&Marshal.GetLastWin32Error()==87) {
                        // WinForms can initialize a uniform-alpha layered window.
                        // Reset only this popup's layer before switching to ARGB.
                        long style=GetWindowLongPtr(Handle,-20).ToInt64();
                        SetWindowLongPtr(Handle,-20,new IntPtr(style&~0x80000L));SetWindowLongPtr(Handle,-20,new IntPtr(style|0x80000L));
                        presented=UpdateLayeredWindow(Handle,screen,ref position,ref size,memory,ref origin,0,ref blend,2);
                    }
                    if(presented)lastPresentedKey=key;
                    if(owner.DiagnosticsEnabled&&!renderPreviewSaved&&frameAlpha==255&&!layoutAnimation.Enabled&&owner.Current!=null&&owner.Dpi!=null) {
                        try {
                            Directory.CreateDirectory(DeviceSettings.DataDirectory);
                            bitmap.Save(Path.Combine(DeviceSettings.DataDirectory,"active-ui-render.png"),ImageFormat.Png);
                            File.WriteAllText(Path.Combine(DeviceSettings.DataDirectory,"visual-status.json"),"{\"layeredCompositor\":"+presented.ToString().ToLower()+",\"kind\":\"program-render\",\"percent\":"+owner.Current.Percent+",\"nativeTraySize\":"+SystemInformation.SmallIconSize.Width+"}");
                            renderPreviewSaved=true;
                        } catch(IOException) {}catch(UnauthorizedAccessException){}
                    }
                    return presented;
                } finally {
                    if(previous!=IntPtr.Zero)SelectObject(memory,previous);if(image!=IntPtr.Zero)DeleteObject(image);
                    if(memory!=IntPtr.Zero)DeleteDC(memory);if(screen!=IntPtr.Zero)ReleaseDC(IntPtr.Zero,screen);
                }
            }
        }
        PanelFeedback Feedback(){var value=owner.GetFeedback();value.KeyboardFocus=keyboardFocus;value.HoverAction=hoverAction;value.PressedAction=pressedAction;return value;}
        static RectangleF StageRect(int index,int count) {
            return VisualDesign.StageRect(index,count);
        }
        static int StageAtPoint(PointF point,DpiConfig dpi) {
            if(dpi==null||dpi.Stages==null)return -1;
            for(int i=0;i<dpi.Stages.Count;i++)if(StageRect(i,dpi.Stages.Count).Contains(point))return i;
            return -1;
        }
        public static void PaintPanel(Graphics g,BatteryReading current,BatteryReading lastGood,string error,bool busy,DpiConfig dpi,string dpiError,bool dpiBusy,bool dark,float openProgress=1f) {
            VisualDesign.PaintPanel(g,current,lastGood,error,busy,dpi,dpiError,dpiBusy,dark,openProgress);
        }
    }
    public sealed class TrayContext:ApplicationContext {
        readonly NotifyIcon tray=new NotifyIcon();
        readonly System.Windows.Forms.Timer refresh=new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer liveSync=new System.Windows.Forms.Timer();
        readonly Control dispatch=new Control();
        readonly Func<bool,MouseSnapshot> readSnapshot;
        readonly Func<int,DpiConfig> selectStage;
        readonly Func<HeadsetReading> readHeadset;
        readonly Func<AudioReading> readAudio;
        readonly Func<string,AudioReading> writeOutput;
        readonly Func<bool,AudioReading> writeMute;
        readonly Func<string,int,AudioReading> writeVolume;
        readonly ToolStripMenuItem audioSettingsMenu=new ToolStripMenuItem("音源设置");
        readonly System.Windows.Forms.Timer deviceDebounce=new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer expiryTimer=new System.Windows.Forms.Timer(),audioDebounce=new System.Windows.Forms.Timer(),operationTimer=new System.Windows.Forms.Timer();
        readonly SnapshotWriter fileWriter=new SnapshotWriter();
        readonly LowBatteryPolicy lowBattery=new LowBatteryPolicy();
        readonly bool persistStatus;
        GenericBatteryWorker genericWorker;
        int genericBusy;
        bool genericQueued;string genericQueuedTarget,genericTarget;
        int historyGeneration;bool clearingHistory;
        DateTime genericChecked=DateTime.MinValue;
        readonly Dictionary<string,WirelessDevice> wirelessCache=new Dictionary<string,WirelessDevice>();
        public WirelessDevice[] WirelessDevices=new WirelessDevice[0];
        public bool NativeMousePresent=true,GenericReady;
        public string GenericError;
        public string GenericHealthSummary;
        public int GenericFailureCount;
        public ReaderHealth[] GenericHealth=new ReaderHealth[0];
        public bool GenericScanning {get{return genericBusy!=0;}}
        public bool UseWirelessLayout {get{return GenericReady||!NativeMousePresent||WirelessDevices.Length>0;}}
        DeviceEventsWindow deviceEvents;
        AudioControl.NotificationListener audioEvents;
        bool sessionLocked,audioNeedsReconcile;
        string trayDeviceKey;
        DateTime audioOperationStarted;
        bool pendingHeadsetRefresh,manualRefresh;
        int stageInFlight=-1;
        string audioTargetInFlight,dpiActionFailure,audioActionFailure;
        DateTime dpiFailureAt,audioFailureAt;
        bool pendingAudioRefresh;
        int headsetBusy,audioBusy,headsetFailures;
        DateTime headsetCheckedUtc=DateTime.MinValue,audioCheckedUtc=DateTime.MinValue;
        HeadsetReading lastKnownHeadset;
        string pendingAudioOutput;
        bool? pendingMicrophoneMute;
        bool? microphoneTargetInFlight;
        int? pendingVolume,volumeTargetInFlight;
        string pendingVolumeEndpoint;
        DateTime actionRequestedAt;
        Icon icon;Details details;int busy;volatile bool disposed;
        bool switching,panelOpen,pendingRead,pendingBattery;
        int pendingStage=-1;
        DateTime lastSyncUtc=DateTime.MinValue;
        int completedReads,completedSwitches;
        string lastIconKey,lastTooltip;
        int iconUpdates;
        public BatteryReading Current,LastGood;
        public DpiConfig Dpi;
        public HeadsetReading Headset;
        public AudioReading Audio;
        public string ErrorMessage="Not yet read";
        public string DpiError="";
        public bool IsRefreshing {get{return busy!=0;}}
        public bool IsDpiRefreshing {get{return switching||pendingStage>=0;}}
        public bool IsAudioBusy {get{return audioBusy!=0;}}
        public bool IsHeadsetRefreshing {get{return headsetBusy!=0;}}
        public bool AudioControlsAvailable {get{return !audioNeedsReconcile;}}
        public int? DesiredVolume {get{return pendingVolume??volumeTargetInFlight;}}
        internal bool DiagnosticsEnabled {get{return persistStatus;}}
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
        static MouseSnapshot ReadWithRetry(bool includeBattery) {
            MouseSnapshot result=null;
            for(int attempt=0;attempt<2;attempt++) {
                try {
                    var next=MouseBattery.ReadSnapshot(includeBattery&&(result==null||result.Battery==null));
                    if(result!=null&&result.Battery!=null&&next.Battery==null){next.Battery=result.Battery;next.BatteryError=null;}
                    result=next;
                    if(result.Dpi!=null&&(!includeBattery||result.Battery!=null))break;
                } catch(Exception e) {
                    if(result==null)result=new MouseSnapshot {BatteryError=e.Message,DpiError=e.Message};
                }
                if(attempt<1)Thread.Sleep(150);
            }
            return result;
        }
        public TrayContext(bool showPanel=false) : this(showPanel,ReadWithRetry,MouseBattery.SetActiveDpiStage,HeadsetBattery.Read,AudioBrokerClient.Read,true,AudioBrokerClient.Switch,AudioBrokerClient.Mute,AudioBrokerClient.Volume) {
            deviceDebounce.Interval=1200;deviceDebounce.Tick+=delegate {deviceDebounce.Stop();RefreshBattery(false);};
            deviceEvents=new DeviceEventsWindow(delegate {if(!disposed){deviceDebounce.Stop();deviceDebounce.Start();}},delegate{Style.InvalidateTheme();UpdateIcon();},delegate(bool locked){sessionLocked=locked;refresh.Interval=locked?Math.Max(300000,DeviceSettings.Current.BackgroundSeconds*1000):DeviceSettings.Current.BackgroundSeconds*1000;if(locked){if(details!=null&&!details.IsDisposed)details.CloseAnimated();}else{deviceDebounce.Stop();deviceDebounce.Start();}});
            audioDebounce.Interval=200;audioDebounce.Tick+=delegate{audioDebounce.Stop();RequestAudioRead(true);};
            ThreadPool.QueueUserWorkItem(delegate{try{var listener=AudioControl.Listen(delegate{try{dispatch.BeginInvoke((MethodInvoker)delegate{if(!disposed){audioDebounce.Stop();audioDebounce.Start();}});}catch(InvalidOperationException){}});if(disposed)listener.Dispose();else audioEvents=listener;}catch(Exception){SafeDiagnostic.Transition("audio_notifications","unavailable");}});
            try {string path=Path.Combine(DeviceSettings.DataDirectory,"low-battery-state.txt");if(File.Exists(path)&&new FileInfo(path).Length<65536){string text=File.ReadAllText(path);lowBattery.Restore("mouse",text.Contains("mouse=1"));lowBattery.Restore("headset",text.Contains("headset=1"));foreach(System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text,@"(?m)^(device-[a-f0-9]{24})=1\r?$"))lowBattery.Restore(match.Groups[1].Value,true);}}catch(IOException){}catch(UnauthorizedAccessException){}
        }
        internal TrayContext(bool showPanel,Func<bool,MouseSnapshot> read,Func<int,DpiConfig> select) : this(showPanel,read,select,null,null) {}
        internal TrayContext(bool showPanel,Func<bool,MouseSnapshot> read,Func<int,DpiConfig> select,Func<HeadsetReading> headsetReader,Func<AudioReading> audioReader,bool diagnostics=false,Func<string,AudioReading> outputWriter=null,Func<bool,AudioReading> muteWriter=null,Func<string,int,AudioReading> volumeWriter=null) {
            persistStatus=diagnostics;
            readSnapshot=read;selectStage=select;readHeadset=headsetReader;readAudio=audioReader;writeOutput=outputWriter;writeMute=muteWriter;writeVolume=volumeWriter;
            if(diagnostics){genericWorker=new GenericBatteryWorker();try{NativeMousePresent=MouseBattery.Enumerate().Count>0;}catch{NativeMousePresent=false;}}
            var unused=dispatch.Handle;
            var menu=new ContextMenuStrip();
            var detail=new ToolStripMenuItem("设备电量与音频");detail.Click+=delegate {ShowDetails();};menu.Items.Add(detail);
            var update=new ToolStripMenuItem("\u5237\u65b0");update.Click+=delegate {RefreshBattery();};menu.Items.Add(update);
            menu.Items.Add(audioSettingsMenu);menu.Opening+=delegate {BuildAudioSettingsMenu();};
            var settingsItem=new ToolStripMenuItem("设置…");settingsItem.Click+=delegate {ShowSettings();};menu.Items.Add(settingsItem);
            menu.Items.Add(new ToolStripSeparator());
            var exit=new ToolStripMenuItem("\u9000\u51fa");exit.Click+=delegate {ExitThread();};menu.Items.Add(exit);
            tray.ContextMenuStrip=menu;tray.MouseClick+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left)ShowDetails();};
            if(diagnostics&&DeviceSettings.Current.RememberReadings)ReadingCache.Load(Path.Combine(DeviceSettings.DataDirectory,"readings-cache.json"),out LastGood,out lastKnownHeadset);
            if(diagnostics&&DeviceSettings.Current.RememberReadings)foreach(var device in ReadingCache.LoadWireless(Path.Combine(DeviceSettings.DataDirectory,"wireless-cache.json")))wirelessCache[device.Id]=device;
            UpdateIcon();tray.Visible=diagnostics;
            refresh.Interval=DeviceSettings.Current.BackgroundSeconds*1000;refresh.Tick+=delegate {if(!panelOpen){RequestRead(true,false);RequestHeadsetRead(false);RequestAudioRead(false);RequestWirelessRead(false);}};refresh.Start();
            expiryTimer.Tick+=delegate{expiryTimer.Stop();UpdateIcon();};
            operationTimer.Interval=500;operationTimer.Tick+=delegate{if(audioBusy!=0&&(DateTime.UtcNow-audioOperationStarted).TotalSeconds>4){audioActionFailure="音频响应较慢，结果尚未确认";audioFailureAt=DateTime.UtcNow;if(details!=null&&!details.IsDisposed)details.Invalidate();}};
            liveSync.Interval=2000;liveSync.Tick+=delegate {
                if(!panelOpen||details==null||details.IsDisposed||!details.Visible){liveSync.Stop();return;}
                bool batteryDue=Current==null||(DateTime.UtcNow-Current.TimestampUtc).TotalSeconds>=30;
                RequestRead(batteryDue,false);
                RequestHeadsetRead(false);RequestAudioRead(false);
                RequestWirelessRead(false);
            };
            tray.BalloonTipClicked+=delegate {ShowDetails(true);};
            RefreshBattery(false);
            if(showPanel)dispatch.BeginInvoke((MethodInvoker)delegate {ShowDetails(true);});
        }
        void ShowDetails(bool reveal=false) {
            if(details!=null&&!details.IsDisposed){if(reveal)details.Reveal();else details.ToggleAnimated();return;}
            details=new Details(this);details.OpenAnimated(reveal);
        }
        internal void ListenForShowRequest(ShowPanelSignal signal) {
            signal.Listen(delegate {if(disposed)return;try {dispatch.BeginInvoke((MethodInvoker)delegate {if(!disposed)ShowDetails(true);});}catch(InvalidOperationException){}});
        }
        internal void ListenForExitRequest(ShowPanelSignal signal){signal.Listen(delegate{try{dispatch.BeginInvoke((MethodInvoker)delegate{if(!disposed)ExitThread();});}catch(InvalidOperationException){}});}
        public void ShowSettings() {
            if(details==null||details.IsDisposed){details=new Details(this);details.OpenAnimated(true);}else details.Reveal();
            details.ShowPreferences();
        }
        public void RefreshAudioDevices(){RequestAudioRead(true);}
        public void ConfigurationChanged(bool refreshAudio=true){refresh.Interval=sessionLocked?Math.Max(300000,DeviceSettings.Current.BackgroundSeconds*1000):DeviceSettings.Current.BackgroundSeconds*1000;UpdateIcon();if(refreshAudio){if(audioEvents!=null)audioEvents.SetMicrophoneTarget(null);RequestAudioRead(true);}}
        public PanelFeedback GetFeedback() {
            var now=DateTime.UtcNow;
            return new PanelFeedback {PendingStage=pendingStage>=0?pendingStage:stageInFlight,
                PendingOutput=pendingAudioOutput??audioTargetInFlight,MicrophonePending=pendingMicrophoneMute.HasValue||microphoneTargetInFlight.HasValue,
                DpiFailure=(now-dpiFailureAt).TotalSeconds<10?dpiActionFailure:null,
                AudioFailure=audioNeedsReconcile?"音频状态未确认，控制暂时停用":(now-audioFailureAt).TotalSeconds<10?audioActionFailure:null,
                AudioBlocked=audioNeedsReconcile,
                ShowPendingHints=(now-actionRequestedAt).TotalMilliseconds>=280,
                ManualRefreshing=manualRefresh&&(busy!=0||headsetBusy!=0||audioBusy!=0||pendingRead||genericBusy!=0)};
        }
        public void PanelOpened() {
            panelOpen=true;liveSync.Start();RefreshBattery(false);
        }
        public void PanelClosed() {
            if(disposed)return;panelOpen=false;liveSync.Stop();SaveStatus();
        }
        void UpdateIcon() {
            if(busy==0&&headsetBusy==0&&audioBusy==0&&genericBusy==0&&!pendingRead)manualRefresh=false;
            AudioBindings preferences;try{preferences=DeviceSettings.Current;}catch{preferences=new AudioBindings();}
            DateTime now=DateTime.UtcNow;var mouse=UiState.Mouse(Current,LastGood,now);var headset=UiState.Headset(Headset,now);
            var presentation=UiState.Tray(preferences.TraySource,mouse,headset);var shown=presentation.Battery;
            if(UseWirelessLayout){presentation=WirelessTray(preferences.TraySource);shown=presentation.Battery;}
            bool stale=shown.Percent.HasValue&&!shown.Fresh;
            bool dark=Style.Dark;string key=shown.Key+"|"+dark+"|"+SystemInformation.SmallIconSize.Width;
            if(key!=lastIconKey) {
                using(var bitmap=Style.Tray(shown.Percent,shown.Charging,dark,stale,shown.Approximate)) {
                    IntPtr handle=bitmap.GetHicon();var next=(Icon)Icon.FromHandle(handle).Clone();DestroyIcon(handle);
                    var old=icon;icon=next;tray.Icon=icon;if(old!=null)old.Dispose();
                }
                lastIconKey=key;iconUpdates++;
            }
            string tooltip=presentation.Tooltip;
            if(tooltip!=lastTooltip){tray.Text=tooltip;lastTooltip=tooltip;}
            if(details!=null&&!details.IsDisposed)details.SyncDevices();
            if(persistStatus&&preferences.LowBatteryNotifications)EvaluateLowBattery(mouse,headset);
            ScheduleExpiry();
            SaveStatus();
        }
        void ScheduleExpiry(){
            expiryTimer.Stop();var now=DateTime.UtcNow;DateTime earliest=DateTime.MaxValue;
            Action<DateTime> add=delegate(DateTime time){if(time==DateTime.MinValue||time>now.AddSeconds(5))return;var expiry=time.AddSeconds(90.1);if(expiry>now&&expiry<earliest)earliest=expiry;};
            if(Current!=null)add(Current.TimestampUtc);if(Headset!=null){add(Headset.BatteryTimestampUtc);add(Headset.ChargingTimestampUtc);}foreach(var device in WirelessDevices)if(device.Fresh)add(device.ConfirmedTime);
            if(earliest!=DateTime.MaxValue){expiryTimer.Interval=Math.Max(250,Math.Min(91000,(int)(earliest-now).TotalMilliseconds));expiryTimer.Start();}
        }
        TrayView WirelessTray(string source) {
            var layout=WirelessLayout.Build(this,0,100000);var available=new List<WirelessCard>();
            foreach(var card in layout.All)if(card.Id!="empty")available.Add(card);
            WirelessCard selected=null;string fixedId=DeviceSettings.Current.TrayDeviceId;
            if(!String.IsNullOrEmpty(fixedId))selected=available.Find(delegate(WirelessCard card){return card.Id==fixedId&&card.Battery.Fresh&&card.Battery.Percent.HasValue;});
            if(selected==null&&source=="auto")selected=available.Find(delegate(WirelessCard card){return card.Id==trayDeviceKey&&card.Battery.Fresh&&card.Battery.Percent.HasValue;});
            foreach(var card in available) {
                if(selected!=null&&(selected.Id==fixedId||source=="auto"&&selected.Id==trayDeviceKey))break;
                bool prefer=source=="auto"||source=="lowest"||card.Kind==source;
                if(!prefer)continue;
                if(selected==null||card.Battery.Fresh&&!selected.Battery.Fresh||card.Battery.Fresh==selected.Battery.Fresh&&(source=="lowest"?card.Battery.Percent.HasValue&&(!selected.Battery.Percent.HasValue||card.Battery.Percent<selected.Battery.Percent):!selected.Battery.Percent.HasValue&&card.Battery.Percent.HasValue))selected=card;
            }
            if(source=="lowest"&&selected!=null){var previous=available.Find(delegate(WirelessCard card){return card.Id==trayDeviceKey&&card.Battery.Fresh==selected.Battery.Fresh&&card.Battery.Percent==selected.Battery.Percent;});if(previous!=null)selected=previous;}
            if(selected==null&&available.Count>0){selected=available[0];foreach(var card in available)if(card.Battery.Fresh&&card.Battery.Percent.HasValue){selected=card;break;}}
            var battery=selected==null?new BatteryView {Name="无线设备"}:selected.Battery;
            trayDeviceKey=selected==null?null:selected.Id;
            var tooltip=new StringBuilder();foreach(var card in available){if(tooltip.Length>0)tooltip.Append(" | ");tooltip.Append(card.Name).Append(' ').Append(card.Battery.Approximate?"约":"").Append(card.Battery.Percent.HasValue?card.Battery.Percent+"%":"--");}
            string qualifier=selected!=null&&selected.Battery.Percent.HasValue&&!selected.Battery.Fresh?"（上次 "+selected.Battery.TimeText+"）":"";
            string text=(selected==null?"无线设备":selected.Name)+qualifier+"\n"+tooltip.ToString();if(text.Length>63)text=text.Substring(0,62)+"…";
            return new TrayView {Battery=battery,Tooltip=text};
        }
        void EvaluateLowBattery(BatteryView mouse,BatteryView headset) {
            bool oldMouse=lowBattery.IsLatched("mouse"),oldHeadset=lowBattery.IsLatched("headset");
            bool allowed=NativeDisplay.MayNotify();var messages=new List<string>();
            int threshold=DeviceSettings.Current.WarningThreshold;
            if(lowBattery.Observe("mouse",mouse,allowed,threshold))messages.Add("鼠标剩余 "+mouse.Percent+"%");
            if(lowBattery.Observe("headset",headset,allowed,threshold))messages.Add("耳机剩余 "+headset.Percent+"%");
            bool changed=false;
            foreach(var device in WirelessDevices){bool before=lowBattery.IsLatched(device.Id);var view=WirelessLayout.View(device);if(lowBattery.Observe(device.Id,view,allowed,threshold)){string name;var pref=DeviceSettings.Current;if(!pref.DeviceAliases.TryGetValue(device.Id,out name))name=device.Name;messages.Add(name+"剩余 "+(device.Approximate?"约":"")+device.Percent+"%");}changed|=before!=lowBattery.IsLatched(device.Id);}
            if(oldMouse!=lowBattery.IsLatched("mouse")||oldHeadset!=lowBattery.IsLatched("headset")||changed)try {
                Directory.CreateDirectory(DeviceSettings.DataDirectory);
                var saved=new StringBuilder("mouse="+(lowBattery.IsLatched("mouse")?1:0)+"\nheadset="+(lowBattery.IsLatched("headset")?1:0));
                foreach(var device in WirelessDevices)if(lowBattery.IsLatched(device.Id))saved.Append('\n').Append(device.Id).Append("=1");
                fileWriter.Enqueue(Path.Combine(DeviceSettings.DataDirectory,"low-battery-state.txt"),saved.ToString());
            }catch(IOException){}catch(UnauthorizedAccessException){}
            if(messages.Count>0){string text=String.Join(" · ",messages.ToArray());if(text.Length>240)text=text.Substring(0,237)+"…";tray.ShowBalloonTip(5000,"外设电量较低",text,ToolTipIcon.Warning);}
        }
        void SaveStatus() {
            if(!persistStatus)return;
            try {
                if(!clearingHistory&&DeviceSettings.Current.RememberReadings)fileWriter.Enqueue(Path.Combine(DeviceSettings.DataDirectory,"readings-cache.json"),ReadingCache.EncodeNative(Current??LastGood,Headset??lastKnownHeadset));
                string path=Path.Combine(DeviceSettings.DataDirectory,"status.json");
                string safe=Escape(ErrorMessage);
                var stages=new StringBuilder("[");
                if(Dpi!=null&&Dpi.Stages!=null)for(int i=0;i<Dpi.Stages.Count;i++) {
                    if(i>0)stages.Append(',');stages.Append("{\"x\":").Append(Dpi.Stages[i].XDpi).Append(",\"y\":").Append(Dpi.Stages[i].YDpi).Append('}');
                }
                stages.Append(']');
                string json="{\"version\":\""+Escape(AppRuntime.Version)+"\",\"page\":\""+(details==null||details.IsDisposed?"closed":details.CurrentPage)+"\",\"liveVerified\":"+(Current!=null&&UiState.Recent(Current.TimestampUtc,DateTime.UtcNow,90)?"true":"false")+
                    ",\"percent\":"+(Current!=null?Current.Percent.ToString():"null")+
                    ",\"charging\":"+(Current!=null?Current.Charging.ToString().ToLower():"null")+
                    ",\"dpiLiveVerified\":"+(Dpi!=null&&String.IsNullOrEmpty(DpiError)?"true":"false")+
                    ",\"dpi\":"+(Dpi!=null&&Dpi.Stages!=null&&Dpi.ActiveStage>=0&&Dpi.ActiveStage<Dpi.Stages.Count?Dpi.Stages[Dpi.ActiveStage].XDpi.ToString():"null")+
                    ",\"activeStage\":"+(Dpi!=null?Dpi.ActiveStage.ToString():"null")+
                    ",\"stages\":"+stages+
                    ",\"panelOpen\":"+panelOpen.ToString().ToLower()+",\"syncIntervalMs\":"+(panelOpen?"2000":refresh.Interval.ToString())+
                    ",\"busy\":"+(busy!=0?"true":"false")+",\"pendingStage\":"+pendingStage+
                    ",\"completedReads\":"+completedReads+",\"completedSwitches\":"+completedSwitches+
                    ",\"iconUpdates\":"+iconUpdates+
                    ",\"batteryCheckedAtUtc\":"+(Current!=null?"\""+Current.TimestampUtc.ToString("o")+"\"":"null")+
                    ",\"dpiCheckedAtUtc\":"+(Dpi!=null?"\""+Dpi.TimestampUtc.ToString("o")+"\"":"null")+
                    ",\"checkedAtUtc\":\""+lastSyncUtc.ToString("o")+"\",\"detail\":\""+safe+"\",\"dpiDetail\":\""+Escape(DpiError)+"\""+
                    ",\"headset\":{\"receiverPresent\":"+(Headset!=null&&Headset.ReceiverPresent?"true":"false")+
                    ",\"connectionVerified\":"+(Headset!=null&&Headset.ConnectionVerified?"true":"false")+
                    ",\"connected\":"+(Headset!=null&&Headset.ConnectionVerified?Headset.Connected.ToString().ToLower():"null")+
                    ",\"percent\":"+(Headset!=null&&Headset.Percent.HasValue?Headset.Percent.Value.ToString():"null")+
                    ",\"detail\":\""+Escape(Headset==null?"Not yet read":Headset.Detail)+"\"}"+
                    ",\"audio\":{\"activeOutput\":\""+Escape(Audio==null?"unknown":Audio.ActiveOutput)+"\",\"headsetAvailable\":"+(Audio!=null&&Audio.HeadsetAvailable?"true":"false")+
                    ",\"speakerAvailable\":"+(Audio!=null&&Audio.SpeakerAvailable?"true":"false")+
                    ",\"volumePercent\":"+(Audio!=null&&Audio.OutputVolumePercent.HasValue?Audio.OutputVolumePercent.Value.ToString():"null")+
                    ",\"microphoneMuted\":"+(Audio!=null&&Audio.MicrophoneMuted.HasValue?Audio.MicrophoneMuted.Value.ToString().ToLower():"null")+
                    ",\"error\":\""+Escape(Audio==null?"":SafeDiagnostic.Clean(Audio.Error))+"\"},\"universalReady\":"+GenericReady.ToString().ToLower()+",\"universalDeviceCount\":"+WirelessDevices.Length+",\"universalError\":\""+Escape(GenericError)+"\",\"readerHealth\":\""+Escape(GenericHealthSummary)+"\",\"readerFailures\":"+GenericFailureCount+",\"audioReconcilePending\":"+audioNeedsReconcile.ToString().ToLower()+"}";
                fileWriter.Enqueue(path,json);
            } catch(IOException) {}catch(UnauthorizedAccessException){}
        }
        static string Escape(string value) {return (value??"").Replace("\\","\\\\").Replace("\"","\\\"").Replace("\r"," ").Replace("\n"," ");}
        public void RefreshBattery(bool manual=true) {
            manualRefresh|=manual;
            RequestRead(true,true);
            RequestHeadsetRead(true);RequestAudioRead(true);
            RequestWirelessRead(true);
        }
        public void RefreshDevice(string device) {
            manualRefresh=true;
            if(device=="mouse")RequestRead(true,true);
            else if(device=="headset")RequestHeadsetRead(true);
            else RefreshBattery();
            UpdateIcon();
        }
        public void RefreshWireless(string deviceId=null){manualRefresh=true;RequestWirelessRead(true,deviceId);UpdateIcon();}
        void RequestWirelessRead(bool force,string deviceId=null) {
            if(disposed||genericWorker==null)return;
            if(clearingHistory){genericQueued=true;genericQueuedTarget=null;return;}
            if(!genericWorker.Available){GenericError="请使用完整发行包以启用通用设备识别。";return;}
            if(genericBusy!=0){if(force){genericQueuedTarget=genericQueued&&genericQueuedTarget!=deviceId?null:deviceId;genericQueued=true;}return;}
            if(!force&&(DateTime.UtcNow-genericChecked).TotalSeconds<(panelOpen?8:DeviceSettings.Current.BackgroundSeconds))return;
            genericBusy=1;genericTarget=deviceId;int epoch=historyGeneration;
            ThreadPool.QueueUserWorkItem(delegate {
                var snapshot=genericWorker.Poll(force,deviceId,delegate(WirelessSnapshot partial){if(disposed)return;try{dispatch.BeginInvoke((MethodInvoker)delegate{if(!disposed&&epoch==historyGeneration)ApplyWireless(partial,false);});}catch(InvalidOperationException){}});if(disposed)return;
                try{dispatch.BeginInvoke((MethodInvoker)delegate {
                    if(disposed)return;genericBusy=0;genericTarget=null;genericChecked=DateTime.UtcNow;
                    if(epoch==historyGeneration)ApplyWireless(snapshot,true);
                    UpdateIcon();if(genericQueued){string target=genericQueuedTarget;genericQueued=false;genericQueuedTarget=null;RequestWirelessRead(true,target);}
                });}catch(InvalidOperationException){}
            });
        }
        void ApplyWireless(WirelessSnapshot snapshot,bool complete){
            if(clearingHistory)return;
            if(String.IsNullOrEmpty(snapshot.Error)){
                GenericReady=true;GenericError=null;WirelessDevices=snapshot.Devices??new WirelessDevice[0];GenericHealth=snapshot.Health??new ReaderHealth[0];GenericFailureCount=0;
                foreach(var health in GenericHealth)if(health.State=="error"||health.State=="timeout"||health.State=="unavailable"||health.State=="backoff")GenericFailureCount++;
                GenericFailureCount=Math.Max(GenericFailureCount,(snapshot.Errors??new string[0]).Length);GenericHealthSummary=GenericFailureCount>0?"部分读取暂不可用（"+GenericFailureCount+"项）":null;
                var pref=DeviceSettings.Current;
                foreach(var device in WirelessDevices){WirelessDevice cached;
                    // A legacy PID-only key cannot prove which physical device
                    // produced a historical reading, even if only one is here now.
                    if(pref.RememberReadings&&!device.Percent.HasValue&&wirelessCache.TryGetValue(device.Id,out cached)&&device.SameCacheIdentity(cached)&&(DateTime.UtcNow-cached.ConfirmedTime).TotalDays<=7){device.Percent=cached.Percent;device.ConfirmedAtUtc=cached.ConfirmedAtUtc;device.Fresh=false;device.Approximate=cached.Approximate;device.Charging=null;device.SourceQuality="cached";}
                    if(device.Percent.HasValue)wirelessCache[device.Id]=device;
                }
                foreach(string key in new List<string>(wirelessCache.Keys))if((DateTime.UtcNow-wirelessCache[key].ConfirmedTime).TotalDays>7)wirelessCache.Remove(key);
                if(complete&&!clearingHistory&&pref.RememberReadings)fileWriter.Enqueue(Path.Combine(DeviceSettings.DataDirectory,"wireless-cache.json"),ReadingCache.EncodeWireless(wirelessCache.Values));
                bool previous=NativeMousePresent;if(snapshot.NativeMousePresent.HasValue)NativeMousePresent=snapshot.NativeMousePresent.Value;if(NativeMousePresent&&!previous)RequestRead(true,false);
            }else{GenericError=snapshot.Error;GenericHealthSummary="读取组件暂不可用";foreach(var device in WirelessDevices){device.Fresh=false;device.Charging=null;}}
            if(complete){string state=GenericError??GenericHealthSummary??"ready";ThreadPool.QueueUserWorkItem(delegate{SafeDiagnostic.Transition("device_readers",state);});}
            UpdateIcon();
        }
        public bool IsDeviceRefreshing(string id){return genericBusy!=0&&(genericTarget==null||genericTarget==id);}
        public void ClearReadingHistory(){
            if(clearingHistory)return;clearingHistory=true;historyGeneration++;wirelessCache.Clear();if(Current==null||!UiState.Recent(Current.TimestampUtc,DateTime.UtcNow,90)){Current=null;LastGood=null;}else LastGood=Current;lastKnownHeadset=null;
            foreach(var device in WirelessDevices)if(!device.Fresh){device.Percent=null;device.ConfirmedAtUtc=null;device.Charging=null;device.SourceQuality="unknown";}
            if(Headset!=null&&!UiState.Recent(Headset.BatteryTimestampUtc,DateTime.UtcNow,90))Headset.Percent=null;
            ThreadPool.QueueUserWorkItem(delegate{bool ok=fileWriter.Flush(5000);if(ok)foreach(string name in new[]{"readings-cache.json","wireless-cache.json"}){string path=Path.Combine(DeviceSettings.DataDirectory,name);fileWriter.Forget(path);try{if(File.Exists(path))File.Delete(path);}catch(IOException){ok=false;}catch(UnauthorizedAccessException){ok=false;}}
                if(genericWorker!=null)genericWorker.Reset();if(!disposed)try{dispatch.BeginInvoke((MethodInvoker)delegate{clearingHistory=false;genericQueued=false;genericQueuedTarget=null;if(details!=null&&!details.IsDisposed)details.Notice(ok?"历史读数已清除":"本地缓存仍被占用，请稍后重试");RefreshBattery(false);});}catch(InvalidOperationException){}});UpdateIcon();
        }
        public string DiagnosticSummary() {
            var now=DateTime.UtcNow;var mouse=UiState.Mouse(Current,LastGood,now);var headset=UiState.Headset(Headset,now);
            var text="MyFinder Device Battery "+AppRuntime.Version+"\r\n鼠标："+mouse.Tooltip+"\r\n耳机："+headset.Tooltip+
                "\r\n耳机无线："+(ShowHeadset(Headset,Audio)?"已确认连接":"未确认连接")+"\r\n音源："+(Audio==null?"未确认":Audio.ActiveOutput)+
                "\r\n后台间隔："+DeviceSettings.Current.BackgroundSeconds+"秒\r\n自启："+PopupPages.StartupState();
            foreach(var device in WirelessDevices)text+="\r\n"+device.Kind+"："+WirelessLayout.View(device).Tooltip;
            text+="\r\n通用读取："+(GenericReady?"已就绪":GenericScanning?"识别中":"尚未就绪")+"\r\n读取健康："+(GenericError??GenericHealthSummary??"正常");foreach(var item in GenericHealth)if(item.State!="ok"&&item.State!="no_devices")text+="\r\n"+item.Provider+"："+item.State+" / "+item.ErrorCode;
            return String.Join(Environment.NewLine,Array.ConvertAll(text.Split(new[]{"\r\n"},StringSplitOptions.None),delegate(string line){return SafeDiagnostic.Clean(line);}));
        }
        internal static bool ShowHeadset(HeadsetReading headset,AudioReading audio) {
            return VisualDesign.ShouldShowHeadset(headset);
        }
        void RequestHeadsetRead(bool force) {
            if(disposed||readHeadset==null)return;
            if(headsetBusy!=0){if(force)pendingHeadsetRefresh=true;return;}
            double interval=panelOpen?(headsetFailures==0?6:Math.Min(60,10*Math.Pow(2,Math.Min(headsetFailures-1,3)))):60;
            if(!force&&(DateTime.UtcNow-headsetCheckedUtc).TotalSeconds<interval)return;
            headsetBusy=1;
            ThreadPool.QueueUserWorkItem(delegate {
                HeadsetReading result;
                try {result=readHeadset();}catch(Exception e){result=new HeadsetReading {Detail=e.Message};}
                if(disposed)return;
                try {dispatch.BeginInvoke((MethodInvoker)delegate {
                    if(disposed)return;
                    headsetBusy=0;headsetCheckedUtc=DateTime.UtcNow;
                    if(result==null)result=new HeadsetReading {Detail="Headset query returned no state"};
                    if(result.ConnectionVerified) {
                        headsetFailures=0;
                        if(result.Connected&&!result.Percent.HasValue&&lastKnownHeadset!=null&&lastKnownHeadset.Connected) {
                            result.Percent=lastKnownHeadset.Percent;result.BatteryTimestampUtc=lastKnownHeadset.BatteryTimestampUtc;
                        }
                        lastKnownHeadset=result;
                    } else {
                        headsetFailures++;
                        if((result.ReceiverPresent||!result.ReceiverPresenceVerified)&&lastKnownHeadset!=null) {
                            result.ReceiverPresent=lastKnownHeadset.ReceiverPresent;
                            result.ConnectionTimestampUtc=lastKnownHeadset.ConnectionTimestampUtc;
                            result.Connected=lastKnownHeadset.Connected&&(DateTime.UtcNow-lastKnownHeadset.ConnectionTimestampUtc).TotalSeconds<120;
                            if(result.Connected){result.Percent=lastKnownHeadset.Percent;result.BatteryTimestampUtc=lastKnownHeadset.BatteryTimestampUtc;}
                        }
                        if(!result.ReceiverPresent&&result.ReceiverPresenceVerified)lastKnownHeadset=null;
                    }
                    Headset=result;UpdateIcon();if(pendingHeadsetRefresh){pendingHeadsetRefresh=false;RequestHeadsetRead(true);}
                });}catch(ObjectDisposedException){}catch(InvalidOperationException){}
            });
        }
        void RequestAudioRead(bool force) {
            if(disposed||readAudio==null)return;
            if(audioBusy!=0){if(force)pendingAudioRefresh=true;return;}
            if(!force&&(DateTime.UtcNow-audioCheckedUtc).TotalSeconds<(panelOpen?(audioEvents!=null&&audioEvents.Ready?10:2):60))return;
            RunAudioOperation(readAudio);
        }
        void RunAudioOperation(Func<AudioReading> operation) {
            if(disposed||audioBusy!=0)return;audioBusy=1;audioOperationStarted=DateTime.UtcNow;operationTimer.Start();
            if(details!=null&&!details.IsDisposed)details.Invalidate();
            ThreadPool.QueueUserWorkItem(delegate {
                AudioReading result;try {result=operation();}catch(Exception e){result=new AudioReading {Error=e.Message};}
                if(disposed)return;
                try {dispatch.BeginInvoke((MethodInvoker)delegate {
                    if(disposed)return;audioBusy=0;operationTimer.Stop();
                    bool wasWrite=audioTargetInFlight!=null||microphoneTargetInFlight.HasValue||volumeTargetInFlight.HasValue;
                    if(result!=null&&result.OutcomeUnknown){audioNeedsReconcile=true;pendingAudioOutput=null;pendingMicrophoneMute=null;pendingVolume=null;pendingVolumeEndpoint=null;pendingAudioRefresh=wasWrite;}
                    else if(audioTargetInFlight==null&&!microphoneTargetInFlight.HasValue&&!volumeTargetInFlight.HasValue&&result!=null&&result.OperationSucceeded)audioNeedsReconcile=false;
                    if((audioTargetInFlight!=null||microphoneTargetInFlight.HasValue||volumeTargetInFlight.HasValue)&&result!=null&&!result.OperationSucceeded){audioActionFailure=result.Error??"音频操作未确认";audioFailureAt=DateTime.UtcNow;}
                    microphoneTargetInFlight=null;audioTargetInFlight=null;volumeTargetInFlight=null;audioCheckedUtc=DateTime.UtcNow;
                    if(result!=null&&(!String.IsNullOrEmpty(result.ConsoleOutputId)||Audio==null))Audio=result;
                    if(audioEvents!=null&&Audio!=null)audioEvents.SetMicrophoneTarget(Audio.MicrophoneId);
                    UpdateIcon();RunPendingAudio();
                });}catch(ObjectDisposedException){}catch(InvalidOperationException){}
            });
        }
        public void SelectAudioOutput(string target) {
            if(writeOutput==null||audioNeedsReconcile||target!="speaker"&&target!="headset")return;
            if(Audio==null||target=="speaker"&&!Audio.SpeakerAvailable||target=="headset"&&!Audio.HeadsetAvailable)return;
            if(target==pendingAudioOutput||target==audioTargetInFlight&&pendingAudioOutput==null)return;
            audioActionFailure=null;actionRequestedAt=DateTime.UtcNow;pendingAudioOutput=target;RunPendingAudio();
            if(details!=null&&!details.IsDisposed)details.Invalidate();
        }
        public void ToggleMicrophoneMute() {
            if(writeMute==null||audioNeedsReconcile||Audio==null||!Audio.MicrophoneMuted.HasValue)return;
            audioActionFailure=null;actionRequestedAt=DateTime.UtcNow;pendingMicrophoneMute=!(pendingMicrophoneMute??microphoneTargetInFlight??Audio.MicrophoneMuted.Value);RunPendingAudio();
            if(details!=null&&!details.IsDisposed)details.Invalidate();
        }
        public void SelectOutputVolume(string endpoint,int percent) {
            if(writeVolume==null||audioNeedsReconcile||Audio==null||String.IsNullOrEmpty(endpoint)||percent<0||percent>100)return;
            audioActionFailure=null;pendingVolume=percent;pendingVolumeEndpoint=endpoint;RunPendingAudio();
            if(details!=null&&!details.IsDisposed)details.Invalidate();
        }
        void RunPendingAudio() {
            if(disposed||audioBusy!=0)return;
            if(audioNeedsReconcile){if(pendingAudioRefresh){pendingAudioRefresh=false;RunAudioOperation(readAudio);}return;}
            if(pendingAudioOutput!=null) {
                string target=pendingAudioOutput;pendingAudioOutput=null;audioTargetInFlight=target;
                RunAudioOperation(delegate {return writeOutput(target);});
            } else if(pendingMicrophoneMute.HasValue) {
                bool mute=pendingMicrophoneMute.Value;pendingMicrophoneMute=null;
                microphoneTargetInFlight=mute;
                RunAudioOperation(delegate {return writeMute(mute);});
            } else if(pendingVolume.HasValue) {
                int value=pendingVolume.Value;string endpoint=pendingVolumeEndpoint;pendingVolume=null;pendingVolumeEndpoint=null;volumeTargetInFlight=value;
                RunAudioOperation(delegate {return writeVolume(endpoint,value);});
            } else if(pendingAudioRefresh) {
                pendingAudioRefresh=false;RunAudioOperation(readAudio);
            }
        }
        void BuildAudioSettingsMenu() {
            while(audioSettingsMenu.DropDownItems.Count>0)audioSettingsMenu.DropDownItems[0].Dispose();
            AudioBindings bindings;
            try {bindings=DeviceSettings.Current;}
            catch(Exception e){bindings=new AudioBindings();audioSettingsMenu.DropDownItems.Add(new ToolStripMenuItem("无法读取设置："+e.Message){Enabled=false});}
            foreach(string roleValue in new[]{"speaker","headset","microphone"}) {
                string role=roleValue;
                var group=new ToolStripMenuItem(role=="speaker"?"扬声器按钮":role=="headset"?"耳机按钮":"麦克风按钮");
                string selected=bindings.EndpointFor(role);
                var automatic=new ToolStripMenuItem("自动识别（默认）"){Checked=String.IsNullOrEmpty(selected)};
                automatic.Click+=delegate {SaveAudioEndpoint(role,null);};group.DropDownItems.Add(automatic);
                group.DropDownItems.Add(new ToolStripSeparator());
                int count=0;bool selectedFound=false;
                if(Audio!=null&&Audio.Devices!=null)foreach(var device in Audio.Devices) {
                    if(device.Flow!=(role=="microphone"?1:0))continue;
                    string id=device.Id;
                    string label=String.IsNullOrWhiteSpace(device.Name)?"未命名设备":device.Name;
                    bool isSelected=String.Equals(id,selected,StringComparison.OrdinalIgnoreCase);
                    var item=new ToolStripMenuItem(label.Replace("&","&&")){Checked=isSelected,ToolTipText=device.InterfaceName??label};
                    item.Click+=delegate {SaveAudioEndpoint(role,id);};group.DropDownItems.Add(item);
                    count++;selectedFound|=isSelected;
                }
                if(count==0)group.DropDownItems.Add(new ToolStripMenuItem("未发现设备，请刷新列表"){Enabled=false});
                if(!String.IsNullOrEmpty(selected)&&!selectedFound)group.DropDownItems.Add(new ToolStripMenuItem("当前绑定设备未连接"){Enabled=false,Checked=true});
                audioSettingsMenu.DropDownItems.Add(group);
            }
            audioSettingsMenu.DropDownItems.Add(new ToolStripSeparator());
            var refreshDevices=new ToolStripMenuItem("刷新设备列表");refreshDevices.Click+=delegate {RequestAudioRead(true);};audioSettingsMenu.DropDownItems.Add(refreshDevices);
            var reset=new ToolStripMenuItem("恢复默认音源识别");reset.Click+=delegate {
                try {DeviceSettings.ResetAudio();RequestAudioRead(true);}
                catch(Exception e){MessageBox.Show("无法保存音源设置："+e.Message,"设备电量",MessageBoxButtons.OK,MessageBoxIcon.Error);}
            };audioSettingsMenu.DropDownItems.Add(reset);
        }
        void SaveAudioEndpoint(string role,string id) {
            try {DeviceSettings.SetEndpoint(role,id);RequestAudioRead(true);}
            catch(Exception e){MessageBox.Show("无法保存音源设置："+e.Message,"设备电量",MessageBoxButtons.OK,MessageBoxIcon.Error);}
        }
        void RequestRead(bool includeBattery,bool queueWhenBusy) {
            if(persistStatus&&!NativeMousePresent)return;
            if(disposed||busy!=0&&!queueWhenBusy)return;
            pendingRead=true;pendingBattery|=includeBattery;RunNext();
        }
        public void SelectDpiStage(int stage) {
            if(disposed||Dpi==null||Dpi.Stages==null||stage<0||stage>=Dpi.Stages.Count)return;
            if(UiState.SkipConfirmedStage(Dpi,DpiError,stage,switching||pendingStage>=0,DateTime.UtcNow))return;
            if(pendingStage==stage||stageInFlight==stage&&pendingStage<0)return;
            dpiActionFailure=null;actionRequestedAt=DateTime.UtcNow;
            pendingStage=stage;RunNext();if(details!=null&&!details.IsDisposed)details.Invalidate();SaveStatus();
        }
        void RunNext() {
            if(disposed||busy!=0||pendingStage<0&&!pendingRead)return;
            int stage=pendingStage;bool includeBattery=pendingBattery;
            if(stage>=0)pendingStage=-1;
            else {pendingRead=false;pendingBattery=false;}
            busy=1;switching=stage>=0;stageInFlight=stage;
            if(details!=null&&!details.IsDisposed)details.Invalidate();SaveStatus();
            ThreadPool.QueueUserWorkItem(delegate {
                MouseSnapshot snapshot=null;DpiConfig switched=null;string failure=null;
                try {if(stage>=0)switched=selectStage(stage);else snapshot=readSnapshot(includeBattery);}catch(Exception e){failure=e.Message;}
                if(disposed)return;
                try {dispatch.BeginInvoke((MethodInvoker)delegate {
                    if(disposed)return;
                    if(stage>=0) {
                        if(switched!=null){Dpi=switched;DpiError=switched.RefreshError??"";completedSwitches++;}
                        else {DpiError=failure??"DPI switch did not return a verified result";dpiActionFailure=DpiError;dpiFailureAt=DateTime.UtcNow;}
                        pendingRead=true;pendingBattery=true;
                    } else {
                        completedReads++;
                        if(snapshot!=null) {
                            if(includeBattery){Current=snapshot.Battery;ErrorMessage=snapshot.BatteryError;if(Current!=null)LastGood=Current;}
                            if(snapshot.Dpi!=null){Dpi=snapshot.Dpi;DpiError=snapshot.DpiError??"";}else DpiError=snapshot.DpiError??failure??"DPI read did not return a verified result";
                        } else {if(includeBattery){Current=null;ErrorMessage=failure;}DpiError=failure;}
                    }
                    busy=0;switching=false;stageInFlight=-1;lastSyncUtc=DateTime.UtcNow;UpdateIcon();RunNext();
                });}catch(ObjectDisposedException){}catch(InvalidOperationException){}
            });
        }
        protected override void ExitThreadCore() {
            disposed=true;refresh.Stop();refresh.Dispose();liveSync.Stop();liveSync.Dispose();deviceDebounce.Stop();deviceDebounce.Dispose();expiryTimer.Dispose();audioDebounce.Dispose();operationTimer.Dispose();if(deviceEvents!=null)deviceEvents.Dispose();if(audioEvents!=null){var listener=audioEvents;ThreadPool.QueueUserWorkItem(delegate{listener.Dispose();});}tray.Visible=false;
            if(genericWorker!=null)genericWorker.Dispose();
            if(persistStatus)AudioBrokerClient.Shutdown();fileWriter.Dispose();
            if(details!=null)details.Close();tray.Dispose();if(icon!=null)icon.Dispose();dispatch.Dispose();base.ExitThreadCore();
        }
    }
    static class Program {
        [STAThread] static void Main(string[] args) {
            if(args.Length==1&&args[0]=="--audio-broker"){AudioBrokerClient.RunServer();return;}
            if(args.Length==1&&args[0]=="--exit"){using(var stop=new ShowPanelSignal("Local\\MyFinderDeviceBattery.Exit"))stop.Request();return;}
            if(args.Length==1&&args[0]=="--package-smoke"){
                using(var output=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false)) {AutoFlush=true})try{using(var worker=new GenericBatteryWorker(null,true)){var snapshot=worker.Poll(true);bool ok=String.IsNullOrEmpty(snapshot.Error)&&snapshot.ProviderCount==26;output.WriteLine("{\"schema\":1,\"packageSmoke\":true,\"version\":\""+AppRuntime.Version+"\",\"providerCount\":"+snapshot.ProviderCount+",\"error\":\""+(ok?"":"package_reader_failed")+"\"}");Environment.ExitCode=ok?0:1;}}catch(Exception){output.WriteLine("{\"schema\":1,\"packageSmoke\":true,\"error\":\"package_smoke_failed\"}");Environment.ExitCode=1;}return;
            }
            NativeDisplay.EnableDpiAwareness();
            if(args.Length==2 && args[0]=="--preview") {
                Directory.CreateDirectory(args[1]);
                using(var b=new Bitmap(900,430))
                using(var g=Graphics.FromImage(b)) {
                    g.Clear(Color.FromArgb(130,133,141));
                    using(var bar=new SolidBrush(Color.FromArgb(245,246,248)))g.FillRectangle(bar,0,0,b.Width,55);
                    using(var line=new Pen(Color.FromArgb(225,226,229),1))g.DrawLine(line,0,54,b.Width,54);
                    Style.Text(g,"Finder 菜单栏常态（只占小图标位）",13,FontStyle.Regular,Color.FromArgb(100,101,106),new RectangleF(24,8,420,36),StringAlignment.Near);
                    using(var trayPreview=TrayGlyph.Render(72,false,false,false,32))g.DrawImage(trayPreview,new RectangleF(820,14,27,27));
                    Style.Text(g,"点击后展开",12,FontStyle.Regular,Color.FromArgb(100,101,106),new RectangleF(670,8,135,36),StringAlignment.Far);
                    using(var shadow=new SolidBrush(Color.FromArgb(50,0,0,0)))g.FillRectangle(shadow,559,83,VisualDesign.PanelSize.Width,VisualDesign.PanelSize.Height);
                    g.TranslateTransform(548,72);Details.PaintPanel(g,null,null,null,false,null,null,false,false);g.ResetTransform();
                    Style.Text(g,"点击空白区域后动画缩回",13,FontStyle.Regular,Color.White,new RectangleF(560,350,280,28),StringAlignment.Center);
                    b.Save(Path.Combine(args[1],"appearance-preview.png"),ImageFormat.Png);
                }
                return;
            }
            bool fresh;using(var signal=new ShowPanelSignal())using(var stop=new ShowPanelSignal("Local\\MyFinderDeviceBattery.Exit"))using(var mutex=new Mutex(true,"Local\\ATK-A9-Air-Battery-Tray",out fresh)) {
                if(!fresh){if(args.Length==1&&args[0]=="--show")signal.Request();return;}
                Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
                var context=new TrayContext(args.Length==1&&args[0]=="--show");context.ListenForShowRequest(signal);context.ListenForExitRequest(stop);Application.Run(context);
            }
        }
    }
}
