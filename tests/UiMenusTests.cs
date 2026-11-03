using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

// Hidden windows, fake readers and injected clipboard/browser/configuration only.
// All files live next to the test executable, which must be built under TEMP.
static class UiMenusTests {
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    const string SupportUrl="https://github.com/wushuai0802/myfinder-device-battery/blob/main/docs/compatibility.md";
    static int checks,failures;
    static object Field(object target,string name){return target.GetType().GetField(name,Private).GetValue(target);}
    static void Set(object target,string name,object value){target.GetType().GetField(name,Private).SetValue(target,value);}
    static object Call(object target,string name,params object[] args){return target.GetType().GetMethod(name,Private|BindingFlags.Public).Invoke(target,args);}
    static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
    static void Run(string name,Action action){try{action();Console.WriteLine("PASS "+name);}catch(Exception error){failures++;Console.Error.WriteLine("FAIL "+name+": "+(error.InnerException??error).Message);}}
    static void Pump(Func<bool> ready){DateTime until=DateTime.UtcNow.AddSeconds(5);while(!ready()&&DateTime.UtcNow<until){Application.DoEvents();Thread.Sleep(2);}Check(ready(),"fake readers settle");}
    static WirelessDevice Device(string id,string name,string kind="mouse"){
        return new WirelessDevice {Id=id,Name=name,Kind=kind,Percent=65,Online=true,Fresh=true,ConnectionState="connected",MeasurementState="current",ConfirmedAtUtc=DateTime.UtcNow.ToString("o")};
    }
    static List<PopupControl> Rows(TrayContext owner,PopupPage page,int action=210){return PopupPages.Build(owner,new PopupNavigation {Page=page,PreferenceAction=action,VisibleChoices=100},false);}
    static string Texts(List<PopupControl> rows){return String.Join("|",rows.FindAll(delegate(PopupControl row){return row.Id!=6;}).ConvertAll(delegate(PopupControl row){return row.Text;}).ToArray());}
    static int Selected(List<PopupControl> rows){return rows.FindAll(delegate(PopupControl row){return row.Selected;}).Count;}
    static PopupPage MissingPage(){PopupPage page;Check(Enum.TryParse("DeviceMissing",out page),"a dedicated device-missing page exists");return page;}
    sealed class Fixture:IDisposable {
        internal readonly TrayContext Owner;
        internal readonly Details Popup;
        internal readonly PopupNavigation Navigation;
        internal int Reads,Saves;
        internal string Copied,Opened;
        internal Fixture(){
            DeviceSettings.UseTransientConfiguration(new AudioBindings {RememberReadings=false,ReducedMotion=true,VolumeCollapsed=false});
            Owner=new TrayContext(false,delegate(bool battery){Interlocked.Increment(ref Reads);return new MouseSnapshot();},delegate(int stage){throw new Exception("No DPI writes");},null,null,false,
                null,null,null,delegate{return false;},null,null,null,null,delegate(AudioBindings settings){Saves++;DeviceSettings.UseTransientConfiguration(settings);});
            Pump(delegate{return !Owner.IsRefreshing;});((System.Windows.Forms.Timer)Field(Owner,"refresh")).Stop();Owner.GenericReady=true;Owner.NativeMousePresent=false;
            var constructor=typeof(Details).GetConstructor(new[]{typeof(TrayContext),typeof(Action<string>),typeof(Action<string>)});
            Popup=constructor==null?new Details(Owner):(Details)constructor.Invoke(new object[]{Owner,new Action<string>(delegate(string text){Copied=text;}),new Action<string>(delegate(string url){Opened=url;})});
            Set(Owner,"details",Popup);Navigation=(PopupNavigation)Field(Popup,"navigation");
        }
        internal List<PopupControl> Controls(){return (List<PopupControl>)Call(Popup,"ControlsForPage");}
        internal void Page(PopupPage page){Navigation.Page=page;Navigation.Offset=0;Call(Popup,"UpdateLayoutSize");}
        internal PopupControl Find(string text){
            for(int attempt=0;attempt<50;attempt++){
                var rows=Controls();var row=rows.Find(delegate(PopupControl item){return item.Text==text;});if(row!=null)return row;
                var next=rows.Find(delegate(PopupControl item){return item.Id==8;});if(next==null)break;Activate(next);
            }
            throw new Exception("unreachable row: "+text);
        }
        internal PopupControl ById(int id){var row=Controls().Find(delegate(PopupControl item){return item.Id==id;});Check(row!=null,"visible action "+id);return row;}
        internal void Activate(PopupControl row){Call(Popup,"PerformAction",row);}
        internal void Key(Keys key){Call(Popup,"ProcessCmdKey",Message.Create(IntPtr.Zero,0,IntPtr.Zero,IntPtr.Zero),key);}
        internal void Click(PopupControl row){
            float scale=(float)Field(Popup,"dpiScale");var point=new MouseEventArgs(MouseButtons.Left,1,(int)((row.Rect.X+row.Rect.Width/2)*scale),(int)((row.Rect.Y+row.Rect.Height/2)*scale),0);
            typeof(Control).GetMethod("OnMouseDown",Private).Invoke(Popup,new object[]{point});typeof(Control).GetMethod("OnMouseUp",Private).Invoke(Popup,new object[]{point});
        }
        internal void Update(bool available){
            if(!available){Set(Owner,"updates",null);return;}
            var controller=(UpdateController)FormatterServices.GetUninitializedObject(typeof(UpdateController));
            Set(controller,"available",new UpdateRelease {Tag="v9.8.7"});Set(Owner,"updates",controller);
        }
        public void Dispose(){Set(Owner,"updates",null);Owner.ExitThread();if(!Popup.IsDisposed)Popup.Dispose();Owner.Dispose();}
    }
    static void MoreRows(){
        using(var f=new Fixture()){
            const string expected="音源与麦克风|显示与提醒|设备管理|设备没显示？|状态与诊断|退出";
            Check(Texts(Rows(f.Owner,PopupPage.More))==expected,"more options contains exactly the six requested rows without an update");
            f.Update(true);var rows=Rows(f.Owner,PopupPage.More);
            Check(Texts(rows)=="有新版本 9.8.7|"+expected&&rows[1].Id==260,"available update is first and keeps action 260");
            f.Update(false);Check(Texts(Rows(f.Owner,PopupPage.More))==expected,"the update row disappears when no release is available");
            Check(Rows(f.Owner,PopupPage.Main).Exists(delegate(PopupControl row){return row.Id==0&&row.Enabled;}),"sync-all remains on the main footer");
            Check(Rows(f.Owner,PopupPage.Diagnostics).Exists(delegate(PopupControl row){return row.Id==261&&row.Text=="检查更新";}),"manual update checking remains in diagnostics");
        }
    }
    static void PreferenceOrder(){
        using(var f=new Fixture())Check(Texts(Rows(f.Owner,PopupPage.Preferences))=="外观|托盘显示|减弱动效|低电量提醒|提醒阈值|后台同步间隔|跨重启保留读数|清除历史读数|自动检查更新","preferences retain their actions in the requested order");
    }
    static void HelpActions(){
        using(var f=new Fixture()){
            var page=MissingPage();f.Page(PopupPage.More);var entry=f.Find("设备没显示？");Call(f.Popup,"FocusControl",entry);f.Key(Keys.Enter);
            Check(f.Navigation.Page==page&&PopupPages.Title(f.Navigation)=="设备没显示？","keyboard entry opens the titled help page");
            var rows=Rows(f.Owner,page);
            Check(Texts(rows)=="请确认接收器已插好，设备已开机并唤醒|重新识别设备|查看支持清单|复制诊断摘要|反馈到 GitHub|反馈不代表一定能适配","help rows use the exact copy and order");
            var feedback=rows.Find(delegate(PopupControl row){return row.Text=="反馈到 GitHub";});
            Check(feedback.Id==258&&feedback.Detail=="提交前可检查内容\n提交需要 GitHub 账号","help shares feedback action 258 and its review/account description");
            Check(rows[1].Information&&rows[6].Information,"help explanations are static text");
            int reads=f.Reads;f.Activate(f.Find("重新识别设备"));Pump(delegate{return !f.Owner.IsRefreshing;});
            Check(f.Reads>reads&&f.Find("重新识别设备").Id==0,"help rediscovery routes through the existing sync-all action");
            f.Activate(f.Find("复制诊断摘要"));Check(f.Copied==f.Owner.DiagnosticSummary()&&f.Find("复制诊断摘要").Id==250,"help copy routes to the same diagnostic summary");
            f.Activate(f.Find("查看支持清单"));Check(f.Opened==SupportUrl,"support action opens only the fixed compatibility URL");
            f.Activate(feedback);Check(f.Opened==f.Owner.CreateFeedbackReport().IssueUrl&&f.Copied==f.Owner.CreateFeedbackReport().FullText,"help feedback uses the existing sanitized report and browser workflow");
            f.Key(Keys.Escape);Check(f.Navigation.Page==PopupPage.More&&(string)Field(f.Popup,"keyboardFocusKey")==entry.StableKey,"Escape returns focus to the originating more-options row");
            f.Page(PopupPage.Diagnostics);f.Activate(f.Find("复制诊断摘要"));Check(f.Copied==f.Owner.DiagnosticSummary(),"original diagnostic copy remains reachable");
            f.Activate(f.Find("反馈到 GitHub"));Check(f.Opened==f.Owner.CreateFeedbackReport().IssueUrl,"original diagnostic feedback remains reachable");
            f.Page(PopupPage.Devices);rows=Rows(f.Owner,PopupPage.Devices);Check(rows[rows.Count-1].Text=="设备没显示？","help entry is the last management row");
            f.Activate(f.Find("设备没显示？"));Check(f.Navigation.Page==page,"device management routes to the same help page");f.Activate(f.ById(6));Check(f.Navigation.Page==PopupPage.Devices,"help back returns to management");
        }
    }
    static void EmptyCard(){
        using(var f=new Fixture()){
            var layout=WirelessLayout.Build(f.Owner,0,1000);Check(layout.All[0].Id=="empty"&&layout.All[0].Subtitle=="点这里查看原因","empty card shows the requested reason hint");
            var rows=f.Controls();var card=rows.Find(delegate(PopupControl row){return row.DeviceKey=="empty"&&row.Enabled;});Check(card!=null,"empty card has an enabled hit target");
            Check(PopupPages.Hit(rows,new PointF(220,layout.All[0].Top+30))==card,"the empty card, including its meter area, opens help");
            f.Click(card);Check(f.Navigation.Page==MissingPage(),"pointer click opens help from an empty card");f.Key(Keys.Escape);Check(f.Navigation.Page==PopupPage.Main,"empty-card help returns to the main page");
            Call(f.Popup,"FocusControl",card);f.Key(Keys.Space);Check(f.Navigation.Page==MissingPage(),"keyboard space opens empty-card help");f.Key(Keys.Escape);
            var handle=f.Popup.Handle;var tree=(AccessibleObject)Call(f.Popup,"CreateAccessibilityInstance");rows=f.Controls();int index=rows.FindIndex(delegate(PopupControl row){return row.StableKey==card.StableKey;});
            Check(tree.GetChild(index).Name=="设备没显示？"&&tree.GetChild(index).Description=="点这里查看原因","empty card exposes useful screen-reader text");tree.GetChild(index).DoDefaultAction();Check(f.Navigation.Page==MissingPage(),"screen-reader default action opens empty-card help");f.Key(Keys.Escape);
            Set(f.Owner,"genericBusy",1);layout=WirelessLayout.Build(f.Owner,0,1000);Check(layout.All[0].Subtitle=="正在识别设备…","scanning retains its existing hint");
            rows=f.Controls();Check(!rows.Exists(delegate(PopupControl row){return row.DeviceKey=="empty"&&row.Enabled;}),"scanning card is unavailable to pointer and keyboard");
            tree.GetChild(index).DoDefaultAction();Check(f.Navigation.Page==PopupPage.Main,"an existing accessible child cannot open help after scanning starts");
            f.Activate(card);Check(f.Navigation.Page==PopupPage.Main,"router also rejects a stale empty-card action during scanning");
        }
    }
    static void HiddenDetails(){
        using(var f=new Fixture())foreach(string id in new[]{"native:mouse","native:headset","hidden-generic"}){
            var pref=DeviceSettings.Current;pref.HiddenDeviceIds.Clear();pref.HiddenDeviceIds.Add(id);DeviceSettings.UseTransientConfiguration(pref);
            f.Owner.WirelessDevices=id=="hidden-generic"?new[]{Device(id,"隐藏鼠标")}:new WirelessDevice[0];f.Page(PopupPage.Devices);
            var managed=f.Controls().Find(delegate(PopupControl row){return row.DeviceKey==id&&row.ActionKey=="manage";});Check(managed!=null&&managed.Enabled,"hidden device stays reachable in management: "+id);f.Activate(managed);
            f.Activate(f.Find("设备详情"));Check(f.Navigation.Page==(id=="native:mouse"?PopupPage.Mouse:id=="native:headset"?PopupPage.Headset:PopupPage.Device),"management opens the original hidden-device details: "+id);
        }
    }
    static void TrayRulesAndPins(){
        using(var f=new Fixture()){
            f.Owner.NativeMousePresent=true;f.Owner.WirelessDevices=new[]{Device("mouse-a","鼠标 A"),Device("keyboard-b","键盘 B","keyboard"),Device("hidden-c","隐藏设备")};
            var pref=DeviceSettings.Current;pref.HiddenDeviceIds.Add("hidden-c");pref.TraySource="active";DeviceSettings.UseTransientConfiguration(pref);
            var rows=Rows(f.Owner,PopupPage.ChoosePreference,211);
            Check(Texts(rows)=="当前使用的设备|最低电量|自动选择有数据的设备|鼠标优先|耳机优先|键盘优先|固定：ATK A9 Air+|固定：鼠标 A|固定：键盘 B","tray chooser orders the six rules before current visible devices");
            Check(Selected(rows)==1&&rows[1].Id==415&&rows[2].Id==414&&rows[3].Id==410,"rule IDs and selection preserve the old configuration mapping");
            var ids=new HashSet<int>();foreach(var row in rows)Check(row.Id<0||ids.Add(row.Id),"tray choice IDs do not collide");
            int[] ruleIds={415,414,410,411,412,413};string[] sources={"active","lowest","auto","mouse","headset","keyboard"};
            for(int i=0;i<ruleIds.Length;i++){
                pref=DeviceSettings.Current;pref.TrayDeviceId="mouse-a";DeviceSettings.UseTransientConfiguration(pref);f.Page(PopupPage.Preferences);f.Activate(f.ById(211));
                var rule=f.Find(new[]{"当前使用的设备","最低电量","自动选择有数据的设备","鼠标优先","耳机优先","键盘优先"}[i]);Check(rule.Id==ruleIds[i],"rule retains action ID "+sources[i]);f.Activate(rule);
                Check(DeviceSettings.Current.TraySource==sources[i]&&DeviceSettings.Current.TrayDeviceId==null,"choosing a rule clears the fixed device: "+sources[i]);
                rows=Rows(f.Owner,PopupPage.ChoosePreference,211);Check(Selected(rows)==1&&rows.Find(delegate(PopupControl row){return row.Selected;}).Id==ruleIds[i],"one selected rule: "+sources[i]);
            }
            f.Page(PopupPage.Preferences);f.Activate(f.ById(211));var pin=f.Find("固定：鼠标 A");f.Activate(pin);
            Check(DeviceSettings.Current.TrayDeviceId=="mouse-a"&&f.Navigation.Page==PopupPage.Preferences,"fixed choice writes the existing TrayDeviceId and returns");
            rows=Rows(f.Owner,PopupPage.ChoosePreference,211);Check(Selected(rows)==1&&rows.Find(delegate(PopupControl row){return row.Selected;}).DeviceKey=="mouse-a","a fixed device excludes every rule selection");
            Check(Rows(f.Owner,PopupPage.Preferences).Find(delegate(PopupControl row){return row.Id==211;}).Value=="固定：鼠标 A","preferences show the fixed device's name");
            pref=DeviceSettings.Current;pref.DeviceAliases["mouse-a"]="新别名";DeviceSettings.UseTransientConfiguration(pref);Check(Rows(f.Owner,PopupPage.Preferences).Find(delegate(PopupControl row){return row.Id==211;}).Value=="固定：新别名","fixed value follows the existing alias");
            f.Navigation.SelectedDeviceKey="mouse-a";f.Navigation.SelectedDeviceName="新别名";f.Page(PopupPage.Device);var toggle=f.Find("固定为托盘主设备");Check(toggle.Checked,"device detail reflects the tray chooser pin");f.Activate(toggle);
            Check(DeviceSettings.Current.TrayDeviceId==null&&Selected(Rows(f.Owner,PopupPage.ChoosePreference,211))==1,"detail unpin restores the rule selection");f.Activate(f.Find("固定为托盘主设备"));
            Check(DeviceSettings.Current.TrayDeviceId=="mouse-a"&&Rows(f.Owner,PopupPage.ChoosePreference,211).Exists(delegate(PopupControl row){return row.Selected&&row.DeviceKey=="mouse-a";}),"detail pin updates the same tray chooser setting");
            f.Owner.WirelessDevices=new WirelessDevice[0];rows=Rows(f.Owner,PopupPage.ChoosePreference,211);
            Check(Selected(rows)==1&&rows.Exists(delegate(PopupControl row){return row.Selected&&row.Text=="固定：已断开的设备";}),"a disconnected fixed device has one explicit selected row");
            Check(DeviceSettings.Current.TrayDeviceId=="mouse-a","building a disconnected chooser never changes configuration");
            pref=DeviceSettings.Current;pref.HiddenDeviceIds.Add("native:mouse");pref.TrayDeviceId="native:mouse";DeviceSettings.UseTransientConfiguration(pref);rows=Rows(f.Owner,PopupPage.ChoosePreference,211);
            Check(Selected(rows)==1&&rows.Exists(delegate(PopupControl row){return row.Selected&&row.Text=="固定：已断开的设备";}),"hidden fixed device is preserved even though it is absent from the choices");
            Check(f.Saves==9,"rule and both pin entry points all use the injected settings writer");
        }
    }
    static void LegacyVolumeDefaults(){
        string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ui-menu-settings-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);string path=Path.Combine(root,"settings.json");
        var fresh=DeviceSettings.LoadBindings(path);Check(fresh.VolumeCollapsed&&fresh.TraySource=="active","missing configuration defaults to collapsed volume and active tray");
        using(var f=new Fixture()){
            DeviceSettings.UseTransientConfiguration(fresh);var rows=f.Controls();Check(!rows.Exists(delegate(PopupControl row){return row.Id==240;})&&rows.Exists(delegate(PopupControl row){return row.Id==241&&row.Text=="展开系统音量";}),"a new installation actually starts with the main volume slider collapsed");
        }
        File.WriteAllText(path,"{\"SchemaVersion\":1}");var old=DeviceSettings.LoadBindings(path);Check(!old.VolumeCollapsed&&old.TraySource=="mouse","old omitted fields keep expanded volume and mouse tray");
        foreach(string source in new[]{"active","auto","mouse","headset","keyboard","lowest"}){
            File.WriteAllText(path,"{\"SchemaVersion\":1,\"TraySource\":\""+source+"\"}");var loaded=DeviceSettings.LoadBindings(path);Check(!loaded.VolumeCollapsed&&loaded.TraySource==source,"legacy configuration without VolumeCollapsed keeps expanded volume: "+source);
        }
        foreach(bool collapsed in new[]{false,true})foreach(string source in new[]{"active","auto","mouse","headset","keyboard","lowest"}){
            File.WriteAllText(path,"{\"SchemaVersion\":1,\"VolumeCollapsed\":"+(collapsed?"true":"false")+",\"TraySource\":\""+source+"\",\"TrayDeviceId\":\"offline-pin\"}");var loaded=DeviceSettings.LoadBindings(path);
            Check(loaded.VolumeCollapsed==collapsed&&loaded.TraySource==source&&loaded.TrayDeviceId=="offline-pin","legacy explicit values survive: "+source+"/"+collapsed);DeviceSettings.SaveBindings(path,loaded);loaded=DeviceSettings.LoadBindings(path);
            Check(loaded.SchemaVersion==1&&loaded.Clone().VolumeCollapsed==collapsed&&loaded.TraySource==source&&loaded.TrayDeviceId=="offline-pin","configuration format and explicit values round-trip unchanged");
        }
        typeof(DeviceSettings).GetMethod("UseDefaults",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
        Check(!DeviceSettings.Current.VolumeCollapsed,"an unreadable existing configuration retains the prior expanded fallback");DeviceSettings.UseTransientConfiguration(old);
    }
    static void NativePins(){
        using(var f=new Fixture()){
            f.Owner.NativeMousePresent=true;f.Owner.Headset=new HeadsetReading {Connected=true,ConnectionVerified=true,ReceiverPresent=true,Percent=80,BatteryTimestampUtc=DateTime.UtcNow};
            foreach(string id in new[]{"native:mouse","native:headset"}){
                string name=id=="native:mouse"?DeviceSettings.Current.MouseAlias:DeviceSettings.Current.HeadsetAlias;
                f.Page(PopupPage.Preferences);f.Activate(f.ById(211));f.Activate(f.Find("固定："+name));f.Page(id=="native:mouse"?PopupPage.Mouse:PopupPage.Headset);
                var toggle=f.Find("固定为托盘主设备");Check(toggle.Checked&&toggle.DeviceKey==id,"native detail reflects the chooser pin: "+id);f.Activate(toggle);
                Check(DeviceSettings.Current.TrayDeviceId==null&&Selected(Rows(f.Owner,PopupPage.ChoosePreference,211))==1,"native detail unpin restores exactly one rule: "+id);f.Activate(f.Find("固定为托盘主设备"));
                Check(Rows(f.Owner,PopupPage.ChoosePreference,211).Exists(delegate(PopupControl row){return row.Selected&&row.DeviceKey==id;}),"native detail pin returns to the chooser: "+id);
            }
        }
    }
    static void TrayPaginationAndIdentity(){
        using(var f=new Fixture()){
            var devices=new List<WirelessDevice>();for(int i=0;i<64;i++)devices.Add(Device("fixture-"+i,new string('长',29)+i.ToString("D2")));
            f.Owner.WirelessDevices=devices.ToArray();var pref=DeviceSettings.Current;pref.TrayDeviceId=devices[63].Id;DeviceSettings.UseTransientConfiguration(pref);
            var all=Rows(f.Owner,PopupPage.ChoosePreference,211);var ids=new HashSet<int>();foreach(var row in all)Check(row.Id<0||ids.Add(row.Id),"64 device choices keep unique action IDs");
            Check(all.Count==71&&Selected(all)==1,"all 64 reader devices remain selectable after the six rules");
            var nav=new PopupNavigation {Page=PopupPage.ChoosePreference,PreferenceAction=211,VisibleChoices=3};var seen=new HashSet<string>();
            for(int offset=0;offset<70;offset+=3){nav.Offset=offset;var page=PopupPages.Build(f.Owner,nav,false);foreach(var row in page)if(row.Id>=500)seen.Add(row.DeviceKey);Check(PopupPages.SizeFor(page).Width==260&&PopupPages.SizeFor(page).Height<300,"tray choices use existing bounded pagination");}
            Check(seen.Count==64,"pagination reaches every current fixed-device choice");
            var target=all.Find(delegate(PopupControl row){return row.DeviceKey==devices[0].Id;});devices.Reverse();f.Owner.WirelessDevices=devices.ToArray();var moved=Rows(f.Owner,PopupPage.ChoosePreference,211);var same=PopupPages.FindStable(moved,target.StableKey);
            Check(same!=null&&same.Id!=target.Id&&PopupPages.SameAction(target,same),"a fixed choice retains stable device identity across discovery reorder");
            Check(!PopupPages.SameAction(target,moved.Find(delegate(PopupControl row){return row.Id==target.Id;})),"reused numeric slots cannot transfer a fixed-device action");
            Check(PopupPages.ReconcileFocus(moved,target.StableKey,1)==same,"fixed-choice keyboard focus follows stable identity");
            pref=DeviceSettings.Current;pref.TrayDeviceId="offline-pin";DeviceSettings.UseTransientConfiguration(pref);f.Page(PopupPage.Preferences);f.Activate(f.ById(211));f.Activate(f.Find("固定：已断开的设备"));
            Check(DeviceSettings.Current.TrayDeviceId=="offline-pin"&&f.Navigation.Page==PopupPage.Preferences,"the disconnected selected row preserves the existing fixed setting when activated");
            f.Owner.NativeMousePresent=false;f.Owner.WirelessDevices=new WirelessDevice[0];pref=DeviceSettings.Current;pref.TrayDeviceId=null;DeviceSettings.UseTransientConfiguration(pref);
            Check(Rows(f.Owner,PopupPage.ChoosePreference,211).Count==7,"the empty placeholder is never offered as a fixed tray device");
        }
    }
    static void HelpAccessibilityAndBounds(){
        using(var f=new Fixture()){
            var page=MissingPage();f.Page(page);var handle=f.Popup.Handle;var tree=(AccessibleObject)Call(f.Popup,"CreateAccessibilityInstance");var controls=f.Controls();
            for(int i=0;i<controls.Count;i++){
                var row=controls[i];var child=tree.GetChild(i);Check(child.Name==row.Text&&child.Description==row.Detail,"help uses the shared screen-reader text model");
                Check(child.Role==(row.Information?AccessibleRole.StaticText:AccessibleRole.PushButton),"help uses the existing accessible control roles");
            }
            f.Key(Keys.Tab);Check((int)Field(f.Popup,"keyboardFocus")==6,"help keyboard navigation starts with the shared back control");f.Key(Keys.Tab);Check((int)Field(f.Popup,"keyboardFocus")==0,"Tab skips explanatory text and reaches rediscovery");
            var nav=new PopupNavigation {Page=page,VisibleChoices=2};var seen=new HashSet<string>();
            for(int offset=0;offset<=4;offset+=2){nav.Offset=offset;var rows=PopupPages.Build(f.Owner,nav,false);foreach(var row in rows)if(row.Id!=6&&row.Id!=7&&row.Id!=8)seen.Add(row.Text);Check(PopupPages.SizeFor(rows).Width==260&&PopupPages.SizeFor(rows).Height<280,"help uses existing bounded pagination");}
            Check(seen.Count==6,"all help content is reachable through existing pagination");
            foreach(float scale in new[]{1f,1.25f,1.5f,1.75f,2f})foreach(bool dark in new[]{false,true})foreach(bool contrast in new[]{false,true}){
                VisualDesign.HighContrastOverride=contrast;var rows=Rows(f.Owner,page);var size=UiState.ScaleSize(PopupPages.SizeFor(rows),scale);
                using(var bitmap=new Bitmap(size.Width,size.Height))using(var graphics=Graphics.FromImage(bitmap)){
                    graphics.ScaleTransform(scale,scale);PopupPages.Paint(graphics,f.Owner,new PopupNavigation {Page=page},rows,dark,-1,-1,0);
                    foreach(var row in rows)Check(row.Rect.Left>=0&&row.Rect.Right<=260,"help hit regions fit the 260px panel at "+scale);
                    var explanation=rows[1];using(var font=new Font("Microsoft YaHei UI",11.5f,FontStyle.Regular,GraphicsUnit.Pixel)){
                        var measured=graphics.MeasureString(explanation.Text,font,new SizeF(explanation.Rect.Width-29,1000));Check(measured.Height<=explanation.Rect.Height-8,"the complete help explanation fits without truncation at "+scale);
                    }
                }
            }
        }
    }
    [STAThread] static int Main(){
        var cached=typeof(DeviceSettings).GetField("current",BindingFlags.Static|BindingFlags.NonPublic);object previous=cached.GetValue(null);
        try{
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);NativeDisplay.EnableDpiAwareness();Application.EnableVisualStyles();
            Run("more-options rows",MoreRows);Run("preference order",PreferenceOrder);Run("help actions and routes",HelpActions);Run("empty-card entry",EmptyCard);Run("hidden detail access",HiddenDetails);Run("tray rules and fixed devices",TrayRulesAndPins);Run("native pin consistency",NativePins);Run("tray pagination and identity",TrayPaginationAndIdentity);Run("new and legacy volume defaults",LegacyVolumeDefaults);Run("help accessibility and bounds",HelpAccessibilityAndBounds);
            Console.WriteLine(checks+" checks, "+failures+" failed groups; fake devices, no real clipboard/browser/settings writes");return failures==0?0:1;
        }finally{VisualDesign.HighContrastOverride=null;cached.SetValue(null,previous);}
    }
}
