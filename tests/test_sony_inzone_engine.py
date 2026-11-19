"""Exercise Windows descriptor discovery and the scoped Sony reader without hardware."""
import ctypes
import sys
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "engine"))
sys.path.insert(0, str(ROOT / "engine" / "tests"))
sys.path.insert(0, str(ROOT / "tests"))
import _hidstub  # noqa: F401
import test_topology_cache as T
from device_pack import BRAND_PROVIDERS
from known_ids import build_index
from providers import sony_inzone as sony
from server import scoped_poll
from standardhid import Caps


class InzoneEnumerationTests(unittest.TestCase):
    def test_buds_and_neighbor_through_windows_topology_and_scoped_poll(self):
        case = T.DescriptorCacheTests()
        case.setUp()
        self.addCleanup(case.doCleanups)
        case.identities.update({T.PATH_A: (sony.VID, sony.BUDS), T.PATH_B: (sony.VID, 0x0E53)})
        case.parent.side_effect = lambda path: "receiver-a" if path == T.PATH_A else "receiver-b"

        def caps(data, pointer):
            value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
            value.page, value.usage = 0xFF04, 1
            value.input = value.output = 64
            value.iv = value.ov = 1
            return 0x00110000

        def fields(report_type, values, length, data):
            values[0].report, values[0].bits, values[0].count = 2, 8, 63
            return 0x00110000

        case.hid.HidP_GetCaps.side_effect = caps
        case.hid.HidP_GetValueCaps.side_effect = fields
        inventory = case.scan([T.PATH_A, T.PATH_B])
        exact = next(info for info in inventory if info["product_id"] == sony.BUDS)
        other = next(info for info in inventory if info["product_id"] == 0x0E53)
        self.assertTrue(sony.reports_proven(exact))
        self.assertNotIn("output_report_sizes", other)
        reader = sony.SonyInzoneProvider()
        reader.tid = 2
        handle = MagicMock()
        handle.write.return_value = 64
        handle.read.return_value = bytes.fromhex(
            "02 12 04 FF 0F 00 96 C3 14 04 10 02 00 00 64 00 64 FF 61 AB").ljust(64, b"\0")
        with patch.object(sony.hid, "device", return_value=handle):
            results = scoped_poll(reader, inventory, exact["physical_id"])
        handle.open_path.assert_called_once_with(exact["path"])
        handle.write.assert_called_once()
        self.assertEqual([d.level for d in results], [100, 100, 97])
        self.assertEqual({d.unit_id for d in results}, {"left", "right", "case"})

    def test_catalogue_indexes_only_the_three_receiver_ids(self):
        self.assertIn("sony_inzone", BRAND_PROVIDERS)
        rows = [r for r in build_index()["devices"] if r["provider"] == "sony_inzone"]
        self.assertEqual({(r["vendorId"], r["productId"], r["kind"]) for r in rows},
                         {(sony.VID, pid, "headset") for pid in sony.PIDS})
