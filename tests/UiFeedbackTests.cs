using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

// Injected input, snapshots and writers only. Never enumerate HID or real audio.
static class UiFeedbackTests {
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    const string A="device-111111111111111111111111",B="device-222222222222222222222222";
    const string PhysA="device-aaaaaaaaaaaaaaaaaaaaaaaa",PhysB="device-bbbbbbbbbbbbbbbbbbbbbbbb";
    static int checks,failures;
    static string output;
    static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
    static object Field(object owner,string name){return owner.GetType().GetField(name,Private).GetValue(owner);}
    static void Set(object owner,string name,object value){owner.GetType().GetField(name,Private).SetValue(owner,value);}
    static object Call(object owner,string name,params object[] args){return owner.GetType().GetMethod(name,Private|BindingFlags.Public).Invoke(owner,args);}
    static void Run(string name,Action action){try{action();Console.WriteLine("PASS "+name);}catch(Exception e){failures++;Console.Error.WriteLine("FAIL "+name+": "+(e.InnerException??e));}}
    static void Pump(Func<bool> done){DateTime until=DateTime.UtcNow.AddSeconds(5);while(!done()&&DateTime.UtcNow<until){Application.DoEvents();Thread.Sleep(5);}Check(done(),"fake audio operation finished");}
    static void Collapsed(AudioBindings settings,bool value){var field=typeof(AudioBindings).GetField("VolumeCollapsed");Check(field!=null,"VolumeCollapsed is a persisted preference");field.SetValue(settings,value);}
    static AudioDeviceChoice Endpoint(string id,int? form,int flow=0){var choice=new AudioDeviceChoice {Id=id,Name="Endpoint "+id,Flow=flow};var field=typeof(AudioDeviceChoice).GetField("FormFactor");Check(field!=null,"audio snapshots retain endpoint form factors");field.SetValue(choice,form);return choice;}
    static AudioReading Audio(){return new AudioReading {OperationSucceeded=true,TimestampUtc=DateTime.UtcNow,SpeakerAvailable=true,SpeakerId="speaker",SpeakerName="Speakers",HeadsetAvailable=true,HeadsetId="headphones",HeadsetName="Headphones",MicrophoneAvailable=true,MicrophoneId="mic",MicrophoneMuted=false,ConsoleOutputId="speaker",MultimediaOutputId="speaker",VolumeEndpointId="speaker",OutputVolumePercent=55};}
    static WirelessDevice Mouse(string id,string physical,int percent){return new WirelessDevice {Id=id,PhysicalId=physical,Kind="mouse",Name=id==A?"Mouse A":"Mouse B",Percent=percent,Online=true,Fresh=true,ConnectionState="connected",MeasurementState="current",ConfirmedAtUtc=DateTime.UtcNow.ToString("o")};}
    sealed class Fixture:IDisposable {
        internal readonly TrayContext Owner;
        internal readonly Details Popup;
        internal readonly PopupNavigation Navigation;
        internal DateTime Now=DateTime.UtcNow;
        internal int Reads,VolumeWrites,LastVolume=-1,OutputWrites;
        internal readonly ManualResetEvent VolumeGate=new ManualResetEvent(true);
        internal Fixture(AudioBindings settings=null,Func<AudioReading> readAudio=null,Func<string,AudioReading> switchAudio=null){
            DeviceSettings.UseTransientConfiguration(settings??new AudioBindings {RememberReadings=false,ReducedMotion=true,TraySource="auto",VolumeCollapsed=false});
            Owner=new TrayContext(false,delegate(bool battery){Interlocked.Increment(ref Reads);return new MouseSnapshot {Battery=new BatteryReading {Percent=80,TimestampUtc=Now}};},delegate(int stage){throw new Exception("No DPI writes");},null,delegate{return readAudio==null?Audio():readAudio();},false,
                delegate(string target){Interlocked.Increment(ref OutputWrites);if(switchAudio!=null)return switchAudio(target);var value=Audio();value.ActiveOutput=target;return value;},delegate(bool mute){throw new Exception("No microphone writes");},
                delegate(string endpoint,int value){VolumeGate.WaitOne();LastVolume=value;Interlocked.Increment(ref VolumeWrites);var audio=Audio();audio.OutputVolumePercent=value;return audio;},delegate{return false;},null,null,delegate{return Now;},null,
                delegate(AudioBindings pref){DeviceSettings.SaveBindings(Path.Combine(output,"ui-feedback-fixture-settings.json"),pref);DeviceSettings.UseTransientConfiguration(pref);});
            Pump(delegate{return !Owner.IsRefreshing&&!Owner.IsAudioBusy&&Owner.Audio!=null;});
            ((System.Windows.Forms.Timer)Field(Owner,"refresh")).Stop();
            Popup=new Details(Owner);Set(Owner,"details",Popup);Navigation=(PopupNavigation)Field(Popup,"navigation");
        }
        internal List<PopupControl> Controls(){return (List<PopupControl>)Call(Popup,"ControlsForPage");}
        internal void MouseEvent(string name,PointF point){float scale=(float)Field(Popup,"dpiScale");typeof(Control).GetMethod(name,Private).Invoke(Popup,new object[]{new MouseEventArgs(MouseButtons.Left,1,(int)(point.X*scale),(int)(point.Y*scale),0)});}
        internal void Click(PopupControl control){var point=new PointF(control.Rect.X+control.Rect.Width/2,control.Rect.Y+control.Rect.Height/2);MouseEvent("OnMouseDown",point);MouseEvent("OnMouseUp",point);}
        internal void Activity(string physical,bool native=false){Owner.HandleMouseActivity(new MouseActivitySource(physical,native,native?(ushort)0x373B:(ushort)0x046D,native?(ushort)0x1278:(ushort)0xC547));}
        public void Dispose(){VolumeGate.Set();Owner.ExitThread();if(!Popup.IsDisposed)Popup.Dispose();Owner.Dispose();VolumeGate.Dispose();}
    }
    static void Configuration(){
        string path=Path.Combine(output,"ui-feedback-settings.json");
        if(File.Exists(path))File.Delete(path);
        Check(DeviceSettings.LoadBindings(path).TraySource=="active","a missing configuration defaults to active");
        foreach(string source in new[]{"auto","mouse","headset","keyboard","lowest"}){
            File.WriteAllText(path,"{\"SchemaVersion\":1,\"TraySource\":\""+source+"\"}");Check(DeviceSettings.LoadBindings(path).TraySource==source,"old explicit "+source+" stays unchanged");
        }
        File.WriteAllText(path,"{\"SchemaVersion\":1}");var old=DeviceSettings.LoadBindings(path);Check(old.TraySource=="mouse","old omitted TraySource retains mouse default");
        var pref=new AudioBindings {TraySource="active"};Collapsed(pref,true);DeviceSettings.SaveBindings(path,pref);var loaded=DeviceSettings.LoadBindings(path);
        Check(loaded.TraySource=="active"&&(bool)typeof(AudioBindings).GetField("VolumeCollapsed").GetValue(loaded.Clone()),"active and collapsed survive a save/load and clone");
        Collapsed(old,false);Check(!(bool)typeof(AudioBindings).GetField("VolumeCollapsed").GetValue(old),"old files expand volume by default");
    }
    static void ActiveTray(){
        using(var f=new Fixture(new AudioBindings {TraySource="active",RememberReadings=false,ReducedMotion=true})){
            f.Owner.NativeMousePresent=false;var a=Mouse(A,PhysA,71);var b=Mouse(B,PhysB,42);f.Owner.WirelessDevices=new[]{a,b};Call(f.Owner,"UpdateIcon");
            Check((string)Field(f.Owner,"trayDeviceKey")==A,"no activity falls back to auto");int reads=f.Reads;
            f.Activity(PhysB);Check((string)Field(f.Owner,"trayDeviceKey")==B,"fresh activity selects its physical mouse even when Record returns false");
            int updates=(int)Field(f.Owner,"iconUpdates");for(int i=0;i<200;i++)f.Activity(PhysB);
            Check((int)Field(f.Owner,"iconUpdates")==updates&&f.Reads==reads,"repeated fresh activity performs no icon redraw or device read");
            f.Activity(PhysA);f.Now=f.Now.AddMilliseconds(2999);f.Activity(PhysA);Check((string)Field(f.Owner,"trayDeviceKey")==B,"a change is held for at least three seconds");
            f.Now=f.Now.AddMilliseconds(1);f.Activity(PhysA);Check((string)Field(f.Owner,"trayDeviceKey")==A,"another mouse can take over at three seconds");
            var pref=DeviceSettings.Current;pref.TrayDeviceId=B;DeviceSettings.UseTransientConfiguration(pref);Call(f.Owner,"UpdateIcon");f.Now=f.Now.AddSeconds(3);f.Activity(PhysA);Check((string)Field(f.Owner,"trayDeviceKey")==B,"fixed device has highest priority");
            pref.TrayDeviceId=null;DeviceSettings.UseTransientConfiguration(pref);f.Owner.WirelessDevices=new[]{b};Call(f.Owner,"UpdateIcon");Check((string)Field(f.Owner,"trayDeviceKey")==B,"removed active mouse falls back to auto");
            f.Owner.WirelessDevices=new[]{a,b};f.Now=f.Now.AddSeconds(3);f.Activity(PhysA);a.Percent=null;Call(f.Owner,"UpdateIcon");Check((string)Field(f.Owner,"trayDeviceKey")==B,"active mouse without battery falls back to auto");a.Percent=71;
            Set(f.Owner,"sessionLocked",true);f.Now=f.Now.AddSeconds(3);f.Activity(PhysA);Check((string)Field(f.Owner,"trayDeviceKey")==B,"lock prevents activity-driven tray updates");Set(f.Owner,"sessionLocked",false);
            f.Owner.Current=new BatteryReading {Percent=80,TimestampUtc=DateTime.UtcNow};f.Activity(PhysA,true);Check((string)Field(f.Owner,"trayDeviceKey")=="native:mouse","exact native ATK activity maps to its card");
            var rows=PopupPages.Build(f.Owner,new PopupNavigation {Page=PopupPage.ChoosePreference,PreferenceAction=211,VisibleChoices=20},false);Check(rows.Exists(delegate(PopupControl c){return c.Text=="当前使用的设备"&&c.Detail.Contains("USB");}),"popup describes the USB-only active source");
        }
    }
    static void VolumeLayouts(){
        foreach(bool wireless in new[]{false,true})using(var f=new Fixture()){
            f.Owner.GenericReady=wireless;Call(f.Popup,"UpdateLayoutSize");var expanded=f.Controls();var slider=expanded.Find(delegate(PopupControl c){return c.Id==240;});var collapse=expanded.Find(delegate(PopupControl c){return c.Text=="收起系统音量";});
            Check(slider!=null&&slider.Slider&&collapse!=null&&!slider.Rect.IntersectsWith(collapse.Rect),"collapse and slider hit regions do not overlap in "+wireless);
            int height=f.Popup.ClientSize.Height;f.Click(collapse);var rows=f.Controls();Check(!rows.Exists(delegate(PopupControl c){return c.Slider;}),"collapsed main panel contains no slider in "+wireless);
            var expand=rows.Find(delegate(PopupControl c){return c.Text=="展开系统音量";});Check(expand!=null&&expand.Value=="55%"&&f.Popup.ClientSize.Height<height,"collapsed strip retains live percent and reduces panel height");
            Call(f.Popup,"UpdateFallbackRegion");float scale=(float)Field(f.Popup,"dpiScale");Check(f.Popup.Region.IsVisible((expand.Rect.X+20)*scale,(expand.Rect.Y+10)*scale),"fallback mask includes collapsed strip");
            var footer=rows.Find(delegate(PopupControl c){return c.Id==0;});Check(footer.Rect.Top>=expand.Rect.Bottom&&footer.Rect.Bottom*scale<=f.Popup.ClientSize.Height+1,"footer follows the collapsed strip");
            f.Owner.Audio.OutputVolumePercent=23;f.Popup.SyncDevices();Check(f.Controls().Find(delegate(PopupControl c){return c.Text=="展开系统音量";}).Value=="23%","external volume updates collapsed percent");
            int before=f.VolumeWrites;var oldTrack=PopupPages.VolumeTrack(slider.Rect);var oldPoint=new PointF(oldTrack.X+100,oldTrack.Y+oldTrack.Height/2);f.MouseEvent("OnMouseDown",oldPoint);f.MouseEvent("OnMouseUp",oldPoint);
            Check(f.VolumeWrites==before&&!f.Controls().Exists(delegate(PopupControl c){return c.Id==240&&c.Slider;}),"click at old slider x leaves volume collapsed without a volume write");
            var pref=DeviceSettings.Current;Collapsed(pref,true);DeviceSettings.UseTransientConfiguration(pref);f.Owner.ConfigurationChanged(false);
            var message=Message.Create(IntPtr.Zero,0,IntPtr.Zero,IntPtr.Zero);expand=f.Controls().Find(delegate(PopupControl c){return c.Text=="展开系统音量";});Call(f.Popup,"FocusControl",expand);Call(f.Popup,"ProcessCmdKey",message,Keys.Space);
            Check(f.Controls().Exists(delegate(PopupControl c){return c.Slider;}),"keyboard space expands volume");
            pref=DeviceSettings.Current;Collapsed(pref,true);DeviceSettings.UseTransientConfiguration(pref);f.Owner.ConfigurationChanged(false);var handle=f.Popup.Handle;var tree=(AccessibleObject)Call(f.Popup,"CreateAccessibilityInstance");rows=f.Controls();int index=rows.FindIndex(delegate(PopupControl c){return c.Text=="展开系统音量";});
            Check(tree.GetChild(index).Role==AccessibleRole.PushButton&&tree.GetChild(index).Name=="展开系统音量","collapse strip has a clear accessible button name");tree.GetChild(index).Value="95%";Check(f.VolumeWrites==before,"accessible collapsed button cannot write volume");tree.GetChild(index).DoDefaultAction();Check(f.Controls().Exists(delegate(PopupControl c){return c.Slider;}),"assistive default action expands volume");
        }
    }
    static void VolumeDuringDrag(){
        using(var f=new Fixture()){
            var slider=f.Controls().Find(delegate(PopupControl c){return c.Id==240;});var point=new PointF(slider.Rect.X+130,slider.Rect.Y+35);f.MouseEvent("OnMouseDown",point);Check((bool)Field(f.Popup,"draggingVolume"),"fake drag starts");
            var pref=DeviceSettings.Current;Collapsed(pref,true);DeviceSettings.UseTransientConfiguration(pref);f.Owner.ConfigurationChanged(false);f.MouseEvent("OnMouseUp",point);
            Check(f.VolumeWrites==0&&f.Navigation.VolumePreview==-1&&!(bool)Field(f.Popup,"draggingVolume"),"collapsing cancels uncommitted pointer preview");
            f.VolumeGate.Reset();f.Owner.SelectOutputVolume("speaker",35);f.Owner.SelectOutputVolume("speaker",64);Check(f.Owner.DesiredVolume==64,"queue contains the latest submitted volume");
            Collapsed(pref,false);DeviceSettings.UseTransientConfiguration(pref);f.Owner.ConfigurationChanged(false);f.Click(f.Controls().Find(delegate(PopupControl c){return c.Text=="收起系统音量";}));
            Check(f.Owner.DesiredVolume==64,"collapse keeps already submitted audio queue");f.VolumeGate.Set();Pump(delegate{return !f.Owner.IsAudioBusy&&!f.Owner.DesiredVolume.HasValue;});Check(f.LastVolume==64&&f.Owner.Audio.OutputVolumePercent==64,"submitted last value is committed and shown after collapse");
        }
    }
    static void AudioDetection(){
        bool ambiguous;var endpoints=new List<AudioEndpointInfo> {new AudioEndpointInfo {Id="headphones",Flow=0,FormFactor=3,Name="Wired headphones"}};
        Check(AudioControl.FindEndpoint(endpoints,"headset",new AudioBindings(),out ambiguous).Id=="headphones"&&!ambiguous,"unique Windows Headphones form factor resolves");
        endpoints[0].FormFactor=5;Check(AudioControl.FindEndpoint(endpoints,"headset",out ambiguous).Id=="headphones","Windows Headset form factor also resolves");
        endpoints.Add(new AudioEndpointInfo {Id="second",Flow=0,FormFactor=3});Check(AudioControl.FindEndpoint(endpoints,"headset",out ambiguous)==null&&ambiguous,"multiple headphones are never guessed");
        var pref=new AudioBindings {HeadsetEndpointId="second"};Check(AudioControl.FindEndpoint(endpoints,"headset",pref,out ambiguous).Id=="second","explicit binding resolves multiple endpoints");
        endpoints.RemoveAt(1);Check(AudioControl.FindEndpoint(endpoints,"headset",pref,out ambiguous)==null,"unplugged binding does not silently retarget");
        endpoints.Add(new AudioEndpointInfo {Id="hyperx",Flow=0,InterfaceName="HyperX Cloud Alpha Wireless"});Check(AudioControl.FindEndpoint(endpoints,"headset",out ambiguous).Id=="hyperx","existing HyperX automatic identity keeps priority");
        endpoints.Clear();endpoints.Add(new AudioEndpointInfo {Id="speaker",Flow=0,FormFactor=1,Name="Headphones by name only"});endpoints.Add(new AudioEndpointInfo {Id="capture",Flow=1,FormFactor=3});
        Check(AudioControl.FindEndpoint(endpoints,"headset",out ambiguous)==null&&!ambiguous,"a speaker name or a capture endpoint cannot masquerade as headphone output");
        var encoded=Audio();encoded.Devices=new[]{Endpoint("headphones",5)};var decoded=AudioBrokerClient.Decode<AudioReading>(AudioBrokerClient.Encode(encoded));Check(decoded.Devices[0].FormFactor==5,"form factor survives the existing audio broker transport");
    }
    static void BatteryTextFits(){
        foreach(float scale in new[]{1f,1.25f,1.5f,2f})using(var image=new Bitmap(520,100))using(var g=Graphics.FromImage(image)){
            g.ScaleTransform(scale,scale);g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var texts=new List<string> {"--"};for(int percent=0;percent<=100;percent++){texts.Add(percent.ToString());texts.Add("≈"+percent);}
            foreach(string text in texts){
                float preferred=text.StartsWith("≈")?18:VisualDesign.BatteryDigitSize;
                float size=CompactRenderer.BatteryTextSize(g,text,preferred,43);
                using(var font=new Font("Segoe UI",size,FontStyle.Bold,GraphicsUnit.Pixel))using(var format=new StringFormat {Alignment=StringAlignment.Far,LineAlignment=StringAlignment.Center,Trimming=StringTrimming.None,FormatFlags=StringFormatFlags.NoWrap}){
                    Check(size>0&&size<=preferred&&g.MeasureString(text,font,Int32.MaxValue,format).Width<=43,"battery text "+text+" fits the unchanged 43px region at "+scale);
                    int fitted,lines;g.MeasureString(text,font,new SizeF(43,32),format,out fitted,out lines);Check(fitted==text.Length&&lines==1,"the complete battery label fits without clipping or wrapping");
                }
                using(var font=new Font("Segoe UI",preferred,FontStyle.Bold,GraphicsUnit.Pixel))if(g.MeasureString(text,font).Width<=43)Check(size==preferred,"a label that already fits keeps its preferred size");
                if(text=="--"||text.Length==2||text.StartsWith("≈")&&text.Length==3)Check(size==preferred,"two digit and unavailable battery sizes stay unchanged for "+text);
            }
        }
    }
    static void CollapsedVolumeTargets(){
        foreach(bool wireless in new[]{false,true})foreach(bool audioCard in new[]{false,true})foreach(float scale in new[]{1f,1.25f,1.5f,2f})using(var f=new Fixture()){
            var pref=DeviceSettings.Current;pref.VolumeCollapsed=true;DeviceSettings.UseTransientConfiguration(pref);
            f.Owner.GenericReady=wireless;f.Owner.NativeMousePresent=true;
            f.Owner.Audio.Devices=audioCard?new[]{Endpoint("headphones",3)}:new AudioDeviceChoice[0];
            if(wireless)f.Owner.WirelessDevices=new[]{Mouse(A,PhysA,71),Mouse(B,PhysB,42)};
            Set(f.Popup,"dpiScale",scale);f.Popup.SyncDevices();
            var layout=(WirelessLayout)Call(f.Popup,"MainWireless");
            var card=wireless?layout.VolumeRect:VisualDesign.SystemVolumeRectFor(audioCard,f.Owner.Dpi);
            var toggle=f.Controls().Find(delegate(PopupControl c){return c.Id==241;});
            Check(toggle!=null&&toggle.Rect==VisualDesign.VolumeToggleRect(card,true)&&toggle.Rect.Width>=28&&toggle.Rect.Height>=24,"collapsed arrow hit area follows the scaled "+wireless+" layout with audio card "+audioCard);
            Check(toggle.Text=="展开系统音量"&&toggle.Value=="55%"&&!toggle.Slider,"Id 241 keeps its accessible text and value");
            Check(Math.Abs(toggle.Rect.Y+toggle.Rect.Height/2-(card.Y+card.Height/2))<.01f,"collapsed arrow target is vertically centered");
            var chevron=VisualDesign.VolumeChevronRect(card,true);Check(toggle.Rect.Contains(chevron),"drawn triangle lies within its hit area");
            int volume=f.Owner.Audio.OutputVolumePercent.Value;
            foreach(float x in new[]{card.X+15,card.X+card.Width/2}){
                var point=new PointF(x,card.Y+card.Height/2);f.MouseEvent("OnMouseMove",point);Check((int)Field(f.Popup,"hoverAction")!=241,"hovering collapsed strip text does not highlight the arrow");
                f.MouseEvent("OnMouseDown",point);f.MouseEvent("OnMouseUp",point);
                Check(DeviceSettings.Current.VolumeCollapsed&&f.VolumeWrites==0&&f.Owner.Audio.OutputVolumePercent==volume,"clicking the collapsed strip left or middle changes neither expansion nor volume");
            }
            var center=new PointF(chevron.X+chevron.Width/2,chevron.Y+chevron.Height/2);f.MouseEvent("OnMouseMove",center);
            Check((int)Field(f.Popup,"hoverAction")==241,"hovering the triangle highlights its button");
            f.MouseEvent("OnMouseDown",center);f.MouseEvent("OnMouseUp",center);
            Check(!DeviceSettings.Current.VolumeCollapsed&&f.VolumeWrites==0&&f.Controls().Exists(delegate(PopupControl c){return c.Id==240&&c.Slider;}),"only the triangle expands volume at "+scale);
            var expanded=VisualDesign.VolumeToggleRect(new RectangleF(0,50,260,48),false);Check(expanded==new RectangleF(228,52,28,24),"expanded hit geometry stays unchanged");
            Check(VisualDesign.VolumeChevronRect(new RectangleF(0,50,260,48),false)==new RectangleF(235,56,14,14),"expanded triangle drawing stays unchanged");
        }
        using(var f=new Fixture(new AudioBindings {RememberReadings=false,VolumeCollapsed=true})){
            Set(f.Popup,"headsetProgress",.5f);Call(f.Popup,"UpdateLayoutSize");var toggle=f.Controls().Find(delegate(PopupControl c){return c.Id==241;});
            var card=new RectangleF(0,VisualDesign.MouseCardHeight(f.Owner.Dpi)+52+VisualDesign.CardGap,260,32);
            Check(toggle.Rect==VisualDesign.VolumeToggleRect(card,true),"classic Id 241 follows the intermediate second-card animation offset");
            f.Click(toggle);Check(!DeviceSettings.Current.VolumeCollapsed&&f.VolumeWrites==0,"the displaced classic arrow still expands without writing volume");
        }
    }
    static AudioReading AutomaticAudio(List<AudioEndpointInfo> endpoints,SpeakerEndpointMemory memory,string current){
        bool ambiguous;var speaker=memory.FindSpeaker(endpoints,DeviceSettings.Current,current,out ambiguous);
        var headset=AudioControl.FindEndpoint(endpoints,"headset",out ambiguous);
        var result=new AudioReading {OperationSucceeded=true,TimestampUtc=DateTime.UtcNow,ConsoleOutputId=current,MultimediaOutputId=current,CommunicationsOutputId="communications",VolumeEndpointId=current,OutputVolumePercent=55,
            SpeakerAvailable=speaker!=null,SpeakerId=speaker==null?null:speaker.Id,HeadsetAvailable=headset!=null,HeadsetId=headset==null?null:headset.Id};
        result.ActiveOutput=speaker!=null&&speaker.Id==current?"speaker":headset!=null&&headset.Id==current?"headset":"other";
        var choices=new List<AudioDeviceChoice>();foreach(var endpoint in endpoints)choices.Add(new AudioDeviceChoice {Id=endpoint.Id,Flow=endpoint.Flow,FormFactor=endpoint.FormFactor,Name=endpoint.Id});result.Devices=choices.ToArray();return result;
    }
    static void AutomaticAudioRoundTrip(){
        foreach(int form in new[]{1,9}){
            var memory=new SpeakerEndpointMemory();var endpoints=new List<AudioEndpointInfo> {new AudioEndpointInfo {Id="original",Flow=0,FormFactor=form},new AudioEndpointInfo {Id="headphones",Flow=0,FormFactor=3},new AudioEndpointInfo {Id="other",Flow=0,FormFactor=1}};
            string current="original";
            using(var f=new Fixture(null,delegate{return AutomaticAudio(endpoints,memory,current);},delegate(string target){
                var before=AutomaticAudio(endpoints,memory,current);string id=target=="speaker"?before.SpeakerId:before.HeadsetId;
                if(id==null)throw new Exception("Fake target must be available");current=id;return AutomaticAudio(endpoints,memory,current);
            })){
                Check(f.Owner.Audio.SpeakerAvailable&&f.Owner.Audio.SpeakerId=="original","non-Realtek default is an available speaker target");
                f.Click(f.Controls().Find(delegate(PopupControl c){return c.Id==101;}));Pump(delegate{return f.OutputWrites==1&&!f.Owner.IsAudioBusy;});
                var speaker=f.Controls().Find(delegate(PopupControl c){return c.Id==100;});
                Check(current=="headphones"&&f.Owner.Audio.ActiveOutput=="headset"&&speaker!=null&&speaker.Enabled&&f.Owner.Audio.SpeakerId=="original","switching to wired headphones keeps the original output available");
                f.Click(speaker);Pump(delegate{return f.OutputWrites==2&&!f.Owner.IsAudioBusy;});
                Check(current=="original"&&f.Owner.Audio.ActiveOutput=="speaker"&&f.Owner.Audio.CommunicationsOutputId=="communications"&&f.VolumeWrites==0,"switching back restores the remembered USB/HDMI endpoint through the fake audio queue");
            }
        }
    }
    static void AmbiguousSpeakerHint(){
        using(var f=new Fixture()){
            f.Owner.Audio.SpeakerAvailable=false;f.Owner.Audio.SpeakerId=null;f.Owner.Audio.SpeakerName=null;
            var speaker=PopupPages.Build(f.Owner,new PopupNavigation {Page=PopupPage.Audio},false).Find(delegate(PopupControl c){return c.Id==100;});
            Check(!speaker.Enabled&&speaker.Detail!=null&&speaker.Detail.Contains("音频设置")&&speaker.Detail.Contains("绑定"),"unresolved speaker details explain how to bind an endpoint");
        }
    }
    static void AudioCards(){
        foreach(bool wireless in new[]{false,true})using(var f=new Fixture()){
            f.Owner.GenericReady=wireless;var pref=DeviceSettings.Current;
            f.Owner.Audio.Devices=new[]{Endpoint("headphones",3),Endpoint("speaker",1),Endpoint("mic",4,1)};
            f.Popup.SyncDevices();
            var layout=WirelessLayout.Build(f.Owner,0,1000);var card=layout.All.Find(delegate(WirelessCard c){return c.Id=="native:audio";});
            Check(card!=null&&card.Name=="耳机"&&card.Subtitle=="Endpoint headphones"&&(card.Battery==null||!card.Battery.Percent.HasValue),"audio card uses endpoint title with no battery in "+wireless);
            var controls=PopupPages.Build(f.Owner,new PopupNavigation(),false);foreach(int id in new[]{100,101,102})Check(controls.Exists(delegate(PopupControl c){return c.Id==id&&c.Enabled;}),"audio card has existing action "+id);
            var actual=f.Controls();bool disjoint=true;for(int i=0;i<actual.Count;i++)for(int j=i+1;j<actual.Count;j++)disjoint&=!actual[i].Rect.IntersectsWith(actual[j].Rect);
            Check(disjoint&&f.Popup.ClientSize.Height==(int)Math.Round((wireless?layout.Size.Height:VisualDesign.PanelSizeFor(true,f.Owner.Dpi).Height)*(float)Field(f.Popup,"dpiScale")),"plugged audio card has matching real popup size and nonoverlapping hit targets");
            Check(!f.Owner.CreateFeedbackReport().FullText.Contains("Endpoint headphones"),"audio-only card keeps endpoint names out of feedback reports");
            Check(!controls.Exists(delegate(PopupControl c){return c.DeviceKey=="native:audio"&&(c.Id==5||c.Id>=2000);}),"audio card has no battery refresh target");
            f.Owner.Audio.Devices=new[]{Endpoint("speaker",1)};f.Popup.SyncDevices();Check(!WirelessLayout.Build(f.Owner,0,1000).All.Exists(delegate(WirelessCard c){return c.Id=="native:audio";})&&!f.Controls().Exists(delegate(PopupControl c){return c.Id>=100&&c.Id<=102;}),"unplug removes audio card and actual hit targets");
            pref.HeadsetEndpointId="speaker";DeviceSettings.UseTransientConfiguration(pref);Check(WirelessLayout.Build(f.Owner,0,1000).All.Exists(delegate(WirelessCard c){return c.Id=="native:audio";}),"active bound endpoint can identify a driver-reported speaker");
            f.Owner.Audio.Devices=new AudioDeviceChoice[0];Check(!WirelessLayout.Build(f.Owner,0,1000).All.Exists(delegate(WirelessCard c){return c.Id=="native:audio";}),"unplug removes bound audio card too");
            f.Owner.Audio.Devices=new[]{Endpoint("speaker",1)};Check(WirelessLayout.Build(f.Owner,0,1000).All.Exists(delegate(WirelessCard c){return c.Id=="native:audio";}),"replug restores bound card");
            pref.HeadsetEndpointId=null;DeviceSettings.UseTransientConfiguration(pref);f.Owner.Audio.Devices=new[]{Endpoint("headphones",3),Endpoint("second",5)};f.Owner.Audio.HeadsetAvailable=false;
            card=WirelessLayout.Build(f.Owner,0,1000).All.Find(delegate(WirelessCard c){return c.Id=="native:audio";});controls=PopupPages.Build(f.Owner,new PopupNavigation(),false);
            Check(card!=null&&card.Subtitle.Contains("绑定")&&controls.Exists(delegate(PopupControl c){return c.Id==101&&!c.Enabled&&c.Detail.Contains("绑定");}),"ambiguous card guides explicit binding and disables headset action");
            f.Owner.Headset=new HeadsetReading {Connected=true,ConnectionVerified=true};Check(!WirelessLayout.Build(f.Owner,0,1000).All.Exists(delegate(WirelessCard c){return c.Id=="native:audio";}),"native headset suppresses duplicate controls");f.Owner.Headset=null;
            f.Owner.WirelessDevices=new[]{new WirelessDevice {Id=A,Name="Generic wireless headset",Kind="headset",Online=true}};Check(WirelessLayout.Build(f.Owner,0,1000).All.Exists(delegate(WirelessCard c){return c.Id=="native:audio";}),"generic headset still receives shared audio card");
            DeviceSettings.SetDeviceHidden(pref,"native:audio",true);DeviceSettings.UseTransientConfiguration(pref);Check(!WirelessLayout.Build(f.Owner,0,1000).All.Exists(delegate(WirelessCard c){return c.Id=="native:audio";}),"audio card respects persisted hide mechanism");
            Check(f.OutputWrites==0&&f.VolumeWrites==0,"audio discovery and hotplug never write audio");
        }
    }
    static AudioDeviceChoice ReceiverEndpoint(string id,string product){
        var endpoint=Endpoint(id,3);endpoint.InterfaceName=product;return endpoint;
    }
    static void CheckAudioCardHidden(Fixture f,string scenario){
        Check(!WirelessLayout.Build(f.Owner,0,1000).All.Exists(delegate(WirelessCard c){return c.Id=="native:audio";}),scenario+" has no audio-only card");
        f.Popup.SyncDevices();var controls=f.Controls();
        Check(!controls.Exists(delegate(PopupControl c){return c.Id>=100&&c.Id<=102;}),scenario+" has no speaker/headset/microphone hit targets");
        Check(controls.Exists(delegate(PopupControl c){return c.Id==241;})&&controls.Exists(delegate(PopupControl c){return c.Id==240;})==!DeviceSettings.Current.VolumeCollapsed,scenario+" keeps the configured system volume controls");
        var settings=PopupPages.Build(f.Owner,new PopupNavigation {Page=PopupPage.Audio},false);
        foreach(int id in new[]{100,101,102})Check(settings.Exists(delegate(PopupControl c){return c.Id==id;}),scenario+" keeps audio settings action "+id);
    }
    static void NativeReceiverAudio(){
        foreach(bool wireless in new[]{false,true})using(var f=new Fixture()){
            f.Owner.GenericReady=wireless;f.Owner.Headset=new HeadsetReading {ReceiverPresent=true,Connected=false};
            var receiver=ReceiverEndpoint("hyperx","HyperX Cloud Alpha Wireless");f.Owner.Audio.Devices=new[]{receiver};
            Check(VisualDesign.AudioCardFor(f.Owner.Headset,f.Owner.Audio)==null&&!VisualDesign.HasAudioCard(f.Owner.Headset,f.Owner.Audio),"disconnected HyperX receiver alone cannot create the classic audio card");
            CheckAudioCardHidden(f,"disconnected HyperX receiver in "+wireless);
            Check(f.Owner.Audio.Devices[0]==receiver&&f.Owner.Audio.HeadsetId=="headphones"&&f.Owner.Audio.HeadsetAvailable&&f.Owner.Audio.OutputVolumePercent==55,"visibility filtering preserves the audio snapshot and targets");
            f.Owner.Headset.Connected=true;
            CheckAudioCardHidden(f,"unconfirmed HyperX connection without a prior confirmation");
            f.Owner.Headset.ConnectionVerified=true;f.Popup.SyncDevices();var layout=WirelessLayout.Build(f.Owner,0,1000);
            Check(VisualDesign.ShouldShowHeadset(f.Owner.Headset)&&layout.All.Exists(delegate(WirelessCard c){return c.NativeHeadset;})&&!layout.All.Exists(delegate(WirelessCard c){return c.AudioOnly;}),"reconnected HyperX keeps its native card without a duplicate");
            Check(f.Controls().Exists(delegate(PopupControl c){return c.Id==101&&c.Enabled;}),"reconnected HyperX restores the existing audio controls");
            f.Owner.Headset.ConnectionVerified=false;f.Owner.Headset.ConnectionTimestampUtc=DateTime.UtcNow;
            Check(VisualDesign.ShouldShowHeadset(f.Owner.Headset)&&WirelessLayout.Build(f.Owner,0,1000).All.Exists(delegate(WirelessCard c){return c.NativeHeadset;}),"cached native connection retains the existing native card rule");
            Check(f.OutputWrites==0&&f.VolumeWrites==0,"receiver visibility and reconnection never write audio");
        }
    }
    static void NativeReceiverWithWired(){
        foreach(bool wireless in new[]{false,true})using(var f=new Fixture()){
            f.Owner.GenericReady=wireless;f.Owner.Headset=new HeadsetReading {ReceiverPresent=true};
            var wired=ReceiverEndpoint("realtek","Realtek(R) Audio");wired.Name="Realtek Headphones";
            f.Owner.Audio.Devices=new[]{ReceiverEndpoint("hyperx","HyperX Cloud Alpha Wireless"),wired};
            var card=VisualDesign.AudioCardFor(f.Owner.Headset,f.Owner.Audio);
            Check(card!=null&&card.EndpointId=="realtek"&&card.Subtitle==wired.Name,"only the remaining Realtek endpoint identifies the wired card");
            var layout=WirelessLayout.Build(f.Owner,0,1000);
            Check(layout.All.Exists(delegate(WirelessCard c){return c.AudioOnly&&c.Subtitle==wired.Name;}),"wireless layout also uses the remaining wired endpoint");
            f.Popup.SyncDevices();Check(f.Controls().Exists(delegate(PopupControl c){return c.Id==101&&c.Enabled&&c.Detail==wired.Name;}),"wired card controls use the same filtered card qualification");
        }
    }
    static void NativeReceiverIdentity(){
        DeviceSettings.UseTransientConfiguration(new AudioBindings());
        var headset=new HeadsetReading {ReceiverPresent=true};
        var endpoints=new[]{ReceiverEndpoint("interface","hYpErX cLoUd AlPhA wIrElEsS"),ReceiverEndpoint("english",null),ReceiverEndpoint("chinese",""),ReceiverEndpoint("partial","HyperX Cloud Alpha Wireless Pro"),ReceiverEndpoint("other","Other vendor")};
        endpoints[1].Name="Headphones (HyperX Cloud Alpha Wireless)";endpoints[2].Name="耳机 (HyperX Cloud Alpha Wireless)";endpoints[4].Name=endpoints[1].Name;
        for(int i=0;i<endpoints.Length;i++){
            var audio=new AudioReading {Devices=new[]{endpoints[i]}};
            Check((VisualDesign.AudioCardFor(headset,audio)==null)==(i<3),"native receiver filtering follows the existing interface/name identity for "+endpoints[i].Id);
            headset.ReceiverPresent=false;Check(VisualDesign.AudioCardFor(headset,audio)!=null,"an absent native receiver does not suppress "+endpoints[i].Id);headset.ReceiverPresent=true;
        }
    }
    static void BoundNativeReceiver(){
        foreach(bool wireless in new[]{false,true})using(var f=new Fixture(new AudioBindings {HeadsetEndpointId="HYPERX",RememberReadings=false,ReducedMotion=true})){
            f.Owner.GenericReady=wireless;f.Owner.Headset=new HeadsetReading {ReceiverPresent=true};
            var receiver=ReceiverEndpoint("hyperx","HyperX Cloud Alpha Wireless");receiver.FormFactor=1;
            f.Owner.Audio.Devices=new[]{receiver,Endpoint("realtek",3)};
            Check(VisualDesign.AudioCardFor(f.Owner.Headset,f.Owner.Audio)==null,"an explicitly bound disconnected native receiver does not create a card or retarget to wired headphones");
            CheckAudioCardHidden(f,"bound disconnected native receiver in "+wireless);
        }
    }
    static void GenericReceiverAudio(){
        foreach(string state in new[]{"connected","disconnected","unconfirmed",null})using(var f=new Fixture()){
            var device=new WirelessDevice {Id=A,Name="  Acme Wireless  ",Kind="headset",Online=state=="disconnected"||state=="unconfirmed",ConnectionState=state};
            f.Owner.WirelessDevices=new[]{device};var receiver=ReceiverEndpoint("acme","  aCmE WiReLeSs  ");f.Owner.Audio.Devices=new[]{receiver};
            var pref=DeviceSettings.Current;pref.DeviceAliases[A]="Renamed headset";DeviceSettings.UseTransientConfiguration(pref);
            CheckAudioCardHidden(f,"offline generic headset with state "+state);
            receiver.InterfaceName="Acme Wireless Pro";
            Check(WirelessLayout.Build(f.Owner,0,1000).All.Exists(delegate(WirelessCard c){return c.AudioOnly;}),"partial interface-name match never suppresses a generic audio card");
            receiver.InterfaceName="Acme Wireless";device.Kind="mouse";
            Check(WirelessLayout.Build(f.Owner,0,1000).All.Exists(delegate(WirelessCard c){return c.AudioOnly;}),"an offline non-headset reading cannot suppress headphones");
            device.Kind="headset";device.Online=true;device.ConnectionState="connected";
            Check(WirelessLayout.Build(f.Owner,0,1000).All.Exists(delegate(WirelessCard c){return c.AudioOnly;}),"a reconnected generic headset retains its shared audio card");
            device.Online=false;device.Name=null;receiver.InterfaceName=" ";
            Check(WirelessLayout.Build(f.Owner,0,1000).All.Exists(delegate(WirelessCard c){return c.AudioOnly;}),"missing product and interface names do not identify a receiver");
            device.Name="Acme Wireless";receiver.InterfaceName=null;receiver.Name=device.Name;
            Check(WirelessLayout.Build(f.Owner,0,1000).All.Exists(delegate(WirelessCard c){return c.AudioOnly;}),"generic receiver identity never falls back to the endpoint name");
            Check(f.OutputWrites==0&&f.VolumeWrites==0,"generic visibility checks never write audio");
        }
    }
    static void GenericReceiverWithWiredAndBinding(){
        using(var f=new Fixture()){
            f.Owner.WirelessDevices=new[]{new WirelessDevice {Id=A,Name="Acme Wireless",Kind="headset",ConnectionState="disconnected"}};
            var receiver=ReceiverEndpoint("acme","Acme Wireless");var wired=ReceiverEndpoint("realtek","Realtek(R) Audio");wired.Name="Realtek Headphones";
            f.Owner.Audio.Devices=new[]{receiver,wired};var layout=WirelessLayout.Build(f.Owner,0,1000);
            Check(layout.All.Exists(delegate(WirelessCard c){return c.AudioOnly&&c.Subtitle==wired.Name;}),"an offline generic receiver leaves the unique wired endpoint as the card target");
            f.Popup.SyncDevices();Check(f.Controls().Exists(delegate(PopupControl c){return c.Id==101&&c.Detail==wired.Name;}),"generic receiver filtering also qualifies wired card controls");
            var pref=DeviceSettings.Current;pref.HeadsetEndpointId="acme";DeviceSettings.UseTransientConfiguration(pref);receiver.FormFactor=1;
            CheckAudioCardHidden(f,"explicitly bound offline generic receiver");
            pref.HeadsetEndpointId="realtek";DeviceSettings.UseTransientConfiguration(pref);
            Check(WirelessLayout.Build(f.Owner,0,1000).All.Exists(delegate(WirelessCard c){return c.AudioOnly&&c.Subtitle==wired.Name;}),"an explicit wired binding remains visible beside an offline generic receiver");
        }
    }
    static void PagingAndClassicTray(){
        using(var f=new Fixture()){
            f.Owner.NativeMousePresent=false;f.Owner.GenericReady=true;f.Owner.WirelessDevices=new[]{Mouse(A,PhysA,71),Mouse(B,PhysB,42)};
            var expanded=WirelessLayout.Build(f.Owner,0,205);Check(expanded.Paged&&expanded.Cards.Count==1,"expanded volume reserves room when paging");
            var pref=DeviceSettings.Current;Collapsed(pref,true);DeviceSettings.UseTransientConfiguration(pref);var collapsed=WirelessLayout.Build(f.Owner,0,205);
            Check(!collapsed.Paged&&collapsed.Cards.Count==2&&collapsed.Size.Height<=205&&collapsed.Footer==collapsed.VolumeRect.Bottom,"collapsed volume releases room for the next card without moving the footer into it");
            var next=WirelessLayout.Build(f.Owner,expanded.End,205);Check(next.Start==1&&next.Cards[0].Id==B,"scroll offset remains usable after collapse");
            f.Owner.NativeMousePresent=true;f.Owner.GenericReady=false;f.Owner.WirelessDevices=new WirelessDevice[0];pref.TraySource="active";DeviceSettings.UseTransientConfiguration(pref);Call(f.Owner,"UpdateIcon");
            Check(!f.Owner.UseWirelessLayout&&(string)Field(f.Owner,"trayDeviceKey")=="native:mouse","classic layout uses the same nonempty active fallback");
            f.Activity(PhysA,true);Check((string)Field(f.Owner,"trayDeviceKey")=="native:mouse","classic native activity keeps its own card");
        }
    }
    static void Previews(){
        using(var f=new Fixture())foreach(bool wireless in new[]{false,true})foreach(bool dark in new[]{false,true})foreach(bool collapsed in new[]{false,true}){
            f.Owner.GenericReady=wireless;f.Owner.Audio.Devices=new[]{Endpoint("headphones",3)};var pref=DeviceSettings.Current;Collapsed(pref,collapsed);DeviceSettings.UseTransientConfiguration(pref);
            var layout=WirelessLayout.Build(f.Owner,0,1000);var size=wireless?layout.Size:VisualDesign.PanelSizeFor(true,f.Owner.Dpi);using(var image=new Bitmap(size.Width,size.Height))using(var g=Graphics.FromImage(image)){
                g.Clear(Color.Transparent);if(wireless)CompactRenderer.PaintWireless(g,f.Owner,layout,dark,f.Owner.GetFeedback());else VisualDesign.PaintDevicesPanel(g,f.Owner.Current,null,null,false,null,null,false,dark,null,f.Owner.Audio);
                image.Save(Path.Combine(output,"ui-feedback-"+(wireless?"wireless":"classic")+"-"+(dark?"dark":"light")+"-"+(collapsed?"collapsed":"expanded")+".png"));
            }
        }
    }
    [STAThread] static int Main(string[] args){output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);Run("battery text fitting",BatteryTextFits);Run("configuration",Configuration);Run("active tray",ActiveTray);Run("volume layouts",VolumeLayouts);Run("collapsed volume targets",CollapsedVolumeTargets);Run("collapse during drag/queue",VolumeDuringDrag);Run("audio detection",AudioDetection);Run("automatic audio round trip",AutomaticAudioRoundTrip);Run("ambiguous speaker hint",AmbiguousSpeakerHint);Run("audio cards",AudioCards);Run("native receiver audio",NativeReceiverAudio);Run("native receiver with wired headphones",NativeReceiverWithWired);Run("native receiver identity",NativeReceiverIdentity);Run("bound native receiver",BoundNativeReceiver);Run("generic receiver audio",GenericReceiverAudio);Run("generic receiver with wired headphones and binding",GenericReceiverWithWiredAndBinding);Run("paging and classic tray",PagingAndClassicTray);if(failures==0)Run("previews",Previews);Console.WriteLine(checks+" checks, "+failures+" failed groups; fake devices/audio only");return failures==0?0:1;}
}
