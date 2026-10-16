using System;
using System.Text;
using AtkBattery;
class Probe {
    static int Main(string[] args) {
        Console.OutputEncoding = Encoding.UTF8;
        bool select=args.Length==2&&args[0]=="--select-stage";
        if(!select&&(args.Length>1 || (args.Length==1 && args[0]!="--enumerate" && args[0]!="--diagnostic" && args[0]!="--power" && args[0]!="--dpi-read" && args[0]!="--self-test" && args[0]!="--help"))) {
            Console.Error.WriteLine("Unknown option; no device query sent");return 2;
        }
        if(args.Length==1&&args[0]=="--help") {Console.WriteLine("No args: verified A9 Air battery query. --enumerate: descriptors only. --power/--diagnostic: query device. --dpi-read: read-only EEPROM/CRC diagnostics. --select-stage 1..8: switch to an existing stage and verify readback. --self-test: offline only.");return 0;}
        if(args.Length==1&&args[0]=="--self-test")return Tests.Run();
        if(args.Length>0 && args[0]=="--enumerate") {
            foreach(var d in MouseBattery.Enumerate())
                Console.WriteLine("Usage="+d.UsagePage.ToString("X4")+":"+d.Usage.ToString("X4")+" Input="+d.InputLength+" Output="+d.OutputLength+" Feature="+d.FeatureLength+" Product="+d.Product);
            return 0;
        }
        try {
            if(select) {
                int stage;if(!int.TryParse(args[1],out stage)||stage<1||stage>8)throw new ArgumentException("Stage must be 1..8; no device query sent");
                var config=MouseBattery.SetActiveDpiStage(stage-1);var active=config.Stages[config.ActiveStage];
                Console.WriteLine("{\"activeStage\":"+config.ActiveStage+",\"xDpi\":"+active.XDpi+",\"yDpi\":"+active.YDpi+",\"verified\":true}");return 0;
            }
            if(args.Length>0 && args[0]=="--diagnostic") { Console.WriteLine(MouseBattery.Diagnostics());return 0; }
            if(args.Length>0 && args[0]=="--dpi-read") { Console.WriteLine(MouseBattery.DpiDiagnostics());return 0; }
            if(args.Length>0 && args[0]=="--power") { Console.WriteLine(MouseBattery.PowerDiagnostic());return 0; }
            var b=MouseBattery.Read();
            Console.WriteLine("{\"percent\":"+b.Percent+",\"charging\":"+b.Charging.ToString().ToLower()+",\"millivolts\":"+b.Millivolts+",\"timestampUtc\":\""+b.TimestampUtc.ToString("o")+"\"}");
            return 0;
        } catch(Exception e) {Console.Error.WriteLine(e.Message);return 1;}
    }
}
