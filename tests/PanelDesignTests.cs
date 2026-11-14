using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

// Offline fixtures only. The pipeline supplies an output directory outside the worktree.
static class PanelDesignTests {
    const BindingFlags Static=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks,failures;
    static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
    static void Run(string name,Action test){try{test();Console.WriteLine("PASS "+name);}catch(Exception error){failures++;Console.Error.WriteLine("FAIL "+name+": "+(error.InnerException??error));}}
    static void NativeLabel(Graphics g,string text,RectangleF rect,float size,bool bold,Color color,StringAlignment align,StringTrimming trimming,bool wrap=false){
        string family=wrap?"Microsoft YaHei UI":"Segoe UI";foreach(char c in text)if(c>=0x2e80&&c<=0x9fff){family="Microsoft YaHei UI";break;}
        g.PixelOffsetMode=PixelOffsetMode.Default;if(g.TextRenderingHint==System.Drawing.Text.TextRenderingHint.SystemDefault)g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using(var font=new Font(family,size,bold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel))
        using(var brush=new SolidBrush(color))using(var format=new StringFormat {Alignment=align,LineAlignment=StringAlignment.Center,Trimming=trimming,FormatFlags=wrap?0:StringFormatFlags.NoWrap})g.DrawString(text,font,brush,rect,format);
    }
    static void TextNativeExperiments(string output){
        Directory.CreateDirectory(output);var clock=System.Diagnostics.Stopwatch.StartNew();var lines=new List<string>();
        Mode("normal","ocean",true,"grid");VisualDesign.HighContrastOverride=false;
        // Experiment 1: retain every first frame; GC is a controlled intervention,
        // not evidence that GC caused the recorded CI transition.
        using(var fixture=new Fixture()){
            var owner=fixture.Owner;owner.GenericReady=true;owner.NativeMousePresent=true;owner.Headset=null;owner.Audio=null;owner.Dpi=Dpi(8);
            var devices=new List<WirelessDevice>();for(int i=0;i<4;i++)devices.Add(GridDevice(i,8));owner.WirelessDevices=devices.ToArray();
            var layout=WirelessLayout.Build(owner,0,520);int[] gc={GC.CollectionCount(0),GC.CollectionCount(1),GC.CollectionCount(2)};
            int paints=0;var old=CompactRenderer.TextRasterObserver;
            CompactRenderer.TextRasterObserver=delegate(string stage,Graphics g,string text,RectangleF rect,Font font,Color color,StringFormat format){if(stage=="paint-label")paints++;};
            try{using(var first=RenderGrid(owner,layout,false,1.5f,new PanelFeedback())){
                first.Save(Path.Combine(output,"lifecycle-first.png"));
                for(int i=0;i<16;i++){
                    if(i==7){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}
                    using(var next=RenderGrid(owner,WirelessLayout.Build(owner,0,520),false,1.5f,new PanelFeedback())){
                        next.Save(Path.Combine(output,"lifecycle-"+i.ToString("D2")+".png"));lines.Add("lifecycle "+i+" same="+!Different(first,next));
                    }
                }
            }}finally{CompactRenderer.TextRasterObserver=old;}
            lines.Add("nativePaints="+paints+" GC delta="+(GC.CollectionCount(0)-gc[0])+"/"+(GC.CollectionCount(1)-gc[1])+"/"+(GC.CollectionCount(2)-gc[2]));
        }
        // Experiment 2: transparent local raster -> integer device-space blit,
        // versus untouched DrawString, including a nonrectangular active clip.
        int comparisons=0,differences=0;
        foreach(float scale in new[]{1f,1.25f,1.5f,2f})foreach(Color background in new[]{Color.Transparent,Color.White,Color.FromArgb(255,27,36,48)})foreach(bool clipped in new[]{false,true}){
            string id="sprite-"+comparisons;var rect=new RectangleF(11,55,104,18);var color=Color.FromArgb(95,107,124);
            using(var direct=new Bitmap(320,220,PixelFormat.Format32bppPArgb))using(var replay=new Bitmap(320,220,PixelFormat.Format32bppPArgb)){
                for(int step=0;step<2;step++)using(var g=Graphics.FromImage(step==0?direct:replay)){
                    g.Clear(background);g.ScaleTransform(scale,scale);g.TranslateTransform(.25f,.5f);g.SmoothingMode=SmoothingMode.AntiAlias;
                    if(clipped)using(var path=new GraphicsPath()){path.AddEllipse(12,52,95,22);g.SetClip(path);}
                    if(step==0)NativeLabel(g,"ATK A9 Air+",rect,11,false,color,StringAlignment.Near,StringTrimming.EllipsisCharacter);
                    else{
                        float x=rect.X*scale+.25f*scale,y=rect.Y*scale+.5f*scale;int left=(int)Math.Floor(x)-4,top=(int)Math.Floor(y)-4;
                        using(var sprite=new Bitmap((int)Math.Ceiling(rect.Width*scale)+9,(int)Math.Ceiling(rect.Height*scale)+9,PixelFormat.Format32bppPArgb)){
                            using(var drawing=Graphics.FromImage(sprite)){drawing.ScaleTransform(scale,scale);drawing.TranslateTransform(.25f-left/scale,.5f-top/scale);NativeLabel(drawing,"ATK A9 Air+",rect,11,false,color,StringAlignment.Near,StringTrimming.EllipsisCharacter);}
                            g.ResetTransform();g.DrawImageUnscaled(sprite,left,top);
                        }
                    }
                }
                bool same=!Different(direct,replay);if(!same)differences++;lines.Add(id+" scale="+scale+" background="+background.ToArgb()+" clip="+clipped+" same="+same);
                direct.Save(Path.Combine(output,id+"-native.png"));replay.Save(Path.Combine(output,id+"-replay.png"));comparisons++;
            }
        }
        lines.Add("sprite comparisons="+comparisons+" differences="+differences+" elapsedMs="+clock.ElapsedMilliseconds);
        File.WriteAllLines(Path.Combine(output,"native-experiments.txt"),lines.ToArray());foreach(var line in lines)Console.WriteLine(line);
    }
    static void TextRasterReuseContract(){
        LabelRasterCache.Clear();
        int draws=0;var old=CompactRenderer.TextRasterObserver;
        CompactRenderer.TextRasterObserver=delegate(string stage,Graphics g,string text,RectangleF rect,Font font,Color color,StringFormat format){draws++;};
        try{using(var first=new Bitmap(240,100,PixelFormat.Format32bppPArgb))using(var second=new Bitmap(240,100,PixelFormat.Format32bppPArgb)){
            for(int i=0;i<2;i++)using(var g=Graphics.FromImage(i==0?first:second)){
                g.ScaleTransform(1.5f,1.5f);CompactRenderer.DrawLabel(g,"reuse-contract-ATK",new RectangleF(11,12,130,20),11,false,Color.FromArgb(95,107,124),StringAlignment.Near);
                if(i==0)Check(draws==1,"contract first frame performs one native text raster");
            }
            Check(draws==1,"same complete text input must reuse its first native raster, not call DrawString again (actual="+draws+")");
            Check(!Different(first,second),"raster reuse preserves the first frame byte for byte");
        }}finally{CompactRenderer.TextRasterObserver=old;}
    }
    sealed class TextInput {
        internal string Text="ATK A9 Air+";
        internal RectangleF Rect=new RectangleF(11,20,140,40);
        internal float Size=11,Scale=1,DpiX=96,DpiY=96,X,Y;
        internal bool Bold,Wrap,HighContrast;
        internal int Clip,Contrast=4;
        internal Color Color=Color.FromArgb(95,107,124),Background=Color.Transparent;
        internal StringAlignment Align=StringAlignment.Near;
        internal StringTrimming Trimming=StringTrimming.EllipsisCharacter;
        internal System.Drawing.Text.TextRenderingHint Hint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        internal int NativeContext;
        internal TextInput Copy(){return (TextInput)MemberwiseClone();}
    }
    static Bitmap TextImage(TextInput input,bool native){
        var image=new Bitmap(480,240,PixelFormat.Format32bppPArgb);image.SetResolution(input.DpiX,input.DpiY);
        try{using(var g=Graphics.FromImage(image)){
            g.Clear(input.Background);g.ScaleTransform(input.Scale,input.Scale);g.TranslateTransform(input.X,input.Y);
            g.SmoothingMode=SmoothingMode.AntiAlias;g.TextRenderingHint=input.Hint;g.TextContrast=input.Contrast;
            if(input.NativeContext==1)g.RotateTransform(12);
            if(input.NativeContext==2)g.CompositingMode=CompositingMode.SourceCopy;
            if(input.NativeContext==3)g.CompositingQuality=CompositingQuality.GammaCorrected;
            if(input.NativeContext==4)g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            if(input.NativeContext==5){g.PageUnit=GraphicsUnit.Point;g.PageScale=1.1f;}
            if(input.Clip==1)g.SetClip(new RectangleF(17,22,64,22));
            if(input.Clip==2)using(var path=new GraphicsPath()){path.AddEllipse(12,18,110,36);path.AddRectangle(new RectangleF(35,25,14,8));g.SetClip(path);}
            VisualDesign.HighContrastOverride=input.HighContrast;
            string before=GridGraphicsInput(g);
            if(native)NativeLabel(g,input.Text,input.Rect,input.Size,input.Bold,input.Color,input.Align,input.Trimming,input.Wrap);
            else if(input.Wrap)CompactRenderer.DrawWrappedLabel(g,input.Text,input.Rect,input.Size,input.Color);
            else CompactRenderer.DrawLabel(g,input.Text,input.Rect,input.Size,input.Bold,input.Color,input.Align,input.Trimming);
            if(!native)Check(before==GridGraphicsInput(g),"text replay restores transform, clip and all Graphics settings");
        }return image;}catch{image.Dispose();throw;}
    }
    static void TextRasterInputs(){
        LabelRasterCache.Clear();var inputs=new List<TextInput>();var basic=new TextInput();inputs.Add(basic);
        for(int i=0;i<24;i++){
            var c=basic.Copy();switch(i){
                case 0:c.Text="ATK A9 Air++";break;case 1:c.Text="设备 ATK A9";break;case 2:c.Bold=true;break;case 3:c.Size=12;break;
                case 4:c.Color=Color.FromArgb(25,109,230);break;case 5:c.Color=Color.FromArgb(127,95,107,124);break;
                case 6:c.Scale=1.25f;break;case 7:c.Scale=1.5f;break;case 8:c.Scale=2;break;
                case 9:c.DpiX=c.DpiY=120;break;case 10:c.DpiX=144;c.DpiY=120;break;
                case 11:c.X=1;break;case 12:c.X=.25f;break;case 13:c.Y=.5f;break;
                case 14:c.Clip=1;break;case 15:c.Clip=2;break;case 16:c.Contrast=0;break;
                case 17:c.Hint=System.Drawing.Text.TextRenderingHint.AntiAlias;break;
                case 18:c.Align=StringAlignment.Far;break;case 19:c.Trimming=StringTrimming.None;break;
                case 20:c.Wrap=true;c.Trimming=StringTrimming.None;c.Text="Wrapped ASCII and 中文文字\nsecond line";break;
                case 21:c.Size=15;break;case 22:c.HighContrast=true;break;
                case 23:c.Rect=new RectangleF(11,20,45,20);break;
            }inputs.Add(c);
        }
        foreach(float scale in new[]{1.25f,1.5f,2f}){
            var c=basic.Copy();c.Scale=scale;c.DpiX=c.DpiY=144;c.X=.25f;c.Y=.5f;c.Clip=2;c.Color=Color.FromArgb(170,95,107,124);c.Background=Color.FromArgb(180,27,36,48);
            c.Text="设备 ATK A9 Air+";c.Bold=true;inputs.Add(c);
        }
        int draws=0;var old=CompactRenderer.TextRasterObserver;
        CompactRenderer.TextRasterObserver=delegate(string stage,Graphics g,string text,RectangleF rect,Font font,Color color,StringFormat format){draws++;};
        try{
            for(int i=0;i<inputs.Count;i++){
                var c=inputs[i];int before=draws;
                using(var native=TextImage(c,true))using(var first=TextImage(c,false))using(var second=TextImage(c,false)){
                    Check(draws==before+1,"changed complete input creates exactly one native raster: "+i);
                    Check(!Different(native,first),"first cached text equals direct native pixels: "+i);
                    Check(!Different(first,second),"repeat text equals the retained first raster: "+i);
                }
            }
            // Background is not retained in the sprite; the current destination
            // must still compose correctly without another native text draw.
            int saved=draws;foreach(Color background in new[]{Color.White,Color.FromArgb(255,27,36,48),Color.FromArgb(128,40,70,100)}){
                var c=basic.Copy();c.Background=background;
                using(var native=TextImage(c,true))using(var cached=TextImage(c,false))Check(!Different(native,cached),"text composes with the current background");
            }Check(draws==saved,"background pixels are never part of or replayed from the text cache");
            for(int i=1;i<=5;i++){
                var c=basic.Copy();c.NativeContext=i;int before=draws,count=LabelRasterCache.Count;
                using(var native=TextImage(c,true))using(var first=TextImage(c,false))using(var second=TextImage(c,false)){
                    Check(!Different(native,first)&&!Different(first,second),"unsupported context preserves direct native drawing: "+i);
                    Check(draws==before+2&&LabelRasterCache.Count==count,"unsupported context is not cached: "+i);
                }
            }
        }finally{CompactRenderer.TextRasterObserver=old;VisualDesign.HighContrastOverride=false;LabelRasterCache.Clear();}
    }
    static List<Bitmap> CachedTextImages(){
        var result=new List<Bitmap>();var items=(System.Collections.IEnumerable)typeof(LabelRasterCache).GetField("recent",Static).GetValue(null);
        foreach(var item in items)result.Add((Bitmap)item.GetType().GetField("Image",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(item));return result;
    }
    static bool ImageDisposed(Bitmap image){try{image.GetPixel(0,0);return false;}catch(ArgumentException){return true;}catch(ObjectDisposedException){return true;}}
    static void TextRasterResources(){
        LabelRasterCache.Clear();try{
            using(var image=new Bitmap(100,80,PixelFormat.Format32bppPArgb))using(var g=Graphics.FromImage(image)){
                var rect=new RectangleF(0,0,80,20);Action<string> draw=delegate(string text){CompactRenderer.DrawLabel(g,text,rect,11,false,Color.Black,StringAlignment.Near);};
                draw("retained");draw("evicted");Bitmap oldest=CachedTextImages()[0];draw("retained");
                for(int i=0;i<LabelRasterCache.Capacity-1;i++)draw("entry-"+i);
                Check(LabelRasterCache.Count==LabelRasterCache.Capacity&&LabelRasterCache.Bytes<=LabelRasterCache.ByteLimit,"LRU enforces its entry and byte limits");
                Check(ImageDisposed(oldest),"entry eviction disposes its native bitmap immediately");
                var retained=CachedTextImages();LabelRasterCache.Clear();
                foreach(var bitmap in retained)Check(ImageDisposed(bitmap),"explicit cache cleanup disposes every retained bitmap");
                Check(LabelRasterCache.Count==0&&LabelRasterCache.Bytes==0,"cache cleanup releases entries and accounting");
                rect=new RectangleF(0,0,800,160);for(int i=0;i<24;i++)draw("byte-budget-"+i);
                Check(LabelRasterCache.Count<24&&LabelRasterCache.Bytes<=LabelRasterCache.ByteLimit,"byte budget evicts before the entry limit");
                int count=LabelRasterCache.Count;long bytes=LabelRasterCache.Bytes;rect=new RectangleF(0,0,2000,400);draw("uncached oversize text");
                Check(LabelRasterCache.Count==count&&LabelRasterCache.Bytes==bytes,"oversize labels use the native fallback without a retained allocation");
            }
        }finally{LabelRasterCache.Clear();}
    }
    static object Geometry(string name,params object[] args){var method=typeof(VisualDesign).GetMethod(name,Static);Check(method!=null,"shared geometry exists: "+name);return method.Invoke(null,args);}
    static RectangleF Rect(string name,params object[] args){return (RectangleF)Geometry(name,args);}
    static Color Role(ThemePalette palette,string name){var property=typeof(ThemePalette).GetProperty(name,BindingFlags.Instance|BindingFlags.NonPublic);Check(property!=null,"palette role exists: "+name);return (Color)property.GetValue(palette,null);}
    static void Mode(string font,string style="ocean",bool collapsed=false,string layout="bar"){
        var settings=new AudioBindings {FontSize=font,ThemeStyle=style,VolumeCollapsed=collapsed,ReducedMotion=true,RememberReadings=false,LowBatteryNotifications=false};
        var field=typeof(AudioBindings).GetField("PanelLayout");if(field!=null)field.SetValue(settings,layout);else Check(layout=="bar","PanelLayout must exist for the new default ring layout");
        DeviceSettings.UseTransientConfiguration(settings);
    }
    static string Layout(AudioBindings settings){var field=typeof(AudioBindings).GetField("PanelLayout");Check(field!=null,"PanelLayout is persisted");return (string)field.GetValue(settings);}
    static void LayoutSettings(){
        Check(Layout(new AudioBindings())=="ring","new settings default to ring");
        var field=typeof(AudioBindings).GetField("PanelLayout");var member=(System.Runtime.Serialization.DataMemberAttribute)Attribute.GetCustomAttribute(field,typeof(System.Runtime.Serialization.DataMemberAttribute));Check(member!=null&&member.Order==30,"layout uses the unused data member 30");
        string directory=Path.Combine(Path.GetTempPath(),"myfinder-ring-settings-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);string path=Path.Combine(directory,"settings.json");
        Check(Layout(DeviceSettings.LoadBindings(path))=="ring","missing file defaults to ring");
        File.WriteAllText(path,"{\"SchemaVersion\":1,\"Theme\":\"dark\",\"MouseAlias\":\"保留名称\"}");var legacy=DeviceSettings.LoadBindings(path);Check(Layout(legacy)=="ring"&&legacy.Theme=="dark"&&legacy.MouseAlias=="保留名称","legacy file migrates only the layout default");
        foreach(string value in new[]{"ring","bar","grid","invalid","",null}){
            field.SetValue(legacy,value);DeviceSettings.SaveBindings(path,legacy);var loaded=DeviceSettings.LoadBindings(path);string expected=value=="bar"||value=="grid"?value:"ring";
            Check(Layout(loaded)==expected&&Layout(loaded.Clone())==expected,"layout round trip, clone and normalization: "+value);DeviceSettings.UseTransientConfiguration(loaded);Check(Layout(DeviceSettings.Current)==expected,"transient settings normalize layout too");
        }
        foreach(string value in new[]{"{}","[]","42","true","null"}){File.WriteAllText(path,"{\"SchemaVersion\":1,\"PanelLayout\":"+value+",\"MouseAlias\":\"保留名称\"}");var loaded=DeviceSettings.LoadBindings(path);Check(Layout(loaded)=="ring"&&loaded.MouseAlias=="保留名称","non-string layout falls back without discarding other fields: "+value);}
    }
    static void RingGeometry(){
        foreach(string font in new[]{"normal","large"})foreach(int count in new[]{4,5,8})foreach(bool headset in new[]{false,true})foreach(bool collapsed in new[]{false,true}){
            Mode(font,"ocean",collapsed,"ring");bool large=font=="large";int header=large?62:54,bottom=large?12:10;int height=header+VisualDesign.SelectionTrackHeight(count)+bottom;
            Check(VisualDesign.HeaderHeight==header&&VisualDesign.MouseCardHeight(Dpi(count))==height,"ring row and controls have specified heights");
            var ring=VisualDesign.BatteryRingRect(0);Check(ring.Width==(large?44:38)&&ring.Height==ring.Width&&ring.X==(large?12:11)&&ring.Y==(header-ring.Height)/2,"ring wraps its centered device icon on the left");
            Check(VisualDesign.HeaderTextRect(0,false).X==(large?66:58),"ring name starts after the gauge");
            Check(VisualDesign.StageTrackRect(0,count).Top==header&&VisualDesign.StageTrackRect(0,count).Left==(large?12:10),"ring controls follow the device row without a bar gap");
            int expected=height+1+(headset?(header+VisualDesign.SelectionHeight+bottom+1):0)+VisualDesign.SystemVolumeHeightFor(collapsed)+8+VisualDesign.FooterHeight;
            Check(VisualDesign.PanelSizeFor(headset,Dpi(count)).Height==expected,"ring calculated height includes separators and the external footer");
            using(var fixture=new Fixture()){
                fixture.Owner.Dpi=Dpi(count);if(!headset)fixture.Owner.Headset=null;var layout=WirelessLayout.Build(fixture.Owner,0,10000);Check(layout.Size==VisualDesign.PanelSizeFor(headset,fixture.Owner.Dpi),"native and wireless ring heights agree");
                var rows=PopupPages.Build(fixture.Owner,new PopupNavigation(),headset);Check(rows.Find(delegate(PopupControl row){return row.Id==2;}).Rect==new RectangleF(0,0,VisualDesign.PanelWidth,header),"entire ring row opens details");Check(!rows.Exists(delegate(PopupControl row){return row.Id==4||row.Id==5;}),"ring numbers belong to the detail row; refresh remains in the footer");
                if(count==4&&!collapsed)Console.WriteLine("ring "+font+" / headset="+headset+": "+expected);
            }
        }
    }
    static DpiConfig Dpi(int count=4){var dpi=new DpiConfig {DeviceKey=MouseBattery.DeviceKey("panel-design-fixture"),ActiveStage=1,TimestampUtc=DateTime.UtcNow};for(int i=0;i<count;i++)dpi.Stages.Add(new DpiStage {XDpi=400*(i+1),YDpi=400*(i+1)});return dpi;}
    static void GridBasics(){
        Check(PanelLayouts.Effective("grid")=="grid","grid must have its own geometry, not the phase-2 ring fallback");
        foreach(string font in new[]{"normal","large"}){Mode(font,"ocean",false,"grid");using(var fixture=new Fixture()){
            var owner=fixture.Owner;owner.GenericReady=true;var layout=WirelessLayout.Build(owner,0,10000);var rows=PopupPages.Build(owner,new PopupNavigation(),true,layout);
            int width=font=="large"?146:126,height=font=="large"?104:92;
            Check(rows.Find(delegate(PopupControl row){return row.Id==2;}).Rect==new RectangleF(0,0,width,height),"native mouse occupies the first grid tile");
            Check(rows.Find(delegate(PopupControl row){return row.Id==3;}).Rect==new RectangleF(width+8,0,width,height),"headset occupies the second column");
            Check(!rows.Exists(delegate(PopupControl row){return row.Id==4||row.Id==5;}),"grid tile is one direct detail action");
            var dpi=rows.Find(delegate(PopupControl row){return row.Id==10;});Check(dpi.Rect.Top>=height+8,"DPI belongs to the fixed quick controls below the grid");
        }}
    }
    static WirelessDevice GridDevice(int index,int stages=0){var values=new int[stages];for(int i=0;i<stages;i++)values[i]=400*(i+1);return new WirelessDevice {Id="grid-"+index,Name="设备 "+index,Kind=stages>0?"mouse":"keyboard",Percent=85,Fresh=true,Online=true,ConnectionState="connected",ConfirmedAtUtc=DateTime.UtcNow.ToString("o"),Dpi=stages>0?new WirelessDpi {Supported=true,CanSet=true,Current=800,Stages=values,TargetToken=new string((char)('a'+index%6),64),CheckedAtUtc=DateTime.UtcNow.ToString("o")}:null};}
    static void GridOverflowHeight(string font){
        Mode(font,"ocean",false,"grid");using(var fixture=new Fixture()){
            var owner=fixture.Owner;owner.GenericReady=true;owner.Dpi=Dpi(8);owner.WirelessDevices=new[]{GridDevice(0,8),GridDevice(1,8),GridDevice(2,8),GridDevice(3,8)};
            var layout=WirelessLayout.Build(owner,0,520);
            Console.WriteLine("grid overflow "+font+": height="+layout.Size.Height+", volume="+layout.VolumeRect+", footer="+layout.Footer);
            Check(layout.Size.Height<=520,"five mice x eight stages must fit the actual 520px available height");
            Check(layout.VolumeRect.Bottom<=520-VisualDesign.FooterHeight&&layout.Footer+VisualDesign.FooterHeight<=520,"system volume and footer stay visible at 520px");
        }
    }
    static Bitmap RenderGrid(TrayContext owner,WirelessLayout layout,bool dark,float scale,PanelFeedback feedback,GridFrameCapture coreCapture=null){
        var size=UiState.ScaleSize(layout.Size,scale);var image=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppPArgb);
        using(var g=Graphics.FromImage(image)){g.Clear(Color.Transparent);g.ScaleTransform(scale,scale);
            if(coreCapture!=null)coreCapture.GraphicsEntry=GridGraphicsInput(g);
            CompactRenderer.PaintWireless(g,owner,layout,dark,feedback);
            if(coreCapture!=null)coreCapture.GraphicsExit=GridGraphicsInput(g);
        }return image;
    }
    static bool GridSameArea(Bitmap left,Bitmap right,Rectangle area){
        if(area.Width<=0||area.Height<=0)return true;
        var a=left.LockBits(area,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);var b=right.LockBits(area,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try{var x=new byte[area.Width*4];var y=new byte[x.Length];for(int row=0;row<area.Height;row++){
            System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(a.Scan0,row*a.Stride),x,0,x.Length);System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(b.Scan0,row*b.Stride),y,0,y.Length);
            for(int i=0;i<x.Length;i++)if(x[i]!=y[i])return false;
        }return true;}finally{left.UnlockBits(a);right.UnlockBits(b);}
    }
    static string GridDifference(Bitmap left,Bitmap right,Rectangle area){
        int count=0,firstX=-1,firstY=-1,minX=area.Right,minY=area.Bottom,maxX=-1,maxY=-1;int leftArgb=0,rightArgb=0;
        for(int y=area.Top;y<area.Bottom;y++)for(int x=area.Left;x<area.Right;x++){
            int a=left.GetPixel(x,y).ToArgb(),b=right.GetPixel(x,y).ToArgb();if(a==b)continue;
            if(firstX<0){firstX=x;firstY=y;leftArgb=a;rightArgb=b;}count++;minX=Math.Min(minX,x);minY=Math.Min(minY,y);maxX=Math.Max(maxX,x);maxY=Math.Max(maxY,y);
        }
        return "GetPixel area="+area+" count="+count+(count==0?"":" first=("+firstX+","+firstY+") left=0x"+leftArgb.ToString("X8")+" right=0x"+rightArgb.ToString("X8")+" bounds=("+minX+","+minY+")- ("+maxX+","+maxY+")");
    }
    // Use exactly the LockBits format and row width used by GridSameArea, not GetPixel conversions.
    static string GridRawDifference(Bitmap left,Bitmap right,Rectangle area){
        string result="LockBits area="+area+" bitmapFormats="+left.PixelFormat+"/"+right.PixelFormat+" requested="+PixelFormat.Format32bppArgb;
        if(area.Width<=0||area.Height<=0)return result+" empty";
        BitmapData a=null,b=null;
        try{
            a=left.LockBits(area,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);b=right.LockBits(area,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
            result+=" lockedFormats="+a.PixelFormat+"/"+b.PixelFormat+" stride="+a.Stride+"/"+b.Stride+" rowBytes="+(area.Width*4);
            var x=new byte[area.Width*4];var y=new byte[x.Length];
            for(int row=0;row<area.Height;row++){
                System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(a.Scan0,row*a.Stride),x,0,x.Length);System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(b.Scan0,row*b.Stride),y,0,y.Length);
                for(int i=0;i<x.Length;i++)if(x[i]!=y[i])return result+" first=("+(area.Left+i/4)+","+(area.Top+row)+") row="+row+" rowByte="+i+" channel="+"BGRA"[i%4]+" scan0Offsets="+((long)row*a.Stride+i)+"/"+((long)row*b.Stride+i)+" left="+x[i]+" right="+y[i];
            }
            return result+" first=<none>";
        }finally{try{if(a!=null)left.UnlockBits(a);}finally{if(b!=null)right.UnlockBits(b);}}
    }
    static string GridText(string text){if(text==null)return "<null>";text=text.Replace("\r","\\r").Replace("\n","\\n");return text.Length<=192?text:text.Substring(0,192)+"<truncated>";}
    static string GridFeedback(PanelFeedback feedback){return feedback==null?"<null>":feedback.Key;}
    static string GridControl(PopupControl control){
        if(control==null)return "<null>";
        return "id="+control.Id+" deviceKey="+GridText(control.DeviceKey)+" rect="+control.Rect+" visible="+control.VisibleRect+" viewport="+(control.Viewport.HasValue?control.Viewport.Value.ToString():"<none>")+" enabled="+control.Enabled+" text="+GridText(control.Text)+" value="+GridText(control.Value);
    }
    static string GridFrameState(TrayContext owner,WirelessLayout layout,PanelFeedback feedback){
        try{
        var grid=layout.Grid;
        string state="utc="+DateTime.UtcNow.ToString("o")+" feedback="+GridText(GridFeedback(feedback))+" size="+layout.Size+" cards="+layout.Cards.Count+" all="+layout.All.Count+" controls="+grid.Controls+" viewport="+grid.Viewport+" scroll="+grid.ScrollOffset+"/"+grid.MaximumScroll+" groups="+grid.Groups.Count+" volume="+layout.VolumeRect+" footer="+layout.Footer+
            " owner(native="+owner.NativeMousePresent+",genericReady="+owner.GenericReady+",genericScanning="+owner.GenericScanning+",refresh="+owner.IsRefreshing+",audioBusy="+owner.IsAudioBusy+",headsetBusy="+owner.IsHeadsetRefreshing+",dpiBusy="+owner.IsDpiRefreshing+",CanSetNativeDpi="+owner.CanSetNativeDpi+",dpiError="+GridText(owner.DpiError)+",genericError="+GridText(owner.GenericError)+",health="+GridText(owner.GenericHealthSummary)+",idle="+GridText(owner.GenericIdleSummary)+")"+
            " fixtureBatteryUtc="+(owner.Current==null?"<null>":owner.Current.TimestampUtc.ToString("o"))+" lastGoodUtc="+(owner.LastGood==null?"<null>":owner.LastGood.TimestampUtc.ToString("o"))+" fixtureDpi="+(owner.Dpi==null?"<null>":owner.Dpi.TimestampUtc.ToString("o")+"/stage="+owner.Dpi.ActiveStage)+" headsetBatteryUtc="+(owner.Headset==null?"<null>":owner.Headset.BatteryTimestampUtc.ToString("o"))+" audioUtc="+(owner.Audio==null?"<null>":owner.Audio.TimestampUtc.ToString("o"));
        // The fixture has at most six objects; cap unexpected input and individual free-text fields.
        for(int i=0;i<Math.Min(8,layout.All.Count);i++){
            var card=layout.All[i];var device=card.Device;var battery=card.Battery;var dpi=device==null?null:device.Dpi;bool sleeping=device!=null&&(device.ConnectionState=="sleeping"||device.MeasurementState=="sleeping");
            state+="; object"+i+"="+GridText(card.Id)+"/visible="+layout.Cards.Contains(card)+"/status="+GridText(GridLayout.Status(battery,sleeping))+"/battery="+(battery==null?"<null>":battery.Percent+"/fresh="+battery.Fresh+"/charging="+battery.Charging+"/confirmedUtc="+battery.ConfirmedAt.ToString("o"))+"/title="+GridText(card.Name)+"/subtitle="+GridText(card.Subtitle);
            if(device!=null)state+="/rawBattery="+device.Percent+"/fresh="+device.Fresh+"/online="+device.Online+"/connection="+GridText(device.ConnectionState)+"/measurement="+GridText(device.MeasurementState)+"/batteryConfirmedUtc="+GridText(device.ConfirmedAtUtc)+"/batteryMeasuredUtc="+GridText(device.MeasuredAtUtc);
            if(dpi!=null){var pending=owner.DesiredWirelessDpi(card.Id,dpi.TargetToken);string failure=owner.WirelessDpiFailure(card.Id,dpi.TargetToken);state+="/dpiUtc="+GridText(dpi.CheckedAtUtc)+"/dpiCurrent="+dpi.Current+"/dpiFresh="+WirelessLayout.DpiFresh(device)+"/supported="+dpi.Supported+"/CanSet="+dpi.CanSet+"/CanSetWirelessDpi="+owner.CanSetWirelessDpi(card.Id,dpi.TargetToken)+"/pending="+pending+"/failure="+GridText(failure)+"/dpiError="+GridText(dpi.Error)+"/dpiLabel="+GridText(WirelessLayout.DpiSubtitle(device,pending,failure??dpi.Error));}
        }
        return state+(layout.All.Count>8?"; objectsOmitted="+(layout.All.Count-8):"");
        }catch(Exception error){return "STATE_CAPTURE_FAILED utc="+DateTime.UtcNow.ToString("o")+" "+GridText(error.Message);}
    }
    [DataContract] sealed class GridFrameCapture {
        [DataMember] internal string Before,After,GraphicsEntry,GraphicsExit;
        [DataMember] internal bool DrawingCapture;
        [DataMember] internal List<string> Events=new List<string>();
        internal void Record(Func<string> capture){try{Events.Add(Events.Count+" "+capture());}catch(Exception error){Events.Add("CAPTURE_FAILED "+GridText(error.Message));}}
        public override string ToString(){return "before={"+Before+"}"+Environment.NewLine+"after={"+After+"}";}
    }
    static string GridFloat(float value){return value.ToString("R",CultureInfo.InvariantCulture);}
    static string GridRect(RectangleF rect){return "["+GridFloat(rect.X)+","+GridFloat(rect.Y)+","+GridFloat(rect.Width)+","+GridFloat(rect.Height)+"]";}
    static string GridGraphicsInput(Graphics g){
        using(var transform=g.Transform)using(var clip=g.Clip){
            return "transform=["+String.Join(",",Array.ConvertAll(transform.Elements,GridFloat))+"] clip="+GridRect(g.ClipBounds)+" clipData="+Convert.ToBase64String(clip.GetRegionData().Data)+" infinite="+clip.IsInfinite(g)+" empty="+g.IsClipEmpty+" visibleClip="+GridRect(g.VisibleClipBounds)+" visibleEmpty="+g.IsVisibleClipEmpty+
                " dpi="+GridFloat(g.DpiX)+"/"+GridFloat(g.DpiY)+" page="+g.PageUnit+"/"+GridFloat(g.PageScale)+" renderingOrigin="+g.RenderingOrigin+" hint="+g.TextRenderingHint+" contrast="+g.TextContrast+" pixelOffset="+g.PixelOffsetMode+" smoothing="+g.SmoothingMode+" compositing="+g.CompositingMode+"/"+g.CompositingQuality+" interpolation="+g.InterpolationMode;
        }
    }
    static Bitmap RenderGridObserved(TrayContext owner,WirelessLayout layout,bool dark,float scale,PanelFeedback feedback,out GridFrameCapture state,bool captureDrawing=true,bool captureCoreGraphics=false){
        var capture=new GridFrameCapture {Before=GridFrameState(owner,layout,feedback),DrawingCapture=captureDrawing};state=capture;
        if(!captureDrawing){var unobserved=RenderGrid(owner,layout,dark,scale,feedback,captureCoreGraphics?capture:null);capture.After=GridFrameState(owner,layout,feedback);return unobserved;}
        var size=UiState.ScaleSize(layout.Size,scale);var image=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppPArgb);
        var oldRaster=CompactRenderer.TextRasterObserver;var oldBounds=InteractionLayout.TextBoundsObserver;var oldRegion=InteractionLayout.Observer;
        try{using(var g=Graphics.FromImage(image)){
            g.Clear(Color.Transparent);g.ScaleTransform(scale,scale);
            capture.Record(delegate{return "frame-entry "+GridGraphicsInput(g);});capture.GraphicsEntry=capture.Events[capture.Events.Count-1];
            if(captureDrawing){
                CompactRenderer.TextRasterObserver=delegate(string stage,Graphics drawing,string text,RectangleF rect,Font font,Color color,StringFormat format){
                    capture.Record(delegate{return stage+" measuring="+InteractionLayout.Measuring+" text="+text+" rect="+GridRect(rect)+" font="+font.Name+"/family="+font.FontFamily.Name+"/size="+GridFloat(font.Size)+"/unit="+font.Unit+"/style="+font.Style+"/charset="+font.GdiCharSet+"/vertical="+font.GdiVerticalFont+" color=0x"+color.ToArgb().ToString("X8")+" format="+format.Alignment+"/"+format.LineAlignment+"/"+format.Trimming+"/"+format.FormatFlags+"/digit="+format.DigitSubstitutionLanguage+"/"+format.DigitSubstitutionMethod+"/hotkey="+format.HotkeyPrefix+" "+GridGraphicsInput(drawing);});
                    if(oldRaster!=null)oldRaster(stage,drawing,text,rect,font,color,format);
                };
                InteractionLayout.TextBoundsObserver=delegate(Graphics drawing,string key,RectangleF rect,RectangleF ink,bool hit){
                    capture.Record(delegate{return "measure-result hit="+hit+" key="+key+" rect="+GridRect(rect)+" relativeInk="+GridRect(ink)+" measuring="+InteractionLayout.Measuring+" "+GridGraphicsInput(drawing);});
                    if(oldBounds!=null)oldBounds(drawing,key,rect,ink,hit);
                };
                InteractionLayout.Observer=delegate(InteractionRegion region){
                    capture.Record(delegate{return "interaction id="+region.Id+" content="+GridRect(region.Content)+" bounds="+GridRect(region.Bounds)+" card="+GridRect(region.Card)+" limit="+GridRect(region.Limit)+" padding="+GridFloat(region.Padding)+" radius="+GridFloat(region.Radius)+" enabled="+region.Enabled+" "+GridGraphicsInput(g);});
                    if(oldRegion!=null)oldRegion(region);
                };
            }
            CompactRenderer.PaintWireless(g,owner,layout,dark,feedback);
            capture.Record(delegate{return "frame-exit "+GridGraphicsInput(g);});capture.GraphicsExit=capture.Events[capture.Events.Count-1];
        }capture.After=GridFrameState(owner,layout,feedback);return image;
        }catch{image.Dispose();throw;}
        finally{CompactRenderer.TextRasterObserver=oldRaster;InteractionLayout.TextBoundsObserver=oldBounds;InteractionLayout.Observer=oldRegion;}
    }
    [DataContract] sealed class GridPixel {
        [DataMember] internal int X,Y;
        [DataMember] internal string Left,Right;
    }
    [DataContract] sealed class GridFailureArtifact {
        [DataMember] internal string Label,Parameters,Hidden,Area,Raw,Summary,PixelBounds;
        [DataMember] internal GridFrameCapture LeftFrame,RightFrame;
        [DataMember] internal List<GridPixel> Pixels=new List<GridPixel>();
        [DataMember] internal List<string> Environment=new List<string>();
    }
    static string GridFileEvidence(string path){
        try{using(var stream=File.OpenRead(path))using(var hash=SHA256.Create())return path+" sha256="+BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","");}
        catch(Exception error){return path+" UNAVAILABLE "+GridText(error.Message);}
    }
    static void GridSaveDiagnostic(string label,string parameters,GridFrameCapture leftState,GridFrameCapture rightState,PopupControl hidden,Bitmap left,Bitmap right,Rectangle area,string raw,string summary,string outputRoot=null){
        var data=new GridFailureArtifact {Label=label,Parameters=parameters,Hidden=GridControl(hidden),Area=area.ToString(),Raw=raw,Summary=summary,LeftFrame=leftState,RightFrame=rightState};
        int minX=area.Right,minY=area.Bottom,maxX=-1,maxY=-1;
        for(int y=area.Top;y<area.Bottom;y++)for(int x=area.Left;x<area.Right;x++){
            int a=left.GetPixel(x,y).ToArgb(),b=right.GetPixel(x,y).ToArgb();if(a==b)continue;
            data.Pixels.Add(new GridPixel {X=x,Y=y,Left="0x"+a.ToString("X8"),Right="0x"+b.ToString("X8")});minX=Math.Min(minX,x);minY=Math.Min(minY,y);maxX=Math.Max(maxX,x);maxY=Math.Max(maxY,y);
        }
        var bounds=data.Pixels.Count==0?area:Rectangle.FromLTRB(minX,minY,maxX+1,maxY+1);data.PixelBounds=bounds.ToString();
        data.Environment.Add("utc="+DateTime.UtcNow.ToString("o")+" pid="+System.Diagnostics.Process.GetCurrentProcess().Id+" reportedOS="+System.Environment.OSVersion+" CLR="+System.Environment.Version+" x64="+System.Environment.Is64BitProcess+" culture="+CultureInfo.CurrentCulture.Name+" uiCulture="+CultureInfo.CurrentUICulture.Name+" highContrast="+VisualDesign.HighContrast+" GITHUB_SHA="+System.Environment.GetEnvironmentVariable("GITHUB_SHA"));
        // An unmanifested .NET Framework test reports Windows 6.2 even on Windows 11. Read the build without changing system configuration.
        try{using(var os=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))data.Environment.Add("windowsBuild="+os.GetValue("CurrentBuildNumber")+"."+os.GetValue("UBR")+" displayVersion="+os.GetValue("DisplayVersion"));}catch(Exception error){data.Environment.Add("OS_BUILD_CAPTURE_FAILED "+GridText(error.Message));}
        data.Environment.Add("bitmaps="+left.Size+"/"+left.PixelFormat+"/dpi="+GridFloat(left.HorizontalResolution)+"/"+GridFloat(left.VerticalResolution)+" | "+right.Size+"/"+right.PixelFormat+"/dpi="+GridFloat(right.HorizontalResolution)+"/"+GridFloat(right.VerticalResolution));
        foreach(string source in new[]{"tests/PanelDesignTests.cs","scripts/CompactRenderer.cs","scripts/InteractionLayout.cs","scripts/VisualDesign.cs","scripts/GridLayout.cs","scripts/WirelessLayout.cs","scripts/ThemePalette.cs"})data.Environment.Add(GridFileEvidence(Path.GetFullPath(source)));
        data.Environment.Add(GridFileEvidence(typeof(PanelDesignTests).Assembly.Location));
        foreach(string font in new[]{"segoeui.ttf","msyh.ttc"})data.Environment.Add(GridFileEvidence(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Fonts),font)));
        try{foreach(System.Diagnostics.ProcessModule module in System.Diagnostics.Process.GetCurrentProcess().Modules)if(String.Equals(module.ModuleName,"gdiplus.dll",StringComparison.OrdinalIgnoreCase))data.Environment.Add("gdiplus="+module.FileVersionInfo.FileVersion+" "+GridFileEvidence(module.FileName));}catch(Exception error){data.Environment.Add("MODULE_CAPTURE_FAILED "+GridText(error.Message));}
        string runnerTemp=System.Environment.GetEnvironmentVariable("RUNNER_TEMP");
        string root=outputRoot??(String.IsNullOrEmpty(runnerTemp)?Path.Combine(Directory.GetCurrentDirectory(),"tmp","grid-pixel-diagnostics"):Path.Combine(runnerTemp,"myfinder-grid-pixel-diagnostics"));
        string directory=Path.Combine(root,System.Diagnostics.Process.GetCurrentProcess().Id+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        left.Save(Path.Combine(directory,"left.png"),ImageFormat.Png);right.Save(Path.Combine(directory,"right.png"),ImageFormat.Png);
        using(var stream=File.Create(Path.Combine(directory,"difference.json")))new DataContractJsonSerializer(typeof(GridFailureArtifact)).WriteObject(stream,data);
        // Originals and assertions keep their full areas. This is only an 8x nearest-neighbor view of the difference bounds: left | right | magenta map.
        const int zoom=8;using(var detail=new Bitmap(bounds.Width*3+2,bounds.Height,PixelFormat.Format32bppPArgb)){
            for(int y=0;y<bounds.Height;y++)for(int x=0;x<bounds.Width;x++){
                var a=left.GetPixel(bounds.Left+x,bounds.Top+y);var b=right.GetPixel(bounds.Left+x,bounds.Top+y);
                detail.SetPixel(x,y,a);detail.SetPixel(bounds.Width+1+x,y,b);detail.SetPixel((bounds.Width+1)*2+x,y,a.ToArgb()==b.ToArgb()?Color.Black:Color.Magenta);
            }
            using(var enlarged=new Bitmap(detail.Width*zoom,detail.Height*zoom,PixelFormat.Format32bppPArgb))using(var g=Graphics.FromImage(enlarged)){
                g.CompositingMode=CompositingMode.SourceCopy;g.InterpolationMode=InterpolationMode.NearestNeighbor;g.PixelOffsetMode=PixelOffsetMode.Half;g.DrawImage(detail,new Rectangle(0,0,enlarged.Width,enlarged.Height),0,0,detail.Width,detail.Height,GraphicsUnit.Pixel);enlarged.Save(Path.Combine(directory,"difference-8x.png"),ImageFormat.Png);
            }
        }
        Console.Error.WriteLine("GRID_DIAGNOSTIC_ARTIFACT "+directory);
    }
    static void GridDiagnostic(string label,string parameters,GridFrameCapture leftState,GridFrameCapture rightState,PopupControl hidden,Bitmap left,Bitmap right,Rectangle area){
        // No temporary-file dependency in CI. Diagnostic failures must never replace the caller's Check.
        try{
            string raw,pixels;try{raw=GridRawDifference(left,right,area);}catch(Exception error){raw="RAW_DIFF_FAILED "+GridText(error.Message);}
            try{pixels=GridDifference(left,right,area);}catch(Exception error){pixels="GETPIXEL_DIFF_FAILED "+GridText(error.Message);}
            Console.Error.WriteLine("GRID_DIAGNOSTIC label="+label+Environment.NewLine+"parameters="+parameters+Environment.NewLine+"hidden="+GridControl(hidden)+Environment.NewLine+"left="+leftState+Environment.NewLine+"right="+rightState+Environment.NewLine+"raw="+raw+Environment.NewLine+"diff="+pixels);
            try{GridSaveDiagnostic(label,parameters,leftState,rightState,hidden,left,right,area,raw,pixels);}catch(Exception error){Console.Error.WriteLine("GRID_DIAGNOSTIC_ARTIFACT_FAILED "+GridText(error.Message));}
        }catch(Exception){/* Preserve the original assertion even if stderr itself is unavailable. */}
    }
    static void GridRequireSame(string label,string parameters,GridFrameCapture leftState,GridFrameCapture rightState,Bitmap left,Bitmap right,Rectangle area){
        bool same=GridSameArea(left,right,area);if(!same)GridDiagnostic(label,parameters,leftState,rightState,null,left,right,area);Check(same,label);
    }
    static void GridRepaintIdentity(){
        Mode("normal","ocean",true,"grid");using(var fixture=new Fixture()){
            var owner=fixture.Owner;owner.GenericReady=true;owner.NativeMousePresent=true;owner.Headset=null;owner.Audio=null;owner.Dpi=Dpi(8);
            var devices=new List<WirelessDevice>();for(int i=0;i<4;i++){var device=GridDevice(i,8);device.Id="device-"+i.ToString().PadLeft(24,'0');device.Provider="logitech";device.SourceQuality="verified";device.MeasurementState="measured";device.Dpi.StageSource="onboard";devices.Add(device);}owner.WirelessDevices=devices.ToArray();
            typeof(TrayContext).GetField("nativeNeedsReconcile",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(owner,false);
            var layout=WirelessLayout.Build(owner,0,520);GridFrameCapture firstState;
            // Retain the very first frame. The explicit --grid-repaint entry runs this in a fresh process; no unasserted font warmup.
            using(var first=RenderGridObserved(owner,layout,false,1.5f,new PanelFeedback(),out firstState)){
                var full=new Rectangle(Point.Empty,first.Size);
                foreach(bool captureDrawing in new[]{false,true}){
                    GridFrameCapture state;using(var next=RenderGridObserved(owner,layout,false,1.5f,new PanelFeedback(),out state,captureDrawing))
                        GridRequireSame("same layout redraw preserves every pixel, with capture off/on","font=normal,native=True,audio=False,collapsed=True,scale=1.5,dark=False,offset=0,capture="+captureDrawing,firstState,state,first,next,full);
                }
                foreach(int offset in new[]{0,31}){
                    var rebuilt=WirelessLayout.Build(owner,0,520,false,offset);PopupPages.Build(owner,new PopupNavigation(),false,rebuilt);GridFrameCapture state;
                    using(var next=RenderGridObserved(owner,rebuilt,false,1.5f,new PanelFeedback(),out state)){
                        string parameters="font=normal,native=True,audio=False,collapsed=True,scale=1.5,dark=False,offset="+offset;
                        if(offset==0)GridRequireSame("rebuilt same-state layout preserves every pixel",parameters,firstState,state,first,next,full);
                        else{
                            GridRequireSame("scrolling preserves the complete device area after same-state redraws",parameters,firstState,state,first,next,new Rectangle(0,0,first.Width,(int)(layout.Grid.Controls.Top*1.5f)));
                            int fixedTop=(int)Math.Ceiling(layout.VolumeTop*1.5f);GridRequireSame("scrolling preserves the complete fixed volume and footer",parameters,firstState,state,first,next,new Rectangle(0,fixedTop,first.Width,first.Height-fixedTop));
                        }
                    }
                }
            }
        }
    }
    // This optional entry is independent of the unchanged full Panel matrix. All bitmaps,
    // including the first one in each process, remain comparison inputs; there is no warmup.
    [DataContract] sealed class GridProbeComparison {
        [DataMember] internal int LeftFrame,RightFrame;
        [DataMember] internal string Label,Area;
        [DataMember] internal bool Same;
        [DataMember] internal double ElapsedMs;
    }
    [DataContract] sealed class GridProbeFrame {
        [DataMember] internal int Number,Offset,Width,Height;
        [DataMember] internal string Label,Sha256,BaselinePng,PixelFormat;
        [DataMember] internal float Scale,DpiX,DpiY;
        [DataMember] internal bool Dark,RegionObserver;
        [DataMember] internal double DrawMs,ElapsedMs;
        [DataMember] internal GridFrameCapture State;
        [DataMember] internal List<GridProbeComparison> Comparisons=new List<GridProbeComparison>();
    }
    [DataContract] sealed class GridProbeResult {
        [DataMember] internal int Schema=1,ProcessId,FrameLimit=32,BudgetMs,DrawsStarted;
        [DataMember] internal string Case,Status="running",Error,ArtifactError,StartedUtc,Parameters,FinalFrameSha256;
        [DataMember] internal double ElapsedMs;
        [DataMember] internal List<GridProbeFrame> Frames=new List<GridProbeFrame>();
        [DataMember] internal List<string> Environment=new List<string>();
    }
    sealed class GridProbeMismatch:Exception {internal GridProbeMismatch(string message):base(message){}}
    sealed class GridProbeImage:IDisposable {
        internal Bitmap Image;
        internal GridProbeFrame Frame;
        public void Dispose(){Image.Dispose();}
    }
    static void GridProbeJson(string path,object value){using(var stream=File.Create(path))new DataContractJsonSerializer(value.GetType()).WriteObject(stream,value);}
    sealed class GridProbe {
        internal readonly GridProbeResult Result;
        internal readonly string DirectoryPath;
        readonly System.Diagnostics.Stopwatch clock=System.Diagnostics.Stopwatch.StartNew();
        internal GridProbe(string name,string output,int budget){
            DirectoryPath=Path.GetFullPath(output);Directory.CreateDirectory(DirectoryPath);
            Result=new GridProbeResult {Case=name,BudgetMs=budget,ProcessId=System.Diagnostics.Process.GetCurrentProcess().Id,StartedUtc=DateTime.UtcNow.ToString("o"),Parameters="font=normal,native=True,audio=False,collapsed=True,theme=ocean; cold=1.5/light; prefix=1.25/light,1.25/dark,1.5/light"};
            Save();
        }
        internal void Budget(){if(clock.ElapsedMilliseconds>=Result.BudgetMs)throw new TimeoutException("Per-process diagnostic deadline exceeded");}
        internal void Save(){Result.ElapsedMs=clock.Elapsed.TotalMilliseconds;GridProbeJson(Path.Combine(DirectoryPath,"result.json"),Result);}
        void Journal(string name,object value){
            using(var stream=new FileStream(Path.Combine(DirectoryPath,name+".jsonl"),FileMode.Append,FileAccess.Write,FileShare.Read)){
                new DataContractJsonSerializer(value.GetType()).WriteObject(stream,value);stream.WriteByte(10);
            }
        }
        internal GridProbeImage Draw(TrayContext owner,WirelessLayout layout,bool dark,float scale,PanelFeedback feedback,bool capture,string label,bool baseline=false){
            Budget();if(Result.DrawsStarted>=Result.FrameLimit)throw new InvalidOperationException("32-frame diagnostic cap exceeded");
            int number=++Result.DrawsStarted;Console.WriteLine("GRID_PROBE_FRAME_START "+number+" "+label+" capture="+capture);
            var drawClock=System.Diagnostics.Stopwatch.StartNew();GridFrameCapture state;
            var image=RenderGridObserved(owner,layout,dark,scale,feedback,out state,capture,true);
            try{
                var frame=new GridProbeFrame {Number=number,Label=label,Scale=scale,Dark=dark,Offset=layout.Grid.ScrollOffset,Width=image.Width,Height=image.Height,PixelFormat=image.PixelFormat.ToString(),DpiX=image.HorizontalResolution,DpiY=image.VerticalResolution,DrawMs=drawClock.Elapsed.TotalMilliseconds,ElapsedMs=clock.Elapsed.TotalMilliseconds,State=state,RegionObserver=InteractionLayout.Observer!=null};
                // Hash the same unpadded LockBits rows used by GridSameArea, not PNG encoder bytes.
                var bits=image.LockBits(new Rectangle(Point.Empty,image.Size),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
                try{using(var hash=SHA256.Create()){
                    var row=new byte[image.Width*4];for(int y=0;y<image.Height;y++){
                        System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(bits.Scan0,y*bits.Stride),row,0,row.Length);hash.TransformBlock(row,0,row.Length,row,0);
                    }
                    hash.TransformFinalBlock(new byte[0],0,0);frame.Sha256=BitConverter.ToString(hash.Hash).Replace("-","");
                }}finally{image.UnlockBits(bits);}
                Result.Frames.Add(frame);Result.FinalFrameSha256=frame.Sha256;
                if(number==1||baseline){frame.BaselinePng=number==1?"first.png":"baseline-"+number+".png";image.Save(Path.Combine(DirectoryPath,frame.BaselinePng),ImageFormat.Png);}
                if(number==1){
                    Result.Environment.Add("reportedOS="+System.Environment.OSVersion+" CLR="+System.Environment.Version+" x64="+System.Environment.Is64BitProcess+" culture="+CultureInfo.CurrentCulture.Name+" uiCulture="+CultureInfo.CurrentUICulture.Name+" highContrast="+VisualDesign.HighContrast);
                    foreach(System.Diagnostics.ProcessModule module in System.Diagnostics.Process.GetCurrentProcess().Modules)
                        if(String.Equals(module.ModuleName,"gdiplus.dll",StringComparison.OrdinalIgnoreCase))Result.Environment.Add("gdiplus="+module.FileVersionInfo.FileVersion+" "+GridFileEvidence(module.FileName));
                }
                Journal("frames",frame);Budget();return new GridProbeImage {Image=image,Frame=frame};
            }catch{image.Dispose();throw;}
        }
        internal void Compare(GridProbeImage left,GridProbeImage right,Rectangle area,string label,PopupControl hidden=null){
            Budget();bool same=GridSameArea(left.Image,right.Image,area);
            var comparison=new GridProbeComparison {LeftFrame=left.Frame.Number,RightFrame=right.Frame.Number,Label=label,Area=area.ToString(),Same=same,ElapsedMs=clock.Elapsed.TotalMilliseconds};
            right.Frame.Comparisons.Add(comparison);Journal("comparisons",comparison);
            if(!same){
                try{GridSaveDiagnostic(label,Result.Parameters+",case="+Result.Case+",scale="+GridFloat(right.Frame.Scale)+",dark="+right.Frame.Dark+",offset="+right.Frame.Offset+",capture="+right.Frame.State.DrawingCapture+",frames="+left.Frame.Number+"/"+right.Frame.Number,left.Frame.State,right.Frame.State,hidden,left.Image,right.Image,area,GridRawDifference(left.Image,right.Image,area),GridDifference(left.Image,right.Image,area),Path.Combine(DirectoryPath,"differences"));}
                catch(Exception error){Result.ArtifactError=error.ToString();Console.Error.WriteLine("GRID_PROBE_ARTIFACT_FAILED "+GridText(error.Message));}
                throw new GridProbeMismatch(label+" (frames "+left.Frame.Number+"/"+right.Frame.Number+")");
            }
            Budget();
        }
        internal void FixedAreas(GridProbeImage left,GridProbeImage right,WirelessLayout baseline){
            Compare(left,right,new Rectangle(0,0,left.Image.Width,(int)(baseline.Grid.Controls.Top*right.Frame.Scale)),"complete device area");
            int top=(int)Math.Ceiling(baseline.VolumeTop*right.Frame.Scale);Compare(left,right,new Rectangle(0,top,left.Image.Width,left.Image.Height-top),"complete fixed volume/footer");
        }
    }
    static void GridProbeCold(GridProbe probe,TrayContext owner,WirelessLayout first,bool capture){
        using(var baseline=probe.Draw(owner,first,false,1.5f,new PanelFeedback(),capture,"cold first")){
            for(int i=1;i<32;i++){
                int offset=i>=16&&i<24?31:0;bool rebuild=i>=8&&i<24;bool observed=i>=24?!capture:capture;
                var layout=rebuild?WirelessLayout.Build(owner,0,520,false,offset):first;
                if(rebuild)PopupPages.Build(owner,new PopupNavigation(),false,layout);
                string label=(rebuild?"Build offset="+offset:"same layout")+(i>=24?" capture toggled":"");
                using(var next=probe.Draw(owner,layout,false,1.5f,new PanelFeedback(),observed,label)){
                    if(offset==31)probe.FixedAreas(baseline,next,first);
                    else probe.Compare(baseline,next,new Rectangle(Point.Empty,baseline.Image.Size),"complete same-state frame");
                }
            }
        }
    }
    static void GridProbeMatrixPrefix(GridProbe probe,TrayContext owner,WirelessLayout first,bool capture){
        // A contiguous suffix of the real matrix before 1.5/light. Earlier 1x history
        // cannot also fit the 32-frame cap; no hand-made label measure or cache clearing.
        WirelessLayout.Build(owner,0,10000);
        for(int step=0;step<3;step++){
            float scale=step==2?1.5f:1.25f;bool dark=step==1;string prefix="matrix scale="+GridFloat(scale)+" dark="+dark;
            using(var baseline=probe.Draw(owner,first,dark,scale,new PanelFeedback(),capture,prefix+" baseline",true))foreach(int offset in new[]{0,31,Int32.MaxValue}){
                var layout=WirelessLayout.Build(owner,0,520,false,offset);var rows=PopupPages.Build(owner,new PopupNavigation(),false,layout);var regions=new List<InteractionRegion>();
                Check(layout.Size.Height==520&&layout.Grid.Controls==first.Grid.Controls&&layout.VolumeRect==first.VolumeRect&&layout.Footer==first.Footer,"probe preserves matrix layout regions");
                InteractionLayout.Observer=regions.Add;
                try{using(var image=probe.Draw(owner,layout,dark,scale,new PanelFeedback(),capture,prefix+" offset="+offset)){
                    probe.FixedAreas(baseline,image,first);
                    if(offset==0)probe.Compare(baseline,image,new Rectangle(Point.Empty,image.Image.Size),"rebuilt offset0 complete same-state frame");
                }}finally{InteractionLayout.Observer=null;}
                // Preserve the original between-paint hit/visibility sequence too.
                foreach(var row in rows){
                    var visible=row.VisibleRect;if(row.Information||row.Id<0)continue;
                    var region=regions.Find(delegate(InteractionRegion item){return item.Id==row.Id;});
                    Check(visible.IsEmpty?region==null:region!=null&&region.Limit==visible,"probe matrix clipped draw/hit bounds agree");
                    if(row.Enabled&&!visible.IsEmpty)Check(PopupPages.Hit(rows,new PointF(visible.X+visible.Width/2,visible.Y+visible.Height/2))==row,"probe matrix visible controls remain clickable");
                }
                var partial=rows.Find(delegate(PopupControl row){return row.Viewport.HasValue&&row.Enabled&&!row.Information&&!row.VisibleRect.IsEmpty&&row.VisibleRect!=row.Rect;});
                if(partial!=null){var point=new PointF(partial.Rect.X+partial.Rect.Width/2,partial.Rect.Top<partial.VisibleRect.Top?partial.Rect.Top+.25f:partial.Rect.Bottom-.25f);Check(PopupPages.Hit(rows,point)!=partial,"probe matrix cropped control cannot be hit");}
                var hidden=rows.Find(delegate(PopupControl row){return row.Viewport.HasValue&&!row.Information&&row.VisibleRect.IsEmpty;});
                Check(hidden!=null,"probe matrix must exercise an actually hidden control");
                using(var ordinary=probe.Draw(owner,layout,dark,scale,new PanelFeedback(),capture,prefix+" ordinary offset="+offset))
                using(var hovered=probe.Draw(owner,layout,dark,scale,new PanelFeedback {HoverAction=hidden.Id,PressedAction=hidden.Id,KeyboardFocus=hidden.Id},capture,prefix+" hidden feedback offset="+offset)){
                    probe.Compare(ordinary,hovered,new Rectangle(Point.Empty,ordinary.Image.Size),"original offscreen-only feedback complete frame",hidden);
                    probe.FixedAreas(baseline,ordinary,first);
                }
            }
        }
    }
    static int GridRepaintProbe(string[] args){
        if(args.Length!=7||args[1]!="--case"||args[3]!="--output"||args[5]!="--budget-ms")throw new ArgumentException("--grid-repaint-probe --case cold-off|cold-on|matrix-prefix-off|matrix-prefix-on --output DIRECTORY --budget-ms 1..35000");
        string name=args[2];if(Array.IndexOf(new[]{"cold-off","cold-on","matrix-prefix-off","matrix-prefix-on"},name)<0)throw new ArgumentException("Unknown diagnostic case");
        int budget;if(!Int32.TryParse(args[6],out budget)||budget<1||budget>35000)throw new ArgumentException("Invalid diagnostic budget");
        var probe=new GridProbe(name,args[4],budget);int exit=2;
        try{
            Mode("normal","ocean",true,"grid");using(var fixture=new Fixture()){
                probe.Budget();var owner=fixture.Owner;owner.GenericReady=true;owner.NativeMousePresent=true;owner.Headset=null;owner.Audio=null;owner.Dpi=Dpi(8);
                var devices=new List<WirelessDevice>();for(int i=0;i<4;i++){var device=GridDevice(i,8);device.Id="device-"+i.ToString().PadLeft(24,'0');device.Provider="logitech";device.SourceQuality="verified";device.MeasurementState="measured";device.Dpi.StageSource="onboard";devices.Add(device);}owner.WirelessDevices=devices.ToArray();
                typeof(TrayContext).GetField("nativeNeedsReconcile",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(owner,false);
                var layout=WirelessLayout.Build(owner,0,520);bool capture=name.EndsWith("-on",StringComparison.Ordinal);
                if(name.StartsWith("cold-",StringComparison.Ordinal))GridProbeCold(probe,owner,layout,capture);else GridProbeMatrixPrefix(probe,owner,layout,capture);
            }
            probe.Budget();probe.Result.Status="not_reproduced";exit=0;
        }catch(GridProbeMismatch error){probe.Result.Status="difference";probe.Result.Error=error.Message;exit=1;}
        catch(TimeoutException error){probe.Result.Status="timeout";probe.Result.Error=error.Message;exit=124;}
        catch(Exception error){probe.Result.Status="error";probe.Result.Error=error.ToString();exit=2;}
        finally{probe.Save();Console.WriteLine("GRID_PROBE_RESULT case="+name+" status="+probe.Result.Status+" frames="+probe.Result.Frames.Count+" elapsedMs="+probe.Result.ElapsedMs.ToString("F1",CultureInfo.InvariantCulture));}
        return exit;
    }
    static void GridOverflowViewportMatrix(){
        foreach(string font in new[]{"normal","large"})foreach(bool native in new[]{false,true})foreach(bool audio in new[]{false,true})foreach(bool collapsed in new[]{false,true}){
            Mode(font,"ocean",collapsed,"grid");using(var fixture=new Fixture()){
                var owner=fixture.Owner;owner.GenericReady=true;owner.NativeMousePresent=native;owner.Dpi=Dpi(8);var devices=new List<WirelessDevice>();
                for(int i=0;i<(native?4:5);i++){var device=GridDevice(i,8);device.Id="device-"+i.ToString().PadLeft(24,'0');device.Provider="logitech";device.SourceQuality="verified";device.MeasurementState="measured";device.Dpi.StageSource="onboard";devices.Add(device);}owner.WirelessDevices=devices.ToArray();
                typeof(TrayContext).GetField("nativeNeedsReconcile",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(owner,false);
                if(!audio){owner.Headset=null;owner.Audio=null;}
                var first=WirelessLayout.Build(owner,0,520);var normal=WirelessLayout.Build(owner,0,10000);
                Check(first.Grid.Overflow&&first.Grid.VisibleRows==1&&first.Cards.Count==2,"overflow reserves a complete tile row, not a zero-height device viewport");
                Check(normal.Grid.MaximumScroll==0&&normal.Grid.ScrollOffset==0&&normal.Grid.Viewport.Bottom==normal.VolumeTop,"normal height keeps every group and introduces no scrolling");
                Check(first.Grid.Groups.Count==(audio?6:5)&&first.Grid.Groups.TrueForAll(delegate(GridControlGroup group){return group.Audio||group.Count==8;}),"all five eight-stage mice and optional audio group remain present");
                foreach(float scale in new[]{1f,1.25f,1.5f,2f})foreach(bool dark in new[]{false,true}){
                    GridFrameCapture baselineState;using(var baseline=RenderGridObserved(owner,first,dark,scale,new PanelFeedback(),out baselineState))foreach(int offset in new[]{0,31,Int32.MaxValue}){
                        var layout=WirelessLayout.Build(owner,0,520,false,offset);var rows=PopupPages.Build(owner,new PopupNavigation(),audio,layout);var regions=new List<InteractionRegion>();
                        Check(layout.Size.Height==520&&layout.Grid.Controls==first.Grid.Controls&&layout.VolumeRect==first.VolumeRect&&layout.Footer==first.Footer,"scrolling never moves the bounded card, system volume or footer");
                        Check(rows.FindAll(delegate(PopupControl row){return row.DpiValue>0||row.Id>=10&&row.Id<18;}).Count==40,"no DPI stages are discarded by clipping");
                        int enabled=rows.FindAll(delegate(PopupControl row){return row.Enabled&&(row.DpiValue>0||row.Id>=10&&row.Id<18);}).Count;
                        Check(enabled==40,"clipping tests use forty genuinely enabled fixture DPI actions: "+font+"/native="+native+"/audio="+audio+"/collapsed="+collapsed+" actual="+enabled);
                        Check(rows.FindAll(delegate(PopupControl row){return row.Id>=100&&row.Id<=102;}).Count==(audio?3:0),"audio controls follow audio group presence");
                        Check(rows.Exists(delegate(PopupControl row){return row.Id==240;})!=collapsed,"volume expansion is independent of group scrolling");
                        InteractionLayout.Observer=regions.Add;
                        GridFrameCapture imageState;try{using(var image=RenderGridObserved(owner,layout,dark,scale,new PanelFeedback(),out imageState)){
                            var topArea=new Rectangle(0,0,image.Width,(int)(first.Grid.Controls.Top*scale));bool topSame=GridSameArea(baseline,image,topArea);
                            if(!topSame)GridDiagnostic("scrolling cannot paint into the device tiles","font="+font+",native="+native+",audio="+audio+",collapsed="+collapsed+",scale="+scale+",dark="+dark+",offset="+offset,baselineState,imageState,null,baseline,image,topArea);
                            Check(topSame,"scrolling cannot paint into the device tiles");
                            int fixedTop=(int)Math.Ceiling(first.VolumeTop*scale);var fixedArea=new Rectangle(0,fixedTop,image.Width,image.Height-fixedTop);bool fixedSame=GridSameArea(baseline,image,fixedArea);
                            if(!fixedSame)GridDiagnostic("scrolling cannot paint over system volume or the footer","font="+font+",native="+native+",audio="+audio+",collapsed="+collapsed+",scale="+scale+",dark="+dark+",offset="+offset,baselineState,imageState,null,baseline,image,fixedArea);
                            Check(fixedSame,"scrolling cannot paint over system volume or the footer");
                        }}finally{InteractionLayout.Observer=null;}
                        foreach(var row in rows){
                            var visible=row.VisibleRect;if(!visible.IsEmpty)Check(visible.Left>=0&&visible.Right<=layout.Size.Width&&visible.Top>=0&&visible.Bottom<=layout.Size.Height,"all visible rectangles fit the actual 520px panel at every scale");
                            if(row.Viewport.HasValue&&!visible.IsEmpty)Check(layout.Grid.Viewport.Contains(visible)&&visible.Bottom<=layout.VolumeTop,"group hit and accessibility bounds stay inside their shared viewport");
                            if(row.Information||row.Id<0)continue;var region=regions.Find(delegate(InteractionRegion item){return item.Id==row.Id;});
                            Check(visible.IsEmpty?region==null:region!=null&&region.Limit==visible,"clipped drawing limits equal the visible hit rectangle, including partial controls");
                            if(row.Enabled&&!visible.IsEmpty)Check(PopupPages.Hit(rows,new PointF(visible.X+visible.Width/2,visible.Y+visible.Height/2))==row,"visible parts alone remain clickable");
                        }
                        var last=layout.Grid.Groups[layout.Grid.Groups.Count-1];if(offset==Int32.MaxValue)Check(layout.Grid.ScrollOffset==layout.Grid.MaximumScroll&&layout.Grid.Viewport.Contains(last.Track),"scrolling to the end fully exposes the last group without bottom overflow");
                        var partial=rows.Find(delegate(PopupControl row){return row.Viewport.HasValue&&row.Enabled&&!row.Information&&!row.VisibleRect.IsEmpty&&row.VisibleRect!=row.Rect;});
                        if(offset==31)Check(partial!=null,"the regression exercises a genuinely enabled partially visible stage");
                        if(partial!=null){var point=new PointF(partial.Rect.X+partial.Rect.Width/2,partial.Rect.Top<partial.VisibleRect.Top?partial.Rect.Top+.25f:partial.Rect.Bottom-.25f);Check(PopupPages.Hit(rows,point)!=partial,"the cropped part of a partially visible control cannot be hit");}
                        var hidden=rows.Find(delegate(PopupControl row){return row.Viewport.HasValue&&!row.Information&&row.VisibleRect.IsEmpty;});
                        if(hidden!=null){var ordinaryFeedback=new PanelFeedback();var hoveredFeedback=new PanelFeedback {HoverAction=hidden.Id,PressedAction=hidden.Id,KeyboardFocus=hidden.Id};GridFrameCapture ordinaryState,hoveredState;using(var ordinary=RenderGridObserved(owner,layout,dark,scale,ordinaryFeedback,out ordinaryState))using(var hovered=RenderGridObserved(owner,layout,dark,scale,hoveredFeedback,out hoveredState)){
                            var fullArea=new Rectangle(Point.Empty,ordinary.Size);bool same=GridSameArea(ordinary,hovered,fullArea);
                            if(!same)GridDiagnostic("offscreen hover, press and focus feedback leaves no ink in another region","font="+font+",native="+native+",audio="+audio+",collapsed="+collapsed+",scale="+scale+",dark="+dark+",offset="+offset,ordinaryState,hoveredState,hidden,ordinary,hovered,fullArea);
                            Check(same,"offscreen hover, press and focus feedback leaves no ink in another region");
                        }}
                    }
                }
                var clamped=WirelessLayout.Build(owner,0,520,false,Int32.MaxValue);owner.WirelessDevices=new WirelessDevice[0];owner.Headset=null;owner.Audio=null;
                var reduced=WirelessLayout.Build(owner,0,520,false,clamped.Grid.ScrollOffset);Check(reduced.Grid.ScrollOffset==0&&!reduced.Grid.Overflow,"shrinking the group list clamps obsolete offsets to zero");
            }
        }
    }
    static void GridCountsAndPaging(){
        foreach(string font in new[]{"normal","large"})foreach(int count in new[]{1,2,3,4,5}){Mode(font,"ocean",false,"grid");using(var fixture=new Fixture()){
            var owner=fixture.Owner;owner.NativeMousePresent=false;owner.Headset=null;owner.GenericReady=true;var devices=new List<WirelessDevice>();for(int i=0;i<count;i++)devices.Add(GridDevice(i));owner.WirelessDevices=devices.ToArray();
            var full=WirelessLayout.Build(owner,0,10000);int rows=(count+1)/2,expected=rows*(GridLayout.TileHeight+8)+VisualDesign.SystemVolumeHeight+2+VisualDesign.FooterHeight;
            Check(full.Size.Height==expected&&full.Cards.Count==count,"1..5 tiles use row height plus the volume-only controls and footer");Console.WriteLine("grid "+font+" / "+count+" devices / volume only: "+expected);
            for(int i=0;i<count;i++){var tile=full.Cards[i].Tile;Check(tile.Height==GridLayout.TileHeight&&tile.Width==(i==count-1&&count%2==1?VisualDesign.PanelWidth:GridLayout.TileWidth),"only the global odd last tile spans both columns");Check(tile.Top==i/2*(GridLayout.TileHeight+8),"grid rows have an eight-pixel gutter");}
            int maximum=GridLayout.TileHeight+8+(int)full.Grid.Controls.Height+VisualDesign.FooterHeight;
            for(int offset=0;offset<count+3;offset++){
                var page=WirelessLayout.Build(owner,offset,maximum);Check(page.Size.Height<=maximum&&page.Start%2==0&&page.Cards.Count<=2,"bounded grid pages scroll whole rows");
                Check(page.Grid.Controls.Top==GridLayout.TileHeight+8&&page.VolumeRect==WirelessLayout.Build(owner,0,maximum).VolumeRect,"scrolling keeps quick controls and volume at identical coordinates");
                var hit=PopupPages.Build(owner,new PopupNavigation(),false,page);var regions=new List<InteractionRegion>();InteractionLayout.Observer=regions.Add;
                try{using(var image=new Bitmap(page.Size.Width,page.Size.Height))using(var g=Graphics.FromImage(image))CompactRenderer.PaintWireless(g,owner,page,false,new PanelFeedback());}finally{InteractionLayout.Observer=null;}
                foreach(var control in hit)if(control.Id>=0&&!control.Information){var region=regions.Find(delegate(InteractionRegion r){return r.Id==control.Id;});Check(region!=null&&region.Limit==control.Rect,"paged tile, control and footer hits equal their drawing limits");Check(control.Rect.Bottom<=page.Size.Height,"all page hit rectangles fit the computed height");}
            }
        }}
    }
    static void GridGroupsAndHidden(){
        foreach(string font in new[]{"normal","large"}){Mode(font,"ocean",false,"grid");using(var fixture=new Fixture()){
            var owner=fixture.Owner;owner.GenericReady=true;owner.Dpi=Dpi(8);owner.WirelessDevices=new[]{GridDevice(0,5),GridDevice(1,8),GridDevice(2)};
            var pref=DeviceSettings.Current;pref.DeviceOrder=new List<string>{"grid-1","native:headset","grid-0","native:mouse","grid-2"};DeviceSettings.UseTransientConfiguration(pref);
            var layout=WirelessLayout.Build(owner,0,10000);Check(layout.Grid.Groups.Count==4&&layout.Grid.Groups[0].Card.NativeMouse&&layout.Grid.Groups[1].Card.Id=="grid-1"&&layout.Grid.Groups[2].Card.Id=="grid-0"&&layout.Grid.Groups[3].Audio,"fixed controls put native DPI first, other mice in device order, then audio");
            foreach(var group in layout.Grid.Groups){Check(group.Title.Bottom<=group.Track.Top,"non-focusable group heading precedes its controls");if(group.Count>4){Check(GridLayout.Segment(group,4).Top-GridLayout.Segment(group,0).Bottom==2,"more than four DPI values wrap with a two-pixel gutter");Check(group.Track.Contains(GridLayout.Segment(group,group.Count-1)),"last stage stays within its group track");}}
            var before=layout.Grid.Controls;var bounded=WirelessLayout.Build(owner,2,GridLayout.TileHeight+8+(int)before.Height+VisualDesign.FooterHeight);var first=WirelessLayout.Build(owner,0,bounded.Size.Height);Check(bounded.Grid.Controls==first.Grid.Controls,"all DPI groups stay fixed even when their tiles are off-page");
            var controls=PopupPages.Build(owner,new PopupNavigation(),true,bounded);Check(controls.Exists(delegate(PopupControl c){return c.DpiValue>0&&c.DeviceKey=="grid-0";})&&controls.Exists(delegate(PopupControl c){return c.Id==10;}),"off-page mice keep every DPI control");Check(!controls.Exists(delegate(PopupControl c){return c.Information&&c.Id>=0;}),"headings cannot be focused");
            var regions=new List<InteractionRegion>();InteractionLayout.Observer=regions.Add;try{using(var image=new Bitmap(bounded.Size.Width,bounded.Size.Height))using(var g=Graphics.FromImage(image))CompactRenderer.PaintWireless(g,owner,bounded,false,new PanelFeedback());}finally{InteractionLayout.Observer=null;}foreach(var control in controls)if(control.DpiValue>0||control.Id>=10&&control.Id<18){var region=regions.Find(delegate(InteractionRegion r){return r.Id==control.Id;});Check(region!=null&&region.Limit==control.Rect,"all off-page multi-mouse DPI groups retain identical paint and hit rectangles");}
            pref=DeviceSettings.Current;pref.HiddenDeviceIds=new List<string>{"native:mouse","grid-1","native:headset"};DeviceSettings.UseTransientConfiguration(pref);layout=WirelessLayout.Build(owner,0,10000);Check(layout.All.Count==2&&layout.Grid.Groups.Count==1&&layout.Grid.Groups[0].Card.Id=="grid-0","hidden tiles and their DPI/audio groups disappear together");
            controls=PopupPages.Build(owner,new PopupNavigation(),false,layout);Check(!controls.Exists(delegate(PopupControl c){return c.DeviceKey=="native:mouse"||c.DeviceKey=="grid-1"||c.Id>=100&&c.Id<=102;}),"hidden devices leave no input targets");
            owner.WirelessDevices=new[]{GridDevice(2)};layout=WirelessLayout.Build(owner,0,10000);Check(layout.Grid.Groups.Count==0&&layout.VolumeTop==layout.Grid.Controls.Top,"no DPI and no audio puts volume at the top without headings");
        }}
    }
    static void GridTypographyAndStates(){
        foreach(string font in new[]{"normal","large"})foreach(float scale in new[]{1f,1.25f,1.5f,1.75f,2f}){Mode(font,"ocean",false,"grid");using(var fixture=new Fixture())using(var host=new Control()){
            var owner=fixture.Owner;owner.NativeMousePresent=false;owner.Headset=null;owner.GenericReady=true;var handle=host.Handle;
            foreach(bool wide in new[]{false,true})foreach(int? percent in new int?[]{0,1,2,3,50,100,null})foreach(int state in new[]{0,1,2,3}){
                var device=GridDevice(0);device.Name="非常长的设备名称用于验证单行省略";device.Percent=percent;device.Approximate=percent==100;device.Fresh=state!=1;device.Online=state!=1;device.ConnectionState=state==2?"sleeping":state==1?"disconnected":"connected";device.MeasurementState=state==2?"sleeping":"current";device.ConfirmedAtUtc=(state==1?DateTime.UtcNow.AddHours(-2):DateTime.UtcNow).ToString("o");device.Charging=state==3;device.ChargingCheckedAtUtc=DateTime.UtcNow.ToString("o");owner.WirelessDevices=wide?new[]{device}:new[]{device,GridDevice(1)};
                var layout=WirelessLayout.Build(owner,0,10000);var tile=layout.Cards[0].Tile;var view=layout.Cards[0].Battery;bool sleeping=state==2;string status=GridLayout.Status(view,sleeping);Check(status==(sleeping?"休眠":state==1&&percent.HasValue?"上次":state==3?"充电":percent.HasValue&&percent<20?"电量低":null),"labels reflect only explicit charging/sleep and fresh/low state");
                var labels=new List<DrawnLabel>();var chevrons=new List<RectangleF>();CompactRenderer.LabelObserver=labels.Add;CompactRenderer.SymbolObserver=delegate(string icon,RectangleF rect){if(icon=="chevron")chevrons.Add(rect);};
                try{using(var image=Render(owner,true,false,new PanelFeedback {HoverAction=1000},scale)){if(sleeping&&percent<20&&scale==1){var palette=ThemePalette.Current(false);Check(image.GetPixel((int)tile.Left+5,(int)(tile.Top+tile.Height/2)).ToArgb()==ThemePalette.Blend(palette.Card,palette.SegmentHover,palette.SegmentHover.A).ToArgb(),"sleeping retains only the requested hover fill, never an old low-reading tint");}} }finally{CompactRenderer.LabelObserver=null;CompactRenderer.SymbolObserver=null;}
                string text=sleeping?"休眠":percent.HasValue?(percent==100?"≈":"")+percent.Value:"--";var number=labels.Find(delegate(DrawnLabel label){return label.Text==text;});Check(number!=null&&number.FontSize==(sleeping||!percent.HasValue?13:font=="large"?30:26),"grid keeps 26/30px digits and the existing 13px placeholders");
                var title=labels.Find(delegate(DrawnLabel label){return label.Text==device.Name;});Check(title!=null&&tile.Contains(title.LogicalRect),"device names stay in their tile");
                using(var image=new Bitmap(1,1))using(var g=Graphics.FromImage(image)){g.ScaleTransform(scale,scale);var ink=InteractionLayout.LabelBounds(g,text,number.LogicalRect,number.FontSize,true,number.Alignment,StringTrimming.None);Check(ink.Left>=tile.Left+GridLayout.Inset-1&&ink.Right<=tile.Right-GridLayout.Inset+1,"approximate full readings fit both widths at every monitor scale");if(wide)Check(title.LogicalRect.Right<=ink.Left-4,"wide tile name truncates before its number");
                    if(!sleeping&&percent.HasValue){var percentLabel=labels.Find(delegate(DrawnLabel label){return label.Text=="%";});var percentInk=InteractionLayout.LabelBounds(g,"%",percentLabel.LogicalRect,percentLabel.FontSize,true,StringAlignment.Near,StringTrimming.None);Check(percentInk.Right<=tile.Right-GridLayout.Inset+1&&Math.Abs(ink.Bottom-percentInk.Bottom)<=2,"grid percent stays inside and shares the number baseline");}
                }
                Check(chevrons.Count==1&&chevrons[0]==GridLayout.Chevron(tile,status),"only the hovered tile reveals its shared 7x10 detail arrow");
                var rows=PopupPages.Build(owner,new PopupNavigation(),false,layout);var control=rows.Find(delegate(PopupControl c){return c.DeviceKey==device.Id&&c.Id==1000;});var tree=new PopupAccessibility(host,delegate{return rows;},delegate{return -1;},null,delegate{return scale;},null,null);string name=tree.GetChild(rows.IndexOf(control)).Name;Check(name.Contains("键盘")&&name.Contains(device.Name)&&name.Contains("打开详情")&&(sleeping?name.Contains("休眠")&&!name.Contains("85%"):name.Contains(percent.HasValue?percent+"%":"--")),"accessible tile includes type, device, reading/state and direct detail action");
            }
        }}
    }
    static void GridFeedbackAndControls(){
        foreach(string font in new[]{"normal","large"})foreach(bool contrast in new[]{false,true})foreach(bool dark in new[]{false,true}){Mode(font,"ocean",false,"grid");VisualDesign.HighContrastOverride=contrast;using(var fixture=new Fixture()){
            var owner=fixture.Owner;var labels=new List<DrawnLabel>();var pressedLabels=new List<DrawnLabel>();var arrows=new List<RectangleF>();var rings=new List<RectangleF>();CompactRenderer.LabelObserver=labels.Add;CompactRenderer.SymbolObserver=delegate(string symbol,RectangleF rect){if(symbol=="chevron")arrows.Add(rect);};CompactRenderer.InteractionOutlineObserver=rings.Add;
            try{
                using(var normal=Render(owner,false,dark,new PanelFeedback())){
                    Check(arrows.Count==0&&rings.Count==0,"unfocused tiles have no arrow or selected accent outline");CompactRenderer.LabelObserver=pressedLabels.Add;
                    using(var hover=Render(owner,false,dark,new PanelFeedback {HoverAction=2}))using(var press=Render(owner,false,dark,new PanelFeedback {PressedAction=2}))using(var focus=Render(owner,false,dark,new PanelFeedback {KeyboardFocus=2}))Check(Different(normal,hover)&&Different(normal,press)&&Different(normal,focus),"tiles show distinct hover, press and keyboard focus feedback");
                }
            }finally{CompactRenderer.LabelObserver=null;CompactRenderer.SymbolObserver=null;CompactRenderer.InteractionOutlineObserver=null;}
            // The second rendering is hover; the following press moves the same
            // content by one logical pixel before rasterization.
            var baseLabel=labels.Find(delegate(DrawnLabel label){return label.Text==DeviceSettings.Current.MouseAlias;});var moved=pressedLabels.FindAll(delegate(DrawnLabel label){return label.Text==DeviceSettings.Current.MouseAlias;});Check(moved.Count==3&&moved[1].DeviceRect.Y-baseLabel.DeviceRect.Y==1,"pressed tile contents move by exactly one logical pixel");
            owner.Audio.MicrophoneMuted=true;var layout=WirelessLayout.Build(owner,0,10000);var group=layout.Grid.Groups.Find(delegate(GridControlGroup item){return item.Audio;});var area=Rectangle.Round(GridLayout.Microphone(group));
            using(var muted=Render(owner,true,dark,new PanelFeedback()))using(var pending=Render(owner,true,dark,new PanelFeedback {MicrophonePending=true,ShowPendingHints=true}))if(!contrast)Check(RedPixels(muted,area)>0&&RedPixels(pending,area)==0,"grid microphone keeps existing confirmed and pending ink");
            var controls=PopupPages.Build(owner,new PopupNavigation(),true,layout);Check(controls.Find(delegate(PopupControl c){return c.Id==10;}).Text.StartsWith(DeviceSettings.Current.MouseAlias+" DPI")&&controls.Find(delegate(PopupControl c){return c.Id==102;}).Text.StartsWith("音源，"),"control accessible names include the device/group prefix");
            var feedback=new PanelFeedback {ShowPendingHints=true,PendingStage=1,PendingOutput="headset",MicrophonePending=true};labels.Clear();CompactRenderer.LabelObserver=labels.Add;try{using(var image=Render(owner,true,dark,feedback)){} }finally{CompactRenderer.LabelObserver=null;}Check(labels.Exists(delegate(DrawnLabel label){return label.Text=="800…";})&&labels.Exists(delegate(DrawnLabel label){return label.Text=="耳机…";}),"grid reuses existing native DPI and audio pending hints");
            owner.Dpi=null;owner.Headset=null;owner.Audio=null;using(var image=Render(owner,false,dark,new PanelFeedback()))Check(image.Size==VisualDesign.PanelSizeFor(false,null),"no DPI and no audio still renders a bounded volume card");
        }}VisualDesign.HighContrastOverride=false;
    }
    static AudioReading Audio(){return new AudioReading {TimestampUtc=DateTime.UtcNow,OperationSucceeded=true,ActiveOutput="speaker",ConsoleOutputId="speaker",SpeakerAvailable=true,SpeakerId="speaker",SpeakerName="扬声器",HeadsetAvailable=true,HeadsetId="headset",HeadsetName="耳机",MicrophoneAvailable=true,MicrophoneId="mic",MicrophoneName="麦克风",MicrophoneMuted=false,VolumeEndpointId="speaker",OutputVolumePercent=55,Devices=new AudioDeviceChoice[0]};}
    static HeadsetReading Headset(){return new HeadsetReading {Connected=true,ConnectionVerified=true,ReceiverPresent=true,Percent=46,ConnectionTimestampUtc=DateTime.UtcNow,BatteryTimestampUtc=DateTime.UtcNow};}
    sealed class Fixture:IDisposable {
        internal readonly TrayContext Owner;
        internal Fixture(){
            Owner=new TrayContext(false,delegate(bool battery){return new MouseSnapshot {Battery=new BatteryReading {Percent=85,TimestampUtc=DateTime.UtcNow},Dpi=Dpi()};},delegate(int stage){throw new Exception("No device writes");},delegate{return new HeadsetReading();},Audio,false,delegate(string id){throw new Exception("No audio writes");},delegate(bool muted){throw new Exception("No audio writes");},delegate(string id,int value){throw new Exception("No audio writes");},delegate{return false;});
            DateTime deadline=DateTime.UtcNow.AddSeconds(5);while((Owner.IsRefreshing||Owner.IsAudioBusy||Owner.IsHeadsetRefreshing||Owner.Audio==null)&&DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(2);}
            Check(!Owner.IsRefreshing&&!Owner.IsAudioBusy&&Owner.Audio!=null,"injected readers settle");
            ((System.Windows.Forms.Timer)typeof(TrayContext).GetField("refresh",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(Owner)).Stop();
            Owner.NativeMousePresent=true;Owner.Current=new BatteryReading {Percent=85,TimestampUtc=DateTime.UtcNow};Owner.Dpi=Dpi();Owner.Audio=Audio();Owner.Headset=Headset();Owner.GenericReady=false;Owner.WirelessDevices=new WirelessDevice[0];
        }
        public void Dispose(){Owner.ExitThread();Owner.Dispose();}
    }
    static Bitmap Render(TrayContext owner,bool wireless,bool dark,PanelFeedback feedback,float scale=1){
        var layout=wireless?WirelessLayout.Build(owner,0,10000):null;var logical=wireless?layout.Size:VisualDesign.PanelSizeFor(VisualDesign.HasAudioCard(owner.Headset,owner.Audio),owner.Dpi);var physical=UiState.ScaleSize(logical,scale);
        var image=new Bitmap(physical.Width,physical.Height,PixelFormat.Format32bppPArgb);
        using(var g=Graphics.FromImage(image)){g.Clear(Color.Transparent);g.ScaleTransform(scale,scale);if(wireless)CompactRenderer.PaintWireless(g,owner,layout,dark,feedback);else CompactRenderer.Paint(g,owner.Current,owner.LastGood,null,false,owner.Dpi,null,false,dark,owner.Headset,owner.Audio,1,-1,false,1,-1,feedback);}
        return image;
    }
    static Bitmap OpeningFrame(TrayContext owner,bool wireless,bool dark,float progress){
        var layout=WirelessLayout.Build(owner,0,10000);var size=wireless?layout.Size:VisualDesign.PanelSizeFor(VisualDesign.HasAudioCard(owner.Headset,owner.Audio),owner.Dpi);
        var image=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppPArgb);
        try{using(var g=Graphics.FromImage(image)){
            var method=typeof(CompactRenderer).GetMethod(wireless?"PaintWireless":"Paint",Static);var parameters=method.GetParameters();
            Check(parameters[parameters.Length-1].Name=="batteryProgress","both render paths accept independent opening fill progress");
            object[] args=wireless?new object[]{g,owner,layout,dark,new PanelFeedback(),progress}:new object[]{g,owner.Current,owner.LastGood,null,false,owner.Dpi,null,false,dark,owner.Headset,owner.Audio,1f,-1,false,1f,-1,new PanelFeedback(),false,progress};
            method.Invoke(null,args);
        }return image;}catch{image.Dispose();throw;}
    }
    static void OpeningFill(){
        foreach(bool reduced in new[]{false,true})foreach(float elapsed in new[]{-10f,0f,60f,120f,190f,240f,500f}){
            float actual=(float)Geometry("BatteryOpeningProgress",elapsed,reduced),t=Math.Max(0,Math.Min(1,elapsed/240f));
            Check(Math.Abs(actual-(reduced?1:1-(float)Math.Pow(1-t,3)))<.0001f,"opening fill uses the specified 240ms ease-out and reduced-motion final state");
        }
        foreach(string layout in PanelLayouts.Keys)foreach(string font in new[]{"normal","large"})foreach(bool dark in new[]{false,true})foreach(bool contrast in new[]{false,true}){
            Mode(font,"ocean",false,layout);var pref=DeviceSettings.Current;pref.ReducedMotion=false;DeviceSettings.UseTransientConfiguration(pref);VisualDesign.HighContrastOverride=contrast;
            using(var fixture=new Fixture())foreach(bool wireless in new[]{false,true}){
                var owner=fixture.Owner;owner.GenericReady=wireless;
                owner.WirelessDevices=new[]{new WirelessDevice {Id="opening-sleep",Name="注入休眠",Kind="keyboard",Percent=85,Fresh=true,Online=true,ConnectionState="sleeping",ConfirmedAtUtc=DateTime.UtcNow.ToString("o")}};
                foreach(int? percent in new int?[]{0,1,2,3,50,100,null})foreach(bool fresh in new[]{false,true}){
                    owner.Current=percent.HasValue?new BatteryReading {Percent=percent.Value,Charging=percent==50,TimestampUtc=fresh?DateTime.UtcNow:DateTime.UtcNow.AddHours(-2)}:null;owner.LastGood=null;
                    var labels=new List<DrawnLabel>();CompactRenderer.LabelObserver=labels.Add;
                    try{using(var start=OpeningFrame(owner,wireless,dark,0))using(var middle=OpeningFrame(owner,wireless,dark,.875f))using(var end=OpeningFrame(owner,wireless,dark,1)){
                        Check(start.Size==end.Size&&middle.Size==end.Size,"opening never changes computed panel height");
                        Rectangle area=layout=="ring"?Rectangle.Ceiling(VisualDesign.BatteryRingRect(0)):layout=="grid"?Rectangle.Ceiling(GridLayout.Bar((wireless?WirelessLayout.Build(owner,0,10000):GridLayout.Native(owner.Current,null,owner.Dpi,owner.Headset,owner.Audio)).Cards[0].Tile)):Rectangle.Ceiling(VisualDesign.BatteryBarRect(0));
                        if(!PopupPages.Reduced&&percent.GetValueOrDefault()>0){Check(!SameArea(start,end,area),"opening start has no battery fill");if(percent>=50)Check(!SameArea(start,middle,area)&&!SameArea(middle,end,area),"opening has a distinct intermediate and final fill");}
                        Check(labels.Count%3==0,"opening frames draw the same number of labels");int count=labels.Count/3;
                        for(int i=0;i<count;i++)Check(labels[i].Text==labels[count+i].Text&&labels[i].Text==labels[2*count+i].Text,"numbers and state labels keep real values throughout the animation");
                    }}finally{CompactRenderer.LabelObserver=null;}
                }
                pref=DeviceSettings.Current;pref.ReducedMotion=true;DeviceSettings.UseTransientConfiguration(pref);
                using(var start=OpeningFrame(owner,wireless,dark,0))using(var end=OpeningFrame(owner,wireless,dark,1))Check(!Different(start,end),"reduced motion paints the final state immediately on both paths");
                pref.ReducedMotion=false;DeviceSettings.UseTransientConfiguration(pref);
            }
        }
        VisualDesign.HighContrastOverride=false;
    }
    static void BarGeometry(){
        foreach(string font in new[]{"normal","large"}){Mode(font);var track=Rect("BatteryBarRect",0f);Check(track==new RectangleF(12,VisualDesign.LargeFont?56:50,VisualDesign.PanelWidth-24,4),"4px battery track uses the specified header and side insets");
            foreach(int? percent in new int?[]{0,1,2,3,50,100,null})foreach(bool fresh in new[]{false,true})foreach(bool approximate in new[]{false,true}){
                var value=new BatteryView {Percent=percent,Fresh=fresh,Approximate=approximate};var fill=Rect("BatteryFillRect",0f,value);float expected=track.Width*percent.GetValueOrDefault()/100f;
                Check(Math.Abs(fill.Width-expected)<.001f,"battery fill is proportional, independent of freshness and approximation");Check(fill.Width==0||track.Contains(fill),"battery fill never leaves its track");
            }
            foreach(int percent in new[]{-20,120}){var fill=Rect("BatteryFillRect",0f,new BatteryView {Percent=percent});Check(fill.Width>=0&&fill.Width<=track.Width,"out-of-range defensive clamping");}
        }
    }
    static void BatteryTypography(){
        foreach(string font in new[]{"normal","large"})foreach(float scale in new[]{1f,1.25f,1.5f,1.75f,2f}){
            Mode(font);Check(VisualDesign.BatteryDigitSize==(font=="large"?22:19),"battery type size follows the brief");
            using(var image=new Bitmap((int)(VisualDesign.PanelWidth*scale),(int)(VisualDesign.GenericCardHeight*scale)))using(var g=Graphics.FromImage(image)){
                g.ScaleTransform(scale,scale);g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                foreach(var view in new[]{new BatteryView {Percent=100,Fresh=true,Approximate=true,Charging=true},new BatteryView {Percent=0,Fresh=true},new BatteryView {Percent=1,Fresh=true},new BatteryView {Percent=2,Fresh=true},new BatteryView {Percent=3,Fresh=true},new BatteryView {Percent=50,Fresh=true,Charging=true},new BatteryView {Percent=100,Fresh=false,Unconfirmed=true},new BatteryView(),new BatteryView {Fresh=true}}){
                    g.Clear(Color.Transparent);
                    var labels=new List<DrawnLabel>();var bolts=new List<RectangleF>();CompactRenderer.LabelObserver=labels.Add;CompactRenderer.SymbolObserver=delegate(string symbol,RectangleF rect){if(symbol=="bolt")bolts.Add(rect);};
                    try{typeof(CompactRenderer).GetMethod("Meter",Static).Invoke(null,new object[]{g,view,0f,(float)VisualDesign.GenericCardHeight,false,new PanelFeedback(),4,1f});}finally{CompactRenderer.LabelObserver=null;CompactRenderer.SymbolObserver=null;}
                    string text=view.Percent.HasValue?(view.Approximate?"≈":"")+view.Percent.Value:"--";var digits=labels.Find(delegate(DrawnLabel label){return label.Text==text;});
                    Check(digits!=null&&digits.Bold&&digits.FontSize==(view.Percent.HasValue?VisualDesign.BatteryDigitSize:13),"full sized digits and the smaller existing no-reading placeholder are drawn");
                    var digitInk=InteractionLayout.LabelBounds(g,text,digits.LogicalRect,digits.FontSize,true,StringAlignment.Far,System.Drawing.StringTrimming.None);
                    Check(digitInk.Left>=digits.LogicalRect.Left&&digitInk.Right<=VisualDesign.PanelWidth-12,"even charging approximate 100 fits the right inset");
                    if(!view.Percent.HasValue&&scale==1)Check(InkMatches(image,Rectangle.Round(VisualDesign.HeaderMeterHitRect(0)),ThemePalette.Current(false).Muted),"no-reading placeholder keeps secondary ink even when the provider marks the connection fresh");
                    Check(bolts.Count==(view.Charging?1:0),"charging glyph requires explicit charging data");
                    if(view.Charging){var bolt=bolts[0];Check(bolt.Size==new SizeF(10,13)&&Math.Abs(digitInk.Left-bolt.Right-4)<.01f,"charging glyph is 10x13 and four pixels before the actual number");Check(bolt.Left>=VisualDesign.MouseNameRect.Right,"charging approximate 100 never covers the device name");}
                    if(view.Percent.HasValue){var percent=labels.Find(delegate(DrawnLabel label){return label.Text=="%";});Check(percent!=null&&percent.Bold&&percent.FontSize==(font=="large"?11:10),"percent is 10/11px semibold text");
                        var percentInk=InteractionLayout.LabelBounds(g,"%",percent.LogicalRect,percent.FontSize,true,StringAlignment.Near,System.Drawing.StringTrimming.None);Check(percentInk.Right<=VisualDesign.PanelWidth-12&&percentInk.Left-digitInk.Right>=0&&percentInk.Left-digitInk.Right<=3,"percent is adjacent and within the right inset: "+font+" / "+scale+" / "+text+" digits="+digitInk+" percent="+percentInk);
                        Check(Math.Abs(percentInk.Bottom-digitInk.Bottom)<=2,"percent and digits share their baseline");
                    }
                }
            }
        }
    }
    static void HitGeometry(){HitGeometry("bar");}
    static void HitGeometry(string panelLayout){
        foreach(string font in new[]{"normal","large"})foreach(bool wireless in new[]{false,true})foreach(int count in new[]{4,5,8})foreach(bool collapsed in new[]{false,true})foreach(int audioCard in new[]{0,1,2}){
            Mode(font,"ocean",collapsed,panelLayout);using(var fixture=new Fixture()){
                var owner=fixture.Owner;owner.Dpi=Dpi(count);owner.GenericReady=wireless;
                if(audioCard!=1)owner.Headset=null;
                if(audioCard==2)owner.Audio.Devices=new[]{new AudioDeviceChoice {Id="headset",Name="耳机",Flow=0,FormFactor=3}};
                if(wireless){var stages=new int[count];for(int i=0;i<count;i++)stages[i]=400*(i+1);owner.WirelessDevices=new[]{new WirelessDevice {Id="dpi-mouse-fixture",Name="通用 DPI 鼠标",Kind="mouse",Percent=80,Fresh=true,Online=true,ConnectionState="connected",ConfirmedAtUtc=DateTime.UtcNow.ToString("o"),Dpi=new WirelessDpi {Supported=true,CanSet=true,Current=800,Stages=stages,TargetToken=new string('a',64),CheckedAtUtc=DateTime.UtcNow.ToString("o")}}};}
                var layout=WirelessLayout.Build(owner,0,10000);var rows=PopupPages.Build(owner,new PopupNavigation(),VisualDesign.ShouldShowHeadset(owner.Headset),wireless?layout:null);var regions=new List<InteractionRegion>();InteractionLayout.Observer=regions.Add;
                try{using(var image=Render(owner,wireless,false,new PanelFeedback())){}}finally{InteractionLayout.Observer=null;}
                foreach(var row in rows){if(!(row.Id>=2&&row.Id<=5||row.Id>=10&&row.Id<18||row.Id>=100&&row.Id<=102||row.Id==200||row.Id==240||row.Id==241||row.Id==0||row.Id==1||row.DpiValue>0||row.Id>=1000&&row.Id<3000))continue;
                    var region=regions.Find(delegate(InteractionRegion item){return item.Id==row.Id;});Check(region!=null,"drawn control exists for hit action "+row.Id);var drawn=region.Limit;if(panelLayout=="bar"&&(row.Id==3||row.Id==5||row.Id==200||row.Id>=100&&row.Id<=102))drawn.Y+=wireless?layout.Cards.Find(delegate(WirelessCard card){return card.NativeHeadset||card.AudioOnly;}).Top:VisualDesign.MouseCardHeight(owner.Dpi)+VisualDesign.CardGap;
                    Check(drawn==row.Rect,"draw and hit rectangles are identical for "+row.Id+" / "+font+" / "+wireless+": "+drawn+" != "+row.Rect);
                    if(row.Enabled)Check(PopupPages.Hit(rows,new PointF(row.Rect.Left+row.Rect.Width/2,row.Rect.Top+row.Rect.Height/2))==row,"center hit resolves the drawn action");
                }
                for(int i=0;i<rows.Count;i++)for(int j=i+1;j<rows.Count;j++)Check(!rows[i].Rect.IntersectsWith(rows[j].Rect),"main control rectangles remain disjoint");
                if(count>4){Check(VisualDesign.StageRect(4,count).Top-VisualDesign.StageRect(0,count).Bottom==2,"DPI rows have exactly two pixels between segments");Check(VisualDesign.StageTrackRect(0,count).Contains(VisualDesign.StageRect(count-1,count)),"last DPI stage stays within its shared track");}
            }
        }
    }
    static void RenderMatrix(){RenderMatrix("bar");}
    static void RenderMatrix(string panelLayout){
        foreach(string font in new[]{"normal","large"})foreach(string style in ThemePalette.Keys)foreach(bool dark in new[]{false,true})foreach(bool headset in new[]{false,true})foreach(bool contrast in new[]{false,true}){
            VisualDesign.HighContrastOverride=contrast;
            Mode(font,style,false,panelLayout);using(var fixture=new Fixture()){
                var owner=fixture.Owner;if(!headset)owner.Headset=null;
                foreach(bool wireless in new[]{false,true}){owner.GenericReady=wireless;using(var image=Render(owner,wireless,dark,new PanelFeedback())){
                    var expected=VisualDesign.PanelSizeFor(headset,owner.Dpi);Check(image.Size==expected,"all theme/font/headset matrices use the shared panel height");
                }}
                using(var native=Render(owner,false,dark,new PanelFeedback()))using(var wireless=Render(owner,true,dark,new PanelFeedback()))Check(SameArea(native,wireless,new Rectangle(0,0,native.Width,(int)VisualDesign.FooterRectFor(headset,owner.Dpi).Top)),"both paths paint identical native device and volume cards");
                owner.GenericReady=true;
                foreach(int? percent in new int?[]{0,1,2,3,50,100,null})foreach(bool fresh in new[]{false,true}){
                    owner.WirelessDevices=new[]{new WirelessDevice {Id="keyboard-fixture",Name="非常长的设备名称用于验证省略而不是覆盖电量",Kind="keyboard",Percent=percent,Approximate=percent==100,Fresh=fresh,Online=fresh,ConnectionState=fresh?"connected":"unconfirmed",ConfirmedAtUtc=(fresh?DateTime.UtcNow:DateTime.UtcNow.AddHours(-2)).ToString("o"),Charging=fresh&&percent==50,ChargingCheckedAtUtc=DateTime.UtcNow.ToString("o")}};
                    using(var image=Render(owner,true,dark,new PanelFeedback()))Check(image.Size==WirelessLayout.Build(owner,0,10000).Size,"generic boundary, cached, no-reading and charging cards render with the same battery geometry");
                }
            }
        }
        VisualDesign.HighContrastOverride=false;
    }
    static Bitmap Gauge(BatteryView view,bool dark,bool sleeping=false){var bitmap=new Bitmap(VisualDesign.PanelWidth,VisualDesign.HeaderHeight);using(var g=Graphics.FromImage(bitmap)){g.Clear(ThemePalette.Current(dark).Card);g.SmoothingMode=SmoothingMode.AntiAlias;CompactRenderer.RingGauge(g,view,"mouse",0,dark,sleeping);}return bitmap;}
    static void RingBoundaries(){
        foreach(string font in new[]{"normal","large"})foreach(bool dark in new[]{false,true})foreach(bool contrast in new[]{false,true}){
            Mode(font,"ocean",false,"ring");VisualDesign.HighContrastOverride=contrast;var bounds=Rectangle.Round(VisualDesign.BatteryRingRect(0));
            using(var zero=Gauge(new BatteryView {Percent=0,Fresh=true},dark))using(var one=Gauge(new BatteryView {Percent=1,Fresh=true},dark))using(var two=Gauge(new BatteryView {Percent=2,Fresh=true},dark))using(var three=Gauge(new BatteryView {Percent=3,Fresh=true},dark))using(var full=Gauge(new BatteryView {Percent=100,Fresh=true},dark)){
                Color ink=contrast?SystemColors.Highlight:ThemePalette.LevelColor(1,dark);Check(Different(zero,one)&&!Different(one,two)&&!Different(two,three),"one to three percent has a visible, stable single dot");Check(ColorPixels(one,bounds,ink)>0,"tiny ring progress uses level or system highlight ink");
                Check(ColorPixels(full,bounds,contrast?SystemColors.Highlight:ThemePalette.LevelColor(100,dark))>ColorPixels(one,bounds,ink)*10,"100 percent is a complete ring");
            }
            using(var empty=Gauge(new BatteryView(),dark))using(var sleeping=Gauge(new BatteryView {Percent=85,Fresh=true,Charging=true},dark,true))Check(!Different(empty,sleeping),"sleeping paints only the base ring and muted icon, never the old value or charging badge");
            if(!contrast)using(var stale=Gauge(new BatteryView {Percent=50,Fresh=false},dark))Check(ColorPixels(stale,bounds,ThemePalette.Current(dark).Muted)>0,"cached progress uses secondary ink");
            var bolts=new List<RectangleF>();CompactRenderer.SymbolObserver=delegate(string icon,RectangleF rect){if(icon=="bolt")bolts.Add(rect);};try{using(var charged=Gauge(new BatteryView {Percent=50,Fresh=true,Charging=true},dark)){} }finally{CompactRenderer.SymbolObserver=null;}
            Check(bolts.Count==1&&VisualDesign.RingChargeBadgeRect(0).Size==new SizeF(12,12)&&VisualDesign.RingChargeBadgeRect(0).Contains(bolts[0]),"explicit charging has a twelve-pixel tangent badge");
        }
        VisualDesign.HighContrastOverride=false;
    }
    static void RingNamesAndTypography(){
        foreach(string font in new[]{"normal","large"})foreach(float scale in new[]{1f,1.25f,1.5f,1.75f,2f}){
            Mode(font,"ocean",false,"ring");using(var fixture=new Fixture())using(var host=new Control()){
                var owner=fixture.Owner;owner.GenericReady=true;owner.NativeMousePresent=false;owner.Headset=null;var handle=host.Handle;
                foreach(int? percent in new int?[]{0,1,2,3,50,100,null})foreach(bool fresh in new[]{false,true})foreach(bool sleeping in new[]{false,true}){
                    var device=new WirelessDevice {Id="ring-boundary",Name="非常长的设备名称用于验证省略而不是覆盖读数",Kind="mouse",Percent=percent,Fresh=fresh,Online=fresh,Approximate=percent==100,ConnectionState=sleeping?"sleeping":fresh?"connected":"unconfirmed",MeasurementState=sleeping?"sleeping":"current",ConfirmedAtUtc=DateTime.UtcNow.ToString("o"),Charging=fresh&&percent==100,ChargingCheckedAtUtc=DateTime.UtcNow.ToString("o")};owner.WirelessDevices=new[]{device};
                    var labels=new List<DrawnLabel>();var icons=new List<RectangleF>();CompactRenderer.LabelObserver=labels.Add;CompactRenderer.SymbolObserver=delegate(string icon,RectangleF rect){if(icon=="chevron")icons.Add(rect);};
                    try{using(var image=Render(owner,true,false,new PanelFeedback {HoverAction=1000},scale)){} }finally{CompactRenderer.LabelObserver=null;CompactRenderer.SymbolObserver=null;}
                    var rows=PopupPages.Build(owner,new PopupNavigation(),false);var row=rows.Find(delegate(PopupControl item){return item.Id==1000;});var tree=new PopupAccessibility(host,delegate{return rows;},delegate{return -1;},null,delegate{return 1f;},null,null);string name=tree.GetChild(rows.IndexOf(row)).Name;
                    string expectedState=sleeping?"休眠":!percent.HasValue?"--":fresh&&percent==100?"充电中":!fresh?"待确认":fresh&&percent<20?"电量低":"%";
                    Check(name.Contains(device.Name)&&name.Contains("打开详情")&&name.Contains(expectedState),"ring spoken name includes battery state: "+name);
                    string text=sleeping?"休眠":percent.HasValue?(percent==100?"≈":"")+percent.Value:"--";var number=labels.Find(delegate(DrawnLabel label){return label.Text==text;});Check(number!=null,"ring number or placeholder is visible");
                    using(var bitmap=new Bitmap(1,1))using(var g=Graphics.FromImage(bitmap)){
                        g.ScaleTransform(scale,scale);var ink=InteractionLayout.LabelBounds(g,text,number.LogicalRect,number.FontSize,true,StringAlignment.Far,StringTrimming.None);Check(ink.Right<=VisualDesign.PanelWidth-12+.01f,"ring text stays inside the right padding");
                        var title=labels.Find(delegate(DrawnLabel label){return label.Text==device.Name;});Check(title!=null&&title.LogicalRect.Right<=icons[0].Left,"name truncates before the revealed chevron and number");
                    }
                    Check(icons.Count==1&&icons[0].Size==new SizeF(7,10),"detail chevron appears on hover only");
                }
            }
        }
    }
    static void ContrastAndShadows(){
        foreach(string style in ThemePalette.Keys)foreach(bool dark in new[]{false,true}){
            Mode("normal",style);var palette=ThemePalette.Current(dark);Check(Role(palette,"PanelBorder").A==(dark?18:15),"card border is a subtle seven/six percent line");Check(Role(palette,"PanelShadowNear").A>0&&Role(palette,"PanelShadowFar").A>0,"custom shadows exist only for ordinary themes");
            using(var image=new Bitmap(260,VisualDesign.GenericCardHeight+8))using(var g=Graphics.FromImage(image)){
                g.Clear(Color.Transparent);g.SmoothingMode=SmoothingMode.AntiAlias;var method=typeof(CompactRenderer).GetMethod("DrawPanelCard",Static);Check(method!=null,"main-only card surface preserves dialog surfaces");method.Invoke(null,new object[]{g,0f,(float)VisualDesign.GenericCardHeight,dark,null});
                Check(image.GetPixel(100,VisualDesign.GenericCardHeight-1).A>0,"downward shadow survives inside the card slot");Check(image.GetPixel(0,VisualDesign.GenericCardHeight).A==0&&image.GetPixel(100,VisualDesign.GenericCardHeight).A==0,"shadow does not spill into the next card or outside the panel");
            }
        }
        VisualDesign.HighContrastOverride=true;
        try{foreach(string style in ThemePalette.Keys)foreach(bool dark in new[]{false,true}){
            Mode("normal",style);var palette=ThemePalette.Current(dark);Check(Role(palette,"PanelShadowNear").A==0&&Role(palette,"PanelShadowFar").A==0,"high contrast never uses custom shadow colors");Check(Role(palette,"PanelBorder")==SystemColors.WindowText,"high contrast retains the solid system border");using(var fixture=new Fixture())foreach(bool wireless in new[]{false,true})using(var image=Render(fixture.Owner,wireless,dark,new PanelFeedback {KeyboardFocus=102}))Check(image.Width==VisualDesign.PanelWidth,"high contrast rendering completes");
        }}finally{VisualDesign.HighContrastOverride=false;}
    }
    static void MicrophoneNames(){
        foreach(bool wireless in new[]{false,true}){Mode("normal");using(var fixture=new Fixture())using(var host=new Control()){
            var owner=fixture.Owner;owner.GenericReady=wireless;var handle=host.Handle;
            foreach(bool? muted in new bool?[]{true,false,null}){owner.Audio.MicrophoneMuted=muted;var rows=PopupPages.Build(owner,new PopupNavigation(),true);int index=rows.FindIndex(delegate(PopupControl row){return row.Id==102;});var tree=new PopupAccessibility(host,delegate{return rows;},delegate{return -1;},delegate(PopupControl row){throw new Exception("No invocation");},delegate{return 1f;},null,null);
                Check(tree.GetChild(index).Name.Contains(muted.HasValue?muted.Value?"已静音":"未静音":"未知"),"icon-only microphone keeps the original state in its accessible name: "+tree.GetChild(index).Name);
            }
            typeof(TrayContext).GetField("audioNeedsReconcile",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(owner,true);
            var blocked=PopupPages.Build(owner,new PopupNavigation(),true);Check(blocked.Find(delegate(PopupControl row){return row.Id==102;}).Text.Contains("待确认"),"blocked microphone keeps the pending state in its spoken name");
            typeof(TrayContext).GetField("audioNeedsReconcile",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(owner,false);
            typeof(TrayContext).GetField("pendingMicrophoneMute",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(owner,(bool?)true);
            var pending=PopupPages.Build(owner,new PopupNavigation(),true);Check(pending.Find(delegate(PopupControl row){return row.Id==102;}).Text.Contains("待确认"),"in-flight microphone keeps the pending state in its spoken name");
            typeof(TrayContext).GetField("pendingMicrophoneMute",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(owner,null);
            owner.Audio.MicrophoneMuted=true;owner.Audio.MicrophoneAvailable=false;
            var unavailable=PopupPages.Build(owner,new PopupNavigation(),true);var mic=unavailable.Find(delegate(PopupControl row){return row.Id==102;});
            Check(!mic.Enabled&&mic.Text.Contains("未知"),"unavailable microphones cannot announce an old mute state as current");
            var volume=unavailable.Find(delegate(PopupControl row){return row.Id==240;});Check(volume.Text.Contains(owner.Audio.SpeakerName),"system output device name is in the slider's spoken name");
        }}
    }
    static bool SameArea(Bitmap a,Bitmap b,Rectangle area){for(int y=area.Top;y<area.Bottom;y++)for(int x=area.Left;x<area.Right;x++)if(a.GetPixel(x,y)!=b.GetPixel(x,y))return false;return true;}
    static int ColorPixels(Bitmap image,Rectangle area,Color color){int result=0;for(int y=area.Top;y<area.Bottom;y++)for(int x=area.Left;x<area.Right;x++)if(image.GetPixel(x,y).ToArgb()==color.ToArgb())result++;return result;}
    static bool InkMatches(Bitmap image,Rectangle area,Color expected){
        // A 13px hyphen can have at most 107/255 coverage. Compare premultiplied
        // channels within one quantization unit instead of requiring opaque ink.
        int count=0;for(int y=area.Top;y<area.Bottom;y++)for(int x=area.Left;x<area.Right;x++){var pixel=image.GetPixel(x,y);if(pixel.A<32)continue;count++;if(Math.Abs(pixel.R-expected.R)*pixel.A>255||Math.Abs(pixel.G-expected.G)*pixel.A>255||Math.Abs(pixel.B-expected.B)*pixel.A>255)throw new Exception("placeholder ink at "+x+","+y+" is "+pixel+", expected "+expected);}Check(count>0,"placeholder has visible ink in "+area);return true;
    }
    static bool Different(Bitmap a,Bitmap b){for(int y=0;y<a.Height;y++)for(int x=0;x<a.Width;x++)if(a.GetPixel(x,y)!=b.GetPixel(x,y))return true;return false;}
    static void MicrophonePresentation(){
        foreach(string font in new[]{"normal","large"})foreach(bool wireless in new[]{false,true})foreach(bool dark in new[]{false,true}){Mode(font);using(var fixture=new Fixture()){
            var owner=fixture.Owner;owner.Audio.MicrophoneMuted=true;var area=Rectangle.Round(VisualDesign.MicrophoneMuteRectFor(owner.Dpi));
            using(var muted=Render(owner,wireless,dark,new PanelFeedback()))using(var pending=Render(owner,wireless,dark,new PanelFeedback {MicrophonePending=true,ShowPendingHints=true})){
                Check(RedPixels(muted,area)>0&&RedPixels(pending,area)==0,"pending microphone icon uses secondary ink instead of a confirmed red mute state");
            }
            owner.Audio.MicrophoneAvailable=false;using(var unavailable=Render(owner,wireless,dark,new PanelFeedback()))Check(RedPixels(unavailable,area)==0,"unavailable microphone never paints an old mute state in red");
        }}
    }
    static int RedPixels(Bitmap image,Rectangle area){
        // The 14px icon has antialiased strokes; count red ink rather than requiring
        // a fully covered exact-color pixel. The faint red surface stays below 40.
        int result=0;for(int y=area.Top;y<area.Bottom;y++)for(int x=area.Left;x<area.Right;x++){var pixel=image.GetPixel(x,y);if(pixel.R-pixel.G>40&&pixel.R-pixel.B>40)result++;}return result;
    }
    static void FooterPaging(){FooterPaging("bar");}
    static void FooterPaging(string panelLayout){
        foreach(string font in new[]{"normal","large"}){Mode(font,"ocean",false,panelLayout);using(var fixture=new Fixture()){
            var owner=fixture.Owner;owner.NativeMousePresent=false;owner.Headset=null;owner.GenericReady=true;owner.WirelessDevices=new[]{new WirelessDevice {Id="one",Name="第一台",Kind="keyboard"},new WirelessDevice {Id="two",Name="第二台",Kind="mouse"},new WirelessDevice {Id="three",Name="第三台",Kind="gamepad"}};
            foreach(int offset in new[]{0,1}){
                int maximum=VisualDesign.GenericCardHeight+VisualDesign.DeviceGap+VisualDesign.SystemVolumeHeight+VisualDesign.MainFooterGap+VisualDesign.FooterHeight;var layout=WirelessLayout.Build(owner,offset,maximum);var rows=PopupPages.Build(owner,new PopupNavigation(),false,layout);var regions=new List<InteractionRegion>();InteractionLayout.Observer=regions.Add;
                try{using(var image=new Bitmap(layout.Size.Width,layout.Size.Height))using(var g=Graphics.FromImage(image))CompactRenderer.PaintWireless(g,owner,layout,false,new PanelFeedback());}finally{InteractionLayout.Observer=null;}
                Check(layout.Paged&&layout.Cards.Count==1,"bounded device pages reserve the volume and footer");
                Check(layout.Size.Height<=maximum,"paged device panel stays within its available height");
                foreach(int id in new[]{0,1,902,903}){var row=rows.Find(delegate(PopupControl item){return item.Id==id;});var region=regions.Find(delegate(InteractionRegion item){return item.Id==id;});Check(row!=null&&region!=null&&row.Rect==region.Limit&&row.Rect.Size==new SizeF(28,28),"pagination, refresh and more use their actual 28x28 drawing rectangles");}
            }
        }}
    }
    static void FloatingSegments(){
        foreach(string font in new[]{"normal","large"})foreach(string style in ThemePalette.Keys)foreach(bool dark in new[]{false,true}){
            Mode(font,style);var rect=new RectangleF(12,12,92,VisualDesign.SelectionHeight);var palette=ThemePalette.Current(dark);
            Func<bool,bool,Bitmap> render=delegate(bool hover,bool pressed){var image=new Bitmap(120,64);using(var g=Graphics.FromImage(image)){g.Clear(palette.Track);g.SmoothingMode=SmoothingMode.AntiAlias;CompactRenderer.Selection(g,rect,"800",null,true,true,hover,pressed,false,dark);}return image;};
            using(var normal=render(false,false))using(var hover=render(true,false))using(var pressed=render(false,true)){
                Check(normal.GetPixel(20,15).ToArgb()!=palette.Selected.ToArgb(),"selected thumb is neutral, not an accent slab");Check(Different(normal,hover)&&Different(normal,pressed),"selected segment has subtle hover and one-pixel press feedback");
            }
        }
    }
    static void PlainFooterAndVolume(){
        foreach(string font in new[]{"normal","large"}){Mode(font);using(var fixture=new Fixture()){
            var owner=fixture.Owner;var labels=new List<DrawnLabel>();CompactRenderer.LabelObserver=labels.Add;
            try{using(var image=Render(owner,false,false,new PanelFeedback())){
                var footer=VisualDesign.FooterRectFor(true,owner.Dpi);Check(image.GetPixel(45,(int)footer.Top+3).A==0,"footer has no input-like background");Check(!labels.Exists(delegate(DrawnLabel label){return label.Text.StartsWith("系统音量 ·");}),"main volume has no device-name row");
            }}finally{CompactRenderer.LabelObserver=null;}
            Check(VisualDesign.SystemVolumeHeight==(font=="large"?40:36)&&VisualDesign.SystemVolumeHeightFor(true)==(font=="large"?36:32),"expanded and collapsed volume heights follow the design");var mic=VisualDesign.MicrophoneMuteRectFor(owner.Dpi);Check(mic.Width==(font=="large"?38:34),"icon-only microphone is 34/38px wide");
            Check(VisualDesign.FooterRefreshRect(0).Size==new SizeF(28,28)&&VisualDesign.FooterMoreRect(0).Size==new SizeF(28,28),"footer icon buttons stay 28x28 in both fonts");
            var track=VisualDesign.MainVolumeTrack(VisualDesign.VolumeSliderRect(VisualDesign.SystemVolumeRectFor(true,owner.Dpi)));Check(track.Height==4,"system volume has a four-pixel track");
            foreach(int value in new[]{0,50,100}){
                owner.Audio.OutputVolumePercent=value;
                using(var normal=Render(owner,false,true,new PanelFeedback()))using(var hover=Render(owner,false,true,new PanelFeedback {HoverAction=240}))using(var drag=Render(owner,false,true,new PanelFeedback {PressedAction=240,VolumePreview=value})){
                    var knob=VisualDesign.VolumeKnobRect(track,value);var active=VisualDesign.VolumeKnobRect(track,value,true);Check(knob.Size==new SizeF(12,12)&&active.Size==new SizeF(14,14),"volume knobs grow from twelve to fourteen pixels");
                    var sample=new Rectangle((int)active.X-1,(int)active.Y-1,17,17);Color ink=ThemePalette.Current(true).Knob;Check(ColorPixels(hover,sample,ink)>ColorPixels(normal,sample,ink)&&ColorPixels(hover,sample,ink)==ColorPixels(drag,sample,ink),"real hover and drag both paint the larger knob at zero, mid and full volume");
                }
            }
            Console.WriteLine(font+" panel heights: no headset="+VisualDesign.PanelSizeFor(false,owner.Dpi).Height+", headset="+VisualDesign.PanelSizeFor(true,owner.Dpi).Height+", eight DPI="+VisualDesign.PanelSizeFor(false,Dpi(8)).Height+"/"+VisualDesign.PanelSizeFor(true,Dpi(8)).Height);
        }}
    }
    static void PendingSegments(){
        foreach(string font in new[]{"normal","large"}){Mode(font);using(var fixture=new Fixture()){
            var owner=fixture.Owner;owner.Dpi.Stages[2].YDpi=1600;
            foreach(bool hints in new[]{false,true}){
                var labels=new List<DrawnLabel>();CompactRenderer.LabelObserver=labels.Add;
                try{using(var image=Render(owner,false,false,new PanelFeedback {PendingStage=2,PendingOutput="headset",ShowPendingHints=hints})){} }finally{CompactRenderer.LabelObserver=null;}
                Check(labels.Exists(delegate(DrawnLabel label){return label.Text=="Y 1600…";})==hints&&labels.Exists(delegate(DrawnLabel label){return label.Text=="耳机…";})==hints,"native DPI and audio pending ellipses respect the existing hint time");
            }
            var rect=VisualDesign.StageRect(1,4);var labelsNormal=new List<DrawnLabel>();var labelsPressed=new List<DrawnLabel>();
            using(var image=new Bitmap(VisualDesign.PanelWidth,VisualDesign.CardHeightNormal))using(var g=Graphics.FromImage(image)){
                var focus=new List<RectangleF>();CompactRenderer.InteractionOutlineObserver=focus.Add;
                try{
                    CompactRenderer.LabelObserver=labelsNormal.Add;CompactRenderer.Selection(g,rect,"800",null,true,true,false,false,false,false,true,VisualDesign.PanelCardRect(0,VisualDesign.CardHeightNormal),11);
                    CompactRenderer.LabelObserver=labelsPressed.Add;CompactRenderer.Selection(g,rect,"800",null,true,true,false,true,false,false,false,VisualDesign.PanelCardRect(0,VisualDesign.CardHeightNormal),11);
                }finally{CompactRenderer.LabelObserver=null;CompactRenderer.InteractionOutlineObserver=null;}
                Check(focus.Count==1&&focus[0]==VisualDesign.SelectionFocusRect(rect),"segment focus is outside the floating thumb with a one-pixel gutter and two-pixel stroke");
                Check(labelsPressed[0].DeviceRect.Top-labelsNormal[0].DeviceRect.Top==1,"pressed segment contents actually move down one pixel");
                g.Clear(ThemePalette.Current(false).Track);CompactRenderer.Selection(g,rect,"800",null,true,true,false,false,true,false);
                var pip=VisualDesign.SelectionPipRect(rect);Check(image.GetPixel((int)pip.X+1,(int)pip.Y+1).ToArgb()!=image.GetPixel((int)pip.X+3,(int)pip.Y+1).ToArgb(),"pending replaces the solid pip with separated dots");
            }
        }}
    }
    static void RenderPreviews(string directory,string panelLayout="bar",bool fullMatrix=false){
        Directory.CreateDirectory(directory);const int scale=2;
        foreach(string style in ThemePalette.Keys)foreach(bool dark in new[]{false,true}){
            var frames=new List<Bitmap>();
            try{
                foreach(string font in new[]{"normal","large"}){
                    Mode(font,style,false,panelLayout);using(var fixture=new Fixture()){
                        var owner=fixture.Owner;frames.Add(Render(owner,false,dark,new PanelFeedback(),scale));
                        if(style=="ocean"){
                            string stem=Path.Combine(directory,"main-panel-"+panelLayout+"-ocean-"+(dark?"dark":"light")+"-"+font);
                            frames[frames.Count-1].Save(stem+"-headset.png",ImageFormat.Png);var ear=owner.Headset;owner.Headset=null;using(var noHeadset=Render(owner,false,dark,new PanelFeedback(),scale))noHeadset.Save(stem+"-no-headset.png",ImageFormat.Png);owner.Headset=ear;
                        }
                        if(fullMatrix){
                            var ear=owner.Headset;
                            foreach(bool contrast in new[]{false,true})foreach(bool headset in new[]{false,true})foreach(bool wireless in new[]{false,true}){
                                VisualDesign.HighContrastOverride=contrast;owner.Headset=headset?ear:null;
                                string path=Path.Combine(directory,"main-panel-"+panelLayout+"-"+style+"-"+(dark?"dark":"light")+"-"+font+"-"+(headset?"headset":"no-headset")+"-"+(contrast?"high-contrast":"standard")+"-"+(wireless?"wireless":"native")+".png");
                                using(var image=Render(owner,wireless,dark,new PanelFeedback(),scale))image.Save(path,ImageFormat.Png);
                            }
                            VisualDesign.HighContrastOverride=false;owner.Headset=ear;
                        }
                        owner.GenericReady=true;owner.Audio.MicrophoneMuted=true;
                        owner.WirelessDevices=new[]{
                            new WirelessDevice {Id="charging-keyboard",Name="VGN V98 Pro V3",Kind="keyboard",Transport="2.4G",Percent=72,Fresh=true,Online=true,ConnectionState="connected",ConfirmedAtUtc=DateTime.UtcNow.ToString("o"),Charging=true,ChargingCheckedAtUtc=DateTime.UtcNow.ToString("o")},
                            new WirelessDevice {Id="full-approximate",Name="非常长的设备名称用于验证省略",Kind="headset",Transport="Bluetooth",Percent=100,Fresh=true,Approximate=true,Online=true,ConnectionState="connected",ConfirmedAtUtc=DateTime.UtcNow.ToString("o")},
                            new WirelessDevice {Id="low-gamepad",Name="Xbox 无线手柄",Kind="gamepad",Transport="Bluetooth",Percent=12,Fresh=true,Online=true,ConnectionState="connected",ConfirmedAtUtc=DateTime.UtcNow.ToString("o")},
                            new WirelessDevice {Id="stale-mouse",Name="历史读数",Kind="mouse",Percent=46,Fresh=false,Online=false,ConnectionState="disconnected",ConfirmedAtUtc=DateTime.UtcNow.AddHours(-2).ToString("o")},
                            new WirelessDevice {Id="empty-reading",Name="无读数设备",Kind="keyboard",Online=true,ConnectionState="connected"}
                        };
                        frames.Add(Render(owner,true,dark,new PanelFeedback(),scale));
                    }
                }
                int width=0,height=0;foreach(var frame in frames){width+=frame.Width+32;height=Math.Max(height,frame.Height);}
                using(var sheet=new Bitmap(width+32,height+112,PixelFormat.Format32bppPArgb))using(var g=Graphics.FromImage(sheet)){
                    g.Clear(dark?Color.FromArgb(27,34,48):Color.FromArgb(237,241,247));
                    CompactRenderer.DrawLabel(g,PanelLayouts.Name(panelLayout)+" / "+ThemePalette.Name(style)+" / "+(dark?"深色":"浅色")+"  ·  标准原生 / 标准多设备 / 较大原生 / 较大多设备",new RectangleF(32,8,width-32,56),24,true,dark?Color.White:Color.FromArgb(27,36,48),StringAlignment.Near);
                    int x=32;foreach(var frame in frames){g.DrawImageUnscaled(frame,x,80);x+=frame.Width+32;}
                    string path=Path.Combine(directory,"main-panel-"+panelLayout+"-"+style+(dark?"-dark":"-light")+".png");sheet.Save(path,ImageFormat.Png);Console.WriteLine(path);
                }
            }finally{foreach(var frame in frames)frame.Dispose();}
        }
        if(panelLayout=="grid")RenderGridOverflowPreviews(directory);
    }
    static void RenderLayoutChoices(string directory,bool fullMatrix){
        foreach(string style in fullMatrix?ThemePalette.Keys:new[]{"ocean"})foreach(string font in new[]{"normal","large"})foreach(bool dark in new[]{false,true})foreach(bool contrast in new[]{false,true}){
            Mode(font,style,false,"ring");VisualDesign.HighContrastOverride=contrast;using(var fixture=new Fixture()){
                var nav=new PopupNavigation {Page=PopupPage.ChoosePreference,PreferenceAction=229};var rows=PopupPages.Build(fixture.Owner,nav,true);var size=UiState.ScaleSize(PopupPages.SizeFor(rows),2);
                string path=Path.Combine(directory,"layout-chooser-"+style+"-"+(dark?"dark":"light")+"-"+font+"-"+(contrast?"high-contrast":"standard")+".png");
                using(var image=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppPArgb))using(var g=Graphics.FromImage(image)){g.ScaleTransform(2,2);PopupPages.Paint(g,fixture.Owner,nav,rows,dark,-1,-1,-1);image.Save(path,ImageFormat.Png);}Console.WriteLine(path);
            }
        }
        VisualDesign.HighContrastOverride=false;
    }
    static void RenderGridOverflowPreviews(string directory){
        foreach(string font in new[]{"normal","large"})foreach(bool dark in new[]{false,true}){
            Mode(font,"ocean",false,"grid");using(var fixture=new Fixture()){
                var owner=fixture.Owner;owner.GenericReady=true;owner.Dpi=Dpi(8);owner.WirelessDevices=new[]{GridDevice(0,8),GridDevice(1,8),GridDevice(2,8),GridDevice(3,8)};
                for(int i=0;i<owner.WirelessDevices.Length;i++){var device=owner.WirelessDevices[i];device.Id="device-"+i.ToString().PadLeft(24,'0');device.Provider="logitech";device.SourceQuality="verified";device.MeasurementState="measured";device.Dpi.StageSource="onboard";}
                string stem=Path.Combine(directory,"main-panel-grid-five-mice-ocean-"+(dark?"dark":"light")+"-"+font);
                var normal=WirelessLayout.Build(owner,0,10000);using(var image=RenderGrid(owner,normal,dark,2,new PanelFeedback()))image.Save(stem+"-normal.png",ImageFormat.Png);
                foreach(int offset in new[]{0,Int32.MaxValue}){
                    var bounded=WirelessLayout.Build(owner,0,520,false,offset);string path=stem+"-520-"+(offset==0?"top":"bottom")+".png";
                    using(var image=RenderGrid(owner,bounded,dark,2,new PanelFeedback()))image.Save(path,ImageFormat.Png);Console.WriteLine(path);
                }
            }
        }
    }
    [STAThread] static int Main(string[] args){var previous=DeviceSettings.Current;try{
        if(args.Length==2&&args[0]=="--text-native-experiments"){TextNativeExperiments(args[1]);return 0;}
        if(args.Length==1&&args[0]=="--text-raster"){
            Run("text raster reuse contract",TextRasterReuseContract);Run("text raster complete inputs and native pixel equality",TextRasterInputs);Run("text raster bounded resources and disposal",TextRasterResources);return failures==0?0:1;
        }
        if(args.Length>0&&args[0]=="--grid-repaint-probe")return GridRepaintProbe(args);
        if(args.Length==1&&args[0]=="--grid-repaint"){
            Run("150% native light first-frame, same-layout redraw and rebuilt offsets",GridRepaintIdentity);return failures==0?0:1;
        }
        if(args.Length>=2&&args[0]=="--render"){
            VisualDesign.HighContrastOverride=false;bool matrix=Array.IndexOf(args,"--matrix")>=0;var layouts=args.Length>2&&args[2]!="all"&&args[2]!="--matrix"?new[]{args[2]}:PanelLayouts.Keys;
            foreach(string layout in layouts)RenderPreviews(args[1],layout,matrix);RenderLayoutChoices(args[1],matrix);return 0;
        }
        Run("opening fill frames, real labels, states and reduced motion",OpeningFill);
        if(args.Length==1&&args[0]=="--opening-fill")return failures==0?0:1;
        if(args.Length==1&&args[0]=="--polish"){
            foreach(string layout in PanelLayouts.Keys){string key=layout;Run("complete rendering matrix / "+key,delegate{RenderMatrix(key);});}
            Console.WriteLine("192 layout/palette/light/font/headset/contrast combinations, both render paths; "+checks+" checks, "+failures+" failed groups");return failures==0?0:1;
        }
        Run("grid overflow 520px, five mice x eight stages, normal",delegate{GridOverflowHeight("normal");});
        Run("grid overflow 520px, five mice x eight stages, large",delegate{GridOverflowHeight("large");});
        Run("grid overflow viewport, clipping, fixed volume and scale matrix",GridOverflowViewportMatrix);
        Run("150% native light first-frame, same-layout redraw and rebuilt offsets",GridRepaintIdentity);
        Run("text raster reuse contract",TextRasterReuseContract);Run("text raster complete inputs and native pixel equality",TextRasterInputs);Run("text raster bounded resources and disposal",TextRasterResources);
        if(args.Length==1&&args[0]=="--grid-overflow")return failures==0?0:1;
        Run("grid dispatch, two-column tiles and fixed quick controls",GridBasics);
        Run("grid 1..5 devices, whole-row paging and fixed controls",GridCountsAndPaging);Run("grid all-device control groups and hidden devices",GridGroupsAndHidden);
        Run("grid shared drawing and hit geometry",delegate{HitGeometry("grid");});Run("grid complete theme and render path matrix",delegate{RenderMatrix("grid");});Run("grid typography, status and accessibility at five DPI scales",GridTypographyAndStates);
        Run("grid feedback, control names and pending semantics",GridFeedbackAndControls);
        if(args.Length==1&&args[0]=="--grid")return failures==0?0:1;
        Run("layout defaults, migration, persistence and normalization",LayoutSettings);Run("ring geometry, full-row details and calculated heights",RingGeometry);
        Run("ring draw and hit geometry",delegate{HitGeometry("ring");});Run("ring color, font, headset, contrast and render path matrix",delegate{RenderMatrix("ring");});Run("ring tiny, full, cached, sleeping and charging boundaries",RingBoundaries);Run("ring state accessibility and typography at five DPI scales",RingNamesAndTypography);
        Run("ring paging reserves volume and the external footer",delegate{FooterPaging("ring");});
        if(args.Length==1&&args[0]=="--ring")return failures==0?0:1;
        VisualDesign.HighContrastOverride=false;Mode("normal");Run("battery bar proportions and bounds",BarGeometry);Run("battery digits and percent baselines at five DPI scales",BatteryTypography);Run("shared draw and hit geometry",HitGeometry);Run("four themes x light/dark x fonts x headset x high contrast x render paths",RenderMatrix);Run("card shadows and high contrast",ContrastAndShadows);Run("icon-only microphone accessible state",MicrophoneNames);Run("microphone pending and unavailable ink",MicrophonePresentation);Run("floating segmented controls",FloatingSegments);Run("pending hints, press offset and segment focus",PendingSegments);Run("plain footer and single-row volume",PlainFooterAndVolume);Run("bounded device page footer geometry",FooterPaging);
        Console.WriteLine(checks+" checks, "+failures+" failed groups; fake devices and audio only");return failures==0?0:1;
    }finally{CompactRenderer.LabelObserver=null;CompactRenderer.SymbolObserver=null;CompactRenderer.TextRasterObserver=null;InteractionLayout.Observer=null;InteractionLayout.TextBoundsObserver=null;VisualDesign.HighContrastOverride=null;DeviceSettings.UseTransientConfiguration(previous);}}
}
