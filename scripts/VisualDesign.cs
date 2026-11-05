using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using AtkBattery;

namespace AtkBatteryTray {
    // Shared logical geometry: rendering and hit testing must use the same layout.
    public static class VisualDesign {
        internal static bool LargeFont {get{return DeviceSettings.Current.FontSize=="large";}}
        internal static float DialogScale {get{return LargeFont?1.15f:1f;}}
        public static int PanelWidth {get{return LargeFont?300:260;}}
        public static int CardHeightNormal {get{return LargeFont?112:96;}}
        public static int CardHeightExpanded {get{return LargeFont?144:126;}}
        public static int HeadsetCardHeight {get{return CardHeightNormal;}}
        public static int FooterHeight {get{return LargeFont?36:30;}}
        internal static int HeaderHeight {get{return LargeFont?62:56;}}
        internal static int SecondCardStep {get{return HeadsetCardHeight+CardGap;}}
        internal static bool? HighContrastOverride=null;
        public static bool HighContrast {get{return HighContrastOverride??System.Windows.Forms.SystemInformation.HighContrast;}}
        public const int CardGap=8;
        public static int GenericCardHeight {get{return LargeFont?68:60;}}
        internal static int SelectionHeight {get{return LargeFont?34:30;}}
        internal const int VolumeTrackHeight=6;
        internal const float SelectionRadius=7,VolumeTrackRadius=3;
        internal static float DeviceNameFontSize {get{return LargeFont?14f:12f;}}
        internal static float SubtitleFontSize {get{return LargeFont?13f:11f;}}
        internal static float BodyFontSize {get{return LargeFont?14f:12f;}}
        internal static float HeadingFontSize {get{return LargeFont?15f:13f;}}
        internal static float DialogTitleFontSize {get{return LargeFont?18f:15f;}}
        internal static float SmallFontSize {get{return LargeFont?13f:11f;}}
        internal const float ToggleWidth=30,ToggleHeight=18,ToggleKnobSize=14,ToggleInset=2;
        public static int SystemVolumeHeight {get{return LargeFont?56:48;}}
        public static int SystemVolumeHeightFor(bool collapsed){return collapsed?(LargeFont?36:32):SystemVolumeHeight;}
        public static int CurrentSystemVolumeHeight {get{return SystemVolumeHeightFor(DeviceSettings.Current.VolumeCollapsed);}}
        internal static float SnapFontSize(float size){return (float)Math.Max(1,Math.Round(size,MidpointRounding.AwayFromZero));}
        internal static RectangleF SnapTextRect(RectangleF rect){
            float x=(float)Math.Round(rect.X,MidpointRounding.AwayFromZero),y=(float)Math.Round(rect.Y,MidpointRounding.AwayFromZero);
            float right=(float)Math.Round(rect.Right,MidpointRounding.AwayFromZero),bottom=(float)Math.Round(rect.Bottom,MidpointRounding.AwayFromZero);
            return new RectangleF(x,y,Math.Max(1,right-x),Math.Max(1,bottom-y));
        }
        internal static RectangleF VolumeSliderRect(RectangleF card){return new RectangleF(card.X,card.Y,card.Width-36,card.Height);}
        internal static RectangleF VolumeToggleRect(RectangleF card,bool collapsed){return new RectangleF(card.Right-32,collapsed?card.Y+(float)Math.Round((card.Height-24)/2):card.Y+2,28,24);}
        internal static RectangleF VolumeChevronRect(RectangleF card,bool collapsed){return new RectangleF(card.Right-25,collapsed?card.Y+(float)Math.Round((card.Height-14)/2):card.Y+6,14,14);}
        public const float BatteryRingSize=28,BatteryRingWidth=3.2f;
        public static float BatteryDigitSize {get{return LargeFont?23f:20f;}}
        internal static float BatteryApproximateDigitSize {get{return LargeFont?20f:18f;}}
        internal static RectangleF BatteryRingRect(float top){return new RectangleF(LargeFont?198:168,top+(LargeFont?17:14),BatteryRingSize,BatteryRingSize);}
        internal static RectangleF BatteryDigitsRect(float top){return new RectangleF(LargeFont?229:198,top+(LargeFont?14:12),LargeFont?49:43,LargeFont?34:32);}
        internal static RectangleF BatteryPercentRect(float top){return new RectangleF(LargeFont?278:241,top+(LargeFont?20:18),LargeFont?13:10,LargeFont?22:20);}
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
        public static Size PanelSizeFor(bool headset,DpiConfig dpi=null){return new Size(PanelWidth,(int)FooterRectFor(headset,dpi).Bottom);}
        public static RectangleF MouseNameRect {get{return new RectangleF(10,6,LargeFont?180:150,LargeFont?50:44);}}
        public static RectangleF MouseMeterRect {get{return new RectangleF(LargeFont?198:168,6,LargeFont?94:84,LargeFont?50:44);}}
        public static RectangleF HeadsetNameRectFor(DpiConfig dpi){return new RectangleF(10,MouseCardHeight(dpi)+14,LargeFont?180:150,LargeFont?50:44);}
        public static RectangleF HeadsetMeterRectFor(DpiConfig dpi){return new RectangleF(LargeFont?198:168,MouseCardHeight(dpi)+14,LargeFont?94:84,LargeFont?50:44);}
        public static RectangleF SystemVolumeRectFor(bool headset,DpiConfig dpi){return new RectangleF(0,MouseCardHeight(dpi)+(headset?SecondCardStep:0)+CardGap,PanelWidth,CurrentSystemVolumeHeight);}
        public static RectangleF FooterRectFor(bool headset,DpiConfig dpi){return new RectangleF(0,SystemVolumeRectFor(headset,dpi).Bottom,PanelWidth,FooterHeight);}
        public static RectangleF RefreshRectFor(bool headset,DpiConfig dpi){var footer=FooterRectFor(headset,dpi);return new RectangleF(8,footer.Y+1,PanelWidth-52,FooterHeight-2);}
        public static RectangleF MoreRectFor(bool headset,DpiConfig dpi){var footer=FooterRectFor(headset,dpi);return new RectangleF(PanelWidth-34,footer.Y+1,28,FooterHeight-2);}
        public static RectangleF RefreshRect {get{return RefreshRectFor(false,null);}}
        public static RectangleF SettingsRect {get{return MoreRectFor(false,null);}}
        public static RectangleF SpeakerOutputRect {get{return SpeakerOutputRectFor(null);}}
        public static RectangleF HeadsetOutputRect {get{return HeadsetOutputRectFor(null);}}
        public static RectangleF MicrophoneMuteRect {get{return MicrophoneMuteRectFor(null);}}
        public static RectangleF SpeakerOutputRectFor(DpiConfig dpi){return new RectangleF(11,MouseCardHeight(dpi)+(LargeFont?71:63),LargeFont?100:86,SelectionHeight);}
        public static RectangleF HeadsetOutputRectFor(DpiConfig dpi){return new RectangleF(LargeFont?113:99,MouseCardHeight(dpi)+(LargeFont?71:63),LargeFont?100:86,SelectionHeight);}
        public static RectangleF MicrophoneMuteRectFor(DpiConfig dpi){return new RectangleF(LargeFont?219:191,MouseCardHeight(dpi)+(LargeFont?71:63),LargeFont?70:58,SelectionHeight);}
        public static RectangleF StageRect(int index,int count) {
            count=Math.Max(1,Math.Min(8,count));int columns=Math.Min(4,count),column=index%columns,row=index/columns;
            float inner=PanelWidth-24,width=(inner-(columns-1)*2f)/columns;
            return new RectangleF(12+column*(width+2),(LargeFont?63:55)+row*SelectionHeight,width,count>4?SelectionHeight-2:SelectionHeight);
        }
        public static Bitmap Tray(int? percent,bool charge,bool dark,bool stale){return TrayGlyph.Render(percent,charge,dark,stale,32);}
        public static Bitmap Tray(int? percent,bool charge,bool dark,bool stale,int size){return TrayGlyph.Render(percent,charge,dark,stale,size);}
        public static void DrawSurface(Graphics g,RectangleF rect,bool dark){CompactRenderer.DrawSurface(g,rect,dark);}
        public static void DrawLabel(Graphics g,string text,RectangleF rect,float size,bool bold,Color color,StringAlignment align){CompactRenderer.DrawLabel(g,text,rect,size,bold,color,align);}
        public static void DrawSymbol(Graphics g,string name,RectangleF rect,Color color){CompactRenderer.DrawSymbol(g,name,rect,color);}
        public static void PaintPanel(Graphics g,BatteryReading current,BatteryReading lastGood,string error,bool busy,DpiConfig dpi,string dpiError,bool dpiBusy,bool dark,float openProgress=1,int hoverStage=-1,bool hoverRefresh=false) {
            PaintDevicesPanel(g,current,lastGood,error,busy,dpi,dpiError,dpiBusy,dark,null,null,openProgress,hoverStage,hoverRefresh,0);
        }
        public static void PaintDevicesPanel(Graphics g,BatteryReading current,BatteryReading lastGood,string error,bool busy,DpiConfig dpi,string dpiError,bool dpiBusy,bool dark,HeadsetReading headset,AudioReading audio,float openProgress=1,int hoverStage=-1,bool hoverRefresh=false,float headsetProgress=1,int hoverAudio=-1,PanelFeedback feedback=null,bool hoverSettings=false) {
            CompactRenderer.Paint(g,current,lastGood,error,busy,dpi,dpiError,dpiBusy,dark,headset,audio,openProgress,hoverStage,hoverRefresh,headsetProgress,hoverAudio,feedback,hoverSettings);
        }
        public static GraphicsPath RoundRect(float x,float y,float width,float height,float radius) {
            var path=new GraphicsPath();float d=Math.Max(.01f,Math.Min(radius*2,Math.Min(width,height)));
            path.AddArc(x,y,d,d,180,90);path.AddArc(x+width-d,y,d,d,270,90);
            path.AddArc(x+width-d,y+height-d,d,d,0,90);path.AddArc(x,y+height-d,d,d,90,90);path.CloseFigure();return path;
        }
    }
}
