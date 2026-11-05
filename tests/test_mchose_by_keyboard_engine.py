"""BY scoped-worker and history tests using synthetic replies, never hardware."""
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
from test_mchose_by_keyboard import P, entry, fixture, identity, reply
from catalog import verification_for
from device_pack import BRAND_PROVIDERS
from isolation import wire_encode
from known_ids import build_index
import server


class MchoseByKeyboardBridgeTests(unittest.TestCase):
    receiver = {}

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
        rejected = [entry(vendor_id=0x3554, product_id=0x2001),
                    entry(vendor_id=0x41E4, product_id=0xFA09),
                    entry(vendor_id=0x41E4, product_id=0x2201)]
        self.assertEqual(server.recognized_candidate_count(P.name, rejected), 0)
        self.assertEqual(server.recognized_candidate_count(P.name, rejected + [self.entry()]), 1)

    def test_silence_or_unknown_model_never_creates_or_refreshes_a_reading(self):
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
        for frames, count in (([identity(), reply()], 1),
                              ([identity("000000000000")], 0), ([], 0)):
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
                self.assertEqual(response["statuses"][0]["name"], "MCHOSE X75 V2")


class Mchose41e4KeyboardBridgeTests(MchoseByKeyboardBridgeTests):
    receiver = dict(vendor_id=0x41E4, product_id=0x2001,
                    product_string="MCHOSE 2.4G Wireless")


if __name__ == "__main__":
    unittest.main()
