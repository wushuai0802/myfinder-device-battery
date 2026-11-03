using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AtkBattery;

namespace AtkBatteryTray {
    // Dialog-only roles. The panel palette and its exact color values stay intact.
    internal sealed class DialogTheme {
        internal readonly bool Dark,HighContrast;
        internal readonly ThemePalette Palette;
        internal readonly Color Card,Section,Foreground,Muted,Accent,Border,Track,Selected,SelectedForeground,Error;
        internal DialogTheme(bool dark){
            Dark=dark;HighContrast=VisualDesign.HighContrast;Palette=ThemePalette.Current(dark);
            Card=Palette.Card;Foreground=Palette.Foreground;Border=Palette.Border;Track=Palette.Track;
            Selected=Palette.Selected;SelectedForeground=Palette.SelectedForeground;
            Section=HighContrast?SystemColors.Window:ThemePalette.Blend(Card,Foreground,10);
            Muted=HighContrast?SystemColors.GrayText:Readable(Palette.Muted,Section,4.5);
            Accent=HighContrast?SystemColors.Highlight:Readable(Palette.Accent,Section,4.5);
            Error=HighContrast?SystemColors.WindowText:ThemePalette.LevelColor(0,dark);
        }
        static double Luminance(Color c){
            double[] v={c.R/255.0,c.G/255.0,c.B/255.0};
            for(int i=0;i<v.Length;i++)v[i]=v[i]<=.04045?v[i]/12.92:Math.Pow((v[i]+.055)/1.055,2.4);
            return .2126*v[0]+.7152*v[1]+.0722*v[2];
        }
        internal static double Contrast(Color a,Color b){double x=Luminance(a),y=Luminance(b);return (Math.Max(x,y)+.05)/(Math.Min(x,y)+.05);}
        internal Color Readable(Color ink,Color background,double minimum){
            if(HighContrast||Contrast(ink,background)>=minimum)return ink;
            for(int alpha=1;alpha<=255;alpha++){var next=ThemePalette.Blend(ink,Foreground,alpha);if(Contrast(next,background)>=minimum)return next;}
            return Foreground;
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
            if(windows11){set(35,ColorTranslator.ToWin32(theme.Card));set(36,ColorTranslator.ToWin32(theme.Foreground));}
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
        readonly Font bodyFont,titleFont,descriptionFont;
        internal ThemedDialog():this(Style.Dark){}
        internal ThemedDialog(bool dark){
            Theme=new DialogTheme(dark);
            bodyFont=new Font("Microsoft YaHei UI",14,FontStyle.Regular,GraphicsUnit.Pixel);
            titleFont=new Font("Microsoft YaHei UI",18,FontStyle.Bold,GraphicsUnit.Pixel);
            descriptionFont=new Font("Microsoft YaHei UI",12,FontStyle.Regular,GraphicsUnit.Pixel);
            AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;Font=bodyFont;
            BackColor=Theme.Card;ForeColor=Theme.Foreground;Padding=new Padding(24);
            FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
            StartPosition=FormStartPosition.CenterParent;ShowInTaskbar=false;
        }
        internal Label Caption(string text,bool title=false,bool description=false){
            return new Label {Text=text,AutoSize=true,UseMnemonic=false,BackColor=Color.Transparent,ForeColor=description?Theme.Muted:Theme.Foreground,Font=title?titleFont:description?descriptionFont:bodyFont,Margin=new Padding(0,0,0,4)};
        }
        protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);ThemedTitleBar.Apply(Handle,Theme);}
        protected override void Dispose(bool disposing){base.Dispose(disposing);if(disposing){bodyFont.Dispose();titleFont.Dispose();descriptionFont.Dispose();}}
    }

    internal enum ThemedButtonKind {Primary,Secondary,Text}
    internal sealed class ThemedButton:Button {
        readonly DialogTheme theme;readonly ThemedButtonKind kind;
        bool hovered,pressed;
        internal ThemedButton(DialogTheme theme,ThemedButtonKind kind=ThemedButtonKind.Secondary){
            this.theme=theme;this.kind=kind;
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.SupportsTransparentBackColor,true);
            FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;UseVisualStyleBackColor=false;
            BackColor=Color.Transparent;ForeColor=theme.Foreground;Height=32;Padding=new Padding(14,0,14,0);Margin=new Padding(8,0,0,0);
            AccessibleRole=AccessibleRole.PushButton;
        }
        float ScaleFactor {get{return Font.SizeInPoints*96f/72f/14f;}}
        public override Size GetPreferredSize(Size proposedSize){
            var text=TextRenderer.MeasureText(Text,Font,Size.Empty,TextFormatFlags.SingleLine);
            int width=Math.Max(MinimumSize.Width,text.Width+Padding.Horizontal),height=Math.Max(MinimumSize.Height,Math.Max((int)Math.Round(32*ScaleFactor),text.Height+4));
            return new Size(MaximumSize.Width>0?Math.Min(width,MaximumSize.Width):width,MaximumSize.Height>0?Math.Min(height,MaximumSize.Height):height);
        }
        internal Color FillFor(bool hover,bool down){
            if(!Enabled)return theme.Card;
            Color fill=kind==ThemedButtonKind.Primary?theme.Selected:theme.Card;
            if(!hover&&!down)return fill;
            Color next=theme.Interaction(fill,down);
            if(kind!=ThemedButtonKind.Primary||theme.HighContrast)return next;
            // Keep the selected text color legible even on a hovered primary.
            for(int alpha=255;alpha>=0;alpha--){var candidate=ThemePalette.Blend(fill,next,alpha);if(DialogTheme.Contrast(theme.SelectedForeground,candidate)>=4.5)return candidate;}
            return fill;
        }
        internal Color TextFor(bool hover,bool down){
            if(!Enabled)return theme.Muted;
            if(theme.HighContrast&&(hover||down))return theme.SelectedForeground;
            return kind==ThemedButtonKind.Primary?theme.SelectedForeground:theme.Readable(kind==ThemedButtonKind.Text?theme.Accent:theme.Foreground,FillFor(hover,down),4.5);
        }
        protected override void OnPaint(PaintEventArgs e){
            float scale=ScaleFactor;var rect=new RectangleF(2*scale,2*scale,Width-4*scale,Height-4*scale);if(rect.Width<=0||rect.Height<=0)return;
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using(var path=VisualDesign.RoundRect(rect.X,rect.Y,rect.Width,rect.Height,6*scale)){
                using(var brush=new SolidBrush(FillFor(hovered,pressed)))e.Graphics.FillPath(brush,path);
                if(kind==ThemedButtonKind.Secondary||!Enabled&&kind==ThemedButtonKind.Primary||theme.HighContrast)using(var pen=new Pen(theme.Border,scale))e.Graphics.DrawPath(pen,path);
            }
            var text=ClientRectangle;text.Inflate(-Padding.Horizontal/2,0);
            TextRenderer.DrawText(e.Graphics,Text,Font,text,TextFor(hovered,pressed),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine);
            if(Focused&&ShowFocusCues){using(var path=VisualDesign.RoundRect(.5f*scale,.5f*scale,Width-scale,Height-scale,7*scale))using(var pen=new Pen(theme.Accent,scale))e.Graphics.DrawPath(pen,path);}
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
        protected readonly DialogTheme theme;readonly int radius;
        internal ThemedSection(DialogTheme theme,int radius=12){
            this.theme=theme;this.radius=radius;
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.SupportsTransparentBackColor,true);
            BackColor=Color.Transparent;ForeColor=theme.Foreground;Padding=new Padding(12);
        }
        protected virtual Color Outline {get{return theme.Border;}}
        protected override void OnPaint(PaintEventArgs e){
            base.OnPaint(e);if(Width<2||Height<2)return;
            float scale=Font.SizeInPoints*96f/72f/14f;
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using(var path=VisualDesign.RoundRect(.5f,.5f,Width-1,Height-1,radius*scale)){
                using(var brush=new SolidBrush(theme.Section))e.Graphics.FillPath(brush,path);
                using(var pen=new Pen(Outline,scale))e.Graphics.DrawPath(pen,path);
            }
        }
    }
    internal sealed class ThemedTextBox:ThemedSection {
        internal readonly TextBox Input;
        internal ThemedTextBox(DialogTheme theme,TextBox input):base(theme,6){
            Input=input;Padding=new Padding(8);TabStop=false;
            input.BorderStyle=BorderStyle.None;input.BackColor=theme.Section;input.ForeColor=theme.Foreground;
            input.Dock=DockStyle.Fill;input.Margin=Padding.Empty;Controls.Add(input);
            input.GotFocus+=delegate{Invalidate();};input.LostFocus+=delegate{Invalidate();};
            if(input.Multiline)DialogScrollbars.Attach(input,theme);
            Height=input.PreferredHeight+Padding.Vertical;
        }
        protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);if(Input!=null&&!Input.Multiline)Height=Input.PreferredHeight+Padding.Vertical;}
        protected override Color Outline {get{return Input.ContainsFocus?theme.Accent:theme.Border;}}
    }

    internal sealed class ThemedMessageBody:Panel {
        readonly Label message;bool arranging;
        internal ThemedMessageBody(DialogTheme theme,string title,string text){
            message=new Label {Name="MessageText",AccessibleName=title,Text=text,UseMnemonic=false,ForeColor=theme.Foreground,BackColor=Color.Transparent};
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

    internal sealed class ThemedProgress:Control {
        readonly DialogTheme theme;readonly Timer animation=new Timer {Interval=30};
        readonly System.Diagnostics.Stopwatch clock=new System.Diagnostics.Stopwatch();
        bool indeterminate;int value;
        internal ThemedProgress(DialogTheme theme){
            this.theme=theme;Height=6;TabStop=false;AccessibleRole=AccessibleRole.ProgressBar;
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.SupportsTransparentBackColor,true);BackColor=Color.Transparent;
            animation.Tick+=delegate{UpdateAnimation();Invalidate();};
        }
        bool Reduced {get{return DeviceSettings.Current.ReducedMotion||!SystemInformation.IsMenuAnimationEnabled||theme.HighContrast;}}
        internal bool AnimationRunning {get{return animation.Enabled;}}
        internal bool Indeterminate {get{return indeterminate;}set{if(indeterminate==value)return;indeterminate=value;AccessibleDescription=value?"进度未知":this.value+"%";clock.Restart();UpdateAnimation();Invalidate();}}
        internal int Value {get{return value;}set{this.value=Math.Max(0,Math.Min(100,value));AccessibleDescription=indeterminate?"进度未知":this.value+"%";Invalidate();}}
        void UpdateAnimation(){if(indeterminate&&Visible&&IsHandleCreated&&!Reduced)animation.Start();else animation.Stop();}
        protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);UpdateAnimation();}
        protected override void OnVisibleChanged(EventArgs e){base.OnVisibleChanged(e);UpdateAnimation();}
        protected override void OnPaint(PaintEventArgs e){
            if(Width<=0||Height<=0)return;UpdateAnimation();
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using(var path=VisualDesign.RoundRect(0,0,Width,Height,Height/2f)){
                using(var brush=new SolidBrush(theme.Track))e.Graphics.FillPath(brush,path);
                float width=indeterminate?(Reduced?Width:Width*.28f):Width*value/100f;
                float x=indeterminate&&!Reduced?(Width-width)*(float)((Math.Sin(clock.Elapsed.TotalSeconds*Math.PI)+1)/2):0;
                if(width>0)using(var fill=VisualDesign.RoundRect(x,0,width,Height,Height/2f))using(var brush=new SolidBrush(indeterminate&&Reduced&&!theme.HighContrast?Color.FromArgb(128,theme.Accent):theme.Accent))e.Graphics.FillPath(brush,fill);
            }
        }
        protected override void Dispose(bool disposing){if(disposing)animation.Dispose();base.Dispose(disposing);}
    }

    internal sealed class ThemedAliasDialog:ThemedDialog {
        internal readonly TextBox Input;
        internal ThemedAliasDialog(string title,string value,int maxLength){
            Text=title;ClientSize=new Size(360,162);
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Margin=Padding.Empty};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Input=new TextBox {Name="AliasInput",AccessibleName=title,Text=value,MaxLength=maxLength};
            var input=new ThemedTextBox(Theme,Input) {Dock=DockStyle.Top,Margin=new Padding(0,0,0,16)};
            var buttons=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false,Margin=Padding.Empty};
            var save=new ThemedButton(Theme,ThemedButtonKind.Primary) {Text="保存",DialogResult=DialogResult.OK,AutoSize=true,TabIndex=2};
            var cancel=new ThemedButton(Theme) {Text="取消",DialogResult=DialogResult.Cancel,AutoSize=true,TabIndex=1};
            buttons.Controls.AddRange(new Control[]{save,cancel});layout.Controls.Add(input,0,0);layout.Controls.Add(buttons,0,2);Controls.Add(layout);AcceptButton=save;CancelButton=cancel;ActiveControl=Input;
        }
    }

    internal static class ThemedMessage {
        internal static DialogResult Show(IWin32Window owner,string title,string text,MessageBoxButtons buttons){return Show(owner,title,text,buttons,MessageBoxDefaultButton.Button1);}
        internal static DialogResult Show(IWin32Window owner,string title,string text,MessageBoxButtons buttons,MessageBoxDefaultButton defaultButton){
            using(var dialog=Create(title,text,buttons,defaultButton))return dialog.ShowDialog(owner);
        }
        internal static ThemedDialog Create(string title,string text,MessageBoxButtons buttons,MessageBoxDefaultButton defaultButton=MessageBoxDefaultButton.Button1){
            var dialog=new ThemedDialog {Text=title,ClientSize=new Size(440,210)};
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Margin=Padding.Empty};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var heading=dialog.Caption(title,true);layout.Controls.Add(heading,0,0);
            var message=new ThemedMessageBody(dialog.Theme,title,text) {Dock=DockStyle.Fill,Margin=new Padding(0,12,0,16)};
            layout.Controls.Add(message,0,1);
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
            layout.Controls.Add(bar,0,2);dialog.Controls.Add(layout);
            bool resizing=false;
            LayoutEventHandler fit=delegate{
                if(resizing)return;resizing=true;
                try{
                    int width=Math.Max(1,dialog.ClientSize.Width-dialog.Padding.Horizontal);float scale=dialog.Font.SizeInPoints*96f/72f/14f;
                    int surrounding=dialog.Padding.Vertical+heading.GetPreferredSize(new Size(width,0)).Height+heading.Margin.Vertical+message.Margin.Vertical+bar.GetPreferredSize(new Size(width,0)).Height+bar.Margin.Vertical;
                    int maximum=Math.Min((int)Math.Round(560*scale),Screen.FromControl(dialog).WorkingArea.Height-(dialog.Height-dialog.ClientSize.Height)-(int)Math.Round(48*scale));
                    int height=Math.Min(surrounding+message.GetPreferredSize(new Size(width,0)).Height,Math.Max(surrounding,maximum));
                    if(dialog.ClientSize.Height!=height)dialog.ClientSize=new Size(dialog.ClientSize.Width,height);
                }finally{resizing=false;}
            };
            dialog.Layout+=fit;fit(dialog,new LayoutEventArgs(dialog,null));
            dialog.AcceptButton=defaultControl;dialog.CancelButton=cancelControl;dialog.ActiveControl=defaultControl;return dialog;
        }
    }
}
