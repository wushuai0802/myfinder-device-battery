"""Identity-bound DPI IPC regressions. No live HID or Windows settings."""
import sys
import types
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "engine"))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "engine" / "tests"))
import _hidstub
from providers.base import DeviceStatus
from server import BatteryEngine, historical, normalise, public_id, utcnow
from isolation import IsolationError


TOKEN = "a" * 64
PHYSICAL = "fixture-receiver-a"


def evidence(current=400):
    return {"supported": True, "canSet": True, "current": current,
            "stages": [400, 800, 1600, 3200], "stageSource": "quick",
            "checkedAtUtc": utcnow(), "targetToken": TOKEN, "error": None}


class NoReader:
    def close(self):
        pass


class DpiEngine(BatteryEngine):
    def __init__(self):
        super().__init__(process_factory=NoReader)
        self.commands = []
        self.answer = None
        self.present = True
        self.status = DeviceStatus("gpw-fixture", "PRO X Wireless", 75, False, True,
                                  "logitech", kind="mouse", physical_id=PHYSICAL,
                                  dpi=evidence(), dpi_target={"slot": 1, "unit": "private-fixture"})
        self.device_id = public_id(self.status.key)
        self._accept("logitech|" + PHYSICAL, "logitech", [self.status], utcnow(), PHYSICAL)
        self._snapshot()

    def _single_reader(self, slot, command, budget):
        self.commands.append(command)
        if command["action"] == "topology":
            return {"inventory": [{"physical_id": PHYSICAL, "vendor_id": 0x046D}]
                    if self.present else []}
        if isinstance(self.answer, Exception):
            raise self.answer
        return self.answer or {"dpi": evidence(command["dpi"]), "success": True,
                               "outcomeUnknown": False, "error": None,
                               "confirmedDpi": command["dpi"]}

    def change(self, value=800, token=TOKEN, expected=400):
        with patch.object(self._hidlist, "interface_paths", return_value=frozenset()):
            return self.set_dpi(self.device_id, value, token, expected, 7, 2)


class LogitechDpiEngineTests(unittest.TestCase):
    def test_one_scoped_write_and_readback_preserve_battery_evidence(self):
        engine = DpiEngine()
        original = engine.previous[engine.device_id].copy()
        reply = engine.change()
        self.assertTrue(reply["dpiResult"]["success"])
        self.assertEqual((reply["requestId"], reply["generation"]), (7, 2))
        self.assertEqual([c["action"] for c in engine.commands], ["topology", "set_dpi"])
        self.assertEqual(engine.commands[1]["physicalId"], PHYSICAL)
        device = reply["devices"][0]
        self.assertEqual(device["dpi"]["current"], 800)
        for key in ("measuredAtUtc", "confirmedAtUtc", "queriedAtUtc", "chargingCheckedAtUtc"):
            self.assertEqual(device[key], original[key])
        self.assertNotIn("private-fixture", str(reply))

    def test_stale_token_rejected_before_any_hardware_access(self):
        engine = DpiEngine()
        reply = engine.change(token="b" * 64)
        self.assertFalse(reply["dpiResult"]["success"])
        self.assertEqual(engine.commands, [])
        self.assertFalse(reply["devices"][0]["dpi"]["canSet"])

    def test_expected_current_and_offered_values_are_checked(self):
        for kwargs, error in (({"expected": 1600}, "current_changed"),
                              ({"value": 450}, "unsupported_dpi"),
                              ({"value": True}, "invalid_request")):
            engine = DpiEngine()
            reply = engine.change(**kwargs)
            self.assertEqual(reply["dpiResult"]["error"], error)
            self.assertEqual(engine.commands, [])

    def test_disconnected_receiver_does_not_dispatch_write(self):
        engine = DpiEngine()
        engine.present = False
        reply = engine.change()
        self.assertEqual(reply["dpiResult"]["error"], "disconnected")
        self.assertEqual([c["action"] for c in engine.commands], ["topology"])

    def test_timeout_is_unknown_and_never_retried(self):
        engine = DpiEngine()
        engine.answer = IsolationError("timeout")
        reply = engine.change()
        self.assertTrue(reply["dpiResult"]["outcomeUnknown"])
        self.assertIsNone(reply["devices"][0]["dpi"]["current"])
        self.assertIsNone(reply["devices"][0]["dpi"]["targetToken"])
        self.assertTrue(reply["devices"][0]["fresh"])
        engine.change()
        self.assertEqual(len(engine.commands), 2)

    def test_only_fresh_poll_can_restore_capability_after_unknown(self):
        engine = DpiEngine()
        engine.answer = IsolationError("timeout")
        engine.change()
        engine.answer = None
        engine.status.dpi = evidence(800)
        engine._accept("logitech|" + PHYSICAL, "logitech", [engine.status], utcnow(), PHYSICAL)
        engine._snapshot()
        reply = engine.change(value=1600, expected=800)
        self.assertTrue(reply["dpiResult"]["success"])

    def test_cached_sleeping_or_expired_dpi_is_display_only(self):
        engine = DpiEngine()
        original = engine.previous[engine.device_id]
        cached = historical(original)
        self.assertFalse(cached["dpi"]["canSet"])
        self.assertIsNone(cached["dpi"]["targetToken"])
        self.assertEqual(cached["dpi"]["current"], 400)
        engine.status.online = False
        self.assertFalse(normalise(engine.status, original, utcnow())["dpi"]["canSet"])
        engine.status.online = True
        engine.status.dpi["checkedAtUtc"] = "2020-01-01T00:00:00Z"
        self.assertFalse(normalise(engine.status, original, utcnow())["dpi"]["canSet"])

    def test_reader_failure_drops_private_capability(self):
        engine = DpiEngine()
        engine._failure("logitech|" + PHYSICAL, "logitech", "timeout")
        self.assertNotIn(engine.device_id, engine.dpi_targets)
        reply = engine.change()
        self.assertEqual(reply["dpiResult"]["error"], "stale_target")
        self.assertEqual(engine.commands, [])


if __name__ == "__main__":
    unittest.main()
