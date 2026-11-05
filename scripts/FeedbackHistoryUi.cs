using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;

namespace AtkBatteryTray {
    internal sealed class MyFeedbackDialog:ThemedDialog {
        readonly FeedbackReplies replies;readonly FeedbackReport report;readonly Action<string> open;readonly Func<string,bool> confirm;
        readonly RecordList records;readonly TextBox comments=new TextBox();readonly Label status,emptyRecords;readonly ThemedSection listCard;
        readonly ThemedButton refresh,web,supplement,delete;CancellationTokenSource pending;bool busy,loading;
        internal MyFeedbackDialog(FeedbackReplies service,FeedbackReport snapshot,bool dark,Action<string> openLink=null,long selected=0,Func<string,bool> confirmation=null):base(dark){
            replies=service;report=snapshot;open=openLink??delegate(string url){Process.Start(new ProcessStartInfo(url) {UseShellExecute=true});};
            confirm=confirmation??delegate(string question){return ThemedMessage.Show(this,"删除本地记录",question,MessageBoxButtons.YesNo,MessageBoxDefaultButton.Button2)==DialogResult.Yes;};
            Text="我的反馈";ClientSize=new Size(600,580);ShowInTaskbar=false;FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,Margin=Padding.Empty};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            for(int i=0;i<6;i++)layout.RowStyles.Add(new RowStyle(i==3?SizeType.Percent:SizeType.AutoSize,i==3?100:0));
            var title=Caption("我的反馈",true);title.Name="FeedbackHistoryTitle";title.Dock=DockStyle.Fill;layout.Controls.Add(title,0,0);
            var notice=Caption("在程序里提交的反馈和维护者回复，补充资料会公开显示在对应 issue 里。",false,true);
            notice.Name="FeedbackHistoryNotice";notice.Dock=DockStyle.Fill;notice.Margin=new Padding(0,0,0,12);layout.Controls.Add(notice,0,1);
            listCard=new ThemedSection(Theme) {Dock=DockStyle.Fill};records=new RecordList(Theme);
            records.Name="FeedbackRecords";records.AccessibleName="我的反馈列表";records.Dock=DockStyle.Fill;records.IntegralHeight=false;records.HorizontalScrollbar=true;
            records.BackColor=Theme.Card;records.ForeColor=Theme.Foreground;records.BorderStyle=BorderStyle.None;records.DisplayMember="Text";
            records.SelectedIndexChanged+=delegate{if(!loading)SelectRecord();};listCard.Controls.Add(records);
            emptyRecords=Caption("还没有在程序里提交过反馈",false,true);emptyRecords.Name="FeedbackHistoryEmpty";emptyRecords.Dock=DockStyle.Fill;emptyRecords.TextAlign=ContentAlignment.MiddleLeft;
            listCard.Controls.Add(emptyRecords);layout.Controls.Add(listCard,0,2);
            comments.Name="FeedbackComments";comments.AccessibleName="反馈回复";comments.ReadOnly=true;comments.Multiline=true;comments.WordWrap=true;comments.ScrollBars=ScrollBars.Vertical;
            var commentCard=new ThemedTextBox(Theme,comments) {Dock=DockStyle.Fill};layout.Controls.Add(commentCard,0,3);
            status=Caption("",false,true);status.Name="FeedbackHistoryStatus";status.Dock=DockStyle.Fill;layout.Controls.Add(status,0,4);
            var buttons=new FlowLayoutPanel {WrapContents=true,FlowDirection=FlowDirection.RightToLeft,Margin=Padding.Empty};
            var close=Button("CloseFeedbackHistory","关闭",delegate{Close();});close.DialogResult=DialogResult.Cancel;
            refresh=Button("RefreshFeedbackReplies","刷新",RefreshReplies);web=Button("OpenFeedbackWeb","在网页查看",OpenWeb);
            supplement=Button("SupplementFeedback","补充资料",Supplement,ThemedButtonKind.Primary);delete=Button("DeleteFeedbackRecord","删除本地记录",Delete,ThemedButtonKind.Text);
            delete.Margin=new Padding(0,0,16,0);buttons.Controls.AddRange(new Control[]{close,refresh,web,supplement});
            var buttonBar=new HistoryButtonBar(delete,buttons) {Dock=DockStyle.Fill,Margin=Padding.Empty};layout.Controls.Add(buttonBar,0,5);Controls.Add(layout);CancelButton=close;
            ConfigureLayout(layout,buttonBar,delegate(int width){return title.GetPreferredSize(new Size(width,0)).Height+title.Margin.Vertical+notice.GetPreferredSize(new Size(width,0)).Height+notice.Margin.Vertical+listCard.Height+listCard.Margin.Vertical+commentCard.Padding.Vertical+commentCard.Margin.Vertical+5*comments.Font.Height+status.GetPreferredSize(new Size(width,0)).Height+status.Margin.Vertical;});
            StartPosition=FormStartPosition.CenterParent;records.FontChanged+=delegate{SizeRecords();};records.ClientSizeChanged+=delegate{SizeRecords();};
            FormClosing+=delegate{CancelPending();};Reload(selected);
        }
        sealed class Entry {internal FeedbackRecord Record;public string Text {get{return Record.ListText;}}}
        // Measure the wrapping actions at their actual remaining width. An autosized
        // percent column instead measures them at its minimum width and leaves empty rows.
        sealed class HistoryButtonBar:Panel {
            readonly ThemedButton removal;readonly FlowLayoutPanel actions;bool arranging;
            internal HistoryButtonBar(ThemedButton removal,FlowLayoutPanel actions){this.removal=removal;this.actions=actions;Controls.Add(removal);Controls.Add(actions);AutoSize=true;AutoSizeMode=AutoSizeMode.GrowAndShrink;}
            public override Size GetPreferredSize(Size proposedSize){
                if(removal==null||actions==null)return base.GetPreferredSize(proposedSize);
                var left=removal.GetPreferredSize(Size.Empty);int gap=left.Width+removal.Margin.Horizontal;
                var right=actions.GetPreferredSize(new Size(Math.Max(1,(proposedSize.Width>0?proposedSize.Width:Width)-gap),0));
                return new Size(gap+right.Width,Math.Max(left.Height+removal.Margin.Vertical,right.Height));
            }
            protected override void OnLayout(LayoutEventArgs e){
                base.OnLayout(e);if(removal==null||actions==null||arranging)return;arranging=true;
                try{
                    var left=removal.GetPreferredSize(Size.Empty);int gap=left.Width+removal.Margin.Horizontal;
                    removal.Bounds=new Rectangle(removal.Margin.Left,removal.Margin.Top,left.Width,left.Height);
                    actions.Bounds=new Rectangle(gap,0,Math.Max(1,ClientSize.Width-gap),ClientSize.Height);
                }finally{arranging=false;}
            }
        }
        sealed class RecordList:ListBox {
            readonly DialogTheme theme;
            internal RecordList(DialogTheme theme){this.theme=theme;DrawMode=DrawMode.OwnerDrawFixed;}
            protected override void OnDrawItem(DrawItemEventArgs e){
                using(var brush=new SolidBrush(theme.Card))e.Graphics.FillRectangle(brush,e.Bounds);
                if(e.Index<0||e.Index>=Items.Count)return;
                float scale=DialogDpi.Scale(this);bool selected=(e.State&DrawItemState.Selected)!=0;
                var row=new RectangleF(e.Bounds.X+2*scale,e.Bounds.Y+2*scale,Math.Max(1,e.Bounds.Width-4*scale),Math.Max(1,e.Bounds.Height-4*scale));
                var state=e.Graphics.Save();e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
                if(selected)CompactRenderer.Fill(e.Graphics,row,theme.HighContrast?theme.Selected:theme.Interaction(theme.Card,false),VisualDesign.SelectionRadius*scale);
                var entry=(Entry)Items[e.Index];
                if(entry.Record.Unread>0)using(var brush=new SolidBrush(theme.Accent))e.Graphics.FillEllipse(brush,e.Bounds.X+8*scale,e.Bounds.Y+(e.Bounds.Height-5*scale)/2,5*scale,5*scale);
                var text=new Rectangle(e.Bounds.X+(int)Math.Round(20*scale),e.Bounds.Y,Math.Max(1,Math.Max(e.Bounds.Width,HorizontalExtent)-(int)Math.Round(28*scale)),e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics,GetItemText(Items[e.Index]),Font,text,selected&&theme.HighContrast?theme.SelectedForeground:theme.Foreground,TextFormatFlags.SingleLine|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding);
                if(Focused&&ShowFocusCues&&(e.State&DrawItemState.Focus)!=0)CompactRenderer.FocusRing(e.Graphics,row,theme.Accent,VisualDesign.SelectionRadius*scale,scale);
                e.Graphics.Restore(state);
            }
            protected override void OnGotFocus(EventArgs e){base.OnGotFocus(e);Invalidate();}
            protected override void OnLostFocus(EventArgs e){base.OnLostFocus(e);Invalidate();}
            protected override void OnChangeUICues(UICuesEventArgs e){base.OnChangeUICues(e);Invalidate();}
        }
        void SizeRecords(){
            records.ItemHeight=(int)Math.Ceiling(records.Font.Height*1.8);
            int extent=0;foreach(var item in records.Items)extent=Math.Max(extent,TextRenderer.MeasureText(records.GetItemText(item),records.Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding).Width);
            records.HorizontalExtent=extent+(int)Math.Round(28*DialogDpi.Scale(records));
            int height=Math.Max(2,Math.Min(6,records.Items.Count))*records.ItemHeight;
            if(records.HorizontalExtent>records.ClientSize.Width)height+=SystemInformation.HorizontalScrollBarHeight;
            listCard.Height=height+listCard.Padding.Vertical;
        }
        ThemedButton Button(string name,string text,Action action,ThemedButtonKind kind=ThemedButtonKind.Secondary){var button=new ThemedButton(Theme,kind) {Name=name,Text=text,AutoSize=true};button.Click+=delegate{action();};return button;}
        FeedbackRecord Selected {get{var entry=records.SelectedItem as Entry;return entry==null?null:replies.Store.Find(entry.Record.Number);}}
        internal void Reload(long selected=0){
            var current=Selected;if(selected==0&&current!=null)selected=current.Number;
            loading=true;records.BeginUpdate();records.Items.Clear();
            var saved=replies.Store.Snapshot().Records;saved.Sort(delegate(FeedbackRecord a,FeedbackRecord b){return b.CreatedAt.CompareTo(a.CreatedAt);});
            int index=-1;foreach(var record in saved)if(record.Number>0){if(record.Number==selected)index=records.Items.Count;records.Items.Add(new Entry {Record=record});}
            records.SelectedIndex=index>=0?index:records.Items.Count>0?0:-1;records.EndUpdate();loading=false;
            emptyRecords.Visible=records.Items.Count==0;if(emptyRecords.Visible)emptyRecords.BringToFront();SizeRecords();SelectRecord();
        }
        void SelectRecord(){
            var record=Selected;comments.Text=record==null?"还没有在程序里提交过反馈":FeedbackText.Comments(record);
            if(record!=null&&record.Unread>0)try{
                replies.Store.MarkRead(record.Number);var entry=(Entry)records.SelectedItem;entry.Record=replies.Store.Find(record.Number);
                loading=true;int index=records.SelectedIndex;records.Items[index]=entry;records.SelectedIndex=index;loading=false;
            }catch(Exception){loading=false;status.Text="未能保存已读状态，请稍后重试。";}
            Buttons();
        }
        void Buttons(){var record=Selected;refresh.Enabled=!busy&&records.Items.Count>0;web.Enabled=record!=null;delete.Enabled=!busy&&record!=null;supplement.Enabled=!busy&&record!=null&&record.MaySupplement;}
        void OpenWeb(){var record=Selected;if(record==null)return;try{open(FeedbackClient.IssueUrl(record.Number));}catch(Exception){status.Text="无法打开浏览器，请稍后重试。";}}
        void Delete(){var record=Selected;if(record==null||busy||!confirm("仅删除此电脑上的反馈记录和补充凭证，不会删除 GitHub 上的 issue。确定删除吗？"))return;
            try{replies.Store.Delete(record.Number);status.Text="本地记录已删除。";Reload();}catch(Exception){status.Text="无法删除本地记录，请稍后重试。";}
        }
        void Supplement(){var record=Selected;if(record==null||!record.MaySupplement||busy)return;using(var dialog=new FeedbackSupplementDialog(replies,record.Number,report,Theme.Dark))dialog.ShowDialog(this);Reload(record.Number);}
        void RefreshReplies(){
            if(busy)return;busy=true;Buttons();status.Text="正在检查回复…";var cancellation=new CancellationTokenSource();pending=cancellation;var token=cancellation.Token;
            ThreadPool.QueueUserWorkItem(delegate{
                try{var result=replies.Poll(false,null,token);if(!token.IsCancellationRequested)try{BeginInvoke((MethodInvoker)delegate{if(IsDisposed||Disposing||token.IsCancellationRequested)return;pending=null;busy=false;status.Text=result.Message;Reload();});}catch(InvalidOperationException){}}
                catch(OperationCanceledException){}finally{cancellation.Dispose();}
            });
        }
        void CancelPending(){if(pending!=null)try{pending.Cancel();}catch(ObjectDisposedException){}}
        protected override void Dispose(bool disposing){if(disposing)CancelPending();base.Dispose(disposing);}
    }
    internal sealed class FeedbackSupplementDialog:ThemedDialog {
        readonly FeedbackReplies replies;readonly long number;readonly FeedbackReport report;
        readonly TextBox description=new TextBox();readonly CheckBox diagnostics;readonly LinkLabel preview;readonly Label remaining,status;readonly ThemedButton submit;
        CancellationTokenSource pending;bool busy,success;
        internal FeedbackSupplementDialog(FeedbackReplies service,long issue,FeedbackReport snapshot,bool dark):base(dark){
            replies=service;number=issue;report=snapshot;Text="补充资料 · #"+number;ClientSize=new Size(540,440);ShowInTaskbar=false;
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,Margin=Padding.Empty};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            for(int i=0;i<5;i++)layout.RowStyles.Add(new RowStyle(i==0?SizeType.Percent:SizeType.AutoSize,i==0?100:0));
            var content=new ThemedSection(Theme) {Dock=DockStyle.Fill};var contentLayout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Margin=Padding.Empty};contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));contentLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            contentLayout.Controls.Add(Caption("描述（必填，5–2000 字）",false,false,true),0,0);
            description.Name="SupplementDescription";description.AccessibleName="补充描述";description.Multiline=true;description.AcceptsReturn=true;description.TextChanged+=delegate{ValidateInput();};
            var textCard=new ThemedTextBox(Theme,description) {Dock=DockStyle.Fill};contentLayout.Controls.Add(textCard,0,1);
            remaining=Caption("",false,true);remaining.Name="SupplementRemaining";contentLayout.Controls.Add(remaining,0,2);content.Controls.Add(contentLayout);layout.Controls.Add(content,0,0);
            var diagnosticCard=new ThemedSection(Theme) {Dock=DockStyle.Fill,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink};var row=new FlowLayoutPanel {Dock=DockStyle.Top,AutoSize=true,WrapContents=true,Margin=Padding.Empty};
            diagnostics=new ThemedSwitch(Theme) {Name="IncludeDiagnostics",Text="附带诊断信息（推荐）",Checked=true,Margin=new Padding(0,0,12,0)};
            preview=new LinkLabel {Name="ViewDiagnostics",Text="查看内容",AutoSize=true,LinkColor=Theme.Accent,BackColor=Theme.Card,Margin=new Padding(0,4,0,3)};
            preview.LinkClicked+=delegate{var record=replies.Store.Find(number);string text=record!=null&&record.Pending!=null?record.Pending.Diagnostics??"未附带诊断信息。":report.FullText;using(var dialog=FeedbackForm.CreateDiagnosticsDialog(Theme.Dark,text))dialog.ShowDialog(this);};
            row.Controls.AddRange(new Control[]{diagnostics,preview});diagnosticCard.Controls.Add(row);layout.Controls.Add(diagnosticCard,0,1);
            var warning=Caption("补充内容会公开显示在 GitHub，请勿填写密码、账号等个人信息。",false,true);warning.Name="SupplementNotice";warning.Dock=DockStyle.Fill;layout.Controls.Add(warning,0,2);
            status=Caption("",false,true);status.Name="SupplementStatus";status.Dock=DockStyle.Fill;layout.Controls.Add(status,0,3);
            var buttons=new FlowLayoutPanel {Dock=DockStyle.Fill,AutoSize=true,WrapContents=true,FlowDirection=FlowDirection.RightToLeft,Margin=Padding.Empty};
            submit=new ThemedButton(Theme,ThemedButtonKind.Primary) {Name="SubmitSupplement",Text="提交",AutoSize=true};submit.Click+=delegate{Submit();};
            var cancel=new ThemedButton(Theme) {Text="关闭",AutoSize=true,DialogResult=DialogResult.Cancel};buttons.Controls.AddRange(new Control[]{submit,cancel});layout.Controls.Add(buttons,0,4);Controls.Add(layout);AcceptButton=submit;CancelButton=cancel;
            ConfigureLayout(layout,buttons,delegate(int width){return content.Padding.Vertical+content.Margin.Vertical+textCard.Padding.Vertical+5*description.Font.Height+remaining.Height+diagnosticCard.GetPreferredSize(new Size(width,0)).Height+diagnosticCard.Margin.Vertical+warning.GetPreferredSize(new Size(width,0)).Height+warning.Margin.Vertical+status.GetPreferredSize(new Size(width,0)).Height+status.Margin.Vertical+20;});
            FormClosing+=delegate{CancelPending();};RestorePending();
        }
        void RestorePending(){var record=replies.Store.Find(number);if(record!=null&&record.Pending!=null){description.Text=record.Pending.Description;diagnostics.Checked=record.Pending.Diagnostics!=null;status.Text="有待确认的补充，重试将使用原内容。";}ValidateInput();}
        void ValidateInput(){
            if(submit==null||remaining==null)return;int length=description.Text.Trim().Length;
            remaining.Text=length<5?"还需输入 "+(5-length)+" 字；还可输入 "+(2000-length)+" 字":length>2000?"已超出 "+(length-2000)+" 字（最多 2000 字）":"还可输入 "+(2000-length)+" 字";
            var record=replies.Store.Find(number);bool saved=record!=null&&record.Pending!=null;
            description.ReadOnly=busy||saved||success;diagnostics.Enabled=!busy&&!saved&&!success;preview.Enabled=!busy;
            submit.Text=success?"关闭":saved?"重试确认":"提交";submit.Enabled=!busy&&(success||record!=null&&record.MaySupplement&&(saved||FeedbackInput.Valid(record.Category,description.Text)));
        }
        void Submit(){
            if(success){Close();return;}if(busy||!submit.Enabled)return;
            string text=description.Text,diagnosticText=diagnostics.Checked?report.FullText:null;busy=true;ValidateInput();status.Text="正在提交…";
            var cancellation=new CancellationTokenSource();pending=cancellation;var token=cancellation.Token;
            ThreadPool.QueueUserWorkItem(delegate{
                try{var result=replies.Supplement(number,text,diagnosticText,token);if(!token.IsCancellationRequested)try{BeginInvoke((MethodInvoker)delegate{if(IsDisposed||Disposing||token.IsCancellationRequested)return;pending=null;busy=false;success=result.Kind==SupplementKind.Success;status.Text=result.Message;ValidateInput();});}catch(InvalidOperationException){}}
                catch(OperationCanceledException){}finally{cancellation.Dispose();}
            });
        }
        void CancelPending(){if(pending!=null)try{pending.Cancel();}catch(ObjectDisposedException){}}
        protected override void Dispose(bool disposing){if(disposing)CancelPending();base.Dispose(disposing);}
    }
    internal sealed class FeedbackReplyController:IDisposable {
        readonly FeedbackReplies replies;readonly Control dispatch;readonly Action<FeedbackBalloon> notify;
        readonly System.Threading.Timer timer;readonly object gate=new object();CancellationTokenSource pending;bool disposed;
        internal FeedbackReplyController(FeedbackReplies service,Control control,Action<FeedbackBalloon> balloon){
            replies=service;dispatch=control;notify=balloon;replies.BalloonReady+=OnBalloon;
            timer=new System.Threading.Timer(delegate{Check();},null,60000,60000);
        }
        void OnBalloon(FeedbackBalloon balloon){try{dispatch.BeginInvoke((MethodInvoker)delegate{lock(gate){if(!disposed)notify(balloon);}});}catch(InvalidOperationException){}}
        void Check(){
            CancellationTokenSource cancel;lock(gate){if(disposed||pending!=null||!DeviceSettings.Current.CheckFeedbackReplies||DeviceSettings.UsingDefaults)return;cancel=new CancellationTokenSource();pending=cancel;}
            try{replies.Poll(true,null,cancel.Token);}catch(OperationCanceledException){}
            finally{lock(gate){if(pending==cancel)pending=null;}cancel.Dispose();}
        }
        internal void ConfigurationChanged(){lock(gate){if((!DeviceSettings.Current.CheckFeedbackReplies||DeviceSettings.UsingDefaults)&&pending!=null)pending.Cancel();}}
        public void Dispose(){lock(gate){disposed=true;timer.Dispose();replies.BalloonReady-=OnBalloon;if(pending!=null)pending.Cancel();}}
    }
    internal enum TrayBalloonKind {LowBattery,Update,Feedback}
    internal sealed class TrayBalloonRoute {
        internal TrayBalloonKind Kind;internal long Number;
        internal void Set(TrayBalloonKind kind,long number=0){Kind=kind;Number=number;}
        internal void Click(Action update,Action lowBattery,Action<long> feedback){var kind=Kind;long number=Number;Set(TrayBalloonKind.LowBattery);if(kind==TrayBalloonKind.Update)update();else if(kind==TrayBalloonKind.Feedback)feedback(number);else lowBattery();}
    }
}
