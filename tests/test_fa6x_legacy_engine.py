"""Legacy FA65 child/parent health using synthetic inventory and strict fake HID."""
import io
import json
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "engine"))
sys.path.insert(0, str(ROOT / "engine" / "tests"))
from test_fa6x_passive import F, descriptor, fixture, legacy_inventory, report
from isolation import wire_encode, wire_decode
import server


def child_replies(commands):
    lines = "".join(json.dumps(command, default=wire_encode) + "\n" for command in commands)
    with patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(lines.encode()))), \
            patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
        server.reader_child()
    return [json.loads(line, object_hook=wire_decode) for line in output.getvalue().splitlines()]


class ReplyReader:
    def __init__(self, inventory, replies):
        self.inventory, self.replies, self.pending = inventory, replies, None

    def start(self, command, budget):
        if self.pending is not None:
            raise AssertionError("reader reentered")
        self.pending = command

    def result(self):
        command, self.pending = self.pending, None
        if command["action"] == "topology":
            return {"inventory": self.inventory}
        if command.get("provider") == "bluetooth":
            return {"statuses": [], "checkedAtUtc": server.utcnow()}
        if command["action"] != "query" or command["provider"] != "fa6x_passive":
            raise AssertionError(command)
        return self.replies.pop(0)

    def close(self):
        self.pending = None


class Fa6xLegacyEngineTests(unittest.TestCase):
    def query(self, infos, sequence=1):
        return dict(action="query", sequence=sequence, provider="fa6x_passive",
                    physicalId="usb:a", inventory=infos)

    def engine(self, infos, replies):
        engine = server.BatteryEngine(process_factory=lambda: ReplyReader(infos, replies))
        engine.providers = ["fa6x_passive"]
        self.addCleanup(engine.close)
        return engine

    def poll(self, engine):
        with patch.object(engine._hidlist, "interface_paths", return_value=frozenset({"PRIVATE-PATH"})):
            return engine.poll(force=True)

    def health(self, snapshot):
        health, = [h for h in snapshot["health"] if h["provider"] == "fa6x_passive"]
        return health

    def assert_safe(self, value):
        serialized = json.dumps(value)
        for private in ("PRIVATE", "03 93 40 00 43", "0393400043000000"):
            self.assertNotIn(private, serialized)

    def assert_not_failure(self, engine, snapshot):
        self.assertEqual(snapshot["errors"], [])
        self.assertEqual((engine.failures, engine.retry_after), ({}, {}))
        internal, = [h for h in engine.health.values() if h["provider"] == "fa6x_passive"]
        self.assertNotIn("errorCode", internal)
        self.assert_safe(snapshot["health"])
        self.assert_safe(snapshot["devices"])

    def test_sc650_reported_samples_and_invalid_lengths_cross_json_without_refresh(self):
        infos = [descriptor(input_report_length=5, input_report_sizes={"3": 5})]
        broken = [dict(infos[0], input_report_sizes=None)]
        commands = [self.query(selected, n)
                    for n, selected in enumerate((broken, infos, infos, infos, []), 1)]
        frames = [[3, 0x8E, 0x40, 1, 91], [], [3, 0x8E, 0x40, 1, 49], [],
                  [3, 0x8E, 0x40, 1], [3, 0x8E, 0x40, 1, 91, 0],
                  [3, 0x8E, 0x41, 1, 91]]
        with fixture(frames, infos) as (bus, _):
            replies = child_replies(commands)
            self.assertEqual(bus.opened, [infos[0]["path"]])
            self.assertEqual(bus.closed, bus.opened)
        self.assertEqual([r["candidateCount"] for r in replies], [1, 1, 1, 1, 0])
        self.assertEqual(replies[0]["diagnostics"], [
            "input report 03 length unproven; declared=out:unknown,in:unknown,feature:unknown; not opened"])
        engine = self.engine(infos, replies)
        rejected = self.poll(engine)
        self.assertEqual(rejected["devices"], [])
        self.assertEqual(self.health(rejected)["errorCode"], "in:03 unproven;declared=out:unknown,...")
        self.assert_not_failure(engine, rejected)
        for percent in (91, 49):
            fresh = self.poll(engine)
            self.assertEqual(self.health(fresh)["state"], "ok")
            device, = fresh["devices"]
            self.assertEqual((device["percent"], device["fresh"], device["name"], device["verificationLevel"]),
                             (percent, True, F.PIDS[0xFA60], "C"))
            self.assertIsNone(device["charging"])
            self.assertIsNone(device["dpi"])
            self.assert_not_failure(engine, fresh)
        silent = self.poll(engine)
        historical, = silent["devices"]
        self.assertEqual((historical["percent"], historical["fresh"], historical["chargingKnown"]),
                         (49, False, False))
        self.assertEqual((historical["measuredAtUtc"], historical["confirmedAtUtc"]),
                         (device["measuredAtUtc"], device["confirmedAtUtc"]))
        self.assertEqual(self.health(silent)["reason"], "no_measurement")
        self.assert_not_failure(engine, silent)
        self.assert_not_failure(engine, self.poll(engine))

    def test_child_proof_failure_recovery_silence_and_removal_cross_json(self):
        infos = legacy_inventory()
        broken = [dict(infos[0], input_report_sizes=None), infos[1]]
        commands = [self.query(selected, n) for n, selected in enumerate((broken, infos, infos, []), 1)]
        with fixture([report(67, model=0x93, state=0)], infos) as (bus, _):
            replies = child_replies(commands)
            self.assertEqual(bus.opened, [infos[0]["path"]])
            self.assertEqual(bus.closed, bus.opened)
        self.assertEqual([r["sequence"] for r in replies], [1, 2, 3, 4])
        self.assertEqual([r["candidateCount"] for r in replies], [1, 1, 1, 0])
        self.assertEqual([r["diagnostics"] for r in replies], [
            ["input report 03 length unproven; declared=out:unknown,in:unknown,feature:unknown; not opened"], [],
            ["no battery notification; no new measurement"], []])
        engine = self.engine(infos, replies)
        rejected = self.poll(engine)
        self.assertEqual(rejected["devices"], [])
        self.assertEqual(self.health(rejected)["errorCode"], "in:03 unproven;declared=out:unknown,...")
        self.assert_not_failure(engine, rejected)

        fresh = self.poll(engine)
        self.assertEqual(self.health(fresh)["state"], "ok")
        self.assertFalse(self.health(fresh).get("errorCode"))
        device, = fresh["devices"]
        self.assertEqual((device["percent"], device["fresh"], device["verificationLevel"]), (67, True, "C"))
        self.assertEqual((device["kind"], device["name"]), ("mouse", F.PIDS[0xFA65]))
        self.assertIsNone(device["charging"])
        self.assertIsNone(device["dpi"])
        self.assert_not_failure(engine, fresh)

        silent = self.poll(engine)
        self.assertEqual((self.health(silent)["state"], self.health(silent)["reason"]),
                         ("unavailable", "no_measurement"))
        self.assertEqual(self.health(silent)["errorCode"], "no valid battery notification")
        historical, = silent["devices"]
        self.assertEqual((historical["percent"], historical["fresh"], historical["connectionState"],
                          historical["chargingKnown"]), (67, False, "unconfirmed", False))
        self.assertEqual(historical["measuredAtUtc"], device["measuredAtUtc"])
        self.assertEqual(historical["confirmedAtUtc"], device["confirmedAtUtc"])
        self.assert_not_failure(engine, silent)

        cleared = self.poll(engine)
        self.assertFalse(self.health(cleared).get("errorCode"))
        self.assertEqual(self.health(cleared)["diagnostics"], [])
        self.assertNotIn("diagnosticSummary", engine.health["fa6x_passive|usb:a"])
        self.assert_not_failure(engine, cleared)

    def test_identity_release_and_proof_failures_are_distinct_from_silence(self):
        base = legacy_inventory()
        cases = [([base[0]], "legacy FA65 companion missing or ambiguous"),
                 ([base[1]], "legacy FA65 state input missing or ambiguous"),
                 ([base[0], dict(base[1], release_number=True)], "legacy FA65 release unproven"),
                 ([dict(base[0], path=b""), base[1]], "legacy FA65 collection path unproven"),
                 ([dict(base[0], input_report_sizes={"3": 4}), base[1]],
                  "in:03 unproven;declared=out:unknown,in:03/4,..."),
                 (base, "no valid battery notification")]
        for infos, expected in cases:
            with self.subTest(reason=expected), fixture([], infos) as (bus, _):
                response, = child_replies([self.query(infos)])
                self.assertEqual(len(bus.opened), int(infos is base))
            engine = self.engine(infos, [response])
            snapshot = self.poll(engine)
            health = self.health(snapshot)
            self.assertEqual((health["state"], health["reason"], health["errorCode"]),
                             ("unavailable", "no_measurement", expected))
            self.assertLessEqual(len(health["errorCode"]), 48)
            self.assert_not_failure(engine, snapshot)

    def test_only_exact_fixed_diagnostics_can_cross_child_or_parent_boundaries(self):
        reason = "legacy FA65 release unproven; not opened"
        unsafe = ["PRIVATE-PATH PRIVATE-PRODUCT PRIVATE-SERIAL PRIVATE-EXCEPTION 03 93 40 00 43",
                  reason + "; PRIVATE-PATH", " " + reason, {"PRIVATE": "value"}, None]
        provider = F.Fa6xPassiveProvider()
        provider._diag = unsafe + [reason]
        self.assertEqual(server.reader_diagnostics(provider), [reason])
        engine = server.BatteryEngine([], include_native=True)
        self.addCleanup(engine.close)
        engine._accept("fa6x_passive|usb:a", "fa6x_passive", [], server.utcnow(), "usb:a", 1,
                       diagnostics=unsafe + [reason])
        snapshot = engine._snapshot()
        self.assertEqual(self.health(snapshot)["diagnostics"], [reason])
        self.assertEqual(self.health(snapshot)["errorCode"], "legacy FA65 release unproven")
        self.assert_not_failure(engine, snapshot)
        for message, expected in server.FA6X_DIAGNOSTICS.items():
            engine._accept("fa6x_passive|usb:a", "fa6x_passive", [], server.utcnow(), "usb:a", 1,
                           diagnostics=[message])
            snapshot = engine._snapshot()
            self.assertEqual(self.health(snapshot)["errorCode"], expected)
            self.assertLessEqual(len(expected), 48)
            self.assert_not_failure(engine, snapshot)

    def test_private_identity_packets_and_io_exceptions_are_not_public_diagnostics(self):
        infos = [dict(d, path=b"PRIVATE-STATE" if i == 0 else b"PRIVATE-COMM",
                      product_string="PRIVATE-PRODUCT", serial_number="PRIVATE-SERIAL")
                 for i, d in enumerate(legacy_inventory())]
        for failure in ("open", "read", "invalid"):
            frames = [OSError("PRIVATE-EXCEPTION")] if failure == "read" else [
                report(67, model=0x93, state=3)]
            with self.subTest(failure=failure), fixture(frames, infos) as (bus, _):
                bus.fail_open = failure == "open"
                response, = child_replies([self.query(infos)])
            self.assertEqual(response["statuses"], [])
            self.assert_safe(response)
            engine = self.engine(infos, [response])
            snapshot = self.poll(engine)
            self.assertTrue(self.health(snapshot)["errorCode"])
            self.assert_not_failure(engine, snapshot)

    def test_enumeration_failure_keeps_history_and_fixed_reason_without_failure_backoff(self):
        infos = legacy_inventory()
        with fixture([report(67, model=0x93, state=0)], infos) as (_, provider):
            first, = provider.poll()
            with patch.object(F.hidlist, "enumerate", side_effect=OSError("PRIVATE-EXCEPTION")):
                stale, = provider.poll()
            self.assertEqual((stale.level, stale.measured_at_utc, stale.cached), (67, first.measured_at_utc, True))
            diagnostic = json.loads(json.dumps(server.reader_diagnostics(provider)))
        engine = server.BatteryEngine([], include_native=True)
        self.addCleanup(engine.close)
        engine._accept("fa6x_passive|usb:a", "fa6x_passive", [stale], server.utcnow(), "usb:a", 1,
                       diagnostics=diagnostic)
        snapshot = engine._snapshot()
        self.assertEqual(self.health(snapshot)["errorCode"], "enumeration failed; retaining history")
        self.assert_not_failure(engine, snapshot)

    def test_old_web_diagnostics_also_reach_health_without_new_device_io(self):
        info = descriptor(0xFA65)
        with fixture([], [info]) as (bus, _):
            response, = child_replies([self.query([info])])
            self.assertEqual(bus.opened, [info["path"]])
            self.assertEqual(len(bus.reads), 1)
        engine = self.engine([info], [response])
        snapshot = self.poll(engine)
        self.assertEqual(self.health(snapshot)["errorCode"], "no valid battery notification")
        self.assert_not_failure(engine, snapshot)

    def test_unenabled_fa93_stays_unsupported_without_a_legacy_read(self):
        infos = [dict(d, product_id=0xFA93) for d in legacy_inventory()]
        with fixture([], infos) as (bus, _):
            response, = child_replies([self.query(infos)])
            self.assertEqual((response["statuses"], response["candidateCount"], response["diagnostics"]), ([], 0, []))
            self.assertEqual((bus.opened, bus.reads), ([], []))


if __name__ == "__main__":
    unittest.main()
