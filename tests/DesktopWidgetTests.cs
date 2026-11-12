using System;
using System.Drawing;
using AtkBattery;
using AtkBatteryTray;

internal static class DesktopWidgetTests {
    [STAThread] static int Main() {
        try {
            var settings=new AudioBindings {DesktopWidgetEnabled=true,
                DesktopWidgetPosition=TrayIconPosition.Capture(new Point(-20,40),new Rectangle(-1920,0,1920,1080))};
            var copy=settings.Clone();
            if(!copy.DesktopWidgetEnabled||copy.DesktopWidgetPosition.X!=-20)throw new Exception("Widget settings lost during clone");
            settings.DesktopWidgetOpacity=70;
            if(settings.Clone().DesktopWidgetOpacity!=70)throw new Exception("Widget opacity lost during clone");
            var temporary=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"widget-"+Guid.NewGuid().ToString("N")+".json");
            try {
                DeviceSettings.SaveBindings(temporary,settings);
                var loaded=DeviceSettings.LoadBindings(temporary);
                if(loaded.DesktopWidgetOpacity!=70||!loaded.DesktopWidgetEnabled)throw new Exception("Widget settings round trip failed");
                settings.DesktopWidgetOpacity=0;DeviceSettings.SaveBindings(temporary,settings);
                if(DeviceSettings.LoadBindings(temporary).DesktopWidgetOpacity!=88)throw new Exception("Invalid opacity not normalized");
                System.IO.File.WriteAllText(temporary,"{\"SchemaVersion\":1}");
                if(DeviceSettings.LoadBindings(temporary).DesktopWidgetOpacity!=88)throw new Exception("Legacy settings default incorrect");
            } finally {if(System.IO.File.Exists(temporary))System.IO.File.Delete(temporary);}
            copy.DesktopWidgetPosition.X=99;
            if(settings.DesktopWidgetPosition.X!=-20)throw new Exception("Widget position was not deep cloned");
            var point=DesktopWidget.KeepVisible(new Point(9999,-9999),new Size(270,140),new Rectangle(0,0,1920,1080));
            if(point!=new Point(1650,0))throw new Exception("Offscreen position not corrected");
            point=DesktopWidget.KeepVisible(new Point(-1800,100),new Size(270,140),new Rectangle(-1920,0,1920,1080));
            if(point!=new Point(-1800,100))throw new Exception("Negative monitor coordinates lost");
            using(var widget=new DesktopWidget(null,delegate{},delegate(Point p){})) {
                if(widget.TopMost||widget.ShowInTaskbar||widget.FormBorderStyle!=System.Windows.Forms.FormBorderStyle.None)throw new Exception("Widget window chrome is incorrect");
                bool close=false,opacity=false;
                foreach(System.Windows.Forms.ToolStripItem item in widget.ContextMenuStrip.Items){close|=item.Text=="关闭悬浮窗";opacity|=item.Text.StartsWith("不透明度");}
                if(!close||!opacity)throw new Exception("Widget context menu missing actions");
            }
            Console.WriteLine("Desktop widget tests passed.");return 0;
        } catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}