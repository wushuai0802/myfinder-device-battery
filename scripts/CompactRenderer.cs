using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using AtkBattery;

namespace AtkBatteryTray {
    public static class CompactRenderer {
        static Color Fg(bool dark){return PopupPages.Foreground(dark);}
        static Color Muted(bool dark){return PopupPages.Muted(dark);}
        static Color Track(bool dark){return PopupPages.Track(dark);}
        static Color Blue(bool dark){return PopupPages.Blue(dark);}
        public static void DrawSurface(Graphics g,RectangleF rect,bool dark) {
            using(var shape=VisualDesign.RoundRect(rect.X,rect.Y,rect.Width,rect.Height,16)) {
                using(var fill=new LinearGradientBrush(rect,dark?Color.FromArgb(48,53,61):Color.White,dark?Color.FromArgb(41,45,52):Color.FromArgb(250,251,253),110))g.FillPath(fill,shape);
                using(var pen=new Pen(dark?Color.FromArgb(75,83,94):Color.FromArgb(220,226,235),1))g.DrawPath(pen,shape);
            }
        }
        public static void DrawLabel(Graphics g,string text,RectangleF rect,float fontSize,bool bold,Color color,StringAlignment align) {
            string family="Segoe UI";foreach(char c in text??"")if(c>=0x2e80&&c<=0x9fff){family="Microsoft YaHei UI";break;}
            using(var font=new Font(family,fontSize,bold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel))
            using(var brush=new SolidBrush(color))
            using(var format=new StringFormat {Alignment=align,LineAlignment=StringAlignment.Center,Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap})g.DrawString(text??"",font,brush,rect,format);
        }
        public static void DrawSymbol(Graphics g,string name,RectangleF rect,Color color) {
            var state=g.Save();g.TranslateTransform(rect.X,rect.Y);g.ScaleTransform(rect.Width/20,rect.Height/20);
            using(var pen=new Pen(color,1.6f) {StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round}) {
                if(name=="mouse") {
                    using(var shape=VisualDesign.RoundRect(4,1.5f,12,17,5.5f))g.DrawPath(pen,shape);
                    g.DrawLine(pen,10,4.5f,10,8);g.DrawLine(pen,10,11,10,14);
                }else if(name=="headset") {
                    g.DrawArc(pen,3,2,14,14,180,180);
                    using(var left=VisualDesign.RoundRect(2.5f,9,4,8,1.5f))g.DrawPath(pen,left);
                    using(var right=VisualDesign.RoundRect(13.5f,9,4,8,1.5f))g.DrawPath(pen,right);
                }else if(name=="keyboard") {
                    using(var shape=VisualDesign.RoundRect(1,4,18,13,2.5f))g.DrawPath(pen,shape);
                    for(int row=0;row<2;row++)for(int column=0;column<4;column++)g.DrawLine(pen,4+column*4,7+row*3,4.5f+column*4,7+row*3);
                    g.DrawLine(pen,6,13,14,13);
                }else if(name=="chevron"||name=="back") {
                    g.DrawLines(pen,name=="back"?new[]{new PointF(12,4),new PointF(6,10),new PointF(12,16)}:new[]{new PointF(7,4),new PointF(13,10),new PointF(7,16)});
                }else if(name=="more") {
                    using(var brush=new SolidBrush(color))for(int i=0;i<3;i++)g.FillEllipse(brush,3+i*6,9,2.2f,2.2f);
                }else if(name=="refresh") {
                    g.DrawArc(pen,3,3,14,14,35,300);g.DrawLines(pen,new[]{new PointF(17,2),new PointF(17,7),new PointF(12,7)});
                }else if(name=="speaker") {
                    g.DrawPolygon(pen,new[]{new PointF(2,7),new PointF(6,7),new PointF(11,3),new PointF(11,17),new PointF(6,13),new PointF(2,13)});
                    g.DrawArc(pen,9,4,8,12,-65,130);g.DrawArc(pen,10,7,4,6,-70,140);
                }else if(name=="mic"||name=="mic-off") {
                    using(var shape=VisualDesign.RoundRect(7,2,6,10,3))g.DrawPath(pen,shape);
                    g.DrawArc(pen,4,5,12,11,0,180);g.DrawLine(pen,10,16,10,19);g.DrawLine(pen,6,19,14,19);
                    if(name=="mic-off")g.DrawLine(pen,2,2,18,18);
                }else if(name=="settings") {
                    g.DrawLine(pen,2,5,18,5);g.DrawLine(pen,2,10,18,10);g.DrawLine(pen,2,15,18,15);
                    g.DrawEllipse(pen,6,3,4,4);g.DrawEllipse(pen,12,8,4,4);g.DrawEllipse(pen,4,13,4,4);
                }else if(name=="power") {
                    g.DrawArc(pen,3,3,14,14,-50,280);g.DrawLine(pen,10,1,10,9);
                }else if(name=="check")g.DrawLines(pen,new[]{new PointF(3,10),new PointF(8,15),new PointF(17,5)});
                else if(name=="bolt")g.DrawPolygon(pen,new[]{new PointF(12,1),new PointF(5,11),new PointF(10,11),new PointF(8,19),new PointF(15,8),new PointF(10,8)});
                else {g.DrawEllipse(pen,2,2,16,16);g.DrawLine(pen,10,9,10,14);g.DrawEllipse(pen,9.5f,5.5f,1,1);}
            }
            g.Restore(state);
        }
        static void Fill(Graphics g,RectangleF rect,Color color,float radius) {
            using(var path=VisualDesign.RoundRect(rect.X,rect.Y,rect.Width,rect.Height,radius))using(var brush=new SolidBrush(color))g.FillPath(brush,path);
        }
        static void Outline(Graphics g,RectangleF rect,Color color,float width) {
            using(var path=VisualDesign.RoundRect(rect.X+.5f,rect.Y+.5f,rect.Width-1,rect.Height-1,7))using(var pen=new Pen(color,width))g.DrawPath(pen,path);
        }
        static void Highlight(Graphics g,RectangleF rect,int id,PanelFeedback feedback,bool dark) {
            if(feedback!=null&&(feedback.HoverAction==id||feedback.PressedAction==id))Fill(g,rect,Color.FromArgb(feedback.PressedAction==id?dark?30:19:dark?18:10,Fg(dark)),8);
        }
        static void Meter(Graphics g,BatteryView view,float top,bool dark,PanelFeedback feedback,int action) {
            Highlight(g,new RectangleF(168,top+6,82,44),action,feedback,dark);
            var ring=new RectangleF(171,top+15,25,25);Color level=view.Fresh?TrayGlyph.LevelColor(view.Percent,dark):TrayGlyph.LevelColor(null,dark);
            using(var track=new Pen(Track(dark),3.7f))g.DrawEllipse(track,ring);
            if(view.Percent.HasValue&&view.Percent.Value>0)using(var pen=new Pen(level,3.7f) {StartCap=LineCap.Round,EndCap=LineCap.Round}) {
                if(view.Percent==100)g.DrawEllipse(pen,ring);else g.DrawArc(pen,ring,-90,view.Percent.Value*3.6f);
            }
            if(view.Charging)DrawSymbol(g,"bolt",new RectangleF(179,top+22,9,12),Fg(dark));
            DrawLabel(g,view.Percent.HasValue?(view.Approximate?"≈":"")+view.Percent.Value.ToString():"--",new RectangleF(198,top+9,43,32),view.Approximate?16:18,true,Fg(dark),StringAlignment.Far);
            if(view.Percent.HasValue)DrawLabel(g,"%",new RectangleF(241,top+15,10,21),10,false,Fg(dark),StringAlignment.Near);
        }
        static void Header(Graphics g,string name,string subtitle,string icon,float top,bool dark,PanelFeedback feedback,int action) {
            Highlight(g,new RectangleF(10,top+6,150,44),action,feedback,dark);
            Fill(g,new RectangleF(11,top+13,22,27),Color.FromArgb(dark?9:5,Fg(dark)),7);
            DrawSymbol(g,icon,new RectangleF(15,top+17,14,19),Fg(dark));
            DrawLabel(g,name,new RectangleF(38,top+8,111,20),11.5f,true,Fg(dark),StringAlignment.Near);
            DrawSymbol(g,"chevron",new RectangleF(151,top+14,7,10),Muted(dark));
            DrawLabel(g,subtitle,new RectangleF(38,top+28,123,19),11,false,Muted(dark),StringAlignment.Near);
        }
        static void Selection(Graphics g,RectangleF rect,string label,string icon,bool selected,bool enabled,bool hovered,bool pressed,bool pending,bool dark) {
            if(selected)using(var path=VisualDesign.RoundRect(rect.X,rect.Y,rect.Width,rect.Height,7))using(var fill=new LinearGradientBrush(rect,dark?Color.FromArgb(50,139,250):Color.FromArgb(18,117,235),dark?Color.FromArgb(35,126,240):Color.FromArgb(8,101,214),90))g.FillPath(fill,path);
            else if(hovered||pressed)Fill(g,rect,Color.FromArgb(pressed?dark?27:40:dark?16:27,Fg(dark)),7);
            if(pending&&!selected)Outline(g,rect,Blue(dark),1.2f);
            Color color=selected?Color.White:enabled?Fg(dark):Muted(dark);
            if(!String.IsNullOrEmpty(icon)) {
                float width=label.Length*11+18;float x=rect.X+(rect.Width-width)/2;
                DrawSymbol(g,icon,new RectangleF(x,rect.Y+(rect.Height-13)/2,13,13),color);
                DrawLabel(g,label,new RectangleF(x+17,rect.Y,rect.Right-x-18,rect.Height),11,false,color,StringAlignment.Near);
            }else DrawLabel(g,label,rect,11.5f,selected,color,StringAlignment.Center);
        }
        public static void Paint(Graphics g,BatteryReading current,BatteryReading lastGood,string error,bool busy,DpiConfig dpi,string dpiError,bool dpiBusy,bool dark,HeadsetReading headset,AudioReading audio,float openProgress=1,int hoverStage=-1,bool hoverRefresh=false,float headsetProgress=1,int hoverAudio=-1,PanelFeedback feedback=null,bool hoverSettings=false) {
            if(g==null)return;
            g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
            var preferences=DeviceSettings.Current;var now=DateTime.UtcNow;var mouse=UiState.Mouse(current,lastGood,now);var ear=UiState.Headset(headset,now);
            int mouseHeight=VisualDesign.MouseCardHeight(dpi);bool visible=VisualDesign.ShouldShowHeadset(headset);float progress=visible?Math.Max(0,Math.Min(1,headsetProgress)):0;
            DrawSurface(g,new RectangleF(.5f,.5f,259,mouseHeight-1),dark);
            string dpiText="DPI未读取";
            if(dpi!=null&&dpi.Stages!=null&&dpi.ActiveStage>=0&&dpi.ActiveStage<dpi.Stages.Count)dpiText=(String.IsNullOrEmpty(dpiError)?"当前 ":"上次 ")+dpi.Stages[dpi.ActiveStage].XDpi+" DPI";
            string subtitle=mouse.Percent.HasValue&&!mouse.Fresh?"上次确认 "+mouse.TimeText:!mouse.Percent.HasValue?"等待设备回读":dpiText;
            Header(g,preferences.MouseAlias,subtitle,"mouse",0,dark,feedback,2);Meter(g,mouse,0,dark,feedback,4);
            int count=dpi!=null&&dpi.Stages!=null?dpi.Stages.Count:0;
            if(count>0) {
                var last=VisualDesign.StageRect(count-1,count);Fill(g,new RectangleF(10,53,240,last.Bottom-51),Track(dark),9);
                for(int i=0;i<count;i++) {
                    var rect=VisualDesign.StageRect(i,count);var value=dpi.Stages[i];bool selected=dpi.ActiveStage==i;bool hint=feedback!=null&&feedback.ShowPendingHints&&feedback.PendingStage==i;
                    if(value.XDpi==value.YDpi)Selection(g,rect,value.XDpi.ToString(),null,selected,true,hoverStage==i,feedback!=null&&feedback.PressedAction==10+i,hint,dark);
                    else {
                        Selection(g,rect,"",null,selected,true,hoverStage==i,feedback!=null&&feedback.PressedAction==10+i,hint,dark);
                        Color color=selected?Color.White:Fg(dark);DrawLabel(g,"X "+value.XDpi,new RectangleF(rect.X,rect.Y,rect.Width,rect.Height/2),10.5f,false,color,StringAlignment.Center);DrawLabel(g,"Y "+value.YDpi,new RectangleF(rect.X,rect.Y+rect.Height/2,rect.Width,rect.Height/2),10.5f,false,color,StringAlignment.Center);
                    }
                }
            }else{Fill(g,new RectangleF(11,55,238,30),Track(dark),9);DrawLabel(g,"等待DPI档位回读",new RectangleF(13,55,234,30),11,false,Muted(dark),StringAlignment.Center);}
            float top=mouseHeight+8;
            float scaleX,scaleY;
            using(var matrix=g.Transform){var transform=matrix.Elements;scaleX=(float)Math.Sqrt(transform[0]*transform[0]+transform[1]*transform[1]);scaleY=(float)Math.Sqrt(transform[2]*transform[2]+transform[3]*transform[3]);}
            if(progress>0)using(var layer=new Bitmap(Math.Max(1,(int)Math.Ceiling(260*scaleX)),Math.Max(1,(int)Math.Ceiling(96*scaleY)),PixelFormat.Format32bppPArgb)) {
                using(var drawing=Graphics.FromImage(layer)) {
                    drawing.ScaleTransform(scaleX,scaleY);
                    drawing.Clear(Color.Transparent);drawing.SmoothingMode=SmoothingMode.AntiAlias;drawing.PixelOffsetMode=PixelOffsetMode.HighQuality;drawing.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
                    DrawSurface(drawing,new RectangleF(.5f,.5f,259,95),dark);
                    Header(drawing,preferences.HeadsetAlias,ear.Percent.HasValue&&!ear.Fresh?"上次确认 "+ear.TimeText:ear.Percent.HasValue?"已连接":"电量未读取","headset",0,dark,feedback,3);Meter(drawing,ear,0,dark,feedback,5);
                    Fill(drawing,new RectangleF(9,53,178,34),Track(dark),9);
                    for(int i=0;i<2;i++) {
                        var rect=i==0?VisualDesign.SpeakerOutputRectFor(dpi):VisualDesign.HeadsetOutputRectFor(dpi);rect.Y-=top;
                        string target=i==0?"speaker":"headset";Selection(drawing,rect,i==0?"扬声器":"耳机",i==0?"speaker":"headset",audio!=null&&audio.ActiveOutput==target,audio!=null&&(i==0?audio.SpeakerAvailable:audio.HeadsetAvailable),hoverAudio==i,feedback!=null&&feedback.PressedAction==100+i,feedback!=null&&feedback.ShowPendingHints&&feedback.PendingOutput==target,dark);
                    }
                    var mic=VisualDesign.MicrophoneMuteRectFor(dpi);mic.Y-=top;bool muted=audio!=null&&audio.MicrophoneMuted==true;
                    Color red=TrayGlyph.LevelColor(10,dark);Fill(drawing,mic,muted?Color.FromArgb(dark?35:20,red):Track(dark),9);
                    DrawSymbol(drawing,muted?"mic-off":"mic",new RectangleF(mic.X+4,mic.Y+8,12,14),muted?red:Fg(dark));
                    DrawLabel(drawing,audio==null||!audio.MicrophoneMuted.HasValue?"未知":muted?"已静音":"未静音",new RectangleF(mic.X+17,mic.Y,mic.Width-19,mic.Height),10.5f,false,muted?red:Fg(dark),StringAlignment.Center);
                    if(hoverAudio==2||feedback!=null&&feedback.PressedAction==102)Outline(drawing,mic,muted?red:Muted(dark),.8f);
                }
                var saved=g.Save();g.SetClip(new RectangleF(0,top,260,Math.Max(0,104*progress-8)),CombineMode.Intersect);
                using(var attributes=new ImageAttributes()){var matrix=new ColorMatrix();matrix.Matrix33=progress;attributes.SetColorMatrix(matrix);g.DrawImage(layer,new Rectangle(0,(int)top,260,96),0,0,layer.Width,layer.Height,GraphicsUnit.Pixel,attributes);}g.Restore(saved);
            }
            float footer=mouseHeight+104*progress;string status;
            string failure=feedback==null?null:feedback.DpiFailure??feedback.AudioFailure;
            if(!String.IsNullOrEmpty(failure))status=failure;
            else if(feedback!=null&&feedback.ManualRefreshing)status="正在同步…";
            else if(feedback!=null&&feedback.ShowPendingHints&&feedback.PendingStage>=0)status="DPI等待确认…";
            else if(feedback!=null&&feedback.ShowPendingHints&&!String.IsNullOrEmpty(feedback.PendingOutput))status="音源等待确认…";
            else if(mouse.Percent.HasValue&&!mouse.Fresh)status="上次确认 "+mouse.TimeText;
            else if(visible&&ear.Percent.HasValue&&!ear.Fresh)status="耳机上次 "+ear.TimeText;
            else status=mouse.Fresh?"刚刚同步":"等待设备回读";
            Fill(g,new RectangleF(8,footer+3,208,24),dark?Color.FromArgb(225,41,45,52):Color.FromArgb(225,250,251,253),7);
            Highlight(g,new RectangleF(8,footer+3,208,24),0,feedback,dark);
            DrawSymbol(g,"refresh",new RectangleF(11,footer+10,12,12),Muted(dark));
            DrawLabel(g,status,new RectangleF(29,footer+3,184,25),11,false,String.IsNullOrEmpty(failure)?Muted(dark):TrayGlyph.LevelColor(10,dark),StringAlignment.Near);
            Fill(g,new RectangleF(228,footer+3,24,24),dark?Color.FromArgb(48,53,61):Color.FromArgb(246,248,251),7);Highlight(g,new RectangleF(228,footer+3,24,24),1,feedback,dark);
            DrawSymbol(g,"more",new RectangleF(232,footer+7,16,16),Fg(dark));
            if(feedback!=null&&feedback.KeyboardFocus>=0) {
                int id=feedback.KeyboardFocus;var rect=RectangleF.Empty;
                if(id==0)rect=new RectangleF(8,footer+3,208,24);else if(id==1)rect=new RectangleF(228,footer+3,24,24);
                else if(id==2)rect=VisualDesign.MouseNameRect;else if(id==4)rect=VisualDesign.MouseMeterRect;
                else if(id==3&&progress>.98f)rect=VisualDesign.HeadsetNameRectFor(dpi);else if(id==5&&progress>.98f)rect=VisualDesign.HeadsetMeterRectFor(dpi);
                else if(id>=10&&id<10+count)rect=VisualDesign.StageRect(id-10,count);
                else if(id>=100&&id<=102&&progress>.98f)rect=id==100?VisualDesign.SpeakerOutputRectFor(dpi):id==101?VisualDesign.HeadsetOutputRectFor(dpi):VisualDesign.MicrophoneMuteRectFor(dpi);
                if(!rect.IsEmpty)Outline(g,rect,Blue(dark),1.1f);
            }
        }
        internal static void PaintWireless(Graphics g,TrayContext owner,WirelessLayout layout,bool dark,PanelFeedback feedback) {
            g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
            foreach(var card in layout.Cards) {
                var saved=g.Save();
                g.SetClip(new RectangleF(0,card.Top,260,card.Height),CombineMode.Intersect);
                if(card.NativeMouse) {
                    g.TranslateTransform(0,card.Top);
                    Paint(g,owner.Current,owner.LastGood,owner.ErrorMessage,owner.IsRefreshing,owner.Dpi,owner.DpiError,owner.IsDpiRefreshing,dark,null,owner.Audio,1,feedback.HoverAction>=10&&feedback.HoverAction<18?feedback.HoverAction-10:-1,false,0,-1,feedback);
                }else if(card.NativeHeadset) {
                    g.TranslateTransform(0,card.Top-VisualDesign.MouseCardHeight(owner.Dpi)-8);
                    Paint(g,owner.Current,owner.LastGood,owner.ErrorMessage,owner.IsRefreshing,owner.Dpi,owner.DpiError,owner.IsDpiRefreshing,dark,owner.Headset,owner.Audio,1,-1,false,1,feedback.HoverAction>=100&&feedback.HoverAction<=102?feedback.HoverAction-100:-1,feedback);
                }else {
                    int index=layout.All.IndexOf(card);
                    DrawSurface(g,new RectangleF(.5f,card.Top+.5f,259,59),dark);
                    string subtitle=card.Subtitle;
                    if(subtitle==null)subtitle=card.Battery.Percent.HasValue&&!card.Battery.Fresh?"上次确认 "+card.Battery.TimeText:card.Battery.Fresh?card.Device.Transport+" · 已连接":card.Device.Online?"电量暂未读取":"设备未连接";
                    string icon=card.Kind=="keyboard"?"keyboard":card.Kind=="headset"?"headset":card.Kind=="mouse"?"mouse":"info";
                    Header(g,card.Name,subtitle,icon,card.Top,dark,feedback,1000+index);Meter(g,card.Battery,card.Top,dark,feedback,2000+index);
                    if(feedback.KeyboardFocus==1000+index)Outline(g,new RectangleF(10,card.Top+6,150,44),Blue(dark),1);
                    if(feedback.KeyboardFocus==2000+index)Outline(g,new RectangleF(168,card.Top+6,84,44),Blue(dark),1);
                }
                g.Restore(saved);
            }
            float top=layout.Footer;float width=layout.Paged?146:208;
            Fill(g,new RectangleF(8,top+3,width,24),dark?Color.FromArgb(225,41,45,52):Color.FromArgb(225,250,251,253),7);
            string status=feedback.ManualRefreshing?"正在同步…":layout.All[0].Id=="empty"?owner.GenericScanning?"正在识别设备…":"暂无可读取电量的设备":layout.Paged?(layout.Start+1)+"–"+layout.End+" / "+layout.All.Count+" 台设备":"已识别 "+layout.All.Count+" 台设备";
            var failure=feedback.DpiFailure??feedback.AudioFailure;
            DrawSymbol(g,"refresh",new RectangleF(11,top+10,12,12),Muted(dark));
            DrawLabel(g,failure??status,new RectangleF(29,top+3,width-25,25),11,false,String.IsNullOrEmpty(failure)?Muted(dark):TrayGlyph.LevelColor(10,dark),StringAlignment.Near);
            if(layout.Paged) {
                DrawSymbol(g,"back",new RectangleF(166,top+8,14,14),layout.Start>0?Fg(dark):Muted(dark));
                DrawSymbol(g,"chevron",new RectangleF(198,top+8,14,14),layout.End<layout.All.Count?Fg(dark):Muted(dark));
            }
            Fill(g,new RectangleF(228,top+3,24,24),dark?Color.FromArgb(48,53,61):Color.FromArgb(246,248,251),7);DrawSymbol(g,"more",new RectangleF(232,top+7,16,16),Fg(dark));
            if(feedback.KeyboardFocus==0)Outline(g,new RectangleF(8,top+3,width,24),Blue(dark),1);
            if(feedback.KeyboardFocus==1)Outline(g,new RectangleF(226,top+1,28,28),Blue(dark),1);
        }
    }
}
