using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

// Offline fixtures only. The pipeline supplies an output directory outside the worktree.
static class PanelDesignTests {
    const BindingFlags Static=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks,failures;
    static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
    static void Run(string name,Action test){try{test();Console.WriteLine("PASS "+name);}catch(Exception error){failures++;Console.Error.WriteLine("FAIL "+name+": "+(error.InnerException??error).Message);}}
    static object Geometry(string name,params object[] args){var method=typeof(VisualDesign).GetMethod(name,Static);Check(method!=null,"shared geometry exists: "+name);return method.Invoke(null,args);}
    static RectangleF Rect(string name,params object[] args){return (RectangleF)Geometry(name,args);}
    static Color Role(ThemePalette palette,string name){var property=typeof(ThemePalette).GetProperty(name,BindingFlags.Instance|BindingFlags.NonPublic);Check(property!=null,"palette role exists: "+name);return (Color)property.GetValue(palette,null);}
    static void Mode(string font,string style="ocean",bool collapsed=false,string layout="bar"){
        var settings=new AudioBindings {FontSize=font,ThemeStyle=style,VolumeCollapsed=collapsed,ReducedMotion=true,RememberReadings=false,LowBatteryNotifications=false};
        var field=typeof(AudioBindings).GetField("PanelLayout");if(field!=null)field.SetValue(settings,layout);else Check(layout=="bar","PanelLayout must exist for the new default ring layout");
        DeviceSettings.UseTransientConfiguration(settings);
    }
    static string Layout(AudioBindings settings){var field=typeof(AudioBindings).GetField("PanelLayout");Check(field!=null,"PanelLayout is persisted");return (string)field.GetValue(settings);}
    static void LayoutSettings(){
        Check(Layout(new AudioBindings())=="ring","new settings default to ring");
        var field=typeof(AudioBindings).GetField("PanelLayout");var member=(System.Runtime.Serialization.DataMemberAttribute)Attribute.GetCustomAttribute(field,typeof(System.Runtime.Serialization.DataMemberAttribute));Check(member!=null&&member.Order==30,"layout uses the unused data member 30");
        string directory=Path.Combine(Path.GetTempPath(),"myfinder-ring-settings-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);string path=Path.Combine(directory,"settings.json");
        Check(Layout(DeviceSettings.LoadBindings(path))=="ring","missing file defaults to ring");
        File.WriteAllText(path,"{\"SchemaVersion\":1,\"Theme\":\"dark\",\"MouseAlias\":\"保留名称\"}");var legacy=DeviceSettings.LoadBindings(path);Check(Layout(legacy)=="ring"&&legacy.Theme=="dark"&&legacy.MouseAlias=="保留名称","legacy file migrates only the layout default");
        foreach(string value in new[]{"ring","bar","grid","invalid","",null}){
            field.SetValue(legacy,value);DeviceSettings.SaveBindings(path,legacy);var loaded=DeviceSettings.LoadBindings(path);string expected=value=="bar"||value=="grid"?value:"ring";
            Check(Layout(loaded)==expected&&Layout(loaded.Clone())==expected,"layout round trip, clone and normalization: "+value);DeviceSettings.UseTransientConfiguration(loaded);Check(Layout(DeviceSettings.Current)==expected,"transient settings normalize layout too");
        }
        foreach(string value in new[]{"{}","[]","42","true","null"}){File.WriteAllText(path,"{\"SchemaVersion\":1,\"PanelLayout\":"+value+",\"MouseAlias\":\"保留名称\"}");var loaded=DeviceSettings.LoadBindings(path);Check(Layout(loaded)=="ring"&&loaded.MouseAlias=="保留名称","non-string layout falls back without discarding other fields: "+value);}
    }
    static void RingGeometry(){
        foreach(string font in new[]{"normal","large"})foreach(int count in new[]{4,5,8})foreach(bool headset in new[]{false,true})foreach(bool collapsed in new[]{false,true}){
            Mode(font,"ocean",collapsed,"ring");bool large=font=="large";int header=large?62:54,bottom=large?12:10;int height=header+VisualDesign.SelectionTrackHeight(count)+bottom;
            Check(VisualDesign.HeaderHeight==header&&VisualDesign.MouseCardHeight(Dpi(count))==height,"ring row and controls have specified heights");
            var ring=VisualDesign.BatteryRingRect(0);Check(ring.Width==(large?44:38)&&ring.Height==ring.Width&&ring.X==(large?12:11)&&ring.Y==(header-ring.Height)/2,"ring wraps its centered device icon on the left");
            Check(VisualDesign.HeaderTextRect(0,false).X==(large?66:58),"ring name starts after the gauge");
            Check(VisualDesign.StageTrackRect(0,count).Top==header&&VisualDesign.StageTrackRect(0,count).Left==(large?12:10),"ring controls follow the device row without a bar gap");
            int expected=height+1+(headset?(header+VisualDesign.SelectionHeight+bottom+1):0)+VisualDesign.SystemVolumeHeightFor(collapsed)+8+VisualDesign.FooterHeight;
            Check(VisualDesign.PanelSizeFor(headset,Dpi(count)).Height==expected,"ring calculated height includes separators and the external footer");
            using(var fixture=new Fixture()){
                fixture.Owner.Dpi=Dpi(count);if(!headset)fixture.Owner.Headset=null;var layout=WirelessLayout.Build(fixture.Owner,0,10000);Check(layout.Size==VisualDesign.PanelSizeFor(headset,fixture.Owner.Dpi),"native and wireless ring heights agree");
                var rows=PopupPages.Build(fixture.Owner,new PopupNavigation(),headset);Check(rows.Find(delegate(PopupControl row){return row.Id==2;}).Rect==new RectangleF(0,0,VisualDesign.PanelWidth,header),"entire ring row opens details");Check(!rows.Exists(delegate(PopupControl row){return row.Id==4||row.Id==5;}),"ring numbers belong to the detail row; refresh remains in the footer");
                if(count==4&&!collapsed)Console.WriteLine("ring "+font+" / headset="+headset+": "+expected);
            }
        }
    }
    static DpiConfig Dpi(int count=4){var dpi=new DpiConfig {DeviceKey=MouseBattery.DeviceKey("panel-design-fixture"),ActiveStage=1,TimestampUtc=DateTime.UtcNow};for(int i=0;i<count;i++)dpi.Stages.Add(new DpiStage {XDpi=400*(i+1),YDpi=400*(i+1)});return dpi;}
    static void GridBasics(){
        Check(PanelLayouts.Effective("grid")=="grid","grid must have its own geometry, not the phase-2 ring fallback");
        foreach(string font in new[]{"normal","large"}){Mode(font,"ocean",false,"grid");using(var fixture=new Fixture()){
            var owner=fixture.Owner;owner.GenericReady=true;var layout=WirelessLayout.Build(owner,0,10000);var rows=PopupPages.Build(owner,new PopupNavigation(),true,layout);
            int width=font=="large"?146:126,height=font=="large"?104:92;
            Check(rows.Find(delegate(PopupControl row){return row.Id==2;}).Rect==new RectangleF(0,0,width,height),"native mouse occupies the first grid tile");
            Check(rows.Find(delegate(PopupControl row){return row.Id==3;}).Rect==new RectangleF(width+8,0,width,height),"headset occupies the second column");
            Check(!rows.Exists(delegate(PopupControl row){return row.Id==4||row.Id==5;}),"grid tile is one direct detail action");
            var dpi=rows.Find(delegate(PopupControl row){return row.Id==10;});Check(dpi.Rect.Top>=height+8,"DPI belongs to the fixed quick controls below the grid");
        }}
    }
    static WirelessDevice GridDevice(int index,int stages=0){var values=new int[stages];for(int i=0;i<stages;i++)values[i]=400*(i+1);return new WirelessDevice {Id="grid-"+index,Name="设备 "+index,Kind=stages>0?"mouse":"keyboard",Percent=85,Fresh=true,Online=true,ConnectionState="connected",ConfirmedAtUtc=DateTime.UtcNow.ToString("o"),Dpi=stages>0?new WirelessDpi {Supported=true,CanSet=true,Current=800,Stages=values,TargetToken=new string((char)('a'+index%6),64),CheckedAtUtc=DateTime.UtcNow.ToString("o")}:null};}
    static void GridOverflowHeight(string font){
        Mode(font,"ocean",false,"grid");using(var fixture=new Fixture()){
            var owner=fixture.Owner;owner.GenericReady=true;owner.Dpi=Dpi(8);owner.WirelessDevices=new[]{GridDevice(0,8),GridDevice(1,8),GridDevice(2,8),GridDevice(3,8)};
            var layout=WirelessLayout.Build(owner,0,520);
            Console.WriteLine("grid overflow "+font+": height="+layout.Size.Height+", volume="+layout.VolumeRect+", footer="+layout.Footer);
            Check(layout.Size.Height<=520,"five mice x eight stages must fit the actual 520px available height");
            Check(layout.VolumeRect.Bottom<=520-VisualDesign.FooterHeight&&layout.Footer+VisualDesign.FooterHeight<=520,"system volume and footer stay visible at 520px");
        }
    }
    static Bitmap RenderGrid(TrayContext owner,WirelessLayout layout,bool dark,float scale,PanelFeedback feedback){
        var size=UiState.ScaleSize(layout.Size,scale);var image=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppPArgb);
        using(var g=Graphics.FromImage(image)){g.Clear(Color.Transparent);g.ScaleTransform(scale,scale);CompactRenderer.PaintWireless(g,owner,layout,dark,feedback);}return image;
    }
    static bool GridSameArea(Bitmap left,Bitmap right,Rectangle area){
        if(area.Width<=0||area.Height<=0)return true;
        var a=left.LockBits(area,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);var b=right.LockBits(area,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try{var x=new byte[area.Width*4];var y=new byte[x.Length];for(int row=0;row<area.Height;row++){
            System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(a.Scan0,row*a.Stride),x,0,x.Length);System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(b.Scan0,row*b.Stride),y,0,y.Length);
            for(int i=0;i<x.Length;i++)if(x[i]!=y[i])return false;
        }return true;}finally{left.UnlockBits(a);right.UnlockBits(b);}
    }
    static string GridDifference(Bitmap left,Bitmap right,Rectangle area){
        int count=0,firstX=-1,firstY=-1,minX=area.Right,minY=area.Bottom,maxX=-1,maxY=-1;int leftArgb=0,rightArgb=0;
        for(int y=area.Top;y<area.Bottom;y++)for(int x=area.Left;x<area.Right;x++){
            int a=left.GetPixel(x,y).ToArgb(),b=right.GetPixel(x,y).ToArgb();if(a==b)continue;
            if(firstX<0){firstX=x;firstY=y;leftArgb=a;rightArgb=b;}count++;minX=Math.Min(minX,x);minY=Math.Min(minY,y);maxX=Math.Max(maxX,x);maxY=Math.Max(maxY,y);
        }
        return count==0?"none":"count="+count+" first=("+firstX+","+firstY+") left=0x"+leftArgb.ToString("X8")+" right=0x"+rightArgb.ToString("X8")+" bounds=("+minX+","+minY+")- ("+maxX+","+maxY+") area="+area;
    }
    static string GridFeedback(PanelFeedback feedback){return feedback==null?"<null>":feedback.Key;}
    static string GridControl(PopupControl control){
        if(control==null)return "<null>";
        return "id="+control.Id+" deviceKey="+control.DeviceKey+" rect="+control.Rect+" visible="+control.VisibleRect+" viewport="+(control.Viewport.HasValue?control.Viewport.Value.ToString():"<none>");
    }
    static string GridFrameState(TrayContext owner,WirelessLayout layout,PanelFeedback feedback){
        var grid=layout.Grid;
        string tags="";for(int i=0;i<layout.Cards.Count;i++){var card=layout.Cards[i];bool sleeping=card.Device!=null&&(card.Device.ConnectionState=="sleeping"||card.Device.MeasurementState=="sleeping");string status=GridLayout.Status(card.Battery,sleeping)??"<none>";bool dpiFresh=card.Device!=null&&WirelessLayout.DpiStageCount(card.Device)>0&&WirelessLayout.DpiFresh(card.Device);tags+=(i==0?"":";")+"card"+i+"="+status+"/batteryFresh="+(card.Battery!=null&&card.Battery.Fresh)+"/dpiFresh="+dpiFresh;}
        return "feedback="+GridFeedback(feedback)+" size="+layout.Size+" cards="+layout.Cards.Count+" all="+layout.All.Count+" controls="+grid.Controls+" viewport="+grid.Viewport+" scroll="+grid.ScrollOffset+"/"+grid.MaximumScroll+" groups="+grid.Groups.Count+" volume="+layout.VolumeRect+" footer="+layout.Footer+" tags="+tags+" owner(native="+owner.NativeMousePresent+",genericReady="+owner.GenericReady+",genericScanning="+owner.GenericScanning+",audio="+(owner.Audio!=null)+",headset="+(owner.Headset!=null)+",dpi="+(owner.Dpi!=null)+",refresh="+owner.IsRefreshing+",audioBusy="+owner.IsAudioBusy+",headsetBusy="+owner.IsHeadsetRefreshing+")";
    }
    static void GridDiagnostic(string label,string parameters,TrayContext owner,WirelessLayout leftLayout,WirelessLayout rightLayout,PanelFeedback leftFeedback,PanelFeedback rightFeedback,PopupControl hidden,Bitmap left,Bitmap right,Rectangle area){
        string path=Path.Combine(Path.GetTempPath(),"myfinder-ui-grid-ci-1113h-"+Guid.NewGuid().ToString("N")+".log");
        string text="label="+label+Environment.NewLine+"parameters="+parameters+Environment.NewLine+"hidden="+GridControl(hidden)+Environment.NewLine+"left="+GridFrameState(owner,leftLayout,leftFeedback)+Environment.NewLine+"right="+GridFrameState(owner,rightLayout,rightFeedback)+Environment.NewLine+"diff="+GridDifference(left,right,area)+Environment.NewLine;
        try{File.WriteAllText(path,text);Console.Error.WriteLine("GRID_DIAGNOSTIC "+path);}
        catch(Exception error){Console.Error.WriteLine("GRID_DIAGNOSTIC_WRITE_FAILED "+error.Message+"; "+text);}
    }
    static void GridOverflowViewportMatrix(){
        foreach(string font in new[]{"normal","large"})foreach(bool native in new[]{false,true})foreach(bool audio in new[]{false,true})foreach(bool collapsed in new[]{false,true}){
            Mode(font,"ocean",collapsed,"grid");using(var fixture=new Fixture()){
                var owner=fixture.Owner;owner.GenericReady=true;owner.NativeMousePresent=native;owner.Dpi=Dpi(8);var devices=new List<WirelessDevice>();
                for(int i=0;i<(native?4:5);i++){var device=GridDevice(i,8);device.Id="device-"+i.ToString().PadLeft(24,'0');device.Provider="logitech";device.SourceQuality="verified";device.MeasurementState="measured";device.Dpi.StageSource="onboard";devices.Add(device);}owner.WirelessDevices=devices.ToArray();
                typeof(TrayContext).GetField("nativeNeedsReconcile",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(owner,false);
                if(!audio){owner.Headset=null;owner.Audio=null;}
                var first=WirelessLayout.Build(owner,0,520);var normal=WirelessLayout.Build(owner,0,10000);
                Check(first.Grid.Overflow&&first.Grid.VisibleRows==1&&first.Cards.Count==2,"overflow reserves a complete tile row, not a zero-height device viewport");
                Check(normal.Grid.MaximumScroll==0&&normal.Grid.ScrollOffset==0&&normal.Grid.Viewport.Bottom==normal.VolumeTop,"normal height keeps every group and introduces no scrolling");
                Check(first.Grid.Groups.Count==(audio?6:5)&&first.Grid.Groups.TrueForAll(delegate(GridControlGroup group){return group.Audio||group.Count==8;}),"all five eight-stage mice and optional audio group remain present");
                foreach(float scale in new[]{1f,1.25f,1.5f,2f})foreach(bool dark in new[]{false,true}){
                    using(var baseline=RenderGrid(owner,first,dark,scale,new PanelFeedback()))foreach(int offset in new[]{0,31,Int32.MaxValue}){
                        var layout=WirelessLayout.Build(owner,0,520,false,offset);var rows=PopupPages.Build(owner,new PopupNavigation(),audio,layout);var regions=new List<InteractionRegion>();
                        Check(layout.Size.Height==520&&layout.Grid.Controls==first.Grid.Controls&&layout.VolumeRect==first.VolumeRect&&layout.Footer==first.Footer,"scrolling never moves the bounded card, system volume or footer");
                        Check(rows.FindAll(delegate(PopupControl row){return row.DpiValue>0||row.Id>=10&&row.Id<18;}).Count==40,"no DPI stages are discarded by clipping");
                        int enabled=rows.FindAll(delegate(PopupControl row){return row.Enabled&&(row.DpiValue>0||row.Id>=10&&row.Id<18);}).Count;
                        Check(enabled==40,"clipping tests use forty genuinely enabled fixture DPI actions: "+font+"/native="+native+"/audio="+audio+"/collapsed="+collapsed+" actual="+enabled);
                        Check(rows.FindAll(delegate(PopupControl row){return row.Id>=100&&row.Id<=102;}).Count==(audio?3:0),"audio controls follow audio group presence");
                        Check(rows.Exists(delegate(PopupControl row){return row.Id==240;})!=collapsed,"volume expansion is independent of group scrolling");
                        InteractionLayout.Observer=regions.Add;
                        try{using(var image=RenderGrid(owner,layout,dark,scale,new PanelFeedback())){
                            var topArea=new Rectangle(0,0,image.Width,(int)(first.Grid.Controls.Top*scale));bool topSame=GridSameArea(baseline,image,topArea);
                            if(!topSame)GridDiagnostic("scrolling cannot paint into the device tiles","font="+font+",native="+native+",audio="+audio+",collapsed="+collapsed+",scale="+scale+",dark="+dark+",offset="+offset,owner,first,layout,new PanelFeedback(),new PanelFeedback(),null,baseline,image,topArea);
                            Check(topSame,"scrolling cannot paint into the device tiles");
                            int fixedTop=(int)Math.Ceiling(first.VolumeTop*scale);var fixedArea=new Rectangle(0,fixedTop,image.Width,image.Height-fixedTop);bool fixedSame=GridSameArea(baseline,image,fixedArea);
                            if(!fixedSame)GridDiagnostic("scrolling cannot paint over system volume or the footer","font="+font+",native="+native+",audio="+audio+",collapsed="+collapsed+",scale="+scale+",dark="+dark+",offset="+offset,owner,first,layout,new PanelFeedback(),new PanelFeedback(),null,baseline,image,fixedArea);
                            Check(fixedSame,"scrolling cannot paint over system volume or the footer");
                        }}finally{InteractionLayout.Observer=null;}
                        foreach(var row in rows){
                            var visible=row.VisibleRect;if(!visible.IsEmpty)Check(visible.Left>=0&&visible.Right<=layout.Size.Width&&visible.Top>=0&&visible.Bottom<=layout.Size.Height,"all visible rectangles fit the actual 520px panel at every scale");
                            if(row.Viewport.HasValue&&!visible.IsEmpty)Check(layout.Grid.Viewport.Contains(visible)&&visible.Bottom<=layout.VolumeTop,"group hit and accessibility bounds stay inside their shared viewport");
                            if(row.Information||row.Id<0)continue;var region=regions.Find(delegate(InteractionRegion item){return item.Id==row.Id;});
                            Check(visible.IsEmpty?region==null:region!=null&&region.Limit==visible,"clipped drawing limits equal the visible hit rectangle, including partial controls");
                            if(row.Enabled&&!visible.IsEmpty)Check(PopupPages.Hit(rows,new PointF(visible.X+visible.Width/2,visible.Y+visible.Height/2))==row,"visible parts alone remain clickable");
                        }
                        var last=layout.Grid.Groups[layout.Grid.Groups.Count-1];if(offset==Int32.MaxValue)Check(layout.Grid.ScrollOffset==layout.Grid.MaximumScroll&&layout.Grid.Viewport.Contains(last.Track),"scrolling to the end fully exposes the last group without bottom overflow");
                        var partial=rows.Find(delegate(PopupControl row){return row.Viewport.HasValue&&row.Enabled&&!row.Information&&!row.VisibleRect.IsEmpty&&row.VisibleRect!=row.Rect;});
                        if(offset==31)Check(partial!=null,"the regression exercises a genuinely enabled partially visible stage");
                        if(partial!=null){var point=new PointF(partial.Rect.X+partial.Rect.Width/2,partial.Rect.Top<partial.VisibleRect.Top?partial.Rect.Top+.25f:partial.Rect.Bottom-.25f);Check(PopupPages.Hit(rows,point)!=partial,"the cropped part of a partially visible control cannot be hit");}
                        var hidden=rows.Find(delegate(PopupControl row){return row.Viewport.HasValue&&!row.Information&&row.VisibleRect.IsEmpty;});
                        if(hidden!=null){var ordinaryFeedback=new PanelFeedback();var hoveredFeedback=new PanelFeedback {HoverAction=hidden.Id,PressedAction=hidden.Id,KeyboardFocus=hidden.Id};using(var ordinary=RenderGrid(owner,layout,dark,scale,ordinaryFeedback))using(var hovered=RenderGrid(owner,layout,dark,scale,hoveredFeedback)){
                            var fullArea=new Rectangle(Point.Empty,ordinary.Size);bool same=GridSameArea(ordinary,hovered,fullArea);
                            if(!same)GridDiagnostic("offscreen hover, press and focus feedback leaves no ink in another region","font="+font+",native="+native+",audio="+audio+",collapsed="+collapsed+",scale="+scale+",dark="+dark+",offset="+offset,owner,layout,layout,ordinaryFeedback,hoveredFeedback,hidden,ordinary,hovered,fullArea);
                            Check(same,"offscreen hover, press and focus feedback leaves no ink in another region");
                        }}
                    }
                }
                var clamped=WirelessLayout.Build(owner,0,520,false,Int32.MaxValue);owner.WirelessDevices=new WirelessDevice[0];owner.Headset=null;owner.Audio=null;
                var reduced=WirelessLayout.Build(owner,0,520,false,clamped.Grid.ScrollOffset);Check(reduced.Grid.ScrollOffset==0&&!reduced.Grid.Overflow,"shrinking the group list clamps obsolete offsets to zero");
            }
        }
    }
    static void GridCountsAndPaging(){
        foreach(string font in new[]{"normal","large"})foreach(int count in new[]{1,2,3,4,5}){Mode(font,"ocean",false,"grid");using(var fixture=new Fixture()){
            var owner=fixture.Owner;owner.NativeMousePresent=false;owner.Headset=null;owner.GenericReady=true;var devices=new List<WirelessDevice>();for(int i=0;i<count;i++)devices.Add(GridDevice(i));owner.WirelessDevices=devices.ToArray();
            var full=WirelessLayout.Build(owner,0,10000);int rows=(count+1)/2,expected=rows*(GridLayout.TileHeight+8)+VisualDesign.SystemVolumeHeight+2+VisualDesign.FooterHeight;
            Check(full.Size.Height==expected&&full.Cards.Count==count,"1..5 tiles use row height plus the volume-only controls and footer");Console.WriteLine("grid "+font+" / "+count+" devices / volume only: "+expected);
            for(int i=0;i<count;i++){var tile=full.Cards[i].Tile;Check(tile.Height==GridLayout.TileHeight&&tile.Width==(i==count-1&&count%2==1?VisualDesign.PanelWidth:GridLayout.TileWidth),"only the global odd last tile spans both columns");Check(tile.Top==i/2*(GridLayout.TileHeight+8),"grid rows have an eight-pixel gutter");}
            int maximum=GridLayout.TileHeight+8+(int)full.Grid.Controls.Height+VisualDesign.FooterHeight;
            for(int offset=0;offset<count+3;offset++){
                var page=WirelessLayout.Build(owner,offset,maximum);Check(page.Size.Height<=maximum&&page.Start%2==0&&page.Cards.Count<=2,"bounded grid pages scroll whole rows");
                Check(page.Grid.Controls.Top==GridLayout.TileHeight+8&&page.VolumeRect==WirelessLayout.Build(owner,0,maximum).VolumeRect,"scrolling keeps quick controls and volume at identical coordinates");
                var hit=PopupPages.Build(owner,new PopupNavigation(),false,page);var regions=new List<InteractionRegion>();InteractionLayout.Observer=regions.Add;
                try{using(var image=new Bitmap(page.Size.Width,page.Size.Height))using(var g=Graphics.FromImage(image))CompactRenderer.PaintWireless(g,owner,page,false,new PanelFeedback());}finally{InteractionLayout.Observer=null;}
                foreach(var control in hit)if(control.Id>=0&&!control.Information){var region=regions.Find(delegate(InteractionRegion r){return r.Id==control.Id;});Check(region!=null&&region.Limit==control.Rect,"paged tile, control and footer hits equal their drawing limits");Check(control.Rect.Bottom<=page.Size.Height,"all page hit rectangles fit the computed height");}
            }
        }}
    }
    static void GridGroupsAndHidden(){
        foreach(string font in new[]{"normal","large"}){Mode(font,"ocean",false,"grid");using(var fixture=new Fixture()){
            var owner=fixture.Owner;owner.GenericReady=true;owner.Dpi=Dpi(8);owner.WirelessDevices=new[]{GridDevice(0,5),GridDevice(1,8),GridDevice(2)};
            var pref=DeviceSettings.Current;pref.DeviceOrder=new List<string>{"grid-1","native:headset","grid-0","native:mouse","grid-2"};DeviceSettings.UseTransientConfiguration(pref);
            var layout=WirelessLayout.Build(owner,0,10000);Check(layout.Grid.Groups.Count==4&&layout.Grid.Groups[0].Card.NativeMouse&&layout.Grid.Groups[1].Card.Id=="grid-1"&&layout.Grid.Groups[2].Card.Id=="grid-0"&&layout.Grid.Groups[3].Audio,"fixed controls put native DPI first, other mice in device order, then audio");
            foreach(var group in layout.Grid.Groups){Check(group.Title.Bottom<=group.Track.Top,"non-focusable group heading precedes its controls");if(group.Count>4){Check(GridLayout.Segment(group,4).Top-GridLayout.Segment(group,0).Bottom==2,"more than four DPI values wrap with a two-pixel gutter");Check(group.Track.Contains(GridLayout.Segment(group,group.Count-1)),"last stage stays within its group track");}}
            var before=layout.Grid.Controls;var bounded=WirelessLayout.Build(owner,2,GridLayout.TileHeight+8+(int)before.Height+VisualDesign.FooterHeight);var first=WirelessLayout.Build(owner,0,bounded.Size.Height);Check(bounded.Grid.Controls==first.Grid.Controls,"all DPI groups stay fixed even when their tiles are off-page");
            var controls=PopupPages.Build(owner,new PopupNavigation(),true,bounded);Check(controls.Exists(delegate(PopupControl c){return c.DpiValue>0&&c.DeviceKey=="grid-0";})&&controls.Exists(delegate(PopupControl c){return c.Id==10;}),"off-page mice keep every DPI control");Check(!controls.Exists(delegate(PopupControl c){return c.Information&&c.Id>=0;}),"headings cannot be focused");
            var regions=new List<InteractionRegion>();InteractionLayout.Observer=regions.Add;try{using(var image=new Bitmap(bounded.Size.Width,bounded.Size.Height))using(var g=Graphics.FromImage(image))CompactRenderer.PaintWireless(g,owner,bounded,false,new PanelFeedback());}finally{InteractionLayout.Observer=null;}foreach(var control in controls)if(control.DpiValue>0||control.Id>=10&&control.Id<18){var region=regions.Find(delegate(InteractionRegion r){return r.Id==control.Id;});Check(region!=null&&region.Limit==control.Rect,"all off-page multi-mouse DPI groups retain identical paint and hit rectangles");}
            pref=DeviceSettings.Current;pref.HiddenDeviceIds=new List<string>{"native:mouse","grid-1","native:headset"};DeviceSettings.UseTransientConfiguration(pref);layout=WirelessLayout.Build(owner,0,10000);Check(layout.All.Count==2&&layout.Grid.Groups.Count==1&&layout.Grid.Groups[0].Card.Id=="grid-0","hidden tiles and their DPI/audio groups disappear together");
            controls=PopupPages.Build(owner,new PopupNavigation(),false,layout);Check(!controls.Exists(delegate(PopupControl c){return c.DeviceKey=="native:mouse"||c.DeviceKey=="grid-1"||c.Id>=100&&c.Id<=102;}),"hidden devices leave no input targets");
            owner.WirelessDevices=new[]{GridDevice(2)};layout=WirelessLayout.Build(owner,0,10000);Check(layout.Grid.Groups.Count==0&&layout.VolumeTop==layout.Grid.Controls.Top,"no DPI and no audio puts volume at the top without headings");
        }}
    }
    static void GridTypographyAndStates(){
        foreach(string font in new[]{"normal","large"})foreach(float scale in new[]{1f,1.25f,1.5f,1.75f,2f}){Mode(font,"ocean",false,"grid");using(var fixture=new Fixture())using(var host=new Control()){
            var owner=fixture.Owner;owner.NativeMousePresent=false;owner.Headset=null;owner.GenericReady=true;var handle=host.Handle;
            foreach(bool wide in new[]{false,true})foreach(int? percent in new int?[]{0,1,2,3,50,100,null})foreach(int state in new[]{0,1,2,3}){
                var device=GridDevice(0);device.Name="非常长的设备名称用于验证单行省略";device.Percent=percent;device.Approximate=percent==100;device.Fresh=state!=1;device.Online=state!=1;device.ConnectionState=state==2?"sleeping":state==1?"disconnected":"connected";device.MeasurementState=state==2?"sleeping":"current";device.ConfirmedAtUtc=(state==1?DateTime.UtcNow.AddHours(-2):DateTime.UtcNow).ToString("o");device.Charging=state==3;device.ChargingCheckedAtUtc=DateTime.UtcNow.ToString("o");owner.WirelessDevices=wide?new[]{device}:new[]{device,GridDevice(1)};
                var layout=WirelessLayout.Build(owner,0,10000);var tile=layout.Cards[0].Tile;var view=layout.Cards[0].Battery;bool sleeping=state==2;string status=GridLayout.Status(view,sleeping);Check(status==(sleeping?"休眠":state==1&&percent.HasValue?"上次":state==3?"充电":percent.HasValue&&percent<20?"电量低":null),"labels reflect only explicit charging/sleep and fresh/low state");
                var labels=new List<DrawnLabel>();var chevrons=new List<RectangleF>();CompactRenderer.LabelObserver=labels.Add;CompactRenderer.SymbolObserver=delegate(string icon,RectangleF rect){if(icon=="chevron")chevrons.Add(rect);};
                try{using(var image=Render(owner,true,false,new PanelFeedback {HoverAction=1000},scale)){if(sleeping&&percent<20&&scale==1){var palette=ThemePalette.Current(false);Check(image.GetPixel((int)tile.Left+5,(int)(tile.Top+tile.Height/2)).ToArgb()==ThemePalette.Blend(palette.Card,palette.SegmentHover,palette.SegmentHover.A).ToArgb(),"sleeping retains only the requested hover fill, never an old low-reading tint");}} }finally{CompactRenderer.LabelObserver=null;CompactRenderer.SymbolObserver=null;}
                string text=sleeping?"休眠":percent.HasValue?(percent==100?"≈":"")+percent.Value:"--";var number=labels.Find(delegate(DrawnLabel label){return label.Text==text;});Check(number!=null&&number.FontSize==(sleeping||!percent.HasValue?13:font=="large"?30:26),"grid keeps 26/30px digits and the existing 13px placeholders");
                var title=labels.Find(delegate(DrawnLabel label){return label.Text==device.Name;});Check(title!=null&&tile.Contains(title.LogicalRect),"device names stay in their tile");
                using(var image=new Bitmap(1,1))using(var g=Graphics.FromImage(image)){g.ScaleTransform(scale,scale);var ink=InteractionLayout.LabelBounds(g,text,number.LogicalRect,number.FontSize,true,number.Alignment,StringTrimming.None);Check(ink.Left>=tile.Left+GridLayout.Inset-1&&ink.Right<=tile.Right-GridLayout.Inset+1,"approximate full readings fit both widths at every monitor scale");if(wide)Check(title.LogicalRect.Right<=ink.Left-4,"wide tile name truncates before its number");
                    if(!sleeping&&percent.HasValue){var percentLabel=labels.Find(delegate(DrawnLabel label){return label.Text=="%";});var percentInk=InteractionLayout.LabelBounds(g,"%",percentLabel.LogicalRect,percentLabel.FontSize,true,StringAlignment.Near,StringTrimming.None);Check(percentInk.Right<=tile.Right-GridLayout.Inset+1&&Math.Abs(ink.Bottom-percentInk.Bottom)<=2,"grid percent stays inside and shares the number baseline");}
                }
                Check(chevrons.Count==1&&chevrons[0]==GridLayout.Chevron(tile,status),"only the hovered tile reveals its shared 7x10 detail arrow");
                var rows=PopupPages.Build(owner,new PopupNavigation(),false,layout);var control=rows.Find(delegate(PopupControl c){return c.DeviceKey==device.Id&&c.Id==1000;});var tree=new PopupAccessibility(host,delegate{return rows;},delegate{return -1;},null,delegate{return scale;},null,null);string name=tree.GetChild(rows.IndexOf(control)).Name;Check(name.Contains("键盘")&&name.Contains(device.Name)&&name.Contains("打开详情")&&(sleeping?name.Contains("休眠")&&!name.Contains("85%"):name.Contains(percent.HasValue?percent+"%":"--")),"accessible tile includes type, device, reading/state and direct detail action");
            }
        }}
    }
    static void GridFeedbackAndControls(){
        foreach(string font in new[]{"normal","large"})foreach(bool contrast in new[]{false,true})foreach(bool dark in new[]{false,true}){Mode(font,"ocean",false,"grid");VisualDesign.HighContrastOverride=contrast;using(var fixture=new Fixture()){
            var owner=fixture.Owner;var labels=new List<DrawnLabel>();var pressedLabels=new List<DrawnLabel>();var arrows=new List<RectangleF>();var rings=new List<RectangleF>();CompactRenderer.LabelObserver=labels.Add;CompactRenderer.SymbolObserver=delegate(string symbol,RectangleF rect){if(symbol=="chevron")arrows.Add(rect);};CompactRenderer.InteractionOutlineObserver=rings.Add;
            try{
                using(var normal=Render(owner,false,dark,new PanelFeedback())){
                    Check(arrows.Count==0&&rings.Count==0,"unfocused tiles have no arrow or selected accent outline");CompactRenderer.LabelObserver=pressedLabels.Add;
                    using(var hover=Render(owner,false,dark,new PanelFeedback {HoverAction=2}))using(var press=Render(owner,false,dark,new PanelFeedback {PressedAction=2}))using(var focus=Render(owner,false,dark,new PanelFeedback {KeyboardFocus=2}))Check(Different(normal,hover)&&Different(normal,press)&&Different(normal,focus),"tiles show distinct hover, press and keyboard focus feedback");
                }
            }finally{CompactRenderer.LabelObserver=null;CompactRenderer.SymbolObserver=null;CompactRenderer.InteractionOutlineObserver=null;}
            // The second rendering is hover; the following press moves the same
            // content by one logical pixel before rasterization.
            var baseLabel=labels.Find(delegate(DrawnLabel label){return label.Text==DeviceSettings.Current.MouseAlias;});var moved=pressedLabels.FindAll(delegate(DrawnLabel label){return label.Text==DeviceSettings.Current.MouseAlias;});Check(moved.Count==3&&moved[1].DeviceRect.Y-baseLabel.DeviceRect.Y==1,"pressed tile contents move by exactly one logical pixel");
            owner.Audio.MicrophoneMuted=true;var layout=WirelessLayout.Build(owner,0,10000);var group=layout.Grid.Groups.Find(delegate(GridControlGroup item){return item.Audio;});var area=Rectangle.Round(GridLayout.Microphone(group));
            using(var muted=Render(owner,true,dark,new PanelFeedback()))using(var pending=Render(owner,true,dark,new PanelFeedback {MicrophonePending=true,ShowPendingHints=true}))if(!contrast)Check(RedPixels(muted,area)>0&&RedPixels(pending,area)==0,"grid microphone keeps existing confirmed and pending ink");
            var controls=PopupPages.Build(owner,new PopupNavigation(),true,layout);Check(controls.Find(delegate(PopupControl c){return c.Id==10;}).Text.StartsWith(DeviceSettings.Current.MouseAlias+" DPI")&&controls.Find(delegate(PopupControl c){return c.Id==102;}).Text.StartsWith("音源，"),"control accessible names include the device/group prefix");
            var feedback=new PanelFeedback {ShowPendingHints=true,PendingStage=1,PendingOutput="headset",MicrophonePending=true};labels.Clear();CompactRenderer.LabelObserver=labels.Add;try{using(var image=Render(owner,true,dark,feedback)){} }finally{CompactRenderer.LabelObserver=null;}Check(labels.Exists(delegate(DrawnLabel label){return label.Text=="800…";})&&labels.Exists(delegate(DrawnLabel label){return label.Text=="耳机…";}),"grid reuses existing native DPI and audio pending hints");
            owner.Dpi=null;owner.Headset=null;owner.Audio=null;using(var image=Render(owner,false,dark,new PanelFeedback()))Check(image.Size==VisualDesign.PanelSizeFor(false,null),"no DPI and no audio still renders a bounded volume card");
        }}VisualDesign.HighContrastOverride=false;
    }
    static AudioReading Audio(){return new AudioReading {TimestampUtc=DateTime.UtcNow,OperationSucceeded=true,ActiveOutput="speaker",ConsoleOutputId="speaker",SpeakerAvailable=true,SpeakerId="speaker",SpeakerName="扬声器",HeadsetAvailable=true,HeadsetId="headset",HeadsetName="耳机",MicrophoneAvailable=true,MicrophoneId="mic",MicrophoneName="麦克风",MicrophoneMuted=false,VolumeEndpointId="speaker",OutputVolumePercent=55,Devices=new AudioDeviceChoice[0]};}
    static HeadsetReading Headset(){return new HeadsetReading {Connected=true,ConnectionVerified=true,ReceiverPresent=true,Percent=46,ConnectionTimestampUtc=DateTime.UtcNow,BatteryTimestampUtc=DateTime.UtcNow};}
    sealed class Fixture:IDisposable {
        internal readonly TrayContext Owner;
        internal Fixture(){
            Owner=new TrayContext(false,delegate(bool battery){return new MouseSnapshot {Battery=new BatteryReading {Percent=85,TimestampUtc=DateTime.UtcNow},Dpi=Dpi()};},delegate(int stage){throw new Exception("No device writes");},delegate{return new HeadsetReading();},Audio,false,delegate(string id){throw new Exception("No audio writes");},delegate(bool muted){throw new Exception("No audio writes");},delegate(string id,int value){throw new Exception("No audio writes");},delegate{return false;});
            DateTime deadline=DateTime.UtcNow.AddSeconds(5);while((Owner.IsRefreshing||Owner.IsAudioBusy||Owner.IsHeadsetRefreshing||Owner.Audio==null)&&DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(2);}
            Check(!Owner.IsRefreshing&&!Owner.IsAudioBusy&&Owner.Audio!=null,"injected readers settle");
            ((System.Windows.Forms.Timer)typeof(TrayContext).GetField("refresh",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(Owner)).Stop();
            Owner.NativeMousePresent=true;Owner.Current=new BatteryReading {Percent=85,TimestampUtc=DateTime.UtcNow};Owner.Dpi=Dpi();Owner.Audio=Audio();Owner.Headset=Headset();Owner.GenericReady=false;Owner.WirelessDevices=new WirelessDevice[0];
        }
        public void Dispose(){Owner.ExitThread();Owner.Dispose();}
    }
    static Bitmap Render(TrayContext owner,bool wireless,bool dark,PanelFeedback feedback,float scale=1){
        var layout=wireless?WirelessLayout.Build(owner,0,10000):null;var logical=wireless?layout.Size:VisualDesign.PanelSizeFor(VisualDesign.HasAudioCard(owner.Headset,owner.Audio),owner.Dpi);var physical=UiState.ScaleSize(logical,scale);
        var image=new Bitmap(physical.Width,physical.Height,PixelFormat.Format32bppPArgb);
        using(var g=Graphics.FromImage(image)){g.Clear(Color.Transparent);g.ScaleTransform(scale,scale);if(wireless)CompactRenderer.PaintWireless(g,owner,layout,dark,feedback);else CompactRenderer.Paint(g,owner.Current,owner.LastGood,null,false,owner.Dpi,null,false,dark,owner.Headset,owner.Audio,1,-1,false,1,-1,feedback);}
        return image;
    }
    static Bitmap OpeningFrame(TrayContext owner,bool wireless,bool dark,float progress){
        var layout=WirelessLayout.Build(owner,0,10000);var size=wireless?layout.Size:VisualDesign.PanelSizeFor(VisualDesign.HasAudioCard(owner.Headset,owner.Audio),owner.Dpi);
        var image=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppPArgb);
        try{using(var g=Graphics.FromImage(image)){
            var method=typeof(CompactRenderer).GetMethod(wireless?"PaintWireless":"Paint",Static);var parameters=method.GetParameters();
            Check(parameters[parameters.Length-1].Name=="batteryProgress","both render paths accept independent opening fill progress");
            object[] args=wireless?new object[]{g,owner,layout,dark,new PanelFeedback(),progress}:new object[]{g,owner.Current,owner.LastGood,null,false,owner.Dpi,null,false,dark,owner.Headset,owner.Audio,1f,-1,false,1f,-1,new PanelFeedback(),false,progress};
            method.Invoke(null,args);
        }return image;}catch{image.Dispose();throw;}
    }
    static void OpeningFill(){
        foreach(bool reduced in new[]{false,true})foreach(float elapsed in new[]{-10f,0f,60f,120f,190f,240f,500f}){
            float actual=(float)Geometry("BatteryOpeningProgress",elapsed,reduced),t=Math.Max(0,Math.Min(1,elapsed/240f));
            Check(Math.Abs(actual-(reduced?1:1-(float)Math.Pow(1-t,3)))<.0001f,"opening fill uses the specified 240ms ease-out and reduced-motion final state");
        }
        foreach(string layout in PanelLayouts.Keys)foreach(string font in new[]{"normal","large"})foreach(bool dark in new[]{false,true})foreach(bool contrast in new[]{false,true}){
            Mode(font,"ocean",false,layout);var pref=DeviceSettings.Current;pref.ReducedMotion=false;DeviceSettings.UseTransientConfiguration(pref);VisualDesign.HighContrastOverride=contrast;
            using(var fixture=new Fixture())foreach(bool wireless in new[]{false,true}){
                var owner=fixture.Owner;owner.GenericReady=wireless;
                owner.WirelessDevices=new[]{new WirelessDevice {Id="opening-sleep",Name="注入休眠",Kind="keyboard",Percent=85,Fresh=true,Online=true,ConnectionState="sleeping",ConfirmedAtUtc=DateTime.UtcNow.ToString("o")}};
                foreach(int? percent in new int?[]{0,1,2,3,50,100,null})foreach(bool fresh in new[]{false,true}){
                    owner.Current=percent.HasValue?new BatteryReading {Percent=percent.Value,Charging=percent==50,TimestampUtc=fresh?DateTime.UtcNow:DateTime.UtcNow.AddHours(-2)}:null;owner.LastGood=null;
                    var labels=new List<DrawnLabel>();CompactRenderer.LabelObserver=labels.Add;
                    try{using(var start=OpeningFrame(owner,wireless,dark,0))using(var middle=OpeningFrame(owner,wireless,dark,.875f))using(var end=OpeningFrame(owner,wireless,dark,1)){
                        Check(start.Size==end.Size&&middle.Size==end.Size,"opening never changes computed panel height");
                        Rectangle area=layout=="ring"?Rectangle.Ceiling(VisualDesign.BatteryRingRect(0)):layout=="grid"?Rectangle.Ceiling(GridLayout.Bar((wireless?WirelessLayout.Build(owner,0,10000):GridLayout.Native(owner.Current,null,owner.Dpi,owner.Headset,owner.Audio)).Cards[0].Tile)):Rectangle.Ceiling(VisualDesign.BatteryBarRect(0));
                        if(!PopupPages.Reduced&&percent.GetValueOrDefault()>0){Check(!SameArea(start,end,area),"opening start has no battery fill");if(percent>=50)Check(!SameArea(start,middle,area)&&!SameArea(middle,end,area),"opening has a distinct intermediate and final fill");}
                        Check(labels.Count%3==0,"opening frames draw the same number of labels");int count=labels.Count/3;
                        for(int i=0;i<count;i++)Check(labels[i].Text==labels[count+i].Text&&labels[i].Text==labels[2*count+i].Text,"numbers and state labels keep real values throughout the animation");
                    }}finally{CompactRenderer.LabelObserver=null;}
                }
                pref=DeviceSettings.Current;pref.ReducedMotion=true;DeviceSettings.UseTransientConfiguration(pref);
                using(var start=OpeningFrame(owner,wireless,dark,0))using(var end=OpeningFrame(owner,wireless,dark,1))Check(!Different(start,end),"reduced motion paints the final state immediately on both paths");
                pref.ReducedMotion=false;DeviceSettings.UseTransientConfiguration(pref);
            }
        }
        VisualDesign.HighContrastOverride=false;
    }
    static void BarGeometry(){
        foreach(string font in new[]{"normal","large"}){Mode(font);var track=Rect("BatteryBarRect",0f);Check(track==new RectangleF(12,VisualDesign.LargeFont?56:50,VisualDesign.PanelWidth-24,4),"4px battery track uses the specified header and side insets");
            foreach(int? percent in new int?[]{0,1,2,3,50,100,null})foreach(bool fresh in new[]{false,true})foreach(bool approximate in new[]{false,true}){
                var value=new BatteryView {Percent=percent,Fresh=fresh,Approximate=approximate};var fill=Rect("BatteryFillRect",0f,value);float expected=track.Width*percent.GetValueOrDefault()/100f;
                Check(Math.Abs(fill.Width-expected)<.001f,"battery fill is proportional, independent of freshness and approximation");Check(fill.Width==0||track.Contains(fill),"battery fill never leaves its track");
            }
            foreach(int percent in new[]{-20,120}){var fill=Rect("BatteryFillRect",0f,new BatteryView {Percent=percent});Check(fill.Width>=0&&fill.Width<=track.Width,"out-of-range defensive clamping");}
        }
    }
    static void BatteryTypography(){
        foreach(string font in new[]{"normal","large"})foreach(float scale in new[]{1f,1.25f,1.5f,1.75f,2f}){
            Mode(font);Check(VisualDesign.BatteryDigitSize==(font=="large"?22:19),"battery type size follows the brief");
            using(var image=new Bitmap((int)(VisualDesign.PanelWidth*scale),(int)(VisualDesign.GenericCardHeight*scale)))using(var g=Graphics.FromImage(image)){
                g.ScaleTransform(scale,scale);g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                foreach(var view in new[]{new BatteryView {Percent=100,Fresh=true,Approximate=true,Charging=true},new BatteryView {Percent=0,Fresh=true},new BatteryView {Percent=1,Fresh=true},new BatteryView {Percent=2,Fresh=true},new BatteryView {Percent=3,Fresh=true},new BatteryView {Percent=50,Fresh=true,Charging=true},new BatteryView {Percent=100,Fresh=false,Unconfirmed=true},new BatteryView(),new BatteryView {Fresh=true}}){
                    g.Clear(Color.Transparent);
                    var labels=new List<DrawnLabel>();var bolts=new List<RectangleF>();CompactRenderer.LabelObserver=labels.Add;CompactRenderer.SymbolObserver=delegate(string symbol,RectangleF rect){if(symbol=="bolt")bolts.Add(rect);};
                    try{typeof(CompactRenderer).GetMethod("Meter",Static).Invoke(null,new object[]{g,view,0f,(float)VisualDesign.GenericCardHeight,false,new PanelFeedback(),4,1f});}finally{CompactRenderer.LabelObserver=null;CompactRenderer.SymbolObserver=null;}
                    string text=view.Percent.HasValue?(view.Approximate?"≈":"")+view.Percent.Value:"--";var digits=labels.Find(delegate(DrawnLabel label){return label.Text==text;});
                    Check(digits!=null&&digits.Bold&&digits.FontSize==(view.Percent.HasValue?VisualDesign.BatteryDigitSize:13),"full sized digits and the smaller existing no-reading placeholder are drawn");
                    var digitInk=InteractionLayout.LabelBounds(g,text,digits.LogicalRect,digits.FontSize,true,StringAlignment.Far,System.Drawing.StringTrimming.None);
                    Check(digitInk.Left>=digits.LogicalRect.Left&&digitInk.Right<=VisualDesign.PanelWidth-12,"even charging approximate 100 fits the right inset");
                    if(!view.Percent.HasValue&&scale==1)Check(InkMatches(image,Rectangle.Round(VisualDesign.HeaderMeterHitRect(0)),ThemePalette.Current(false).Muted),"no-reading placeholder keeps secondary ink even when the provider marks the connection fresh");
                    Check(bolts.Count==(view.Charging?1:0),"charging glyph requires explicit charging data");
                    if(view.Charging){var bolt=bolts[0];Check(bolt.Size==new SizeF(10,13)&&Math.Abs(digitInk.Left-bolt.Right-4)<.01f,"charging glyph is 10x13 and four pixels before the actual number");Check(bolt.Left>=VisualDesign.MouseNameRect.Right,"charging approximate 100 never covers the device name");}
                    if(view.Percent.HasValue){var percent=labels.Find(delegate(DrawnLabel label){return label.Text=="%";});Check(percent!=null&&percent.Bold&&percent.FontSize==(font=="large"?11:10),"percent is 10/11px semibold text");
                        var percentInk=InteractionLayout.LabelBounds(g,"%",percent.LogicalRect,percent.FontSize,true,StringAlignment.Near,System.Drawing.StringTrimming.None);Check(percentInk.Right<=VisualDesign.PanelWidth-12&&percentInk.Left-digitInk.Right>=0&&percentInk.Left-digitInk.Right<=3,"percent is adjacent and within the right inset: "+font+" / "+scale+" / "+text+" digits="+digitInk+" percent="+percentInk);
                        Check(Math.Abs(percentInk.Bottom-digitInk.Bottom)<=2,"percent and digits share their baseline");
                    }
                }
            }
        }
    }
    static void HitGeometry(){HitGeometry("bar");}
    static void HitGeometry(string panelLayout){
        foreach(string font in new[]{"normal","large"})foreach(bool wireless in new[]{false,true})foreach(int count in new[]{4,5,8})foreach(bool collapsed in new[]{false,true})foreach(int audioCard in new[]{0,1,2}){
            Mode(font,"ocean",collapsed,panelLayout);using(var fixture=new Fixture()){
                var owner=fixture.Owner;owner.Dpi=Dpi(count);owner.GenericReady=wireless;
                if(audioCard!=1)owner.Headset=null;
                if(audioCard==2)owner.Audio.Devices=new[]{new AudioDeviceChoice {Id="headset",Name="耳机",Flow=0,FormFactor=3}};
                if(wireless){var stages=new int[count];for(int i=0;i<count;i++)stages[i]=400*(i+1);owner.WirelessDevices=new[]{new WirelessDevice {Id="dpi-mouse-fixture",Name="通用 DPI 鼠标",Kind="mouse",Percent=80,Fresh=true,Online=true,ConnectionState="connected",ConfirmedAtUtc=DateTime.UtcNow.ToString("o"),Dpi=new WirelessDpi {Supported=true,CanSet=true,Current=800,Stages=stages,TargetToken=new string('a',64),CheckedAtUtc=DateTime.UtcNow.ToString("o")}}};}
                var layout=WirelessLayout.Build(owner,0,10000);var rows=PopupPages.Build(owner,new PopupNavigation(),VisualDesign.ShouldShowHeadset(owner.Headset),wireless?layout:null);var regions=new List<InteractionRegion>();InteractionLayout.Observer=regions.Add;
                try{using(var image=Render(owner,wireless,false,new PanelFeedback())){}}finally{InteractionLayout.Observer=null;}
                foreach(var row in rows){if(!(row.Id>=2&&row.Id<=5||row.Id>=10&&row.Id<18||row.Id>=100&&row.Id<=102||row.Id==200||row.Id==240||row.Id==241||row.Id==0||row.Id==1||row.DpiValue>0||row.Id>=1000&&row.Id<3000))continue;
                    var region=regions.Find(delegate(InteractionRegion item){return item.Id==row.Id;});Check(region!=null,"drawn control exists for hit action "+row.Id);var drawn=region.Limit;if(panelLayout=="bar"&&(row.Id==3||row.Id==5||row.Id==200||row.Id>=100&&row.Id<=102))drawn.Y+=wireless?layout.Cards.Find(delegate(WirelessCard card){return card.NativeHeadset||card.AudioOnly;}).Top:VisualDesign.MouseCardHeight(owner.Dpi)+VisualDesign.CardGap;
                    Check(drawn==row.Rect,"draw and hit rectangles are identical for "+row.Id+" / "+font+" / "+wireless+": "+drawn+" != "+row.Rect);
                    if(row.Enabled)Check(PopupPages.Hit(rows,new PointF(row.Rect.Left+row.Rect.Width/2,row.Rect.Top+row.Rect.Height/2))==row,"center hit resolves the drawn action");
                }
                for(int i=0;i<rows.Count;i++)for(int j=i+1;j<rows.Count;j++)Check(!rows[i].Rect.IntersectsWith(rows[j].Rect),"main control rectangles remain disjoint");
                if(count>4){Check(VisualDesign.StageRect(4,count).Top-VisualDesign.StageRect(0,count).Bottom==2,"DPI rows have exactly two pixels between segments");Check(VisualDesign.StageTrackRect(0,count).Contains(VisualDesign.StageRect(count-1,count)),"last DPI stage stays within its shared track");}
            }
        }
    }
    static void RenderMatrix(){RenderMatrix("bar");}
    static void RenderMatrix(string panelLayout){
        foreach(string font in new[]{"normal","large"})foreach(string style in ThemePalette.Keys)foreach(bool dark in new[]{false,true})foreach(bool headset in new[]{false,true})foreach(bool contrast in new[]{false,true}){
            VisualDesign.HighContrastOverride=contrast;
            Mode(font,style,false,panelLayout);using(var fixture=new Fixture()){
                var owner=fixture.Owner;if(!headset)owner.Headset=null;
                foreach(bool wireless in new[]{false,true}){owner.GenericReady=wireless;using(var image=Render(owner,wireless,dark,new PanelFeedback())){
                    var expected=VisualDesign.PanelSizeFor(headset,owner.Dpi);Check(image.Size==expected,"all theme/font/headset matrices use the shared panel height");
                }}
                using(var native=Render(owner,false,dark,new PanelFeedback()))using(var wireless=Render(owner,true,dark,new PanelFeedback()))Check(SameArea(native,wireless,new Rectangle(0,0,native.Width,(int)VisualDesign.FooterRectFor(headset,owner.Dpi).Top)),"both paths paint identical native device and volume cards");
                owner.GenericReady=true;
                foreach(int? percent in new int?[]{0,1,2,3,50,100,null})foreach(bool fresh in new[]{false,true}){
                    owner.WirelessDevices=new[]{new WirelessDevice {Id="keyboard-fixture",Name="非常长的设备名称用于验证省略而不是覆盖电量",Kind="keyboard",Percent=percent,Approximate=percent==100,Fresh=fresh,Online=fresh,ConnectionState=fresh?"connected":"unconfirmed",ConfirmedAtUtc=(fresh?DateTime.UtcNow:DateTime.UtcNow.AddHours(-2)).ToString("o"),Charging=fresh&&percent==50,ChargingCheckedAtUtc=DateTime.UtcNow.ToString("o")}};
                    using(var image=Render(owner,true,dark,new PanelFeedback()))Check(image.Size==WirelessLayout.Build(owner,0,10000).Size,"generic boundary, cached, no-reading and charging cards render with the same battery geometry");
                }
            }
        }
        VisualDesign.HighContrastOverride=false;
    }
    static Bitmap Gauge(BatteryView view,bool dark,bool sleeping=false){var bitmap=new Bitmap(VisualDesign.PanelWidth,VisualDesign.HeaderHeight);using(var g=Graphics.FromImage(bitmap)){g.Clear(ThemePalette.Current(dark).Card);g.SmoothingMode=SmoothingMode.AntiAlias;CompactRenderer.RingGauge(g,view,"mouse",0,dark,sleeping);}return bitmap;}
    static void RingBoundaries(){
        foreach(string font in new[]{"normal","large"})foreach(bool dark in new[]{false,true})foreach(bool contrast in new[]{false,true}){
            Mode(font,"ocean",false,"ring");VisualDesign.HighContrastOverride=contrast;var bounds=Rectangle.Round(VisualDesign.BatteryRingRect(0));
            using(var zero=Gauge(new BatteryView {Percent=0,Fresh=true},dark))using(var one=Gauge(new BatteryView {Percent=1,Fresh=true},dark))using(var two=Gauge(new BatteryView {Percent=2,Fresh=true},dark))using(var three=Gauge(new BatteryView {Percent=3,Fresh=true},dark))using(var full=Gauge(new BatteryView {Percent=100,Fresh=true},dark)){
                Color ink=contrast?SystemColors.Highlight:ThemePalette.LevelColor(1,dark);Check(Different(zero,one)&&!Different(one,two)&&!Different(two,three),"one to three percent has a visible, stable single dot");Check(ColorPixels(one,bounds,ink)>0,"tiny ring progress uses level or system highlight ink");
                Check(ColorPixels(full,bounds,contrast?SystemColors.Highlight:ThemePalette.LevelColor(100,dark))>ColorPixels(one,bounds,ink)*10,"100 percent is a complete ring");
            }
            using(var empty=Gauge(new BatteryView(),dark))using(var sleeping=Gauge(new BatteryView {Percent=85,Fresh=true,Charging=true},dark,true))Check(!Different(empty,sleeping),"sleeping paints only the base ring and muted icon, never the old value or charging badge");
            if(!contrast)using(var stale=Gauge(new BatteryView {Percent=50,Fresh=false},dark))Check(ColorPixels(stale,bounds,ThemePalette.Current(dark).Muted)>0,"cached progress uses secondary ink");
            var bolts=new List<RectangleF>();CompactRenderer.SymbolObserver=delegate(string icon,RectangleF rect){if(icon=="bolt")bolts.Add(rect);};try{using(var charged=Gauge(new BatteryView {Percent=50,Fresh=true,Charging=true},dark)){} }finally{CompactRenderer.SymbolObserver=null;}
            Check(bolts.Count==1&&VisualDesign.RingChargeBadgeRect(0).Size==new SizeF(12,12)&&VisualDesign.RingChargeBadgeRect(0).Contains(bolts[0]),"explicit charging has a twelve-pixel tangent badge");
        }
        VisualDesign.HighContrastOverride=false;
    }
    static void RingNamesAndTypography(){
        foreach(string font in new[]{"normal","large"})foreach(float scale in new[]{1f,1.25f,1.5f,1.75f,2f}){
            Mode(font,"ocean",false,"ring");using(var fixture=new Fixture())using(var host=new Control()){
                var owner=fixture.Owner;owner.GenericReady=true;owner.NativeMousePresent=false;owner.Headset=null;var handle=host.Handle;
                foreach(int? percent in new int?[]{0,1,2,3,50,100,null})foreach(bool fresh in new[]{false,true})foreach(bool sleeping in new[]{false,true}){
                    var device=new WirelessDevice {Id="ring-boundary",Name="非常长的设备名称用于验证省略而不是覆盖读数",Kind="mouse",Percent=percent,Fresh=fresh,Online=fresh,Approximate=percent==100,ConnectionState=sleeping?"sleeping":fresh?"connected":"unconfirmed",MeasurementState=sleeping?"sleeping":"current",ConfirmedAtUtc=DateTime.UtcNow.ToString("o"),Charging=fresh&&percent==100,ChargingCheckedAtUtc=DateTime.UtcNow.ToString("o")};owner.WirelessDevices=new[]{device};
                    var labels=new List<DrawnLabel>();var icons=new List<RectangleF>();CompactRenderer.LabelObserver=labels.Add;CompactRenderer.SymbolObserver=delegate(string icon,RectangleF rect){if(icon=="chevron")icons.Add(rect);};
                    try{using(var image=Render(owner,true,false,new PanelFeedback {HoverAction=1000},scale)){} }finally{CompactRenderer.LabelObserver=null;CompactRenderer.SymbolObserver=null;}
                    var rows=PopupPages.Build(owner,new PopupNavigation(),false);var row=rows.Find(delegate(PopupControl item){return item.Id==1000;});var tree=new PopupAccessibility(host,delegate{return rows;},delegate{return -1;},null,delegate{return 1f;},null,null);string name=tree.GetChild(rows.IndexOf(row)).Name;
                    string expectedState=sleeping?"休眠":!percent.HasValue?"--":fresh&&percent==100?"充电中":!fresh?"待确认":fresh&&percent<20?"电量低":"%";
                    Check(name.Contains(device.Name)&&name.Contains("打开详情")&&name.Contains(expectedState),"ring spoken name includes battery state: "+name);
                    string text=sleeping?"休眠":percent.HasValue?(percent==100?"≈":"")+percent.Value:"--";var number=labels.Find(delegate(DrawnLabel label){return label.Text==text;});Check(number!=null,"ring number or placeholder is visible");
                    using(var bitmap=new Bitmap(1,1))using(var g=Graphics.FromImage(bitmap)){
                        g.ScaleTransform(scale,scale);var ink=InteractionLayout.LabelBounds(g,text,number.LogicalRect,number.FontSize,true,StringAlignment.Far,StringTrimming.None);Check(ink.Right<=VisualDesign.PanelWidth-12+.01f,"ring text stays inside the right padding");
                        var title=labels.Find(delegate(DrawnLabel label){return label.Text==device.Name;});Check(title!=null&&title.LogicalRect.Right<=icons[0].Left,"name truncates before the revealed chevron and number");
                    }
                    Check(icons.Count==1&&icons[0].Size==new SizeF(7,10),"detail chevron appears on hover only");
                }
            }
        }
    }
    static void ContrastAndShadows(){
        foreach(string style in ThemePalette.Keys)foreach(bool dark in new[]{false,true}){
            Mode("normal",style);var palette=ThemePalette.Current(dark);Check(Role(palette,"PanelBorder").A==(dark?18:15),"card border is a subtle seven/six percent line");Check(Role(palette,"PanelShadowNear").A>0&&Role(palette,"PanelShadowFar").A>0,"custom shadows exist only for ordinary themes");
            using(var image=new Bitmap(260,VisualDesign.GenericCardHeight+8))using(var g=Graphics.FromImage(image)){
                g.Clear(Color.Transparent);g.SmoothingMode=SmoothingMode.AntiAlias;var method=typeof(CompactRenderer).GetMethod("DrawPanelCard",Static);Check(method!=null,"main-only card surface preserves dialog surfaces");method.Invoke(null,new object[]{g,0f,(float)VisualDesign.GenericCardHeight,dark,null});
                Check(image.GetPixel(100,VisualDesign.GenericCardHeight-1).A>0,"downward shadow survives inside the card slot");Check(image.GetPixel(0,VisualDesign.GenericCardHeight).A==0&&image.GetPixel(100,VisualDesign.GenericCardHeight).A==0,"shadow does not spill into the next card or outside the panel");
            }
        }
        VisualDesign.HighContrastOverride=true;
        try{foreach(string style in ThemePalette.Keys)foreach(bool dark in new[]{false,true}){
            Mode("normal",style);var palette=ThemePalette.Current(dark);Check(Role(palette,"PanelShadowNear").A==0&&Role(palette,"PanelShadowFar").A==0,"high contrast never uses custom shadow colors");Check(Role(palette,"PanelBorder")==SystemColors.WindowText,"high contrast retains the solid system border");using(var fixture=new Fixture())foreach(bool wireless in new[]{false,true})using(var image=Render(fixture.Owner,wireless,dark,new PanelFeedback {KeyboardFocus=102}))Check(image.Width==VisualDesign.PanelWidth,"high contrast rendering completes");
        }}finally{VisualDesign.HighContrastOverride=false;}
    }
    static void MicrophoneNames(){
        foreach(bool wireless in new[]{false,true}){Mode("normal");using(var fixture=new Fixture())using(var host=new Control()){
            var owner=fixture.Owner;owner.GenericReady=wireless;var handle=host.Handle;
            foreach(bool? muted in new bool?[]{true,false,null}){owner.Audio.MicrophoneMuted=muted;var rows=PopupPages.Build(owner,new PopupNavigation(),true);int index=rows.FindIndex(delegate(PopupControl row){return row.Id==102;});var tree=new PopupAccessibility(host,delegate{return rows;},delegate{return -1;},delegate(PopupControl row){throw new Exception("No invocation");},delegate{return 1f;},null,null);
                Check(tree.GetChild(index).Name.Contains(muted.HasValue?muted.Value?"已静音":"未静音":"未知"),"icon-only microphone keeps the original state in its accessible name: "+tree.GetChild(index).Name);
            }
            typeof(TrayContext).GetField("audioNeedsReconcile",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(owner,true);
            var blocked=PopupPages.Build(owner,new PopupNavigation(),true);Check(blocked.Find(delegate(PopupControl row){return row.Id==102;}).Text.Contains("待确认"),"blocked microphone keeps the pending state in its spoken name");
            typeof(TrayContext).GetField("audioNeedsReconcile",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(owner,false);
            typeof(TrayContext).GetField("pendingMicrophoneMute",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(owner,(bool?)true);
            var pending=PopupPages.Build(owner,new PopupNavigation(),true);Check(pending.Find(delegate(PopupControl row){return row.Id==102;}).Text.Contains("待确认"),"in-flight microphone keeps the pending state in its spoken name");
            typeof(TrayContext).GetField("pendingMicrophoneMute",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(owner,null);
            owner.Audio.MicrophoneMuted=true;owner.Audio.MicrophoneAvailable=false;
            var unavailable=PopupPages.Build(owner,new PopupNavigation(),true);var mic=unavailable.Find(delegate(PopupControl row){return row.Id==102;});
            Check(!mic.Enabled&&mic.Text.Contains("未知"),"unavailable microphones cannot announce an old mute state as current");
            var volume=unavailable.Find(delegate(PopupControl row){return row.Id==240;});Check(volume.Text.Contains(owner.Audio.SpeakerName),"system output device name is in the slider's spoken name");
        }}
    }
    static bool SameArea(Bitmap a,Bitmap b,Rectangle area){for(int y=area.Top;y<area.Bottom;y++)for(int x=area.Left;x<area.Right;x++)if(a.GetPixel(x,y)!=b.GetPixel(x,y))return false;return true;}
    static int ColorPixels(Bitmap image,Rectangle area,Color color){int result=0;for(int y=area.Top;y<area.Bottom;y++)for(int x=area.Left;x<area.Right;x++)if(image.GetPixel(x,y).ToArgb()==color.ToArgb())result++;return result;}
    static bool InkMatches(Bitmap image,Rectangle area,Color expected){
        // A 13px hyphen can have at most 107/255 coverage. Compare premultiplied
        // channels within one quantization unit instead of requiring opaque ink.
        int count=0;for(int y=area.Top;y<area.Bottom;y++)for(int x=area.Left;x<area.Right;x++){var pixel=image.GetPixel(x,y);if(pixel.A<32)continue;count++;if(Math.Abs(pixel.R-expected.R)*pixel.A>255||Math.Abs(pixel.G-expected.G)*pixel.A>255||Math.Abs(pixel.B-expected.B)*pixel.A>255)throw new Exception("placeholder ink at "+x+","+y+" is "+pixel+", expected "+expected);}Check(count>0,"placeholder has visible ink in "+area);return true;
    }
    static bool Different(Bitmap a,Bitmap b){for(int y=0;y<a.Height;y++)for(int x=0;x<a.Width;x++)if(a.GetPixel(x,y)!=b.GetPixel(x,y))return true;return false;}
    static void MicrophonePresentation(){
        foreach(string font in new[]{"normal","large"})foreach(bool wireless in new[]{false,true})foreach(bool dark in new[]{false,true}){Mode(font);using(var fixture=new Fixture()){
            var owner=fixture.Owner;owner.Audio.MicrophoneMuted=true;var area=Rectangle.Round(VisualDesign.MicrophoneMuteRectFor(owner.Dpi));
            using(var muted=Render(owner,wireless,dark,new PanelFeedback()))using(var pending=Render(owner,wireless,dark,new PanelFeedback {MicrophonePending=true,ShowPendingHints=true})){
                Check(RedPixels(muted,area)>0&&RedPixels(pending,area)==0,"pending microphone icon uses secondary ink instead of a confirmed red mute state");
            }
            owner.Audio.MicrophoneAvailable=false;using(var unavailable=Render(owner,wireless,dark,new PanelFeedback()))Check(RedPixels(unavailable,area)==0,"unavailable microphone never paints an old mute state in red");
        }}
    }
    static int RedPixels(Bitmap image,Rectangle area){
        // The 14px icon has antialiased strokes; count red ink rather than requiring
        // a fully covered exact-color pixel. The faint red surface stays below 40.
        int result=0;for(int y=area.Top;y<area.Bottom;y++)for(int x=area.Left;x<area.Right;x++){var pixel=image.GetPixel(x,y);if(pixel.R-pixel.G>40&&pixel.R-pixel.B>40)result++;}return result;
    }
    static void FooterPaging(){FooterPaging("bar");}
    static void FooterPaging(string panelLayout){
        foreach(string font in new[]{"normal","large"}){Mode(font,"ocean",false,panelLayout);using(var fixture=new Fixture()){
            var owner=fixture.Owner;owner.NativeMousePresent=false;owner.Headset=null;owner.GenericReady=true;owner.WirelessDevices=new[]{new WirelessDevice {Id="one",Name="第一台",Kind="keyboard"},new WirelessDevice {Id="two",Name="第二台",Kind="mouse"},new WirelessDevice {Id="three",Name="第三台",Kind="gamepad"}};
            foreach(int offset in new[]{0,1}){
                int maximum=VisualDesign.GenericCardHeight+VisualDesign.DeviceGap+VisualDesign.SystemVolumeHeight+VisualDesign.MainFooterGap+VisualDesign.FooterHeight;var layout=WirelessLayout.Build(owner,offset,maximum);var rows=PopupPages.Build(owner,new PopupNavigation(),false,layout);var regions=new List<InteractionRegion>();InteractionLayout.Observer=regions.Add;
                try{using(var image=new Bitmap(layout.Size.Width,layout.Size.Height))using(var g=Graphics.FromImage(image))CompactRenderer.PaintWireless(g,owner,layout,false,new PanelFeedback());}finally{InteractionLayout.Observer=null;}
                Check(layout.Paged&&layout.Cards.Count==1,"bounded device pages reserve the volume and footer");
                Check(layout.Size.Height<=maximum,"paged device panel stays within its available height");
                foreach(int id in new[]{0,1,902,903}){var row=rows.Find(delegate(PopupControl item){return item.Id==id;});var region=regions.Find(delegate(InteractionRegion item){return item.Id==id;});Check(row!=null&&region!=null&&row.Rect==region.Limit&&row.Rect.Size==new SizeF(28,28),"pagination, refresh and more use their actual 28x28 drawing rectangles");}
            }
        }}
    }
    static void FloatingSegments(){
        foreach(string font in new[]{"normal","large"})foreach(string style in ThemePalette.Keys)foreach(bool dark in new[]{false,true}){
            Mode(font,style);var rect=new RectangleF(12,12,92,VisualDesign.SelectionHeight);var palette=ThemePalette.Current(dark);
            Func<bool,bool,Bitmap> render=delegate(bool hover,bool pressed){var image=new Bitmap(120,64);using(var g=Graphics.FromImage(image)){g.Clear(palette.Track);g.SmoothingMode=SmoothingMode.AntiAlias;CompactRenderer.Selection(g,rect,"800",null,true,true,hover,pressed,false,dark);}return image;};
            using(var normal=render(false,false))using(var hover=render(true,false))using(var pressed=render(false,true)){
                Check(normal.GetPixel(20,15).ToArgb()!=palette.Selected.ToArgb(),"selected thumb is neutral, not an accent slab");Check(Different(normal,hover)&&Different(normal,pressed),"selected segment has subtle hover and one-pixel press feedback");
            }
        }
    }
    static void PlainFooterAndVolume(){
        foreach(string font in new[]{"normal","large"}){Mode(font);using(var fixture=new Fixture()){
            var owner=fixture.Owner;var labels=new List<DrawnLabel>();CompactRenderer.LabelObserver=labels.Add;
            try{using(var image=Render(owner,false,false,new PanelFeedback())){
                var footer=VisualDesign.FooterRectFor(true,owner.Dpi);Check(image.GetPixel(45,(int)footer.Top+3).A==0,"footer has no input-like background");Check(!labels.Exists(delegate(DrawnLabel label){return label.Text.StartsWith("系统音量 ·");}),"main volume has no device-name row");
            }}finally{CompactRenderer.LabelObserver=null;}
            Check(VisualDesign.SystemVolumeHeight==(font=="large"?40:36)&&VisualDesign.SystemVolumeHeightFor(true)==(font=="large"?36:32),"expanded and collapsed volume heights follow the design");var mic=VisualDesign.MicrophoneMuteRectFor(owner.Dpi);Check(mic.Width==(font=="large"?38:34),"icon-only microphone is 34/38px wide");
            Check(VisualDesign.FooterRefreshRect(0).Size==new SizeF(28,28)&&VisualDesign.FooterMoreRect(0).Size==new SizeF(28,28),"footer icon buttons stay 28x28 in both fonts");
            var track=VisualDesign.MainVolumeTrack(VisualDesign.VolumeSliderRect(VisualDesign.SystemVolumeRectFor(true,owner.Dpi)));Check(track.Height==4,"system volume has a four-pixel track");
            foreach(int value in new[]{0,50,100}){
                owner.Audio.OutputVolumePercent=value;
                using(var normal=Render(owner,false,true,new PanelFeedback()))using(var hover=Render(owner,false,true,new PanelFeedback {HoverAction=240}))using(var drag=Render(owner,false,true,new PanelFeedback {PressedAction=240,VolumePreview=value})){
                    var knob=VisualDesign.VolumeKnobRect(track,value);var active=VisualDesign.VolumeKnobRect(track,value,true);Check(knob.Size==new SizeF(12,12)&&active.Size==new SizeF(14,14),"volume knobs grow from twelve to fourteen pixels");
                    var sample=new Rectangle((int)active.X-1,(int)active.Y-1,17,17);Color ink=ThemePalette.Current(true).Knob;Check(ColorPixels(hover,sample,ink)>ColorPixels(normal,sample,ink)&&ColorPixels(hover,sample,ink)==ColorPixels(drag,sample,ink),"real hover and drag both paint the larger knob at zero, mid and full volume");
                }
            }
            Console.WriteLine(font+" panel heights: no headset="+VisualDesign.PanelSizeFor(false,owner.Dpi).Height+", headset="+VisualDesign.PanelSizeFor(true,owner.Dpi).Height+", eight DPI="+VisualDesign.PanelSizeFor(false,Dpi(8)).Height+"/"+VisualDesign.PanelSizeFor(true,Dpi(8)).Height);
        }}
    }
    static void PendingSegments(){
        foreach(string font in new[]{"normal","large"}){Mode(font);using(var fixture=new Fixture()){
            var owner=fixture.Owner;owner.Dpi.Stages[2].YDpi=1600;
            foreach(bool hints in new[]{false,true}){
                var labels=new List<DrawnLabel>();CompactRenderer.LabelObserver=labels.Add;
                try{using(var image=Render(owner,false,false,new PanelFeedback {PendingStage=2,PendingOutput="headset",ShowPendingHints=hints})){} }finally{CompactRenderer.LabelObserver=null;}
                Check(labels.Exists(delegate(DrawnLabel label){return label.Text=="Y 1600…";})==hints&&labels.Exists(delegate(DrawnLabel label){return label.Text=="耳机…";})==hints,"native DPI and audio pending ellipses respect the existing hint time");
            }
            var rect=VisualDesign.StageRect(1,4);var labelsNormal=new List<DrawnLabel>();var labelsPressed=new List<DrawnLabel>();
            using(var image=new Bitmap(VisualDesign.PanelWidth,VisualDesign.CardHeightNormal))using(var g=Graphics.FromImage(image)){
                var focus=new List<RectangleF>();CompactRenderer.InteractionOutlineObserver=focus.Add;
                try{
                    CompactRenderer.LabelObserver=labelsNormal.Add;CompactRenderer.Selection(g,rect,"800",null,true,true,false,false,false,false,true,VisualDesign.PanelCardRect(0,VisualDesign.CardHeightNormal),11);
                    CompactRenderer.LabelObserver=labelsPressed.Add;CompactRenderer.Selection(g,rect,"800",null,true,true,false,true,false,false,false,VisualDesign.PanelCardRect(0,VisualDesign.CardHeightNormal),11);
                }finally{CompactRenderer.LabelObserver=null;CompactRenderer.InteractionOutlineObserver=null;}
                Check(focus.Count==1&&focus[0]==VisualDesign.SelectionFocusRect(rect),"segment focus is outside the floating thumb with a one-pixel gutter and two-pixel stroke");
                Check(labelsPressed[0].DeviceRect.Top-labelsNormal[0].DeviceRect.Top==1,"pressed segment contents actually move down one pixel");
                g.Clear(ThemePalette.Current(false).Track);CompactRenderer.Selection(g,rect,"800",null,true,true,false,false,true,false);
                var pip=VisualDesign.SelectionPipRect(rect);Check(image.GetPixel((int)pip.X+1,(int)pip.Y+1).ToArgb()!=image.GetPixel((int)pip.X+3,(int)pip.Y+1).ToArgb(),"pending replaces the solid pip with separated dots");
            }
        }}
    }
    static void RenderPreviews(string directory,string panelLayout="bar",bool fullMatrix=false){
        Directory.CreateDirectory(directory);const int scale=2;
        foreach(string style in ThemePalette.Keys)foreach(bool dark in new[]{false,true}){
            var frames=new List<Bitmap>();
            try{
                foreach(string font in new[]{"normal","large"}){
                    Mode(font,style,false,panelLayout);using(var fixture=new Fixture()){
                        var owner=fixture.Owner;frames.Add(Render(owner,false,dark,new PanelFeedback(),scale));
                        if(style=="ocean"){
                            string stem=Path.Combine(directory,"main-panel-"+panelLayout+"-ocean-"+(dark?"dark":"light")+"-"+font);
                            frames[frames.Count-1].Save(stem+"-headset.png",ImageFormat.Png);var ear=owner.Headset;owner.Headset=null;using(var noHeadset=Render(owner,false,dark,new PanelFeedback(),scale))noHeadset.Save(stem+"-no-headset.png",ImageFormat.Png);owner.Headset=ear;
                        }
                        if(fullMatrix){
                            var ear=owner.Headset;
                            foreach(bool contrast in new[]{false,true})foreach(bool headset in new[]{false,true})foreach(bool wireless in new[]{false,true}){
                                VisualDesign.HighContrastOverride=contrast;owner.Headset=headset?ear:null;
                                string path=Path.Combine(directory,"main-panel-"+panelLayout+"-"+style+"-"+(dark?"dark":"light")+"-"+font+"-"+(headset?"headset":"no-headset")+"-"+(contrast?"high-contrast":"standard")+"-"+(wireless?"wireless":"native")+".png");
                                using(var image=Render(owner,wireless,dark,new PanelFeedback(),scale))image.Save(path,ImageFormat.Png);
                            }
                            VisualDesign.HighContrastOverride=false;owner.Headset=ear;
                        }
                        owner.GenericReady=true;owner.Audio.MicrophoneMuted=true;
                        owner.WirelessDevices=new[]{
                            new WirelessDevice {Id="charging-keyboard",Name="VGN V98 Pro V3",Kind="keyboard",Transport="2.4G",Percent=72,Fresh=true,Online=true,ConnectionState="connected",ConfirmedAtUtc=DateTime.UtcNow.ToString("o"),Charging=true,ChargingCheckedAtUtc=DateTime.UtcNow.ToString("o")},
                            new WirelessDevice {Id="full-approximate",Name="非常长的设备名称用于验证省略",Kind="headset",Transport="Bluetooth",Percent=100,Fresh=true,Approximate=true,Online=true,ConnectionState="connected",ConfirmedAtUtc=DateTime.UtcNow.ToString("o")},
                            new WirelessDevice {Id="low-gamepad",Name="Xbox 无线手柄",Kind="gamepad",Transport="Bluetooth",Percent=12,Fresh=true,Online=true,ConnectionState="connected",ConfirmedAtUtc=DateTime.UtcNow.ToString("o")},
                            new WirelessDevice {Id="stale-mouse",Name="历史读数",Kind="mouse",Percent=46,Fresh=false,Online=false,ConnectionState="disconnected",ConfirmedAtUtc=DateTime.UtcNow.AddHours(-2).ToString("o")},
                            new WirelessDevice {Id="empty-reading",Name="无读数设备",Kind="keyboard",Online=true,ConnectionState="connected"}
                        };
                        frames.Add(Render(owner,true,dark,new PanelFeedback(),scale));
                    }
                }
                int width=0,height=0;foreach(var frame in frames){width+=frame.Width+32;height=Math.Max(height,frame.Height);}
                using(var sheet=new Bitmap(width+32,height+112,PixelFormat.Format32bppPArgb))using(var g=Graphics.FromImage(sheet)){
                    g.Clear(dark?Color.FromArgb(27,34,48):Color.FromArgb(237,241,247));
                    CompactRenderer.DrawLabel(g,PanelLayouts.Name(panelLayout)+" / "+ThemePalette.Name(style)+" / "+(dark?"深色":"浅色")+"  ·  标准原生 / 标准多设备 / 较大原生 / 较大多设备",new RectangleF(32,8,width-32,56),24,true,dark?Color.White:Color.FromArgb(27,36,48),StringAlignment.Near);
                    int x=32;foreach(var frame in frames){g.DrawImageUnscaled(frame,x,80);x+=frame.Width+32;}
                    string path=Path.Combine(directory,"main-panel-"+panelLayout+"-"+style+(dark?"-dark":"-light")+".png");sheet.Save(path,ImageFormat.Png);Console.WriteLine(path);
                }
            }finally{foreach(var frame in frames)frame.Dispose();}
        }
        if(panelLayout=="grid")RenderGridOverflowPreviews(directory);
    }
    static void RenderLayoutChoices(string directory,bool fullMatrix){
        foreach(string style in fullMatrix?ThemePalette.Keys:new[]{"ocean"})foreach(string font in new[]{"normal","large"})foreach(bool dark in new[]{false,true})foreach(bool contrast in new[]{false,true}){
            Mode(font,style,false,"ring");VisualDesign.HighContrastOverride=contrast;using(var fixture=new Fixture()){
                var nav=new PopupNavigation {Page=PopupPage.ChoosePreference,PreferenceAction=229};var rows=PopupPages.Build(fixture.Owner,nav,true);var size=UiState.ScaleSize(PopupPages.SizeFor(rows),2);
                string path=Path.Combine(directory,"layout-chooser-"+style+"-"+(dark?"dark":"light")+"-"+font+"-"+(contrast?"high-contrast":"standard")+".png");
                using(var image=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppPArgb))using(var g=Graphics.FromImage(image)){g.ScaleTransform(2,2);PopupPages.Paint(g,fixture.Owner,nav,rows,dark,-1,-1,-1);image.Save(path,ImageFormat.Png);}Console.WriteLine(path);
            }
        }
        VisualDesign.HighContrastOverride=false;
    }
    static void RenderGridOverflowPreviews(string directory){
        foreach(string font in new[]{"normal","large"})foreach(bool dark in new[]{false,true}){
            Mode(font,"ocean",false,"grid");using(var fixture=new Fixture()){
                var owner=fixture.Owner;owner.GenericReady=true;owner.Dpi=Dpi(8);owner.WirelessDevices=new[]{GridDevice(0,8),GridDevice(1,8),GridDevice(2,8),GridDevice(3,8)};
                for(int i=0;i<owner.WirelessDevices.Length;i++){var device=owner.WirelessDevices[i];device.Id="device-"+i.ToString().PadLeft(24,'0');device.Provider="logitech";device.SourceQuality="verified";device.MeasurementState="measured";device.Dpi.StageSource="onboard";}
                string stem=Path.Combine(directory,"main-panel-grid-five-mice-ocean-"+(dark?"dark":"light")+"-"+font);
                var normal=WirelessLayout.Build(owner,0,10000);using(var image=RenderGrid(owner,normal,dark,2,new PanelFeedback()))image.Save(stem+"-normal.png",ImageFormat.Png);
                foreach(int offset in new[]{0,Int32.MaxValue}){
                    var bounded=WirelessLayout.Build(owner,0,520,false,offset);string path=stem+"-520-"+(offset==0?"top":"bottom")+".png";
                    using(var image=RenderGrid(owner,bounded,dark,2,new PanelFeedback()))image.Save(path,ImageFormat.Png);Console.WriteLine(path);
                }
            }
        }
    }
    [STAThread] static int Main(string[] args){var previous=DeviceSettings.Current;try{
        if(args.Length>=2&&args[0]=="--render"){
            VisualDesign.HighContrastOverride=false;bool matrix=Array.IndexOf(args,"--matrix")>=0;var layouts=args.Length>2&&args[2]!="all"&&args[2]!="--matrix"?new[]{args[2]}:PanelLayouts.Keys;
            foreach(string layout in layouts)RenderPreviews(args[1],layout,matrix);RenderLayoutChoices(args[1],matrix);return 0;
        }
        Run("opening fill frames, real labels, states and reduced motion",OpeningFill);
        if(args.Length==1&&args[0]=="--opening-fill")return failures==0?0:1;
        if(args.Length==1&&args[0]=="--polish"){
            foreach(string layout in PanelLayouts.Keys){string key=layout;Run("complete rendering matrix / "+key,delegate{RenderMatrix(key);});}
            Console.WriteLine("192 layout/palette/light/font/headset/contrast combinations, both render paths; "+checks+" checks, "+failures+" failed groups");return failures==0?0:1;
        }
        Run("grid overflow 520px, five mice x eight stages, normal",delegate{GridOverflowHeight("normal");});
        Run("grid overflow 520px, five mice x eight stages, large",delegate{GridOverflowHeight("large");});
        Run("grid overflow viewport, clipping, fixed volume and scale matrix",GridOverflowViewportMatrix);
        if(args.Length==1&&args[0]=="--grid-overflow")return failures==0?0:1;
        Run("grid dispatch, two-column tiles and fixed quick controls",GridBasics);
        Run("grid 1..5 devices, whole-row paging and fixed controls",GridCountsAndPaging);Run("grid all-device control groups and hidden devices",GridGroupsAndHidden);
        Run("grid shared drawing and hit geometry",delegate{HitGeometry("grid");});Run("grid complete theme and render path matrix",delegate{RenderMatrix("grid");});Run("grid typography, status and accessibility at five DPI scales",GridTypographyAndStates);
        Run("grid feedback, control names and pending semantics",GridFeedbackAndControls);
        if(args.Length==1&&args[0]=="--grid")return failures==0?0:1;
        Run("layout defaults, migration, persistence and normalization",LayoutSettings);Run("ring geometry, full-row details and calculated heights",RingGeometry);
        Run("ring draw and hit geometry",delegate{HitGeometry("ring");});Run("ring color, font, headset, contrast and render path matrix",delegate{RenderMatrix("ring");});Run("ring tiny, full, cached, sleeping and charging boundaries",RingBoundaries);Run("ring state accessibility and typography at five DPI scales",RingNamesAndTypography);
        Run("ring paging reserves volume and the external footer",delegate{FooterPaging("ring");});
        if(args.Length==1&&args[0]=="--ring")return failures==0?0:1;
        VisualDesign.HighContrastOverride=false;Mode("normal");Run("battery bar proportions and bounds",BarGeometry);Run("battery digits and percent baselines at five DPI scales",BatteryTypography);Run("shared draw and hit geometry",HitGeometry);Run("four themes x light/dark x fonts x headset x high contrast x render paths",RenderMatrix);Run("card shadows and high contrast",ContrastAndShadows);Run("icon-only microphone accessible state",MicrophoneNames);Run("microphone pending and unavailable ink",MicrophonePresentation);Run("floating segmented controls",FloatingSegments);Run("pending hints, press offset and segment focus",PendingSegments);Run("plain footer and single-row volume",PlainFooterAndVolume);Run("bounded device page footer geometry",FooterPaging);
        Console.WriteLine(checks+" checks, "+failures+" failed groups; fake devices and audio only");return failures==0?0:1;
    }finally{CompactRenderer.LabelObserver=null;CompactRenderer.SymbolObserver=null;InteractionLayout.Observer=null;VisualDesign.HighContrastOverride=null;DeviceSettings.UseTransientConfiguration(previous);}}
}
