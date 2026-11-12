using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

// Focused drawing checks. The runner compiles only under TEMP; no hardware or settings writes.
static class HoverTests {
    static int failures,checks;
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;}
    static void Run(string name,Action test){try{test();Console.WriteLine("PASS "+name);}catch(Exception e){failures++;Console.WriteLine("FAIL "+name+": "+e.Message);}}
    static void Style(string style){var pref=DeviceSettings.Current;pref.ThemeStyle=style;DeviceSettings.UseTransientConfiguration(pref);Check(ThemePalette.Current(false).Accent==ThemePalette.For(style,false).Accent,"fixture applies the requested palette");}
    static void Collapsed(bool collapsed){var pref=DeviceSettings.Current;pref.VolumeCollapsed=collapsed;DeviceSettings.UseTransientConfiguration(pref);}
    static int AccentPixels(Bitmap image,Color accent){int count=0;for(int y=0;y<image.Height;y++)for(int x=0;x<image.Width;x++){var c=image.GetPixel(x,y);if(c.A>200&&Math.Abs(c.R-accent.R)+Math.Abs(c.G-accent.G)+Math.Abs(c.B-accent.B)<12)count++;}return count;}
    static void Feedback(){
        foreach(string style in ThemePalette.Keys)foreach(float scale in new[]{1f,1.5f,2f})foreach(bool dark in new[]{false,true})foreach(bool pressed in new[]{false,true}){
            Style(style);
            using(var image=new Bitmap((int)(80*scale),(int)(60*scale)))using(var g=Graphics.FromImage(image)){
                g.Clear(ThemePalette.Current(dark).Card);g.ScaleTransform(scale,scale);g.SmoothingMode=SmoothingMode.AntiAlias;
                CompactRenderer.Highlight(g,new RectangleF(10,10,60,40),7,new PanelFeedback {HoverAction=pressed?-1:7,PressedAction=pressed?7:-1},dark);
                Check(AccentPixels(image,ThemePalette.Current(dark).Accent)==0,"hover/press must not paint an accent outline at "+(96*scale)+" DPI");
            }
        }
    }
    static void Focus(){
        using(var image=new Bitmap(80,60))using(var g=Graphics.FromImage(image)){
            g.Clear(ThemePalette.Current(false).Card);g.SmoothingMode=SmoothingMode.AntiAlias;
            CompactRenderer.Highlight(g,new RectangleF(10,10,60,40),7,new PanelFeedback {KeyboardFocus=7},false);
            Check(AccentPixels(image,ThemePalette.Current(false).Accent)>0,"the shared highlight path must paint keyboard focus independently of hover");
        }
        using(var image=new Bitmap(80,60))using(var g=Graphics.FromImage(image)){
            var palette=ThemePalette.Current(false);g.Clear(palette.Card);
            CompactRenderer.Highlight(g,new RectangleF(10,10,60,40),-1,new PanelFeedback(),false);
            Check(AccentPixels(image,palette.Accent)==0&&image.GetPixel(40,30)==palette.Card,"information rows never match the -1 no-action sentinel");
            CompactRenderer.Highlight(g,new RectangleF(10,10,60,40),7,new PanelFeedback {HoverAction=7,KeyboardFocus=7},false);
            Check(AccentPixels(image,palette.Accent)>0&&image.GetPixel(40,30)!=palette.Card,"hover fill and keyboard focus coexist");
        }
    }
    sealed class Scene {
        internal string Name;internal Size Size;internal Action<Graphics,PanelFeedback,bool> Paint;
        internal Func<int,float> Offset;
    }
    sealed class Fixture:IDisposable {
        internal TrayContext Owner;
        internal Fixture(){
            DeviceSettings.UseTransientConfiguration(new AudioBindings {PanelLayout="bar",ReducedMotion=true,RememberReadings=false,LowBatteryNotifications=false,VolumeCollapsed=false});
            Owner=new TrayContext(false,delegate(bool battery){return new MouseSnapshot {Battery=new BatteryReading {Percent=80,TimestampUtc=DateTime.UtcNow},Dpi=Dpi()};},delegate(int stage){throw new Exception("No writes");},delegate{return new HeadsetReading();},Audio,false,delegate(string id){throw new Exception("No writes");},delegate(bool muted){throw new Exception("No writes");},delegate(string id,int v){throw new Exception("No writes");},delegate{return false;});
            DateTime until=DateTime.UtcNow.AddSeconds(5);while((Owner.IsRefreshing||Owner.IsAudioBusy||Owner.IsHeadsetRefreshing||Owner.Audio==null)&&DateTime.UtcNow<until){Application.DoEvents();Thread.Sleep(2);}
            Check(!Owner.IsRefreshing&&!Owner.IsAudioBusy&&Owner.Audio!=null,"injected readers settle");
            ((System.Windows.Forms.Timer)typeof(TrayContext).GetField("refresh",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(Owner)).Stop();
            Owner.Current=new BatteryReading {Percent=80,TimestampUtc=DateTime.UtcNow};Owner.Dpi=Dpi();Owner.Audio=Audio();Owner.NativeMousePresent=false;Owner.GenericReady=true;
            Owner.WirelessDevices=new[]{Device("a",80,4),Device("b",100,8),Device("c",null,0),Device("d",15,0)};
        }
        public void Dispose(){Owner.ExitThread();Owner.Dispose();}
    }
    static DpiConfig Dpi(){var dpi=new DpiConfig {ActiveStage=1,TimestampUtc=DateTime.UtcNow};foreach(int value in new[]{400,800,1600,3200,6400,12800,20000,24000})dpi.Stages.Add(new DpiStage {XDpi=value,YDpi=value==6400?3200:value});return dpi;}
    static AudioReading Audio(){return new AudioReading {TimestampUtc=DateTime.UtcNow,SpeakerAvailable=true,SpeakerId="speaker",SpeakerName="扬声器",HeadsetAvailable=true,HeadsetId="headset",HeadsetName="耳机",MicrophoneAvailable=true,MicrophoneMuted=false,ActiveOutput="speaker",VolumeEndpointId="speaker",OutputVolumePercent=0,Devices=new[]{new AudioDeviceChoice {Id="headset",Name="耳机",Flow=0,FormFactor=3}}};}
    static WirelessDevice Device(string key,int? percent,int stages){return new WirelessDevice {Id="device-"+key.PadLeft(24,'0'),Name="设备 "+key,Kind=stages>0?"mouse":"keyboard",Transport="2.4G",Online=true,Fresh=true,ConnectionState="connected",MeasurementState="measured",Percent=percent,Approximate=key=="b",ConfirmedAtUtc=DateTime.UtcNow.ToString("o"),Dpi=stages==0?null:new WirelessDpi {Supported=true,CanSet=true,Current=800,Stages=stages==4?new[]{400,800,1600,3200}:new[]{400,800,1600,3200,6400,12800,20000,24000},TargetToken=new string(key[0],64),CheckedAtUtc=DateTime.UtcNow.ToString("o")}};}
    static List<Scene> Scenes(TrayContext owner){
        var scenes=new List<Scene>();
        var headset=new HeadsetReading {Connected=true,ConnectionVerified=true,Percent=80,TimestampUtc=DateTime.UtcNow,BatteryTimestampUtc=DateTime.UtcNow};
        scenes.Add(new Scene {Name="主面板",Size=VisualDesign.PanelSizeFor(true,owner.Dpi),Paint=delegate(Graphics g,PanelFeedback f,bool dark){Collapsed(false);CompactRenderer.Paint(g,owner.Current,null,null,false,owner.Dpi,null,false,dark,headset,owner.Audio,1,-1,false,1,-1,f);}});
        scenes.Add(new Scene {Name="音源卡",Size=VisualDesign.PanelSizeFor(true,owner.Dpi),Paint=delegate(Graphics g,PanelFeedback f,bool dark){Collapsed(false);CompactRenderer.Paint(g,owner.Current,null,null,false,owner.Dpi,null,false,dark,null,owner.Audio,1,-1,false,1,-1,f);}});
        scenes.Add(new Scene {Name="收起音量",Size=new Size(VisualDesign.PanelWidth,VisualDesign.SystemVolumeHeightFor(true)),Paint=delegate(Graphics g,PanelFeedback f,bool dark){Collapsed(true);CompactRenderer.SystemVolume(g,new RectangleF(0,0,VisualDesign.PanelWidth,VisualDesign.SystemVolumeHeightFor(true)),owner.Audio,dark,f);Collapsed(false);}});
        var layout=WirelessLayout.Build(owner,1,380);
        scenes.Add(new Scene {Name="多设备 / 翻页",Size=layout.Size,Offset=delegate(int id){return NativeOffset(layout,id);},Paint=delegate(Graphics g,PanelFeedback f,bool dark){CompactRenderer.PaintWireless(g,owner,layout,dark,f);}});
        var all=WirelessLayout.Build(owner,0,1000);
        scenes.Add(new Scene {Name="多设备 / 全部",Size=all.Size,Offset=delegate(int id){return NativeOffset(all,id);},Paint=delegate(Graphics g,PanelFeedback f,bool dark){CompactRenderer.PaintWireless(g,owner,all,dark,f);}});
        foreach(var page in new[]{PopupPage.More,PopupPage.Audio,PopupPage.Preferences,PopupPage.ChoosePreference,PopupPage.PickBinding}){
            var nav=new PopupNavigation {Page=page,PreferenceAction=223,VisibleChoices=100};var rows=PopupPages.Build(owner,nav,false);
            scenes.Add(new Scene {Name=PopupPages.Title(nav),Size=PopupPages.SizeFor(rows),Paint=delegate(Graphics g,PanelFeedback f,bool dark){PopupPages.Paint(g,owner,nav,rows,dark,f.HoverAction,f.PressedAction,f.KeyboardFocus);}});
        }
        for(int kind=0;kind<5;kind++){
            int variant=kind;
            int width=VisualDesign.PanelWidth,height=VisualDesign.SelectionHeight+34;
            scenes.Add(new Scene {Name=new[]{"弹窗 / 按钮","弹窗 / 主按钮","弹窗 / 文本按钮","弹窗 / 类型分段","弹窗 / 开关"}[kind],Size=new Size(width,height),Paint=delegate(Graphics g,PanelFeedback f,bool dark){
                var card=new RectangleF(.5f,.5f,width-1,height-1);var rect=new RectangleF(16,17,width-32,VisualDesign.SelectionHeight);CompactRenderer.DrawSurface(g,card,dark);
                if(variant==0||variant==1||variant==3){CompactRenderer.Fill(g,rect,ThemePalette.Current(dark).Track,VisualDesign.SelectionRadius);CompactRenderer.Selection(g,rect,variant==3?"问题反馈":"确认",null,variant==1,true,f.HoverAction==0,f.PressedAction==0,false,dark,f.KeyboardFocus==0,card);}
                else InteractionLayout.Paint(g,card,rect,0,f,dark,false,delegate{CompactRenderer.DrawLabel(g,variant==2?"查看详情":"附带诊断信息",variant==2?rect:new RectangleF(rect.X+6,rect.Y,rect.Width-51,rect.Height),VisualDesign.DeviceNameFontSize,false,variant==2?ThemePalette.Current(dark).Accent:ThemePalette.Current(dark).Foreground,variant==2?StringAlignment.Center:StringAlignment.Near);if(variant==4)PopupPages.DrawToggle(g,rect,true,true,dark);});
            }});
        }
        return scenes;
    }
    static float NativeOffset(WirelessLayout layout,int id){if(id==3||id==5||id==200||id>=100&&id<=102)foreach(var card in layout.Cards)if(card.NativeHeadset||card.AudioOnly)return card.Top;return 0;}
    static Bitmap Render(Scene scene,float scale,bool dark,PanelFeedback feedback){var image=new Bitmap((int)Math.Ceiling(scene.Size.Width*scale),(int)Math.Ceiling(scene.Size.Height*scale),PixelFormat.Format32bppPArgb);using(var g=Graphics.FromImage(image)){g.Clear(Color.Transparent);g.ScaleTransform(scale,scale);g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;scene.Paint(g,feedback,dark);}return image;}
    static List<InteractionRegion> Regions(Scene scene,float scale,bool dark,PanelFeedback feedback){var regions=new List<InteractionRegion>();InteractionLayout.Observer=regions.Add;try{using(var image=Render(scene,scale,dark,feedback)){}return regions;}finally{InteractionLayout.Observer=null;}}
    static void Geometry(InteractionRegion r,float scale,string name){
        float[] padding={r.Content.Left-r.Bounds.Left,r.Content.Top-r.Bounds.Top,r.Bounds.Right-r.Content.Right,r.Bounds.Bottom-r.Content.Bottom};
        foreach(float p in padding)Check(p>=-.001f&&p<=6.001f&&Math.Abs(p-padding[0])*scale<=1,name+" equal bounded padding");
        Check((r.Bounds.Left-r.Card.Left)>=7.999f&&r.Card.Right-r.Bounds.Right>=7.999f&&r.Bounds.Top-r.Card.Top>=7.999f&&r.Card.Bottom-r.Bounds.Bottom>=7.999f,name+" >=8px card clearance");
        Check(r.Limit.Contains(r.Bounds),name+" stays in its own control/segment");
        // Independent signed distance to the inset card, sampled along the entire
        // feedback perimeter (including corners), at subpixel intervals.
        float radius=Math.Min(r.Radius,Math.Min(r.Bounds.Width,r.Bounds.Height)/2);
        for(int corner=0;corner<4;corner++)for(int step=0;step<=90;step++){
            double a=(180+corner*90+step)*Math.PI/180;float x=(corner==0||corner==3?r.Bounds.Left+radius:r.Bounds.Right-radius)+radius*(float)Math.Cos(a),y=(corner<2?r.Bounds.Top+radius:r.Bounds.Bottom-radius)+radius*(float)Math.Sin(a);
            float dx=Math.Abs(x-(r.Card.Left+r.Card.Right)/2)-(r.Card.Width/2-VisualDesign.PanelRadius),dy=Math.Abs(y-(r.Card.Top+r.Card.Bottom)/2)-(r.Card.Height/2-VisualDesign.PanelRadius);
            double distance=Math.Sqrt(Math.Pow(Math.Max(dx,0),2)+Math.Pow(Math.Max(dy,0),2))+Math.Min(Math.Max(dx,dy),0)-VisualDesign.PanelRadius;
            Check(distance<=-7.995,name+" avoids rounded card corners");
        }
        float gap=Math.Min(Math.Min(r.Bounds.Left-r.Card.Left,r.Card.Right-r.Bounds.Right),Math.Min(r.Bounds.Top-r.Card.Top,r.Card.Bottom-r.Bounds.Bottom));
        Check(Math.Abs(r.Radius-(r.Bounds.Height>=r.Card.Height/2?Math.Max(0,VisualDesign.PanelRadius-gap):8))<.001,name+" concentric or 8px corner radius");
    }
    static bool IsInteger(float value){return Math.Abs(value-(float)Math.Round(value))<.001f;}
    static void AllRegions(){using(var fixture=new Fixture())foreach(string fontSize in new[]{"normal","large"}){
        var pref=DeviceSettings.Current;pref.FontSize=fontSize;DeviceSettings.UseTransientConfiguration(pref);
        foreach(float scale in new[]{1f,1.25f,1.5f,1.75f,2f})foreach(var scene in Scenes(fixture.Owner)){
        var labels=scale==1f?new List<DrawnLabel>():null;CompactRenderer.LabelObserver=labels==null?(Action<DrawnLabel>)null:labels.Add;
        var baseline=Regions(scene,scale,false,new PanelFeedback());Check(baseline.Count>0,scene.Name+" has measured controls");
        CompactRenderer.LabelObserver=null;
        if(labels!=null){
            Check(labels.Count>0,scene.Name+" records 96 DPI drawn labels in "+fontSize);
            foreach(var label in labels)Check(IsInteger(label.FontSize)&&IsInteger(label.DeviceRect.X)&&IsInteger(label.DeviceRect.Y)&&IsInteger(label.DeviceRect.Width)&&IsInteger(label.DeviceRect.Height),scene.Name+" 96 DPI integer label "+label.Text+": "+label.DeviceRect+" font="+label.FontSize);
        }
        if(scene.Name=="主面板")Check(baseline.Exists(delegate(InteractionRegion r){return r.Id==240;}),"expanded main volume is included in the state matrix");
        foreach(var r in baseline){Geometry(r,scale,scene.Name+" "+r.Id);if(r.Id<0)continue;
            foreach(int state in new[]{1,2,3,4}){var f=new PanelFeedback {HoverAction=state==1||state==4?r.Id:-1,PressedAction=state==2?r.Id:-1,KeyboardFocus=state>=3?r.Id:-1};var outlines=new List<RectangleF>();CompactRenderer.InteractionOutlineObserver=outlines.Add;
                try{foreach(var active in Regions(scene,scale,false,f))if(active.Id==r.Id)Geometry(active,scale,scene.Name+" "+r.Id+" state "+state);}
                finally{CompactRenderer.InteractionOutlineObserver=null;}
                Check(outlines.Count==(state>=3&&r.Enabled?1:0),scene.Name+" "+r.Id+" only enabled keyboard focus draws an outline (state "+state+")");
            }
        }
    }
    pref=DeviceSettings.Current;pref.FontSize="normal";DeviceSettings.UseTransientConfiguration(pref);
    }}
    static void SelectionStates(){
        foreach(string style in ThemePalette.Keys)foreach(float scale in new[]{1f,1.5f,2f})foreach(bool dark in new[]{false,true}){
            Style(style);
            Func<bool,bool,bool,Bitmap> draw=delegate(bool hovered,bool pressed,bool focus){var image=new Bitmap((int)(120*scale),(int)(50*scale));using(var g=Graphics.FromImage(image)){g.Clear(ThemePalette.Current(dark).Card);g.ScaleTransform(scale,scale);g.SmoothingMode=SmoothingMode.AntiAlias;CompactRenderer.Selection(g,new RectangleF(10,10,100,30),"800",null,true,true,hovered,pressed,false,dark,focus);}return image;};
            using(var normal=draw(false,false,false))using(var hover=draw(true,false,false))using(var press=draw(false,true,false))using(var focused=draw(false,false,true)){
                bool focusVisible=false,hoverVisible=false,pressVisible=false;for(int y=0;y<normal.Height;y++)for(int x=0;x<normal.Width;x++){hoverVisible|=normal.GetPixel(x,y)!=hover.GetPixel(x,y);pressVisible|=normal.GetPixel(x,y)!=press.GetPixel(x,y);focusVisible|=normal.GetPixel(x,y)!=focused.GetPixel(x,y);}Check(hoverVisible&&pressVisible,"neutral selected segment retains subtle pointer feedback");Check(focusVisible,"keyboard focus remains visible over selected "+style+" fill");
            }
        }
    }
    static void HighContrast(){VisualDesign.HighContrastOverride=true;try{foreach(int state in new[]{1,2,3})using(var image=new Bitmap(80,60))using(var g=Graphics.FromImage(image)){g.Clear(SystemColors.Window);g.SmoothingMode=SmoothingMode.AntiAlias;CompactRenderer.Highlight(g,new RectangleF(10,10,60,40),7,new PanelFeedback {HoverAction=state==1?7:-1,PressedAction=state==2?7:-1,KeyboardFocus=state==3?7:-1},false);Check(AccentPixels(image,SystemColors.Highlight)>0,"high contrast keeps the system highlight outline");}}finally{VisualDesign.HighContrastOverride=false;}}
    static void InkBounds(){
        foreach(float scale in new[]{1f,1.5f,2f})foreach(string text in new[]{"80","100","≈100","设备名称","同步全部设备","这是一个很长的名称用于检查省略"})foreach(StringAlignment alignment in new[]{StringAlignment.Near,StringAlignment.Center,StringAlignment.Far}){
            using(var image=new Bitmap((int)(200*scale),(int)(60*scale)))using(var g=Graphics.FromImage(image)){
                g.Clear(Color.Transparent);g.ScaleTransform(scale,scale);g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                var rect=new RectangleF(12,10,126,40);float size=text.Length<=4?20:11.5f;bool bold=text.Length<=4;
                var ink=InteractionLayout.LabelBounds(g,text,rect,size,bold,alignment,StringTrimming.EllipsisCharacter);
                CompactRenderer.DrawLabel(g,text,rect,size,bold,Color.White,alignment);
                int left=image.Width,top=image.Height,right=-1,bottom=-1;for(int y=0;y<image.Height;y++)for(int x=0;x<image.Width;x++)if(image.GetPixel(x,y).A>=128){left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);}
                Check(right>=left,"text has visible ink");
                Check(Math.Abs(left-ink.Left*scale)<=1.01f&&Math.Abs(top-ink.Top*scale)<=1.01f&&Math.Abs(right+1-ink.Right*scale)<=1.01f&&Math.Abs(bottom+1-ink.Bottom*scale)<=1.01f,"measured glyph ink matches raster at "+scale+" / "+text+" / "+alignment+": expected "+ink+" raster="+Rectangle.FromLTRB(left,top,right+1,bottom+1));
            }
        }
    }
    static void RenderSheets(string output){
        Directory.CreateDirectory(output);
        using(var fixture=new Fixture())foreach(string style in ThemePalette.Keys)foreach(bool dark in new[]{false,true}){
            Style(style);var scenes=Scenes(fixture.Owner);var entries=new List<Tuple<Scene,InteractionRegion>>();
            foreach(var scene in scenes){var seen=new HashSet<int>();foreach(var r in Regions(scene,1,dark,new PanelFeedback()))if(r.Id>=0&&seen.Add(r.Id))entries.Add(Tuple.Create(scene,r));}
            const int rowHeight=68,width=1200;
            using(var sheet=new Bitmap(width,48+entries.Count*rowHeight,PixelFormat.Format32bppPArgb))using(var g=Graphics.FromImage(sheet)){
                g.Clear(Color.FromArgb(105,110,118));CompactRenderer.DrawLabel(g,ThemePalette.Name(style)+" / "+(dark?"深色":"浅色")+"   交互状态：默认 | 悬停 | 按下 | 键盘焦点",new RectangleF(12,4,width-24,40),16,true,Color.White,StringAlignment.Near);
                for(int row=0;row<entries.Count;row++){
                    var scene=entries[row].Item1;var region=entries[row].Item2;int id=region.Id;float y=48+row*rowHeight;
                    CompactRenderer.DrawLabel(g,scene.Name+" / "+id,new RectangleF(8,y,140,rowHeight),11,false,Color.White,StringAlignment.Near);
                    for(int state=0;state<4;state++)using(var image=Render(scene,1,dark,new PanelFeedback {HoverAction=state==1?id:-1,PressedAction=state==2?id:-1,KeyboardFocus=state==3?id:-1})){
                        float offset=scene.Offset!=null?scene.Offset(id):(scene.Name=="主面板"||scene.Name=="音源卡")&&(id==3||id==5||id==200||id>=100&&id<=102)?VisualDesign.MouseCardHeight(fixture.Owner.Dpi)+8:0;
                        float top=Math.Max(0,Math.Min(image.Height-rowHeight,region.Bounds.Top+offset+(region.Bounds.Height-rowHeight)/2));float height=Math.Min(rowHeight,image.Height);
                        g.DrawImage(image,new RectangleF(154+state*260,y+(rowHeight-height)/2,260,height),new RectangleF(0,top,260,height),GraphicsUnit.Pixel);
                    }
                }
                string path=Path.Combine(output,"interaction-states-"+style+"-"+(dark?"dark":"light")+".png");sheet.Save(path,ImageFormat.Png);Console.WriteLine(path);
            }
        }
    }
    [STAThread] static int Main(string[] args){var previous=DeviceSettings.Current;try{DeviceSettings.UseTransientConfiguration(new AudioBindings());VisualDesign.HighContrastOverride=false;if(args.Length==2&&args[0]=="--render"){RenderSheets(args[1]);return 0;}Run("fill-only hover and press",Feedback);Run("independent keyboard focus",Focus);Run("glyph bounds match rendered content",InkBounds);Run("all regions: geometry and outline recording at 96/120/144/168/192 DPI",AllRegions);Run("selected neutral segments show hover, press and focus",SelectionStates);Run("high contrast system outlines",HighContrast);Console.WriteLine(checks+" checks, "+failures+" failed groups");return failures==0?0:1;}finally{InteractionLayout.Observer=null;CompactRenderer.InteractionOutlineObserver=null;CompactRenderer.LabelObserver=null;DeviceSettings.UseTransientConfiguration(previous);VisualDesign.HighContrastOverride=null;}}
}
