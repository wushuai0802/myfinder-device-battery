using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

static class UiAndControllerTests {
    static int passed;
    static void Check(bool value,string name){if(!value)throw new Exception("FAIL "+name);passed++;}
    static void CreateHandles(Control control){var handle=control.Handle;foreach(Control child in control.Controls)CreateHandles(child);}
    static void Pump(Func<bool> done) {
        var end=DateTime.UtcNow.AddSeconds(4);
        while(!done()&&DateTime.UtcNow<end){Application.DoEvents();Thread.Sleep(10);}
        Check(done(),"queued fake operation completes within its time budget");
    }
    static DpiConfig Dpi(int active) {
        var result=new DpiConfig {ActiveStage=active,TimestampUtc=DateTime.UtcNow};
        foreach(int value in new[]{400,800,1200,1600})result.Stages.Add(new DpiStage {XDpi=value,YDpi=value});
        return result;
    }
    static AudioReading Audio(string output,bool mute,int volume=55) {
        return new AudioReading {ActiveOutput=output,MicrophoneMuted=mute,SpeakerAvailable=true,HeadsetAvailable=true,MicrophoneAvailable=true,
            SpeakerId="speaker",HeadsetId="headset",MicrophoneId="mic",ConsoleOutputId=output,MultimediaOutputId=output,OperationSucceeded=true,OutputVolumePercent=volume,VolumeEndpointId=output,
            SpeakerName="扬声器 (USB DAC)",HeadsetName="耳机 (HyperX Cloud Alpha Wireless)",MicrophoneName="麦克风 (HyperX Cloud Alpha Wireless)",
            Devices=new[]{new AudioDeviceChoice {Id="speaker",Name="Speakers (USB DAC)",Flow=0},new AudioDeviceChoice {Id="headset",Name="Headphones (HyperX)",Flow=0},new AudioDeviceChoice {Id="mic",Name="Microphone (HyperX)",Flow=1}}};
    }
    static Bitmap Render(BatteryReading mouse,HeadsetReading headset,PanelFeedback feedback,bool dark,float scale,DpiConfig dpi=null) {
        dpi=dpi??Dpi(1);var logical=VisualDesign.PanelSizeFor(headset,dpi);var physical=UiState.ScaleSize(logical,scale);
        var bitmap=new Bitmap(physical.Width,physical.Height,PixelFormat.Format32bppPArgb);
        using(var g=Graphics.FromImage(bitmap)) {
            g.Clear(Color.Transparent);g.ScaleTransform(scale,scale);
            VisualDesign.PaintDevicesPanel(g,mouse,null,null,false,dpi,null,feedback!=null&&feedback.PendingStage>=0,dark,headset,Audio("headset",true),1,-1,false,1,-1,feedback);
        }
        return bitmap;
    }
    [STAThread]
    static int Main(string[] args) {
        var cached=typeof(DeviceSettings).GetField("current",BindingFlags.NonPublic|BindingFlags.Static);object previous=cached.GetValue(null);
        TrayContext context=null;
        try {
            NativeDisplay.EnableDpiAwareness();Application.EnableVisualStyles();
            string output=Path.Combine(Path.GetFullPath(args[0]),"ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
            cached.SetValue(null,new AudioBindings());
            var now=DateTime.UtcNow;
            var mouse=new BatteryReading {Percent=85,TimestampUtc=now};
            var headset=new HeadsetReading {Connected=true,ConnectionVerified=true,ReceiverPresent=true,Percent=46,Charging=false,ConnectionTimestampUtc=now,BatteryTimestampUtc=now,ChargingTimestampUtc=now};
            var liveMouse=UiState.Mouse(mouse,null,now);var liveHeadset=UiState.Headset(headset,now);
            Check(liveMouse.Fresh&&liveHeadset.Fresh,"live readings are fresh");
            var cachedMouse=UiState.Mouse(null,mouse,now);
            Check(!cachedMouse.Fresh&&cachedMouse.Tooltip.Contains("上次"),"cached mouse retains its qualifier");
            Check(UiState.Tray("mouse",cachedMouse,liveHeadset).Tooltip.Contains("上次"),"combined tooltip retains cached mouse qualifier");
            Check(UiState.Tray("headset",liveMouse,liveHeadset).Battery.Name=="耳机","explicit headset tray source");
            Check(UiState.Tray("lowest",liveMouse,liveHeadset).Battery.Percent==46,"lowest mode chooses the lower confirmed reading");
            var staleHeadset=UiState.Headset(headset,now.AddSeconds(91));
            Check(!staleHeadset.Fresh&&staleHeadset.Key!=liveHeadset.Key,"freshness boundary changes visible cache key without a new packet");
            Check(UiState.Tray("lowest",liveMouse,staleHeadset).Battery.Name=="鼠标","stale headset does not displace a live mouse reading");
            headset.Charging=true;
            Check(UiState.Headset(headset,now).Charging&&!UiState.Headset(headset,now.AddSeconds(91)).Charging,"expired charging state is removed");headset.Charging=false;
            Check(UiState.Tray("mouse",cachedMouse,staleHeadset).Tooltip.Length<=63,"tray tooltip respects Framework length limit");
            Check(UiState.SkipConfirmedStage(Dpi(1),null,1,false,now),"fresh current stage can skip a redundant write");
            Check(!UiState.SkipConfirmedStage(Dpi(1),"timeout",1,false,now),"failed reads never suppress a requested write");
            var oldDpi=Dpi(1);oldDpi.TimestampUtc=now.AddSeconds(-20);
            Check(!UiState.SkipConfirmedStage(oldDpi,null,1,false,now),"stale stage is revalidated by a real selection");
            var policy=new LowBatteryPolicy();var low=new BatteryView {Name="鼠标",Fresh=true,Percent=20};
            Check(!policy.Observe("mouse",low,false)&&!policy.IsLatched("mouse"),"quiet or fullscreen state defers notification");
            Check(policy.Observe("mouse",low,true)&&!policy.Observe("mouse",low,true),"low battery notifies once per discharge cycle");
            var restored=new LowBatteryPolicy();restored.Restore("mouse",policy.IsLatched("mouse"));
            Check(!restored.Observe("mouse",low,true),"persisted latch prevents repeated notification after restart");
            low.Percent=30;Check(!policy.Observe("mouse",low,true)&&!policy.IsLatched("mouse"),"recharge rearms notification");
            low.Percent=18;low.Fresh=false;Check(!policy.Observe("mouse",low,true),"cached low battery never sends a warning");
            low.Fresh=true;low.Charging=true;Check(!policy.Observe("mouse",low,true),"charging does not produce a low battery warning");
            low.Charging=false;low.Percent=18;var customPolicy=new LowBatteryPolicy();Check(!customPolicy.Observe("mouse",low,true,15),"custom threshold defers the notification until it is reached");
            foreach(float scale in new[]{1f,1.25f,1.5f,1.75f,2f}) {
                var point=UiState.LogicalPoint(new Point((int)(150*scale),(int)(160*scale)),scale);
                Check(Math.Abs(point.X-150)<.81&&Math.Abs(point.Y-160)<.81,"hit coordinate maps correctly at "+scale);
                using(var image=Render(mouse,headset,null,false,scale)) {
                    Check(image.GetPixel((int)(130*scale),(int)(100*scale)).A==0,"separate cards keep a transparent gap at "+scale);
                    Check(image.Width==(int)Math.Round(260*scale)&&image.Height==(int)Math.Round(230*scale),"compact layout size matches scale "+scale);
                    image.Save(Path.Combine(output,"cards-"+scale.ToString(System.Globalization.CultureInfo.InvariantCulture)+".png"));
                }
            }
            var eight=Dpi(1);foreach(int value in new[]{2400,3200,4800,6400})eight.Stages.Add(new DpiStage {XDpi=value,YDpi=value});
            Check(VisualDesign.PanelSizeFor(true,eight).Height==260,"eight-stage panel reserves the second DPI row");
            for(int i=0;i<8;i++)Check(VisualDesign.StageRect(i,8).Bottom<=VisualDesign.MouseCardHeight(eight),"eight-stage hit area stays inside the mouse card");
            using(var image=Render(mouse,headset,null,false,2,eight))image.Save(Path.Combine(output,"cards-eight-stages.png"));
            using(var image=Render(new BatteryReading {Percent=100,TimestampUtc=now},null,null,true,2))image.Save(Path.Combine(output,"mouse-only-100.png"));
            using(var image=Render(mouse,headset,new PanelFeedback {PendingStage=2,PendingOutput="speaker",MicrophonePending=true,KeyboardFocus=12,ShowPendingHints=true},true,1.5f))image.Save(Path.Combine(output,"cards-dark-pending.png"));
            var stale=new HeadsetReading {Connected=true,ConnectionVerified=true,ReceiverPresent=true,Percent=46,BatteryTimestampUtc=now.AddMinutes(-5)};
            using(var image=Render(mouse,stale,null,false,1f))image.Save(Path.Combine(output,"cards-cached.png"));
            string signalName="Local\\MyFinderBatteryTest-"+Guid.NewGuid().ToString("N");
            using(var done=new ManualResetEvent(false))using(var listener=new ShowPanelSignal(signalName))using(var sender=new ShowPanelSignal(signalName)) {
                listener.Listen(delegate {done.Set();});sender.Request();Check(done.WaitOne(2000),"secondary launch signal wakes the existing listener without polling");
            }
            int events=0;
            using(var window=new DeviceEventsWindow(delegate {events++;})) {
                var message=Message.Create(window.Handle,0x219,new IntPtr(7),IntPtr.Zero);
                typeof(DeviceEventsWindow).GetMethod("WndProc",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(window,new object[]{message});
                Check(events==1,"device change broadcast invokes a refresh callback");
            }
            int stage=1,writes=0,volume=55,volumeWrites=0;string active="speaker";bool muted=false,failAudio=false;
            context=new TrayContext(false,delegate(bool includeBattery){return new MouseSnapshot {Battery=includeBattery?new BatteryReading {Percent=85}:null,Dpi=Dpi(stage)};},
                delegate(int selected){Interlocked.Increment(ref writes);Thread.Sleep(60);stage=selected;return Dpi(stage);},
                delegate {return headset;},delegate {return Audio(active,muted,volume);},false,
                delegate(string target){Thread.Sleep(60);if(failAudio){var failure=Audio(active,muted,volume);failure.OperationSucceeded=false;failure.Error="Test failure";return failure;}active=target;return Audio(active,muted,volume);},
                delegate(bool target){Thread.Sleep(60);muted=target;return Audio(active,muted,volume);},
                delegate(string endpoint,int target){Interlocked.Increment(ref volumeWrites);Thread.Sleep(60);if(endpoint!=active){var failure=Audio(active,muted,volume);failure.OperationSucceeded=false;failure.Error="Target changed";return failure;}volume=target;return Audio(active,muted,volume);});
            Pump(delegate {return context.Current!=null&&context.Audio!=null&&!context.IsAudioBusy;});
            context.SelectDpiStage(1);Check(writes==0,"controller skips a confirmed duplicate stage click");
            context.SelectDpiStage(2);context.SelectDpiStage(3);
            Check(context.GetFeedback().PendingStage==3&&context.Dpi.ActiveStage==1,"queued selection shows pending target while preserving confirmed stage");
            Pump(delegate {return context.Dpi.ActiveStage==3&&!context.IsDpiRefreshing;});
            Check(writes==2,"serialized stage writes preserve the last requested target");
            context.SelectAudioOutput("headset");
            Check(context.GetFeedback().PendingOutput=="headset"&&context.Audio.ActiveOutput=="speaker","audio pending state does not optimistically change confirmed output");
            Pump(delegate {return !context.IsAudioBusy;});Check(context.Audio.ActiveOutput=="headset","verified output becomes selected");
            failAudio=true;context.SelectAudioOutput("speaker");Pump(delegate {return !context.IsAudioBusy;});
            Check(context.GetFeedback().AudioFailure=="Test failure"&&context.Audio.ActiveOutput=="headset","failed audio operation keeps confirmed state and exposes an error");
            context.ToggleMicrophoneMute();Pump(delegate {return !context.IsAudioBusy;});Check(context.Audio.MicrophoneMuted==true,"fake microphone operation is verified");
            failAudio=false;context.SelectOutputVolume("headset",30);context.SelectOutputVolume("headset",40);context.SelectOutputVolume("headset",50);
            Check(context.Audio.OutputVolumePercent==55&&context.DesiredVolume==50,"volume preview does not replace the last confirmed reading");
            Pump(delegate {return !context.IsAudioBusy&&!context.DesiredVolume.HasValue;});Check(context.Audio.OutputVolumePercent==50&&volumeWrites==2,"volume queue merges intermediate requests");
            context.SelectOutputVolume("speaker",70);Pump(delegate {return !context.IsAudioBusy;});Check(context.Audio.OutputVolumePercent==50&&context.GetFeedback().AudioFailure=="Target changed","stale output volume target does not affect a different endpoint");
            var originalHeadset=headset;headset=new HeadsetReading {ReceiverPresent=true,Detail="Temporary timeout"};context.RefreshDevice("headset");
            Pump(delegate{return context.Headset==headset;});Check(VisualDesign.ShouldShowHeadset(context.Headset)&&!UiState.Headset(context.Headset,DateTime.UtcNow).Fresh,"radio timeout keeps the card and labels the reading as cached");
            headset=new HeadsetReading {ConnectionVerified=true,Connected=false,ReceiverPresent=true};context.RefreshDevice("headset");Pump(delegate{return context.Headset==headset;});Check(!VisualDesign.ShouldShowHeadset(context.Headset),"confirmed radio disconnect collapses the headset card");
            headset=originalHeadset;context.RefreshDevice("headset");Pump(delegate{return context.Headset==headset;});
            context.SelectAudioOutput("headset");Pump(delegate{return !context.IsAudioBusy;});
            var blockedField=typeof(TrayContext).GetField("audioNeedsReconcile",BindingFlags.Instance|BindingFlags.NonPublic);
            blockedField.SetValue(context,true);
            var blockedControls=PopupPages.Build(context,new PopupNavigation {Page=PopupPage.Audio},true);
            Check(blockedControls.TrueForAll(delegate(PopupControl row){return row.Id!=100&&row.Id!=101&&row.Id!=102&&row.Id!=240||!row.Enabled;}),"unconfirmed audio state blocks output, microphone and volume hit targets");
            var blockedOutput=blockedControls.Find(delegate(PopupControl row){return row.Id==100;});
            Check(PopupPages.Hit(blockedControls,new PointF(blockedOutput.Rect.X+10,blockedOutput.Rect.Y+10))==null,"disabled audio controls cannot be activated by pointer hit testing");
            using(var image=Render(mouse,headset,new PanelFeedback {AudioBlocked=true,AudioFailure="音频状态待确认"},false,1.75f))image.Save(Path.Combine(output,"cards-audio-awaiting-confirmation.png"));
            blockedField.SetValue(context,false);
            using(var settings=new SettingsForm(context)) {
                settings.PerformLayout();CreateHandles(settings);settings.PerformLayout();
                using(var image=new Bitmap(settings.Width,settings.Height)){settings.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(output,"settings.png"));}
                Check(settings.Width>=464&&settings.Height>=430,"settings form fits its controls");
                foreach(Control child in settings.Controls[0].Controls)Check(child.Bottom<=settings.Controls[0].ClientSize.Height,"settings row remains within the client area");
                foreach(Control child in settings.Controls[0].Controls)if(child is ComboBox){var picker=(ComboBox)child;Check(picker.SelectedIndex>=0&&!String.IsNullOrEmpty(picker.Text),"settings dropdown retains a readable selected value");}
            }
            using(var details=new Details(context)) {
                var message=Message.Create(IntPtr.Zero,0,IntPtr.Zero,IntPtr.Zero);
                var process=typeof(Details).GetMethod("ProcessCmdKey",BindingFlags.Instance|BindingFlags.NonPublic);
                Check((bool)process.Invoke(details,new object[]{message,Keys.Tab}),"keyboard Tab is handled by the popup");
                Check((int)typeof(Details).GetField("keyboardFocus",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(details)==0,"keyboard focus starts on refresh");
                process.Invoke(details,new object[]{message,Keys.Tab});process.Invoke(details,new object[]{message,Keys.Enter});
                var navigation=(PopupNavigation)typeof(Details).GetField("navigation",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(details);
                Check(navigation.Page==PopupPage.More,"more action opens the native secondary page");
                process.Invoke(details,new object[]{message,Keys.Escape});
                Check(navigation.Page==PopupPage.Main&&(int)typeof(Details).GetField("keyboardFocus",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(details)==1,"return restores focus to its originating control");
                foreach(PopupPage page in Enum.GetValues(typeof(PopupPage)))if(page!=PopupPage.Main) {
                    navigation.Page=page;var controls=PopupPages.Build(context,navigation,true);var logical=PopupPages.SizeFor(controls);
                    foreach(var control in controls)Check(control.Rect.Left>=0&&control.Rect.Right<=logical.Width&&control.Rect.Bottom<=logical.Height,"secondary page bounds "+page);
                    using(var image=new Bitmap(logical.Width*2,logical.Height*2))using(var graphics=Graphics.FromImage(image)){graphics.Clear(Color.Transparent);graphics.ScaleTransform(2,2);PopupPages.Paint(graphics,context,navigation,controls,false,-1,-1,-1);image.Save(Path.Combine(output,"page-"+page+".png"));}
                }
                navigation.Page=PopupPage.Preferences;navigation.VisibleChoices=3;navigation.Offset=0;
                var compactPreferences=PopupPages.Build(context,navigation,true);
                Check(compactPreferences.Exists(delegate(PopupControl row){return row.Id==8;})&&PopupPages.SizeFor(compactPreferences).Height<260,"secondary preferences paginate in a small high-DPI work area");
                navigation.Offset=5;compactPreferences=PopupPages.Build(context,navigation,true);
                Check(compactPreferences.Exists(delegate(PopupControl row){return row.Id==217;})&&compactPreferences.Exists(delegate(PopupControl row){return row.Id==7;}),"clear-history remains reachable on the last preferences page");
                navigation.VisibleChoices=7;navigation.Offset=0;
                navigation.Page=PopupPage.Main;
                var mainControls=PopupPages.Build(context,navigation,true);
                bool disjoint=true;for(int a=0;a<mainControls.Count;a++)for(int b=a+1;b<mainControls.Count;b++)disjoint&=!mainControls[a].Rect.IntersectsWith(mainControls[b].Rect);
                Check(disjoint,"expanded click regions do not overlap");
                float uiScale=(float)typeof(Details).GetField("dpiScale",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(details);
                var first=VisualDesign.StageRect(0,4);var second=VisualDesign.StageRect(1,4);int beforeWrites=writes;
                var down=new MouseEventArgs(MouseButtons.Left,1,(int)((first.X+first.Width/2)*uiScale),(int)((first.Y+first.Height/2)*uiScale),0);
                var up=new MouseEventArgs(MouseButtons.Left,1,(int)((second.X+second.Width/2)*uiScale),(int)((second.Y+second.Height/2)*uiScale),0);
                typeof(Control).GetMethod("OnMouseDown",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(details,new object[]{down});
                typeof(Control).GetMethod("OnMouseUp",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(details,new object[]{up});
                Check(writes==beforeWrites&&!context.IsDpiRefreshing,"pressing one stage and releasing on another never writes to the device");
                Check((bool)process.Invoke(details,new object[]{message,Keys.Escape}),"Escape closes the popup through the animation controller");
            }
            context.ExitThread();context=null;
            Console.WriteLine("PASS "+passed+" UI/controller checks; hardware and real audio writes were replaced with fakes");
            Console.WriteLine("UI previews: "+output);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally {if(context!=null)context.ExitThread();cached.SetValue(null,previous);}
    }
}
