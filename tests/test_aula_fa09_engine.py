"""Synthetic AULA worker/history/diagnostic checks; no hardware access."""
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
from test_aula_fa09 import P, aula_entry, aula_fixture, aula_reply
from test_mchose_by_keyboard import entry
from isolation import wire_encode
from providers.vgn_beiying import VgnBeiyingProvider
import server


class AulaFa09BridgeTests(unittest.TestCase):
    def child(self, infos, physical="one"):
        command = dict(action="query", sequence=1, provider=P.name,
                       physicalId=physical, inventory=infos)
        data = (json.dumps(command, default=wire_encode) + "\n").encode()
        with patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
            server.reader_child()
        return json.loads(output.getvalue())

    def test_child_preserves_feature_descriptor_and_physical_scope(self):
        infos = [aula_entry("one"), aula_entry("two")]
        with aula_fixture([aula_reply()], infos) as bus:
            result = self.child(infos)
        status, = result["statuses"]
        self.assertEqual((status["name"], status["level"], status["charging_known"]),
                         ("2.4G 无线键盘（3554:FA09）", 64, False))
        self.assertEqual(status["key"], "mchose_by_keyboard:one:instance:one")
        self.assertEqual(status["legacy_key"], "mchose_by_keyboard:one")
        self.assertEqual(result["candidateCount"], 1)
        self.assertFalse(result.get("errorCode"))
        self.assertEqual(bus.opened, [infos[0]["path"]])
        self.assertEqual(bus.opened, bus.closed)
        self.assertEqual(len(bus.feature_writes), 1)

    def test_zero_silence_io_failure_and_backoff_keep_history_without_errors(self):
        infos = [aula_entry(), entry(path=b"other-collection", usage_page=0xFF02)]
        engine = server.BatteryEngine([P(), VgnBeiyingProvider()], include_native=True)
        self.addCleanup(engine.close)
        with aula_fixture([aula_reply()], infos):
            first, = engine.poll()["devices"]
        self.assertEqual((first["name"], first["percent"], first["charging"]),
                         ("2.4G 无线键盘（3554:FA09）", 64, None))
        self.assertEqual(first["id"], server.public_id("mchose_by_keyboard:receiver-a"))
        for frames, options in (([aula_reply(0)], {}), ([], {}),
                                ([aula_reply()], dict(error_at="feature_read")),
                                ([aula_reply()], {})):
            with self.subTest(frames=frames, options=options), aula_fixture(frames, infos, **options):
                snapshot = engine.poll()
            stale, = snapshot["devices"]
            self.assertEqual(stale["id"], first["id"])
            self.assertEqual((stale["percent"], stale["confirmedAtUtc"]), (64, first["confirmedAtUtc"]))
            self.assertFalse(stale["fresh"])
            self.assertEqual(stale["connectionState"], "unconfirmed")
            self.assertIsNone(stale["charging"])
            self.assertEqual(snapshot["errors"], [])
            self.assertFalse(engine.failures)
        # A fresh reader that actually encounters an I/O exception also stays
        # non-error; the preceding sequence may already be backing off.
        engine2 = server.BatteryEngine([P()], include_native=True)
        self.addCleanup(engine2.close)
        with aula_fixture([aula_reply()], error_at="feature_read"):
            self.assertEqual(engine2.poll()["errors"], [])

    def test_child_rejection_summary_reaches_health_without_behavior_change(self):
        cases = ((entry("one", input_report_sizes=None), "report sizes unproven"),
                 (aula_entry("one", feature_report_sizes=None), "report sizes unproven"),
                 (aula_entry("one", product_string="AULA F87"), "receiver name unrecognized"))
        for info, summary in cases:
            with self.subTest(info=info), aula_fixture([aula_reply()], [info]) as bus:
                result = self.child([info])
            self.assertEqual(bus.opened, [])

            class Reader:
                def start(self, command, budget):
                    self.command = command

                def result(self):
                    return {"inventory": [info]} if self.command["action"] == "topology" else result

                def close(self):
                    pass

            engine = server.BatteryEngine(process_factory=Reader)
            self.addCleanup(engine.close)
            engine.providers = [P.name]
            with patch.object(engine._hidlist, "interface_paths", return_value=frozenset()):
                snapshot = engine.poll()
            health, = [h for h in snapshot["health"] if h["provider"] == P.name]
            self.assertEqual((health["state"], health["reason"], health["errorCode"]),
                             ("unavailable", "no_measurement", summary))
            self.assertNotIn("diagnosticSummary", health)
            self.assertEqual(snapshot["errors"], [])
            self.assertFalse(engine.failures)
            self.assertFalse(engine.retry_after)

    def test_summary_is_fixed_scoped_and_cleared_after_recovery(self):
        engine = server.BatteryEngine([], include_native=True)
        self.addCleanup(engine.close)
        diagnostics = ["report IDs/lengths unknown or incompatible: nothing sent"]
        engine._accept("r", P.name, [], server.utcnow(), "p", 1, diagnostics)
        self.assertEqual(engine.health["r"]["diagnosticSummary"], "report sizes unproven")
        for name, lines in ((P.name, ["private-device-path"]), ("vgn_keyboard", diagnostics),
                            (P.name, [])):
            engine._accept("r", name, [], server.utcnow(), "p", 1, lines)
            self.assertNotIn("diagnosticSummary", engine.health["r"])
        with aula_fixture([aula_reply()]):
            statuses = P().poll()
        engine._accept("r", P.name, statuses, server.utcnow(), "p", 1, diagnostics)
        self.assertEqual(engine.health["r"]["state"], "ok")
        self.assertNotIn("diagnosticSummary", engine.health["r"])
        self.assertNotIn("errorCode", engine._snapshot()["health"][0])

    def test_feedback_projection_preserves_internal_health_and_real_errors(self):
        engine = server.BatteryEngine([], include_native=True)
        self.addCleanup(engine.close)
        diagnostics = ["report IDs/lengths unknown or incompatible: nothing sent"]
        engine._accept("r", P.name, [], server.utcnow(), "p", 1, diagnostics)
        engine._failure("other", "other", "read_failed")
        previous = dict(engine.health["r"])
        retry_after = dict(engine.retry_after)
        for complete in (False, True, False):
            snapshot = engine._snapshot(complete=complete)
            health, = [h for h in snapshot["health"] if h["provider"] == P.name]
            self.assertEqual((health["state"], health["reason"], health["errorCode"]),
                             ("unavailable", "no_measurement", "report sizes unproven"))
            self.assertEqual(snapshot["errors"], ["other: read_failed"])
            self.assertEqual(engine.health["r"], previous)
            self.assertNotIn("errorCode", engine.health["r"])
            self.assertEqual(engine.failures, {"other": 1})
            self.assertEqual(engine.retry_after, retry_after)
        # A real FA09 failure still replaces the prior diagnostic and keeps
        # normal error reporting/back-off; it must not be hidden by the summary.
        engine._failure("r", P.name, "timeout")
        snapshot = engine._snapshot()
        health, = [h for h in snapshot["health"] if h["provider"] == P.name]
        self.assertEqual((health["state"], health["errorCode"]), ("timeout", "timeout"))
        self.assertIn(P.name + ": timeout", snapshot["errors"])
        self.assertEqual(engine.failures["r"], 1)
        self.assertIn("r", engine.retry_after)


if __name__ == "__main__":
    unittest.main()
