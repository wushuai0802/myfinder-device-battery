using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;

namespace AtkBatteryTray {
    internal sealed class UpdateDialog:Form {
        readonly Button install=new Button(),skip=new Button(),cancel=new Button();
        readonly Label status=new Label();readonly ProgressBar progress=new ProgressBar();
        bool downloading,handedOff;
        internal event Action InstallRequested,SkipRequested,CancelRequested;
        internal UpdateDialog(UpdateRelease release,bool stable){
            Text="MyFinder 设备电量 · 软件更新";StartPosition=FormStartPosition.CenterScreen;ShowInTaskbar=true;
            FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;AutoScaleMode=AutoScaleMode.Dpi;Font=new Font("Microsoft YaHei UI",9);
            ClientSize=new Size(520,430);Padding=new Padding(16);
            var title=new Label {Text="有新版本 "+release.Version,Location=new Point(16,16),Size=new Size(488,28),Font=new Font(Font,FontStyle.Bold)};
            var notes=new TextBox {Name="ReleaseNotes",Text=UpdateText.Notes(release.Body),ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical,WordWrap=true,Location=new Point(16,50),Size=new Size(488,235),TabStop=true};
            status.Location=new Point(16,294);status.Size=new Size(488,56);status.Text=stable?"点击“更新并重启”后才会下载，完成签名与文件校验后安装。":"当前为绿色模式。打开发布页面下载；安装到稳定目录后可以一键更新。";
            progress.Location=new Point(16,354);progress.Size=new Size(488,15);
            install.Text=stable?"更新并重启":"打开发布页面";install.Location=new Point(326,389);install.Size=new Size(178,28);
            skip.Text="跳过此版本";skip.Location=new Point(16,389);skip.Size=new Size(120,28);
            cancel.Text="取消";cancel.Location=new Point(232,389);cancel.Size=new Size(86,28);
            Controls.AddRange(new Control[]{title,notes,status,progress,install,skip,cancel});CancelButton=cancel;
            install.Click+=delegate{if(InstallRequested!=null)InstallRequested();};
            skip.Click+=delegate{if(SkipRequested!=null)SkipRequested();};
            cancel.Click+=delegate{if(downloading){if(CancelRequested!=null)CancelRequested();status.Text="正在取消…";cancel.Enabled=false;}else Close();};
            FormClosing+=delegate(object sender,FormClosingEventArgs e){if(downloading&&!handedOff){e.Cancel=true;if(CancelRequested!=null)CancelRequested();status.Text="正在取消…";}};
        }
        internal void Downloading(){downloading=true;handedOff=false;install.Enabled=false;skip.Enabled=false;cancel.Enabled=true;status.Text="正在下载和校验…";progress.Style=ProgressBarStyle.Marquee;}
        internal void Progress(long bytes,long total){if(IsDisposed)return;status.Text="正在下载："+(bytes/1024)+" KB"+(total>0?" / "+(total/1024)+" KB":"");if(total>0){progress.Style=ProgressBarStyle.Continuous;progress.Value=(int)Math.Min(100,bytes*100/total);}}
        internal void Finished(string text){downloading=false;handedOff=false;status.Text=text;install.Enabled=true;skip.Enabled=true;cancel.Enabled=true;progress.Style=ProgressBarStyle.Continuous;progress.Value=0;}
        internal void Handoff(){handedOff=true;cancel.Enabled=false;status.Text="校验完成，正在等待程序正常退出并安装…";}
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
