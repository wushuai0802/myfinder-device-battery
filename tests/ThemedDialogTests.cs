using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

// Offline fixtures only. Compile/run with an output directory under TEMP.
static class ThemedDialogTests {
    const BindingFlags Members=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks,failures;
    static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
    static void Run(string name,Action test){try{test();Console.WriteLine("PASS "+name);}catch(Exception e){failures++;Console.Error.WriteLine("FAIL "+name+": "+(e.InnerException??e).Message);}}
    static Type Type(string name){var type=typeof(UpdateDialog).Assembly.GetType("AtkBatteryTray."+name);Check(type!=null,name+" is available");return type;}
    static object Field(object instance,string name){return instance.GetType().GetField(name,Members).GetValue(instance);}
    static object Call(object instance,string method,params object[] args){return instance.GetType().GetMethod(method,Members).Invoke(instance,args);}
    static T Find<T>(Control control,string name) where T:Control {var matches=control.Controls.Find(name,true);Check(matches.Length==1,"one "+name);return (T)matches[0];}
    static UpdateRelease Release(){return new UpdateRelease {Tag="v9.9.9",Body="更新说明\n- 第一项\n- 第二项\n<script>plain text</script>\u0001"};}
    static FeedbackReport Report(){return new FeedbackReport("fixture","fixture-windows",new WirelessDevice[0],new SuppressedDevice[0],new UnadaptedDevice[0],new ReaderHealth[0],new string[0]);}
    static double Luminance(Color color){double[] v={color.R/255.0,color.G/255.0,color.B/255.0};for(int i=0;i<3;i++)v[i]=v[i]<=.04045?v[i]/12.92:Math.Pow((v[i]+.055)/1.055,2.4);return .2126*v[0]+.7152*v[1]+.0722*v[2];}
    static double Contrast(Color a,Color b){double x=Luminance(a),y=Luminance(b);return (Math.Max(x,y)+.05)/(Math.Min(x,y)+.05);}
    static Color ColorField(object instance,string name){return (Color)Field(instance,name);}
    static void SelectTheme(string style,bool dark){DeviceSettings.UseTransientConfiguration(new AudioBindings {ThemeStyle=style,Theme=dark?"dark":"light",ReducedMotion=true,RememberReadings=false});}
    static void Show(Form form){form.Opacity=0;form.ShowInTaskbar=false;form.Show();Application.DoEvents();}
    static void Key(Form form,Keys key){typeof(Form).GetMethod("ProcessDialogKey",Members).Invoke(form,new object[]{key});Application.DoEvents();}
    static void StateAndKeyboard(){
        using(var dialog=new UpdateDialog(Release(),true)){
            var install=(Button)Field(dialog,"install");var skip=(Button)Field(dialog,"skip");var cancel=(Button)Field(dialog,"cancel");var status=(Label)Field(dialog,"status");
            Check(dialog.AcceptButton==install&&dialog.CancelButton==cancel&&cancel.Text=="稍后","Enter installs and Escape uses Later");
            Check(status.Text=="点击‘更新并重启’后下载，签名和文件校验通过后才安装。"&&!status.Text.Contains("通过，"),"ready copy describes future verification only");
            Check(Find<Label>(dialog,"UpdateTitle").Text=="发现新版本"&&Find<Label>(dialog,"NewVersion").Text=="9.9.9","separate title and accented target version");
            var notes=Find<TextBox>(dialog,"ReleaseNotes");Check(notes.ReadOnly&&notes.Multiline&&notes.Text.Contains("• 第一项")&&notes.Text.Contains("<script>plain text</script>")&&!notes.Text.Contains("\u0001"),"sanitized plain notes with bullet lines");
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
            Check(((Button)Field(dialog,"install")).Text=="打开发布页面"&&((Label)Field(dialog,"status")).Text=="当前为绿色模式。打开发布页面下载；安装到稳定目录后可以一键更新。","portable behavior/copy preserved");
        }
    }
    static void ComponentsAndContrast(){
        foreach(string style in ThemePalette.Keys)foreach(bool dark in new[]{true,false}){
            SelectTheme(style,dark);using(var dialog=new UpdateDialog(Release(),true)){
                Check(Type("ThemedDialog").IsInstanceOfType(dialog)&&dialog.FormBorderStyle!=FormBorderStyle.None&&dialog.AutoScaleMode==AutoScaleMode.Dpi,"native chrome and DPI scaling");
                var palette=ThemePalette.Current(dark);Check(dialog.BackColor==palette.Card&&dialog.ForeColor==palette.Foreground&&Math.Abs(dialog.Font.SizeInPoints*96/72-14)<.1,"theme card and 14px body font");
                var theme=typeof(UpdateDialog).BaseType.GetField("Theme",Members).GetValue(dialog);
                foreach(string surface in new[]{"Card","Section"})foreach(string text in new[]{"Foreground","Muted"})Check(Contrast(ColorField(theme,text),ColorField(theme,surface))>=4.5,"text contrast "+style+dark+surface+text);
                Check(Contrast(ColorField(theme,"Accent"),palette.Card)>=3,"focus ring contrast");
                foreach(string field in new[]{"install","skip","cancel"}){
                    var button=(Button)Field(dialog,field);Check(Type("ThemedButton").IsInstanceOfType(button)&&button.AccessibilityObject.Role==AccessibleRole.PushButton,"real accessible Button subclass");
                    foreach(bool enabled in new[]{true,false})for(int state=0;state<3;state++){
                        button.Enabled=enabled;var fill=(Color)Call(button,"FillFor",state>0,state==2);var ink=(Color)Call(button,"TextFor",state>0,state==2);
                        Check(Contrast(ink,fill)>=4.5,"button text contrast "+style+dark+field+state+enabled);
                    }
                    button.Enabled=true;
                }
                var input=Find<TextBox>(dialog,"ReleaseNotes");Check(input.BorderStyle==BorderStyle.None&&Type("ThemedSection").IsInstanceOfType(input.Parent)&&input.BackColor==ColorField(theme,"Section"),"rounded themed notes section");
                var progress=(Control)Field(dialog,"progress");Check(Type("ThemedProgress").IsInstanceOfType(progress)&&progress.Height==6,"six-pixel progress");
                dialog.Downloading();Check(!(bool)Call(progress,"get_AnimationRunning"),"reduced motion does not start a timer");
                using(var bitmap=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));}
            }
            using(var feedback=new FeedbackForm(Report(),false,dark,delegate{},delegate{},"")){
                Check(Type("ThemedDialog").IsInstanceOfType(feedback)&&feedback.BackColor==ThemePalette.Current(dark).Card,"feedback shares dialog theme");
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
            Check(String.Join(",",attributes.ConvertAll(delegate(int v){return v.ToString();}).ToArray())==(fallback?"20,19":"20")+(windows11?",35,36":""),"DWM tries 19 only after 20 fails and colors only Windows 11");
            Check(values[0]==1&&(!windows11||values[values.Count-2]==ColorTranslator.ToWin32(theme.Card)&&values[values.Count-1]==ColorTranslator.ToWin32(theme.Foreground)),"DWM receives dark flag and real COLORREFs");
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
        foreach(float scale in new[]{1f,1.25f,1.5f,1.75f,2f}){
            using(var feedback=new FeedbackForm(Report(),false,Style.Dark,delegate{},delegate{},"")){
                feedback.Scale(new SizeF(scale,scale));feedback.Font=new Font("Microsoft YaHei UI",14*scale,FontStyle.Regular,GraphicsUnit.Pixel);Show(feedback);
                var submit=Find<Button>(feedback,"SubmitFeedback");var cancel=Find<Button>(feedback,"CancelFeedback");
                Check(submit.Left>=0&&submit.Right<=submit.Parent.ClientSize.Width&&cancel.Left>=0&&cancel.Right<=cancel.Parent.ClientSize.Width,"feedback buttons fit horizontally at "+scale);
                Check(!submit.Bounds.IntersectsWith(cancel.Bounds),"feedback button bounds never overlap at "+scale);
            }
            using(var alias=new ThemedAliasDialog("名称","我的设备",12)){
                alias.Scale(new SizeF(scale,scale));alias.Font=new Font("Microsoft YaHei UI",14*scale,FontStyle.Regular,GraphicsUnit.Pixel);Show(alias);
                Check(alias.Input.Parent.Height>=alias.Input.PreferredHeight+alias.Input.Parent.Padding.Vertical,"single-line input retains eight-DIP vertical padding at "+scale);
            }
            using(var update=new UpdateDialog(Release(),true)){
                update.Scale(new SizeF(scale,scale));update.Font=new Font("Microsoft YaHei UI",14*scale,FontStyle.Regular,GraphicsUnit.Pixel);Show(update);
                var install=(Button)Field(update,"install");var skip=(Button)Field(update,"skip");var cancel=(Button)Field(update,"cancel");
                Check(!install.Bounds.IntersectsWith(cancel.Bounds)&&!cancel.Bounds.IntersectsWith(skip.Bounds)&&install.Right<=install.Parent.ClientSize.Width,"update buttons fit without overlap at "+scale);
                Check(Find<TextBox>(update,"ReleaseNotes").Height>=100*scale,"notes retain scrollable remaining space at "+scale);
            }
        }
    }
    static void RenderedDisabledButtons(){
        using(var update=new UpdateDialog(Release(),true)){
            update.Downloading();Show(update);var install=(Button)Field(update,"install");
            Check(!install.Enabled,"download entered before showing preserves disabled action");
            using(var image=new Bitmap(install.Width,install.Height)){
                install.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));
                Check(image.GetPixel(install.Width/2,4).ToArgb()==ThemePalette.Current(Style.Dark).Card.ToArgb(),"disabled primary renders its neutral background");
            }
            using(var image=ClientImage(update)){
                var point=update.PointToClient(install.PointToScreen(new Point(install.Width/2,4)));
                Check(image.GetPixel(point.X,point.Y).ToArgb()==ThemePalette.Current(Style.Dark).Card.ToArgb(),"full form preview preserves disabled button rendering");
            }
            update.Shutdown();
        }
    }
    static Bitmap ClientImage(Form form){
        Show(form);using(var image=new Bitmap(form.Width,form.Height)){
            form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));
            var client=form.PointToScreen(Point.Empty);var area=new Rectangle(client.X-form.Left,client.Y-form.Top,form.ClientSize.Width,form.ClientSize.Height);
            return image.Clone(area,PixelFormat.Format32bppArgb);
        }
    }
    static void Render(string output){
        Directory.CreateDirectory(output);
        foreach(string style in ThemePalette.Keys)foreach(bool dark in new[]{true,false}){
            SelectTheme(style,dark);
            using(var sheet=new Bitmap(1880,1250))using(var g=Graphics.FromImage(sheet))using(var font=new Font("Microsoft YaHei UI",14,FontStyle.Regular,GraphicsUnit.Pixel))using(var heading=new Font("Microsoft YaHei UI",18,FontStyle.Bold,GraphicsUnit.Pixel)){
                g.Clear(Color.FromArgb(115,120,128));
                g.DrawString(ThemePalette.Name(style)+" / "+(dark?"深色":"浅色")+" · 弹窗客户区预览（保留系统标题栏；此处不渲染标题栏）",heading,Brushes.White,24,16);
                string[] states={"准备","下载 45%","校验通过 / Handoff","失败 / 可重试"};
                for(int state=0;state<4;state++)using(var dialog=new UpdateDialog(Release(),true)){
                    if(state>0){dialog.Downloading();dialog.Progress(5945426,13212057);}
                    if(state==2)dialog.Handoff();if(state==3)dialog.Finished("更新未完成：无法下载文件，请稍后再试。");
                    g.DrawString("更新 · "+states[state],font,Brushes.White,24+state*464,60);
                    using(var image=ClientImage(dialog))g.DrawImageUnscaled(image,24+state*464,88);
                }
                g.DrawString("反馈问题",font,Brushes.White,24,620);
                using(var feedback=new FeedbackForm(Report(),false,dark,delegate{},delegate{},null,null,new FeedbackSession())){
                    Find<TextBox>(feedback,"Description").Text="连接设备后电量没有刷新，希望增加更多型号的支持。";
                    using(var image=ClientImage(feedback))g.DrawImageUnscaled(image,24,648);
                    g.DrawString("诊断信息 · 只读",font,Brushes.White,1052,620);
                    using(var diagnostics=feedback.CreateDiagnosticsDialog())using(var image=ClientImage(diagnostics))g.DrawImageUnscaled(image,1052,648);
                }
                g.DrawString("名称输入 · 12 字上限",font,Brushes.White,624,620);
                using(var alias=new ThemedAliasDialog("托盘图标名称","我的设备",12))using(var image=ClientImage(alias))g.DrawImageUnscaled(image,624,648);
                g.DrawString("重复提交确认 · 默认选择“否”",font,Brushes.White,588,850);
                using(var message=ThemedMessage.Create("反馈问题","刚刚已提交过一次，确定还要再提交吗？",MessageBoxButtons.YesNo,MessageBoxDefaultButton.Button2))using(var image=ClientImage(message))g.DrawImageUnscaled(image,588,878);
                string path=Path.Combine(output,"dialogs-"+style+"-"+(dark?"dark":"light")+".png");sheet.Save(path,ImageFormat.Png);Console.WriteLine(path);
            }
        }
    }
    [STAThread] static int Main(string[] args){
        NativeDisplay.EnableDpiAwareness();Application.EnableVisualStyles();var previous=DeviceSettings.Current;VisualDesign.HighContrastOverride=false;
        try{if(args.Length==2&&args[0]=="--render"){Render(args[1]);return 0;}SelectTheme("ocean",false);Run("update states and keyboard in all themes",delegate{foreach(string style in ThemePalette.Keys)foreach(bool dark in new[]{true,false}){SelectTheme(style,dark);StateAndKeyboard();}});Run("components and all theme contrasts",ComponentsAndContrast);Run("system-only high contrast and chrome",HighContrast);Run("progress motion and byte boundaries",ProgressMotion);Run("modal message return values",MessageResults);Run("message close and actual feedback repeat confirmation",MessageCloseAndRepeat);Run("alias and diagnostic dialogs",AliasAndDiagnostic);Run("dialog layout from 100 to 200 percent",LayoutBounds);Run("disabled button bitmap state",RenderedDisabledButtons);Console.WriteLine(checks+" themed-dialog checks, "+failures+" failed groups");return failures==0?0:1;}
        finally{DeviceSettings.UseTransientConfiguration(previous);VisualDesign.HighContrastOverride=null;}
    }
}
