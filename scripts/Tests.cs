using System;
using AtkBattery;
static class Tests {
    static int passed;
    static void Assert(bool ok,string name){if(!ok)throw new Exception("FAIL "+name);passed++;}
    static byte[] Response(int pct,int charge) {
        var b=MouseBattery.QueryPacket(4,17);b[6]=(byte)pct;b[7]=(byte)charge;b[8]=0x0f;b[9]=0xa0;
        int sum=0;for(int i=0;i<16;i++)sum+=b[i];b[16]=(byte)((0x55-sum)&255);return b;
    }
    static void FixFrameChecksum(byte[] b) {
        int sum=0;for(int i=0;i<16;i++)sum+=b[i];b[16]=(byte)((0x55-sum)&255);
    }
    static byte[] SystemResponse(int count,int active) {
        var b=new byte[17];b[0]=8;b[1]=8;b[5]=10;
        b[6]=1;b[7]=84;b[8]=(byte)count;b[9]=(byte)((85-count)&255);
        b[10]=(byte)active;b[11]=(byte)((85-active)&255);b[13]=85;b[15]=85;
        FixFrameChecksum(b);return b;
    }
    static void MustFail(Action action,string name) {bool threw=false;try{action();}catch{threw=true;}Assert(threw,name);}
    public static int Run() {
        try {
            foreach(byte op in new byte[]{1,3,4,16}) {
                var b=MouseBattery.QueryPacket(op,17);int sum=0;foreach(byte x in b)sum+=x;
                Assert(b.Length==17&&b[0]==8&&b[1]==op&&(sum&255)==0x55,"query framing "+op);
            }
            Assert(MouseBattery.QueryPacket(3,17)[16]==0x4a,"online checksum");
            Assert(MouseBattery.QueryPacket(4,17)[16]==0x49,"battery checksum");
            Assert(MouseBattery.QueryPacket(16,17)[16]==0x3d,"identity checksum");
            foreach(byte op in new byte[]{2,5,7,9,13,15,19,22,24}) {
                byte copy=op;MustFail(delegate{MouseBattery.QueryPacket(copy,17);},"forbidden write "+op);
            }
            MustFail(delegate{MouseBattery.QueryPacket(4,16);},"short query");
            MustFail(delegate{MouseBattery.QueryPacket(4,513);},"oversize query");
            var r=MouseBattery.ParseBattery(Response(42,0));Assert(r.Percent==42&&!r.Charging&&r.Millivolts==4000,"read discharging");
            r=MouseBattery.ParseBattery(Response(100,1));Assert(r.Percent==100&&r.Charging,"read charging");
            r=MouseBattery.ParseBattery(Response(0,0));Assert(r.Percent==0,"zero is valid percent");
            MustFail(delegate{MouseBattery.ParseBattery(Response(101,0));},"invalid percent");
            MustFail(delegate{MouseBattery.ParseBattery(Response(40,2));},"invalid charge flag");
            MustFail(delegate{MouseBattery.ParseBattery(new byte[16]);},"short response");
            MustFail(delegate{MouseBattery.ParseBattery(null);},"null response");
            var bad=Response(42,0);bad[6]++;MustFail(delegate{MouseBattery.ParseBattery(bad);},"checksum mismatch");
            bad=Response(42,0);bad[0]=7;MustFail(delegate{MouseBattery.ParseBattery(bad);},"wrong report id");
            bad=Response(42,0);bad[1]=3;MustFail(delegate{MouseBattery.ParseBattery(bad);},"wrong opcode");
            var dpi=MouseBattery.PackPaw3955Stage(800,1600);
            Assert(dpi.Length==6&&dpi[0]==31&&dpi[1]==3&&dpi[2]==63&&dpi[3]==6&&dpi[4]==0&&dpi[5]==0xee,"PAW3955 DPI packing");
            var stage=MouseBattery.ParsePaw3955Stage(dpi,0,0x1b00);
            Assert(stage.XDpi==800&&stage.YDpi==1600&&stage.CrcValid,"PAW3955 DPI round trip");
            dpi[5]++;MustFail(delegate{MouseBattery.ParsePaw3955Stage(dpi,0,0x1b00);},"PAW3955 CRC mismatch");
            MustFail(delegate{MouseBattery.PackPaw3955Stage(40001,40001);},"PAW3955 range");
            var system=SystemResponse(4,1);var config=MouseBattery.ParseDpiSystem(system);
            Assert(config.MaxStages==4&&config.ActiveStage==1&&config.CrcValid,"DPI system uses zero-based active index");
            system=SystemResponse(4,1);system[9]--;FixFrameChecksum(system);
            MustFail(delegate{MouseBattery.ParseDpiSystem(system);},"reject damaged count CRC despite valid frame checksum");
            system=SystemResponse(4,1);system[11]--;FixFrameChecksum(system);
            MustFail(delegate{MouseBattery.ParseDpiSystem(system);},"reject damaged active CRC despite valid frame checksum");
            MustFail(delegate{MouseBattery.ParseDpiSystem(SystemResponse(4,4));},"reject invalid active stage without clamping");
            MustFail(delegate{MouseBattery.ParseDpiSystem(SystemResponse(0,0));},"reject zero stage count");
            MustFail(delegate{MouseBattery.ParseDpiSystem(SystemResponse(9,1));},"reject too many stages");
            MustFail(delegate{MouseBattery.ParseDpiSystem(null);},"reject null system response");
            MustFail(delegate{MouseBattery.ParseDpiSystem(new byte[16]);},"reject truncated system response");
            MustFail(delegate{MouseBattery.ParseDpiSystem(new byte[18]);},"reject oversized system response");
            system=SystemResponse(4,1);system[1]=7;FixFrameChecksum(system);
            MustFail(delegate{MouseBattery.ParseDpiSystem(system);},"reject wrong system opcode");
            system=SystemResponse(4,1);system[2]=1;FixFrameChecksum(system);
            MustFail(delegate{MouseBattery.ParseDpiSystem(system);},"reject failed EEPROM status");
            system=SystemResponse(4,1);system[5]=2;FixFrameChecksum(system);
            MustFail(delegate{MouseBattery.ParseDpiSystem(system);},"reject wrong system payload length");
            system=SystemResponse(4,1);system[16]++;
            MustFail(delegate{MouseBattery.ParseDpiSystem(system);},"reject damaged system frame checksum");
            MouseBattery.ValidateActiveDpiIndex(0,4);MouseBattery.ValidateActiveDpiIndex(3,4);
            Assert(true,"accept first and last available stage indices");
            MustFail(delegate{MouseBattery.ValidateActiveDpiIndex(4,4);},"reject out-of-count stage index before write");
            MustFail(delegate{MouseBattery.ValidateActiveDpiIndex(-1,4);},"reject negative stage index before write");
            MustFail(delegate{MouseBattery.ValidateActiveDpiIndex(0,0);},"reject invalid stage count before write");
            MustFail(delegate{MouseBattery.SetActiveDpiStage(-1);},"public switch rejects negative index before HID access");
            MustFail(delegate{MouseBattery.SetActiveDpiStage(8);},"public switch rejects oversized index before HID access");
            Console.WriteLine("PASS "+passed+" offline tests; no HID device opened");return 0;
        }catch(Exception e){Console.Error.WriteLine(e.Message);return 1;}
    }
}
