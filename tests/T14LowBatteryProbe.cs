using System;
using AtkBatteryTray;

internal static class T14LowBatteryProbe {
    static int Main(string[] args) {
        string status = args[0];
        var reading = new BatteryView {
            Percent = 10,
            Fresh = true,
            Unconfirmed = status == "unconfirmed",
            Approximate = status == "approximate"
        };
        const string device = "fixture";
        var policy = new LowBatteryPolicy();
        bool alerted = policy.Observe(device, reading, true, 20);
        Console.WriteLine("{0},{1}", alerted, policy.IsLatched(device));
        return 0;
    }
}
