"""Cross the Windows descriptor-to-reader boundary for the shared FB3E receiver."""
import ctypes
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path[:0] = [str(ROOT / "tests"), str(ROOT / "engine")]
import test_topology_cache as T
from known_ids import build_index
from providers import pulsar
from standardhid import Caps


class VgnFb3eBridgeTests(unittest.TestCase):
    def test_windows_report_proof_reaches_registered_reader_only_for_the_receiver(self):
        case = T.DescriptorCacheTests()
        case.setUp()
        self.addCleanup(case.doCleanups)
        path = r"\\?\hid#vid_3554&pid_fb3e&mi_01&col03#test"
        wired = path.replace("fb3e", "fb3d")
        case.identities.update({path: (0x3554, 0xFB3E), wired: (0x3554, 0xFB3D)})
        case.parent.side_effect = lambda p: r"pnp:usb\vid_3554&pid_" + ("fb3e" if p == path else "fb3d") + r"\test"

        def caps(data, pointer):
            v = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
            v.page, v.usage = 0xFF02, 2
            v.input = v.output = 17
            v.iv = v.ov = 1
            return 0x00110000

        def fields(kind, values, count, data):
            self.assertIn(kind, (0, 1))
            values[0].report, values[0].bits, values[0].count = 8, 8, 16
            return 0x00110000

        case.hid.HidP_GetCaps.side_effect = caps
        case.hid.HidP_GetValueCaps.side_effect = fields
        with patch.object(T.topology, "_read_report_descriptor", side_effect=AssertionError("unexpected fallback")):
            inventory = case.scan([path, wired])
            chosen = pulsar.pick_fb3e(inventory)
            self.assertIsNotNone(chosen)
            self.assertEqual(chosen["path"], path.encode())
            self.assertEqual(chosen["input_report_sizes"], {"8": 17})
            self.assertEqual(chosen["output_report_sizes"], {"8": 17})
            self.assertNotIn("input_report_sizes", next(d for d in inventory if d["path"] == wired.encode()))
            self.assertEqual(case.scan([path, wired]), inventory)
        case.hid.HidD_GetFeature.assert_not_called()
        case.hid.HidD_SetFeature.assert_not_called()
        rows = build_index()["devices"]
        self.assertTrue(any(d["provider"] == "pulsar" and (d["vendorId"], d["productId"]) == (0x3554,0xFB3E) for d in rows))
        self.assertFalse(any(d["provider"] == "pulsar" and (d["vendorId"], d["productId"]) == (0x3554,0xFB3D) for d in rows))
