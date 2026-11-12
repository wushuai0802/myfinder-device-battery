using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

// Offline fixtures only. The pipeline supplies an output directory outside the worktree.
static class PanelDesignTests {
    const BindingFlags Static=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks,failures;
    static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
    static void Run(string name,Action test){try{test();Console.WriteLine("PASS "+name);}catch(Exception error){failures++;Console.Error.WriteLine("FAIL "+name+": "+(error.InnerException??error).Message);}}
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
                    try{typeof(CompactRenderer).GetMethod("Meter",Static).Invoke(null,new object[]{g,view,0f,(float)VisualDesign.GenericCardHeight,false,new PanelFeedback(),4});}finally{CompactRenderer.LabelObserver=null;CompactRenderer.SymbolObserver=null;}
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
    static void RenderPreviews(string directory,string panelLayout="bar"){
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
    }
    [STAThread] static int Main(string[] args){var previous=DeviceSettings.Current;try{
        if(args.Length>=2&&args[0]=="--render"){VisualDesign.HighContrastOverride=false;foreach(string layout in args.Length>2?new[]{args[2]}:new[]{"ring","bar"})RenderPreviews(args[1],layout);return 0;}
        Run("layout defaults, migration, persistence and normalization",LayoutSettings);Run("ring geometry, full-row details and calculated heights",RingGeometry);
        Run("ring draw and hit geometry",delegate{HitGeometry("ring");});Run("ring color, font, headset, contrast and render path matrix",delegate{RenderMatrix("ring");});Run("ring tiny, full, cached, sleeping and charging boundaries",RingBoundaries);Run("ring state accessibility and typography at five DPI scales",RingNamesAndTypography);
        Run("ring paging reserves volume and the external footer",delegate{FooterPaging("ring");});
        if(args.Length==1&&args[0]=="--ring")return failures==0?0:1;
        VisualDesign.HighContrastOverride=false;Mode("normal");Run("battery bar proportions and bounds",BarGeometry);Run("battery digits and percent baselines at five DPI scales",BatteryTypography);Run("shared draw and hit geometry",HitGeometry);Run("four themes x light/dark x fonts x headset x high contrast x render paths",RenderMatrix);Run("card shadows and high contrast",ContrastAndShadows);Run("icon-only microphone accessible state",MicrophoneNames);Run("microphone pending and unavailable ink",MicrophonePresentation);Run("floating segmented controls",FloatingSegments);Run("pending hints, press offset and segment focus",PendingSegments);Run("plain footer and single-row volume",PlainFooterAndVolume);Run("bounded device page footer geometry",FooterPaging);
        Console.WriteLine(checks+" checks, "+failures+" failed groups; fake devices and audio only");return failures==0?0:1;
    }finally{CompactRenderer.LabelObserver=null;CompactRenderer.SymbolObserver=null;InteractionLayout.Observer=null;VisualDesign.HighContrastOverride=null;DeviceSettings.UseTransientConfiguration(previous);}}
}
