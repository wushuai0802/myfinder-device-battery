using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

static class UpdateBadgeTests {
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static int checks,images;static string output;
    static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
    static object Field(object target,string name){return target.GetType().GetField(name,Private).GetValue(target);}
    static void Set(object target,string name,object value){target.GetType().GetField(name,Private).SetValue(target,value);}
    static object Call(object target,string name,params object[] args){return target.GetType().GetMethod(name,Private|BindingFlags.Public).Invoke(target,args);}
    static void Pump(Func<bool> done){var clock=Stopwatch.StartNew();while(!done()&&clock.ElapsedMilliseconds<6000){Application.DoEvents();Thread.Sleep(5);}Check(done(),"asynchronous update UI settles");}
    static UpdateRelease Release(string version){
        var assets=new List<UpdateAsset>();foreach(string name in new[]{UpdateTrust.ZipName(version),"SHA256SUMS.txt","SHA256SUMS.txt.sig"})assets.Add(new UpdateAsset {Name=name,Size=10,Url=UpdateTrust.Repository+"/releases/download/v"+version+"/"+name});
        return new UpdateRelease {Tag="v"+version,Body="- 离线测试更新",Assets=assets.ToArray()};
    }
    sealed class Http:IUpdateTransport {
        internal int Requests;internal UpdateRelease Release;
        public UpdateResponse Get(Uri uri,CancellationToken token){Requests++;token.ThrowIfCancellationRequested();var bytes=Encoding.UTF8.GetBytes(UpdateJson.Text(Release==null?new UpdateRelease[0]:new[]{Release}));return new UpdateResponse(200,new MemoryStream(bytes),bytes.Length,null,null);}
    }
    static void State(){
        var http=new Http {Release=Release("99.0.1")};string path=Path.Combine(output,"state.json");var now=DateTime.UtcNow;
        File.WriteAllText(path,"{\"NotifiedVersion\":\"98.0.1\",\"SkippedVersion\":\"98.0.2\"}");
        var checker=new UpdateChecker(http,path,"98.0.0",now.AddDays(-1));
        Check(!checker.BadgeVisible(true,true)&&checker.CachedRelease==null,"old state loads without a cached update or seen version");
        Check(checker.Check(true,true,now).Version=="99.0.1"&&checker.BadgeVisible(true,true),"new eligible release displays badge");
        Check(checker.MarkNotified("99.0.1")&&!checker.MarkNotified("99.0.1"),"one balloon per version");
        Check(!checker.BadgeVisible(false,true)&&!checker.BadgeVisible(true,false),"both settings gate badge independently");
        checker=new UpdateChecker(http,path,"98.0.0",now);Check(checker.BadgeVisible(true,true)&&!checker.MarkNotified("99.0.1"),"unseen badge and balloon dedup survive restart");
        checker.MarkSeen("99.0.1",false);Check(checker.BadgeVisible(true,true),"automatic presentation does not acknowledge");
        checker.MarkSeen("99.0.1",true);Check(!checker.BadgeVisible(true,true)&&checker.CachedRelease!=null,"manual viewing clears only tray badge");
        checker=new UpdateChecker(http,path,"98.0.0",now);Check(!checker.BadgeVisible(true,true)&&checker.CachedRelease!=null,"seen version and panel update survive restart");
        http.Release=Release("99.0.2");checker.Check(false,false,now.AddMinutes(1));Check(checker.BadgeVisible(true,true),"higher release shows badge again");
        checker.MarkSeen("98.0.9",true);Check(checker.BadgeVisible(true,true)&&UpdateJson.ReadFile<UpdateState>(path,65536).SeenVersion=="99.0.1","late older dialog cannot regress seen watermark");
        checker.Skip("99.0.2");Check(!checker.BadgeVisible(true,true)&&checker.CachedRelease==null,"skip clears badge and panel availability");
        Check(new UpdateChecker(http,path,"98.0.0",now).CachedRelease==null,"skip survives restart");
        Check(new UpdateChecker(http,path,"99.0.2",now).CachedRelease==null,"installed version cannot restore stale update");
        http.Release=Release("99.0.3");checker.Check(false,false,now);Check(!checker.BadgeVisible(true,false)&&checker.CachedRelease.Version=="99.0.3","manual check works with auto checks disabled");
        http.Release=null;checker.Check(false,false,now);Check(checker.CachedRelease==null&&!checker.BadgeVisible(true,true),"successful empty catalog clears cached offer");
        string settings=Path.Combine(output,"settings.json");File.WriteAllText(settings,"{\"SchemaVersion\":1,\"MouseAlias\":\"保留\"}");
        var pref=DeviceSettings.LoadBindings(settings);Check(pref.ShowUpdateBadge&&pref.Clone().ShowUpdateBadge&&pref.MouseAlias=="保留","old preferences default on and preserve existing fields");
        pref.ShowUpdateBadge=false;DeviceSettings.SaveBindings(settings,pref);Check(!DeviceSettings.LoadBindings(settings).ShowUpdateBadge&&!pref.Clone().ShowUpdateBadge,"disabled badge setting saves and clones");
    }
    static void Tooltips(){
        string battery="鼠标 80% · 充电中";Check(UpdateBadge.Tooltip(battery,"99.0.1")==battery+" · 有新版本 99.0.1","short tooltip suffix");
        for(int length=0;length<=63;length++){
            battery=new string('电',length);string value=UpdateBadge.Tooltip(battery,"99.0.1-beta.123456789");
            Check(value.Length<=63&&value.StartsWith(battery,StringComparison.Ordinal),"tooltip keeps all battery characters at length "+length);
            Check(value.Length==0||!Char.IsHighSurrogate(value[value.Length-1]),"tooltip does not leave a split surrogate");
        }
        Check(UpdateBadge.Tooltip(battery,null)==battery,"no update leaves tooltip unchanged");
    }
    sealed class Fixture:IDisposable {
        internal readonly TrayContext Owner;internal readonly Details Popup;internal readonly UpdateController Updates;internal readonly Http Transport;
        internal readonly string DirectoryPath;internal int Balloons,Saves;
        internal Fixture(){
            DirectoryPath=Path.Combine(output,"ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(DirectoryPath);
            var pref=new AudioBindings {ReducedMotion=true,RememberReadings=false};TrayConfiguration.Add(pref);DeviceSettings.UseTransientConfiguration(pref);
            Owner=new TrayContext(false,delegate(bool battery){return new MouseSnapshot {Battery=new BatteryReading {Percent=80,TimestampUtc=DateTime.UtcNow}};},delegate(int stage){throw new Exception("No hardware writes");},null,null,false,null,null,null,delegate{return false;},null,null,null,null,delegate(AudioBindings value){Saves++;DeviceSettings.UseTransientConfiguration(value);});
            Pump(delegate{return !Owner.IsRefreshing;});((System.Windows.Forms.Timer)Field(Owner,"refresh")).Stop();
            Transport=new Http {Release=Release("99.0.1")};
            Updates=new UpdateController((Control)Field(Owner,"dispatch"),delegate{Call(Owner,"UpdateIcon");},delegate(string title,string text,bool update){Balloons++;},Transport,DirectoryPath,Path.Combine(DirectoryPath,"portable.exe"),false);
            Set(Updates,"started",DateTime.UtcNow.AddDays(-1));Set(Owner,"updates",Updates);
            Popup=new Details(Owner);Set(Owner,"details",Popup);
            Updates.Check(true);Pump(delegate{return Updates.Available&&(int)Field(Updates,"checking")==0;});
            Check(Balloons==1&&Updates.BadgeVisible,"automatic discovery shows one balloon and unseen badge");
        }
        internal List<PopupControl> Rows(PopupPage page){return PopupPages.Build(Owner,new PopupNavigation {Page=page,VisibleChoices=100},false);}
        internal UpdateDialog Dialog {get{return (UpdateDialog)Field(Updates,"dialog");}}
        internal void Seen(){Pump(delegate{return Dialog!=null&&Dialog.Visible&&!Updates.BadgeVisible&&UpdateJson.ReadFile<UpdateState>(Path.Combine(DirectoryPath,"update-state.json"),65536).SeenVersion=="99.0.1";});Check(Updates.Available&&Rows(PopupPage.More).Exists(r=>r.Id==260),"seen offer remains in panel");Check(UpdateJson.ReadFile<UpdateState>(Path.Combine(DirectoryPath,"update-state.json"),65536).SeenVersion=="99.0.1","exact dialog version persisted");}
        public void Dispose(){Owner.ExitThread();Owner.Dispose();if(!Popup.IsDisposed)Popup.Dispose();}
    }
    static void Ui(){
        foreach(string source in new[]{"panel","menu","balloon"})using(var f=new Fixture()){
            Check(f.Rows(PopupPage.More).Find(r=>r.Id==260).Icon=="update","panel offer has a dot");
            var icons=(TrayIconSet)Field(f.Owner,"trayIcons");
            Check(((string)Field(icons.First,"lastKey")).EndsWith("|True|"+f.Owner.UpdateDotColor.ToArgb()+"|"+Style.TaskbarDark),"first icon owns badge");
            foreach(var group in DeviceSettings.Current.TrayGroups)Check(((string)Field(icons.For(group.Id),"lastKey")).EndsWith("|False|0|"+Style.TaskbarDark),"extra icon has no badge");
            if(source=="panel")Call(f.Popup,"PerformAction",f.Rows(PopupPage.More).Find(r=>r.Id==260));
            if(source=="menu"){
                var menu=((NotifyIcon)Field(f.Owner,"tray")).ContextMenuStrip;Call(menu,"OnOpening",new System.ComponentModel.CancelEventArgs());
                ToolStripMenuItem item=null;foreach(ToolStripItem candidate in menu.Items)if(candidate.Text=="查看新版本 99.0.1…")item=(ToolStripMenuItem)candidate;
                Check(item!=null,"context menu names available version");item.PerformClick();
            }
            if(source=="balloon"){((TrayBalloonRoute)Field(f.Owner,"balloonRoute")).Set(TrayBalloonKind.Update);Call(Field(f.Owner,"tray"),"OnBalloonTipClicked");}
            f.Seen();
        }
        using(var f=new Fixture()){
            f.Owner.OpenUpdatedPanel(new TrayPanelAnchor(new Point(900,900),new Rectangle(0,0,1280,960),TaskbarEdge.Bottom));
            Application.DoEvents();Check(f.Updates.BadgeVisible,"post-install automatic panel never marks offer seen");
            Call(f.Updates,"ShowAvailable",false);Pump(delegate{return f.Dialog!=null&&f.Dialog.Visible;});Check(f.Updates.BadgeVisible,"automatic update dialog does not mark seen");
            f.Updates.Open();f.Seen();
            f.Updates.Check(false);Pump(delegate{return f.Transport.Requests==2&&(int)Field(f.Updates,"checking")==0;});Application.DoEvents();
            var skip=(ThemedButton)f.Dialog.Controls.Find("SkipUpdate",true)[0];skip.PerformClick();Pump(delegate{return !f.Updates.Available;});
            Check(!f.Rows(PopupPage.More).Exists(r=>r.Id==260),"skip removes panel entry");
        }
        using(var f=new Fixture()){
            var rows=f.Rows(PopupPage.Preferences);int auto=rows.FindIndex(r=>r.Id==219);Check(rows[auto+1].Id==225&&rows[auto+1].Checked,"badge switch immediately follows auto checks");
            Call(f.Popup,"PerformAction",rows[auto+1]);Check(f.Saves==1&&!f.Updates.BadgeVisible&&f.Updates.Available,"badge switch saves and clears only dot");
            Call(f.Popup,"PerformAction",rows[auto+1]);Check(f.Updates.BadgeVisible,"reenabling unseen badge restores it");
            Call(f.Popup,"PerformAction",rows[auto]);Check(!f.Updates.BadgeVisible&&f.Updates.Available,"automatic-check switch suppresses dot");
            f.Updates.Check(false);f.Seen();Check(f.Transport.Requests==2,"manual check still fetches while automatic checks are disabled");
        }
    }
    static Rectangle PixelBounds(Bitmap bitmap,Color? color){
        int left=bitmap.Width,top=bitmap.Height,right=-1,bottom=-1;
        for(int y=0;y<bitmap.Height;y++)for(int x=0;x<bitmap.Width;x++){
            var pixel=bitmap.GetPixel(x,y);if(pixel.A==0||color.HasValue&&pixel.ToArgb()!=color.Value.ToArgb())continue;
            left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x);bottom=Math.Max(bottom,y);
        }
        return right<left?Rectangle.Empty:Rectangle.FromLTRB(left,top,right+1,bottom+1);
    }
    static void BadgeSizes(){
        foreach(bool dark in new[]{false,true}){
            int previous=0;
            for(int size=16;size<=64;size++)using(var bitmap=new Bitmap(size,size,PixelFormat.Format32bppPArgb)){
                UpdateBadge.Paint(bitmap,UpdateBadge.Blue,dark);
                var actual=PixelBounds(bitmap,null);int expected=Math.Max(4,(size+2)/5);
                Check(actual==new Rectangle(size-expected,0,expected,expected),"painted badge uses rounded 20% diameter at top right: "+size+"px dark="+dark);
                Check(actual.Width>=previous,"painted badge diameter is monotonically nondecreasing: "+size+"px dark="+dark);
                Check(PixelBounds(bitmap,UpdateBadge.Blue)==new Rectangle(actual.X+1,1,expected-2,expected-2),"painted badge keeps a one-pixel outline: "+size+"px dark="+dark);
                Check(bitmap.GetPixel(actual.X+expected/2,0).ToArgb()==UpdateBadge.Outline(dark).ToArgb(),"painted badge uses taskbar outline color: "+size+"px dark="+dark);
                previous=actual.Width;
            }
        }
    }
    static void Drawings(){
        BadgeSizes();int[] sizes={16,20,24,28,32,40,48};
        string raw=Path.Combine(output,"icons");Directory.CreateDirectory(raw);
        foreach(string theme in ThemePalette.Keys)foreach(bool dark in new[]{false,true}){
            var palette=ThemePalette.For(theme,dark);
            using(var sheet=new Bitmap(720,60+5*sizes.Length*50,PixelFormat.Format32bppPArgb))using(var g=Graphics.FromImage(sheet)){
                Color backdrop=UpdateBadge.Outline(dark);g.Clear(backdrop);
                CompactRenderer.DrawLabel(g,theme+" / "+(dark?"dark":"light")+"   80% off/on   |   10% off/on",new RectangleF(12,4,696,30),16,true,palette.Foreground,StringAlignment.Near);
                int row=0;
                foreach(string kind in new[]{"number","mouse","keyboard","headset","device"})foreach(int size in sizes){
                    int y=40+row++*50;CompactRenderer.DrawLabel(g,kind+" "+size+"px",new RectangleF(8,y,120,50),12,false,palette.Foreground,StringAlignment.Near);
                    int column=0;foreach(int percent in new[]{80,10})foreach(bool badge in new[]{false,true}){
                        Color background=kind=="number"?TrayGlyph.NumberFill(percent,false):TrayGlyph.LevelColor(percent,dark);
                        Color color=UpdateBadge.ColorFor(palette.Accent,background,dark);
                        Check(UpdateBadge.Contrast(color,UpdateBadge.Outline(dark))>=3,"badge outline contrast >= 3:1: "+theme+" "+dark);
                        if(percent==10)Check(color.ToArgb()!=background.ToArgb()&&Math.Abs(color.GetHue()-background.GetHue())>30,"badge visibly separate from low red");
                        using(var bitmap=kind=="number"?TrayGlyph.Render(percent,false,dark,false,size,false,badge?(Color?)color:null):TrayGlyph.RenderDevice(percent,false,dark,false,size,kind,badge?(Color?)color:null)){
                            string name=theme+"-"+(dark?"dark":"light")+"-"+kind+"-"+size+"-"+percent+"-"+(badge?"on":"off")+".png";
                            bitmap.Save(Path.Combine(raw,name),ImageFormat.Png);images++;
                            int x=130+column++*145;g.DrawImageUnscaled(bitmap,x,y+(50-size)/2);
                            var state=g.Save();g.InterpolationMode=InterpolationMode.NearestNeighbor;g.PixelOffsetMode=PixelOffsetMode.Half;g.DrawImage(bitmap,new Rectangle(x+60,y+2,44,44));g.Restore(state);
                            if(badge){var bounds=UpdateBadge.Bounds(size);Check(bitmap.GetPixel(bounds.X+bounds.Width/2,bounds.Height/2).ToArgb()==color.ToArgb(),"native badge center uses exact solid color");}
                        }
                    }
                }
                sheet.Save(Path.Combine(output,theme+"-"+(dark?"dark":"light")+".png"),ImageFormat.Png);
            }
        }
        foreach(int size in sizes)foreach(bool approximate in new[]{false,true})for(int percent=0;percent<=100;percent++){
            using(var before=TrayGlyph.Render(percent,false,true,false,size,approximate))using(var after=TrayGlyph.Render(percent,false,true,false,size,approximate,UpdateBadge.Blue)){
                for(int y=0;y<size;y++)for(int x=0;x<size;x++){
                    var pixel=before.GetPixel(x,y);if(pixel.A>200&&pixel.R<30&&pixel.G<30&&pixel.B<30){
                        var shifted=after.GetPixel(x,Math.Min(size-1,y+1));
                        // Edge blending can change by a few levels against the curved fill;
                        // the dark digit coverage must survive, with no badge/outline pixels.
                        Check(y+1<size&&shifted.A>200&&shifted.R<50&&shifted.G<50&&shifted.B<50,"dot must preserve digit core after its one-pixel offset: "+size+"px "+percent+" approximate="+approximate+" at "+x+","+y);
                    }
                }
            }
        }
        foreach(bool dark in new[]{false,true})foreach(Color accent in new[]{Color.Red,Color.OrangeRed,TrayGlyph.LevelColor(80,true),TrayGlyph.LevelColor(10,true)})Check(UpdateBadge.ColorFor(accent,TrayGlyph.LevelColor(80,true),dark)==UpdateBadge.Blue,"red and same-background accents use fixed blue");
        foreach(string theme in ThemePalette.Keys)foreach(bool dark in new[]{false,true})foreach(bool taskbarDark in new[]{false,true}){
            var color=UpdateBadge.ColorFor(ThemePalette.For(theme,dark).Accent,TrayGlyph.NumberFill(10,false),taskbarDark);
            Check(UpdateBadge.Contrast(color,UpdateBadge.Outline(taskbarDark))>=3,"forced panel theme still contrasts with actual taskbar outline");
        }
        Console.WriteLine("Exported "+images+" native icons and 8 contact sheets: "+output);
    }
    [STAThread] static int Main(string[] args){
        bool render=args[0]=="--render";output=Path.Combine(Path.GetFullPath(args[render?1:0]),"update-badge");Directory.CreateDirectory(output);
        var previous=DeviceSettings.Current;VisualDesign.HighContrastOverride=false;
        try{if(!render){State();Tooltips();Ui();}Drawings();Console.WriteLine("PASS "+checks+" update badge checks; fake HTTP/hardware and isolated state only");return 0;}
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{DeviceSettings.UseTransientConfiguration(previous);VisualDesign.HighContrastOverride=null;}
    }
}
