"""MCHOSE V3 health through synthetic inventory and fake HID only."""
import io
import json
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "engine" / "tests"))
from test_mchose_v3 import M, BUSY, CHARGING, REQUEST, fixture, inventory as v3_inventory
from isolation import wire_encode
import server


def inventory(pid=0x1018):
    return [dict(d, physical_id="receiver-a") for d in v3_inventory(pid)]


class MchoseV3HealthTests(unittest.TestCase):
    def setUp(self):
        self.engine = server.BatteryEngine([], include_native=True)
        self.addCleanup(self.engine.close)
        self.reader = "mchose|receiver-a"

    def accept(self, provider, infos, statuses=(), candidate_count=None):
        if candidate_count is None:
            candidate_count = server.recognized_candidate_count("mchose", infos)
        self.engine._accept(self.reader, "mchose", statuses, server.utcnow(),
                            physical="receiver-a", candidate_count=candidate_count,
                            diagnostics=server.reader_diagnostics(provider))
        return self.engine._snapshot()

    def test_exact_v3_candidates_are_counted_once_per_vid_pid(self):
        for pid in (0x1014, 0x1018):
            with self.subTest(pid=pid):
                self.assertEqual(server.recognized_candidate_count("mchose", inventory(pid) * 2), 1)
        self.assertEqual(server.recognized_candidate_count("mchose", inventory(0x1014) + inventory(0x1018)), 2)
        self.assertEqual(server.recognized_candidate_count("mchose", []), 0)

    def test_unknown_pids_and_other_vendors_are_not_supported_candidates(self):
        wrong = [dict(d, vendor_id=vid) for pid in (0x1014, 0x1018)
                 for vid in (0x5253, 0x2023, 0xA8A5, 0x9999) for d in inventory(pid)]
        wrong += [d for pid in (0x1013, 0x1019, 0x6008, 0x600A, 0x3033) for d in inventory(pid)]
        for info in wrong:
            with self.subTest(vid=info["vendor_id"], pid=info["product_id"]):
                self.assertEqual(server.recognized_candidate_count("mchose", [info]), 0)
        self.assertEqual(server.recognized_candidate_count("mchose", wrong + inventory(0x1014)), 1)

    def test_missing_collection_keeps_unavailable_health_without_a_card(self):
        for pid in (0x1014, 0x1018):
            infos = [d for d in inventory(pid) if d["usage_page"] != 0xFF01]
            provider = M.MchoseProvider()
            with self.subTest(pid=pid), fixture([], infos) as bus:
                statuses = server.scoped_poll(provider, infos, "receiver-a")
                snapshot = self.accept(provider, infos, statuses)
                self.assertEqual((snapshot["devices"], snapshot["errors"]), ([], []))
                health, = snapshot["health"]
                self.assertEqual(health["state"], "unavailable")
                self.assertEqual(health.get("errorCode"), "V3 missing or ambiguous FF01:0001 collection")
                self.assertEqual((bus.opened, bus.sent), ([], []))

    def test_proof_and_io_failures_outrank_identity_and_successful_proof_notes(self):
        for pid in (0x1014, 0x1018):
            infos = inventory(pid)
            info = next(d for d in infos if d["usage_page"] == 0xFF01)
            cases = [([info, dict(info, path=b"duplicate")], None, [],
                      "V3 missing or ambiguous FF01:0001 collection"),
                     ([dict(info, path=b"")], None, [], "V3 collection path unresolved")]
            for direction in ("input", "output"):
                cases.append(([dict(info, **{direction + "_report_sizes": None,
                                             direction + "_report_proof": "failed",
                                             "report_descriptor_error": "collection maximum mismatch"})],
                              None, [], "V3 " + direction + " report 4D/64 length unproven"))
            cases += [(infos, fail, [], "V3 power I/O failed") for fail in ("open", "write", "read")]
            cases.append((infos, "short", [], "V3 power query write failed or short"))
            cases += [(infos, None, replies, "no valid V3 power reply")
                      for replies in ([], [BUSY], [bytes(64)])]
            for selected, fail, replies, expected in cases:
                provider = M.MchoseProvider()
                with self.subTest(pid=pid, reason=expected, fail=fail), fixture(replies, selected, fail) as bus:
                    statuses = server.scoped_poll(provider, selected, "receiver-a")
                    snapshot = self.accept(provider, selected, statuses)
                    self.assertEqual((snapshot["devices"], snapshot["errors"]), ([], []))
                    health, = snapshot["health"]
                    self.assertEqual(health["state"], "unavailable")
                    self.assertEqual(health.get("errorCode"), expected)
                    self.assertLessEqual(len(health["errorCode"]), 48)
                    diagnostic = " ".join(health["diagnostics"])
                    self.assertIn(expected, diagnostic)
                    if "length unproven" in expected:
                        self.assertIn("proof=caps+descriptor failed; descriptor=collection maximum mismatch", diagnostic)
                    self.assertLessEqual(len(health["diagnostics"]), 16)
                    self.assertTrue(all(len(line) <= 1024 for line in health["diagnostics"]))
                    attempted = expected.startswith("V3 power") or expected.startswith("no valid")
                    self.assertEqual(bus.opened, [info["path"]] if attempted else [])
                    self.assertEqual(bus.sent, [REQUEST] if attempted and fail != "open" else [])
                    self.assertEqual(self.engine.failures, {})
                    self.assertEqual(self.engine.retry_after, {})

    def test_compact_reason_is_separate_from_real_errors_and_retry_state(self):
        infos = inventory(0x1014)
        provider = M.MchoseProvider()
        with fixture([], infos):
            self.assertEqual(server.scoped_poll(provider, infos, "receiver-a"), [])
        snapshot = self.accept(provider, infos, candidate_count=1)
        health, = snapshot["health"]
        self.assertEqual(health.get("errorCode"), "no valid V3 power reply")
        self.assertNotIn("errorCode", self.engine.health[self.reader])
        self.assertEqual(snapshot["errors"], [])
        self.assertEqual(self.engine.failures, {})
        self.assertEqual(self.engine.retry_after, {})
        self.assertEqual(self.engine._snapshot()["health"], snapshot["health"])

    def test_real_timeout_keeps_precedence_and_existing_backoff(self):
        self.engine._failure(self.reader, "mchose", "timeout")
        self.engine.health[self.reader]["diagnosticSummary"] = "no valid V3 power reply"
        failures, retries = dict(self.engine.failures), dict(self.engine.retry_after)
        snapshot = self.engine._snapshot()
        health, = snapshot["health"]
        self.assertEqual((health["state"], health["errorCode"]), ("timeout", "timeout"))
        self.assertNotIn("diagnosticSummary", health)
        self.assertEqual(snapshot["errors"], ["mchose: timeout"])
        self.assertEqual((self.engine.failures, self.engine.retry_after), (failures, retries))
        self.assertEqual(self.engine.health[self.reader]["diagnosticSummary"], "no valid V3 power reply")

    def test_raw_legacy_logs_and_exception_identity_never_reach_health(self):
        infos = [dict(d, path=b"C:/PRIVATE-HID-PATH", product_string="PRIVATE-PRODUCT",
                      serial_number="PRIVATE-SERIAL") for d in inventory(0x1014)]
        provider = M.MchoseProvider()
        with fixture([], infos) as bus, patch.object(bus, "open_path", side_effect=OSError("PRIVATE-EXCEPTION")):
            statuses = server.scoped_poll(provider, infos, "receiver-a")
        provider._diag.append("C:/PRIVATE-HID-PATH PRIVATE-PRODUCT PRIVATE-SERIAL PRIVATE-EXCEPTION " + CHARGING.hex())
        snapshot = self.accept(provider, infos, statuses)
        self.assertEqual(snapshot["health"][0].get("errorCode"), "V3 power I/O failed")
        serialized = json.dumps(snapshot)
        self.assertNotIn("PRIVATE", serialized)
        self.assertNotIn(CHARGING.hex(), serialized)
        provider = M.MchoseProvider()
        provider._diag.append("V3 power I/O failed; C:/PRIVATE-LEGACY PRIVATE-SERIAL")
        self.assertEqual(server.reader_diagnostics(provider), [])
        snapshot = self.accept(provider, [], candidate_count=0)
        self.assertEqual(snapshot["health"][0]["state"], "unsupported")
        self.assertFalse(snapshot["health"][0].get("errorCode"))
        self.assertNotIn("PRIVATE", json.dumps(snapshot))

    def test_child_query_exports_only_safe_v3_diagnostics_and_candidate_count(self):
        for pid in (0x1014, 0x1018):
            infos = [dict(d, path=b"C:/PRIVATE-HID-PATH", product_string="PRIVATE-PRODUCT",
                          serial_number="PRIVATE-SERIAL", input_report_sizes=None,
                          input_report_proof="failed", report_descriptor_error="PRIVATE-DESCRIPTOR-ERROR")
                     for d in inventory(pid)]
            request = dict(action="query", sequence=1, provider="mchose", inventory=infos, physicalId="receiver-a")
            data = (json.dumps(request, default=wire_encode) + "\n").encode()
            with self.subTest(pid=pid), fixture([], infos) as bus, \
                    patch.object(M.MchoseProvider, "diagnostics", side_effect=AssertionError("no legacy log export")), \
                    patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                    patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
                server.reader_child()
                response = json.loads(output.getvalue())
                self.assertEqual((response["statuses"], response["candidateCount"]), ([], 1))
                self.assertIn("descriptor=unknown", " ".join(response["diagnostics"]))
                self.assertNotIn("PRIVATE", output.getvalue())
                self.assertEqual((bus.opened, bus.sent, bus.reads), ([], [], []))
            self.engine._accept(self.reader, "mchose", [], response["checkedAtUtc"], "receiver-a",
                                response["candidateCount"], diagnostics=response["diagnostics"])
            snapshot = self.engine._snapshot()
            health, = snapshot["health"]
            self.assertEqual(health["state"], "unavailable")
            self.assertEqual(health.get("errorCode"), "V3 input report 4D/64 length unproven")
            self.assertEqual(snapshot["errors"], [])
            self.assertNotIn("PRIVATE", json.dumps(snapshot))

    def test_recovered_device_clears_previous_failure_diagnostics(self):
        for pid in (0x1014, 0x1018):
            infos = inventory(pid)
            provider = M.MchoseProvider()
            with self.subTest(pid=pid), fixture([], infos, fail="read"):
                statuses = server.scoped_poll(provider, infos, "receiver-a")
            failed = self.accept(provider, infos, statuses)
            self.assertEqual(failed["health"][0].get("errorCode"), "V3 power I/O failed")
            with fixture([CHARGING], infos) as bus:
                statuses = server.scoped_poll(provider, infos, "receiver-a")
                self.assertEqual(bus.sent, [REQUEST])
            recovered = self.accept(provider, infos, statuses)
            health, = recovered["health"]
            self.assertEqual(health["state"], "ok")
            self.assertNotIn("errorCode", health)
            self.assertNotIn("diagnosticSummary", self.engine.health[self.reader])
            self.assertNotIn("V3 power I/O failed", " ".join(health["diagnostics"]))
            self.assertIn("V3 valid power reply", health["diagnostics"])
            self.assertEqual(recovered["errors"], [])
            current, = [d for d in recovered["devices"] if d["fresh"]]
            self.assertEqual((current["percent"], current["verificationLevel"]), (85, "C"))
            self.assertIsNone(current["dpi"])
            with fixture([], []):
                self.assertEqual(server.scoped_poll(provider, [], "receiver-a"), [])
            self.assertEqual(server.reader_diagnostics(provider), [])


if __name__ == "__main__":
    unittest.main()
