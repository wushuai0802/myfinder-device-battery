using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace AtkBatteryTray {
    // Native-size rendering based on Gemini's TrayTesterV3 prototype.
    // Numeric fitting and premultiplied alpha follow the approach documented
    // by Fan4Metal/mouse_tray; no mouse or system state is read here.
    public static class TrayGlyph {
        public static Color LevelColor(int? percent,bool dark) {
            if(VisualDesign.HighContrast)return percent.HasValue?SystemColors.WindowText:SystemColors.GrayText;
            if(!percent.HasValue)return dark?Color.FromArgb(160,166,176):Color.FromArgb(100,108,119);
            if(percent.Value<20)return dark?Color.FromArgb(255,98,91):Color.FromArgb(201,42,34);
            if(percent.Value<50)return dark?Color.FromArgb(228,201,103):Color.FromArgb(213,180,63);
            return dark?Color.FromArgb(61,212,135):Color.FromArgb(34,155,90);
        }
        internal static Color NumberFill(int? percent,bool stale){return VisualDesign.HighContrast?SystemColors.Window:percent.HasValue?LevelColor(stale?(int?)null:percent,true):Color.FromArgb(83,94,108);}
        public static Bitmap Render(int? percent,bool charge,bool dark,bool stale,int size,bool approximate=false,Color? updateBadge=null,bool? taskbarDark=null) {
            size=Math.Max(16,Math.Min(64,size));int canvas=size*4;
            using(var large=new Bitmap(canvas,canvas,PixelFormat.Format32bppPArgb)) {
                using(var g=Graphics.FromImage(large)) {
                    g.Clear(Color.Transparent);g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                    g.ScaleTransform(canvas/32f,canvas/32f);
                    // The level color occupies the entire badge so it remains
                    // visible at 16px. Dark digits contrast with all three bands.
                    Color fill=NumberFill(percent,stale);
                    Color fg=VisualDesign.HighContrast?SystemColors.WindowText:percent.HasValue?Color.FromArgb(12,17,21):Color.White;
                    Color halo=Color.Transparent;
                    var badgeRect=new RectangleF(.8f,.8f,30.4f,30.4f);
                    using(var badge=new SolidBrush(fill))g.FillEllipse(badge,badgeRect);
                    using(var edge=new Pen(VisualDesign.HighContrast?SystemColors.WindowText:Color.FromArgb(65,Color.White),.8f))g.DrawEllipse(edge,badgeRect);
                    // Leave a full native pixel between the digit tops and the dot.
                    if(updateBadge.HasValue)g.TranslateTransform(0,32f/size);
                    DrawDigits(g,percent.HasValue?(approximate?"~":"")+percent.Value.ToString():"--",charge,fg,halo);
                    if(updateBadge.HasValue)g.TranslateTransform(0,-32f/size);
                    if(charge) {
                        if(updateBadge.HasValue)g.TranslateTransform(-24,0);
                        using(var marker=new SolidBrush(Color.FromArgb(12,17,21)))g.FillEllipse(marker,25f,1.2f,6.4f,9.6f);
                        PointF[] bolt={new PointF(29.1f,2.2f),new PointF(26.2f,6.2f),new PointF(28.1f,6.2f),new PointF(27.2f,9.6f),new PointF(30.2f,5.2f),new PointF(28.4f,5.2f)};
                        using(var brush=new SolidBrush(Color.White))g.FillPolygon(brush,bolt);
                    }
                }
                var result=new Bitmap(size,size,PixelFormat.Format32bppPArgb);
                using(var g=Graphics.FromImage(result)) {
                    g.CompositingMode=CompositingMode.SourceCopy;g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                    using(var attributes=new ImageAttributes()){attributes.SetWrapMode(WrapMode.TileFlipXY);g.DrawImage(large,new Rectangle(0,0,size,size),0,0,canvas,canvas,GraphicsUnit.Pixel,attributes);}
                }
                result.SetPixel(0,0,Color.Transparent);result.SetPixel(size-1,0,Color.Transparent);
                result.SetPixel(0,size-1,Color.Transparent);result.SetPixel(size-1,size-1,Color.Transparent);
                if(updateBadge.HasValue)UpdateBadge.Paint(result,updateBadge.Value,taskbarDark??dark);
                return result;
            }
        }
        public static Bitmap RenderDevice(int? percent,bool charge,bool dark,bool stale,int size,string kind,Color? updateBadge=null,bool? taskbarDark=null){
            size=Math.Max(16,Math.Min(64,size));int canvas=size*4;
            using(var large=new Bitmap(canvas,canvas,PixelFormat.Format32bppPArgb)){
                using(var g=Graphics.FromImage(large)){
                    g.Clear(Color.Transparent);g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.ScaleTransform(canvas/32f,canvas/32f);
                    Color gray=LevelColor(null,dark),foreground=VisualDesign.HighContrast?SystemColors.WindowText:dark?Color.FromArgb(244,246,248):Color.FromArgb(27,36,48);
                    Color symbol=percent.HasValue&&!stale?foreground:gray;
                    if(updateBadge.HasValue)g.TranslateTransform(-4,0);
                    if(kind=="mouse"||kind=="keyboard"||kind=="headset"||kind=="gamepad")CompactRenderer.DrawSymbol(g,kind,new RectangleF(4,1,24,19),symbol);
                    else {
                        using(var pen=new Pen(symbol,1.9f))using(var shape=VisualDesign.RoundRect(7,2,18,17,3)){
                            g.DrawPath(pen,shape);g.DrawLine(pen,12,6,20,6);g.DrawLine(pen,12,10,20,10);g.DrawEllipse(pen,14.5f,13,3,3);
                        }
                    }
                    if(updateBadge.HasValue)g.TranslateTransform(4,0);
                    if(charge){
                        if(updateBadge.HasValue)g.TranslateTransform(-24,0);
                        using(var marker=new SolidBrush(Color.FromArgb(12,17,21)))g.FillEllipse(marker,25f,1.2f,6.4f,9.6f);
                        PointF[] bolt={new PointF(29.1f,2.2f),new PointF(26.2f,6.2f),new PointF(28.1f,6.2f),new PointF(27.2f,9.6f),new PointF(30.2f,5.2f),new PointF(28.4f,5.2f)};
                        using(var brush=new SolidBrush(percent.HasValue&&!stale?Color.White:gray))g.FillPolygon(brush,bolt);
                    }
                }
                var result=new Bitmap(size,size,PixelFormat.Format32bppPArgb);
                using(var g=Graphics.FromImage(result))using(var attributes=new ImageAttributes()){
                    g.CompositingMode=CompositingMode.SourceCopy;g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                    attributes.SetWrapMode(WrapMode.TileFlipXY);g.DrawImage(large,new Rectangle(0,0,size,size),0,0,canvas,canvas,GraphicsUnit.Pixel,attributes);
                    // Paint the bar at native resolution so resampling cannot
                    // brighten its colors or blur a one-pixel positive reading.
                    g.CompositingMode=CompositingMode.SourceOver;g.SmoothingMode=SmoothingMode.AntiAlias;
                    float barHeight=(float)Math.Round(size*.26f),barWidth=size-2,barTop=size-1-barHeight;
                    Color gray=LevelColor(null,dark);
                    using(var track=VisualDesign.RoundRect(1,barTop,barWidth,barHeight,1))
                    using(var brush=new SolidBrush(Color.FromArgb(100,gray)))g.FillPath(brush,track);
                    if(percent.HasValue&&percent.Value>0){
                        float filled=Math.Max(1,barWidth*Math.Min(100,percent.Value)/100f);
                        using(var fill=VisualDesign.RoundRect(1,barTop,filled,barHeight,1))
                        using(var brush=new SolidBrush(stale?gray:LevelColor(percent,dark)))g.FillPath(brush,fill);
                    }
                }
                // Keep the native margin transparent after bicubic downsampling.
                for(int pixel=0;pixel<size;pixel++){result.SetPixel(pixel,0,Color.Transparent);result.SetPixel(pixel,size-1,Color.Transparent);result.SetPixel(0,pixel,Color.Transparent);result.SetPixel(size-1,pixel,Color.Transparent);}
                if(updateBadge.HasValue)UpdateBadge.Paint(result,updateBadge.Value,taskbarDark??dark);
                return result;
            }
        }
        static void DrawDigits(Graphics g,string text,bool charge,Color fg,Color halo) {
            if(text=="--") {
                using(var pen=new Pen(fg,2.3f)) {
                    pen.StartCap=LineCap.Round;pen.EndCap=LineCap.Round;
                    g.DrawLine(pen,9.5f,16.2f,13.5f,16.2f);g.DrawLine(pen,18.5f,16.2f,22.5f,16.2f);
                }
                return;
            }
            using(var path=new GraphicsPath())
            using(var format=new StringFormat {FormatFlags=StringFormatFlags.NoWrap}) {
                FontFamily family;try{family=new FontFamily("Bahnschrift");}catch{family=new FontFamily("Segoe UI");}
                using(family)path.AddString(text,family,(int)FontStyle.Bold,100f,PointF.Empty,format);
                var bounds=path.GetBounds();if(bounds.Width<=0||bounds.Height<=0)return;
                // Keep normal two-digit readouts large; 100 gets its own fit.
                float targetHeight=text.Length>=3?16.5f:18.2f;
                float sy=targetHeight/bounds.Height;
                float sx=Math.Min(sy,(text.Length>=3?23f:24.4f)/bounds.Width);
                float cx=charge?14.8f:16f;
                using(var matrix=new Matrix()) {
                    matrix.Translate(cx,16.2f);matrix.Scale(sx,sy);matrix.Translate(-bounds.X-bounds.Width/2f,-bounds.Y-bounds.Height/2f);path.Transform(matrix);
                }
                using(var brush=new SolidBrush(fg))g.FillPath(brush,path);
            }
        }
    }
}
