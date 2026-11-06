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
import test_atk_nano as N
from catalog import verification_for
from isolation import wire_encode
from known_ids import build_index
import server


class AtkNanoBridgeTests(unittest.TestCase):
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
