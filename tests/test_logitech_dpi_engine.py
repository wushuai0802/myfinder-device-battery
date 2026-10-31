"""Identity-bound DPI IPC regressions. No live HID or Windows settings."""
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "engine"))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "engine" / "tests"))
import _hidstub
from providers.base import DeviceStatus
from server import BatteryEngine, historical, normalise, public_id, utcnow
from isolation import IsolationError
from test_engine_reliability import FakePool


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


class BluetoothDpiSchedulingTests(unittest.TestCase):
    def setUp(self):
        status = DeviceStatus("gpw-fixture", "PRO X Wireless", 75, source="logitech", kind="mouse",
                              dpi=evidence(), dpi_target={"slot": 1, "unit": "private-fixture"})
        self.pool = FakePool([{"physical_id": PHYSICAL, "vendor_id": 0x046D, "product_id": 0xC547}], {
            ("bluetooth", ""): (100, []),
            ("logitech", PHYSICAL): (0, [status]),
            ("set_dpi", PHYSICAL): (0, {"dpi": evidence(800), "success": True,
                                       "outcomeUnknown": False, "error": None, "confirmedDpi": 800}),
        })
        self.engine = BatteryEngine(process_factory=self.pool.factory)
        self.engine.providers = ["logitech", "bluetooth"]
        self.addCleanup(self.engine.close)
        clock = patch("server.time.monotonic", return_value=1000.0)
        self.clock = clock.start()
        self.addCleanup(clock.stop)
        sleeper = patch("server.time.sleep", side_effect=self.advance)
        sleeper.start()
        self.addCleanup(sleeper.stop)
        paths = patch.object(self.engine._hidlist, "interface_paths", return_value=frozenset())
        paths.start()
        self.addCleanup(paths.stop)
        self.device_id = public_id(status.key + ":instance:" + PHYSICAL)

    def advance(self, seconds):
        self.clock.return_value += seconds

    def change(self):
        return self.engine.set_dpi(self.device_id, 800, TOKEN, 400, 7, 2)

    def test_dpi_proceeds_while_bluetooth_uses_other_slot(self):
        original = self.engine.poll()["devices"][0]
        pending = self.pool.children[1].pending
        started = self.clock.return_value
        result = self.change()
        self.assertTrue(result["dpiResult"]["success"])
        self.assertLess(self.clock.return_value - started, .1)
        self.assertEqual(self.pool.calls.count(("set_dpi", PHYSICAL)), 1)
        self.assertIs(self.pool.children[1].pending, pending)
        self.assertEqual(self.pool.children[1].closed, 0)
        self.assertEqual((result["requestId"], result["generation"]), (7, 2))
        for field in ("measuredAtUtc", "confirmedAtUtc", "queriedAtUtc", "chargingCheckedAtUtc"):
            self.assertEqual(result["devices"][0][field], original[field])

    def test_dpi_reclaims_bluetooth_slot_without_failure_or_backoff_changes(self):
        self.engine.poll()
        self.engine._affinity["logitech|" + PHYSICAL] = 1
        self.engine._bluetooth_empty_polls = 2
        self.engine.last_poll["bluetooth"] = 900
        started = self.clock.return_value
        result = self.change()
        self.assertTrue(result["dpiResult"]["success"])
        self.assertLess(self.clock.return_value - started, .1)
        self.assertEqual(self.pool.children[1].closed, 1)
        self.assertEqual(self.engine._bluetooth_empty_polls, 2)
        self.assertEqual(self.engine.last_poll["bluetooth"], 900)
        for state in (self.engine.failures, self.engine.retry_after, self.engine.health):
            self.assertNotIn("bluetooth", state)
        self.assertIsNone(self.engine._bluetooth_slot)
        self.pool.outcomes[("bluetooth", "")] = (0, [])
        self.assertEqual(self.engine.poll()["errors"], [])
        self.assertEqual(self.pool.calls.count(("bluetooth", "")), 2)
        self.assertEqual(self.pool.calls.count(("set_dpi", PHYSICAL)), 1)

    def test_completed_bluetooth_is_collected_before_dpi(self):
        self.pool.outcomes[("bluetooth", "")] = (.5, [DeviceStatus("bt:keyboard", "Bluetooth Keyboard", 55, source="bluetooth")])
        self.engine.poll()
        self.engine._bluetooth_empty_polls = 3
        self.advance(1)
        result = self.change()
        self.assertTrue(result["dpiResult"]["success"])
        self.assertEqual(next(d["percent"] for d in result["devices"] if d["provider"] == "bluetooth"), 55)
        self.assertEqual(self.engine._bluetooth_empty_polls, 0)
        self.assertEqual(self.engine.last_poll["bluetooth"], self.clock.return_value)
        self.assertIsNone(self.pool.children[1].pending)
        self.assertEqual(self.pool.calls.count(("bluetooth", "")), 1)

    def test_bluetooth_timeout_is_collected_before_dpi(self):
        self.engine.poll()
        self.advance(14.1)
        result = self.change()
        self.assertTrue(result["dpiResult"]["success"])
        self.assertIn("bluetooth: timeout", result["errors"])
        self.assertEqual(self.engine.failures["bluetooth"], 1)
        self.assertEqual(self.pool.calls.count(("set_dpi", PHYSICAL)), 1)

    def test_unknown_dpi_after_reclaim_is_never_retried(self):
        self.engine.poll()
        self.engine._affinity["logitech|" + PHYSICAL] = 1
        self.pool.outcomes[("set_dpi", PHYSICAL)] = (0, IsolationError("timeout"))
        result = self.change()
        self.assertTrue(result["dpiResult"]["outcomeUnknown"])
        self.assertFalse(result["devices"][0]["dpi"]["canSet"])
        self.assertFalse(self.change()["dpiResult"]["success"])
        self.assertEqual(self.pool.calls.count(("set_dpi", PHYSICAL)), 1)
        self.assertNotIn("bluetooth", self.engine.failures)
        self.assertNotIn("bluetooth", self.engine.retry_after)

    def test_invalid_dpi_target_does_not_cancel_bluetooth(self):
        self.engine.poll()
        self.engine._affinity["logitech|" + PHYSICAL] = 1
        pending = self.pool.children[1].pending
        result = self.engine.set_dpi(self.device_id, 800, "b" * 64, 400)
        self.assertFalse(result["dpiResult"]["success"])
        self.assertIs(self.pool.children[1].pending, pending)
        self.assertEqual(self.pool.children[1].closed, 0)
        self.assertNotIn(("set_dpi", PHYSICAL), self.pool.calls)


if __name__ == "__main__":
    unittest.main()
