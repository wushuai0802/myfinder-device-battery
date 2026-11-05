"""VGN BY registration, worker boundary and passive descriptor discovery fixtures."""
import ctypes
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
import test_vgn_beiying as V
import test_topology_cache as T
from catalog import verification_for
from device_pack import BRAND_PROVIDERS
from isolation import wire_encode
from known_ids import build_index
import server
from standardhid import Caps

P = V.P


class VgnBeiyingBridgeTests(unittest.TestCase):
    def test_registration_and_offline_index_use_exact_pairs(self):
        self.assertIn((P.name, "VgnBeiyingProvider"), server.PROVIDERS)
        self.assertEqual(server.PROVIDER_VIDS[P.name], (0x258A, 0x391D))
        self.assertIn(P.name, BRAND_PROVIDERS)
        self.assertEqual(verification_for(P.name)[0], "C")
        pairs = {(0x258A, 0x024F), (0x258A, 0x027F), (0x258A, 0x027B),
                 (0x391D, 0x2A01), (0x391D, 0x2A02)}
        for index in (build_index(), build_index(engine_root=ENGINE)):
            self.assertNotIn(P.name, index["unparsed"])
            self.assertNotIn(P.name, index["partial"])
            self.assertEqual({(r["vendorId"], r["productId"]) for r in index["devices"]
                              if r["provider"] == P.name}, pairs)
            self.assertEqual(index["vendors"][P.name], [0x258A, 0x391D])
        for vid in (0x258A, 0x3554, 0x391D, 0x41E4):
            for pid in (0x024F, 0x027B, 0x027F, 0xFA09, 0x2A01, 0x2A02, 0x024E):
                self.assertEqual(server.recognized_candidate_count(P.name, [
                    V.entry(vendor_id=vid, product_id=pid)]), int((vid, pid) in pairs))

    def test_worker_preserves_sizes_and_scopes_one_physical_receiver(self):
        for vid, pid in V.V.PIDS:
            infos = [V.entry(physical, vendor_id=vid, product_id=pid) for physical in ("one", "two")]
            request = dict(action="query", sequence=1, provider=P.name,
                           physicalId="one", inventory=infos)
            data = (json.dumps(request, default=wire_encode) + "\n").encode()
            with self.subTest(vid=vid, pid=pid), V.fixture([V.reply()], infos) as bus, \
                    patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                    patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
                server.reader_child()
            response = json.loads(output.getvalue())
            self.assertFalse(response.get("errorCode"))
            self.assertEqual(response["candidateCount"], 1)
            self.assertEqual(len(response["statuses"]), 1)
            self.assertEqual(bus.writes, [(infos[0]["path"], V.V.make_request())])
            self.assertEqual(bus.opened, bus.closed)

    def test_silence_does_not_refresh_history_and_sibling_charging_is_unknown(self):
        for vid, pid in V.V.PIDS:
            engine = server.BatteryEngine([P()], include_native=True)
            self.addCleanup(engine.close)
            info = V.entry(vendor_id=vid, product_id=pid)
            with self.subTest(vid=vid, pid=pid), V.fixture([V.reply()], [info]):
                first, = engine.poll()["devices"]
            self.assertEqual((first["percent"], first["kind"], first["verificationLevel"]),
                             (99, "keyboard", "C"))
            self.assertEqual(first["charging"], True if (vid, pid) == (0x258A, 0x024F) else None)
            for frames in ([], [V.V.make_request()], [V.reply(255)]):
                with V.fixture(frames, [info]):
                    snapshot = engine.poll()
                stale, = snapshot["devices"]
                self.assertEqual(stale["percent"], 99)
                self.assertEqual(stale["confirmedAtUtc"], first["confirmedAtUtc"])
                self.assertFalse(stale["fresh"])
                self.assertEqual(stale["connectionState"], "unconfirmed")
                self.assertIsNone(stale["charging"])
                self.assertEqual(snapshot["errors"], [])
                self.assertFalse(engine.failures)

    def descriptor_fixture(self, identity=(0x258A, 0x024F), collection=(0xFF02, 2), fixed=True):
        case = T.DescriptorCacheTests()
        case.setUp()
        self.addCleanup(case.doCleanups)
        case.identities[T.PATH_A] = identity

        def caps(data, pointer):
            value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
            value.page, value.usage = collection
            value.input = value.output = 20
            if fixed:
                value.ib = value.ob = 1
            else:
                value.iv = value.ov = 1
            return 0x00110000

        def fields(report_type, values, length, data):
            self.assertIn(report_type, (0, 1))
            if fixed:
                for key, value in V.FIXED_ARRAY.items():
                    setattr(values[0], key, value)
            else:
                values[0].report, values[0].bits, values[0].count = 19, 8, 19
            return 0x00110000

        case.hid.HidP_GetCaps.side_effect = caps
        case.hid.HidP_GetButtonCaps.side_effect = fields
        case.hid.HidP_GetValueCaps.side_effect = fields
        return case

    def test_all_five_descriptors_are_proven_passively_and_cached(self):
        for identity in V.V.PIDS:
            for fixed in (True, False):
                with self.subTest(identity=identity, fixed=fixed):
                    case = self.descriptor_fixture(identity, fixed=fixed)
                    first, = case.scan([T.PATH_A])
                    for direction in ("input", "output"):
                        self.assertEqual(first[direction + "_report_sizes"], {"19": 20})
                    case.kernel.CreateFileW.reset_mock()
                    self.assertEqual(case.scan([T.PATH_A]), [first])
                    case.kernel.CreateFileW.assert_not_called()
                    case.hid.HidD_GetFeature.assert_not_called()
                    case.hid.HidD_SetFeature.assert_not_called()
                    case.kernel.ReadFile.assert_not_called()
                    case.kernel.WriteFile.assert_not_called()
                    case.doCleanups()

    def test_failed_proof_is_not_cached_or_sent_and_can_recover(self):
        case = self.descriptor_fixture()
        good = case.hid.HidP_GetButtonCaps.side_effect
        case.hid.HidP_GetButtonCaps.side_effect = lambda *args: 0
        info, = case.scan([T.PATH_A])
        self.assertNotIn(T.PATH_A, case.cache)
        self.assertIsNone(info["input_report_sizes"])
        with V.fixture([V.reply()], [info]) as bus:
            self.assertEqual(P().poll(), [])
            self.assertEqual((bus.opened, bus.writes), ([], []))
        case.hid.HidP_GetButtonCaps.side_effect = good
        info, = case.scan([T.PATH_A])
        with V.fixture([V.reply()], [info]) as bus:
            self.assertEqual(len(P().poll()), 1)
            self.assertEqual(len(bus.writes), 1)

    def test_fixed_usage_does_not_expand_to_wired_unknown_or_mchose_endpoint(self):
        for identity, collection in (((0x258A, 0x024E), (0xFF00, 1)),
                                     ((0x258A, 0x027E), (0xFF00, 1)),
                                     ((0x258A, 0x026A), (0xFF00, 1)),
                                     ((0x258A, 0x010C), (0xFF00, 1)),
                                     ((0x391D, 0x2003), (0xFF00, 1)),
                                     ((0x258A, 0x024F), (0xFF04, 2)),
                                     ((0x258A, 0x024F), (0xFF02, 1)),
                                     ((0x3554, 0x024F), (0xFF02, 2)),
                                     ((0x3554, 0xFA09), (0xFF02, 2)),
                                     ((0x3554, 0xFA09), (0xFF04, 2)),
                                     ((0x41E4, 0x2001), (0xFF04, 2))):
            with self.subTest(identity=identity, collection=collection):
                case = self.descriptor_fixture(identity, collection)
                info, = case.scan([T.PATH_A])
                self.assertIsNone(info.get("input_report_sizes"))
                self.assertIsNone(info.get("output_report_sizes"))
                with V.fixture([V.reply()], [info]) as bus:
                    self.assertEqual(P().poll(), [])
                    self.assertEqual((bus.opened, bus.writes), ([], []))
                case.doCleanups()


if __name__ == "__main__":
    unittest.main()
