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
        readonly ThemedButton install,skip,cancel;
        readonly Label status;readonly ThemedProgress progress;
        bool downloading,handedOff;
        internal event Action InstallRequested,SkipRequested,CancelRequested;
        internal UpdateDialog(UpdateRelease release,bool stable){
            Text="MyFinder 设备电量 · 软件更新";StartPosition=FormStartPosition.CenterScreen;ShowInTaskbar=true;
            ClientSize=new Size(440,500);
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,Margin=Padding.Empty};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            for(int i=0;i<6;i++)layout.RowStyles.Add(new RowStyle(i==2?SizeType.Percent:SizeType.AutoSize,i==2?100:0));
            var title=Caption("发现新版本",true);title.Name="UpdateTitle";title.Margin=Padding.Empty;layout.Controls.Add(title,0,0);
            var versions=new FlowLayoutPanel {Dock=DockStyle.Fill,AutoSize=true,WrapContents=false,Margin=new Padding(0,6,0,16)};
            var current=Caption("当前 "+AppRuntime.Version+" → ");current.Margin=Padding.Empty;
            var next=Caption(release.Version);next.Name="NewVersion";next.ForeColor=Theme.Accent;next.Margin=Padding.Empty;
            versions.Controls.AddRange(new Control[]{current,next});layout.Controls.Add(versions,0,1);
            var notes=new TextBox {Name="ReleaseNotes",AccessibleName="发布说明",Text=Notes(release.Body),ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical,WordWrap=true,TabStop=true};
            var section=new ThemedSection(Theme) {Dock=DockStyle.Fill,Margin=new Padding(0,0,0,16)};
            notes.BorderStyle=BorderStyle.None;notes.BackColor=Theme.Section;notes.ForeColor=Theme.Foreground;notes.Dock=DockStyle.Fill;section.Controls.Add(notes);
            layout.Controls.Add(section,0,2);
            status=Caption(stable?"点击‘更新并重启’后下载，签名和文件校验通过后才安装。":"当前为绿色模式。打开发布页面下载；安装到稳定目录后可以一键更新。",false,true);
            status.Name="UpdateStatus";status.Dock=DockStyle.Fill;status.Margin=new Padding(0,0,0,12);layout.Controls.Add(status,0,3);
            progress=new ThemedProgress(Theme) {Name="UpdateProgress",Dock=DockStyle.Top,Margin=new Padding(0,0,0,16)};layout.Controls.Add(progress,0,4);
            install=new ThemedButton(Theme,ThemedButtonKind.Primary) {Name="InstallUpdate",Text=stable?"更新并重启":"打开发布页面",AutoSize=true,TabIndex=1};
            skip=new ThemedButton(Theme,ThemedButtonKind.Text) {Name="SkipUpdate",Text="跳过此版本",AutoSize=true,Margin=Padding.Empty,TabIndex=3};
            cancel=new ThemedButton(Theme) {Name="LaterUpdate",Text="稍后",AutoSize=true,TabIndex=2};
            var buttons=new TableLayoutPanel {Dock=DockStyle.Fill,AutoSize=true,ColumnCount=3,RowCount=1,Margin=Padding.Empty};
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            skip.Anchor=AnchorStyles.Left;buttons.Controls.Add(skip,0,0);buttons.Controls.Add(cancel,1,0);buttons.Controls.Add(install,2,0);
            layout.Controls.Add(buttons,0,5);Controls.Add(layout);AcceptButton=install;CancelButton=cancel;ActiveControl=install;
            install.Click+=delegate{if(InstallRequested!=null)InstallRequested();};
            skip.Click+=delegate{if(SkipRequested!=null)SkipRequested();};
            cancel.Click+=delegate{if(downloading){if(CancelRequested!=null)CancelRequested();status.Text="正在取消…";cancel.Enabled=false;}else Close();};
            FormClosing+=delegate(object sender,FormClosingEventArgs e){if(downloading&&!handedOff){e.Cancel=true;if(CancelRequested!=null)CancelRequested();status.Text="正在取消…";}};
        }
        static string Notes(string text){
            var lines=UpdateText.Notes(text).Replace("\r\n","\n").Replace('\r','\n').Split('\n');
            for(int i=0;i<lines.Length;i++)if(lines[i].StartsWith("- ",StringComparison.Ordinal))lines[i]="• "+lines[i].Substring(2);
            return String.Join(Environment.NewLine,lines);
        }
        internal void Downloading(){downloading=true;handedOff=false;install.Enabled=false;skip.Enabled=false;cancel.Enabled=true;status.ForeColor=Theme.Muted;status.Text="正在下载和校验…";progress.Value=0;progress.Indeterminate=true;}
        internal void Progress(long bytes,long total){
            if(IsDisposed)return;int percent=total>0?(int)Math.Max(0,Math.Min(100,bytes*100.0/total)):0;
            status.Text="已下载 "+(Math.Max(0,bytes)/1048576.0).ToString("F1",CultureInfo.InvariantCulture)+" MB"+(total>0?" / "+(total/1048576.0).ToString("F1",CultureInfo.InvariantCulture)+" MB（"+percent.ToString(CultureInfo.InvariantCulture)+"%）":"");
            progress.Indeterminate=total<=0;progress.Value=percent;
        }
        internal void Finished(string text){downloading=false;handedOff=false;status.Text=text;status.ForeColor=text.StartsWith("已取消",StringComparison.Ordinal)?Theme.Muted:Theme.Error;install.Enabled=true;skip.Enabled=true;cancel.Enabled=true;progress.Indeterminate=false;progress.Value=0;}
        internal void Handoff(){handedOff=true;cancel.Enabled=false;progress.Indeterminate=false;progress.Value=100;status.ForeColor=Theme.Muted;status.Text="签名与文件校验通过，正在退出并安装…";}
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
        UpdateChecker checker;UpdateRelease available;UpdateDialog dialog;
        CancellationTokenSource checkCancel,downloadCancel;bool automaticInFlight,downloadCommitted;
        volatile bool disposed;int checking,installing;
        string status="尚未检查";
        internal string LastFailure {get;private set;}
        internal string Status {get{return available!=null?"有新版本 "+available.Version:!DeviceSettings.Current.AutoCheckUpdates&&checking==0?"自动检查已关闭":status;}}
        internal bool Available {get{return available!=null;}}
        internal UpdateController(Control control,Action refresh,Action<string,string,bool> notify){
            dispatch=control;changed=refresh;balloon=notify;data=DeviceSettings.DataDirectory;
            installer=new UpdateProcess(Application.ExecutablePath,data);http=new UpdateHttp(AppRuntime.Version);
            timer=new System.Threading.Timer(delegate{Check(true);},null,0,60000);
        }
        void Post(Action action){if(disposed)return;try{dispatch.BeginInvoke((MethodInvoker)delegate{if(!disposed)action();});}catch(InvalidOperationException){}}
        void Initialize(){
            if(checker!=null)return;
            checker=new UpdateChecker(http,Path.Combine(data,"update-state.json"),AppRuntime.Version,started);
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
                    if(checker.ErrorCode!=null)SafeDiagnostic.Transition("updates",checker.ErrorCode);
                    if(release!=null){
                        bool notify=checker.MarkNotified(release.Version);
                        Post(delegate{available=release;status="有新版本 "+release.Version;changed();if(automatic){if(notify)balloon("MyFinder 设备电量更新",status,true);}else ShowAvailable();});
                    }else if(!automatic)Post(delegate{status=checker.ErrorCode==null?"未发现新版本或检查已暂缓":"检查未完成，详情已记入诊断";changed();});
                }catch(Exception){SafeDiagnostic.Transition("updates","update_check_failed");if(!automatic)Post(delegate{status="检查未完成，详情已记入诊断";changed();});}
                finally{lock(gate){if(checkCancel==cancellation)checkCancel=null;}cancellation.Dispose();Interlocked.Exchange(ref checking,0);}
            });
        }
        internal void ConfigurationChanged(){lock(gate){if(!DeviceSettings.Current.AutoCheckUpdates&&automaticInFlight&&checkCancel!=null)checkCancel.Cancel();}changed();}
        internal void Open(){if(available!=null)ShowAvailable();else Check(false);}
        void ShowAvailable(){
            if(dialog!=null&&!dialog.IsDisposed){dialog.Activate();return;}var release=available;if(release==null)return;
            ThreadPool.QueueUserWorkItem(delegate{
                bool stable=false;try{stable=installer.IsStable(CancellationToken.None);}catch(Exception){SafeDiagnostic.Transition("updates","update_install_probe_failed");}
                bool installed=stable;
                Post(delegate{
                    if(dialog!=null&&!dialog.IsDisposed){dialog.Activate();return;}
                    var form=new UpdateDialog(release,installed);dialog=form;
                    form.SkipRequested+=delegate{
                        ThreadPool.QueueUserWorkItem(delegate{try{checker.Skip(release.Version);Post(delegate{if(available==release)available=null;status="已跳过 "+release.Version;changed();form.Close();});}catch(Exception){Post(delegate{form.Finished("无法保存跳过记录，请稍后再试。");});}});
                    };
                    form.InstallRequested+=delegate{
                        if(!installed){try{Process.Start(new ProcessStartInfo(release.PageUrl) {UseShellExecute=true});form.Close();}catch(Exception){form.Finished("无法打开发布页面，请稍后再试。");}return;}
                        BeginInstall(release,form);
                    };
                    form.CancelRequested+=delegate{lock(gate){if(downloadCancel!=null&&!downloadCommitted)downloadCancel.Cancel();}};
                    form.Show();
                });
            });
        }
        void BeginInstall(UpdateRelease release,UpdateDialog form){
            if(Interlocked.CompareExchange(ref installing,1,0)!=0)return;
            var cancellation=new CancellationTokenSource();lock(gate){downloadCancel=cancellation;downloadCommitted=false;}form.Downloading();
            ThreadPool.QueueUserWorkItem(delegate{
                try{
                    if(!checker.DownloadsAllowed(DateTime.UtcNow))throw new InvalidOperationException("GitHub 暂时限制更新请求，请稍后再试。");
                    var download=new UpdateDownload(http,installer,AppRuntime.Version,installer.InstallDirectory,Path.Combine(data,"updates"));
                    long lastProgress=0;
                    download.Install(release,cancellation.Token,delegate(long bytes,long total){long now=Stopwatch.GetTimestamp();if(now-lastProgress<Stopwatch.Frequency/5&&bytes!=total)return;lastProgress=now;Post(delegate{if(!form.IsDisposed)form.Progress(bytes,total);});},delegate{
                        if(disposed)throw new OperationCanceledException();
                        // Finish cancellation on the UI thread before starting the auxiliary process.
                        dispatch.Invoke((MethodInvoker)delegate{lock(gate){cancellation.Token.ThrowIfCancellationRequested();if(disposed||form.IsDisposed)throw new OperationCanceledException();downloadCommitted=true;form.Handoff();}});
                    });
                    installer.RequestExit();
                }catch(OperationCanceledException){Post(delegate{if(!form.IsDisposed)form.Finished("已取消更新。");});}
                catch(Exception error){
                    if(cancellation.IsCancellationRequested){Post(delegate{if(!form.IsDisposed)form.Finished("已取消更新。");});return;}
                    var rate=error as UpdateHttpFailure;if(rate!=null)try{checker.Defer(rate,DateTime.UtcNow);}catch(Exception){}
                    string message=SafeDiagnostic.Clean(error.Message);SafeDiagnostic.Transition("updates","update_install_failed: "+message);
                    Post(delegate{if(!form.IsDisposed)form.Finished("更新未完成："+message);});
                }finally{lock(gate){if(downloadCancel==cancellation)downloadCancel=null;}cancellation.Dispose();Interlocked.Exchange(ref installing,0);}
            });
        }
        public void Dispose(){disposed=true;timer.Dispose();lock(gate){if(checkCancel!=null)checkCancel.Cancel();if(downloadCancel!=null&&!downloadCommitted)downloadCancel.Cancel();}if(dialog!=null&&!dialog.IsDisposed)dialog.Shutdown();}
    }
}
