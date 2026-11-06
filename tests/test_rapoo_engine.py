"""Rapoo diagnostic transport and feedback using synthetic workers and inputs."""
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
from test_rapoo import R, descriptor, fixture, report
from isolation import wire_encode
import server


class RapooDiagnosticBridgeTests(unittest.TestCase):
    def test_worker_reasons_reach_feedback_without_errors_or_backoff(self):
        missing = "missing or ambiguous FF00:0002 collection; not opened"
        cases = [([descriptor(usage=1)], [], missing, False),
                 ([descriptor(), descriptor(path=b"private-duplicate")], [], missing, False),
                 ([descriptor(input_report_sizes={"7": None})], [],
                  'report 07 unproven; sizes={"7":null}; not opened', False),
                 ([descriptor(input_report_sizes={"8": 19})], [],
                  'report 07 unproven; sizes={"8":19}; not opened', False),
                 ([descriptor()], [], "opened; no input reports; no new measurement", True),
                 ([descriptor()], [report(report_id=6), report()[:8]],
                  "opened; no valid report 07; seen=06/19,07/8", True),
                 ([descriptor()], [report(101)],
                  "opened; no valid report 07; seen=07/19", True)]
        for proof in ("caps", "descriptor"):
            cases.append(([descriptor(input_report_proof=proof)], [],
                          "opened; no input reports; no new measurement; proof=" + proof, True))
        cases.append(([descriptor(input_report_sizes=None, input_report_proof="failed",
                                  report_descriptor_error="long item")], [],
                      "report 07 unproven; sizes=null; proof=caps+descriptor failed; "
                      "descriptor=long item; not opened", False))
        cases.append(([descriptor(input_report_sizes=None, input_report_proof="failed",
                                  report_descriptor_error="private-path-and-bytes")], [],
                      "report 07 unproven; sizes=null; proof=caps+descriptor failed; "
                      "descriptor=unknown; not opened", False))
        for infos, frames, expected, opened in cases:
            command = dict(action="query", sequence=1, provider="rapoo",
                           physicalId="usb:receiver-a", inventory=infos)
            data = (json.dumps(command, default=wire_encode) + "\n").encode()
            with self.subTest(expected=expected), fixture(frames, infos) as bus, \
                    patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                    patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
                server.reader_child()
                response = json.loads(output.getvalue())
                self.assertEqual(response["statuses"], [])
                self.assertEqual(response["candidateCount"], 1)
                self.assertEqual(response["diagnostics"], [expected])
                self.assertEqual(bool(bus.opened), opened)
                self.assertNotIn("private", json.dumps(response))

            class Reader:
                def start(self, command, budget):
                    self.command = command

                def result(self):
                    return {"inventory": infos} if self.command["action"] == "topology" else response

                def close(self):
                    pass

            engine = server.BatteryEngine(process_factory=Reader)
            self.addCleanup(engine.close)
            engine.providers = ["rapoo"]
            with patch.object(engine._hidlist, "interface_paths", return_value=frozenset()):
                snapshot = engine.poll()
            health, = [h for h in snapshot["health"] if h["provider"] == "rapoo"]
            self.assertEqual((health["state"], health["reason"], health["errorCode"]),
                             ("unavailable", "no_measurement", expected))
            self.assertEqual(health["diagnostics"], [expected])
            self.assertNotIn("diagnosticSummary", health)
            self.assertEqual(snapshot["errors"], [])
            self.assertFalse(engine.failures)
            self.assertFalse(engine.retry_after)
            # Repeated snapshots keep the summary a presentation-only value.
            before = {key: dict(value) for key, value in engine.health.items()}
            self.assertEqual(engine._snapshot()["errors"], [])
            self.assertEqual(engine.health, before)
            self.assertTrue(all("errorCode" not in item for item in engine.health.values()))

    def test_provider_mode_recovery_clears_summary_and_real_failure_still_reports(self):
        provider = R.RapooProvider()
        engine = server.BatteryEngine([provider], include_native=True)
        self.addCleanup(engine.close)
        self.addCleanup(provider.close)
        with fixture() as bus:
            health, = engine.poll()["health"]
            self.assertEqual(health["errorCode"], "opened; no input reports; no new measurement")
            bus.frames = [report(63)]
            snapshot = engine.poll()
            health, = snapshot["health"]
            self.assertEqual((health["state"], snapshot["devices"][0]["percent"]), ("ok", 63))
            self.assertNotIn("errorCode", health)
            self.assertEqual(snapshot["errors"], [])
            # A later timeout retains its real error and normal failure counter.
            engine._failure("rapoo", "rapoo", "timeout")
            failed = engine._snapshot()
            self.assertEqual(failed["health"][0]["errorCode"], "timeout")
            self.assertEqual(failed["errors"], ["rapoo: timeout"])
            self.assertEqual(engine.failures, {"rapoo": 1})


if __name__ == "__main__":
    unittest.main()
