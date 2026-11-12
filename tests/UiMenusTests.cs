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
        internal void TrayChooser(){Page(PopupPage.Preferences);Activate(ById(211));Activate(Find(TrayConfiguration.FirstName));Activate(Find("显示规则"));}
        internal void Activate(PopupControl row){Call(Popup,"PerformAction",row);}
        internal void Feedback(PopupControl row,string category){
            Exception failure=null;bool shown=false;string beforeCopy=Copied,beforeOpen=Opened;
            using(var timer=new System.Windows.Forms.Timer {Interval=10}){
                timer.Tick+=delegate{
                    foreach(Form window in Application.OpenForms){
                        var form=window as FeedbackForm;if(form==null)continue;timer.Stop();
                        try{
                            shown=true;Check(form.SelectedCategory==category,"entry selects the correct feedback type");
                            Check((bool)Field(Popup,"ignoreDeactivate"),"the panel keeps its modal deactivation guard");
                            Check(Copied==beforeCopy&&Opened==beforeOpen,"opening feedback does not copy or open a browser");
                            // Explicitly inject the empty relay so this fallback test stays offline.
                            Set(form,"relay","");Set(Field(form,"client"),"relay","");
                            ((TextBox)form.Controls.Find("Description",true)[0]).Text="设备连接以后没有显示";
                            ((Button)form.Controls.Find("SubmitFeedback",true)[0]).PerformClick();
                            Check(Copied.StartsWith("类型："+FeedbackInput.CategoryLabel(category))&&Copied.Contains(Owner.CreateFeedbackReport().FullText),"browser fallback retains category and full sanitized diagnostics");
                            Check(Opened.Length<=6000&&Uri.UnescapeDataString(Opened).Contains("设备连接以后没有显示"),"browser prefill includes the entered description");
                        }catch(Exception error){failure=error;}finally{form.Close();}
                        break;
                    }
                };
                timer.Start();Activate(row);
            }
            if(failure!=null)throw failure;Check(shown,"entry opens the modal feedback form");Check(!(bool)Field(Popup,"ignoreDeactivate"),"modal deactivation guard is released");
        }
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
        using(var f=new Fixture()){
            Check(Texts(Rows(f.Owner,PopupPage.Preferences))=="显示|外观|配色|布局|字号|托盘图标|减弱动效|提醒|低电量提醒|提醒阈值|数据|后台同步间隔|跨重启保留读数|清除历史读数|更新|自动检查更新|托盘图标显示更新角标|提醒测试版更新|自动检查反馈回复","preferences include theme style, layout, font size and the requested noninteractive groups");
            f.Page(PopupPage.Preferences);var toggle=f.Find("自动检查反馈回复");Check(toggle.Id==226&&toggle.Checked,"feedback checking defaults on in the settings page");f.Activate(toggle);
            Check(!DeviceSettings.Current.CheckFeedbackReplies&&f.Saves==1,"feedback checking toggle uses the existing configuration writer");
            var beta=f.Find("提醒测试版更新");Check(beta.Id==227&&beta.Checked,"beta update reminders default on in the settings page");f.Activate(beta);
            Check(!DeviceSettings.Current.IncludeBetaUpdates&&f.Saves==2,"beta reminder toggle uses the existing configuration writer");
        }
    }
    static bool IsInteger(float value){return Math.Abs(value-(float)Math.Round(value))<.001f;}
    static void FontSizePreferencesAndLayout(){
        string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ui-font-settings-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);string path=Path.Combine(root,"settings.json");
        Check(new AudioBindings().FontSize=="normal"&&DeviceSettings.LoadBindings(path).FontSize=="normal","new and missing configurations default FontSize to normal");
        File.WriteAllText(path,"{\"SchemaVersion\":1,\"Theme\":\"dark\",\"MouseAlias\":\"保留鼠标\"}");
        var legacy=DeviceSettings.LoadBindings(path);Check(legacy.FontSize=="normal"&&legacy.Theme=="dark"&&legacy.MouseAlias=="保留鼠标","legacy configuration defaults FontSize to normal without losing fields");
        foreach(string mode in new[]{"normal","large","unknown",null}){
            var candidate=legacy.Clone();candidate.FontSize=mode;DeviceSettings.SaveBindings(path,candidate);
            var loaded=DeviceSettings.LoadBindings(path);string expected=mode=="large"?"large":"normal";
            Check(loaded.FontSize==expected&&loaded.Clone().FontSize==expected&&loaded.MouseAlias=="保留鼠标","FontSize round-trips and normalizes: "+(mode??"null"));
        }
        File.WriteAllText(path,"{\"SchemaVersion\":1,\"FontSize\":{},\"MouseAlias\":\"对象回退\"}");
        var malformed=DeviceSettings.LoadBindings(path);Check(malformed.FontSize=="normal"&&malformed.MouseAlias=="对象回退","non-string FontSize falls back to normal");
        using(var f=new Fixture()){
            f.Owner.NativeMousePresent=true;f.Owner.Current=new BatteryReading {Percent=82,TimestampUtc=DateTime.UtcNow};
            f.Owner.Dpi=new DpiConfig {DeviceKey=MouseBattery.DeviceKey("font-test"),ActiveStage=1,TimestampUtc=DateTime.UtcNow};
            foreach(int v in new[]{400,800,1600,3200})f.Owner.Dpi.Stages.Add(new DpiStage {XDpi=v,YDpi=v});
            f.Owner.Headset=new HeadsetReading {Connected=true,ConnectionVerified=true,ReceiverPresent=true,Percent=64,BatteryTimestampUtc=DateTime.UtcNow};
            f.Owner.Audio=new AudioReading {TimestampUtc=DateTime.UtcNow,SpeakerAvailable=true,SpeakerId="speaker",SpeakerName="桌面扬声器",HeadsetAvailable=true,HeadsetId="headset",HeadsetName="无线耳机",MicrophoneAvailable=true,MicrophoneId="mic",MicrophoneMuted=false,ActiveOutput="speaker",ConsoleOutputId="speaker",MultimediaOutputId="speaker",VolumeEndpointId="speaker",OutputVolumePercent=50,Devices=new[]{new AudioDeviceChoice {Id="speaker",Name="桌面扬声器",Flow=0,FormFactor=1},new AudioDeviceChoice {Id="headset",Name="无线耳机",Flow=0,FormFactor=3},new AudioDeviceChoice {Id="mic",Name="无线麦克风",Flow=1}}};
            f.Owner.WirelessDevices=new[]{Device("w-mouse","无线鼠标"),Device("w-kbd","无线键盘","keyboard"),Device("w-pad","无线手柄","gamepad")};
            f.Page(PopupPage.Preferences);var row=f.Find("字号");Check(row.Id==228&&row.Value=="标准","preferences display standard font size by default");
            f.Activate(row);Check(f.Navigation.Page==PopupPage.ChoosePreference&&PopupPages.Title(f.Navigation)=="字号","activating font size opens the secondary chooser");
            var choices=f.Controls();Check(Texts(choices)=="标准|较大"&&Selected(choices)==1&&choices.Find(delegate(PopupControl c){return c.Id==460;}).Selected,"chooser lists standard and large with standard selected");
            int savesBefore=f.Saves;f.Activate(choices.Find(delegate(PopupControl c){return c.Id==461;}));
            Check(f.Saves==savesBefore+1&&DeviceSettings.Current.FontSize=="large"&&VisualDesign.LargeFont&&f.Navigation.Page==PopupPage.Preferences,"choosing large saves and returns to preferences");
            Check(f.Find("字号").Value=="较大"&&VisualDesign.PanelWidth==300&&VisualDesign.DeviceNameFontSize==14f&&VisualDesign.SubtitleFontSize==13f,"large mode enlarges font and panel metrics");
            foreach(string fontMode in new[]{"normal","large"}){
                var pref=DeviceSettings.Current;pref.FontSize=fontMode;DeviceSettings.UseTransientConfiguration(pref);
                var observed=new List<DrawnLabel>();CompactRenderer.LabelObserver=observed.Add;
                try{
                    var mainSize=VisualDesign.PanelSizeFor(true,f.Owner.Dpi);
                    using(var bmp=new Bitmap(mainSize.Width,mainSize.Height))using(var g=Graphics.FromImage(bmp))CompactRenderer.Paint(g,f.Owner.Current,null,null,false,f.Owner.Dpi,null,false,false,f.Owner.Headset,f.Owner.Audio);
                    var wirelessLayout=WirelessLayout.Build(f.Owner,0,1000);
                    using(var bmp=new Bitmap(wirelessLayout.Size.Width,wirelessLayout.Size.Height))using(var g=Graphics.FromImage(bmp))CompactRenderer.PaintWireless(g,f.Owner,wirelessLayout,false,new PanelFeedback());
                    foreach(PopupPage page in Enum.GetValues(typeof(PopupPage)))if(page!=PopupPage.Main){
                        var nav=new PopupNavigation {Page=page,PreferenceAction=228,SelectedDeviceKey="w-mouse",SelectedDeviceName="无线鼠标",VisibleChoices=7,MaximumHeight=360};
                        var pageRows=PopupPages.Build(f.Owner,nav,true);var sz=PopupPages.SizeFor(pageRows);
                        Check(sz.Width==VisualDesign.PanelWidth&&sz.Height<=360,"page "+page+" fits within 360px work area in "+fontMode+" mode (actual "+sz+")");
                        float bottom=0;
                        foreach(var item in pageRows){
                            if(item.Id==6)continue;
                            Check(item.Rect.Top>=bottom&&item.Rect.Left>=0&&item.Rect.Right<=sz.Width&&item.Rect.Bottom<=sz.Height-(VisualDesign.LargeFont?32:28),"row "+item.Text+" on "+page+" does not overlap or overflow in "+fontMode);
                            bottom=item.Rect.Bottom;
                        }
                        using(var bmp=new Bitmap(sz.Width,sz.Height))using(var g=Graphics.FromImage(bmp))PopupPages.Paint(g,f.Owner,nav,pageRows,false,-1,-1,-1);
                    }
                    Check(observed.Count>40,"96 DPI observer captured key labels in "+fontMode);
                    foreach(var label in observed){
                        Check(IsInteger(label.FontSize),"96 DPI font size is integer for "+label.Text+": "+label.FontSize);
                        Check(IsInteger(label.DeviceRect.X)&&IsInteger(label.DeviceRect.Y)&&IsInteger(label.DeviceRect.Width)&&IsInteger(label.DeviceRect.Height),"96 DPI label coordinates are integers for "+label.Text+": "+label.DeviceRect);
                    }
                }finally{CompactRenderer.LabelObserver=null;}
            }
            var reset=DeviceSettings.Current;reset.FontSize="normal";DeviceSettings.UseTransientConfiguration(reset);
        }
    }
    static void PanelLayoutPreferences(){
        foreach(string font in new[]{"normal","large"})foreach(bool wireless in new[]{false,true})foreach(bool headset in new[]{false,true})using(var f=new Fixture()){
            var pref=DeviceSettings.Current;pref.FontSize=font;pref.VolumeCollapsed=false;DeviceSettings.UseTransientConfiguration(pref);
            f.Owner.NativeMousePresent=true;f.Owner.GenericReady=wireless;f.Owner.Current=new BatteryReading {Percent=85,TimestampUtc=DateTime.UtcNow};f.Owner.Dpi=new DpiConfig {DeviceKey=MouseBattery.DeviceKey("ring-choice"),ActiveStage=0,TimestampUtc=DateTime.UtcNow};foreach(int value in new[]{400,800,1600,3200})f.Owner.Dpi.Stages.Add(new DpiStage {XDpi=value,YDpi=value});
            f.Owner.Headset=headset?new HeadsetReading {Connected=true,ConnectionVerified=true,Percent=50,BatteryTimestampUtc=DateTime.UtcNow}:null;f.Owner.Audio=new AudioReading {Devices=new AudioDeviceChoice[0]};f.Popup.SyncDevices();
            f.Page(PopupPage.Preferences);var row=f.Find("布局");Check(row.Id==229&&row.Value=="环形","new and legacy defaults are shown as ring in preferences");f.Activate(row);Check(PopupPages.Title(f.Navigation)=="布局"&&Texts(f.Controls())=="环形|电量条|网格","layout chooser has exactly the three text choices");
            int saves=f.Saves;
            foreach(int index in new[]{1,0,2}){
                f.Activate(f.ById(470+index));Check(f.Saves==++saves&&DeviceSettings.Current.PanelLayout==PanelLayouts.Keys[index],"choosing a layout saves exactly once through the existing configuration writer");
                Check(f.ById(470+index).Selected&&Selected(f.Controls())==1,"layout selection is effective immediately and retains its check mark");
                f.Page(PopupPage.Main);var expected=wireless?WirelessLayout.Build(f.Owner,0,10000).Size:VisualDesign.PanelSizeFor(headset,f.Owner.Dpi);float scale=(float)Field(f.Popup,"dpiScale");Check(f.Popup.ClientSize==UiState.ScaleSize(expected,scale),"switching layout updates the actual popup height: "+font+" / "+wireless+" / "+headset+" / "+PanelLayouts.Keys[index]);
                Call(f.Popup,"UpdateFallbackRegion");if(PanelLayouts.Keys[index]!="grid")Check(f.Popup.Region.IsVisible(VisualDesign.PanelWidth/2f*scale,(VisualDesign.MouseCardHeight(f.Owner.Dpi)+.5f)*scale)==VisualDesign.RingLayout,"fallback window uses a continuous ring panel and preserves the bar's gaps");
                var controls=f.Controls();var name=controls.Find(delegate(PopupControl item){return item.Id==2;});f.Activate(name);Check(f.Navigation.Page==PopupPage.Mouse,"device row still opens the existing detail page");
                f.Page(PopupPage.ChoosePreference);f.Navigation.PreferenceAction=229;
            }
            var grid=WirelessLayout.Build(f.Owner,0,10000).Size;pref=DeviceSettings.Current;pref.PanelLayout="ring";DeviceSettings.UseTransientConfiguration(pref);Check(WirelessLayout.Build(f.Owner,0,10000).Size!=grid,"grid no longer uses the temporary ring geometry");
            f.Page(PopupPage.Main);f.Popup.SyncDevices();pref=DeviceSettings.Current;pref.ReducedMotion=false;pref.PanelLayout="bar";DeviceSettings.UseTransientConfiguration(pref);f.Owner.ConfigurationChanged(false);
            if(!PopupPages.Reduced){Check(((System.Windows.Forms.Timer)Field(f.Popup,"pageAnimation")).Enabled,"layout height change uses the existing animation timer");Pump(delegate{return !((System.Windows.Forms.Timer)Field(f.Popup,"pageAnimation")).Enabled;});}
            var settled=wireless?WirelessLayout.Build(f.Owner,0,10000).Size:VisualDesign.PanelSizeFor(headset,f.Owner.Dpi);Check(f.Popup.ClientSize==UiState.ScaleSize(settled,(float)Field(f.Popup,"dpiScale")),"animated layout change settles at the calculated height");
        }
    }
    static DpiConfig GridDpi(int count=4){var dpi=new DpiConfig {DeviceKey=MouseBattery.DeviceKey("grid-input"),ActiveStage=0,TimestampUtc=DateTime.UtcNow};for(int i=0;i<count;i++)dpi.Stages.Add(new DpiStage {XDpi=(i+1)*400,YDpi=(i+1)*400});return dpi;}
    static string QuickControls(List<PopupControl> controls,float top){return String.Join("|",controls.FindAll(delegate(PopupControl c){return c.Rect.Top>=top&&c.Id!=0&&c.Id!=1&&c.Id!=902&&c.Id!=903;}).ConvertAll(delegate(PopupControl c){return c.StableKey+":"+c.Rect+":"+c.Text;}).ToArray());}
    static void GridInputAndRegion(){
        foreach(string font in new[]{"normal","large"})foreach(bool wireless in new[]{false,true})using(var f=new Fixture()){
            var pref=DeviceSettings.Current;pref.PanelLayout="grid";pref.FontSize=font;DeviceSettings.UseTransientConfiguration(pref);f.Owner.NativeMousePresent=true;f.Owner.GenericReady=wireless;f.Owner.Current=new BatteryReading {Percent=85,TimestampUtc=DateTime.UtcNow};f.Owner.Dpi=GridDpi();f.Owner.Headset=new HeadsetReading {Connected=true,ConnectionVerified=true,Percent=50,BatteryTimestampUtc=DateTime.UtcNow};f.Owner.Audio=new AudioReading {Devices=new AudioDeviceChoice[0]};
            if(wireless)f.Owner.WirelessDevices=new[]{Device("grid-a","第一台键盘","keyboard"),Device("grid-b","第二台键盘","keyboard"),Device("grid-c","手柄","gamepad")};
            Set(f.Owner,"nativeNeedsReconcile",false);f.Owner.DpiError=null;f.Popup.SyncDevices();f.Page(PopupPage.Main);var layout=(WirelessLayout)Call(f.Popup,"MainWireless");var tiles=f.Controls().FindAll(delegate(PopupControl c){return c.Id>=0&&c.Rect.Bottom<=layout.Grid.Controls.Top;});
            f.Key(Keys.Tab);Check((string)Field(f.Popup,"keyboardFocusKey")==tiles[0].StableKey,"first Tab enters the grid");f.Key(Keys.Right);Check((string)Field(f.Popup,"keyboardFocusKey")==tiles[1].StableKey,"Right stays within the current grid row");
            if(wireless){f.Key(Keys.Down);Check((string)Field(f.Popup,"keyboardFocusKey")==tiles[3].StableKey,"Down keeps the column");f.Key(Keys.Left);Check((string)Field(f.Popup,"keyboardFocusKey")==tiles[2].StableKey,"Left moves to the adjacent column");f.Key(Keys.Down);Check((string)Field(f.Popup,"keyboardFocusKey")==tiles[4].StableKey,"Down enters the odd spanning last tile");f.Key(Keys.Up);Check((string)Field(f.Popup,"keyboardFocusKey")==tiles[2].StableKey,"Up from a wide tile resolves a spatially adjacent tile");}
            f.Key(Keys.Tab);Check((int)Field(f.Popup,"keyboardFocus")==10,"Tab enters fixed DPI controls from any focused tile");
            string snapshot=QuickControls(f.Controls(),layout.Grid.Controls.Top);
            foreach(var tile in tiles){
                Check(!tile.Selected,"tiles have no selected state");f.Click(tile);var card=layout.All.Find(delegate(WirelessCard c){return c.Id==tile.DeviceKey;});Check(f.Navigation.Page==(card.NativeMouse?PopupPage.Mouse:card.NativeHeadset?PopupPage.Headset:PopupPage.Device),"one pointer click directly opens the original detail route");if(!card.NativeMouse&&!card.NativeHeadset)Check(f.Navigation.SelectedDeviceKey==tile.DeviceKey,"direct detail action retains target identity");f.Key(Keys.Escape);Check(f.Navigation.Page==PopupPage.Main&&QuickControls(f.Controls(),layout.Grid.Controls.Top)==snapshot,"returning from any tile leaves the fixed controls unchanged");
                Call(f.Popup,"FocusControl",tile);f.Key(Keys.Enter);Check(f.Navigation.Page!=PopupPage.Main,"Enter directly opens tile details");f.Key(Keys.Escape);Check((string)Field(f.Popup,"keyboardFocusKey")==tile.StableKey,"Back restores the same tile focus by device identity");Call(f.Popup,"FocusControl",tile);f.Key(Keys.Space);Check(f.Navigation.Page!=PopupPage.Main,"Space directly opens tile details");f.Key(Keys.Escape);
            }
            foreach(float scale in new[]{1f,1.25f,1.5f,1.75f,2f}){Set(f.Popup,"dpiScale",scale);f.Page(PopupPage.Main);layout=(WirelessLayout)Call(f.Popup,"MainWireless");Call(f.Popup,"UpdateFallbackRegion");
                Check(!f.Popup.Region.IsVisible((GridLayout.TileWidth+4)*scale,GridLayout.TileHeight/2f*scale),"fallback region releases the transparent column gap at every DPI scale");Check(!f.Popup.Region.IsVisible(VisualDesign.PanelWidth/2f*scale,(layout.Grid.Controls.Top-4)*scale),"fallback region releases the gap above fixed controls");Check(f.Popup.Region.IsVisible(VisualDesign.PanelWidth/2f*scale,(layout.Grid.Controls.Top+layout.Grid.Controls.Height/2)*scale),"fixed controls stay in the fallback region");
                foreach(var control in f.Controls())if(control.Id>=0&&!control.Information)Check(control.Rect.Bottom*scale<=f.Popup.ClientSize.Height+1,"all actual input targets fit the scaled client");
            }
        }
    }
    static void GridScrollAndAnimation(){
        foreach(string font in new[]{"normal","large"})using(var f=new Fixture()){
            var pref=DeviceSettings.Current;pref.PanelLayout="grid";pref.FontSize=font;DeviceSettings.UseTransientConfiguration(pref);f.Owner.NativeMousePresent=true;f.Owner.Current=new BatteryReading {Percent=85,TimestampUtc=DateTime.UtcNow};f.Owner.Dpi=GridDpi(8);f.Owner.Headset=null;f.Owner.Audio=new AudioReading {Devices=new AudioDeviceChoice[0]};var devices=new List<WirelessDevice>();for(int i=0;i<18;i++)devices.Add(Device("scroll-"+i,"设备 "+i,"keyboard"));f.Owner.WirelessDevices=devices.ToArray();Set(f.Popup,"dpiScale",2f);f.Page(PopupPage.Main);f.Popup.SyncDevices();
            var first=(WirelessLayout)Call(f.Popup,"MainWireless");Check(first.Paged,"bounded screen pages the grid");string controls=QuickControls(f.Controls(),first.Grid.Controls.Top);
            Action<float,int> wheel=delegate(float y,int delta){typeof(Control).GetMethod("OnMouseWheel",Private).Invoke(f.Popup,new object[]{new MouseEventArgs(MouseButtons.None,0,30,(int)(y*2),delta)});};
            wheel(first.Grid.Controls.Top+20,-120);Check(f.Navigation.DeviceScroll==0,"wheel over fixed controls never scrolls the grid");wheel(30,-120);var next=(WirelessLayout)Call(f.Popup,"MainWireless");Check(next.Start==2&&next.Grid.Controls==first.Grid.Controls&&QuickControls(f.Controls(),next.Grid.Controls.Top)==controls,"wheel over tiles scrolls exactly one row without moving or replacing controls");
            f.Activate(f.ById(902));Check(((WirelessLayout)Call(f.Popup,"MainWireless")).Start==0,"previous page returns to the first full row");f.Activate(f.ById(903));next=(WirelessLayout)Call(f.Popup,"MainWireless");Check(next.Start==first.End&&next.Start%2==0,"next page starts on a grid row boundary");
            f.Navigation.DeviceScroll=0;f.Page(PopupPage.Main);var last=first.Cards[first.Cards.Count-1];Call(f.Popup,"FocusControl",f.Controls().Find(delegate(PopupControl c){return c.DeviceKey==last.Id&&c.Id>=1000&&c.Id<2000;}));int index=first.All.IndexOf(last);f.Key(Keys.Down);next=(WirelessLayout)Call(f.Popup,"MainWireless");var focused=f.Controls().Find(delegate(PopupControl c){return c.StableKey==(string)Field(f.Popup,"keyboardFocusKey");});Check(next.Start==2&&focused.DeviceKey==first.All[index+2].Id,"Down at a viewport edge reveals the next row and preserves its column");
            f.Owner.WirelessDevices=new WirelessDevice[0];f.Navigation.DeviceScroll=0;Set(f.Popup,"dpiScale",1f);f.Page(PopupPage.Main);f.Popup.SyncDevices();int oldHeight=f.Popup.ClientSize.Height;
            pref=DeviceSettings.Current;pref.ReducedMotion=false;DeviceSettings.UseTransientConfiguration(pref);f.Owner.Dpi=null;f.Owner.ConfigurationChanged(false);
            if(!PopupPages.Reduced){Check(((System.Windows.Forms.Timer)Field(f.Popup,"pageAnimation")).Enabled,"DPI group removal uses the existing height animation");Pump(delegate{return !((System.Windows.Forms.Timer)Field(f.Popup,"pageAnimation")).Enabled;});}
            var settled=(WirelessLayout)Call(f.Popup,"MainWireless");Check(f.Popup.ClientSize.Height==settled.Size.Height&&f.Popup.ClientSize.Height<oldHeight,"control group removal settles at the newly calculated height");
        }
    }
    static void HelpActions(){
        using(var f=new Fixture()){
            f.Owner.WirelessDevices=new[]{Device("help-device","帮助设备")};
            var page=MissingPage();f.Page(PopupPage.More);var entry=f.Find("设备没显示？");Call(f.Popup,"FocusControl",entry);f.Key(Keys.Enter);
            Check(f.Navigation.Page==page&&PopupPages.Title(f.Navigation)=="设备没显示？","keyboard entry opens the titled help page");
            var rows=Rows(f.Owner,page);
            Check(!rows.Exists(delegate(PopupControl row){return row.Text=="当前状态";}),"more-options help omits an empty-state reason when devices are visible");
            Check(Texts(rows)=="请确认接收器已插好，设备已开机并唤醒|重新识别设备|查看支持清单|复制诊断摘要|反馈问题|我的反馈|反馈不代表一定能适配","help rows use the exact copy and order");
            var feedback=rows.Find(delegate(PopupControl row){return row.Text=="反馈问题";});
            Check(feedback.Id==258&&feedback.Detail=="在程序内提交，不需要 GitHub 账号","help shares feedback action 258 and the configured-relay description");
            Check(rows[1].Information&&rows[7].Information,"help explanations are static text");
            Check(rows[6].Id==262&&!rows[6].Information&&Rows(f.Owner,PopupPage.Diagnostics).Exists(delegate(PopupControl row){return row.Id==262&&row.Text=="我的反馈";}),"both feedback pages expose the same history action");
            int reads=f.Reads;f.Activate(f.Find("重新识别设备"));Pump(delegate{return !f.Owner.IsRefreshing;});
            Check(f.Reads>reads&&f.Find("重新识别设备").Id==0,"help rediscovery routes through the existing sync-all action");
            f.Activate(f.Find("复制诊断摘要"));Check(f.Copied==f.Owner.DiagnosticSummary()&&f.Find("复制诊断摘要").Id==250,"help copy routes to the same diagnostic summary");
            f.Activate(f.Find("查看支持清单"));Check(f.Opened==SupportUrl,"support action opens only the fixed compatibility URL");
            f.Feedback(feedback,"device");
            f.Key(Keys.Escape);Check(f.Navigation.Page==PopupPage.More&&(string)Field(f.Popup,"keyboardFocusKey")==entry.StableKey,"Escape returns focus to the originating more-options row");
            f.Page(PopupPage.Diagnostics);f.Activate(f.Find("复制诊断摘要"));Check(f.Copied==f.Owner.DiagnosticSummary(),"original diagnostic copy remains reachable");
            f.Feedback(f.Find("反馈问题"),"other");
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
    static void HiddenEmptyCard(){
        using(var f=new Fixture()){
            var device=Device("hidden-mouse","隐藏鼠标");f.Owner.WirelessDevices=new[]{device};
            var pref=DeviceSettings.Current;pref.HiddenDeviceIds.Add(device.Id);DeviceSettings.UseTransientConfiguration(pref);
            f.Owner.GenericError="fixture error";
            var layout=WirelessLayout.Build(f.Owner,0,1000);
            Check(layout.All.Count==1&&layout.All[0].Id=="empty"&&layout.All[0].Subtitle=="设备已隐藏，可在设备管理中恢复","all-hidden card retains the original recovery reason ahead of reader errors");
            var card=f.Controls().Find(delegate(PopupControl row){return row.DeviceKey=="empty";});
            Check(card!=null&&card.Enabled&&card.Text=="设备管理"&&card.Detail==layout.All[0].Subtitle,"hidden empty-card target describes device management and recovery");
            f.Click(card);Check(f.Navigation.Page==PopupPage.Devices,"hidden empty-card click opens device management");f.Key(Keys.Escape);
            Call(f.Popup,"FocusControl",card);f.Key(Keys.Enter);Check(f.Navigation.Page==PopupPage.Devices,"hidden empty-card keyboard action opens device management");f.Key(Keys.Escape);
            var handle=f.Popup.Handle;var tree=(AccessibleObject)Call(f.Popup,"CreateAccessibilityInstance");var controls=f.Controls();int index=controls.FindIndex(delegate(PopupControl row){return row.StableKey==card.StableKey;});
            Check(tree.GetChild(index).Name=="设备管理"&&tree.GetChild(index).Description==layout.All[0].Subtitle,"hidden empty card exposes its recovery route to screen readers");
            tree.GetChild(index).DoDefaultAction();Check(f.Navigation.Page==PopupPage.Devices,"hidden empty-card accessible action opens device management");f.Key(Keys.Escape);
            Set(f.Owner,"genericBusy",1);layout=WirelessLayout.Build(f.Owner,0,1000);
            Check(layout.All[0].Subtitle=="正在识别设备…"&&!f.Controls().Exists(delegate(PopupControl row){return row.DeviceKey=="empty"&&row.Enabled;}),"scanning still disables an all-hidden empty card");
            f.Activate(card);Check(f.Navigation.Page==PopupPage.Main,"stale hidden-card action is rejected during scanning");Set(f.Owner,"genericBusy",0);
            f.Owner.WirelessDevices=new WirelessDevice[0];
            Check(WirelessLayout.Build(f.Owner,0,1000).All[0].Subtitle=="点这里查看原因","offline hidden settings alone do not claim all detected devices are hidden");
        }
    }
    static void EmptyReasonsAndBounds(){
        using(var f=new Fixture()){
            Action<string,string> expect=delegate(string reason,string subtitle){
                var layout=WirelessLayout.Build(f.Owner,0,1000);Check(layout.All[0].Subtitle==subtitle,"empty-card subtitle for "+reason);
                var rows=Rows(f.Owner,MissingPage());var status=rows[1];
                Check(status.Information&&status.Text=="当前状态"&&status.Value==reason&&status.WrapText,"help starts with the complete wrapped reason: "+reason);
                Check(status.Rect.Bottom<rows[2].Rect.Top&&PopupPages.Hit(rows,new PointF(status.Rect.X+10,status.Rect.Y+10))==null,"current status is informational and does not overlap help actions");
                foreach(float scale in new[]{1f,1.25f,1.5f,1.75f,2f})foreach(bool dark in new[]{false,true})foreach(bool contrast in new[]{false,true}){
                    VisualDesign.HighContrastOverride=contrast;var size=UiState.ScaleSize(PopupPages.SizeFor(rows),scale);
                    using(var bitmap=new Bitmap(size.Width,size.Height))using(var graphics=Graphics.FromImage(bitmap))using(var font=new Font("Microsoft YaHei UI",11.5f,FontStyle.Regular,GraphicsUnit.Pixel)){
                        graphics.ScaleTransform(scale,scale);PopupPages.Paint(graphics,f.Owner,new PopupNavigation {Page=MissingPage()},rows,dark,-1,-1,0);
                        var measured=graphics.MeasureString(status.Text+"\n"+status.Value,font,new SizeF(status.Rect.Width-29,1000));
                        Check(measured.Height<=status.Rect.Height-12,"full status label and reason fit without truncation at "+scale+": "+reason);
                        bool hasReasonInk=false;int bottom=(int)((status.Rect.Bottom-6)*scale),right=(int)((status.Rect.Right-23)*scale);
                        for(int y=(int)((status.Rect.Y+6+font.GetHeight(graphics))*scale);y<bottom&&!hasReasonInk;y++)for(int x=(int)((status.Rect.X+6)*scale);x<right;x++)if(bitmap.GetPixel(x,y).ToArgb()!=bitmap.GetPixel((int)((status.Rect.X+1)*scale),y).ToArgb()){hasReasonInk=true;break;}
                        Check(hasReasonInk,"wrapped current-state value is painted below its label at "+scale+": "+reason);
                    }
                }
                VisualDesign.HighContrastOverride=null;
            };
            f.Owner.GenericReady=false;f.Owner.GenericError="fixture error";f.Owner.GenericFailureCount=1;f.Owner.GenericHealthSummary="fixture health";
            expect("设备读取组件暂不可用","点这里查看原因");
            f.Page(PopupPage.Main);f.Click(f.Controls().Find(delegate(PopupControl row){return row.DeviceKey=="empty";}));
            Check(f.Navigation.Page==MissingPage()&&f.Controls()[1].Value=="设备读取组件暂不可用","reader-unavailable empty card opens help with its complete reason");
            Set(f.Owner,"genericBusy",1);expect("正在识别设备…","正在识别设备…");Set(f.Owner,"genericBusy",0);
            f.Owner.GenericError=null;expect("部分读取器暂不可用","点这里查看原因");
            f.Owner.GenericFailureCount=0;expect("部分读取器暂不可用","点这里查看原因");
            f.Owner.GenericHealthSummary=null;expect("设备读取组件尚未就绪","点这里查看原因");
            f.Owner.GenericReady=true;expect("尚未连接兼容设备","点这里查看原因");
            var device=Device("hidden-reason","隐藏键盘","keyboard");f.Owner.WirelessDevices=new[]{device};
            var pref=DeviceSettings.Current;pref.HiddenDeviceIds.Add(device.Id);DeviceSettings.UseTransientConfiguration(pref);
            expect("设备已隐藏，可在设备管理中恢复","设备已隐藏，可在设备管理中恢复");
            f.Page(MissingPage());var handle=f.Popup.Handle;var tree=(AccessibleObject)Call(f.Popup,"CreateAccessibilityInstance");
            Check(tree.GetChild(1).Name=="当前状态"&&tree.GetChild(1).Value=="设备已隐藏，可在设备管理中恢复"&&tree.GetChild(1).Role==AccessibleRole.StaticText,"screen readers receive the full current status as static text");
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
            Check(Texts(rows)=="当前使用的设备|最低电量|自动选择有数据的设备|鼠标优先|耳机优先|键盘优先|固定：ATK A9 Air+|固定：鼠标 A|固定：键盘 B|固定：隐藏设备","tray chooser orders the six rules before current devices, including hidden devices");
            Check(Selected(rows)==1&&rows[1].Id==415&&rows[2].Id==414&&rows[3].Id==410,"rule IDs and selection preserve the old configuration mapping");
            var ids=new HashSet<int>();foreach(var row in rows)Check(row.Id<0||ids.Add(row.Id),"tray choice IDs do not collide");
            int[] ruleIds={415,414,410,411,412,413};string[] sources={"active","lowest","auto","mouse","headset","keyboard"};
            for(int i=0;i<ruleIds.Length;i++){
                pref=DeviceSettings.Current;pref.TrayDeviceId="mouse-a";DeviceSettings.UseTransientConfiguration(pref);f.TrayChooser();
                var rule=f.Find(new[]{"当前使用的设备","最低电量","自动选择有数据的设备","鼠标优先","耳机优先","键盘优先"}[i]);Check(rule.Id==ruleIds[i],"rule retains action ID "+sources[i]);f.Activate(rule);
                Check(DeviceSettings.Current.TraySource==sources[i]&&DeviceSettings.Current.TrayDeviceId==null,"choosing a rule clears the fixed device: "+sources[i]);
                rows=Rows(f.Owner,PopupPage.ChoosePreference,211);Check(Selected(rows)==1&&rows.Find(delegate(PopupControl row){return row.Selected;}).Id==ruleIds[i],"one selected rule: "+sources[i]);
            }
            f.TrayChooser();var pin=f.Find("固定：鼠标 A");f.Activate(pin);
            Check(DeviceSettings.Current.TrayDeviceId=="mouse-a"&&f.Navigation.Page==PopupPage.TrayGroup,"fixed choice writes the existing TrayDeviceId and returns to its group");
            rows=Rows(f.Owner,PopupPage.ChoosePreference,211);Check(Selected(rows)==1&&rows.Find(delegate(PopupControl row){return row.Selected;}).DeviceKey=="mouse-a","a fixed device excludes every rule selection");
            Check(Rows(f.Owner,PopupPage.TrayIcons).Find(delegate(PopupControl row){return row.GroupKey==TrayConfiguration.FirstId;}).Value=="固定：鼠标 A","group list shows the fixed device's name");
            pref=DeviceSettings.Current;pref.DeviceAliases["mouse-a"]="新别名";DeviceSettings.UseTransientConfiguration(pref);Check(Rows(f.Owner,PopupPage.TrayIcons).Find(delegate(PopupControl row){return row.GroupKey==TrayConfiguration.FirstId;}).Value=="固定：新别名","group summary follows the existing alias");
            f.Navigation.SelectedDeviceKey="mouse-a";f.Navigation.SelectedDeviceName="新别名";f.Page(PopupPage.Device);var toggle=f.Find("固定为托盘主设备");Check(toggle.Checked,"device detail reflects the tray chooser pin");f.Activate(toggle);
            Check(DeviceSettings.Current.TrayDeviceId==null&&Selected(Rows(f.Owner,PopupPage.ChoosePreference,211))==1,"detail unpin restores the rule selection");f.Activate(f.Find("固定为托盘主设备"));
            Check(DeviceSettings.Current.TrayDeviceId=="mouse-a"&&Rows(f.Owner,PopupPage.ChoosePreference,211).Exists(delegate(PopupControl row){return row.Selected&&row.DeviceKey=="mouse-a";}),"detail pin updates the same tray chooser setting");
            f.Owner.WirelessDevices=new WirelessDevice[0];rows=Rows(f.Owner,PopupPage.ChoosePreference,211);
            Check(Selected(rows)==1&&rows.Exists(delegate(PopupControl row){return row.Selected&&row.Text=="固定：已断开的设备";}),"a disconnected fixed device has one explicit selected row");
            Check(DeviceSettings.Current.TrayDeviceId=="mouse-a","building a disconnected chooser never changes configuration");
            pref=DeviceSettings.Current;pref.HiddenDeviceIds.Add("native:mouse");pref.TrayDeviceId="native:mouse";DeviceSettings.UseTransientConfiguration(pref);rows=Rows(f.Owner,PopupPage.ChoosePreference,211);
            Check(Selected(rows)==1&&rows.Exists(delegate(PopupControl row){return row.Selected&&row.DeviceKey=="native:mouse"&&row.Text=="固定："+pref.MouseAlias;}),"hidden connected native mouse keeps its real fixed-device name");
            Check(f.Saves==9,"rule and both pin entry points all use the injected settings writer");
        }
    }
    static void HiddenFixedDevices(){
        using(var f=new Fixture()){
            var device=Device("hidden-pin","隐藏设备");f.Owner.WirelessDevices=new[]{device};
            var pref=DeviceSettings.Current;pref.HiddenDeviceIds.Add(device.Id);pref.DeviceAliases[device.Id]="隐藏设备别名";DeviceSettings.UseTransientConfiguration(pref);
            f.TrayChooser();f.Activate(f.Find("固定：隐藏设备别名"));
            Check(DeviceSettings.Current.TrayDeviceId==device.Id&&f.Navigation.Page==PopupPage.TrayGroup,"hidden device can be fixed through its group rule chooser");
            Check(Rows(f.Owner,PopupPage.TrayIcons).Find(delegate(PopupControl row){return row.GroupKey==TrayConfiguration.FirstId;}).Value=="固定：隐藏设备别名","hidden fixed device displays its real alias in the group summary");
            var rows=Rows(f.Owner,PopupPage.ChoosePreference,211);
            Check(Selected(rows)==1&&rows.Exists(delegate(PopupControl row){return row.Selected&&row.DeviceKey==device.Id&&row.Text=="固定：隐藏设备别名";}),"hidden fixed device has one selected choice with its real name");
            f.Owner.WirelessDevices=new WirelessDevice[0];rows=Rows(f.Owner,PopupPage.ChoosePreference,211);
            Check(Selected(rows)==1&&rows.Exists(delegate(PopupControl row){return row.Selected&&row.DeviceKey==device.Id&&row.Text=="固定：已断开的设备";}),"disconnected hidden-device placeholder is reported as disconnected");
            Check(Rows(f.Owner,PopupPage.TrayIcons).Find(delegate(PopupControl row){return row.GroupKey==TrayConfiguration.FirstId;}).Value=="固定：已断开的设备","disconnected hidden pin is also explicit in the group summary");
            pref=DeviceSettings.Current;pref.TrayDeviceId=null;DeviceSettings.UseTransientConfiguration(pref);rows=Rows(f.Owner,PopupPage.ChoosePreference,211);
            Check(!rows.Exists(delegate(PopupControl row){return row.ActionKey=="tray:pin";}),"offline hidden management placeholders are not offered as current devices");
            f.Owner.NativeMousePresent=true;f.Owner.Headset=new HeadsetReading {Connected=true,ConnectionVerified=true,ReceiverPresent=true,Percent=80,BatteryTimestampUtc=DateTime.UtcNow};
            pref=DeviceSettings.Current;pref.HiddenDeviceIds.Add("native:mouse");pref.HiddenDeviceIds.Add("native:headset");DeviceSettings.UseTransientConfiguration(pref);
            foreach(string id in new[]{"native:mouse","native:headset"}){
                pref=DeviceSettings.Current;pref.TrayDeviceId=id;DeviceSettings.UseTransientConfiguration(pref);rows=Rows(f.Owner,PopupPage.ChoosePreference,211);
                string name=id=="native:mouse"?pref.MouseAlias:pref.HeadsetAlias;
                Check(Selected(rows)==1&&rows.Exists(delegate(PopupControl row){return row.Selected&&row.DeviceKey==id&&row.Text=="固定："+name;}),"hidden native pin keeps its name: "+id);
            }
            f.Owner.NativeMousePresent=false;f.Owner.Headset=null;rows=Rows(f.Owner,PopupPage.ChoosePreference,211);
            Check(Selected(rows)==1&&rows.Exists(delegate(PopupControl row){return row.Selected&&row.DeviceKey=="native:headset"&&row.Text=="固定：已断开的设备";}),"disconnected hidden native headset is not mistaken for a current card");
            Check(f.Saves==1,"hidden-device choice uses only the injected configuration writer");
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
                f.TrayChooser();f.Activate(f.Find("固定："+name));f.Page(id=="native:mouse"?PopupPage.Mouse:PopupPage.Headset);
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
            pref=DeviceSettings.Current;pref.TrayDeviceId="offline-pin";DeviceSettings.UseTransientConfiguration(pref);f.TrayChooser();f.Activate(f.Find("固定：已断开的设备"));
            Check(DeviceSettings.Current.TrayDeviceId=="offline-pin"&&f.Navigation.Page==PopupPage.TrayGroup,"the disconnected selected row preserves the existing fixed setting and returns to its group");
            f.Owner.NativeMousePresent=false;f.Owner.WirelessDevices=new WirelessDevice[0];pref=DeviceSettings.Current;pref.TrayDeviceId=null;DeviceSettings.UseTransientConfiguration(pref);
            Check(Rows(f.Owner,PopupPage.ChoosePreference,211).Count==7,"the empty placeholder is never offered as a fixed tray device");
        }
    }
    static void HelpAccessibilityAndBounds(){
        using(var f=new Fixture()){
            f.Owner.WirelessDevices=new[]{Device("help-device","帮助设备")};
            var page=MissingPage();f.Page(page);var handle=f.Popup.Handle;var tree=(AccessibleObject)Call(f.Popup,"CreateAccessibilityInstance");var controls=f.Controls();
            for(int i=0;i<controls.Count;i++){
                var row=controls[i];var child=tree.GetChild(i);Check(child.Name==row.Text&&child.Description==row.Detail,"help uses the shared screen-reader text model");
                Check(child.Role==(row.Information?AccessibleRole.StaticText:AccessibleRole.PushButton),"help uses the existing accessible control roles");
            }
            f.Key(Keys.Tab);Check((int)Field(f.Popup,"keyboardFocus")==6,"help keyboard navigation starts with the shared back control");f.Key(Keys.Tab);Check((int)Field(f.Popup,"keyboardFocus")==0,"Tab skips explanatory text and reaches rediscovery");
            var nav=new PopupNavigation {Page=page,VisibleChoices=2};var seen=new HashSet<string>();
            for(int offset=0;offset<=6;offset+=2){nav.Offset=offset;var rows=PopupPages.Build(f.Owner,nav,false);foreach(var row in rows)if(row.Id!=6&&row.Id!=7&&row.Id!=8)seen.Add(row.Text);Check(PopupPages.SizeFor(rows).Width==260&&PopupPages.SizeFor(rows).Height<280,"help uses existing bounded pagination");}
            Check(seen.Count==7,"all help content including history is reachable through existing pagination");
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
    [STAThread] static int Main(string[] args){
        var cached=typeof(DeviceSettings).GetField("current",BindingFlags.Static|BindingFlags.NonPublic);object previous=cached.GetValue(null);
        try{
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);NativeDisplay.EnableDpiAwareness();Application.EnableVisualStyles();
            Run("grid pointer, spatial keyboard, focus and fallback region",GridInputAndRegion);Run("grid whole-row scroll, fixed controls and group height animation",GridScrollAndAnimation);
            if(args.Length==1&&args[0]=="--grid")return failures==0?0:1;
            Run("layout preferences, immediate save and popup heights",PanelLayoutPreferences);
            Run("more-options rows",MoreRows);Run("preference order",PreferenceOrder);Run("font size preferences, large layout and 96 DPI integer coordinates",FontSizePreferencesAndLayout);Run("help actions and routes",HelpActions);Run("empty-card entry",EmptyCard);Run("hidden empty-card recovery",HiddenEmptyCard);Run("empty reasons and wrapped bounds",EmptyReasonsAndBounds);Run("hidden detail access",HiddenDetails);Run("tray rules and fixed devices",TrayRulesAndPins);Run("hidden fixed devices",HiddenFixedDevices);Run("native pin consistency",NativePins);Run("tray pagination and identity",TrayPaginationAndIdentity);Run("new and legacy volume defaults",LegacyVolumeDefaults);Run("help accessibility and bounds",HelpAccessibilityAndBounds);
            Console.WriteLine(checks+" checks, "+failures+" failed groups; fake devices, no real clipboard/browser/settings writes");return failures==0?0:1;
        }finally{VisualDesign.HighContrastOverride=null;cached.SetValue(null,previous);}
    }
}
