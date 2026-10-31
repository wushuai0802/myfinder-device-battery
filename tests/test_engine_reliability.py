"""Evidence/identity/scheduling regressions; no live hardware is enumerated.

Real subprocess tests run only local fixture code, including a hung reader and
its descendant. Protocol tests replace HID queries before scoped_poll is used.
"""
from __future__ import annotations

import ctypes
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import unittest
from dataclasses import asdict
from unittest.mock import patch

ENGINE = Path(__file__).resolve().parents[1] / "engine"
sys.path.insert(0, str(ENGINE))
sys.path.insert(0, str(ENGINE / "tests"))
import _hidstub
from providers.base import DeviceStatus
from providers.bluetooth import BluetoothProvider, BluetoothWatcher, K_BAT
from providers import hidlist
from server import BatteryEngine, normalise, public_id, scoped_poll, utcnow, recognized_candidate_count
from isolation import ReaderProcess, IsolationError
from topology import physical_groups, physical_instance, discover
from catalog import load_catalog, validate_catalog, verification_for, markdown


def bt_snapshot(level=75, connected=True):
    root = r"BTHENUM\DEV_AABBCCDDEEFF\7&private"
    data = {"devs": [{"id": root, "name": "Test Keyboard", "status": "OK"}],
            "props": [], "aep": [{"addr": "AABBCCDDEEFF", "conn": connected, "le": False, "cls": 0x0540}]}
    if level is not None:
        data["props"].append({"id": root, "key": K_BAT, "data": level})
    return json.dumps(data)


def receiver(which, pid=0x0025):
    return {"vendor_id": 0x388D, "product_id": pid, "path": ("fixture-" + which).encode(),
            "usage_page": 0xFF1C, "usage": 0x0092, "product_string": "Hyzen", "physical_id": "physical-" + which}


class FakePool:
    def __init__(self, inventory=(), outcomes=None):
        self.inventory = list(inventory)
        self.outcomes = outcomes or {}
        self.calls = []
        self.active = {}
        self.max_active = 0
        self.physical_overlap = False
        self.children = []

    def factory(self):
        child = FakeReader(self)
        self.children.append(child)
        return child


class FakeReader:
    def __init__(self, pool):
        self.pool = pool
        self.pending = None
        self.closed = 0

    def start(self, command, budget):
        if self.pending:
            raise AssertionError("fixture reader reentered")
        physical = command.get("physicalId", "")
        if physical and physical in self.pool.active.values():
            self.pool.physical_overlap = True
        self.pool.active[id(self)] = physical
        self.pool.max_active = max(self.pool.max_active, len(self.pool.active))
        name = command.get("provider", command["action"])
        self.pool.calls.append((name, physical))
        delay, outcome = self.pool.outcomes.get((name, physical), (0, []))
        self.pending = (time.monotonic(), delay, budget, command, outcome)

    def result(self):
        if not self.pending:
            return None
        start, delay, budget, command, outcome = self.pending
        elapsed = time.monotonic() - start
        if elapsed < min(delay, budget):
            return None
        self.pending = None
        self.pool.active.pop(id(self), None)
        if delay > budget:
            self.closed += 1
            raise IsolationError("timeout")
        if isinstance(outcome, Exception):
            raise outcome
        if command["action"] == "topology":
            return {"inventory": self.pool.inventory}
        if command["action"] == "smoke":
            return {"providerCount": 26, "devices": []}
        physical = command.get("physicalId", "")
        statuses = []
        for value in outcome:
            value = dict(asdict(value))
            if physical:
                value.update(physical_id=physical, legacy_key=value["key"], key=value["key"] + ":instance:" + physical)
            statuses.append(value)
        return {"statuses": statuses, "checkedAtUtc": utcnow()}

    def close(self):
        self.pending = None
        self.pool.active.pop(id(self), None)
        self.closed += 1


def poll_fixture(pool, providers=("lofree",), **kwargs):
    engine = BatteryEngine(process_factory=pool.factory)
    engine.providers = list(providers)
    return engine


class MeasurementEvidenceTests(unittest.TestCase):
    def test_missing_bt_property_keeps_timestamp_and_is_cached(self):
        provider = BluetoothProvider()
        first = provider.handle(bt_snapshot())[0]
        value = normalise(first, None, "2026-10-17T00:00:00Z")
        second = provider.handle(bt_snapshot(None))[0]
        again = normalise(second, value, "2026-10-17T01:00:00Z")
        self.assertTrue(second.cached)
        self.assertEqual(again["percent"], 75)
        self.assertEqual(again["confirmedAtUtc"], value["confirmedAtUtc"])
        self.assertEqual(again["queriedAtUtc"], value["queriedAtUtc"])
        self.assertEqual(again["measurementState"], "cached")
        self.assertFalse(again["fresh"])
        self.assertIsNone(again["charging"])

    def test_bt_system_property_is_not_device_measurement(self):
        first = BluetoothProvider().handle(bt_snapshot())[0]
        value = normalise(first, None, utcnow())
        self.assertEqual(value["sourceQuality"], "system_property")
        self.assertFalse(value["fresh"])
        self.assertIsNone(value["measuredAtUtc"])
        self.assertIsNotNone(value["queriedAtUtc"])
        self.assertIsNone(value["chargingCheckedAtUtc"])

    def test_bt_smoothing_does_not_refresh_missing_device(self):
        provider = BluetoothProvider()
        first = provider.handle(bt_snapshot())[0]
        second = provider.handle(json.dumps({"devs": [], "props": [], "aep": []}))[0]
        self.assertFalse(first.cached)
        self.assertTrue(second.cached)
        self.assertFalse(second.connection_known)
        self.assertEqual(second.queried_at_utc, first.queried_at_utc)

    def test_equal_real_reply_is_a_new_measurement(self):
        first = normalise(DeviceStatus("same", "Mouse", 75), None, "2026-10-17T00:00:00Z")
        again = normalise(DeviceStatus("same", "Mouse", 75), first, "2026-10-17T01:00:00Z")
        self.assertTrue(again["fresh"])
        self.assertEqual(again["confirmedAtUtc"], "2026-10-17T01:00:00Z")

    def test_charging_unknown_is_not_false(self):
        unknown = normalise(DeviceStatus("k", "Keyboard", 75, source="standard_hid"), None, utcnow())
        known = normalise(DeviceStatus("m", "Mouse", 75, charging=False, source="fake", charging_known=True), None, utcnow())
        self.assertIsNone(unknown["charging"])
        self.assertFalse(unknown["chargingKnown"])
        self.assertIs(known["charging"], False)
        self.assertTrue(known["chargingKnown"])

    def test_invalid_bt_values_are_never_clamped(self):
        for value in (-1, 101, True, "75", 0.5):
            self.assertEqual(BluetoothProvider().handle(bt_snapshot(value)), [], value)

    def test_unnamed_bluetooth_device_does_not_leak_mac_as_name(self):
        data = json.loads(bt_snapshot())
        data["devs"][0]["name"] = ""
        status = BluetoothProvider().handle(json.dumps(data))[0]
        value = normalise(status, None, utcnow())
        self.assertNotIn("AABBCCDDEEFF", json.dumps(value))
        self.assertEqual(value["name"], "Bluetooth device")

    def test_cached_value_cannot_resurrect_charging(self):
        first = normalise(DeviceStatus("m", "Mouse", 50, charging=True), None, utcnow())
        again = normalise(DeviceStatus("m", "Mouse", 50, charging=True, cached=True), first, utcnow())
        self.assertIsNone(again["charging"])
        self.assertIsNone(again["chargingCheckedAtUtc"])

    def test_silent_receiver_is_not_confirmed_disconnected(self):
        value = normalise(DeviceStatus("m", "Mouse", 50, online=False), None, utcnow())
        self.assertEqual(value["connectionState"], "unconfirmed")

    def test_cached_bt_battery_does_not_freeze_connection_timestamp(self):
        provider = BluetoothProvider()
        first = normalise(provider.handle(bt_snapshot())[0], None, "2026-10-17T00:00:00Z")
        second = normalise(provider.handle(bt_snapshot(None))[0], first, "2026-10-17T01:00:00Z")
        self.assertEqual(second["connectionCheckedAtUtc"], "2026-10-17T01:00:00Z")
        self.assertEqual(second["confirmedAtUtc"], first["confirmedAtUtc"])

    def test_future_history_is_not_kept(self):
        engine = BatteryEngine([], include_native=True)
        engine.previous[public_id("x")] = normalise(DeviceStatus("x", "Mouse", 52), None, "2999-01-01T00:00:00Z")
        self.assertEqual(engine.poll()["devices"], [])
        engine.close()

    def test_expired_cached_value_becomes_unknown(self):
        engine = BatteryEngine([], include_native=True)
        value = normalise(DeviceStatus("x", "Mouse", 52), None, "2020-01-01T00:00:00Z")
        engine.by_provider["fake"] = {value["id"]: dict(value, measurementState="cached")}
        device = engine.poll()["devices"][0]
        self.assertIsNone(device["percent"])
        self.assertEqual(device["measurementState"], "unknown")
        engine.close()


class BluetoothPollingTests(unittest.TestCase):
    def setUp(self):
        self.pool = FakePool()
        self.engine = poll_fixture(self.pool, ("bluetooth",))
        self.addCleanup(self.engine.close)
        clock = patch("server.time.monotonic", return_value=1000.0)
        self.clock = clock.start()
        self.addCleanup(clock.stop)
        paths = patch.object(hidlist, "interface_paths", return_value=frozenset())
        self.paths = paths.start()
        self.addCleanup(paths.stop)

    def poll_at(self, stamp, queried=True, **kwargs):
        self.clock.return_value = stamp
        before = self.pool.calls.count(("bluetooth", ""))
        result = self.engine.poll(**kwargs)
        self.assertEqual(self.pool.calls.count(("bluetooth", "")) - before, int(queried))
        return result

    def empty_backoff(self):
        for stamp in (1000, 1060, 1120):
            self.poll_at(stamp)

    def test_three_empty_successes_change_interval_from_60_to_300_seconds(self):
        self.poll_at(1000)
        self.poll_at(1059, queried=False)
        self.poll_at(1060)
        self.poll_at(1119, queried=False)
        self.poll_at(1120)
        for stamp in (1180, 1300, 1419):
            self.poll_at(stamp, queried=False)
        self.poll_at(1421)

    def test_devices_without_battery_also_count_as_empty_successes(self):
        unknown = DeviceStatus("bt:unknown", "Keyboard", None, source="bluetooth")
        self.pool.outcomes[("bluetooth", "")] = (0, [unknown])
        self.empty_backoff()
        self.poll_at(1180, queried=False)
        self.poll_at(1420)

    def test_battery_result_restores_60_seconds_and_clears_empty_count(self):
        self.empty_backoff()
        unknown = DeviceStatus("bt:unknown", "Keyboard", None, source="bluetooth")
        battery = DeviceStatus("bt:battery", "Mouse", 0, source="bluetooth")
        self.pool.outcomes[("bluetooth", "")] = (0, [unknown, battery])
        self.poll_at(1121, force=True)
        self.poll_at(1180, queried=False)
        self.poll_at(1181)
        self.pool.outcomes[("bluetooth", "")] = (0, [])
        for stamp in (1241, 1301, 1361):
            self.poll_at(stamp)
        self.poll_at(1421, queried=False)

    def assert_failure_does_not_count(self, code):
        self.poll_at(1000)
        self.pool.outcomes[("bluetooth", "")] = (0, IsolationError(code))
        failed = self.poll_at(1060)
        self.assertIn("bluetooth: " + code, failed["errors"])
        self.assertEqual(self.engine.failures["bluetooth"], 1)
        self.assertEqual(self.engine.retry_after["bluetooth"], 1065)
        self.assertEqual(self.engine.last_poll["bluetooth"], 1000)
        self.poll_at(1061, queried=False)
        self.pool.outcomes[("bluetooth", "")] = (0, [])
        self.poll_at(1065)
        self.poll_at(1125)
        self.poll_at(1185, queried=False)

    def test_failures_do_not_advance_or_reset_empty_count(self):
        self.assert_failure_does_not_count("read_failed")

    def test_timeouts_do_not_advance_or_reset_empty_count(self):
        self.assert_failure_does_not_count("timeout")

    def test_force_always_queries_and_its_results_count(self):
        for _ in range(3):
            self.poll_at(1000, force=True)
        self.poll_at(1060, queried=False)
        self.poll_at(1060, force=True)
        self.pool.outcomes[("bluetooth", "")] = (0, IsolationError("read_failed"))
        self.poll_at(1060, force=True)
        self.pool.outcomes[("bluetooth", "")] = (0, [])
        self.poll_at(1060, force=True)
        self.poll_at(1120, queried=False)

    def test_hid_path_changes_restore_60_seconds_and_clear_empty_count(self):
        self.empty_backoff()
        self.poll_at(1180, queried=False)
        self.paths.return_value = frozenset({r"\\?\hid#bthenum#fixture"})
        for stamp in (1180, 1240, 1300):
            self.poll_at(stamp)
        self.poll_at(1360, queried=False)
        self.paths.return_value = frozenset()
        self.poll_at(1360)


class BluetoothCommandTests(unittest.TestCase):
    def test_query_and_watcher_use_systemroot_absolute_powershell_path(self):
        system_root = str(Path(__file__).resolve().parent / "Windows fixture")
        process = subprocess.CompletedProcess([], 0, stdout=b"{}", stderr=b"")
        with patch.dict(os.environ, {"SystemRoot": system_root}, clear=True), \
                patch("providers.bluetooth.subprocess.run", return_value=process) as run:
            provider = BluetoothProvider()
            self.assertEqual(provider._query(), "{}")
            query = run.call_args.args[0]
            watcher = BluetoothWatcher(provider, lambda _: None).command()
        expected = os.path.join(system_root, "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
        for command in (query, watcher):
            self.assertTrue(os.path.isabs(command[0]))
            self.assertEqual(command[0], expected)
            self.assertEqual(command[1:6], ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command"])

    def test_missing_systemroot_fails_without_starting_powershell(self):
        with patch.dict(os.environ, {}, clear=True), \
                patch("providers.bluetooth.subprocess.run") as run, \
                patch("providers.bluetooth.subprocess.Popen") as popen:
            provider = BluetoothProvider()
            self.assertIsNone(provider._query())
            with patch("providers.bluetooth.sys.platform", "win32"):
                engine = BatteryEngine([provider], include_native=True)
                self.addCleanup(engine.close)
                self.assertIn("bluetooth: read_failed", engine.poll()["errors"])
            watcher = BluetoothWatcher(provider, lambda _: None)
            with self.assertRaises(OSError):
                watcher.command()
            with self.assertLogs("halo_battery", level="WARNING"):
                watcher._run()
            self.assertTrue(watcher.failed)
            run.assert_not_called()
            popen.assert_not_called()

    def test_unusable_query_output_is_a_poll_failure(self):
        provider = BluetoothProvider()
        for raw in (None, "", "invalid json"):
            with self.subTest(raw=raw), patch("providers.bluetooth.sys.platform", "win32"), \
                    patch.object(provider, "_query", return_value=raw):
                with self.assertRaises(RuntimeError):
                    provider.poll()

    def test_unsuccessful_powershell_exit_is_a_query_failure(self):
        process = subprocess.CompletedProcess([], 1, stdout=b"{}", stderr=b"failed")
        with patch.dict(os.environ, {"SystemRoot": str(ENGINE)}, clear=True), \
                patch("providers.bluetooth.subprocess.run", return_value=process):
            self.assertIsNone(BluetoothProvider()._query())


class IdentityTests(unittest.TestCase):
    @unittest.skipUnless(os.name == "nt", "Windows descriptor discovery")
    def test_native_interface_excluded_before_createfile(self):
        native = r"\\?\hid#vid_373b&pid_1278&mi_02#fixture"
        with patch("topology.c.WinDLL") as dll:
            self.assertEqual(discover([native], {(0x373B, 0x1278)}), [])
            dll.return_value.CreateFileW.assert_not_called()

    def test_equal_name_and_pid_are_not_grouping_evidence(self):
        a, b = receiver("a"), receiver("b")
        self.assertEqual(len(physical_groups([a, b])), 2)
        with patch("topology.usb_parent", return_value=None):
            self.assertNotEqual(physical_instance(dict(a, physical_id="")), physical_instance(dict(b, physical_id="")))

    def test_lofree_receivers_are_read_without_wired_false_suppression(self):
        from providers.lofree import LofreeProvider
        a, b, wired = receiver("a"), receiver("b"), receiver("wired", 0x0024)
        values = {a["path"]: 25, b["path"]: 75}
        statuses = []
        with patch.object(LofreeProvider, "_read", side_effect=lambda path: values[path]):
            for physical in (a["physical_id"], b["physical_id"]):
                statuses.extend(scoped_poll(LofreeProvider(), [a, b, wired], physical))
        self.assertEqual([s.level for s in statuses], [25, 75])
        self.assertNotEqual(statuses[0].key, statuses[1].key)

    def test_first_only_reader_sees_each_physical_receiver(self):
        from providers.keychron import KeychronProvider, KEYCHRON_VID
        a, b = receiver("a"), receiver("b")
        for value in (a, b):
            value.update(vendor_id=KEYCHRON_VID, product_id=0xD028, interface_number=4)
        reply = [0xB4, 0x06] + [0] * 18 + [50]
        with patch.object(KeychronProvider, "_query", return_value=reply):
            first = scoped_poll(KeychronProvider(), [a, b], a["physical_id"])
            second = scoped_poll(KeychronProvider(), [a, b], b["physical_id"])
        self.assertEqual(len(first), 1)
        self.assertEqual(len(second), 1)
        self.assertNotEqual(first[0].key, second[0].key)

    def test_receiver_slots_remain_distinct(self):
        class Slots:
            def poll(self):
                return [DeviceStatus("slot:1", "Same Mouse", 25), DeviceStatus("slot:2", "Same Mouse", 75)]
        items = scoped_poll(Slots(), [receiver("a")], "physical-a")
        self.assertEqual(len({s.key for s in items}), 2)

    def test_receiver_multiple_slots_explicitly_refresh_same_group(self):
        class Slots:
            name = "slots"
            def poll(self):
                return [DeviceStatus("slot:1", "A", 25), DeviceStatus("slot:2", "B", 75)]
        statuses = scoped_poll(Slots(), [receiver("a")], "physical-a")
        self.assertTrue(all(normalise(s, None, utcnow())["refreshScope"] == "provider" for s in statuses))

    def test_logitech_single_reply_still_queries_receiver_group(self):
        value = normalise(DeviceStatus("slot:1", "A", 25, source="logitech", physical_id="physical-a"), None, utcnow())
        self.assertEqual(value["refreshScope"], "provider")

    def test_single_unit_adapter_retains_device_scope(self):
        value = normalise(DeviceStatus("a", "A", 25, source="lofree", physical_id="physical-a"), None, utcnow())
        self.assertEqual(value["refreshScope"], "device")

    def test_parent_native_ids_never_enter_query_tasks(self):
        native = dict(receiver("native"), vendor_id=0x373B, product_id=0x1278)
        pool = FakePool([native])
        engine = poll_fixture(pool, ("pulsar", "standard_hid"))
        with patch.object(hidlist, "interface_paths", return_value=frozenset()):
            engine.poll()
        self.assertFalse(any(physical for name, physical in pool.calls))
        engine.close()

    def test_multi_instance_legacy_id_is_suppressed_before_partial(self):
        outcomes = {("lofree", "physical-a"): (0, [DeviceStatus("lofree:0025", "Same", 25, source="lofree")]),
                    ("lofree", "physical-b"): (0.04, [DeviceStatus("lofree:0025", "Same", 75, source="lofree")])}
        pool = FakePool([receiver("a"), receiver("b")], outcomes)
        engine = poll_fixture(pool)
        frames = []
        with patch.object(hidlist, "interface_paths", return_value=frozenset()):
            result = engine.poll(progress=frames.append)
        self.assertEqual(len(result["devices"]), 2)
        self.assertTrue(all(d["legacyId"] is None for frame in frames for d in frame["devices"]))
        self.assertEqual({d["percent"] for d in result["devices"]}, {25, 75})
        engine.close()

    def test_shared_receiver_parent_does_not_merge_different_slots(self):
        engine = BatteryEngine([], include_native=True)
        one = normalise(DeviceStatus("slot:1", "Same", 25, source="logitech", physical_id="receiver"), None, utcnow())
        other = normalise(DeviceStatus("standard", "Same", 75, source="standard_hid", physical_id="receiver"), None, utcnow())
        engine.by_provider = {"vendor": {one["id"]: one}, "standard": {other["id"]: other}}
        self.assertEqual(len(engine.poll()["devices"]), 2)
        engine.close()

    def test_proven_unit_identity_allows_specialist_priority(self):
        engine = BatteryEngine([], include_native=True)
        one = normalise(DeviceStatus("v", "Mouse", 25, source="logitech", physical_id="receiver", unit_id="slot1"), None, utcnow())
        other = normalise(DeviceStatus("s", "Mouse", 75, source="standard_hid", physical_id="receiver", unit_id="slot1"), None, utcnow())
        engine.by_provider = {"vendor": {one["id"]: one}, "standard": {other["id"]: other}}
        engine.previous = {other["id"]: other}
        self.assertEqual([d["percent"] for d in engine.poll()["devices"]], [25])
        engine.close()


class TopologyReconciliationTests(unittest.TestCase):
    def setUp(self):
        paths = patch.object(hidlist, "interface_paths", return_value=frozenset())
        paths.start()
        self.addCleanup(paths.stop)

    def fixture(self, pool, providers=("lofree",)):
        engine = poll_fixture(pool, providers)
        self.addCleanup(engine.close)
        return engine

    def assert_reader_removed(self, engine, key):
        for state in (engine.by_provider, engine.health, engine.last_poll,
                      engine._affinity, engine.retry_after, engine.failures):
            self.assertNotIn(key, state)

    def test_targeted_removal_keeps_history_and_other_receiver_unchanged(self):
        pool = FakePool([receiver("a"), receiver("b")], {
            ("lofree", "physical-a"): (0, [DeviceStatus("a", "A", 25, source="lofree")]),
            ("lofree", "physical-b"): (0, [DeviceStatus("b", "B", 75, source="lofree")])})
        engine = self.fixture(pool)
        first = {d["name"]: d for d in engine.poll()["devices"]}
        other_health = dict(engine.health["lofree|physical-b"])
        other_poll = engine.last_poll["lofree|physical-b"]
        other_slot = engine._affinity["lofree|physical-b"]
        pool.inventory[:] = [receiver("b")]
        pool.calls.clear()
        frames = []
        second = engine.poll(force=True, device_id=first["A"]["id"], progress=frames.append)
        current = {d["name"]: d for d in second["devices"]}
        self.assertEqual(pool.calls, [("topology", "")])
        self.assertEqual(current["A"]["percent"], first["A"]["percent"])
        for field in ("confirmedAtUtc", "measuredAtUtc", "queriedAtUtc", "connectionCheckedAtUtc"):
            self.assertEqual(current["A"][field], first["A"][field])
        self.assertEqual(current["A"]["connectionState"], "unconfirmed")
        self.assertEqual(current["A"]["measurementState"], "cached")
        self.assertFalse(current["A"]["fresh"])
        self.assertFalse(current["A"]["online"])
        self.assertEqual(current["B"], first["B"])
        self.assertEqual(engine.health["lofree|physical-b"], other_health)
        self.assertEqual(engine.last_poll["lofree|physical-b"], other_poll)
        self.assertEqual(engine._affinity["lofree|physical-b"], other_slot)
        self.assert_reader_removed(engine, "lofree|physical-a")
        self.assertTrue(all(not d["fresh"] and d["connectionState"] == "unconfirmed"
                            for frame in frames for d in frame["devices"] if d["name"] == "A"))

    def test_full_removal_keeps_original_measurement_as_unconfirmed_history(self):
        pool = FakePool([receiver("a")], {
            ("lofree", "physical-a"): (0, [DeviceStatus("a", "A", 25, source="lofree")])})
        engine = self.fixture(pool)
        first = engine.poll()["devices"][0]
        pool.inventory.clear()
        second = engine.poll(force=True)["devices"][0]
        self.assertEqual(second["percent"], first["percent"])
        self.assertEqual(second["measuredAtUtc"], first["measuredAtUtc"])
        self.assertEqual(second["confirmedAtUtc"], first["confirmedAtUtc"])
        self.assertEqual(second["connectionState"], "unconfirmed")
        self.assertFalse(second["fresh"])
        self.assertFalse(second["online"])
        self.assert_reader_removed(engine, "lofree|physical-a")

    def test_first_failed_reader_is_removed_and_empty_round_parks_children(self):
        pool = FakePool([receiver("a")], {
            ("lofree", "physical-a"): (0, IsolationError("timeout"))})
        engine = self.fixture(pool)
        first = engine.poll()
        self.assertEqual(first["errors"], ["lofree: timeout"])
        self.assertNotIn("lofree|physical-a", engine.by_provider)
        self.assertIn("lofree|physical-a", engine.health)
        pool.inventory.clear()
        frames = []
        second = engine.poll(park_empty_readers=True, progress=frames.append)
        self.assertEqual(second["devices"], [])
        self.assertEqual(second["errors"], [])
        self.assertTrue(all(frame["errors"] == [] for frame in frames))
        self.assertTrue(any(h["provider"] == "lofree" and h["state"] == "no_devices"
                            for h in second["health"]))
        self.assertTrue(all(child.closed == 1 for child in pool.children))
        self.assert_reader_removed(engine, "lofree|physical-a")

    def test_full_removal_retains_live_receiver_failure_and_backoff(self):
        pool = FakePool([receiver("a"), receiver("b")], {
            ("lofree", "physical-a"): (0, IsolationError("timeout")),
            ("lofree", "physical-b"): (0, IsolationError("read_failed"))})
        engine = self.fixture(pool)
        engine.poll()
        retry = engine.retry_after["lofree|physical-b"]
        slot = engine._affinity["lofree|physical-b"]
        pool.inventory[:] = [receiver("b")]
        second = engine.poll(park_empty_readers=True)
        self.assertEqual(second["errors"], ["lofree: read_failed"])
        self.assertEqual(engine.health["lofree|physical-b"]["state"], "backoff")
        self.assertEqual(engine.retry_after["lofree|physical-b"], retry)
        self.assertEqual(engine.failures["lofree|physical-b"], 1)
        self.assertEqual(engine._affinity["lofree|physical-b"], slot)
        self.assertTrue(all(child.closed == 0 for child in pool.children))
        self.assert_reader_removed(engine, "lofree|physical-a")

    def test_reconnected_reader_does_not_inherit_removed_poll_interval(self):
        pool = FakePool([receiver("a")])
        engine = self.fixture(pool, ("standard_hid",))
        engine.poll()
        self.assertIn("standard_hid|physical-a", engine.last_poll)
        pool.inventory.clear()
        engine.poll()
        self.assert_reader_removed(engine, "standard_hid|physical-a")
        pool.inventory[:] = [receiver("a")]
        pool.calls.clear()
        engine.poll()
        self.assertIn(("standard_hid", "physical-a"), pool.calls)

    def test_failed_topology_does_not_discard_target_reader(self):
        pool = FakePool([receiver("a")], {
            ("lofree", "physical-a"): (0, [DeviceStatus("a", "A", 25, source="lofree")])})
        engine = self.fixture(pool)
        first = engine.poll()["devices"][0]
        pool.inventory.clear()
        pool.outcomes[("topology", "")] = (0, IsolationError("timeout"))
        second = engine.poll(force=True, device_id=first["id"])
        self.assertIn("topology: timeout", second["errors"])
        self.assertEqual(second["devices"], [first])
        self.assertIn("lofree|physical-a", engine.by_provider)


class SchedulerTests(unittest.TestCase):
    def test_successful_topology_clears_only_its_prior_failure_and_partial_error(self):
        pool = FakePool([], {("topology", ""): (0, IsolationError("read_failed"))})
        engine = poll_fixture(pool)
        frames = []
        with patch.object(hidlist, "interface_paths", return_value=frozenset()):
            first = engine.poll(park_empty_readers=True)
            self.assertIn("topology: read_failed", first["errors"])
            self.assertIn("topology", engine.retry_after)
            self.assertEqual(engine.failures["topology"], 1)
            engine._failure("independent", "independent", "read_failed")
            independent_retry = engine.retry_after["independent"]
            engine._affinity["independent"] = 1
            pool.outcomes[("topology", "")] = (0, [])
            second = engine.poll(park_empty_readers=True, progress=frames.append)
        self.assertNotIn("topology", engine.health)
        self.assertNotIn("topology", engine.retry_after)
        self.assertNotIn("topology", engine.failures)
        self.assertTrue(all("topology: read_failed" not in frame["errors"] for frame in frames))
        self.assertIn("independent: read_failed", second["errors"])
        self.assertIn("independent", engine.health)
        self.assertEqual(engine.failures["independent"], 1)
        self.assertEqual(engine.retry_after["independent"], independent_retry)
        self.assertEqual(engine._affinity["independent"], 1)
        self.assertTrue(all(child.closed == 0 for child in pool.children))
        engine.close()

    def test_background_healthy_empty_round_parks_only_reader_slots(self):
        pool = FakePool([receiver("a")])
        engine = poll_fixture(pool, ("standard_hid",))
        with patch.object(hidlist, "interface_paths", return_value=frozenset()):
            result = engine.poll(park_empty_readers=True)
        self.assertEqual(result["devices"], [])
        self.assertTrue(all(child.closed == 1 for child in pool.children))
        self.assertIn("standard_hid|physical-a", engine.last_poll)
        self.assertIn("standard_hid|physical-a", engine._affinity)
        self.assertEqual(engine.health["standard_hid|physical-a"]["state"], "unsupported")
        engine.close()

    def test_foreground_empty_round_retains_reader_discovery_cache(self):
        pool = FakePool([receiver("a")])
        engine = poll_fixture(pool, ("standard_hid",))
        with patch.object(hidlist, "interface_paths", return_value=frozenset()):
            engine.poll(park_empty_readers=False)
        self.assertTrue(all(child.closed == 0 for child in pool.children))
        engine.close()

    def test_parked_reader_slots_can_force_query_again(self):
        pool = FakePool([receiver("a")])
        engine = poll_fixture(pool, ("standard_hid",))
        with patch.object(hidlist, "interface_paths", return_value=frozenset()):
            engine.poll(park_empty_readers=True)
            pool.calls.clear()
            engine.poll(force=True, park_empty_readers=True)
        self.assertIn(("standard_hid", "physical-a"), pool.calls)
        engine.close()

    def test_system_property_measurement_never_parks_as_not_fresh(self):
        bt = DeviceStatus("bt:x", "Bluetooth", 50, source="bluetooth", source_quality="system_property", queried_at_utc=utcnow())
        pool = FakePool([], {("bluetooth", ""): (0, [bt])})
        engine = poll_fixture(pool, ("bluetooth",))
        with patch.object(hidlist, "interface_paths", return_value=frozenset()):
            result = engine.poll(park_empty_readers=True)
        self.assertFalse(result["devices"][0]["fresh"])
        self.assertTrue(all(child.closed == 0 for child in pool.children))
        engine.close()

    def test_known_candidate_unavailable_never_parks(self):
        pool = FakePool([receiver("a")])
        engine = poll_fixture(pool, ("lofree",))
        engine.health["known-grace"] = {"provider": "known", "state": "unavailable", "deviceCount": 0}
        with patch.object(hidlist, "interface_paths", return_value=frozenset()):
            engine.poll(park_empty_readers=True)
        self.assertTrue(all(child.closed == 0 for child in pool.children))
        engine.close()

    def test_protocol_grace_cached_status_never_parks(self):
        cached = DeviceStatus("a", "Grace", 50, source="lofree", cached=True, online=False)
        pool = FakePool([receiver("a")], {("lofree", "physical-a"): (0, [cached])})
        engine = poll_fixture(pool)
        with patch.object(hidlist, "interface_paths", return_value=frozenset()):
            engine.poll(park_empty_readers=True)
        self.assertTrue(all(child.closed == 0 for child in pool.children))
        engine.close()

    def test_package_smoke_never_parks_before_lifecycle_check(self):
        pool = FakePool()
        engine = BatteryEngine(package_smoke=True, process_factory=pool.factory)
        result = engine.poll(park_empty_readers=True)
        self.assertTrue(result["packageSmoke"])
        self.assertTrue(all(child.closed == 0 for child in pool.children))
        engine.close()

    def test_no_declared_standard_battery_is_unsupported_not_failure(self):
        engine = BatteryEngine([], include_native=True)
        engine._accept("standard|physical-a", "standard_hid", [], utcnow(), "physical-a", candidate_count=0)
        result = engine._snapshot()
        self.assertEqual(result["health"][0]["state"], "unsupported")
        self.assertEqual(result["health"][0]["reason"], "no_declared_battery")
        self.assertEqual(result["errors"], [])
        engine.close()

    def test_declared_battery_without_measurement_is_unavailable(self):
        engine = BatteryEngine([], include_native=True)
        engine._accept("standard|physical-a", "standard_hid", [], utcnow(), "physical-a", candidate_count=1)
        result = engine._snapshot()
        self.assertEqual(result["health"][0]["state"], "unavailable")
        engine.close()

    def test_unknown_pid_is_not_supported_candidate_by_brand_alone(self):
        known = dict(receiver("known"), vendor_id=0x3434, product_id=0xD028)
        unknown = dict(receiver("unknown"), vendor_id=0x3434, product_id=0xFFFF)
        self.assertEqual(recognized_candidate_count("keychron", [known]), 1)
        self.assertEqual(recognized_candidate_count("keychron", [unknown]), 0)

    def test_round_budget_bounds_slow_reader_and_keeps_fast_partial(self):
        outcomes = {("lofree", "physical-a"): (0.01, [DeviceStatus("a", "Fast", 25, source="lofree")]),
                    ("lofree", "physical-b"): (10, [])}
        pool = FakePool([receiver("a"), receiver("b")], outcomes)
        engine = poll_fixture(pool)
        frames = []
        with patch.object(hidlist, "interface_paths", return_value=frozenset()), patch("server.MAX_ROUND_SECONDS", 0.12):
            start = time.monotonic()
            result = engine.poll(progress=frames.append)
        self.assertLess(time.monotonic() - start, 0.5)
        self.assertTrue(any(d["name"] == "Fast" for frame in frames for d in frame["devices"]))
        self.assertTrue(any("timeout" in error for error in result["errors"]))
        self.assertFalse(pool.active)
        engine.close()

    def test_partial_fast_result_arrives_before_slow_reader(self):
        outcomes = {("lofree", "physical-a"): (0.02, [DeviceStatus("a", "Fast", 25, source="lofree")]),
                    ("lofree", "physical-b"): (0.25, [DeviceStatus("b", "Slow", 75, source="lofree")])}
        pool = FakePool([receiver("a"), receiver("b")], outcomes)
        engine = poll_fixture(pool)
        frames = []
        start = time.monotonic()
        with patch.object(hidlist, "interface_paths", return_value=frozenset()):
            result = engine.poll(request_id=12, generation=34, progress=lambda f: frames.append((time.monotonic() - start, f)))
        fast = [(stamp, frame) for stamp, frame in frames if any(d["name"] == "Fast" for d in frame["devices"])]
        self.assertLess(fast[0][0], 0.2)
        self.assertFalse(fast[0][1]["complete"])
        self.assertTrue(result["complete"])
        self.assertTrue(all(f["requestId"] == 12 and f["generation"] == 34 for _, f in frames))
        self.assertLessEqual(pool.max_active, 2)
        self.assertFalse(pool.physical_overlap)
        engine.close()

    def test_partial_error_does_not_erase_healthy_device(self):
        outcomes = {("lofree", "physical-a"): (0, [DeviceStatus("a", "Good", 25, source="lofree")]),
                    ("lofree", "physical-b"): (0, IsolationError("read_failed"))}
        pool = FakePool([receiver("a"), receiver("b")], outcomes)
        engine = poll_fixture(pool)
        with patch.object(hidlist, "interface_paths", return_value=frozenset()):
            result = engine.poll()
        self.assertEqual([d["name"] for d in result["devices"]], ["Good"])
        self.assertIn("lofree: read_failed", result["errors"])
        self.assertTrue(any(h["state"] == "error" for h in result["health"]))
        self.assertNotIn("physical-b", json.dumps(result))
        engine.close()

    def test_targeted_refresh_never_queries_other_receiver(self):
        outcomes = {("lofree", "physical-a"): (0, [DeviceStatus("a", "A", 25, source="lofree")]),
                    ("lofree", "physical-b"): (0, [DeviceStatus("b", "B", 75, source="lofree")])}
        pool = FakePool([receiver("a"), receiver("b")], outcomes)
        engine = poll_fixture(pool)
        with patch.object(hidlist, "interface_paths", return_value=frozenset()):
            first = engine.poll()
            pool.calls.clear()
            engine.poll(force=True, device_id=next(d["id"] for d in first["devices"] if d["name"] == "A"))
        self.assertEqual([p for name, p in pool.calls if name == "lofree"], ["physical-a"])
        engine.close()

    def test_unknown_target_has_no_hardware_or_process_work(self):
        pool = FakePool([receiver("a")])
        engine = poll_fixture(pool)
        result = engine.poll(force=True, device_id="device-unknown")
        self.assertEqual(pool.calls, [])
        self.assertIn("selection: unknown_target", result["errors"])
        engine.close()

    def test_provider_failures_have_backoff_but_force_can_retry(self):
        pool = FakePool([receiver("a")], {("lofree", "physical-a"): (0, IsolationError("read_failed"))})
        engine = poll_fixture(pool)
        with patch.object(hidlist, "interface_paths", return_value=frozenset()):
            engine.poll()
            pool.calls.clear()
            result = engine.poll()
            self.assertFalse(any(name == "lofree" for name, _ in pool.calls))
            self.assertTrue(any(h["state"] == "backoff" for h in result["health"]))
            pool.calls.clear()
            engine.poll(force=True)
            self.assertTrue(any(name == "lofree" for name, _ in pool.calls))
        engine.close()

    def test_same_receiver_vendor_and_standard_hid_are_serial(self):
        pool = FakePool([receiver("a")], {("lofree", "physical-a"): (0.04, []),
                                         ("standard_hid", "physical-a"): (0.04, [])})
        engine = poll_fixture(pool, ("lofree", "standard_hid"))
        with patch.object(hidlist, "interface_paths", return_value=frozenset()):
            engine.poll()
        self.assertFalse(pool.physical_overlap)
        engine.close()


def wait_result(child, timeout=3):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        value = child.result()
        if value is not None:
            return value
        time.sleep(0.005)
    raise AssertionError("fixture child did not finish")


class ProcessIsolationTests(unittest.TestCase):
    def test_termination_pending_slot_cannot_be_reused(self):
        child = ReaderProcess()
        class StillRunning:
            def poll(self):
                return None
        child.process = StillRunning()
        child.quarantined = True
        with self.assertRaises(IsolationError) as caught:
            child.start({"action": "fixture"}, 0.1)
        self.assertEqual(caught.exception.code, "termination_pending")
        child.process = None
        child.close()

    def test_hung_child_is_killed_before_slot_reuse(self):
        script = "import sys,time\nfor line in sys.stdin:\n time.sleep(60)\n"
        child = ReaderProcess([sys.executable, "-I", "-B", "-c", script])
        child.start({"action": "fixture"}, 0.08)
        process = child.process
        started = time.monotonic()
        with self.assertRaises(IsolationError) as caught:
            wait_result(child)
        self.assertEqual(caught.exception.code, "timeout")
        self.assertIsNotNone(process.poll())
        self.assertIsNone(child.pending)
        self.assertLess(time.monotonic() - started, 2)
        child.close()

    def test_wrong_sequence_is_discarded_and_child_reaped(self):
        script = "import sys,json\nfor line in sys.stdin:\n print(json.dumps({'sequence':999,'devices':[]}),flush=True)\n"
        child = ReaderProcess([sys.executable, "-I", "-B", "-c", script])
        child.start({"action": "fixture"}, 1)
        process = child.process
        with self.assertRaises(IsolationError) as caught:
            wait_result(child)
        self.assertEqual(caught.exception.code, "invalid_response")
        self.assertIsNotNone(process.poll())
        child.close()

    def test_child_receives_binary_paths_without_loss(self):
        script = "import sys,json\nfor line in sys.stdin:\n x=json.loads(line);print(json.dumps({'sequence':x['sequence'],'path':x['path']}),flush=True)\n"
        child = ReaderProcess([sys.executable, "-I", "-B", "-c", script])
        child.start({"path": b"fixture-\xff\x00"}, 1)
        self.assertEqual(wait_result(child)["path"], b"fixture-\xff\x00")
        child.close()

    @unittest.skipUnless(os.name == "nt", "Windows nested Job lifecycle")
    def test_closing_child_job_kills_its_descendant(self):
        script = ("import sys,json,subprocess\nfor line in sys.stdin:\n"
                  " x=json.loads(line);p=subprocess.Popen([sys.executable,'-I','-B','-c','import time;time.sleep(60)'],creationflags=0x08000000)\n"
                  " print(json.dumps({'sequence':x['sequence'],'childPid':p.pid}),flush=True)\n")
        child = ReaderProcess([sys.executable, "-I", "-B", "-c", script])
        child.start({"action": "fixture"}, 2)
        pid = wait_result(child)["childPid"]
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.OpenProcess.argtypes = [ctypes.c_uint32, ctypes.c_int, ctypes.c_uint32]
        kernel.OpenProcess.restype = ctypes.c_void_p
        kernel.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
        kernel.CloseHandle.argtypes = [ctypes.c_void_p]
        handle = kernel.OpenProcess(0x100000, False, pid)
        self.assertTrue(handle)
        try:
            child.close()
            self.assertEqual(kernel.WaitForSingleObject(handle, 2000), 0)
        finally:
            kernel.CloseHandle(handle)

    @unittest.skipUnless(sys.platform == "win32", "Requires Windows portable runtime paths and hidapi in -I children")
    def test_private_package_smoke_accepts_windows_utf8_bom(self):
        command = [sys.executable, "-I", "-B", str(ENGINE / "server.py"), "--package-smoke"]
        request = b"\xef\xbb\xbf" + b'{"command":"poll","requestId":9,"generation":2,"force":true}\n'
        process = subprocess.run(command, input=request, capture_output=True, timeout=10,
                                 creationflags=0x08000000 if os.name == "nt" else 0)
        self.assertEqual(process.returncode, 0)
        frames = [json.loads(line) for line in process.stdout.splitlines()]
        self.assertTrue(frames[-1]["complete"])
        self.assertTrue(all(f["requestId"] == 9 and f["generation"] == 2 for f in frames))
        self.assertEqual(frames[-1]["errors"], [])

    @unittest.skipUnless(sys.platform == "win32", "Requires Windows portable runtime paths and hidapi in -I children")
    def test_private_real_package_smoke_accepts_ids_and_eof(self):
        # Imports all shipped readers inside a controlled child but never
        # enumerates HID, reads Windows Bluetooth or sends vendor commands.
        command = [sys.executable, "-I", "-B", str(ENGINE / "server.py"), "--package-smoke"]
        request = json.dumps({"command": "poll", "requestId": 7, "generation": 8, "force": True}) + "\n"
        process = subprocess.run(command, input=request, capture_output=True, text=True, timeout=10,
                                 creationflags=0x08000000 if os.name == "nt" else 0)
        self.assertEqual(process.returncode, 0, process.stderr)
        frames = [json.loads(line) for line in process.stdout.splitlines()]
        self.assertGreaterEqual(len(frames), 2)
        self.assertTrue(frames[-1]["complete"])
        self.assertTrue(all(f["schema"] == 2 and f["requestId"] == 7 and f["generation"] == 8 for f in frames))
        self.assertEqual(frames[-1]["providerCount"], 26)
        self.assertEqual(frames[-1]["errors"], [])
        self.assertEqual(frames[-1]["devices"], [])


class CompatibilityCatalogTests(unittest.TestCase):
    def test_catalog_reader_names_and_native_exclusions_align(self):
        result = validate_catalog()
        self.assertEqual(result["readerCount"], 26)
        self.assertEqual(result["nativeProfileCount"], 2)
        self.assertEqual(result["errors"], [])

    def test_generic_readers_do_not_inherit_upstream_hardware_claims(self):
        self.assertTrue(all(entry["verificationLevel"] == "C" for entry in load_catalog()["readers"]))
        self.assertEqual(verification_for("lofree")[0], "C")
        self.assertEqual(verification_for("unknown")[0], "E")

    def test_infi75_is_observed_without_compatibility_claim(self):
        entry = load_catalog()["observedUnsupported"][0]
        self.assertEqual(entry["verificationLevel"], "D")
        self.assertFalse(entry["compatible"])
        self.assertEqual(entry["match"]["vendorId"], "05AC")
        self.assertEqual(entry["capabilities"], [])

    def test_normalized_readings_carry_catalog_evidence(self):
        value = normalise(DeviceStatus("a", "A", 25, source="lofree"), None, utcnow())
        self.assertEqual(value["verificationLevel"], "C")
        self.assertEqual(value["verificationLabel"], load_catalog()["levels"]["C"])

    def test_generated_markdown_covers_exact_catalog_entries(self):
        table = markdown()
        for entry in load_catalog()["readers"] + load_catalog()["nativeProfiles"] + load_catalog()["observedUnsupported"]:
            self.assertIn(entry["name"], table)
        self.assertIn(load_catalog()["upstream"]["commit"], table)
        self.assertIn("BEGIN GENERATED COMPATIBILITY CATALOG", table)


if __name__ == "__main__":
    unittest.main()
