using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

// Real HWND/desktop captures only; no DrawToBitmap or WM_PRINT fallback.
// Run from an unlocked interactive Windows desktop. Do not cover the fixtures.
internal static class CaptureDialogs {
    const BindingFlags Members=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
    static readonly List<Form> windows=new List<Form>();
    static string output;static int captures,checks;static PromptMask prompt;static bool checksOnly;
    static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);checks++;}
    static T Find<T>(Control root,string name) where T:Control {
        var found=root.Controls.Find(name,true);Require(found.Length==1,"Missing fixture control: "+name);return (T)found[0];
    }
    static void Pump(){
        // Let native invalidation, delayed RichEdit handle creation and DWM finish.
        var clock=Stopwatch.StartNew();do{Application.DoEvents();Thread.Sleep(10);}while(clock.ElapsedMilliseconds<80);
        try{DwmFlush();}catch(DllNotFoundException){}catch(EntryPointNotFoundException){}
    }
    static FeedbackReport Report(){return new FeedbackReport("fixture","fixture-windows",new WirelessDevice[0],new SuppressedDevice[0],new UnadaptedDevice[0],new ReaderHealth[0],new string[0]);}
    static UpdateRelease Release(int lines){
        var notes=new List<string>();for(int i=1;i<=lines;i++)notes.Add("- 改进设备显示与交互 "+i);
        return new UpdateRelease {Tag="v9.9.9",Body=String.Join("\n",notes)};
    }
    static void Open(ThemedDialog form){
        windows.Add(form);form.TopMost=true;form.Show();form.FitToWorkingArea(true);form.Activate();Pump();
        Require(IsWindowVisible(form.Handle)&&form.Opacity==1,"Fixture must be natively visible and opaque");
        Require(Screen.PrimaryScreen.WorkingArea.Contains(form.Bounds),"Window must fit the primary visible work area: "+form.Bounds);
        float scale=DialogDpi.Scale(form);
        Console.WriteLine(form.GetType().Name+" DPI="+Math.Round(scale*96)+" bounds="+form.Bounds);
        if(form is UpdateDialog){
            var notes=Find<ThemedReleaseNotes>(form,"ReleaseNotes");
            Require(form.ActiveControl==form.AcceptButton&&notes.SelectionLength==0&&notes.HideSelection,"Update must open on its primary action without selected notes");
            Require(form.ClientSize.Width/scale>=370&&form.ClientSize.Width/scale<=390,"Update client width must be about 380 logical pixels");
            Require(notes.Height>=2*notes.LineHeight&&notes.Height<=8*notes.LineHeight+5,"Release notes must show two to eight lines");
            if(notes.LineCountFor(notes.Width)<=6)Require(form.Height/scale<=420,"Typical update window exceeds 420 logical pixels");
        }
        AssertReadOnly(form);
    }
    static void AssertReadOnly(Control control){
        var text=control as TextBoxBase;
        if(text!=null&&text.ReadOnly)Require(text.SelectionLength==0&&text.HideSelection&&text.TabStop&&!text.Focused,"Read-only control initially selected/focused: "+text.Name);
        foreach(Control child in control.Controls)AssertReadOnly(child);
    }
    static Rectangle Region(Form form,Control control){
        var rect=control.RectangleToScreen(control.ClientRectangle);
        for(Control parent=control.Parent;parent!=null;parent=parent.Parent)rect=Rectangle.Intersect(rect,parent.RectangleToScreen(parent.ClientRectangle));
        rect.Offset(-form.Left,-form.Top);return rect;
    }
    static Rectangle SegmentRegion(Form form,ThemedSegments group,int index){
        var rect=group.RectangleToScreen(group.SegmentBounds(index));rect.Offset(-form.Left,-form.Top);
        return Rectangle.Intersect(rect,Region(form,group));
    }
    static void Reveal(Control control){
        for(Control parent=control.Parent;parent!=null;parent=parent.Parent){var scroll=parent as ScrollableControl;if(scroll!=null&&scroll.AutoScroll)scroll.ScrollControlIntoView(control);}
        Pump();
    }
    sealed class Pixels {
        internal readonly int Width,Height;readonly int[] data;
        internal Pixels(Bitmap image){
            Width=image.Width;Height=image.Height;data=new int[Width*Height];
            var locked=image.LockBits(new Rectangle(0,0,Width,Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
            try{for(int y=0;y<Height;y++)Marshal.Copy(IntPtr.Add(locked.Scan0,y*locked.Stride),data,y*Width,Width);}finally{image.UnlockBits(locked);}
        }
        internal Color At(int x,int y){return Color.FromArgb(data[y*Width+x]);}
        internal int Count(Rectangle area,Func<Color,bool> predicate){
            area=Rectangle.Intersect(area,new Rectangle(0,0,Width,Height));int count=0;
            for(int y=area.Top;y<area.Bottom;y++)for(int x=area.Left;x<area.Right;x++)if(predicate(At(x,y)))count++;
            return count;
        }
    }
    static bool Near(Color a,Color b,int tolerance){return Math.Abs(a.R-b.R)<=tolerance&&Math.Abs(a.G-b.G)<=tolerance&&Math.Abs(a.B-b.B)<=tolerance;}
    static double Brightness(Color c){return .2126*c.R+.7152*c.G+.0722*c.B;}
    static void SegmentPixels(Pixels pixels,Rectangle region,DialogTheme theme){
        if(region.Width<4||region.Height<4)return;
        double area=region.Width*region.Height;
        int dark=pixels.Count(region,delegate(Color c){return theme.Dark?Brightness(c)<Brightness(theme.Card)*.6:c.R<12&&c.G<12&&c.B<12;});
        int selected=pixels.Count(region,delegate(Color c){return Near(c,theme.Selected,12);});
        Require(dark/area<.12,"Unselected segment contains a large black/dark patch");
        Require(selected/area<.16,"Unselected segment contains a large selected-color patch");
    }
    static void SwitchPixels(Pixels pixels,Rectangle region,DialogTheme theme,float scale){
        // Ignore the legitimate toggle on the right and the one-pixel focus outline.
        region.Inflate(-(int)Math.Ceiling(2*scale),-(int)Math.Ceiling(2*scale));
        region.Width=Math.Max(0,region.Width-(int)Math.Ceiling(48*scale));
        if(region.Width==0||region.Height<=0)return;
        int selected=pixels.Count(region,delegate(Color c){return Near(c,theme.Selected,12);});
        Require(selected<Math.Max(4,region.Width*region.Height*.025),"Switch label/left margin contains selected-color contamination");
    }
    sealed class PromptMask {
        readonly List<Point> ink=new List<Point>();readonly Color color;readonly Size size;
        internal PromptMask(Pixels pixels,Rectangle bounds,Color foreground){
            size=bounds.Size;color=foreground;
            for(int y=0;y<size.Height;y++)for(int x=0;x<size.Width;x++)if(Near(pixels.At(bounds.X+x,bounds.Y+y),color,24))ink.Add(new Point(x,y));
            Require(ink.Count>=20,"Cannot read the real-screen character-count prompt (desktop may be locked or covered)");
        }
        internal bool FoundOutside(Pixels pixels,Rectangle search,Rectangle allowed){
            // Match actual antialiased glyphs, not the background. Several distant
            // ink anchors reject candidates before the full mask comparison.
            search=Rectangle.Intersect(search,new Rectangle(0,0,pixels.Width,pixels.Height));
            for(int y=search.Top;y<=search.Bottom-size.Height;y++)for(int x=search.Left;x<=search.Right-size.Width;x++){
                if(allowed.Contains(new Rectangle(x,y,size.Width,size.Height)))continue;
                bool candidate=true;
                for(int a=0;a<7;a++){var p=ink[a*(ink.Count-1)/6];if(!Near(pixels.At(x+p.X,y+p.Y),color,28)){candidate=false;break;}}
                if(!candidate)continue;
                int matched=0;foreach(var p in ink)if(Near(pixels.At(x+p.X,y+p.Y),color,28))matched++;
                if(matched>=ink.Count*.92)return true;
            }
            return false;
        }
    }
    static void Validate(Form form,Bitmap image,bool opening){
        var themed=(ThemedDialog)form;var pixels=new Pixels(image);var client=Region(form,form);
        Require(Near(pixels.At(client.Left+2,client.Top+2),themed.Theme.Background,8),"Desktop capture is blank, obscured, or not the fixture");
        var feedback=form as FeedbackForm;
        if(feedback!=null){
            var group=Find<ThemedSegments>(form,"Categories");
            for(int i=0;i<group.OptionCount;i++){
                if(i!=group.SelectedIndex)SegmentPixels(pixels,SegmentRegion(form,group,i),themed.Theme);
            }
            SwitchPixels(pixels,Region(form,Find<ThemedSwitch>(form,"IncludeDiagnostics")),themed.Theme,DialogDpi.Scale(form));
            var count=Find<Label>(form,"FeedbackRemaining");var allowed=Region(form,count);
            if(prompt==null&&count.Text.StartsWith("还需输入",StringComparison.Ordinal)&&allowed.Width>0&&allowed.Height==count.Height){
                var prefix=allowed;prefix.Width=Math.Min(prefix.Width,TextRenderer.MeasureText("还需输入",count.Font).Width);
                prompt=new PromptMask(pixels,prefix,count.ForeColor);
            }
            if(prompt!=null)Require(!prompt.FoundOutside(pixels,client,allowed),"Character-count prompt appears outside its label");
        }
        if(opening){
            foreach(var text in ReadOnlyControls(form)){
                var area=Region(form,text);if(area.Width<=0||area.Height<=0)continue;
                ReadOnlyPixels(pixels,area,text.Name);
            }
        }
    }
    static void ReadOnlyPixels(Pixels pixels,Rectangle area,string name){
        int selected=pixels.Count(area,delegate(Color c){return Near(c,SystemColors.Highlight,10);});
        Require(selected<area.Width*area.Height*.03,"Read-only text opens with a large system-selection highlight: "+name);
    }
    static IEnumerable<TextBoxBase> ReadOnlyControls(Control root){
        var text=root as TextBoxBase;if(text!=null&&text.ReadOnly)yield return text;
        foreach(Control child in root.Controls)foreach(var nested in ReadOnlyControls(child))yield return nested;
    }
    static void Capture(ThemedDialog form,string name,bool opening=false){
        Pump();Require(Screen.PrimaryScreen.WorkingArea.Contains(form.Bounds),"Capture outside the primary screen: "+name);
        // Explicitly requested non-screen mode exercises the exact same native
        // messages/layout. It never claims capture acceptance or writes fake PNGs.
        if(checksOnly)return;
        using(var image=new Bitmap(form.Width,form.Height,PixelFormat.Format32bppArgb)){
            using(var graphics=Graphics.FromImage(image))graphics.CopyFromScreen(form.Location,Point.Empty,image.Size,CopyPixelOperation.SourceCopy);
            string file=Path.Combine(output,name+".png");image.Save(file,ImageFormat.Png);captures++;
            try{Validate(form,image,opening);}catch(Exception error){throw new InvalidOperationException(file+": "+error.Message,error);}
        }
    }
    static void ResizeAndInvalidate(ThemedDialog form,string name){
        var original=form.ClientSize;var area=Screen.PrimaryScreen.WorkingArea;
        form.ClientSize=new Size(Math.Min(original.Width+48,area.Right-form.Left-(form.Width-original.Width)),Math.Min(original.Height+40,area.Bottom-form.Top-(form.Height-original.Height)));
        Capture(form,name+"-grow");form.ClientSize=original;Capture(form,name+"-shrink");
        form.Invalidate(true);Capture(form,name+"-invalidate");
    }
    static void Hover(ThemedDialog form,Control control,string name){
        Reveal(control);var point=new IntPtr((control.Height/2<<16)|(control.Width/2&0xffff));
        // WinForms synthesizes MouseEnter from WM_MOUSEMOVE; NCMOUSEMOVE primes
        // its enter tracking so repeated cycles do not leave an old hover state.
        SendMessage(control.Handle,0x02A3,IntPtr.Zero,IntPtr.Zero);
        SendMessage(control.Handle,0x00A0,IntPtr.Zero,IntPtr.Zero);
        SendMessage(control.Handle,0x0200,IntPtr.Zero,point);
        Require((bool)control.GetType().GetField("hovered",Members).GetValue(control),"Native mouse-enter did not set the hovered state");Capture(form,name+"-enter");
        SendMessage(control.Handle,0x0200,IntPtr.Zero,new IntPtr((control.Height/2<<16)|Math.Max(1,control.Width/2-2)));Capture(form,name+"-move");
        SendMessage(control.Handle,0x02A3,IntPtr.Zero,IntPtr.Zero);
        Require(!(bool)control.GetType().GetField("hovered",Members).GetValue(control),"Native mouse-leave did not clear the hovered state");Capture(form,name+"-leave");
    }
    static IntPtr SegmentPoint(ThemedSegments group,int index,int offset=0){
        var bounds=group.SegmentBounds(index);return new IntPtr(((bounds.Top+bounds.Height/2)<<16)|((bounds.Left+bounds.Width/2+offset)&0xffff));
    }
    static void HoverSegment(ThemedDialog form,ThemedSegments group,int index,string name){
        Reveal(group);SendMessage(group.Handle,0x0200,IntPtr.Zero,SegmentPoint(group,index));
        Require((int)group.GetType().GetField("hovered",Members).GetValue(group)==index,"Native mouse move did not hover the segment");Capture(form,name+"-enter");
        SendMessage(group.Handle,0x0200,IntPtr.Zero,SegmentPoint(group,index,-2));Capture(form,name+"-move");
        SendMessage(group.Handle,0x02A3,IntPtr.Zero,IntPtr.Zero);
        Require((int)group.GetType().GetField("hovered",Members).GetValue(group)==-1,"Native mouse leave did not clear the segment hover");Capture(form,name+"-leave");
    }
    static void ExerciseFeedback(string suffix,bool dark){
        prompt=null;
        using(var form=new FeedbackForm(Report(),true,dark,delegate{},delegate{},"",null,new FeedbackSession())){
            Open(form);Capture(form,"feedback-"+suffix+"-open",true);
            var remaining=Find<Label>(form,"FeedbackRemaining");Reveal(remaining);Capture(form,"feedback-"+suffix+"-prompt");
            if(!checksOnly)Require(prompt!=null,"Character-count glyph template must be captured");
            string[] categories={"device","battery","ui","other"};
            var group=Find<ThemedSegments>(form,"Categories");
            for(int i=0;i<categories.Length;i++){
                string category=categories[i];
                Reveal(group);var point=SegmentPoint(group,i);
                SendMessage(group.Handle,0x0201,new IntPtr(1),point);SendMessage(group.Handle,0x0202,IntPtr.Zero,point);
                Require(group.SelectedIndex==i&&form.SelectedCategory==category&&(group.GetOption("Category-"+category).State&AccessibleStates.Checked)!=0,"Native segment click and accessible selection failed");
                Capture(form,"feedback-"+suffix+"-"+category+"-select");HoverSegment(form,group,i,"feedback-"+suffix+"-"+category);
                HoverSegment(form,group,(i+1)%categories.Length,"feedback-"+suffix+"-"+category+"-unselected");
                Require(group.SelectedIndex==i,"Hover must not change any segment's selected value");
            }
            var input=Find<TextBox>(form,"Description");Reveal(input);input.Focus();
            foreach(char ch in "连接设备后电量没有刷新，请检查。")SendMessage(input.Handle,0x0102,(IntPtr)ch,IntPtr.Zero);
            Require(input.Text.Length>=5,"Native text entry failed");Capture(form,"feedback-"+suffix+"-typing");
            var toggle=Find<ThemedSwitch>(form,"IncludeDiagnostics");Reveal(toggle);
            for(int i=0;i<2;i++){bool was=toggle.Checked;SendMessage(toggle.Handle,0x00F5,IntPtr.Zero,IntPtr.Zero);Require(toggle.Checked!=was,"Native switch click failed");Capture(form,"feedback-"+suffix+"-switch-"+i);}
            ResizeAndInvalidate(form,"feedback-"+suffix);
            using(var diagnostics=form.CreateDiagnosticsDialog()){
                form.Hide();Open(diagnostics);Capture(diagnostics,"diagnostics-"+suffix+"-open",true);ResizeAndInvalidate(diagnostics,"diagnostics-"+suffix);
            }
        }
        prompt=null;
    }
    static void ExerciseUpdate(string suffix){
        using(var form=new UpdateDialog(Release(5),true)){
            Open(form);Capture(form,"update-"+suffix+"-ready",true);ResizeAndInvalidate(form,"update-"+suffix+"-ready");
            var notes=Find<ThemedReleaseNotes>(form,"ReleaseNotes");var before=notes.RectangleToScreen(notes.ClientRectangle);int height=form.Height;
            form.Downloading();form.Progress(5945426,13212057);Pump();
            int added=Find<ThemedProgress>(form,"UpdateProgress").Height+(int)Math.Round(12*DialogDpi.Scale(form));
            Require(notes.RectangleToScreen(notes.ClientRectangle)==before&&form.Height==height+added,"Download must add just the progress row below the notes (notes "+before+" -> "+notes.RectangleToScreen(notes.ClientRectangle)+", height "+height+" -> "+form.Height+", expected delta "+added+")");
            Capture(form,"update-"+suffix+"-downloading");ResizeAndInvalidate(form,"update-"+suffix+"-downloading");
            form.Handoff();Capture(form,"update-"+suffix+"-verified");ResizeAndInvalidate(form,"update-"+suffix+"-verified");
            form.Finished("更新未完成：无法下载文件，请稍后再试。");Capture(form,"update-"+suffix+"-failed");ResizeAndInvalidate(form,"update-"+suffix+"-failed");
        }
        foreach(int lines in new[]{2,8,9})using(var form=new UpdateDialog(Release(lines),true)){
            Open(form);Capture(form,"update-"+suffix+"-notes-"+lines,true);
            Require(Find<ThemedReleaseNotes>(form,"ReleaseNotes").ScrollBars==(lines>8?RichTextBoxScrollBars.Vertical:RichTextBoxScrollBars.None),"Eight-line scrollbar boundary failed");
        }
    }
    static void PixelCheckerSelfTest(){
        var theme=new DialogTheme(true);var area=new Rectangle(0,0,200,40);
        using(var image=new Bitmap(200,40,PixelFormat.Format32bppArgb))using(var g=Graphics.FromImage(image)){
            g.Clear(theme.Track);SegmentPixels(new Pixels(image),area,theme);
            foreach(var color in new[]{Color.Black,theme.Selected}){
                g.Clear(theme.Track);using(var b=new SolidBrush(color))g.FillRectangle(b,0,0,150,40);
                bool rejected=false;try{SegmentPixels(new Pixels(image),area,theme);}catch(InvalidOperationException){rejected=true;}Require(rejected,"Checker must reject injected segment contamination");
            }
            g.Clear(theme.Card);SwitchPixels(new Pixels(image),area,theme,1);
            using(var b=new SolidBrush(theme.Selected))g.FillRectangle(b,3,5,50,20);
            bool switchRejected=false;try{SwitchPixels(new Pixels(image),area,theme,1);}catch(InvalidOperationException){switchRejected=true;}Require(switchRejected,"Checker must reject injected switch contamination");
            g.Clear(theme.Card);ReadOnlyPixels(new Pixels(image),area,"fixture");
            using(var b=new SolidBrush(SystemColors.Highlight))g.FillRectangle(b,0,0,150,30);
            bool selectionRejected=false;try{ReadOnlyPixels(new Pixels(image),area,"fixture");}catch(InvalidOperationException){selectionRejected=true;}Require(selectionRejected,"Checker must reject initial selected text");
        }
        using(var image=new Bitmap(300,120,PixelFormat.Format32bppArgb))using(var g=Graphics.FromImage(image))using(var font=new Font("Microsoft YaHei UI",12,GraphicsUnit.Pixel)){
            g.Clear(theme.Card);TextRenderer.DrawText(g,"还需输入",font,new Point(4,4),theme.Muted,theme.Card);
            var allowed=new Rectangle(4,4,80,20);var mask=new PromptMask(new Pixels(image),allowed,theme.Muted);
            Require(!mask.FoundOutside(new Pixels(image),new Rectangle(0,0,300,120),allowed),"Prompt in its own label must pass");
            using(var clone=image.Clone(allowed,PixelFormat.Format32bppArgb))g.DrawImageUnscaled(clone,100,60);
            Require(mask.FoundOutside(new Pixels(image),new Rectangle(0,0,300,120),allowed),"Checker must find a duplicate prompt outside its label");
        }
    }
    static void Fatal(object error){Console.Error.WriteLine(error);Console.Error.Flush();Environment.Exit(1);}
    [STAThread] static int Main(string[] args){
        SetErrorMode(3);AppDomain.CurrentDomain.UnhandledException+=delegate(object sender,UnhandledExceptionEventArgs e){Fatal(e.ExceptionObject);};
        Application.ThreadException+=delegate(object sender,ThreadExceptionEventArgs e){Fatal(e.Exception);};
        try{
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            NativeDisplay.EnableDpiAwareness();Application.EnableVisualStyles();DialogVisibility.ConsumeStartupShowCommand();
            VisualDesign.HighContrastOverride=false;
            DeviceSettings.UseTransientConfiguration(new AudioBindings {ThemeStyle="ocean",Theme="dark",ReducedMotion=true});
            PixelCheckerSelfTest();
            Require(args.Length==1,"Usage: CaptureDialogs.exe <TEMP output directory> | --checks-only");
            checksOnly=args[0]=="--checks-only";
            if(!checksOnly){
                output=Path.GetFullPath(args[0]);
                Require(output.StartsWith(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"Output must be below TEMP");
                Directory.CreateDirectory(output);
            }
            ThemedDialog.WorkingAreaProvider=delegate{return Screen.PrimaryScreen.WorkingArea;};
            using(var watchdog=new System.Threading.Timer(delegate{Fatal(new TimeoutException("Capture exceeded five minutes"));},null,300000,Timeout.Infinite)){
                foreach(string style in ThemePalette.Keys)foreach(bool dark in new[]{false,true}){
                    DeviceSettings.UseTransientConfiguration(new AudioBindings {ThemeStyle=style,Theme=dark?"dark":"light",ReducedMotion=true});
                    string suffix=style+"-"+(dark?"dark":"light");ExerciseFeedback(suffix,dark);ExerciseUpdate(suffix);
                    using(var alias=new ThemedAliasDialog("托盘图标名称","我的设备",12)){
                        Open(alias);Capture(alias,"alias-"+suffix+"-open",true);alias.Input.SelectAll();
                        foreach(char ch in "测试名称")SendMessage(alias.Input.Handle,0x0102,(IntPtr)ch,IntPtr.Zero);
                        Capture(alias,"alias-"+suffix+"-typing");ResizeAndInvalidate(alias,"alias-"+suffix);
                    }
                    using(var confirmation=ThemedMessage.Create("确认提示","刚刚已提交过一次，确定还要再提交吗？",MessageBoxButtons.YesNo,MessageBoxDefaultButton.Button2)){
                        Open(confirmation);Capture(confirmation,"confirmation-"+suffix+"-open",true);Hover(confirmation,(Control)confirmation.AcceptButton,"confirmation-"+suffix);ResizeAndInvalidate(confirmation,"confirmation-"+suffix);
                    }
                    Console.WriteLine("PASS "+suffix);
                }
            }
            Console.WriteLine(checksOnly?checks+" pixel-checker/native-interaction assertions passed; real-screen capture NOT run":captures+" real-screen PNGs; "+checks+" assertions passed; output="+output);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{foreach(var form in windows)if(!form.IsDisposed)form.Dispose();Application.ExitThread();}
    }
}
