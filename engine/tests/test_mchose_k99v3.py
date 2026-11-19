"""Replay the public K99 V3 descriptor and battery captures using fake HID only."""
import sys
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
import battery_hid
from providers import mchose_k99v3 as k99

DESCRIPTOR = bytes.fromhex(
    "05 01 09 00 a1 01 05 01 09 00 15 00 26 ff 00 95 40 75 08 81 02 "
    "05 01 09 00 15 00 26 ff 00 95 40 75 08 91 02 c0"
)


def collection(**changes):
    info = dict(vendor_id=0x3837, product_id=0x3033, product_string=k99.PRODUCT,
                interface_number=1, usage_page=1, usage=0, path=b"k99-fixture",
                physical_id=r"pnp:usb\vid_3837&pid_3033\fixture",
                input_report_length=65, output_report_length=65,
                input_report_sizes={"0": 65}, output_report_sizes={"0": 65})
    info.update(changes)
    return info


class K99V3Tests(unittest.TestCase):
    def test_upstream_descriptor_and_both_captured_frames(self):
        reports = battery_hid.parse_report_descriptor(DESCRIPTOR)[(1, 0)]
        self.assertEqual(reports, {"input": {"0": 65}, "output": {"0": 65}, "feature": {}})
        for prefix, expected in (("aa 03 00 38 00 00 00 00 28", 40),
                                 ("aa 03 01 00 00 00 00 00 64", 100)):
            with self.subTest(expected=expected):
                dev = MagicMock()
                dev.write.return_value = 65
                dev.read.return_value = bytes.fromhex(prefix).ljust(64, b"\0")
                with patch.object(battery_hid.hidlist, "enumerate", return_value=[collection()]), \
                     patch.object(battery_hid.hid, "device", return_value=dev):
                    card, = k99.MchoseK99V3Provider().poll()
                dev.write.assert_called_once_with(bytes.fromhex("00 55 03 00 38 38").ljust(65, b"\0"))
                dev.close.assert_called_once()
                self.assertEqual((card.level, card.kind, card.charging_known), (expected, "keyboard", False))

    def test_shared_pid_and_unproven_collection_never_open(self):
        for rows in ([collection(product_string="MCHOSE G87 V2 2.4G")],
                     [collection(product_string="MCHOSE K87S 2.4G")],
                     [collection(interface_number=2)], [collection(usage=2)],
                     [collection(physical_id="collection:fallback")],
                     [collection(input_report_sizes={})], [collection(output_report_sizes={"1": 65})],
                     [collection(), collection(path=b"second")]):
            with self.subTest(rows=rows), patch.object(battery_hid.hidlist, "enumerate", return_value=rows), \
                    patch.object(battery_hid.hid, "device") as factory:
                self.assertEqual(k99.MchoseK99V3Provider().poll(), [])
                factory.assert_not_called()

    def test_rejects_other_commands_or_invalid_percent(self):
        for packet in (bytes(64), bytes.fromhex("aa 05").ljust(64, b"\0"),
                       bytes.fromhex("aa 03"), bytes.fromhex("aa 03").ljust(8, b"\0") + bytes([101]) + bytes(55)):
            self.assertIsNone(k99.parse_reply(packet))
