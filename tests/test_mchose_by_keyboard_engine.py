"""BY scoped-worker and history tests using synthetic replies, never hardware."""
import io
import json
from datetime import datetime, timedelta, timezone
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ENGINE = Path(__file__).resolve().parents[1] / "engine"
sys.path.insert(0, str(ENGINE))
sys.path.insert(0, str(ENGINE / "tests"))
from test_mchose_by_keyboard import P, entry, fixture, identity, reply, notification
from test_aula_fa09 import aula_entry, aula_fixture, aula_reply
from providers.vgn_beiying import VgnBeiyingProvider
from providers.base import DeviceStatus
from catalog import verification_for
from device_pack import BRAND_PROVIDERS
from isolation import wire_encode
from known_ids import build_index
import server


class MchoseByKeyboardBridgeTests(unittest.TestCase):
    receiver = {}
    unknown_battery = True

    def entry(self, physical="receiver-a", **changes):
        return entry(physical, **(self.receiver | changes))

    def fixture(self, frames=(), inventory=None, **kwargs):
        return fixture(frames, [self.entry()] if inventory is None else inventory, **kwargs)

    def test_registration_level_and_offline_index_remain_narrow(self):
        self.assertIn((P.name, "MchoseByKeyboardProvider"), server.PROVIDERS)
        self.assertEqual(server.PROVIDER_VIDS[P.name], (0x3554, 0x41E4))
        self.assertIn(P.name, BRAND_PROVIDERS)
        self.assertEqual(verification_for(P.name)[0], "C")
        # Also exercise offline snapshot parsing: no reader import is needed.
        for index in (build_index(), build_index(engine_root=ENGINE)):
            self.assertNotIn(P.name, index["unparsed"])
            self.assertNotIn(P.name, index["partial"])
            rows = [row for row in index["devices"] if row["provider"] == P.name]
            self.assertEqual({(row["vendorId"], row["productId"], row["kind"]) for row in rows},
                             {(0x3554, 0xFA09, "keyboard"), (0x41E4, 0x2001, "keyboard")})
            self.assertEqual(index["vendors"][P.name], [0x3554, 0x41E4])
            self.assertEqual([row["provider"] for row in index["devices"]
                              if (row["vendorId"], row["productId"]) == (0x3554, 0xFA09)], [P.name])
        rejected = [entry(vendor_id=0x3554, product_id=0x2001),
                    entry(vendor_id=0x41E4, product_id=0xFA09),
                    entry(vendor_id=0x41E4, product_id=0x2201)]
        self.assertEqual(server.recognized_candidate_count(P.name, rejected), 0)
        self.assertEqual(server.recognized_candidate_count(P.name, rejected + [self.entry()]), 1)

    def test_silence_or_identity_instead_of_battery_never_refreshes_a_reading(self):
        engine = server.BatteryEngine([P()], include_native=True)
        self.addCleanup(engine.close)
        with self.fixture():
            empty = engine.poll()
        self.assertEqual((empty["devices"], empty["errors"]), ([], []))
        with self.fixture([identity(), reply(63, 0x10)]):
            first, = engine.poll()["devices"]
        self.assertEqual((first["percent"], first["kind"], first["verificationLevel"]),
                         (63, "keyboard", "C"))
        for frames in ([], [identity()], [identity("000000000000")], [reply(0)]):
            with self.subTest(frames=frames), self.fixture(frames):
                snapshot = engine.poll()
            stale, = snapshot["devices"]
            self.assertEqual(stale["percent"], 63)
            self.assertEqual(stale["confirmedAtUtc"], first["confirmedAtUtc"])
            self.assertFalse(stale["fresh"])
            self.assertEqual(stale["connectionState"], "unconfirmed")
            self.assertIsNone(stale["charging"])
            self.assertEqual(snapshot["errors"], [])
            self.assertFalse(engine.failures)

    def test_child_preserves_descriptor_proof_and_scopes_identification(self):
        infos = [self.entry("one"), self.entry("two")]
        request = dict(action="query", sequence=1, provider=P.name,
                       physicalId="one", inventory=infos)
        data = (json.dumps(request, default=wire_encode) + "\n").encode()
        for frames, name in (([identity(), reply()], "MCHOSE X75 V2"),
                             ([identity("000000000000"), reply()],
                              "2.4G 无线键盘（3554:FA09）" if self.unknown_battery else None),
                             ([identity("000000000000")], None), ([], None)):
            count = int(name is not None)
            with self.subTest(count=count), self.fixture(frames, infos) as bus, \
                    patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                    patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
                server.reader_child()
            response = json.loads(output.getvalue())
            self.assertEqual(len(response["statuses"]), count)
            self.assertEqual(response["candidateCount"], 1)
            self.assertFalse(response.get("errorCode"))
            self.assertEqual(bus.opened, [infos[0]["path"]])
            self.assertEqual(bus.opened, bus.closed)
            if count:
                self.assertEqual(response["statuses"][0]["name"], name)


class Mchose41e4KeyboardBridgeTests(MchoseByKeyboardBridgeTests):
    receiver = dict(vendor_id=0x41E4, product_id=0x2001,
                    product_string="MCHOSE 2.4G Wireless")
    unknown_battery = False


class SharedFa09BridgeTests(unittest.TestCase):
    def test_one_neutral_card_and_zero_reply_preserves_unconfirmed_history(self):
        infos = [entry(path=b"fa09-ff04"), entry(path=b"fa09-ff02", usage_page=0xFF02,
                                              input_report_sizes=None)]
        engine = server.BatteryEngine([VgnBeiyingProvider(), P()], include_native=True)
        self.addCleanup(engine.close)
        with fixture([identity("000000000000"), reply(63, 0xFF)], infos) as bus:
            snapshot = engine.poll()
        first, = snapshot["devices"]
        self.assertEqual((first["name"], first["percent"], first["charging"]),
                         ("2.4G 无线键盘（3554:FA09）", 63, None))
        self.assertEqual(snapshot["errors"], [])
        self.assertEqual(bus.opened, [infos[0]["path"]])
        self.assertEqual(bus.opened, bus.closed)
        for frames in ([], [reply(0)]):
            with self.subTest(frames=frames), fixture(frames, infos) as bus:
                snapshot = engine.poll()
            stale, = snapshot["devices"]
            self.assertEqual((stale["name"], stale["percent"], stale["confirmedAtUtc"]),
                             (first["name"], 63, first["confirmedAtUtc"]))
            self.assertFalse(stale["fresh"])
            self.assertEqual(stale["connectionState"], "unconfirmed")
            self.assertEqual(snapshot["errors"], [])
            self.assertEqual(bus.opened, [infos[0]["path"]])

    def test_by_notification_replaces_unconfirmed_feature_history_on_same_receiver(self):
        engine = server.BatteryEngine([P(), VgnBeiyingProvider()], include_native=True)
        self.addCleanup(engine.close)
        ff04 = aula_entry(path=b"ff04")
        ff02 = entry(path=b"ff02", usage_page=0xFF02)
        old = DeviceStatus("mchose_by_keyboard:receiver-a", "2.4G 无线键盘（3554:FA09）", 100,
                           source=P.name, kind="keyboard", physical_id="receiver-a",
                           charging_known=False, unconfirmed=True)
        stamp = (datetime.now(timezone.utc) - timedelta(minutes=2)).isoformat()
        first = server.normalise(old, None, stamp)
        engine.previous[first["id"]] = server.historical(first)
        self.assertTrue(first["unconfirmed"])
        with aula_fixture([aula_reply()], [ff04]) as bus:
            stale, = engine.poll()["devices"]
        self.assertEqual(stale["confirmedAtUtc"], first["confirmedAtUtc"])
        self.assertFalse(stale["fresh"])
        self.assertTrue(stale["unconfirmed"])
        self.assertEqual((bus.opened, bus.feature_writes, bus.feature_reads), ([], [], []))
        with aula_fixture([identity("030000000115"), notification()], [ff04, ff02]) as bus:
            current, = engine.poll()["devices"]
        self.assertEqual((current["id"], current["name"], current["percent"]),
                         (first["id"], "MCHOSE G75 Pro", 95))
        self.assertNotIn("unconfirmed", current)
        self.assertTrue(current["fresh"])
        self.assertNotEqual(current["confirmedAtUtc"], first["confirmedAtUtc"])
        self.assertEqual(bus.opened, [b"ff02"])
        self.assertEqual((bus.feature_writes, bus.feature_reads), ([], []))
        with aula_fixture([notification(0)], [ff04, ff02]):
            stale, = engine.poll()["devices"]
        self.assertEqual(stale["confirmedAtUtc"], current["confirmedAtUtc"])
        self.assertFalse(stale["fresh"])


if __name__ == "__main__":
    unittest.main()
