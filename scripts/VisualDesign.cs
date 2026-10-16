using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using AtkBattery;

namespace AtkBatteryTray {
    // Shared logical geometry: rendering and hit testing must use the same layout.
    public static class VisualDesign {
        public const int PanelWidth=260,CardHeightNormal=96,CardHeightExpanded=126,HeadsetCardHeight=96,FooterHeight=30;
        public static Size PanelSize {get{return new Size(260,126);}}
        public static float PanelRadius {get{return 16;}}
        public static int MouseCardHeight(DpiConfig dpi){return dpi!=null&&dpi.Stages!=null&&dpi.Stages.Count>4?126:96;}
        public static bool ShouldShowHeadset(HeadsetReading headset) {
            return headset!=null&&headset.Connected&&(headset.ConnectionVerified||(headset.ReceiverPresent&&headset.ConnectionTimestampUtc!=DateTime.MinValue));
        }
        public static Size PanelSizeFor(HeadsetReading headset){return PanelSizeFor(headset,null);}
        public static Size PanelSizeFor(HeadsetReading headset,DpiConfig dpi){return PanelSizeFor(ShouldShowHeadset(headset),dpi);}
        public static Size PanelSizeFor(bool headset,DpiConfig dpi=null){return new Size(260,MouseCardHeight(dpi)+(headset?104:0)+30);}
        public static RectangleF MouseNameRect {get{return new RectangleF(10,6,150,44);}}
        public static RectangleF MouseMeterRect {get{return new RectangleF(168,6,84,44);}}
        public static RectangleF HeadsetNameRectFor(DpiConfig dpi){return new RectangleF(10,MouseCardHeight(dpi)+14,150,44);}
        public static RectangleF HeadsetMeterRectFor(DpiConfig dpi){return new RectangleF(168,MouseCardHeight(dpi)+14,84,44);}
        public static RectangleF FooterRectFor(bool headset,DpiConfig dpi){return new RectangleF(0,MouseCardHeight(dpi)+(headset?104:0),260,30);}
        public static RectangleF RefreshRectFor(bool headset,DpiConfig dpi){var footer=FooterRectFor(headset,dpi);return new RectangleF(8,footer.Y+1,208,28);}
        public static RectangleF MoreRectFor(bool headset,DpiConfig dpi){var footer=FooterRectFor(headset,dpi);return new RectangleF(226,footer.Y+1,28,28);}
        public static RectangleF RefreshRect {get{return RefreshRectFor(false,null);}}
        public static RectangleF SettingsRect {get{return MoreRectFor(false,null);}}
        public static RectangleF SpeakerOutputRect {get{return SpeakerOutputRectFor(null);}}
        public static RectangleF HeadsetOutputRect {get{return HeadsetOutputRectFor(null);}}
        public static RectangleF MicrophoneMuteRect {get{return MicrophoneMuteRectFor(null);}}
        public static RectangleF SpeakerOutputRectFor(DpiConfig dpi){return new RectangleF(11,MouseCardHeight(dpi)+63,86,30);}
        public static RectangleF HeadsetOutputRectFor(DpiConfig dpi){return new RectangleF(99,MouseCardHeight(dpi)+63,86,30);}
        public static RectangleF MicrophoneMuteRectFor(DpiConfig dpi){return new RectangleF(191,MouseCardHeight(dpi)+63,58,30);}
        public static RectangleF StageRect(int index,int count) {
            count=Math.Max(1,Math.Min(8,count));int columns=Math.Min(4,count),column=index%columns,row=index/columns;
            float width=(236-(columns-1)*2f)/columns;
            return new RectangleF(12+column*(width+2),55+row*30,width,count>4?28:30);
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
