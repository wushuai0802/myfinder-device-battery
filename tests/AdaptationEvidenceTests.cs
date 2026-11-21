using System;
using System.IO;
using AtkBattery;
using AtkBatteryTray;

static class AdaptationEvidenceTests {
    static int checks;

    static void Check(bool condition, string message) {
        if (!condition) throw new Exception(message);
        checks++;
    }

    static void Reject(string install, string data, string output) {
        try {
            AdaptationEvidence.CreateStartInfo(install, data, output);
        } catch (ArgumentException) {
            checks++;
            return;
        }
        throw new Exception("Accepted a protected or invalid output path.");
    }

    [STAThread] static int Main(string[] args) {
        try {
            string root = Path.Combine(Path.GetFullPath(args[0]), "adaptation evidence");
            string install = Path.Combine(root, "app");
            string engine = Path.Combine(install, "engine");
            string runtime = Path.Combine(engine, "runtime");
            string data = Path.Combine(root, "data");
            string sibling = Path.Combine(root, "data-export");
            Directory.CreateDirectory(runtime);
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(sibling);
            string interpreter = Path.Combine(runtime, "python.exe");
            string script = Path.Combine(engine, "hid_inventory.py");
            string output = Path.Combine(sibling, "设备资料 01.txt");
            File.WriteAllText(interpreter, "");
            File.WriteAllText(script, "");

            var start = AdaptationEvidence.CreateStartInfo(install, data, output);
            Check(start.FileName == interpreter && start.WorkingDirectory == engine, "Bundled runtime and script directory.");
            Check(!start.UseShellExecute && start.CreateNoWindow, "The exporter uses a direct hidden process.");
            Check(start.Arguments == "-I -B \"" + script + "\" --out \"" + output + "\"", "Exact argument list with spaces and Unicode.");
            Check(!File.Exists(output), "Constructing the command produces no file.");
            Reject(install, data, "relative.txt");
            Reject(install, data, "C:relative.txt");
            Reject(install, data, @"\\example.invalid\share\inventory.txt");
            Reject(install, data, output + ":alternate-stream");
            Reject(install, data, Path.Combine(engine, "inventory.txt"));
            Reject(install, data, Path.Combine(data, "inventory.txt"));
            Reject(install, data, Path.Combine(root, "DATA", "case-insensitive.txt"));
            Reject(install, data, sibling);

            DeviceSettings.UseTransientConfiguration(new AudioBindings { RememberReadings = false, ReducedMotion = true });
            using (var owner = new TrayContext(false, delegate(bool readBattery) { return new MouseSnapshot(); },
                delegate(int dpi) { throw new InvalidOperationException(); })) {
                var rows = PopupPages.Build(owner, new PopupNavigation { Page = PopupPage.Diagnostics, VisibleChoices = 100 }, false);
                int row = rows.FindIndex(item => item.Id == AdaptationEvidence.ActionId);
                Check(row > 0 && rows[row - 1].Id == 262 && rows[row].Text == "导出适配资料" &&
                    rows[row].Enabled && !rows[row].Information, "The export action is available on the diagnostics page.");
            }
            Console.WriteLine("PASS " + checks + " adaptation export checks; fake paths and menu only");
            return 0;
        } catch (Exception error) {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
