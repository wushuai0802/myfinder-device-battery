"""VGN registration, descriptor transport and non-error sleep/history behavior."""
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
from test_vgn_keyboard import P, entry, fixture, reply
from catalog import verification_for
from isolation import wire_encode
from known_ids import build_index
import server


class VgnKeyboardBridgeTests(unittest.TestCase):
    def test_registration_historical_level_a_and_known_id_index(self):
        self.assertIn(("vgn_keyboard", "VgnKeyboardProvider"), server.PROVIDERS)
        self.assertEqual(server.PROVIDER_VIDS["vgn_keyboard"], (0x320F,))
        self.assertEqual(verification_for("vgn_keyboard")[0], "A")
        index = build_index()
        self.assertNotIn("vgn_keyboard", index["unparsed"])
        row, = [row for row in index["devices"] if row["provider"] == "vgn_keyboard"]
        self.assertEqual((row["vendorId"], row["productId"], row["kind"]),
                         (0x320F, 0x5088, "keyboard"))

    def test_sleep_and_silence_keep_history_without_errors_then_wake_refreshes(self):
        engine = server.BatteryEngine([P()], include_native=True)
        self.addCleanup(engine.close)
        with fixture([reply()]):
            first, = engine.poll()["devices"]
        self.assertEqual((first["percent"], first["verificationLevel"], first["kind"]),
                         (93, "A", "keyboard"))
        self.assertTrue(first["fresh"])
        self.assertIsNone(first["charging"])
        for frames in ([reply(0, **{"3": 0xFF})], []):
            with self.subTest(frames=frames), fixture(frames):
                snapshot = engine.poll()
            stale, = snapshot["devices"]
            self.assertEqual(stale["percent"], 93)
            self.assertEqual(stale["confirmedAtUtc"], first["confirmedAtUtc"])
            self.assertEqual(stale["connectionState"], "unconfirmed")
            self.assertFalse(stale["fresh"])
            self.assertIsNone(stale["charging"])
            self.assertEqual(snapshot["errors"], [])
            self.assertFalse(engine.failures)
            self.assertFalse(engine.retry_after)
        with fixture([reply(92)]):
            awake, = engine.poll()["devices"]
        self.assertEqual(awake["percent"], 92)
        self.assertTrue(awake["fresh"])

    def test_sleep_before_any_reading_does_not_create_zero_percent_card(self):
        engine = server.BatteryEngine([P()], include_native=True)
        self.addCleanup(engine.close)
        with fixture([reply(0, **{"3": 0xFF})]):
            snapshot = engine.poll()
        self.assertEqual(snapshot["devices"], [])
        self.assertEqual(snapshot["errors"], [])

    def test_scoped_reader_preserves_report_sizes_and_sleep_is_not_an_error(self):
        infos = [entry("private-one"), entry("private-two")]
        request = dict(action="query", sequence=1, provider="vgn_keyboard",
                       physicalId="private-one", inventory=infos)
        data = (json.dumps(request, default=wire_encode) + "\n").encode()
        for frame, expected_count in ((reply(), 1), (reply(0, **{"3": 0xFF}), 0)):
            with self.subTest(count=expected_count), fixture([frame], infos) as bus, \
                    patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                    patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
                server.reader_child()
            response = json.loads(output.getvalue())
            self.assertEqual(len(response["statuses"]), expected_count)
            self.assertEqual(response["candidateCount"], 1)
            self.assertFalse(response.get("errorCode"))
            self.assertEqual(bus.opened, [infos[0]["path"]])
            if expected_count:
                self.assertEqual(response["statuses"][0]["level"], 93)
            else:
                self.assertEqual(response["diagnostics"],
                                 ["device asleep/offline; no new reading; connection unknown"])


if __name__ == "__main__":
    unittest.main()
