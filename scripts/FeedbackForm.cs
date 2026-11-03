using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;

namespace AtkBattery {
    internal static class FeedbackRelay {
        internal const string Url="";
        internal static void ValidateUri(Uri uri){
            if(uri==null||!uri.IsAbsoluteUri||uri.Scheme!=Uri.UriSchemeHttps||uri.UserInfo.Length!=0||uri.Query.Length!=0||uri.Fragment.Length!=0)throw new ArgumentException("Invalid feedback relay URL.");
        }
        internal static Uri Endpoint(string address){var uri=new Uri(address.TrimEnd('/')+"/feedback",UriKind.Absolute);ValidateUri(uri);return uri;}
    }
    internal static class FeedbackInput {
        static readonly string[] categories={"device","battery","ui","other"};
        static readonly string[] labels={"设备不识别","电量或 DPI 不对","界面或操作","其他"};
        internal static string CategoryAt(int index){return categories[index];}
        internal static string CategoryLabel(string category){int index=Array.IndexOf(categories,category);if(index<0)throw new ArgumentException("Invalid feedback category.");return labels[index];}
        internal static string DefaultCategory(bool deviceMissing){return deviceMissing?"device":"other";}
        internal static bool Valid(string category,string description){int length=(description??"").Trim().Length;return Array.IndexOf(categories,category)>=0&&length>=5&&length<=2000;}
        internal static string Prefix(string text,int length){if(length>0&&length<text.Length&&Char.IsHighSurrogate(text[length-1]))length--;return text.Substring(0,length);}
    }
    [DataContract] internal sealed class FeedbackPayload {
        [DataMember(Name="schema")] public int Schema=1;
        [DataMember(Name="category")] public string Category;
        [DataMember(Name="description")] public string Description;
        [DataMember(Name="diagnostics")] public string Diagnostics;
        [DataMember(Name="appVersion")] public string AppVersion;
        internal static byte[] Create(string category,string description,string diagnostics){
            if(!FeedbackInput.Valid(category,description))throw new ArgumentException("Invalid feedback input.");
            const string marker="[已截断]";
            if(diagnostics!=null&&diagnostics.Length>20000)diagnostics=FeedbackInput.Prefix(diagnostics,20000-marker.Length)+marker;
            var payload=new FeedbackPayload {Category=category,Description=description.Trim(),Diagnostics=diagnostics,AppVersion=AppRuntime.Version};
            using(var stream=new MemoryStream()){new DataContractJsonSerializer(typeof(FeedbackPayload)).WriteObject(stream,payload);return stream.ToArray();}
        }
    }
    internal interface IFeedbackTransport {UpdateResponse Post(Uri uri,byte[] body,CancellationToken token);}
    internal sealed class FeedbackHttp:IFeedbackTransport {
        internal static HttpWebRequest CreateRequest(Uri uri){
            FeedbackRelay.ValidateUri(uri);
            ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
            ServicePointManager.CheckCertificateRevocationList=true;
            var request=(HttpWebRequest)WebRequest.Create(uri);
            request.Method="POST";request.ContentType="application/json";request.UserAgent="MyFinderDeviceBattery/"+AppRuntime.Version;
            request.AllowAutoRedirect=false;request.Timeout=20000;request.ReadWriteTimeout=20000;
            request.UseDefaultCredentials=false;request.Credentials=null;request.PreAuthenticate=false;request.AutomaticDecompression=DecompressionMethods.None;
            request.ServicePoint.Expect100Continue=false;return request;
        }
        public UpdateResponse Post(Uri uri,byte[] body,CancellationToken token){
            var request=CreateRequest(uri);request.ContentLength=body.Length;
            // Keep cancellation registered through response reads, not just through GetResponse.
            var registration=token.Register(request.Abort);
            try{
                token.ThrowIfCancellationRequested();using(var stream=request.GetRequestStream()){stream.Write(body,0,body.Length);}
                token.ThrowIfCancellationRequested();HttpWebResponse response;
                try{response=(HttpWebResponse)request.GetResponse();}catch(WebException error){response=error.Response as HttpWebResponse;if(response==null)throw;}
                return new UpdateResponse((int)response.StatusCode,response.GetResponseStream(),response.ContentLength,null,null,delegate{response.Dispose();registration.Dispose();});
            }catch{registration.Dispose();throw;}
        }
    }
    internal sealed class FeedbackSession {
        internal static readonly FeedbackSession Current=new FeedbackSession();
        readonly object gate=new object();readonly Func<DateTime> clock;DateTime? lastSuccess;
        internal FeedbackSession(Func<DateTime> now=null){clock=now??delegate{return DateTime.UtcNow;};}
        internal bool NeedsConfirmation {get{lock(gate)return lastSuccess.HasValue&&clock()<lastSuccess.Value.AddMinutes(10);}}
        internal void MarkSuccess(){lock(gate)lastSuccess=clock();}
    }
    internal enum FeedbackResultKind {Success,RateLimited,Unavailable,Browser}
    internal sealed class FeedbackResult {
        internal readonly FeedbackResultKind Kind;internal readonly long Number;internal readonly string Url;
        internal FeedbackResult(FeedbackResultKind kind,long number=0,string url=null){Kind=kind;Number=number;Url=url;}
    }
    [DataContract] internal sealed class FeedbackReceipt {
        [DataMember(Name="number",IsRequired=true)] public long Number {get;set;}
        [DataMember(Name="url",IsRequired=true)] public string Url {get;set;}
    }
    internal sealed class FeedbackClient {
        readonly string relay;readonly IFeedbackTransport http;readonly FeedbackSession session;readonly int timeoutMilliseconds;
        internal FeedbackClient(string address,IFeedbackTransport transport,FeedbackSession history,int timeout=20000){relay=address;http=transport;session=history;timeoutMilliseconds=timeout;}
        internal static bool IsIssueUrl(long number,string url){return number>0&&String.Equals(url,"https://github.com/wushuai0802/myfinder-device-battery/issues/"+number.ToString(CultureInfo.InvariantCulture),StringComparison.Ordinal);}
        internal FeedbackResult Submit(string category,string description,string diagnostics,CancellationToken token){
            token.ThrowIfCancellationRequested();
            if(!FeedbackInput.Valid(category,description))return new FeedbackResult(FeedbackResultKind.Unavailable);
            if(String.IsNullOrEmpty(relay))return new FeedbackResult(FeedbackResultKind.Browser);
            try{
                var endpoint=FeedbackRelay.Endpoint(relay);byte[] body=FeedbackPayload.Create(category,description,diagnostics);
                using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token)){
                    timeout.CancelAfter(timeoutMilliseconds);var cancel=timeout.Token;
                    using(var response=http.Post(endpoint,body,cancel)){
                        cancel.ThrowIfCancellationRequested();
                        if(response.Status==429)return new FeedbackResult(FeedbackResultKind.RateLimited);
                        if(response.Status!=201||response.Length>16384||response.Body==null)return new FeedbackResult(FeedbackResultKind.Unavailable);
                        using(var output=new MemoryStream()){
                            byte[] buffer=new byte[4096];int total=0;
                            while(true){
                                // An unknown-length body that fills the budget fails closed;
                                // do not probe beyond 16 KB even to detect a larger response.
                                if(total==16384){if(response.Length!=16384)throw new InvalidDataException("Feedback response is too large.");break;}
                                cancel.ThrowIfCancellationRequested();int read=response.Body.Read(buffer,0,Math.Min(buffer.Length,16384-total));cancel.ThrowIfCancellationRequested();
                                if(read==0)break;total+=read;output.Write(buffer,0,read);
                            }
                            if(response.Length>=0&&response.Length!=total)throw new InvalidDataException("Truncated feedback response.");
                            var receipt=UpdateJson.Read<FeedbackReceipt>(output.ToArray());
                            if(receipt==null||!IsIssueUrl(receipt.Number,receipt.Url))return new FeedbackResult(FeedbackResultKind.Unavailable);
                            cancel.ThrowIfCancellationRequested();session.MarkSuccess();return new FeedbackResult(FeedbackResultKind.Success,receipt.Number,receipt.Url);
                        }
                    }
                }
            }catch(Exception){token.ThrowIfCancellationRequested();return new FeedbackResult(FeedbackResultKind.Unavailable);}
        }
    }
    internal sealed class FeedbackBrowserSubmission {
        internal readonly string Text,Url;
        internal FeedbackBrowserSubmission(string text,string url){Text=text;Url=url;}
        internal string Open(Action<string> copy,Action<string> open){
            const string review="提交前可检查内容；提交需要 GitHub 账号。";bool copied=false;
            try{copy(Text);copied=true;}catch(Exception){}
            try{open(Url);return (copied?"完整报告已复制。":"剪贴板不可用，请检查浏览器预填内容。")+review;}
            catch(Exception){return (copied?"无法打开浏览器，完整报告已复制。":"无法打开浏览器或复制报告，请稍后重试。")+review;}
        }
    }
}

namespace AtkBatteryTray {
    internal sealed class FeedbackForm:Form {
        readonly FeedbackReport report;readonly FeedbackClient client;readonly FeedbackSession session;readonly string relay;
        readonly Action<string> copy,open;readonly Func<string,bool> confirm;
        readonly FlowLayoutPanel categories=new FlowLayoutPanel();
        readonly TextBox description=new TextBox();readonly CheckBox diagnostics=new CheckBox();readonly LinkLabel preview=new LinkLabel();
        readonly Label remaining=new Label(),status=new Label();
        readonly Button submit=new Button(),cancel=new Button(),fallback=new Button(),view=new Button();
        CancellationTokenSource pending;FeedbackResult receipt;bool busy;
        internal string SelectedCategory {get{foreach(RadioButton radio in categories.Controls)if(radio.Checked)return (string)radio.Tag;return "other";}}
        internal FeedbackForm(FeedbackReport snapshot,bool deviceMissing,bool dark,Action<string> copyText=null,Action<string> openLink=null,string address=null,IFeedbackTransport transport=null,FeedbackSession history=null,Func<string,bool> confirmation=null){
            report=snapshot;relay=address??FeedbackRelay.Url;session=history??FeedbackSession.Current;
            copy=copyText??Clipboard.SetText;open=openLink??delegate(string url){Process.Start(new ProcessStartInfo(url) {UseShellExecute=true});};
            confirm=confirmation??delegate(string question){return MessageBox.Show(this,question,"反馈问题",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)==DialogResult.Yes;};
            client=new FeedbackClient(relay,transport??new FeedbackHttp(),session);
            Text="反馈问题";ShowInTaskbar=false;StartPosition=FormStartPosition.CenterParent;FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
            AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;Font=new Font("Microsoft YaHei UI",9);
            BackColor=dark?Color.FromArgb(41,45,52):Color.FromArgb(250,251,253);ForeColor=PopupPages.Foreground(dark);ClientSize=new Size(540,460);
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(16),ColumnCount=1,RowCount=10};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            for(int i=0;i<10;i++)layout.RowStyles.Add(new RowStyle(i==4?SizeType.Percent:SizeType.AutoSize,i==4?100:0));
            layout.Controls.Add(Caption("类型"),0,0);
            categories.AutoSize=true;categories.Dock=DockStyle.Fill;categories.WrapContents=true;categories.Margin=new Padding(0,2,0,10);
            for(int i=0;i<4;i++){
                string category=FeedbackInput.CategoryAt(i);var radio=new RadioButton {Name="Category-"+category,Text=FeedbackInput.CategoryLabel(category),Tag=category,AutoSize=true,Margin=new Padding(0,2,14,2)};
                categories.Controls.Add(radio);radio.Checked=category==FeedbackInput.DefaultCategory(deviceMissing);
            }
            layout.Controls.Add(categories,0,1);layout.Controls.Add(Caption("描述（必填，5–2000 字）"),0,2);
            var hint=Caption("发生了什么？用的是什么设备、怎么连接的？");hint.ForeColor=PopupPages.Muted(dark);layout.Controls.Add(hint,0,3);
            description.Name="Description";description.AccessibleName="描述";description.Multiline=true;description.AcceptsReturn=true;description.ScrollBars=ScrollBars.Vertical;description.Dock=DockStyle.Fill;
            description.BackColor=PopupPages.Track(dark);description.ForeColor=ForeColor;description.BorderStyle=BorderStyle.FixedSingle;description.Margin=new Padding(0,5,0,4);description.TextChanged+=delegate{UpdateValidation();};layout.Controls.Add(description,0,4);
            remaining.Name="FeedbackRemaining";remaining.AutoSize=true;remaining.Margin=new Padding(0,0,0,10);layout.Controls.Add(remaining,0,5);
            var diagnosticRow=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill,Margin=new Padding(0,0,0,10),WrapContents=true};
            diagnostics.Name="IncludeDiagnostics";diagnostics.Text="附带诊断信息（推荐）";diagnostics.AutoSize=true;diagnostics.Checked=true;diagnostics.Margin=new Padding(0,3,12,3);
            preview.Name="ViewDiagnostics";preview.Text="查看内容";preview.AutoSize=true;preview.LinkColor=PopupPages.Blue(dark);preview.ActiveLinkColor=preview.LinkColor;preview.Margin=new Padding(0,4,0,3);preview.LinkClicked+=delegate{ShowDiagnostics();};
            diagnosticRow.Controls.Add(diagnostics);diagnosticRow.Controls.Add(preview);layout.Controls.Add(diagnosticRow,0,6);
            var notice=Caption(String.IsNullOrEmpty(relay)?"将打开 GitHub 提交，需要 GitHub 账号":"提交后会公开显示在 GitHub 项目的问题列表里。诊断不含序列号和文件路径，请不要在描述里填写个人信息。");
            notice.Name="FeedbackNotice";notice.Margin=new Padding(0,0,0,10);notice.Dock=DockStyle.Fill;layout.Controls.Add(notice,0,7);
            status.Name="FeedbackStatus";status.AutoSize=true;status.Dock=DockStyle.Fill;status.Margin=new Padding(0,0,0,10);layout.Controls.Add(status,0,8);
            var buttons=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=true,Margin=Padding.Empty};
            submit.Name="SubmitFeedback";submit.Text=String.IsNullOrEmpty(relay)?"在浏览器中提交":"提交";submit.AutoSize=true;submit.MinimumSize=new Size(82,30);submit.Click+=delegate{Submit();};
            cancel.Name="CancelFeedback";cancel.Text="取消";cancel.AutoSize=true;cancel.MinimumSize=new Size(82,30);cancel.Click+=delegate{Close();};
            fallback.Name="BrowserFallback";fallback.Text="改用浏览器提交";fallback.AutoSize=true;fallback.MinimumSize=new Size(132,30);fallback.Visible=false;fallback.Click+=delegate{if(ConfirmRepeat())OpenBrowser();};
            view.Name="ViewIssue";view.Text="在浏览器中查看";view.AutoSize=true;view.MinimumSize=new Size(132,30);view.Visible=false;
            view.Click+=delegate{if(receipt!=null&&FeedbackClient.IsIssueUrl(receipt.Number,receipt.Url))try{open(receipt.Url);}catch(Exception){status.Text="无法打开浏览器，请稍后重试。";}};
            buttons.Controls.AddRange(new Control[]{submit,cancel,view,fallback});layout.Controls.Add(buttons,0,9);Controls.Add(layout);AcceptButton=submit;CancelButton=cancel;
            FormClosing+=delegate{AbortSubmission();};UpdateValidation();
        }
        static Label Caption(string text){return new Label {Text=text,AutoSize=true,Margin=new Padding(0,0,0,3)};}
        void UpdateValidation(){
            int length=description.Text.Trim().Length;
            remaining.Text=length<5?"还需输入 "+(5-length)+" 字；还可输入 "+(2000-length)+" 字":length>2000?"已超出 "+(length-2000)+" 字（最多 2000 字）":"还可输入 "+(2000-length)+" 字";
            submit.Enabled=!busy&&(receipt!=null||FeedbackInput.Valid(SelectedCategory,description.Text));
            fallback.Enabled=!busy&&FeedbackInput.Valid(SelectedCategory,description.Text);
        }
        bool ConfirmRepeat(){return !session.NeedsConfirmation||confirm("刚刚已提交过一次，确定还要再提交吗？");}
        void EnableInputs(bool enabled){categories.Enabled=enabled;description.Enabled=enabled;diagnostics.Enabled=enabled;preview.Enabled=enabled;}
        void Submit(){
            if(receipt!=null){Close();return;}
            if(busy||!FeedbackInput.Valid(SelectedCategory,description.Text)||!ConfirmRepeat())return;
            if(String.IsNullOrEmpty(relay)){OpenBrowser();return;}
            string category=SelectedCategory,text=description.Text,diagnosticText=diagnostics.Checked?report.FullText:null;
            busy=true;EnableInputs(false);fallback.Visible=false;status.Text="正在提交…";UpdateValidation();
            var cancellation=new CancellationTokenSource();pending=cancellation;var token=cancellation.Token;
            ThreadPool.QueueUserWorkItem(delegate{
                try{
                    var result=client.Submit(category,text,diagnosticText,token);
                    if(!token.IsCancellationRequested)try{BeginInvoke((MethodInvoker)delegate{if(!IsDisposed&&!Disposing&&!token.IsCancellationRequested)Complete(result);});}catch(InvalidOperationException){}
                }catch(OperationCanceledException){}finally{cancellation.Dispose();}
            });
        }
        void Complete(FeedbackResult result){
            pending=null;busy=false;
            if(result.Kind==FeedbackResultKind.Success){receipt=result;status.Text="已提交，编号 #"+result.Number.ToString(CultureInfo.InvariantCulture);submit.Text="关闭";view.Visible=true;cancel.Visible=false;CancelButton=submit;}
            else{EnableInputs(true);status.Text=result.Kind==FeedbackResultKind.RateLimited?"提交太频繁，请稍后再试":"暂时无法提交";fallback.Visible=result.Kind!=FeedbackResultKind.RateLimited;}
            UpdateValidation();
        }
        void OpenBrowser(){if(busy||!FeedbackInput.Valid(SelectedCategory,description.Text))return;status.Text=report.BrowserSubmission(SelectedCategory,description.Text,diagnostics.Checked).Open(copy,open);}
        void ShowDiagnostics(){
            using(var dialog=new Form {Text="诊断信息",ShowInTaskbar=false,StartPosition=FormStartPosition.CenterParent,AutoScaleDimensions=new SizeF(96,96),AutoScaleMode=AutoScaleMode.Dpi,Font=Font,ClientSize=new Size(620,420),BackColor=BackColor,ForeColor=ForeColor,MinimizeBox=false}){
                var text=new TextBox {Name="DiagnosticText",Text=report.FullText,ReadOnly=true,Multiline=true,WordWrap=false,ScrollBars=ScrollBars.Both,Dock=DockStyle.Fill,BackColor=description.BackColor,ForeColor=ForeColor};
                var close=new Button {Text="关闭",DialogResult=DialogResult.Cancel,Dock=DockStyle.Bottom,Height=32};dialog.Controls.Add(text);dialog.Controls.Add(close);dialog.CancelButton=close;dialog.ShowDialog(this);
            }
        }
        void AbortSubmission(){var cancellation=pending;if(cancellation!=null)try{cancellation.Cancel();}catch(ObjectDisposedException){}}
        protected override void Dispose(bool disposing){if(disposing)AbortSubmission();base.Dispose(disposing);}
    }
}
