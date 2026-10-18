using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

// Hidden, owned windows and fake readers/writers only. Configuration changes
// use the transient cache; this suite never saves the user's settings.
static class PopupStateRegressionTests {
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static int checks;
    static object Field(object target,string name){return target.GetType().GetField(name,Private).GetValue(target);}
    static object Call(object target,string name,params object[] values){return target.GetType().GetMethod(name,Private).Invoke(target,values);}
    static void Check(bool value,string name){if(!value)throw new Exception("FAIL: "+name);checks++;}
    static void Reject(Action action,string name){bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}catch(InvalidDataException){rejected=true;}Check(rejected,name);}
    static void Pump(Func<bool> done) {
        var watch=Stopwatch.StartNew();
        while(!done()&&watch.ElapsedMilliseconds<3000){Application.DoEvents();Thread.Sleep(2);}
        Check(done(),"fake controller settles");
    }
    static AudioReading Audio(int volume=55) {
        return new AudioReading {OperationSucceeded=true,ConsoleOutputId="speaker",MultimediaOutputId="speaker",ActiveOutput="speaker",SpeakerId="speaker",HeadsetId="headset",MicrophoneId="mic",SpeakerAvailable=true,HeadsetAvailable=true,MicrophoneAvailable=true,MicrophoneMuted=false,OutputVolumePercent=volume,VolumeEndpointId="speaker",Devices=new AudioDeviceChoice[0]};
    }
    static DpiConfig Dpi() {
        var value=new DpiConfig {ActiveStage=1,TimestampUtc=DateTime.UtcNow};
        foreach(int dpi in new[]{400,800,1200,1600})value.Stages.Add(new DpiStage {XDpi=dpi,YDpi=dpi});
        return value;
    }
    static WirelessDevice Device(string id,string name,int percent) {
        return new WirelessDevice {Id=id,Name=name,Kind="mouse",Percent=percent,Provider="fixture",Transport="2.4G",Online=true,Fresh=true,ConnectionState="connected",SourceQuality="verified",MeasurementState="measured",ConfirmedAtUtc=DateTime.UtcNow.ToString("o")};
    }
    sealed class Fixture:IDisposable {
        internal readonly TrayContext Owner;
        internal readonly Details Popup;
        internal readonly PopupNavigation Navigation;
        internal int VolumeWrites,LastVolume=-1;
        internal string LastEndpoint;
        internal Fixture() {
            DeviceSettings.UseTransientConfiguration(new AudioBindings {ReducedMotion=true,RememberReadings=false,Theme="light"});
            Owner=new TrayContext(false,delegate(bool battery){return new MouseSnapshot {Battery=battery?new BatteryReading {Percent=85,TimestampUtc=DateTime.UtcNow}:null,Dpi=Dpi()};},
                delegate(int stage){throw new Exception("Unexpected DPI write");},delegate{return new HeadsetReading {ConnectionVerified=true,Connected=false};},delegate{return Audio();},false,
                delegate(string endpoint){throw new Exception("Unexpected output write");},delegate(bool muted){throw new Exception("Unexpected microphone write");},
                delegate(string endpoint,int volume){LastEndpoint=endpoint;LastVolume=volume;Interlocked.Increment(ref VolumeWrites);return Audio(volume);},delegate{return false;});
            Pump(delegate{return Owner.Current!=null&&Owner.Audio!=null&&!Owner.IsRefreshing&&!Owner.IsAudioBusy&&!Owner.IsHeadsetRefreshing;});
            Popup=new Details(Owner);typeof(TrayContext).GetField("details",Private).SetValue(Owner,Popup);
            Navigation=(PopupNavigation)Field(Popup,"navigation");
        }
        internal void Page(PopupPage page){Navigation.Page=page;Navigation.Offset=0;Call(Popup,"UpdateLayoutSize");}
        internal List<PopupControl> Controls(){return (List<PopupControl>)Call(Popup,"ControlsForPage");}
        internal PopupControl Control(int id) {
            var result=Controls().Find(delegate(PopupControl row){return row.Id==id;});
            if(result==null)throw new Exception("Missing fixture control "+id);return result;
        }
        internal MouseEventArgs At(PopupControl control,float fraction) {
            float scale=(float)Field(Popup,"dpiScale");
            return new MouseEventArgs(MouseButtons.Left,1,(int)((control.Rect.Left+control.Rect.Width*fraction)*scale),(int)((control.Rect.Top+control.Rect.Height/2)*scale),0);
        }
        internal void Mouse(string name,MouseEventArgs args){typeof(Control).GetMethod(name,Private).Invoke(Popup,new object[]{args});}
        internal void Click(int id){var point=At(Control(id),.5f);Mouse("OnMouseDown",point);Mouse("OnMouseUp",point);}
        internal void FindPageWith(int id){for(int i=0;i<20&&!Controls().Exists(delegate(PopupControl row){return row.Id==id;});i++){if(!Controls().Exists(delegate(PopupControl row){return row.Id==8;}))throw new Exception("Unreachable fixture control "+id);Click(8);}}
        internal void BeginVolume(){Page(PopupPage.Audio);Mouse("OnMouseDown",At(Control(240),.4f));Check((bool)Field(Popup,"draggingVolume")&&Popup.Capture,"volume drag acquires capture");}
        internal void CheckCancelled(string name) {
            Check(!(bool)Field(Popup,"draggingVolume")&&!Popup.Capture&&Navigation.VolumePreview==-1&&Field(Popup,"volumeEndpoint")==null&&Field(Popup,"pressedControl")==null&&(int)Field(Popup,"pressedAction")==-1,name);
        }
        public void Dispose(){Owner.ExitThread();if(!Popup.IsDisposed)Popup.Dispose();Owner.Dispose();}
    }
    static void InputModeAndTooltips() {
        using(var fixture=new Fixture()) {
            fixture.Page(PopupPage.Main);var name=fixture.Control(2);fixture.Mouse("OnMouseMove",fixture.At(name,.5f));
            Check(!String.IsNullOrEmpty((string)Field(fixture.Popup,"lastHoverTip")),"mouse hover starts a contextual tooltip");
            fixture.Click(2);Check(fixture.Navigation.Page==PopupPage.Mouse&&(string)Field(fixture.Popup,"lastHoverTip")==null,"pointer navigation dismisses the previous tooltip immediately");
            fixture.Click(6);var feedback=(PanelFeedback)Call(fixture.Popup,"Feedback");
            Check(fixture.Navigation.Page==PopupPage.Main&&feedback.KeyboardFocus==-1&&!(bool)Field(fixture.Popup,"keyboardFocusVisible"),"returning with the mouse does not leave a keyboard focus outline on the name or card");
            var message=Message.Create(IntPtr.Zero,0,IntPtr.Zero,IntPtr.Zero);
            Call(fixture.Popup,"FocusControl",fixture.Control(2));Call(fixture.Popup,"ProcessCmdKey",message,Keys.Enter);
            Check(fixture.Navigation.Page==PopupPage.Mouse,"keyboard activation opens the same device detail");
            Call(fixture.Popup,"ProcessCmdKey",message,Keys.Escape);feedback=(PanelFeedback)Call(fixture.Popup,"Feedback");
            Check(fixture.Navigation.Page==PopupPage.Main&&feedback.KeyboardFocus==2&&(bool)Field(fixture.Popup,"keyboardFocusVisible"),"keyboard return retains a visible focus on its originating control");
            fixture.Click(2);fixture.Click(6);fixture.Mouse("OnMouseMove",fixture.At(fixture.Control(2),.5f));
            var handle=fixture.Popup.Handle;
            var tree=(AccessibleObject)Call(fixture.Popup,"CreateAccessibilityInstance");var rows=fixture.Controls();int index=rows.FindIndex(delegate(PopupControl row){return row.Id==2;});
            tree.GetChild(index).Select(AccessibleSelection.TakeFocus);feedback=(PanelFeedback)Call(fixture.Popup,"Feedback");
            Check(feedback.KeyboardFocus==2&&(string)Field(fixture.Popup,"lastHoverTip")==null,"assistive focus restores the visible keyboard cue and hides obsolete pointer help");
            Check(fixture.VolumeWrites==0,"focus and tooltip navigation performs no audio write");
        }
    }
    static void DeviceSettingsAndOrganization() {
        string directory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"organization-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string legacyPath=Path.Combine(directory,"legacy.json");File.WriteAllText(legacyPath,"{\"SchemaVersion\":1,\"TraySource\":\"lowest\",\"Theme\":\"light\"}");
        var old=DeviceSettings.LoadBindings(legacyPath);
        Check(old.HiddenDeviceIds.Count==0&&old.DeviceOrder.Count==0&&old.AudioDeviceBindings.Count==0&&!old.ExplicitAudioBindingsOnly,"old schema-1 settings default to unchanged visibility, order and automatic global audio");
        var settings=new AudioBindings {SpeakerEndpointId="global-speaker",HeadsetEndpointId="global-headset",MicrophoneEndpointId="global-mic"};
        settings.HiddenDeviceIds.Add("device-b");settings.DeviceOrder.AddRange(new[]{"device-c","device-a"});settings.AudioDeviceBindings["device-a"]=new AudioEndpointBindings {HeadsetEndpointId="a-output",MicrophoneEndpointId="a-mic"};
        var clone=settings.Clone();clone.HiddenDeviceIds.Clear();clone.DeviceOrder.Reverse();clone.AudioDeviceBindings["device-a"].MicrophoneEndpointId="changed";
        Check(settings.HiddenDeviceIds.Count==1&&settings.DeviceOrder[0]=="device-c"&&settings.AudioDeviceBindings["device-a"].MicrophoneEndpointId=="a-mic","cloning settings isolates presentation collections and each device audio mapping");
        string path=Path.Combine(directory,"settings.json");DeviceSettings.SaveBindings(path,settings);var loaded=DeviceSettings.LoadBindings(path);
        Check(loaded.HiddenDeviceIds[0]=="device-b"&&loaded.DeviceOrder[0]=="device-c"&&loaded.AudioDeviceBindings["device-a"].HeadsetEndpointId=="a-output","bounded visibility, order and device audio mappings round-trip in an isolated file");
        byte[] originalSettings=File.ReadAllBytes(path);var oversized=settings.Clone();
        for(int i=0;i<8;i++)oversized.AudioDeviceBindings["large-"+i]=new AudioEndpointBindings {SpeakerEndpointId=new string('a',4096),HeadsetEndpointId=new string('b',4096),MicrophoneEndpointId=new string('c',4096)};
        Reject(delegate{DeviceSettings.SaveBindings(path,oversized);},"individually valid audio mappings cannot serialize settings beyond the readable 64 KiB limit");
        byte[] unchanged=File.ReadAllBytes(path);bool identical=originalSettings.Length==unchanged.Length;for(int i=0;identical&&i<originalSettings.Length;i++)identical=originalSettings[i]==unchanged[i];
        Check(identical&&Directory.GetFiles(directory,".audio-*.tmp").Length==0,"oversized settings leave the original bytes intact and no staging file behind");
        DeviceSettings.UseTransientConfiguration(loaded);var scoped=DeviceSettings.ForAudioDevice("device-a");var unbound=DeviceSettings.ForAudioDevice("device-other");
        Check(scoped.ExplicitAudioBindingsOnly&&scoped.SpeakerEndpointId==null&&scoped.HeadsetEndpointId=="a-output"&&scoped.MicrophoneEndpointId=="a-mic","scoped audio uses only the selected device's explicit mapping");
        Check(unbound.ExplicitAudioBindingsOnly&&unbound.SpeakerEndpointId==null&&unbound.HeadsetEndpointId==null&&unbound.MicrophoneEndpointId==null&&DeviceSettings.ForAudioDevice(null).HeadsetEndpointId=="global-headset","unbound devices never inherit the global HyperX mapping and global settings stay intact");
        scoped.AudioDeviceBindings["device-a"].MicrophoneEndpointId="local-only";Check(DeviceSettings.Current.AudioDeviceBindings["device-a"].MicrophoneEndpointId=="a-mic","audio request snapshots cannot mutate cached device mappings");
        var invalid=settings.Clone();invalid.HiddenDeviceIds.Add("device-b");Reject(delegate{DeviceSettings.UseTransientConfiguration(invalid);},"duplicate hidden identifiers are rejected");
        invalid=settings.Clone();invalid.DeviceOrder.Add("bad\nidentifier");Reject(delegate{DeviceSettings.UseTransientConfiguration(invalid);},"control characters in ordered identifiers are rejected");
        invalid=settings.Clone();invalid.AudioDeviceBindings[" "]=new AudioEndpointBindings();Reject(delegate{DeviceSettings.UseTransientConfiguration(invalid);},"blank scoped audio identifiers are rejected");
        invalid=settings.Clone();invalid.AudioDeviceBindings["device-a"]=null;Reject(delegate{DeviceSettings.UseTransientConfiguration(invalid);},"null scoped audio bindings are rejected");
        invalid=settings.Clone();invalid.AudioDeviceBindings["device-a"].HeadsetEndpointId=new string('x',4097);Reject(delegate{DeviceSettings.UseTransientConfiguration(invalid);},"oversized scoped endpoint IDs are rejected");
        var bounded=new AudioBindings();for(int i=0;i<128;i++){bounded.HiddenDeviceIds.Add("hidden-"+i);bounded.DeviceOrder.Add("order-"+i);bounded.AudioDeviceBindings["audio-"+i]=new AudioEndpointBindings();}
        DeviceSettings.UseTransientConfiguration(bounded);Check(DeviceSettings.Current.HiddenDeviceIds.Count==128,"128 bounded presentation and mapping entries are accepted");
        bounded.HiddenDeviceIds.Add("overflow");Reject(delegate{DeviceSettings.UseTransientConfiguration(bounded);},"more than 128 hidden identifiers are rejected");bounded.HiddenDeviceIds.Remove("overflow");
        bounded.DeviceOrder.Add("overflow");Reject(delegate{DeviceSettings.UseTransientConfiguration(bounded);},"more than 128 ordered identifiers are rejected");bounded.DeviceOrder.Remove("overflow");
        bounded.AudioDeviceBindings["overflow"]=new AudioEndpointBindings();Reject(delegate{DeviceSettings.UseTransientConfiguration(bounded);},"more than 128 scoped audio mappings are rejected");

        using(var fixture=new Fixture()) {
            var owner=fixture.Owner;owner.NativeMousePresent=false;owner.GenericReady=true;
            var a=Device("device-a","A",70);var b=Device("device-b","B",60);var c=Device("device-c",new string('长',32),50);var original=new[]{a,b,c};owner.WirelessDevices=original;
            var pref=DeviceSettings.Current;pref.DeviceOrder.AddRange(new[]{c.Id,a.Id});pref.TrayDeviceId=c.Id;DeviceSettings.UseTransientConfiguration(pref);
            var layout=WirelessLayout.Build(owner,0,1000);Check(layout.All[0].Id==c.Id&&layout.All[1].Id==a.Id&&layout.All[2].Id==b.Id,"explicit stable order precedes otherwise unchanged discovery order");
            DeviceSettings.SetDeviceHidden(pref,c.Id,true);DeviceSettings.UseTransientConfiguration(pref);owner.ConfigurationChanged(false);layout=WirelessLayout.Build(owner,0,1000);
            var managed=WirelessLayout.Build(owner,0,1000,true);
            Check(layout.All.Count==2&&layout.All[0].Id==a.Id&&managed.All.Count==3&&managed.All[0].Hidden,"hidden devices leave the primary layout and stay restorable in management");
            Check(Present(owner,"auto").Battery.Name!=c.Name&&owner.WirelessDevices==original&&owner.WirelessDevices[2]==c&&c.Fresh,"hiding a pinned device falls back without altering discovery state or reading evidence");
            var nav=new PopupNavigation {Page=PopupPage.Devices,VisibleChoices=2};var before=PopupPages.Build(owner,nav,false);var target=before.Find(delegate(PopupControl row){return row.DeviceKey==a.Id&&row.ActionKey=="manage";});
            DeviceSettings.MoveDevice(pref,a.Id,0,new[]{c.Id,a.Id,b.Id});DeviceSettings.UseTransientConfiguration(pref);var after=PopupPages.Build(owner,nav,false);var same=PopupPages.FindStable(after,target.StableKey);
            Check(same!=null&&same.Id!=target.Id&&PopupPages.SameAction(target,same),"management focus and action identity follow a device when its numeric row changes");
            Check(PopupPages.ReconcileFocus(after,target.StableKey,1)==same,"keyboard focus follows the reordered management device");
            DeviceSettings.MoveDevice(pref,a.Id,1,new[]{a.Id,c.Id,b.Id});Check(pref.DeviceOrder[1]==a.Id,"one-step downward ordering updates the intended stable device");
            DeviceSettings.MoveDevice(pref,a.Id,0,new[]{c.Id,a.Id,b.Id});Check(pref.DeviceOrder[0]==a.Id,"moving upward or to top preserves every device identity");
            DeviceSettings.SetDeviceHidden(pref,c.Id,false);DeviceSettings.UseTransientConfiguration(pref);Check(WirelessLayout.Build(owner,0,1000).All.Exists(delegate(WirelessCard card){return card.Id==c.Id;}),"restoring visibility returns the original device to the primary layout");
            DeviceSettings.SetDeviceHidden(pref,"offline-hidden",true);DeviceSettings.UseTransientConfiguration(pref);managed=WirelessLayout.Build(owner,0,1000,true);
            Check(managed.All.Exists(delegate(WirelessCard card){return card.Id=="offline-hidden"&&card.Hidden;}),"a hidden device remains restorable while disconnected and absent from discovery");
            var options=new PopupNavigation {Page=PopupPage.ManageDevice,SelectedDeviceKey="offline-hidden",SelectedDeviceName="隐藏设备",VisibleChoices=2};
            var optionRows=PopupPages.Build(owner,options,false);var show=optionRows.Find(delegate(PopupControl row){return row.Id==252;});
            Check(show!=null&&show.Enabled&&!show.Checked&&show.DeviceKey=="offline-hidden","hidden offline devices expose a stable enabled restore toggle on the first management page");
            Check(PopupPages.SizeFor(optionRows).Width==260&&PopupPages.SizeFor(optionRows).Height<260,"management remains paginated and 260 pixels wide in a small work area");
        }
    }
    static void DeviceAudioNavigation() {
        using(var fixture=new Fixture()) {
            fixture.Owner.NativeMousePresent=false;fixture.Owner.GenericReady=true;var headset=Device("generic-headset","Generic headset",80);headset.Kind="headset";fixture.Owner.WirelessDevices=new[]{headset};
            fixture.Navigation.SelectedDeviceKey=headset.Id;fixture.Navigation.SelectedDeviceName=headset.Name;fixture.Page(PopupPage.Device);fixture.FindPageWith(200);fixture.Click(200);
            Check(fixture.Navigation.Page==PopupPage.Audio&&fixture.Navigation.AudioDeviceKey==headset.Id&&fixture.Owner.AudioScopeDeviceId==headset.Id&&!fixture.Owner.AudioControlsAvailable,"generic headset entry switches scope and disables stale global controls immediately");
            Check(fixture.Control(100).DeviceKey==headset.Id&&!fixture.Control(100).Selected&&!fixture.Control(100).Enabled,"unconfirmed scoped audio rows cannot appear as selected global outputs");
            Pump(delegate{return !fixture.Owner.IsAudioBusy&&fixture.Owner.AudioControlsAvailable;});fixture.Popup.ShowPreferences();
            Check(fixture.Owner.AudioScopeDeviceId==null,"temporarily leaving audio for preferences returns controls to global scope");fixture.Click(6);
            Check(fixture.Navigation.Page==PopupPage.Audio&&fixture.Navigation.AudioDeviceKey==headset.Id&&fixture.Owner.AudioScopeDeviceId==headset.Id,"return navigation restores the original explicit device audio scope");
            Pump(delegate{return !fixture.Owner.IsAudioBusy&&fixture.Owner.AudioControlsAvailable;});fixture.Click(203);
            Check(fixture.Navigation.Page==PopupPage.Bindings&&fixture.Control(231).Value=="未绑定","the device binding page starts explicitly unbound");
            fixture.Click(231);Check(fixture.Navigation.Page==PopupPage.PickBinding&&fixture.Control(300).Text=="未绑定"&&fixture.Control(300).DeviceKey==headset.Id,"the per-device picker exposes an explicit empty binding with scoped action identity");
            fixture.Click(6);Check(fixture.Owner.AudioScopeDeviceId==headset.Id,"returning from picker retains the original device scope");fixture.Click(6);fixture.Click(6);
            Check(fixture.Navigation.Page==PopupPage.Device&&fixture.Navigation.AudioDeviceKey==null&&fixture.Owner.AudioScopeDeviceId==null,"leaving the audio page group returns control to global scope");
            Pump(delegate{return !fixture.Owner.IsAudioBusy;});Check(fixture.VolumeWrites==0,"scoped navigation performs no physical audio write or settings save");
        }
    }
    static void MainSystemVolume() {
        using(var fixture=new Fixture()) {
            fixture.Page(PopupPage.Main);var slider=fixture.Control(240);var originalName=fixture.Control(2);string originalIdentity=originalName.StableKey;
            Check(fixture.Controls().FindAll(delegate(PopupControl row){return row.Id==240&&row.Slider;}).Count==1&&slider.Enabled&&slider.Value=="55%","main popup exposes one directly usable global system volume slider");
            var headerPoint=fixture.At(slider,.25f);fixture.Mouse("OnMouseDown",headerPoint);int preview=fixture.Navigation.VolumePreview;
            Check(fixture.VolumeWrites==0&&fixture.Control(240).Value==preview+"%"&&((PanelFeedback)Call(fixture.Popup,"Feedback")).VolumePreview==preview,"drag preview updates main presentation immediately without an audio write");
            fixture.Mouse("OnMouseUp",headerPoint);Pump(delegate{return !fixture.Owner.IsAudioBusy;});
            Check(fixture.VolumeWrites==1&&fixture.LastEndpoint=="speaker"&&fixture.LastVolume==preview,"main slider release commits its original global output endpoint exactly once");
            Call(fixture.Popup,"FocusControl",fixture.Control(240));var message=Message.Create(IntPtr.Zero,0,IntPtr.Zero,IntPtr.Zero);Call(fixture.Popup,"ProcessCmdKey",message,Keys.Right);
            Pump(delegate{return !fixture.Owner.IsAudioBusy;});Check(fixture.VolumeWrites==2&&fixture.LastVolume==Math.Min(100,preview+2),"main volume accepts keyboard two-percent adjustment through the existing confirmed queue");
            var tree=(AccessibleObject)Call(fixture.Popup,"CreateAccessibilityInstance");var rows=fixture.Controls();var child=tree.GetChild(rows.FindIndex(delegate(PopupControl row){return row.Id==240;}));
            child.Value="67%";Pump(delegate{return !fixture.Owner.IsAudioBusy;});Check(fixture.VolumeWrites==3&&fixture.LastVolume==67&&child.Role==AccessibleRole.Slider,"main volume exposes bounded percentage adjustment to assistive clients");
            fixture.Mouse("OnMouseDown",fixture.At(fixture.Control(240),.8f));fixture.Popup.Capture=false;fixture.CheckCancelled("main slider capture loss clears the same pointer transaction");fixture.Click(1);
            Check(fixture.Navigation.Page==PopupPage.More&&fixture.VolumeWrites==3,"a cancelled main volume drag cannot swallow the next more-options click");fixture.Click(6);
            Check(fixture.Control(2).StableKey==originalIdentity,"adding volume does not change the device header's hit or action identity");
            var external=Audio(24);external.ConsoleOutputId="other-output";external.VolumeEndpointId="other-output";external.Devices=new[]{new AudioDeviceChoice {Id="other-output",Name="外部切换后的系统输出",Flow=0}};fixture.Owner.Audio=external;fixture.Popup.SyncDevices();
            Check(fixture.Control(240).Value=="24%"&&fixture.Control(240).Detail=="外部切换后的系统输出","external output and volume changes refresh the main slider's current name and percentage");
            fixture.Mouse("OnMouseDown",fixture.At(fixture.Control(240),.7f));fixture.Owner.Audio=Audio(32);fixture.Popup.SyncDevices();fixture.CheckCancelled("main volume cancels when its actual output identity changes");
            fixture.Mouse("OnMouseUp",fixture.At(fixture.Control(240),.7f));Check(fixture.VolumeWrites==3,"main drag never sends a stale request to the newly selected output");
            var noOutput=new AudioReading {OperationSucceeded=true,Devices=new AudioDeviceChoice[0]};fixture.Owner.Audio=noOutput;fixture.Popup.SyncDevices();slider=fixture.Control(240);
            Check(!slider.Enabled&&slider.Value=="不可用"&&PopupPages.Hit(fixture.Controls(),new PointF(slider.Rect.Left+20,slider.Rect.Top+20))==null,"no default output disables the main volume pointer target explicitly");
            rows=fixture.Controls();child=tree.GetChild(rows.FindIndex(delegate(PopupControl row){return row.Id==240;}));child.Value="90%";Check(fixture.VolumeWrites==3&&(child.State&AccessibleStates.Unavailable)!=0,"missing output rejects accessible writes too");
            fixture.Owner.Audio=Audio(55);fixture.Popup.SyncDevices();var blocked=typeof(TrayContext).GetField("audioNeedsReconcile",Private);blocked.SetValue(fixture.Owner,true);
            Check(!fixture.Control(240).Enabled,"unknown audio state disables main volume until a complete read is confirmed");blocked.SetValue(fixture.Owner,false);

            fixture.Owner.NativeMousePresent=false;fixture.Owner.GenericReady=true;fixture.Owner.WirelessDevices=new[]{Device("one","鼠标",80),Device("two","键盘",60),Device("three","耳机",40)};
            var layout=WirelessLayout.Build(fixture.Owner,0,260);var controls=PopupPages.Build(fixture.Owner,new PopupNavigation(),false,layout);int count=controls.FindAll(delegate(PopupControl row){return row.Id==240;}).Count;
            Check(count==1&&layout.Paged&&layout.Size.Height<=260&&layout.VolumeRect.Bottom<=layout.Footer,"small-screen wireless pagination reserves space for a single shared volume row and footer");
            bool disjoint=true;for(int a=0;a<controls.Count;a++)for(int b=a+1;b<controls.Count;b++)disjoint&=!controls[a].Rect.IntersectsWith(controls[b].Rect);
            Check(disjoint&&layout.Cards.TrueForAll(delegate(WirelessCard card){return card.Height==60;}),"shared volume keeps compact card heights, transparent gaps and disjoint hit regions");
            string output=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"main-volume-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
            foreach(float scale in new[]{1f,1.25f,1.5f,1.75f,2f})foreach(bool dark in new[]{false,true})foreach(bool highContrast in new[]{false,true}) {
                VisualDesign.HighContrastOverride=highContrast;var size=UiState.ScaleSize(layout.Size,scale);
                using(var image=new Bitmap(size.Width,size.Height))using(var graphics=Graphics.FromImage(image)) {
                    graphics.Clear(Color.Transparent);graphics.ScaleTransform(scale,scale);CompactRenderer.PaintWireless(graphics,fixture.Owner,layout,dark,new PanelFeedback {KeyboardFocus=240});
                    Check(image.GetPixel((int)(130*scale),(int)((layout.VolumeTop-4)*scale)).A==0,"volume surface preserves its transparent card gap at scale "+scale);
                    image.Save(Path.Combine(output,"main-"+scale.ToString(System.Globalization.CultureInfo.InvariantCulture)+(dark?"-dark":"-light")+(highContrast?"-contrast":"")+".png"));
                }
            }
            VisualDesign.HighContrastOverride=null;Console.WriteLine("Main volume previews: "+output);
        }
    }
    static void CaptureCancellation() {
        using(var fixture=new Fixture()) {
            fixture.BeginVolume();fixture.Popup.Capture=false;
            fixture.CheckCancelled("capture loss atomically clears drag, preview, endpoint and pressed target");
            fixture.Click(203);
            Check(fixture.Navigation.Page==PopupPage.Bindings&&fixture.VolumeWrites==0,"next button click after capture loss reaches bindings without a volume write");

            fixture.BeginVolume();var slider=fixture.Control(240);var release=fixture.At(slider,.8f);
            fixture.Mouse("OnMouseMove",release);int expected=fixture.Navigation.VolumePreview;
            fixture.Mouse("OnMouseUp",release);
            Pump(delegate{return !fixture.Owner.IsAudioBusy;});
            Check(fixture.VolumeWrites==1&&fixture.LastEndpoint=="speaker"&&fixture.LastVolume==expected&&expected>=0,"normal release commits the captured endpoint and value exactly once");
            fixture.CheckCancelled("normal release also clears all pointer state");

            fixture.BeginVolume();var cancel=Message.Create(fixture.Popup.Handle,0x001F,IntPtr.Zero,IntPtr.Zero);
            Call(fixture.Popup,"WndProc",cancel);
            fixture.CheckCancelled("WM_CANCELMODE abandons the drag without waiting for mouse-up");
            fixture.Mouse("OnMouseUp",fixture.At(fixture.Control(240),.5f));
            Check(fixture.VolumeWrites==1,"mouse-up after cancelled input cannot submit a stale preview");

            fixture.BeginVolume();var changed=Audio();changed.VolumeEndpointId="external-output";changed.ConsoleOutputId="external-output";fixture.Owner.Audio=changed;fixture.Popup.SyncDevices();
            fixture.CheckCancelled("a confirmed external output switch cancels a drag bound to the previous endpoint");
            fixture.Mouse("OnMouseUp",fixture.At(fixture.Control(240),.5f));
            Check(fixture.VolumeWrites==1,"releasing after an output switch never submits to the former endpoint");fixture.Owner.Audio=Audio();fixture.Popup.SyncDevices();

            fixture.BeginVolume();changed=Audio();changed.VolumeEndpointId="external-output";changed.ConsoleOutputId="external-output";fixture.Owner.Audio=changed;
            fixture.Mouse("OnMouseUp",fixture.At(fixture.Control(240),.5f));
            Check(fixture.VolumeWrites==1,"mouse-up rechecks the current endpoint even before a device repaint");fixture.Owner.Audio=Audio();fixture.Popup.SyncDevices();

            fixture.BeginVolume();fixture.Popup.CloseAnimated();
            fixture.CheckCancelled("closing the popup cancels the drag before disposal");
            Check(fixture.VolumeWrites==1,"closing does not commit an abandoned volume preview");
        }
    }
    static void PinRendering() {
        using(var fixture=new Fixture()) {
            fixture.Page(PopupPage.Mouse);var rows=fixture.Controls();var pin=fixture.Control(218);
            var handle=fixture.Popup.Handle;
            var tree=(AccessibleObject)Call(fixture.Popup,"CreateAccessibilityInstance");
            var child=tree.GetChild(rows.FindIndex(delegate(PopupControl row){return row.StableKey==pin.StableKey;}));
            child.Select(AccessibleSelection.TakeFocus);
            Check((child.State&AccessibleStates.Checked)==0,"accessible pin begins unchecked");
            Check((bool)Call(fixture.Popup,"RenderLayeredFrame"),"hidden owned popup publishes its initial frame");
            string before=(string)Field(fixture.Popup,"lastPresentedKey");
            var pref=DeviceSettings.Current;pref.TrayDeviceId="native:mouse";DeviceSettings.UseTransientConfiguration(pref);
            fixture.Owner.ConfigurationChanged(false);
            Check(fixture.Control(218).Checked&&(child.State&AccessibleStates.Checked)!=0,"pin state updates while accessible focus stays on the same action");
            Check((bool)Call(fixture.Popup,"RenderLayeredFrame")&&(string)Field(fixture.Popup,"lastPresentedKey")!=before,"changing only the saved pin invalidates the layered frame cache");
            Check((int)Field(fixture.Popup,"keyboardFocus")==218&&(int)Field(fixture.Popup,"pressedAction")==-1,"pin refresh does not rely on pointer feedback or a focus change");
            string pinned=(string)Field(fixture.Popup,"lastPresentedKey");pref.TrayDeviceId=null;DeviceSettings.UseTransientConfiguration(pref);
            fixture.Owner.ConfigurationChanged(false);Call(fixture.Popup,"RenderLayeredFrame");
            Check(!fixture.Control(218).Checked&&(child.State&AccessibleStates.Checked)==0&&(string)Field(fixture.Popup,"lastPresentedKey")!=pinned,"unpinning refreshes the same focused control immediately");
        }
    }
    static TrayView Present(TrayContext owner,string source){return (TrayView)Call(owner,"WirelessTray",source);}
    static void TraySelectionAndTooltips() {
        using(var fixture=new Fixture()) {
            var owner=fixture.Owner;owner.NativeMousePresent=false;owner.GenericReady=true;
            var a=Device("device-a","Fixture A",50);var b=Device("device-b","Fixture B",50);owner.WirelessDevices=new[]{b,a};
            var pref=DeviceSettings.Current;pref.TraySource="lowest";DeviceSettings.UseTransientConfiguration(pref);
            Check(Present(owner,"lowest").Battery.Name==b.Name,"lowest initially selects the first equal reading");
            owner.WirelessDevices=new[]{a,b};Check(Present(owner,"lowest").Battery.Name==b.Name,"lowest keeps an equal previous choice stable after reorder");
            pref.TrayDeviceId=a.Id;DeviceSettings.UseTransientConfiguration(pref);
            Check(Present(owner,"lowest").Battery.Name==a.Name,"an available explicit pin wins over the previous equal lowest choice");
            a.Percent=75;Check(Present(owner,"lowest").Battery.Name==a.Name,"an available pin also wins over a lower unpinned reading");
            a.Fresh=false;Check(Present(owner,"lowest").Battery.Name==b.Name,"an unavailable pin yields to a confirmed current reading");
            a.Fresh=true;Check(Present(owner,"lowest").Battery.Name==a.Name,"a recovered explicit pin wins again");
            pref.TrayDeviceId=null;DeviceSettings.UseTransientConfiguration(pref);
            Check(Present(owner,"lowest").Battery.Name==b.Name,"unpinning restores ordinary lowest selection");

            var bluetooth=Device("bt-fixture","Bluetooth",55);bluetooth.Kind="keyboard";bluetooth.Provider="bluetooth";bluetooth.Transport="Bluetooth";
            bluetooth.Fresh=false;bluetooth.SourceQuality="system_property";bluetooth.MeasurementState="system_property";bluetooth.ConfirmedAtUtc=null;bluetooth.QueriedAtUtc=DateTime.UtcNow.ToString("o");
            owner.WirelessDevices=new[]{bluetooth};pref.TraySource="auto";pref.DeviceAliases[bluetooth.Id]="桌面键盘";DeviceSettings.UseTransientConfiguration(pref);
            var system=Present(owner,"auto");var view=WirelessLayout.View(bluetooth);
            Check(system.Tooltip==view.TooltipFor("桌面键盘")&&system.Tooltip.Contains("系统提供")&&system.Tooltip.Contains("最近查询 "+view.QueryText)&&!system.Tooltip.Contains("上次"),"Bluetooth tray preserves alias, system source and actual query time through the shared formatter");
            bluetooth.QueriedAtUtc=null;system=Present(owner,"auto");
            Check(system.Tooltip.Contains("系统提供")&&!system.Tooltip.Contains("最近查询")&&!system.Tooltip.Contains("上次 ）"),"missing Bluetooth query time does not invent an empty historical timestamp");

            var history=Device("history","历史鼠标",33);history.Fresh=false;history.ConfirmedAtUtc=DateTime.UtcNow.AddHours(-2).ToString("o");owner.WirelessDevices=new[]{history};
            var historical=Present(owner,"auto");Check(historical.Tooltip==WirelessLayout.View(history).Tooltip&&historical.Tooltip.Contains("上次"),"historical tray values preserve the original confirmation time");
            history.ConfirmedAtUtc=null;Check(Present(owner,"auto").Tooltip.Contains("历史读数，时间未知"),"history without a timestamp stays explicitly unknown in the tray");
            var current=Device("current","当前鼠标",80);current.Charging=true;current.ChargingCheckedAtUtc=DateTime.UtcNow.ToString("o");owner.WirelessDevices=new[]{current};
            var live=Present(owner,"auto");Check(live.Tooltip==WirelessLayout.View(current).Tooltip&&live.Tooltip.Contains("充电中")&&!live.Tooltip.Contains("上次"),"a current tray measurement keeps its current charging qualifier");
            var extras=new List<WirelessDevice>{bluetooth};for(int i=0;i<8;i++)extras.Add(Device("extra-"+i,"Extra "+i,90));owner.WirelessDevices=extras.ToArray();
            Check(Present(owner,"keyboard").Tooltip.StartsWith("桌面键盘 55%（系统提供）")&&Present(owner,"keyboard").Tooltip.Length<=63,"selected-device qualifier is retained before the bounded secondary device summary");
            Check(!fixture.Popup.Visible&&!owner.DiagnosticsEnabled&&fixture.VolumeWrites==0,"tray checks use no visible UI, persistence or physical writes");
        }
    }
    [STAThread] static int Main() {
        var cached=typeof(DeviceSettings).GetField("current",BindingFlags.Static|BindingFlags.NonPublic);object previous=cached.GetValue(null);
        try {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            NativeDisplay.EnableDpiAwareness();Application.EnableVisualStyles();
            CaptureCancellation();PinRendering();TraySelectionAndTooltips();InputModeAndTooltips();DeviceSettingsAndOrganization();DeviceAudioNavigation();MainSystemVolume();
            Console.WriteLine("PASS "+checks+" popup/tray state regression checks; hidden owned windows, transient settings and fake hardware only");
            return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{VisualDesign.HighContrastOverride=null;cached.SetValue(null,previous);}
    }
}
