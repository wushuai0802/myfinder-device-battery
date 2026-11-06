using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using AtkBattery;

namespace AtkBatteryTray {
    internal sealed class DrawnLabel {
        internal string Text,Family;
        internal RectangleF LogicalRect,DeviceRect;
        internal float FontSize;
        internal bool Bold;
        internal StringAlignment Alignment;
    }
    public static class CompactRenderer {
        internal static Action<RectangleF> InteractionOutlineObserver=null;
        internal static Action<DrawnLabel> LabelObserver=null;
        static Color Fg(bool dark){return PopupPages.Foreground(dark);}
        static Color Muted(bool dark){return PopupPages.Muted(dark);}
        static Color Track(bool dark){return PopupPages.Track(dark);}
        static Color Blue(bool dark){return PopupPages.Blue(dark);}
        public static void DrawSurface(Graphics g,RectangleF rect,bool dark,BatteryView battery=null) {
            var palette=ThemePalette.Current(dark);bool low=ThemePalette.IsLow(battery,DeviceSettings.Current.WarningThreshold)&&!VisualDesign.HighContrast;
            using(var shape=VisualDesign.RoundRect(rect.X,rect.Y,rect.Width,rect.Height,VisualDesign.PanelRadius)) {
                using(var fill=new SolidBrush(palette.Card))g.FillPath(fill,shape);
                if(low)using(var tint=new SolidBrush(ThemePalette.WarningOverlay(dark)))g.FillPath(tint,shape);
                using(var pen=new Pen(low?palette.WarningBorder:palette.Border,1))g.DrawPath(pen,shape);
            }
        }
        public static void DrawLabel(Graphics g,string text,RectangleF rect,float fontSize,bool bold,Color color,StringAlignment align,StringTrimming trimming=StringTrimming.EllipsisCharacter) {
            rect=VisualDesign.SnapTextRect(rect);fontSize=VisualDesign.SnapFontSize(fontSize);
            if(InteractionLayout.Measuring){InteractionLayout.Include(InteractionLayout.LabelBounds(g,text,rect,fontSize,bold,align,trimming));return;}
            string family="Segoe UI";foreach(char c in text??"")if(c>=0x2e80&&c<=0x9fff){family="Microsoft YaHei UI";break;}
            if(LabelObserver!=null) {
                float sx,sy,tx,ty;using(var matrix=g.Transform){var m=matrix.Elements;sx=m[0];sy=m[3];tx=m[4];ty=m[5];}
                LabelObserver(new DrawnLabel {Text=text??"",Family=family,LogicalRect=rect,DeviceRect=new RectangleF(rect.X*sx+tx,rect.Y*sy+ty,rect.Width*sx,rect.Height*sy),FontSize=fontSize,Bold=bold,Alignment=align});
            }
            var previousOffset=g.PixelOffsetMode;var previousHint=g.TextRenderingHint;
            g.PixelOffsetMode=PixelOffsetMode.Default;
            if(previousHint==TextRenderingHint.SystemDefault)g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
            try {
            using(var font=new Font(family,fontSize,bold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel))
            using(var brush=new SolidBrush(color))
            using(var format=new StringFormat {Alignment=align,LineAlignment=StringAlignment.Center,Trimming=trimming,FormatFlags=StringFormatFlags.NoWrap})g.DrawString(text??"",font,brush,rect,format);
            } finally {g.PixelOffsetMode=previousOffset;g.TextRenderingHint=previousHint;}
        }
        internal static void DrawWrappedLabel(Graphics g,string text,RectangleF rect,float fontSize,Color color) {
            rect=VisualDesign.SnapTextRect(rect);fontSize=VisualDesign.SnapFontSize(fontSize);
            if(InteractionLayout.Measuring){InteractionLayout.Include(InteractionLayout.LabelBounds(g,text,rect,fontSize,false,StringAlignment.Near,StringTrimming.None,true));return;}
            if(LabelObserver!=null) {
                float sx,sy,tx,ty;using(var matrix=g.Transform){var m=matrix.Elements;sx=m[0];sy=m[3];tx=m[4];ty=m[5];}
                LabelObserver(new DrawnLabel {Text=text??"",Family="Microsoft YaHei UI",LogicalRect=rect,DeviceRect=new RectangleF(rect.X*sx+tx,rect.Y*sy+ty,rect.Width*sx,rect.Height*sy),FontSize=fontSize,Bold=false,Alignment=StringAlignment.Near});
            }
            var previousOffset=g.PixelOffsetMode;var previousHint=g.TextRenderingHint;
            g.PixelOffsetMode=PixelOffsetMode.Default;
            if(previousHint==TextRenderingHint.SystemDefault)g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
            try {
                using(var font=new Font("Microsoft YaHei UI",fontSize,FontStyle.Regular,GraphicsUnit.Pixel))
                using(var brush=new SolidBrush(color))
                using(var format=new StringFormat {LineAlignment=StringAlignment.Center})g.DrawString(text??"",font,brush,rect,format);
            } finally {g.PixelOffsetMode=previousOffset;g.TextRenderingHint=previousHint;}
        }
        internal static float BatteryTextSize(Graphics g,string text,float preferredSize,float width){
            var previousOffset=g.PixelOffsetMode;g.PixelOffsetMode=PixelOffsetMode.Default;
            try{
            using(var format=new StringFormat {FormatFlags=StringFormatFlags.NoWrap}){
                for(float size=VisualDesign.SnapFontSize(preferredSize);size>=1;size-=1f)using(var font=new Font("Segoe UI",size,FontStyle.Bold,GraphicsUnit.Pixel)){
                    // Measure with DrawLabel's font and GDI+ padding, in logical
                    // units so DPI transforms leave the ring and percent fixed.
                    if(g.MeasureString(text,font,Int32.MaxValue,format).Width<=width)return size;
                }
            }
            return 1;
            }finally{g.PixelOffsetMode=previousOffset;}
        }
        public static void DrawSymbol(Graphics g,string name,RectangleF rect,Color color) {
            if(InteractionLayout.Measuring){InteractionLayout.Include(InteractionLayout.SymbolBounds(name,rect));return;}
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
                }else if(name=="gamepad") {
                    using(var shape=new GraphicsPath()) {
                        shape.AddBezier(6,5,3.5f,5,3,7,2.5f,9);
                        shape.AddBezier(2.5f,9,1,14,1.5f,16,3.5f,16);
                        shape.AddBezier(3.5f,16,5,16,6,13,7.5f,13);
                        shape.AddLine(7.5f,13,12.5f,13);
                        shape.AddBezier(12.5f,13,14,13,15,16,16.5f,16);
                        shape.AddBezier(16.5f,16,18.5f,16,19,14,17.5f,9);
                        shape.AddBezier(17.5f,9,17,7,16.5f,5,14,5);
                        shape.AddBezier(14,5,11,5.8f,9,5.8f,6,5);
                        shape.CloseFigure();g.DrawPath(pen,shape);
                    }
                    g.DrawLine(pen,4.5f,9,8.5f,9);g.DrawLine(pen,6.5f,7,6.5f,11);
                    g.DrawEllipse(pen,12,9,1.4f,1.4f);g.DrawEllipse(pen,14.5f,7,1.4f,1.4f);
                }else if(name=="chevron"||name=="back") {
                    g.DrawLines(pen,name=="back"?new[]{new PointF(12,4),new PointF(6,10),new PointF(12,16)}:new[]{new PointF(7,4),new PointF(13,10),new PointF(7,16)});
                }else if(name=="collapse"||name=="expand") {
                    g.DrawLines(pen,name=="collapse"?new[]{new PointF(4,12),new PointF(10,6),new PointF(16,12)}:new[]{new PointF(4,7),new PointF(10,13),new PointF(16,7)});
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
        internal static void Fill(Graphics g,RectangleF rect,Color color,float radius) {
            if(InteractionLayout.Measuring){InteractionLayout.Include(rect);return;}
            using(var path=VisualDesign.RoundRect(rect.X,rect.Y,rect.Width,rect.Height,radius))using(var brush=new SolidBrush(color))g.FillPath(brush,path);
        }
        internal static void Outline(Graphics g,RectangleF rect,Color color,float width) {
            if(InteractionOutlineObserver!=null&&g.IsVisible(rect))InteractionOutlineObserver(rect);
            using(var path=VisualDesign.RoundRect(rect.X+.5f,rect.Y+.5f,rect.Width-1,rect.Height-1,VisualDesign.SelectionRadius))using(var pen=new Pen(color,width))g.DrawPath(pen,path);
        }
        internal static void Highlight(Graphics g,RectangleF rect,int id,PanelFeedback feedback,bool dark,float radius=8) {
            if(id<0||feedback==null)return;
            if(feedback!=null&&(feedback.HoverAction==id||feedback.PressedAction==id)) {
                if(VisualDesign.HighContrast)FocusRing(g,rect,Blue(dark),radius,feedback.PressedAction==id?2:1);
                else Fill(g,rect,ThemePalette.Current(dark).Interaction(feedback.PressedAction==id),radius);
            }
            if(feedback!=null&&feedback.KeyboardFocus==id)FocusRing(g,rect,Blue(dark),radius,1);
        }
        internal static void FocusRing(Graphics g,RectangleF rect,Color color,float radius,float width=1){
            if(InteractionOutlineObserver!=null&&g.IsVisible(rect))InteractionOutlineObserver(rect);
            float inset=width/2;using(var path=VisualDesign.RoundRect(rect.X+inset,rect.Y+inset,rect.Width-width,rect.Height-width,Math.Max(0,radius-inset)))using(var pen=new Pen(color,width))g.DrawPath(pen,path);
        }
        static void Meter(Graphics g,BatteryView view,float top,float height,bool dark,PanelFeedback feedback,int action) {
            int width=VisualDesign.PanelWidth,meterLeft=VisualDesign.LargeFont?194:164;
            InteractionLayout.Paint(g,new RectangleF(.5f,top+.5f,width-1,height-1),new RectangleF(meterLeft,top,width-meterLeft,VisualDesign.HeaderHeight),action,feedback,dark,false,delegate{
            var ring=VisualDesign.BatteryRingRect(top);Color level=ThemePalette.LevelColor(view.Fresh?view.Percent:null,dark);
            var ink=ring;ink.Inflate(VisualDesign.BatteryRingWidth/2,VisualDesign.BatteryRingWidth/2);InteractionLayout.Include(ink);
            using(var track=new Pen(Track(dark),VisualDesign.BatteryRingWidth))g.DrawEllipse(track,ring);
            if(view.Percent.HasValue&&view.Percent.Value>0)using(var pen=new Pen(level,VisualDesign.BatteryRingWidth) {StartCap=LineCap.Round,EndCap=LineCap.Round}) {
                if(view.Percent==100)g.DrawEllipse(pen,ring);else g.DrawArc(pen,ring,-90,view.Percent.Value*3.6f);
            }
            if(view.Charging)DrawSymbol(g,"bolt",new RectangleF(ring.X+9.5f,ring.Y+8,9,12),Fg(dark));
            Color digit=ThemePalette.IsLow(view,DeviceSettings.Current.WarningThreshold)?ThemePalette.LevelColor(0,dark):view.Fresh?Fg(dark):Muted(dark);
            string text=view.Percent.HasValue?(view.Approximate?"≈":"")+view.Percent.Value.ToString():"--";
            var digits=VisualDesign.BatteryDigitsRect(top);
            DrawLabel(g,text,digits,BatteryTextSize(g,text,view.Approximate?VisualDesign.BatteryApproximateDigitSize:VisualDesign.BatteryDigitSize,digits.Width),true,digit,StringAlignment.Far,StringTrimming.None);
            if(view.Percent.HasValue)DrawLabel(g,"%",VisualDesign.BatteryPercentRect(top),VisualDesign.SubtitleFontSize,false,digit,StringAlignment.Near);
            if(view.Percent.HasValue&&view.Unconfirmed)DrawLabel(g,"待确认",new RectangleF(meterLeft,top+(VisualDesign.LargeFont?44:38),width-meterLeft-8,VisualDesign.LargeFont?19:17),VisualDesign.SmallFontSize,false,Muted(dark),StringAlignment.Far);
            });
        }
        static void Header(Graphics g,string name,string subtitle,string icon,float top,float height,bool dark,PanelFeedback feedback,int action) {
            int width=VisualDesign.PanelWidth,headerWidth=VisualDesign.LargeFont?194:164;
            InteractionLayout.Paint(g,new RectangleF(.5f,top+.5f,width-1,height-1),new RectangleF(0,top,headerWidth,VisualDesign.HeaderHeight),action,feedback,dark,false,delegate{
            Fill(g,new RectangleF(11,top+(VisualDesign.LargeFont?15:13),22,27),ThemePalette.Current(dark).IconFill,7);
            DrawSymbol(g,icon,new RectangleF(15,top+(VisualDesign.LargeFont?19:17),14,19),Fg(dark));
            bool reveal=feedback!=null&&(feedback.HoverAction==action||feedback.KeyboardFocus==action);
            DrawLabel(g,name,new RectangleF(38,top+8,reveal?(headerWidth-53):(headerWidth-39),VisualDesign.LargeFont?22:20),VisualDesign.DeviceNameFontSize,true,Fg(dark),StringAlignment.Near);
            if(reveal)DrawSymbol(g,"chevron",new RectangleF(headerWidth-13,top+(VisualDesign.LargeFont?15:14),7,10),Muted(dark));
            DrawLabel(g,subtitle,new RectangleF(38,top+(VisualDesign.LargeFont?32:28),headerWidth-41,VisualDesign.LargeFont?21:19),VisualDesign.SubtitleFontSize,false,Muted(dark),StringAlignment.Near);
            });
        }
        internal static void Selection(Graphics g,RectangleF rect,string label,string icon,bool selected,bool enabled,bool hovered,bool pressed,bool pending,bool dark,bool focused=false,RectangleF? card=null,int id=0,Action extra=null) {
            if(selected&&!enabled)Fill(g,rect,Track(dark),VisualDesign.SelectionRadius);
            else if(selected)Fill(g,rect,ThemePalette.Current(dark).Selected,VisualDesign.SelectionRadius);
            if(enabled&&pending&&!selected)Outline(g,rect,Blue(dark),1.2f);
            var container=card??RectangleF.FromLTRB(rect.Left-8,rect.Top-8,rect.Right+8,rect.Bottom+8);
            InteractionLayout.Paint(g,container,rect,id,enabled?new PanelFeedback {HoverAction=hovered?id:-1,PressedAction=pressed?id:-1,KeyboardFocus=focused?id:-1}:null,dark,selected,delegate{
            Color color=!enabled?Muted(dark):selected||VisualDesign.HighContrast&&(hovered||pressed)?PopupPages.SelectedForeground(dark):Fg(dark);
            if(!String.IsNullOrEmpty(icon)) {
                float fontSize=VisualDesign.SmallFontSize,width=label.Length*fontSize+18;float x=(float)Math.Floor(rect.X+(rect.Width-width)/2);
                DrawSymbol(g,icon,new RectangleF(x,(float)Math.Floor(rect.Y+(rect.Height-13)/2),13,13),color);
                DrawLabel(g,label,new RectangleF(x+17,rect.Y,rect.Right-x-18,rect.Height),fontSize,false,color,StringAlignment.Near);
            }else if(extra!=null)extra();else DrawLabel(g,label,rect,VisualDesign.DeviceNameFontSize,selected,color,StringAlignment.Center);
            });
        }
        internal static void DrawVolumeTrack(Graphics g,RectangleF meter,bool dark,float fraction,float start=0,Color? fill=null) {
            Fill(g,meter,Track(dark),VisualDesign.VolumeTrackRadius);
            float width=meter.Width*Math.Max(0,Math.Min(1,fraction));
            if(width>0)Fill(g,new RectangleF(meter.X+meter.Width*start,meter.Y,width,meter.Height),fill??Blue(dark),Math.Min(VisualDesign.VolumeTrackRadius,width/2));
        }
        internal static void SystemVolume(Graphics g,RectangleF rect,AudioReading audio,bool dark,PanelFeedback feedback) {
            bool collapsed=DeviceSettings.Current.VolumeCollapsed;
            bool blocked=feedback!=null&&feedback.AudioBlocked;
            bool enabled=!blocked&&audio!=null&&audio.OutputVolumePercent.HasValue&&!String.IsNullOrEmpty(audio.VolumeEndpointId);
            int preview=feedback==null?-1:feedback.VolumePreview;
            int? value=enabled?(int?)(preview>=0?Math.Max(0,Math.Min(100,preview)):audio.OutputVolumePercent.Value):null;
            var surface=new RectangleF(rect.X+.5f,rect.Y+.5f,rect.Width-1,rect.Height-1);
            DrawSurface(g,surface,dark);
            var toggle=VisualDesign.VolumeToggleRect(rect,collapsed);
            InteractionLayout.Paint(g,surface,new RectangleF(toggle.X,rect.Y,toggle.Width,rect.Height),241,feedback,dark,false,delegate{DrawSymbol(g,collapsed?"expand":"collapse",VisualDesign.VolumeChevronRect(rect,collapsed),Fg(dark));});
            if(collapsed) {
                DrawSymbol(g,"speaker",new RectangleF(rect.X+11,rect.Y+(float)Math.Floor((rect.Height-16)/2),16,16),enabled?Fg(dark):Muted(dark));
                DrawLabel(g,value.HasValue?value+"%":"音量未确认",new RectangleF(rect.X+36,rect.Y+3,rect.Width-74,rect.Height-6),VisualDesign.SubtitleFontSize,false,enabled?Fg(dark):Muted(dark),StringAlignment.Near);
                return;
            }
            var slider=VisualDesign.VolumeSliderRect(rect);
            InteractionLayout.Paint(g,surface,slider,240,enabled?feedback:null,dark,false,delegate{
            string endpoint=enabled?UiState.CurrentOutputName(audio):blocked?"待确认":"不可用";
            string label="系统音量 · "+(enabled&&preview>=0?"待应用 · ":"")+endpoint;
            int valueWidth=VisualDesign.LargeFont?46:38,labelHeight=VisualDesign.LargeFont?24:22;
            DrawLabel(g,label,new RectangleF(slider.X+11,slider.Y+(VisualDesign.LargeFont?6:5),slider.Width-(valueWidth+26),labelHeight),VisualDesign.SubtitleFontSize,false,enabled?Fg(dark):Muted(dark),StringAlignment.Near);
            DrawLabel(g,value.HasValue?value+"%":"--",new RectangleF(slider.Right-(valueWidth+11),slider.Y+(VisualDesign.LargeFont?6:5),valueWidth,labelHeight),VisualDesign.SubtitleFontSize,false,enabled?Fg(dark):Muted(dark),StringAlignment.Far);
            var meter=PopupPages.VolumeTrack(slider);DrawVolumeTrack(g,meter,dark,value.GetValueOrDefault()/100f);
            if(value.HasValue) {
                float width=meter.Width*value.Value/100f;
                float x=meter.X+width;
                float diameter=feedback!=null&&(feedback.HoverAction==240||feedback.PressedAction==240)?14:12;
                var knob=new RectangleF(x-diameter/2,meter.Y+meter.Height/2-diameter/2,diameter,diameter);
                var ink=knob;ink.Inflate(.5f,.5f);InteractionLayout.Include(ink);
                using(var brush=new SolidBrush(ThemePalette.Current(dark).Knob))g.FillEllipse(brush,knob);
                using(var pen=new Pen(Blue(dark),1))g.DrawEllipse(pen,knob);
            }
            });
        }
        public static void Paint(Graphics g,BatteryReading current,BatteryReading lastGood,string error,bool busy,DpiConfig dpi,string dpiError,bool dpiBusy,bool dark,HeadsetReading headset,AudioReading audio,float openProgress=1,int hoverStage=-1,bool hoverRefresh=false,float headsetProgress=1,int hoverAudio=-1,PanelFeedback feedback=null,bool hoverSettings=false) {
            if(g==null)return;
            g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
            var preferences=DeviceSettings.Current;var now=DateTime.UtcNow;var mouse=UiState.Mouse(current,lastGood,now);var ear=UiState.Headset(headset,now);
            var audioCard=VisualDesign.AudioCardFor(headset,audio);
            int panelWidth=VisualDesign.PanelWidth,headsetHeight=VisualDesign.HeadsetCardHeight,stageTop=VisualDesign.LargeFont?61:53;
            int mouseHeight=VisualDesign.MouseCardHeight(dpi);bool visible=VisualDesign.ShouldShowHeadset(headset)||audioCard!=null;float progress=visible?Math.Max(0,Math.Min(1,headsetProgress)):0;
            DrawSurface(g,new RectangleF(.5f,.5f,panelWidth-1,mouseHeight-1),dark,mouse);
            string dpiText="DPI未读取";
            if(dpi!=null&&dpi.Stages!=null&&dpi.ActiveStage>=0&&dpi.ActiveStage<dpi.Stages.Count)dpiText=(String.IsNullOrEmpty(dpiError)?"当前 ":"上次 ")+dpi.Stages[dpi.ActiveStage].XDpi+" DPI";
            string subtitle=mouse.Percent.HasValue&&!mouse.Fresh?"上次确认 "+mouse.TimeText:!mouse.Percent.HasValue?"等待设备回读":dpiText;
            Header(g,preferences.MouseAlias,subtitle,"mouse",0,mouseHeight,dark,feedback,2);Meter(g,mouse,0,mouseHeight,dark,feedback,4);
            int count=dpi!=null&&dpi.Stages!=null?dpi.Stages.Count:0;
            if(count>0) {
                var last=VisualDesign.StageRect(count-1,count);Fill(g,new RectangleF(10,stageTop,panelWidth-20,last.Bottom-(stageTop-2)),Track(dark),9);
                for(int i=0;i<count;i++) {
                    var rect=VisualDesign.StageRect(i,count);var value=dpi.Stages[i];bool selected=dpi.ActiveStage==i;bool hint=feedback!=null&&feedback.ShowPendingHints&&feedback.PendingStage==i;
                    var card=new RectangleF(.5f,.5f,panelWidth-1,mouseHeight-1);bool focused=feedback!=null&&feedback.KeyboardFocus==10+i;
                    if(value.XDpi==value.YDpi)Selection(g,rect,value.XDpi.ToString(),null,selected,true,hoverStage==i||feedback!=null&&feedback.HoverAction==10+i,feedback!=null&&feedback.PressedAction==10+i,hint,dark,focused,card,10+i);
                    else {
                        Selection(g,rect,"",null,selected,true,hoverStage==i||feedback!=null&&feedback.HoverAction==10+i,feedback!=null&&feedback.PressedAction==10+i,hint,dark,focused,card,10+i,delegate{
                            Color color=selected?PopupPages.SelectedForeground(dark):Fg(dark);DrawLabel(g,"X "+value.XDpi,new RectangleF(rect.X,rect.Y,rect.Width,rect.Height/2),VisualDesign.SmallFontSize,false,color,StringAlignment.Center);DrawLabel(g,"Y "+value.YDpi,new RectangleF(rect.X,rect.Y+rect.Height/2,rect.Width,rect.Height/2),VisualDesign.SmallFontSize,false,color,StringAlignment.Center);
                        });
                    }
                }
            }else{Fill(g,new RectangleF(11,stageTop+2,panelWidth-22,VisualDesign.SelectionHeight),Track(dark),9);DrawLabel(g,"等待DPI档位回读",new RectangleF(13,stageTop+2,panelWidth-26,VisualDesign.SelectionHeight),VisualDesign.SubtitleFontSize,false,Muted(dark),StringAlignment.Center);}
            float top=mouseHeight+8;
            float scaleX,scaleY;
            using(var matrix=g.Transform){var transform=matrix.Elements;scaleX=(float)Math.Sqrt(transform[0]*transform[0]+transform[1]*transform[1]);scaleY=(float)Math.Sqrt(transform[2]*transform[2]+transform[3]*transform[3]);}
            if(progress>0)using(var layer=new Bitmap(Math.Max(1,(int)Math.Ceiling(panelWidth*scaleX)),Math.Max(1,(int)Math.Ceiling(headsetHeight*scaleY)),PixelFormat.Format32bppPArgb)) {
                using(var drawing=Graphics.FromImage(layer)) {
                    drawing.ScaleTransform(scaleX,scaleY);
                    drawing.Clear(Color.Transparent);drawing.SmoothingMode=SmoothingMode.AntiAlias;drawing.PixelOffsetMode=PixelOffsetMode.HighQuality;drawing.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
                    DrawSurface(drawing,new RectangleF(.5f,.5f,panelWidth-1,headsetHeight-1),dark,audioCard==null?ear:null);
                    if(audioCard==null){Header(drawing,preferences.HeadsetAlias,ear.Percent.HasValue&&!ear.Fresh?"上次确认 "+ear.TimeText:ear.Percent.HasValue?"已连接":"电量未读取","headset",0,headsetHeight,dark,feedback,3);Meter(drawing,ear,0,headsetHeight,dark,feedback,5);}
                    else {
                        InteractionLayout.Paint(drawing,new RectangleF(.5f,.5f,panelWidth-1,headsetHeight-1),new RectangleF(0,0,panelWidth,VisualDesign.LargeFont?60:52),200,feedback,dark,false,delegate{
                        DrawSymbol(drawing,"headset",new RectangleF(15,VisualDesign.LargeFont?19:17,16,19),Fg(dark));
                        bool reveal=feedback!=null&&(feedback.HoverAction==200||feedback.KeyboardFocus==200);
                        DrawLabel(drawing,"耳机",new RectangleF(38,8,reveal?(panelWidth-58):(panelWidth-49),VisualDesign.LargeFont?22:20),VisualDesign.DeviceNameFontSize,true,Fg(dark),StringAlignment.Near);
                        DrawLabel(drawing,audioCard.Subtitle,new RectangleF(38,VisualDesign.LargeFont?32:28,panelWidth-58,VisualDesign.LargeFont?21:19),VisualDesign.SubtitleFontSize,false,Muted(dark),StringAlignment.Near);
                        if(reveal)DrawSymbol(drawing,"chevron",new RectangleF(panelWidth-18,VisualDesign.LargeFont?15:14,7,10),Muted(dark));
                        });
                    }
                    Fill(drawing,new RectangleF(9,stageTop,VisualDesign.LargeFont?206:178,VisualDesign.SelectionHeight+4),Track(dark),9);
                    for(int i=0;i<2;i++) {
                        var rect=i==0?VisualDesign.SpeakerOutputRectFor(dpi):VisualDesign.HeadsetOutputRectFor(dpi);rect.Y-=top;
                        string target=i==0?"speaker":"headset";Selection(drawing,rect,i==0?"扬声器":"耳机",i==0?"speaker":"headset",audio!=null&&audio.ActiveOutput==target,(feedback==null||!feedback.AudioBlocked)&&audio!=null&&(i==0?audio.SpeakerAvailable:audio.HeadsetAvailable&&(audioCard==null||audioCard.HasTarget)),hoverAudio==i||feedback!=null&&feedback.HoverAction==100+i,feedback!=null&&feedback.PressedAction==100+i,feedback!=null&&feedback.ShowPendingHints&&feedback.PendingOutput==target,dark,feedback!=null&&feedback.KeyboardFocus==100+i,new RectangleF(.5f,.5f,panelWidth-1,headsetHeight-1),100+i);
                    }
                    var mic=VisualDesign.MicrophoneMuteRectFor(dpi);mic.Y-=top;bool muted=audio!=null&&audio.MicrophoneMuted==true;
                    bool blocked=feedback!=null&&feedback.AudioBlocked;Color red=ThemePalette.LevelColor(10,dark);Fill(drawing,mic,!blocked&&muted?ThemePalette.Current(dark).MutedMicrophoneFill:Track(dark),7);
                    var micFeedback=blocked||audio==null||!audio.MicrophoneAvailable?null:new PanelFeedback {HoverAction=hoverAudio==2||feedback!=null&&feedback.HoverAction==102?102:-1,PressedAction=feedback==null?-1:feedback.PressedAction,KeyboardFocus=feedback==null?-1:feedback.KeyboardFocus};
                    InteractionLayout.Paint(drawing,new RectangleF(.5f,.5f,panelWidth-1,headsetHeight-1),mic,102,micFeedback,dark,false,delegate{
                    DrawSymbol(drawing,muted?"mic-off":"mic",new RectangleF(mic.X+4,mic.Y+(float)Math.Floor((mic.Height-14)/2),12,14),blocked?Muted(dark):muted?red:Fg(dark));
                    DrawLabel(drawing,blocked?"待确认":audio==null||!audio.MicrophoneMuted.HasValue?"未知":muted?"已静音":"未静音",new RectangleF(mic.X+17,mic.Y,mic.Width-19,mic.Height),VisualDesign.SmallFontSize,false,blocked?Muted(dark):muted?red:Fg(dark),StringAlignment.Center);
                    });
                }
                var saved=g.Save();g.SetClip(new RectangleF(0,top,panelWidth,Math.Max(0,VisualDesign.SecondCardStep*progress-8)),CombineMode.Intersect);
                using(var attributes=new ImageAttributes()){var matrix=new ColorMatrix();matrix.Matrix33=progress;attributes.SetColorMatrix(matrix);g.DrawImage(layer,new Rectangle(0,(int)top,panelWidth,headsetHeight),0,0,layer.Width,layer.Height,GraphicsUnit.Pixel,attributes);}g.Restore(saved);
            }
            var volume=new RectangleF(0,mouseHeight+VisualDesign.SecondCardStep*progress+VisualDesign.CardGap,panelWidth,VisualDesign.CurrentSystemVolumeHeight);
            SystemVolume(g,volume,audio,dark,feedback);
            float footer=volume.Bottom;string status;
            string failure=feedback==null?null:feedback.DpiFailure??feedback.AudioFailure;
            if(!String.IsNullOrEmpty(failure))status=failure;
            else if(feedback!=null&&feedback.ManualRefreshing)status="正在同步…";
            else if(feedback!=null&&feedback.ShowPendingHints&&feedback.PendingStage>=0)status="DPI等待确认…";
            else if(feedback!=null&&feedback.ShowPendingHints&&!String.IsNullOrEmpty(feedback.PendingOutput))status="音源等待确认…";
            else if(mouse.Percent.HasValue&&!mouse.Fresh)status="上次确认 "+mouse.TimeText;
            else if(visible&&ear.Percent.HasValue&&!ear.Fresh)status="耳机上次 "+ear.TimeText;
            else status=UiState.SyncText(mouse,now);
            DrawSync(g,status,footer,panelWidth-52,dark,!String.IsNullOrEmpty(failure),feedback);
            DrawMore(g,footer,dark,feedback);
        }
        internal static void PaintWireless(Graphics g,TrayContext owner,WirelessLayout layout,bool dark,PanelFeedback feedback) {
            g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
            int panelWidth=VisualDesign.PanelWidth,stageTop=VisualDesign.LargeFont?61:53;
            foreach(var card in layout.Cards) {
                var saved=g.Save();
                g.SetClip(new RectangleF(0,card.Top,panelWidth,card.Height),CombineMode.Intersect);
                if(card.NativeMouse) {
                    g.TranslateTransform(0,card.Top);
                    Paint(g,owner.Current,owner.LastGood,owner.ErrorMessage,owner.IsRefreshing,owner.Dpi,owner.DpiError,owner.IsDpiRefreshing,dark,null,owner.Audio,1,feedback.HoverAction>=10&&feedback.HoverAction<18?feedback.HoverAction-10:-1,false,0,-1,feedback);
                }else if(card.NativeHeadset||card.AudioOnly) {
                    g.TranslateTransform(0,card.Top-VisualDesign.MouseCardHeight(owner.Dpi)-8);
                    Paint(g,owner.Current,owner.LastGood,owner.ErrorMessage,owner.IsRefreshing,owner.Dpi,owner.DpiError,owner.IsDpiRefreshing,dark,owner.Headset,owner.Audio,1,-1,false,1,feedback.HoverAction>=100&&feedback.HoverAction<=102?feedback.HoverAction-100:-1,feedback);
                }else {
                    int index=layout.All.IndexOf(card);
                    DrawSurface(g,new RectangleF(.5f,card.Top+.5f,panelWidth-1,card.Height-1),dark,card.Battery);
                    string subtitle=card.Subtitle;
                    if(subtitle==null)subtitle=WirelessLayout.Subtitle(card.Device,card.Battery);
                    int dpiCount=WirelessLayout.DpiStageCount(card.Device);
                    if(dpiCount>0)subtitle=WirelessLayout.DpiSubtitle(card.Device,owner.DesiredWirelessDpi(card.Id,card.Device.Dpi.TargetToken),owner.WirelessDpiFailure(card.Id,card.Device.Dpi.TargetToken)??card.Device.Dpi.Error);
                    string icon=card.Kind=="keyboard"?"keyboard":card.Kind=="headset"?"headset":card.Kind=="mouse"?"mouse":card.Kind=="gamepad"?"gamepad":"info";
                    Header(g,card.Name,subtitle,icon,card.Top,card.Height,dark,feedback,1000+index);Meter(g,card.Battery,card.Top,card.Height,dark,feedback,2000+index);
                    if(dpiCount>0) {
                        var dpi=card.Device.Dpi;bool enabled=owner.CanSetWirelessDpi(card.Id,dpi.TargetToken);int? pending=owner.DesiredWirelessDpi(card.Id,dpi.TargetToken);bool confirmed=WirelessLayout.DpiFresh(card.Device)&&String.IsNullOrEmpty(owner.WirelessDpiFailure(card.Id,dpi.TargetToken));
                        var last=WirelessLayout.DpiRect(card,dpiCount-1);Fill(g,new RectangleF(10,card.Top+stageTop,panelWidth-20,last.Bottom-card.Top-(stageTop-2)),Track(dark),9);
                        for(int i=0;i<dpiCount;i++) {
                            var rect=WirelessLayout.DpiRect(card,i);int id=WirelessLayout.DpiActionId(index,i);bool waiting=pending==dpi.Stages[i],selected=confirmed&&dpi.Current==dpi.Stages[i];
                            Selection(g,rect,dpi.Stages[i].ToString(),null,selected,enabled||selected&&pending.HasValue,enabled&&feedback.HoverAction==id,enabled&&feedback.PressedAction==id,waiting,dark,feedback.KeyboardFocus==id,new RectangleF(.5f,card.Top+.5f,panelWidth-1,card.Height-1),id);
                            if(waiting&&!enabled)Outline(g,rect,Blue(dark),1.2f);
                        }
                    }
                }
                g.Restore(saved);
            }
            if(layout.HasVolume)SystemVolume(g,layout.VolumeRect,owner.Audio,dark,feedback);
            float top=layout.Footer;int footerHeight=VisualDesign.FooterHeight;float width=layout.Paged?(panelWidth-114):(panelWidth-52);
            string status=feedback.ManualRefreshing?"正在同步…":layout.All[0].Id=="empty"?owner.GenericScanning?"正在识别设备…":"暂无可读取电量的设备":layout.Paged?(layout.Start+1)+"–"+layout.End+" / "+layout.All.Count+" 台设备":"已识别 "+layout.All.Count+" 台设备";
            var failure=feedback.DpiFailure??feedback.AudioFailure??(!String.IsNullOrEmpty(owner.GenericHealthSummary)?owner.GenericHealthSummary:!String.IsNullOrEmpty(owner.GenericError)?"读取器暂不可用":null);
            DrawSync(g,failure??owner.GenericIdleSummary??status,top,width,dark,!String.IsNullOrEmpty(failure),feedback);
            if(layout.Paged) {
                float iconTop=top+(footerHeight-14)/2;
                InteractionLayout.Paint(g,new RectangleF(0,top,panelWidth,footerHeight),new RectangleF(panelWidth-102,top,28,footerHeight),902,layout.Start>0?feedback:null,dark,false,delegate{DrawSymbol(g,"back",new RectangleF(panelWidth-94,iconTop,14,14),layout.Start>0?Fg(dark):Muted(dark));});
                InteractionLayout.Paint(g,new RectangleF(0,top,panelWidth,footerHeight),new RectangleF(panelWidth-68,top,28,footerHeight),903,layout.End<layout.All.Count?feedback:null,dark,false,delegate{DrawSymbol(g,"chevron",new RectangleF(panelWidth-62,iconTop,14,14),layout.End<layout.All.Count?Fg(dark):Muted(dark));});
            }
            DrawMore(g,top,dark,feedback);
        }
        static void DrawMore(Graphics g,float top,bool dark,PanelFeedback feedback){
            int panelWidth=VisualDesign.PanelWidth,footerHeight=VisualDesign.FooterHeight;
            Fill(g,new RectangleF(panelWidth-32,top+(footerHeight-24)/2,24,24),ThemePalette.Current(dark).Card,7);
            InteractionLayout.Paint(g,new RectangleF(0,top,panelWidth,footerHeight),new RectangleF(panelWidth-34,top,28,footerHeight),1,feedback,dark,false,delegate{DrawSymbol(g,"more",new RectangleF(panelWidth-28,top+(footerHeight-16)/2,16,16),Fg(dark));});
        }
        internal static RectangleF SyncPillRect(Graphics g,string text,float top,float maximumWidth) {
            string family="Segoe UI";foreach(char c in text??"")if(c>=0x2e80&&c<=0x9fff){family="Microsoft YaHei UI";break;}
            var previousOffset=g.PixelOffsetMode;g.PixelOffsetMode=PixelOffsetMode.Default;
            try {
            using(var font=new Font(family,VisualDesign.SubtitleFontSize,FontStyle.Regular,GraphicsUnit.Pixel))
            using(var format=new StringFormat {FormatFlags=StringFormatFlags.NoWrap}) {
                float width=(float)Math.Ceiling(g.MeasureString(text??"",font,Int32.MaxValue,format).Width)+26;
                return new RectangleF(8,top+3,Math.Min(maximumWidth,Math.Max(26,width)),VisualDesign.FooterHeight-6);
            }
            } finally {g.PixelOffsetMode=previousOffset;}
        }
        static RectangleF DrawSync(Graphics g,string text,float top,float maximumWidth,bool dark,bool failure,PanelFeedback feedback) {
            if(feedback!=null&&!String.IsNullOrEmpty(feedback.UpdatedNotice)){text=feedback.UpdatedNotice;failure=false;}
            var pill=SyncPillRect(g,text,top,maximumWidth);
            Fill(g,pill,ThemePalette.Current(dark).SyncFill,7);
            string label=text;int footerHeight=VisualDesign.FooterHeight;
            InteractionLayout.Paint(g,new RectangleF(0,top,VisualDesign.PanelWidth,footerHeight),new RectangleF(8,top,maximumWidth,footerHeight),0,feedback,dark,false,delegate{
            DrawSymbol(g,"refresh",new RectangleF(11,top+(footerHeight-12)/2,12,12),Muted(dark));
            DrawLabel(g,label,new RectangleF(29,top+3,pill.Width-26,footerHeight-6),VisualDesign.SubtitleFontSize,false,failure?ThemePalette.LevelColor(10,dark):Muted(dark),StringAlignment.Near);
            });
            return pill;
        }
    }
}
