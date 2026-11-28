"""Engine-level registration and triage index checks for vgn_v4_keyboard & asus_azoth OMNI."""
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "engine"))
sys.path.insert(0, str(ROOT / "engine" / "tests"))

import _hidstub  # noqa: F401
import device_pack
import diagnostics_projection
import known_ids
import server
from providers import asus_azoth, vgn_v4_keyboard as V


class VgnV4KeyboardEngineTests(unittest.TestCase):
    def test_registered_in_server_device_pack_projection_and_known_ids(self):
        name = V.VgnV4KeyboardProvider.name
        self.assertEqual(server.PROVIDER_VIDS[name], (0x391D,))
        self.assertIn(name, server.READER_DIAGNOSTICS)
        self.assertIn(name, server.NO_CHARGING_EVIDENCE)
        self.assertIn(name, diagnostics_projection.PROJECTED_DIAGNOSTIC_PROVIDERS)
        self.assertIn(name, diagnostics_projection.FIXED_FIRST_DIAGNOSTIC_PROVIDERS)
        self.assertIn(name, device_pack.BRAND_PROVIDERS)

        index = known_ids.build_index()
        self.assertNotIn(name, index["unparsed"])
        self.assertEqual(index["vendors"][name], [0x391D])
        indexed = {
            (row["vendorId"], row["productId"])
            for row in index["devices"]
            if row["provider"] == name
        }
        self.assertEqual(
            indexed,
            {(0x391D, pid) for pid in (0x2A03, 0x2A04, 0x2A0A, 0x200B, 0x200C, 0x201C)},
        )

    def test_asus_azoth_omni_models_include_azoth_extreme_and_scope_rx(self):
        self.assertEqual(asus_azoth.KEYBOARDS[0x1B42], "ROG Azoth Extreme")
        self.assertEqual(asus_azoth.KEYBOARDS[0x1CF1], "ROG Azoth Extreme SE")
        self.assertEqual(asus_azoth.KEYBOARDS[0x1B7A], "ROG Strix Scope II 96 RX Wireless")


if __name__ == "__main__":
    unittest.main()
