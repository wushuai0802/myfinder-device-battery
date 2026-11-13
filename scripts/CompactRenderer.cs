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
        internal static Action<string,RectangleF> SymbolObserver=null;
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
        internal static void DrawPanelCard(Graphics g,float top,float height,bool dark,BatteryView battery) {
            DrawPanelSurface(g,VisualDesign.PanelCardRect(top,height),dark,battery);
        }
        static void DrawPanelSurface(Graphics g,RectangleF rect,bool dark,BatteryView battery,float radius=VisualDesign.MainCardRadius) {
            var palette=ThemePalette.Current(dark);
            if(!VisualDesign.HighContrast) {
                Fill(g,VisualDesign.PanelShadowRect(rect,2),palette.PanelShadowFar,radius);
                Fill(g,VisualDesign.PanelShadowRect(rect,1),palette.PanelShadowNear,radius);
            }
            using(var shape=VisualDesign.RoundRect(rect.X,rect.Y,rect.Width,rect.Height,radius)) {
                using(var fill=new SolidBrush(palette.Card))g.FillPath(fill,shape);
                if(ThemePalette.IsLow(battery,DeviceSettings.Current.WarningThreshold)&&!VisualDesign.HighContrast)using(var tint=new SolidBrush(ThemePalette.WarningOverlay(dark)))g.FillPath(tint,shape);
                using(var pen=new Pen(palette.PanelBorder,1))g.DrawPath(pen,shape);
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
            if(SymbolObserver!=null)SymbolObserver(name,rect);
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
        internal static void Highlight(Graphics g,RectangleF rect,int id,PanelFeedback feedback,bool dark,float radius=8,float focusWidth=1,RectangleF? focusRect=null) {
            if(id<0||feedback==null)return;
            if(feedback!=null&&(feedback.HoverAction==id||feedback.PressedAction==id)) {
                if(VisualDesign.HighContrast)FocusRing(g,rect,Blue(dark),radius,feedback.PressedAction==id?2:1);
                else Fill(g,rect,ThemePalette.Current(dark).Interaction(feedback.PressedAction==id),radius);
            }
            if(feedback!=null&&feedback.KeyboardFocus==id)FocusRing(g,focusRect??rect,Blue(dark),radius,focusWidth);
        }
        internal static void FocusRing(Graphics g,RectangleF rect,Color color,float radius,float width=1){
            if(InteractionOutlineObserver!=null&&g.IsVisible(rect))InteractionOutlineObserver(rect);
            float inset=width/2;using(var path=VisualDesign.RoundRect(rect.X+inset,rect.Y+inset,rect.Width-width,rect.Height-width,Math.Max(0,radius-inset)))using(var pen=new Pen(color,width))g.DrawPath(pen,path);
        }
        static void Meter(Graphics g,BatteryView view,float top,float height,bool dark,PanelFeedback feedback,int action,float batteryProgress=1) {
            InteractionLayout.Paint(g,VisualDesign.PanelCardRect(top,height),VisualDesign.HeaderMeterHitRect(top),action,feedback,dark,false,delegate{
            Color digit=!view.Percent.HasValue||!view.Fresh?Muted(dark):ThemePalette.IsLow(view,DeviceSettings.Current.WarningThreshold)?ThemePalette.LevelColor(0,dark):Fg(dark);
            string text=view.Percent.HasValue?(view.Approximate?"≈":"")+view.Percent.Value.ToString():"--";
            var digits=view.Percent.HasValue?VisualDesign.BatteryDigitsRect(top):VisualDesign.BatteryPlaceholderRect(top);
            float size=view.Percent.HasValue?VisualDesign.BatteryDigitSize:13;
            var digitInk=InteractionLayout.LabelBounds(g,text,digits,size,true,StringAlignment.Far,StringTrimming.None);
            if(view.Percent.HasValue){var percentInk=InteractionLayout.LabelBounds(g,"%",VisualDesign.BatteryPercentRect(top),VisualDesign.BatteryPercentSize,true,StringAlignment.Near,StringTrimming.None);digits=VisualDesign.SnapTextRect(VisualDesign.AlignBatteryDigits(digits,digitInk,percentInk));digitInk=InteractionLayout.LabelBounds(g,text,digits,size,true,StringAlignment.Far,StringTrimming.None);}
            if(view.Charging)DrawSymbol(g,"bolt",VisualDesign.BatteryChargingRect(top,digitInk),ThemePalette.Current(dark).Charging);
            DrawLabel(g,text,digits,size,true,digit,StringAlignment.Far,StringTrimming.None);
            if(view.Percent.HasValue)DrawLabel(g,"%",VisualDesign.BatteryPercentRect(top),VisualDesign.BatteryPercentSize,true,Muted(dark),StringAlignment.Near,StringTrimming.None);
            if(view.Percent.HasValue&&view.Unconfirmed)DrawLabel(g,"待确认",VisualDesign.BatteryPendingRect(top),VisualDesign.SmallFontSize,false,Muted(dark),StringAlignment.Far);
            });
            var track=VisualDesign.BatteryBarRect(top);var fill=VisualDesign.BatteryFillRect(top,view);fill.Width*=VisualDesign.BatteryFillProgress(batteryProgress);
            if(VisualDesign.HighContrast) {
                using(var path=VisualDesign.RoundRect(track.X+.5f,track.Y+.5f,track.Width-1,track.Height-1,VisualDesign.BatteryBarRadius))using(var pen=new Pen(Fg(dark),1))g.DrawPath(pen,path);
            }else Fill(g,track,Track(dark),VisualDesign.BatteryBarRadius);
            if(fill.Width>0)Fill(g,fill,view.Fresh?ThemePalette.LevelColor(view.Percent,dark):Muted(dark),VisualDesign.BatteryBarRadius);
        }
        static void Header(Graphics g,string name,string subtitle,string icon,float top,float height,bool dark,PanelFeedback feedback,int action,bool audioOnly=false) {
            InteractionLayout.Paint(g,VisualDesign.PanelCardRect(top,height),audioOnly?VisualDesign.AudioHeaderHitRect(top):VisualDesign.HeaderNameHitRect(top),action,feedback,dark,false,delegate{
            Fill(g,VisualDesign.IconTileRect(top),Track(dark),VisualDesign.IconTileRadius);
            DrawSymbol(g,icon,VisualDesign.HeaderIconRect(top),Fg(dark));
            bool reveal=audioOnly&&feedback!=null&&(feedback.HoverAction==action||feedback.KeyboardFocus==action);
            var title=VisualDesign.HeaderTextRect(top,false,audioOnly,reveal);
            DrawLabel(g,name,title,VisualDesign.DeviceNameFontSize,true,Fg(dark),StringAlignment.Near);
            if(reveal)DrawSymbol(g,"chevron",VisualDesign.AudioHeaderChevronRect(top),Muted(dark));
            DrawLabel(g,subtitle,VisualDesign.HeaderTextRect(top,true,audioOnly),VisualDesign.SubtitleFontSize,false,Muted(dark),StringAlignment.Near);
            });
        }
        internal static void Selection(Graphics g,RectangleF rect,string label,string icon,bool selected,bool enabled,bool hovered,bool pressed,bool pending,bool dark,bool focused=false,RectangleF? card=null,int id=0,Action extra=null,RectangleF? viewport=null) {
            var palette=ThemePalette.Current(dark);
            if(pending)label+="…";
            if(selected) {
                if(!VisualDesign.HighContrast&&!dark&&enabled)Fill(g,VisualDesign.PanelShadowRect(rect,1),palette.PanelShadowNear,VisualDesign.SelectionRadius);
                Fill(g,rect,enabled?palette.SegmentFill:Track(dark),VisualDesign.SelectionRadius);
                using(var path=VisualDesign.RoundRect(rect.X+.5f,rect.Y+.5f,rect.Width-1,rect.Height-1,VisualDesign.SelectionRadius))using(var pen=new Pen(palette.SegmentBorder,1))g.DrawPath(pen,path);
            }
            if(enabled&&(hovered||pressed))Fill(g,rect,palette.SegmentHover,VisualDesign.SelectionRadius);
            if(pending&&!selected)Outline(g,rect,Blue(dark),1.2f);
            var container=card??RectangleF.FromLTRB(rect.Left-8,rect.Top-8,rect.Right+8,rect.Bottom+8);
            InteractionLayout.Paint(g,container,rect,id,enabled?new PanelFeedback {HoverAction=hovered?id:-1,PressedAction=pressed?id:-1,KeyboardFocus=focused?id:-1}:null,dark,false,delegate{
            Color color=!enabled?Muted(dark):VisualDesign.HighContrast&&(selected||hovered||pressed)?PopupPages.SelectedForeground(dark):selected?Fg(dark):Muted(dark);
            if(!String.IsNullOrEmpty(icon)) {
                DrawSymbol(g,icon,VisualDesign.SelectionIconRect(rect,label),color);
                DrawLabel(g,label,VisualDesign.SelectionIconLabelRect(rect,label),VisualDesign.SmallFontSize,selected,color,StringAlignment.Near);
            }else if(extra!=null)extra();else DrawLabel(g,label,VisualDesign.SelectionTextRect(rect),VisualDesign.DeviceNameFontSize,selected,color,StringAlignment.Center);
            if(selected&&enabled||pending) {
                var pip=VisualDesign.SelectionPipRect(rect);Color ink=VisualDesign.HighContrast&&selected&&enabled?palette.SelectedForeground:Blue(dark);
                if(pending)for(int i=0;i<3;i++)Fill(g,VisualDesign.SelectionPendingDotRect(rect,i),ink,1);
                else Fill(g,pip,ink,1);
            }
            },false,1,2,VisualDesign.SelectionFocusRect(rect),viewport);
        }
        internal static void DrawVolumeTrack(Graphics g,RectangleF meter,bool dark,float fraction,float start=0,Color? fill=null) {
            Fill(g,meter,Track(dark),VisualDesign.VolumeTrackRadius);
            float width=meter.Width*Math.Max(0,Math.Min(1,fraction));
            if(width>0)Fill(g,new RectangleF(meter.X+meter.Width*start,meter.Y,width,meter.Height),fill??Blue(dark),Math.Min(VisualDesign.VolumeTrackRadius,width/2));
        }
        internal static void SystemVolume(Graphics g,RectangleF rect,AudioReading audio,bool dark,PanelFeedback feedback,bool drawSurface=true) {
            bool collapsed=DeviceSettings.Current.VolumeCollapsed;
            bool blocked=feedback!=null&&feedback.AudioBlocked;
            bool enabled=!blocked&&audio!=null&&audio.OutputVolumePercent.HasValue&&!String.IsNullOrEmpty(audio.VolumeEndpointId);
            int preview=feedback==null?-1:feedback.VolumePreview;
            int? value=enabled?(int?)(preview>=0?Math.Max(0,Math.Min(100,preview)):audio.OutputVolumePercent.Value):null;
            var surface=VisualDesign.PanelCardSurface(rect);
            if(drawSurface)DrawPanelSurface(g,surface,dark,null);
            var toggle=VisualDesign.VolumeToggleRect(rect,collapsed);
            InteractionLayout.Paint(g,surface,toggle,241,feedback,dark,false,delegate{DrawSymbol(g,collapsed?"expand":"collapse",VisualDesign.VolumeChevronRect(rect,collapsed),Fg(dark));});
            if(collapsed) {
                DrawSymbol(g,"speaker",new RectangleF(rect.X+11,rect.Y+(float)Math.Floor((rect.Height-16)/2),16,16),enabled?Fg(dark):Muted(dark));
                DrawLabel(g,value.HasValue?value+"%":"音量未确认",new RectangleF(rect.X+36,rect.Y+3,rect.Width-74,rect.Height-6),VisualDesign.SubtitleFontSize,false,enabled?Fg(dark):Muted(dark),StringAlignment.Near);
                return;
            }
            var slider=VisualDesign.VolumeSliderRect(rect);
            InteractionLayout.Paint(g,surface,slider,240,enabled?feedback:null,dark,false,delegate{
            DrawSymbol(g,"speaker",VisualDesign.VolumeSpeakerRect(slider),enabled?Fg(dark):Muted(dark));
            DrawLabel(g,value.HasValue?value+"%":"--",VisualDesign.VolumeValueRect(slider),VisualDesign.SubtitleFontSize,false,enabled?Fg(dark):Muted(dark),StringAlignment.Far);
            var meter=PopupPages.VolumeTrack(slider);DrawVolumeTrack(g,meter,dark,value.GetValueOrDefault()/100f);
            if(value.HasValue) {
                var knob=VisualDesign.VolumeKnobRect(meter,value.Value,feedback!=null&&(feedback.HoverAction==240||feedback.PressedAction==240));
                var ink=knob;ink.Inflate(.5f,.5f);InteractionLayout.Include(ink);
                using(var brush=new SolidBrush(ThemePalette.Current(dark).Knob))g.FillEllipse(brush,knob);
                using(var pen=new Pen(Blue(dark),1))g.DrawEllipse(pen,knob);
            }
            });
        }
        internal static string MouseSubtitle(BatteryView mouse,DpiConfig dpi,string dpiError) {
            string text="DPI未读取";if(dpi!=null&&dpi.Stages!=null&&dpi.ActiveStage>=0&&dpi.ActiveStage<dpi.Stages.Count)text=(String.IsNullOrEmpty(dpiError)?"当前 ":"上次 ")+dpi.Stages[dpi.ActiveStage].XDpi+" DPI";
            return mouse.Percent.HasValue&&!mouse.Fresh?"上次确认 "+mouse.TimeText:!mouse.Percent.HasValue?"等待设备回读":text;
        }
        internal static string HeadsetSubtitle(BatteryView ear){return ear.Percent.HasValue&&!ear.Fresh?"上次确认 "+ear.TimeText:ear.Percent.HasValue?"已连接":"电量未读取";}
        internal static string RingStatus(BatteryView view,string subtitle) {
            if(view!=null&&view.Unconfirmed)return "待确认"+(String.IsNullOrEmpty(subtitle)?"":" · "+subtitle);
            if(view==null||!view.Fresh||!view.Percent.HasValue)return subtitle??"电量未读取";
            string state=view.Charging?"充电中":ThemePalette.IsLow(view,DeviceSettings.Current.WarningThreshold)?"电量低":null;
            return state==null?subtitle:!String.IsNullOrEmpty(subtitle)&&subtitle.Contains(state)?subtitle:state+(String.IsNullOrEmpty(subtitle)?"":" · "+subtitle);
        }
        // The current provider contract has no distinct per-device sleep flag.
        // Honor an explicit sleep state only; cached/offline is not proof of sleep.
        internal static bool RingSleeping(WirelessDevice device){return device!=null&&(device.ConnectionState=="sleeping"||device.MeasurementState=="sleeping");}
        static void NativeDpiControls(Graphics g,DpiConfig dpi,float top,bool dark,PanelFeedback feedback,int hoverStage=-1,GridControlGroup group=null,RectangleF? surface=null) {
            int count=dpi!=null&&dpi.Stages!=null?dpi.Stages.Count:0;
            if(count>0) {
                Fill(g,group==null?VisualDesign.StageTrackRect(top,count):group.Track,Track(dark),9);
                for(int i=0;i<count;i++) {
                    var rect=group==null?VisualDesign.StageRect(i,count):GridLayout.Segment(group,i);if(group==null)rect.Y+=top;var value=dpi.Stages[i];bool selected=dpi.ActiveStage==i,hint=feedback!=null&&feedback.ShowPendingHints&&feedback.PendingStage==i;
                    var card=surface??VisualDesign.PanelCardRect(top,VisualDesign.MouseCardHeight(dpi));bool focused=feedback!=null&&feedback.KeyboardFocus==10+i;
                    if(value.XDpi==value.YDpi)Selection(g,rect,value.XDpi.ToString(),null,selected,true,hoverStage==i||feedback!=null&&feedback.HoverAction==10+i,feedback!=null&&feedback.PressedAction==10+i,hint,dark,focused,card,10+i,null,group==null?null:(RectangleF?)group.Viewport);
                    else Selection(g,rect,"",null,selected,true,hoverStage==i||feedback!=null&&feedback.HoverAction==10+i,feedback!=null&&feedback.PressedAction==10+i,hint,dark,focused,card,10+i,delegate{
                        Color color=selected?VisualDesign.HighContrast?PopupPages.SelectedForeground(dark):Fg(dark):Muted(dark);DrawLabel(g,"X "+value.XDpi,VisualDesign.SelectionDetailRect(rect,false),VisualDesign.SmallFontSize,selected,color,StringAlignment.Center);DrawLabel(g,"Y "+value.YDpi+(hint?"…":""),VisualDesign.SelectionDetailRect(rect,true),VisualDesign.SmallFontSize,selected,color,StringAlignment.Center);
                    },group==null?null:(RectangleF?)group.Viewport);
                }
            }else{var waiting=VisualDesign.StageTrackRect(top,0);Fill(g,waiting,Track(dark),9);DrawLabel(g,"等待DPI档位回读",waiting,VisualDesign.SubtitleFontSize,false,Muted(dark),StringAlignment.Center);}
        }
        static void AudioControls(Graphics g,float top,AudioReading audio,AudioCardView audioCard,bool dark,PanelFeedback feedback,int hoverAudio=-1,GridControlGroup group=null,RectangleF? surface=null) {
            Fill(g,group==null?VisualDesign.AudioSelectionTrack(top):group.Track,Track(dark),9);
            for(int i=0;i<2;i++) {
                var rect=group==null?VisualDesign.AudioOutputRect(top,i):GridLayout.Segment(group,i);string target=i==0?"speaker":"headset";
                Selection(g,rect,i==0?"扬声器":"耳机",target,audio!=null&&audio.ActiveOutput==target,(feedback==null||!feedback.AudioBlocked)&&audio!=null&&(i==0?audio.SpeakerAvailable:audio.HeadsetAvailable&&(audioCard==null||audioCard.HasTarget)),hoverAudio==i||feedback!=null&&feedback.HoverAction==100+i,feedback!=null&&feedback.PressedAction==100+i,feedback!=null&&feedback.ShowPendingHints&&feedback.PendingOutput==target,dark,feedback!=null&&feedback.KeyboardFocus==100+i,surface??VisualDesign.PanelCardRect(top,VisualDesign.HeadsetCardHeight),100+i,null,group==null?null:(RectangleF?)group.Viewport);
            }
            var mic=group==null?VisualDesign.MicrophoneRect(top):GridLayout.Microphone(group);bool muted=audio!=null&&audio.MicrophoneMuted==true,blocked=feedback!=null&&feedback.AudioBlocked;
            bool uncertain=blocked||feedback!=null&&feedback.MicrophonePending||audio==null||!audio.MicrophoneAvailable||!audio.MicrophoneMuted.HasValue;
            Color red=ThemePalette.LevelColor(10,dark);Fill(g,mic,!uncertain&&muted?ThemePalette.Current(dark).MutedMicrophoneFill:Track(dark),VisualDesign.MicrophoneRadius);
            var micFeedback=blocked||audio==null||!audio.MicrophoneAvailable||!audio.MicrophoneMuted.HasValue?null:new PanelFeedback {HoverAction=hoverAudio==2||feedback!=null&&feedback.HoverAction==102?102:-1,PressedAction=feedback==null?-1:feedback.PressedAction,KeyboardFocus=feedback==null?-1:feedback.KeyboardFocus};
            if(feedback!=null&&feedback.ShowPendingHints&&feedback.MicrophonePending)Outline(g,mic,Blue(dark),1.2f);
            InteractionLayout.Paint(g,surface??VisualDesign.PanelCardRect(top,VisualDesign.HeadsetCardHeight),mic,102,micFeedback,dark,false,delegate{DrawSymbol(g,muted?"mic-off":"mic",VisualDesign.CenteredRect(mic,14,16),uncertain?Muted(dark):muted?red:Fg(dark));},true,1,1,null,group==null?null:(RectangleF?)group.Viewport);
        }
        internal static void RingGauge(Graphics g,BatteryView view,string icon,float top,bool dark,bool sleeping=false,float batteryProgress=1) {
            var palette=ThemePalette.Current(dark);var outer=VisualDesign.BatteryRingRect(top);float width=VisualDesign.BatteryRingWidth;var circle=outer;circle.Inflate(-width/2,-width/2);
            using(var pen=new Pen(VisualDesign.HighContrast?SystemColors.WindowText:palette.Track,VisualDesign.HighContrast?1:width))g.DrawEllipse(pen,circle);
            bool reading=view!=null&&view.Percent.HasValue&&!sleeping;float percent=(reading?Math.Max(0,Math.Min(100,view.Percent.Value)):0)*VisualDesign.BatteryFillProgress(batteryProgress);
            Color ink=VisualDesign.HighContrast?SystemColors.Highlight:view!=null&&view.Fresh?ThemePalette.LevelColor(view.Percent,dark):palette.Muted;
            if(percent==100){using(var pen=new Pen(ink,width))g.DrawEllipse(pen,circle);}
            else if(percent>0&&percent<=3){using(var brush=new SolidBrush(ink))g.FillEllipse(brush,outer.X+outer.Width/2-width/2,outer.Y,width,width);}
            else if(percent>3)using(var pen=new Pen(ink,width) {StartCap=LineCap.Round,EndCap=LineCap.Round})g.DrawArc(pen,circle,-90,percent*3.6f);
            DrawSymbol(g,icon,VisualDesign.HeaderIconRect(top),reading?palette.Muted:palette.Tertiary);
            if(reading&&view.Charging){var badge=VisualDesign.RingChargeBadgeRect(top);using(var brush=new SolidBrush(palette.Card))g.FillEllipse(brush,badge);DrawSymbol(g,"bolt",VisualDesign.CenteredRect(badge,8,10),palette.Charging);}
        }
        static RectangleF RingNumber(Graphics g,BatteryView view,float top,bool dark,bool sleeping,bool draw) {
            bool reading=view!=null&&view.Percent.HasValue&&!sleeping;string text=sleeping?"休眠":reading?(view.Approximate?"≈":"")+view.Percent.Value:"--";
            var digits=reading?VisualDesign.BatteryDigitsRect(top):VisualDesign.BatteryPlaceholderRect(top);float size=reading?VisualDesign.BatteryDigitSize:13;
            var ink=InteractionLayout.LabelBounds(g,text,digits,size,true,StringAlignment.Far,StringTrimming.None);
            if(reading){var percentInk=InteractionLayout.LabelBounds(g,"%",VisualDesign.BatteryPercentRect(top),VisualDesign.BatteryPercentSize,true,StringAlignment.Near,StringTrimming.None);digits=VisualDesign.SnapTextRect(VisualDesign.AlignBatteryDigits(digits,ink,percentInk));ink=InteractionLayout.LabelBounds(g,text,digits,size,true,StringAlignment.Far,StringTrimming.None);}
            RectangleF number=ink;
            if(reading&&view.Charging){var bolt=VisualDesign.BatteryChargingRect(top,ink);if(draw)DrawSymbol(g,"bolt",bolt,ThemePalette.Current(dark).Charging);number=RectangleF.Union(number,bolt);}
            if(draw){DrawLabel(g,text,digits,size,true,!reading?ThemePalette.Current(dark).Tertiary:!view.Fresh?Muted(dark):ThemePalette.IsLow(view,DeviceSettings.Current.WarningThreshold)?ThemePalette.LevelColor(0,dark):Fg(dark),StringAlignment.Far,StringTrimming.None);if(reading)DrawLabel(g,"%",VisualDesign.BatteryPercentRect(top),VisualDesign.BatteryPercentSize,true,Muted(dark),StringAlignment.Near,StringTrimming.None);}
            return number;
        }
        static void RingHeader(Graphics g,string name,string subtitle,string icon,BatteryView view,float top,bool dark,PanelFeedback feedback,int action,bool audioOnly=false,bool sleeping=false,float batteryProgress=1) {
            var row=VisualDesign.HeaderRowRect(top);bool hover=feedback!=null&&(feedback.HoverAction==action||feedback.PressedAction==action),reveal=hover||feedback!=null&&feedback.KeyboardFocus==action;
            if(hover){if(VisualDesign.HighContrast)FocusRing(g,row,Blue(dark),6,1);else Fill(g,row,ThemePalette.Current(dark).SegmentHover,0);}
            RingGauge(g,view,icon,top,dark,sleeping,batteryProgress);
            var number=audioOnly?new RectangleF(VisualDesign.PanelWidth-12,top,0,0):RingNumber(g,view,top,dark,sleeping,false);
            var focus=row;focus.Inflate(-2,-2);
            InteractionLayout.Paint(g,row,row,action,feedback,dark,false,delegate{
                var title=VisualDesign.HeaderTextRect(top,false,audioOnly,reveal);title.Width=Math.Max(1,Math.Min(title.Right,number.Left-(reveal?19:6))-title.Left);
                DrawLabel(g,name,title,VisualDesign.LargeFont?14:12.5f,true,Fg(dark),StringAlignment.Near);
                string status=sleeping?"休眠":RingStatus(view,subtitle);var statusRect=VisualDesign.HeaderTextRect(top,true,audioOnly,reveal);statusRect.Width=title.Width;
                var dot=VisualDesign.StatusDotRect(top);Color color=sleeping||view==null||!view.Fresh||!view.Percent.HasValue?ThemePalette.Current(dark).Tertiary:ThemePalette.IsLow(view,DeviceSettings.Current.WarningThreshold)?ThemePalette.LevelColor(0,dark):ThemePalette.LevelColor(100,dark);
                // Include the decorative dot in the measured header ink too.
                InteractionLayout.Include(dot);using(var brush=new SolidBrush(color))g.FillEllipse(brush,dot);statusRect.X+=dot.Width+4;statusRect.Width=Math.Max(1,statusRect.Width-dot.Width-4);
                DrawLabel(g,status,statusRect,VisualDesign.SubtitleFontSize,false,Muted(dark),StringAlignment.Near);
                if(!audioOnly)RingNumber(g,view,top,dark,sleeping,true);
                if(reveal)DrawSymbol(g,"chevron",VisualDesign.RingChevronRect(top,number),Muted(dark));
            },false,0,2,focus);
        }
        static void RingSeparator(Graphics g,float top,bool dark){using(var pen=new Pen(ThemePalette.Current(dark).PanelBorder,1))g.DrawLine(pen,12,top-.5f,VisualDesign.PanelWidth-12,top-.5f);}
        static GraphicsState BeginRingPanel(Graphics g,float bottom,bool dark) {
            g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
            var surface=VisualDesign.PanelCardRect(0,bottom);DrawPanelSurface(g,surface,dark,null,14);var saved=g.Save();using(var clip=VisualDesign.RoundRect(surface.X,surface.Y,surface.Width,surface.Height,14))g.SetClip(clip,CombineMode.Intersect);return saved;
        }
        static void PaintRing(Graphics g,BatteryReading current,BatteryReading lastGood,DpiConfig dpi,string dpiError,bool dark,HeadsetReading headset,AudioReading audio,float headsetProgress,PanelFeedback feedback,float batteryProgress) {
            feedback=feedback??new PanelFeedback();var pref=DeviceSettings.Current;var mouse=UiState.Mouse(current,lastGood,DateTime.UtcNow);var ear=UiState.Headset(headset,DateTime.UtcNow);var audioCard=VisualDesign.AudioCardFor(headset,audio);
            bool visible=VisualDesign.ShouldShowHeadset(headset)||audioCard!=null;float progress=visible?Math.Max(0,Math.Min(1,headsetProgress)):0;
            float top=VisualDesign.MouseCardHeight(dpi)+VisualDesign.DeviceGap;var volume=new RectangleF(0,VisualDesign.MouseCardHeight(dpi)+VisualDesign.SecondCardStep*progress+VisualDesign.DeviceGap,VisualDesign.PanelWidth,VisualDesign.CurrentSystemVolumeHeight);
            var saved=BeginRingPanel(g,volume.Bottom,dark);
            RingHeader(g,pref.MouseAlias,MouseSubtitle(mouse,dpi,dpiError),"mouse",mouse,0,dark,feedback,2,false,false,batteryProgress);NativeDpiControls(g,dpi,0,dark,feedback);
            if(progress>0){RingSeparator(g,top,dark);var clip=g.Save();g.SetClip(new RectangleF(0,top,VisualDesign.PanelWidth,VisualDesign.HeadsetCardHeight*progress),CombineMode.Intersect);
                RingHeader(g,audioCard==null?pref.HeadsetAlias:"耳机",audioCard==null?HeadsetSubtitle(ear):audioCard.Subtitle,"headset",audioCard==null?ear:null,top,dark,feedback,audioCard==null?3:200,audioCard!=null,false,batteryProgress);AudioControls(g,top,audio,audioCard,dark,feedback);g.Restore(clip);
            }
            RingSeparator(g,volume.Top,dark);SystemVolume(g,volume,audio,dark,feedback,false);g.Restore(saved);
            string failure=feedback.DpiFailure??feedback.AudioFailure;
            string status=!String.IsNullOrEmpty(failure)?failure:feedback.ManualRefreshing?"正在同步…":feedback.ShowPendingHints&&feedback.PendingStage>=0?"DPI等待确认…":feedback.ShowPendingHints&&!String.IsNullOrEmpty(feedback.PendingOutput)?"音源等待确认…":mouse.Percent.HasValue&&!mouse.Fresh?"上次确认 "+mouse.TimeText:visible&&ear.Percent.HasValue&&!ear.Fresh?"耳机上次 "+ear.TimeText:UiState.SyncText(mouse,DateTime.UtcNow);
            float footer=volume.Bottom+VisualDesign.MainFooterGap;DrawSync(g,status,footer,VisualDesign.PanelWidth-52,dark,!String.IsNullOrEmpty(failure),feedback);DrawMore(g,footer,dark,feedback);
        }
        static void WirelessDpiControls(Graphics g,TrayContext owner,WirelessCard card,int index,bool dark,PanelFeedback feedback,WirelessLayout layout=null) {
            int count=WirelessLayout.DpiStageCount(card.Device);if(count==0)return;
            var dpi=card.Device.Dpi;bool enabled=owner.CanSetWirelessDpi(card.Id,dpi.TargetToken),confirmed=WirelessLayout.DpiFresh(card.Device)&&String.IsNullOrEmpty(owner.WirelessDpiFailure(card.Id,dpi.TargetToken));int? pending=owner.DesiredWirelessDpi(card.Id,dpi.TargetToken);
            Fill(g,layout!=null&&layout.Grid!=null?layout.Grid.Group(card).Track:VisualDesign.StageTrackRect(card.Top,count),Track(dark),9);
            for(int i=0;i<count;i++){var rect=WirelessLayout.DpiRect(card,i);int id=WirelessLayout.DpiActionId(index,i);bool selected=confirmed&&dpi.Current==dpi.Stages[i];Selection(g,rect,dpi.Stages[i].ToString(),null,selected,enabled||selected&&pending.HasValue,enabled&&feedback.HoverAction==id,enabled&&feedback.PressedAction==id,pending==dpi.Stages[i],dark,feedback.KeyboardFocus==id,layout!=null&&layout.Grid!=null?layout.Grid.GroupSurface:VisualDesign.PanelCardRect(card.Top,card.Height),id,null,layout!=null&&layout.Grid!=null?(RectangleF?)layout.Grid.Viewport:null);}
        }
        static RectangleF GridNumber(Graphics g,WirelessCard card,bool dark,bool draw) {
            var tile=card.Tile;var view=card.Battery;bool sleeping=RingSleeping(card.Device),reading=view!=null&&view.Percent.HasValue&&!sleeping;
            string text=sleeping?"休眠":reading?(view.Approximate?"≈":"")+view.Percent.Value:"--";float size=reading?(VisualDesign.LargeFont?30:26):13;
            var digits=GridLayout.Number(tile);bool wide=tile.Width>GridLayout.TileWidth;var alignment=wide?StringAlignment.Far:StringAlignment.Near;if(reading)digits.Width-=18;
            var ink=InteractionLayout.LabelBounds(g,text,digits,size,true,alignment,StringTrimming.None);digits.Y+=GridLayout.NumberBaseline(tile)-ink.Bottom;digits=VisualDesign.SnapTextRect(digits);ink=InteractionLayout.LabelBounds(g,text,digits,size,true,alignment,StringTrimming.None);
            if(draw)DrawLabel(g,text,digits,size,true,!reading?ThemePalette.Current(dark).Tertiary:!view.Fresh?Muted(dark):ThemePalette.IsLow(view,DeviceSettings.Current.WarningThreshold)?ThemePalette.LevelColor(0,dark):Fg(dark),alignment,StringTrimming.None);
            if(reading){float percentSize=VisualDesign.LargeFont?13:12;var percent=new RectangleF(ink.Right+1,digits.Y,18,22);var percentInk=InteractionLayout.LabelBounds(g,"%",percent,percentSize,true,StringAlignment.Near,StringTrimming.None);percent.X+=ink.Right+1-percentInk.Left;percent.Y+=ink.Bottom-percentInk.Bottom;percent=VisualDesign.SnapTextRect(percent);if(draw)DrawLabel(g,"%",percent,percentSize,true,Muted(dark),StringAlignment.Near,StringTrimming.None);ink=RectangleF.Union(ink,InteractionLayout.LabelBounds(g,"%",percent,percentSize,true,StringAlignment.Near,StringTrimming.None));}
            return ink;
        }
        static void GridTile(Graphics g,WirelessLayout layout,WirelessCard card,bool dark,PanelFeedback feedback,float batteryProgress) {
            var tile=card.Tile;var surface=VisualDesign.PanelCardSurface(tile);int action=GridLayout.DetailAction(layout,card);var view=card.Battery;bool sleeping=RingSleeping(card.Device);string status=GridLayout.Status(view,sleeping);var palette=ThemePalette.Current(dark);
            DrawPanelSurface(g,surface,dark,sleeping?null:view);
            bool active=feedback.HoverAction==action||feedback.PressedAction==action,reveal=active||feedback.KeyboardFocus==action;
            if(active){if(VisualDesign.HighContrast)FocusRing(g,surface,palette.Accent,10,1);else Fill(g,surface,palette.SegmentHover,10);}
            var focus=surface;focus.Inflate(-2,-2);
            InteractionLayout.Paint(g,tile,tile,action,feedback,dark,false,delegate{
                string icon=card.Kind=="keyboard"?"keyboard":card.Kind=="headset"?"headset":card.Kind=="mouse"?"mouse":card.Kind=="gamepad"?"gamepad":"info";
                DrawSymbol(g,icon,GridLayout.Icon(tile),sleeping?palette.Tertiary:Muted(dark));
                var number=GridNumber(g,card,dark,true);var name=GridLayout.Name(tile);if(tile.Width>GridLayout.TileWidth)name.Width=Math.Max(1,Math.Min(name.Right,number.Left-6)-name.Left);
                DrawLabel(g,card.Name,name,VisualDesign.LargeFont?13:11,false,Muted(dark),StringAlignment.Near);
                if(!String.IsNullOrEmpty(status)){var tag=GridLayout.Tag(tile,status);Color ink=status=="充电"?palette.Charging:status=="电量低"?ThemePalette.LevelColor(0,dark):Muted(dark);Fill(g,tag,palette.StatusFill(ink),tag.Height/2);DrawLabel(g,status,tag,VisualDesign.LargeFont?11:10,false,ink,StringAlignment.Center);}
                if(reveal)DrawSymbol(g,"chevron",GridLayout.Chevron(tile,status),Muted(dark));
                var bar=GridLayout.Bar(tile);if(VisualDesign.HighContrast){using(var path=VisualDesign.RoundRect(bar.X+.5f,bar.Y+.5f,bar.Width-1,bar.Height-1,2))using(var pen=new Pen(Fg(dark),1))g.DrawPath(pen,path);}else Fill(g,bar,Track(dark),2);
                if(!sleeping&&view!=null&&view.Percent.HasValue){bar.Width*=Math.Max(0,Math.Min(100,view.Percent.Value))/100f*VisualDesign.BatteryFillProgress(batteryProgress);if(bar.Width>0)Fill(g,bar,view.Fresh?ThemePalette.LevelColor(view.Percent,dark):Muted(dark),2);}
            },false,1,2,focus);
        }
        static void PaintGridContent(Graphics g,WirelessLayout layout,DpiConfig dpi,AudioReading audio,AudioCardView audioCard,bool dark,PanelFeedback feedback,TrayContext owner=null,float batteryProgress=1) {
            g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
            foreach(var card in layout.Cards)GridTile(g,layout,card,dark,feedback,batteryProgress);
            var surface=VisualDesign.PanelCardSurface(layout.Grid.Controls);DrawPanelSurface(g,surface,dark,null);
            var saved=g.Save();g.SetClip(layout.Grid.Viewport,CombineMode.Intersect);
            try{
            foreach(var group in layout.Grid.Groups){DrawLabel(g,group.Audio?"音源":group.Card.Name+" · DPI",group.Title,VisualDesign.SmallFontSize,false,Muted(dark),StringAlignment.Near);
                if(group.Audio)AudioControls(g,0,audio,audioCard,dark,feedback,-1,group,layout.Grid.GroupSurface);
                else if(group.Card.NativeMouse)NativeDpiControls(g,dpi,0,dark,feedback,-1,group,layout.Grid.GroupSurface);
                else WirelessDpiControls(g,owner,group.Card,layout.All.IndexOf(group.Card),dark,feedback,layout);
            }
            }finally{g.Restore(saved);}
            if(layout.Grid.Overflow&&layout.Grid.Viewport.Height>=4){
                var view=layout.Grid.Viewport;float height=Math.Max(16,view.Height*view.Height/layout.Grid.ContentHeight),top=view.Top+(view.Height-height)*layout.Grid.ScrollOffset/layout.Grid.MaximumScroll;
                Fill(g,new RectangleF(VisualDesign.PanelWidth-5,view.Top+2,2,Math.Max(1,view.Height-4)),Track(dark),1);
                Fill(g,new RectangleF(VisualDesign.PanelWidth-5,top+2,2,Math.Max(1,height-4)),Muted(dark),1);
            }
            if(layout.Grid.Groups.Count>0)RingSeparator(g,layout.VolumeTop,dark);SystemVolume(g,layout.VolumeRect,audio,dark,feedback,false);
        }
        static void PaintGrid(Graphics g,BatteryReading current,BatteryReading lastGood,DpiConfig dpi,string dpiError,HeadsetReading headset,AudioReading audio,bool dark,PanelFeedback feedback,float batteryProgress) {
            var layout=GridLayout.Native(current,lastGood,dpi,headset,audio);PaintGridContent(g,layout,dpi,audio,VisualDesign.AudioCardFor(headset,audio),dark,feedback,null,batteryProgress);
            var mouse=UiState.Mouse(current,lastGood,DateTime.UtcNow);string failure=feedback.DpiFailure??feedback.AudioFailure;
            string status=failure??(feedback.ManualRefreshing?"正在同步…":feedback.ShowPendingHints&&feedback.PendingStage>=0?"DPI等待确认…":feedback.ShowPendingHints&&!String.IsNullOrEmpty(feedback.PendingOutput)?"音源等待确认…":mouse.Percent.HasValue&&!mouse.Fresh?"上次确认 "+mouse.TimeText:UiState.SyncText(mouse,DateTime.UtcNow));
            DrawSync(g,status,layout.Footer,VisualDesign.PanelWidth-52,dark,!String.IsNullOrEmpty(failure),feedback);DrawMore(g,layout.Footer,dark,feedback);
        }
        static void PaintRingWireless(Graphics g,TrayContext owner,WirelessLayout layout,bool dark,PanelFeedback feedback,float batteryProgress) {
            feedback=feedback??new PanelFeedback();var saved=BeginRingPanel(g,layout.VolumeRect.Bottom,dark);
            foreach(var card in layout.Cards){
                if(card.Top>0)RingSeparator(g,card.Top,dark);int index=layout.All.IndexOf(card);string subtitle=card.Subtitle;var view=card.Battery;
                if(card.NativeMouse){subtitle=MouseSubtitle(view,owner.Dpi,owner.DpiError);RingHeader(g,card.Name,subtitle,"mouse",view,card.Top,dark,feedback,2,false,false,batteryProgress);NativeDpiControls(g,owner.Dpi,card.Top,dark,feedback);}
                else if(card.NativeHeadset||card.AudioOnly){RingHeader(g,card.Name,card.AudioOnly?card.Subtitle:HeadsetSubtitle(view),"headset",view,card.Top,dark,feedback,card.AudioOnly?200:3,card.AudioOnly,false,batteryProgress);AudioControls(g,card.Top,owner.Audio,card.AudioOnly?UiState.AudioCard(owner.Audio,DeviceSettings.Current,owner.Headset,owner.WirelessDevices):null,dark,feedback);}
                else {
                    if(subtitle==null)subtitle=WirelessLayout.Subtitle(card.Device,view);
                    if(WirelessLayout.DpiStageCount(card.Device)>0)subtitle=WirelessLayout.DpiSubtitle(card.Device,owner.DesiredWirelessDpi(card.Id,card.Device.Dpi.TargetToken),owner.WirelessDpiFailure(card.Id,card.Device.Dpi.TargetToken)??card.Device.Dpi.Error);
                    string icon=card.Kind=="keyboard"?"keyboard":card.Kind=="headset"?"headset":card.Kind=="mouse"?"mouse":card.Kind=="gamepad"?"gamepad":"info";
                    RingHeader(g,card.Name,subtitle,icon,view,card.Top,dark,feedback,1000+index,false,RingSleeping(card.Device),batteryProgress);WirelessDpiControls(g,owner,card,index,dark,feedback);
                }
            }
            if(layout.HasVolume){RingSeparator(g,layout.VolumeTop,dark);SystemVolume(g,layout.VolumeRect,owner.Audio,dark,feedback,false);}g.Restore(saved);
            WirelessFooter(g,owner,layout,dark,feedback);
        }
        public static void Paint(Graphics g,BatteryReading current,BatteryReading lastGood,string error,bool busy,DpiConfig dpi,string dpiError,bool dpiBusy,bool dark,HeadsetReading headset,AudioReading audio,float openProgress=1,int hoverStage=-1,bool hoverRefresh=false,float headsetProgress=1,int hoverAudio=-1,PanelFeedback feedback=null,bool hoverSettings=false,float batteryProgress=1) {
            if(g==null)return;
            if(VisualDesign.GridLayout){PaintGrid(g,current,lastGood,dpi,dpiError,headset,audio,dark,feedback??new PanelFeedback {HoverAction=hoverRefresh?0:hoverSettings?1:hoverStage>=0?10+hoverStage:hoverAudio>=0?100+hoverAudio:-1},batteryProgress);return;}
            if(VisualDesign.RingLayout){PaintRing(g,current,lastGood,dpi,dpiError,dark,headset,audio,headsetProgress,feedback??new PanelFeedback {HoverAction=hoverRefresh?0:hoverSettings?1:hoverStage>=0?10+hoverStage:hoverAudio>=0?100+hoverAudio:-1},batteryProgress);return;}
            g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
            var preferences=DeviceSettings.Current;var now=DateTime.UtcNow;var mouse=UiState.Mouse(current,lastGood,now);var ear=UiState.Headset(headset,now);
            var audioCard=VisualDesign.AudioCardFor(headset,audio);
            int panelWidth=VisualDesign.PanelWidth,headsetHeight=VisualDesign.HeadsetCardHeight;
            int mouseHeight=VisualDesign.MouseCardHeight(dpi);bool visible=VisualDesign.ShouldShowHeadset(headset)||audioCard!=null;float progress=visible?Math.Max(0,Math.Min(1,headsetProgress)):0;
            DrawPanelCard(g,0,mouseHeight,dark,mouse);
            string dpiText="DPI未读取";
            if(dpi!=null&&dpi.Stages!=null&&dpi.ActiveStage>=0&&dpi.ActiveStage<dpi.Stages.Count)dpiText=(String.IsNullOrEmpty(dpiError)?"当前 ":"上次 ")+dpi.Stages[dpi.ActiveStage].XDpi+" DPI";
            string subtitle=mouse.Percent.HasValue&&!mouse.Fresh?"上次确认 "+mouse.TimeText:!mouse.Percent.HasValue?"等待设备回读":dpiText;
            Header(g,preferences.MouseAlias,subtitle,"mouse",0,mouseHeight,dark,feedback,2);Meter(g,mouse,0,mouseHeight,dark,feedback,4,batteryProgress);
            NativeDpiControls(g,dpi,0,dark,feedback,hoverStage);
            float top=mouseHeight+VisualDesign.CardGap;
            float scaleX,scaleY;
            using(var matrix=g.Transform){var transform=matrix.Elements;scaleX=(float)Math.Sqrt(transform[0]*transform[0]+transform[1]*transform[1]);scaleY=(float)Math.Sqrt(transform[2]*transform[2]+transform[3]*transform[3]);}
            if(progress>0)using(var layer=new Bitmap(Math.Max(1,(int)Math.Ceiling(panelWidth*scaleX)),Math.Max(1,(int)Math.Ceiling(headsetHeight*scaleY)),PixelFormat.Format32bppPArgb)) {
                using(var drawing=Graphics.FromImage(layer)) {
                    drawing.ScaleTransform(scaleX,scaleY);
                    drawing.Clear(Color.Transparent);drawing.SmoothingMode=SmoothingMode.AntiAlias;drawing.PixelOffsetMode=PixelOffsetMode.HighQuality;drawing.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
                    DrawPanelCard(drawing,0,headsetHeight,dark,audioCard==null?ear:null);
                    if(audioCard==null){Header(drawing,preferences.HeadsetAlias,ear.Percent.HasValue&&!ear.Fresh?"上次确认 "+ear.TimeText:ear.Percent.HasValue?"已连接":"电量未读取","headset",0,headsetHeight,dark,feedback,3);Meter(drawing,ear,0,headsetHeight,dark,feedback,5,batteryProgress);}
                    else Header(drawing,"耳机",audioCard.Subtitle,"headset",0,headsetHeight,dark,feedback,200,true);
                    AudioControls(drawing,0,audio,audioCard,dark,feedback,hoverAudio);
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
        internal static void PaintWireless(Graphics g,TrayContext owner,WirelessLayout layout,bool dark,PanelFeedback feedback,float batteryProgress=1) {
            if(VisualDesign.GridLayout){feedback=feedback??new PanelFeedback();var audioGroup=layout.Grid.Groups.Find(delegate(GridControlGroup group){return group.Audio;});PaintGridContent(g,layout,owner.Dpi,owner.Audio,audioGroup!=null&&audioGroup.Card.AudioOnly?UiState.AudioCard(owner.Audio,DeviceSettings.Current,owner.Headset,owner.WirelessDevices):null,dark,feedback,owner,batteryProgress);WirelessFooter(g,owner,layout,dark,feedback);return;}
            if(VisualDesign.RingLayout){PaintRingWireless(g,owner,layout,dark,feedback,batteryProgress);return;}
            g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
            int panelWidth=VisualDesign.PanelWidth;
            feedback=feedback??new PanelFeedback();
            foreach(var card in layout.Cards) {
                var saved=g.Save();
                g.SetClip(new RectangleF(0,card.Top,panelWidth,card.Height),CombineMode.Intersect);
                if(card.NativeMouse) {
                    g.TranslateTransform(0,card.Top);
                    Paint(g,owner.Current,owner.LastGood,owner.ErrorMessage,owner.IsRefreshing,owner.Dpi,owner.DpiError,owner.IsDpiRefreshing,dark,null,owner.Audio,1,feedback.HoverAction>=10&&feedback.HoverAction<18?feedback.HoverAction-10:-1,false,0,-1,feedback,false,batteryProgress);
                }else if(card.NativeHeadset||card.AudioOnly) {
                    g.TranslateTransform(0,card.Top-VisualDesign.MouseCardHeight(owner.Dpi)-8);
                    Paint(g,owner.Current,owner.LastGood,owner.ErrorMessage,owner.IsRefreshing,owner.Dpi,owner.DpiError,owner.IsDpiRefreshing,dark,owner.Headset,owner.Audio,1,-1,false,1,feedback.HoverAction>=100&&feedback.HoverAction<=102?feedback.HoverAction-100:-1,feedback,false,batteryProgress);
                }else {
                    int index=layout.All.IndexOf(card);
                    DrawPanelCard(g,card.Top,card.Height,dark,card.Battery);
                    string subtitle=card.Subtitle;
                    if(subtitle==null)subtitle=WirelessLayout.Subtitle(card.Device,card.Battery);
                    int dpiCount=WirelessLayout.DpiStageCount(card.Device);
                    if(dpiCount>0)subtitle=WirelessLayout.DpiSubtitle(card.Device,owner.DesiredWirelessDpi(card.Id,card.Device.Dpi.TargetToken),owner.WirelessDpiFailure(card.Id,card.Device.Dpi.TargetToken)??card.Device.Dpi.Error);
                    string icon=card.Kind=="keyboard"?"keyboard":card.Kind=="headset"?"headset":card.Kind=="mouse"?"mouse":card.Kind=="gamepad"?"gamepad":"info";
                    Header(g,card.Name,subtitle,icon,card.Top,card.Height,dark,feedback,1000+index);Meter(g,card.Battery,card.Top,card.Height,dark,feedback,2000+index,batteryProgress);
                    if(dpiCount>0) {
                        var dpi=card.Device.Dpi;bool enabled=owner.CanSetWirelessDpi(card.Id,dpi.TargetToken);int? pending=owner.DesiredWirelessDpi(card.Id,dpi.TargetToken);bool confirmed=WirelessLayout.DpiFresh(card.Device)&&String.IsNullOrEmpty(owner.WirelessDpiFailure(card.Id,dpi.TargetToken));
                        Fill(g,VisualDesign.StageTrackRect(card.Top,dpiCount),Track(dark),9);
                        for(int i=0;i<dpiCount;i++) {
                            var rect=WirelessLayout.DpiRect(card,i);int id=WirelessLayout.DpiActionId(index,i);bool waiting=pending==dpi.Stages[i],selected=confirmed&&dpi.Current==dpi.Stages[i];
                            Selection(g,rect,dpi.Stages[i].ToString(),null,selected,enabled||selected&&pending.HasValue,enabled&&feedback.HoverAction==id,enabled&&feedback.PressedAction==id,waiting,dark,feedback.KeyboardFocus==id,VisualDesign.PanelCardRect(card.Top,card.Height),id);
                        }
                    }
                }
                g.Restore(saved);
            }
            if(layout.HasVolume)SystemVolume(g,layout.VolumeRect,owner.Audio,dark,feedback);
            WirelessFooter(g,owner,layout,dark,feedback);
        }
        static void WirelessFooter(Graphics g,TrayContext owner,WirelessLayout layout,bool dark,PanelFeedback feedback) {
            int panelWidth=VisualDesign.PanelWidth;
            float top=layout.Footer;int footerHeight=VisualDesign.FooterHeight;float width=layout.Paged?(panelWidth-114):(panelWidth-52);
            string status=feedback.ManualRefreshing?"正在同步…":layout.All[0].Id=="empty"?owner.GenericScanning?"正在识别设备…":"暂无可读取电量的设备":layout.Paged?(layout.Start+1)+"–"+layout.End+" / "+layout.All.Count+" 台设备":"已识别 "+layout.All.Count+" 台设备";
            var failure=feedback.DpiFailure??feedback.AudioFailure??(!String.IsNullOrEmpty(owner.GenericHealthSummary)?owner.GenericHealthSummary:!String.IsNullOrEmpty(owner.GenericError)?"读取器暂不可用":null);
            DrawSync(g,failure??owner.GenericIdleSummary??status,top,width,dark,!String.IsNullOrEmpty(failure),feedback);
            if(layout.Paged) {
                FooterButton(g,VisualDesign.FooterPageRect(top,false),top,"back",902,layout.Start>0?feedback:null,dark,layout.Start>0?Fg(dark):Muted(dark));
                FooterButton(g,VisualDesign.FooterPageRect(top,true),top,"chevron",903,layout.End<layout.All.Count?feedback:null,dark,layout.End<layout.All.Count?Fg(dark):Muted(dark));
            }
            DrawMore(g,top,dark,feedback);
        }
        static void DrawMore(Graphics g,float top,bool dark,PanelFeedback feedback){
            FooterButton(g,VisualDesign.FooterMoreRect(top),top,"more",1,feedback,dark,Fg(dark));
        }
        static void FooterButton(Graphics g,RectangleF rect,float top,string icon,int action,PanelFeedback feedback,bool dark,Color color) {
            bool active=feedback!=null&&(feedback.HoverAction==action||feedback.PressedAction==action);
            if(active)Fill(g,rect,ThemePalette.Current(dark).SegmentHover,7);
            InteractionLayout.Paint(g,new RectangleF(0,top,VisualDesign.PanelWidth,VisualDesign.FooterHeight),rect,action,feedback,dark,false,delegate{
                DrawSymbol(g,icon,VisualDesign.CenteredRect(rect,14,14),VisualDesign.HighContrast&&active?ThemePalette.Current(dark).SelectedForeground:color);
            },false,1);
        }
        static RectangleF DrawSync(Graphics g,string text,float top,float maximumWidth,bool dark,bool failure,PanelFeedback feedback) {
            if(feedback!=null&&!String.IsNullOrEmpty(feedback.UpdatedNotice)){text=feedback.UpdatedNotice;failure=false;}
            var textRect=VisualDesign.FooterTextRect(top,maximumWidth);
            FooterButton(g,VisualDesign.FooterRefreshRect(top),top,"refresh",0,feedback,dark,Muted(dark));
            DrawLabel(g,text,textRect,VisualDesign.SubtitleFontSize,false,failure?ThemePalette.LevelColor(10,dark):Muted(dark),StringAlignment.Near);
            return textRect;
        }
    }
}
