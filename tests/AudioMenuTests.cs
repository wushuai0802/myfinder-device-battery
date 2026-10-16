using System;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

static class AudioMenuTests {
    static int passed;
    static void Check(bool value,string name){if(!value)throw new Exception("FAIL "+name);passed++;}
    [STAThread]
    static int Main() {
        const BindingFlags fields=BindingFlags.NonPublic|BindingFlags.Instance;
        var cached=typeof(DeviceSettings).GetField("current",BindingFlags.NonPublic|BindingFlags.Static);
        object old=cached.GetValue(null);
        var menu=new ToolStripMenuItem("Audio settings");
        try {
            // Exercise the actual menu builder without constructing the tray,
            // starting timers, opening hardware, or reading/writing user files.
            var context=(TrayContext)FormatterServices.GetUninitializedObject(typeof(TrayContext));
            typeof(TrayContext).GetField("audioSettingsMenu",fields).SetValue(context,menu);
            context.Audio=new AudioReading {Devices=new[] {
                new AudioDeviceChoice {Id="render",Name="DAC & Speakers",Flow=0},
                new AudioDeviceChoice {Id="capture",Name="USB microphone",Flow=1}
            }};
            var build=typeof(TrayContext).GetMethod("BuildAudioSettingsMenu",fields);
            cached.SetValue(null,new AudioBindings());
            build.Invoke(context,null);
            Check(menu.HasDropDownItems&&menu.DropDownItems.Count==6,"audio settings contains the three roles and refresh/reset actions");
            var speaker=(ToolStripMenuItem)menu.DropDownItems[0];
            var headset=(ToolStripMenuItem)menu.DropDownItems[1];
            var microphone=(ToolStripMenuItem)menu.DropDownItems[2];
            Check(((ToolStripMenuItem)speaker.DropDownItems[0]).Checked,"automatic binding is checked by default");
            Check(speaker.DropDownItems.Count==3&&speaker.DropDownItems[2].Text=="DAC && Speakers","speaker lists playback endpoints and preserves literal ampersands");
            Check(headset.DropDownItems.Count==3&&headset.DropDownItems[2].Text=="DAC && Speakers","headset lists playback endpoints");
            Check(microphone.DropDownItems.Count==3&&microphone.DropDownItems[2].Text=="USB microphone","microphone lists capture endpoints only");
            cached.SetValue(null,new AudioBindings {SpeakerEndpointId="render",MicrophoneEndpointId="missing"});
            build.Invoke(context,null);
            speaker=(ToolStripMenuItem)menu.DropDownItems[0];
            microphone=(ToolStripMenuItem)menu.DropDownItems[2];
            Check(menu.DropDownItems.Count==6,"reopening rebuilds without duplicating menu entries");
            Check(!((ToolStripMenuItem)speaker.DropDownItems[0]).Checked&&((ToolStripMenuItem)speaker.DropDownItems[2]).Checked,"saved endpoint is checked after reopening");
            Check(microphone.DropDownItems.Count==4&&!microphone.DropDownItems[3].Enabled&&((ToolStripMenuItem)microphone.DropDownItems[3]).Checked,"disconnected binding is visible without changing selection");
            context.Audio.Devices=new AudioDeviceChoice[0];
            build.Invoke(context,null);
            speaker=(ToolStripMenuItem)menu.DropDownItems[0];
            Check(speaker.DropDownItems.Count==4&&!speaker.DropDownItems[2].Enabled,"empty device list remains usable and reports the missing binding");
            Console.WriteLine("PASS "+passed+" audio-menu checks; no hardware, tray icon or user settings accessed");
            return 0;
        } catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally {menu.Dispose();cached.SetValue(null,old);}
    }
}
