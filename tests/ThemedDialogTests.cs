using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

// Offline fixtures only. Compile/run with an output directory under TEMP.
static class ThemedDialogTests {
    const BindingFlags Members=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks,failures;
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window,int command);
    [DllImport("user32.dll")] static extern IntPtr GetActiveWindow();
    delegate bool EnumWindow(IntPtr window,IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint thread,EnumWindow callback,IntPtr parameter);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct StartupInfo {
        internal uint Size;internal IntPtr Reserved,Desktop,Title;
        internal uint X,Y,Width,Height,XChars,YChars,Fill,Flags;
        internal ushort Show,ReservedSize;internal IntPtr ReservedBytes,Input,Output,Error;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern void GetStartupInfo(out StartupInfo info);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct PrintRect {internal int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] struct PrintRange {internal IntPtr Device,Target;internal PrintRect Area,Page;internal int First,Last;}
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,ref PrintRange range);
    static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
    static void Run(string name,Action test){try{test();Console.WriteLine("PASS "+name);}catch(Exception e){failures++;Console.Error.WriteLine("FAIL "+name+": "+(e.InnerException??e));}}
    static Type Type(string name){var type=typeof(UpdateDialog).Assembly.GetType("AtkBatteryTray."+name);Check(type!=null,name+" is available");return type;}
    static object Field(object instance,string name){return instance.GetType().GetField(name,Members).GetValue(instance);}
    static object Call(object instance,string method,params object[] args){return instance.GetType().GetMethod(method,Members).Invoke(instance,args);}
    static T Find<T>(Control control,string name) where T:Control {var matches=control.Controls.Find(name,true);Check(matches.Length==1,"one "+name);return (T)matches[0];}
    static UpdateRelease Release(){return new UpdateRelease {Tag="v9.9.9",Body="更新说明\n- 第一项\n- 第二项\n<script>plain text</script>\u0001"};}
    static FeedbackReport Report(){return new FeedbackReport("fixture","fixture-windows",new WirelessDevice[0],new SuppressedDevice[0],new UnadaptedDevice[0],new ReaderHealth[0],new string[0]);}
    static double Luminance(Color color){double[] v={color.R/255.0,color.G/255.0,color.B/255.0};for(int i=0;i<3;i++)v[i]=v[i]<=.04045?v[i]/12.92:Math.Pow((v[i]+.055)/1.055,2.4);return .2126*v[0]+.7152*v[1]+.0722*v[2];}
    static double Contrast(Color a,Color b){double x=Luminance(a),y=Luminance(b);return (Math.Max(x,y)+.05)/(Math.Min(x,y)+.05);}
    static Color ColorField(object instance,string name){return (Color)Field(instance,name);}
    static void SelectTheme(string style,bool dark){DeviceSettings.UseTransientConfiguration(new AudioBindings {ThemeStyle=style,Theme=dark?"dark":"light",ReducedMotion=true,RememberReadings=false,VolumeCollapsed=false});}
    static void Show(Form form){form.Opacity=0;form.ShowInTaskbar=false;form.Show();Application.DoEvents();}
    static void WithWorkArea(Rectangle area,Action test){
        var previous=ThemedDialog.WorkingAreaProvider;
        try{ThemedDialog.WorkingAreaProvider=delegate{return area;};test();}
        finally{ThemedDialog.WorkingAreaProvider=previous;}
    }
    static void Fits(ThemedDialog dialog,Rectangle area,string context){
        Check(area.Contains(dialog.Bounds),context+": entire window fits injected work area ("+dialog.Bounds+")");
        Check(Math.Abs(dialog.Left-area.Left-(area.Width-dialog.Width)/2)<=1&&Math.Abs(dialog.Top-area.Top-(area.Height-dialog.Height)/2)<=1,context+": centered in work area");
        var button=(Control)dialog.AcceptButton;var client=dialog.RectangleToScreen(dialog.ClientRectangle);
        Check(button.Visible&&client.Contains(button.RectangleToScreen(button.ClientRectangle)),context+": primary button is fully visible");
        for(Control parent=button.Parent;parent!=dialog;parent=parent.Parent)
            Check(parent.RectangleToScreen(parent.ClientRectangle).Contains(button.RectangleToScreen(button.ClientRectangle)),context+": primary button is not clipped by ancestors");
    }
    static void InjectedWorkAreas(){
        foreach(var size in new[]{new Size(1920,1080),new Size(1280,720),new Size(1024,768)})foreach(float scale in new[]{1.5f,2f})WithDpi((int)Math.Round(96*scale),delegate{
            var area=new Rectangle(new Point(-1400,40),size);string context=size+" at "+scale;
            WithWorkArea(area,delegate{
                using(var feedback=new FeedbackForm(Report(),false,false,delegate{},delegate{},FeedbackRelay.Url)){
                    feedback.Scale(new SizeF(scale,scale));feedback.Font=new Font("Microsoft YaHei UI",VisualDesign.BodyFontSize*scale,FontStyle.Regular,GraphicsUnit.Pixel);Show(feedback);Fits(feedback,area,"feedback "+context);
                    var input=Find<TextBox>(feedback,"Description");Check(input.Height>=3*input.Font.Height,"description retains three lines: "+context);
                    var viewport=Find<Panel>(feedback,"DialogContentViewport");var notice=Find<Control>(feedback,"FeedbackNoticeCard");
                    if(size.Height<=768&&scale==2f){
                        Check(viewport.AutoScroll&&viewport.VerticalScroll.Visible,"small work area scrolls feedback content: "+context);
                        var button=(Control)feedback.AcceptButton;var before=button.RectangleToScreen(button.ClientRectangle);
                        viewport.AutoScrollPosition=new Point(0,Int32.MaxValue);Application.DoEvents();
                        Check(viewport.AutoScrollPosition.Y<0&&notice.RectangleToScreen(notice.ClientRectangle).Bottom<=viewport.RectangleToScreen(viewport.ClientRectangle).Bottom,"can scroll to final card: "+context);
                        Check(button.RectangleToScreen(button.ClientRectangle)==before,"scrolling does not move buttons: "+context);
                    }
                    using(var diagnostic=feedback.CreateDiagnosticsDialog()){
                        diagnostic.Scale(new SizeF(scale,scale));diagnostic.Font=feedback.Font;Show(diagnostic);Fits(diagnostic,area,"diagnostic "+context);
                        var text=Find<TextBox>(diagnostic,"DiagnosticText");Check(text.Height>=5*text.Font.Height,"diagnostics retain five lines: "+context);
                    }
                }
                var release=Release();release.Body=Lines(80);using(var update=new UpdateDialog(release,true)){
                    update.Scale(new SizeF(scale,scale));update.Font=new Font("Microsoft YaHei UI",VisualDesign.BodyFontSize*scale,FontStyle.Regular,GraphicsUnit.Pixel);Show(update);Fits(update,area,"update "+context);
                    var notes=Find<ThemedReleaseNotes>(update,"ReleaseNotes");Check(notes.Height>=2*notes.LineHeight,"notes retain two lines: "+context);
                }
                using(var message=ThemedMessage.Create("确认",Lines(100),MessageBoxButtons.YesNo)){
                    message.Scale(new SizeF(scale,scale));message.Font=new Font("Microsoft YaHei UI",VisualDesign.BodyFontSize*scale,FontStyle.Regular,GraphicsUnit.Pixel);Show(message);Fits(message,area,"message "+context);
                    var text=Find<Label>(message,"MessageText");var scroll=(ScrollableControl)text.Parent;scroll.AutoScrollPosition=new Point(0,Int32.MaxValue);Application.DoEvents();
                    Check(scroll.AutoScrollPosition.Y<0&&text.RectangleToScreen(text.ClientRectangle).Bottom<=scroll.RectangleToScreen(scroll.ClientRectangle).Bottom,"message scroll reaches last line: "+context);
                }
                using(var alias=new ThemedAliasDialog("名称","设备",12)){
                    alias.Scale(new SizeF(scale,scale));alias.Font=new Font("Microsoft YaHei UI",VisualDesign.BodyFontSize*scale,FontStyle.Regular,GraphicsUnit.Pixel);Show(alias);Fits(alias,area,"alias "+context);
                }
            });
        });
    }
    static void WorkAreaAndDpiChanges(){
        WithDpi(192,delegate{
        var previous=ThemedDialog.WorkingAreaProvider;var area=new Rectangle(0,0,3200,2400);int reads=0;
        ThemedDialog.WorkingAreaProvider=delegate{reads++;return area;};
        try{using(var dialog=new FeedbackForm(Report(),false,false,delegate{},delegate{},FeedbackRelay.Url)){
            dialog.Scale(new SizeF(2,2));dialog.Font=new Font("Microsoft YaHei UI",24,FontStyle.Regular,GraphicsUnit.Pixel);Show(dialog);
            var original=dialog.Size;var input=Find<TextBox>(dialog,"Description");int originalInput=input.Height;
            var viewport=Find<Panel>(dialog,"DialogContentViewport");bool originalScroll=viewport.VerticalScroll.Visible;
            Check(input.Height>=3*input.Font.Height,"initial client keeps the readable minimum even if Windows clamps it");
            area=new Rectangle(-1280,40,1280,720);dialog.Location=new Point(-1200,80);Application.DoEvents();Fits(dialog,area,"monitor move");
            Check(input.Height<=originalInput&&input.Height>=3*input.Font.Height&&viewport.VerticalScroll.Visible,"monitor move shrinks to readable minimum, then scrolls");
            area=new Rectangle(0,0,3200,2400);dialog.Location=new Point(10,10);Application.DoEvents();Fits(dialog,area,"return to large monitor");
            Check(dialog.Size==original&&input.Height==originalInput&&viewport.VerticalScroll.Visible==originalScroll,"returning restores the original actual size and scrolling state");
            // Send the native DPI notification using a valid suggested RECT. The work
            // area changes without a preceding move, so this must trigger a new fit.
            area=new Rectangle(20,30,1024,768);int before=reads;IntPtr rect=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(PrintRect)));
            try{
                Marshal.StructureToPtr(new PrintRect {Left=20,Top=30,Right=1044,Bottom=798},rect,false);
                SendMessage(dialog.Handle,0x02E0,(IntPtr)(144|(144<<16)),rect);Application.DoEvents();
            }finally{Marshal.FreeHGlobal(rect);}
            Fits(dialog,area,"DPI notification");Check(reads>before&&input.Height>=3*input.Font.Height,"DPI notification remeasures the work area and minimum");
        }}finally{ThemedDialog.WorkingAreaProvider=previous;}
        });
    }
    static void ActualClientSizeLayout(string kind){
        // Sizes are logical pixels; scale them along with the controls. The native
        // maximum simulates a screen clamp even when the injected work area is large.
        foreach(float scale in new[]{1f,2f})WithDpi((int)Math.Round(96*scale),delegate{using(var feedback=new FeedbackForm(Report(),false,false,delegate{},delegate{},FeedbackRelay.Url)){
            var release=Release();release.Body=Lines(80);
            using(var dialog=kind=="feedback"?(ThemedDialog)feedback:kind=="diagnostic"?feedback.CreateDiagnosticsDialog():kind=="update"?new UpdateDialog(release,true):kind=="message"?ThemedMessage.Create("确认",Lines(100),MessageBoxButtons.YesNo):new ThemedAliasDialog("名称","设备",12)){
                dialog.Scale(new SizeF(scale,scale));dialog.Font=new Font("Microsoft YaHei UI",VisualDesign.BodyFontSize*scale,FontStyle.Regular,GraphicsUnit.Pixel);Show(dialog);
                foreach(var logical in new[]{new Size(512,384),new Size(400,300),new Size(512,384)}){
                    var limit=new Size((int)(logical.Width*scale),(int)(logical.Height*scale));
                    var maximum=new Size(limit.Width+dialog.Width-dialog.ClientSize.Width,limit.Height+dialog.Height-dialog.ClientSize.Height);
                    dialog.MaximumSize=maximum;dialog.ClientSize=limit;Application.DoEvents();
                    string context=kind+" client "+logical+" at "+scale;
                    Check(dialog.MaximumSize==maximum&&dialog.ClientSize.Width<=limit.Width&&dialog.ClientSize.Height<=limit.Height,context+": system size limit is respected");
                    var button=(Control)dialog.AcceptButton;var bounds=button.RectangleToScreen(button.ClientRectangle);
                    Check(button.Visible,context+": primary button is visible");
                    for(Control parent=button.Parent;parent!=null;parent=parent.Parent)
                        Check(parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds),context+": primary button is fully inside client and ancestors");
                    Check(Math.Abs(dialog.RectangleToScreen(dialog.ClientRectangle).Bottom-dialog.Padding.Bottom-button.Parent.RectangleToScreen(button.Parent.ClientRectangle).Bottom)<=1,context+": button bar stays at the client bottom");
                    if(kind=="feedback"){
                        var input=Find<TextBox>(dialog,"Description");Check(input.Height>=3*input.Font.Height,context+": description keeps its three-line minimum");
                    }else if(kind=="diagnostic"){
                        var text=Find<TextBox>(dialog,"DiagnosticText");Check(text.Height>=5*text.Font.Height,context+": diagnostics keep their five-line minimum");
                    }else if(kind=="update"){
                        var notes=Find<ThemedReleaseNotes>(dialog,"ReleaseNotes");Check(notes.Height>=2*notes.LineHeight,context+": notes keep their two-line minimum");
                    }else if(kind=="alias"){
                        var input=Find<TextBox>(dialog,"AliasInput");Check(input.Parent.Height>=input.PreferredHeight+input.Parent.Padding.Vertical,context+": alias input is not compressed");
                    }
                    var viewport=Find<Panel>(dialog,"DialogContentViewport");var content=(TableLayoutPanel)viewport.Controls[0];
                    if(kind=="feedback")Check(viewport.AutoScroll&&viewport.VerticalScroll.Visible,context+": minimum content enables scrolling");
                    viewport.AutoScrollPosition=new Point(0,Int32.MaxValue);Application.DoEvents();
                    var last=content.GetControlFromPosition(0,content.RowCount-1);
                    Check(last.RectangleToScreen(last.ClientRectangle).Bottom<=viewport.RectangleToScreen(viewport.ClientRectangle).Bottom,context+": last content control is reachable");
                    Check(button.RectangleToScreen(button.ClientRectangle)==bounds,context+": scrolling leaves the button bar fixed");
                    if(kind=="message"){
                        var text=Find<Label>(dialog,"MessageText");var scroll=(ScrollableControl)text.Parent;scroll.AutoScrollPosition=new Point(0,Int32.MaxValue);Application.DoEvents();
                        Check(text.RectangleToScreen(text.ClientRectangle).Bottom<=scroll.RectangleToScreen(scroll.ClientRectangle).Bottom,context+": last message line is reachable");
                    }
                }
            }
        }});
    }
    static void Key(Form form,Keys key){typeof(Form).GetMethod("ProcessDialogKey",Members).Invoke(form,new object[]{key});Application.DoEvents();}
    static void WithDpi(int dpi,Action test){
        var previous=DialogDpi.DeviceDpiReader;
        try{DialogDpi.DeviceDpiReader=delegate{return dpi;};test();}
        finally{DialogDpi.DeviceDpiReader=previous;}
    }
    static void ButtonFontScaling(){
        SelectTheme("ocean",true);var theme=new DialogTheme(true);
        foreach(var sample in new[]{new {Name="Microsoft Sans Serif",Points=8.25f},new {Name="Segoe UI",Points=9f},new {Name="Microsoft YaHei UI",Points=9f}})
        using(var font=new Font(sample.Name,sample.Points,FontStyle.Regular,GraphicsUnit.Point))
        foreach(var kind in new[]{ThemedButtonKind.Primary,ThemedButtonKind.Secondary,ThemedButtonKind.Text})
        using(var button=new ThemedButton(theme,kind) {Font=font,Text="Button"}){
            int dpi=button.DeviceDpi,expected=(int)Math.Round(30*dpi/96f),actual=button.GetPreferredSize(Size.Empty).Height;
            Check(actual==expected,sample.Name+" "+sample.Points+"pt "+kind+" at "+dpi+" DPI: expected height "+expected+", actual "+actual);
        }
    }
    static void ButtonDpiMatrix(){
        SelectTheme("ocean",true);var theme=new DialogTheme(true);
        foreach(var sample in new[]{new {Name="Microsoft Sans Serif",Points=8.25f},new {Name="Segoe UI",Points=9f},new {Name="Microsoft YaHei UI",Points=9f}})
        foreach(var display in new[]{new {Dpi=96,Height=30},new {Dpi=120,Height=38},new {Dpi=144,Height=45},new {Dpi=192,Height=60}})
        WithDpi(display.Dpi,delegate{
            using(var font=new Font(sample.Name,sample.Points,FontStyle.Regular,GraphicsUnit.Point))
            using(var larger=new Font(sample.Name,sample.Points*2,FontStyle.Regular,GraphicsUnit.Point))
            using(var form=new Form {AutoScaleMode=AutoScaleMode.None,Font=font})
            using(var bar=new FlowLayoutPanel {AutoSize=true,WrapContents=false}){
                form.Controls.Add(bar);
                foreach(var kind in new[]{ThemedButtonKind.Primary,ThemedButtonKind.Secondary,ThemedButtonKind.Text})
                using(var button=new ThemedButton(theme,kind) {Font=font,Text="Button",AutoSize=true}){
                    bar.Controls.Add(button);bar.PerformLayout();
                    string context=sample.Name+" "+sample.Points+"pt "+kind+" at "+display.Dpi+" DPI";
                    var preferred=button.GetPreferredSize(Size.Empty);
                    Check(preferred.Height==display.Height&&button.Height==display.Height,context+": preferred and laid-out heights are "+display.Height);
                    button.Font=larger;
                    Check(button.GetPreferredSize(Size.Empty)==preferred,context+": text measurement also uses DPI rather than incoming font size");
                }
                using(var segment=new ThemedSegment(theme) {Font=font,Text="Type"})
                using(var toggle=new ThemedSwitch(theme) {Font=font,Text="Diagnostics"}){
                    foreach(Control control in new Control[]{segment,toggle}){
                        var preferred=control.GetPreferredSize(Size.Empty);control.Font=larger;
                        Check(preferred.Height==display.Height&&control.GetPreferredSize(Size.Empty)==preferred,control.GetType().Name+": DPI determines height and label size at "+display.Dpi);
                    }
                }
                using(var segments=new ThemedSegments(theme) {Font=font,Width=1200}){
                    for(int i=0;i<4;i++)segments.Controls.Add(new ThemedSegment(theme) {Text="Type"});segments.PerformLayout();
                    Check(segments.Height==display.Height&&segments.GetPreferredSize(new Size(1200,0)).Height==display.Height,"segment row uses DPI at "+display.Dpi);
                    foreach(Control child in segments.Controls)Check(child.Height==display.Height,"segment child shares the DPI-scaled row height");
                }
            }
        });
    }
    static void DpiSource(){
        var previous=DialogDpi.DeviceDpiReader;
        try{using(var form=new Form {AutoScaleMode=AutoScaleMode.None})using(var panel=new Panel())using(var button=new ThemedButton(new DialogTheme(false))){
            form.Controls.Add(panel);panel.Controls.Add(button);
            Check(DialogDpi.Scale(button)==form.DeviceDpi/96f,"native lookup uses the window DeviceDpi");
            DialogDpi.DeviceDpiReader=delegate(Control control){return control==form?144:96;};
            Check(button.GetPreferredSize(Size.Empty).Height==45,"window DPI wins over a child's stale DPI");
            panel.Controls.Remove(button);
            Check(button.GetPreferredSize(Size.Empty).Height==30,"standalone button uses its own DPI after reparenting");
            panel.Controls.Add(button);DialogDpi.DeviceDpiReader=delegate{return 0;};
            float expected;using(var graphics=form.CreateGraphics())expected=graphics.DpiX/96f;
            Check(DialogDpi.Scale(button)==expected,"unavailable DeviceDpi falls back to window Graphics.DpiX");
        }}finally{DialogDpi.DeviceDpiReader=previous;}
    }
    sealed class PaintSpyPanel:Panel {
        internal int Paints;
        protected override void OnPaint(PaintEventArgs e){Paints++;e.Graphics.Clear(Color.Magenta);base.OnPaint(e);}
    }
    static void OpaqueDialogPainting(){
        foreach(string style in ThemePalette.Keys)foreach(bool dark in new[]{false,true}){
            SelectTheme(style,dark);var theme=new DialogTheme(dark);
            using(var parent=new PaintSpyPanel {BackColor=theme.Card,Size=new Size(400,100)}){
                Control[] controls={new ThemedButton(theme),new ThemedSection(theme),new ThemedTextBox(theme,new TextBox()),new ThemedSegments(theme),new ThemedSegment(theme),new ThemedSwitch(theme),new ThemedProgress(theme),new ThemedMessageBody(theme,"确认","文字")};
                foreach(var control in controls)using(control){
                    parent.Controls.Add(control);control.AutoSize=false;control.Size=new Size(240,40);control.CreateControl();
                    Check(control.BackColor.A==255,control.GetType().Name+" has an opaque background");
                    parent.Paints=0;
                    using(var dirty=new Bitmap(control.Width,control.Height))using(var clean=new Bitmap(control.Width,control.Height)){
                        using(var g=Graphics.FromImage(dirty)){g.Clear(Color.Magenta);Call(control,"OnPaintBackground",new PaintEventArgs(g,control.ClientRectangle));Call(control,"OnPaint",new PaintEventArgs(g,control.ClientRectangle));}
                        using(var g=Graphics.FromImage(clean)){g.Clear(theme.Card);Call(control,"OnPaintBackground",new PaintEventArgs(g,control.ClientRectangle));Call(control,"OnPaint",new PaintEventArgs(g,control.ClientRectangle));}
                        Check(SamePixels(dirty,clean),control.GetType().Name+" erases poisoned pixels across the whole client area");
                    }
                    Check(parent.Paints==0,control.GetType().Name+" does not replay parent paint into its background");
                }
            }
            using(var feedback=new FeedbackForm(Report(),true,dark,delegate{},delegate{},"")){
                Show(feedback);
                foreach(string name in new[]{"FeedbackRemaining","FeedbackStatus","ViewDiagnostics"})Check(Find<Control>(feedback,name).BackColor==theme.Card,name+" uses the card background");
                var segment=Find<ThemedSegment>(feedback,"Category-battery");
                using(var initial=new Bitmap(segment.Width,segment.Height))using(var after=new Bitmap(segment.Width,segment.Height)){
                    segment.DrawToBitmap(initial,segment.ClientRectangle);Call(segment,"OnMouseEnter",EventArgs.Empty);Call(segment,"OnMouseLeave",EventArgs.Empty);segment.DrawToBitmap(after,segment.ClientRectangle);
                    Check(SamePixels(initial,after),"leaving a segment clears its hover without changing selection");
                }
                using(var image=new Bitmap(segment.Width+20,segment.Height+20))using(var g=Graphics.FromImage(image)){
                    g.Clear(Color.Magenta);g.TranslateTransform(10,10);Call(segment,"OnPaint",new PaintEventArgs(g,segment.ClientRectangle));
                    bool outsideClean=true;
                    for(int y=0;y<image.Height;y++)for(int x=0;x<image.Width;x++)if((x<10||y<10||x>=10+segment.Width||y>=10+segment.Height)&&image.GetPixel(x,y).ToArgb()!=Color.Magenta.ToArgb())outsideClean=false;
                    Check(outsideClean,"segment drawing stays inside its own rectangle");
                }
            }
        }
    }
    static void InitialReadOnlyFocus(){
        using(var update=new UpdateDialog(Release(),true)){
            Show(update);var notes=Find<ThemedReleaseNotes>(update,"ReleaseNotes");
            Check(update.ActiveControl==update.AcceptButton&&!notes.Focused,"update opens on its primary action");
            Check(notes.SelectionLength==0&&notes.HideSelection&&notes.TabStop,"notes start unselected and remain keyboard accessible");
            for(int i=0;i<8&&!notes.Focused;i++)Key(update,Keys.Tab);
            Check(notes.Focused,"Tab reaches release notes from the primary action");
            notes.Select(0,3);Check(notes.SelectionLength==3,"read-only notes still allow selection for copying");
        }
        using(var feedback=new FeedbackForm(Report(),false,false,delegate{},delegate{},""))using(var diagnostic=feedback.CreateDiagnosticsDialog()){
            Show(diagnostic);var text=Find<TextBox>(diagnostic,"DiagnosticText");
            Check(diagnostic.ActiveControl==diagnostic.AcceptButton&&text.SelectionLength==0&&text.HideSelection&&text.TabStop,"diagnostics open unselected on Close and retain Tab access");
            Key(diagnostic,Keys.Tab);Check(text.Focused,"Tab reaches the diagnostic text from Close");
        }
    }
    static void CompactUpdateSize(){
        foreach(float dpiScale in new[]{1f,1.25f,1.5f,2f})WithDpi((int)Math.Round(dpiScale*96),delegate{
        foreach(int lines in new[]{1,3,6,8,20}){
            var release=Release();release.Body=Lines(lines);using(var update=new UpdateDialog(release,true)){
                update.Scale(new SizeF(dpiScale,dpiScale));update.Font=new Font("Microsoft YaHei UI",VisualDesign.BodyFontSize*dpiScale,FontStyle.Regular,GraphicsUnit.Pixel);
                Show(update);float scale=DialogDpi.Scale(update);var notes=Find<ThemedReleaseNotes>(update,"ReleaseNotes");
                Check(update.ClientSize.Width/scale>=370&&update.ClientSize.Width/scale<=390,"update width is about 380 logical pixels");
                Check(notes.Height>=2*notes.LineHeight&&notes.Height<=8*notes.LineHeight+5,"notes viewport contains two to eight lines");
                if(lines>=3&&lines<=6)Check(update.Height/scale<=420,"common update fits within 420 logical pixels");
                var card=Find<ThemedSection>(update,"UpdateStatusCard");var status=Find<Label>(update,"UpdateStatus");
                Check(card.Height==card.Padding.Vertical+status.Font.Height,"ready status card reserves one text row");
            }
        }
        });
    }
    static void SharedPanelGeometry(){
        SelectTheme("ocean",true);var theme=new DialogTheme(true);
        using(var primary=new ThemedButton(theme,ThemedButtonKind.Primary))using(var secondary=new ThemedButton(theme)){
            Check(primary.Height==30&&primary.GetPreferredSize(Size.Empty).Height==30,"dialog buttons use the panel's 30px selection height");
            Check(primary.FillFor(false,false)==theme.Selected&&secondary.FillFor(false,false)==theme.Track,"buttons use selected and unselected panel fills");
        }
        foreach(string method in new[]{"Selection","Highlight","Outline","Fill","DrawVolumeTrack"})Check(typeof(CompactRenderer).GetMethod(method,BindingFlags.Static|BindingFlags.NonPublic)!=null,"shared panel primitive: "+method);
        Check(typeof(PopupPages).GetMethod("DrawToggle",BindingFlags.Static|BindingFlags.NonPublic)!=null,"secondary-page switch drawing is reusable");
        using(var section=new ThemedSection(theme) {Size=new Size(200,80)})using(var actual=new Bitmap(200,80))using(var expected=new Bitmap(200,80)){
            section.BackColor=theme.Card;section.DrawToBitmap(actual,new Rectangle(Point.Empty,actual.Size));
            using(var g=Graphics.FromImage(expected)){g.Clear(theme.Card);g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;CompactRenderer.DrawSurface(g,new RectangleF(.5f,.5f,199,79),true);}
            for(int y=0;y<80;y++)for(int x=0;x<200;x++)Check(actual.GetPixel(x,y)==expected.GetPixel(x,y),"card pixels use DrawSurface including its 1px outline");
        }
    }
    static void DialogBackground(){
        foreach(string style in ThemePalette.Keys)foreach(bool dark in new[]{true,false}){
            SelectTheme(style,dark);var palette=ThemePalette.Current(dark);var field=typeof(ThemePalette).GetField("Background",Members);Check(field!=null,"palette defines Background");
            var background=(Color)field.GetValue(palette);Check(background==ThemePalette.Blend(palette.Card,dark?Color.Black:palette.Foreground,dark?64:13),"only the new background uses 25% / 5% mixing");
            Check(Luminance(palette.Card)-Luminance(background)>=.004,"card/background luminance delta >= .004 for "+style+dark);
            Check(Contrast(palette.Foreground,background)>=4.5&&Contrast(palette.Muted,background)>=4.5,"text on new background remains readable");
            using(var dialog=new UpdateDialog(Release(),true))Check(dialog.BackColor==background&&dialog.Padding==new Padding(16),"window uses background and 16px margins");
            var attributes=new Dictionary<int,int>();ThemedTitleBar.Apply(new DialogTheme(dark),true,delegate(int a,int v){attributes[a]=v;return 0;});
            Check(attributes.ContainsKey(33)&&attributes[33]==2&&attributes[35]==ColorTranslator.ToWin32(background),"Windows 11 requests round corners and a matching title bar");
        }
    }
    static void SegmentsAndSwitch(){
        using(var form=new FeedbackForm(Report(),true,false,delegate{},delegate{},"")){
            Show(form);var first=Find<RadioButton>(form,"Category-device");var second=Find<RadioButton>(form,"Category-battery");
            Check(first.GetType()!=typeof(RadioButton)&&second.GetType()==first.GetType(),"feedback types use panel segments");
            Check(first.Height==VisualDesign.StageRect(0,4).Height&&first.Top==Find<RadioButton>(form,"Category-ui").Top,"wide feedback uses one row of panel-height segments");
            foreach(string category in new[]{"device","battery","ui","other"}){
                var option=Find<RadioButton>(form,"Category-"+category);Check(option.AccessibilityObject.Name==option.Text&&option.AccessibilityObject.Role==AccessibleRole.RadioButton,"segment accessible name and radio role");
            }
            first.Focus();first.GetType().GetMethod("ProcessDialogKey",Members).Invoke(first,new object[]{Keys.Right});Application.DoEvents();
            Check(second.Focused&&second.Checked&&form.SelectedCategory=="battery"&&!first.Checked,"Right changes focus and the original selected value");
            second.GetType().GetMethod("ProcessDialogKey",Members).Invoke(second,new object[]{Keys.Left});Check(first.Checked&&form.SelectedCategory=="device","Left selects the preceding segment");
            Key(form,Keys.Tab);Check(!first.ContainsFocus&&!second.ContainsFocus,"Tab leaves the radio group");
            var toggle=Find<CheckBox>(form,"IncludeDiagnostics");Check(toggle.GetType()!=typeof(CheckBox)&&toggle.Checked,"diagnostics uses a checked switch");
            Check(toggle.AccessibilityObject.Name==toggle.Text&&toggle.AccessibilityObject.Role==AccessibleRole.CheckButton&&(toggle.AccessibilityObject.State&AccessibleStates.Checked)!=0,"switch retains accessible checked state");
            toggle.AccessibilityObject.DoDefaultAction();Check(!toggle.Checked,"accessible switch action updates the original checkbox value");
            toggle.GetType().GetMethod("OnClick",Members).Invoke(toggle,new object[]{EventArgs.Empty});Check(toggle.Checked,"whole switch row toggles");
            toggle.Focus();SendMessage(toggle.Handle,0x0100,(IntPtr)Keys.Space,IntPtr.Zero);SendMessage(toggle.Handle,0x0101,(IntPtr)Keys.Space,IntPtr.Zero);Application.DoEvents();
            Check(!toggle.Checked&&(toggle.AccessibilityObject.State&AccessibleStates.Checked)==0,"native Space key toggles checked and accessible state");
            form.ClientSize=new Size(360,form.ClientSize.Height);Application.DoEvents();var third=Find<RadioButton>(form,"Category-ui");
            Check(first.Top==second.Top&&third.Top>first.Top,"four feedback segments wrap into two rows");
        }
    }
    static void AdaptiveReleaseNotes(){
        int previous=0;
        foreach(int lines in new[]{1,7,80}){
            var release=Release();release.Body=Lines(lines);using(var dialog=new UpdateDialog(release,true)){
                Show(dialog);var notes=Find<Control>(dialog,"ReleaseNotes");
                Check(notes.Font.SizeInPoints*96/72==VisualDesign.BodyFontSize,"release notes use 12px body text");
                Check(dialog.ClientSize.Height>previous,"short, medium and long notes grow the window");previous=dialog.ClientSize.Height;
                Check(dialog.Height<=ThemedDialog.WorkingAreaProvider(dialog).Height,"update window stays in the injected work area");
                var scroll=notes.GetType().GetProperty("ScrollBars").GetValue(notes,null);Check(scroll.ToString()==(lines==80?"Vertical":"None"),"only notes exceeding eight lines scroll");
                var rich=(ThemedReleaseNotes)notes;Check(rich.Height>=2*rich.LineHeight&&rich.Height<=8*rich.LineHeight+5,"release viewport stays between two and eight lines");
                if(lines==7){int next=rich.Text.IndexOf('\n')+1;Check(Math.Abs(rich.GetPositionFromCharIndex(next).Y-rich.GetPositionFromCharIndex(0).Y-rich.LineHeight)<=1,"native rich edit applies 1.4x line spacing");}
            }
        }
        foreach(int lines in new[]{2,8,9}){
            var release=Release();release.Body=Lines(lines);using(var dialog=new UpdateDialog(release,true)){
                Show(dialog);var notes=Find<ThemedReleaseNotes>(dialog,"ReleaseNotes");
                Check(notes.ScrollBars==(lines>8?RichTextBoxScrollBars.Vertical:RichTextBoxScrollBars.None),"exact eight-line scrollbar boundary: "+lines+" (measured "+notes.LineCountFor(notes.Width)+", height "+notes.Height+", line "+notes.LineHeight+", font "+notes.Font.Height+")");
                if(lines<=8)Check(notes.GetPositionFromCharIndex(notes.TextLength).Y+notes.Font.Height<=notes.ClientSize.Height,"last release-note line remains fully visible: "+lines);
            }
        }
        foreach(float scale in new[]{1f,1.25f,1.5f,1.75f,2f})WithDpi((int)Math.Round(96*scale),delegate{
            var release=Release();release.Body=Lines(100);using(var dialog=new UpdateDialog(release,true)){
                dialog.Scale(new SizeF(scale,scale));dialog.Font=new Font("Microsoft YaHei UI",VisualDesign.BodyFontSize*scale,FontStyle.Regular,GraphicsUnit.Pixel);Show(dialog);
                Check(dialog.Height<=ThemedDialog.WorkingAreaProvider(dialog).Height,"long notes obey the injected working-area cap at "+scale);
            }
        });
    }
    static bool SamePixels(Bitmap left,Bitmap right){
        if(left.Size!=right.Size)return false;
        for(int y=0;y<left.Height;y++)for(int x=0;x<left.Width;x++)if(left.GetPixel(x,y)!=right.GetPixel(x,y))return false;return true;
    }
    static void ComparePaint(Control control,Color background,float scale,Action<Graphics,RectangleF> draw){
        control.BackColor=background;
        using(var actual=new Bitmap(control.Width,control.Height))using(var expected=new Bitmap(control.Width,control.Height)){
            using(var g=Graphics.FromImage(actual)){g.Clear(background);Call(control,"OnPaint",new PaintEventArgs(g,new Rectangle(Point.Empty,actual.Size)));}
            using(var g=Graphics.FromImage(expected)){g.Clear(background);g.ScaleTransform(scale,scale);g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;draw(g,new RectangleF(0,0,control.Width/scale,control.Height/scale));}
            if(!SamePixels(actual,expected)){
                for(int y=0;y<actual.Height;y++)for(int x=0;x<actual.Width;x++)if(actual.GetPixel(x,y)!=expected.GetPixel(x,y))throw new Exception(control.GetType().Name+" "+control.Name+" mismatch at "+scale+" / "+x+","+y+": "+actual.GetPixel(x,y)+" expected "+expected.GetPixel(x,y));
            }
            Check(true,control.GetType().Name+" calls the panel drawing path at "+scale);
        }
    }
    static void SharedControlPainting(){
        foreach(string style in ThemePalette.Keys)foreach(bool dark in new[]{true,false})foreach(float scale in new[]{1f,1.25f,1.5f,2f})WithDpi((int)Math.Round(96*scale),delegate{
            SelectTheme(style,dark);var theme=new DialogTheme(dark);
            // Keep the English default font fixed while the simulated monitor DPI changes.
            using(var font=new Font("Microsoft Sans Serif",8.25f,FontStyle.Regular,GraphicsUnit.Point)){
                foreach(var kind in new[]{ThemedButtonKind.Primary,ThemedButtonKind.Secondary,ThemedButtonKind.Text})using(var button=new ThemedButton(theme,kind) {Font=font,Text="按钮",Size=new Size((int)(120*scale),(int)Math.Round(VisualDesign.SelectionHeight*scale))}){
                    Check(button.GetPreferredSize(Size.Empty).Height==(int)Math.Round(VisualDesign.StageRect(0,4).Height*scale),"dialog/button and DPI segment heights are identical");
                    for(int interaction=0;interaction<3;interaction++){
                        if(interaction==1)Call(button,"OnMouseEnter",EventArgs.Empty);if(interaction==2)Call(button,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,10,10,0));
                        bool hover=interaction>0,pressed=interaction==2;
                        button.Name=style+dark+kind+interaction;
                        ComparePaint(button,theme.Background,scale,delegate(Graphics g,RectangleF rect){
                            if(kind!=ThemedButtonKind.Text){CompactRenderer.Fill(g,rect,theme.Track,VisualDesign.SelectionRadius);CompactRenderer.Selection(g,rect,button.Text,null,kind==ThemedButtonKind.Primary,true,hover,pressed,false,dark);}
                            else{InteractionLayout.Paint(g,DialogInteraction.Card(button,scale,rect),rect,0,new PanelFeedback {HoverAction=hover?0:-1,PressedAction=pressed?0:-1},dark,false,delegate{CompactRenderer.DrawLabel(g,button.Text,rect,VisualDesign.DeviceNameFontSize,false,theme.Accent,StringAlignment.Center);});}
                        });
                    }
                }
                using(var progress=new ThemedProgress(theme) {Font=font,Size=new Size((int)(180*scale),(int)Math.Round(VisualDesign.VolumeTrackHeight*scale)),Value=45}){
                    Check(progress.Height==(int)Math.Round(PopupPages.VolumeTrack(new RectangleF(0,0,260,48)).Height*scale),"progress uses the volume track height");
                    Check(((ProgressBar)progress).Value==45&&progress.AccessibleDescription=="45%","native progress value and accessible percentage agree");
                    ComparePaint(progress,theme.Card,scale,delegate(Graphics g,RectangleF rect){CompactRenderer.DrawVolumeTrack(g,rect,dark,.45f);});
                }
                foreach(bool on in new[]{true,false})using(var toggle=new ThemedSwitch(theme) {AutoSize=false,Font=font,Text="诊断",Checked=on,Size=new Size((int)(240*scale),(int)Math.Round(30*scale))}){
                    var geometry=PopupPages.ToggleBounds(new RectangleF(0,0,240,30));Check(geometry.Size==new SizeF(VisualDesign.ToggleWidth,VisualDesign.ToggleHeight),"switch uses the secondary page's exact bounds");
                    ComparePaint(toggle,theme.Card,scale,delegate(Graphics g,RectangleF rect){CompactRenderer.DrawLabel(g,toggle.Text,new RectangleF(6,0,rect.Width-51,rect.Height),VisualDesign.DeviceNameFontSize,false,theme.Foreground,StringAlignment.Near);PopupPages.DrawToggle(g,rect,on,true,dark);});
                }
                foreach(bool selected in new[]{true,false})using(var segment=new ThemedSegment(theme) {Font=font,Text="类型",Checked=selected,Size=new Size((int)(120*scale),(int)Math.Round(VisualDesign.SelectionHeight*scale))}){
                    ComparePaint(segment,theme.Track,scale,delegate(Graphics g,RectangleF rect){CompactRenderer.Fill(g,rect,theme.Track,VisualDesign.SelectionRadius);CompactRenderer.Selection(g,rect,segment.Text,null,selected,true,false,false,false,dark);});
                }
                using(var section=new ThemedSection(theme) {Font=font,Size=new Size((int)(200*scale),(int)(80*scale))}){
                    ComparePaint(section,theme.Background,scale,delegate(Graphics g,RectangleF rect){CompactRenderer.DrawSurface(g,new RectangleF(.5f,.5f,rect.Width-1,rect.Height-1),dark);});
                }
                using(var input=new TextBox())using(var wrapper=new ThemedTextBox(theme,input) {Font=font,Size=new Size((int)(200*scale),(int)(40*scale))}){
                    ComparePaint(wrapper,theme.Card,scale,delegate(Graphics g,RectangleF rect){CompactRenderer.Fill(g,rect,theme.Track,VisualDesign.SelectionRadius);});
                }
            }
        });
    }
    static void CardsAndTypography(){
        using(var update=new UpdateDialog(Release(),true))using(var feedback=new FeedbackForm(Report(),false,Style.Dark,delegate{},delegate{},""))using(var alias=new ThemedAliasDialog("名称","我的设备",12))using(var message=ThemedMessage.Create("确认","测试确认",MessageBoxButtons.OK))using(var diagnostic=feedback.CreateDiagnosticsDialog()){
            Show(update);Show(feedback);Show(alias);Show(message);Show(diagnostic);
            foreach(var pair in new[]{new {Form=(Form)update,Names=new[]{"VersionCard","ReleaseNotesCard","UpdateStatusCard"}},new {Form=(Form)feedback,Names=new[]{"FeedbackTypeCard","FeedbackDescriptionCard","FeedbackDiagnosticsCard","FeedbackNoticeCard"}},new {Form=(Form)alias,Names=new[]{"AliasCard"}},new {Form=(Form)message,Names=new[]{"MessageCard"}},new {Form=(Form)diagnostic,Names=new[]{"DiagnosticsCard"}}}){
                int bottom=16;foreach(string name in pair.Names){var card=Find<ThemedSection>(pair.Form,name);Check(card.Margin.Bottom==VisualDesign.CardGap,"cards share panel CardGap");int top=pair.Form.PointToClient(card.PointToScreen(Point.Empty)).Y;Check(top==bottom,"cards have 16px outer margin and panel gaps");bottom=top+card.Height+VisualDesign.CardGap;}
            }
            Check(Find<Label>(update,"UpdateTitle").Font.Bold&&Find<Label>(update,"UpdateTitle").Font.SizeInPoints*96/72==15,"update headline remains the only 15px heading");
            var description=Find<TextBox>(feedback,"Description");var wrapper=(ThemedTextBox)description.Parent;
            Check(wrapper.Padding==new Padding(10)&&description.BackColor==feedback.Theme.Track&&description.BorderStyle==BorderStyle.None,"multiline input has a panel track and 10px inset");
            Check(alias.Input.BackColor==alias.Theme.Track&&alias.Input.BorderStyle==BorderStyle.None,"name input shares the panel track");
            description.Focus();Check(description.Focused,"feedback input accepts keyboard focus");
            ComparePaint(wrapper,feedback.Theme.Card,1,delegate(Graphics g,RectangleF rect){CompactRenderer.Fill(g,rect,feedback.Theme.Track,VisualDesign.SelectionRadius);CompactRenderer.Outline(g,rect,feedback.Theme.Accent,1);});
        }
    }
    static void SystemControlsInHighContrast(){
        VisualDesign.HighContrastOverride=true;
        try{using(var update=new UpdateDialog(Release(),true))using(var feedback=new FeedbackForm(Report(),false,true,delegate{},delegate{},"")){
            foreach(var root in new Control[]{update,feedback})SystemControlTree(root);
        }}finally{VisualDesign.HighContrastOverride=false;}
    }
    static void SystemControlTree(Control control){
        if(control is ButtonBase||control is ThemedProgress||control is ThemedSection){
            bool painted=(bool)typeof(Control).GetMethod("GetStyle",Members).Invoke(control,new object[]{ControlStyles.UserPaint});
            Check(!painted,"high contrast uses native painting: "+control.GetType().Name);
        }
        foreach(Control child in control.Controls)SystemControlTree(child);
    }
    static void StateAndKeyboard(){
        using(var dialog=new UpdateDialog(Release(),true)){
            var install=(Button)Field(dialog,"install");var skip=(Button)Field(dialog,"skip");var cancel=(Button)Field(dialog,"cancel");var status=(Label)Field(dialog,"status");
            Check(dialog.AcceptButton==install&&dialog.CancelButton==cancel&&cancel.Text=="稍后","Enter installs and Escape uses Later");
            Check(status.Text=="点击更新后下载，签名与文件校验通过后安装。"&&!status.Text.Contains("通过，"),"compact ready copy describes future verification only");
            Check(Find<Label>(dialog,"UpdateTitle").Text=="发现新版本"&&Find<Label>(dialog,"NewVersion").Text=="9.9.9","separate title and accented target version");
            var notes=Find<RichTextBox>(dialog,"ReleaseNotes");Check(notes.ReadOnly&&notes.Multiline&&!notes.DetectUrls&&notes.Text.Contains("• 第一项")&&notes.Text.Contains("<script>plain text</script>")&&!notes.Text.Contains("\u0001"),"sanitized plain notes with bullet lines");
            int installs=0,skips=0,cancels=0;dialog.InstallRequested+=delegate{installs++;};dialog.SkipRequested+=delegate{skips++;};dialog.CancelRequested+=delegate{cancels++;};
            Show(dialog);Key(dialog,Keys.Enter);skip.PerformClick();Check(installs==1&&skips==1,"default and skip dispatch existing events");
            dialog.Downloading();Check(!install.Enabled&&!skip.Enabled&&cancel.Enabled&&!status.Text.Contains("通过"),"download disables install/skip without claiming verification");
            dialog.Progress(3355443,13212057);Check(status.Text=="已下载 3.2 MB / 12.6 MB（25%）","invariant MB format with integer percentage");
            dialog.Progress(123456,-1);Check((bool)Call(Field(dialog,"progress"),"get_Indeterminate")&&!status.Text.Contains("通过"),"unknown size uses indeterminate progress");
            Key(dialog,Keys.Escape);Check(cancels==1&&!cancel.Enabled&&!dialog.IsDisposed&&status.Text=="正在取消…","Escape cancels an active download without closing");
            dialog.Finished("已取消更新。");Check(install.Enabled&&skip.Enabled&&cancel.Enabled,"cancel completion restores buttons");
            dialog.Downloading();dialog.Progress(10,10);Check(!status.Text.Contains("通过"),"100 percent is not verification");
            dialog.Handoff();Check(status.Text=="签名与文件校验通过，正在退出并安装…"&&!cancel.Enabled&&!install.Enabled&&!skip.Enabled,"only handoff claims verified and disables cancel");
            dialog.Finished("更新未完成：测试错误");Check(install.Enabled&&skip.Enabled&&cancel.Enabled&&status.ForeColor==ThemePalette.LevelColor(0,Style.Dark),"failure restores actions and uses battery red");
            Key(dialog,Keys.Escape);Check(dialog.IsDisposed||!dialog.Visible,"Escape closes after completion");
        }
        using(var dialog=new UpdateDialog(Release(),false)){
            Check(((Button)Field(dialog,"install")).Text=="打开发布页面"&&((Label)Field(dialog,"status")).Text=="绿色模式：请打开发布页面下载安装。","portable action remains explicit in one status line");
        }
    }
    static void ComponentsAndContrast(){
        foreach(string style in ThemePalette.Keys)foreach(bool dark in new[]{true,false}){
            SelectTheme(style,dark);using(var dialog=new UpdateDialog(Release(),true)){
                Check(Type("ThemedDialog").IsInstanceOfType(dialog)&&dialog.FormBorderStyle!=FormBorderStyle.None&&dialog.AutoScaleMode==AutoScaleMode.Dpi,"native chrome and DPI scaling");
                var palette=ThemePalette.Current(dark);Check(dialog.BackColor==palette.Background&&dialog.ForeColor==palette.Foreground&&Math.Abs(dialog.Font.SizeInPoints*96/72-VisualDesign.BodyFontSize)<.1,"theme background and panel body font");
                var theme=typeof(UpdateDialog).BaseType.GetField("Theme",Members).GetValue(dialog);
                foreach(string surface in new[]{"Card","Section"})foreach(string text in new[]{"Foreground","Muted"})Check(Contrast(ColorField(theme,text),ColorField(theme,surface))>=4.5,"text contrast "+style+dark+surface+text);
                Check(Contrast(ColorField(theme,"Accent"),palette.Card)>=3,"focus ring contrast");
                foreach(string field in new[]{"install","skip","cancel"}){
                    var button=(Button)Field(dialog,field);Check(Type("ThemedButton").IsInstanceOfType(button)&&button.AccessibilityObject.Role==AccessibleRole.PushButton,"real accessible Button subclass");
                    foreach(bool enabled in new[]{true,false})for(int state=0;state<3;state++){
                        button.Enabled=enabled;var fill=(Color)Call(button,"FillFor",state>0,state==2);var ink=(Color)Call(button,"TextFor",state>0,state==2);
                        Check(Contrast(ink,fill)>=(enabled&&field!="skip"?4.5:3),"panel button text contrast "+style+dark+field+state+enabled);
                    }
                    button.Enabled=true;
                }
                var input=Find<RichTextBox>(dialog,"ReleaseNotes");Check(input.BorderStyle==BorderStyle.None&&Type("ThemedSection").IsInstanceOfType(input.Parent)&&input.BackColor==palette.Card,"panel card for release notes");
                var progress=(Control)Field(dialog,"progress");Check(Type("ThemedProgress").IsInstanceOfType(progress)&&progress.Height==6,"six-pixel progress");
                dialog.Downloading();Check(!(bool)Call(progress,"get_AnimationRunning"),"reduced motion does not start a timer");
                using(var bitmap=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));}
            }
            using(var feedback=new FeedbackForm(Report(),false,dark,delegate{},delegate{},"")){
                Check(Type("ThemedDialog").IsInstanceOfType(feedback)&&feedback.BackColor==ThemePalette.Current(dark).Background,"feedback shares dialog theme");
                Check(Type("ThemedTextBox").IsInstanceOfType(Find<TextBox>(feedback,"Description").Parent),"feedback input shares wrapper");
            }
        }
    }
    static void HighContrast(){
        VisualDesign.HighContrastOverride=true;
        try{using(var dialog=new UpdateDialog(Release(),true)){
            Check(dialog.BackColor.IsSystemColor&&dialog.ForeColor.IsSystemColor,"high contrast uses system window colors");
            var theme=typeof(UpdateDialog).BaseType.GetField("Theme",Members).GetValue(dialog);
            foreach(string role in new[]{"Card","Section","Foreground","Muted","Accent","Border","Track","Selected","SelectedForeground","Error"})Check(ColorField(theme,role).IsSystemColor,"system-only role "+role);
            int calls=0;ThemedTitleBar.Apply((DialogTheme)theme,true,delegate{calls++;return 0;});Check(calls==0,"high contrast makes no DWM calls");
        }}finally{VisualDesign.HighContrastOverride=false;}
        foreach(bool windows11 in new[]{false,true})foreach(bool fallback in new[]{false,true}){
            var attributes=new List<int>();var values=new List<int>();var theme=new DialogTheme(true);
            ThemedTitleBar.Apply(theme,windows11,delegate(int attribute,int value){attributes.Add(attribute);values.Add(value);return attribute==20&&fallback?-1:0;});
            Check(String.Join(",",attributes.ConvertAll(delegate(int v){return v.ToString();}).ToArray())==(fallback?"20,19":"20")+(windows11?",33,35,36":""),"DWM tries 19 only after 20 fails and requests corners/colors only Windows 11");
            Check(values[0]==1&&(!windows11||values[values.Count-3]==2&&values[values.Count-2]==ColorTranslator.ToWin32(theme.Background)&&values[values.Count-1]==ColorTranslator.ToWin32(theme.Foreground)),"DWM receives round corners, dark flag and background COLORREF");
        }
    }
    static void ProgressMotion(){
        var pref=DeviceSettings.Current;pref.ReducedMotion=false;DeviceSettings.UseTransientConfiguration(pref);
        using(var dialog=new UpdateDialog(Release(),true)){
            Show(dialog);dialog.Downloading();var progress=(ThemedProgress)Field(dialog,"progress");
            Check(progress.AnimationRunning==SystemInformation.IsMenuAnimationEnabled,"unknown download animates when Windows permits motion");
            var clock=(System.Diagnostics.Stopwatch)Field(progress,"clock");Thread.Sleep(20);long before=clock.ElapsedTicks;
            dialog.Progress(20000,-1);Check(clock.ElapsedTicks>=before,"unknown-size progress reports do not restart the motion clock");
            dialog.Progress(1,2);Check(!progress.Indeterminate&&!progress.AnimationRunning&&progress.Value==50,"known total stops indeterminate animation");
            dialog.Progress(Int64.MaxValue,Int64.MaxValue);Check(progress.Value==100&&((Label)Field(dialog,"status")).Text.EndsWith("（100%）"),"large byte counts cannot overflow percentage");
            dialog.Progress(-1,0);Check(progress.Indeterminate&&((Label)Field(dialog,"status")).Text=="已下载 0.0 MB","unknown total and negative byte count format safely");
            var current=DeviceSettings.Current;current.ReducedMotion=true;DeviceSettings.UseTransientConfiguration(current);
            using(var image=new Bitmap(progress.Width,progress.Height)){progress.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));Check(!progress.AnimationRunning,"reduced motion stops existing animation on repaint");}
            dialog.Shutdown();Check(!progress.AnimationRunning,"disposed progress owns no active timer");
        }
    }
    static string Lines(int count){return String.Join(Environment.NewLine,Array.ConvertAll(new int[count],delegate(int unused){return "第几行：设备连接与更新说明";}));}
    static void ScrollbarThemes(){
        var hook=Type("DialogScrollbars").GetField("SetWindowTheme",BindingFlags.Static|BindingFlags.NonPublic);
        Check(hook!=null,"window theme call is injectable");var previous=hook.GetValue(null);
        var calls=new List<string>();var handles=new List<IntPtr>();
        hook.SetValue(null,new Func<IntPtr,string,string,int>(delegate(IntPtr window,string theme,string sub){Check(sub==null,"no theme sub-ID");handles.Add(window);calls.Add(theme);return 0;}));
        try{
            foreach(bool dark in new[]{true,false}){
                SelectTheme("ocean",dark);calls.Clear();handles.Clear();
                using(var update=new UpdateDialog(Release(),true))using(var feedback=new FeedbackForm(Report(),false,dark,delegate{},delegate{},""))using(var diagnostic=feedback.CreateDiagnosticsDialog()){
                    Show(update);Show(feedback);Show(diagnostic);
                    foreach(var input in new TextBoxBase[]{Find<RichTextBox>(update,"ReleaseNotes"),Find<TextBox>(feedback,"Description"),Find<TextBox>(diagnostic,"DiagnosticText")}){
                        Check(handles.Contains(input.Handle),"real multiline handle receives window theme: "+input.Name);
                        int before=calls.Count;typeof(Control).GetMethod("RecreateHandle",Members).Invoke(input,null);
                        Check(calls.Count>before&&handles[handles.Count-1]==input.Handle,"recreated edit handle reapplies theme: "+input.Name);
                    }
                    Check(calls.TrueForAll(delegate(string theme){return theme==(dark?"DarkMode_Explorer":"Explorer");}),"all multiline calls use the current light/dark theme");
                    using(var message=ThemedMessage.Create("确认",Lines(90),MessageBoxButtons.YesNo,MessageBoxDefaultButton.Button2)){
                        int before=calls.Count;Show(message);Check(calls.Count>before,"overflow message scroll area receives window theme");
                    }
                }
            }
            VisualDesign.HighContrastOverride=true;calls.Clear();
            using(var update=new UpdateDialog(Release(),true))using(var feedback=new FeedbackForm(Report(),false,true,delegate{},delegate{},""))using(var diagnostic=feedback.CreateDiagnosticsDialog())using(var message=ThemedMessage.Create("确认",Lines(90),MessageBoxButtons.YesNo,MessageBoxDefaultButton.Button2)){
                Show(update);Show(feedback);Show(diagnostic);Show(message);Check(calls.Count==0,"high contrast never calls SetWindowTheme");
            }
            VisualDesign.HighContrastOverride=false;
            foreach(bool missingEntry in new[]{false,true}){
                hook.SetValue(null,new Func<IntPtr,string,string,int>(delegate{if(missingEntry)throw new EntryPointNotFoundException();return -1;}));
                using(var update=new UpdateDialog(Release(),true)){Show(update);Check(update.Visible,"theme failure is ignored");}
            }
        }finally{hook.SetValue(null,previous);VisualDesign.HighContrastOverride=false;}
    }
    static void ConditionalScrollbars(){
        foreach(bool dark in new[]{true,false}){
            SelectTheme("ocean",dark);
            var longRelease=Release();longRelease.Body=Lines(80);
            using(var initialLong=new UpdateDialog(longRelease,true)){
                Show(initialLong);Check(Find<RichTextBox>(initialLong,"ReleaseNotes").ScrollBars==RichTextBoxScrollBars.Vertical,"initial long notes settle after handle creation");
                using(var bitmap=ClientImage(initialLong))Check(bitmap.Width==initialLong.ClientSize.Width,"initial long notes render without handle recreation during creation");
            }
            using(var update=new UpdateDialog(Release(),true)){
                Show(update);var notes=Find<RichTextBox>(update,"ReleaseNotes");
                Check(notes.ScrollBars==RichTextBoxScrollBars.None,"short release notes have no scrollbars");
                notes.Text=Lines(80);Application.DoEvents();Check(notes.ScrollBars==RichTextBoxScrollBars.Vertical,"long release notes scroll vertically");
                notes.Text="简短说明";Application.DoEvents();Check(notes.ScrollBars==RichTextBoxScrollBars.None,"shortened release notes remove scrollbars");
                notes.Text=new string('中',400);Application.DoEvents();Check(notes.ScrollBars==RichTextBoxScrollBars.Vertical,"wrapped notes measure their actual height");
                update.ClientSize=new Size(1000,1200);Application.DoEvents();Check(notes.ScrollBars==RichTextBoxScrollBars.None,"widening rechecks wrapped notes within the eight-line cap");
            }
            using(var feedback=new FeedbackForm(Report(),false,dark,delegate{},delegate{},"")){
                Show(feedback);var description=Find<TextBox>(feedback,"Description");
                Check(description.ScrollBars==ScrollBars.None,"empty feedback has no scrollbars");
                description.Text=new string('中',1900);Application.DoEvents();Check(description.ScrollBars==ScrollBars.Vertical,"typing long feedback enables scrolling");
                description.Text="简短描述";Application.DoEvents();Check(description.ScrollBars==ScrollBars.None,"deleting feedback removes scrollbar");
                using(var diagnostic=feedback.CreateDiagnosticsDialog()){
                    Show(diagnostic);var input=Find<TextBox>(diagnostic,"DiagnosticText");
                    input.Text="短诊断";Application.DoEvents();Check(input.ScrollBars==ScrollBars.None,"short diagnostics have no scrollbars");
                    input.Text=Lines(80);Application.DoEvents();Check(input.ScrollBars==ScrollBars.Vertical,"tall diagnostics only scroll vertically");
                    input.Text=new string('W',300);Application.DoEvents();Check(input.ScrollBars==ScrollBars.Horizontal,"wide diagnostics only scroll horizontally");
                    input.Text=Lines(80)+Environment.NewLine+new string('W',300);Application.DoEvents();Check(input.ScrollBars==ScrollBars.Both,"wide and tall diagnostics scroll both ways");
                    input.Text=Lines(8);diagnostic.ClientSize=new Size(620,180);Application.DoEvents();Check(input.ScrollBars==ScrollBars.Vertical,"shrinking diagnostics enables vertical scrollbar with the smaller panel body font");
                    diagnostic.ClientSize=new Size(620,500);Application.DoEvents();Check(input.ScrollBars==ScrollBars.None,"growing diagnostics removes vertical scrollbar");
                }
            }
        }
    }
    static void UpdateProgressVisibility(){
        using(var dialog=new UpdateDialog(Release(),true)){
            Show(dialog);var progress=(ThemedProgress)Field(dialog,"progress");var status=(Label)Field(dialog,"status");var install=(Button)Field(dialog,"install");var notes=Find<RichTextBox>(dialog,"ReleaseNotes");
            Check(!progress.Visible,"ready hides empty progress track");
            Rectangle notesBounds=notes.RectangleToScreen(notes.ClientRectangle);Point button=install.PointToScreen(Point.Empty);int readyStatusHeight=status.Height,readyHeight=dialog.Height;
            int added=(int)Math.Round(12*DialogDpi.Scale(dialog))+progress.Height;
            for(int state=0;state<6;state++){
                if(state==0)dialog.Downloading();if(state==1)dialog.Progress(1,2);if(state==2)dialog.Handoff();if(state==3)dialog.Finished("更新未完成：测试错误");if(state==4)dialog.Finished("已取消更新。");if(state==5)dialog.Downloading();
                Application.DoEvents();bool visible=state<3||state==5;
                Check(progress.Visible==visible,"progress visibility in state "+state);
                Check(notes.RectangleToScreen(notes.ClientRectangle)==notesBounds,"notes remain fixed in state "+state);
                Check(dialog.Height==readyHeight+(visible?added:0)&&install.PointToScreen(Point.Empty)==new Point(button.X,button.Y+(visible?added:0)),"only the progress row changes height and button position in state "+state);
                Check(status.Height==readyStatusHeight+(visible?added-progress.Height:0),"status keeps one text row plus progress spacing in state "+state);
            }
        }
        using(var portable=new UpdateDialog(Release(),false)){Show(portable);Check(!((Control)Field(portable,"progress")).Visible,"portable ready hides progress");}
    }
    static bool ContainsTextBox(Control control){if(control is TextBoxBase)return true;foreach(Control child in control.Controls)if(ContainsTextBox(child))return true;return false;}
    static void PlainMessageLayout(){
        foreach(float scale in new[]{1f,1.25f,1.5f,1.75f,2f})WithDpi((int)Math.Round(96*scale),delegate{
            int previousHeight=0;
            foreach(int lines in new[]{1,8,100})using(var dialog=ThemedMessage.Create("反馈问题",lines==1?"刚刚已提交过一次，确定还要再提交吗？":Lines(lines),MessageBoxButtons.YesNo,MessageBoxDefaultButton.Button2)){
                dialog.Scale(new SizeF(scale,scale));dialog.Font=new Font("Microsoft YaHei UI",VisualDesign.BodyFontSize*scale,FontStyle.Regular,GraphicsUnit.Pixel);Show(dialog);
                Check(!ContainsTextBox(dialog),"message contains no text boxes at "+scale);
                var body=Find<Label>(dialog,"MessageText");Check(body.ForeColor==dialog.ForeColor&&Math.Abs(body.Font.SizeInPoints-dialog.Font.SizeInPoints)<.1,"message uses foreground and body font");
                var no=Find<ThemedButton>(dialog,"MessageNo");var yes=Find<ThemedButton>(dialog,"MessageYes");
                Check(dialog.AcceptButton==no&&dialog.ActiveControl==no,"No retains default keyboard focus");
                Check(yes.FillFor(false,false)==dialog.Theme.Selected&&no.FillFor(false,false)==dialog.Theme.Track,"Yes has primary styling independently of focus");
                Check(dialog.ClientSize.Height>=previousHeight,"message grows with text until capped");previousHeight=dialog.ClientSize.Height;
                Check(dialog.ClientSize.Height<=560*scale+2,"message height is capped at "+scale);
                var area=(ScrollableControl)body.Parent;Check(area.VerticalScroll.Visible==(lines==100)&&!area.HorizontalScroll.Visible,"only overflowing message scrolls at "+scale);
                Check(body.Height>=body.GetPreferredSize(new Size(body.Width,0)).Height,"wrapped message text is not clipped");
                Check(no.RectangleToScreen(no.ClientRectangle).Bottom<=dialog.RectangleToScreen(dialog.ClientRectangle).Bottom,"message buttons remain visible");
                if(lines==100){area.AutoScrollPosition=new Point(0,body.Height);Application.DoEvents();Check(area.AutoScrollPosition.Y<0,"long message can reach later text");}
            }
        });
    }
    static void MessageResults(){
        var show=Type("ThemedMessage").GetMethod("Show",BindingFlags.Static|BindingFlags.NonPublic,null,new[]{typeof(IWin32Window),typeof(string),typeof(string),typeof(MessageBoxButtons)},null);
        foreach(var buttons in new[]{MessageBoxButtons.OK,MessageBoxButtons.OKCancel,MessageBoxButtons.YesNo,MessageBoxButtons.YesNoCancel,MessageBoxButtons.RetryCancel,MessageBoxButtons.AbortRetryIgnore}){
            var expected=buttons==MessageBoxButtons.OK?new[]{DialogResult.OK}:buttons==MessageBoxButtons.OKCancel?new[]{DialogResult.OK,DialogResult.Cancel}:buttons==MessageBoxButtons.YesNo?new[]{DialogResult.Yes,DialogResult.No}:buttons==MessageBoxButtons.YesNoCancel?new[]{DialogResult.Yes,DialogResult.No,DialogResult.Cancel}:buttons==MessageBoxButtons.RetryCancel?new[]{DialogResult.Retry,DialogResult.Cancel}:new[]{DialogResult.Abort,DialogResult.Retry,DialogResult.Ignore};
            foreach(var result in expected){
                Exception failure=null;bool visited=false;
                using(var timer=new System.Windows.Forms.Timer {Interval=10}){
                    timer.Tick+=delegate{foreach(Form window in Application.OpenForms)if(window.Text=="result-fixture"){
                        timer.Stop();visited=true;try{var button=Find<Button>(window,"Message"+result);button.PerformClick();}catch(Exception e){failure=e;window.Close();}break;
                    }};
                    timer.Start();Check((DialogResult)show.Invoke(null,new object[]{null,"result-fixture","测试确认",buttons})==result,"message return "+buttons+result);
                }
                if(failure!=null)throw failure;Check(visited,"real modal message was used");
            }
        }
    }
    static void MessageCloseAndRepeat(){
        using(var timer=new System.Windows.Forms.Timer {Interval=10}){
            timer.Tick+=delegate{foreach(Form window in Application.OpenForms)if(window.Text=="close-fixture"){timer.Stop();window.Close();break;}};
            timer.Start();var show=Type("ThemedMessage").GetMethod("Show",BindingFlags.Static|BindingFlags.NonPublic,null,new[]{typeof(IWin32Window),typeof(string),typeof(string),typeof(MessageBoxButtons)},null);
            Check((DialogResult)show.Invoke(null,new object[]{null,"close-fixture","错误提示",MessageBoxButtons.OK})==DialogResult.OK,"OK-only title-bar close returns OK like MessageBox");
        }
        var session=new FeedbackSession();session.MarkSuccess();int copies=0,opens=0;
        using(var feedback=new FeedbackForm(Report(),false,Style.Dark,delegate{copies++;},delegate{opens++;},"",null,session)){
            Show(feedback);Find<TextBox>(feedback,"Description").Text="测试重复确认行为";
            foreach(bool allow in new[]{false,true}){
                Exception failure=null;bool visited=false;
                using(var timer=new System.Windows.Forms.Timer {Interval=10}){
                    timer.Tick+=delegate{foreach(Form window in Application.OpenForms)if(window!=feedback&&window.Text=="反馈问题"){
                        timer.Stop();visited=true;try{Check(window.AcceptButton==Find<Button>(window,"MessageNo"),"repeat defaults to No");if(allow)Find<Button>(window,"MessageYes").PerformClick();else Key(window,Keys.Enter);}catch(Exception e){failure=e;window.DialogResult=DialogResult.No;}break;
                    }};
                    timer.Start();Find<Button>(feedback,"SubmitFeedback").PerformClick();
                }
                if(failure!=null)throw failure;Check(visited&&copies==(allow?1:0)&&opens==(allow?1:0),"themed confirmation preserves repeat submission decision "+allow);
            }
        }
    }
    static void AliasAndDiagnostic(){
        var alias=Type("ThemedAliasDialog");
        foreach(int limit in new[]{12,32})using(var form=(Form)Activator.CreateInstance(alias,Members,null,new object[]{"名称","原名称",limit},CultureInfo.InvariantCulture)){
            var input=Find<TextBox>(form,"AliasInput");Check(input.MaxLength==limit&&input.Text=="原名称"&&form.AcceptButton!=null&&form.CancelButton!=null,"alias limit and default/cancel behavior");
            Show(form);Key(form,Keys.Escape);Check(form.DialogResult==DialogResult.Cancel,"alias Escape never saves");
        }
        using(var form=new FeedbackForm(Report(),false,Style.Dark,delegate{},delegate{},"")){
            using(var diagnostic=(Form)Call(form,"CreateDiagnosticsDialog"))Check(Type("ThemedDialog").IsInstanceOfType(diagnostic)&&Find<TextBox>(diagnostic,"DiagnosticText").ReadOnly,"diagnostics use shared native dialog");
        }
    }
    static void LayoutBounds(){
        foreach(float scale in new[]{1f,1.25f,1.5f,1.75f,2f})WithDpi((int)Math.Round(96*scale),delegate{
            using(var feedback=new FeedbackForm(Report(),false,Style.Dark,delegate{},delegate{},"")){
                feedback.Scale(new SizeF(scale,scale));feedback.Font=new Font("Microsoft YaHei UI",VisualDesign.BodyFontSize*scale,FontStyle.Regular,GraphicsUnit.Pixel);Show(feedback);
                var submit=Find<Button>(feedback,"SubmitFeedback");var cancel=Find<Button>(feedback,"CancelFeedback");
                Check(submit.Left>=0&&submit.Right<=submit.Parent.ClientSize.Width&&cancel.Left>=0&&cancel.Right<=cancel.Parent.ClientSize.Width,"feedback buttons fit horizontally at "+scale);
                Check(!submit.Bounds.IntersectsWith(cancel.Bounds),"feedback button bounds never overlap at "+scale);
            }
            using(var alias=new ThemedAliasDialog("名称","我的设备",12)){
                alias.Scale(new SizeF(scale,scale));alias.Font=new Font("Microsoft YaHei UI",VisualDesign.BodyFontSize*scale,FontStyle.Regular,GraphicsUnit.Pixel);Show(alias);
                Check(alias.Input.Parent.Height>=alias.Input.PreferredHeight+alias.Input.Parent.Padding.Vertical,"single-line input retains eight-DIP vertical padding at "+scale);
            }
            using(var update=new UpdateDialog(Release(),true)){
                update.Scale(new SizeF(scale,scale));update.Font=new Font("Microsoft YaHei UI",VisualDesign.BodyFontSize*scale,FontStyle.Regular,GraphicsUnit.Pixel);Show(update);
                var install=(Button)Field(update,"install");var skip=(Button)Field(update,"skip");var cancel=(Button)Field(update,"cancel");
                Check(!install.Bounds.IntersectsWith(cancel.Bounds)&&!cancel.Bounds.IntersectsWith(skip.Bounds)&&install.Right<=install.Parent.ClientSize.Width,"update buttons fit without overlap at "+scale);
                var notes=Find<ThemedReleaseNotes>(update,"ReleaseNotes");Check(notes.Height>=2*notes.LineHeight,"notes retain at least two readable lines at "+scale);
            }
        });
    }
    static void RenderedDisabledButtons(){
        using(var update=new UpdateDialog(Release(),true)){
            update.Downloading();Show(update);var install=(Button)Field(update,"install");
            Check(!install.Enabled,"download entered before showing preserves disabled action");
            using(var image=new Bitmap(install.Width,install.Height)){
                install.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));
                Check(image.GetPixel(install.Width/2,4).ToArgb()==ThemePalette.Current(Style.Dark).Track.ToArgb(),"disabled primary renders the panel track");
            }
            using(var image=ClientImage(update)){
                var point=update.PointToClient(install.PointToScreen(new Point(install.Width/2,4)));
                Check(image.GetPixel(point.X,point.Y).ToArgb()==ThemePalette.Current(Style.Dark).Track.ToArgb(),"full form preview preserves disabled panel selection rendering");
            }
            update.Shutdown();
        }
    }
    static Bitmap ClientImage(Form form){
        Show(form);using(var image=new Bitmap(form.Width,form.Height)){
            form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));
            var client=form.PointToScreen(Point.Empty);var area=new Rectangle(client.X-form.Left,client.Y-form.Top,form.ClientSize.Width,form.ClientSize.Height);
            var result=image.Clone(area,PixelFormat.Format32bppArgb);
            // RichEdit does not support DrawToBitmap. Ask the same native control
            // to format its real text and paragraph settings into the preview HDC.
            using(var g=Graphics.FromImage(result))foreach(Control control in form.Controls.Find("ReleaseNotes",true)){
                var notes=control as RichTextBox;if(notes==null||!notes.Visible)continue;
                int width=notes.Width-(notes.ScrollBars==RichTextBoxScrollBars.Vertical?SystemInformation.VerticalScrollBarWidth:0);
                using(var content=new Bitmap(Math.Max(1,width),Math.Max(1,notes.Height)))using(var ink=Graphics.FromImage(content)){
                    ink.Clear(notes.BackColor);IntPtr hdc=ink.GetHdc();
                    try{
                        var bounds=new PrintRect {Left=30,Top=0,Right=(int)Math.Round((width-2)*1440f/96),Bottom=(int)Math.Round(notes.Height*1440f/96)};
                        var range=new PrintRange {Device=hdc,Target=hdc,Area=bounds,Page=bounds,First=0,Last=notes.TextLength};
                        int printed=SendMessage(notes.Handle,0x0439,(IntPtr)1,ref range).ToInt32();Check(notes.TextLength==0||printed>0,"native release-note text is rendered in the preview");
                    }finally{ink.ReleaseHdc(hdc);SendMessage(notes.Handle,0x0439,IntPtr.Zero,IntPtr.Zero);}
                    var origin=form.PointToClient(notes.PointToScreen(Point.Empty));g.DrawImageUnscaled(content,origin);
                }
            }
            return result;
        }
    }
    static void SaveClient(Form form,string output,string name){
        // The main panel uses its real renderer at the same logical scale as the dialog.
        bool dark=((ThemedDialog)form).Theme.Dark;var dpi=new DpiConfig {ActiveStage=1};
        foreach(int value in new[]{400,800,1600,3200})dpi.Stages.Add(new DpiStage {XDpi=value,YDpi=value});
        var audio=new AudioReading {SpeakerAvailable=true,SpeakerId="speaker",SpeakerName="桌面扬声器",HeadsetAvailable=true,HeadsetId="headset",HeadsetName="有线耳机",ActiveOutput="speaker",MicrophoneAvailable=true,MicrophoneMuted=false,OutputVolumePercent=55,VolumeEndpointId="speaker",TimestampUtc=DateTime.UtcNow};
        using(var image=ClientImage(form))using(var panel=new Bitmap(260,VisualDesign.PanelSizeFor(true,dpi).Height)){
            using(var g=Graphics.FromImage(panel)){g.Clear(Color.FromArgb(115,120,128));CompactRenderer.Paint(g,new BatteryReading {Percent=76,TimestampUtc=DateTime.UtcNow},null,null,false,dpi,null,false,dark,null,audio);}
            using(var comparison=new Bitmap(image.Width+panel.Width+72,Math.Max(image.Height,panel.Height)+88))using(var g=Graphics.FromImage(comparison))using(var font=new Font("Microsoft YaHei UI",12,FontStyle.Regular,GraphicsUnit.Pixel)){
                g.Clear(Color.FromArgb(115,120,128));g.DrawString(ThemePalette.Name(DeviceSettings.Current.ThemeStyle)+" / "+(dark?"深色":"浅色")+" · 弹窗客户区（系统标题栏未渲染）",font,Brushes.White,16,12);
                g.DrawString("同配色主面板 · 100%",font,Brushes.White,image.Width+48,12);
                g.DrawImageUnscaled(image,16,48);g.DrawImageUnscaled(panel,image.Width+48,48);
                string path=Path.Combine(output,name+".png");comparison.Save(path,ImageFormat.Png);Console.WriteLine(path);
            }
        }
    }
    static void Render(string output){
        Directory.CreateDirectory(output);
        foreach(string style in ThemePalette.Keys)foreach(bool dark in new[]{true,false}){
            SelectTheme(style,dark);
            string suffix="-"+style+"-"+(dark?"dark":"light");
            string[] states={"ready","downloading","handoff","failed"};
            for(int state=0;state<4;state++)using(var dialog=new UpdateDialog(Release(),true)){
                    if(state>0){dialog.Downloading();dialog.Progress(5945426,13212057);}
                    if(state==2)dialog.Handoff();if(state==3)dialog.Finished("更新未完成：无法下载文件，请稍后再试。");
                    SaveClient(dialog,output,"dialog-update-"+states[state]+suffix);
                }
                using(var feedback=new FeedbackForm(Report(),false,dark,delegate{},delegate{},null,null,new FeedbackSession())){
                    Find<TextBox>(feedback,"Description").Text="连接设备后电量没有刷新，希望增加更多型号的支持。";
                    SaveClient(feedback,output,"dialogs"+suffix);
                    using(var diagnostics=feedback.CreateDiagnosticsDialog())SaveClient(diagnostics,output,"dialog-diagnostics"+suffix);
                }
            using(var alias=new ThemedAliasDialog("托盘图标名称","我的设备",12))SaveClient(alias,output,"dialog-alias"+suffix);
            using(var message=ThemedMessage.Create("反馈问题","刚刚已提交过一次，确定还要再提交吗？",MessageBoxButtons.YesNo,MessageBoxDefaultButton.Button2))SaveClient(message,output,"dialog-confirmation"+suffix);
            using(var shortNotes=new UpdateDialog(Release(),true))SaveClient(shortNotes,output,"dialog-release-short"+suffix);
            var longRelease=Release();longRelease.Body="本次更新内容\r\n"+Lines(80);
            using(var longNotes=new UpdateDialog(longRelease,true))SaveClient(longNotes,output,"dialog-release-long"+suffix);
        }
        WithDpi(192,delegate{WithWorkArea(new Rectangle(0,0,1024,768),delegate{
            SelectTheme("ocean",true);
            using(var feedback=new FeedbackForm(Report(),false,true,delegate{},delegate{},FeedbackRelay.Url)){
                feedback.Scale(new SizeF(2,2));feedback.Font=new Font("Microsoft YaHei UI",24,FontStyle.Regular,GraphicsUnit.Pixel);
                Find<TextBox>(feedback,"Description").Text="连接设备后电量没有刷新，希望增加更多型号的支持。";
                SaveClient(feedback,output,"dialog-fit-feedback-top-200");
                Find<Panel>(feedback,"DialogContentViewport").AutoScrollPosition=new Point(0,Int32.MaxValue);Application.DoEvents();
                SaveClient(feedback,output,"dialog-fit-feedback-bottom-200");
                using(var diagnostic=feedback.CreateDiagnosticsDialog()){
                    diagnostic.Scale(new SizeF(2,2));diagnostic.Font=feedback.Font;SaveClient(diagnostic,output,"dialog-fit-diagnostics-200");
                }
            }
            var release=Release();release.Body=Lines(80);using(var update=new UpdateDialog(release,true)){
                update.Scale(new SizeF(2,2));update.Font=new Font("Microsoft YaHei UI",24,FontStyle.Regular,GraphicsUnit.Pixel);SaveClient(update,output,"dialog-fit-update-200");
            }
            using(var message=ThemedMessage.Create("确认",Lines(100),MessageBoxButtons.YesNo)){
                message.Scale(new SizeF(2,2));message.Font=new Font("Microsoft YaHei UI",24,FontStyle.Regular,GraphicsUnit.Pixel);SaveClient(message,output,"dialog-fit-message-200");
            }
        });});
    }
    static int ThreadWindowCount(){int count=0;EnumThreadWindows(GetCurrentThreadId(),delegate{count++;return true;},IntPtr.Zero);return count;}
    static void ConsumeStartupShowCommand(){
        var type=Type("DialogVisibility");
        type.GetMethod("ConsumeStartupShowCommand",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
    }
    static int HiddenHost(string kind){
        // No tray context, hardware readers, settings files, update network or real app launch.
        using(var watchdog=new System.Threading.Timer(delegate{Fatal(new TimeoutException("dialog host timeout"));},null,8000,Timeout.Infinite)){
            StartupInfo startup;GetStartupInfo(out startup);
            if(kind!="startup")Check((startup.Flags&1)!=0&&startup.Show==0,"host received STARTF_USESHOWWINDOW/SW_HIDE (flags="+startup.Flags+", show="+startup.Show+")");
            if(kind=="startup"){
                int before=ThreadWindowCount();IntPtr active=GetActiveWindow();ConsumeStartupShowCommand();
                Check(ThreadWindowCount()==before&&GetActiveWindow()==active,"startup helper leaves no window or activation change");
            }
            using(Form form=kind=="startup"?(Form)new Form():kind=="update"?new UpdateDialog(Release(),true):new ThemedDialog())
            using(var timer=new System.Windows.Forms.Timer {Interval=200})
            using(var context=new ApplicationContext()){
                // Match UpdateDialog's taskbar mode. WinForms otherwise creates an
                // owner window that can consume the startup command before this one.
                form.Text="Hidden-start regression fixture";form.ShowInTaskbar=true;
                timer.Tick+=delegate{
                    timer.Stop();Check(IsWindowVisible(form.Handle),kind+": first dialog is natively visible");
                    Check(!IsIconic(form.Handle),kind+": first dialog is not minimized");form.Close();context.ExitThread();
                };
                if(kind=="startup")ShowWindow(form.Handle,1);else form.Show();
                timer.Start();Application.Run(context);return 0;
            }
        }
    }
    static string hostDirectory,hostExecutable;
    static string HostExecutable(){
        if(hostExecutable!=null)return hostExecutable;
        hostDirectory=Path.Combine(Path.GetTempPath(),"myfinder-dialog-host-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(hostDirectory);
        string executable=Path.Combine(hostDirectory,"HiddenDialogHost.exe");
        // Use a GUI-subsystem host through ShellExecute to match updater launches.
        // Invoke only the test entry point, never the real tray application's Main.
        using(var compiler=System.CodeDom.Compiler.CodeDomProvider.CreateProvider("CSharp")){
            var options=new System.CodeDom.Compiler.CompilerParameters {GenerateExecutable=true,OutputAssembly=executable,CompilerOptions="/target:winexe /platform:x64"};
            options.TempFiles=new System.CodeDom.Compiler.TempFileCollection(hostDirectory,false);
            var result=compiler.CompileAssemblyFromSource(options,@"using System;using System.Reflection;
static class Host {
    [STAThread] static int Main(string[] args){
        AppDomain.CurrentDomain.UnhandledException+=delegate(object sender,UnhandledExceptionEventArgs e){Console.Error.WriteLine(e.ExceptionObject);Console.Error.Flush();Environment.Exit(1);};
        try{Console.SetError(new System.IO.StreamWriter(args[1]){AutoFlush=true});var forwarded=new string[args.Length-2];Array.Copy(args,2,forwarded,0,forwarded.Length);return (int)Assembly.LoadFrom(args[0]).EntryPoint.Invoke(null,new object[]{forwarded});}
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}");
            if(result.Errors.HasErrors)throw new Exception("Hidden host compilation failed: "+result.Errors[0]);
        }
        return hostExecutable=executable;
    }
    static void Child(string arguments,ProcessWindowStyle style,bool success,string error){
        string tests=typeof(ThemedDialogTests).Assembly.Location,errorPath=null;
        var info=new ProcessStartInfo(tests,arguments) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=style,RedirectStandardOutput=true,RedirectStandardError=true};
        if(arguments.StartsWith("--hidden-host ",StringComparison.Ordinal)){
            string host=HostExecutable();errorPath=Path.Combine(hostDirectory,Guid.NewGuid().ToString("N")+".stderr");
            info=new ProcessStartInfo(host,"\""+tests+"\" \""+errorPath+"\" "+arguments) {UseShellExecute=true,WindowStyle=style};
        }
        // Exception cases launch the test executable directly so a wrapper's catch
        // cannot mask a missing Main/ThreadException/UnhandledException handler.
        using(var process=Process.Start(info)){
            try{
                var stdout=info.RedirectStandardOutput?process.StandardOutput.ReadToEndAsync():null;
                var errors=info.RedirectStandardError?process.StandardError.ReadToEndAsync():null;
                Check(process.WaitForExit(12000),"child exits within timeout: "+arguments);
                if(errors!=null)Check(stdout.Wait(1000)&&errors.Wait(1000),"child output drains: "+arguments);
                string stderr=errors!=null?errors.Result:File.ReadAllText(errorPath);
                Check(success?process.ExitCode==0:process.ExitCode==1,"child exit code "+process.ExitCode+": "+arguments+"\n"+stderr);
                if(error!=null)Check(stderr.Contains(error),"child reports exception on stderr: "+arguments);
            }finally{if(!process.HasExited){process.Kill();process.WaitForExit(5000);}}
        }
    }
    static void ExistingUpdateWindow(string state){
        using(var form=new UpdateDialog(Release(),true)){
            Show(form);
            if(state=="managed-hidden")form.Hide();else ShowWindow(form.Handle,state=="minimized"?6:0);
            Check(state=="minimized"?IsIconic(form.Handle):!IsWindowVisible(form.Handle),"fixture begins "+state);
            // Bypass the constructor's live update timer; exercise the actual existing-window branch.
            var controller=(UpdateController)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(UpdateController));
            typeof(UpdateController).GetField("dialog",Members).SetValue(controller,form);Call(controller,"ShowAvailable");Application.DoEvents();
            Check(IsWindowVisible(form.Handle)&&!IsIconic(form.Handle),"ShowAvailable restores "+state+" window");
            Check(Object.ReferenceEquals(Field(controller,"dialog"),form),"ShowAvailable reuses the same dialog");
        }
    }
    static int ExceptionHost(string kind){
        if(kind=="main")throw new InvalidOperationException("fixture main exception");
        using(var watchdog=new System.Threading.Timer(delegate{Fatal(new TimeoutException("exception host timeout"));},null,8000,Timeout.Infinite)){
            if(kind=="worker"){
                var thread=new Thread(delegate(){throw new InvalidOperationException("fixture worker exception");});thread.Start();thread.Join();
            }else using(var timer=new System.Windows.Forms.Timer {Interval=50})using(var context=new ApplicationContext()){
                timer.Tick+=delegate{timer.Stop();throw new InvalidOperationException("fixture UI exception");};timer.Start();Application.Run(context);
            }
        }
        return 2;
    }
    static void Fatal(object error){
        try{Console.Error.WriteLine(error);Console.Error.Flush();}finally{Environment.Exit(1);}
    }
    [STAThread] static int Main(string[] args){
        // Install both handlers before initialization, including --render. Exiting here
        // prevents WinForms/Windows Error Reporting from leaving an unattended error box.
        AppDomain.CurrentDomain.UnhandledException+=delegate(object sender,UnhandledExceptionEventArgs e){Fatal(e.ExceptionObject);};
        Application.ThreadException+=delegate(object sender,ThreadExceptionEventArgs e){Fatal(e.Exception);};
        try{Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);return MainCore(args);}
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{if(hostDirectory!=null)Directory.Delete(hostDirectory,true);}
    }
    static int MainCore(string[] args){
        NativeDisplay.EnableDpiAwareness();Application.EnableVisualStyles();var previous=DeviceSettings.Current;VisualDesign.HighContrastOverride=false;
        var previousArea=ThemedDialog.WorkingAreaProvider;ThemedDialog.WorkingAreaProvider=delegate{return new Rectangle(0,0,3200,2400);};
        try{if(args.Length==2&&args[0]=="--render"){Render(args[1]);return 0;}SelectTheme("ocean",false);
            if(args.Length==0||args.Length==1&&args[0]=="--paint-only"){
                Run("opaque dialog paint",OpaqueDialogPainting);Run("initial read-only focus",InitialReadOnlyFocus);Run("compact update size",CompactUpdateSize);
                Console.WriteLine(checks+" paint/focus/size checks, "+failures+" failed groups");if(args.Length==1)return failures==0?0:1;
            }
            if(args.Length==2&&args[0]=="--hidden-host")return HiddenHost(args[1]);
            if(args.Length==2&&args[0]=="--exception-host")return ExceptionHost(args[1]);
            if(args.Length==0||!args[0].StartsWith("--",StringComparison.Ordinal)||args[0]=="--visibility-only"){
                foreach(string kind in new[]{"themed","update","startup"})Run("hidden startup: "+kind,delegate{Child("--hidden-host "+kind,ProcessWindowStyle.Hidden,true,null);});
                Run("normal startup helper has no visible side effects",delegate{Child("--hidden-host startup",ProcessWindowStyle.Normal,true,null);});
                foreach(string state in new[]{"native-hidden","managed-hidden","minimized"})Run("existing update: "+state,delegate{ExistingUpdateWindow(state);});
                foreach(string kind in new[]{"main","UI","worker"})Run("caught host exception: "+kind,delegate{Child("--exception-host "+kind,ProcessWindowStyle.Hidden,false,"fixture "+kind+" exception");});
                Run("caught render exception",delegate{Child("--render \""+Application.ExecutablePath+"\"",ProcessWindowStyle.Hidden,false,"System.IO.IOException");});
                if(args.Length==1&&args[0]=="--visibility-only"){Console.WriteLine(checks+" visibility/exception checks, "+failures+" failed groups");return failures==0?0:1;}
            }
            Run("button height is independent of default font",ButtonFontScaling);
            Run("button and segment heights at 96/120/144/192 DPI",ButtonDpiMatrix);Run("window DPI and Graphics fallback",DpiSource);
            if(args.Length==1&&args[0]=="--dpi-only")return failures==0?0:1;
            foreach(string kind in new[]{"feedback","diagnostic","update","message","alias"})Run("actual client size: "+kind,delegate{ActualClientSizeLayout(kind);});
            if(args.Length==1&&args[0]=="--client-only")return failures==0?0:1;
            Run("injected work areas",InjectedWorkAreas);Run("monitor and DPI changes",WorkAreaAndDpiChanges);
            if(args.Length==1&&args[0]=="--fit-only")return failures==0?0:1;
            Run("shared panel geometry and card painting",SharedPanelGeometry);Run("background separation and rounded native chrome",DialogBackground);Run("accessible segments and switch",SegmentsAndSwitch);Run("content-sized release notes",AdaptiveReleaseNotes);Run("shared button, switch and progress painting",SharedControlPainting);Run("cards and panel typography",CardsAndTypography);Run("native high contrast controls",SystemControlsInHighContrast);
            if(args.Length==1&&args[0]=="--soft-only")return failures==0?0:1;
            Run("window scrollbar themes and handle recreation",ScrollbarThemes);Run("scrollbars follow text and viewport",ConditionalScrollbars);Run("update progress adds only one row",UpdateProgressVisibility);Run("plain wrapped messages and bounded overflow",PlainMessageLayout);Run("update states and keyboard in all themes",delegate{foreach(string style in ThemePalette.Keys)foreach(bool dark in new[]{true,false}){SelectTheme(style,dark);StateAndKeyboard();}});Run("components and all theme contrasts",ComponentsAndContrast);Run("system-only high contrast and chrome",HighContrast);Run("progress motion and byte boundaries",ProgressMotion);Run("modal message return values",MessageResults);Run("message close and actual feedback repeat confirmation",MessageCloseAndRepeat);Run("alias and diagnostic dialogs",AliasAndDiagnostic);Run("dialog layout from 100 to 200 percent",LayoutBounds);Run("disabled button bitmap state",RenderedDisabledButtons);Console.WriteLine(checks+" themed-dialog checks, "+failures+" failed groups");return failures==0?0:1;}
        finally{ThemedDialog.WorkingAreaProvider=previousArea;DeviceSettings.UseTransientConfiguration(previous);VisualDesign.HighContrastOverride=null;}
    }
}
