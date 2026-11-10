"""NANO registration/bridge regressions, with synthetic HID exclusively."""
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
import test_atk_nano as N
from catalog import verification_for
from isolation import wire_encode
from known_ids import build_index
from test_pulsar_diagnostics import child_replies, ReplyReader
import server


class AtkNanoBridgeTests(unittest.TestCase):
    def test_scoped_child_and_manager_publish_safe_failures_and_clear_on_recovery(self):
        private = ("PRIVATE-PATH", "PRIVATE-SERIAL", "PRIVATE-PRODUCT", "PRIVATE-EXCEPTION")
        info = N.entry(path=private[0].encode(), serial_number=private[1],
                       product_string=private[2], report_descriptor_error=private[3])
        unproven = dict(info, input_report_sizes=None)
        commands = [dict(action="query", sequence=i, provider="pulsar",
                         physicalId=info["physical_id"], inventory=[row])
                    for i, row in enumerate((unproven, info, info, info), 1)]
        # One persistent child provider: failed proof -> 73% -> silence -> 0%.
        with N.fixture([N.reply(73), [], N.reply(0)], [info]) as bus:
            responses = child_replies(commands)
        self.assertEqual(bus.writes, [(info["path"], N.REQUEST)] * 3)
        self.assertEqual(len(bus.reads), 3)
        self.assertEqual(bus.opened, bus.closed)
        expected = [["NANO input report unproven"], [], ["NANO no valid battery reply"], []]
        self.assertEqual([r["diagnostics"] for r in responses], expected)
        self.assertEqual([r["candidateCount"] for r in responses], [1] * 4)
        self.assertEqual([r["sequence"] for r in responses], [1, 2, 3, 4])
        self.assertEqual([[s["level"] for s in r["statuses"]] for r in responses], [[], [73], [], [0]])
        engine = server.BatteryEngine(process_factory=lambda: ReplyReader([info], responses))
        engine.providers = ["pulsar"]
        self.addCleanup(engine.close)
        snapshots = []
        for reasons in expected:
            with patch.object(engine._hidlist, "interface_paths", return_value=frozenset({"fake-path"})):
                snapshot = engine.poll(force=True)
            snapshots.append(snapshot)
            health, = [h for h in snapshot["health"] if h["provider"] == "pulsar"]
            self.assertEqual(health["scope"], "device")
            self.assertEqual(health["diagnostics"], reasons)
            self.assertEqual(health.get("errorCode", ""), reasons[-1] if reasons else "")
            self.assertLessEqual(len(health.get("errorCode", "")), 48)
            self.assertEqual(snapshot["errors"], [])
            self.assertEqual((engine.failures, engine.retry_after), ({}, {}))
            self.assertTrue(all("errorCode" not in h for h in engine.health.values()))
            public = json.dumps((snapshot["health"], [r["diagnostics"] for r in responses]))
            for secret in private:
                self.assertNotIn(secret, public)
        initial, fresh, stale, zero = snapshots
        initial_health, = [h for h in initial["health"] if h["provider"] == "pulsar"]
        self.assertEqual((initial_health["state"], initial_health["reason"]),
                         ("unavailable", "no_measurement"))
        self.assertEqual(initial["devices"], [])
        first, = fresh["devices"]
        history, = stale["devices"]
        last, = zero["devices"]
        self.assertEqual((first["percent"], first["fresh"]), (73, True))
        self.assertEqual((history["percent"], history["fresh"], history["connectionState"]),
                         (73, False, "unconfirmed"))
        for field in ("measuredAtUtc", "confirmedAtUtc"):
            self.assertEqual(history[field], first[field])
        self.assertEqual((last["percent"], last["fresh"]), (0, True))
        for snapshot, state in ((fresh, "ok"), (stale, "unavailable"), (zero, "ok")):
            self.assertTrue(all(h["state"] == state for h in snapshot["health"]
                                if h["provider"] == "pulsar"))
            device, = snapshot["devices"]
            self.assertIsNone(device["dpi"])

    def test_untrusted_private_log_suffixes_never_reach_child_or_manager(self):
        secret = "PRIVATE-PATH PRIVATE-SERIAL PRIVATE-PRODUCT PRIVATE-EXCEPTION 08 7D 72"
        proof = "report IDs/lengths unknown or incompatible: nothing sent"
        lines = [secret, proof + "; " + secret,
                 "battery I/O failed; connection unknown " + secret,
                 "NANO requires one proven input report: nothing sent " + secret]
        info = N.entry()
        command = dict(action="query", sequence=1, provider="pulsar",
                       physicalId=info["physical_id"], inventory=[info])
        with N.fixture(), patch.object(N.P, "AtkNanoProvider") as factory:
            factory.return_value.poll.return_value = []
            factory.return_value.diagnostics.return_value = lines
            response, = child_replies([command])
        self.assertEqual(response["diagnostics"], ["NANO report IDs/lengths unproven"])
        engine = server.BatteryEngine(process_factory=lambda: ReplyReader([info], [response]))
        engine.providers = ["pulsar"]
        self.addCleanup(engine.close)
        with patch.object(engine._hidlist, "interface_paths", return_value=frozenset({"fake-path"})):
            snapshot = engine.poll(force=True)
        health, = [h for h in snapshot["health"] if h["provider"] == "pulsar"]
        self.assertEqual(health["errorCode"], "NANO report IDs/lengths unproven")
        self.assertLessEqual(len(health["errorCode"]), 48)
        self.assertNotIn("PRIVATE", json.dumps((response, snapshot)))
        self.assertEqual(snapshot["errors"], [])
        self.assertEqual((engine.failures, engine.retry_after), ({}, {}))

    def test_existing_pulsar_registration_indexes_exact_mouse_id(self):
        self.assertIn(("pulsar", "PulsarProvider"), server.PROVIDERS)
        self.assertIn(0x373B, server.PROVIDER_VIDS["pulsar"])
        self.assertEqual(verification_for("pulsar")[0], "C")
        for root in (None, ENGINE):
            rows = [row for row in build_index(engine_root=root)["devices"]
                    if (row["vendorId"], row["productId"]) == (0x373B, 0x1216)]
            self.assertEqual(len(rows), 1)
            self.assertEqual((rows[0]["provider"], rows[0]["kind"], rows[0]["name"]),
                             ("pulsar", "mouse", "ATK / VXE NANO receiver"))
        self.assertEqual(server.recognized_candidate_count("pulsar", [N.entry()]), 1)
        self.assertEqual(server.recognized_candidate_count(
            "pulsar", [N.entry(), N.entry(vendor_id=0x3554)]), 1)
        self.assertFalse(N.P._dpi_eligible(N.entry()))

    def test_reader_child_preserves_runtime_input_proof_and_unknown_charging(self):
        for proven in (True, False):
            info = N.entry(report_id=0, length=8,
                           input_report_sizes={"0": 8} if proven else None)
            request = dict(action="query", sequence=19, provider="pulsar",
                           physicalId=info["physical_id"], inventory=[info])
            data = (json.dumps(request, default=wire_encode) + "\n").encode()
            with self.subTest(proven=proven), N.fixture([N.reply(73, 0, 8)], [info]) as bus, \
                    patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                    patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
                server.reader_child()
            response = json.loads(output.getvalue())
            self.assertEqual(response["candidateCount"], 1)
            self.assertFalse(response.get("errorCode"))
            self.assertEqual([s["level"] for s in response["statuses"]], [73] if proven else [])
            self.assertEqual(bus.writes, [(info["path"], N.REQUEST)] if proven else [])
            if proven:
                self.assertFalse(response["statuses"][0]["charging_known"])

    def test_public_reading_is_mouse_c_and_silence_retains_only_unconfirmed_history(self):
        engine = server.BatteryEngine([N.P.PulsarProvider()], include_native=True)
        self.addCleanup(engine.close)
        with N.fixture():
            empty = engine.poll()
        self.assertEqual(empty["devices"], [])
        self.assertEqual(empty["errors"], [])
        with N.fixture([N.reply()]):
            first, = engine.poll()["devices"]
        self.assertEqual((first["kind"], first["verificationLevel"], first["percent"]),
                         ("mouse", "C", 73))
        self.assertIsNone(first["charging"])
        self.assertNotEqual(first["physicalId"], N.entry()["physical_id"])
        with N.fixture():
            snapshot = engine.poll()
        stale, = snapshot["devices"]
        self.assertEqual(stale["percent"], 73)
        self.assertEqual(stale["confirmedAtUtc"], first["confirmedAtUtc"])
        self.assertEqual(stale["connectionState"], "unconfirmed")
        self.assertFalse(stale["fresh"])
        self.assertIsNone(stale["charging"])
        self.assertEqual(snapshot["errors"], [])


if __name__ == "__main__":
    unittest.main()
