using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace AtkBattery {
    internal static class AdaptationEvidence {
        internal const int ActionId = 264;

        static bool Within(string path, string directory) {
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        internal static ProcessStartInfo CreateStartInfo(string installRoot, string dataRoot, string target) {
            if (String.IsNullOrWhiteSpace(target) || target.Length < 3 || target[1] != ':' ||
                (target[2] != Path.DirectorySeparatorChar && target[2] != Path.AltDirectorySeparatorChar) ||
                target.IndexOf(':', 2) >= 0)
                throw new ArgumentException("An absolute export path is required.");
            target = Path.GetFullPath(target);
            string driveRoot = Path.GetPathRoot(target);
            if (driveRoot.Length != 3 || driveRoot[1] != ':' ||
                new DriveInfo(driveRoot).DriveType == DriveType.Network)
                throw new ArgumentException("A local export file is required.");
            DevicePackFiles.NoLinks(target);
            DevicePackFiles.NoLinks(installRoot);
            DevicePackFiles.NoLinks(dataRoot);
            if (Within(target, installRoot) || Within(target, dataRoot) ||
                Directory.Exists(target) || !Directory.Exists(Path.GetDirectoryName(target)))
                throw new ArgumentException("Choose a file outside the application folders.");

            string engine = Path.Combine(installRoot, "engine");
            string script = Path.Combine(engine, "hid_inventory.py");
            string interpreter = Path.Combine(engine, "runtime", "python.exe");
            if (!File.Exists(interpreter) || !File.Exists(script))
                throw new FileNotFoundException("The metadata export tool is missing.");
            return new ProcessStartInfo(interpreter,
                "-I -B " + UpdateProcess.Quote(script) + " --out " + UpdateProcess.Quote(target)) {
                WorkingDirectory = engine,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        internal static bool Export(ProcessStartInfo start) {
            try {
                using (var child = Process.Start(start)) {
                    if (child == null) return false;
                    if (child.WaitForExit(30000)) return child.ExitCode == 0;
                    if (!child.HasExited) child.Kill();
                }
            } catch (Win32Exception) {
            } catch (InvalidOperationException) {
            }
            return false;
        }
    }
}
