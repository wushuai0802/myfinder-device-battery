"""T14 checks for FA09 reading evidence and the Windows reminder decision."""

import os
from pathlib import Path
import subprocess
import tempfile
import unittest

from test_aula_fa09_engine import P, aula_fixture, aula_reply, server


ROOT = Path(__file__).resolve().parents[1]
COMPILER = Path(os.environ.get("SystemRoot", "C:/Windows")) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"


class Fa09ReadingCorrectnessTests(unittest.TestCase):
    def test_feature06_field_cannot_become_confirmed_percentage(self):
        for reported_byte in (10, 48, 100):
            with self.subTest(reported_byte=reported_byte):
                engine = server.BatteryEngine([P()], include_native=True)
                self.addCleanup(engine.close)
                with aula_fixture([aula_reply(reported_byte)]) as bus:
                    snapshot = engine.poll()
                self.assertEqual(snapshot["errors"], [])
                for reading in snapshot["devices"]:
                    self.assertTrue(
                        reading.get("percent") is None or reading.get("unconfirmed") is True,
                        "Feature06 must never supply an ordinary percentage",
                    )
                self.assertEqual((bus.feature_writes, bus.feature_reads), ([], []))


@unittest.skipUnless(os.name == "nt" and COMPILER.is_file(), "Windows C# compiler required")
class LowBatteryReadingCorrectnessTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        output = tempfile.TemporaryDirectory(prefix="reading-correctness-")
        cls.addClassCleanup(output.cleanup)
        cls.probe = Path(output.name) / "T14LowBatteryProbe.exe"
        sources = sorted(str(path) for path in (ROOT / "scripts").glob("*.cs")
                         if path.name not in ("Probe.cs", "Tests.cs", "AssemblyInfo.cs"))
        references = ("System.Runtime.Serialization", "System.Windows.Forms", "System.Drawing",
                      "Accessibility", "System.IO.Compression", "System.IO.Compression.FileSystem")
        result = subprocess.run(
            [str(COMPILER), "/nologo", "/target:exe", "/platform:x64", "/main:T14LowBatteryProbe",
             "/out:" + str(cls.probe), *["/reference:" + name + ".dll" for name in references],
             "/resource:" + str(ROOT / "engine/device_pack_schema.json") + ",DevicePackSchema",
             *sources, str(ROOT / "tests/T14LowBatteryProbe.cs")],
            capture_output=True, timeout=60, creationflags=subprocess.CREATE_NO_WINDOW,
        )
        if result.returncode:
            raise AssertionError((result.stdout + result.stderr).decode(errors="replace"))

    def observe(self, status):
        result = subprocess.run([str(self.probe), status], capture_output=True, timeout=10,
                                creationflags=subprocess.CREATE_NO_WINDOW)
        self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))
        return result.stdout.decode("ascii").strip()

    def test_fresh_low_confirmed_reading_can_alert(self):
        self.assertEqual(self.observe("confirmed"), "True,True")

    def test_fresh_low_unconfirmed_reading_does_not_alert_or_latch(self):
        self.assertEqual(self.observe("unconfirmed"), "False,False")

    def test_fresh_low_estimate_can_alert_and_latch(self):
        # Unconfirmed fields may be spurious and suppress alerts.
        # Approximate readings carry a real device value at coarser precision and can alert.
        self.assertEqual(self.observe("approximate"), "True,True")
