using System;
using System.Collections.Generic;
using System.Drawing;
using AtkBattery;

namespace AtkBatteryTray {
    internal sealed class GridControlGroup {
        internal WirelessCard Card;
        internal RectangleF Title,Track;
        internal RectangleF Viewport;
        internal int Count;
        internal bool Audio;
    }
    // One composition for native rendering, universal rendering, input and size.
    internal sealed class GridLayout {
        internal readonly List<GridControlGroup> Groups=new List<GridControlGroup>();
        internal RectangleF Controls,Viewport;
        internal int VisibleRows,ContentHeight,ScrollOffset,MaximumScroll;
        internal bool Overflow {get{return MaximumScroll>0;}}
        // The uncut content surface is used for measuring feedback; only the
        // viewport is painted/hit. Volume never belongs to this surface's scroll.
        internal RectangleF GroupSurface {get{return VisualDesign.PanelCardSurface(new RectangleF(Controls.X,Controls.Y-ScrollOffset,Controls.Width,ContentHeight+Controls.Bottom-Viewport.Bottom));}}
        internal static int TileHeight {get{return VisualDesign.LargeFont?104:92;}}
        internal static int TileWidth {get{return (VisualDesign.PanelWidth-8)/2;}}
        internal static int Inset {get{return VisualDesign.LargeFont?12:11;}}
        internal static int TitleHeight {get{return VisualDesign.LargeFont?20:18;}}
        internal static int DpiCount(WirelessCard card){return card.NativeMouse?card.Dpi==null||card.Dpi.Stages==null?0:Math.Min(8,card.Dpi.Stages.Count):WirelessLayout.DpiStageCount(card.Device);}
        internal static int DetailAction(WirelessLayout layout,WirelessCard card){return card.NativeMouse?2:card.NativeHeadset?3:card.AudioOnly?200:1000+layout.All.IndexOf(card);}
        internal static void Arrange(WirelessLayout layout,int offset,int maximumHeight,int controlOffset=0) {
            var grid=new GridLayout();layout.Grid=grid;float y=9;
            var mice=layout.All.FindAll(delegate(WirelessCard card){return !card.Hidden&&DpiCount(card)>0;});
            mice.Sort(delegate(WirelessCard a,WirelessCard b){return a.NativeMouse==b.NativeMouse?layout.All.IndexOf(a).CompareTo(layout.All.IndexOf(b)):a.NativeMouse?-1:1;});
            foreach(var card in mice){int count=DpiCount(card);grid.Groups.Add(new GridControlGroup {Card=card,Count=count,Title=new RectangleF(12,y,VisualDesign.PanelWidth-24,TitleHeight),Track=new RectangleF(10,y+TitleHeight,VisualDesign.PanelWidth-20,VisualDesign.SelectionTrackHeight(count))});y+=TitleHeight+VisualDesign.SelectionTrackHeight(count)+8;}
            var audio=layout.All.Find(delegate(WirelessCard card){return !card.Hidden&&(card.NativeHeadset||card.AudioOnly);})??layout.All.Find(delegate(WirelessCard card){return !card.Hidden&&card.Kind=="headset";});
            if(audio!=null){grid.Groups.Add(new GridControlGroup {Card=audio,Audio=true,Count=2,Title=new RectangleF(12,y,VisualDesign.PanelWidth-24,TitleHeight),Track=new RectangleF(10,y+TitleHeight,VisualDesign.PanelWidth-VisualDesign.MicrophoneWidth-26,VisualDesign.SelectionHeight)});y+=TitleHeight+VisualDesign.SelectionHeight+8;}
            if(grid.Groups.Count==0)y=0;
            grid.ContentHeight=(int)y;
            int volumeHeight=VisualDesign.SystemVolumeHeightFor(layout.VolumeCollapsed)+2;
            int groupHeight=Math.Min(grid.ContentHeight,Math.Max(0,maximumHeight-(TileHeight+8)-VisualDesign.FooterHeight-volumeHeight));
            int controlsHeight=groupHeight+volumeHeight;
            grid.MaximumScroll=grid.ContentHeight-groupHeight;
            int rows=Math.Max(1,(layout.All.Count+1)/2),available=maximumHeight-controlsHeight-8-VisualDesign.FooterHeight;
            grid.VisibleRows=Math.Min(rows,Math.Max(1,(available+8)/(TileHeight+8)));
            layout.Start=Math.Max(0,Math.Min(offset/2,rows-grid.VisibleRows))*2;
            layout.End=Math.Min(layout.All.Count,layout.Start+grid.VisibleRows*2);
            for(int i=layout.Start;i<layout.End;i++){
                var card=layout.All[i];float top=(i-layout.Start)/2*(TileHeight+8);bool wide=i==layout.All.Count-1&&layout.All.Count%2==1;
                card.Top=top;card.Tile=new RectangleF(wide||i%2==0?0:TileWidth+8,top,wide?VisualDesign.PanelWidth:TileWidth,TileHeight);layout.Cards.Add(card);
            }
            float controlsTop=grid.VisibleRows*(TileHeight+8);
            grid.Controls=new RectangleF(0,controlsTop,VisualDesign.PanelWidth,controlsHeight);
            int topInset=grid.Overflow?8:0;
            grid.Viewport=new RectangleF(0,controlsTop+topInset,VisualDesign.PanelWidth,Math.Max(0,groupHeight-topInset));
            foreach(var group in grid.Groups){group.Viewport=grid.Viewport;group.Title.Offset(0,controlsTop);group.Track.Offset(0,controlsTop);if(!group.Audio)group.Card.DpiTrack=group.Track;}
            layout.VolumeTop=controlsTop+groupHeight;layout.Footer=grid.Controls.Bottom;grid.ScrollTo(controlOffset);
        }
        internal void ScrollTo(int offset){
            int next=Math.Max(0,Math.Min(offset,MaximumScroll)),delta=ScrollOffset-next;ScrollOffset=next;
            if(delta==0)return;
            foreach(var group in Groups){group.Title.Offset(0,delta);group.Track.Offset(0,delta);if(!group.Audio)group.Card.DpiTrack=group.Track;}
        }
        internal int RevealOffset(RectangleF rect){
            float delta=rect.Top<Viewport.Top?rect.Top-Viewport.Top:rect.Bottom>Viewport.Bottom?rect.Bottom-Viewport.Bottom:0;
            return Math.Max(0,Math.Min(MaximumScroll,ScrollOffset+(int)(delta<0?Math.Floor(delta):Math.Ceiling(delta))));
        }
        internal static WirelessLayout Native(bool second,DpiConfig dpi) {
            var layout=new WirelessLayout {VolumeCollapsed=DeviceSettings.Current.VolumeCollapsed};var pref=DeviceSettings.Current;
            if(!pref.HiddenDeviceIds.Contains("native:mouse"))layout.All.Add(new WirelessCard {Id="native:mouse",Name=pref.MouseAlias,Kind="mouse",NativeMouse=true,Dpi=dpi});
            if(second&&!pref.HiddenDeviceIds.Contains("native:headset"))layout.All.Add(new WirelessCard {Id="native:headset",Name=pref.HeadsetAlias,Kind="headset",NativeHeadset=true});
            if(layout.All.Count==0)layout.All.Add(new WirelessCard {Id="empty",Name="无线设备",Kind="device"});
            Arrange(layout,0,Int32.MaxValue);return layout;
        }
        internal static WirelessLayout Native(BatteryReading current,BatteryReading lastGood,DpiConfig dpi,HeadsetReading headset,AudioReading audio) {
            var audioCard=VisualDesign.AudioCardFor(headset,audio);var layout=Native(VisualDesign.ShouldShowHeadset(headset)||audioCard!=null,dpi);
            foreach(var card in layout.All){if(card.NativeMouse)card.Battery=UiState.Mouse(current,lastGood,DateTime.UtcNow);else if(card.NativeHeadset){if(audioCard==null)card.Battery=UiState.Headset(headset,DateTime.UtcNow);else{card.Id="native:audio";card.Name="耳机";card.Subtitle=audioCard.Subtitle;card.NativeHeadset=false;card.AudioOnly=true;}}}
            return layout;
        }
        internal GridControlGroup Group(WirelessCard card){return Groups.Find(delegate(GridControlGroup group){return group.Card==card&&!group.Audio;});}
        internal static RectangleF Segment(GridControlGroup group,int index){return VisualDesign.SelectionSegmentRect(group.Track,index,group.Count);}
        internal static RectangleF Microphone(GridControlGroup group){return new RectangleF(VisualDesign.PanelWidth-VisualDesign.MicrophoneWidth-10,group.Track.Y,VisualDesign.MicrophoneWidth,VisualDesign.SelectionHeight);}
        internal static RectangleF Icon(RectangleF tile){int size=VisualDesign.LargeFont?18:16;return new RectangleF(tile.X+Inset,tile.Y+(tile.Width>TileWidth?VisualDesign.LargeFont?35:31:Inset),size,size);}
        internal static RectangleF Bar(RectangleF tile){return new RectangleF(tile.X+Inset,tile.Bottom-Inset-4,tile.Width-2*Inset,4);}
        internal static RectangleF Name(RectangleF tile){return tile.Width>TileWidth?new RectangleF(tile.X+Inset+26,tile.Y+(VisualDesign.LargeFont?35:31),tile.Width-2*Inset-26,TitleHeight):new RectangleF(tile.X+Inset,tile.Bottom-Inset-TitleHeight-8,tile.Width-2*Inset,TitleHeight);}
        internal static RectangleF Number(RectangleF tile){return tile.Width>TileWidth?new RectangleF(tile.Right-Inset-106,tile.Y+(VisualDesign.LargeFont?27:23),106,VisualDesign.LargeFont?40:36):new RectangleF(tile.X+Inset,tile.Y+(VisualDesign.LargeFont?31:26),tile.Width-2*Inset,VisualDesign.LargeFont?38:34);}
        internal static float NumberBaseline(RectangleF tile){return tile.Y+(VisualDesign.LargeFont?62:54);}
        internal static RectangleF Tag(RectangleF tile,string status){float height=VisualDesign.LargeFont?18:16,width=status.Length*(VisualDesign.LargeFont?11:10)+12;return new RectangleF(tile.Right-Inset-width,tile.Y+Inset,width,height);}
        internal static RectangleF Chevron(RectangleF tile,string status){return new RectangleF(String.IsNullOrEmpty(status)?tile.Right-Inset-7:Tag(tile,status).Left-13,tile.Y+Inset+3,7,10);}
        internal static string Status(BatteryView view,bool sleeping){return sleeping?"休眠":view==null?null:view.Fresh&&view.Charging?"充电":ThemePalette.IsLow(view,DeviceSettings.Current.WarningThreshold)?"电量低":view.Percent.HasValue&&!view.Fresh?"上次":null;}
        internal static string KindName(string kind){return kind=="mouse"?"鼠标":kind=="keyboard"?"键盘":kind=="headset"?"耳机":kind=="gamepad"?"手柄":"设备";}
        internal static PopupControl Spatial(List<PopupControl> tiles,PopupControl current,int dx,int dy){
            if(tiles.Count==0)return null;if(current==null)return tiles[0];PopupControl best=null;float score=Single.MaxValue;
            float x=current.Rect.X+current.Rect.Width/2,y=current.Rect.Y+current.Rect.Height/2;
            foreach(var tile in tiles){if(tile==current)continue;float tx=tile.Rect.X+tile.Rect.Width/2-x,ty=tile.Rect.Y+tile.Rect.Height/2-y;
                if(dx!=0&&(tx*dx<=0||Math.Abs(ty)>1)||dy!=0&&ty*dy<=0)continue;
                float distance=dx!=0?Math.Abs(tx):Math.Abs(ty)*1000+Math.Abs(tx);if(distance<score){score=distance;best=tile;}
            }
            return best??current;
        }
    }
}
