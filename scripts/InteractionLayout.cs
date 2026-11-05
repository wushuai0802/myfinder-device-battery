using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace AtkBatteryTray {
    // Measure the same drawing commands that paint the content. Hit targets stay
    // generous; only the visible feedback follows the content's bounds.
    internal sealed class InteractionRegion {
        internal RectangleF Content,Bounds,Card,Limit;
        internal float Padding,Radius;
        internal int Id;
        internal bool Enabled;
    }
    internal static class InteractionLayout {
        [ThreadStatic] static bool measuring;
        [ThreadStatic] static RectangleF measured;
        [ThreadStatic] static Dictionary<string,RectangleF> textInk;
        internal static Action<InteractionRegion> Observer=null;
        internal static bool Measuring {get{return measuring;}}
        internal static void Include(RectangleF rect){if(measuring&&!rect.IsEmpty)measured=measured.IsEmpty?rect:RectangleF.Union(measured,rect);}
        internal static RectangleF LabelBounds(Graphics g,string text,RectangleF rect,float size,bool bold,StringAlignment alignment,StringTrimming trimming,bool wrap=false){
            if(String.IsNullOrEmpty(text)||rect.Width<=0||rect.Height<=0)return RectangleF.Empty;
            rect=VisualDesign.SnapTextRect(rect);size=VisualDesign.SnapFontSize(size);
            string family="Segoe UI";foreach(char c in text)if(c>=0x2e80&&c<=0x9fff){family="Microsoft YaHei UI";break;}
            float sx,sy,px,py;
            using(var matrix=g.Transform){var m=matrix.Elements;sx=Math.Abs(m[0]);sy=Math.Abs(m[3]);px=rect.X*sx+m[4];py=rect.Y*sy+m[5];}
            if(sx<=0||sy<=0)return RectangleF.Empty;px-=(float)Math.Floor(px);py-=(float)Math.Floor(py);
            var hint=g.TextRenderingHint==System.Drawing.Text.TextRenderingHint.SystemDefault?System.Drawing.Text.TextRenderingHint.AntiAliasGridFit:g.TextRenderingHint;
            string key=String.Join("|",new[]{family,size.ToString(CultureInfo.InvariantCulture),bold.ToString(),rect.Width.ToString(CultureInfo.InvariantCulture),rect.Height.ToString(CultureInfo.InvariantCulture),alignment.ToString(),trimming.ToString(),wrap.ToString(),sx.ToString(CultureInfo.InvariantCulture),sy.ToString(CultureInfo.InvariantCulture),px.ToString(CultureInfo.InvariantCulture),py.ToString(CultureInfo.InvariantCulture),hint.ToString(),text});
            if(textInk==null)textInk=new Dictionary<string,RectangleF>();RectangleF result;
            if(textInk.TryGetValue(key,out result)){if(!result.IsEmpty)result.Offset(rect.Location);return result;}
            // GDI+ grid fitting changes actual glyph ink and its baseline relative
            // to AddString/MeasureCharacterRanges. Measure the very same raster
            // drawing, at its monitor scale and subpixel origin, once per label.
            const int border=4;int width=(int)Math.Ceiling(rect.Width*sx)+2*border+1,height=(int)Math.Ceiling(rect.Height*sy)+2*border+1;
            using(var image=new Bitmap(width,height,PixelFormat.Format32bppPArgb))
            using(var font=new Font(family,size,bold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel))
            using(var format=new StringFormat {Alignment=alignment,LineAlignment=StringAlignment.Center,Trimming=trimming,FormatFlags=wrap?0:StringFormatFlags.NoWrap}){
                using(var drawing=Graphics.FromImage(image))using(var transform=new Matrix(sx,0,0,sy,border+px-rect.X*sx,border+py-rect.Y*sy)){drawing.TextRenderingHint=hint;drawing.PixelOffsetMode=PixelOffsetMode.Default;drawing.Transform=transform;using(var brush=new SolidBrush(Color.White))drawing.DrawString(text,font,brush,rect,format);}
                int left=width,top=height,right=-1,bottom=-1;var pixels=image.LockBits(new Rectangle(0,0,width,height),ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
                try{var row=new byte[width*4];for(int y=0;y<height;y++){Marshal.Copy(IntPtr.Add(pixels.Scan0,y*pixels.Stride),row,0,row.Length);for(int x=0;x<width;x++)if(row[x*4+3]>0){left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);}}}finally{image.UnlockBits(pixels);}
                result=right<left?RectangleF.Empty:RectangleF.FromLTRB((left-border-px)/sx,(top-border-py)/sy,(right+1-border-px)/sx,(bottom+1-border-py)/sy);
            }
            if(textInk.Count>=512)textInk.Clear();textInk[key]=result;if(!result.IsEmpty)result.Offset(rect.Location);return result;
        }
        internal static RectangleF SymbolBounds(string name,RectangleF rect){
            // Stroke bounds, including round caps, in DrawSymbol's 20-unit grid.
            RectangleF b;
            if(name=="more")b=new RectangleF(3,9,14.2f,2.2f);
            else if(name=="back")b=new RectangleF(5.2f,3.2f,7.6f,13.6f);
            else if(name=="chevron")b=new RectangleF(6.2f,3.2f,7.6f,13.6f);
            else if(name=="collapse")b=new RectangleF(3.2f,5.2f,13.6f,7.6f);
            else if(name=="expand")b=new RectangleF(3.2f,6.2f,13.6f,7.6f);
            else if(name=="refresh")b=new RectangleF(2.2f,1.2f,15.6f,16.6f);
            else if(name=="mouse")b=new RectangleF(3.2f,.7f,13.6f,18.6f);
            else if(name=="headset")b=new RectangleF(1.7f,1.2f,16.6f,16.6f);
            else if(name=="keyboard")b=new RectangleF(.2f,3.2f,19.6f,14.6f);
            else if(name=="speaker"||name=="settings")b=new RectangleF(1.2f,2.2f,name=="speaker"?16.6f:17.6f,15.6f);
            else if(name=="mic")b=new RectangleF(3.2f,1.2f,13.6f,18.6f);
            else if(name=="mic-off")b=new RectangleF(1.2f,1.2f,17.6f,18.6f);
            else if(name=="power")b=new RectangleF(2.2f,.2f,15.6f,17.6f);
            else if(name=="check")b=new RectangleF(2.2f,4.2f,15.6f,11.6f);
            else if(name=="bolt")b=new RectangleF(4.2f,.2f,11.6f,19.6f);
            else b=new RectangleF(1.2f,1.2f,17.6f,17.6f);
            return new RectangleF(rect.X+b.X*rect.Width/20,rect.Y+b.Y*rect.Height/20,b.Width*rect.Width/20,b.Height*rect.Height/20);
        }
        internal static float Radius(RectangleF bounds,RectangleF card){
            float gap=Math.Min(Math.Min(bounds.Left-card.Left,card.Right-bounds.Right),Math.Min(bounds.Top-card.Top,card.Bottom-bounds.Bottom));
            return bounds.Height>=card.Height/2?Math.Max(0,VisualDesign.PanelRadius-gap):8;
        }
        internal static bool InsideRoundedCard(RectangleF bounds,float radius,RectangleF card){
            var safe=card;safe.Inflate(-8,-8);
            if(bounds.Left<safe.Left-.001f||bounds.Top<safe.Top-.001f||bounds.Right>safe.Right+.001f||bounds.Bottom>safe.Bottom+.001f)return false;
            float outer=Math.Max(0,VisualDesign.PanelRadius-8),inner=Math.Min(radius,Math.Min(bounds.Width,bounds.Height)/2);
            for(int corner=0;corner<4;corner++)for(int step=0;step<=24;step++){
                double angle=(180+corner*90+step*90.0/24)*Math.PI/180;
                float cx=corner==0||corner==3?bounds.Left+inner:bounds.Right-inner,cy=corner<2?bounds.Top+inner:bounds.Bottom-inner;
                float x=cx+inner*(float)Math.Cos(angle),y=cy+inner*(float)Math.Sin(angle);
                float dx=Math.Max(Math.Max(safe.Left+outer-x,x-(safe.Right-outer)),0),dy=Math.Max(Math.Max(safe.Top+outer-y,y-(safe.Bottom-outer)),0);
                if(dx*dx+dy*dy>outer*outer+.001f)return false;
            }
            return true;
        }
        internal static InteractionRegion Create(RectangleF content,RectangleF card,RectangleF limit,int id){
            var safe=card;safe.Inflate(-8,-8);safe=RectangleF.Intersect(safe,limit);
            if(content.Width>safe.Width+.01f||content.Height>safe.Height+.01f)throw new InvalidOperationException("Interaction content does not fit its card: "+id+" content="+content+" safe="+safe);
            float dx=Math.Max(safe.Left-content.Left,Math.Min(0,safe.Right-content.Right)),dy=Math.Max(safe.Top-content.Top,Math.Min(0,safe.Bottom-content.Bottom));
            content.Offset(dx,dy);
            float padding=Math.Max(0,Math.Min(6,Math.Min(Math.Min(content.Left-safe.Left,safe.Right-content.Right),Math.Min(content.Top-safe.Top,safe.Bottom-content.Bottom))));
            var bounds=content;bounds.Inflate(padding,padding);
            if(!InsideRoundedCard(bounds,Radius(bounds,card),card)){
                float low=0,high=padding;for(int i=0;i<18;i++){float p=(low+high)/2;var candidate=content;candidate.Inflate(p,p);if(InsideRoundedCard(candidate,Radius(candidate,card),card))low=p;else high=p;}padding=low;bounds=content;bounds.Inflate(padding,padding);
            }
            if(!InsideRoundedCard(bounds,Radius(bounds,card),card))throw new InvalidOperationException("Interaction content enters a card corner: "+id);
            return new InteractionRegion {Content=content,Bounds=bounds,Card=card,Limit=limit,Padding=padding,Radius=Radius(bounds,card),Id=id};
        }
        internal static InteractionRegion Paint(Graphics g,RectangleF card,RectangleF limit,int id,PanelFeedback feedback,bool dark,bool selected,Action draw){
            if(!g.IsVisible(limit))return null;
            var previous=measured;bool wasMeasuring=measuring;var state=g.Save();RectangleF content;
            try{measuring=true;measured=RectangleF.Empty;g.SetClip(RectangleF.Empty);draw();content=measured;}
            finally{g.Restore(state);measured=previous;measuring=wasMeasuring;}
            if(content.IsEmpty){draw();return null;}
            var region=Create(content,card,limit,id);
            region.Enabled=feedback!=null&&id>=0;
            if(Observer!=null)Observer(region);
            var value=feedback??new PanelFeedback();
            var palette=ThemePalette.Current(dark);
            if(selected&&value.KeyboardFocus==id&&!VisualDesign.HighContrast&&palette.Accent==palette.Selected){
                // Light palettes use the accent for the selected fill too. Leave
                // a 1px card-colored gutter INSIDE the accent ring so keyboard
                // focus remains visible without extending the feedback bounds.
                var r=region.Bounds;using(var path=VisualDesign.RoundRect(r.X+1,r.Y+1,r.Width-2,r.Height-2,Math.Max(0,region.Radius-1)))using(var pen=new Pen(palette.Card,2))g.DrawPath(pen,path);
            }
            CompactRenderer.Highlight(g,region.Bounds,id,new PanelFeedback {HoverAction=selected&&!VisualDesign.HighContrast?-1:value.HoverAction,PressedAction=selected&&!VisualDesign.HighContrast?-1:value.PressedAction,KeyboardFocus=value.KeyboardFocus},dark,region.Radius);
            state=g.Save();try{g.TranslateTransform(region.Content.X-content.X,region.Content.Y-content.Y);draw();}finally{g.Restore(state);}
            return region;
        }
    }
}
