using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

// Offline only: fake HTTP, clipboard, browser and clock. Build and run under TEMP.
static class FeedbackFormTests {
    const string Relay="https://feedback.invalid/service";
    const string Issue="https://github.com/wushuai0802/myfinder-device-battery/issues/42";
    const string Submitted="已提交，编号 #42。可在「我的反馈」查看回复和补充资料。";
    static int checks,failures;
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window,IntPtr insertAfter,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
    static void Run(string name,Action action){try{action();Console.WriteLine("PASS "+name);}catch(Exception error){failures++;Console.Error.WriteLine("FAIL "+name+": "+error);}}
    static void Pump(Func<bool> ready){var until=DateTime.UtcNow.AddSeconds(5);while(!ready()&&DateTime.UtcNow<until){Application.DoEvents();Thread.Sleep(2);}Check(ready(),"background work settles without blocking the UI");}
    static T Find<T>(Control form,string name) where T:Control {var controls=form.Controls.Find(name,true);Check(controls.Length==1,"one control named "+name);return (T)controls[0];}
    static FeedbackReport Report(){return new FeedbackReport("fixture-version","fixture-windows",new[]{new WirelessDevice {Name="diagnostic-mouse",Kind="mouse",Provider="fixture",Percent=65}},new SuppressedDevice[0],new UnadaptedDevice[0],new ReaderHealth[0],new string[0]);}
    static string Decode(string url){return Uri.UnescapeDataString(url.Substring(url.IndexOf("&diagnostics=",StringComparison.Ordinal)+13));}
    static UpdateResponse Response(int status,string body=null,long? length=null){var bytes=Encoding.UTF8.GetBytes(body??"{}");return new UpdateResponse(status,new MemoryStream(bytes),length??bytes.Length,null,null);}
    static UpdateResponse Success(){return Response(201,"{\"number\":42,\"url\":\""+Issue+"\"}");}
    sealed class FakeHttp:IFeedbackTransport {
        internal int Calls;internal Uri Uri;internal byte[] Body;
        internal Func<CancellationToken,UpdateResponse> Reply=delegate{return Success();};
        public UpdateResponse Post(Uri uri,byte[] body,CancellationToken token){Interlocked.Increment(ref Calls);Uri=uri;Body=body;return Reply(token);}
    }
    static FeedbackResult Submit(FakeHttp http,string relay=Relay,int timeout=20000){return new FeedbackClient(relay,http,new FeedbackSession(),timeout).Submit("other"," valid description ",Report().FullText,CancellationToken.None);}
    static FeedbackForm Form(FakeHttp http,string relay=Relay,Action<string> copy=null,Action<string> open=null,FeedbackSession session=null,Func<string,bool> confirm=null){
        return new FeedbackForm(Report(),false,false,copy??delegate{},open??delegate{},relay,http,session??new FeedbackSession(),confirm??delegate{return true;});
    }
    static void Show(FeedbackForm form){form.Opacity=0;form.Show();Application.DoEvents();Find<TextBox>(form,"Description").Text="设备连接后没有显示电量";}
    static void RelayConfiguration(){
        var uri=new Uri(FeedbackRelay.Url,UriKind.Absolute);
        Check(uri.Scheme==Uri.UriSchemeHttps,"shipped relay uses HTTPS");
        Check(uri.Host=="myfinder-feedback-relay.wushuai0802.deno.net","shipped relay uses the deployed host");
        Check(FeedbackRelay.Url=="https://myfinder-feedback-relay.wushuai0802.deno.net","shipped relay is the exact base URL without a trailing slash");
        Check(FeedbackRelay.Endpoint(FeedbackRelay.Url).AbsoluteUri==FeedbackRelay.Url+"/feedback","shipped relay builds the feedback endpoint");
        var http=new FakeHttp();using(var form=Form(http,null)){
            Show(form);var submit=Find<Button>(form,"SubmitFeedback");
            Check(submit.Text=="提交"&&Find<Label>(form,"FeedbackNotice").Text.Contains("公开显示在 GitHub"),"default form offers in-app submission with the public issue notice");
            submit.PerformClick();Pump(delegate{return Find<Label>(form,"FeedbackStatus").Text==Submitted;});
            Check(http.Calls==1&&http.Uri.AbsoluteUri==FeedbackRelay.Url+"/feedback","default form sends once to the shipped endpoint through fake HTTP");form.Close();
        }
    }
    static void Validation(){
        Check(FeedbackInput.DefaultCategory(true)=="device"&&FeedbackInput.DefaultCategory(false)=="other","two entry defaults");
        string[] ids={"device","battery","ui","other"},labels={"设备不识别","电量或 DPI 不对","界面或操作","其他"};
        for(int i=0;i<ids.Length;i++){Check(FeedbackInput.CategoryAt(i)==ids[i],"category mapping "+i);Check(FeedbackInput.CategoryLabel(ids[i])==labels[i],"category label "+i);}
        foreach(string text in new[]{null,""," \r\n\t ","1234"," 1234 ",new string('中',2001)})Check(!FeedbackInput.Valid("other",text),"invalid trimmed length");
        foreach(string text in new[]{"12345"," \r\n12345\t ",new string('中',2000)})Check(FeedbackInput.Valid("device",text),"valid trimmed boundary");
        Check(!FeedbackInput.Valid("invented","12345"),"unknown category rejected");
        foreach(bool missing in new[]{false,true})using(var form=new FeedbackForm(Report(),missing,false,delegate{},delegate{},"")){
            Check(form.SelectedCategory==FeedbackInput.DefaultCategory(missing),"actual radio default");Check(form.Text=="反馈问题"&&!form.ShowInTaskbar&&form.AutoScaleMode==AutoScaleMode.Dpi,"modal/DPI window properties");
            Check(Find<CheckBox>(form,"IncludeDiagnostics").Checked,"diagnostics checked by default");
            Check(Find<Label>(form,"FeedbackNotice").Text=="将打开 GitHub 提交，需要 GitHub 账号","explicit empty relay retains the browser account notice");
            var submit=Find<Button>(form,"SubmitFeedback");var input=Find<TextBox>(form,"Description");Check(!submit.Enabled&&submit.Text=="在浏览器中提交","initial invalid description disables browser submission");
            input.Text=" 12345 ";Check(submit.Enabled&&Find<Label>(form,"FeedbackRemaining").Text.Contains("1995"),"trimmed remaining count");
            input.Text=new string('中',2001);Check(!submit.Enabled&&Find<Label>(form,"FeedbackRemaining").Text.Contains("超出 1"),"over-limit input stays visible and disabled");
            input.Text="1234";Check(!submit.Enabled&&Find<Label>(form,"FeedbackRemaining").Text.Contains("还需输入 1"),"minimum count");
        }
    }
    static void PayloadAndPolicy(){
        var http=new FakeHttp();Check(Submit(http).Kind==FeedbackResultKind.Success,"valid response");
        Check(http.Calls==1&&http.Uri.AbsoluteUri==Relay+"/feedback","one POST to the configured endpoint");
        var fields=UpdateJson.Read<Dictionary<string,object>>(http.Body,true);var keys=new List<string>(fields.Keys);keys.Sort(StringComparer.Ordinal);
        Check(String.Join(",",keys.ToArray())=="appVersion,category,description,diagnostics,replyKey,schema","exact request field set");
        Check(Convert.ToInt32(fields["schema"])==2&&FeedbackProtocol.ValidKey((string)fields["replyKey"])&&(string)fields["category"]=="other"&&(string)fields["description"]=="valid description","schema, key, category and trimmed description");
        Check((string)fields["diagnostics"]==Report().FullText&&(string)fields["appVersion"]==AppRuntime.Version,"whitelisted diagnostics and runtime version only");
        fields=UpdateJson.Read<Dictionary<string,object>>(FeedbackPayload.Create("ui","中文 & \"描述\"\n第二行",null),true);
        Check(fields.ContainsKey("diagnostics")&&fields["diagnostics"]==null&&(string)fields["description"]=="中文 & \"描述\"\n第二行","null diagnostics and JSON escaping");
        string longText=new string('中',20001);fields=UpdateJson.Read<Dictionary<string,object>>(FeedbackPayload.Create("device","12345",longText),true);
        Check(((string)fields["diagnostics"]).Length==20000&&((string)fields["diagnostics"]).EndsWith("[已截断]"),"bounded diagnostics include truncation marker");
        fields=UpdateJson.Read<Dictionary<string,object>>(FeedbackPayload.Create("battery","12345",new string('中',20000)),true);Check(((string)fields["diagnostics"]).Length==20000&&!((string)fields["diagnostics"]).EndsWith("[已截断]"),"exact diagnostics limit is unchanged");
        var request=FeedbackHttp.CreateRequest(new Uri(Relay+"/feedback"));
        Check(request.Method=="POST"&&request.ContentType=="application/json"&&request.UserAgent=="MyFinderDeviceBattery/"+AppRuntime.Version,"HTTP method and only application headers");
        Check(!request.AllowAutoRedirect&&request.Timeout==20000&&request.ReadWriteTimeout==20000,"no redirects and bounded I/O");
        Check(!request.UseDefaultCredentials&&request.Credentials==null&&!request.PreAuthenticate&&request.CookieContainer==null,"no account, cookies or default credentials");
        foreach(string header in request.Headers.AllKeys)Check(header=="Content-Type"||header=="User-Agent","no extra application header: "+header);
        Check(ServicePointManager.SecurityProtocol==SecurityProtocolType.Tls12,"TLS 1.2 minimum");request.Abort();
        bool rejected=false;try{FeedbackHttp.CreateRequest(new Uri("http://feedback.invalid/feedback"));}catch(ArgumentException){rejected=true;}Check(rejected,"HTTP also rejected by concrete transport before I/O");
    }
    static void Failures(){
        var http=new FakeHttp();Check(Submit(http,"http://feedback.invalid").Kind==FeedbackResultKind.Unavailable&&http.Calls==0,"plain HTTP rejected before transport");
        foreach(int status in new[]{301,302,303,307,308,500,200}){http=new FakeHttp {Reply=delegate{return Response(status);}};Check(Submit(http).Kind==FeedbackResultKind.Unavailable&&http.Calls==1,"status rejected without following: "+status);}
        http=new FakeHttp {Reply=delegate{return Response(429);}};Check(Submit(http).Kind==FeedbackResultKind.RateLimited,"429 distinguished");
        foreach(string body in new[]{"not JSON","{}","{\"number\":0,\"url\":\""+Issue+"\"}","{\"number\":42,\"url\":\"https://evil.invalid/issues/42\"}","{\"number\":42,\"url\":\""+Issue+"?x=1\"}","{\"number\":42,\"url\":\""+Issue+"/\"}","{\"number\":41,\"url\":\""+Issue+"\"}","{\"number\":42,\"url\":\""+Issue+"\"} garbage"}){
            http=new FakeHttp {Reply=delegate{return Response(201,body);}};Check(Submit(http).Kind==FeedbackResultKind.Unavailable,"malformed or mismatched 201");
        }
        http=new FakeHttp {Reply=delegate{return Response(201,new string(' ',16385),-1);}};Check(Submit(http).Kind==FeedbackResultKind.Unavailable,"streamed response cap");
        string receipt="{\"number\":42,\"url\":\""+Issue+"\"}";
        var bounded=new CountingBody(Encoding.UTF8.GetBytes(receipt+new string(' ',16385-receipt.Length)));
        http=new FakeHttp {Reply=delegate{return new UpdateResponse(201,bounded,-1,null,null);}};
        Check(Submit(http).Kind==FeedbackResultKind.Unavailable&&bounded.BytesRead==16384,"oversized valid JSON prefix rejected without reading past 16 KB");
        http=new FakeHttp {Reply=delegate{return Response(201,receipt+new string(' ',16384-receipt.Length));}};
        Check(Submit(http).Kind==FeedbackResultKind.Success,"known-length response at exactly 16 KB");
        http=new FakeHttp {Reply=delegate{return Response(201,"{}",16385);}};Check(Submit(http).Kind==FeedbackResultKind.Unavailable,"declared response cap");
        http=new FakeHttp {Reply=delegate{return Response(201,"{}",10);}};Check(Submit(http).Kind==FeedbackResultKind.Unavailable,"truncated response");
        http=new FakeHttp {Reply=delegate{throw new WebException("offline");}};Check(Submit(http).Kind==FeedbackResultKind.Unavailable,"network failure");
        http=new FakeHttp {Reply=delegate{throw new WebException("timeout",WebExceptionStatus.Timeout);}};Check(Submit(http).Kind==FeedbackResultKind.Unavailable,"transport timeout");
        http=new FakeHttp {Reply=delegate(CancellationToken token){token.WaitHandle.WaitOne();token.ThrowIfCancellationRequested();return Success();}};
        Check(Submit(http,Relay,35).Kind==FeedbackResultKind.Unavailable,"whole-operation timeout cancels blocked transport");
        http=new FakeHttp();Check(Submit(http,"").Kind==FeedbackResultKind.Browser&&http.Calls==0,"empty relay never calls HTTP");
        bool blocked=false;try{UpdateNetwork.ValidateUri(new Uri(Relay),false);}catch(InvalidDataException){blocked=true;}Check(blocked,"feedback relay is not added to the updater whitelist");
    }
    sealed class CountingBody:MemoryStream {
        internal int BytesRead;
        internal CountingBody(byte[] bytes):base(bytes){}
        public override int Read(byte[] buffer,int offset,int count){int read=base.Read(buffer,offset,count);BytesRead+=read;return read;}
    }
    sealed class BlockingBody:Stream {
        readonly CancellationToken token;internal readonly ManualResetEvent Reading=new ManualResetEvent(false);internal bool Aborted;
        internal BlockingBody(CancellationToken value){token=value;}
        public override int Read(byte[] b,int o,int c){Reading.Set();token.WaitHandle.WaitOne();Aborted=true;token.ThrowIfCancellationRequested();return 0;}
        public override bool CanRead{get{return true;}}public override bool CanSeek{get{return false;}}public override bool CanWrite{get{return false;}}
        public override long Length{get{throw new NotSupportedException();}}public override long Position{get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
        public override void Flush(){}public override long Seek(long x,SeekOrigin y){throw new NotSupportedException();}public override void SetLength(long x){throw new NotSupportedException();}public override void Write(byte[] b,int o,int c){throw new NotSupportedException();}
    }
    static void Cancellation(){
        foreach(bool dispose in new[]{false,true}){
            var entered=new ManualResetEvent(false);var stopped=new ManualResetEvent(false);bool aborted=false;int thread=Thread.CurrentThread.ManagedThreadId;
            var http=new FakeHttp {Reply=delegate(CancellationToken token){Check(Thread.CurrentThread.ManagedThreadId!=thread,"request runs off UI thread");using(token.Register(delegate{aborted=true;})){entered.Set();token.WaitHandle.WaitOne();stopped.Set();token.ThrowIfCancellationRequested();return Success();}}};
            using(var form=Form(http)){
                Show(form);Find<Button>(form,"SubmitFeedback").PerformClick();Pump(delegate{return entered.WaitOne(0);});
                Check(Find<Label>(form,"FeedbackStatus").Text=="正在提交…"&&!Find<TextBox>(form,"Description").Enabled&&!Find<CheckBox>(form,"IncludeDiagnostics").Enabled&&!Find<LinkLabel>(form,"ViewDiagnostics").Enabled,"all inputs disabled during submission");
                Check(Find<Button>(form,"CancelFeedback").Enabled,"cancel stays enabled");
                if(dispose)form.Dispose();else Find<Button>(form,"CancelFeedback").PerformClick();
                Pump(delegate{return stopped.WaitOne(0);});Check(aborted,"closing or disposal aborts pending transport");
            }
            entered.Dispose();stopped.Dispose();
        }
        var cancellation=new CancellationTokenSource();BlockingBody stream=null;var complete=new ManualResetEvent(false);bool cancelled=false;
        var blocked=new FakeHttp {Reply=delegate(CancellationToken token){stream=new BlockingBody(token);return new UpdateResponse(201,stream,-1,null,null);}};
        ThreadPool.QueueUserWorkItem(delegate{try{new FeedbackClient(Relay,blocked,new FeedbackSession()).Submit("ui","12345",null,cancellation.Token);}catch(OperationCanceledException){cancelled=true;}finally{complete.Set();}});
        Pump(delegate{return stream!=null&&stream.Reading.WaitOne(0);});cancellation.Cancel();Pump(delegate{return complete.WaitOne(0);});Check(cancelled&&stream.Aborted,"cancel also interrupts response reading");
        stream.Reading.Dispose();complete.Dispose();cancellation.Dispose();
        stream=null;blocked=new FakeHttp {Reply=delegate(CancellationToken token){stream=new BlockingBody(token);return new UpdateResponse(201,stream,-1,null,null);}};
        Check(Submit(blocked,Relay,35).Kind==FeedbackResultKind.Unavailable&&stream.Aborted,"whole-operation timeout also aborts a blocked body read");stream.Reading.Dispose();
    }
    static void BrowserFallback(){
        string copied=null,opened=null;var http=new FakeHttp();
        using(var form=Form(http,"",delegate(string value){copied=value;},delegate(string value){opened=value;})){
            Show(form);Find<TextBox>(form,"Description").Text=" 中文说明 & #\r\n连接失败 ";Find<ThemedSegments>(form,"Categories").GetOption("Category-battery").DoDefaultAction();
            Find<Button>(form,"SubmitFeedback").PerformClick();Check(http.Calls==0&&copied.StartsWith("类型：电量或 DPI 不对\r\n描述：\r\n中文说明 & #\r\n连接失败"),"empty relay copies category and trimmed description first");
            Check(copied.Contains(Report().FullText)&&Decode(opened).Contains("中文说明 & #\r\n连接失败")&&opened.Length<=6000,"full diagnostics copied and description prefilled");
            Find<CheckBox>(form,"IncludeDiagnostics").Checked=false;Find<Button>(form,"SubmitFeedback").PerformClick();Check(!copied.Contains("diagnostic-mouse")&&!Decode(opened).Contains("diagnostic-mouse"),"unchecking diagnostics also respected by browser workflow");
            Find<TextBox>(form,"Description").Text=new string('中',2000);Find<Button>(form,"SubmitFeedback").PerformClick();Check(opened.Length<=6000&&Decode(opened).Contains("完整内容已复制")&&copied.Contains(new string('中',2000)),"long description bounded only in URL");
            Find<TextBox>(form,"Description").Text=String.Concat(new string('中',1998),"😀");Find<Button>(form,"SubmitFeedback").PerformClick();Check(opened.Length<=6000,"truncation does not break a surrogate pair");
            form.Close();
        }
        http=new FakeHttp {Reply=delegate{return Response(500);}};
        using(var form=Form(http,Relay,delegate(string value){copied=value;},delegate(string value){opened=value;})){
            Show(form);Find<Button>(form,"SubmitFeedback").PerformClick();Pump(delegate{return Find<Label>(form,"FeedbackStatus").Text=="暂时无法提交";});
            var fallback=Find<Button>(form,"BrowserFallback");Check(fallback.Visible&&fallback.Text=="改用浏览器提交"&&Find<TextBox>(form,"Description").Enabled,"failure offers browser and restores inputs");
            fallback.PerformClick();Check(http.Calls==1&&copied.Contains("设备连接后没有显示电量")&&Decode(opened).Contains("设备连接后没有显示电量"),"explicit fallback copies and opens without a second POST");form.Close();
        }
        http=new FakeHttp {Reply=delegate{return Response(429);}};using(var form=Form(http)){
            Show(form);Find<Button>(form,"SubmitFeedback").PerformClick();Pump(delegate{return Find<Label>(form,"FeedbackStatus").Text=="提交太频繁，请稍后再试";});Check(!Find<Button>(form,"BrowserFallback").Visible,"429 uses its specific message");form.Close();
        }
        var devices=new List<WirelessDevice>();for(int i=0;i<64;i++)devices.Add(new WirelessDevice {Name=new string('中',80)+i,Kind="mouse",Provider="fixture",Percent=50});
        var large=new FeedbackReport(new string('版',80),new string('窗',80),devices,new SuppressedDevice[0],new UnadaptedDevice[0],new ReaderHealth[0],new string[0]);
        var browser=large.BrowserSubmission("device",new string('中',2000),true);
        Check(browser.Url.Length<=6000&&Decode(browser.Url).Contains("完整内容已复制")&&browser.Text.Contains(large.FullText),"worst-case report and description keep complete clipboard content and bounded prefill");
    }
    static void SuccessAndRepeat(){
        DateTime now=new DateTime(2026,11,3,0,0,0,DateTimeKind.Utc);var session=new FeedbackSession(delegate{return now;});var http=new FakeHttp();string opened=null;
        using(var form=Form(http,Relay,null,delegate(string url){opened=url;},session)){
            Show(form);Find<CheckBox>(form,"IncludeDiagnostics").Checked=false;Find<Button>(form,"SubmitFeedback").PerformClick();Pump(delegate{return Find<Label>(form,"FeedbackStatus").Text==Submitted;});
            Check(UpdateJson.Read<Dictionary<string,object>>(http.Body,true)["diagnostics"]==null,"unchecked diagnostics are null in the actual POST body");
            Check(opened==null&&!Find<TextBox>(form,"Description").Enabled&&Find<Button>(form,"SubmitFeedback").Text=="关闭","success state shows the number without opening automatically");
            Find<Button>(form,"ViewIssue").PerformClick();Check(opened==Issue,"only the exact validated issue is opened");Check(session.NeedsConfirmation,"successful submission remembered in this process");Find<Button>(form,"SubmitFeedback").PerformClick();Check(form.IsDisposed||!form.Visible,"success submit button closes");
        }
        bool allow=false;int confirmations=0;http=new FakeHttp();using(var form=Form(http,Relay,null,null,session,delegate(string question){confirmations++;Check(question=="刚刚已提交过一次，确定还要再提交吗？","exact repeat question");return allow;})){
            Show(form);Find<Button>(form,"SubmitFeedback").PerformClick();Check(http.Calls==0&&confirmations==1,"declining repeat leaves request unsent");allow=true;
            Find<Button>(form,"SubmitFeedback").PerformClick();Pump(delegate{return Find<Label>(form,"FeedbackStatus").Text==Submitted;});Check(http.Calls==1&&confirmations==2,"confirmed repeat submitted once");form.Close();
        }
        now=now.AddMinutes(10).AddTicks(-1);Check(session.NeedsConfirmation,"just inside ten minutes");now=now.AddTicks(1);Check(!session.NeedsConfirmation,"ten-minute boundary");Check(!new FeedbackSession().NeedsConfirmation,"separate process session starts clean");
        http=new FakeHttp {Reply=delegate{return Response(201,"{\"number\":42,\"url\":\"https://evil.invalid/\"}");}};int opens=0;using(var form=Form(http,Relay,null,delegate{opens++;})){
            Show(form);Find<Button>(form,"SubmitFeedback").PerformClick();Pump(delegate{return Find<Label>(form,"FeedbackStatus").Text=="暂时无法提交";});Check(!Find<Button>(form,"ViewIssue").Visible&&opens==0,"untrusted success URL never exposed as a browser action");form.Close();
        }
    }
    static void SmallWorkAreaStates(){
        var previous=ThemedDialog.WorkingAreaProvider;var area=new Rectangle(0,0,1024,768);
        ThemedDialog.WorkingAreaProvider=delegate{return area;};
        // Keep the original desktop size, plus a smaller area that still needs
        // scrolling after removal of the nested card padding.
        try{foreach(int height in new[]{768,560})foreach(string state in new[]{"browser","failure","success"}){
            area.Height=height;
            var http=new FakeHttp {Reply=delegate{return state=="failure"?Response(500):Success();}};
            using(var form=Form(http,state=="browser"?"":Relay)){
                form.Scale(new SizeF(2,2));form.Font=new Font("Microsoft YaHei UI",24,FontStyle.Regular,GraphicsUnit.Pixel);Show(form);
                if(state!="browser"){
                    Find<Button>(form,"SubmitFeedback").PerformClick();
                    Pump(delegate{return Find<Label>(form,"FeedbackStatus").Text==(state=="failure"?"暂时无法提交":Submitted);});
                }
                Check(area.Contains(form.Bounds),state+": entire feedback window fits (window="+form.Bounds+", client="+form.ClientSize+", work area="+area+")");
                foreach(string name in new[]{"SubmitFeedback","CancelFeedback","BrowserFallback","ViewIssue"}){
                    var button=Find<Button>(form,name);if(!button.Visible)continue;
                    var bounds=button.RectangleToScreen(button.ClientRectangle);
                    for(Control parent=button.Parent;parent!=null;parent=parent.Parent)Check(parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds),state+": "+name+" fully visible through ancestors");
                }
                var input=Find<TextBox>(form,"Description");Check(input.Height>=3*input.Font.Height,state+": description keeps three lines");
                var viewport=Find<Panel>(form,"DialogContentViewport");if(height==560)Check(viewport.AutoScroll&&viewport.VerticalScroll.Visible,state+": constrained content can scroll");
                viewport.AutoScrollPosition=new Point(0,Int32.MaxValue);Application.DoEvents();
                var status=Find<Label>(form,"FeedbackStatus");Check(status.RectangleToScreen(status.ClientRectangle).Bottom<=viewport.RectangleToScreen(viewport.ClientRectangle).Bottom,state+": final status reachable");
                form.Close();
            }
        }}finally{ThemedDialog.WorkingAreaProvider=previous;}
    }
    static void NativeBoundsAfterSubmission(){
        var previous=ThemedDialog.WorkingAreaProvider;var area=new Rectangle(0,24,1024,560);
        ThemedDialog.WorkingAreaProvider=delegate{return area;};
        try{foreach(string state in new[]{"failure","success"})using(var form=Form(new FakeHttp {Reply=delegate{return state=="failure"?Response(500):Success();}})){
            form.Scale(new SizeF(2,2));form.Font=new Font("Microsoft YaHei UI",24,FontStyle.Regular,GraphicsUnit.Pixel);Show(form);
            var initial=form.Bounds;Check(area.Contains(initial),state+": initial fit");
            // A native move/resize can arrive after OnLoad's initial fit. Keep the
            // same work area: correcting only monitor changes misses this path.
            Check(SetWindowPos(form.Handle,IntPtr.Zero,initial.Left,initial.Top,initial.Width,initial.Height+32,0x0004|0x0010),"native resize delivered");
            Check(area.Contains(form.Bounds),state+": late native size remains inside work area ("+form.Bounds+")");
            Check(SetWindowPos(form.Handle,IntPtr.Zero,initial.Left+17,initial.Top+23,form.Width,form.Height,0x0004|0x0010),"native move delivered");
            Check(area.Contains(form.Bounds),state+": late same-monitor move remains inside work area ("+form.Bounds+")");
            Find<Button>(form,"SubmitFeedback").PerformClick();
            Pump(delegate{return Find<Label>(form,"FeedbackStatus").Text==(state=="failure"?"暂时无法提交":Submitted);});
            Check(area.Contains(form.Bounds),state+": result-state layout retains the native bounds constraint");
            Check(SetWindowPos(form.Handle,IntPtr.Zero,area.Left-11,area.Top-13,form.Width+40,form.Height+32,0x0004|0x0010),"result-state native bounds delivered");
            Check(area.Contains(form.Bounds),state+": result-state native move and size remain inside work area ("+form.Bounds+")");
            // Legitimate resizing inside the work area must not restore the
            // original preferred size (the diagnostic viewer is resizable).
            var smaller=new Size(form.Width-40,form.Height-40);form.Size=smaller;Application.DoEvents();
            Check(form.Size==smaller&&area.Contains(form.Bounds),state+": valid smaller size is preserved");
            SendMessage(form.Handle,0x0231,IntPtr.Zero,IntPtr.Zero);
            Check(SetWindowPos(form.Handle,IntPtr.Zero,area.Right-20,area.Top+20,form.Width,form.Height,0x0004|0x0010),"interactive move delivered");
            Check(form.Left==area.Right-20,state+": interactive drag can cross a monitor boundary");
            SendMessage(form.Handle,0x0232,IntPtr.Zero,IntPtr.Zero);
            Check(area.Contains(form.Bounds),state+": completed interactive move fits the destination work area");
            form.Close();
        }}finally{ThemedDialog.WorkingAreaProvider=previous;}
    }
    static void PreviewAndLayout(string output){
        var previous=ThemedDialog.WorkingAreaProvider;
        // Request a roomy layout, but Windows may still clamp the actual window.
        ThemedDialog.WorkingAreaProvider=delegate{return new Rectangle(0,0,3200,2400);};
        try{
        using(var form=Form(new FakeHttp(),"")){
            Show(form);Exception failure=null;bool previewed=false;using(var timer=new System.Windows.Forms.Timer {Interval=10}){
                timer.Tick+=delegate{foreach(Form window in Application.OpenForms)if(window.Text=="诊断信息"){timer.Stop();try{var text=Find<TextBox>(window,"DiagnosticText");Check(text.ReadOnly&&text.Multiline&&text.Text==Report().FullText&&!window.ShowInTaskbar,"preview shows exactly the full diagnostics in a read-only modal");previewed=true;}catch(Exception e){failure=e;}window.Close();break;}};
                timer.Start();var link=Find<LinkLabel>(form,"ViewDiagnostics");typeof(LinkLabel).GetMethod("OnLinkClicked",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(link,new object[]{new LinkLabelLinkClickedEventArgs(link.Links[0])});
            }
            if(failure!=null)throw failure;Check(previewed&&!form.IsDisposed,"preview returns to the feedback form");form.Close();
        }
        foreach(bool dark in new[]{false,true})foreach(float scale in new[]{1f,1.25f,1.5f,1.75f,2f})using(var form=new FeedbackForm(Report(),false,dark,delegate{},delegate{},Relay,new FakeHttp(),new FeedbackSession())){
            form.Scale(new SizeF(scale,scale));form.Font=new Font("Microsoft YaHei UI",9*scale);Show(form);var submit=Find<Button>(form,"SubmitFeedback");var input=Find<TextBox>(form,"Description");
            var viewport=Find<Panel>(form,"DialogContentViewport");
            Check(submit.Visible&&form.RectangleToScreen(form.ClientRectangle).Contains(submit.RectangleToScreen(submit.ClientRectangle)),"primary button fits actual client at scale "+scale);
            Check(input.Height>=100||viewport.AutoScroll&&viewport.VerticalScroll.Visible&&input.Height>=3*input.Font.Height,"description stays roomy or scrolls at its readable minimum at scale "+scale);
            var note=Find<Label>(form,"FeedbackNotice");Check(note.GetPreferredSize(new Size(note.Width,0)).Height<=note.Height,"full public/privacy notice fits at scale "+scale);
            if(output!=null&&scale==1f)using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(0,0,image.Width,image.Height));image.Save(Path.Combine(output,dark?"feedback-dark.png":"feedback-light.png"));}
            form.Close();
        }
        }finally{ThemedDialog.WorkingAreaProvider=previous;}
    }
    [STAThread] static int Main(string[] args){
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);NativeDisplay.EnableDpiAwareness();Application.EnableVisualStyles();
        if(args.Length==1&&args[0]=="--fit-only"){
            Run("small work area feedback states",SmallWorkAreaStates);
            Run("native bounds after submission",NativeBoundsAfterSubmission);
            Console.WriteLine(checks+" feedback fit checks, "+failures+" failed groups");return failures==0?0:1;
        }
        Run("shipped relay configuration",RelayConfiguration);Run("validation and defaults",Validation);Run("exact payload and HTTP policy",PayloadAndPolicy);Run("HTTP failure boundaries",Failures);Run("cancellation during send and read",Cancellation);Run("browser fallback",BrowserFallback);Run("success and repeat confirmation",SuccessAndRepeat);Run("diagnostic preview and DPI bounds",delegate{PreviewAndLayout(args.Length>0?args[0]:null);});
        Run("small work area feedback states",SmallWorkAreaStates);
        Run("native bounds after submission",NativeBoundsAfterSubmission);
        Console.WriteLine(checks+" feedback checks, "+failures+" failed groups; no network or real clipboard/browser/configuration");return failures==0?0:1;
    }
}
