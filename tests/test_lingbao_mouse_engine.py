"""Lingbao M1Pro registration, offline index and passive Feature-0F discovery."""
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
import test_lingbao_mouse as T_L
import test_topology_cache as T
from catalog import verification_for
from device_pack import BRAND_PROVIDERS
from isolation import wire_encode
from known_ids import build_index
from providers import lingbao_mouse as L
import server
from standardhid import Caps

P = L.LingbaoMouseProvider


class LingbaoMouseBridgeTests(unittest.TestCase):
    def test_registration_catalog_and_offline_index(self):
        self.assertIn((P.name, "LingbaoMouseProvider"), server.PROVIDERS)
        self.assertEqual(server.PROVIDER_VIDS[P.name], (0x088D,))
        self.assertIn(P.name, BRAND_PROVIDERS)
        self.assertEqual(verification_for(P.name)[0], "A")
        for index in (build_index(), build_index(engine_root=ENGINE)):
            self.assertNotIn(P.name, index["unparsed"])
            self.assertNotIn(P.name, index["partial"])
            self.assertEqual({(row["vendorId"], row["productId"]) for row in index["devices"]
                              if row["provider"] == P.name},
                             {(0x088D, 0x052E), (0x088D, 0x052F)})
            self.assertEqual(index["vendors"][P.name], [0x088D])

    def test_worker_keeps_feature_sizes_and_scopes_one_physical_device(self):
        infos = [T_L.entry(0x052E, "wired-a"), T_L.entry(0x052F, "receiver-b")]
        request = dict(action="query", sequence=1, provider=P.name,
                       physicalId="wired-a", inventory=infos)
        data = (json.dumps(request, default=wire_encode) + "\n").encode()
        with T_L.fixture(inventory=infos, by_path={T_L.WIRED: [T_L.reply(97)]}) as bus, \
                patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
            server.reader_child()
        response = json.loads(output.getvalue())
        self.assertFalse(response.get("errorCode"))
        self.assertEqual(response["candidateCount"], 2)   # both wired and receiver IDs
        status, = response["statuses"]
        self.assertEqual((status["kind"], status["level"]), ("mouse", 97))
        self.assertEqual(bus.writes, [(T_L.WIRED, T_L.REQUEST)])

    def descriptor_case(self, identity=(0x088D, 0x052F), caps_page=0xFF00, caps_usage=0x0001,
                        feature=None):
        feature = L.REPORT_BYTES if feature is None else feature
        case = T.DescriptorCacheTests()
        case.setUp()
        self.addCleanup(case.doCleanups)
        case.identities[T.PATH_A] = identity

        def caps(data, pointer):
            value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
            value.page, value.usage = caps_page, caps_usage
            value.feature = feature
            value.fv = 1
            return 0x00110000

        def fields(report_type, values, length, data):
            self.assertEqual(report_type, 2)   # feature reports only
            values[0].report, values[0].bits, values[0].count = L.REPORT_ID, 8, feature - 1
            return 0x00110000

        case.hid.HidP_GetCaps.side_effect = caps
        case.hid.HidP_GetValueCaps.side_effect = fields
        return case

    def test_feature_descriptor_is_proven_passively_and_cached(self):
        case = self.descriptor_case()
        first, = case.scan([T.PATH_A])
        self.assertEqual(first["feature_report_length"], 33)
        self.assertEqual(first["feature_report_sizes"], {"15": 33})
        case.kernel.CreateFileW.reset_mock()
        self.assertEqual(case.scan([T.PATH_A]), [first])
        case.kernel.CreateFileW.assert_not_called()
        case.hid.HidD_GetFeature.assert_not_called()
        case.hid.HidD_SetFeature.assert_not_called()
        case.kernel.ReadFile.assert_not_called()
        case.kernel.WriteFile.assert_not_called()
        with T_L.fixture(inventory=[first], by_path={first["path"]: [T_L.reply()]}) as bus:
            status, = P().poll()
            self.assertEqual(status.level, 100)
            self.assertEqual(len(bus.writes), 1)

    def test_other_pages_and_unproven_lengths_stay_silent(self):
        for identity, page, usage, feature in (((0x088D, 0x052F), 0x8C, 0x06, 33),
                                               ((0x088D, 0x052E), 0xFF00, 0x0001, 32),
                                               ((0x088D, 0x0520), 0xFF00, 0x0001, 33)):
            with self.subTest(identity=identity, page=page, feature=feature):
                case = self.descriptor_case(identity, page, usage, feature)
                info, = case.scan([T.PATH_A])
                with T_L.fixture([T_L.reply()], [info]) as bus:
                    P().poll()
                    self.assertEqual((bus.opened, bus.writes), ([], []))
                case.doCleanups()


if __name__ == "__main__":
    unittest.main()
