using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
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
            owner=context;FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;StartPosition=FormStartPosition.Manual;
            Text="设备电量与音频控制";AccessibleName=Text;
            displayHeadset=owner.Headset;displayAudio=owner.Audio;
            headsetProgress=TrayContext.ShowHeadset(displayHeadset,displayAudio)?1f:0f;
            layoutTarget=headsetProgress;
            targetSize=VisualDesign.PanelSizeFor(headsetProgress>0f,owner.Dpi);
            ClientSize=targetSize;DoubleBuffered=true;Font=new Font("Segoe UI",9);
            animation.Interval=16;animation.Tick+=delegate {AdvanceAnimation();};
            layoutAnimation.Interval=16;layoutAnimation.Tick+=delegate {AdvanceLayout();};
            Deactivate+=delegate {if(!ignoreDeactivate&&!closing)CloseAnimated();};
            MouseUp+=delegate(object sender,MouseEventArgs e) {
                if(e.Button!=MouseButtons.Left||closing)return;
                var point=PanelPoint(e.Location);
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
                var point=PanelPoint(e.Location);int nextStage=StageAtPoint(point,owner.Dpi);bool nextRefresh=VisualDesign.RefreshRect.Contains(point);int nextAudio=AudioAtPoint(point);
                if(nextStage!=hoverStage||nextRefresh!=hoverRefresh||nextAudio!=hoverAudio){
                    hoverStage=nextStage;hoverRefresh=nextRefresh;hoverAudio=nextAudio;
                    controlTip.SetToolTip(this,nextRefresh?"刷新状态":nextAudio==2?"切换 HyperX 系统麦克风静音":nextAudio==0?"输出到 Realtek 扬声器":nextAudio==1?"输出到 HyperX 耳机":null);Invalidate();
                }
                Cursor=nextStage>=0||nextRefresh||nextAudio>=0?Cursors.Hand:Cursors.Default;
            };
            MouseLeave+=delegate {hoverStage=-1;hoverRefresh=false;hoverAudio=-1;controlTip.SetToolTip(this,null);Cursor=Cursors.Default;Invalidate();};
        }
        PointF PanelPoint(Point point) {return new PointF(point.X,point.Y);}
        int AudioAtPoint(PointF point) {
            if(headsetProgress<.98f||displayAudio==null)return -1;
            if(displayAudio.SpeakerAvailable&&VisualDesign.SpeakerOutputRectFor(owner.Dpi).Contains(point))return 0;
            if(displayAudio.HeadsetAvailable&&VisualDesign.HeadsetOutputRectFor(owner.Dpi).Contains(point))return 1;
            if(displayAudio.MicrophoneMuted.HasValue&&VisualDesign.MicrophoneMuteRectFor(owner.Dpi).Contains(point))return 2;
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
            if(ClientSize!=targetSize) {
                ClientSize=targetSize;
                if(!layeredRendering)UpdateFallbackRegion();
            }
            PositionForSize((int)Math.Round((1-animationProgress)*6));
        }
        void UpdateFallbackRegion() {
            Size mouse=VisualDesign.PanelSizeFor(false,owner.Dpi);
            Region mask;
            using(var shape=Style.Round(new RectangleF(0,0,Width,mouse.Height),VisualDesign.PanelRadius))mask=new Region(shape);
            int headsetTop=mouse.Height+8;
            if(Height>headsetTop) {
                float height=Height-headsetTop;
                using(var shape=Style.Round(new RectangleF(0,headsetTop,Width,height),Math.Min(VisualDesign.PanelRadius,height/2f)))mask.Union(shape);
            }
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
        public void OpenAnimated() {
            anchor=Cursor.Position;closing=false;ignoreDeactivate=true;animationProgress=0;
            frameAlpha=3;PositionForSize(6);Show();Activate();owner.PanelOpened();
            BeginInvoke((MethodInvoker)delegate {ignoreDeactivate=false;});AnimateTo(1f,190f);
        }
        void PositionForSize(int slideY) {
            var screen=Screen.FromPoint(anchor).WorkingArea;
            int x=Math.Max(screen.Left+8,Math.Min(anchor.X-Width/2,screen.Right-Width-8));
            int y=anchor.Y<screen.Top+100?screen.Top+8:anchor.Y-Height-14;
            y+=slideY;
            y=Math.Max(screen.Top+8,Math.Min(y,screen.Bottom-Height-8));Location=new Point(x,y);
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
            VisualDesign.PaintDevicesPanel(e.Graphics,owner.Current,owner.LastGood,owner.ErrorMessage,owner.IsRefreshing,owner.Dpi,owner.DpiError,owner.IsDpiRefreshing,Style.Dark,displayHeadset,displayAudio,animationProgress,hoverStage,hoverRefresh,headsetProgress,hoverAudio);
            e.Graphics.Restore(saved);
        }
        bool RenderLayeredFrame() {
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
            string key=visible.ToString();
            if(key==lastPresentedKey)return true;
            using(var bitmap=new Bitmap(ClientSize.Width,ClientSize.Height,PixelFormat.Format32bppPArgb)) {
                using(var g=Graphics.FromImage(bitmap)) {
                    g.Clear(Color.Transparent);g.ScaleTransform(ClientSize.Width/(float)targetSize.Width,ClientSize.Height/(float)targetSize.Height);
                    VisualDesign.PaintDevicesPanel(g,owner.Current,owner.LastGood,owner.ErrorMessage,owner.IsRefreshing,owner.Dpi,owner.DpiError,owner.IsDpiRefreshing,Style.Dark,displayHeadset,displayAudio,animationProgress,hoverStage,hoverRefresh,headsetProgress,hoverAudio);
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
                    if(!renderPreviewSaved&&frameAlpha==255&&!layoutAnimation.Enabled&&owner.Current!=null&&owner.Dpi!=null) {
                        try {
                            bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"active-ui-render.png"),ImageFormat.Png);
                            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"visual-status.json"),"{\"layeredCompositor\":"+presented.ToString().ToLower()+",\"kind\":\"program-render\",\"percent\":"+owner.Current.Percent+",\"nativeTraySize\":"+SystemInformation.SmallIconSize.Width+"}");
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
        public TrayContext(bool showPanel=false) : this(showPanel,ReadWithRetry,MouseBattery.SetActiveDpiStage,HeadsetBattery.Read,AudioControl.Read) {}
        internal TrayContext(bool showPanel,Func<bool,MouseSnapshot> read,Func<int,DpiConfig> select) : this(showPanel,read,select,null,null) {}
        internal TrayContext(bool showPanel,Func<bool,MouseSnapshot> read,Func<int,DpiConfig> select,Func<HeadsetReading> headsetReader,Func<AudioReading> audioReader) {
            readSnapshot=read;selectStage=select;readHeadset=headsetReader;readAudio=audioReader;
            var unused=dispatch.Handle;
            var menu=new ContextMenuStrip();
            var detail=new ToolStripMenuItem("设备电量与音频");detail.Click+=delegate {ShowDetails();};menu.Items.Add(detail);
            var update=new ToolStripMenuItem("\u5237\u65b0");update.Click+=delegate {RefreshBattery();};menu.Items.Add(update);
            menu.Items.Add(new ToolStripSeparator());
            var exit=new ToolStripMenuItem("\u9000\u51fa");exit.Click+=delegate {ExitThread();};menu.Items.Add(exit);
            tray.ContextMenuStrip=menu;tray.MouseClick+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left)ShowDetails();};
            UpdateIcon();tray.Visible=true;
            refresh.Interval=60000;refresh.Tick+=delegate {if(!panelOpen){RequestRead(true,false);RequestHeadsetRead(false);RequestAudioRead(false);}};refresh.Start();
            liveSync.Interval=2000;liveSync.Tick+=delegate {
                if(!panelOpen||details==null||details.IsDisposed||!details.Visible){liveSync.Stop();return;}
                bool batteryDue=Current==null||(DateTime.UtcNow-Current.TimestampUtc).TotalSeconds>=30;
                RequestRead(batteryDue,false);
                RequestHeadsetRead(false);RequestAudioRead(false);
            };
            RefreshBattery();
            if(showPanel)dispatch.BeginInvoke((MethodInvoker)ShowDetails);
        }
        void ShowDetails() {
            if(details!=null&&!details.IsDisposed){details.ToggleAnimated();return;}
            details=new Details(this);details.OpenAnimated();
        }
        public void PanelOpened() {
            panelOpen=true;liveSync.Start();RefreshBattery();
        }
        public void PanelClosed() {
            if(disposed)return;panelOpen=false;liveSync.Stop();SaveStatus();
        }
        void UpdateIcon() {
            BatteryReading shown=Current??LastGood;bool stale=Current==null&&LastGood!=null;
            bool dark=Style.Dark;string key=(shown==null?-1:shown.Percent)+"|"+(Current!=null&&Current.Charging)+"|"+dark+"|"+stale+"|"+SystemInformation.SmallIconSize.Width;
            if(key!=lastIconKey) {
                using(var bitmap=Style.Tray(shown==null?(int?)null:shown.Percent,Current!=null&&Current.Charging,dark,stale)) {
                    IntPtr handle=bitmap.GetHicon();var next=(Icon)Icon.FromHandle(handle).Clone();DestroyIcon(handle);
                    var old=icon;icon=next;tray.Icon=icon;if(old!=null)old.Dispose();
                }
                lastIconKey=key;iconUpdates++;
            }
            string tooltip=Current!=null?"ATK A9 Air+: "+Current.Percent+"%"+(Current.Charging?" (charging)":"")
                :LastGood!=null?"ATK A9 Air+: last "+LastGood.Percent+"% at "+LastGood.TimestampUtc.ToLocalTime().ToString("HH:mm")
                :"ATK A9 Air+: waiting for a live battery reading";
            if(Headset!=null&&Headset.ConnectionVerified&&Headset.Connected)
                tooltip="ATK "+(shown==null?"--":shown.Percent.ToString())+"% | HyperX "+(Headset.Percent.HasValue?Headset.Percent.Value.ToString():"--")+"%";
            if(tooltip!=lastTooltip){tray.Text=tooltip;lastTooltip=tooltip;}
            if(details!=null&&!details.IsDisposed)details.SyncDevices();
            SaveStatus();
        }
        void SaveStatus() {
            try {
                string root=Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).FullName;
                string path=Path.Combine(root,"outputs","status.json");
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
        public void RefreshBattery() {
            RequestRead(true,true);
            RequestHeadsetRead(true);RequestAudioRead(true);
        }
        internal static bool ShowHeadset(HeadsetReading headset,AudioReading audio) {
            return VisualDesign.ShouldShowHeadset(headset);
        }
        void RequestHeadsetRead(bool force) {
            if(disposed||readHeadset==null||headsetBusy!=0)return;
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
                    Headset=result;UpdateIcon();
                });}catch(ObjectDisposedException){}catch(InvalidOperationException){}
            });
        }
        void RequestAudioRead(bool force) {
            if(disposed||readAudio==null||audioBusy!=0)return;
            if(!force&&(DateTime.UtcNow-audioCheckedUtc).TotalSeconds<(panelOpen?2:60))return;
            RunAudioOperation(readAudio);
        }
        void RunAudioOperation(Func<AudioReading> operation) {
            if(disposed||audioBusy!=0)return;audioBusy=1;
            ThreadPool.QueueUserWorkItem(delegate {
                AudioReading result;try {result=operation();}catch(Exception e){result=new AudioReading {Error=e.Message};}
                if(disposed)return;
                try {dispatch.BeginInvoke((MethodInvoker)delegate {
                    if(disposed)return;audioBusy=0;microphoneTargetInFlight=null;audioCheckedUtc=DateTime.UtcNow;Audio=result;UpdateIcon();RunPendingAudio();
                });}catch(ObjectDisposedException){}catch(InvalidOperationException){}
            });
        }
        public void SelectAudioOutput(string target) {
            if(target!="speaker"&&target!="headset")return;
            if(Audio==null||target=="speaker"&&!Audio.SpeakerAvailable||target=="headset"&&!Audio.HeadsetAvailable)return;
            pendingAudioOutput=target;RunPendingAudio();
        }
        public void ToggleMicrophoneMute() {
            if(Audio==null||!Audio.MicrophoneMuted.HasValue)return;
            pendingMicrophoneMute=!(pendingMicrophoneMute??microphoneTargetInFlight??Audio.MicrophoneMuted.Value);RunPendingAudio();
        }
        void RunPendingAudio() {
            if(disposed||audioBusy!=0)return;
            if(pendingAudioOutput!=null) {
                string target=pendingAudioOutput;pendingAudioOutput=null;
                RunAudioOperation(delegate {return AudioControl.SwitchOutput(target);});
            } else if(pendingMicrophoneMute.HasValue) {
                bool mute=pendingMicrophoneMute.Value;pendingMicrophoneMute=null;
                microphoneTargetInFlight=mute;
                RunAudioOperation(delegate {return AudioControl.SetHeadsetMicrophoneMute(mute);});
            }
        }
        void RequestRead(bool includeBattery,bool queueWhenBusy) {
            if(disposed||busy!=0&&!queueWhenBusy)return;
            pendingRead=true;pendingBattery|=includeBattery;RunNext();
        }
        public void SelectDpiStage(int stage) {
            if(disposed||Dpi==null||Dpi.Stages==null||stage<0||stage>=Dpi.Stages.Count)return;
            pendingStage=stage;RunNext();if(details!=null&&!details.IsDisposed)details.Invalidate();SaveStatus();
        }
        void RunNext() {
            if(disposed||busy!=0||pendingStage<0&&!pendingRead)return;
            int stage=pendingStage;bool includeBattery=pendingBattery;
            if(stage>=0)pendingStage=-1;
            else {pendingRead=false;pendingBattery=false;}
            busy=1;switching=stage>=0;
            if(details!=null&&!details.IsDisposed)details.Invalidate();SaveStatus();
            ThreadPool.QueueUserWorkItem(delegate {
                MouseSnapshot snapshot=null;DpiConfig switched=null;string failure=null;
                try {if(stage>=0)switched=selectStage(stage);else snapshot=readSnapshot(includeBattery);}catch(Exception e){failure=e.Message;}
                if(disposed)return;
                try {dispatch.BeginInvoke((MethodInvoker)delegate {
                    if(disposed)return;
                    if(stage>=0) {
                        if(switched!=null){Dpi=switched;DpiError=switched.RefreshError??"";completedSwitches++;}
                        else DpiError=failure??"DPI switch did not return a verified result";
                        pendingRead=true;pendingBattery=true;
                    } else {
                        completedReads++;
                        if(snapshot!=null) {
                            if(includeBattery){Current=snapshot.Battery;ErrorMessage=snapshot.BatteryError;if(Current!=null)LastGood=Current;}
                            if(snapshot.Dpi!=null){Dpi=snapshot.Dpi;DpiError=snapshot.DpiError??"";}else DpiError=snapshot.DpiError??failure??"DPI read did not return a verified result";
                        } else {if(includeBattery){Current=null;ErrorMessage=failure;}DpiError=failure;}
                    }
                    busy=0;switching=false;lastSyncUtc=DateTime.UtcNow;UpdateIcon();RunNext();
                });}catch(ObjectDisposedException){}catch(InvalidOperationException){}
            });
        }
        protected override void ExitThreadCore() {
            disposed=true;refresh.Stop();refresh.Dispose();liveSync.Stop();liveSync.Dispose();tray.Visible=false;
            if(details!=null)details.Close();tray.Dispose();if(icon!=null)icon.Dispose();dispatch.Dispose();base.ExitThreadCore();
        }
    }
    static class Program {
        [STAThread] static void Main(string[] args) {
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
            bool fresh;using(var mutex=new Mutex(true,"Local\\ATK-A9-Air-Battery-Tray",out fresh)) {
                if(!fresh)return;Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext(args.Length==1&&args[0]=="--show"));
            }
        }
    }
}
