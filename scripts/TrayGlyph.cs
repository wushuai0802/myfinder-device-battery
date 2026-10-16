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
            if(!percent.HasValue)return dark?Color.FromArgb(160,166,176):Color.FromArgb(100,108,119);
            if(percent.Value<20)return dark?Color.FromArgb(255,91,88):Color.FromArgb(200,43,43);
            if(percent.Value<50)return dark?Color.FromArgb(255,183,64):Color.FromArgb(173,112,0);
            return dark?Color.FromArgb(54,211,103):Color.FromArgb(31,145,73);
        }
        public static Bitmap Render(int? percent,bool charge,bool dark,bool stale,int size) {
            size=Math.Max(16,Math.Min(64,size));int canvas=size*4;
            using(var large=new Bitmap(canvas,canvas,PixelFormat.Format32bppPArgb)) {
                using(var g=Graphics.FromImage(large)) {
                    g.Clear(Color.Transparent);g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                    g.ScaleTransform(canvas/32f,canvas/32f);
                    // The level color occupies the entire badge so it remains
                    // visible at 16px. Dark digits contrast with all three bands.
                    Color level=LevelColor(percent,true);
                    Color fill=percent.HasValue?(stale?Color.FromArgb(144,155,150):level):Color.FromArgb(83,94,108);
                    Color fg=percent.HasValue?Color.FromArgb(12,17,21):Color.White;
                    Color halo=Color.Transparent;
                    var badgeRect=new RectangleF(.8f,.8f,30.4f,30.4f);
                    using(var badge=new SolidBrush(fill))g.FillEllipse(badge,badgeRect);
                    using(var edge=new Pen(Color.FromArgb(65,Color.White),.8f))g.DrawEllipse(edge,badgeRect);
                    DrawDigits(g,percent.HasValue?percent.Value.ToString():"--",charge,fg,halo);
                    if(charge) {
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
