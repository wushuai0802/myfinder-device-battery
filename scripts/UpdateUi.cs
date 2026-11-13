using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;

namespace AtkBatteryTray {
    internal sealed class UpdateDialog:ThemedDialog {
        internal readonly string Version;
        readonly ThemedButton install,skip,cancel;
        readonly Label status;readonly ThemedProgress progress;readonly ThemedSection statusArea;
        readonly ThemedSection versionCard;readonly Label title,current,next;readonly FlowLayoutPanel versions;
        bool downloading,handedOff,progressShown;
        internal event Action InstallRequested,SkipRequested,CancelRequested;
        internal UpdateDialog(UpdateRelease release,bool stable):this(release,stable,false){}
        internal UpdateDialog(UpdateRelease release,bool stable,bool cached){
            Version=release.Version;
            Text="MyFinder 设备电量 · 软件更新";StartPosition=FormStartPosition.CenterScreen;ShowInTaskbar=true;
            ClientSize=new Size(380,340);
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Margin=Padding.Empty};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            versionCard=new ThemedSection(Theme) {Name="VersionCard",Dock=DockStyle.Fill,Height=54};
            var versionLayout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};
            versionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));versionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bool beta=release.Prerelease||UpdateVersion.Parse(release.Version).IsBeta;
            title=Caption(cached?(beta?"已缓存测试版":"已缓存版本"):(beta?"发现新测试版":"发现新版本"),true);title.Name="UpdateTitle";title.Margin=Padding.Empty;versionLayout.Controls.Add(title,0,0);
            versions=new FlowLayoutPanel {Dock=DockStyle.Fill,AutoSize=true,WrapContents=false,Margin=new Padding(0,4,0,0)};
            current=Caption("当前 "+AppRuntime.Version+" → ");current.Margin=Padding.Empty;
            next=Caption(release.Version);next.Name="NewVersion";next.ForeColor=Theme.Accent;next.Margin=Padding.Empty;
            versions.Controls.AddRange(new Control[]{current,next});versionLayout.Controls.Add(versions,0,1);versionCard.Controls.Add(versionLayout);layout.Controls.Add(versionCard,0,0);
            var notes=new ThemedReleaseNotes(Theme) {Name="ReleaseNotes",AccessibleName="发布说明",Text=Notes(release.Body),TabStop=true,Dock=DockStyle.Fill};
            var section=new ThemedTextBox(Theme,notes) {Name="ReleaseNotesCard",Dock=DockStyle.Fill};layout.Controls.Add(section,0,1);
            status=Caption(cached?"这是已缓存的官方发布版本；下载时仍会重新校验签名和附件。":stable&&beta?"测试版含待实机验证的功能，不想试用可跳过此版本。":stable?"点击更新后下载，签名与文件校验通过后安装。":"绿色模式：请打开发布页面下载安装。",false,true);
            status.Name="UpdateStatus";status.AutoSize=false;status.AutoEllipsis=true;status.Dock=DockStyle.Fill;status.Margin=Padding.Empty;
            statusArea=new ThemedSection(Theme) {Name="UpdateStatusCard",Dock=DockStyle.Fill};
            progress=new ThemedProgress(Theme) {Name="UpdateProgress",Dock=DockStyle.Bottom,Visible=false};
            statusArea.Controls.Add(status);statusArea.Controls.Add(progress);layout.Controls.Add(statusArea,0,2);
            install=new ThemedButton(Theme,ThemedButtonKind.Primary) {Name="InstallUpdate",Text=stable?"更新并重启":"打开发布页面",AutoSize=true,TabIndex=1};
            skip=new ThemedButton(Theme,ThemedButtonKind.Text) {Name="SkipUpdate",Text="跳过此版本",AutoSize=true,Margin=Padding.Empty,TabIndex=3};
            cancel=new ThemedButton(Theme) {Name="LaterUpdate",Text="稍后",AutoSize=true,TabIndex=2};
            var buttons=new TableLayoutPanel {Dock=DockStyle.Fill,AutoSize=true,ColumnCount=3,RowCount=1,Margin=Padding.Empty};
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            skip.Anchor=AnchorStyles.Left;buttons.Controls.Add(skip,0,0);buttons.Controls.Add(cancel,1,0);buttons.Controls.Add(install,2,0);
            layout.Controls.Add(buttons,0,3);Controls.Add(layout);AcceptButton=install;CancelButton=cancel;ActiveControl=install;
            Func<int> surrounding=delegate{SizeStatus();return versionCard.Height+versionCard.Margin.Vertical+statusArea.Height+statusArea.Margin.Vertical+section.Padding.Vertical+section.Margin.Vertical;};
            ConfigureLayout(layout,buttons,delegate(int width){return surrounding()+2*notes.LineHeight+4;},delegate(int width){return surrounding()+notes.PreferredNotesHeight(width-section.Padding.Horizontal);});
            notes.TextChanged+=delegate{FitToWorkingArea(false);};
            Shown+=delegate{notes.Select(0,0);FocusAction(handedOff?null:downloading?cancel:install);};
            install.Click+=delegate{if(InstallRequested!=null)InstallRequested();};
            skip.Click+=delegate{if(SkipRequested!=null)SkipRequested();};
            cancel.Click+=delegate{if(downloading){if(CancelRequested!=null)CancelRequested();status.Text="正在取消…";FocusAction(null);cancel.Enabled=false;}else Close();};
            FormClosing+=delegate(object sender,FormClosingEventArgs e){if(downloading&&!handedOff){e.Cancel=true;if(CancelRequested!=null)CancelRequested();status.Text="正在取消…";}};
        }
        // The notes box is plain text: show GitHub's Markdown headings, bold and code marks as
        // readable text instead of raw symbols.
        internal static string Notes(string text){
            var lines=UpdateText.Notes(text).Replace("\r\n","\n").Replace('\r','\n').Split('\n');
            for(int i=0;i<lines.Length;i++){
                string line=lines[i].Replace("**","").Replace("`","");
                string heading=line.TrimStart('#');
                if(heading.Length<line.Length&&heading.StartsWith(" ",StringComparison.Ordinal))line="【"+heading.Trim()+"】";
                else{int indent=line.Length-line.TrimStart(' ').Length;if(line.Substring(indent).StartsWith("- ",StringComparison.Ordinal))line=line.Substring(0,indent)+"• "+line.Substring(indent+2);}
                lines[i]=line;
            }
            return String.Join(Environment.NewLine,lines);
        }
        void SizeStatus(){
            versionCard.Height=Math.Max(54,versionCard.Padding.Vertical+title.PreferredHeight+versions.Margin.Vertical+Math.Max(current.PreferredHeight,next.PreferredHeight));
            int gap=progressShown?(int)Math.Round(12*DialogDpi.Scale(this)):0;
            status.Padding=new Padding(0,0,0,gap);
            statusArea.Height=statusArea.Padding.Vertical+status.Font.Height+(progressShown?progress.Height+gap:0);
        }
        void ShowProgress(bool visible){progressShown=visible;progress.Visible=visible;SizeStatus();FitToWorkingArea(false);}
        void FocusAction(Control action){
            ActiveControl=action!=null&&action.Enabled?action:null;
            if(!ContainsFocus)return;
            if(ActiveControl!=null)ActiveControl.Focus();else Focus();
        }
        internal void Downloading(){downloading=true;handedOff=false;cancel.Enabled=true;FocusAction(cancel);install.Enabled=false;skip.Enabled=false;status.ForeColor=Theme.Muted;status.Text="正在下载和校验…";ShowProgress(true);progress.Value=0;progress.Indeterminate=true;}
        internal void Progress(long bytes,long total){
            if(IsDisposed)return;int percent=total>0?(int)Math.Max(0,Math.Min(100,bytes*100.0/total)):0;
            status.Text="已下载 "+(Math.Max(0,bytes)/1048576.0).ToString("F1",CultureInfo.InvariantCulture)+" MB"+(total>0?" / "+(total/1048576.0).ToString("F1",CultureInfo.InvariantCulture)+" MB（"+percent.ToString(CultureInfo.InvariantCulture)+"%）":"");
            progress.Indeterminate=total<=0;progress.Value=percent;
        }
        internal void Finished(string text){downloading=false;handedOff=false;status.Text=text;status.ForeColor=text.StartsWith("已取消",StringComparison.Ordinal)||text.StartsWith("下载仍在本地等待",StringComparison.Ordinal)?Theme.Muted:Theme.Error;install.Enabled=true;skip.Enabled=true;cancel.Enabled=true;progress.Indeterminate=false;progress.Value=0;ShowProgress(false);FocusAction(install);}
        internal void Handoff(){handedOff=true;FocusAction(null);cancel.Enabled=false;ShowProgress(true);progress.Indeterminate=false;progress.Value=100;status.ForeColor=Theme.Muted;status.Text="签名与文件校验通过，正在退出并安装…";}
        internal void Shutdown(){handedOff=true;downloading=false;Close();}
    }
    [DataContract] internal sealed class UpdateResult {
        [DataMember(Name="schema")] public int Schema {get;set;}
        [DataMember(Name="status")] public string Status {get;set;}
        [DataMember(Name="version")] public string Version {get;set;}
        [DataMember(Name="reason")] public string Reason {get;set;}
        [DataMember(Name="timeUtc")] public string TimeUtc {get;set;}
        [DataMember(Name="notified")] public bool Notified {get;set;}
    }
    internal sealed class UpdateController:IDisposable {
        readonly Control dispatch;readonly Action changed;readonly Action<string,string,bool> balloon;
        readonly UpdateProcess installer;readonly IUpdateTransport http;readonly string data;
        readonly DateTime started=DateTime.UtcNow;readonly object gate=new object();
        readonly System.Threading.Timer timer;
        volatile UpdateChecker checker;bool? betaChoice;UpdateRelease available;UpdateDialog dialog;bool availableCached;
        CancellationTokenSource checkCancel,downloadCancel;bool automaticInFlight,downloadCommitted;
        volatile bool disposed;int checking,installing;
        string status="尚未检查";
        internal string LastFailure {get;private set;}
        internal string Status {get{
            if(checking!=0)return "正在检查更新…";
            if(checker!=null){
                DateTime now=DateTime.UtcNow;var wait=checker.Backoff(UpdateBackoffScope.Catalog,now);string code=checker.ErrorCode;
                if(code=="update_waiting_catalog"||wait.Waiting(now)||code=="update_http_403"||code=="update_http_429")return CheckStatus(available,now);
            }
            return available!=null?(availableCached?"已缓存版本 ":"有新版本 ")+available.Version:!DeviceSettings.Current.AutoCheckUpdates?"自动检查已关闭":status;
        }}
        internal bool Available {get{return available!=null;}}
        internal string Version {get{return available==null?null:available.Version;}}
        internal bool BadgeVisible {get{return Available&&checker!=null&&checker.BadgeVisible(DeviceSettings.Current.ShowUpdateBadge,DeviceSettings.Current.AutoCheckUpdates);}}
        internal UpdateController(Control control,Action refresh,Action<string,string,bool> notify,IUpdateTransport transport=null,string dataDirectory=null,string executablePath=null,bool runTimer=true){
            dispatch=control;changed=refresh;balloon=notify;data=dataDirectory??DeviceSettings.DataDirectory;betaChoice=DeviceSettings.Current.IncludeBetaUpdates;
            installer=new UpdateProcess(executablePath??Application.ExecutablePath,data);http=transport??new UpdateHttp(AppRuntime.Version);
            timer=new System.Threading.Timer(delegate{Check(true);},null,runTimer?0:Timeout.Infinite,60000);
        }
        void Post(Action action){if(disposed)return;try{dispatch.BeginInvoke((MethodInvoker)delegate{if(!disposed)action();});}catch(InvalidOperationException){}}
        void Initialize(){
            if(checker!=null)return;
            checker=new UpdateChecker(http,Path.Combine(data,"update-state.json"),AppRuntime.Version,started) {IncludeBeta=delegate{return DeviceSettings.Current.IncludeBetaUpdates;}};
            var cached=checker.CachedRelease;if(cached!=null)Post(delegate{available=cached;availableCached=true;changed();});
            try{UpdateDownload.CleanStale(Path.Combine(data,"updates"),DateTime.UtcNow);}catch(Exception){SafeDiagnostic.Transition("updates","update_cleanup_failed");}
            string path=Path.Combine(data,"update-result.json");
            try{
                if(!File.Exists(path))return;var result=UpdateJson.ReadFile<UpdateResult>(path,16384);
                if(result.Schema!=1||result.Status!="failed")return;
                string failure=UpdateText.Notes(result.Reason);if(failure.Length>1500)failure=failure.Substring(0,1500);
                bool notify=!result.Notified;result.Notified=true;AtomicText.Write(path,UpdateJson.Text(result));
                Post(delegate{LastFailure=failure;changed();if(notify)balloon("上次更新失败",failure,false);});
            }catch(Exception){SafeDiagnostic.Transition("updates","update_result_read_failed");}
        }
        internal void Check(bool automatic){
            if(disposed||Interlocked.CompareExchange(ref checking,1,0)!=0)return;
            var cancellation=new CancellationTokenSource();lock(gate){checkCancel=cancellation;automaticInFlight=automatic;}
            if(!automatic)Post(delegate{status="正在检查更新…";changed();});
            ThreadPool.QueueUserWorkItem(delegate{
                try{
                    Initialize();if(disposed)return;
                    bool enabled=DeviceSettings.Current.AutoCheckUpdates&&!DeviceSettings.UsingDefaults;
                    var release=checker.Check(automatic,enabled,DateTime.UtcNow,cancellation.Token);
                    if(checker.ErrorCode!=null&&checker.ErrorCode!="update_waiting_catalog")SafeDiagnostic.Transition("updates",checker.ErrorCode);
                    if(release!=null){
                        bool notify=checker.MarkNotified(release.Version);
                        Post(delegate{available=checker.CachedRelease;availableCached=false;if(available==null){if(!automatic)status="未发现新版本";changed();return;}status="有新版本 "+available.Version;changed();if(automatic){if(notify&&available!=null)balloon("MyFinder 设备电量更新",status,true);}else ShowAvailable(true);});
                    }else Post(delegate{var cached=checker.CachedRelease;if(automatic&&available==cached)return;available=cached;availableCached=cached!=null;if(!automatic)status=CheckStatus(cached,DateTime.UtcNow);changed();});
                }catch(Exception){SafeDiagnostic.Transition("updates","update_check_failed");if(!automatic)Post(delegate{status="检查未完成，详情已记入诊断";changed();});}
                finally{lock(gate){if(checkCancel==cancellation)checkCancel=null;}cancellation.Dispose();Interlocked.Exchange(ref checking,0);}
            });
        }
        internal void ConfigurationChanged(){lock(gate){if(!DeviceSettings.Current.AutoCheckUpdates&&automaticInFlight&&checkCancel!=null)checkCancel.Cancel();}var current=checker;bool betas=DeviceSettings.Current.IncludeBetaUpdates;
            if(current!=null){available=current.CachedRelease;if(betaChoice.HasValue&&betaChoice.Value!=betas){current.ForgetLastCheck();betaChoice=betas;Check(true);}}betaChoice=betas;changed();}
        internal void Open(){if(available!=null)ShowAvailable(true);else Check(false);}
        void Viewed(UpdateDialog form,bool userInitiated){
            if(!userInitiated||form.IsDisposed||!form.Visible)return;
            string version=form.Version;
            ThreadPool.QueueUserWorkItem(delegate{try{checker.MarkSeen(version,true);Post(changed);}catch(Exception){SafeDiagnostic.Transition("updates","update_seen_write_failed");}});
        }
        void ShowAvailable(bool userInitiated){
            if(dialog!=null&&!dialog.IsDisposed){DialogVisibility.RestoreAndActivate(dialog);Viewed(dialog,userInitiated);return;}var release=available;if(release==null)return;
            ThreadPool.QueueUserWorkItem(delegate{
                bool stable=false;try{stable=installer.IsStable(CancellationToken.None);}catch(Exception){SafeDiagnostic.Transition("updates","update_install_probe_failed");}
                bool installed=stable;
                Post(delegate{
                    if(dialog!=null&&!dialog.IsDisposed){DialogVisibility.RestoreAndActivate(dialog);Viewed(dialog,userInitiated);return;}
                    if(available==null||available.Version!=release.Version)return;
                    var form=new UpdateDialog(release,installed,availableCached);dialog=form;
                    form.SkipRequested+=delegate{
                        ThreadPool.QueueUserWorkItem(delegate{try{checker.Skip(release.Version);Post(delegate{if(available!=null&&available.Version==release.Version)available=null;status="已跳过 "+release.Version;changed();form.Close();});}catch(Exception){Post(delegate{form.Finished("无法保存跳过记录，请稍后再试。");});}});
                    };
                    form.InstallRequested+=delegate{
                        if(!installed){try{Process.Start(new ProcessStartInfo(release.PageUrl) {UseShellExecute=true});form.Close();}catch(Exception){form.Finished("无法打开发布页面，请稍后再试。");}return;}
                        BeginInstall(release,form);
                    };
                    form.CancelRequested+=delegate{lock(gate){if(downloadCancel!=null&&!downloadCommitted)downloadCancel.Cancel();}};
                    form.Show();Viewed(form,userInitiated);
                });
            });
        }
        static string Remaining(UpdateBackoffInfo wait,DateTime now){TimeSpan left=wait.UntilUtc-now;if(left.TotalSeconds<60)return "不到1分钟";if(left.TotalHours<1)return ((int)Math.Ceiling(left.TotalMinutes)).ToString(CultureInfo.InvariantCulture)+"分钟";return ((int)Math.Ceiling(left.TotalHours)).ToString(CultureInfo.InvariantCulture)+"小时";}
        string CheckStatus(UpdateRelease cached,DateTime now){
            var wait=checker.Backoff(UpdateBackoffScope.Catalog,now);string code=checker.ErrorCode;
            if(code=="update_waiting_catalog")return "本地等待检查，剩余 "+Remaining(wait,now)+(cached==null?"":"；已缓存版本 "+cached.Version);
            if(code=="update_http_403"&&wait.Source!="server")return "访问被拒绝，无法确认限流；本地等待 "+Remaining(wait,now)+(cached==null?"":"；已缓存版本 "+cached.Version);
            if((code=="update_http_403"||code=="update_http_429")&&wait.Source=="server")return "刚收到服务端限流，请在 "+Remaining(wait,now)+" 后重试"+(cached==null?"":"；已缓存版本 "+cached.Version);
            if(code=="update_http_429")return "刚收到服务端限流，已本地等待 "+Remaining(wait,now)+(cached==null?"":"；已缓存版本 "+cached.Version);
            if(wait.Waiting(now))return "本地等待检查，剩余 "+Remaining(wait,now)+(cached==null?"":"；已缓存版本 "+cached.Version);
            return code==null?"未发现新版本":"检查未完成，详情已记入诊断";
        }
        static string DownloadWait(UpdateBackoffInfo wait,DateTime now){return "下载仍在本地等待，剩余 "+Remaining(wait,now)+"；不会重复请求附件。";}
        static string DownloadFailure(UpdateHttpFailure error,UpdateBackoffInfo wait,DateTime now){
            if(error.Status==403&&wait.Source!="server")return "访问被拒绝，无法确认限流；下载已本地等待 "+Remaining(wait,now)+"。";
            if((error.Status==403||error.Status==429)&&wait.Source=="server")return "刚收到服务端限流，下载请在 "+Remaining(wait,now)+" 后重试。";
            if(error.Status==429)return "服务端限制下载，已本地等待 "+Remaining(wait,now)+"。";
            return SafeDiagnostic.Clean(error.Message);
        }
        void BeginInstall(UpdateRelease release,UpdateDialog form){
            if(Interlocked.CompareExchange(ref installing,1,0)!=0)return;
            var cancellation=new CancellationTokenSource();lock(gate){downloadCancel=cancellation;downloadCommitted=false;}form.Downloading();
            ThreadPool.QueueUserWorkItem(delegate{
                try{
                    cancellation.Token.ThrowIfCancellationRequested();DateTime checkTime=DateTime.UtcNow;var wait=checker.Backoff(UpdateBackoffScope.Asset,checkTime);if(wait.Waiting(checkTime)){Post(delegate{if(!form.IsDisposed)form.Finished(DownloadWait(wait,DateTime.UtcNow));});return;}
                    var download=new UpdateDownload(http,installer,AppRuntime.Version,installer.InstallDirectory,Path.Combine(data,"updates")) {AllowBeta=DeviceSettings.Current.IncludeBetaUpdates};
                    long lastProgress=0;
                    download.Install(release,cancellation.Token,delegate(long bytes,long total){long now=Stopwatch.GetTimestamp();if(now-lastProgress<Stopwatch.Frequency/5&&bytes!=total)return;lastProgress=now;Post(delegate{if(!form.IsDisposed)form.Progress(bytes,total);});},delegate{
                        if(disposed)throw new OperationCanceledException();
                        // Finish cancellation on the UI thread before starting the auxiliary process.
                        dispatch.Invoke((MethodInvoker)delegate{lock(gate){cancellation.Token.ThrowIfCancellationRequested();if(disposed||form.IsDisposed)throw new OperationCanceledException();downloadCommitted=true;form.Handoff();}});
                    });
                    checker.Complete(UpdateBackoffScope.Asset);
                    installer.RequestExit();
                }catch(OperationCanceledException){Post(delegate{if(!form.IsDisposed)form.Finished("已取消更新。");});}
                catch(Exception error){
                    if(cancellation.IsCancellationRequested){Post(delegate{if(!form.IsDisposed)form.Finished("已取消更新。");});return;}
                    var rate=error as UpdateHttpFailure;string message;if(rate!=null){try{checker.Defer(rate,DateTime.UtcNow,UpdateBackoffScope.Asset);}catch(Exception){}message=DownloadFailure(rate,checker.Backoff(UpdateBackoffScope.Asset,DateTime.UtcNow),DateTime.UtcNow);}else message=SafeDiagnostic.Clean(error.Message);SafeDiagnostic.Transition("updates","update_install_failed: "+message);
                    Post(delegate{if(!form.IsDisposed)form.Finished("更新未完成："+message);});
                }finally{lock(gate){if(downloadCancel==cancellation)downloadCancel=null;}cancellation.Dispose();Interlocked.Exchange(ref installing,0);}
            });
        }
        public void Dispose(){disposed=true;timer.Dispose();lock(gate){if(checkCancel!=null)checkCancel.Cancel();if(downloadCancel!=null&&!downloadCommitted)downloadCancel.Cancel();}if(dialog!=null&&!dialog.IsDisposed)dialog.Shutdown();}
    }
}
