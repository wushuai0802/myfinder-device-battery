"""ATK N9 V2 registry and public bridge tests using synthetic HID only."""
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
import test_atk_headset as A
from catalog import verification_for
from device_pack import BRAND_PROVIDERS
from isolation import wire_encode
from known_ids import build_index
import server


class AtkHeadsetBridgeTests(unittest.TestCase):
    private_text = (r"C:\private\receiver-path", "secret-serial-175", "private-product-175",
                    "private-path", "PRIVATE-RAW-FRAME")

    def assert_safe_health(self, engine, snapshot, summary):
        health, = snapshot["health"]
        self.assertEqual(health.get("errorCode"), summary)
        self.assertLessEqual(len(summary), 48)
        self.assertTrue(health["diagnostics"])
        self.assertNotIn("diagnosticSummary", health)
        self.assertEqual(snapshot["errors"], [])
        self.assertFalse(engine.failures)
        self.assertFalse(engine.retry_after)
        self.assertTrue(all("errorCode" not in row for row in engine.health.values()))
        public = json.dumps(snapshot)
        for private in self.private_text:
            self.assertNotIn(private, public)
            self.assertNotIn(json.dumps(private)[1:-1], public)
        return health

    def test_issue175_unproven_reports_publish_safe_reason_without_io(self):
        info = A.entry(product_id=0x12D6, path=self.private_text[0].encode(),
                       serial_number=self.private_text[1], product_string=self.private_text[2],
                       input_report_sizes=None, input_report_proof="failed",
                       report_descriptor_error=self.private_text[0],
                       declared_report_sizes={"input": {"85": 64, self.private_text[1]: 64},
                                              "output": {"85": 64}, "feature": {}})
        inventory = [A.entry(product_id=0x12D6, usage_page=page,
                             path=("other-%x" % page).encode())
                     for page in (0x000B, 0x000C, 0xFF82)] + [info]
        engine = server.BatteryEngine([A.P()], include_native=True)
        self.addCleanup(engine.close)
        with A.fixture([A.reply()], inventory) as bus:
            snapshot = engine.poll()
        self.assertEqual((bus.opened, bus.writes, bus.reads), ([], [], []))
        self.assertEqual(snapshot["devices"], [])
        health = self.assert_safe_health(engine, snapshot, "report sizes unproven")
        diagnostic, = health["diagnostics"]
        self.assertIn("report IDs/lengths unknown or incompatible: nothing sent", diagnostic)
        self.assertIn("proof=caps+descriptor failed; descriptor=unknown", diagnostic)
        self.assertIn("declared=out:55/64,in:55/64,in:unknown,feature:none", diagnostic)
        self.assertEqual(json.loads(json.dumps(snapshot))["health"][0]["diagnostics"], [diagnostic])

    def test_rejected_interfaces_and_enumeration_publish_reasons_without_io(self):
        cases = (([A.entry(usage_page=0xFF82)], "collection missing or ambiguous", None),
                 ([A.entry(), A.entry(path=b"second-ff90")], "collection missing or ambiguous", None),
                 ([A.entry(physical_id="collection:unresolved")], "physical parent unresolved", None),
                 ([A.entry()], "enumeration failed", OSError(self.private_text[0])))
        for inventory, summary, error in cases:
            engine = server.BatteryEngine([A.P()], include_native=True)
            self.addCleanup(engine.close)
            with self.subTest(summary=summary, count=len(inventory)), A.fixture([], inventory) as bus:
                if error:
                    with patch.object(A.H.hidlist, "enumerate", side_effect=error):
                        snapshot = engine.poll()
                else:
                    snapshot = engine.poll()
            self.assertEqual((bus.opened, bus.writes, bus.reads), ([], [], []))
            self.assert_safe_health(engine, snapshot, summary)

    def test_failed_queries_publish_safe_reasons_with_unchanged_io(self):
        raw = list(b"PRIVATE-RAW-FRAME") + [0] * 47
        cases = (({}, [], "no valid battery reply", 1, 1),
                 ({}, [A.reply(0, 0x1A)], "device asleep/offline", 1, 1),
                 ({}, [A.disconnected()], "device asleep/offline", 1, 1),
                 ({}, [A.reply(length=22)], "input report length rejected", 1, 2),
                 ({}, [raw], "battery reply rejected", 1, 2),
                 ({"write_result": 63}, [A.reply()], "battery query write short/failed", 1, 0),
                 ({"error_at": "open"}, [], "battery I/O failed", 0, 0),
                 ({"error_at": "write"}, [], "battery I/O failed", 1, 0),
                 ({"error_at": "read"}, [], "battery I/O failed", 1, 1))
        for options, frames, summary, writes, reads in cases:
            engine = server.BatteryEngine([A.P()], include_native=True)
            self.addCleanup(engine.close)
            with self.subTest(summary=summary, options=options), A.fixture(frames, **options) as bus:
                snapshot = engine.poll()
            self.assertEqual(snapshot["devices"], [])
            self.assert_safe_health(engine, snapshot, summary)
            self.assertEqual(bus.opened, [A.entry()["path"]])
            self.assertEqual(bus.opened, bus.closed)
            self.assertEqual(bus.writes, [(A.entry()["path"], A.REQUEST)] * writes)
            self.assertEqual(len(bus.reads), reads)
            self.assertTrue(all(length == 64 and 0 < timeout <= 1000 for length, timeout in bus.reads))
            self.assertLessEqual(bus.now, 1.001)
            self.assertNotIn(str(raw), json.dumps(snapshot))

    def test_unknown_diagnostic_text_never_becomes_compact_reason(self):
        engine = server.BatteryEngine([A.P()], include_native=True)
        self.addCleanup(engine.close)
        # Unknown lines, including ones resembling known text, are not feedback.
        engine._accept("atk_headset|receiver-a", "atk_headset", [], server.utcnow(),
                       physical="receiver-a", candidate_count=1,
                       diagnostics=[self.private_text[0], "rejected battery reply " + self.private_text[1]])
        health, = engine._snapshot(0, 0)["health"]
        self.assertFalse(health.get("errorCode"))
        self.assertNotIn("errorCode", engine.health["atk_headset|receiver-a"])

    def test_registration_kind_and_grade_c(self):
        self.assertIn(("atk_headset", "AtkHeadsetProvider"), server.PROVIDERS)
        self.assertEqual(server.PROVIDER_VIDS["atk_headset"], (0x373B,))
        self.assertIn("atk_headset", BRAND_PROVIDERS)
        self.assertEqual(verification_for("atk_headset")[0], "C")
        self.assertNotIn("atk_headset", server.DPI_PROVIDERS)

    def test_offline_index_has_only_the_two_enabled_v2_receivers(self):
        expected = [(0x373B, 0x1262, "ATK N9 V2 Ultimate", "headset"),
                    (0x373B, 0x12D6, "ATK N9 V2 ULTRA", "headset")]
        for engine_root in (None, ENGINE):
            with self.subTest(engine_root=engine_root):
                index = build_index(engine_root=engine_root)
                rows = [row for row in index["devices"] if row["provider"] == "atk_headset"]
                self.assertEqual([(row["vendorId"], row["productId"], row["name"], row["kind"])
                                  for row in rows], expected)
                self.assertEqual(index["vendors"]["atk_headset"], [0x373B])
                self.assertNotIn("atk_headset", index["unparsed"])
                self.assertNotIn("atk_headset", index["partial"])

    def test_candidate_count_does_not_claim_initial_n9_or_rs7(self):
        unsupported = [A.entry(product_id=pid) for pid in (0x1204, 0x1258, 0x12D4, 0x1278)]
        self.assertEqual(server.recognized_candidate_count("atk_headset", unsupported), 0)
        supported = [A.entry(), A.entry("ultra", product_id=0x12D6)]
        self.assertEqual(server.recognized_candidate_count("atk_headset", unsupported + supported), 2)

    def test_scoped_poll_keeps_two_receivers_separate_and_hides_private_identity(self):
        inventory = [A.entry("one"), A.entry("two", product_id=0x12D6)]
        statuses = []
        with A.fixture([A.reply(20), A.reply(90, 3)], inventory) as bus:
            for physical in ("one", "two"):
                statuses.extend(server.scoped_poll(A.P(), inventory, physical))
        self.assertEqual(bus.opened, [entry["path"] for entry in inventory])
        values = [server.normalise(status, None, "2026-11-06T00:00:00Z") for status in statuses]
        self.assertEqual([value["percent"] for value in values], [20, 90])
        self.assertEqual([value["charging"] for value in values], [False, True])
        self.assertEqual(len({value["physicalId"] for value in values}), 2)
        self.assertTrue(all(value["physicalId"] not in ("one", "two") for value in values))
        self.assertTrue(all(value["kind"] == "headset" and value["fresh"]
                            and value["verificationLevel"] == "C" for value in values))

    def test_silent_or_off_receiver_has_no_new_measurement_or_engine_error(self):
        engine = server.BatteryEngine([A.P()], include_native=True)
        self.addCleanup(engine.close)
        for frames in ([], [A.reply(0, 0x1A)], [A.disconnected()]):
            with self.subTest(initial_frames=frames), A.fixture(frames):
                snapshot = engine.poll()
            self.assertEqual(snapshot["devices"], [])
            self.assertEqual(snapshot["errors"], [])
        with A.fixture([A.reply(73)]):
            first, = engine.poll()["devices"]
        for frames in ([], [A.reply(0, 0x1A)], [A.disconnected()]):
            with self.subTest(history_frames=frames), A.fixture(frames):
                snapshot = engine.poll()
            stale, = snapshot["devices"]
            self.assertEqual(stale["percent"], 73)
            self.assertEqual(stale["confirmedAtUtc"], first["confirmedAtUtc"])
            self.assertEqual(stale["connectionState"], "unconfirmed")
            self.assertFalse(stale["fresh"])
            self.assertIsNone(stale["charging"])
            self.assertEqual(snapshot["errors"], [])
            self.assertFalse(engine.failures)
            self.assertFalse(engine.retry_after)
            self.assertTrue(snapshot["health"][0].get("errorCode"))
        # A rejected packet earlier in a successful poll is not a failure summary.
        with A.fixture([A.reply(command=0xAA), A.reply(72, 3)]) as bus:
            snapshot = engine.poll()
            recovered, = snapshot["devices"]
        self.assertEqual(recovered["percent"], 72)
        self.assertTrue(recovered["fresh"])
        self.assertTrue(recovered["charging"])
        self.assertFalse(snapshot["health"][0].get("errorCode"))
        self.assertEqual(snapshot["health"][0]["diagnostics"], ["rejected battery reply"])
        self.assertEqual(snapshot["errors"], [])
        self.assertFalse(engine.failures)
        self.assertFalse(engine.retry_after)
        self.assertEqual(bus.writes, [(A.entry()["path"], A.REQUEST)])
        self.assertEqual(len(bus.reads), 2)

    def test_reader_child_preserves_per_report_proof_across_json(self):
        for proven in (True, False):
            info = A.entry(product_id=0x12D6, input_report_length=22,
                           input_report_sizes={"85": 22} if proven else None,
                           input_report_proof="descriptor" if proven else "failed",
                           report_descriptor_error=self.private_text[0])
            request = dict(action="query", sequence=17, provider="atk_headset",
                           physicalId=info["physical_id"], inventory=[info])
            data = (json.dumps(request, default=wire_encode) + "\n").encode()
            with self.subTest(proven=proven), A.fixture([A.reply(length=22)], [info]) as bus, \
                    patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                    patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
                server.reader_child()
            response = json.loads(output.getvalue())
            self.assertEqual(response["sequence"], 17)
            self.assertEqual(response["candidateCount"], 1)
            self.assertFalse(response.get("errorCode"))
            self.assertEqual([status["level"] for status in response["statuses"]], [73] if proven else [])
            self.assertEqual(bus.opened, [info["path"]] if proven else [])
            self.assertEqual(bus.writes, [(info["path"], A.REQUEST)] if proven else [])
            diagnostics = response.get("diagnostics")
            self.assertIsInstance(diagnostics, list)
            self.assertEqual(diagnostics, [] if proven else [
                "report IDs/lengths unknown or incompatible: nothing sent; "
                "proof=caps+descriptor failed; descriptor=unknown; "
                "declared=out:55/64,in:unknown,feature:unknown"])
            engine = server.BatteryEngine([A.P()], include_native=True)
            self.addCleanup(engine.close)
            # Consume the actual child JSON at the same acceptance boundary as
            # the isolated scheduler; failed reads have no statuses to decode.
            if not proven:
                engine._accept("atk_headset|" + info["physical_id"], "atk_headset", [],
                               response["checkedAtUtc"], physical=info["physical_id"],
                               candidate_count=response["candidateCount"], diagnostics=diagnostics)
                snapshot = engine._snapshot(0, 0)
                health = self.assert_safe_health(engine, snapshot, "report sizes unproven")
                self.assertEqual((health["state"], health["reason"]), ("unavailable", "no_measurement"))
                self.assertEqual(health["diagnostics"], diagnostics)


if __name__ == "__main__":
    unittest.main()
