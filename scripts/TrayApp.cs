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
        public static bool Dark {
            get {
                using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return key!=null && Convert.ToInt32(key.GetValue("SystemUsesLightTheme",1))==0;
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
        public static Bitmap Tray(int? percent,bool charge,bool dark,bool stale) {
            return TrayGlyph.Render(percent,charge,dark,stale,SystemInformation.SmallIconSize.Width);
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
        int hoverAudio=-1;
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
            Deactivate+=delegate {if(!ignoreDeactivate&&!closing)CloseAnimated();};
            MouseUp+=delegate(object sender,MouseEventArgs e) {
                if(e.Button!=MouseButtons.Left||closing)return;
                var point=PanelPoint(e.Location);
                keyboardFocus=-1;
                if(VisualDesign.SettingsRect.Contains(point)){owner.ShowSettings();return;}
                if(VisualDesign.RefreshRect.Contains(point))owner.RefreshBattery();
                else {
                    int audio=AudioAtPoint(point);int slot=StageAtPoint(point,owner.Dpi);
                    if(audio==0)owner.SelectAudioOutput("speaker");
                    else if(audio==1)owner.SelectAudioOutput("headset");
                    else if(audio==2)owner.ToggleMicrophoneMute();
                    else if(slot>=0)owner.SelectDpiStage(slot);
                }
                Invalidate();
            };
            MouseMove+=delegate(object sender,MouseEventArgs e) {
                var point=PanelPoint(e.Location);int nextStage=StageAtPoint(point,owner.Dpi);bool nextRefresh=VisualDesign.RefreshRect.Contains(point),nextSettings=VisualDesign.SettingsRect.Contains(point);int nextAudio=AudioAtPoint(point);
                if(nextStage!=hoverStage||nextRefresh!=hoverRefresh||nextAudio!=hoverAudio||nextSettings!=hoverSettings){
                    hoverStage=nextStage;hoverRefresh=nextRefresh;hoverAudio=nextAudio;hoverSettings=nextSettings;
                    string audioTip=nextAudio==2?"切换麦克风静音："+(displayAudio==null?"":displayAudio.MicrophoneName)
                        :nextAudio==0?"输出到："+(displayAudio==null?"":displayAudio.SpeakerName)
                        :nextAudio==1?"输出到："+(displayAudio==null?"":displayAudio.HeadsetName):null;
                    if(audioTip!=null&&displayAudio!=null&&!String.IsNullOrEmpty(displayAudio.Error))audioTip+="\n"+displayAudio.Error;
                    controlTip.SetToolTip(this,nextSettings?"设置：音源绑定、托盘电量和提醒":nextRefresh?"刷新状态":nextStage>=0&&!String.IsNullOrEmpty(owner.DpiError)?owner.DpiError:audioTip);Invalidate();
                }
                Cursor=nextStage>=0||nextRefresh||nextSettings||nextAudio>=0?Cursors.Hand:Cursors.Default;
            };
            MouseLeave+=delegate {hoverStage=-1;hoverRefresh=false;hoverSettings=false;hoverAudio=-1;controlTip.SetToolTip(this,null);Cursor=Cursors.Default;Invalidate();};
        }
        PointF PanelPoint(Point point) {return UiState.LogicalPoint(point,dpiScale);}
        protected override bool ProcessCmdKey(ref Message message,Keys keys) {
            if(keys==Keys.Escape){CloseAnimated();return true;}
            var targets=new List<int>{0,1};
            if(owner.Dpi!=null&&owner.Dpi.Stages!=null)for(int i=0;i<owner.Dpi.Stages.Count;i++)targets.Add(10+i);
            if(headsetProgress>.98f&&displayAudio!=null){if(displayAudio.SpeakerAvailable)targets.Add(100);if(displayAudio.HeadsetAvailable)targets.Add(101);if(displayAudio.MicrophoneAvailable&&displayAudio.MicrophoneMuted.HasValue)targets.Add(102);}
            if(keys==Keys.Tab||keys==(Keys.Tab|Keys.Shift)||keys==Keys.Right||keys==Keys.Left) {
                int direction=keys==(Keys.Tab|Keys.Shift)||keys==Keys.Left?-1:1,index=targets.IndexOf(keyboardFocus);
                keyboardFocus=targets[(index<0?(direction>0?0:targets.Count-1):(index+direction+targets.Count)%targets.Count)];Invalidate();return true;
            }
            if(keys==Keys.Enter||keys==Keys.Space) {
                if(keyboardFocus==0)owner.RefreshBattery();else if(keyboardFocus==1)owner.ShowSettings();
                else if(keyboardFocus>=100){if(keyboardFocus==102)owner.ToggleMicrophoneMute();else owner.SelectAudioOutput(keyboardFocus==100?"speaker":"headset");}
                else if(keyboardFocus>=10)owner.SelectDpiStage(keyboardFocus-10);
                return true;
            }
            return base.ProcessCmdKey(ref message,keys);
        }
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
            bool visible=TrayContext.ShowHeadset(owner.Headset,owner.Audio);
            if(visible||headsetProgress==0f){displayHeadset=owner.Headset;displayAudio=owner.Audio;}
            float next=visible?1f:0f;
            if(next!=layoutTarget) {
                layoutStart=headsetProgress;layoutTarget=next;layoutClock.Restart();layoutAnimation.Start();
            } else UpdateLayoutSize();
            Invalidate();
        }
        void UpdateLayoutSize() {
            Size mouse=VisualDesign.PanelSizeFor(false,owner.Dpi),both=VisualDesign.PanelSizeFor(true,owner.Dpi);
            targetSize=new Size(mouse.Width,mouse.Height+(int)Math.Round((both.Height-mouse.Height)*headsetProgress));
            Size physical=UiState.ScaleSize(targetSize,dpiScale);
            if(ClientSize!=physical) {
                ClientSize=physical;
                if(!layeredRendering)UpdateFallbackRegion();
            }
            PositionForSize((int)Math.Round((1-animationProgress)*6));
        }
        void UpdateFallbackRegion() {
            Size mouse=VisualDesign.PanelSizeFor(false,owner.Dpi);
            Region mask;
            using(var shape=Style.Round(new RectangleF(0,0,targetSize.Width,mouse.Height),VisualDesign.PanelRadius))mask=new Region(shape);
            int headsetTop=mouse.Height+8;
            if(targetSize.Height>headsetTop) {
                float height=targetSize.Height-headsetTop;
                using(var shape=Style.Round(new RectangleF(0,headsetTop,targetSize.Width,height),Math.Min(VisualDesign.PanelRadius,height/2f)))mask.Union(shape);
            }
            using(var transform=new Matrix()){transform.Scale(dpiScale,dpiScale);mask.Transform(transform);}
            var previous=Region;Region=mask;if(previous!=null)previous.Dispose();
        }
        void AdvanceLayout() {
            float elapsed=Math.Min(1f,(float)layoutClock.Elapsed.TotalMilliseconds/190f);
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
            dpiScale=NativeDisplay.ScaleAt(anchor);UpdateLayoutSize();closing=false;ignoreDeactivate=true;animationProgress=0;
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
            if(closing){closing=false;owner.PanelOpened();AnimateTo(1f,190f);}
            Show();Activate();
        }
        public void CloseAnimated() {
            if(closing)return;closing=true;owner.PanelClosed();AnimateTo(0f,135f);
        }
        public void ToggleAnimated() {
            if(closing) { closing=false;owner.PanelOpened();AnimateTo(1f,190f); }
            else CloseAnimated();
        }
        void AnimateTo(float target,float durationMs) {
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
            if(disposing){owner.PanelClosed();animation.Stop();animation.Dispose();layoutAnimation.Stop();layoutAnimation.Dispose();controlTip.Dispose();}
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
            float sx=ClientSize.Width/(float)targetSize.Width,sy=ClientSize.Height/(float)targetSize.Height;
            var saved=e.Graphics.Save();e.Graphics.ScaleTransform(sx,sy);
            VisualDesign.PaintDevicesPanel(e.Graphics,owner.Current,owner.LastGood,owner.ErrorMessage,owner.IsRefreshing,owner.Dpi,owner.DpiError,owner.IsDpiRefreshing,Style.Dark,displayHeadset,displayAudio,animationProgress,hoverStage,hoverRefresh,headsetProgress,hoverAudio,Feedback(),hoverSettings);
            e.Graphics.Restore(saved);
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
            visible.Append('|').Append(UiState.Mouse(owner.Current,owner.LastGood,now).Key).Append('|').Append(UiState.Headset(displayHeadset,now).Key);
            if(displayAudio!=null)visible.Append('|').Append(displayAudio.MicrophoneAvailable);
            string key=visible.ToString();
            if(key==lastPresentedKey)return true;
            using(var bitmap=new Bitmap(ClientSize.Width,ClientSize.Height,PixelFormat.Format32bppPArgb)) {
                using(var g=Graphics.FromImage(bitmap)) {
                    g.Clear(Color.Transparent);g.ScaleTransform(ClientSize.Width/(float)targetSize.Width,ClientSize.Height/(float)targetSize.Height);
                    VisualDesign.PaintDevicesPanel(g,owner.Current,owner.LastGood,owner.ErrorMessage,owner.IsRefreshing,owner.Dpi,owner.DpiError,owner.IsDpiRefreshing,Style.Dark,displayHeadset,displayAudio,animationProgress,hoverStage,hoverRefresh,headsetProgress,hoverAudio,feedback,hoverSettings);
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
        PanelFeedback Feedback(){var value=owner.GetFeedback();value.KeyboardFocus=keyboardFocus;return value;}
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
        readonly ToolStripMenuItem audioSettingsMenu=new ToolStripMenuItem("音源设置");
        readonly System.Windows.Forms.Timer deviceDebounce=new System.Windows.Forms.Timer();
        readonly LowBatteryPolicy lowBattery=new LowBatteryPolicy();
        readonly bool persistStatus;
        DeviceEventsWindow deviceEvents;
        SettingsForm settingsForm;
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
        public TrayContext(bool showPanel=false) : this(showPanel,ReadWithRetry,MouseBattery.SetActiveDpiStage,HeadsetBattery.Read,AudioControl.Read,true,AudioControl.SwitchOutput,AudioControl.SetHeadsetMicrophoneMute) {
            deviceDebounce.Interval=1200;deviceDebounce.Tick+=delegate {deviceDebounce.Stop();RefreshBattery(false);};
            deviceEvents=new DeviceEventsWindow(delegate {if(!disposed){deviceDebounce.Stop();deviceDebounce.Start();}});
            try {string path=Path.Combine(DeviceSettings.DataDirectory,"low-battery-state.txt");if(File.Exists(path)){string text=File.ReadAllText(path);lowBattery.Restore("mouse",text.Contains("mouse=1"));lowBattery.Restore("headset",text.Contains("headset=1"));}}catch(IOException){}catch(UnauthorizedAccessException){}
        }
        internal TrayContext(bool showPanel,Func<bool,MouseSnapshot> read,Func<int,DpiConfig> select) : this(showPanel,read,select,null,null) {}
        internal TrayContext(bool showPanel,Func<bool,MouseSnapshot> read,Func<int,DpiConfig> select,Func<HeadsetReading> headsetReader,Func<AudioReading> audioReader,bool diagnostics=false,Func<string,AudioReading> outputWriter=null,Func<bool,AudioReading> muteWriter=null) {
            persistStatus=diagnostics;
            readSnapshot=read;selectStage=select;readHeadset=headsetReader;readAudio=audioReader;writeOutput=outputWriter;writeMute=muteWriter;
            var unused=dispatch.Handle;
            var menu=new ContextMenuStrip();
            var detail=new ToolStripMenuItem("设备电量与音频");detail.Click+=delegate {ShowDetails();};menu.Items.Add(detail);
            var update=new ToolStripMenuItem("\u5237\u65b0");update.Click+=delegate {RefreshBattery();};menu.Items.Add(update);
            menu.Items.Add(audioSettingsMenu);menu.Opening+=delegate {BuildAudioSettingsMenu();};
            var settingsItem=new ToolStripMenuItem("设置…");settingsItem.Click+=delegate {ShowSettings();};menu.Items.Add(settingsItem);
            menu.Items.Add(new ToolStripSeparator());
            var exit=new ToolStripMenuItem("\u9000\u51fa");exit.Click+=delegate {ExitThread();};menu.Items.Add(exit);
            tray.ContextMenuStrip=menu;tray.MouseClick+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left)ShowDetails();};
            UpdateIcon();tray.Visible=diagnostics;
            refresh.Interval=60000;refresh.Tick+=delegate {if(!panelOpen){RequestRead(true,false);RequestHeadsetRead(false);RequestAudioRead(false);}};refresh.Start();
            liveSync.Interval=2000;liveSync.Tick+=delegate {
                if(!panelOpen||details==null||details.IsDisposed||!details.Visible){liveSync.Stop();return;}
                bool batteryDue=Current==null||(DateTime.UtcNow-Current.TimestampUtc).TotalSeconds>=30;
                RequestRead(batteryDue,false);
                RequestHeadsetRead(false);RequestAudioRead(false);
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
        public void ShowSettings() {
            if(settingsForm!=null&&!settingsForm.IsDisposed){settingsForm.Activate();return;}
            if(details!=null&&!details.IsDisposed)details.CloseAnimated();
            settingsForm=new SettingsForm(this);settingsForm.Show();
            RequestAudioRead(true);
        }
        public void RefreshAudioDevices(){RequestAudioRead(true);}
        public void ConfigurationChanged(){UpdateIcon();RequestAudioRead(true);}
        public PanelFeedback GetFeedback() {
            var now=DateTime.UtcNow;
            return new PanelFeedback {PendingStage=pendingStage>=0?pendingStage:stageInFlight,
                PendingOutput=pendingAudioOutput??audioTargetInFlight,MicrophonePending=pendingMicrophoneMute.HasValue||microphoneTargetInFlight.HasValue,
                DpiFailure=(now-dpiFailureAt).TotalSeconds<10?dpiActionFailure:null,
                AudioFailure=(now-audioFailureAt).TotalSeconds<10?audioActionFailure:null,
                ManualRefreshing=manualRefresh&&(busy!=0||headsetBusy!=0||audioBusy!=0||pendingRead)};
        }
        public void PanelOpened() {
            panelOpen=true;liveSync.Start();RefreshBattery(false);
        }
        public void PanelClosed() {
            if(disposed)return;panelOpen=false;liveSync.Stop();SaveStatus();
        }
        void UpdateIcon() {
            if(busy==0&&headsetBusy==0&&audioBusy==0&&!pendingRead)manualRefresh=false;
            AudioBindings preferences;try{preferences=DeviceSettings.Current;}catch{preferences=new AudioBindings();}
            DateTime now=DateTime.UtcNow;var mouse=UiState.Mouse(Current,LastGood,now);var headset=UiState.Headset(Headset,now);
            var presentation=UiState.Tray(preferences.TraySource,mouse,headset);var shown=presentation.Battery;
            bool stale=shown.Percent.HasValue&&!shown.Fresh;
            bool dark=Style.Dark;string key=shown.Key+"|"+dark+"|"+SystemInformation.SmallIconSize.Width;
            if(key!=lastIconKey) {
                using(var bitmap=Style.Tray(shown.Percent,shown.Charging,dark,stale)) {
                    IntPtr handle=bitmap.GetHicon();var next=(Icon)Icon.FromHandle(handle).Clone();DestroyIcon(handle);
                    var old=icon;icon=next;tray.Icon=icon;if(old!=null)old.Dispose();
                }
                lastIconKey=key;iconUpdates++;
            }
            string tooltip=presentation.Tooltip;
            if(tooltip!=lastTooltip){tray.Text=tooltip;lastTooltip=tooltip;}
            if(details!=null&&!details.IsDisposed)details.SyncDevices();
            if(persistStatus&&preferences.LowBatteryNotifications)EvaluateLowBattery(mouse,headset);
            SaveStatus();
        }
        void EvaluateLowBattery(BatteryView mouse,BatteryView headset) {
            bool oldMouse=lowBattery.IsLatched("mouse"),oldHeadset=lowBattery.IsLatched("headset");
            bool allowed=NativeDisplay.MayNotify();var messages=new List<string>();
            if(lowBattery.Observe("mouse",mouse,allowed))messages.Add("鼠标剩余 "+mouse.Percent+"%");
            if(lowBattery.Observe("headset",headset,allowed))messages.Add("耳机剩余 "+headset.Percent+"%");
            if(oldMouse!=lowBattery.IsLatched("mouse")||oldHeadset!=lowBattery.IsLatched("headset"))try {
                Directory.CreateDirectory(DeviceSettings.DataDirectory);
                File.WriteAllText(Path.Combine(DeviceSettings.DataDirectory,"low-battery-state.txt"),"mouse="+(lowBattery.IsLatched("mouse")?1:0)+"\nheadset="+(lowBattery.IsLatched("headset")?1:0));
            }catch(IOException){}catch(UnauthorizedAccessException){}
            if(messages.Count>0)tray.ShowBalloonTip(5000,"外设电量较低",String.Join(" · ",messages.ToArray()),ToolTipIcon.Warning);
        }
        void SaveStatus() {
            if(!persistStatus)return;
            try {
                Directory.CreateDirectory(DeviceSettings.DataDirectory);
                string path=Path.Combine(DeviceSettings.DataDirectory,"status.json");
                string safe=Escape(ErrorMessage);
                var stages=new StringBuilder("[");
                if(Dpi!=null&&Dpi.Stages!=null)for(int i=0;i<Dpi.Stages.Count;i++) {
                    if(i>0)stages.Append(',');stages.Append("{\"x\":").Append(Dpi.Stages[i].XDpi).Append(",\"y\":").Append(Dpi.Stages[i].YDpi).Append('}');
                }
                stages.Append(']');
                string json="{\"liveVerified\":"+(Current!=null?"true":"false")+
                    ",\"percent\":"+(Current!=null?Current.Percent.ToString():"null")+
                    ",\"charging\":"+(Current!=null?Current.Charging.ToString().ToLower():"null")+
                    ",\"dpiLiveVerified\":"+(Dpi!=null&&String.IsNullOrEmpty(DpiError)?"true":"false")+
                    ",\"dpi\":"+(Dpi!=null&&Dpi.Stages!=null&&Dpi.ActiveStage>=0&&Dpi.ActiveStage<Dpi.Stages.Count?Dpi.Stages[Dpi.ActiveStage].XDpi.ToString():"null")+
                    ",\"activeStage\":"+(Dpi!=null?Dpi.ActiveStage.ToString():"null")+
                    ",\"stages\":"+stages+
                    ",\"panelOpen\":"+panelOpen.ToString().ToLower()+",\"syncIntervalMs\":"+(panelOpen?"2000":"60000")+
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
                    ",\"microphoneMuted\":"+(Audio!=null&&Audio.MicrophoneMuted.HasValue?Audio.MicrophoneMuted.Value.ToString().ToLower():"null")+
                    ",\"error\":\""+Escape(Audio==null?"":Audio.Error)+"\"}}";
                File.WriteAllText(path,json,new UTF8Encoding(false));
            } catch(IOException) {}catch(UnauthorizedAccessException){}
        }
        static string Escape(string value) {return (value??"").Replace("\\","\\\\").Replace("\"","\\\"").Replace("\r"," ").Replace("\n"," ");}
        public void RefreshBattery(bool manual=true) {
            manualRefresh|=manual;
            RequestRead(true,true);
            RequestHeadsetRead(true);RequestAudioRead(true);
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
                        if(result.ReceiverPresent&&lastKnownHeadset!=null) {
                            result.ConnectionTimestampUtc=lastKnownHeadset.ConnectionTimestampUtc;
                            result.Connected=lastKnownHeadset.Connected&&(DateTime.UtcNow-lastKnownHeadset.ConnectionTimestampUtc).TotalSeconds<120;
                            if(result.Connected){result.Percent=lastKnownHeadset.Percent;result.BatteryTimestampUtc=lastKnownHeadset.BatteryTimestampUtc;}
                        }
                        if(!result.ReceiverPresent)lastKnownHeadset=null;
                    }
                    Headset=result;UpdateIcon();if(pendingHeadsetRefresh){pendingHeadsetRefresh=false;RequestHeadsetRead(true);}
                });}catch(ObjectDisposedException){}catch(InvalidOperationException){}
            });
        }
        void RequestAudioRead(bool force) {
            if(disposed||readAudio==null)return;
            if(audioBusy!=0){if(force)pendingAudioRefresh=true;return;}
            if(!force&&(DateTime.UtcNow-audioCheckedUtc).TotalSeconds<(panelOpen?2:60))return;
            RunAudioOperation(readAudio);
        }
        void RunAudioOperation(Func<AudioReading> operation) {
            if(disposed||audioBusy!=0)return;audioBusy=1;
            if(details!=null&&!details.IsDisposed)details.Invalidate();
            ThreadPool.QueueUserWorkItem(delegate {
                AudioReading result;try {result=operation();}catch(Exception e){result=new AudioReading {Error=e.Message};}
                if(disposed)return;
                try {dispatch.BeginInvoke((MethodInvoker)delegate {
                    if(disposed)return;audioBusy=0;
                    if((audioTargetInFlight!=null||microphoneTargetInFlight.HasValue)&&result!=null&&!result.OperationSucceeded){audioActionFailure=result.Error??"音频操作未确认";audioFailureAt=DateTime.UtcNow;}
                    microphoneTargetInFlight=null;audioTargetInFlight=null;audioCheckedUtc=DateTime.UtcNow;Audio=result;
                    if(settingsForm!=null&&!settingsForm.IsDisposed)settingsForm.AudioUpdated();
                    UpdateIcon();RunPendingAudio();
                });}catch(ObjectDisposedException){}catch(InvalidOperationException){}
            });
        }
        public void SelectAudioOutput(string target) {
            if(writeOutput==null||target!="speaker"&&target!="headset")return;
            if(Audio==null||target=="speaker"&&!Audio.SpeakerAvailable||target=="headset"&&!Audio.HeadsetAvailable)return;
            audioActionFailure=null;pendingAudioOutput=target;RunPendingAudio();
            if(details!=null&&!details.IsDisposed)details.Invalidate();
        }
        public void ToggleMicrophoneMute() {
            if(writeMute==null||Audio==null||!Audio.MicrophoneMuted.HasValue)return;
            audioActionFailure=null;pendingMicrophoneMute=!(pendingMicrophoneMute??microphoneTargetInFlight??Audio.MicrophoneMuted.Value);RunPendingAudio();
            if(details!=null&&!details.IsDisposed)details.Invalidate();
        }
        void RunPendingAudio() {
            if(disposed||audioBusy!=0)return;
            if(pendingAudioOutput!=null) {
                string target=pendingAudioOutput;pendingAudioOutput=null;audioTargetInFlight=target;
                RunAudioOperation(delegate {return writeOutput(target);});
            } else if(pendingMicrophoneMute.HasValue) {
                bool mute=pendingMicrophoneMute.Value;pendingMicrophoneMute=null;
                microphoneTargetInFlight=mute;
                RunAudioOperation(delegate {return writeMute(mute);});
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
            if(disposed||busy!=0&&!queueWhenBusy)return;
            pendingRead=true;pendingBattery|=includeBattery;RunNext();
        }
        public void SelectDpiStage(int stage) {
            if(disposed||Dpi==null||Dpi.Stages==null||stage<0||stage>=Dpi.Stages.Count)return;
            if(UiState.SkipConfirmedStage(Dpi,DpiError,stage,switching||pendingStage>=0,DateTime.UtcNow))return;
            dpiActionFailure=null;
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
            disposed=true;refresh.Stop();refresh.Dispose();liveSync.Stop();liveSync.Dispose();deviceDebounce.Stop();deviceDebounce.Dispose();if(deviceEvents!=null)deviceEvents.Dispose();tray.Visible=false;
            if(settingsForm!=null)settingsForm.Close();
            if(details!=null)details.Close();tray.Dispose();if(icon!=null)icon.Dispose();dispatch.Dispose();base.ExitThreadCore();
        }
    }
    static class Program {
        [STAThread] static void Main(string[] args) {
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
            bool fresh;using(var signal=new ShowPanelSignal())using(var mutex=new Mutex(true,"Local\\ATK-A9-Air-Battery-Tray",out fresh)) {
                if(!fresh){if(args.Length==1&&args[0]=="--show")signal.Request();return;}
                Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
                var context=new TrayContext(args.Length==1&&args[0]=="--show");context.ListenForShowRequest(signal);Application.Run(context);
            }
        }
    }
}
