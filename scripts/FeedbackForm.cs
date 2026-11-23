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
        internal const string Url="https://myfinder-feedback-relay.wushuai591459380.workers.dev";
        internal const string DenoUrl="https://myfinder-feedback-relay.wushuai0802.deno.net";
        internal static string[] Addresses(string address) {
            return address==Url?new[]{Url,DenoUrl}:new[]{address};
        }
        internal static int Deadline(string address,int timeout) {
            return address==Url?Math.Min(5000,timeout):timeout;
        }
        internal static bool RejectedBeforeWrite(int status,string error) {
            return (status==429&&error=="rate_limited")||(status==503&&error=="disabled");
        }
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
        internal const string ComparisonQuestion = "MyFinder 显示的电量和官方软件一致吗？";
        internal static string WithComparison(string description, int choice, int difference) {
            string answer;
            switch (choice) {
                case 1: answer = "一致"; break;
                case 2:
                    if (difference < 1 || difference > 100) return description;
                    answer = "相差 " + difference.ToString(CultureInfo.InvariantCulture) + "%";
                    break;
                case 3: answer = "官方读不到"; break;
                default: return description;
            }
            return (description??"").TrimEnd() + "\r\n" + ComparisonQuestion + answer;
        }
        internal static string Prefix(string text,int length){if(length>0&&length<text.Length&&Char.IsHighSurrogate(text[length-1]))length--;return text.Substring(0,length);}
    }
    [DataContract] internal sealed class FeedbackPayload {
        [DataMember(Name="schema")] public int Schema=1;
        [DataMember(Name="category")] public string Category;
        [DataMember(Name="description")] public string Description;
        [DataMember(Name="diagnostics")] public string Diagnostics;
        [DataMember(Name="appVersion")] public string AppVersion;
        [DataMember(Name="replyKey",EmitDefaultValue=false)] public string ReplyKey;
        internal static string LimitDiagnostics(string diagnostics){
            const string marker="[已截断]";
            return diagnostics!=null&&diagnostics.Length>20000?FeedbackInput.Prefix(diagnostics,20000-marker.Length)+marker:diagnostics;
        }
        internal static byte[] Create(string category,string description,string diagnostics,string replyKey=null){
            if(!FeedbackInput.Valid(category,description))throw new ArgumentException("Invalid feedback input.");
            if(replyKey!=null&&!FeedbackProtocol.ValidKey(replyKey))throw new ArgumentException("Invalid feedback key.");
            var payload=new FeedbackPayload {Schema=replyKey==null?1:2,ReplyKey=replyKey,Category=category,Description=description.Trim(),Diagnostics=LimitDiagnostics(diagnostics),AppVersion=AppRuntime.Version};
            using(var stream=new MemoryStream()){new DataContractJsonSerializer(typeof(FeedbackPayload)).WriteObject(stream,payload);return stream.ToArray();}
        }
    }
    internal interface IFeedbackTransport {UpdateResponse Post(Uri uri,byte[] body,CancellationToken token);}
    internal sealed class FeedbackNotSentException:IOException {
        internal FeedbackNotSentException(Exception error):base("Feedback request was not sent.",error){}
    }
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
            bool sending=false;CancellationTokenRegistration registration=default(CancellationTokenRegistration);
            try{
                var request=CreateRequest(uri);request.ContentLength=body.Length;
                // Keep cancellation registered through response reads, not just through GetResponse.
                registration=token.Register(request.Abort);
                token.ThrowIfCancellationRequested();using(var stream=request.GetRequestStream()){sending=true;stream.Write(body,0,body.Length);}
                token.ThrowIfCancellationRequested();HttpWebResponse response;
                try{response=(HttpWebResponse)request.GetResponse();}catch(WebException error){response=error.Response as HttpWebResponse;if(response==null)throw;}
                return new UpdateResponse((int)response.StatusCode,response.GetResponseStream(),response.ContentLength,null,response.Headers["Retry-After"],delegate{response.Dispose();registration.Dispose();});
            }catch(Exception error){registration.Dispose();if(!sending)throw new FeedbackNotSentException(error);throw;}
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
    internal enum FeedbackFailure {Unspecified,Connection,Service}
    internal sealed class FeedbackResult {
        internal readonly FeedbackResultKind Kind;internal readonly long Number;internal readonly string Url;
        internal readonly FeedbackFailure Failure;
        internal readonly bool ReplyAccess,Saved;
        internal FeedbackResult(FeedbackResultKind kind,long number=0,string url=null,bool replyAccess=false,bool saved=true,FeedbackFailure failure=FeedbackFailure.Unspecified){Kind=kind;Number=number;Url=url;ReplyAccess=replyAccess;Saved=saved;Failure=failure;}
    }
    [DataContract] internal sealed class FeedbackReceipt {
        [DataMember(Name="number",IsRequired=true)] public long Number {get;set;}
        [DataMember(Name="url",IsRequired=true)] public string Url {get;set;}
        [DataMember(Name="replyAccess")] public bool ReplyAccess {get;set;}
    }
    internal sealed class FeedbackClient {
        readonly string relay;readonly IFeedbackTransport http;readonly FeedbackSession session;readonly int timeoutMilliseconds;readonly FeedbackStore store;readonly Func<DateTime> clock;readonly Func<byte[],FeedbackReceipt> readReceipt;
        internal FeedbackClient(string address,IFeedbackTransport transport,FeedbackSession history,int timeout=20000,FeedbackStore records=null,Func<DateTime> now=null,Func<byte[],FeedbackReceipt> receiptReader=null){relay=address;http=transport;session=history;timeoutMilliseconds=timeout;store=records??new FeedbackStore(null);clock=now??delegate{return DateTime.UtcNow;};readReceipt=receiptReader??delegate(byte[] bytes){return UpdateJson.Read<FeedbackReceipt>(bytes);};}
        internal static string IssueUrl(long number){if(number<=0||number>9007199254740991L)throw new ArgumentException("Invalid issue number.");return "https://github.com/wushuai0802/myfinder-device-battery/issues/"+number.ToString(CultureInfo.InvariantCulture);}
        internal static bool IsIssueUrl(long number,string url){return number>0&&number<=9007199254740991L&&String.Equals(url,IssueUrl(number),StringComparison.Ordinal);}
        internal FeedbackResult Submit(string category,string description,string diagnostics,CancellationToken token){
            token.ThrowIfCancellationRequested();
            if(!FeedbackInput.Valid(category,description))return new FeedbackResult(FeedbackResultKind.Unavailable);
            if(String.IsNullOrEmpty(relay))return new FeedbackResult(FeedbackResultKind.Browser);
            string creation=null;bool mayHaveCreated=false;
            var failure=FeedbackFailure.Unspecified;
            try{
                string key=FeedbackProtocol.NewKey();
                creation=store.BeginCreation(category,description,key,clock());
                foreach(string address in FeedbackRelay.Addresses(relay)) {
                    bool canSwitch=address==FeedbackRelay.Url;
                    bool production=canSwitch||address==FeedbackRelay.DenoUrl;
                    var endpoint=FeedbackRelay.Endpoint(address);
                    token.ThrowIfCancellationRequested();
                    try {
                        using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token)){
                            timeout.CancelAfter(FeedbackRelay.Deadline(address,timeoutMilliseconds));
                            var cancel=timeout.Token;
                            for(int attempt=0;attempt<2;attempt++){
                                var payload=FeedbackPayload.Create(category,description,diagnostics,attempt==0?key:null);
                                cancel.ThrowIfCancellationRequested();
                                mayHaveCreated=true;
                                using(var response=http.Post(endpoint,payload,cancel)){
                                    if(response.Status==400||response.Status==413||
                                        !production&&(response.Status==429||response.Status==503))mayHaveCreated=false;
                                    cancel.ThrowIfCancellationRequested();
                                    if(production&&(response.Status==429||response.Status==503)){
                                        if(response.Status==503)failure=FeedbackFailure.Service;
                                        var rejection=UpdateJson.Read<FeedbackError>(FeedbackProtocol.Read(response,16384,cancel));
                                        // The relay returns these codes before it calls GitHub to create an issue.
                                        // A generic upstream failure is ambiguous, even with a 503 status.
                                        if(FeedbackRelay.RejectedBeforeWrite(response.Status,rejection==null?null:rejection.Error)){
                                            mayHaveCreated=false;
                                            if(canSwitch)break;
                                        }
                                    }
                                    if(response.Status==429)return new FeedbackResult(FeedbackResultKind.RateLimited);
                                    if(response.Status==400&&attempt==0){
                                        var error=UpdateJson.Read<FeedbackError>(FeedbackProtocol.Read(response,16384,cancel));
                                        if(error!=null&&error.Error=="invalid")continue;
                                    }
                                    if(response.Status!=201){
                                        if(response.Status==502||response.Status==503)failure=FeedbackFailure.Service;
                                        return new FeedbackResult(FeedbackResultKind.Unavailable,failure:failure);
                                    }
                                    var receipt=readReceipt(FeedbackProtocol.Read(response,16384,cancel));
                                    if(receipt==null||!IsIssueUrl(receipt.Number,receipt.Url))return new FeedbackResult(FeedbackResultKind.Unavailable);
                                    session.MarkSuccess();
                                    var result=new FeedbackResult(FeedbackResultKind.Success,receipt.Number,receipt.Url,attempt==0&&receipt.ReplyAccess);
                                    try{store.CompleteCreation(creation,result,clock());}catch(Exception){return new FeedbackResult(FeedbackResultKind.Success,receipt.Number,receipt.Url,result.ReplyAccess,false);}
                                    return result;
                                }
                            }
                        }
                    }catch(FeedbackNotSentException){
                        mayHaveCreated=false;
                        token.ThrowIfCancellationRequested();
                        if(failure==FeedbackFailure.Unspecified)failure=FeedbackFailure.Connection;
                        if(!canSwitch)throw;
                    }
                }
                return new FeedbackResult(FeedbackResultKind.Unavailable,failure:failure);
            }catch(WebException){
                token.ThrowIfCancellationRequested();
                if(failure==FeedbackFailure.Unspecified)failure=FeedbackFailure.Connection;
                return new FeedbackResult(FeedbackResultKind.Unavailable,failure:failure);
            }catch(Exception){
                token.ThrowIfCancellationRequested();
                return new FeedbackResult(FeedbackResultKind.Unavailable,failure:failure);
            }
            finally{if(creation!=null&&!mayHaveCreated)try{store.CancelCreation(creation);}catch(Exception){}}
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
    internal static class FeedbackDraft {
        internal static string Category,Description,EntryCategory;
        internal static bool Diagnostics;
        internal static int ComparisonChoice, ComparisonDifference;
        internal static void Clear(){Category=null;Description=null;EntryCategory=null;Diagnostics=true;ComparisonChoice=0;ComparisonDifference=0;}
        internal static void SaveIfChanged(string category,string description,bool diagnostics,string initialCategory,int comparisonChoice=0,int comparisonDifference=0){
            if(category==initialCategory&&String.IsNullOrEmpty(description)&&diagnostics&&comparisonChoice==0){Clear();return;}
            Category=category;Description=description;Diagnostics=diagnostics;EntryCategory=initialCategory;
            ComparisonChoice=comparisonChoice;ComparisonDifference=comparisonDifference;
        }
    }

    internal sealed class FeedbackForm:ThemedDialog {
        readonly FeedbackReport report;readonly FeedbackClient client;readonly FeedbackSession session;readonly string relay;readonly FeedbackReplies replies;
        readonly Action<string> copy,open;readonly Func<string,bool> confirm;
        readonly Action<long> openHistory;long? requestedHistory;
        readonly ThemedSegments categories,comparison;
        readonly TextBox description=new TextBox();readonly CheckBox diagnostics;readonly LinkLabel preview=new LinkLabel();
        readonly TextBox difference = new TextBox { Name="BatteryComparisonDifference", AccessibleName="相差百分比", MaxLength=3, TextAlign=HorizontalAlignment.Right };
        readonly Label remaining=new Label(),status=new Label();
        readonly ThemedButton submit,cancel,fallback,copyReport,view,mine;
        readonly FeedbackActionBar actionBar;
        CancellationTokenSource pending;FeedbackResult receipt;bool busy,inputError;
        internal string SelectedCategory {get{return categories.SelectedValue??"other";}}
        internal FeedbackForm(FeedbackReport snapshot,bool deviceMissing,bool dark,Action<string> copyText=null,Action<string> openLink=null,string address=null,IFeedbackTransport transport=null,FeedbackSession history=null,Func<string,bool> confirmation=null,FeedbackReplies feedbackReplies=null,Action<long> openMyFeedback=null):base(dark){
            report=snapshot;relay=address??FeedbackRelay.Url;session=history??FeedbackSession.Current;openHistory=openMyFeedback;
            copy=copyText??Clipboard.SetText;open=openLink??delegate(string url){Process.Start(new ProcessStartInfo(url) {UseShellExecute=true});};
            confirm=confirmation??delegate(string question){return ThemedMessage.Show(this,"反馈问题",question,MessageBoxButtons.YesNo,MessageBoxDefaultButton.Button2)==DialogResult.Yes;};
            replies=feedbackReplies??new FeedbackReplies(new FeedbackStore(null),transport??new FeedbackHttp(),relay);
            client=new FeedbackClient(relay,transport??new FeedbackHttp(),session,20000,replies.Store);
            Text="反馈问题";ShowInTaskbar=false;StartPosition=FormStartPosition.CenterParent;FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
            ClientSize=new Size(540,568);categories=new ThemedSegments(Theme) {Name="Categories",AccessibleName="类型"};diagnostics=new ThemedSwitch(Theme);
            submit=new ThemedButton(Theme,ThemedButtonKind.Primary);cancel=new ThemedButton(Theme);fallback=new ThemedButton(Theme);copyReport=new ThemedButton(Theme);view=new ThemedButton(Theme,ThemedButtonKind.Text);
            mine=new ThemedButton(Theme,ThemedButtonKind.Text) {Name="MyFeedback",Text=replies.Store.MenuText,AutoSize=true,Enabled=openMyFeedback!=null};
            mine.Click+=delegate{
                if(busy||openHistory==null)return;
                requestedHistory=receipt==null?0:receipt.Number;
                bool modal=Modal;Close();
                if(!modal)OpenRequestedHistory();
            };
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,Margin=Padding.Empty};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            for(int i=0;i<5;i++)layout.RowStyles.Add(new RowStyle(i==1?SizeType.Percent:SizeType.AutoSize,i==1?100:0));
            var typeCard=new ThemedSection(Theme) {Name="FeedbackTypeCard",Dock=DockStyle.Fill,Height=54};
            var typeTitle=Caption("类型",false,false,true);typeTitle.Dock=DockStyle.Top;
            categories.Dock=DockStyle.Bottom;categories.Margin=Padding.Empty;
            for(int i=0;i<4;i++){
                string category=FeedbackInput.CategoryAt(i);categories.AddOption("Category-"+category,FeedbackInput.CategoryLabel(category),category);
                if(category==FeedbackInput.DefaultCategory(deviceMissing))categories.SelectedIndex=i;
            }
            comparison=new ThemedSegments(Theme) {
                Name="BatteryComparison",AccessibleName=FeedbackInput.ComparisonQuestion,
                Anchor=AnchorStyles.Left|AnchorStyles.Right,Margin=Padding.Empty
            };
            comparison.AddOption("Comparison-Unanswered","不填","unanswered");
            comparison.AddOption("Comparison-Agreement","一致","agreement");
            comparison.AddOption("Comparison-Difference","有差异","difference");
            comparison.AddOption("Comparison-Unavailable","官方读不到","unavailable");
            bool restoreDraft=FeedbackDraft.Description!=null&&FeedbackDraft.EntryCategory==FeedbackInput.DefaultCategory(deviceMissing);
            if(restoreDraft){
                for(int i=0;i<categories.OptionCount;i++)if(FeedbackInput.CategoryAt(i)==FeedbackDraft.Category){categories.SelectedIndex=i;break;}
            }
            Action sizeTypeCard=delegate{typeCard.Height=typeCard.Padding.Vertical+typeTitle.PreferredHeight+4+categories.Height;};
            categories.SizeChanged+=delegate{sizeTypeCard();};typeTitle.SizeChanged+=delegate{sizeTypeCard();};
            typeCard.Controls.Add(categories);typeCard.Controls.Add(typeTitle);layout.Controls.Add(typeCard,0,0);
            sizeTypeCard();
            var descriptionCard=new ThemedSection(Theme) {Name="FeedbackDescriptionCard",Dock=DockStyle.Fill};
            var descriptionLayout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,Margin=Padding.Empty};descriptionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            for(int i=0;i<5;i++)descriptionLayout.RowStyles.Add(new RowStyle(i==2?SizeType.Percent:SizeType.AutoSize,i==2?100:0));
            descriptionLayout.Controls.Add(Caption("描述（必填，5–2000 字）",false,false,true),0,0);
            var hint=Caption("发生了什么？用的是什么设备、怎么连接的？",false,true);descriptionLayout.Controls.Add(hint,0,1);
            description.Name="Description";description.AccessibleName="描述";description.Multiline=true;description.AcceptsReturn=true;description.Dock=DockStyle.Fill;
            description.TextChanged+=delegate{InputChanged();};descriptionLayout.Controls.Add(new ThemedTextBox(Theme,description) {Dock=DockStyle.Fill,Margin=new Padding(0,5,0,4)},0,2);
            remaining.Name="FeedbackRemaining";remaining.AutoSize=true;remaining.Font=hint.Font;remaining.BackColor=Theme.Card;remaining.ForeColor=Theme.Muted;remaining.Margin=Padding.Empty;descriptionLayout.Controls.Add(remaining,0,3);
            descriptionCard.Controls.Add(descriptionLayout);layout.Controls.Add(descriptionCard,0,1);
            var diagnosticCard=new ThemedSection(Theme) {Name="FeedbackDiagnosticsCard",Dock=DockStyle.Fill,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink};
            var diagnosticRow=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Top,Margin=Padding.Empty,WrapContents=true};
            diagnostics.Name="IncludeDiagnostics";diagnostics.Text="附带诊断信息（推荐）";diagnostics.AccessibleName=diagnostics.Text;diagnostics.Checked=true;diagnostics.Margin=new Padding(0,0,12,0);
            if(restoreDraft){description.Text=FeedbackDraft.Description;diagnostics.Checked=FeedbackDraft.Diagnostics;}
            preview.Name="ViewDiagnostics";preview.Text="查看内容";preview.AutoSize=true;preview.BackColor=Theme.Card;preview.LinkColor=Theme.Accent;preview.ActiveLinkColor=preview.LinkColor;preview.VisitedLinkColor=preview.LinkColor;preview.DisabledLinkColor=Theme.Muted;preview.Margin=new Padding(0,4,0,3);preview.LinkClicked+=delegate{ShowDiagnostics();};
            diagnosticRow.Controls.Add(diagnostics);diagnosticRow.Controls.Add(preview);
            diagnosticCard.Controls.Add(diagnosticRow);layout.Controls.Add(diagnosticCard,0,2);
            var comparisonPanel=new TableLayoutPanel {Name="FeedbackComparison",Dock=DockStyle.Fill,AutoSize=true,RowCount=2,ColumnCount=1,Margin=new Padding(0,6,0,0)};
            comparisonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            comparisonPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            comparisonPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var comparisonCaption=Caption(FeedbackInput.ComparisonQuestion+"（可选）",false,true);
            comparisonCaption.Dock=DockStyle.Fill;
            comparison.SelectedIndex=restoreDraft?FeedbackDraft.ComparisonChoice:0;
            if(restoreDraft&&FeedbackDraft.ComparisonDifference>0)
                difference.Text=FeedbackDraft.ComparisonDifference.ToString(CultureInfo.InvariantCulture);
            difference.Dock=DockStyle.Fill;
            var differenceBox=new ThemedTextBox(Theme,difference) {Width=60,Height=difference.PreferredHeight+20,Margin=new Padding(4,0,0,0)};
            var percentLabel=Caption("%",false,true);
            percentLabel.Margin=new Padding(0,4,0,0);
            var comparisonRow=new TableLayoutPanel {
                AutoSize=true,Dock=DockStyle.Fill,Margin=Padding.Empty,RowCount=1,ColumnCount=2
            };
            comparisonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            comparisonRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            comparisonRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var percentageRow=new FlowLayoutPanel {
                AutoSize=true,Anchor=AnchorStyles.Left,Margin=Padding.Empty,WrapContents=false
            };
            percentageRow.Controls.Add(differenceBox);
            percentageRow.Controls.Add(percentLabel);
            comparisonRow.Controls.Add(comparison,0,0);
            comparisonRow.Controls.Add(percentageRow,1,0);
            Action showDifference=delegate {
                percentageRow.Visible=comparison.SelectedIndex==2;
            };
            showDifference();
            comparison.SelectedIndexChanged+=delegate{showDifference();InputChanged();};
            difference.TextChanged+=delegate{InputChanged();};
            comparisonPanel.Controls.Add(comparisonCaption,0,0);
            comparisonPanel.Controls.Add(comparisonRow,0,1);
            descriptionLayout.Controls.Add(comparisonPanel,0,4);
            var notice=Caption(String.IsNullOrEmpty(relay)?"将打开 GitHub 提交，需要 GitHub 账号":"提交后会公开显示在 GitHub 项目的问题列表里。诊断不含序列号和文件路径，请不要在描述里填写个人信息。",false,true);
            notice.Name="FeedbackNotice";
            notice.AutoSize=false;
            notice.Margin=Padding.Empty;
            notice.Dock=DockStyle.Fill;
            var noticeCard=new FeedbackNoticeSection(Theme,notice) {Name="FeedbackNoticeCard",Dock=DockStyle.Fill};
            layout.Controls.Add(noticeCard,0,3);
            status.Name="FeedbackStatus";status.AutoSize=true;status.Dock=DockStyle.Fill;status.Font=hint.Font;status.BackColor=Theme.Card;status.ForeColor=Theme.Foreground;status.Margin=new Padding(0,0,0,6);status.Visible=false;
            submit.Name="SubmitFeedback";submit.Text=String.IsNullOrEmpty(relay)?"在浏览器中提交":"提交";submit.AutoSize=true;submit.MinimumSize=new Size(82,30);submit.Click+=delegate{Submit();};
            cancel.Name="CancelFeedback";cancel.Text="取消";cancel.AutoSize=true;cancel.MinimumSize=new Size(82,30);cancel.Click+=delegate{Close();};
            fallback.Name="BrowserFallback";fallback.Text="改用浏览器提交";fallback.AutoSize=true;fallback.MinimumSize=new Size(132,30);fallback.Visible=false;fallback.Click+=delegate{if(ConfirmRepeat())OpenBrowser();};
            copyReport.Name="CopyFeedbackReport";copyReport.Text="复制报告";copyReport.AutoSize=true;copyReport.MinimumSize=new Size(82,30);copyReport.Visible=false;copyReport.Click+=delegate{CopyReport();};
            view.Name="ViewIssue";view.Text="在浏览器中查看";view.AutoSize=true;view.MinimumSize=new Size(132,30);view.Visible=false;
            view.Click+=delegate{if(receipt!=null&&FeedbackClient.IsIssueUrl(receipt.Number,receipt.Url))try{open(receipt.Url);}catch(Exception){SetStatus("无法打开浏览器，请稍后重试。");}};
            actionBar=new FeedbackActionBar(mine,submit,cancel,view,fallback,copyReport) {Dock=DockStyle.Fill,Margin=Padding.Empty};
            var footer=new TableLayoutPanel {AutoSize=true,Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            footer.Controls.Add(status,0,0);footer.Controls.Add(actionBar,0,1);
            layout.Controls.Add(footer,0,4);Controls.Add(layout);AcceptButton=submit;CancelButton=cancel;
            ConfigureLayout(layout,footer,delegate(int width){
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
            string initialCategory=FeedbackInput.DefaultCategory(deviceMissing);
            Shown+=delegate{LayoutFooter();};
            FormClosing+=delegate{AbortSubmission();if(receipt==null)FeedbackDraft.SaveIfChanged(SelectedCategory,description.Text,diagnostics.Checked,initialCategory,comparison.SelectedIndex,DifferenceValue);else FeedbackDraft.Clear();};UpdateValidation();
        }
        sealed class FeedbackNoticeSection:ThemedSection {
            readonly Label notice;

            internal FeedbackNoticeSection(DialogTheme theme,Label notice):base(theme){
                this.notice=notice;
                Controls.Add(notice);
                AutoSizeMode=AutoSizeMode.GrowAndShrink;
                AutoSize=true;
            }

            public override Size GetPreferredSize(Size proposedSize){
                if(notice==null)return base.GetPreferredSize(proposedSize);
                int width=proposedSize.Width>0?proposedSize.Width:Width;
                int textWidth=Math.Max(1,width-Padding.Horizontal);
                return new Size(width,notice.GetPreferredSize(new Size(textWidth,0)).Height+Padding.Vertical);
            }
        }
        sealed class FeedbackActionBar:Panel {
            readonly ThemedButton history;
            readonly ThemedButton browserFallback,copyReport;
            readonly FlowLayoutPanel actions;
            readonly Control[] singleRowOrder,groupedOrder;
            bool arranging;

            internal FeedbackActionBar(ThemedButton history,ThemedButton submit,ThemedButton cancel,
                    ThemedButton view,ThemedButton browserFallback,ThemedButton copyReport){
                this.history=history;
                this.browserFallback=browserFallback;
                this.copyReport=copyReport;
                singleRowOrder=new Control[]{submit,cancel,view,browserFallback,copyReport};
                // The flow is right to left within each row.
                groupedOrder=new Control[]{copyReport,browserFallback,submit,cancel,view};
                actions=new FlowLayoutPanel {FlowDirection=FlowDirection.RightToLeft,WrapContents=true,Margin=Padding.Empty};
                actions.Controls.AddRange(singleRowOrder);
                AutoSize=true;
                AutoSizeMode=AutoSizeMode.GrowAndShrink;
                Controls.Add(history);
                Controls.Add(actions);
            }

            public override Size GetPreferredSize(Size proposedSize){
                if(history==null||actions==null)return base.GetPreferredSize(proposedSize);
                Size historySize=history.GetPreferredSize(Size.Empty);
                int historyWidth=historySize.Width+history.Margin.Horizontal;
                int width=proposedSize.Width>0?proposedSize.Width:Width;
                int actionWidth=Math.Max(1,width-historyWidth);
                int actionsHeight=MeasureActions(actionWidth,NeedsGroupedRows(actionWidth));
                return new Size(width,Math.Max(historySize.Height+history.Margin.Vertical,actionsHeight));
            }

            bool NeedsGroupedRows(int width){
                if(!browserFallback.Visible||!copyReport.Visible)return false;
                int required=0;
                foreach(Control action in singleRowOrder){
                    if(action.Visible)required+=action.GetPreferredSize(Size.Empty).Width+action.Margin.Horizontal;
                }
                return required>width;
            }

            int MeasureActions(int width,bool grouped){
                int height=0,rowWidth=0,rowHeight=0;
                foreach(Control action in grouped?groupedOrder:singleRowOrder){
                    if(!action.Visible)continue;
                    Size size=action.GetPreferredSize(Size.Empty)+action.Margin.Size;
                    if(rowWidth>0&&rowWidth+size.Width>width){
                        height+=rowHeight;
                        rowWidth=rowHeight=0;
                    }
                    rowWidth+=size.Width;
                    rowHeight=Math.Max(rowHeight,size.Height);
                    if(grouped&&action==browserFallback){
                        height+=rowHeight;
                        rowWidth=rowHeight=0;
                    }
                }
                return height+rowHeight;
            }

            void SetGroupedRows(bool grouped){
                if(actions.GetFlowBreak(browserFallback)==grouped)return;
                var order=grouped?groupedOrder:singleRowOrder;
                actions.SuspendLayout();
                try{
                    for(int i=0;i<order.Length;i++)actions.Controls.SetChildIndex(order[i],i);
                    actions.SetFlowBreak(browserFallback,grouped);
                }finally{actions.ResumeLayout(false);}
            }

            protected override void OnLayout(LayoutEventArgs e){
                base.OnLayout(e);
                if(history==null||actions==null||arranging)return;
                arranging=true;
                try{
                    Size historySize=history.GetPreferredSize(Size.Empty);
                    int historyWidth=historySize.Width+history.Margin.Horizontal;
                    int actionWidth=Math.Max(1,ClientSize.Width-historyWidth);
                    bool grouped=NeedsGroupedRows(actionWidth);
                    int actionHeight=MeasureActions(actionWidth,grouped);
                    int actionTop=Math.Max(0,ClientSize.Height-actionHeight);
                    SetGroupedRows(grouped);
                    actions.Bounds=new Rectangle(historyWidth,actionTop,actionWidth,actionHeight);
                    actions.PerformLayout();
                    int historyTop=history.Margin.Top+Math.Max(0,actionTop+(actionHeight-historySize.Height-history.Margin.Vertical)/2);
                    history.Bounds=new Rectangle(history.Margin.Left,historyTop,historySize.Width,historySize.Height);
                }finally{arranging=false;}
            }
        }
        int DifferenceValue {
            get {
                int value;
                return Int32.TryParse(difference.Text,NumberStyles.None,CultureInfo.InvariantCulture,out value)&&value>=1&&value<=100?value:0;
            }
        }
        string SubmittedDescription {get{return FeedbackInput.WithComparison(description.Text,comparison.SelectedIndex,DifferenceValue);}}
        bool InputValid {get{return (comparison.SelectedIndex!=2||DifferenceValue>0)&&FeedbackInput.Valid(SelectedCategory,description.Text)&&FeedbackInput.Valid(SelectedCategory,SubmittedDescription);}}
        void LayoutFooter(){
            actionBar.Parent.PerformLayout(actionBar,"PreferredSize");
            actionBar.PerformLayout();
            PerformLayout();
        }
        // Modal callers consume this only after ShowDialog has returned and
        // Windows has restored the windows disabled by its modal loop.
        internal void OpenRequestedHistory(){
            if(Modal||Visible||!requestedHistory.HasValue||openHistory==null)return;
            long number=requestedHistory.Value;requestedHistory=null;openHistory(number);
        }
        void SetStatus(string text){
            status.Text=text;
            status.Visible=text.Length!=0;
            LayoutFooter();
        }
        void InputChanged(){
            if(inputError){inputError=false;SetStatus("");}
            UpdateValidation();
        }
        void UpdateValidation(){
            int length=description.Text.Trim().Length;
            int total=SubmittedDescription.Trim().Length;
            remaining.Text=length<5?"还需输入 "+(5-length)+" 字；还可输入 "+(2000-total)+" 字":total>2000?"已超出 "+(total-2000)+" 字（最多 2000 字）":"还可输入 "+(2000-total)+" 字";
            if(comparison.SelectedIndex==2&&DifferenceValue==0)remaining.Text+="。相差请输入 1–100 的整数。";
            submit.Enabled=!busy;
            mine.Enabled=!busy&&openHistory!=null;
            fallback.Enabled=copyReport.Enabled=!busy&&InputValid;
        }
        bool ConfirmRepeat(){return !session.NeedsConfirmation||confirm("刚刚已提交过一次，确定还要再提交吗？");}
        void EnableInputs(bool enabled){categories.Enabled=enabled;description.Enabled=enabled;diagnostics.Enabled=enabled;preview.Enabled=enabled;comparison.Enabled=enabled;difference.Enabled=enabled;}
        void Submit(){
            if(receipt!=null){Close();return;}
            if(busy)return;
            if(!InputValid){
                inputError=true;
                int length=description.Text.Trim().Length;
                if(length<5){
                    SetStatus("描述至少 5 个字，还差 "+(5-length)+" 个字");
                    description.Focus();
                }else if(comparison.SelectedIndex==2&&DifferenceValue==0){
                    SetStatus("相差请输入 1–100 的整数。");
                    difference.Focus();
                }else{
                    SetStatus("描述最多 2000 个字，请缩短后再提交。");
                    description.Focus();
                }
                return;
            }
            if(!ConfirmRepeat())return;
            if(String.IsNullOrEmpty(relay)){OpenBrowser();return;}
            string category=SelectedCategory,text=SubmittedDescription,diagnosticText=diagnostics.Checked?report.FullText:null;
            busy=true;EnableInputs(false);fallback.Visible=copyReport.Visible=false;SetStatus("正在提交…");UpdateValidation();
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
            if(result.Kind==FeedbackResultKind.Success){
                FeedbackDraft.Clear();receipt=result;
                submit.Text="关闭";mine.Text="查看我的反馈";view.Visible=true;cancel.Visible=false;CancelButton=submit;
                SetStatus("已提交，编号 #"+result.Number.ToString(CultureInfo.InvariantCulture)+"。"+(result.Saved?"":"本地记录未保存，请记下编号并在网页查看。"));
            }
            else{
                EnableInputs(true);
                fallback.Visible=copyReport.Visible=true;
                if(result.Kind==FeedbackResultKind.RateLimited)SetStatus("提交太频繁，请过一会儿再试。");
                else{
                    string reason="暂时无法提交";
                    if(result.Failure==FeedbackFailure.Connection)reason="连不上反馈服务";
                    else if(result.Failure==FeedbackFailure.Service)reason="反馈服务暂时停用";
                    SetStatus("提交失败："+reason+"。可以改用浏览器提交，或复制报告发到群里。");
                }
            }
            UpdateValidation();
        }
        void OpenBrowser(){if(busy||!InputValid)return;SetStatus(report.BrowserSubmission(SelectedCategory,SubmittedDescription,diagnostics.Checked).Open(copy,open));}
        void CopyReport(){
            if(busy||!InputValid)return;
            try{
                copy(report.BrowserSubmission(SelectedCategory,SubmittedDescription,diagnostics.Checked).Text);
                SetStatus("已复制");
            }catch(Exception){SetStatus("无法复制报告，请稍后再试。");}
        }
        internal ThemedDialog CreateDiagnosticsDialog(){return CreateDiagnosticsDialog(Theme.Dark,report.FullText);}
        internal static ThemedDialog CreateDiagnosticsDialog(bool dark,string contents){
            var dialog=new ThemedDialog(dark) {Text="诊断信息",ClientSize=new Size(620,420),FormBorderStyle=FormBorderStyle.Sizable};
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var text=new TextBox {Name="DiagnosticText",AccessibleName="诊断信息",Text=contents,ReadOnly=true,Multiline=true,WordWrap=false,HideSelection=true,SelectionLength=0,TabStop=true};
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
