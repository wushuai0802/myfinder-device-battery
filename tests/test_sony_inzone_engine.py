"""Original H9 protocol/bridge tests, without accessing hardware."""
import base64
import json
from pathlib import Path
import sys
import subprocess
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "engine"))
from providers import sony_inzone as S
import server
from catalog import verification_for


def frame(level=73, charging=0):
    return S.SIGNATURE + bytes([0, 0, 0, charging, level, 0])


class SonyInzoneTests(unittest.TestCase):
    @unittest.skipUnless(sys.platform == "win32", "Windows PowerShell parser")
    def test_powershell_script_syntax(self):
        command = ('$tokens=$null; $errors=$null; '
                   '[void][System.Management.Automation.Language.Parser]::ParseInput('
                   '[Console]::In.ReadToEnd(),[ref]$tokens,[ref]$errors); '
                   '$errors | ForEach-Object { $_.Message }; if ($errors.Count) { exit 1 }')
        result = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", command],
                                input=S.PS_SCRIPT.encode(), capture_output=True, timeout=15)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_frame_boundaries_and_values(self):
        for level in (0, 1, 73, 100):
            for charging in (0, 1):
                self.assertEqual(S.parse_reply(frame(level, charging)), (level, bool(charging)))
        for data in (b"", frame()[:-1], frame(101), frame(255), frame(charging=2),
                     b"\x05" + frame()[1:]):
            self.assertIsNone(S.parse_reply(data))
        self.assertEqual(S.parse_reply(b"noise" + frame()), (73, False))

    def test_poll_and_public_headset_reading(self):
        output = json.dumps(dict(port="COM12", data=base64.b64encode(frame(42, 1)).decode())).encode()
        with patch.object(S.sys, "platform", "win32"), patch.object(S.subprocess, "run", return_value=
                SimpleNamespace(returncode=0, stdout=output)) as run:
            provider = S.SonyInzoneProvider()
            statuses = provider.poll()
        self.assertEqual(len(statuses), 1)
        self.assertEqual(run.call_args.kwargs["timeout"], 8)
        engine = server.BatteryEngine([provider], include_native=True)
        self.addCleanup(engine.close)
        engine._accept("sony_inzone", "sony_inzone", statuses, server.utcnow())
        device, = engine._snapshot()["devices"]
        self.assertEqual(device["percent"], 42)
        self.assertEqual(device["kind"], "headset")
        self.assertTrue(device["charging"])
        self.assertNotIn("COM12", json.dumps(device))

    def test_failed_or_invalid_output_does_not_create_reading(self):
        for output in (b"", b"bad-json", b'{"port":"COM1","data":"!"}',
                       b'{"port":"wrong","data":""}'):
            with patch.object(S.sys, "platform", "win32"), patch.object(S.subprocess, "run",
                    return_value=SimpleNamespace(returncode=0, stdout=output)):
                self.assertEqual(S.SonyInzoneProvider().poll(), [])

    def test_registration(self):
        self.assertIn(("sony_inzone", "SonyInzoneProvider"), server.PROVIDERS)
        self.assertIn("sony_inzone", server.SYSTEM_PROVIDERS)
        self.assertEqual(verification_for("sony_inzone")[0], "C")
        self.assertIn("VID_054C&PID_0E53&MI_06", S.PS_SCRIPT)
        self.assertIn("$ports.Count -ne 1", S.PS_SCRIPT)


if __name__ == "__main__":
    unittest.main()