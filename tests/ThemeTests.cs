using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

// All readers and writers are injected. Build/run under TEMP, never real settings.
static class ThemeTests {
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    const BindingFlags Static=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static readonly string[] Styles={"ocean","native","amber","mist"};
    static readonly string[] Names={"海蓝","原生","琥珀","青雾"};
    const string HistoryNotice="关闭保留不删除历史，清除需单独操作";
    static readonly string[] Roles={"Card","Border","Foreground","Muted","Track","Accent","Selected","SelectedForeground"};
    static readonly string[][] Colors={
        new[]{"2C3038","4B535E","F4F6F8","A7B1C1","424852","4D9BFF","1F6FEB","FFFFFF"},
        new[]{"FFFFFF","DCE2EB","1B2430","5F6B7C","E7EBF1","0865D6","0865D6","FFFFFF"},
        new[]{"2B2B2B","3E3E3E","FFFFFF","ABABAB","454545","60CDFF","0067C0","FFFFFF"},
        new[]{"FFFFFF","E5E5E5","1A1A1A","666666","E6E6E6","005FB8","005FB8","FFFFFF"},
        new[]{"231F1C","38322D","F3EFEA","A89E95","3D3631","E89150","E89150","1A1208"},
        new[]{"FFFDFA","E6DFD5","2A241F","6F665C","EEE8DF","A3521E","A3521E","FFFFFF"},
        new[]{"242B33","323C47","DDE3EA","98A4B3","36414C","5CC2C4","5CC2C4","0E171B"},
        new[]{"FAFCFD","D9E2EC","1E293B","5A6A80","E4EBF1","1F7A7C","1F7A7C","FFFFFF"}
    };
    static int checks,failures;
    static string output;
    static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
    static void Run(string name,Action action){try{action();Console.WriteLine("PASS "+name);}catch(Exception e){failures++;Console.Error.WriteLine("FAIL "+name+": "+(e.InnerException??e).Message);}}
    static object Field(object target,string name){return target.GetType().GetField(name,Private).GetValue(target);}
    static void Set(object target,string name,object value){target.GetType().GetField(name,Private).SetValue(target,value);}
    static object Call(object target,string name,params object[] args){return target.GetType().GetMethod(name,Private|BindingFlags.Public).Invoke(target,args);}
    static Type PaletteType(){var type=typeof(CompactRenderer).Assembly.GetType("AtkBatteryTray.ThemePalette");Check(type!=null,"central ThemePalette exists");return type;}
    static object Palette(string style,bool dark){return PaletteType().GetMethod("For",Static).Invoke(null,new object[]{style,dark});}
    static Color Role(object palette,string role){return (Color)palette.GetType().GetField(role,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).GetValue(palette);}
    static Color Hex(string value){return Color.FromArgb(Convert.ToInt32(value,16)|unchecked((int)0xff000000));}
    static void Style(AudioBindings pref,string style){var field=typeof(AudioBindings).GetField("ThemeStyle");Check(field!=null,"ThemeStyle is persisted");field.SetValue(pref,style);}
    static string Style(AudioBindings pref){var field=typeof(AudioBindings).GetField("ThemeStyle");Check(field!=null,"ThemeStyle is persisted");return (string)field.GetValue(pref);}
    static double Luminance(Color color){double[] v={color.R/255.0,color.G/255.0,color.B/255.0};for(int i=0;i<3;i++)v[i]=v[i]<=.04045?v[i]/12.92:Math.Pow((v[i]+.055)/1.055,2.4);return .2126*v[0]+.7152*v[1]+.0722*v[2];}
    static double Contrast(Color a,Color b){double x=Luminance(a),y=Luminance(b);return (Math.Max(x,y)+.05)/(Math.Min(x,y)+.05);}
    static Color Level(int? percent,bool dark){return (Color)PaletteType().GetMethod("LevelColor",Static).Invoke(null,new object[]{percent,dark});}
    static void Configuration(){
        string path=Path.Combine(output,"theme-settings.json");
        Check(Style(new AudioBindings())=="ocean","new configuration defaults to ocean");
        File.WriteAllText(path,"{\"SchemaVersion\":1,\"Theme\":\"dark\",\"MouseAlias\":\"保留名称\",\"WarningThreshold\":15}");
        var old=DeviceSettings.LoadBindings(path);Check(Style(old)=="ocean"&&old.Theme=="dark"&&old.MouseAlias=="保留名称"&&old.WarningThreshold==15,"old settings retain every other preference");
        foreach(string style in new[]{"ocean","native","amber","mist","unknown",null}){
            var pref=old.Clone();Style(pref,style);DeviceSettings.SaveBindings(path,pref);var loaded=DeviceSettings.LoadBindings(path);
            Check(Style(loaded)==(Array.IndexOf(Styles,style)>=0?style:"ocean")&&loaded.Theme=="dark"&&loaded.MouseAlias==old.MouseAlias&&loaded.WarningThreshold==15,"round trip and unknown fallback preserve settings: "+style);
            Check(Style(loaded.Clone())==Style(loaded),"clone retains style");
        }
        File.WriteAllText(path,"{\"SchemaVersion\":1,\"ThemeStyle\":\"future\",\"Theme\":\"light\",\"MouseAlias\":\"未知配色不重置\"}");
        var unknown=DeviceSettings.LoadBindings(path);Check(Style(unknown)=="ocean"&&unknown.Theme=="light"&&unknown.MouseAlias=="未知配色不重置","unknown style in input JSON does not reset configuration");
        var attribute=(DataMemberAttribute)Attribute.GetCustomAttribute(typeof(AudioBindings).GetField("ThemeStyle"),typeof(DataMemberAttribute));Check(attribute!=null&&attribute.Order==21,"ThemeStyle uses next DataMember order");
    }
    static void Palettes(){
        VisualDesign.HighContrastOverride=false;
        for(int i=0;i<Styles.Length;i++)foreach(bool dark in new[]{true,false}){
            var palette=Palette(Styles[i],dark);var colors=Colors[i*2+(dark?0:1)];
            for(int j=0;j<Roles.Length;j++)Check(Role(palette,Roles[j]).ToArgb()==Hex(colors[j]).ToArgb(),Styles[i]+" "+dark+" exact "+Roles[j]);
            var card=Role(palette,"Card");Check(Contrast(Role(palette,"Foreground"),card)>=4.5,"primary contrast");Check(Contrast(Role(palette,"Muted"),card)>=4.5,"secondary contrast");
            Check(palette.GetType().GetField("Background",BindingFlags.Instance|BindingFlags.NonPublic)==null,"unused dialog background role is removed");
            Check(Contrast(Role(palette,"Accent"),card)>=3,"accent contrast");Check(Contrast(Role(palette,"SelectedForeground"),Role(palette,"Selected"))>=4.5,"selected contrast");
            foreach(int value in new[]{10,30,80})Check(Contrast(Level(value,dark),card)>=3,"semantic contrast "+Styles[i]+" "+dark+" "+value);
            var pref=new AudioBindings();Style(pref,Styles[i]);DeviceSettings.UseTransientConfiguration(pref);
            Check(PopupPages.Foreground(dark)==Role(palette,"Foreground")&&PopupPages.Muted(dark)==Role(palette,"Muted")&&PopupPages.Blue(dark)==Role(palette,"Accent")&&PopupPages.Track(dark)==Role(palette,"Track"),"popup colors use active palette");
        }
        foreach(bool dark in new[]{true,false})Check(Role(Palette("future",dark),"Card")==Role(Palette("ocean",dark),"Card"),"unknown palette falls back to ocean");
        foreach(int value in new[]{10,30,80})Check(Level(value,true)==TrayGlyph.LevelColor(value,true),"deep semantic color unchanged");
        Check(Level(80,false).ToArgb()==Hex("1A8A50").ToArgb()&&Level(30,false).ToArgb()==Hex("9E6400").ToArgb()&&Level(10,false).ToArgb()==Hex("C92A22").ToArgb(),"light semantic colors are exact");
    }
    static AudioReading Audio(){return new AudioReading {TimestampUtc=DateTime.UtcNow,SpeakerAvailable=true,SpeakerId="speaker",SpeakerName="桌面扬声器",HeadsetAvailable=true,HeadsetId="headset",HeadsetName="有线耳机",MicrophoneAvailable=true,MicrophoneId="mic",MicrophoneMuted=false,ActiveOutput="speaker",ConsoleOutputId="speaker",MultimediaOutputId="speaker",VolumeEndpointId="speaker",OutputVolumePercent=55,Devices=new[]{new AudioDeviceChoice {Id="headset",Name="有线耳机",Flow=0,FormFactor=3}}};}
    static DpiConfig Dpi(){var dpi=new DpiConfig {DeviceKey=MouseBattery.DeviceKey("theme-fixture"),TimestampUtc=DateTime.UtcNow,ActiveStage=1};foreach(int v in new[]{400,800,1600,3200})dpi.Stages.Add(new DpiStage {XDpi=v,YDpi=v});return dpi;}
    static WirelessDevice Device(string id,string name,int? percent,bool fresh=true,bool approximate=false){return new WirelessDevice {Id=id,Name=name,Kind="mouse",Transport="2.4 GHz",Percent=percent,Fresh=fresh,Approximate=approximate,Online=true,ConnectionState="connected",MeasurementState=fresh?"current":"cached",ConfirmedAtUtc=(fresh?DateTime.UtcNow:DateTime.UtcNow.AddHours(-2)).ToString("o")};}
    sealed class Fixture:IDisposable {
        internal readonly TrayContext Owner;internal readonly Details Popup;internal readonly PopupNavigation Nav;internal int Saves;
        internal Fixture(string style="ocean"){
            var pref=new AudioBindings {ReducedMotion=true,RememberReadings=false,VolumeCollapsed=false};if(typeof(AudioBindings).GetField("ThemeStyle")!=null)Style(pref,style);DeviceSettings.UseTransientConfiguration(pref);
            Owner=new TrayContext(false,delegate(bool battery){return new MouseSnapshot {Battery=new BatteryReading {Percent=76,TimestampUtc=DateTime.UtcNow},Dpi=Dpi()};},delegate(int stage){throw new Exception("No device writes");},null,Audio,false,null,null,null,delegate{return false;},null,null,null,null,
                delegate(AudioBindings settings){Saves++;DeviceSettings.SaveBindings(Path.Combine(output,"theme-choice.json"),settings);DeviceSettings.UseTransientConfiguration(settings);});
            DateTime until=DateTime.UtcNow.AddSeconds(5);while((Owner.IsRefreshing||Owner.IsAudioBusy||Owner.Audio==null)&&DateTime.UtcNow<until){Application.DoEvents();Thread.Sleep(2);}Check(!Owner.IsRefreshing&&!Owner.IsAudioBusy&&Owner.Audio!=null,"fake readers settle");
            ((System.Windows.Forms.Timer)Field(Owner,"refresh")).Stop();Owner.Current=new BatteryReading {Percent=76,TimestampUtc=DateTime.UtcNow};Owner.Dpi=Dpi();Owner.Audio=Audio();Owner.NativeMousePresent=true;
            Popup=new Details(Owner);Set(Owner,"details",Popup);Nav=(PopupNavigation)Field(Popup,"navigation");
        }
        internal List<PopupControl> Rows(){return (List<PopupControl>)Call(Popup,"ControlsForPage");}
        internal void Activate(PopupControl row){Call(Popup,"PerformAction",row);}
        public void Dispose(){Owner.ExitThread();if(!Popup.IsDisposed)Popup.Dispose();Owner.Dispose();}
    }
    static void Choices(){
        using(var f=new Fixture()){
            f.Nav.Page=PopupPage.Preferences;var rows=PopupPages.Build(f.Owner,new PopupNavigation {Page=PopupPage.Preferences,VisibleChoices=100},false);
            var choice=rows.Find(delegate(PopupControl row){return row.Text=="配色";});Check(choice!=null&&choice.Value=="海蓝","preferences include current theme style after appearance");
            Check(rows.IndexOf(choice)==rows.FindIndex(delegate(PopupControl row){return row.Id==210;})+1,"style immediately follows appearance");f.Activate(choice);Check(f.Nav.Page==PopupPage.ChoosePreference&&PopupPages.Title(f.Nav)=="配色","style chooser title");
            for(int i=0;i<Styles.Length;i++){
                rows=f.Rows();var row=rows.Find(delegate(PopupControl c){return c.Text==Names[i];});Check(row!=null,"all four choices reachable");int oldSaves=f.Saves;f.Activate(row);
                Check(f.Saves==oldSaves+1&&Style(DeviceSettings.LoadBindings(Path.Combine(output,"theme-choice.json")))==Styles[i],"choice is saved through the normal configuration writer");
                Check(f.Nav.Page==PopupPage.ChoosePreference&&f.Nav.Offset==0,"choice stays on current page");rows=f.Rows();Check(rows.FindAll(delegate(PopupControl c){return c.Selected;}).Count==1&&rows.Find(delegate(PopupControl c){return c.Text==Names[i];}).Selected,"choice is immediately selected");
            }
            f.Activate(f.Rows().Find(delegate(PopupControl c){return c.Id==6;}));Check(f.Nav.Page==PopupPage.Preferences,"back still returns to preferences");
        }
    }
    static void Groups(){
        using(var f=new Fixture()){
            var rows=PopupPages.Build(f.Owner,new PopupNavigation {Page=PopupPage.Preferences,VisibleChoices=100},false);
            string actual=String.Join("|",rows.FindAll(delegate(PopupControl row){return row.Id!=6;}).ConvertAll(delegate(PopupControl row){return row.Text;}).ToArray());
            Check(actual=="显示|外观|配色|字号|托盘图标|减弱动效|提醒|低电量提醒|提醒阈值|数据|后台同步间隔|跨重启保留读数|清除历史读数|更新|自动检查更新|托盘图标显示更新角标|提醒测试版更新|自动检查反馈回复","preference sections and order");
            foreach(string name in new[]{"显示","提醒","数据","更新"}){
                var row=rows.Find(delegate(PopupControl c){return c.Text==name;});Check(row.Information&&row.Id<0&&row.Rect.Height>=23&&row.Rect.Height<=25,"section is 24px information");Check(PopupPages.Hit(rows,new PointF(row.Rect.X+10,row.Rect.Y+10))==null&&PopupPages.FindStable(rows,row.StableKey)==null,"section cannot be hit or focused");
            }
            foreach(int count in new[]{2,3,7}){
                var seen=new HashSet<string>();var nav=new PopupNavigation {Page=PopupPage.Preferences,VisibleChoices=count};
                for(int page=0;page<20;page++){
                    rows=PopupPages.Build(f.Owner,nav,false);var size=PopupPages.SizeFor(rows);float bottom=0;foreach(var row in rows){Check(row.Rect.Bottom<=size.Height&&row.Rect.Top>=bottom,"paged rows fit without overlap");bottom=row.Rect.Bottom;if(row.Id!=6&&row.Id!=7&&row.Id!=8)seen.Add(row.Text);}
                    var next=rows.Find(delegate(PopupControl c){return c.Id==8;});if(next==null)break;nav.Offset+=count;
                }
                Check(seen.Count==18,"every grouped preference remains reachable at "+count);
            }
        }
    }
    static string PageTexts(List<PopupControl> rows){return String.Join("|",rows.FindAll(delegate(PopupControl row){return row.Id!=6&&row.Id!=7&&row.Id!=8;}).ConvertAll(delegate(PopupControl row){return row.Text;}).ToArray());}
    static int PageOffset(List<PopupControl> rows,int id,int fallback){
        var row=rows.Find(delegate(PopupControl c){return c.Id==id;});Check(row!=null,"page navigation exists: "+id);
        var field=typeof(PopupControl).GetField("PageOffset");return field==null?fallback:(int)field.GetValue(row);
    }
    static Color Pixel(Bitmap image,float x,float y,int scale){return image.GetPixel((int)(x*scale),(int)(y*scale));}
    static int Pixels(Bitmap image,RectangleF area,int scale,Func<Color,bool> match){
        int count=0;for(int y=(int)(area.Top*scale);y<(int)(area.Bottom*scale);y++)for(int x=(int)(area.Left*scale);x<(int)(area.Right*scale);x++)if(match(image.GetPixel(x,y)))count++;return count;
    }
    static void PreferencePages(){
        const string first="显示|外观|配色|字号|托盘图标|减弱动效|提醒|低电量提醒|提醒阈值",second="数据|后台同步间隔|跨重启保留读数|清除历史读数|更新|自动检查更新|托盘图标显示更新角标|提醒测试版更新|自动检查反馈回复";
        using(var f=new Fixture()){
            var nav=new PopupNavigation {Page=PopupPage.Preferences,VisibleChoices=7};var rows=PopupPages.Build(f.Owner,nav,false);
            Check(PageTexts(rows)==first,"first preferences page keeps all seven display/reminder settings together");
            Check(rows.FindAll(delegate(PopupControl row){return row.Id>=200;}).Count==7&&!rows.Exists(delegate(PopupControl row){return row.Detail==HistoryNotice;}),"first page has seven settings and no history notice");
            using(var image=RenderPanel(PopupPages.SizeFor(rows),2,delegate(Graphics g){PopupPages.Paint(g,f.Owner,nav,rows,false,-1,-1,-1);})){
                var footer=new RectangleF(13,image.Height/2-25,234,20);Check(Pixels(image,footer,2,delegate(Color c){return c.ToArgb()!=ThemePalette.Current(false).Card.ToArgb();})==0,"first page footer does not repeat the data notice");
            }
            int firstHeight=PopupPages.SizeFor(rows).Height;nav.Offset=PageOffset(rows,8,7);rows=PopupPages.Build(f.Owner,nav,false);
            Check(PageTexts(rows)==second&&rows.FindAll(delegate(PopupControl row){return row.Id>=200;}).Count==7,"second page has all seven data/update/feedback settings");
            int groupHeight=Math.Max(firstHeight,PopupPages.SizeFor(rows).Height);
            var clear=rows.Find(delegate(PopupControl row){return row.Id==217;});var update=rows.Find(delegate(PopupControl row){return row.Section&&row.Text=="更新";});
            Check(clear.Detail==HistoryNotice&&clear.Rect.Bottom+2==update.Rect.Top,"the only history notice is directly below the data action");
            nav.Offset=PageOffset(rows,7,0);Check(PageTexts(PopupPages.Build(f.Owner,nav,false))==first,"previous page returns to the entire first group pair");
            foreach(int limit in new[]{groupHeight,groupHeight-1,340,260,230,210}){
                nav=new PopupNavigation {Page=PopupPage.Preferences,VisibleChoices=Math.Max(2,Math.Min(7,(limit+24-88)/36-2))};Set(nav,"MaximumHeight",limit);
                var seen=new HashSet<int>();var starts=new HashSet<int>();int pages=0;
                while(true){
                    rows=PopupPages.Build(f.Owner,nav,false);Check(starts.Add(nav.Offset),"bounded pagination advances without looping");pages++;
                    var size=PopupPages.SizeFor(rows);Check(size.Width==260&&size.Height<=limit,"preferences fit the available logical work area: "+limit);
                    float bottom=0;foreach(var row in rows){Check(row.Rect.Top>=bottom&&row.Rect.Bottom<=size.Height-28,"preference rows and footer never overlap");bottom=row.Rect.Bottom;if(row.Id>=200)seen.Add(row.Id);}
                    foreach(float scale in new[]{1f,1.25f,1.5f,2f})Check(UiState.ScaleSize(size,scale).Height<=Math.Ceiling(limit*scale),"preferences also fit after DPI scaling");
                    var next=rows.Find(delegate(PopupControl row){return row.Id==8;});if(next==null)break;nav.Offset=PageOffset(rows,8,nav.Offset+nav.VisibleChoices);Check(pages<20,"bounded preferences finish");
                }
                Check(seen.Count==14,"all fourteen settings remain reachable with height "+limit);Check(limit>=groupHeight?pages==2:pages>2,"group paging is used only when both full pages fit");
                while(nav.Offset>0){rows=PopupPages.Build(f.Owner,nav,false);int before=nav.Offset;nav.Offset=PageOffset(rows,7,Math.Max(0,before-nav.VisibleChoices));Check(nav.Offset<before,"bounded previous page always moves backwards");}
            }
            f.Nav.Page=PopupPage.Preferences;rows=f.Rows();if(PageTexts(rows)==first){f.Activate(rows.Find(delegate(PopupControl row){return row.Id==8;}));Check(PageTexts(f.Rows())==second,"real popup action follows the group page boundary");f.Activate(f.Rows().Find(delegate(PopupControl row){return row.Id==7;}));Check(PageTexts(f.Rows())==first,"real popup previous action restores first groups");}
        }
    }
    static void ToggleKnobs(){
        const int scale=4;VisualDesign.HighContrastOverride=false;
        foreach(string style in Styles)foreach(bool dark in new[]{true,false})foreach(bool on in new[]{false,true})using(var f=new Fixture(style)){
            var nav=new PopupNavigation {Page=PopupPage.Preferences,VisibleChoices=100};var rows=PopupPages.Build(f.Owner,nav,false);var row=rows.Find(delegate(PopupControl c){return c.Id==212;});row.Checked=on;
            var p=ThemePalette.Current(dark);float left=row.Rect.Right-37,top=row.Rect.Y+(row.Rect.Height-18)/2,cx=left+(on?21:9),cy=top+9;
            Color expected=on?(dark&&(style=="amber"||style=="mist")?p.SelectedForeground:Color.White):dark?p.Muted:Color.White;
            using(var image=RenderPanel(PopupPages.SizeFor(rows),scale,delegate(Graphics g){PopupPages.Paint(g,f.Owner,nav,rows,dark,-1,-1,-1);})){
                Color center=Pixel(image,cx,cy,scale),track=Pixel(image,on?left+5:left+25,cy,scale);
                Check(center.ToArgb()==expected.ToArgb(),"toggle knob color: "+style+" "+dark+" "+on);
                Color boundary=!on&&!dark?p.Muted:center;Check(Contrast(boundary,track)>=3,"toggle boundary contrast: "+style+" "+dark+" "+on);
                if(!on&&!dark){var strip=new RectangleF(cx-7.5f,cy-2,1.5f,4);Check(Pixels(image,strip,scale,delegate(Color c){return c.ToArgb()==p.Muted.ToArgb();})>0,"light off toggle has a 1px secondary-text outline");}
            }
        }
    }
    static void ThemeSwatches(){
        const int scale=4;
        foreach(string style in Styles)foreach(bool dark in new[]{true,false})using(var f=new Fixture(style)){
            var nav=new PopupNavigation {Page=PopupPage.ChoosePreference,PreferenceAction=223};var rows=PopupPages.Build(f.Owner,nav,false);var current=ThemePalette.Current(dark);
            using(var image=RenderPanel(PopupPages.SizeFor(rows),scale,delegate(Graphics g){PopupPages.Paint(g,f.Owner,nav,rows,dark,-1,-1,-1);}))foreach(var row in rows)if(row.Id!=6){
                var p=ThemePalette.For(row.PaletteKey,dark);float x=row.Rect.X+6,y=row.Rect.Y+(row.Rect.Height-18)/2;
                Check(Pixel(image,x+8,y+9,scale).ToArgb()==p.Card.ToArgb(),"mini swatch shows its own card color: "+row.PaletteKey);
                Check(Pixels(image,new RectangleF(x+6,y,18,1),scale,delegate(Color c){return c.ToArgb()==p.Border.ToArgb();})>0,"mini swatch has the palette border");
                Check(Pixel(image,x+22,y+9,scale).ToArgb()==p.Accent.ToArgb(),"8px accent dot stays inside the right of the swatch");
                Check(Pixels(image,new RectangleF(x+31,row.Rect.Top+2,6,row.Rect.Height-4),scale,delegate(Color c){return c.ToArgb()!=current.Card.ToArgb();})==0,"all names leave a clear gap after the 30px swatch");
                Color text=row.Selected?current.Accent:current.Foreground;Check(Pixels(image,new RectangleF(x+38,row.Rect.Top,60,row.Rect.Height),scale,delegate(Color c){return c.ToArgb()==text.ToArgb();})>0,"all four names start after their swatches and selected text uses accent");
                Check(Pixel(image,180,row.Rect.Y+row.Rect.Height/2,scale).ToArgb()==current.Card.ToArgb(),"theme rows keep the card background, including the selected row");
                if(row.Selected)Check(Pixels(image,new RectangleF(row.Rect.Right-22,row.Rect.Top+(row.Rect.Height-14)/2,14,14),scale,delegate(Color c){return c.ToArgb()==current.Accent.ToArgb();})>0,"selected theme retains an accent check mark");
            }
            var selected=rows.Find(delegate(PopupControl row){return row.Selected;});
            using(var hover=RenderPanel(PopupPages.SizeFor(rows),scale,delegate(Graphics g){PopupPages.Paint(g,f.Owner,nav,rows,dark,selected.Id,selected.Id,selected.Id);}))Check(Pixel(hover,180,selected.Rect.Y+selected.Rect.Height/2,scale).ToArgb()==current.Card.ToArgb(),"hover, press and keyboard focus do not flood a theme row");
            nav.PreferenceAction=210;rows=PopupPages.Build(f.Owner,nav,false);selected=rows.Find(delegate(PopupControl row){return row.Selected;});
            using(var ordinary=RenderPanel(PopupPages.SizeFor(rows),scale,delegate(Graphics g){PopupPages.Paint(g,f.Owner,nav,rows,dark,-1,-1,-1);}))Check(Pixel(ordinary,180,selected.Rect.Y+selected.Rect.Height/2,scale).ToArgb()==current.Selected.ToArgb(),"other chooser pages keep their selected fill");
            VisualDesign.HighContrastOverride=true;nav.PreferenceAction=223;rows=PopupPages.Build(f.Owner,nav,false);
            using(var hc=RenderPanel(PopupPages.SizeFor(rows),scale,delegate(Graphics g){PopupPages.Paint(g,f.Owner,nav,rows,dark,-1,-1,-1);}))foreach(var row in rows)if(row.Id!=6)Check(Pixels(hc,new RectangleF(row.Rect.X+6,row.Rect.Top+2,30,row.Rect.Height-4),scale,delegate(Color c){return c.ToArgb()!=SystemColors.Window.ToArgb();})==0,"high contrast omits every colored swatch");
            VisualDesign.HighContrastOverride=false;
        }
    }
    static Bitmap PopupRow(Fixture f,PopupNavigation nav,PopupControl row,bool dark,float scale,Action<Graphics> extra=null){
        var rows=new List<PopupControl>{row};
        return RenderPanel(PopupPages.SizeFor(rows),scale,delegate(Graphics g){PopupPages.Paint(g,f.Owner,nav,rows,dark,-1,-1,-1);if(extra!=null)extra(g);});
    }
    static Bitmap RowReference(Fixture f,PopupNavigation nav,PopupControl row,bool dark,float scale,Action<Graphics> draw){
        return PopupRow(f,nav,new PopupControl {Id=-1,Rect=row.Rect},dark,scale,draw);
    }
    static bool SameArea(Bitmap a,Bitmap b,RectangleF area,float scale){
        var crop=Rectangle.FromLTRB((int)Math.Floor(area.Left*scale),(int)Math.Floor(area.Top*scale),(int)Math.Ceiling(area.Right*scale),(int)Math.Ceiling(area.Bottom*scale));
        using(var left=a.Clone(crop,PixelFormat.Format32bppPArgb))using(var right=b.Clone(crop,PixelFormat.Format32bppPArgb))return SamePixels(left,right);
    }
    static void IconDetailRows(){
        foreach(bool dark in new[]{false,true})foreach(float scale in new[]{1f,1.25f,1.5f,2f})using(var f=new Fixture()){
            int count=0;
            foreach(var page in new[]{PopupPage.Preferences,PopupPage.Audio,PopupPage.Diagnostics,PopupPage.DeviceMissing}){
                var nav=new PopupNavigation {Page=page,VisibleChoices=100};
                foreach(var row in PopupPages.Build(f.Owner,nav,false))if(!String.IsNullOrEmpty(row.Icon)&&!String.IsNullOrEmpty(row.Detail)){
                    count++;row.Selected=false;
                    var rect=row.Rect;var icon=new RectangleF(16,rect.Top+4,16,16);var title=new RectangleF(40,rect.Top+1,187,22);
                    var detail=new RectangleF(title.Left,rect.Top+22,rect.Right-title.Left-(row.Id==217?12:24),18);
                    Check(icon.Top+icon.Height/2==title.Top+title.Height/2&&!icon.IntersectsWith(detail)&&detail.Left==title.Left,"reference icon aligns with the title and clears its detail column");
                    using(var actual=PopupRow(f,nav,row,dark,scale))using(var expected=RowReference(f,nav,row,dark,scale,delegate(Graphics g){
                        var p=ThemePalette.Current(dark);VisualDesign.DrawSymbol(g,row.Icon,icon,p.Muted);
                        VisualDesign.DrawLabel(g,row.Text,title,11.5f,false,row.Enabled?p.Foreground:p.Muted,StringAlignment.Near);
                        var lines=row.Detail.Split('\n');for(int i=0;i<lines.Length;i++)VisualDesign.DrawLabel(g,lines[i],new RectangleF(detail.X,detail.Y+18*i,detail.Width,detail.Height),11,false,p.Muted,StringAlignment.Near);
                    }))Check(SamePixels(actual,expected),"icon/title/detail rendering matches disjoint aligned rectangles: "+page+" / "+row.Id+" / "+scale+" / "+dark);
                }
            }
            Check(count>=5,"covers clear-history, both audio rows and both multiline feedback rows");
        }
    }
    static void UnchangedRowContent(){
        foreach(bool dark in new[]{false,true})foreach(float scale in new[]{1f,1.25f,1.5f,2f})using(var f=new Fixture()){
            var nav=new PopupNavigation {Page=PopupPage.More};
            foreach(string detail in new[]{null,""}){
                var row=new PopupControl {Id=299,Rect=new RectangleF(10,44,240,34),Text="Single line",Icon="refresh",Detail=detail};
                using(var actual=PopupRow(f,nav,row,dark,scale))using(var expected=RowReference(f,nav,row,dark,scale,delegate(Graphics g){
                    var p=ThemePalette.Current(dark);VisualDesign.DrawSymbol(g,"refresh",new RectangleF(16,53,16,16),p.Muted);
                    VisualDesign.DrawLabel(g,row.Text,new RectangleF(40,44,187,34),11.5f,false,p.Foreground,StringAlignment.Near);
                }))Check(SamePixels(actual,expected),"single-line icon and text retain their original rectangles");
            }
            foreach(int id in new[]{217,299}){
                var row=new PopupControl {Id=id,Rect=new RectangleF(10,44,240,44),Text="Title",Detail="Description"};
                using(var actual=PopupRow(f,nav,row,dark,scale))using(var expected=RowReference(f,nav,row,dark,scale,delegate(Graphics g){
                    var p=ThemePalette.Current(dark);VisualDesign.DrawLabel(g,row.Text,new RectangleF(16,45,211,22),11.5f,false,p.Foreground,StringAlignment.Near);
                    VisualDesign.DrawLabel(g,row.Detail,new RectangleF(16,66,id==217?222:210,18),11,false,p.Muted,StringAlignment.Near);
                }))Check(SamePixels(actual,expected),"rows without icons retain their original title/detail rectangles: "+id);
            }
            foreach(string kind in new[]{"toggle","slider","swatch","wrapped"}){
                var row=new PopupControl {Id=299,Rect=new RectangleF(10,44,240,54),Text="Special row",Icon="refresh",Detail="Description",Toggle=kind=="toggle",Slider=kind=="slider",PaletteKey=kind=="swatch"?"ocean":null,WrapText=kind=="wrapped"};
                using(var actual=PopupRow(f,nav,row,dark,scale)){
                    row.Detail=null;
                    using(var expected=PopupRow(f,nav,row,dark,scale))Check(SameArea(actual,expected,new RectangleF(15,44,19,54),scale),"special-row icon retains its single-line position: "+kind);
                }
            }
        }
    }
    static void Geometry(){
        Check(VisualDesign.PanelWidth==260&&VisualDesign.BatteryBarRect(0).Height==4&&VisualDesign.BatteryDigitSize==19&&VisualDesign.MainCardRadius==10&&VisualDesign.PanelRadius==12,"main geometry changes without changing dialog radius");
        using(var f=new Fixture()){
            var rows=PopupPages.Build(f.Owner,new PopupNavigation(),true);var expected=new Dictionary<int,RectangleF>{{0,new RectangleF(8,265,28,28)},{1,new RectangleF(226,265,28,28)},{2,new RectangleF(10,6,154,44)},{4,new RectangleF(168,6,80,44)},{200,new RectangleF(10,120,240,44)},{100,new RectangleF(12,180,97,26)},{101,new RectangleF(111,180,97,26)},{102,new RectangleF(216,178,34,30)},{240,new RectangleF(8,236,216,20)},{241,new RectangleF(228,234,28,24)}};
            for(int i=0;i<4;i++)expected[10+i]=new RectangleF(12+i*59.5f,66,57.5f,26);
            foreach(var pair in expected){var row=rows.Find(delegate(PopupControl c){return c.Id==pair.Key;});Check(row!=null&&row.Rect==pair.Value,"classic hit region matches the redesigned geometry exactly: "+pair.Key);}
            f.Owner.GenericReady=true;f.Owner.NativeMousePresent=false;f.Owner.Audio.Devices=new AudioDeviceChoice[0];f.Owner.WirelessDevices=new[]{Device("theme-one","很长很长的设备名称",75)};
            var layout=WirelessLayout.Build(f.Owner,0,1000);rows=PopupPages.Build(f.Owner,new PopupNavigation(),false,layout);
            Check(rows.Find(delegate(PopupControl c){return c.Id==1000;}).Rect==new RectangleF(10,6,154,44)&&rows.Find(delegate(PopupControl c){return c.Id==2000;}).Rect==new RectangleF(168,6,80,44),"wireless name and meter use the same redesigned header hits");
        }
        using(var image=new Bitmap(520,100))using(var g=Graphics.FromImage(image))foreach(string style in Styles)foreach(float scale in new[]{1f,1.25f,1.5f,2f}){
            g.ResetTransform();g.ScaleTransform(scale,scale);
            foreach(string text in new[]{"0","100","≈100","--"}){
                float preferred=VisualDesign.BatteryDigitSize;float size=CompactRenderer.BatteryTextSize(g,text,preferred,VisualDesign.BatteryDigitsRect(0).Width);
                using(var font=new Font("Segoe UI",size,FontStyle.Bold,GraphicsUnit.Pixel))using(var format=new StringFormat {FormatFlags=StringFormatFlags.NoWrap}){
                    Check(size==preferred&&g.MeasureString(text,font,Int32.MaxValue,format).Width<=VisualDesign.BatteryDigitsRect(0).Width,"full sized battery label fits "+style+" "+text+" "+scale);
                    int fitted,lines;g.MeasureString(text,font,VisualDesign.BatteryDigitsRect(0).Size,format,out fitted,out lines);Check(fitted==text.Length&&lines==1,"all battery glyphs fit in one line");
                }
            }
            var bar=VisualDesign.BatteryBarRect(0);var digits=VisualDesign.BatteryDigitsRect(0);var percent=VisualDesign.BatteryPercentRect(0);
            Check(bar.X==12&&bar.Right==248&&bar.Height==4&&percent.Right==248,"battery track and percentage respect the right inset");
            Check(digits.Bottom<bar.Top&&percent.Bottom<bar.Top,"digits and percent remain above the battery bar");
            using(var font=new Font("Segoe UI",11,FontStyle.Regular,GraphicsUnit.Pixel))using(var format=new StringFormat {FormatFlags=StringFormatFlags.NoWrap}){int fitted,lines;g.MeasureString("%",font,percent.Size,format,out fitted,out lines);Check(fitted==1&&lines==1,"percent glyph fits at 11px");}
        }
    }
    static Bitmap Card(int percent,bool fresh,bool dark,bool wireless,string style="ocean",int threshold=20,bool headset=false){
        var pref=new AudioBindings {VolumeCollapsed=false,RememberReadings=false,WarningThreshold=threshold};Style(pref,style);DeviceSettings.UseTransientConfiguration(pref);
        var image=new Bitmap(260,400);using(var g=Graphics.FromImage(image)){
            g.Clear(Color.Transparent);
            if(wireless)using(var f=new Fixture(style)){DeviceSettings.UseTransientConfiguration(pref);f.Owner.GenericReady=true;f.Owner.NativeMousePresent=false;f.Owner.Audio.Devices=new AudioDeviceChoice[0];f.Owner.WirelessDevices=new[]{Device("theme-low","电量测试",percent,fresh)};CompactRenderer.PaintWireless(g,f.Owner,WirelessLayout.Build(f.Owner,0,1000),dark,new PanelFeedback());}
            else {
                var ear=headset?new HeadsetReading {Connected=true,ConnectionVerified=true,ReceiverPresent=true,Percent=percent,ConnectionTimestampUtc=DateTime.UtcNow,BatteryTimestampUtc=fresh?DateTime.UtcNow:DateTime.UtcNow.AddHours(-2)}:null;
                CompactRenderer.Paint(g,new BatteryReading {Percent=headset?76:percent,TimestampUtc=fresh||headset?DateTime.UtcNow:DateTime.UtcNow.AddHours(-2)},null,null,false,Dpi(),null,false,dark,ear,Audio());
            }
        }return image;
    }
    static void LowBattery(){
        VisualDesign.HighContrastOverride=false;
        foreach(string style in Styles)foreach(bool dark in new[]{true,false})foreach(int threshold in new[]{10,15,20})foreach(int kind in new[]{0,1,2}){
            bool wireless=kind==1,headset=kind==2;int y=headset?VisualDesign.MouseCardHeight(Dpi())+VisualDesign.CardGap:0;
            using(var low=Card(threshold-1,true,dark,wireless,style,threshold,headset))using(var equal=Card(threshold,true,dark,wireless,style,threshold,headset))using(var history=Card(threshold-1,false,dark,wireless,style,threshold,headset)){
                Check(low.GetPixel(20,y+48)!=equal.GetPixel(20,y+48),"fresh low battery tints "+style+" "+kind+" "+dark+" "+threshold);Check(history.GetPixel(20,y+48)==equal.GetPixel(20,y+48),"threshold equality and history do not tint");
                // Thin 11px percent strokes are antialiased; test their red hue,
                // independently of exact-color checks on the semantic palette.
                bool digit=false,percent=false;for(int x=199;x<248;x++)for(int row=y+13;row<y+37;row++){var pixel=low.GetPixel(x,row);if(pixel.R>100&&pixel.R-pixel.G>40&&pixel.R-pixel.B>40){if(x<235)digit=true;else percent=true;}}
                Check(digit&&!percent,"low digits use red while percent stays muted: "+style+" dark="+dark+" kind="+kind+" threshold="+threshold+" digits="+digit+" percent="+percent);
            }
            VisualDesign.HighContrastOverride=true;using(var low=Card(threshold-1,true,dark,wireless,style,threshold,headset))using(var equal=Card(threshold,true,dark,wireless,style,threshold,headset))Check(low.GetPixel(20,y+48)==equal.GetPixel(20,y+48),"high contrast does not tint");VisualDesign.HighContrastOverride=false;
        }
    }
    static bool SamePixels(Bitmap a,Bitmap b){
        if(a.Size!=b.Size)return false;
        var rect=new Rectangle(Point.Empty,a.Size);var x=a.LockBits(rect,ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);var y=b.LockBits(rect,ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
        try{var left=new byte[x.Stride*x.Height];var right=new byte[y.Stride*y.Height];Marshal.Copy(x.Scan0,left,0,left.Length);Marshal.Copy(y.Scan0,right,0,right.Length);for(int i=0;i<left.Length;i++)if(left[i]!=right[i])return false;return true;}
        finally{a.UnlockBits(x);b.UnlockBits(y);}
    }
    static Bitmap ContrastFrame(string style,bool dark,int variant){
        using(var f=new Fixture(style)){
            if(variant==0)return RenderPanel(VisualDesign.PanelSizeFor(true,f.Owner.Dpi),1,delegate(Graphics g){CompactRenderer.Paint(g,f.Owner.Current,null,null,false,f.Owner.Dpi,null,false,dark,null,f.Owner.Audio,1,0,false,1,0,new PanelFeedback {HoverAction=240,PressedAction=1,KeyboardFocus=2});});
            if(variant==1){f.Owner.GenericReady=true;f.Owner.NativeMousePresent=false;f.Owner.Audio.Devices=new AudioDeviceChoice[0];f.Owner.WirelessDevices=new[]{Device("hc-low","低电量",12),Device("hc-full","满电量",100)};var layout=WirelessLayout.Build(f.Owner,0,1000);return RenderPanel(layout.Size,1,delegate(Graphics g){CompactRenderer.PaintWireless(g,f.Owner,layout,dark,new PanelFeedback {HoverAction=1000,PressedAction=0,KeyboardFocus=1});});}
            var nav=new PopupNavigation {Page=variant==2?PopupPage.Preferences:variant==3?PopupPage.ChoosePreference:PopupPage.Audio,PreferenceAction=223,VisibleChoices=100};var rows=PopupPages.Build(f.Owner,nav,false);
            foreach(var row in rows){if(row.Id==223)row.Value="固定测试文本";if(variant==3)row.Selected=row.PaletteKey=="ocean";}
            return RenderPanel(PopupPages.SizeFor(rows),1,delegate(Graphics g){PopupPages.Paint(g,f.Owner,nav,rows,dark,240,6,223);});
        }
    }
    static void HighContrast(){
        VisualDesign.HighContrastOverride=true;
        foreach(string style in Styles)foreach(bool dark in new[]{true,false}){
            var p=Palette(style,dark);Color[] system={SystemColors.Window,SystemColors.WindowText,SystemColors.WindowText,SystemColors.GrayText,SystemColors.Control,SystemColors.Highlight,SystemColors.Highlight,SystemColors.HighlightText};
            for(int i=0;i<Roles.Length;i++)Check(Role(p,Roles[i]).ToArgb()==system[i].ToArgb(),"high contrast system role "+Roles[i]);Check(Level(10,dark)==SystemColors.WindowText,"high contrast semantic colors are system colors");
        }
        for(int variant=0;variant<5;variant++)using(var expected=ContrastFrame("ocean",false,variant))foreach(string style in Styles)foreach(bool dark in new[]{true,false})using(var actual=ContrastFrame(style,dark,variant))Check(SamePixels(expected,actual),"high contrast rendered colors ignore style and mode in "+variant);
        VisualDesign.HighContrastOverride=false;
    }
    static void Surfaces(){
        foreach(string style in Styles)foreach(bool dark in new[]{true,false})using(var f=new Fixture(style)){
            var p=ThemePalette.Current(dark);
            using(var card=RenderPanel(new Size(260,96),1,delegate(Graphics g){CompactRenderer.DrawSurface(g,new RectangleF(.5f,.5f,259,95),dark);}))Check(card.GetPixel(80,20).ToArgb()==p.Card.ToArgb()&&card.GetPixel(80,70).ToArgb()==p.Card.ToArgb(),"flat exact card fill");
            using(var main=RenderPanel(VisualDesign.PanelSizeFor(true,f.Owner.Dpi),1,delegate(Graphics g){CompactRenderer.Paint(g,f.Owner.Current,null,null,false,f.Owner.Dpi,null,false,dark,null,f.Owner.Audio);}))Check(main.GetPixel(78,71).ToArgb()==p.SegmentFill.ToArgb()&&main.GetPixel(100,90).ToArgb()==p.Accent.ToArgb(),"neutral floating selected segment and 12px accent pip");
            using(var image=new Bitmap(1,1))using(var g=Graphics.FromImage(image)){
                var status=VisualDesign.FooterTextRect(0,208);
                Check(status==new RectangleF(39,3,175,24)&&status.Right<VisualDesign.FooterMoreRect(0).Left,"plain status text is bounded between its icon buttons");
            }
            var rows=PopupPages.Build(f.Owner,new PopupNavigation {Page=PopupPage.ChoosePreference,PreferenceAction=223},false);
            foreach(var row in rows)if(row.Id!=6)Check(!String.IsNullOrEmpty(row.PaletteKey),"each style choice carries its two-color swatch");
            using(var normal=RenderPanel(new Size(260,96),1,delegate(Graphics g){CompactRenderer.Paint(g,f.Owner.Current,null,null,false,f.Owner.Dpi,null,false,dark,null,null);}))
            using(var hover=RenderPanel(new Size(260,96),1,delegate(Graphics g){CompactRenderer.Paint(g,f.Owner.Current,null,null,false,f.Owner.Dpi,null,false,dark,null,null,1,-1,false,1,-1,new PanelFeedback {HoverAction=2});})){
                bool changed=false;for(int x=12;x<164;x++)for(int y=10;y<40;y++)changed|=normal.GetPixel(x,y)!=hover.GetPixel(x,y);Check(changed,"name row hover remains visible without a persistent arrow");
                var symbols=new List<string>();CompactRenderer.SymbolObserver=delegate(string name,RectangleF rect){symbols.Add(name);};try{using(var frame=RenderPanel(new Size(260,VisualDesign.CardHeightNormal),1,delegate(Graphics g){CompactRenderer.Paint(g,f.Owner.Current,null,null,false,f.Owner.Dpi,null,false,dark,null,null,1,-1,false,1,-1,new PanelFeedback {HoverAction=2});})){}Check(!symbols.Contains("chevron"),"device title does not paint an arrow even on hover");}finally{CompactRenderer.SymbolObserver=null;}
            }
        }
    }
    static Rectangle KnobBounds(Bitmap image,Color color,PointF center,int scale){
        int left=image.Width,top=image.Height,right=-1,bottom=-1;
        // White knob and white card can share a color. Follow only the solid
        // interior connected to the center, enclosed by the accent outline.
        var pending=new Queue<Point>();var visited=new HashSet<int>();pending.Enqueue(new Point((int)(center.X*scale),(int)(center.Y*scale)));
        while(pending.Count>0){
            var point=pending.Dequeue();int x=point.X,y=point.Y;
            if(x<0||y<0||x>=image.Width||y>=image.Height||Math.Abs(x-center.X*scale)>=10*scale||Math.Abs(y-center.Y*scale)>=10*scale||!visited.Add(y*image.Width+x)||image.GetPixel(x,y).ToArgb()!=color.ToArgb())continue;
            left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x);bottom=Math.Max(bottom,y);
            pending.Enqueue(new Point(x-1,y));pending.Enqueue(new Point(x+1,y));pending.Enqueue(new Point(x,y-1));pending.Enqueue(new Point(x,y+1));
        }
        Check(right>=left&&bottom>=top,"knob has visible solid pixels");return Rectangle.FromLTRB(left,top,right+1,bottom+1);
    }
    static void VolumeKnobs(){
        const int scale=4;
        foreach(string style in Styles)foreach(bool dark in new[]{true,false})foreach(bool secondary in new[]{false,true})using(var f=new Fixture(style)){
            var nav=new PopupNavigation {Page=PopupPage.Audio,VisibleChoices=100};var rows=PopupPages.Build(f.Owner,nav,false);var row=rows.Find(delegate(PopupControl c){return c.Id==240;});
            var rect=secondary?row.Rect:VisualDesign.VolumeSliderRect(new RectangleF(0,0,260,VisualDesign.SystemVolumeHeight));var track=PopupPages.VolumeTrack(rect);var center=new PointF(track.X+track.Width*.55f,track.Y+track.Height/2);
            var palette=ThemePalette.Current(dark);Color expected=dark?palette.Foreground:Color.White;
            Check(palette.Knob.ToArgb()==expected.ToArgb(),"slider is white in light mode and retains the existing light knob in dark mode: "+style+" "+dark);
            Check(Contrast(dark?expected:palette.Accent,palette.Track)>=3,"slider fill or light-mode accent outline contrasts with the unfilled track");
            var bounds=new List<Rectangle>();
            for(int state=0;state<3;state++){
                var feedback=new PanelFeedback {HoverAction=state==1?240:-1,PressedAction=state==2?240:-1};
                using(var image=RenderPanel(secondary?PopupPages.SizeFor(rows):new Size(260,VisualDesign.SystemVolumeHeight),scale,delegate(Graphics g){
                    g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;g.PixelOffsetMode=System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    if(secondary)PopupPages.Paint(g,f.Owner,nav,rows,dark,feedback.HoverAction,feedback.PressedAction,-1);else CompactRenderer.SystemVolume(g,new RectangleF(0,0,260,VisualDesign.SystemVolumeHeight),f.Owner.Audio,dark,feedback);
                })){
                    Check(Pixel(image,center.X,center.Y,scale).ToArgb()==expected.ToArgb(),"both slider renderers use the correct knob fill");
                    Check(Pixels(image,new RectangleF(center.X-1,center.Y-(state==0?6:7)-.5f,2,1),scale,delegate(Color c){return c.ToArgb()==palette.Accent.ToArgb();})>0,"slider retains its accent outline above the track");
                    bounds.Add(KnobBounds(image,palette.Knob,center,scale));
                }
            }
            foreach(var bound in bounds)Check(Math.Abs(bound.Width-bound.Height)<=1,"volume knob stays circular");
            Check(Math.Abs(bounds[0].Width/(float)scale-11)<=.5f&&Math.Abs(bounds[1].Width/(float)scale-13)<=.5f&&bounds[1].Size==bounds[2].Size,"both sliders grow from 12px to 14px on hover/drag inside the 1px outline");
        }
    }
    static void RepaintCache(){
        using(var f=new Fixture()){
            Call(f.Popup,"UpdateLayoutSize");var handle=f.Popup.Handle;
            Check((bool)Call(f.Popup,"RenderLayeredFrame"),"hidden layered frame presents");var before=(string)Field(f.Popup,"lastPresentedKey");
            var pref=DeviceSettings.Current;Style(pref,"amber");DeviceSettings.UseTransientConfiguration(pref);
            Check((bool)Call(f.Popup,"RenderLayeredFrame"),"style change presents again");var after=(string)Field(f.Popup,"lastPresentedKey");Check(before!=after,"ThemeStyle invalidates the visible-content cache without another setting changing");
        }
    }
    static void RenderThemes(){
        RenderTrayIcons();
        const float scale=2;
        foreach(string style in Styles)foreach(bool dark in new[]{true,false})using(var f=new Fixture(style)){
            var pages=new List<Bitmap>();
            try {
                pages.Add(RenderPanel(VisualDesign.PanelSizeFor(true,f.Owner.Dpi),scale,delegate(Graphics g){CompactRenderer.Paint(g,f.Owner.Current,null,null,false,f.Owner.Dpi,null,false,dark,null,f.Owner.Audio,1,-1,false,1,-1,new PanelFeedback());}));
                f.Owner.GenericReady=true;f.Owner.NativeMousePresent=false;f.Owner.Audio.Devices=new AudioDeviceChoice[0];
                f.Owner.WirelessDevices=new[]{Device("preview-low","低电量 · 无线鼠标",14),Device("preview-full","满电 · 无线键盘",100),Device("preview-approx","估算电量 · 耳机",100,true,true),Device("preview-history","历史读数 · 仅供参考",12,false),Device("preview-long","这是一个很长很长的设备名称用于检查标题的省略显示和箭头预留区域",76)};
                f.Owner.WirelessDevices[1].Kind="keyboard";f.Owner.WirelessDevices[2].Kind="headset";
                var layout=WirelessLayout.Build(f.Owner,0,1000);
                pages.Add(RenderPanel(layout.Size,scale,delegate(Graphics g){CompactRenderer.PaintWireless(g,f.Owner,layout,dark,new PanelFeedback());}));
                var nav=new PopupNavigation {Page=PopupPage.Preferences,VisibleChoices=7};
                for(int page=0;page<2;page++){
                    var rows=PopupPages.Build(f.Owner,nav,false);
                    pages.Add(RenderPanel(PopupPages.SizeFor(rows),scale,delegate(Graphics g){PopupPages.Paint(g,f.Owner,nav,rows,dark,-1,-1,-1);}));
                    if(page==0)nav.Offset=PageOffset(rows,8,7);
                }
                var chooser=new PopupNavigation {Page=PopupPage.ChoosePreference,PreferenceAction=223,VisibleChoices=7};var choices=PopupPages.Build(f.Owner,chooser,false);
                pages.Add(RenderPanel(PopupPages.SizeFor(choices),scale,delegate(Graphics g){PopupPages.Paint(g,f.Owner,chooser,choices,dark,-1,-1,-1);}));
                int height=0;foreach(var page in pages)height=Math.Max(height,page.Height);
                using(var sheet=new Bitmap((260*5+24*6)*(int)scale,height+104*(int)scale,PixelFormat.Format32bppPArgb))using(var g=Graphics.FromImage(sheet)){
                    g.Clear(Color.FromArgb(128,128,128));g.ScaleTransform(scale,scale);
                    CompactRenderer.DrawLabel(g,ThemePalette.Name(style)+" / "+(dark?"深色":"浅色"),new RectangleF(24,12,1000,28),18,true,Color.White,StringAlignment.Near);
                    string[] titles={"主界面 / 音源与展开音量","多设备 / 低电量与历史读数","显示与提醒 / 第 1 页","显示与提醒 / 第 2 页","配色选择 / 点击后停留"};
                    for(int i=0;i<pages.Count;i++){
                        float x=24+i*284;CompactRenderer.DrawLabel(g,titles[i],new RectangleF(x,48,260,24),12,false,Color.White,StringAlignment.Near);
                        g.DrawImage(pages[i],new RectangleF(x,80,260,pages[i].Height/scale));
                    }
                    string path=Path.Combine(output,style+"-"+(dark?"dark":"light")+".png");sheet.Save(path,ImageFormat.Png);Console.WriteLine(path);
                }
            }finally{foreach(var page in pages)page.Dispose();}
        }
    }
    static void RenderTrayIcons(){
        int?[] levels={0,1,15,46,80,100,null,46,46};string[] labels={"0%","1%","15%","46%","80%","100%","无读数","充电","历史"};
        string[] kinds={"mouse","keyboard","headset","device"};
        using(var sheet=new Bitmap(180+levels.Length*152+12,1088,PixelFormat.Format32bppPArgb))using(var g=Graphics.FromImage(sheet)){
            g.Clear(Color.FromArgb(115,120,128));
            CompactRenderer.DrawLabel(g,"托盘图标 / 原生像素尺寸；设备图标依次为鼠标、键盘、耳机、通用设备；1% 检查最小填充",new RectangleF(12,4,sheet.Width-24,28),15,true,Color.White,StringAlignment.Near);
            for(int state=0;state<levels.Length;state++)CompactRenderer.DrawLabel(g,labels[state],new RectangleF(180+state*152,32,152,24),13,true,Color.White,StringAlignment.Center);
            int row=0;foreach(bool dark in new[]{false,true})foreach(string style in new[]{"number","device"})foreach(int size in new[]{16,20,24,32}){
                int y=60+row++*64;Color background=dark?Color.FromArgb(32,32,32):Color.FromArgb(243,243,243);using(var brush=new SolidBrush(background))g.FillRectangle(brush,0,y,sheet.Width,64);
                CompactRenderer.DrawLabel(g,(dark?"深色":"浅色")+" / "+(style=="number"?"数字":"设备")+" / "+size+"px",new RectangleF(10,y,164,64),13,false,dark?Color.White:Color.Black,StringAlignment.Near);
                for(int state=0;state<levels.Length;state++){
                    int count=style=="device"?4:1;float start=180+state*152+(152-(count*size+(count-1)*4))/2f;
                    for(int kind=0;kind<count;kind++)using(var bitmap=style=="device"?TrayGlyph.RenderDevice(levels[state],state==7,dark,state==8,size,kinds[kind]):TrayGlyph.Render(levels[state],state==7,dark,state==8,size))g.DrawImageUnscaled(bitmap,(int)start+kind*(size+4),y+(64-size)/2);
                }
            }
            string path=Path.Combine(output,"tray-icons.png");sheet.Save(path,ImageFormat.Png);Console.WriteLine(path);
        }
    }
    static Bitmap RenderPanel(Size size,float scale,Action<Graphics> paint){
        var image=new Bitmap((int)Math.Ceiling(size.Width*scale),(int)Math.Ceiling(size.Height*scale),PixelFormat.Format32bppPArgb);
        using(var g=Graphics.FromImage(image)){g.Clear(Color.Transparent);g.ScaleTransform(scale,scale);paint(g);}return image;
    }
    [STAThread] static int Main(string[] args){
        bool render=args.Length>0&&args[0]=="--render";output=Path.GetFullPath(args[render?1:0]);Directory.CreateDirectory(output);var previous=DeviceSettings.Current;
        try{VisualDesign.HighContrastOverride=false;if(render){RenderThemes();return 0;}Run("ThemeStyle compatibility",Configuration);Run("exact palette and WCAG contrast",Palettes);Run("choice save and stay",Choices);Run("noninteractive preference groups and paging",Groups);Run("group-aligned preferences and bounded fallback",PreferencePages);Run("toggle knob rendering and contrast",ToggleKnobs);Run("mini swatches, selection and label spacing",ThemeSwatches);Run("icon/detail aligned rendering",IconDetailRows);Run("unchanged single-line, icon-free and special rows",UnchangedRowContent);Run("geometry and hit targets",Geometry);Run("fresh low battery rendering",LowBattery);Run("high contrast system roles and rendered frames",HighContrast);Run("flat surfaces, swatches, sync sizing and title feedback",Surfaces);Run("round volume knob states",VolumeKnobs);Run("ThemeStyle repaint cache",RepaintCache);Console.WriteLine(checks+" checks, "+failures+" failed groups; injected readers and TEMP settings only");return failures==0?0:1;}
        finally{VisualDesign.HighContrastOverride=null;DeviceSettings.UseTransientConfiguration(previous);}
    }
}
