"""FA6x Input 03 proof using existing fake Windows descriptor fixtures only."""
import ctypes
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "engine"))
sys.path.insert(0, str(ROOT / "engine" / "tests"))
import _hidstub  # noqa: F401
from providers import fa6x_passive as F
import test_topology_cache as fixtures


class Fa6xTopologyTests(unittest.TestCase):
    def setUp(self):
        self.fixture = fixtures.DescriptorCacheTests()
        self.addCleanup(self.fixture.doCleanups)
        self.fixture.setUp()

    def assert_descriptor_only(self):
        f = self.fixture
        f.hid.HidD_GetFeature.assert_not_called()
        f.hid.HidD_SetFeature.assert_not_called()
        f.kernel.ReadFile.assert_not_called()
        f.kernel.WriteFile.assert_not_called()
        self.assertTrue(all(call.args[1] == 0 for call in f.kernel.CreateFileW.call_args_list))
        self.assertTrue(all(call.args[0] == 0 for call in f.hid.HidP_GetValueCaps.call_args_list))

    def test_exact_collections_prove_input_only_and_cache_complete_descriptors(self):
        f = self.fixture
        for pid, collection in ((0xFA60, (0x000A, 0)), (0xFA65, (0xFF00, 1))):
            for sizes in ({3: 5}, {3: 8}, {3: 19, 9: 65}):
                with self.subTest(pid=pid, sizes=sizes):
                    f.cache.clear()
                    f.batch3_descriptor((0x1D57, pid), collection, sizes, {4: 65})
                    info, = f.scan([fixtures.PATH_A])
                    self.assertEqual(info["input_report_sizes"], {str(k): v for k, v in sizes.items()})
                    self.assertNotIn("output_report_sizes", info)
                    self.assertNotIn("feature_report_sizes", info)
                    self.assertEqual(F.Fa6xPassiveProvider()._pick([info]), info)
                    before = f.kernel.CreateFileW.call_count
                    self.assertEqual(f.scan([fixtures.PATH_A]), [info])
                    self.assertEqual(f.kernel.CreateFileW.call_count, before)
        self.assert_descriptor_only()

    def test_incomplete_proof_is_not_cached_and_retries_without_io(self):
        f = self.fixture
        for pid, collection in F.COLLECTIONS.items():
            for defect in ("no_03", "short", "not_bytes", "alias", "partial", "failure"):
                with self.subTest(pid=pid, defect=defect):
                    f.cache.clear()
                    sizes = {3: 4} if defect == "short" else {3: 8}
                    f.batch3_descriptor((0x1D57, pid), collection, sizes, {4: 65})
                    original = f.hid.HidP_GetValueCaps.side_effect

                    def fields(report_type, values, length, data):
                        result = original(report_type, values, length, data)
                        if defect == "no_03":
                            values[0].report = 4
                        elif defect == "not_bytes":
                            values[0].bits = 16
                        elif defect == "alias":
                            values[0].alias = 1
                        elif defect == "partial":
                            ctypes.cast(length, ctypes.POINTER(ctypes.c_ushort))[0] = 0
                        elif defect == "failure":
                            return 0
                        return result

                    f.hid.HidP_GetValueCaps.side_effect = fields
                    info, = f.scan([fixtures.PATH_A])
                    self.assertIsNone(F.Fa6xPassiveProvider()._pick([info]))
                    self.assertNotIn(fixtures.PATH_A, f.cache)
                    f.batch3_descriptor((0x1D57, pid), collection, {3: 8}, {4: 65})
                    recovered, = f.scan([fixtures.PATH_A])
                    self.assertEqual(F.Fa6xPassiveProvider()._pick([recovered]), recovered)
        self.assert_descriptor_only()

    def test_neighboring_ids_and_communication_collections_are_not_probed(self):
        f = self.fixture
        cases = [((0x1D57, 0xFA60), (0x000B, 0)), ((0x1D57, 0xFA60), (0xFF00, 1)),
                 ((0x1D57, 0xFA65), (0xFF01, 2)), ((0x1D57, 0xFA65), (0x000A, 0)),
                 ((0x1D57, 0xFA65), (0xFF00, 2)), ((0x1D57, 0xFA61), (0x000A, 0)),
                 ((0x1D57, 0x1013), (0x000A, 0)), ((0x1D58, 0xFA65), (0xFF00, 1))]
        for identity, collection in cases:
            with self.subTest(identity=identity, collection=collection):
                f.cache.clear()
                f.batch3_descriptor(identity, collection, {3: 8}, {4: 65})
                info, = f.scan([fixtures.PATH_A])
                self.assertNotIn("input_report_sizes", info)
                self.assertIsNone(F.Fa6xPassiveProvider()._pick([info]))
        f.hid.HidP_GetValueCaps.assert_not_called()
        f.hid.HidP_GetButtonCaps.assert_not_called()
        self.assert_descriptor_only()


if __name__ == "__main__":
    unittest.main()
