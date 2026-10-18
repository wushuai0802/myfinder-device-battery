"""Physical receiver refresh regressions: fake topology/readers, no HID access."""
import copy
import io
import json
import sys
import types
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "engine"))
import server
from providers.base import DeviceStatus
from isolation import IsolationError, wire_encode, wire_decode
from test_engine_reliability import FakePool, hidlist, poll_fixture, receiver


def outcome(key, name, value, source="lofree", **kwargs):
    return (0, [DeviceStatus(key, name, value, source=source, **kwargs)])


class PhysicalRefreshTests(unittest.TestCase):
    def setUp(self):
        paths = patch.object(hidlist, "interface_paths", return_value=frozenset())
        paths.start()
        self.addCleanup(paths.stop)

    def engine(self, pool, providers=("lofree",)):
        engine = poll_fixture(pool, providers)
        self.addCleanup(engine.close)
        return engine

    def pair(self):
        pool = FakePool([receiver("a"), receiver("b")], {
            ("lofree", "physical-a"): outcome("a", "A", 25),
            ("lofree", "physical-b"): outcome("b", "B", 75)})
        engine = self.engine(pool)
        first = engine.poll(force=True)
        pool.calls.clear()
        return pool, engine, first

    def test_same_pid_receivers_only_selected_one_is_queried(self):
        pool, engine, first = self.pair()
        pool.outcomes[("lofree", "physical-a")] = outcome("a", "A", 35)
        second = engine.poll(force=True, physical_id=server.public_id("physical-a"))
        self.assertEqual(pool.calls, [("topology", server.public_id("physical-a")), ("lofree", "physical-a")])
        self.assertEqual(next(d["percent"] for d in second["devices"] if d["name"] == "A"), 35)
        self.assertEqual(next(d for d in second["devices"] if d["name"] == "B"),
                         next(d for d in first["devices"] if d["name"] == "B"))
        self.assertIn("lofree", engine._ambiguous_providers)

    def test_forgotten_device_is_recovered_without_device_id_or_bluetooth(self):
        gpw = dict(receiver("gpw"), vendor_id=0x046D, product_id=0xC547)
        pool = FakePool([gpw], {("logitech", "physical-gpw"): outcome("gpw", "PRO X Wireless", 75, "logitech", kind="mouse")})
        engine = self.engine(pool, ("logitech",))
        first = engine.poll(force=True)
        old_id = first["devices"][0]["id"]
        engine.by_provider["logitech|physical-gpw"] = {}
        engine.previous[old_id] = dict(first["devices"][0], confirmedAtUtc="2020-01-01T00:00:00Z")
        self.assertEqual(engine._snapshot()["devices"], [])
        self.assertNotIn(old_id, engine.provider_by_id)
        self.assertNotIn(old_id, engine.physical_by_id)
        pool.calls.clear()
        second = engine.poll(force=True, physical_id=server.public_id("physical-gpw"))
        self.assertEqual(pool.calls, [("topology", server.public_id("physical-gpw")), ("logitech", "physical-gpw")])
        self.assertEqual(second["devices"][0]["id"], old_id)
        self.assertEqual(second["devices"][0]["percent"], 75)
        self.assertTrue(second["devices"][0]["fresh"])
        self.assertEqual(second["devices"][0]["physicalId"], server.public_id("physical-gpw"))

    def test_unknown_physical_preserves_all_unrelated_groups_errors_and_identity(self):
        pool, engine, first = self.pair()
        for name in ("manager", "selection", "topology", "independent"):
            engine._failure(name, name, "read_failed")
        states = (engine.by_provider, engine.health, engine.last_poll, engine.failures,
                  engine.retry_after, engine._affinity, engine.provider_by_id, engine.physical_by_id)
        original = copy.deepcopy(states)
        ambiguous = set(engine._ambiguous_providers)
        second = engine.poll(force=True, physical_id=server.public_id("absent"), park_empty_readers=True)
        self.assertEqual(pool.calls, [("topology", server.public_id("absent"))])
        self.assertEqual(second["devices"], first["devices"])
        self.assertEqual((engine.by_provider, engine.health, engine.last_poll, engine.failures,
                          engine.retry_after, engine._affinity, engine.provider_by_id, engine.physical_by_id), original)
        self.assertEqual(engine._ambiguous_providers, ambiguous)
        self.assertTrue(all(child.closed == 0 for child in pool.children))

    def test_missing_selected_receiver_expires_only_its_state(self):
        pool, engine, first = self.pair()
        a = next(d for d in first["devices"] if d["name"] == "A")
        b = next(d for d in first["devices"] if d["name"] == "B")
        engine.dpi_targets[a["id"]] = {"private": "a"}
        engine.dpi_targets[b["id"]] = {"private": "b"}
        pool.inventory[:] = [receiver("b")]
        second = engine.poll(force=True, physical_id=server.public_id("physical-a"))
        current = {d["name"]: d for d in second["devices"]}
        self.assertEqual(pool.calls, [("topology", server.public_id("physical-a"))])
        self.assertEqual(current["B"], b)
        self.assertFalse(current["A"]["online"])
        self.assertFalse(current["A"]["fresh"])
        self.assertEqual(current["A"]["connectionState"], "unconfirmed")
        self.assertEqual(current["A"]["confirmedAtUtc"], a["confirmedAtUtc"])
        self.assertEqual(current["A"]["percent"], a["percent"])
        self.assertNotIn(a["id"], engine.dpi_targets)
        self.assertIn(b["id"], engine.dpi_targets)
        for state in (engine.by_provider, engine.health, engine.last_poll, engine.failures, engine.retry_after, engine._affinity):
            self.assertNotIn("lofree|physical-a", state)
        self.assertIn("lofree", engine._ambiguous_providers)

    def test_empty_physical_scan_does_not_emit_provider_wide_no_devices(self):
        engine = self.engine(FakePool())
        result = engine.poll(force=True, physical_id=server.public_id("absent"))
        self.assertEqual(result["devices"], [])
        self.assertEqual(result["health"], [])
        self.assertEqual(result["errors"], [])

    def test_physical_scan_keeps_provider_level_fault_and_ambiguity(self):
        pool, engine, _first = self.pair()
        engine._failure("lofree", "lofree", "independent_failure")
        old = copy.deepcopy(engine.health["lofree"])
        engine.poll(force=True, physical_id=server.public_id("physical-a"))
        self.assertEqual(engine.health["lofree"], old)
        self.assertIn("lofree", engine._ambiguous_providers)

    def test_physical_scan_cannot_migrate_an_unproven_legacy_reading(self):
        pool = FakePool([receiver("a")], {("lofree", "physical-a"): outcome("legacy", "A", None, cached=True, online=False)})
        engine = self.engine(pool)
        legacy = server.normalise(DeviceStatus("legacy", "Old A", 99, source="lofree"), None, server.utcnow())
        engine.previous[legacy["id"]] = legacy
        result = engine.poll(force=True, physical_id=server.public_id("physical-a"))
        device = next(d for d in result["devices"] if d["name"] == "A")
        self.assertIsNone(device["percent"])
        self.assertIsNone(device["legacyId"])
        self.assertEqual(engine._ambiguous_providers, set())

    def test_multiple_readers_for_selected_physical_are_serial(self):
        pool = FakePool([receiver("a"), receiver("b")])
        engine = self.engine(pool, ("lofree", "standard_hid", "bluetooth"))
        engine.poll(force=True, physical_id=server.public_id("physical-a"))
        self.assertEqual(pool.calls, [("topology", server.public_id("physical-a")), ("lofree", "physical-a"), ("standard_hid", "physical-a")])
        self.assertFalse(pool.physical_overlap)

    def test_native_receiver_is_excluded_without_changing_native_presence(self):
        native = dict(receiver("native"), vendor_id=0x373B, product_id=0x1278)
        pool = FakePool([native, receiver("b")])
        engine = self.engine(pool, ("pulsar", "lofree", "standard_hid"))
        engine.native_mouse = True
        result = engine.poll(force=True, physical_id=server.public_id("physical-native"))
        self.assertEqual(pool.calls, [("topology", server.public_id("physical-native"))])
        self.assertTrue(result["nativeMousePresent"])

    def test_physical_topology_failure_keeps_global_topology_fault_and_devices(self):
        pool, engine, first = self.pair()
        engine._failure("topology", "topology", "original_fault")
        original = copy.deepcopy(engine.health["topology"])
        pool.outcomes[("topology", server.public_id("physical-a"))] = (0, IsolationError("timeout"))
        result = engine.poll(force=True, physical_id=server.public_id("physical-a"))
        self.assertEqual(pool.calls, [("topology", server.public_id("physical-a"))])
        self.assertEqual(result["devices"], first["devices"])
        self.assertEqual(engine.health["topology"], original)
        self.assertIn("topology: timeout", result["errors"])
        scoped = next(h for h in result["health"] if h.get("errorCode") == "timeout")
        global_fault = next(h for h in result["health"] if h.get("errorCode") == "original_fault")
        self.assertEqual(scoped["physicalId"], server.public_id("physical-a"))
        self.assertIsNone(global_fault["physicalId"])
        pool.outcomes.pop(("topology", server.public_id("physical-a")))
        recovered = engine.poll(force=True)
        self.assertNotIn("topology: timeout", recovered["errors"])
        self.assertNotIn("topology: original_fault", recovered["errors"])

    def test_health_uses_matching_hashes_and_never_raw_physical_ids(self):
        pool, engine, _first = self.pair()
        engine._failure("manager", "manager", "read_failed")
        result = engine.poll(force=True, physical_id=server.public_id("physical-a"))
        physical = [h for h in result["health"] if h["provider"] == "lofree"]
        self.assertEqual({h["physicalId"] for h in physical}, {server.public_id("physical-a"), server.public_id("physical-b")})
        self.assertIsNone(next(h["physicalId"] for h in result["health"] if h["provider"] == "manager"))
        self.assertNotIn("physical-a", json.dumps(result))
        self.assertNotIn("physical-b", json.dumps(result))
        self.assertNotIn("physicalId", engine.health["manager"], "snapshot metadata does not mutate stored health")

    def test_physical_health_preserves_unavailable_unsupported_and_error_meanings(self):
        engine = self.engine(FakePool())
        engine._accept("logitech|physical-a", "logitech", [], server.utcnow(), "physical-a", candidate_count=1)
        engine._accept("standard_hid|physical-a", "standard_hid", [], server.utcnow(), "physical-a", candidate_count=0)
        engine._failure("lofree|physical-b", "lofree", "timeout")
        health = {h["provider"]: h for h in engine._snapshot()["health"]}
        self.assertEqual((health["logitech"]["state"], health["standard_hid"]["state"], health["lofree"]["state"]),
                         ("unavailable", "unsupported", "timeout"))
        self.assertEqual(health["logitech"]["physicalId"], server.public_id("physical-a"))
        self.assertEqual(health["standard_hid"]["physicalId"], server.public_id("physical-a"))
        self.assertEqual(health["lofree"]["physicalId"], server.public_id("physical-b"))

    def test_invalid_or_conflicting_targets_never_start_reader_work(self):
        for physical in ("", "pnp:usb\\raw", "device-unknown", "device-" + "A" * 24, [], 1, True):
            pool = FakePool([receiver("a")])
            engine = self.engine(pool)
            result = engine.poll(force=True, physical_id=physical)
            self.assertEqual(pool.calls, [])
            self.assertIn("selection: unknown_target", result["errors"])
        pool = FakePool([receiver("a")])
        engine = self.engine(pool)
        engine.poll(device_id=server.public_id("device"), physical_id=server.public_id("physical-a"))
        self.assertEqual(pool.calls, [])

    def test_direct_mode_does_not_fall_back_to_all_providers(self):
        calls = []
        provider = types.SimpleNamespace(name="fake", poll=lambda: calls.append("called") or [])
        engine = server.BatteryEngine([provider], include_native=True, process_factory=FakePool().factory)
        self.addCleanup(engine.close)
        result = engine.poll(physical_id=server.public_id("physical-a"))
        self.assertEqual(calls, [])
        self.assertIn("selection: unknown_target", result["errors"])


class PhysicalTopologyAndIpcTests(unittest.TestCase):
    def test_only_selected_descriptor_paths_reach_discover_and_native_is_skipped(self):
        paths = [r"\\?\hid#vid_046d&pid_c547#a1", r"\\?\hid#vid_046d&pid_c547#a2",
                 r"\\?\hid#vid_046d&pid_c547#b", r"\\?\hid#vid_373b&pid_1278#native",
                 r"\\?\hid#vid&0002046d_pid&b123#bluetooth"]
        def parent(path):
            self.assertNotIn("native", path)
            self.assertNotIn("bluetooth", path)
            return "pnp:usb\\a" if path.endswith(("a1", "a2")) else "pnp:usb\\b"
        with patch("topology.usb_parent", side_effect=parent), patch("topology.discover", return_value=[]) as discover:
            server.physical_topology(paths, server.public_id("pnp:usb\\a"))
        discover.assert_called_once_with(paths[:2], server.NATIVE)
        with patch("topology.discover") as discover:
            with self.assertRaises(ValueError):
                server.physical_topology(None, server.public_id("pnp:usb\\a"))
        discover.assert_not_called()

    def test_partial_topology_preserves_other_child_instances_and_full_cache(self):
        a, b = receiver("a"), receiver("b")
        commands = [
            {"action": "query", "provider": "lofree", "physicalId": "physical-b", "inventory": [b]},
            {"action": "query", "provider": "bluetooth"},
            {"action": "query", "provider": "lofree", "physicalId": "physical-a", "inventory": [a]},
            {"action": "topology", "paths": ["fixture-all"]},
            {"action": "topology", "paths": ["fixture-all"], "physicalId": server.public_id("physical-a")},
            {"action": "query", "provider": "lofree", "physicalId": "physical-b", "inventory": [b]},
            {"action": "query", "provider": "bluetooth"},
            {"action": "query", "provider": "lofree", "physicalId": "physical-a", "inventory": [a]},
            {"action": "topology", "paths": ["fixture-all"]}]
        created = []
        class Provider:
            def __init__(self, name):
                self.name, self.count = name, 0
                created.append(name)
            def poll(self):
                self.count += 1
                return [DeviceStatus("unit", self.name, self.count, source=self.name)]
        data = b"".join((json.dumps(dict(command, sequence=i), default=wire_encode) + "\n").encode() for i, command in enumerate(commands))
        output = io.StringIO()
        with patch.object(server.sys, "stdin", types.SimpleNamespace(buffer=io.BytesIO(data))), patch.object(server.sys, "stdout", output), \
                patch.object(server, "provider_class", side_effect=lambda name: lambda: Provider(name)), \
                patch.object(server, "recognized_candidate_count", return_value=0), \
                patch.object(server, "physical_topology", return_value=[]), patch("topology.discover", return_value=[a, b]) as discover:
            server.reader_child()
        replies = [json.loads(line, object_hook=wire_decode) for line in output.getvalue().splitlines()]
        self.assertEqual(replies[5]["statuses"][0]["level"], 2)
        self.assertEqual(replies[6]["statuses"][0]["level"], 2)
        self.assertEqual(replies[7]["statuses"][0]["level"], 1)
        self.assertEqual(created.count("lofree"), 3)
        self.assertEqual(created.count("bluetooth"), 1)
        self.assertEqual(replies[-1]["inventory"], [a, b])
        discover.assert_called_once()

    def test_poll_ipc_accepts_physical_scope_and_rejects_invalid_or_mixed_fields(self):
        physical = server.public_id("pnp:usb\\a")
        commands = [{"command": "poll", "physicalId": physical, "force": True, "requestId": 7, "generation": 9}]
        commands += [{"command": "poll", "physicalId": bad} for bad in (None, "", "raw", [], True)]
        commands += [{"command": "poll", "physicalId": physical, "deviceId": value} for value in (None, server.public_id("unit"))]
        commands += [{"command": "set_dpi", "physicalId": physical}, {"command": "poll", "deviceId": "device-unknown"}]
        calls = []
        class Engine:
            def poll(self, **kwargs):
                calls.append(kwargs)
                return {"schema": 2, "complete": True, "devices": []}
            def close(self):
                pass
        with patch.object(server, "BatteryEngine", return_value=Engine()), \
                patch.object(server.sys, "stdin", io.StringIO("".join(json.dumps(command) + "\n" for command in commands))), \
                patch.object(server.sys, "stdout", io.StringIO()), patch.object(server.sys, "argv", ["server.py"]):
            server.main()
        self.assertEqual(len(calls), 1)
        self.assertEqual((calls[0]["physical_id"], calls[0]["device_id"]), (physical, None))
        self.assertEqual((calls[0]["request_id"], calls[0]["generation"]), (7, 9))


if __name__ == "__main__":
    unittest.main()
