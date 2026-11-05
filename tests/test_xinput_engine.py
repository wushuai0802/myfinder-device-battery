"""System gamepad routing, evidence and deduplication without live hardware."""
import io
import json
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ENGINE = Path(__file__).resolve().parents[1] / "engine"
sys.path.insert(0, str(ENGINE))
sys.path.insert(0, str(ENGINE / "tests"))
import _hidstub  # noqa: F401
from providers.base import DeviceStatus
from providers.bluetooth import BluetoothProvider, K_BAT
from providers.xinput import XInputProvider
from test_xinput import xinput_fixture
from test_engine_reliability import FakePool, poll_fixture
from catalog import verification_for
import server


def bluetooth_pad(key="bt:xbox", name="Xbox Wireless Controller", level=82):
    return DeviceStatus(key, name, level, source="bluetooth", kind="gamepad",
                        source_quality="system_property", connection_known=True, charging_known=False)


def xinput_statuses(slots):
    with xinput_fixture(slots):
        return XInputProvider().poll()


class SystemGamepadTests(unittest.TestCase):
    def engine(self):
        engine = server.BatteryEngine([], include_native=True)
        self.addCleanup(engine.close)
        return engine

    def accept(self, engine, provider, statuses):
        engine._accept(provider, provider, statuses, server.utcnow())

    def test_bluetooth_gamepad_from_windows_battery_property_is_accepted(self):
        root = r"BTHLE\DEV_AABBCCDDEEFF\fixture"
        raw = json.dumps({"devs": [{"id": root, "name": "Xbox Wireless Controller", "status": "OK"}],
                          "props": [{"id": root, "key": K_BAT, "data": 100}],
                          "aep": [{"addr": "AABBCCDDEEFF", "conn": True, "le": True, "cls": 0x03C4}]})
        status, = BluetoothProvider().handle(raw)
        self.assertEqual(status.kind, "gamepad")
        engine = self.engine()
        self.accept(engine, "bluetooth", [status])
        device, = engine._snapshot()["devices"]
        self.assertEqual((device["percent"], device["provider"], device["transport"]),
                         (100, "bluetooth", "Bluetooth"))
        self.assertEqual(device["sourceQuality"], "system_property")
        self.assertIsNone(device["charging"])

    def test_other_hid_gamepads_and_mismatched_sources_remain_excluded(self):
        engine = self.engine()
        for provider, source in (("pulsar", "pulsar"), ("g3m", "g3m"),
                                 ("bluetooth", "flydigi"), ("xinput", "bluetooth"),
                                 ("flydigi", "xinput"), ("pulsar", "bluetooth")):
            with self.subTest(provider=provider, source=source):
                self.accept(engine, provider, [DeviceStatus("pad", "Controller", 60,
                                                           kind="gamepad", source=source)])
                self.assertEqual(engine.by_provider[provider], {})

    def test_xinput_tier_is_system_data_without_bluetooth_or_charging_claim(self):
        status, = xinput_statuses({0: (3, 2)})
        value = server.normalise(status, None, server.utcnow())
        self.assertEqual((value["percent"], value["kind"], value["verificationLevel"]), (65, "gamepad", "C"))
        self.assertTrue(value["approximate"])
        self.assertEqual(value["sourceQuality"], "system_property")
        self.assertEqual(value["measurementState"], "current")
        self.assertEqual(value["transport"], "USB / 2.4G")
        self.assertIsNone(value["measuredAtUtc"])
        self.assertIsNotNone(value["queriedAtUtc"])
        self.assertFalse(value["fresh"])
        self.assertIsNone(value["charging"])
        self.assertFalse(value["chargingKnown"])

    def test_wired_and_unknown_clear_an_earlier_wireless_percentage(self):
        engine = self.engine()
        for battery_type in (1, 0xFF):
            with self.subTest(battery_type=battery_type):
                self.accept(engine, "xinput", xinput_statuses({0: (3, 3)}))
                self.assertEqual(engine._snapshot()["devices"][0]["percent"], 100)
                self.accept(engine, "xinput", xinput_statuses({0: (battery_type, 0)}))
                value, = engine._snapshot()["devices"]
                self.assertIsNone(value["percent"])
                self.assertFalse(value["approximate"])
                self.assertEqual(value["measurementState"], "unknown")
                self.assertEqual(value["sourceQuality"], "system_property")
                self.assertEqual(value["connectionState"], "connected")
                self.assertIsNone(value["confirmedAtUtc"])

    def test_bluetooth_wins_in_either_completion_order_without_cached_duplicate(self):
        for order in (("xinput", "bluetooth"), ("bluetooth", "xinput")):
            with self.subTest(order=order):
                engine = self.engine()
                statuses = {"xinput": xinput_statuses({0: (3, 3)}), "bluetooth": [bluetooth_pad()]}
                for name in order:
                    self.accept(engine, name, statuses[name])
                    engine._snapshot(complete=False)
                for _ in range(2):
                    device, = engine._snapshot()["devices"]
                    self.assertEqual((device["provider"], device["percent"]), ("bluetooth", 82))

    def test_dedup_skips_equal_wireless_count_in_slot_order_and_keeps_wired(self):
        engine = self.engine()
        self.accept(engine, "xinput", xinput_statuses({0: (1, 0), 1: (3, 1), 2: (2, 2), 3: (3, 3)}))
        self.accept(engine, "bluetooth", [bluetooth_pad("bt:one"), bluetooth_pad("bt:two", "xBoX Elite")])
        devices = engine._snapshot()["devices"]
        remaining = {d["id"] for d in devices if d["provider"] == "xinput"}
        self.assertEqual(remaining, {server.public_id("xinput:0"), server.public_id("xinput:3")})
        self.assertEqual(len([d for d in devices if d["provider"] == "bluetooth"]), 2)

    def test_unknown_types_non_xbox_and_non_gamepads_do_not_consume_slots(self):
        for status in (bluetooth_pad(name="Other gamepad"),
                       DeviceStatus("bt:headset", "Xbox Headset", 82, source="bluetooth", kind="headset"),
                       bluetooth_pad(level=None),
                       DeviceStatus("bt:offline", "Xbox", 82, online=False, source="bluetooth", kind="gamepad")):
            with self.subTest(status=status):
                engine = self.engine()
                self.accept(engine, "xinput", xinput_statuses({0: (0xFF, 0), 1: (3, 3)}))
                self.accept(engine, "bluetooth", [status])
                self.assertEqual(len([d for d in engine._snapshot()["devices"] if d["provider"] == "xinput"]), 2)
        engine = self.engine()
        self.accept(engine, "xinput", xinput_statuses({0: (0xFF, 0), 1: (3, 3)}))
        self.accept(engine, "bluetooth", [bluetooth_pad()])
        remaining, = [d for d in engine._snapshot()["devices"] if d["provider"] == "xinput"]
        self.assertEqual(remaining["id"], server.public_id("xinput:0"))

    def test_removing_bluetooth_restores_xinput_despite_retained_bluetooth_history(self):
        engine = self.engine()
        self.accept(engine, "xinput", xinput_statuses({0: (3, 3)}))
        self.accept(engine, "bluetooth", [bluetooth_pad()])
        self.assertEqual(len(engine._snapshot()["devices"]), 1)
        self.accept(engine, "bluetooth", [])
        devices = engine._snapshot()["devices"]
        self.assertEqual(len([d for d in devices if d["provider"] == "xinput"]), 1)
        self.assertTrue(all(not d["online"] for d in devices if d["provider"] == "bluetooth"))

    def test_registration_and_isolated_poll_need_no_hid_vid_or_inventory(self):
        self.assertIn(("xinput", "XInputProvider"), server.PROVIDERS)
        self.assertNotIn("xinput", server.PROVIDER_VIDS)
        self.assertEqual(verification_for("xinput")[0], "C")
        self.assertEqual(server.recognized_candidate_count("xinput", []), 0)
        pool = FakePool([], {("xinput", ""): (0, xinput_statuses({0: (3, 3)}))})
        engine = poll_fixture(pool, ("xinput", "bluetooth"))
        self.addCleanup(engine.close)
        with patch.object(engine._hidlist, "interface_paths", return_value=frozenset()):
            device, = engine.poll(wait_bluetooth=True)["devices"]
            self.assertEqual((device["id"], device["provider"]), (server.public_id("xinput:0"), "xinput"))
            self.assertEqual(pool.calls.count(("xinput", "")), 1)
            pool.calls.clear()
            refreshed, = engine.poll(force=True, device_id=device["id"])["devices"]
        self.assertEqual(refreshed["percent"], 100)
        self.assertEqual(pool.calls, [("xinput", "")])

    def test_reader_child_uses_system_query_without_scoped_hid_poll(self):
        request = (json.dumps({"action": "query", "sequence": 1, "provider": "xinput"}) + "\n").encode()
        with xinput_fixture({0: (3, 3)}) as (dll, _), \
                patch.object(server, "scoped_poll", side_effect=AssertionError("no HID scope")), \
                patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(request))), \
                patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
            server.reader_child()
        response = json.loads(output.getvalue())
        self.assertNotIn("errorCode", response)
        self.assertEqual(response["statuses"][0]["key"], "xinput:0")
        self.assertEqual(response["statuses"][0]["level"], 100)
        self.assertEqual(dll.battery_calls, [(0, 0)])

    def test_xinput_restored_after_targeted_bluetooth_refresh_can_refresh_itself(self):
        pool = FakePool([], {("xinput", ""): (0, xinput_statuses({0: (3, 3)})),
                             ("bluetooth", ""): (0, [bluetooth_pad()])})
        engine = poll_fixture(pool, ("xinput", "bluetooth"))
        self.addCleanup(engine.close)
        with patch.object(engine._hidlist, "interface_paths", return_value=frozenset()):
            bluetooth, = engine.poll(wait_bluetooth=True)["devices"]
            self.assertEqual(bluetooth["provider"], "bluetooth")
            pool.outcomes[("bluetooth", "")] = (0, [])
            devices = engine.poll(force=True, device_id=bluetooth["id"])["devices"]
            restored, = [d for d in devices if d["provider"] == "xinput"]
            pool.calls.clear()
            refreshed = engine.poll(force=True, device_id=restored["id"])
        self.assertEqual(pool.calls, [("xinput", "")])
        self.assertEqual(refreshed["errors"], [])
        self.assertEqual([d["percent"] for d in refreshed["devices"] if d["provider"] == "xinput"], [100])


if __name__ == "__main__":
    unittest.main()
