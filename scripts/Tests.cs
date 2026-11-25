using System;
using System.IO;
using System.Reflection;
using AtkBattery;
static class Tests {
    static int passed;
    static void Assert(bool ok,string name){if(!ok)throw new Exception("FAIL "+name);passed++;}
    static void RunWirelessDpiTests() {
        const BindingFlags Any=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        Assembly asm=typeof(Tests).Assembly.GetType("AtkBattery.WirelessDpi")!=null?typeof(Tests).Assembly:null;
        if(asm==null) {
            string sibling=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"AtkMouseBattery.exe");
            if(File.Exists(sibling))asm=Assembly.LoadFrom(sibling);
        }
        if(asm==null)return;
        Type dpiType=asm.GetType("AtkBattery.WirelessDpi",true);
        Type devType=asm.GetType("AtkBattery.WirelessDevice",true);
        Type workerType=asm.GetType("AtkBattery.GenericBatteryWorker",true);
        Type layoutType=asm.GetType("AtkBatteryTray.WirelessLayout",true);
        MethodInfo canWrite=dpiType.GetMethod("CanWrite",Any);
        MethodInfo parse=workerType.GetMethod("Parse",Any);
        MethodInfo dpiFresh=layoutType.GetMethod("DpiFresh",Any);
        MethodInfo dpiSubtitle=layoutType.GetMethod("DpiSubtitle",Any);
        MethodInfo dpiStageCount=layoutType.GetMethod("DpiStageCount",Any);
        string token=new string('a',64);
        string now=DateTime.UtcNow.ToString("o");
        Func<string,string,string,bool,string,object> makeDevice=delegate(string provider,string stageSource,string modelSource,bool canSet,string targetToken) {
            object dev=Activator.CreateInstance(devType);
            devType.GetField("Id").SetValue(dev,"device-000000000000000000000001");
            devType.GetField("Name").SetValue(dev,"Fixture Mouse");
            devType.GetField("Kind").SetValue(dev,"mouse");
            devType.GetField("Provider").SetValue(dev,provider);
            devType.GetField("ModelSource").SetValue(dev,modelSource);
            devType.GetField("Online").SetValue(dev,true);
            devType.GetField("Fresh").SetValue(dev,true);
            devType.GetField("ConnectionState").SetValue(dev,"connected");
            object dpi=Activator.CreateInstance(dpiType);
            dpiType.GetField("Supported").SetValue(dpi,true);
            dpiType.GetField("CanSet").SetValue(dpi,canSet);
            dpiType.GetField("Current").SetValue(dpi,(int?)1600);
            dpiType.GetField("Stages").SetValue(dpi,new[]{400,800,1600,3200});
            dpiType.GetField("StageSource").SetValue(dpi,stageSource);
            dpiType.GetField("CheckedAtUtc").SetValue(dpi,now);
            dpiType.GetField("TargetToken").SetValue(dpi,targetToken);
            dpiType.GetField("Error").SetValue(dpi,null);
            devType.GetField("Dpi").SetValue(dev,dpi);
            return dev;
        };
        Func<object,string,bool> checkCanWrite=delegate(object dev,string tok) {
            object dpi=devType.GetField("Dpi").GetValue(dev);
            return (bool)canWrite.Invoke(dpi,new[]{dev,tok});
        };
        foreach(string provider in new[]{"logitech","razer","mchose","asus","asus_omni","lamzu","gwolves"}) {
            Assert(checkCanWrite(makeDevice(provider,"onboard",null,true,token),token),provider+" onboard CanWrite allowed");
            Assert(checkCanWrite(makeDevice(provider,"quick",null,true,token),token),provider+" quick CanWrite allowed");
        }
        foreach(string provider in new[]{"pulsar","families"}) {
            Assert(checkCanWrite(makeDevice(provider,"onboard",null,true,token),token),provider+" onboard CanWrite allowed");
            Assert(!checkCanWrite(makeDevice(provider,"quick",null,true,token),token),provider+" quick CanWrite rejected");
        }
        foreach(string forbidden in new[]{"bluetooth","standard_hid","xinput","unknown",""}) {
            Assert(!checkCanWrite(makeDevice(forbidden,"onboard",null,true,token),token),"forbidden provider '"+forbidden+"' onboard rejected");
            Assert(!checkCanWrite(makeDevice(forbidden,"quick",null,true,token),token),"forbidden provider '"+forbidden+"' quick rejected");
        }
        foreach(string provider in new[]{"logitech","pulsar","razer","mchose","families"}) {
            Assert(!checkCanWrite(makeDevice(provider,"onboard","pack",true,token),token),provider+" pack modelSource CanWrite rejected");
        }
        object readOnlyDev=makeDevice("logitech","onboard",null,false,null);
        Assert(!checkCanWrite(readOnlyDev,null)&&!checkCanWrite(readOnlyDev,token),"read-only GPW3 0x8100 CanWrite rejected");
        Assert((int)dpiStageCount.Invoke(null,new[]{readOnlyDev})==4,"read-only DPI retains onboard stage count");
        Assert((bool)dpiFresh.Invoke(null,new[]{readOnlyDev}),"read-only live DPI is fresh without targetToken");
        Assert((string)dpiSubtitle.Invoke(null,new object[]{readOnlyDev,null,null})=="DPI · 1600","read-only live DPI subtitle displays current value without stale or error marker");
        string roJson="{\"schema\":2,\"requestId\":1,\"generation\":1,\"complete\":true,\"devices\":[{\"id\":\"device-000000000000000000000001\",\"name\":\"PRO X SUPERLIGHT 2\",\"kind\":\"mouse\",\"provider\":\"logitech\",\"percent\":85,\"fresh\":true,\"online\":true,\"connectionState\":\"connected\",\"confirmedAtUtc\":\""+now+"\",\"dpi\":{\"supported\":true,\"canSet\":false,\"current\":1600,\"stages\":[400,800,1600,3200],\"stageSource\":\"onboard\",\"checkedAtUtc\":\""+now+"\",\"targetToken\":null,\"error\":null}}]}";
        object snap=parse.Invoke(null,new object[]{roJson});
        Array parsedDevs=(Array)snap.GetType().GetField("Devices").GetValue(snap);
        object parsedRo=parsedDevs.GetValue(0);
        object parsedRoDpi=devType.GetField("Dpi").GetValue(parsedRo);
        Assert((bool)dpiType.GetField("Supported").GetValue(parsedRoDpi)&&!(bool)dpiType.GetField("CanSet").GetValue(parsedRoDpi)&&dpiType.GetField("TargetToken").GetValue(parsedRoDpi)==null,"parsed read-only DPI keeps Supported=true and CanSet=false");
        Assert((bool)dpiFresh.Invoke(null,new[]{parsedRo})&&(string)dpiSubtitle.Invoke(null,new object[]{parsedRo,null,null})=="DPI · 1600","parsed read-only DPI renders fresh current DPI subtitle");
        dpiType.GetField("CheckedAtUtc").SetValue(parsedRoDpi,DateTime.UtcNow.AddSeconds(-95).ToString("o"));
        Assert(!(bool)dpiFresh.Invoke(null,new[]{parsedRo})&&(string)dpiSubtitle.Invoke(null,new object[]{parsedRo,null,null})=="DPI · 上次 1600","stale read-only DPI falls back to historical subtitle");
    }
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
            RunWirelessDpiTests();
            Console.WriteLine("PASS "+passed+" offline tests; no HID device opened");return 0;
        }catch(Exception e){Console.Error.WriteLine(e.Message);return 1;}
    }
}
