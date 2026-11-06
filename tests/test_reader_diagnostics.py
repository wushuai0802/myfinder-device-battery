"""Real C# JSON/feedback projection of synthetic reader health; no app startup."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from urllib.parse import parse_qs, urlsplit

from test_moondrop_engine import E, server

ROOT = Path(__file__).resolve().parents[1]
COMPILER = Path(os.environ.get("SystemRoot", "C:/Windows")) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"


@unittest.skipUnless(os.name == "nt" and COMPILER.is_file(), "Windows C# compiler required")
class ReaderDiagnosticsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        temp = tempfile.TemporaryDirectory(prefix="reader-diagnostics-", dir=ROOT)
        cls.addClassCleanup(temp.cleanup)
        cls.exe = Path(temp.name) / "ReaderDiagnosticsTests.exe"
        sources = sorted(str(p) for p in (ROOT / "scripts").glob("*.cs")
                         if p.name not in ("Probe.cs", "Tests.cs", "AssemblyInfo.cs"))
        refs = ("System.Runtime.Serialization", "System.Windows.Forms", "System.Drawing",
                "Accessibility", "System.IO.Compression", "System.IO.Compression.FileSystem")
        result = subprocess.run(
            [str(COMPILER), "/nologo", "/target:exe", "/platform:x64", "/main:ReaderDiagnosticsTests",
             "/out:" + str(cls.exe),
             *["/reference:" + ref + ".dll" for ref in refs],
             "/resource:" + str(ROOT / "engine/device_pack_schema.json") + ",DevicePackSchema",
             *sources, str(ROOT / "tests/ReaderDiagnosticsTests.cs")],
            capture_output=True, timeout=60, creationflags=subprocess.CREATE_NO_WINDOW)
        if result.returncode:
            raise AssertionError(result.stdout.decode(errors="replace") + result.stderr.decode(errors="replace"))

    def project(self, reason, provider="moondrop"):
        snapshot = {"schema": 2, "devices": [], "health": [
            {"provider": provider, "state": "unavailable", "errorCode": reason}]}
        result = subprocess.run([str(self.exe)], input=json.dumps(snapshot).encode(),
                                capture_output=True, timeout=10, creationflags=subprocess.CREATE_NO_WINDOW)
        self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))
        parsed, feedback = result.stdout.decode().replace("\r\n", "\n").split("\n===FEEDBACK===\n")
        feedback, url = feedback.split("\n===URL===\n")
        return parsed, feedback, url.strip()

    def test_compact_failure_survives_unchanged_parser_clipboard_and_url(self):
        info = E.entry(input_report_sizes=None, input_report_proof="failed",
                       report_descriptor_error="caps/descriptor disagreement",
                       declared_report_sizes={d: {str(i): 4096 for i in range(8)}
                                              for d in ("input", "output", "feature")})
        with E.fixture(inventory=[info]):
            provider = E.P()
            self.assertEqual(provider.poll(), [])
        engine = server.BatteryEngine([], include_native=True)
        self.addCleanup(engine.close)
        engine._accept("reader", "moondrop", [], server.utcnow(), "receiver-a", 1,
                       diagnostics=provider.diagnostics())
        health = engine._snapshot()["health"][0]
        reason = health["errorCode"]
        self.assertGreater(len(health["diagnostics"][0]), 200)
        self.assertLessEqual(len(reason), 48)
        self.assertTrue(reason.endswith("..."))
        for name in ("moondrop", "rapoo", "other"):
            parsed, feedback, url = self.project(reason, name)
            self.assertEqual(parsed, reason)
            self.assertIn(name + " | unavailable | " + reason, feedback)
            self.assertIn(reason, parse_qs(urlsplit(url).query)["diagnostics"][0])
            self.assertLessEqual(len(url), 6000)

    def test_alternate_report_summary_is_complete_within_existing_limit(self):
        full = ("input report 4B length unproven; proof=descriptor; "
                "declared=out:4B/64,in:4C/64,feature:none; not opened; nothing sent")
        reason = server.reader_health_summary(full)
        self.assertEqual(reason, "in:4B unproven;declared=out:4B/64,in:4C/64,...")
        parsed, feedback, url = self.project(reason)
        self.assertEqual(parsed, reason)
        self.assertIn(reason, feedback)
        self.assertIn(reason, parse_qs(urlsplit(url).query)["diagnostics"][0])

    def test_existing_redaction_and_desktop_limit_remain_unchanged(self):
        parsed, feedback, _ = self.project("PRIVATE-HEALTH C:/private/secret")
        self.assertEqual(parsed, "PRIVATE-HEALTH C:/private/secret")
        self.assertNotIn("PRIVATE-HEALTH", feedback)
        self.assertNotIn("C:/private/secret", feedback)
        self.assertIn("[redacted]", feedback)
        self.assertIn("[path]", feedback)
        parsed, feedback, url = self.project("x" * 2000)
        self.assertEqual(len(parsed), 48)
        self.assertIn(parsed, feedback)
        self.assertLessEqual(len(url), 6000)
