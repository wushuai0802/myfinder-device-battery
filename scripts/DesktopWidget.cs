using System;
using System.Drawing;
using System.Windows.Forms;
using AtkBattery;

namespace AtkBatteryTray {
    // A read-only view of the same snapshots used by the tray; never polls hardware.
    internal sealed class DesktopWidget:Form {
        readonly TrayContext owner;
        readonly Action hideWidget;
        readonly Action<Point> savePosition;
        readonly Action<int> saveOpacity;
        readonly Timer repaint=new Timer();
        readonly Label title=new Label();
        readonly FlowLayoutPanel rows=new FlowLayoutPanel();
        sealed class BatteryCard:Control {
            readonly WirelessCard card;
            internal BatteryCard(WirelessCard value){card=value;DoubleBuffered=true;}
            protected override void OnPaint(PaintEventArgs e) {
                base.OnPaint(e);bool dark=Style.Dark;var palette=ThemePalette.Current(dark);
                var g=e.Graphics;g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var rect=new RectangleF(1,1,Width-3,Height-3);
                using(var path=VisualDesign.RoundRect(rect.X,rect.Y,rect.Width,rect.Height,10)) {
                    using(var brush=new SolidBrush(ThemePalette.Blend(palette.Card,palette.Accent,dark?14:7)))g.FillPath(brush,path);
                    using(var pen=new Pen(palette.Border))g.DrawPath(pen,path);
                }
                var battery=card.Battery;int? percent=battery==null?null:battery.Percent;
                Style.Ring(g,new RectangleF(12,15,38,38),percent,battery!=null&&battery.Charging,ThemePalette.LevelColor(percent,dark));
                VisualDesign.DrawLabel(g,card.Name,new RectangleF(62,10,Width-76,24),VisualDesign.DeviceNameFontSize,true,palette.Foreground,StringAlignment.Near);
                string subtitle=battery!=null&&battery.Charging?"充电中":battery!=null&&battery.Percent.HasValue?battery.Fresh?"已连接":battery.SystemProvided?"系统提供":"历史读数":"电量暂不可读";
                VisualDesign.DrawLabel(g,subtitle,new RectangleF(62,35,Width-76,20),VisualDesign.SubtitleFontSize,false,palette.Muted,StringAlignment.Near);
            }
        }
        string lastText;
        bool dragging;
        Point dragStart,windowStart;

        internal static Point KeepVisible(Point point,Size size,Rectangle work) {
            return new Point(Math.Max(work.Left,Math.Min(point.X,work.Right-size.Width)),
                Math.Max(work.Top,Math.Min(point.Y,work.Bottom-size.Height)));
        }

        internal DesktopWidget(TrayContext context,Action hide,Action<Point> moved,Action<int> opacityWriter=null) {
            owner=context;hideWidget=hide;savePosition=moved;saveOpacity=opacityWriter;
            Text="设备电量悬浮窗";FormBorderStyle=FormBorderStyle.None;
            ShowInTaskbar=false;TopMost=false;StartPosition=FormStartPosition.Manual;
            AutoScaleMode=AutoScaleMode.Dpi;ClientSize=new Size(270,140);Padding=new Padding(1);
            MaximizeBox=false;MinimizeBox=false;
            title.Text="设备电量";title.Dock=DockStyle.Top;title.Height=34;
            title.TextAlign=ContentAlignment.MiddleLeft;title.Padding=new Padding(10,0,0,0);
            title.MouseDown+=delegate(object s,MouseEventArgs e){if(e.Button!=MouseButtons.Left)return;dragging=true;dragStart=Cursor.Position;windowStart=Location;title.Capture=true;};
            title.MouseMove+=delegate{if(dragging)Location=new Point(windowStart.X+Cursor.Position.X-dragStart.X,windowStart.Y+Cursor.Position.Y-dragStart.Y);};
            title.MouseUp+=delegate{if(!dragging)return;dragging=false;title.Capture=false;RememberPosition();};
            title.MouseCaptureChanged+=delegate{dragging=false;};
            rows.Dock=DockStyle.Fill;rows.FlowDirection=FlowDirection.TopDown;
            rows.WrapContents=false;rows.AutoScroll=true;rows.Padding=new Padding(9,4,9,8);
            Controls.Add(rows);Controls.Add(title);
            var menu=new ContextMenuStrip();
            menu.Items.Add("重新同步",null,delegate{owner.RefreshBattery();});
            menu.Items.Add("打开设备面板",null,delegate{owner.ShowDetails();});
            var opacity=new ToolStripMenuItem("不透明度（越低越透明）");
            foreach(int value in new[]{55,70,85,100}) {
                int selected=value;var item=new ToolStripMenuItem(selected+"%"){Checked=DeviceSettings.Current.DesktopWidgetOpacity==selected};
                item.Click+=delegate{SetOpacity(selected);};opacity.DropDownItems.Add(item);
            }
            var custom=new NumericUpDown {Minimum=40,Maximum=100,Value=DeviceSettings.Current.DesktopWidgetOpacity,Width=130};
            custom.ValueChanged+=delegate{SetOpacity((int)custom.Value);};
            opacity.DropDownItems.Add(new ToolStripSeparator());
            opacity.DropDownItems.Add(new ToolStripControlHost(custom));
            opacity.DropDownOpening+=delegate{
                custom.Value=(decimal)Math.Round(Opacity*100);
                foreach(ToolStripItem entry in opacity.DropDownItems){var choice=entry as ToolStripMenuItem;if(choice!=null)choice.Checked=choice.Text==((int)custom.Value)+"%";}
            };
            menu.Items.Add(opacity);
            menu.Items.Add("关闭悬浮窗",null,delegate{hideWidget();});
            ContextMenuStrip=menu;title.ContextMenuStrip=menu;rows.ContextMenuStrip=menu;
            repaint.Interval=1000;repaint.Tick+=delegate{UpdateReadings();};
            Shown+=delegate{UpdateReadings();repaint.Start();};
            ResizeEnd+=delegate{RememberPosition();};
            FormClosing+=delegate(object s,FormClosingEventArgs e){if(e.CloseReason==CloseReason.UserClosing){e.Cancel=true;hideWidget();}};
            SetOpacity(DeviceSettings.Current.DesktopWidgetOpacity);
            var saved=DeviceSettings.Current.DesktopWidgetPosition;
            var work=saved==null?Screen.PrimaryScreen.WorkingArea:Screen.FromPoint(saved.Point).WorkingArea;
            Location=KeepVisible(saved==null?new Point(work.Right-Width-24,work.Top+24):saved.Point,Size,work);
        }

        void RememberPosition(){Location=KeepVisible(Location,Size,Screen.FromPoint(Location).WorkingArea);savePosition(Location);}
        void SetOpacity(int percent) {
            percent=Math.Max(40,Math.Min(100,percent));
            if(saveOpacity!=null&&DeviceSettings.Current.DesktopWidgetOpacity!=percent) {
                try {saveOpacity(percent);}
                catch(Exception){SafeDiagnostic.Transition("desktop_widget_opacity","save_failed");return;}
            }
            Opacity=VisualDesign.HighContrast?1:percent/100.0;
        }
        protected override void OnHandleCreated(EventArgs e) {
            base.OnHandleCreated(e);
            UpdateShape();
        }
        protected override void OnResize(EventArgs e) {
            base.OnResize(e);
            if(IsHandleCreated)UpdateShape();
        }
        void UpdateShape(){using(var path=VisualDesign.RoundRect(0,0,Width,Height,12)){var previous=Region;Region=new Region(path);if(previous!=null)previous.Dispose();}}
        void UpdateReadings() {
            var cards=WirelessLayout.Build(owner,0,Int32.MaxValue).All;
            var text=new System.Text.StringBuilder();
            foreach(var card in cards) {
                if(card.AudioOnly)continue;
                var battery=card.Battery;
                text.Append(card.Name).Append("  ");
                text.Append(battery!=null&&battery.Percent.HasValue?battery.Percent.Value+"%":"--");
                if(battery!=null&&battery.Charging)text.Append(" · 充电中");
                if(battery!=null&&battery.Percent.HasValue&&!battery.Fresh)text.Append(" · 历史/系统读数");
                text.AppendLine();
            }
            if(text.Length==0)text.Append("暂无可显示的电量设备");
            bool dark=Style.Dark;
            var palette=ThemePalette.Current(dark);
            Opacity=VisualDesign.HighContrast?1:DeviceSettings.Current.DesktopWidgetOpacity/100.0;
            BackColor=palette.Card;ForeColor=palette.Foreground;title.BackColor=palette.Card;title.ForeColor=palette.Muted;
            string next=dark+"|"+DeviceSettings.Current.ThemeStyle+"|"+DeviceSettings.Current.FontSize+"|"+VisualDesign.HighContrast+"|"+text;
            if(next==lastText)return;lastText=next;
            while(rows.Controls.Count>0)rows.Controls[0].Dispose();
            foreach(var card in cards) {
                if(card.AudioOnly)continue;
                rows.Controls.Add(new BatteryCard(card){Width=ClientSize.Width-40,Height=70,Margin=new Padding(0,0,0,7),ContextMenuStrip=ContextMenuStrip});
            }
            if(rows.Controls.Count==0)rows.Controls.Add(new Label {Text="暂无可显示的电量设备",Width=ClientSize.Width-40,Height=50,ForeColor=palette.Muted,TextAlign=ContentAlignment.MiddleCenter,ContextMenuStrip=ContextMenuStrip});
            ClientSize=new Size(ClientSize.Width,Math.Min(440,Math.Max(110,50+rows.Controls.Count*77)));
            Location=KeepVisible(Location,Size,Screen.FromPoint(Location).WorkingArea);
        }
        protected override void Dispose(bool disposing) {
            if(disposing){repaint.Stop();repaint.Dispose();if(ContextMenuStrip!=null)ContextMenuStrip.Dispose();}
            base.Dispose(disposing);
        }
    }
}