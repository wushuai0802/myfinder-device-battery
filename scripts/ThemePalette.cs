using System;
using System.Drawing;
using AtkBattery;

namespace AtkBatteryTray {
    // Panel colors only. TrayGlyph keeps its existing, theme-independent badge.
    internal sealed class ThemePalette {
        internal readonly Color Card,Border,Foreground,Muted,Track,Accent,Selected,SelectedForeground;
        readonly bool dark;
        internal static readonly string[] Keys={"ocean","native","amber","mist"};
        static readonly string[] Names={"海蓝","原生","琥珀","青雾"};
        static readonly ThemePalette[] Palettes={
            new ThemePalette(true, 0x2C3038,0x4B535E,0xF4F6F8,0xA7B1C1,0x424852,0x4D9BFF,0x1F6FEB,0xFFFFFF),
            new ThemePalette(false,0xFFFFFF,0xDCE2EB,0x1B2430,0x5F6B7C,0xE7EBF1,0x0865D6,0x0865D6,0xFFFFFF),
            new ThemePalette(true, 0x2B2B2B,0x3E3E3E,0xFFFFFF,0xABABAB,0x454545,0x60CDFF,0x0067C0,0xFFFFFF),
            new ThemePalette(false,0xFFFFFF,0xE5E5E5,0x1A1A1A,0x666666,0xE6E6E6,0x005FB8,0x005FB8,0xFFFFFF),
            new ThemePalette(true, 0x231F1C,0x38322D,0xF3EFEA,0xA89E95,0x3D3631,0xE89150,0xE89150,0x1A1208),
            new ThemePalette(false,0xFFFDFA,0xE6DFD5,0x2A241F,0x6F665C,0xEEE8DF,0xA3521E,0xA3521E,0xFFFFFF),
            new ThemePalette(true, 0x242B33,0x323C47,0xDDE3EA,0x98A4B3,0x36414C,0x5CC2C4,0x5CC2C4,0x0E171B),
            new ThemePalette(false,0xFAFCFD,0xD9E2EC,0x1E293B,0x5A6A80,0xE4EBF1,0x1F7A7C,0x1F7A7C,0xFFFFFF)
        };
        ThemePalette(bool dark,int card,int border,int foreground,int muted,int track,int accent,int selected,int selectedForeground) {
            this.dark=dark;Card=Rgb(card);Border=Rgb(border);Foreground=Rgb(foreground);Muted=Rgb(muted);Track=Rgb(track);Accent=Rgb(accent);Selected=Rgb(selected);SelectedForeground=Rgb(selectedForeground);
        }
        ThemePalette() {
            Card=SystemColors.Window;Border=SystemColors.WindowText;Foreground=SystemColors.WindowText;Muted=SystemColors.GrayText;
            Track=SystemColors.Control;Accent=SystemColors.Highlight;Selected=SystemColors.Highlight;SelectedForeground=SystemColors.HighlightText;
        }
        static Color Rgb(int rgb){return Color.FromArgb(unchecked((int)0xff000000)|rgb);}
        internal static string Normalize(string style){return Array.IndexOf(Keys,style)<0?"ocean":style;}
        internal static string Name(string style){return Names[Array.IndexOf(Keys,Normalize(style))];}
        internal static ThemePalette For(string style,bool dark) {
            // Read current system colors, including when Windows changes HC theme.
            if(VisualDesign.HighContrast)return new ThemePalette();
            return Palettes[Array.IndexOf(Keys,Normalize(style))*2+(dark?0:1)];
        }
        internal static ThemePalette Current(bool dark){return For(DeviceSettings.Current.ThemeStyle,dark);}
        internal Color Interaction(bool pressed){return VisualDesign.HighContrast?Selected:Color.FromArgb(pressed?(dark?30:19):(dark?18:10),Foreground);}
        internal Color IconFill {get{return VisualDesign.HighContrast?Track:Color.FromArgb(dark?9:5,Foreground);}}
        internal Color SyncFill {get{return VisualDesign.HighContrast?Card:Color.FromArgb(225,Card);}}
        internal Color Knob {get{return VisualDesign.HighContrast?SystemColors.Window:Foreground;}}
        internal static Color LevelColor(int? percent,bool dark) {
            if(VisualDesign.HighContrast)return percent.HasValue?SystemColors.WindowText:SystemColors.GrayText;
            if(!percent.HasValue)return Current(dark).Muted;
            if(dark)return TrayGlyph.LevelColor(percent,true);
            return Rgb(percent.Value<20?0xC92A22:percent.Value<50?0x9E6400:0x1A8A50);
        }
        internal static bool IsLow(BatteryView reading,int threshold){return reading!=null&&reading.Fresh&&reading.Percent.HasValue&&reading.Percent.Value<threshold;}
        internal static Color WarningOverlay(bool dark){return VisualDesign.HighContrast?Color.Transparent:Color.FromArgb(dark?26:16,LevelColor(0,dark));}
        internal static Color Blend(Color background,Color foreground,int alpha) {
            return Color.FromArgb((background.R*(255-alpha)+foreground.R*alpha+127)/255,(background.G*(255-alpha)+foreground.G*alpha+127)/255,(background.B*(255-alpha)+foreground.B*alpha+127)/255);
        }
        internal Color WarningBorder {get{return VisualDesign.HighContrast?Border:Blend(Border,LevelColor(0,dark),80);}}
        internal Color MutedMicrophoneFill {get{return VisualDesign.HighContrast?Track:Color.FromArgb(dark?35:20,LevelColor(0,dark));}}
    }
}
