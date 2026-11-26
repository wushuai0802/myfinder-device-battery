"""Integration tests for Lunafury TYPE33EVO and 1.4.1-beta.4 device batch."""
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
ENGINE = ROOT / "engine"
sys.path.insert(0, str(ENGINE))
sys.path.insert(0, str(ENGINE / "tests"))

import _hidstub  # noqa: F401
import test_lunafury as L
import server


class LunafuryEngineTests(unittest.TestCase):
    def test_scoped_engine_snapshot_keeps_grade_c_and_hides_private_fields(self):
        inv = [
            L._col(0xFFFF, 0x0000, 0, {}, suffix="mi_01&col02"),
            L._col(0xFFFF, 0x0001, 0, {}, suffix="mi_01&col03"),
            L._col(0xFFA0, 0x0001, 65, {"0": 65}, suffix="mi_01&col04"),
        ]
        for row in inv:
            row["serial_number"] = "SECRET-SERIAL-1176"
        bus = L._FakeBus(feature_replies=[L._reply(81, 1)])
        provider = L.LF.LunafuryProvider()
        engine = server.BatteryEngine([provider], include_native=True)
        self.addCleanup(engine.close)
        with patch.object(L.LF.hidlist, "enumerate", return_value=inv), \
             patch.object(L.LF.hid, "device", bus), \
             patch.object(L.LF.time, "sleep"):
            snapshot = engine.poll()
        self.assertEqual(snapshot["errors"], [])
        self.assertEqual(len(snapshot["devices"]), 1)
        dev = snapshot["devices"][0]
        self.assertEqual((dev["name"], dev["percent"], dev["charging"], dev["chargingKnown"], dev["verificationLevel"]),
                         ("Lunafury TYPE33EVO", 81, True, True, "C"))
        self.assertNotIn("SECRET-SERIAL-1176", json.dumps(snapshot))

    def test_am_infinity_97_seen_projection_stays_bounded(self):
        engine = server.BatteryEngine([], include_native=True)
        self.addCleanup(engine.close)
        engine._accept(
            "device:am97", "am_infinity_97", [], server.utcnow(),
            physical=r"pnp:usb\vid_0e8d&pid_0703\synthetic", candidate_count=1,
            diagnostics=["no valid battery answer within bounds; seen=07/64,08/65"],
        )
        snapshot = engine._snapshot()
        health, = snapshot["health"]
        self.assertLessEqual(len(health["errorCode"]), 48)
        self.assertIn("seen=07/64,08/65", health["errorCode"])
        self.assertEqual(snapshot["errors"], [])


if __name__ == "__main__":
    unittest.main()
