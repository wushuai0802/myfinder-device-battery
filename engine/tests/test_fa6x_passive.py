"""Synthetic FA6x notifications only; never run vendor code or access HID."""
import ast
from contextlib import contextmanager
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
from providers import fa6x_passive as F
from catalog import load_catalog, validate_catalog
from device_pack import BRAND_PROVIDERS
from known_ids import build_index
import server


def descriptor(pid=0xFA60, physical="usb:a", **changes):
    page, usage = (0x000A, 0) if pid == 0xFA60 else (0xFF00, 1)
    return dict({"vendor_id": 0x1D57, "product_id": pid, "physical_id": physical,
                 "path": physical.encode() + b":input", "usage_page": page, "usage": usage,
                 "input_report_length": 8, "input_report_sizes": {"3": 8}}, **changes)


def report(level=73, model=0x78, event=0x40, state=2, report_id=3, length=8):
    return ([report_id, model, event, state, level] + [0] * max(0, length - 5))[:length]


class PassiveBus:
    def __init__(self, inventory, frames):
        self.inventory = inventory
        self.queues = {d["path"]: [] for d in inventory}
        if inventory:
            self.queues[inventory[0]["path"]] = list(frames)
        self.opened, self.closed, self.reads = [], [], []
        self.now, self.delay = 100.0, 0
        self.fail_open = False

    def device(self):
        bus = self

        class Handle:
            path = None

            def open_path(self, path):
                self.path = path
                bus.opened.append(path)
                if bus.fail_open:
                    raise OSError("private path")

            def read(self, length, timeout_ms):
                if self.path is None or not 0 < timeout_ms <= 1500:
                    raise AssertionError("unopened or unbounded read")
                bus.reads.append((self.path, length, timeout_ms))
                queue = bus.queues.get(self.path, [])
                if queue:
                    bus.now += min(bus.delay, timeout_ms / 1000)
                    raw = queue.pop(0)
                    if isinstance(raw, Exception):
                        raise raw
                    return raw
                bus.now += timeout_ms / 1000
                return []

            def close(self):
                bus.closed.append(self.path)

            def write(self, *args):
                raise AssertionError("write forbidden")

            def send_feature_report(self, *args):
                raise AssertionError("SET_FEATURE forbidden")

            def get_feature_report(self, *args):
                raise AssertionError("GET_FEATURE forbidden")

        return Handle()


@contextmanager
def fixture(frames=(), inventory=None):
    bus = PassiveBus([descriptor()] if inventory is None else inventory, frames)
    with patch.object(F.hidlist, "enumerate", side_effect=lambda vid: bus.inventory), \
            patch.object(F, "hid", SimpleNamespace(device=bus.device)), \
            patch.object(F, "time", SimpleNamespace(monotonic=lambda: bus.now)):
        provider = F.Fa6xPassiveProvider()
        try:
            yield bus, provider
        finally:
            provider.close()


class Fa6xPassiveTests(unittest.TestCase):
    def test_exact_web_model_table_and_events(self):
        expected = {0xFA60: {0x07, 0x76, 0x77, 0x78, 0x79, 0xE8},
                    0xFA65: {0x71, 0x76, 0x77, 0x78, 0x79, 0x90}}
        self.assertEqual(F.MODELS, expected)
        for pid, models in expected.items():
            for model in range(256):
                for event in (0x40, 0x41):
                    with self.subTest(pid=pid, model=model, event=event):
                        self.assertEqual(F.parse_battery(report(model=model, event=event), pid),
                                         73 if model in models else None)

    def test_known_native_only_models_and_unknown_pid_are_rejected(self):
        for pid in (0xFA60, 0xFA65, 0x1013, 0xFA61):
            for model in (0x13, 0x93, 0x95):
                self.assertIsNone(F.parse_battery(report(model=model), pid))
        self.assertIsNone(F.parse_battery(report(), 0xFA61))

    def test_percent_and_charge_state_never_synthesize_a_measurement(self):
        for pid in F.PIDS:
            for level in (1, 73, 100):
                for state in (0, 1, 2, 3, 0x11, 0x1F, 255):
                    with self.subTest(pid=pid, level=level, state=state), \
                            fixture([report(level, state=state)], [descriptor(pid)]) as (bus, provider):
                        status, = provider.poll()
                        self.assertEqual((status.name, status.level, status.charging, status.charging_known),
                                         (f"2.4G 无线鼠标（1D57:{pid:04X}）", level, False, False))
                        self.assertEqual((status.kind, status.source, status.verification_level),
                                         ("mouse", "fa6x_passive", "C"))
                        self.assertTrue(status.online)
                        self.assertFalse(status.cached)
                        public = server.normalise(status, None, server.utcnow())
                        self.assertIsNone(public["charging"])
                        self.assertIsNotNone(server.valid_time(status.measured_at_utc))
                        self.assertEqual(bus.closed, [])

    def test_invalid_prefix_percent_and_length_ignored(self):
        invalid = [report(level=n) for n in (-1, 0, 101, 255)]
        invalid += [report(event=n) for n in (0, 0x42, 0xC0, 0xFF)]
        invalid += [report(report_id=n) for n in (0, 4, 7)]
        invalid += [report(model=0x13), report(length=4), report(length=7), report(length=9)]
        with fixture(invalid) as (bus, provider):
            self.assertEqual(provider.poll(), [])
            self.assertEqual(len(bus.opened), 1)
        with fixture(invalid + [report(42)]) as (_, provider):
            self.assertEqual(provider.poll()[0].level, 42)

    def test_runtime_report_length_is_not_assumed_from_prefix_or_read_buffer(self):
        for length in (5, 8, 19, 64, 100):
            info = descriptor(input_report_length=128, input_report_sizes={"3": length, "9": 128})
            frames = [report(12, length=length - 1), report(13, length=length + 1),
                      report(14, report_id=9, length=128), report(62, length=length)]
            with self.subTest(length=length), fixture(frames, [info]) as (bus, provider):
                self.assertEqual(provider.poll()[0].level, 62)
                self.assertTrue(all(size == 128 for _, size, _ in bus.reads))

    def test_initial_and_later_polls_drain_to_newest_valid_notification(self):
        with fixture([report(40), report(0), report(41, event=0x41)]) as (bus, provider):
            first, = provider.poll()
            self.assertEqual(first.level, 41)
            self.assertEqual(bus.closed, [])
            bus.queues[descriptor()["path"]] = [report(62), report(model=0), report(63), report(0)]
            before = len(bus.reads)
            newest, = provider.poll()
            self.assertEqual((newest.level, newest.cached), (63, False))
            self.assertEqual(len(bus.opened), 1)
            self.assertTrue(all(timeout == 1 for _, _, timeout in bus.reads[before:]))
            self.assertEqual(bus.closed, [])
        self.assertEqual(bus.closed, bus.opened)

    def test_silence_is_not_an_error_and_late_input_uses_kept_handle(self):
        with fixture() as (bus, provider):
            self.assertEqual(provider.poll(), [])
            self.assertLessEqual(bus.now - 100, 1.5)
            self.assertEqual(bus.closed, [])
            candidates = server.recognized_candidate_count(F.Fa6xPassiveProvider.name, bus.inventory)
            self.assertEqual(candidates, 1)
            engine = server.BatteryEngine(providers=[provider])
            engine._accept("reader", provider.name, [], server.utcnow(), "usb:a", candidates)
            self.assertEqual(engine.health["reader"]["reason"], "no_measurement")
            self.assertEqual(engine.errors, [])
            bus.queues[descriptor()["path"]] = [report(55)]
            self.assertEqual(provider.poll()[0].level, 55)
            self.assertEqual(len(bus.opened), 1)

    def test_history_keeps_original_time_and_idle_health_until_unplug(self):
        with fixture([report(61)]) as (bus, provider):
            first, = provider.poll()
            previous = server.normalise(first, None, "2026-11-06T01:00:00Z")
            for changes in ({}, {"input_report_sizes": None}, {"usage": 14},
                            {"physical_id": "collection:temporarily-unresolved"}):
                bus.inventory = [descriptor(**changes)]
                stale, = provider.poll()
                self.assertEqual((stale.level, stale.online, stale.cached, stale.wake_on_input),
                                 (61, False, True, True))
                self.assertFalse(stale.connection_known)
                self.assertFalse(stale.charging_known)
                public = server.normalise(stale, previous, "2026-11-06T02:00:00Z")
                self.assertEqual(public["measuredAtUtc"], previous["measuredAtUtc"])
                self.assertEqual(public["measurementState"], "cached")
                engine = server.BatteryEngine(providers=[provider])
                engine._accept("reader", provider.name, [stale], server.utcnow(), "usb:a", 1)
                self.assertEqual(engine.health["reader"]["reason"], "no_measurement")
                self.assertEqual(engine.errors, [])
            bus.inventory = [descriptor(), descriptor(path=b"duplicate")]
            before = len(bus.opened)
            self.assertTrue(provider.poll()[0].cached)
            self.assertEqual(len(bus.opened), before)
            bus.inventory = []
            self.assertEqual(provider.poll(), [])
            bus.inventory = [descriptor()]
            self.assertEqual(provider.poll(), [])

    def test_independent_receivers_and_removal(self):
        infos = [descriptor(), descriptor(0xFA65, physical="usb:b")]
        with fixture([report(12)], infos) as (bus, provider):
            bus.queues[infos[1]["path"]] = [report(84, model=0x90)]
            first = provider.poll()
            self.assertEqual([s.level for s in first], [12, 84])
            self.assertEqual(len({s.key for s in first}), 2)
            bus.inventory = [infos[1]]
            bus.queues[infos[1]["path"]] = [report(85)]
            current, = provider.poll()
            self.assertEqual((current.key, current.level, current.cached), (first[1].key, 85, False))
            self.assertIn(infos[0]["path"], bus.closed)
            self.assertEqual(len(provider._last), 1)

    def test_limits_bound_first_listen_and_continuous_queue(self):
        for delay in (0, 0.3):
            with fixture([report(model=0)] * 1000) as (bus, provider):
                bus.delay = delay
                self.assertEqual(provider.poll(), [])
                self.assertLessEqual(bus.now - 100, 1.5)
                self.assertLessEqual(len(bus.reads), 32)
        with fixture() as (bus, provider):
            provider.poll()
            bus.queues[descriptor()["path"]] = [report(22)] * 1000
            before = len(bus.reads)
            self.assertEqual(provider.poll()[0].level, 22)
            self.assertEqual(len(bus.reads) - before, F.MAX_DRAIN)

    def test_failed_open_or_read_closes_and_can_recover(self):
        for fail_open in (False, True):
            with fixture([OSError("private path")]) as (bus, provider):
                bus.fail_open = fail_open
                self.assertEqual(provider.poll(), [])
                self.assertEqual(bus.closed, bus.opened)
                self.assertNotIn("private path", " ".join(provider.diagnostics()))
                bus.fail_open = False
                bus.queues[descriptor()["path"]] = [report(77)]
                self.assertEqual(provider.poll()[0].level, 77)
                bus.queues[descriptor()["path"]] = [OSError("private path")]
                self.assertTrue(provider.poll()[0].cached)
                self.assertEqual(bus.closed, bus.opened)

    def test_enumeration_failure_retains_history(self):
        with fixture([report(66)]) as (_, provider):
            first, = provider.poll()
            with patch.object(F.hidlist, "enumerate", side_effect=OSError("private path")):
                stale, = provider.poll()
            self.assertEqual((stale.key, stale.level, stale.measured_at_utc, stale.cached),
                             (first.key, 66, first.measured_at_utc, True))

    def test_path_or_proven_length_change_reopens_and_proof_loss_closes(self):
        changes = [[], [descriptor(path=b"new-path")], [descriptor(input_report_sizes=None)],
                   [descriptor(input_report_length=9, input_report_sizes={"3": 9})]]
        for inventory in changes:
            with self.subTest(inventory=inventory), fixture() as (bus, provider):
                provider.poll()
                bus.inventory = inventory
                provider.poll()
                self.assertIn(descriptor()["path"], bus.closed)
                self.assertEqual(len(bus.opened), 2 if inventory and inventory[0].get("input_report_sizes") else 1)

    def test_wrong_or_ambiguous_identity_collection_and_parent_never_open(self):
        for pid in F.PIDS:
            changes = [{"vendor_id": 0x1D58}, {"product_id": 0xFA61}, {"product_id": 0x1013},
                       {"usage_page": 0x000B, "usage": 0}, {"usage_page": 0xFF01, "usage": 2},
                       {"usage": 14}, {"physical_id": ""}, {"physical_id": "collection:unresolved"},
                       {"path": b""}]
            changes += [{"usage_page": 0xFF00, "usage": 1} if pid == 0xFA60
                        else {"usage_page": 0x000A, "usage": 0}]
            inventories = [[descriptor(pid, **change)] for change in changes]
            inventories += [[], [descriptor(pid), descriptor(pid, path=b"duplicate")]]
            for inventory in inventories:
                with self.subTest(pid=pid, inventory=inventory), fixture([report()], inventory) as (bus, provider):
                    self.assertEqual(provider.poll(), [])
                    self.assertEqual(bus.opened, [])

    def test_unproven_or_inconsistent_lengths_never_open(self):
        changes = [{"input_report_length": n} for n in (None, True, "8", 4, 4097)]
        changes += [{"input_report_sizes": sizes} for sizes in (
            None, {}, {3: 8}, {"4": 8}, {"3": True}, {"3": "8"}, {"3": 4},
            {"3": 9}, {"3": 7}, {"3": 8, "4": None})]
        for pid in F.PIDS:
            for change in changes:
                with self.subTest(pid=pid, change=change), fixture([report()], [descriptor(pid, **change)]) as (bus, provider):
                    self.assertEqual(provider.poll(), [])
                    self.assertEqual(bus.opened, [])

    def test_ast_allows_only_open_read_close_device_operations(self):
        tree = ast.parse(Path(F.__file__).read_text(encoding="utf-8"))
        calls = [node.func for node in ast.walk(tree) if isinstance(node, ast.Call)]
        attributes = {call.attr for call in calls if isinstance(call, ast.Attribute)}
        self.assertFalse(attributes & {"write", "send_feature_report", "get_feature_report"})
        device_calls = {call.attr for call in calls if isinstance(call, ast.Attribute)
                        and isinstance(call.value, ast.Name) and call.value.id == "dev"}
        self.assertEqual(device_calls, {"open_path", "read", "close"})

    def test_registration_neutral_index_and_catalog(self):
        self.assertIn(("fa6x_passive", "Fa6xPassiveProvider"), server.PROVIDERS)
        self.assertEqual(server.PROVIDER_VIDS["fa6x_passive"], (0x1D57,))
        self.assertIn("fa6x_passive", BRAND_PROVIDERS)
        self.assertNotIn("fa6x_passive", server.DPI_PROVIDERS)
        entries = [e for e in build_index()["devices"] if e["provider"] == "fa6x_passive"]
        self.assertEqual({(e["vendorId"], e["productId"], e["name"], e["kind"]) for e in entries},
                         {(0x1D57, pid, name, "mouse") for pid, name in F.PIDS.items()})
        self.assertTrue(all(e["requiresModelMatch"] for e in entries))
        reader, = [e for e in load_catalog()["readers"] if e["provider"] == "fa6x_passive"]
        self.assertEqual(reader["verificationLevel"], "C")
        self.assertEqual(validate_catalog()["errors"], [])


if __name__ == "__main__":
    unittest.main()
