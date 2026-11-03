using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AtkBattery;

namespace AtkBatteryTray {
    // Dialogs consume the panel's palette without deriving alternative control colors.
    internal sealed class DialogTheme {
        internal readonly bool Dark,HighContrast;
        internal readonly ThemePalette Palette;
        internal readonly Color Background,Card,Section,Foreground,Muted,Accent,Border,Track,Selected,SelectedForeground,Error;
        internal DialogTheme(bool dark){
            Dark=dark;HighContrast=VisualDesign.HighContrast;Palette=ThemePalette.Current(dark);
            Background=Palette.Background;Card=Section=Palette.Card;Foreground=Palette.Foreground;Border=Palette.Border;Track=Palette.Track;
            Selected=Palette.Selected;SelectedForeground=Palette.SelectedForeground;
            Muted=Palette.Muted;Accent=Palette.Accent;
            Error=HighContrast?SystemColors.WindowText:ThemePalette.LevelColor(0,dark);
        }
        internal Color Interaction(Color fill,bool pressed){
            if(HighContrast)return Selected;
            var overlay=Palette.Interaction(pressed);return ThemePalette.Blend(fill,overlay,overlay.A);
        }
    }

    internal static class ThemedTitleBar {
        [DllImport("dwmapi.dll",PreserveSig=true)] static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct NativeVersion {
            internal uint Size,Major,Minor,Build,Platform;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] internal string ServicePack;
        }
        [DllImport("ntdll.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] static extern int RtlGetVersion(ref NativeVersion version);
        internal static void Apply(IntPtr window,DialogTheme theme){
            if(VisualDesign.HighContrast||theme.HighContrast)return;
            try{
                var version=new NativeVersion {Size=(uint)Marshal.SizeOf(typeof(NativeVersion)),ServicePack=String.Empty};
                bool windows11=RtlGetVersion(ref version)==0&&version.Major>=10&&version.Build>=22000;
                Apply(theme,windows11,delegate(int attribute,int value){return DwmSetWindowAttribute(window,attribute,ref value,4);});
            }catch(DllNotFoundException){}catch(EntryPointNotFoundException){}
        }
        internal static void Apply(DialogTheme theme,bool windows11,Func<int,int,int> set){
            if(VisualDesign.HighContrast||theme.HighContrast)return;
            int dark=theme.Dark?1:0;if(set(20,dark)!=0)set(19,dark);
            if(windows11){set(33,2);set(35,ColorTranslator.ToWin32(theme.Background));set(36,ColorTranslator.ToWin32(theme.Foreground));}
        }
    }

    internal static class DialogScrollbars {
        [DllImport("uxtheme.dll",EntryPoint="SetWindowTheme",CharSet=CharSet.Unicode,ExactSpelling=true)] static extern int NativeSetWindowTheme(IntPtr window,string theme,string subId);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,ref RectangleRect rect);
        [StructLayout(LayoutKind.Sequential)] struct RectangleRect {internal int Left,Top,Right,Bottom;}
        internal static Func<IntPtr,string,string,int> SetWindowTheme=NativeSetWindowTheme;
        internal static void Track(Control control,DialogTheme theme){
            control.HandleCreated+=delegate{Apply(control,theme);};if(control.IsHandleCreated)Apply(control,theme);
        }
        static void Apply(Control control,DialogTheme theme){
            if(theme.HighContrast||VisualDesign.HighContrast)return;
            try{SetWindowTheme(control.Handle,theme.Dark?"DarkMode_Explorer":"Explorer",null);}
            catch(DllNotFoundException){}catch(EntryPointNotFoundException){}catch(ExternalException){}
        }
        internal static void Attach(TextBox input,DialogTheme theme){
            Track(input,theme);bool updating=false,ready=input.IsHandleCreated;
            EventHandler update=delegate{
                if(updating||!ready||!input.IsHandleCreated||!input.Multiline)return;
                updating=true;try{Update(input);}finally{updating=false;}
            };
            // ScrollBars recreates the EDIT handle. Wait until its creation has fully returned.
            input.HandleCreated+=delegate{ready=false;input.BeginInvoke((MethodInvoker)delegate{if(input.IsDisposed||!input.IsHandleCreated)return;ready=true;update(input,EventArgs.Empty);});};
            input.HandleDestroyed+=delegate{ready=false;};input.TextChanged+=update;input.ClientSizeChanged+=update;input.FontChanged+=update;
            update(input,EventArgs.Empty);
        }
        static RectangleRect TextRect(TextBox input){var rect=new RectangleRect();SendMessage(input.Handle,0x00B2,IntPtr.Zero,ref rect);return rect;}
        static void Update(TextBox input){
            var rect=TextRect(input);bool hadVertical=(input.ScrollBars&ScrollBars.Vertical)!=0,hadHorizontal=(input.ScrollBars&ScrollBars.Horizontal)!=0;
            int width=Math.Max(1,rect.Right-rect.Left+(hadVertical?SystemInformation.VerticalScrollBarWidth:0));
            int height=Math.Max(1,rect.Bottom-rect.Top+(hadHorizontal?SystemInformation.HorizontalScrollBarHeight:0));
            bool vertical=false,horizontal=false;var flags=TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix|TextFormatFlags.TextBoxControl|TextFormatFlags.ExpandTabs;
            // Start with the full viewport, then account for either scrollbar taking space.
            for(int pass=0;pass<3;pass++){
                int availableWidth=Math.Max(1,width-(vertical?SystemInformation.VerticalScrollBarWidth:0));
                var size=TextRenderer.MeasureText(input.Text+"\u200b",input.Font,new Size(input.WordWrap?availableWidth:Int32.MaxValue,Int32.MaxValue),flags|(input.WordWrap?TextFormatFlags.WordBreak:0));
                horizontal=!input.WordWrap&&size.Width>availableWidth;
                vertical=size.Height>height-(horizontal?SystemInformation.HorizontalScrollBarHeight:0);
            }
            var wanted=(vertical?ScrollBars.Vertical:ScrollBars.None)|(horizontal?ScrollBars.Horizontal:ScrollBars.None);
            if(input.ScrollBars!=wanted)input.ScrollBars=wanted;
            // The native EDIT control is authoritative for wrapped lines and trailing newlines.
            rect=TextRect(input);int lines=SendMessage(input.Handle,0x00BA,IntPtr.Zero,IntPtr.Zero).ToInt32();
            bool actualVertical=(long)lines*input.Font.Height>rect.Bottom-rect.Top;
            if(actualVertical!=vertical){wanted=(actualVertical?ScrollBars.Vertical:ScrollBars.None)|(horizontal?ScrollBars.Horizontal:ScrollBars.None);if(input.ScrollBars!=wanted)input.ScrollBars=wanted;}
        }
    }

    internal class ThemedDialog:Form {
        internal readonly DialogTheme Theme;
        readonly Font bodyFont,titleFont,descriptionFont,cardTitleFont;
        // Tests supply physical pixel work areas, independent of the host monitor.
        internal static Func<Control,Rectangle> WorkingAreaProvider=delegate(Control control){
            var form=control as Form;
            return (control.Visible?Screen.FromControl(control):form!=null&&form.Owner!=null?Screen.FromControl(form.Owner):Screen.FromPoint(Cursor.Position)).WorkingArea;
        };
        TableLayoutPanel content,frame;Panel viewport;Control buttons;
        Func<int,int> minimumContentHeight,preferredContentHeight;
        Size preferredClientSize;Rectangle lastWorkArea;bool fitting,layingOut,loaded,scaling;
        internal ThemedDialog():this(Style.Dark){}
        internal ThemedDialog(bool dark){
            Theme=new DialogTheme(dark);
            bodyFont=new Font("Microsoft YaHei UI",VisualDesign.BodyFontSize,FontStyle.Regular,GraphicsUnit.Pixel);
            titleFont=new Font("Microsoft YaHei UI",15,FontStyle.Bold,GraphicsUnit.Pixel);
            cardTitleFont=new Font("Microsoft YaHei UI",VisualDesign.DeviceNameFontSize,FontStyle.Bold,GraphicsUnit.Pixel);
            descriptionFont=new Font("Microsoft YaHei UI",VisualDesign.SubtitleFontSize,FontStyle.Regular,GraphicsUnit.Pixel);
            AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;Font=bodyFont;
            BackColor=Theme.Background;ForeColor=Theme.Foreground;Padding=new Padding(16);
            FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
            StartPosition=FormStartPosition.Manual;ShowInTaskbar=false;
        }
        // Keep each dialog's existing cards and button layout; only the content scrolls.
        internal void ConfigureLayout(TableLayoutPanel body,Control buttonBar,Func<int,int> minimum,Func<int,int> preferred=null){
            var accept=AcceptButton;var cancel=CancelButton;var active=ActiveControl;
            content=body;buttons=buttonBar;minimumContentHeight=minimum;preferredContentHeight=preferred;
            preferredClientSize=ClientSize;StartPosition=FormStartPosition.Manual;
            content.Controls.Remove(buttons);content.RowStyles.RemoveAt(content.RowStyles.Count-1);content.RowCount--;
            Controls.Remove(content);content.Dock=DockStyle.None;content.Anchor=AnchorStyles.Top|AnchorStyles.Left;
            viewport=new Panel {Name="DialogContentViewport",Dock=DockStyle.Fill,Margin=Padding.Empty};
            DialogScrollbars.Track(viewport,Theme);viewport.Controls.Add(content);
            frame=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};
            frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));frame.RowStyles.Add(new RowStyle(SizeType.Percent,100));frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            frame.Controls.Add(viewport,0,0);frame.Controls.Add(buttons,0,1);Controls.Add(frame);AcceptButton=accept;CancelButton=cancel;ActiveControl=active;
            frame.Layout+=delegate{LayoutClientArea();};
        }
        internal void FitToWorkingArea(bool center){
            if(!loaded||fitting||scaling||IsDisposed||Disposing)return;fitting=true;
            try{
                var area=WorkingAreaProvider(this);if(area.Width<=0||area.Height<=0)return;
                center|=area!=lastWorkArea;lastWorkArea=area;
                // The work area determines the requested size. Windows (or a caller's
                // MaximumSize) can still grant a smaller actual client rectangle.
                int chromeWidth=Width-ClientSize.Width,chromeHeight=Height-ClientSize.Height;
                int width=Math.Max(1,Math.Min(preferredClientSize.Width>0?preferredClientSize.Width:ClientSize.Width,area.Width-chromeWidth));
                int height=preferredClientSize.Height>0?preferredClientSize.Height:ClientSize.Height;
                if(content!=null&&preferredContentHeight!=null){
                    int bodyWidth=Math.Max(1,width-Padding.Horizontal);
                    height=Padding.Vertical+buttons.GetPreferredSize(new Size(bodyWidth,0)).Height+buttons.Margin.Vertical+preferredContentHeight(bodyWidth);
                }
                height=Math.Max(1,Math.Min(height,area.Height-chromeHeight));
                if(ClientSize!=new Size(width,height))ClientSize=new Size(width,height);
                int x=center?area.Left+(area.Width-Width)/2:Math.Max(area.Left,Math.Min(Left,area.Right-Width));
                int y=center?area.Top+(area.Height-Height)/2:Math.Max(area.Top,Math.Min(Top,area.Bottom-Height));
                Location=new Point(x,y);
            }finally{fitting=false;LayoutClientArea();}
        }
        void LayoutClientArea(){
            if(!loaded||content==null||fitting||layingOut||scaling||IsDisposed||Disposing)return;layingOut=true;
            try{
                // Client-size notifications can precede dock layout. Bound the frame
                // explicitly so no measurement uses the previously requested height.
                frame.Bounds=new Rectangle(Padding.Left,Padding.Top,Math.Max(0,ClientSize.Width-Padding.Horizontal),Math.Max(0,ClientSize.Height-Padding.Vertical));
                frame.PerformLayout();
                // Keep the existing flexible rows: shrink them to their readable
                // minimum first, then scroll only the content above the button bar.
                // The scrollbar reduces width, so remeasure wrapping with it present.
                for(int pass=0;pass<3;pass++){
                    int bodyWidth=Math.Max(1,viewport.ClientSize.Width);
                    content.Width=bodyWidth;content.PerformLayout();
                    int minimum=Math.Max(1,minimumContentHeight(bodyWidth));
                    bool scroll=minimum>viewport.ClientSize.Height;
                    viewport.AutoScroll=scroll;viewport.AutoScrollMinSize=new Size(0,scroll?minimum:0);
                    content.SetBounds(0,scroll?viewport.AutoScrollPosition.Y:0,Math.Max(1,viewport.ClientSize.Width),Math.Max(viewport.ClientSize.Height,minimum));
                    content.PerformLayout();
                }
            }finally{layingOut=false;}
        }
        protected override void OnLoad(EventArgs e){loaded=true;FitToWorkingArea(true);base.OnLoad(e);}
        protected override void OnShown(EventArgs e){base.OnShown(e);LayoutClientArea();}
        protected override void OnLayout(LayoutEventArgs e){base.OnLayout(e);LayoutClientArea();}
        protected override void OnClientSizeChanged(EventArgs e){base.OnClientSizeChanged(e);LayoutClientArea();}
        protected override void OnLocationChanged(EventArgs e){
            base.OnLocationChanged(e);if(loaded&&!fitting&&WorkingAreaProvider(this)!=lastWorkArea)FitToWorkingArea(true);
        }
        protected override void ScaleControl(SizeF factor,BoundsSpecified specified){
            bool wasScaling=scaling;scaling=true;
            try{
                base.ScaleControl(factor,specified);
                preferredClientSize=new Size((int)Math.Round(preferredClientSize.Width*factor.Width),(int)Math.Round(preferredClientSize.Height*factor.Height));
            }finally{scaling=wasScaling;}
        }
        protected override void WndProc(ref Message message){
            base.WndProc(ref message);
            // WM_DPICHANGED also covers Framework configurations without automatic DPI events.
            if(message.Msg==0x02E0&&loaded)FitToWorkingArea(true);
        }
        internal Label Caption(string text,bool title=false,bool description=false,bool cardTitle=false){
            return new Label {Text=text,AutoSize=true,UseMnemonic=false,BackColor=Theme.HighContrast?SystemColors.Window:Color.Transparent,ForeColor=description?Theme.Muted:Theme.Foreground,Font=title?titleFont:cardTitle?cardTitleFont:description?descriptionFont:bodyFont,Margin=new Padding(0,0,0,4)};
        }
        protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);ThemedTitleBar.Apply(Handle,Theme);}
        protected override void Dispose(bool disposing){base.Dispose(disposing);if(disposing){bodyFont.Dispose();titleFont.Dispose();descriptionFont.Dispose();cardTitleFont.Dispose();}}
    }

    internal enum ThemedButtonKind {Primary,Secondary,Text}
    internal sealed class ThemedButton:Button {
        readonly DialogTheme theme;readonly ThemedButtonKind kind;
        bool hovered,pressed;
        internal ThemedButton(DialogTheme theme,ThemedButtonKind kind=ThemedButtonKind.Secondary){
            this.theme=theme;this.kind=kind;
            FlatStyle=theme.HighContrast?FlatStyle.Standard:FlatStyle.Flat;FlatAppearance.BorderSize=0;UseVisualStyleBackColor=theme.HighContrast;
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,!theme.HighContrast);
            SetStyle(ControlStyles.SupportsTransparentBackColor,!theme.HighContrast);
            BackColor=theme.HighContrast?SystemColors.Control:Color.Transparent;ForeColor=theme.Foreground;Height=VisualDesign.SelectionHeight;Padding=new Padding(14,0,14,0);Margin=new Padding(VisualDesign.CardGap,0,0,0);
            AccessibleRole=AccessibleRole.PushButton;
        }
        float ScaleFactor {get{return Font.SizeInPoints*96f/72f/VisualDesign.BodyFontSize;}}
        public override Size GetPreferredSize(Size proposedSize){
            Size text;using(var font=new Font(Font.FontFamily,VisualDesign.DeviceNameFontSize*ScaleFactor,kind==ThemedButtonKind.Primary?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel))text=TextRenderer.MeasureText(Text,font,Size.Empty,TextFormatFlags.SingleLine);
            int width=Math.Max(MinimumSize.Width,text.Width+Padding.Horizontal),height=Math.Max(MinimumSize.Height,(int)Math.Round(VisualDesign.SelectionHeight*ScaleFactor));
            return new Size(MaximumSize.Width>0?Math.Min(width,MaximumSize.Width):width,MaximumSize.Height>0?Math.Min(height,MaximumSize.Height):height);
        }
        internal Color FillFor(bool hover,bool down){
            Color fill=kind==ThemedButtonKind.Text?theme.Background:Enabled&&kind==ThemedButtonKind.Primary?theme.Selected:theme.Track;
            return Enabled&&kind!=ThemedButtonKind.Primary&&(hover||down)?theme.Interaction(fill,down):fill;
        }
        internal Color TextFor(bool hover,bool down){
            if(!Enabled)return theme.Muted;
            if(theme.HighContrast&&(hover||down))return theme.SelectedForeground;
            return kind==ThemedButtonKind.Primary?theme.SelectedForeground:kind==ThemedButtonKind.Text?theme.Accent:theme.Foreground;
        }
        protected override void OnPaint(PaintEventArgs e){
            if(theme.HighContrast){base.OnPaint(e);return;}
            float scale=ScaleFactor;var state=e.Graphics.Save();e.Graphics.ScaleTransform(scale,scale);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            var rect=new RectangleF(0,0,Width/scale,Height/scale);
            if(kind!=ThemedButtonKind.Text){
                CompactRenderer.Fill(e.Graphics,rect,theme.Track,VisualDesign.SelectionRadius);
                CompactRenderer.Selection(e.Graphics,rect,Text,null,kind==ThemedButtonKind.Primary,Enabled,hovered,pressed,false,theme.Dark);
            }else{
                if(Enabled)CompactRenderer.Highlight(e.Graphics,rect,0,new PanelFeedback {HoverAction=hovered?0:-1,PressedAction=pressed?0:-1},theme.Dark);
                CompactRenderer.DrawLabel(e.Graphics,Text,rect,VisualDesign.DeviceNameFontSize,false,TextFor(hovered,pressed),StringAlignment.Center);
            }
            if(Enabled&&(hovered||pressed||Focused&&ShowFocusCues))CompactRenderer.Outline(e.Graphics,rect,theme.Accent,1);
            e.Graphics.Restore(state);
        }
        public override void NotifyDefault(bool value){base.NotifyDefault(value);Invalidate();}
        protected override void OnMouseEnter(EventArgs e){hovered=true;Invalidate();base.OnMouseEnter(e);}
        protected override void OnMouseLeave(EventArgs e){hovered=false;Invalidate();base.OnMouseLeave(e);}
        protected override void OnMouseDown(MouseEventArgs e){if(e.Button==MouseButtons.Left)pressed=true;Invalidate();base.OnMouseDown(e);}
        protected override void OnMouseUp(MouseEventArgs e){pressed=false;Invalidate();base.OnMouseUp(e);}
        protected override void OnMouseCaptureChanged(EventArgs e){pressed=false;Invalidate();base.OnMouseCaptureChanged(e);}
        protected override void OnKeyDown(KeyEventArgs e){if(e.KeyCode==Keys.Space)pressed=true;Invalidate();base.OnKeyDown(e);}
        protected override void OnKeyUp(KeyEventArgs e){pressed=false;Invalidate();base.OnKeyUp(e);}
        protected override void OnGotFocus(EventArgs e){Invalidate();base.OnGotFocus(e);}
        protected override void OnLostFocus(EventArgs e){pressed=false;Invalidate();base.OnLostFocus(e);}
        protected override void OnEnabledChanged(EventArgs e){hovered=pressed=false;Invalidate();base.OnEnabledChanged(e);}
    }

    internal class ThemedSection:Panel {
        protected readonly DialogTheme theme;
        internal ThemedSection(DialogTheme theme){
            this.theme=theme;
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,!theme.HighContrast);
            SetStyle(ControlStyles.SupportsTransparentBackColor,!theme.HighContrast);
            BackColor=theme.HighContrast?SystemColors.Window:Color.Transparent;ForeColor=theme.Foreground;Padding=new Padding(12);
            Margin=new Padding(0,0,0,VisualDesign.CardGap);
        }
        protected float ScaleFactor {get{return Font.SizeInPoints*96f/72f/VisualDesign.BodyFontSize;}}
        protected override void OnPaint(PaintEventArgs e){
            base.OnPaint(e);if(theme.HighContrast||Width<2||Height<2)return;
            float scale=ScaleFactor;var state=e.Graphics.Save();e.Graphics.ScaleTransform(scale,scale);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            CompactRenderer.DrawSurface(e.Graphics,new RectangleF(.5f,.5f,Width/scale-1,Height/scale-1),theme.Dark);e.Graphics.Restore(state);
        }
    }
    internal sealed class ThemedTextBox:ThemedSection {
        internal readonly TextBox Input;
        internal ThemedTextBox(DialogTheme theme,TextBox input):base(theme){
            Input=input;Padding=new Padding(input.Multiline?10:8);TabStop=false;
            input.BorderStyle=theme.HighContrast?BorderStyle.Fixed3D:BorderStyle.None;input.BackColor=theme.HighContrast?SystemColors.Window:theme.Track;input.ForeColor=theme.Foreground;
            input.Dock=DockStyle.Fill;input.Margin=Padding.Empty;Controls.Add(input);
            input.GotFocus+=delegate{Invalidate();};input.LostFocus+=delegate{Invalidate();};
            if(input.Multiline)DialogScrollbars.Attach(input,theme);
            Height=input.PreferredHeight+Padding.Vertical;
        }
        protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);if(Input!=null&&!Input.Multiline)Height=Input.PreferredHeight+Padding.Vertical;}
        protected override void OnPaint(PaintEventArgs e){
            if(theme.HighContrast)return;
            float scale=ScaleFactor;var state=e.Graphics.Save();e.Graphics.ScaleTransform(scale,scale);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            var rect=new RectangleF(0,0,Width/scale,Height/scale);CompactRenderer.Fill(e.Graphics,rect,theme.Track,VisualDesign.SelectionRadius);
            if(Input.ContainsFocus)CompactRenderer.Outline(e.Graphics,rect,theme.Accent,1);e.Graphics.Restore(state);
        }
    }

    // Retain the native radio/checkbox models, keyboard behavior and accessibility.
    internal sealed class ThemedSegments:Panel {
        readonly DialogTheme theme;int columns=4;bool arranging;
        internal ThemedSegments(DialogTheme theme){
            this.theme=theme;TabStop=false;AccessibleRole=AccessibleRole.Grouping;
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,!theme.HighContrast);
            BackColor=theme.HighContrast?SystemColors.Window:theme.Card;
        }
        float ScaleFactor {get{return Font.SizeInPoints*96f/72f/VisualDesign.BodyFontSize;}}
        int ColumnsFor(int width){
            int minimum=0;foreach(Control child in Controls)minimum=Math.Max(minimum,child.GetPreferredSize(Size.Empty).Width);
            return width>=minimum*4+6*ScaleFactor?4:2;
        }
        public override Size GetPreferredSize(Size proposedSize){int width=proposedSize.Width>0?proposedSize.Width:Width;int count=ColumnsFor(width);return new Size(width,(int)Math.Round(((4/count)*VisualDesign.SelectionHeight+(4/count-1)*2)*ScaleFactor));}
        protected override void OnLayout(LayoutEventArgs e){
            base.OnLayout(e);if(arranging||Controls.Count==0)return;arranging=true;
            try{
                columns=ColumnsFor(ClientSize.Width);int gap=(int)Math.Round(2*ScaleFactor),rowHeight=(int)Math.Round(VisualDesign.SelectionHeight*ScaleFactor);
                int width=Math.Max(1,(ClientSize.Width-(columns-1)*gap)/columns);
                for(int i=0;i<Controls.Count;i++)Controls[i].SetBounds(i%columns*(width+gap),i/columns*(rowHeight+gap),i%columns==columns-1?ClientSize.Width-i%columns*(width+gap):width,rowHeight);
                Height=(4/columns)*rowHeight+(4/columns-1)*gap;Invalidate();
            }finally{arranging=false;}
        }
        protected override void OnPaint(PaintEventArgs e){
            base.OnPaint(e);if(theme.HighContrast)return;
            float scale=ScaleFactor;var state=e.Graphics.Save();e.Graphics.ScaleTransform(scale,scale);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            for(int row=0;row<4/columns;row++)CompactRenderer.Fill(e.Graphics,new RectangleF(0,row*(VisualDesign.SelectionHeight+2),Width/scale,VisualDesign.SelectionHeight),theme.Track,VisualDesign.SelectionRadius);
            e.Graphics.Restore(state);
        }
        internal bool MoveSelection(ThemedSegment current,Keys key){
            int step=key==Keys.Left?-1:key==Keys.Right?1:key==Keys.Up?-columns:key==Keys.Down?columns:0;
            if(step==0||!Enabled)return false;
            int index=(Controls.IndexOf(current)+step+Controls.Count)%Controls.Count;var next=(RadioButton)Controls[index];
            if(next.Enabled){next.Focus();next.Checked=true;}return true;
        }
    }
    internal sealed class ThemedSegment:RadioButton {
        readonly DialogTheme theme;bool hovered,pressed;
        internal ThemedSegment(DialogTheme theme){
            this.theme=theme;AutoSize=false;Margin=Padding.Empty;
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,!theme.HighContrast);
            SetStyle(ControlStyles.SupportsTransparentBackColor,!theme.HighContrast);
            BackColor=theme.HighContrast?SystemColors.Window:Color.Transparent;ForeColor=theme.Foreground;
        }
        float ScaleFactor {get{return Font.SizeInPoints*96f/72f/VisualDesign.BodyFontSize;}}
        public override Size GetPreferredSize(Size proposedSize){
            if(theme.HighContrast)return base.GetPreferredSize(proposedSize);
            using(var font=new Font(Font.FontFamily,VisualDesign.DeviceNameFontSize*ScaleFactor,FontStyle.Regular,GraphicsUnit.Pixel))return new Size(TextRenderer.MeasureText(Text,font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Width+(int)Math.Round(20*ScaleFactor),(int)Math.Round(VisualDesign.SelectionHeight*ScaleFactor));
        }
        protected override bool ProcessDialogKey(Keys keyData){
            var group=Parent as ThemedSegments;if(group!=null&&(keyData&Keys.Modifiers)==Keys.None&&group.MoveSelection(this,keyData&Keys.KeyCode))return true;
            return base.ProcessDialogKey(keyData);
        }
        protected override void OnPaint(PaintEventArgs e){
            if(theme.HighContrast){base.OnPaint(e);return;}
            float scale=ScaleFactor;var state=e.Graphics.Save();e.Graphics.ScaleTransform(scale,scale);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            var rect=new RectangleF(0,0,Width/scale,Height/scale);
            CompactRenderer.Selection(e.Graphics,rect,Text,null,Checked,Enabled,hovered,pressed,false,theme.Dark);
            if(Focused&&ShowFocusCues)CompactRenderer.Outline(e.Graphics,rect,theme.Accent,1);e.Graphics.Restore(state);
        }
        protected override void OnCheckedChanged(EventArgs e){base.OnCheckedChanged(e);Invalidate();}
        protected override void OnMouseEnter(EventArgs e){hovered=true;Invalidate();base.OnMouseEnter(e);}
        protected override void OnMouseLeave(EventArgs e){hovered=pressed=false;Invalidate();base.OnMouseLeave(e);}
        protected override void OnMouseDown(MouseEventArgs e){pressed=e.Button==MouseButtons.Left;Invalidate();base.OnMouseDown(e);}
        protected override void OnMouseUp(MouseEventArgs e){pressed=false;Invalidate();base.OnMouseUp(e);}
        protected override void OnGotFocus(EventArgs e){Invalidate();base.OnGotFocus(e);}
        protected override void OnLostFocus(EventArgs e){pressed=false;Invalidate();base.OnLostFocus(e);}
        protected override void OnEnabledChanged(EventArgs e){hovered=pressed=false;Invalidate();base.OnEnabledChanged(e);}
    }
    internal sealed class ThemedSwitch:CheckBox {
        readonly DialogTheme theme;bool hovered,pressed;
        internal ThemedSwitch(DialogTheme theme){
            this.theme=theme;AutoSize=true;
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,!theme.HighContrast);
            SetStyle(ControlStyles.SupportsTransparentBackColor,!theme.HighContrast);
            BackColor=theme.HighContrast?SystemColors.Window:Color.Transparent;ForeColor=theme.Foreground;
        }
        float ScaleFactor {get{return Font.SizeInPoints*96f/72f/VisualDesign.BodyFontSize;}}
        public override Size GetPreferredSize(Size proposedSize){return theme.HighContrast?base.GetPreferredSize(proposedSize):new Size(TextRenderer.MeasureText(Text,Font).Width+(int)Math.Round(57*ScaleFactor),(int)Math.Round(VisualDesign.SelectionHeight*ScaleFactor));}
        protected override void OnPaint(PaintEventArgs e){
            if(theme.HighContrast){base.OnPaint(e);return;}
            float scale=ScaleFactor;var state=e.Graphics.Save();e.Graphics.ScaleTransform(scale,scale);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            var rect=new RectangleF(0,0,Width/scale,Height/scale);
            if(Enabled)CompactRenderer.Highlight(e.Graphics,rect,0,new PanelFeedback {HoverAction=hovered?0:-1,PressedAction=pressed?0:-1},theme.Dark);
            CompactRenderer.DrawLabel(e.Graphics,Text,new RectangleF(6,0,rect.Width-51,rect.Height),VisualDesign.DeviceNameFontSize,false,Enabled?theme.Foreground:theme.Muted,StringAlignment.Near);
            PopupPages.DrawToggle(e.Graphics,rect,Checked,Enabled,theme.Dark);
            if(Focused&&ShowFocusCues)CompactRenderer.Outline(e.Graphics,rect,theme.Accent,1);e.Graphics.Restore(state);
        }
        protected override void OnCheckedChanged(EventArgs e){base.OnCheckedChanged(e);Invalidate();}
        protected override void OnMouseEnter(EventArgs e){hovered=true;Invalidate();base.OnMouseEnter(e);}
        protected override void OnMouseLeave(EventArgs e){hovered=pressed=false;Invalidate();base.OnMouseLeave(e);}
        protected override void OnMouseDown(MouseEventArgs e){pressed=e.Button==MouseButtons.Left;Invalidate();base.OnMouseDown(e);}
        protected override void OnMouseUp(MouseEventArgs e){pressed=false;Invalidate();base.OnMouseUp(e);}
        protected override void OnGotFocus(EventArgs e){Invalidate();base.OnGotFocus(e);}
        protected override void OnLostFocus(EventArgs e){pressed=false;Invalidate();base.OnLostFocus(e);}
        protected override void OnEnabledChanged(EventArgs e){hovered=pressed=false;Invalidate();base.OnEnabledChanged(e);}
    }

    internal sealed class ThemedReleaseNotes:RichTextBox {
        readonly DialogTheme theme;bool updating,ready;
        [StructLayout(LayoutKind.Sequential)] struct Paragraph {
            internal uint Size,Mask;internal ushort Numbering,Effects;internal int StartIndent,RightIndent,Offset;internal ushort Alignment,TabCount;
            [MarshalAs(UnmanagedType.ByValArray,SizeConst=32)] internal int[] Tabs;
            internal int SpaceBefore,SpaceAfter,LineSpacing;internal short Style;internal byte LineRule,Outline;internal ushort ShadingWeight,ShadingStyle,NumberingStart,NumberingStyle,NumberingTab,BorderSpace,BorderWidth,Borders;
        }
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,ref Paragraph paragraph);
        internal ThemedReleaseNotes(DialogTheme theme){
            this.theme=theme;ReadOnly=true;Multiline=true;WordWrap=true;DetectUrls=false;BorderStyle=theme.HighContrast?BorderStyle.Fixed3D:BorderStyle.None;
            BackColor=theme.Card;ForeColor=theme.Foreground;ScrollBars=RichTextBoxScrollBars.None;
            DialogScrollbars.Track(this,theme);TextChanged+=delegate{FormatLines();UpdateViewport();};
        }
        internal int LineHeight {get{return (int)Math.Ceiling(Font.Height*1.4);}}
        internal int LineCountFor(int width){
            var flags=TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix|TextFormatFlags.TextBoxControl|TextFormatFlags.WordBreak|TextFormatFlags.ExpandTabs;
            var size=TextRenderer.MeasureText(Text+"\u200b",Font,new Size(Math.Max(1,width-4),Int32.MaxValue),flags);
            // GDI's line metric can differ from Font.Height (GDI+), notably for YaHei.
            int line=TextRenderer.MeasureText("A",Font,new Size(Int32.MaxValue,Int32.MaxValue),flags).Height;
            return Math.Max(1,(int)Math.Ceiling(size.Height/(double)Math.Max(1,line)));
        }
        internal int PreferredNotesHeight(int width){return Math.Max(3,Math.Min(12,LineCountFor(width)))*LineHeight+4;}
        void FormatLines(){
            if(!IsHandleCreated||theme.HighContrast)return;
            int start=SelectionStart,length=SelectionLength;SelectAll();
            float dpi;using(var g=CreateGraphics())dpi=g.DpiY;
            var paragraph=new Paragraph {Size=(uint)Marshal.SizeOf(typeof(Paragraph)),Mask=0x100,LineRule=4,LineSpacing=(int)Math.Round(LineHeight*1440f/dpi),Tabs=new int[32]};
            SendMessage(Handle,0x0447,IntPtr.Zero,ref paragraph);Select(start,length);
        }
        void UpdateViewport(){
            if(updating||!ready||!IsHandleCreated)return;updating=true;
            try{
                int width=ClientSize.Width+(ScrollBars==RichTextBoxScrollBars.Vertical?SystemInformation.VerticalScrollBarWidth:0);
                bool overflow=LineCountFor(width)*LineHeight+4>ClientSize.Height;
                var wanted=overflow?RichTextBoxScrollBars.Vertical:RichTextBoxScrollBars.None;if(ScrollBars!=wanted)ScrollBars=wanted;
            }finally{updating=false;}
        }
        // ScrollBars recreates RichEdit's handle; wait for creation and initial layout.
        protected override void OnHandleCreated(EventArgs e){ready=false;base.OnHandleCreated(e);FormatLines();BeginInvoke((MethodInvoker)delegate{if(!IsDisposed&&IsHandleCreated){ready=true;UpdateViewport();}});}
        protected override void OnHandleDestroyed(EventArgs e){ready=false;base.OnHandleDestroyed(e);}
        protected override void OnSizeChanged(EventArgs e){base.OnSizeChanged(e);UpdateViewport();}
        protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);FormatLines();UpdateViewport();}
    }

    internal sealed class ThemedMessageBody:Panel {
        readonly Label message;bool arranging;
        internal ThemedMessageBody(DialogTheme theme,string title,string text){
            message=new Label {Name="MessageText",AccessibleName=title,Text=text,UseMnemonic=false,ForeColor=theme.Foreground,BackColor=theme.HighContrast?SystemColors.Window:Color.Transparent};
            Controls.Add(message);DialogScrollbars.Track(this,theme);
        }
        public override Size GetPreferredSize(Size proposedSize){return message.GetPreferredSize(new Size(Math.Max(1,proposedSize.Width),0));}
        protected override void OnLayout(LayoutEventArgs e){
            if(arranging||message==null){base.OnLayout(e);return;}
            arranging=true;
            try{
                int width=Math.Max(1,ClientSize.Width+(VerticalScroll.Visible?SystemInformation.VerticalScrollBarWidth:0));
                var size=GetPreferredSize(new Size(width,0));bool overflow=size.Height>ClientSize.Height;
                if(overflow){width=Math.Max(1,width-SystemInformation.VerticalScrollBarWidth);size=GetPreferredSize(new Size(width,0));}
                AutoScroll=overflow;AutoScrollMinSize=new Size(0,overflow?size.Height:0);
                message.SetBounds(0,AutoScrollPosition.Y,width,size.Height);base.OnLayout(e);
            }finally{arranging=false;}
        }
    }

    internal sealed class ThemedProgress:ProgressBar {
        readonly DialogTheme theme;readonly Timer animation=new Timer {Interval=30};
        readonly System.Diagnostics.Stopwatch clock=new System.Diagnostics.Stopwatch();
        bool indeterminate;int value;
        internal ThemedProgress(DialogTheme theme){
            this.theme=theme;Height=VisualDesign.VolumeTrackHeight;TabStop=false;AccessibleRole=AccessibleRole.ProgressBar;
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,!theme.HighContrast);
            SetStyle(ControlStyles.SupportsTransparentBackColor,!theme.HighContrast);BackColor=theme.HighContrast?SystemColors.Control:Color.Transparent;
            animation.Tick+=delegate{UpdateAnimation();Invalidate();};
        }
        bool Reduced {get{return DeviceSettings.Current.ReducedMotion||!SystemInformation.IsMenuAnimationEnabled||theme.HighContrast;}}
        internal bool AnimationRunning {get{return animation.Enabled;}}
        internal bool Indeterminate {get{return indeterminate;}set{if(indeterminate==value)return;indeterminate=value;AccessibleDescription=value?"进度未知":this.value+"%";clock.Restart();if(theme.HighContrast)base.Value=value?100:this.value;UpdateAnimation();Invalidate();}}
        internal new int Value {get{return value;}set{this.value=Math.Max(0,Math.Min(100,value));base.Value=indeterminate&&theme.HighContrast?100:this.value;AccessibleDescription=indeterminate?"进度未知":this.value+"%";Invalidate();}}
        void UpdateAnimation(){if(indeterminate&&Visible&&IsHandleCreated&&!Reduced)animation.Start();else animation.Stop();}
        protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);UpdateAnimation();}
        protected override void OnVisibleChanged(EventArgs e){base.OnVisibleChanged(e);UpdateAnimation();}
        protected override void OnPaint(PaintEventArgs e){
            if(theme.HighContrast){base.OnPaint(e);return;}if(Width<=0||Height<=0)return;UpdateAnimation();
            float scale=Font.SizeInPoints*96f/72f/VisualDesign.BodyFontSize;var state=e.Graphics.Save();e.Graphics.ScaleTransform(scale,scale);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            float fraction=indeterminate?(Reduced?1:.28f):value/100f;
            float start=indeterminate&&!Reduced?(1-fraction)*(float)((Math.Sin(clock.Elapsed.TotalSeconds*Math.PI)+1)/2):0;
            CompactRenderer.DrawVolumeTrack(e.Graphics,new RectangleF(0,0,Width/scale,Height/scale),theme.Dark,fraction,start);e.Graphics.Restore(state);
        }
        protected override void Dispose(bool disposing){if(disposing)animation.Dispose();base.Dispose(disposing);}
    }

    internal sealed class ThemedAliasDialog:ThemedDialog {
        internal readonly TextBox Input;
        internal ThemedAliasDialog(string title,string value,int maxLength){
            Text=title;ClientSize=new Size(360,136);
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Input=new TextBox {Name="AliasInput",AccessibleName=title,Text=value,MaxLength=maxLength};
            var input=new ThemedTextBox(Theme,Input) {Dock=DockStyle.Top,Margin=Padding.Empty};
            var card=new ThemedSection(Theme) {Name="AliasCard",Dock=DockStyle.Fill};card.Controls.Add(input);
            var buttons=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false,Margin=Padding.Empty};
            var save=new ThemedButton(Theme,ThemedButtonKind.Primary) {Text="保存",DialogResult=DialogResult.OK,AutoSize=true,TabIndex=2};
            var cancel=new ThemedButton(Theme) {Text="取消",DialogResult=DialogResult.Cancel,AutoSize=true,TabIndex=1};
            buttons.Controls.AddRange(new Control[]{save,cancel});layout.Controls.Add(card,0,0);layout.Controls.Add(buttons,0,1);Controls.Add(layout);AcceptButton=save;CancelButton=cancel;ActiveControl=Input;
            ConfigureLayout(layout,buttons,delegate(int width){return card.Padding.Vertical+card.Margin.Vertical+input.Height;});
        }
    }

    internal static class ThemedMessage {
        internal static DialogResult Show(IWin32Window owner,string title,string text,MessageBoxButtons buttons){return Show(owner,title,text,buttons,MessageBoxDefaultButton.Button1);}
        internal static DialogResult Show(IWin32Window owner,string title,string text,MessageBoxButtons buttons,MessageBoxDefaultButton defaultButton){
            using(var dialog=Create(title,text,buttons,defaultButton))return dialog.ShowDialog(owner);
        }
        internal static ThemedDialog Create(string title,string text,MessageBoxButtons buttons,MessageBoxDefaultButton defaultButton=MessageBoxDefaultButton.Button1){
            var dialog=new ThemedDialog {Text=title,ClientSize=new Size(440,210)};
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var card=new ThemedSection(dialog.Theme) {Name="MessageCard",Dock=DockStyle.Fill};
            var content=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));content.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var heading=dialog.Caption(title,false,false,true);content.Controls.Add(heading,0,0);
            var message=new ThemedMessageBody(dialog.Theme,title,text) {Dock=DockStyle.Fill,Margin=new Padding(0,VisualDesign.CardGap,0,0)};
            content.Controls.Add(message,0,1);card.Controls.Add(content);layout.Controls.Add(card,0,0);
            var bar=new FlowLayoutPanel {Dock=DockStyle.Fill,AutoSize=true,WrapContents=false,FlowDirection=FlowDirection.RightToLeft,Margin=Padding.Empty};
            DialogResult[] results;
            switch(buttons){
                case MessageBoxButtons.OK:results=new[]{DialogResult.OK};break;
                case MessageBoxButtons.OKCancel:results=new[]{DialogResult.OK,DialogResult.Cancel};break;
                case MessageBoxButtons.YesNo:results=new[]{DialogResult.Yes,DialogResult.No};break;
                case MessageBoxButtons.YesNoCancel:results=new[]{DialogResult.Yes,DialogResult.No,DialogResult.Cancel};break;
                case MessageBoxButtons.RetryCancel:results=new[]{DialogResult.Retry,DialogResult.Cancel};break;
                case MessageBoxButtons.AbortRetryIgnore:results=new[]{DialogResult.Abort,DialogResult.Retry,DialogResult.Ignore};break;
                default:dialog.Dispose();throw new ArgumentOutOfRangeException("buttons");
            }
            int selected=Math.Min(results.Length-1,(int)defaultButton/256);
            ThemedButton defaultControl=null,cancelControl=null;
            for(int i=results.Length-1;i>=0;i--){
                var result=results[i];string label=result==DialogResult.OK?"确定":result==DialogResult.Cancel?"取消":result==DialogResult.Yes?"是":result==DialogResult.No?"否":result==DialogResult.Retry?"重试":result==DialogResult.Abort?"中止":"忽略";
                var button=new ThemedButton(dialog.Theme,i==0?ThemedButtonKind.Primary:ThemedButtonKind.Secondary) {Name="Message"+result,Text=label,DialogResult=result,AutoSize=true,TabIndex=i};
                bar.Controls.Add(button);if(i==selected)defaultControl=button;
                if(result==DialogResult.Cancel||buttons==MessageBoxButtons.OK)cancelControl=button;
            }
            // Yes/No and Abort/Retry/Ignore have no cancel result, like MessageBox.
            if(cancelControl==null){dialog.ControlBox=false;dialog.FormClosing+=delegate(object sender,FormClosingEventArgs e){if(dialog.DialogResult==DialogResult.Cancel)e.Cancel=true;};}
            else if(buttons==MessageBoxButtons.OK)dialog.FormClosing+=delegate{if(dialog.DialogResult==DialogResult.Cancel||dialog.DialogResult==DialogResult.None)dialog.DialogResult=DialogResult.OK;};
            layout.Controls.Add(bar,0,1);dialog.Controls.Add(layout);
            Func<int,int> surrounding=delegate(int width){return card.Padding.Vertical+card.Margin.Vertical+heading.GetPreferredSize(new Size(Math.Max(1,width-card.Padding.Horizontal),0)).Height+heading.Margin.Vertical+message.Margin.Vertical;};
            dialog.ConfigureLayout(layout,bar,delegate(int width){return surrounding(width)+Math.Min(3*message.Font.Height,message.GetPreferredSize(new Size(Math.Max(1,width-card.Padding.Horizontal),0)).Height);},delegate(int width){
                float scale=dialog.Font.SizeInPoints*96f/72f/VisualDesign.BodyFontSize;
                int maximum=(int)Math.Round(560*scale)-dialog.Padding.Vertical-bar.GetPreferredSize(new Size(width,0)).Height;
                return Math.Min(maximum,surrounding(width)+message.GetPreferredSize(new Size(Math.Max(1,width-card.Padding.Horizontal),0)).Height);
            });
            dialog.AcceptButton=defaultControl;dialog.CancelButton=cancelControl;dialog.ActiveControl=defaultControl;return dialog;
        }
    }
}
