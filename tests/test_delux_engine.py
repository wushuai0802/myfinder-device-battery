"""DELUX M900 Ultra offline registration and descriptor bridge regressions."""
import ctypes
import json
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
ENGINE = ROOT / "engine"
sys.path.insert(0, str(ENGINE))
sys.path.insert(0, str(ROOT / "tests"))

import test_topology_cache as T
from catalog import validate_catalog, verification_for
from device_pack import BRAND_PROVIDERS, parse_pack
from known_ids import build_index
from standardhid import Caps
import server

VID, PID = 0x3554, 0xFB26
COLLECTION = (0xFF02, 0x0002)
REPORT = {"8": 17}


class DeluxBridgeTests(unittest.TestCase):
    def test_registration_catalog_and_offline_index_are_exact(self):
        self.assertIn(("delux", "DeluxProvider"), server.PROVIDERS)
        self.assertEqual(server.PROVIDER_VIDS["delux"], (VID,))
        self.assertIn("delux", BRAND_PROVIDERS)
        disabled = parse_pack(json.dumps({
            "format": "myfinder-device-pack", "loader": 1, "packVersion": 1,
            "keyId": "fixture", "createdAt": "2026-11-04T10:00:00Z",
            "entries": [], "disabled": [{"provider": "delux", "vendorId": "3554",
                                          "productId": "FB26", "reason": "fixture"}],
        }))
        self.assertTrue(disabled.loaded, disabled.rejected)
        self.assertEqual(len(disabled.disabled), 1)
        self.assertEqual(verification_for("delux")[0], "A")

        index = build_index()
        self.assertNotIn("delux", index["unparsed"])
        self.assertNotIn("delux", index["partial"])
        self.assertEqual(index["vendors"]["delux"], [VID])
        row, = [item for item in index["devices"] if item["provider"] == "delux"]
        self.assertEqual((row["vendorId"], row["productId"], row["name"], row["kind"]),
                         (VID, PID, "DELUX GH8K/M900 Ultra", "mouse"))
        self.assertEqual(len([item for item in index["devices"] if item["provider"] == "delux"]), 1)

    def test_generated_catalog_stays_synchronized(self):
        self.assertEqual(validate_catalog()["errors"], [])

    def descriptor_fixture(self, rows):
        case = T.DescriptorCacheTests()
        case.setUp()
        self.addCleanup(case.doCleanups)
        case.parent.side_effect = lambda path: "receiver-a"
        for path, (identity, collection) in rows.items():
            case.identities[path] = identity

        def caps(data, pointer):
            value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
            value.page, value.usage = rows[case.handles[data.value]][1]
            value.input = value.output = 17
            value.iv = value.ov = 1
            return 0x00110000

        def fields(report_type, values, length, data):
            self.assertIn(report_type, (0, 1))
            values[0].report, values[0].bits, values[0].count = 8, 8, 16
            return 0x00110000

        case.hid.HidP_GetCaps.side_effect = caps
        case.hid.HidP_GetValueCaps.side_effect = fields
        return case

    def test_topology_proves_only_exact_collection_and_report_08_sizes(self):
        case = self.descriptor_fixture({T.PATH_A: ((VID, PID), COLLECTION),
                                        T.PATH_B: ((VID, PID + 1), COLLECTION)})
        infos = case.scan([T.PATH_A, T.PATH_B])
        exact = next(info for info in infos if info["path"] == T.PATH_A.encode())
        sibling = next(info for info in infos if info["path"] == T.PATH_B.encode())
        self.assertEqual((exact["vendor_id"], exact["product_id"],
                          exact["usage_page"], exact["usage"]), (VID, PID, *COLLECTION))
        self.assertEqual((exact["input_report_length"], exact["output_report_length"]), (17, 17))
        self.assertEqual((exact["input_report_sizes"], exact["output_report_sizes"]), (REPORT, REPORT))
        self.assertNotIn("input_report_sizes", sibling)
        self.assertNotIn("output_report_sizes", sibling)
        self.assertEqual([call.args[0] for call in case.hid.HidP_GetValueCaps.call_args_list], [0, 1])
        case.hid.HidD_GetFeature.assert_not_called()
        case.hid.HidD_SetFeature.assert_not_called()
        case.kernel.ReadFile.assert_not_called()
        case.kernel.WriteFile.assert_not_called()

    def test_complete_descriptor_is_cached_without_device_traffic(self):
        case = self.descriptor_fixture({T.PATH_A: ((VID, PID), COLLECTION)})
        first = case.scan([T.PATH_A])
        self.assertEqual(first[0]["input_report_sizes"], REPORT)
        case.kernel.CreateFileW.reset_mock()
        case.hid.HidP_GetValueCaps.reset_mock()
        self.assertEqual(case.scan([T.PATH_A]), first)
        case.kernel.CreateFileW.assert_not_called()
        case.hid.HidP_GetValueCaps.assert_not_called()
        case.hid.HidD_GetFeature.assert_not_called()
        case.hid.HidD_SetFeature.assert_not_called()
        case.kernel.ReadFile.assert_not_called()
        case.kernel.WriteFile.assert_not_called()

    def test_failed_report_proof_is_not_cached_and_recovers(self):
        case = self.descriptor_fixture({T.PATH_A: ((VID, PID), COLLECTION)})
        original = case.hid.HidP_GetValueCaps.side_effect
        for direction in (0, 1):
            for defect in ("failure", "alias", "non_byte", "partial"):
                with self.subTest(direction=direction, defect=defect):
                    case.cache.clear()

                    def broken(report_type, values, length, data):
                        result = original(report_type, values, length, data)
                        if report_type != direction:
                            return result
                        if defect == "failure":
                            return 0
                        if defect == "alias":
                            values[0].alias = 1
                        elif defect == "non_byte":
                            values[0].bits = 16
                        else:
                            ctypes.cast(length, ctypes.POINTER(ctypes.c_ushort))[0] = 0
                        return result

                    case.hid.HidP_GetValueCaps.side_effect = broken
                    failed, = case.scan([T.PATH_A])
                    field = ("input" if direction == 0 else "output") + "_report_sizes"
                    self.assertIsNone(failed[field])
                    self.assertNotIn(T.PATH_A, case.cache)
                    case.hid.HidP_GetValueCaps.side_effect = original
                    recovered, = case.scan([T.PATH_A])
                    self.assertEqual(recovered["input_report_sizes"], REPORT)
                    self.assertEqual(recovered["output_report_sizes"], REPORT)

    def test_descriptor_bridge_does_not_expand_to_other_id_or_collection(self):
        for identity, collection in (((VID, PID + 1), COLLECTION),
                                     ((VID + 1, PID), COLLECTION),
                                     ((VID, PID), (0xFF02, 1)),
                                     ((VID, PID), (0xFF04, 2))):
            with self.subTest(identity=identity, collection=collection):
                case = self.descriptor_fixture({T.PATH_A: (identity, collection)})
                info, = case.scan([T.PATH_A])
                self.assertNotIn("input_report_sizes", info)
                self.assertNotIn("output_report_sizes", info)
                case.hid.HidP_GetValueCaps.assert_not_called()
                case.doCleanups()

    def test_poll_requires_exact_report_proof_before_opening(self):
        from providers import delux as D

        exact = {"vendor_id": VID, "product_id": PID,
                 "usage_page": COLLECTION[0], "usage": COLLECTION[1],
                 "path": b"delux", "physical_id": "receiver-a",
                 "input_report_length": 17, "output_report_length": 17,
                 "input_report_sizes": dict(REPORT), "output_report_sizes": dict(REPORT)}
        defects = [{"physical_id": "collection:unresolved"}]
        for direction in ("input", "output"):
            for length in (None, 16, 18):
                defects.append({direction + "_report_length": length})
            for sizes in (None, {}, {8: 17}, {"7": 17}, {"8": 16}, {"8": 17, "9": 17}):
                defects.append({direction + "_report_sizes": sizes})
        for changes in defects:
            with self.subTest(changes=changes):
                info = dict(exact, **changes)
                with patch.object(D, "hid", SimpleNamespace()), \
                        patch.object(D.hidlist, "enumerate", return_value=[info]), \
                        patch.object(D.DeluxProvider, "_poll_one") as opened:
                    self.assertEqual(D.DeluxProvider().poll(), [])
                    opened.assert_not_called()
        with patch.object(D, "hid", SimpleNamespace()), \
                patch.object(D.hidlist, "enumerate", return_value=[exact]), \
                patch.object(D.DeluxProvider, "_poll_one", return_value=[]) as opened:
            self.assertEqual(D.DeluxProvider().poll(), [])
            opened.assert_called_once_with(exact)
        for inventory in ([], [exact, dict(exact, path=b"second")]):
            with self.subTest(paths=[info["path"] for info in inventory]):
                with patch.object(D, "hid", SimpleNamespace()), \
                        patch.object(D.hidlist, "enumerate", return_value=inventory), \
                        patch.object(D.DeluxProvider, "_poll_one") as opened:
                    self.assertEqual(D.DeluxProvider().poll(), [])
                    opened.assert_not_called()
        self.assertFalse(D.DeluxProvider.charging_known)
        self.assertEqual(D.DeluxProvider.verification_level, "A")

    def test_recognized_candidates_do_not_expand_across_identity_or_collection(self):
        from providers.delux import DeluxProvider

        def entry(vid=VID, pid=PID, collection=COLLECTION, path=b"delux", sizes=REPORT):
            return {"vendor_id": vid, "product_id": pid, "usage_page": collection[0],
                    "usage": collection[1], "path": path, "input_report_length": 17,
                    "output_report_length": 17, "input_report_sizes": sizes,
                    "output_report_sizes": sizes}

        exact = entry()
        unrelated = [entry(pid=0xFB25), entry(pid=0xFB27), entry(vid=0x3555),
                     entry(collection=(0xFF02, 0x0001)), entry(collection=(0xFF04, 0x0002))]
        self.assertEqual(server.recognized_candidate_count("delux", unrelated[:3]), 0)
        self.assertEqual(server.recognized_candidate_count("delux", unrelated[:3] + [exact]), 1)
        provider = DeluxProvider()
        for info in unrelated:
            with self.subTest(identity=(info["vendor_id"], info["product_id"], info["usage_page"], info["usage"])):
                provider._diag = []
                self.assertIsNone(provider._pick([info]))
        self.assertIs(provider._pick([exact]), exact)


if __name__ == "__main__":
    unittest.main()
