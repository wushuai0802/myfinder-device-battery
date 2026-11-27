"""Engine-level routing and triage index checks for hyperx_stinger_core."""
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "engine"))
sys.path.insert(0, str(ROOT / "engine" / "tests"))

import _hidstub  # noqa: F401
import device_pack
import known_ids
import server
from providers import hyperx_stinger_core as H


class HyperXStingerCoreEngineTests(unittest.TestCase):
    def test_registered_in_server_device_pack_and_known_ids(self):
        self.assertEqual(server.PROVIDER_VIDS[H.HyperXStingerCoreProvider.name], (0x03F0, 0x0951))
        self.assertIn(H.HyperXStingerCoreProvider.name, server.READER_DIAGNOSTICS)
        self.assertNotIn(H.HyperXStingerCoreProvider.name, server.NO_CHARGING_EVIDENCE)
        self.assertIn(H.HyperXStingerCoreProvider.name, device_pack.BRAND_PROVIDERS)

        index = known_ids.build_index()
        self.assertNotIn("hyperx_stinger_core", index["unparsed"])
        self.assertEqual(sorted(index["vendors"]["hyperx_stinger_core"]), [0x03F0, 0x0951])
        indexed = {
            (row["vendorId"], row["productId"])
            for row in index["devices"]
            if row["provider"] == "hyperx_stinger_core"
        }
        self.assertEqual(indexed, {(0x03F0, 0x0A8E), (0x03F0, 0x0A93), (0x0951, 0x170B)})

    def test_server_catalog_includes_stinger_core_and_vgn_ry_ms_wired(self):
        index = known_ids.build_index()
        vgn = {
            (row["vendorId"], row["productId"])
            for row in index["devices"]
            if row["provider"] == "vgn_ry_ms"
        }
        for pid in (0x1004, 0x1008, 0x1015, 0x1028, 0x102F, 0x1048,
                    0x1A04, 0x1A08, 0x1A13, 0x1A25, 0x1A2B, 0x1A41):
            self.assertIn((0x391D, pid), vgn)


if __name__ == "__main__":
    unittest.main()
