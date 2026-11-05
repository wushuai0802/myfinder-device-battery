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
        with A.fixture([A.reply(72, 3)]):
            recovered, = engine.poll()["devices"]
        self.assertEqual(recovered["percent"], 72)
        self.assertTrue(recovered["fresh"])
        self.assertTrue(recovered["charging"])

    def test_reader_child_preserves_per_report_proof_across_json(self):
        for proven in (True, False):
            info = A.entry(input_report_length=22, input_report_sizes={"85": 22} if proven else None)
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


if __name__ == "__main__":
    unittest.main()
