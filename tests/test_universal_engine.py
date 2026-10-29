import importlib
import io
import json
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import patch

ENGINE = Path(__file__).resolve().parents[1] / "engine"
sys.path.insert(0, str(ENGINE))
sys.path.insert(0, str(ENGINE / "tests"))
import _hidstub
from providers.base import DeviceStatus
from providers import hidlist
from server import BatteryEngine, normalise, public_id, PROVIDERS
from standardhid import Caps, Values, percent
import ctypes


class FakeProvider:
    name = "fake"
    def __init__(self, values): self.values = values
    def poll(self):
        if isinstance(self.values, Exception): raise self.values
        return self.values


class EngineTests(unittest.TestCase):
    def test_identity_is_not_a_mac_or_serial(self):
        value = normalise(DeviceStatus("AA:BB:CC:DD:EE:FF:private-serial", "Keyboard", 42, kind="keyboard"), None, "2026-10-17T00:00:00Z")
        text = json.dumps(value)
        self.assertNotIn("private-serial", text)
        self.assertNotIn("AA:BB", text)
        self.assertEqual(value["percent"], 42)
        self.assertEqual(value["kind"], "keyboard")

    def test_invalid_measurements_stay_unknown(self):
        for level in (True, -1, 101, float("nan"), "85", None):
            reading = normalise(DeviceStatus("x", "Mouse", level), None, "now")
            self.assertIsNone(reading["percent"])
            self.assertFalse(reading["fresh"])

    def test_cached_reading_keeps_original_time(self):
        old = {"percent": 37, "confirmedAtUtc": "2026-10-16T00:00:00Z"}
        value = normalise(DeviceStatus("x", "Headset", None, online=False), old, "2026-10-17T00:00:00Z")
        self.assertEqual(value["percent"], 37)
        self.assertEqual(value["confirmedAtUtc"], old["confirmedAtUtc"])
        self.assertFalse(value["fresh"])

    def test_coarse_value_is_explicit(self):
        value = normalise(DeviceStatus("x", "Keyboard", 50, approx="medium", kind="keyboard"), None, "now")
        self.assertTrue(value["approximate"])

    def test_driver_failure_retains_verified_value(self):
        provider = FakeProvider([DeviceStatus("x", "Keyboard", 52, kind="keyboard")])
        with patch.object(hidlist, "interface_paths", return_value=frozenset()):
            engine = BatteryEngine([provider], include_native=True)
            first = engine.poll()["devices"][0]
            provider.values = RuntimeError("private USB path")
            second = engine.poll()
        self.assertEqual(second["devices"][0]["percent"], 52)
        self.assertFalse(second["devices"][0]["fresh"])
        self.assertEqual(second["devices"][0]["confirmedAtUtc"], first["confirmedAtUtc"])
        self.assertNotIn("private USB path", json.dumps(second))

    def test_native_devices_are_not_opened_by_a_second_adapter(self):
        inventory = [{"vendor_id": 0x373B, "product_id": 0x1278}, {"vendor_id": 0x03F0, "product_id": 0x098D}, {"vendor_id": 0x046D, "product_id": 0xC548}]
        with patch.object(hidlist, "enumerate", return_value=inventory):
            BatteryEngine([])
            self.assertEqual(hidlist.enumerate(), [inventory[2]])

    def test_declared_hid_ranges_and_abi(self):
        self.assertEqual(ctypes.sizeof(Caps), 64)
        self.assertEqual(ctypes.sizeof(Values), 72)
        self.assertEqual(percent(42, 0, 100), 42)
        self.assertEqual(percent(255, 0, 255), 100)
        self.assertEqual(percent(2, 0, 4), 50)
        self.assertIsNone(percent(101, 0, 100))
        self.assertIsNone(percent(2, 0, 0))

    def test_voltage_is_labeled_as_an_estimate(self):
        from providers.logitech import parse_battery, F_VOLTAGE
        level, charging, approx = parse_battery(F_VOLTAGE, [0x0F, 0xA0, 0])
        self.assertTrue(0 <= level <= 100)
        self.assertTrue(approx)

    def test_all_selected_providers_import_without_hardware(self):
        for module, name in PROVIDERS:
            self.assertTrue(hasattr(importlib.import_module("providers." + module), name))


if __name__ == "__main__":
    unittest.main()
