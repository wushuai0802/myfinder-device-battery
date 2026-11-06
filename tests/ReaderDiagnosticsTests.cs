using System;
using AtkBattery;

// Pure JSON/feedback projection: no worker, UI, HID, clipboard or browser starts.
static class ReaderDiagnosticsTests {
    static int Main() {
        Console.InputEncoding=new System.Text.UTF8Encoding(false);
        Console.OutputEncoding=new System.Text.UTF8Encoding(false);
        var snapshot=GenericBatteryWorker.Parse(Console.In.ReadToEnd());
        if(!String.IsNullOrEmpty(snapshot.Error)||snapshot.Health.Length!=1)return 1;
        var report=new FeedbackReport("test","test",new WirelessDevice[0],
            new SuppressedDevice[0],new UnadaptedDevice[0],snapshot.Health,
            new[]{"PRIVATE-HEALTH"});
        Console.WriteLine(snapshot.Health[0].ErrorCode);
        Console.WriteLine("===FEEDBACK===");
        Console.WriteLine(report.FullText);
        Console.WriteLine("===URL===");
        Console.WriteLine(report.IssueUrl);
        return 0;
    }
}
