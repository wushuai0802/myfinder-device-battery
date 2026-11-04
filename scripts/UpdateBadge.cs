using System;
using System.Drawing;

namespace AtkBatteryTray {
    internal static class UpdateBadge {
        internal static readonly Color Blue=Color.FromArgb(22,133,230);
        internal static Color Outline(bool taskbarDark){return taskbarDark?Color.FromArgb(32,32,32):Color.FromArgb(243,243,243);}
        static double HueDistance(Color a,Color b){double distance=Math.Abs(a.GetHue()-b.GetHue());return Math.Min(distance,360-distance);}
        static double Luminance(Color color){
            double[] values={color.R/255.0,color.G/255.0,color.B/255.0};
            for(int i=0;i<3;i++)values[i]=values[i]<=.04045?values[i]/12.92:Math.Pow((values[i]+.055)/1.055,2.4);
            return .2126*values[0]+.7152*values[1]+.0722*values[2];
        }
        internal static double Contrast(Color a,Color b){double x=Luminance(a),y=Luminance(b);return (Math.Max(x,y)+.05)/(Math.Min(x,y)+.05);}
        internal static Color ColorFor(Color accent,Color background,bool taskbarDark){
            bool red=accent.GetHue()<30||accent.GetHue()>330||HueDistance(accent,TrayGlyph.LevelColor(10,true))<30;
            bool close=HueDistance(accent,background)<30||Math.Abs(accent.R-background.R)+Math.Abs(accent.G-background.G)+Math.Abs(accent.B-background.B)<100;
            return red||close||Contrast(accent,Outline(taskbarDark))<3?Blue:accent;
        }
        internal static Rectangle Bounds(int size){int diameter=Math.Max(4,(int)Math.Round(size*.2,MidpointRounding.AwayFromZero));return new Rectangle(size-diameter,0,diameter,diameter);}
        // Rasterize concentric circles at native pixel centers: no fractional DPI
        // coordinates or resampling of the one-pixel separator at any size.
        internal static void Paint(Bitmap bitmap,Color color,bool taskbarDark){
            var bounds=Bounds(bitmap.Width);double radius=bounds.Width/2.0;
            const int stroke=1;
            for(int y=0;y<bounds.Height;y++)for(int x=0;x<bounds.Width;x++){
                double dx=x+.5-radius,dy=y+.5-radius,distance=dx*dx+dy*dy;
                if(distance<=radius*radius)bitmap.SetPixel(bounds.X+x,y,distance<=(radius-stroke)*(radius-stroke)?color:Outline(taskbarDark));
            }
        }
        internal static string Tooltip(string battery,string version){
            battery=battery??"";if(String.IsNullOrEmpty(version)||battery.Length>=63)return battery;
            string suffix=" · 有新版本 "+version;int room=63-battery.Length;
            if(room<suffix.Length){if(room<4)return battery;int take=room-1;if(Char.IsHighSurrogate(suffix[take-1]))take--;suffix=suffix.Substring(0,take)+"…";}
            return battery+suffix;
        }
    }
}
