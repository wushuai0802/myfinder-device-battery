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
        internal const string Url="https://myfinder-feedback-relay.wushuai0802.deno.net";
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
    internal sealed class FeedbackForm:ThemedDialog {
        readonly FeedbackReport report;readonly FeedbackClient client;readonly FeedbackSession session;readonly string relay;
        readonly Action<string> copy,open;readonly Func<string,bool> confirm;
        readonly ThemedSegments categories;
        readonly TextBox description=new TextBox();readonly CheckBox diagnostics;readonly LinkLabel preview=new LinkLabel();
        readonly Label remaining=new Label(),status=new Label();
        readonly ThemedButton submit,cancel,fallback,view;
        CancellationTokenSource pending;FeedbackResult receipt;bool busy;
        internal string SelectedCategory {get{return categories.SelectedValue??"other";}}
        internal FeedbackForm(FeedbackReport snapshot,bool deviceMissing,bool dark,Action<string> copyText=null,Action<string> openLink=null,string address=null,IFeedbackTransport transport=null,FeedbackSession history=null,Func<string,bool> confirmation=null):base(dark){
            report=snapshot;relay=address??FeedbackRelay.Url;session=history??FeedbackSession.Current;
            copy=copyText??Clipboard.SetText;open=openLink??delegate(string url){Process.Start(new ProcessStartInfo(url) {UseShellExecute=true});};
            confirm=confirmation??delegate(string question){return ThemedMessage.Show(this,"反馈问题",question,MessageBoxButtons.YesNo,MessageBoxDefaultButton.Button2)==DialogResult.Yes;};
            client=new FeedbackClient(relay,transport??new FeedbackHttp(),session);
            Text="反馈问题";ShowInTaskbar=false;StartPosition=FormStartPosition.CenterParent;FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
            ClientSize=new Size(540,568);categories=new ThemedSegments(Theme) {Name="Categories",AccessibleName="类型"};diagnostics=new ThemedSwitch(Theme);
            submit=new ThemedButton(Theme,ThemedButtonKind.Primary);cancel=new ThemedButton(Theme);fallback=new ThemedButton(Theme);view=new ThemedButton(Theme,ThemedButtonKind.Text);
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,Margin=Padding.Empty};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            for(int i=0;i<5;i++)layout.RowStyles.Add(new RowStyle(i==1?SizeType.Percent:SizeType.AutoSize,i==1?100:0));
            var typeCard=new ThemedSection(Theme) {Name="FeedbackTypeCard",Dock=DockStyle.Fill,Height=54};
            var typeTitle=Caption("类型",false,false,true);typeTitle.Dock=DockStyle.Top;
            categories.Dock=DockStyle.Bottom;categories.Margin=Padding.Empty;
            for(int i=0;i<4;i++){
                string category=FeedbackInput.CategoryAt(i);categories.AddOption("Category-"+category,FeedbackInput.CategoryLabel(category),category);
                if(category==FeedbackInput.DefaultCategory(deviceMissing))categories.SelectedIndex=i;
            }
            categories.SizeChanged+=delegate{typeCard.Height=typeCard.Padding.Vertical+typeTitle.PreferredHeight+4+categories.Height;};
            typeCard.Controls.Add(categories);typeCard.Controls.Add(typeTitle);layout.Controls.Add(typeCard,0,0);
            var descriptionCard=new ThemedSection(Theme) {Name="FeedbackDescriptionCard",Dock=DockStyle.Fill};
            var descriptionLayout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Margin=Padding.Empty};descriptionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            for(int i=0;i<4;i++)descriptionLayout.RowStyles.Add(new RowStyle(i==2?SizeType.Percent:SizeType.AutoSize,i==2?100:0));
            descriptionLayout.Controls.Add(Caption("描述（必填，5–2000 字）",false,false,true),0,0);
            var hint=Caption("发生了什么？用的是什么设备、怎么连接的？",false,true);descriptionLayout.Controls.Add(hint,0,1);
            description.Name="Description";description.AccessibleName="描述";description.Multiline=true;description.AcceptsReturn=true;description.Dock=DockStyle.Fill;
            description.TextChanged+=delegate{UpdateValidation();};descriptionLayout.Controls.Add(new ThemedTextBox(Theme,description) {Dock=DockStyle.Fill,Margin=new Padding(0,5,0,4)},0,2);
            remaining.Name="FeedbackRemaining";remaining.AutoSize=true;remaining.Font=hint.Font;remaining.BackColor=Theme.Card;remaining.ForeColor=Theme.Muted;remaining.Margin=Padding.Empty;descriptionLayout.Controls.Add(remaining,0,3);
            descriptionCard.Controls.Add(descriptionLayout);layout.Controls.Add(descriptionCard,0,1);
            var diagnosticCard=new ThemedSection(Theme) {Name="FeedbackDiagnosticsCard",Dock=DockStyle.Fill,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink};
            var diagnosticRow=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Top,Margin=Padding.Empty,WrapContents=true};
            diagnostics.Name="IncludeDiagnostics";diagnostics.Text="附带诊断信息（推荐）";diagnostics.AccessibleName=diagnostics.Text;diagnostics.Checked=true;diagnostics.Margin=new Padding(0,0,12,0);
            preview.Name="ViewDiagnostics";preview.Text="查看内容";preview.AutoSize=true;preview.BackColor=Theme.Card;preview.LinkColor=Theme.Accent;preview.ActiveLinkColor=preview.LinkColor;preview.VisitedLinkColor=preview.LinkColor;preview.DisabledLinkColor=Theme.Muted;preview.Margin=new Padding(0,4,0,3);preview.LinkClicked+=delegate{ShowDiagnostics();};
            diagnosticRow.Controls.Add(diagnostics);diagnosticRow.Controls.Add(preview);diagnosticCard.Controls.Add(diagnosticRow);layout.Controls.Add(diagnosticCard,0,2);
            var noticeCard=new ThemedSection(Theme) {Name="FeedbackNoticeCard",Dock=DockStyle.Fill,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink};
            var noticeLayout=new TableLayoutPanel {AutoSize=true,Dock=DockStyle.Top,ColumnCount=1,RowCount=2,Margin=Padding.Empty};noticeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            noticeLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));noticeLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var notice=Caption(String.IsNullOrEmpty(relay)?"将打开 GitHub 提交，需要 GitHub 账号":"提交后会公开显示在 GitHub 项目的问题列表里。诊断不含序列号和文件路径，请不要在描述里填写个人信息。",false,true);
            notice.Name="FeedbackNotice";notice.Margin=new Padding(0,0,0,4);notice.Dock=DockStyle.Fill;noticeLayout.Controls.Add(notice,0,0);
            status.Name="FeedbackStatus";status.AutoSize=true;status.Dock=DockStyle.Fill;status.Font=hint.Font;status.BackColor=Theme.Card;status.ForeColor=Theme.Muted;status.Margin=Padding.Empty;noticeLayout.Controls.Add(status,0,1);
            noticeCard.Controls.Add(noticeLayout);layout.Controls.Add(noticeCard,0,3);
            var buttons=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=true,Margin=Padding.Empty};
            submit.Name="SubmitFeedback";submit.Text=String.IsNullOrEmpty(relay)?"在浏览器中提交":"提交";submit.AutoSize=true;submit.MinimumSize=new Size(82,30);submit.Click+=delegate{Submit();};
            cancel.Name="CancelFeedback";cancel.Text="取消";cancel.AutoSize=true;cancel.MinimumSize=new Size(82,30);cancel.Click+=delegate{Close();};
            fallback.Name="BrowserFallback";fallback.Text="改用浏览器提交";fallback.AutoSize=true;fallback.MinimumSize=new Size(132,30);fallback.Visible=false;fallback.Click+=delegate{if(ConfirmRepeat())OpenBrowser();};
            view.Name="ViewIssue";view.Text="在浏览器中查看";view.AutoSize=true;view.MinimumSize=new Size(132,30);view.Visible=false;
            view.Click+=delegate{if(receipt!=null&&FeedbackClient.IsIssueUrl(receipt.Number,receipt.Url))try{open(receipt.Url);}catch(Exception){status.Text="无法打开浏览器，请稍后重试。";}};
            buttons.Controls.AddRange(new Control[]{submit,cancel,view,fallback});layout.Controls.Add(buttons,0,4);Controls.Add(layout);AcceptButton=submit;CancelButton=cancel;
            ConfigureLayout(layout,buttons,delegate(int width){
                int textWidth=Math.Max(1,width-descriptionCard.Padding.Horizontal);
                int minimum=descriptionCard.Padding.Vertical;
                foreach(Control child in descriptionLayout.Controls){
                    minimum+=child.Margin.Vertical;
                    minimum+=child is ThemedTextBox?3*description.Font.Height+child.Padding.Vertical+4:child.GetPreferredSize(new Size(textWidth,0)).Height;
                }
                return typeCard.Height+typeCard.Margin.Vertical+minimum+descriptionCard.Margin.Vertical
                    +diagnosticCard.GetPreferredSize(new Size(width,0)).Height+diagnosticCard.Margin.Vertical
                    +noticeCard.GetPreferredSize(new Size(width,0)).Height+noticeCard.Margin.Vertical;
            });
            FormClosing+=delegate{AbortSubmission();};UpdateValidation();
        }
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
        internal ThemedDialog CreateDiagnosticsDialog(){
            var dialog=new ThemedDialog(Theme.Dark) {Text="诊断信息",ClientSize=new Size(620,420),FormBorderStyle=FormBorderStyle.Sizable};
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var text=new TextBox {Name="DiagnosticText",AccessibleName="诊断信息",Text=report.FullText,ReadOnly=true,Multiline=true,WordWrap=false,HideSelection=true,SelectionLength=0,TabStop=true};
            new DialogCaret(text);
            var card=new ThemedTextBox(dialog.Theme,text) {Name="DiagnosticsCard",Dock=DockStyle.Fill};layout.Controls.Add(card,0,0);
            var close=new ThemedButton(dialog.Theme,ThemedButtonKind.Primary) {Text="关闭",DialogResult=DialogResult.Cancel,AutoSize=true,Anchor=AnchorStyles.Right,Margin=Padding.Empty};layout.Controls.Add(close,0,1);
            dialog.Controls.Add(layout);dialog.AcceptButton=close;dialog.CancelButton=close;dialog.ActiveControl=close;
            dialog.ConfigureLayout(layout,close,delegate(int width){return card.Padding.Vertical+card.Margin.Vertical+5*text.Font.Height+SystemInformation.HorizontalScrollBarHeight+4;});return dialog;
        }
        void ShowDiagnostics(){using(var dialog=CreateDiagnosticsDialog())dialog.ShowDialog(this);}
        void AbortSubmission(){var cancellation=pending;if(cancellation!=null)try{cancellation.Cancel();}catch(ObjectDisposedException){}}
        protected override void Dispose(bool disposing){if(disposing)AbortSubmission();base.Dispose(disposing);}
    }
}
