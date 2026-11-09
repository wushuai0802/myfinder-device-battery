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
from test_mchose_v3 import (M, BUSY, CHARGING, REQUEST, alternate_inventory,
                           fixture, inventory as v3_inventory)
from isolation import wire_encode
import server


def inventory(pid=0x1018):
    return [dict(d, physical_id="receiver-a") for d in v3_inventory(pid)]


class MchoseV3HealthTests(unittest.TestCase):
    def setUp(self):
        self.engine = server.BatteryEngine([], include_native=True)
        self.addCleanup(self.engine.close)
        self.reader = "mchose|receiver-a"

    def accept(self, provider, infos, statuses=(), candidate_count=None, physical="receiver-a"):
        if candidate_count is None:
            candidate_count = server.recognized_candidate_count("mchose", infos)
        self.engine._accept(self.reader, "mchose", statuses, server.utcnow(),
                            physical=physical, candidate_count=candidate_count,
                            diagnostics=server.reader_diagnostics(provider))
        return self.engine._snapshot()

    def test_primary_and_alternate_refusals_are_distinct_safe_and_compact(self):
        for target in (0, 1):
            infos = alternate_inventory("PRIVATE-PARENT")
            parent = infos[0]["physical_id"]
            self.reader = "mchose|" + parent
            infos[target].update(input_report_length=64, output_report_length=64,
                                 input_report_sizes=None, output_report_sizes=None)
            provider = M.MchoseProvider()
            with self.subTest(target=target), fixture([], infos) as bus:
                statuses = server.scoped_poll(provider, infos, parent)
                snapshot = self.accept(provider, infos, statuses, physical=parent)
                health, = snapshot["health"]
                diagnostic = " ".join(health["diagnostics"])
                self.assertIn("collection=" + ("FF01:0001" if target == 0 else "FF0B:00C4"), diagnostic)
                self.assertIn("4D unproven", health["errorCode"])
                self.assertLessEqual(len(health["errorCode"]), 48)
                self.assertEqual((health["state"], snapshot["errors"]), ("unavailable", []))
                self.assertNotIn("PRIVATE", json.dumps(snapshot))
                self.assertEqual((bus.opened, bus.sent, bus.reads), ([], [], []))

    def test_alternate_failure_keeps_history_time_and_recovery_clears_summary(self):
        infos = alternate_inventory()
        parent = infos[0]["physical_id"]
        self.reader = "mchose|" + parent
        provider = M.MchoseProvider()
        with fixture([CHARGING], infos):
            statuses = server.scoped_poll(provider, infos, parent)
        first = self.accept(provider, infos, statuses, physical=parent)
        measured_at = first["devices"][0]["measuredAtUtc"]
        previous = dict(provider._last)
        with fixture([], infos, fail="read") as bus:
            statuses = server.scoped_poll(provider, infos, parent)
            self.assertEqual(bus.sent, [REQUEST])
        failed = self.accept(provider, infos, statuses, physical=parent)
        stale, = failed["devices"]
        self.assertEqual((stale["percent"], stale["fresh"], stale["measuredAtUtc"]),
                         (85, False, measured_at))
        self.assertEqual(provider._last, previous)
        self.assertIn("V3 power I/O failed", " ".join(failed["health"][0]["diagnostics"]))
        self.assertEqual(failed["errors"], [])
        # A gate refusal removes the provider cache, while the engine retains
        # historical power without changing its measurement time.
        infos[1]["input_report_sizes"] = None
        with fixture([], infos):
            statuses = server.scoped_poll(provider, infos, parent)
        refused = self.accept(provider, infos, statuses, physical=parent)
        self.assertIn("4D unproven", refused["health"][0]["errorCode"])
        self.assertEqual(refused["devices"][0]["measuredAtUtc"], measured_at)
        self.assertFalse(refused["devices"][0]["fresh"])
        infos[1]["input_report_sizes"] = {"77": 64}
        with fixture([CHARGING], infos):
            statuses = server.scoped_poll(provider, infos, parent)
        recovered = self.accept(provider, infos, statuses, physical=parent)
        self.assertTrue(recovered["devices"][0]["fresh"])
        self.assertEqual(recovered["devices"][0]["verificationLevel"], "C")
        self.assertNotIn("errorCode", recovered["health"][0])
        self.assertNotIn("diagnosticSummary", self.engine.health[self.reader])
        self.assertNotIn("V3 power I/O failed", " ".join(recovered["health"][0]["diagnostics"]))
        self.assertEqual((self.engine.failures, self.engine.retry_after), ({}, {}))

    def test_alternate_diagnostic_never_outranks_real_timeout(self):
        infos = alternate_inventory()
        parent = infos[0]["physical_id"]
        self.reader = "mchose|" + parent
        infos[1]["input_report_sizes"] = None
        provider = M.MchoseProvider()
        with fixture([], infos):
            statuses = server.scoped_poll(provider, infos, parent)
        self.accept(provider, infos, statuses, physical=parent)
        summary = self.engine.health[self.reader]["diagnosticSummary"]
        self.engine._failure(self.reader, "mchose", "timeout")
        self.engine.health[self.reader]["diagnosticSummary"] = summary
        snapshot = self.engine._snapshot()
        health, = snapshot["health"]
        self.assertEqual((health["state"], health["errorCode"]), ("timeout", "timeout"))
        self.assertEqual(snapshot["errors"], ["mchose: timeout"])

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

    def test_v3_length_unproven_with_declared_sizes_projects_compact_declared_summary(self):
        infos = inventory(0x1018)
        info = next(d for d in infos if d["usage_page"] == 0xFF01)
        info.update(input_report_sizes=None, input_report_proof="failed",
                    report_descriptor_error="collection maximum mismatch",
                    declared_report_sizes={"output": {"77": 64}, "input": {"1": 16},
                                           "feature": {}})
        provider = M.MchoseProvider()
        with fixture([], infos):
            statuses = server.scoped_poll(provider, infos, "receiver-a")
        snapshot = self.accept(provider, infos, statuses)
        health, = snapshot["health"]
        self.assertEqual(
            health["errorCode"],
            "in:4D unproven;declared=out:4D/64,in:01/16,...")
        self.assertLessEqual(len(health["errorCode"]), 48)
        self.assertIn("declared=out:4D/64,in:01/16,feature:none",
                      " ".join(health["diagnostics"]))


if __name__ == "__main__":
    unittest.main()
