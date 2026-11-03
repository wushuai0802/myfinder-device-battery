using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

// This is a renderer, not an application/test runner. Never instantiate TrayContext:
// even its injected constructor starts timers, async reads and tray infrastructure.
// A constructor-free DTO is used ONLY by the production layout/paint methods below.
internal static class RenderPromoAssets {
    const BindingFlags Instance=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    const string Demo="演示数据，非真实读数";
    static readonly string[] Palettes={"ocean","native","amber","mist"};
    static readonly List<Asset> assets=new List<Asset>();
    static string output,commit;
    static int scale;

    [DataContract] sealed class Dimensions {
        [DataMember] public int width;
        [DataMember] public int height;
        internal Dimensions(Size size){width=size.Width;height=size.Height;}
    }
    [DataContract] sealed class Asset {
        [DataMember] public string filename,content,palette,mode,sha256,sourceCommit,dataNotice,rendering;
        [DataMember] public Dimensions logicalSize,pixelSize;
        [DataMember] public double scale;
    }
    [DataContract] sealed class Manifest {
        [DataMember] public int schema=1;
        [DataMember] public string appVersion="1.3.5",sourceCommit,dataNotice=Demo,generatedUtc;
        [DataMember] public int scale,imageCount;
        [DataMember] public string[] notes={
            "面板由 1.3.5 生产绘制代码按逻辑坐标放大；空隙和卡片外保持透明。",
            "反馈和更新仅导出真实窗体客户区，不含 Windows 标题栏；窗体始终隐藏。",
            "托盘图标独立于面板配色：32/64 为生产函数原生尺寸，128 为 64 等比放大。",
            "任务栏为示意构图，非桌面截图；时间、设备名称和电量均为演示值。"
        };
        [DataMember] public List<Asset> images;
    }
    static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
    static void Set(object target,string name,object value){
        var field=target.GetType().GetField(name,Instance);
        Require(field!=null,"Production field changed: "+name);field.SetValue(target,value);
    }
    static void SelectTheme(string palette,bool dark,bool collapsed){
        var pref=new AudioBindings {ThemeStyle=palette,Theme=dark?"dark":"light",ReducedMotion=true,
            RememberReadings=false,AutoCheckUpdates=false,VolumeCollapsed=collapsed,WarningThreshold=20,
            MouseAlias="ATK A9 Air+",HeadsetAlias="HyperX Cloud Alpha"};
        pref.TrayGroups.Add(new TrayGroup {Id="promo-keyboard",Name="键盘",Members=new List<string>{"promo-keyboard"}});
        pref.TrayGroups.Add(new TrayGroup {Id="promo-headset",Name="耳机",Members=new List<string>{"promo-headset"}});
        DeviceSettings.UseTransientConfiguration(pref);
    }
    static AudioReading Audio(){return new AudioReading {TimestampUtc=DateTime.UtcNow,
        SpeakerAvailable=true,SpeakerId="demo-speaker",SpeakerName="桌面扬声器",
        HeadsetAvailable=true,HeadsetId="demo-headset",HeadsetName="HyperX Cloud Alpha",
        MicrophoneAvailable=true,MicrophoneId="demo-microphone",MicrophoneName="耳机麦克风",MicrophoneMuted=false,
        ActiveOutput="speaker",ConsoleOutputId="demo-speaker",MultimediaOutputId="demo-speaker",
        VolumeEndpointId="demo-speaker",OutputVolumePercent=35,Devices=new AudioDeviceChoice[0]};}
    static DpiConfig Dpi(){
        var dpi=new DpiConfig {ActiveStage=1,TimestampUtc=DateTime.UtcNow};
        foreach(int value in new[]{400,800,1600,3200})dpi.Stages.Add(new DpiStage {XDpi=value,YDpi=value});
        return dpi;
    }
    static WirelessDevice Device(string id,string name,string kind,int percent,bool fresh){
        return new WirelessDevice {Id=id,Name=name,Kind=kind,Transport="2.4 GHz",Percent=percent,
            Fresh=fresh,Online=true,ConnectionState="connected",MeasurementState=fresh?"current":"cached",
            ConfirmedAtUtc=(fresh?DateTime.UtcNow:DateTime.UtcNow.Date.AddHours(8).AddMinutes(30)).ToString("o",CultureInfo.InvariantCulture)};
    }
    static TrayContext PaintModel(params WirelessDevice[] devices){
        var owner=(TrayContext)FormatterServices.GetUninitializedObject(typeof(TrayContext));
        GC.SuppressFinalize(owner);
        owner.WirelessDevices=devices;owner.GenericReady=true;owner.NativeMousePresent=false;owner.Audio=Audio();
        Set(owner,"pendingStage",-1);Set(owner,"stageInFlight",-1);
        return owner;
    }
    static Bitmap Canvas(Size logical,Action<Graphics> paint){
        var image=new Bitmap(checked(logical.Width*scale),checked(logical.Height*scale),PixelFormat.Format32bppPArgb);
        try{using(var g=Graphics.FromImage(image)){
            g.Clear(Color.Transparent);g.SmoothingMode=SmoothingMode.AntiAlias;
            g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
            g.ScaleTransform(scale,scale);paint(g);
        }return image;}catch{image.Dispose();throw;}
    }
    static void Save(Bitmap image,string name,string content,string palette,bool dark,Size logical,string rendering){
        string filename=name+".png",path=Path.Combine(output,filename);
        Require(!File.Exists(path),"Refusing to overwrite "+filename);
        image.Save(path,ImageFormat.Png);
        string hash;using(var stream=File.OpenRead(path))using(var algorithm=SHA256.Create())hash=BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
        using(var decoded=new Bitmap(path))Require(decoded.Size==image.Size,"PNG dimension verification failed: "+filename);
        assets.Add(new Asset {filename=filename,content=content,palette=palette,mode=dark?"dark":"light",
            logicalSize=new Dimensions(logical),pixelSize=new Dimensions(image.Size),scale=(double)image.Width/logical.Width,
            sha256=hash,sourceCommit=commit,dataNotice=Demo,rendering=rendering});
    }
    static void Panel(string name,string content,string palette,bool dark,Size logical,Action<Graphics> paint,params Point[] gaps){
        using(var image=Canvas(logical,paint)){
            Require(image.GetPixel(0,0).A==0,"Panel corner lost transparency: "+name);
            foreach(var point in gaps)Require(image.GetPixel(point.X*scale,point.Y*scale).A==0,"Card gap lost transparency: "+name);
            Save(image,name+"-"+palette+"-"+(dark?"dark":"light"),content,palette,dark,logical,"production paint + Graphics.ScaleTransform");
        }
    }
    static void Panels(string palette,bool dark){
        SelectTheme(palette,dark,false);
        var dpi=Dpi();var now=DateTime.UtcNow;
        var mouse=new BatteryReading {Percent=80,TimestampUtc=now};
        var headset=new HeadsetReading {Percent=46,Connected=true,ConnectionVerified=true,ReceiverPresent=true,
            TimestampUtc=now,BatteryTimestampUtc=now,ConnectionTimestampUtc=now};
        Panel("main-dual","ATK A9 Air+ 80%；DPI 400/800/1600/3200，选中 800；HyperX Cloud Alpha 46%；扬声器/耳机/麦克风；音量 35% 展开；底部同步条",
            palette,dark,VisualDesign.PanelSizeFor(true,dpi),delegate(Graphics g){
                CompactRenderer.Paint(g,mouse,null,null,false,dpi,null,false,dark,headset,Audio(),1,-1,false,1,-1,new PanelFeedback());
            },new Point(130,100),new Point(130,204));
        SelectTheme(palette,dark,true);
        var owner=PaintModel(Device("promo-mouse","罗技 G Pro Wireless","mouse",76,true),
            Device("promo-keyboard","Keychron 键盘","keyboard",15,true),Device("promo-headset","雷蛇耳机","headset",100,true),
            Device("promo-history","历史读数设备","mouse",62,false));
        var layout=WirelessLayout.Build(owner,0,10000);
        Require(layout.Cards.Count==4&&!layout.Cards[3].Battery.Fresh,"List demo fixture is invalid.");
        Require(ThemePalette.IsLow(layout.Cards[1].Battery,20),"Keyboard must have a low-battery warning.");
        Panel("main-list","罗技 G Pro Wireless 76%；Keychron 键盘 15% 低电量；雷蛇耳机 100%；历史读数设备 62%；音量条收起",
            palette,dark,layout.Size,delegate(Graphics g){CompactRenderer.PaintWireless(g,owner,layout,dark,new PanelFeedback());},new Point(130,64));
        var low=PaintModel(Device("promo-low","低电量鼠标","mouse",12,true));
        var lowLayout=WirelessLayout.Build(low,0,10000);
        Require(ThemePalette.IsLow(lowLayout.Cards[0].Battery,20),"12% must be below the demo warning threshold.");
        Panel("main-low","单张鼠标卡片 12%，低于 20% 阈值的红色提示",palette,dark,new Size(260,lowLayout.Cards[0].Height),
            delegate(Graphics g){CompactRenderer.PaintWireless(g,low,lowLayout,dark,new PanelFeedback());});
        Page(owner,new PopupNavigation {Page=PopupPage.ChoosePreference,PreferenceAction=223},"theme-picker","配色选择页，当前配色打勾",palette,dark);
        Page(owner,new PopupNavigation {Page=PopupPage.TrayIcons},"tray-icons-page","托盘图标页：托盘图标 1、键盘、耳机，共三个分组",palette,dark);
    }
    static void Page(TrayContext owner,PopupNavigation nav,string name,string content,string palette,bool dark){
        var rows=PopupPages.Build(owner,nav,false);
        if(name=="theme-picker")Require(rows.FindAll(delegate(PopupControl row){return row.Selected;}).Count==1&&
            rows.Exists(delegate(PopupControl row){return row.Selected&&row.PaletteKey==palette;}),"Current palette must be checked.");
        else Require(rows.FindAll(delegate(PopupControl row){return row.ActionKey=="tray:group";}).Count==3,"Expected three tray groups.");
        Panel(name,content,palette,dark,PopupPages.SizeFor(rows),delegate(Graphics g){PopupPages.Paint(g,owner,nav,rows,dark,-1,-1,-1);});
    }

    sealed class BlockedTransport:IFeedbackTransport {
        public UpdateResponse Post(Uri uri,byte[] body,CancellationToken token){throw new InvalidOperationException("Network access is forbidden in the promo renderer.");}
    }
    static void Forbidden(string ignored){throw new InvalidOperationException("Clipboard/browser access is forbidden in the promo renderer.");}
    static T Find<T>(Control root,string name) where T:Control {
        var found=root.Controls.Find(name,true);Require(found.Length==1&&found[0] is T,"Production control changed: "+name);return (T)found[0];
    }
    static void Walk(Control control,Action<Control> action){action(control);foreach(Control child in control.Controls)Walk(child,action);}
    static void LoadHidden(ThemedDialog form){
        form.AutoScaleMode=AutoScaleMode.None;form.ShowInTaskbar=false;
        Walk(form,delegate(Control control){var handle=control.Handle;});
        // OnLoad performs the production layout. Never call Show/OnShown/DoEvents:
        // OnShown intentionally makes a production dialog visible.
        typeof(ThemedDialog).GetMethod("OnLoad",Instance).Invoke(form,new object[]{EventArgs.Empty});
        Walk(form,delegate(Control control){control.PerformLayout();});
        Require(!form.Visible&&!IsWindowVisible(form.Handle),"A promo dialog became visible.");
    }
    [StructLayout(LayoutKind.Sequential)] struct PrintRect {internal int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] struct PrintRange {internal IntPtr Device,Target;internal PrintRect Area,Page;internal int First,Last;}
    [DllImport("user32.dll",CharSet=CharSet.Auto)] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,ref PrintRange value);
    [DllImport("user32.dll",CharSet=CharSet.Auto)] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr value);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    static Point ClientOrigin(Control control,Control root){
        Point point=Point.Empty;
        while(control!=root){Require(control.Parent!=null,"Control is outside the dialog.");point.Offset(control.Left,control.Top);control=control.Parent;}
        return point;
    }
    static void NativeNotes(Form form,Bitmap image){
        // DrawToBitmap omits RichEdit; EM_FORMATRANGE uses the production control's
        // text, fonts and paragraph spacing at the already scaled pixel size.
        foreach(Control control in form.Controls.Find("ReleaseNotes",true)){
            var notes=(RichTextBox)control;
            int width=notes.Width-(notes.ScrollBars==RichTextBoxScrollBars.Vertical?SystemInformation.VerticalScrollBarWidth:0);
            using(var text=new Bitmap(Math.Max(1,width),Math.Max(1,notes.Height)))using(var ink=Graphics.FromImage(text)){
                ink.Clear(notes.BackColor);IntPtr hdc=ink.GetHdc();
                try{
                    var rect=new PrintRect {Left=30,Right=(int)Math.Round((width-2)*1440f/96),Bottom=(int)Math.Round(notes.Height*1440f/96)};
                    var range=new PrintRange {Device=hdc,Target=hdc,Area=rect,Page=rect,First=0,Last=notes.TextLength};
                    int printed=SendMessage(notes.Handle,0x0439,(IntPtr)1,ref range).ToInt32();
                    Require(printed>=notes.TextLength,"Release notes were clipped.");
                }finally{ink.ReleaseHdc(hdc);SendMessage(notes.Handle,0x0439,IntPtr.Zero,IntPtr.Zero);}
                using(var g=Graphics.FromImage(image))g.DrawImageUnscaled(text,ClientOrigin(notes,form));
            }
        }
    }
    static void PaintDialogControls(Form form,Bitmap image){
        // WM_PRINT uses a GDI HDC with aliased GDI+ text defaults. Repaint the
        // visible custom leaves with their exact production OnPaint methods and
        // antialiasing, as on the scaled panel canvas. Hidden fallback actions
        // are deliberately absent from this ready-state list.
        string[] names={"Categories","IncludeDiagnostics","SubmitFeedback","CancelFeedback","InstallUpdate","SkipUpdate","LaterUpdate"};
        using(var g=Graphics.FromImage(image))foreach(string name in names)foreach(Control control in form.Controls.Find(name,true)){
            var state=g.Save();Point point=ClientOrigin(control,form);g.TranslateTransform(point.X,point.Y);
            g.SetClip(control.ClientRectangle,CombineMode.Intersect);g.SmoothingMode=SmoothingMode.AntiAlias;
            g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
            var paint=new PaintEventArgs(g,control.ClientRectangle);
            control.GetType().GetMethod("OnPaintBackground",Instance).Invoke(control,new object[]{paint});
            control.GetType().GetMethod("OnPaint",Instance).Invoke(control,new object[]{paint});g.Restore(state);
        }
    }
    static void Dialog(ThemedDialog form,string name,string content,string palette,bool dark){
        var fonts=new List<Font>();
        try{
            DialogDpi.DeviceDpiReader=delegate{return 96;};LoadHidden(form);
            Size logical=form.ClientSize;
            var originalFonts=new Dictionary<Control,Font>();Walk(form,delegate(Control control){originalFonts.Add(control,control.Font);});
            form.SuspendLayout();DialogDpi.DeviceDpiReader=delegate{return 96*scale;};
            form.Scale(new SizeF(scale,scale));
            foreach(var pair in originalFonts){var font=new Font(pair.Value.FontFamily,pair.Value.Size*scale,pair.Value.Style,pair.Value.Unit);fonts.Add(font);pair.Key.Font=font;}
            form.ClientSize=new Size(logical.Width*scale,logical.Height*scale);form.ResumeLayout(true);
            // Explicit fonts above now have their final DPI. Re-run the existing
            // category size callback so the type caption is measured at that DPI.
            foreach(Control categories in form.Controls.Find("Categories",true))typeof(Control).GetMethod("OnSizeChanged",Instance).Invoke(categories,new object[]{EventArgs.Empty});
            Walk(form,delegate(Control control){control.PerformLayout();});
            // Windows limits the native top-level window's tracking size to the
            // monitor. Paint its production background and real child tree into
            // the full client canvas instead of cropping that native window.
            using(var client=new Bitmap(form.ClientSize.Width,form.ClientSize.Height,PixelFormat.Format32bppArgb)){
                using(var g=Graphics.FromImage(client)){
                    g.Clear(form.BackColor);
                    for(int index=form.Controls.Count-1;index>=0;index--){
                        Control child=form.Controls[index];
                        using(var part=new Bitmap(child.Width,child.Height,PixelFormat.Format32bppArgb)){
                            child.DrawToBitmap(part,new Rectangle(Point.Empty,part.Size));g.DrawImageUnscaled(part,child.Location);
                        }
                    }
                }
                    NativeNotes(form,client);
                    PaintDialogControls(form,client);
                    Require(client.Width==logical.Width*scale&&client.Height==logical.Height*scale,"Dialog scale changed during rendering.");
                    Save(client,name+"-"+palette+"-"+(dark?"dark":"light"),content,palette,dark,logical,
                        "production hidden form at target DPI; DrawToBitmap client + native RichEdit EM_FORMATRANGE (no bitmap upscaling)");
            }
            Require(!form.Visible&&!IsWindowVisible(form.Handle),"A promo dialog became visible.");
        }finally{form.Dispose();foreach(var font in fonts)font.Dispose();DialogDpi.DeviceDpiReader=delegate{return 96;};}
    }
    static void Dialogs(string palette,bool dark){
        SelectTheme(palette,dark,false);
        var report=new FeedbackReport("demo","demo-windows",new WirelessDevice[0],new SuppressedDevice[0],new UnadaptedDevice[0],new ReaderHealth[0],new string[0]);
        var form=new FeedbackForm(report,true,dark,Forbidden,Forbidden,"https://example.invalid",new BlockedTransport(),new FeedbackSession(),delegate{throw new InvalidOperationException("No confirmation actions in renderer.");});
        Find<TextBox>(form,"Description").Text="通过无线接收器连接设备后没有显示电量，希望支持这个型号。";
        Require(form.SelectedCategory=="device"&&Find<CheckBox>(form,"IncludeDiagnostics").Checked,"Feedback demo fixture is invalid.");
        Dialog(form,"feedback","反馈窗口客户区：设备不识别；示例描述；附带诊断开启",palette,dark);
        var release=new UpdateRelease {Tag="v1.3.6",Body="- 优化多设备电量展示。\r\n- 完善托盘图标与配色体验。\r\n- 改进设备连接状态提示。"};
        Dialog(new UpdateDialog(release,true),"update","更新窗口客户区：准备状态；当前 1.3.5 → 1.3.6（示例）；三条演示更新说明",palette,dark);
    }
    static Bitmap Enlarge(Bitmap source,int size){
        var image=new Bitmap(size,size,PixelFormat.Format32bppPArgb);
        using(var g=Graphics.FromImage(image))using(var attributes=new ImageAttributes()){
            g.Clear(Color.Transparent);g.CompositingMode=CompositingMode.SourceCopy;g.InterpolationMode=InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode=PixelOffsetMode.HighQuality;attributes.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(source,new Rectangle(0,0,size,size),0,0,source.Width,source.Height,GraphicsUnit.Pixel,attributes);
        }return image;
    }
    static void TrayIcons(){
        foreach(bool dark in new[]{true,false})foreach(int level in new[]{80,46,15,100})
        foreach(string kind in new[]{"number","mouse","keyboard","headset"})foreach(int size in new[]{32,64}){
            string name="tray-"+(kind=="number"?kind:"device-"+kind)+"-"+level+"-"+(dark?"dark":"light")+"-";
            using(var image=kind=="number"?TrayGlyph.Render(level,false,dark,false,size):TrayGlyph.RenderDevice(level,false,dark,false,size,kind)){
                Require(image.Width==size&&image.GetPixel(0,0).A==0,"Tray size/alpha mismatch.");
                Save(image,name+size,kind+" 托盘图标，"+level+"%","independent",dark,new Size(size,size),"production TrayGlyph native "+size+" px");
                if(size==64)using(var enlarged=Enlarge(image,128))Save(enlarged,name+128,kind+" 托盘图标，"+level+"%","independent",dark,new Size(64,64),"64 px PNG source enlarged uniformly to 128 px (2x bicubic); not native 128 px");
            }
        }
    }
    static void Taskbars(){
        foreach(bool dark in new[]{true,false}){
            SelectTheme("native",dark,false);var logical=new Size(900,96);
            using(var image=Canvas(logical,delegate(Graphics g){
                CompactRenderer.DrawSurface(g,new RectangleF(.5f,.5f,899,95),dark);
                string[] kinds={"mouse","keyboard","headset"};int[] levels={76,15,46};
                for(int i=0;i<kinds.Length;i++)using(var icon=TrayGlyph.RenderDevice(levels[i],false,dark,false,64,kinds[i]))g.DrawImage(icon,new RectangleF(28+i*68,24,48,48));
                CompactRenderer.DrawLabel(g,"10:08",new RectangleF(250,18,130,32),24,false,ThemePalette.Current(dark).Foreground,StringAlignment.Near);
                CompactRenderer.DrawLabel(g,"2026/11/04",new RectangleF(250,51,150,24),14,false,ThemePalette.Current(dark).Muted,StringAlignment.Near);
                CompactRenderer.DrawLabel(g,"任务栏通知区 · 示意构图",new RectangleF(450,20,420,30),18,false,ThemePalette.Current(dark).Foreground,StringAlignment.Far);
                CompactRenderer.DrawLabel(g,Demo,new RectangleF(450,51,420,24),14,false,ThemePalette.Current(dark).Muted,StringAlignment.Far);
            }))Save(image,"taskbar-strip-"+(dark?"dark":"light"),"示意构图（非截图）：从左到右鼠标 76%、键盘 15%、耳机 46%、演示时间","native",dark,logical,
                "illustrative composition + Graphics.ScaleTransform; device icons from production 64 px native renders");
        }
    }
    static void ValidateOutput(string worktree){
        string temporary=Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\','/');
        string root=Path.GetFullPath(worktree).TrimEnd('\\','/');
        Require(output.StartsWith(temporary+"\\",StringComparison.OrdinalIgnoreCase),"Output must be a child directory below TEMP.");
        Require(!String.Equals(output.TrimEnd('\\','/'),root,StringComparison.OrdinalIgnoreCase)&&
            !output.StartsWith(root+"\\",StringComparison.OrdinalIgnoreCase),"Generated files must remain outside the worktree.");
        for(var current=new DirectoryInfo(output);current!=null;current=current.Parent){
            Require(!File.Exists(current.FullName),"Output ancestors must be directories.");
            if(current.Exists)Require((current.Attributes&FileAttributes.ReparsePoint)==0,"Output ancestors cannot be reparse points.");
        }
        Require(!Directory.Exists(output)||Directory.GetFileSystemEntries(output).Length==0,"Output must be empty; existing assets will not be overwritten.");
    }
    [STAThread] static int Main(string[] args){
        try{
            Require(args.Length==4,"Usage: RenderPromoAssets.exe <TEMP output> <scale 1..6> <source commit> <worktree>");
            output=Path.GetFullPath(args[0]);scale=Int32.Parse(args[1],CultureInfo.InvariantCulture);commit=args[2];
            Require(scale>=1&&scale<=6,"Scale must be between 1 and 6.");
            ValidateOutput(args[3]);
            Require(System.Text.RegularExpressions.Regex.IsMatch(commit,"\\A[a-f0-9]{40}\\z"),"Invalid source commit.");
            Directory.CreateDirectory(output);
            VisualDesign.HighContrastOverride=false;
            // Set transient preferences BEFORE any production access to Current/Style.Dark.
            SelectTheme("ocean",true,false);
            Require(AppRuntime.Version=="1.3.5","The compiled production version is not 1.3.5.");
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            DialogDpi.DeviceDpiReader=delegate{return 96;};
            ThemedDialog.WorkingAreaProvider=delegate{return new Rectangle(0,0,16000,16000);};
            foreach(string palette in Palettes)foreach(bool dark in new[]{true,false}){Panels(palette,dark);Dialogs(palette,dark);Console.WriteLine("Rendered "+palette+" "+(dark?"dark":"light"));}
            TrayIcons();Taskbars();
            Require(assets.Count==154,"Expected 154 PNGs (56 panels/dialogs + 96 tray icons + 2 taskbars).");
            var manifest=new Manifest {sourceCommit=commit,scale=scale,imageCount=assets.Count,images=assets,generatedUtc=DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture)};
            using(var stream=File.Create(Path.Combine(output,"manifest.json")))new DataContractJsonSerializer(typeof(Manifest)).WriteObject(stream,manifest);
            Console.WriteLine("Exported "+assets.Count+" PNGs + manifest.json to "+output);return 0;
        }catch(Exception error){Console.Error.WriteLine(error.ToString());return 1;}
    }
}
