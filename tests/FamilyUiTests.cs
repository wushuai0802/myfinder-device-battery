using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

// Offline regression tests + real WinForms/PopupPages screenshot renderer for UI-01.
// All child worker processes run a local copy of this test executable with -I; no HID/hardware is opened.
static class FamilyUiTests {
    const string KeyA="xiberia|unit-0123456789abcdef01234567";
    const string KeyB="atk_v2|unit-fedcba9876543210fedcba98";
    const string KeyC="mchose|unit-aaaaaaaaaaaaaaaaaaaaaaaa";
    const string IdA="device-111111111111111111111111";
    const string IdB="device-222222222222222222222222";
    const string PhysA="device-aaaa00000000000000000001";
    const string PhysB="device-bbbb00000000000000000002";
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static int checks;

    static void Check(bool value,string label){if(!value)throw new Exception("FAIL "+label);checks++;}
    static void Reject(Action action,string label){try{action();}catch(Exception){checks++;return;}throw new Exception("FAIL expected exception: "+label);}
    static object Field(object obj,string name){return obj.GetType().GetField(name,Private).GetValue(obj);}
    static void Set(object obj,string name,object value){obj.GetType().GetField(name,Private).SetValue(obj,value);}
    static object Call(object obj,string name,params object[] args){return obj.GetType().GetMethod(name,Private).Invoke(obj,args);}
    static void Pump(Func<bool> done,int timeoutMs=7000){
        var end=DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while(!done()&&DateTime.UtcNow<end){Application.DoEvents();Thread.Sleep(5);}
        Check(done(),"async UI operation finishes within budget");
    }

    static void SettingsPersistence(string root){
        string path=Path.Combine(root,"audio-settings.json");
        Check(new AudioBindings().AutoDetectMoreDevices,"default AutoDetectMoreDevices is true");
        File.WriteAllText(path,"{\"SchemaVersion\":1,\"Theme\":\"dark\",\"MouseAlias\":\"保留鼠标\"}",new UTF8Encoding(false));
        var legacy=DeviceSettings.LoadBindings(path);
        Check(legacy.AutoDetectMoreDevices&&legacy.Theme=="dark"&&legacy.MouseAlias=="保留鼠标","missing AutoDetectMoreDevices defaults true without losing fields");
        var turnedOff=legacy.Clone();turnedOff.AutoDetectMoreDevices=false;
        DeviceSettings.SaveBindings(path,turnedOff);
        var reloaded=DeviceSettings.LoadBindings(path);
        Check(!reloaded.AutoDetectMoreDevices&&reloaded.Clone().AutoDetectMoreDevices==false,"explicit false survives save/load/clone");
        foreach(string bad in new[]{"\"no\"","123","{}","[]","null"}){
            File.WriteAllText(path,"{\"SchemaVersion\":1,\"AutoDetectMoreDevices\":"+bad+",\"Theme\":\"light\",\"MouseAlias\":\"完好鼠标\"}",new UTF8Encoding(false));
            var recovered=DeviceSettings.LoadBindings(path);
            Check(recovered.AutoDetectMoreDevices&&recovered.Theme=="light"&&recovered.MouseAlias=="完好鼠标","malformed AutoDetectMoreDevices="+bad+" falls back to true cleanly");
        }
    }

    static void StoreEnvelopeAndCorruption(string root){
        string dir=Path.Combine(root,"store-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path=Path.Combine(dir,"family-memory.json");
        var store=new FamilyMemoryStore(path);
        Check(!store.HasEntries&&store.StoppedCount==0,"fresh store is empty");

        string snapJson="{\""+KeyA+"\":{\"working\":\"battery\",\"rounds\":2,\"retired\":[\"probe_a\"],\"disabled\":true,\"silentPolls\":3,\"relearned\":true},\""+KeyB+"\":{\"working\":null,\"rounds\":1,\"retired\":[],\"disabled\":false,\"silentPolls\":0}}";
        store.RecordDisable(snapJson,KeyA,"  XIBERIA\r\n T10  ");
        Check(store.HasEntries&&store.StoppedCount==1,"RecordDisable persists disabled entry");
        string disk=File.ReadAllText(path,Encoding.UTF8);
        Check(disk.Contains("\"schema\":1")&&disk.Contains("\"familyMemory\"")&&disk.Contains("\"stoppedNames\"")&&disk.Contains("\"relearned\":true")&&disk.Contains("\"silentPolls\":3"),"envelope preserves all engine subfields and schema");
        Check(Directory.GetFiles(dir,"*.tmp").Length==0,"atomic save leaves no temp files");

        string engineJson=store.GetFamilyMemoryJson();
        Check(!engineJson.Contains("stoppedNames")&&!engineJson.Contains("\"schema\""),"engine payload excludes stoppedNames and schema");

        var reopened=new FamilyMemoryStore(path);
        var stopped=reopened.GetStoppedDevices();
        Check(stopped.Count==1&&stopped[0].MemoryKey==KeyA&&stopped[0].DisplayName=="XIBERIA T10","store reloads name without control chars");

        // Control chars + 32 char truncation + blank fallback
        string longRaw="\u0007"+new string('甲',40);
        reopened.RecordDisable(null,KeyB,longRaw);
        var two=reopened.GetStoppedDevices();
        Check(two.Count==2,"both disabled entries tracked");
        var entryB=two.Find(delegate(StoppedFamilyDevice item){return item.MemoryKey==KeyB;});
        Check(entryB.DisplayName.Length==32,"display name truncated to 32 chars");

        reopened.RecordDisable(null,KeyC,"  \r\n\t  ");
        var entryC=reopened.GetStoppedDevices().Find(delegate(StoppedFamilyDevice item){return item.MemoryKey==KeyC;});
        Check(entryC!=null&&entryC.DisplayName==FamilyMemoryJson.DefaultDisplayName,"blank name defaults to 未命名设备");

        // Merging a snapshot where KeyB is no longer disabled prunes KeyB from stoppedNames
        string prunedSnap="{\""+KeyA+"\":{\"working\":\"battery\",\"rounds\":2,\"retired\":[],\"disabled\":true,\"silentPolls\":0},\""+KeyB+"\":{\"working\":\"battery\",\"rounds\":1,\"retired\":[],\"disabled\":false,\"silentPolls\":0}}";
        reopened.MergeFromSnapshot(prunedSnap);
        Check(reopened.StoppedCount==1,"MergeFromSnapshot updates stopped count");
        Check(!File.ReadAllText(path,Encoding.UTF8).Contains(KeyB+"\":\"甲"),"pruned non-disabled name is removed from stoppedNames");

        // 256 entry capacity succeeds
        var big=new StringBuilder("{");
        for(int i=0;i<256;i++){
            if(i>0)big.Append(',');
            big.Append("\"fam|unit-").Append(i.ToString("x24")).Append("\":{\"working\":\"r\",\"rounds\":1,\"retired\":[],\"disabled\":false,\"silentPolls\":0}");
        }
        big.Append('}');
        Check(reopened.MergeFromSnapshot(big.ToString()),"256 entries accepted within limit");
        Check(reopened.GetFamilyMemoryJson().Length>20000,"256 entries retained in full");

        // Corrupt JSON triggers .corrupt once and starts empty
        string corruptPath=Path.Combine(root,"corrupt-store","family-memory.json");
        Directory.CreateDirectory(Path.GetDirectoryName(corruptPath));
        File.WriteAllText(corruptPath,"{not-valid-json",new UTF8Encoding(false));
        var c1=new FamilyMemoryStore(corruptPath);
        Check(!c1.HasEntries&&File.Exists(corruptPath+".corrupt"),"corrupt file backed up once and starts empty");
        File.WriteAllText(corruptPath+".corrupt","keep-first-backup",new UTF8Encoding(false));
        File.WriteAllText(corruptPath,"{\"schema\":1,\"familyMemory\":{\"bad_key\":{\"disabled\":true}}}",new UTF8Encoding(false));
        var c2=new FamilyMemoryStore(corruptPath);
        Check(!c2.HasEntries&&File.ReadAllText(corruptPath+".corrupt")=="keep-first-backup","invalid memoryKey triggers empty start without overwriting existing .corrupt backup");

        // Oversize file (>262144 bytes) also triggers empty start
        string overPath=Path.Combine(root,"oversize-store","family-memory.json");
        Directory.CreateDirectory(Path.GetDirectoryName(overPath));
        File.WriteAllText(overPath,new string(' ',262145),new UTF8Encoding(false));
        var c3=new FamilyMemoryStore(overPath);
        Check(!c3.HasEntries&&File.Exists(overPath+".corrupt"),"oversized file backed up and starts empty");
    }

    static string Q(string value){return "\""+value.Replace("\\","\\\\").Replace("\"","\\\"")+"\"";}
    static string FamilyDeviceJson(string id,string physical,string name,bool probe=true,bool canDisable=true,string source="family:xiberia"){
        string stamp=DateTime.UtcNow.ToString("o");
        return "{\"id\":"+Q(id)+",\"physicalId\":"+Q(physical)+",\"name\":"+Q(name)+",\"kind\":\"mouse\",\"percent\":82,\"online\":true,\"fresh\":true,\"connectionState\":\"connected\",\"sourceQuality\":\"device\",\"confirmedAtUtc\":"+Q(stamp)+",\"provider\":\"families\",\"transport\":\"USB / 2.4G\",\"modelSource\":"+Q(source)+",\"familyProbe\":"+(probe?"true":"false")+",\"canDisable\":"+(canDisable?"true":"false")+",\"verificationLabel\":\"协议已接入，待型号实测\"}";
    }
    static string MakeWorker(string root,string mode){
        string path=Path.Combine(root,mode+"-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(path,"runtime"));
        File.Copy(Assembly.GetExecutingAssembly().Location,Path.Combine(path,"runtime","python.exe"));
        File.WriteAllText(Path.Combine(path,"server.py"),"# fake C1 JSON reader");
        return path;
    }

    static void WorkerProtocolAndConcurrency(string root){
        // Snapshot parsing: modelSource, familyProbe, canDisable, fail-closed defaults, complete-only familyMemory, 512 KiB budget
        var parsed=GenericBatteryWorker.Parse("{\"schema\":2,\"requestId\":1,\"generation\":1,\"complete\":true,\"probeFamilies\":true,\"familyMemory\":{\""+KeyA+"\":{\"working\":\"bat\",\"rounds\":1,\"retired\":[],\"disabled\":true,\"silentPolls\":0}},\"devices\":["+FamilyDeviceJson(IdA,PhysA,"ATK X1",true,true,"family:atk_v2")+","+FamilyDeviceJson(IdB,PhysB,"Unknown",false,false,"untrusted")+"],\"health\":[],\"errors\":[]}");
        Check(parsed.ProbeFamilies==true&&parsed.FamilyMemoryJson!=null&&parsed.FamilyMemoryJson.Contains(KeyA),"complete snapshot parses probeFamilies and familyMemory");
        Check(parsed.Devices[0].ModelSource=="family:atk_v2"&&parsed.Devices[0].FamilyProbe&&parsed.Devices[0].CanDisable,"allowed family:<id> modelSource and flags preserved");
        Check(parsed.Devices[1].ModelSource==null&&!parsed.Devices[1].FamilyProbe&&!parsed.Devices[1].CanDisable,"untrusted modelSource cleared and flags default false");

        var legacy=GenericBatteryWorker.Parse("{\"schema\":2,\"requestId\":1,\"generation\":1,\"complete\":false,\"familyMemory\":{\""+KeyA+"\":{\"disabled\":true}},\"devices\":[{\"id\":"+Q(IdA)+",\"name\":\"Legacy\",\"kind\":\"mouse\",\"provider\":\"logitech\",\"percent\":50,\"online\":true,\"fresh\":true,\"confirmedAtUtc\":"+Q(DateTime.UtcNow.ToString("o"))+"}],\"health\":[],\"errors\":[]}");
        Check(!legacy.Devices[0].FamilyProbe&&!legacy.Devices[0].CanDisable&&legacy.FamilyMemoryJson==null,"legacy/partial frames default flags false and omit familyMemory");

        // Large snapshot near 256 entries (within 512 KiB) is not truncated
        var entries=new StringBuilder("{");
        for(int i=0;i<256;i++){if(i>0)entries.Append(',');entries.Append("\"f|unit-").Append(i.ToString("x24")).Append("\":{\"working\":\"read_bat\",\"rounds\":1,\"retired\":[\"r1\"],\"disabled\":false,\"silentPolls\":0}");}
        entries.Append('}');
        string bigSnap="{\"schema\":2,\"requestId\":2,\"generation\":1,\"complete\":true,\"probeFamilies\":true,\"familyMemory\":"+entries+",\"devices\":[],\"health\":[],\"errors\":[]}";
        var bigParsed=GenericBatteryWorker.Parse(bigSnap);
        Check(bigParsed.FamilyMemoryJson!=null&&bigParsed.FamilyMemoryJson.Contains("unit-"+255.ToString("x24")),"256-entry familyMemory within 512 KiB is preserved without truncation");

        // Startup restore runs BEFORE first poll and aborts on failure
        string storePath=Path.Combine(root,"worker-store.json");
        var store=new FamilyMemoryStore(storePath);
        store.RecordDisable("{\""+KeyA+"\":{\"working\":\"bat\",\"rounds\":1,\"retired\":[],\"disabled\":true,\"silentPolls\":0}}",KeyA,"Stopped Mouse");
        string okFolder=MakeWorker(root,"ok");
        using(var worker=new GenericBatteryWorker(okFolder,false,2500)){
            worker.UseFamilyMemory(store);
            var snap=worker.Poll(true);
            Check(String.IsNullOrEmpty(snap.Error)&&snap.Devices.Length==1&&snap.Devices[0].Id==IdB,"poll succeeds and returns only non-disabled device");
            string[] argsLog=File.ReadAllLines(Path.Combine(okFolder,"args.log"));
            Check(argsLog[0].Contains("--probe-families on"),"default AutoDetectMoreDevices passes --probe-families on");
            string[] reqLog=File.ReadAllLines(Path.Combine(okFolder,"requests.log"));
            Check(reqLog.Length>=2&&reqLog[0].Contains("\"op\":\"set\"")&&reqLog[1].Contains("\"command\":\"poll\""),"Start() restores familyMemory via set strictly before first poll");
            Check(!reqLog[0].Contains("stoppedNames"),"set command never sends stoppedNames");

            // Mismatched requestId/generation frames ignored, matching frame accepted
            var dis=worker.DisableFamilyDevice(IdB);
            Check(dis.Ok&&dis.MemoryKey==KeyB,"DisableFamilyDevice accepts matching requestId+generation reply after skipping mismatched frames");
            Check(worker.RestartForProbeFamilies(false),"RestartForProbeFamilies succeeds");
            argsLog=File.ReadAllLines(Path.Combine(okFolder,"args.log"));
            Check(argsLog[argsLog.Length-1].Contains("--probe-families off"),"RestartForProbeFamilies passes --probe-families off");
        }

        // Startup restore failure stops process and never sends poll
        foreach(string failMode in new[]{"set-fail","set-oversize"}){
            string failFolder=MakeWorker(root,failMode);
            using(var worker=new GenericBatteryWorker(failFolder,false,1500)){
                worker.UseFamilyMemory(store);
                var snap=worker.Poll(true);
                Check(!String.IsNullOrEmpty(snap.Error),"restore failure surfaces unavailable error");
                Check(Field(worker,"process")==null,"failed Start() stops child process");
                string[] reqLog=File.ReadAllLines(Path.Combine(failFolder,"requests.log"));
                Check(reqLog.Length==1&&reqLog[0].Contains("\"op\":\"set\""),"failed startup restore never sends poll command");
            }
        }
    }

    sealed class UiHarness:IDisposable {
        internal readonly string Root,WorkerDir;
        internal readonly FamilyMemoryStore Store;
        internal readonly GenericBatteryWorker Worker;
        internal readonly TrayContext Owner;
        internal readonly Details Popup;
        internal readonly PopupNavigation Nav;
        internal int Saves;
        internal bool FailSaves;
        internal UiHarness(string root,string mode="ok"){
            Root=Path.Combine(root,"ui-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            DeviceSettings.UseTransientConfiguration(new AudioBindings {RememberReadings=true,ReducedMotion=true,VolumeCollapsed=false,AutoDetectMoreDevices=true});
            Store=new FamilyMemoryStore(Path.Combine(Root,"family-memory.json"));
            WorkerDir=MakeWorker(Root,mode);
            Worker=new GenericBatteryWorker(WorkerDir,false,2500);
            Owner=new TrayContext(false,delegate{return new MouseSnapshot();},delegate{return null;},null,null,false,null,null,null,delegate{return false;},null,Worker,null,null,delegate(AudioBindings pref){
                if(FailSaves)throw new IOException("disk_full");
                Saves++;DeviceSettings.UseTransientConfiguration(pref);
            },false,Store,Root);
            Pump(delegate{return !Owner.IsRefreshing&&!Owner.GenericScanning;});
            ((System.Windows.Forms.Timer)Field(Owner,"refresh")).Stop();
            Owner.NativeMousePresent=false;
            Popup=new Details(Owner);
            Set(Owner,"details",Popup);
            Nav=(PopupNavigation)Field(Popup,"navigation");
            Nav.Reset();
            Popup.SyncDevices();
        }
        internal List<PopupControl> Controls(){return (List<PopupControl>)Call(Popup,"ControlsForPage");}
        internal PopupControl Find(string text){return Controls().Find(delegate(PopupControl c){return c.Text==text;});}
        internal PopupControl ById(int id){return Controls().Find(delegate(PopupControl c){return c.Id==id;});}
        internal PopupControl FindById(int id){
            for(int i=0;i<15;i++){
                var row=ById(id);if(row!=null)return row;
                var next=ById(8);if(next==null)break;
                Activate(next);
            }
            return null;
        }
        internal void Activate(PopupControl c){Call(Popup,"PerformAction",c);}
        internal void Page(PopupPage page){Nav.Page=page;Nav.Offset=0;Nav.Notice="";Call(Popup,"UpdateLayoutSize");Popup.SyncDevices();}
        public void Dispose(){
            if(Popup!=null&&!Popup.IsDisposed)Popup.Close();
            Owner.Dispose();
            Worker.Dispose();
        }
    }

    static void UiEndToEnd(string root){
        // 1. Preferences: wrapped toggle geometry (normal & large), save failure rollback, worker failure fallback, successful restart
        using(var h=new UiHarness(root,"ok")){
            foreach(string font in new[]{"normal","large"}){
                var p=DeviceSettings.Current;p.FontSize=font;DeviceSettings.UseTransientConfiguration(p);
                h.Page(PopupPage.Preferences);
                var nav=new PopupNavigation {Page=PopupPage.Preferences,VisibleChoices=100};
                var allRows=PopupPages.Build(h.Owner,nav,false);
                var auto=allRows.Find(delegate(PopupControl r){return r.Id==PopupPages.AutoDetectToggleId;});
                Check(auto!=null&&auto.Toggle&&auto.Checked&&!String.IsNullOrEmpty(auto.Detail),"auto detect toggle present with wrapped detail");
                Check(!allRows.Exists(delegate(PopupControl r){return r.Id==PopupPages.StoppedDevicesEntryId;}),"stopped devices entry hidden when 0 stopped");
                int titleH=VisualDesign.LargeFont?24:22;
                float textW=Math.Max(40,auto.Rect.Right-43-(auto.Rect.X+6));
                var titleRect=new RectangleF(auto.Rect.X+6,auto.Rect.Y+4,textW,titleH);
                var detailRect=new RectangleF(auto.Rect.X+6,auto.Rect.Y+4+titleH,textW,auto.Rect.Bottom-4-(auto.Rect.Y+4+titleH));
                var toggleRect=PopupPages.ToggleBounds(auto.Rect);
                Check(!titleRect.IntersectsWith(detailRect)&&!titleRect.IntersectsWith(toggleRect)&&!detailRect.IntersectsWith(toggleRect),"toggle title, detail, and switch rectangles are strictly disjoint in "+font);
                Check(auto.Rect.Height>titleH+16&&toggleRect.Right<=auto.Rect.Right,"wrapped toggle height accommodates multi-line detail in "+font);
                using(var bmp=new Bitmap(VisualDesign.PanelWidth,PopupPages.SizeFor(allRows).Height))using(var g=Graphics.FromImage(bmp))PopupPages.Paint(g,h.Owner,nav,allRows,false,-1,-1,-1);
            }
            var norm=DeviceSettings.Current;norm.FontSize="normal";DeviceSettings.UseTransientConfiguration(norm);

            // Navigate to page containing AutoDetectToggleId on bounded height
            h.Nav.MaximumHeight=360;h.Page(PopupPage.Preferences);
            for(int guard=0;guard<10&&h.ById(PopupPages.AutoDetectToggleId)==null;guard++){
                var next=h.ById(8);if(next==null)break;h.Activate(next);
            }
            var toggle=h.ById(PopupPages.AutoDetectToggleId);
            Check(toggle!=null,"auto detect toggle reachable in bounded Preferences");

            // Save failure rolls back setting and does not restart worker
            h.FailSaves=true;
            h.Activate(toggle);
            Check(DeviceSettings.Current.AutoDetectMoreDevices&&h.Nav.Notice=="设置未保存"&&!h.Owner.IsProbeToggleBusy,"save failure rolls back setting and shows 设置未保存");
            h.FailSaves=false;

            // Successful save + restart
            h.Activate(h.ById(PopupPages.AutoDetectToggleId));
            Check(!DeviceSettings.Current.AutoDetectMoreDevices&&h.Saves==1,"save persisted immediately");
            Pump(delegate{return !h.Owner.IsProbeToggleBusy&&!h.Owner.GenericScanning;});
            Check(h.Owner.WirelessDevices.Length==2,"after restart poll returns 2 devices");
            string[] argsLog=File.ReadAllLines(Path.Combine(h.WorkerDir,"args.log"));
            Check(argsLog[argsLog.Length-1].Contains("--probe-families off"),"worker restarted with --probe-families off");
        }

        // Engine unavailable during toggle preserves user intent and displays notice
        using(var h=new UiHarness(root,"restart-fail")){
            h.Page(PopupPage.Preferences);
            var autoToggle=h.FindById(PopupPages.AutoDetectToggleId);
            Check(autoToggle!=null,"auto detect toggle reachable in Preferences");
            h.Activate(autoToggle);
            Pump(delegate{return !h.Owner.IsProbeToggleBusy;});
            Check(!DeviceSettings.Current.AutoDetectMoreDevices&&h.Nav.Notice=="已保存，读取组件暂未就绪","failed restart keeps saved setting and shows ready notice (auto="+DeviceSettings.Current.AutoDetectMoreDevices+", notice="+h.Nav.Notice+", saves="+h.Saves+", disposed="+h.Popup.IsDisposed+")");
        }

        // 2. Device Details, ConfirmStopReading, multi-layout cleanup, cache cleanup, unknown_device, StoppedDevices pagination & recovery
        using(var h=new UiHarness(root,"two-devices")){
            h.Owner.RefreshWireless();
            Pump(delegate{return !h.Owner.GenericScanning&&h.Owner.WirelessDevices.Length==2;});
            var writer=Field(h.Owner,"fileWriter");
            writer.GetType().GetMethod("Flush",Private).Invoke(writer,new object[]{5000});
            string cachePath=Path.Combine(h.Root,"wireless-cache.json");
            Check(File.Exists(cachePath)&&File.ReadAllText(cachePath).Contains(IdA),"wireless-cache.json initially caches device A");

            // Pin device A to tray so we verify no NullReferenceException after stopping it
            var pref=DeviceSettings.Current;pref.TrayDeviceId=IdA;DeviceSettings.UseTransientConfiguration(pref);

            // Open device A (familyProbe=true, canDisable=true)
            h.Nav.SelectedDeviceKey=IdA;h.Nav.SelectedDeviceName="ATK 烈空 X1";h.Page(PopupPage.Device);
            Check(h.Controls().Exists(delegate(PopupControl r){return r.Information&&r.Text=="适配验证"&&r.Value=="自动试读 · 待实机验证";}),"familyProbe=true shows expected verification label");
            var stopEntry=h.FindById(PopupPages.StopReadingEntryId);
            Check(stopEntry!=null&&stopEntry.Text=="停止读取","canDisable=true shows 停止读取");

            // Non-disableable device hides 停止读取
            h.Nav.SelectedDeviceKey=IdB;h.Nav.SelectedDeviceName="普通精确设备";h.Page(PopupPage.Device);
            Check(h.Controls().Exists(delegate(PopupControl r){return r.Information&&r.Text=="适配验证"&&r.Value=="协议已接入，待型号实测";}),"familyProbe=false keeps VerificationLabel");
            Check(h.FindById(PopupPages.StopReadingEntryId)==null,"canDisable=false hides 停止读取");

            // ConfirmStopReading for device A from Main -> Device -> ConfirmStopReading
            h.Nav.Reset();h.Page(PopupPage.Main);
            h.Activate(h.Controls().Find(delegate(PopupControl r){return r.DeviceKey==IdA&&r.Id>=1000&&r.Id<2000;}));
            Check(h.Nav.Page==PopupPage.Device,"clicking card opens Device page");
            h.Activate(h.FindById(PopupPages.StopReadingEntryId));
            Check(h.Nav.Page==PopupPage.ConfirmStopReading,"clicking 停止读取 opens ConfirmStopReading");
            Check(h.Controls().Exists(delegate(PopupControl r){return r.Information&&r.Text.Contains("隐藏")&&r.Text.Contains("停止读取");}),"confirm page explains hide vs stop reading");

            h.Activate(h.ById(PopupPages.ConfirmStopReadingId));
            Pump(delegate{return !h.Owner.IsFamilyOpBusy;});
            writer.GetType().GetMethod("Flush",Private).Invoke(writer,new object[]{5000});
            Check(h.Nav.Page==PopupPage.Main,"after stopping from Main, returns to Main");
            Check(h.Owner.WirelessDevices.Length==1&&h.Owner.WirelessDevices[0].Id==IdB,"device A removed immediately from WirelessDevices while device B stays");
            Check(!File.ReadAllText(cachePath).Contains(IdA),"device A removed from wireless-cache.json");
            Check(h.Store.StoppedCount==1&&h.Store.GetStoppedDevices()[0].DisplayName=="ATK 烈空 X1","display name persisted in family-memory.json");
            foreach(string layout in new[]{"ring","bar","grid"}){
                var lp=DeviceSettings.Current;lp.PanelLayout=layout;DeviceSettings.UseTransientConfiguration(lp);
                var wl=WirelessLayout.Build(h.Owner,0,520);
                Check(!wl.All.Exists(delegate(WirelessCard c){return c.Id==IdA;})&&wl.All.Exists(delegate(WirelessCard c){return c.Id==IdB;}),"device A absent and B present in "+layout+" layout");
            }

            // StoppedDevices entry visible in Preferences ("1 台") and opens StoppedDevices
            h.Page(PopupPage.Preferences);
            var stoppedEntry=h.FindById(PopupPages.StoppedDevicesEntryId);
            Check(stoppedEntry!=null&&stoppedEntry.Value=="1 台","Preferences shows 1 台 stopped device");
            h.Activate(stoppedEntry);
            Check(h.Nav.Page==PopupPage.StoppedDevices,"opens StoppedDevices page");
            var rowA=h.Controls().Find(delegate(PopupControl r){return r.ActionKey=="family:enable";});
            Check(rowA!=null&&rowA.Text=="ATK 烈空 X1"&&rowA.Value=="恢复读取","StoppedDevices row displays device name and 恢复读取");

            // Click 恢复读取 -> returns to Preferences with notice and NO immediate poll
            int reqBeforeEnable=File.ReadAllLines(Path.Combine(h.WorkerDir,"requests.log")).Length;
            h.Activate(rowA);
            Pump(delegate{return !h.Owner.IsFamilyOpBusy;});
            Check(h.Nav.Page==PopupPage.Preferences&&h.Nav.Notice=="已恢复，下一轮读取时生效","removing last stopped item auto-returns to Preferences with notice");
            Check(h.Store.StoppedCount==0&&h.ById(PopupPages.StoppedDevicesEntryId)==null,"entry hidden again when count returns to 0");
            string[] reqAfterEnable=File.ReadAllLines(Path.Combine(h.WorkerDir,"requests.log"));
            Check(reqAfterEnable.Length==reqBeforeEnable+1&&reqAfterEnable[reqAfterEnable.Length-1].Contains("\"op\":\"enable\""),"enable does not trigger an extra immediate poll");
        }

        // 3. unknown_device keeps stopped record untouched and shows disconnect message
        using(var h=new UiHarness(root,"unknown-disable")){
            h.Owner.RefreshWireless();
            Pump(delegate{return !h.Owner.GenericScanning&&h.Owner.WirelessDevices.Length==1;});
            h.Nav.SelectedDeviceKey=IdA;h.Nav.SelectedDeviceName="XIBERIA T10";h.Page(PopupPage.ConfirmStopReading);
            h.Activate(h.ById(PopupPages.ConfirmStopReadingId));
            Pump(delegate{return !h.Owner.IsFamilyOpBusy;});
            Check(h.Nav.Page==PopupPage.ConfirmStopReading&&h.Nav.Notice=="设备已断开，请重新连接后再试"&&h.Store.StoppedCount==0,"unknown_device keeps confirm page and store unchanged");
        }

        // 4. StoppedDevices multi-page removal & failure retry
        using(var h=new UiHarness(root,"ok")){
            var mem=new StringBuilder("{");
            for(int i=0;i<10;i++){
                if(i>0)mem.Append(',');
                mem.Append("\"fam|unit-").Append(i.ToString("x24")).Append("\":{\"working\":\"bat\",\"rounds\":1,\"retired\":[],\"disabled\":true,\"silentPolls\":0}");
            }
            mem.Append('}');
            h.Store.MergeFromSnapshot(mem.ToString());
            h.Worker.RestartForProbeFamilies(true);
            h.Page(PopupPage.StoppedDevices);
            int perPage=h.Controls().FindAll(delegate(PopupControl r){return r.ActionKey=="family:enable";}).Count;
            var next=h.ById(8);Check(next!=null,"StoppedDevices paginates when > VisibleChoices");
            for(int guard=0;guard<20&&h.ById(8)!=null;guard++)h.Activate(h.ById(8));
            Check(h.ById(8)==null,"reached last page of StoppedDevices");
            int beforeOffset=h.Nav.Offset;
            Check(beforeOffset==10-perPage,"last page reaches end of 10 stopped items");
            var lastRow=h.Controls().Find(delegate(PopupControl r){return r.ActionKey=="family:enable";});
            h.Activate(lastRow);
            Pump(delegate{return !h.Owner.IsFamilyOpBusy;});
            Check(h.Nav.Page==PopupPage.StoppedDevices&&h.Store.StoppedCount==9&&h.Nav.Offset==Math.Max(0,9-perPage),"removing item on last page recalculates offset and stays on StoppedDevices");
        }

        // 5. enable failure keeps row and allows retry
        using(var h=new UiHarness(root,"enable-fail")){
            h.Store.RecordDisable("{\""+KeyA+"\":{\"working\":\"bat\",\"rounds\":1,\"retired\":[],\"disabled\":true,\"silentPolls\":0}}",KeyA,"可重试设备");
            h.Page(PopupPage.StoppedDevices);
            var row=h.Controls().Find(delegate(PopupControl r){return r.ActionKey=="family:enable";});
            Check(row!=null,"stopped row shown before retry");
            h.Activate(row);
            Pump(delegate{return !h.Owner.IsFamilyOpBusy;});
            Check(h.Nav.Page==PopupPage.StoppedDevices&&h.Store.StoppedCount==1&&h.Nav.Notice=="等待读取组件响应超时，请稍后重试","failed enable keeps row and shows error notice");
            var retryRow=h.Controls().Find(delegate(PopupControl r){return r.ActionKey=="family:enable";});
            Check(retryRow!=null&&retryRow.Enabled,"row remains enabled for retry after failure");
        }
    }

    static void RenderScreenshots(string outDir){
        Directory.CreateDirectory(outDir);
        string temp=Path.Combine(Path.GetTempPath(),"ui01-shots-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try{
            var specs=new[]{
                new {File="light-ocean-normal.png",Theme="light",Style="ocean",Font="normal"},
                new {File="light-ocean-large.png",Theme="light",Style="ocean",Font="large"},
                new {File="dark-native-normal.png",Theme="dark",Style="native",Font="normal"},
                new {File="dark-native-large.png",Theme="dark",Style="native",Font="large"},
            };
            foreach(var spec in specs){
                DeviceSettings.UseTransientConfiguration(new AudioBindings {
                    Theme=spec.Theme,ThemeStyle=spec.Style,FontSize=spec.Font,
                    RememberReadings=true,ReducedMotion=true,VolumeCollapsed=false,AutoDetectMoreDevices=true
                });
                var store=new FamilyMemoryStore(Path.Combine(temp,spec.File+".json"));
                store.RecordDisable("{\""+KeyA+"\":{\"working\":\"bat\",\"rounds\":1,\"retired\":[],\"disabled\":true,\"silentPolls\":0}}",KeyA,"XIBERIA T10");
                store.RecordDisable(null,KeyB,"超长名称的未知型号无线三模游戏鼠标 Pro Max Ultra 2026");
                store.RecordDisable(null,KeyC,"");
                using(var owner=new TrayContext(false,delegate{return new MouseSnapshot();},delegate{return null;},null,null,false,null,null,null,delegate{return false;},null,null,null,null,DeviceSettings.UseTransientConfiguration,false,store,temp)){
                    owner.NativeMousePresent=false;
                    owner.GenericReady=true;
                    owner.WirelessDevices=new[]{
                        new WirelessDevice {
                            Id=IdA,PhysicalId=PhysA,Name="ATK 烈空 X1 Ultra",Kind="mouse",Percent=86,Online=true,Fresh=true,
                            ConnectionState="connected",MeasurementState="current",SourceQuality="device",
                            ConfirmedAtUtc=DateTime.UtcNow.ToString("o"),Provider="families",Transport="USB / 2.4G",
                            ModelSource="family:atk_v2",FamilyProbe=true,CanDisable=true,VerificationLabel="协议已接入，待型号实测"
                        }
                    };
                    bool dark=spec.Theme=="dark";
                    var panels=new List<Bitmap>();
                    var titles=new[]{"设置 · 设备识别","设备详情","停读确认","已停读设备列表"};
                    var navPrefs=new PopupNavigation {Page=PopupPage.Preferences,VisibleChoices=7,Offset=10};
                    var navDev=new PopupNavigation {Page=PopupPage.Device,SelectedDeviceKey=IdA,SelectedDeviceName="ATK 烈空 X1 Ultra",VisibleChoices=20};
                    var navConfirm=new PopupNavigation {Page=PopupPage.ConfirmStopReading,SelectedDeviceKey=IdA,SelectedDeviceName="ATK 烈空 X1 Ultra",VisibleChoices=7};
                    var navStopped=new PopupNavigation {Page=PopupPage.StoppedDevices,VisibleChoices=7};
                    var navs=new[]{navPrefs,navDev,navConfirm,navStopped};
                    for(int i=0;i<navs.Length;i++)panels.Add(RenderPopupBitmap(owner,navs[i],dark));

                    int pad=24,gap=20,labelH=36;
                    int totalW=pad*2+gap*(panels.Count-1);
                    int maxH=0;
                    foreach(var p in panels){totalW+=p.Width;maxH=Math.Max(maxH,p.Height);}
                    int totalH=pad*2+labelH+maxH;
                    Color bg=dark?Color.FromArgb(20,23,28):Color.FromArgb(236,240,246);
                    Color headerFg=dark?Color.FromArgb(228,232,240):Color.FromArgb(30,41,59);
                    using(var canvas=new Bitmap(totalW,totalH,PixelFormat.Format32bppPArgb))
                    using(var g=Graphics.FromImage(canvas)){
                        g.Clear(bg);
                        g.SmoothingMode=SmoothingMode.AntiAlias;
                        g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                        int x=pad;
                        using(var font=new Font("Microsoft YaHei UI",16,FontStyle.Bold,GraphicsUnit.Pixel))
                        using(var brush=new SolidBrush(headerFg)){
                            for(int i=0;i<panels.Count;i++){
                                g.DrawString(titles[i],font,brush,x,pad);
                                g.DrawImageUnscaled(panels[i],x,pad+labelH);
                                x+=panels[i].Width+gap;
                                panels[i].Dispose();
                            }
                        }
                        string dest=Path.Combine(outDir,spec.File);
                        canvas.Save(dest,ImageFormat.Png);
                        Console.WriteLine("Screenshot: "+dest);
                    }
                }
            }
        }finally{
            try{if(Directory.Exists(temp))Directory.Delete(temp,true);}catch{}
        }
    }

    static Bitmap RenderPopupBitmap(TrayContext owner,PopupNavigation nav,bool dark){
        const int scale=2;
        using(var form=new Details(owner)){
            var rows=PopupPages.Build(owner,nav,false);
            var logical=PopupPages.SizeFor(rows);
            var formNav=(PopupNavigation)Field(form,"navigation");
            formNav.Page=nav.Page;
            formNav.Offset=nav.Offset;
            formNav.VisibleChoices=nav.VisibleChoices;
            formNav.MaximumHeight=nav.MaximumHeight;
            formNav.SelectedDeviceKey=nav.SelectedDeviceKey;
            formNav.SelectedDeviceName=nav.SelectedDeviceName;
            formNav.Notice=nav.Notice;
            var bmp=new Bitmap(logical.Width*scale,logical.Height*scale,PixelFormat.Format32bppPArgb);
            using(var g=Graphics.FromImage(bmp)){
                g.Clear(Color.Transparent);
                g.ScaleTransform(scale,scale);
                PopupPages.Paint(g,owner,formNav,rows,dark,-1,-1,-1);
            }
            return bmp;
        }
    }

    static long Number(string json,string field){var m=Regex.Match(json,"\""+field+"\":([0-9]+)");return m.Success?Int64.Parse(m.Groups[1].Value):0;}
    static string Text(string json,string field){return Regex.Match(json,"\""+field+"\":\"([^\"]*)\"").Groups[1].Value;}

    static string SerializeKnownMemory(Dictionary<string,bool> known){
        var sb=new StringBuilder("{");bool first=true;
        foreach(var pair in known){
            if(!first)sb.Append(',');first=false;
            sb.Append(Q(pair.Key)).Append(":{\"working\":\"bat\",\"rounds\":1,\"retired\":[],\"disabled\":").Append(pair.Value?"true":"false").Append(",\"silentPolls\":0}");
        }
        sb.Append('}');return sb.ToString();
    }

    static void FakeWorker(string[] args){
        string root=Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).FullName;
        string mode=Path.GetFileName(root);
        File.AppendAllText(Path.Combine(root,"args.log"),String.Join(" ",args)+Environment.NewLine);
        var disabled=new HashSet<string>(StringComparer.Ordinal);
        var known=new Dictionary<string,bool>(StringComparer.Ordinal);
        using(var input=new StreamReader(Console.OpenStandardInput(),Encoding.UTF8))
        using(var output=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false)){AutoFlush=true}){
            string line;
            while((line=input.ReadLine())!=null){
                File.AppendAllText(Path.Combine(root,"requests.log"),line+Environment.NewLine);
                string cmd=Text(line,"command");
                long id=Number(line,"requestId");
                int gen=(int)Number(line,"generation");
                if(cmd=="exit")return;
                if(cmd=="family_memory"){
                    string op=Text(line,"op");
                    if(op=="set"){
                        if(mode.StartsWith("set-oversize")){
                            output.WriteLine("{\"reply\":\"family_memory\",\"requestId\":0,\"ok\":false,\"error\":\"payload_too_large\"}");
                            continue;
                        }
                        if(mode.StartsWith("set-fail")){
                            output.WriteLine("{\"reply\":\"family_memory\",\"requestId\":"+id+",\"generation\":"+gen+",\"ok\":false,\"error\":\"invalid_snapshot\"}");
                            continue;
                        }
                        string slice=FamilyMemoryJson.ExtractTopLevelField(line,"familyMemory");
                        if(slice!=null){
                            var parsed=FamilyMemoryJson.ParseMemoryMapJson(slice);
                            known.Clear();disabled.Clear();
                            foreach(var pair in parsed){
                                bool isDis=FamilyMemoryJson.EntryDisabled(pair.Value);
                                known[pair.Key]=isDis;if(isDis)disabled.Add(pair.Key);
                            }
                        }
                        output.WriteLine("{\"reply\":\"family_memory\",\"requestId\":"+id+",\"generation\":"+gen+",\"ok\":true,\"familyMemory\":"+SerializeKnownMemory(known)+"}");
                        continue;
                    }
                    if(op=="get"){
                        if(mode.StartsWith("restart-fail"))return;
                        bool probe=Array.IndexOf(args,"off")<0;
                        output.WriteLine("{\"reply\":\"family_memory\",\"requestId\":"+id+",\"generation\":"+gen+",\"ok\":true,\"probeFamilies\":"+(probe?"true":"false")+",\"familyMemory\":"+SerializeKnownMemory(known)+"}");
                        continue;
                    }
                    if(op=="disable"){
                        if(mode.StartsWith("unknown-disable")){
                            output.WriteLine("{\"reply\":\"family_memory\",\"requestId\":"+id+",\"generation\":"+gen+",\"ok\":false,\"error\":\"unknown_device\"}");
                            continue;
                        }
                        // Emit stale/mismatched frames first to verify strict requestId+generation filtering
                        output.WriteLine("{\"reply\":\"family_memory\",\"requestId\":"+(id+99)+",\"generation\":"+gen+",\"ok\":false,\"error\":\"timeout\"}");
                        output.WriteLine("{\"reply\":\"family_memory\",\"requestId\":"+id+",\"generation\":"+(gen+7)+",\"ok\":false,\"error\":\"timeout\"}");
                        string devId=Text(line,"deviceId");
                        string mk=devId==IdA?KeyA:KeyB;
                        disabled.Add(mk);known[mk]=true;
                        output.WriteLine("{\"reply\":\"family_memory\",\"requestId\":"+id+",\"generation\":"+gen+",\"ok\":true,\"memoryKey\":"+Q(mk)+",\"familyMemory\":"+SerializeKnownMemory(known)+"}");
                        continue;
                    }
                    if(op=="enable"){
                        if(mode.StartsWith("enable-fail")){
                            output.WriteLine("{\"reply\":\"family_memory\",\"requestId\":"+id+",\"generation\":"+gen+",\"ok\":false,\"error\":\"timeout\"}");
                            continue;
                        }
                        string mk=Text(line,"memoryKey");
                        disabled.Remove(mk);known[mk]=false;
                        output.WriteLine("{\"reply\":\"family_memory\",\"requestId\":"+id+",\"generation\":"+gen+",\"ok\":true,\"memoryKey\":"+Q(mk)+",\"familyMemory\":"+SerializeKnownMemory(known)+"}");
                        continue;
                    }
                }
                var devices=new List<string>();
                if(!disabled.Contains(KeyA))devices.Add(FamilyDeviceJson(IdA,PhysA,"ATK 烈空 X1",true,true,"family:atk_v2"));
                if(!mode.StartsWith("unknown-disable")&&!disabled.Contains(KeyB))devices.Add(FamilyDeviceJson(IdB,PhysB,"普通精确设备",false,false,"family:xiberia"));
                output.WriteLine("{\"schema\":2,\"requestId\":"+id+",\"generation\":"+gen+",\"complete\":true,\"nativeMousePresent\":false,\"providerCount\":26,\"probeFamilies\":true,\"familyMemory\":"+SerializeKnownMemory(known)+",\"devices\":["+String.Join(",",devices.ToArray())+"],\"health\":[],\"errors\":[]}");
            }
        }
    }

    [STAThread]
    static int Main(string[] args){
        if(args.Length>0&&args[0]=="-I"){FakeWorker(args);return 0;}
        var cached=typeof(DeviceSettings).GetField("current",BindingFlags.NonPublic|BindingFlags.Static);
        object previous=cached.GetValue(null);
        try{
            if(args.Length>=2&&args[0]=="--render-ui01"){
                RenderScreenshots(args[1]);
                return 0;
            }
            string root=Path.Combine(Path.GetFullPath(args.Length>0?args[0]:"tmp"),"family-ui-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try{
                SettingsPersistence(root);
                StoreEnvelopeAndCorruption(root);
                WorkerProtocolAndConcurrency(root);
                UiEndToEnd(root);
                Console.WriteLine("PASS "+checks+" UI-01 family memory, worker C1, and WinForms UI checks");
                return 0;
            }finally{
                try{if(Directory.Exists(root))Directory.Delete(root,true);}catch{}
            }
        }catch(Exception error){
            Console.Error.WriteLine(error);
            return 1;
        }finally{
            cached.SetValue(null,previous);
        }
    }
}
