using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Windows.Forms;
using AtkBattery;

namespace AtkBattery {
    [DataContract] internal sealed class FeedbackImageDto {
        [DataMember(Name="mime")] public string Mime;
        [DataMember(Name="data")] public string Data;
    }

    internal sealed class FeedbackImageAttachment:IDisposable {
        internal readonly string Mime,Base64Data,Name;
        internal readonly int ByteLength,Width,Height;
        internal readonly Bitmap Thumbnail;
        bool disposed;

        internal FeedbackImageAttachment(string mime,string base64Data,int byteLength,int width,int height,string name,Bitmap thumbnail){
            Mime=mime;Base64Data=base64Data;ByteLength=byteLength;Width=width;Height=height;Name=name??"图片";Thumbnail=thumbnail;
        }

        internal FeedbackImageDto ToDto(){
            return new FeedbackImageDto {Mime=Mime,Data=Base64Data};
        }

        internal FeedbackImageAttachment Clone(){
            Bitmap copy=null;
            if(Thumbnail!=null&&!disposed){
                copy=new Bitmap(Thumbnail.Width,Thumbnail.Height,PixelFormat.Format32bppArgb);
                using(var g=Graphics.FromImage(copy))g.DrawImageUnscaled(Thumbnail,0,0);
            }
            return new FeedbackImageAttachment(Mime,Base64Data,ByteLength,Width,Height,Name,copy);
        }

        public void Dispose(){
            if(disposed)return;
            disposed=true;
            if(Thumbnail!=null)Thumbnail.Dispose();
        }
    }

    internal sealed class FeedbackImageEncodeResult {
        internal readonly FeedbackImageAttachment Attachment;
        internal readonly string Error;
        internal FeedbackImageEncodeResult(FeedbackImageAttachment attachment,string error){Attachment=attachment;Error=error;}
    }

    internal static class FeedbackImageEncoder {
        internal const int MaxImages=3;
        internal const int MaxEncodedBytes=524288;
        internal const int PngPreferBytes=327680;
        internal const int MaxInputFileBytes=20*1024*1024;
        internal const int MaxDimension=16384;
        internal const long MaxInputPixels=64000000L;
        internal const int MaxLongEdge=1600;
        static readonly string[] SupportedExtensions={".png",".jpg",".jpeg",".bmp",".gif",".tif",".tiff"};

        internal static bool IsSupportedFile(string path){
            if(String.IsNullOrEmpty(path))return false;
            string ext=Path.GetExtension(path);
            if(String.IsNullOrEmpty(ext))return false;
            for(int i=0;i<SupportedExtensions.Length;i++)
                if(String.Equals(ext,SupportedExtensions[i],StringComparison.OrdinalIgnoreCase))return true;
            return false;
        }

        internal static FeedbackImageEncodeResult EncodeFile(string path){
            if(String.IsNullOrEmpty(path)||!File.Exists(path))
                return new FeedbackImageEncodeResult(null,"找不到指定的图片文件。");
            try{
                var info=new FileInfo(path);
                if(info.Length<=0)return new FeedbackImageEncodeResult(null,"图片文件为空。");
                if(info.Length>MaxInputFileBytes)return new FeedbackImageEncodeResult(null,"图片文件过大（不能超过 20 MB）。");
                byte[] raw=File.ReadAllBytes(path);
                using(var ms=new MemoryStream(raw,false))
                using(var image=Image.FromStream(ms,false,true)){
                    return EncodeImage(image,Path.GetFileName(path));
                }
            }catch(OutOfMemoryException){
                return new FeedbackImageEncodeResult(null,"无法解析该图片文件，请换用常见 PNG/JPG 格式。");
            }catch(ArgumentException){
                return new FeedbackImageEncodeResult(null,"不支持的图片格式，请选择 PNG、JPG、BMP 或 GIF 图片。");
            }catch(IOException){
                return new FeedbackImageEncodeResult(null,"读取图片文件失败，请确认文件未被占用。");
            }catch(UnauthorizedAccessException){
                return new FeedbackImageEncodeResult(null,"没有权限读取该图片文件。");
            }
        }

        internal static FeedbackImageEncodeResult EncodeImage(Image source,string name=null){
            if(source==null)return new FeedbackImageEncodeResult(null,"没有可添加的图片。");
            if(source.Width<=0||source.Height<=0||source.Width>MaxDimension||source.Height>MaxDimension||(long)source.Width*source.Height>MaxInputPixels)
                return new FeedbackImageEncodeResult(null,"图片分辨率过高或无效，请先裁剪后再添加。");
            try{
                // Copying into a fresh Bitmap normalizes orientation and strips all EXIF/metadata.
                using(var clean=NormalizeAndScale(source,MaxLongEdge)){
                    byte[] encoded=null;
                    string mime=null;
                    int finalWidth=clean.Width,finalHeight=clean.Height;

                    byte[] png=EncodePng(clean);
                    if(png.Length<=PngPreferBytes){
                        encoded=png;
                        mime="image/png";
                    }else{
                        int[] qualities={84,74,62};
                        for(int i=0;i<qualities.Length;i++){
                            byte[] jpg=EncodeJpeg(clean,qualities[i]);
                            if(jpg.Length<=MaxEncodedBytes){encoded=jpg;mime="image/jpeg";break;}
                        }
                        if(encoded==null&&png.Length<=MaxEncodedBytes){
                            encoded=png;
                            mime="image/png";
                        }
                        if(encoded==null){
                            int[] fallbackEdges={1200,900};
                            int[] fallbackQualities={72,58,50};
                            for(int e=0;e<fallbackEdges.Length&&encoded==null;e++){
                                using(var smaller=ResizeBitmap(clean,fallbackEdges[e])){
                                    for(int q=0;q<fallbackQualities.Length;q++){
                                        byte[] jpg=EncodeJpeg(smaller,fallbackQualities[q]);
                                        if(jpg.Length<=MaxEncodedBytes){
                                            encoded=jpg;mime="image/jpeg";
                                            finalWidth=smaller.Width;finalHeight=smaller.Height;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    if(encoded==null||encoded.Length<16||encoded.Length>MaxEncodedBytes)
                        return new FeedbackImageEncodeResult(null,"图片压缩后仍超过 512 KB，请先裁剪后再添加。");

                    var thumb=CreateThumbnail(clean,96,72);
                    var attachment=new FeedbackImageAttachment(mime,Convert.ToBase64String(encoded),encoded.Length,finalWidth,finalHeight,name??"截图",thumb);
                    return new FeedbackImageEncodeResult(attachment,null);
                }
            }catch(Exception){
                return new FeedbackImageEncodeResult(null,"处理图片失败，请尝试换一张图片。");
            }
        }

        internal static Bitmap CreatePreviewBitmap(FeedbackImageAttachment attachment){
            if(attachment==null||String.IsNullOrEmpty(attachment.Base64Data))return null;
            byte[] bytes=Convert.FromBase64String(attachment.Base64Data);
            using(var ms=new MemoryStream(bytes,false))
            using(var decoded=Image.FromStream(ms,false,true)){
                var copy=new Bitmap(decoded.Width,decoded.Height,PixelFormat.Format32bppArgb);
                using(var g=Graphics.FromImage(copy))g.DrawImage(decoded,0,0,decoded.Width,decoded.Height);
                return copy;
            }
        }

        static RotateFlipType ReadOrientation(Image image){
            try{
                const int orientationId=0x0112;
                if(Array.IndexOf(image.PropertyIdList,orientationId)<0)return RotateFlipType.RotateNoneFlipNone;
                var item=image.GetPropertyItem(orientationId);
                if(item==null||item.Value==null||item.Value.Length<2)return RotateFlipType.RotateNoneFlipNone;
                int value=BitConverter.ToUInt16(item.Value,0);
                switch(value){
                    case 2:return RotateFlipType.RotateNoneFlipX;
                    case 3:return RotateFlipType.Rotate180FlipNone;
                    case 4:return RotateFlipType.Rotate180FlipX;
                    case 5:return RotateFlipType.Rotate90FlipX;
                    case 6:return RotateFlipType.Rotate90FlipNone;
                    case 7:return RotateFlipType.Rotate270FlipX;
                    case 8:return RotateFlipType.Rotate270FlipNone;
                    default:return RotateFlipType.RotateNoneFlipNone;
                }
            }catch(Exception){
                return RotateFlipType.RotateNoneFlipNone;
            }
        }

        static Bitmap NormalizeAndScale(Image source,int maxEdge){
            var orientation=ReadOrientation(source);
            bool swap=orientation==RotateFlipType.Rotate90FlipNone||orientation==RotateFlipType.Rotate90FlipX
                ||orientation==RotateFlipType.Rotate270FlipNone||orientation==RotateFlipType.Rotate270FlipX;
            int srcW=source.Width,srcH=source.Height;
            int outW=srcW,outH=srcH;
            int longEdge=Math.Max(srcW,srcH);
            if(longEdge>maxEdge){
                double ratio=(double)maxEdge/longEdge;
                outW=Math.Max(1,(int)Math.Round(srcW*ratio));
                outH=Math.Max(1,(int)Math.Round(srcH*ratio));
            }
            var bmp=new Bitmap(outW,outH,PixelFormat.Format32bppArgb);
            bmp.SetResolution(96,96);
            using(var g=Graphics.FromImage(bmp)){
                g.CompositingQuality=CompositingQuality.HighQuality;
                g.InterpolationMode=InterpolationMode.HighQualityBicubic;
                g.SmoothingMode=SmoothingMode.HighQuality;
                g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                g.DrawImage(source,0,0,outW,outH);
            }
            if(orientation!=RotateFlipType.RotateNoneFlipNone)bmp.RotateFlip(orientation);
            if(swap&&Math.Max(bmp.Width,bmp.Height)>maxEdge){
                var resized=ResizeBitmap(bmp,maxEdge);
                bmp.Dispose();
                return resized;
            }
            return bmp;
        }

        static Bitmap ResizeBitmap(Bitmap source,int maxEdge){
            int longEdge=Math.Max(source.Width,source.Height);
            if(longEdge<=maxEdge){
                var clone=new Bitmap(source.Width,source.Height,PixelFormat.Format32bppArgb);
                using(var g=Graphics.FromImage(clone))g.DrawImageUnscaled(source,0,0);
                return clone;
            }
            double ratio=(double)maxEdge/longEdge;
            int w=Math.Max(1,(int)Math.Round(source.Width*ratio));
            int h=Math.Max(1,(int)Math.Round(source.Height*ratio));
            var bmp=new Bitmap(w,h,PixelFormat.Format32bppArgb);
            bmp.SetResolution(96,96);
            using(var g=Graphics.FromImage(bmp)){
                g.CompositingQuality=CompositingQuality.HighQuality;
                g.InterpolationMode=InterpolationMode.HighQualityBicubic;
                g.SmoothingMode=SmoothingMode.HighQuality;
                g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                g.DrawImage(source,0,0,w,h);
            }
            return bmp;
        }

        static Bitmap CreateThumbnail(Bitmap source,int maxW,int maxH){
            double ratio=Math.Min((double)maxW/source.Width,(double)maxH/source.Height);
            int w=Math.Max(1,(int)Math.Round(source.Width*ratio));
            int h=Math.Max(1,(int)Math.Round(source.Height*ratio));
            var thumb=new Bitmap(w,h,PixelFormat.Format32bppArgb);
            using(var g=Graphics.FromImage(thumb)){
                g.InterpolationMode=InterpolationMode.HighQualityBicubic;
                g.SmoothingMode=SmoothingMode.HighQuality;
                g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                g.DrawImage(source,0,0,w,h);
            }
            return thumb;
        }

        static byte[] EncodePng(Bitmap bmp){
            using(var ms=new MemoryStream()){
                bmp.Save(ms,ImageFormat.Png);
                return ms.ToArray();
            }
        }

        static byte[] EncodeJpeg(Bitmap source,long quality){
            using(var rgb=new Bitmap(source.Width,source.Height,PixelFormat.Format24bppRgb)){
                using(var g=Graphics.FromImage(rgb)){
                    g.Clear(Color.White);
                    g.DrawImage(source,0,0,source.Width,source.Height);
                }
                var codec=FindJpegCodec();
                using(var ms=new MemoryStream()){
                    if(codec==null){
                        rgb.Save(ms,ImageFormat.Jpeg);
                    }else{
                        using(var parameters=new EncoderParameters(1))
                        using(var param=new EncoderParameter(Encoder.Quality,quality)){
                            parameters.Param[0]=param;
                            rgb.Save(ms,codec,parameters);
                        }
                    }
                    return ms.ToArray();
                }
            }
        }

        static ImageCodecInfo FindJpegCodec(){
            var codecs=ImageCodecInfo.GetImageEncoders();
            for(int i=0;i<codecs.Length;i++)
                if(String.Equals(codecs[i].MimeType,"image/jpeg",StringComparison.OrdinalIgnoreCase))return codecs[i];
            return null;
        }
    }
}

namespace AtkBatteryTray {
    internal static class FeedbackClipboardHelper {
        internal static bool TryPasteInto(FeedbackImageStrip strip){
            if(strip==null||!strip.Enabled)return false;
            try{
                if(Clipboard.ContainsImage()){
                    using(var img=Clipboard.GetImage()){
                        if(img==null)return false;
                        string error;
                        strip.TryAddImage(img,"剪贴板截图",out error);
                        return true;
                    }
                }
                if(Clipboard.ContainsFileDropList()){
                    StringCollection list=Clipboard.GetFileDropList();
                    if(list!=null&&list.Count>0){
                        var paths=new List<string>();
                        foreach(string file in list)if(FeedbackImageEncoder.IsSupportedFile(file))paths.Add(file);
                        if(paths.Count>0){
                            string error;
                            strip.TryAddFiles(paths.ToArray(),out error);
                            return true;
                        }
                    }
                }
            }catch(ExternalException){
                return false;
            }catch(Exception){
                return false;
            }
            return false;
        }
    }

    // Single self-drawn control: avoids child HWND/ThemedButton clipping inside scrollable dialogs.
    internal sealed class FeedbackImageStrip:Control {
        readonly DialogTheme theme;
        readonly List<FeedbackImageAttachment> items=new List<FeedbackImageAttachment>();
        RectangleF addRect;
        readonly List<RectangleF> thumbRects=new List<RectangleF>();
        readonly List<RectangleF> removeRects=new List<RectangleF>();
        int hoverTarget; // 0=none, 1=add, 2=thumb, 3=remove
        int hoverIndex=-1;
        bool pressed;
        readonly Func<string[]> browseFiles;

        internal event EventHandler ImagesChanged;
        internal event Action<string> StatusMessage;

        internal int ImageCount {get{return items.Count;}}

        internal FeedbackImageStrip(DialogTheme theme,Func<string[]> filePicker=null){
            this.theme=theme;
            browseFiles=filePicker;
            Name="FeedbackImages";
            AccessibleName="附带图片";
            DoubleBuffered=true;
            TabStop=true;
            BackColor=theme.Card;
            ForeColor=theme.Foreground;
            Margin=Padding.Empty;
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.Selectable,true);
        }

        internal List<FeedbackImageAttachment> SnapshotAttachments(){
            return new List<FeedbackImageAttachment>(items);
        }

        internal List<FeedbackImageAttachment> CloneAttachments(){
            var list=new List<FeedbackImageAttachment>(items.Count);
            for(int i=0;i<items.Count;i++)list.Add(items[i].Clone());
            return list;
        }

        internal void SetAttachments(IEnumerable<FeedbackImageAttachment> attachments){
            DisposeItems();
            if(attachments!=null){
                foreach(var item in attachments){
                    if(item!=null&&items.Count<FeedbackImageEncoder.MaxImages)items.Add(item.Clone());
                }
            }
            NotifyChanged();
        }

        internal void ClearAttachments(){
            if(items.Count==0)return;
            DisposeItems();
            NotifyChanged();
        }

        internal bool TryAddFiles(string[] paths,out string error){
            error=null;
            if(paths==null||paths.Length==0)return false;
            bool added=false;
            for(int i=0;i<paths.Length;i++){
                if(items.Count>=FeedbackImageEncoder.MaxImages){
                    error="最多只能附带 "+FeedbackImageEncoder.MaxImages+" 张图片。";
                    break;
                }
                if(!FeedbackImageEncoder.IsSupportedFile(paths[i])){
                    error="仅支持 PNG、JPG、BMP、GIF、TIFF 格式的图片。";
                    continue;
                }
                var encoded=FeedbackImageEncoder.EncodeFile(paths[i]);
                if(encoded.Attachment!=null){
                    items.Add(encoded.Attachment);
                    added=true;
                }else if(error==null){
                    error=encoded.Error;
                }
            }
            if(added)NotifyChanged();
            if(error!=null)RaiseStatus(error);
            else if(added)RaiseStatus("");
            return added;
        }

        internal bool TryAddImage(Image image,string name,out string error){
            error=null;
            if(items.Count>=FeedbackImageEncoder.MaxImages){
                error="最多只能附带 "+FeedbackImageEncoder.MaxImages+" 张图片。";
                RaiseStatus(error);
                return false;
            }
            var encoded=FeedbackImageEncoder.EncodeImage(image,name);
            if(encoded.Attachment==null){
                error=encoded.Error??"无法添加该图片。";
                RaiseStatus(error);
                return false;
            }
            items.Add(encoded.Attachment);
            NotifyChanged();
            RaiseStatus("");
            return true;
        }

        internal void RemoveAt(int index){
            if(index<0||index>=items.Count)return;
            items[index].Dispose();
            items.RemoveAt(index);
            NotifyChanged();
            RaiseStatus("");
        }

        void NotifyChanged(){
            UpdateLayoutGeometry();
            Invalidate();
            var handler=ImagesChanged;
            if(handler!=null)handler(this,EventArgs.Empty);
        }

        void RaiseStatus(string message){
            var handler=StatusMessage;
            if(handler!=null)handler(message??"");
        }

        public override Size GetPreferredSize(Size proposedSize){
            float scale=DialogDpi.Scale(this);
            int width=proposedSize.Width>0?proposedSize.Width:Math.Max(220,(int)Math.Round(460*scale));
            int rowHeight=Math.Max((int)Math.Ceiling(Font.Height*1.85f),(int)Math.Round((items.Count>0?48f:30f)*scale));
            return new Size(width,rowHeight);
        }

        void UpdateLayoutGeometry(){
            thumbRects.Clear();
            removeRects.Clear();
            addRect=RectangleF.Empty;
            float scale=DialogDpi.Scale(this);
            float h=Math.Max(1,ClientSize.Height);
            float x=0;
            float gap=8f*scale;
            if(items.Count>0){
                float thumbH=Math.Max(28f*scale,h-4f*scale);
                float thumbW=(float)Math.Round(thumbH*1.32f);
                float thumbY=(h-thumbH)/2f;
                float badgeSize=Math.Max(14f,15f*scale);
                for(int i=0;i<items.Count;i++){
                    var rect=new RectangleF(x,thumbY,thumbW,thumbH);
                    thumbRects.Add(rect);
                    removeRects.Add(new RectangleF(rect.Right-badgeSize-2f*scale,rect.Top+2f*scale,badgeSize,badgeSize));
                    x+=thumbW+gap;
                }
            }
            if(items.Count<FeedbackImageEncoder.MaxImages){
                string label=items.Count==0?"+ 添加图片":"+ 添加 ("+items.Count+"/"+FeedbackImageEncoder.MaxImages+")";
                Size textSize=TextRenderer.MeasureText(label,Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding);
                float btnH=Math.Min(h,Math.Max(textSize.Height+10f*scale,28f*scale));
                float btnW=textSize.Width+20f*scale;
                addRect=new RectangleF(x,(h-btnH)/2f,btnW,btnH);
            }
        }

        protected override void OnResize(EventArgs e){
            base.OnResize(e);
            UpdateLayoutGeometry();
        }

        protected override void OnFontChanged(EventArgs e){
            base.OnFontChanged(e);
            UpdateLayoutGeometry();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e){
            UpdateLayoutGeometry();
            var g=e.Graphics;
            g.SmoothingMode=SmoothingMode.AntiAlias;
            g.InterpolationMode=InterpolationMode.HighQualityBicubic;
            using(var bg=new SolidBrush(theme.Card))g.FillRectangle(bg,ClientRectangle);

            float scale=DialogDpi.Scale(this);
            float radius=Math.Max(4f,6f*scale);

            for(int i=0;i<items.Count&&i<thumbRects.Count;i++){
                var rect=thumbRects[i];
                bool thumbHovered=Enabled&&hoverTarget==2&&hoverIndex==i;
                bool removeHovered=Enabled&&hoverTarget==3&&hoverIndex==i;
                using(var path=RoundedPath(rect,radius)){
                    using(var fill=new SolidBrush(theme.Track))g.FillPath(fill,path);
                    var prevClip=g.Clip;
                    g.SetClip(path,CombineMode.Intersect);
                    var bmp=items[i].Thumbnail;
                    if(bmp!=null){
                        float sx=rect.Width/bmp.Width,sy=rect.Height/bmp.Height;
                        float factor=Math.Max(sx,sy);
                        float dw=bmp.Width*factor,dh=bmp.Height*factor;
                        g.DrawImage(bmp,rect.X+(rect.Width-dw)/2f,rect.Y+(rect.Height-dh)/2f,dw,dh);
                    }
                    g.Clip=prevClip;
                    using(var pen=new Pen(thumbHovered?theme.Accent:theme.Border,Math.Max(1f,scale)))
                        g.DrawPath(pen,path);
                }
                if(Enabled){
                    var badge=removeRects[i];
                    Color badgeBg=removeHovered?theme.Error:Color.FromArgb(185,20,20,20);
                    using(var bBrush=new SolidBrush(badgeBg))g.FillEllipse(bBrush,badge);
                    float pad=badge.Width*0.30f;
                    using(var xPen=new Pen(Color.White,Math.Max(1.3f,1.4f*scale))){
                        xPen.StartCap=LineCap.Round;xPen.EndCap=LineCap.Round;
                        g.DrawLine(xPen,badge.Left+pad,badge.Top+pad,badge.Right-pad,badge.Bottom-pad);
                        g.DrawLine(xPen,badge.Right-pad,badge.Top+pad,badge.Left+pad,badge.Bottom-pad);
                    }
                }
            }

            if(!addRect.IsEmpty){
                bool btnHot=Enabled&&hoverTarget==1;
                Color fill=btnHot?theme.Interaction(theme.Card,pressed):theme.Card;
                Color border=btnHot?theme.Accent:theme.Border;
                Color textCol=Enabled?theme.Foreground:theme.Muted;
                CompactRenderer.Fill(g,addRect,fill,radius);
                using(var btnPath=RoundedPath(addRect,radius))
                using(var btnPen=new Pen(border,Math.Max(1f,scale)))
                    g.DrawPath(btnPen,btnPath);
                string label=items.Count==0?"+ 添加图片":"+ 添加 ("+items.Count+"/"+FeedbackImageEncoder.MaxImages+")";
                var textBounds=Rectangle.Round(addRect);
                TextRenderer.DrawText(g,label,Font,textBounds,textCol,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding);
                if(Focused&&ShowFocusCues)CompactRenderer.FocusRing(g,addRect,theme.Accent,radius,scale);
            }

            float hintLeft=!addRect.IsEmpty?addRect.Right+10f*scale:(thumbRects.Count>0?thumbRects[thumbRects.Count-1].Right+10f*scale:0);
            int hintWidth=(int)Math.Floor(ClientSize.Width-hintLeft);
            if(hintWidth>36){
                string hint=items.Count==0?"可选，最多 3 张，支持拖入或 Ctrl+V 粘贴截图"
                    :items.Count>=FeedbackImageEncoder.MaxImages?"已选 3/3 张，点击缩略图可预览"
                    :"点击缩略图可预览，支持拖入或 Ctrl+V";
                var hintRect=new Rectangle((int)Math.Ceiling(hintLeft),0,hintWidth,ClientSize.Height);
                TextRenderer.DrawText(g,hint,Font,hintRect,theme.Muted,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding);
            }
        }

        static GraphicsPath RoundedPath(RectangleF rect,float radius){
            var path=new GraphicsPath();
            float d=Math.Min(radius*2f,Math.Min(rect.Width,rect.Height));
            if(d<=0.5f){path.AddRectangle(rect);return path;}
            path.AddArc(rect.X,rect.Y,d,d,180,90);
            path.AddArc(rect.Right-d,rect.Y,d,d,270,90);
            path.AddArc(rect.Right-d,rect.Bottom-d,d,d,0,90);
            path.AddArc(rect.X,rect.Bottom-d,d,d,90,90);
            path.CloseFigure();
            return path;
        }

        void HitTestPoint(Point pt,out int target,out int index){
            target=0;index=-1;
            if(!Enabled)return;
            for(int i=0;i<removeRects.Count;i++){
                if(removeRects[i].Contains(pt)){target=3;index=i;return;}
            }
            for(int i=0;i<thumbRects.Count;i++){
                if(thumbRects[i].Contains(pt)){target=2;index=i;return;}
            }
            if(!addRect.IsEmpty&&addRect.Contains(pt)){target=1;index=-1;}
        }

        protected override void OnMouseMove(MouseEventArgs e){
            base.OnMouseMove(e);
            int t,idx;HitTestPoint(e.Location,out t,out idx);
            if(t!=hoverTarget||idx!=hoverIndex){
                hoverTarget=t;hoverIndex=idx;
                Cursor=t!=0?Cursors.Hand:Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e){
            base.OnMouseLeave(e);
            if(hoverTarget!=0||pressed){hoverTarget=0;hoverIndex=-1;pressed=false;Cursor=Cursors.Default;Invalidate();}
        }

        protected override void OnMouseDown(MouseEventArgs e){
            base.OnMouseDown(e);
            if(e.Button!=MouseButtons.Left||!Enabled)return;
            Focus();
            int t,idx;HitTestPoint(e.Location,out t,out idx);
            hoverTarget=t;hoverIndex=idx;
            pressed=(t==1);
            if(pressed)Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e){
            base.OnMouseUp(e);
            if(e.Button!=MouseButtons.Left||!Enabled)return;
            bool wasPressed=pressed;pressed=false;
            if(wasPressed)Invalidate();
            int t,idx;HitTestPoint(e.Location,out t,out idx);
            if(t==1)OpenFilePicker();
            else if(t==3&&idx>=0)RemoveAt(idx);
            else if(t==2&&idx>=0)ShowPreview(idx);
        }

        protected override void OnKeyDown(KeyEventArgs e){
            base.OnKeyDown(e);
            if(!Enabled)return;
            if(e.KeyCode==Keys.Space||e.KeyCode==Keys.Enter){
                if(items.Count<FeedbackImageEncoder.MaxImages){e.Handled=true;OpenFilePicker();}
            }else if((e.KeyCode==Keys.Delete||e.KeyCode==Keys.Back)&&items.Count>0){
                e.Handled=true;RemoveAt(items.Count-1);
            }
        }

        protected override void OnGotFocus(EventArgs e){base.OnGotFocus(e);Invalidate();}
        protected override void OnLostFocus(EventArgs e){base.OnLostFocus(e);Invalidate();}
        protected override void OnChangeUICues(UICuesEventArgs e){base.OnChangeUICues(e);Invalidate();}

        internal void OpenFilePicker(){
            if(!Enabled||items.Count>=FeedbackImageEncoder.MaxImages)return;
            string[] chosen=null;
            if(browseFiles!=null){
                chosen=browseFiles();
            }else{
                using(var dialog=new OpenFileDialog {
                    Title="选择要附带的图片（最多 3 张）",
                    Filter="图片文件 (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff",
                    Multiselect=true,
                    CheckFileExists=true,
                }){
                    if(dialog.ShowDialog(FindForm())==DialogResult.OK)chosen=dialog.FileNames;
                }
            }
            if(chosen!=null&&chosen.Length>0){
                string error;
                TryAddFiles(chosen,out error);
            }
        }

        void ShowPreview(int index){
            if(index<0||index>=items.Count)return;
            var item=items[index];
            using(var bmp=FeedbackImageEncoder.CreatePreviewBitmap(item)){
                if(bmp==null)return;
                using(var dialog=new ThemedDialog(theme.Dark) {
                    Text="图片预览 · "+item.Name+" ("+item.Width+"×"+item.Height+", "+Math.Max(1,(item.ByteLength+1023)/1024)+" KB)",
                    ClientSize=new Size(640,460),
                    FormBorderStyle=FormBorderStyle.Sizable,
                }){
                    var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};
                    layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
                    layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
                    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    var card=new ThemedSection(dialog.Theme) {Dock=DockStyle.Fill};
                    var box=new PictureBox {Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,Image=bmp,BackColor=dialog.Theme.Card};
                    card.Controls.Add(box);
                    layout.Controls.Add(card,0,0);
                    var close=new ThemedButton(dialog.Theme,ThemedButtonKind.Primary) {Text="关闭",DialogResult=DialogResult.Cancel,AutoSize=true,Anchor=AnchorStyles.Right,Margin=Padding.Empty};
                    layout.Controls.Add(close,0,1);
                    dialog.Controls.Add(layout);
                    dialog.AcceptButton=close;dialog.CancelButton=close;
                    dialog.ShowDialog(FindForm());
                }
            }
        }

        void DisposeItems(){
            for(int i=0;i<items.Count;i++)items[i].Dispose();
            items.Clear();
        }

        protected override void Dispose(bool disposing){
            if(disposing)DisposeItems();
            base.Dispose(disposing);
        }
    }
}
