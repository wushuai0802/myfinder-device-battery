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
        internal static Color BadgeFill(int? percent,bool stale){
            if(VisualDesign.HighContrast)return SystemColors.Window;
            if(!percent.HasValue||stale)return Color.FromArgb(78,88,100);
            if(percent.Value<20)return Color.FromArgb(196,42,38);
            if(percent.Value<50)return Color.FromArgb(168,102,4);
            return Color.FromArgb(18,134,72);
        }
        internal static Color UpdateBadgeBackground(string style,int? percent,bool dark,bool stale,bool taskbarDark){
            string normalized=AtkBattery.TrayConfiguration.NormalizeTrayIconStyle(style);
            if(normalized=="badge")return BadgeFill(percent,stale);
            if(normalized=="minimal"||normalized=="device_number")return taskbarDark?Color.FromArgb(24,27,32):Color.FromArgb(242,244,248);
            if(normalized=="device")return LevelColor(percent,dark);
            return NumberFill(percent,stale);
        }
        public static Bitmap RenderStyle(string style,int? percent,bool charge,bool dark,bool stale,int size,string kind,bool approximate=false,Color? updateBadge=null,bool? taskbarDark=null){
            string normalized=AtkBattery.TrayConfiguration.NormalizeTrayIconStyle(style);
            if(normalized=="device")return RenderDevice(percent,charge,dark,stale,size,kind,updateBadge,taskbarDark);
            if(normalized=="device_number")return RenderDeviceNumber(percent,charge,dark,stale,size,kind,approximate,updateBadge,taskbarDark);
            if(normalized=="badge")return RenderBadge(percent,charge,dark,stale,size,approximate,updateBadge,taskbarDark);
            if(normalized=="minimal")return RenderMinimal(percent,charge,dark,stale,size,approximate,updateBadge,taskbarDark);
            return Render(percent,charge,dark,stale,size,approximate,updateBadge,taskbarDark);
        }
        public static Bitmap RenderDeviceNumber(int? percent,bool charge,bool dark,bool stale,int size,string kind,bool approximate=false,Color? updateBadge=null,bool? taskbarDark=null){
            size=Math.Max(16,Math.Min(64,size));int canvas=size*4;
            bool effectiveDark=taskbarDark??dark;
            using(var large=new Bitmap(canvas,canvas,PixelFormat.Format32bppPArgb)){
                using(var g=Graphics.FromImage(large)){
                    g.Clear(Color.Transparent);g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                    g.ScaleTransform(canvas/32f,canvas/32f);
                    Color gray=LevelColor(null,effectiveDark);
                    Color foreground=VisualDesign.HighContrast?SystemColors.WindowText:(effectiveDark?Color.FromArgb(244,246,248):Color.FromArgb(26,34,44));
                    Color symbol=percent.HasValue&&!stale?foreground:gray;

                    if(updateBadge.HasValue&&charge){
                        var symState=g.Save();
                        g.TranslateTransform(16.0f,6.8f);
                        g.ScaleTransform(0.76f,0.88f);
                        g.TranslateTransform(-16.0f,-6.8f);
                        DrawMiniDeviceSymbol(g,kind,symbol);
                        g.Restore(symState);
                    }else{
                        float shiftX=updateBadge.HasValue?-3.2f:(charge?-1.8f:0f);
                        g.TranslateTransform(shiftX,0f);
                        DrawMiniDeviceSymbol(g,kind,symbol);
                        g.TranslateTransform(-shiftX,0f);
                    }

                    if(charge){
                        float boltShift=updateBadge.HasValue?-23.2f:0f;
                        g.TranslateTransform(boltShift,0f);
                        using(var marker=new SolidBrush(VisualDesign.HighContrast?SystemColors.Window:Color.FromArgb(18,24,32)))g.FillEllipse(marker,24.6f,0.8f,6.4f,9.2f);
                        PointF[] bolt={new PointF(28.8f,1.8f),new PointF(25.9f,5.6f),new PointF(27.8f,5.6f),new PointF(26.9f,9.0f),new PointF(29.9f,4.6f),new PointF(28.1f,4.6f)};
                        using(var brush=new SolidBrush(VisualDesign.HighContrast?SystemColors.WindowText:(percent.HasValue&&!stale?Color.White:gray)))g.FillPolygon(brush,bolt);
                        g.TranslateTransform(-boltShift,0f);
                    }

                    Color pillFill=BadgeFill(percent,stale);
                    Color digitFg=VisualDesign.HighContrast?SystemColors.WindowText:Color.White;
                    using(var pill=VisualDesign.RoundRect(1.0f,14.2f,30.0f,16.8f,4.8f)){
                        using(var fillBrush=new SolidBrush(pillFill))g.FillPath(fillBrush,pill);
                        using(var edge=new Pen(VisualDesign.HighContrast?SystemColors.WindowText:Color.FromArgb(48,Color.White),0.75f))g.DrawPath(edge,pill);
                    }
                    DrawDigits(g,percent.HasValue?percent.Value.ToString():"--",false,digitFg,Color.Transparent,13.6f,12.0f,24.8f,26.2f,22.8f,16.0f);
                }
                var result=new Bitmap(size,size,PixelFormat.Format32bppPArgb);
                using(var g=Graphics.FromImage(result))using(var attributes=new ImageAttributes()){
                    g.CompositingMode=CompositingMode.SourceCopy;g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                    attributes.SetWrapMode(WrapMode.TileFlipXY);g.DrawImage(large,new Rectangle(0,0,size,size),0,0,canvas,canvas,GraphicsUnit.Pixel,attributes);
                }
                result.SetPixel(0,0,Color.Transparent);result.SetPixel(size-1,0,Color.Transparent);
                result.SetPixel(0,size-1,Color.Transparent);result.SetPixel(size-1,size-1,Color.Transparent);
                if(updateBadge.HasValue)UpdateBadge.Paint(result,updateBadge.Value,effectiveDark);
                return result;
            }
        }
        static void DrawMiniDeviceSymbol(Graphics g,string kind,Color color){
            using(var pen=new Pen(color,2.0f){StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round})
            using(var fill=new SolidBrush(color)){
                if(kind=="mouse"){
                    using(var body=VisualDesign.RoundRect(11.4f,1.0f,9.2f,11.6f,4.4f))g.DrawPath(pen,body);
                    g.DrawLine(pen,16.0f,3.2f,16.0f,6.4f);
                }else if(kind=="headset"){
                    g.DrawArc(pen,9.2f,1.3f,13.6f,11.4f,180,180);
                    using(var left=VisualDesign.RoundRect(8.2f,6.2f,3.6f,6.0f,1.6f))g.FillPath(fill,left);
                    using(var right=VisualDesign.RoundRect(20.2f,6.2f,3.6f,6.0f,1.6f))g.FillPath(fill,right);
                }else if(kind=="keyboard"){
                    using(var body=VisualDesign.RoundRect(6.2f,2.0f,19.6f,9.8f,2.2f))g.DrawPath(pen,body);
                    g.FillEllipse(fill,9.2f,4.4f,2.0f,2.0f);g.FillEllipse(fill,13.2f,4.4f,2.0f,2.0f);g.FillEllipse(fill,17.2f,4.4f,2.0f,2.0f);g.FillEllipse(fill,21.2f,4.4f,2.0f,2.0f);
                    g.DrawLine(pen,10.6f,9.1f,21.4f,9.1f);
                }else if(kind=="gamepad"){
                    using(var body=VisualDesign.RoundRect(6.8f,2.0f,18.4f,9.6f,4.4f))g.DrawPath(pen,body);
                    g.DrawLine(pen,9.6f,6.8f,13.4f,6.8f);g.DrawLine(pen,11.5f,4.9f,11.5f,8.7f);
                    g.FillEllipse(fill,18.0f,4.8f,2.1f,2.1f);g.FillEllipse(fill,20.4f,6.9f,2.1f,2.1f);
                }else{
                    using(var body=VisualDesign.RoundRect(9.8f,1.6f,12.4f,10.4f,2.6f))g.DrawPath(pen,body);
                    g.DrawLine(pen,13.0f,5.0f,19.0f,5.0f);
                    g.FillEllipse(fill,14.8f,7.4f,2.4f,2.4f);
                }
            }
        }
        public static Bitmap RenderBadge(int? percent,bool charge,bool dark,bool stale,int size,bool approximate=false,Color? updateBadge=null,bool? taskbarDark=null){
            size=Math.Max(16,Math.Min(64,size));int canvas=size*4;
            using(var large=new Bitmap(canvas,canvas,PixelFormat.Format32bppPArgb)){
                using(var g=Graphics.FromImage(large)){
                    g.Clear(Color.Transparent);g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                    g.ScaleTransform(canvas/32f,canvas/32f);
                    Color fill=BadgeFill(percent,stale);
                    Color fg=VisualDesign.HighContrast?SystemColors.WindowText:Color.White;
                    using(var shape=VisualDesign.RoundRect(1.2f,2.2f,29.6f,27.6f,7.5f)){
                        using(var badge=new SolidBrush(fill))g.FillPath(badge,shape);
                        using(var edge=new Pen(VisualDesign.HighContrast?SystemColors.WindowText:Color.FromArgb(46,Color.White),0.85f))g.DrawPath(edge,shape);
                    }
                    if(updateBadge.HasValue)g.TranslateTransform(0,32f/size);
                    DrawDigits(g,percent.HasValue?percent.Value.ToString():"--",charge,fg,Color.Transparent,18.2f,16.4f,24.6f,23.2f);
                    if(updateBadge.HasValue)g.TranslateTransform(0,-32f/size);
                    if(charge){
                        if(updateBadge.HasValue)g.TranslateTransform(-23.5f,0);
                        using(var marker=new SolidBrush(VisualDesign.HighContrast?SystemColors.Window:Color.FromArgb(20,26,34)))g.FillEllipse(marker,24.6f,1.6f,6.6f,9.4f);
                        PointF[] bolt={new PointF(28.9f,2.6f),new PointF(26.0f,6.4f),new PointF(27.9f,6.4f),new PointF(27.0f,9.8f),new PointF(30.0f,5.4f),new PointF(28.2f,5.4f)};
                        using(var brush=new SolidBrush(VisualDesign.HighContrast?SystemColors.WindowText:Color.White))g.FillPolygon(brush,bolt);
                    }
                }
                var result=new Bitmap(size,size,PixelFormat.Format32bppPArgb);
                using(var g=Graphics.FromImage(result))using(var attributes=new ImageAttributes()){
                    g.CompositingMode=CompositingMode.SourceCopy;g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                    attributes.SetWrapMode(WrapMode.TileFlipXY);g.DrawImage(large,new Rectangle(0,0,size,size),0,0,canvas,canvas,GraphicsUnit.Pixel,attributes);
                }
                result.SetPixel(0,0,Color.Transparent);result.SetPixel(size-1,0,Color.Transparent);
                result.SetPixel(0,size-1,Color.Transparent);result.SetPixel(size-1,size-1,Color.Transparent);
                if(updateBadge.HasValue)UpdateBadge.Paint(result,updateBadge.Value,taskbarDark??dark);
                return result;
            }
        }
        public static Bitmap RenderMinimal(int? percent,bool charge,bool dark,bool stale,int size,bool approximate=false,Color? updateBadge=null,bool? taskbarDark=null){
            size=Math.Max(16,Math.Min(64,size));int canvas=size*4;
            bool effectiveDark=taskbarDark??dark;
            using(var large=new Bitmap(canvas,canvas,PixelFormat.Format32bppPArgb)){
                using(var g=Graphics.FromImage(large)){
                    g.Clear(Color.Transparent);g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                    g.ScaleTransform(canvas/32f,canvas/32f);
                    Color fg=VisualDesign.HighContrast?SystemColors.WindowText:LevelColor(stale?(int?)null:percent,effectiveDark);
                    if(updateBadge.HasValue)g.TranslateTransform(0,32f/size);
                    DrawDigits(g,percent.HasValue?percent.Value.ToString():"--",charge,fg,Color.Transparent,22.4f,18.6f,28.8f,27.8f);
                    if(updateBadge.HasValue)g.TranslateTransform(0,-32f/size);
                    if(charge){
                        if(updateBadge.HasValue)g.TranslateTransform(-23.5f,0);
                        Color boltColor=VisualDesign.HighContrast?SystemColors.WindowText:(effectiveDark?Color.White:Color.FromArgb(24,30,38));
                        PointF[] bolt={new PointF(28.8f,2.0f),new PointF(25.6f,6.4f),new PointF(27.7f,6.4f),new PointF(26.7f,10.2f),new PointF(30.2f,5.2f),new PointF(28.1f,5.2f)};
                        using(var brush=new SolidBrush(boltColor))g.FillPolygon(brush,bolt);
                    }
                }
                var result=new Bitmap(size,size,PixelFormat.Format32bppPArgb);
                using(var g=Graphics.FromImage(result))using(var attributes=new ImageAttributes()){
                    g.CompositingMode=CompositingMode.SourceCopy;g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                    attributes.SetWrapMode(WrapMode.TileFlipXY);g.DrawImage(large,new Rectangle(0,0,size,size),0,0,canvas,canvas,GraphicsUnit.Pixel,attributes);
                }
                result.SetPixel(0,0,Color.Transparent);result.SetPixel(size-1,0,Color.Transparent);
                result.SetPixel(0,size-1,Color.Transparent);result.SetPixel(size-1,size-1,Color.Transparent);
                if(updateBadge.HasValue)UpdateBadge.Paint(result,updateBadge.Value,effectiveDark);
                return result;
            }
        }
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
                    // Omit "~" prefix on the tiny 16-32px tray badge so approximate readings keep full two-digit
                    // legibility and never resemble a minus sign ("-"); tooltip and panel still disclose approximation.
                    DrawDigits(g,percent.HasValue?percent.Value.ToString():"--",charge,fg,halo);
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
        static void DrawDigits(Graphics g,string text,bool charge,Color fg,Color halo,float twoDigitHeight=18.2f,float threeDigitHeight=16.5f,float twoDigitWidth=24.4f,float threeDigitWidth=23f,float centerY=16.2f,float? centerX=null) {
            if(text=="--") {
                using(var pen=new Pen(fg,2.3f)) {
                    pen.StartCap=LineCap.Round;pen.EndCap=LineCap.Round;
                    float dashCx=centerX??16f;
                    g.DrawLine(pen,dashCx-6.5f,centerY,dashCx-2.5f,centerY);g.DrawLine(pen,dashCx+2.5f,centerY,dashCx+6.5f,centerY);
                }
                return;
            }
            using(var path=new GraphicsPath())
            using(var format=new StringFormat {FormatFlags=StringFormatFlags.NoWrap}) {
                FontFamily family;try{family=new FontFamily("Bahnschrift");}catch{family=new FontFamily("Segoe UI");}
                using(family)path.AddString(text,family,(int)FontStyle.Bold,100f,PointF.Empty,format);
                var bounds=path.GetBounds();if(bounds.Width<=0||bounds.Height<=0)return;
                // Keep normal two-digit readouts large; 100 gets its own fit.
                float targetHeight=text.Length>=3?threeDigitHeight:twoDigitHeight;
                float sy=targetHeight/bounds.Height;
                float sx=Math.Min(sy,(text.Length>=3?threeDigitWidth:twoDigitWidth)/bounds.Width);
                float cx=centerX??(charge?14.8f:16f);
                using(var matrix=new Matrix()) {
                    matrix.Translate(cx,centerY);matrix.Scale(sx,sy);matrix.Translate(-bounds.X-bounds.Width/2f,-bounds.Y-bounds.Height/2f);path.Transform(matrix);
                }
                using(var brush=new SolidBrush(fg))g.FillPath(brush,path);
            }
        }
    }
}
