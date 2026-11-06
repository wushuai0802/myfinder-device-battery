"""AULA keyboard registration, offline index and passive descriptor discovery."""
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
import test_aula_keyboard as T_A
import test_topology_cache as T
from catalog import verification_for
from device_pack import BRAND_PROVIDERS
from isolation import wire_encode
from known_ids import build_index
from providers import aula_keyboard as A
import server
from standardhid import Caps

P = A.AulaKeyboardProvider


class AulaKeyboardBridgeTests(unittest.TestCase):
    def test_registration_catalog_and_offline_index(self):
        self.assertIn((P.name, "AulaKeyboardProvider"), server.PROVIDERS)
        self.assertEqual(server.PROVIDER_VIDS[P.name], (0x05AC,))
        self.assertIn(P.name, BRAND_PROVIDERS)
        self.assertEqual(verification_for(P.name)[0], "A")
        for index in (build_index(), build_index(engine_root=ENGINE)):
            self.assertNotIn(P.name, index["unparsed"])
            self.assertNotIn(P.name, index["partial"])
            self.assertEqual({(row["vendorId"], row["productId"]) for row in index["devices"]
                              if row["provider"] == P.name}, {(0x05AC, 0x024F)})
            self.assertEqual(index["vendors"][P.name], [0x05AC])
        # recognized_candidate_count is intentionally PID-level (health text only).
        for vid, pid in ((0x05AC, 0x024F), (0x05AC, 0x024E), (0x0C45, 0x800A)):
            self.assertEqual(server.recognized_candidate_count(P.name, [
                T_A.entry(vendor_id=vid, product_id=pid)]), int((vid, pid) == (0x05AC, 0x024F)))

    def test_worker_keeps_sizes_and_scopes_one_receiver(self):
        infos = [T_A.entry("one"), T_A.entry("two")]
        request = dict(action="query", sequence=1, provider=P.name,
                       physicalId="one", inventory=infos)
        data = (json.dumps(request, default=wire_encode) + "\n").encode()
        with T_A.fixture([T_A.reply()], infos) as bus, \
                patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
            server.reader_child()
        response = json.loads(output.getvalue())
        self.assertFalse(response.get("errorCode"))
        self.assertEqual(response["candidateCount"], 1)
        status, = response["statuses"]
        self.assertEqual(status["kind"], "keyboard")
        self.assertEqual(bus.writes, [(infos[0]["path"], T_A.REQUEST)])

    def descriptor_case(self, identity=(0x05AC, 0x024F), caps_page=0xFF60, caps_usage=0x61):
        case = T.DescriptorCacheTests()
        case.setUp()
        self.addCleanup(case.doCleanups)
        case.identities[T.PATH_A] = identity

        def caps(data, pointer):
            value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
            value.page, value.usage = caps_page, caps_usage
            value.input = value.output = A.REPORT_BYTES
            value.iv = value.ov = 1
            return 0x00110000

        def fields(report_type, values, length, data):
            self.assertIn(report_type, (0, 1))
            values[0].report, values[0].bits, values[0].count = 0, 8, A.PAYLOAD_BYTES
            return 0x00110000

        case.hid.HidP_GetCaps.side_effect = caps
        case.hid.HidP_GetValueCaps.side_effect = fields
        return case

    def test_descriptor_proof_is_passive_and_cached(self):
        case = self.descriptor_case()
        first, = case.scan([T.PATH_A])
        for direction in ("input", "output"):
            self.assertEqual(first[direction + "_report_sizes"], {"0": 33})
        case.kernel.CreateFileW.reset_mock()
        self.assertEqual(case.scan([T.PATH_A]), [first])
        case.kernel.CreateFileW.assert_not_called()
        case.hid.HidD_GetFeature.assert_not_called()
        case.hid.HidD_SetFeature.assert_not_called()
        case.kernel.ReadFile.assert_not_called()
        case.kernel.WriteFile.assert_not_called()
        with T_A.fixture([T_A.reply()], [first]) as bus:
            status, = P().poll()
            self.assertEqual(status.level, 33)
            self.assertEqual(len(bus.writes), 1)

    def test_other_identities_and_pages_stay_unproven_and_silent(self):
        for identity, page, usage in (((0x05AC, 0x024F), 0xFF68, 0x61),
                                      ((0x05AC, 0x024E), 0xFF60, 0x61),
                                      ((0x0C45, 0x800A), 0xFF68, 0x61)):
            with self.subTest(identity=identity, page=page):
                case = self.descriptor_case(identity, page, usage)
                info, = case.scan([T.PATH_A])
                self.assertIsNone(info.get("output_report_sizes"))
                with T_A.fixture([T_A.reply()], [info]) as bus:
                    P().poll()
                    self.assertEqual((bus.opened, bus.writes), ([], []))
                case.doCleanups()


if __name__ == "__main__":
    unittest.main()
