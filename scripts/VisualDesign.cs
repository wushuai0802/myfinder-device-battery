using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using AtkBattery;

namespace AtkBatteryTray {
    internal static class PanelLayouts {
        internal static readonly string[] Keys={"ring","bar","grid"};
        internal static readonly string[] Names={"环形","电量条","网格"};
        internal static string Normalize(string layout){return Array.IndexOf(Keys,layout)<0?"ring":layout;}
        internal static string Name(string layout){return Names[Array.IndexOf(Keys,Normalize(layout))];}
        internal static string Effective(string layout){return Normalize(layout);}
    }
    // Shared logical geometry: rendering and hit testing must use the same layout.
    public static class VisualDesign {
        internal static bool RingLayout {get{return PanelLayouts.Effective(DeviceSettings.Current.PanelLayout)=="ring";}}
        internal static bool GridLayout {get{return PanelLayouts.Effective(DeviceSettings.Current.PanelLayout)=="grid";}}
        internal static bool LargeFont {get{return DeviceSettings.Current.FontSize=="large";}}
        internal static float DialogScale {get{return LargeFont?1.15f:1f;}}
        public static int PanelWidth {get{return LargeFont?300:260;}}
        public static int CardHeightNormal {get{return RingLayout?RingGeometry.CardHeight(4):BarGeometry.CardHeight(4);}}
        public static int CardHeightExpanded {get{return RingLayout?RingGeometry.CardHeight(8):BarGeometry.CardHeight(8);}}
        public static int HeadsetCardHeight {get{return CardHeightNormal;}}
        public static int FooterHeight {get{return LargeFont?36:30;}}
        internal static int HeaderHeight {get{return RingLayout?RingGeometry.HeaderHeight:BarGeometry.HeaderHeight;}}
        internal static int DeviceGap {get{return RingLayout?1:CardGap;}}
        internal static int MainFooterGap {get{return RingLayout?8:0;}}
        internal static int SecondCardStep {get{return HeadsetCardHeight+DeviceGap;}}
        internal static bool? HighContrastOverride=null;
        public static bool HighContrast {get{return HighContrastOverride??System.Windows.Forms.SystemInformation.HighContrast;}}
        public const int CardGap=8;
        public static int GenericCardHeight {get{return HeaderHeight+(RingLayout?0:16);}}
        internal static int SelectionHeight {get{return LargeFont?34:30;}}
        internal static int MainControlTop {get{return RingLayout?HeaderHeight:BarGeometry.ControlTop;}}
        internal static int MicrophoneWidth {get{return LargeFont?38:34;}}
        internal static float BatteryPercentSize {get{return LargeFont?11:10;}}
        internal const float MainCardRadius=10,IconTileRadius=9,BatteryBarRadius=2,MicrophoneRadius=9;
        internal const int VolumeTrackHeight=6;
        internal const float SelectionRadius=7,VolumeTrackRadius=3;
        internal static float DeviceNameFontSize {get{return LargeFont?14f:12f;}}
        internal static float SubtitleFontSize {get{return LargeFont?13f:11f;}}
        internal static float BodyFontSize {get{return LargeFont?14f:12f;}}
        internal static float HeadingFontSize {get{return LargeFont?15f:13f;}}
        internal static float DialogTitleFontSize {get{return LargeFont?18f:15f;}}
        internal static float SmallFontSize {get{return LargeFont?13f:11f;}}
        internal const float ToggleWidth=30,ToggleHeight=18,ToggleKnobSize=14,ToggleInset=2;
        public static int SystemVolumeHeight {get{return LargeFont?40:36;}}
        public static int SystemVolumeHeightFor(bool collapsed){return collapsed?(LargeFont?36:32):SystemVolumeHeight;}
        public static int CurrentSystemVolumeHeight {get{return SystemVolumeHeightFor(DeviceSettings.Current.VolumeCollapsed);}}
        internal static float SnapFontSize(float size){return (float)Math.Max(1,Math.Round(size,MidpointRounding.AwayFromZero));}
        internal static RectangleF SnapTextRect(RectangleF rect){
            float x=(float)Math.Round(rect.X,MidpointRounding.AwayFromZero),y=(float)Math.Round(rect.Y,MidpointRounding.AwayFromZero);
            float right=(float)Math.Round(rect.Right,MidpointRounding.AwayFromZero),bottom=(float)Math.Round(rect.Bottom,MidpointRounding.AwayFromZero);
            return new RectangleF(x,y,Math.Max(1,right-x),Math.Max(1,bottom-y));
        }
        internal static RectangleF VolumeSliderRect(RectangleF card){return new RectangleF(card.X+8,card.Y+8,card.Width-44,card.Height-16);}
        internal static RectangleF VolumeToggleRect(RectangleF card,bool collapsed){return new RectangleF(card.Right-32,card.Y+(float)Math.Round((card.Height-24)/2),28,24);}
        internal static RectangleF VolumeChevronRect(RectangleF card,bool collapsed){return CenteredRect(VolumeToggleRect(card,collapsed),14,14);}
        internal static RectangleF MainVolumeTrack(RectangleF slider){return new RectangleF(slider.X+31,slider.Y+slider.Height/2-2,slider.Width-73,4);}
        internal static RectangleF VolumeSpeakerRect(RectangleF slider){return new RectangleF(slider.X+4,slider.Y+(slider.Height-14)/2,14,14);}
        internal static RectangleF VolumeValueRect(RectangleF slider){return new RectangleF(slider.Right-37,slider.Y,34,slider.Height);}
        internal static RectangleF VolumeKnobRect(RectangleF track,int percent,bool active=false){float size=active?14:12;return new RectangleF(track.X+track.Width*Math.Max(0,Math.Min(100,percent))/100f-size/2,track.Y+track.Height/2-size/2,size,size);}
        internal static RectangleF CenteredRect(RectangleF rect,float width,float height){return new RectangleF((float)Math.Floor(rect.X+(rect.Width-width)/2),(float)Math.Floor(rect.Y+(rect.Height-height)/2),width,height);}
        // The last two pixels of each slot contain the layered downward shadow.
        // Nothing extends outside the panel or into an animation's clipped slot.
        internal static RectangleF PanelCardRect(float top,float height){return PanelCardSurface(new RectangleF(0,top,PanelWidth,height));}
        internal static RectangleF PanelCardSurface(RectangleF slot){return new RectangleF(slot.X+.5f,slot.Y+.5f,slot.Width-1,slot.Height-3);}
        internal static RectangleF PanelShadowRect(RectangleF card,int layer){var rect=card;rect.Y+=layer;return rect;}
        // Layout entry points stay stable for rendering, hits and WirelessLayout.
        // Layout composition is dispatched here; control internals stay shared.
        internal static RectangleF HeaderRowRect(float top){return new RectangleF(0,top,PanelWidth,HeaderHeight);}
        internal static RectangleF HeaderNameHitRect(float top){return RingLayout?HeaderRowRect(top):BarGeometry.NameHit(top);}
        internal static RectangleF HeaderMeterHitRect(float top){return RingLayout?HeaderRowRect(top):BarGeometry.MeterHit(top);}
        internal static RectangleF AudioHeaderHitRect(float top){return RingLayout?HeaderRowRect(top):BarGeometry.AudioHit(top);}
        internal static RectangleF IconTileRect(float top){return BarGeometry.IconTile(top);}
        internal static RectangleF HeaderIconRect(float top){return RingLayout?CenteredRect(BatteryRingRect(top),LargeFont?18:16,LargeFont?18:16):CenteredRect(IconTileRect(top),16,19);}
        internal static RectangleF HeaderTextRect(float top,bool subtitle,bool audioOnly=false,bool reveal=false){return RingLayout?RingGeometry.HeaderText(top,subtitle,audioOnly,reveal):BarGeometry.HeaderText(top,subtitle,audioOnly,reveal);}
        internal static RectangleF AudioHeaderChevronRect(float top){return new RectangleF(PanelWidth-21,top+(LargeFont?16:13),7,10);}
        internal static RectangleF BatteryBarRect(float top){return BarGeometry.BatteryBar(top);}
        internal static RectangleF BatteryFillRect(float top,BatteryView view){var rect=BatteryBarRect(top);rect.Width*=Math.Max(0,Math.Min(100,view==null?0:view.Percent.GetValueOrDefault()))/100f;return rect;}
        internal static float BatteryOpeningProgress(float elapsedMs,bool reduced){float t=Math.Max(0,Math.Min(1,elapsedMs/240f));return reduced?1:1-(float)Math.Pow(1-t,3);}
        internal static float BatteryFillProgress(float progress){return PopupPages.Reduced?1:Math.Max(0,Math.Min(1,progress));}
        public static float BatteryRingSize {get{return RingLayout?(LargeFont?44:38):28;}}
        public static float BatteryRingWidth {get{return RingLayout?(LargeFont?3.8f:3.4f):3.2f;}}
        public static float BatteryDigitSize {get{return LargeFont?22f:19f;}}
        internal static float BatteryApproximateDigitSize {get{return BatteryDigitSize;}}
        internal static RectangleF BatteryRingRect(float top){return RingLayout?new RectangleF(LargeFont?12:11,top+(HeaderHeight-BatteryRingSize)/2,BatteryRingSize,BatteryRingSize):new RectangleF(LargeFont?198:168,top+(LargeFont?17:14),BatteryRingSize,BatteryRingSize);}
        internal static RectangleF BatteryDigitsRect(float top){var rect=BarGeometry.BatteryDigits(top);if(RingLayout)rect.Y+=(HeaderHeight-BarGeometry.HeaderHeight)/2;return rect;}
        internal static RectangleF BatteryPercentRect(float top){var rect=BarGeometry.BatteryPercent(top);if(RingLayout)rect.Y+=(HeaderHeight-BarGeometry.HeaderHeight)/2;return rect;}
        internal static RectangleF RingChargeBadgeRect(float top){var ring=BatteryRingRect(top);float distance=(ring.Width+12)/2;return new RectangleF(ring.X+ring.Width/2+distance/(float)Math.Sqrt(2)-6,ring.Y+ring.Height/2+distance/(float)Math.Sqrt(2)-6,12,12);}
        internal static RectangleF StatusDotRect(float top){var text=HeaderTextRect(top,true);float size=LargeFont?7:6;return new RectangleF(text.Left,text.Y+(text.Height-size)/2,size,size);}
        internal static RectangleF RingChevronRect(float top,RectangleF number){return new RectangleF(number.Left-13,top+(HeaderHeight-10)/2,7,10);}
        internal static RectangleF AlignBatteryDigits(RectangleF rect,RectangleF digitInk,RectangleF percentInk){rect.X+=percentInk.Left-1-digitInk.Right;return rect;}
        internal static RectangleF BatteryPlaceholderRect(float top){var rect=BatteryDigitsRect(top);rect.Width=PanelWidth-12-rect.X;return rect;}
        internal static RectangleF BatteryChargingRect(float top,RectangleF digitInk){return new RectangleF(digitInk.Left-14,top+(LargeFont?22:17),10,13);}
        internal static RectangleF BatteryPendingRect(float top){var meter=HeaderMeterHitRect(top);return new RectangleF(meter.X,top+(LargeFont?42:36),meter.Width,14);}
        public static Size PanelSize {get{return PanelSizeFor(false);}}
        public static float PanelRadius {get{return 12;}}
        public static int MouseCardHeight(DpiConfig dpi){return dpi!=null&&dpi.Stages!=null&&dpi.Stages.Count>4?CardHeightExpanded:CardHeightNormal;}
        public static bool ShouldShowHeadset(HeadsetReading headset) {
            return headset!=null&&headset.Connected&&(headset.ConnectionVerified||(headset.ReceiverPresent&&headset.ConnectionTimestampUtc!=DateTime.MinValue));
        }
        internal static AudioCardView AudioCardFor(HeadsetReading headset,AudioReading audio) {
            return ShouldShowHeadset(headset)||DeviceSettings.Current.HiddenDeviceIds.Contains("native:audio")?null:UiState.AudioCard(audio,DeviceSettings.Current,headset);
        }
        internal static bool HasAudioCard(HeadsetReading headset,AudioReading audio){return ShouldShowHeadset(headset)||AudioCardFor(headset,audio)!=null;}
        public static Size PanelSizeFor(HeadsetReading headset){return PanelSizeFor(headset,null);}
        public static Size PanelSizeFor(HeadsetReading headset,DpiConfig dpi){return PanelSizeFor(ShouldShowHeadset(headset),dpi);}
        public static Size PanelSizeFor(bool headset,DpiConfig dpi=null){return GridLayout?AtkBatteryTray.GridLayout.Native(headset,dpi).Size:new Size(PanelWidth,(int)FooterRectFor(headset,dpi).Bottom);}
        public static RectangleF MouseNameRect {get{return HeaderNameHitRect(0);}}
        public static RectangleF MouseMeterRect {get{return HeaderMeterHitRect(0);}}
        public static RectangleF HeadsetNameRectFor(DpiConfig dpi){return HeaderNameHitRect(MouseCardHeight(dpi)+DeviceGap);}
        public static RectangleF HeadsetMeterRectFor(DpiConfig dpi){return HeaderMeterHitRect(MouseCardHeight(dpi)+DeviceGap);}
        public static RectangleF SystemVolumeRectFor(bool headset,DpiConfig dpi){return GridLayout?AtkBatteryTray.GridLayout.Native(headset,dpi).VolumeRect:new RectangleF(0,MouseCardHeight(dpi)+(headset?SecondCardStep:0)+DeviceGap,PanelWidth,CurrentSystemVolumeHeight);}
        public static RectangleF FooterRectFor(bool headset,DpiConfig dpi){return new RectangleF(0,GridLayout?AtkBatteryTray.GridLayout.Native(headset,dpi).Footer:SystemVolumeRectFor(headset,dpi).Bottom+MainFooterGap,PanelWidth,FooterHeight);}
        internal static RectangleF FooterRefreshRect(float top){return new RectangleF(8,top+(FooterHeight-28)/2,28,28);}
        internal static RectangleF FooterMoreRect(float top){return new RectangleF(PanelWidth-34,top+(FooterHeight-28)/2,28,28);}
        internal static RectangleF FooterTextRect(float top,float maximumWidth){return new RectangleF(39,top+3,Math.Max(1,maximumWidth-33),FooterHeight-6);}
        internal static RectangleF FooterPageRect(float top,bool next){return new RectangleF(PanelWidth-(next?68:102),top+(FooterHeight-28)/2,28,28);}
        public static RectangleF RefreshRectFor(bool headset,DpiConfig dpi){return FooterRefreshRect(FooterRectFor(headset,dpi).Y);}
        public static RectangleF MoreRectFor(bool headset,DpiConfig dpi){return FooterMoreRect(FooterRectFor(headset,dpi).Y);}
        public static RectangleF RefreshRect {get{return RefreshRectFor(false,null);}}
        public static RectangleF SettingsRect {get{return MoreRectFor(false,null);}}
        public static RectangleF SpeakerOutputRect {get{return SpeakerOutputRectFor(null);}}
        public static RectangleF HeadsetOutputRect {get{return HeadsetOutputRectFor(null);}}
        public static RectangleF MicrophoneMuteRect {get{return MicrophoneMuteRectFor(null);}}
        internal static RectangleF AudioSelectionTrack(float top){return RingLayout?RingGeometry.AudioTrack(top):BarGeometry.AudioTrack(top);}
        internal static RectangleF AudioOutputRect(float top,int index){return SelectionSegmentRect(AudioSelectionTrack(top),index,2);}
        internal static RectangleF MicrophoneRect(float top){return RingLayout?RingGeometry.Microphone(top):BarGeometry.Microphone(top);}
        public static RectangleF SpeakerOutputRectFor(DpiConfig dpi){return AudioOutputRect(MouseCardHeight(dpi)+DeviceGap,0);}
        public static RectangleF HeadsetOutputRectFor(DpiConfig dpi){return AudioOutputRect(MouseCardHeight(dpi)+DeviceGap,1);}
        public static RectangleF MicrophoneMuteRectFor(DpiConfig dpi){return MicrophoneRect(MouseCardHeight(dpi)+DeviceGap);}
        internal static RectangleF StageTrackRect(float top,int count){return RingLayout?RingGeometry.DpiTrack(top,count):BarGeometry.DpiTrack(top,count);}
        internal static int SelectionTrackHeight(int count){return SelectionHeight+(count>4?SelectionHeight-2:0);}
        internal static RectangleF SelectionSegmentRect(RectangleF track,int index,int count){
            int columns=Math.Max(1,Math.Min(4,count));float width=(track.Width-4-(columns-1)*2f)/columns;
            return new RectangleF(track.X+2+index%columns*(width+2),track.Y+2+index/columns*(SelectionHeight-2),width,SelectionHeight-4);
        }
        internal static RectangleF SelectionTextRect(RectangleF rect){rect.Height-=4;return rect;}
        internal static RectangleF SelectionPipRect(RectangleF rect){return new RectangleF((float)Math.Floor(rect.X+(rect.Width-12)/2),rect.Bottom-3,12,2);}
        internal static RectangleF SelectionPendingDotRect(RectangleF rect,int index){var pip=SelectionPipRect(rect);return new RectangleF(pip.X+index*5,pip.Y,2,2);}
        internal static RectangleF SelectionFocusRect(RectangleF rect){rect.Inflate(3,3);return rect;}
        internal static RectangleF SelectionIconRect(RectangleF rect,string label){float width=(label??"").Length*SmallFontSize+18;return new RectangleF((float)Math.Floor(rect.X+(rect.Width-width)/2),(float)Math.Floor(rect.Y+(rect.Height-15)/2),13,13);}
        internal static RectangleF SelectionIconLabelRect(RectangleF rect,string label){var icon=SelectionIconRect(rect,label);return new RectangleF(icon.X+17,rect.Y,rect.Right-icon.X-18,rect.Height-4);}
        internal static RectangleF SelectionDetailRect(RectangleF rect,bool second){var text=SelectionTextRect(rect);return new RectangleF(text.X,text.Y+(second?text.Height/2:0),text.Width,text.Height/2);}
        public static RectangleF StageRect(int index,int count) {
            count=Math.Max(1,Math.Min(8,count));return SelectionSegmentRect(StageTrackRect(0,count),index,count);
        }
        internal static class RingGeometry {
            internal static int HeaderHeight {get{return LargeFont?62:54;}}
            internal static int Inset {get{return LargeFont?12:10;}}
            internal static int CardHeight(int count){return HeaderHeight+SelectionTrackHeight(count)+Inset;}
            internal static RectangleF HeaderText(float top,bool subtitle,bool audioOnly,bool reveal){float left=LargeFont?66:58,right=audioOnly?PanelWidth-12:LargeFont?200:164;if(reveal)right-=14;return new RectangleF(left,top+(subtitle?(LargeFont?30:26):(LargeFont?9:7)),Math.Max(1,right-left),LargeFont?24:20);}
            internal static RectangleF DpiTrack(float top,int count){return new RectangleF(Inset,top+HeaderHeight,PanelWidth-2*Inset,SelectionTrackHeight(count));}
            internal static RectangleF AudioTrack(float top){return new RectangleF(Inset,top+HeaderHeight,PanelWidth-MicrophoneWidth-2*Inset-6,SelectionHeight);}
            internal static RectangleF Microphone(float top){return new RectangleF(PanelWidth-MicrophoneWidth-Inset,top+HeaderHeight,MicrophoneWidth,SelectionHeight);}
        }
        internal static class BarGeometry {
            internal static int HeaderHeight {get{return LargeFont?56:50;}}
            internal static int ControlTop {get{return HeaderHeight+14;}}
            internal static int CardHeight(int count){return ControlTop+SelectionTrackHeight(count)+12;}
            internal static RectangleF NameHit(float top){return new RectangleF(10,top+6,(LargeFont?200:168)-14,HeaderHeight-6);}
            internal static RectangleF MeterHit(float top){float left=LargeFont?200:168;return new RectangleF(left,top+6,PanelWidth-left-12,HeaderHeight-6);}
            internal static RectangleF AudioHit(float top){return new RectangleF(10,top+6,PanelWidth-20,HeaderHeight-6);}
            internal static RectangleF IconTile(float top){return new RectangleF(12,top+(LargeFont?13:10),30,30);}
            internal static RectangleF HeaderText(float top,bool subtitle,bool audioOnly,bool reveal){float right=audioOnly?PanelWidth-12:NameHit(top).Right;if(audioOnly&&!subtitle&&reveal)right-=12;return new RectangleF(52,top+(subtitle?(LargeFont?30:27):(LargeFont?9:7)),right-52,LargeFont?22:20);}
            internal static RectangleF BatteryBar(float top){return new RectangleF(12,top+HeaderHeight,PanelWidth-24,4);}
            internal static RectangleF BatteryDigits(float top){float width=LargeFont?62:54;return new RectangleF(PanelWidth-22-width,top+(LargeFont?10:8),width,LargeFont?36:32);}
            internal static RectangleF BatteryPercent(float top){return new RectangleF(PanelWidth-26,top+(LargeFont?22:17),14,20);}
            internal static RectangleF AudioTrack(float top){return new RectangleF(10,top+ControlTop,PanelWidth-MicrophoneWidth-26,SelectionHeight);}
            internal static RectangleF Microphone(float top){return new RectangleF(PanelWidth-MicrophoneWidth-10,top+ControlTop,MicrophoneWidth,SelectionHeight);}
            internal static RectangleF DpiTrack(float top,int count){return new RectangleF(10,top+ControlTop,PanelWidth-20,SelectionTrackHeight(count));}
        }
        public static Bitmap Tray(int? percent,bool charge,bool dark,bool stale){return TrayGlyph.Render(percent,charge,dark,stale,32);}
        public static Bitmap Tray(int? percent,bool charge,bool dark,bool stale,int size){return TrayGlyph.Render(percent,charge,dark,stale,size);}
        public static void DrawSurface(Graphics g,RectangleF rect,bool dark){CompactRenderer.DrawSurface(g,rect,dark);}
        public static void DrawLabel(Graphics g,string text,RectangleF rect,float size,bool bold,Color color,StringAlignment align){CompactRenderer.DrawLabel(g,text,rect,size,bold,color,align);}
        public static void DrawSymbol(Graphics g,string name,RectangleF rect,Color color){CompactRenderer.DrawSymbol(g,name,rect,color);}
        public static void PaintPanel(Graphics g,BatteryReading current,BatteryReading lastGood,string error,bool busy,DpiConfig dpi,string dpiError,bool dpiBusy,bool dark,float openProgress=1,int hoverStage=-1,bool hoverRefresh=false) {
            PaintDevicesPanel(g,current,lastGood,error,busy,dpi,dpiError,dpiBusy,dark,null,null,openProgress,hoverStage,hoverRefresh,0);
        }
        public static void PaintDevicesPanel(Graphics g,BatteryReading current,BatteryReading lastGood,string error,bool busy,DpiConfig dpi,string dpiError,bool dpiBusy,bool dark,HeadsetReading headset,AudioReading audio,float openProgress=1,int hoverStage=-1,bool hoverRefresh=false,float headsetProgress=1,int hoverAudio=-1,PanelFeedback feedback=null,bool hoverSettings=false,float batteryProgress=1) {
            CompactRenderer.Paint(g,current,lastGood,error,busy,dpi,dpiError,dpiBusy,dark,headset,audio,openProgress,hoverStage,hoverRefresh,headsetProgress,hoverAudio,feedback,hoverSettings,batteryProgress);
        }
        public static GraphicsPath RoundRect(float x,float y,float width,float height,float radius) {
            var path=new GraphicsPath();float d=Math.Max(.01f,Math.Min(radius*2,Math.Min(width,height)));
            path.AddArc(x,y,d,d,180,90);path.AddArc(x+width-d,y,d,d,270,90);
            path.AddArc(x+width-d,y+height-d,d,d,0,90);path.AddArc(x,y+height-d,d,d,90,90);path.CloseFigure();return path;
        }
    }
}
