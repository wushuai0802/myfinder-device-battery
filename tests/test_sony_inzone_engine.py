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


def payload(level=73, charging=0):
    return base64.b64encode(frame(level, charging)).decode()


def reader(output):
    return (patch.object(S.sys, "platform", "win32"),
            patch.object(S.subprocess, "run", return_value=SimpleNamespace(
                returncode=0, stdout=output, stderr=b"")))


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

    def test_captured_receiver_frames_report_level_and_charging(self):
        # Captured from an original H9 receiver (COM6): battery 0x46 = 70 %, not charging.
        captured = bytes.fromhex("04 FF 0B 00 96 C3 14 04 10 01 00 00 46 C8")
        self.assertEqual(S.parse_reply(captured), (70, False))
        # The two init replies carry other message types and must not be readings.
        for reply in ("04 FF 0A 00 96 C3 12 01 10 01 00 01 7E",
                      "04 FF 0F 00 96 C3 14 02 10 01 00 00 FF 00 00 FF 00 7E"):
            self.assertIsNone(S.parse_reply(bytes.fromhex(reply)))
        output = (json.dumps(dict(ports=1, port="COM6", writeFailed=False,
                                  data=base64.b64encode(captured).decode())) + "\n").encode()
        platform, run = reader(output)
        with platform, run:
            status, = S.SonyInzoneProvider().poll()
        self.assertEqual((status.level, status.charging, status.kind), (70, False, "headset"))

    def test_poll_and_public_headset_reading(self):
        output = (json.dumps(dict(ports=1, port="COM12", data=payload(42, 1), writeFailed=False))
                  + "\n").encode()
        platform, run = reader(output)
        with platform, run as call:
            statuses = S.SonyInzoneProvider().poll()
        self.assertEqual(len(statuses), 1)
        self.assertEqual(call.call_args.kwargs["timeout"], 8)
        engine = server.BatteryEngine([S.SonyInzoneProvider()], include_native=True)
        self.addCleanup(engine.close)
        engine._accept("sony_inzone", "sony_inzone", statuses, server.utcnow())
        device, = engine._snapshot()["devices"]
        self.assertEqual(device["percent"], 42)
        self.assertEqual(device["kind"], "headset")
        self.assertTrue(device["charging"])
        self.assertNotIn("COM12", json.dumps(device))

    def test_failed_or_invalid_output_does_not_create_reading(self):
        for output in (b"", b"bad-json", b'{"port":"COM1","data":"!"}',
                       b'{"port":"wrong","data":""}',
                       b'{"port":"COM12","data":"","writeFailed":true}'):
            with self.subTest(output=output):
                platform, run = reader(output)
                with platform, run:
                    self.assertEqual(S.SonyInzoneProvider().poll(), [])

    def test_diagnostics_describe_port_problems_without_identity(self):
        for row, reason in (({"ports": 0}, S.NO_PORT),
                            ({"ports": 2}, S.AMBIGUOUS_PORT),
                            ({"ports": 1, "port": "COM12", "data": "", "writeFailed": True}, S.PORT_FAILED),
                            ({"ports": 1, "port": "COM12", "data": "", "writeFailed": False}, S.NO_FRAME)):
            with self.subTest(reason=reason):
                platform, run = reader((json.dumps(row) + "\n").encode())
                with platform, run:
                    provider = S.SonyInzoneProvider()
                    self.assertEqual(provider.poll(), [])
                self.assertEqual(provider.diagnostics(), [reason])
                self.assertNotIn("COM12", " ".join(provider.diagnostics()))

    def test_reader_failure_is_reported_without_exception_text(self):
        with patch.object(S.sys, "platform", "win32"), patch.object(
                S.subprocess, "run", side_effect=subprocess.TimeoutExpired("powershell", 8)):
            provider = S.SonyInzoneProvider()
            self.assertEqual(provider.poll(), [])
        self.assertEqual(provider.diagnostics(), [S.READER_FAILED])

    def test_script_prefers_present_registry_ports_and_bounds_retry(self):
        for text in ("VID_054C&PID_0E53&MI_06", "GetPortNames()", "$ports.Count -ne 1",
                     "WriteTimeout = 1000", "$attempt -le 2", "writeFailed"):
            self.assertIn(text, S.PS_SCRIPT)
        # The slow WMI enumeration must never come back: it alone can outlive
        # the caller's bound and leave the child killed mid-transfer.
        self.assertNotIn("Win32_SerialPort", S.PS_SCRIPT)

    def test_only_link_faults_reach_public_health(self):
        engine = server.BatteryEngine([S.SonyInzoneProvider()], include_native=True)
        self.addCleanup(engine.close)
        engine._accept("sony_inzone", "sony_inzone", [], server.utcnow(), diagnostics=[S.PORT_FAILED])
        health = engine.health["sony_inzone"]
        self.assertEqual(health["diagnostics"], [S.PORT_FAILED])
        self.assertEqual(health["diagnosticSummary"], server.SONY_DIAGNOSTICS[S.PORT_FAILED])
        snapshot = engine._snapshot()
        self.assertEqual(snapshot["errors"], [])
        self.assertIn(server.SONY_DIAGNOSTICS[S.PORT_FAILED],
                      json.dumps(snapshot["health"], ensure_ascii=False))
        # A silent receiver or no receiver is presentation-only, not an error.
        for reason in (S.NO_FRAME, S.NO_PORT):
            engine._accept("sony_inzone", "sony_inzone", [], server.utcnow(), diagnostics=[reason])
            self.assertNotIn("diagnosticSummary", engine.health["sony_inzone"])

    def test_registration(self):
        self.assertIn(("sony_inzone", "SonyInzoneProvider"), server.PROVIDERS)
        self.assertIn("sony_inzone", server.SYSTEM_PROVIDERS)
        self.assertIn("sony_inzone", server.READER_DIAGNOSTICS)
        self.assertEqual(verification_for("sony_inzone")[0], "C")


if __name__ == "__main__":
    unittest.main()
