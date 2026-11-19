"""Replay two unrelated keyboard protocols on synthetic, scoped HID inventories."""
from pathlib import Path
import sys
import unittest
from unittest.mock import MagicMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
import battery_hid
from providers.veekos_shine60 import Shine60Provider
from providers.weikav_nut75 import Nut75Provider


def nut(**changes):
    row = dict(vendor_id=0x0C45, product_id=0xFEF9, path=b"nut", product_string="NUT75 2.4G",
               physical_id=r"pnp:usb\vid_0c45&pid_fef9\fixture", usage_page=0xFF60, usage=0x61,
               input_report_length=65, output_report_length=65,
               input_report_sizes={"0": 65}, output_report_sizes={"0": 65})
    return dict(row, **changes)


def shine(**changes):
    row = dict(vendor_id=0x3151, product_id=0x5026, path=b"shine", interface_number=2,
               product_string="Veekos 2.4G Wireless Keyboard", usage_page=0xFFFF, usage=2,
               physical_id=r"pnp:usb\vid_3151&pid_5026\fixture", input_report_length=0,
               output_report_length=0, feature_report_length=65, feature_report_sizes={"131": 65})
    return dict(row, **changes)


def poll(provider, rows, dev):
    with patch.object(battery_hid.hidlist, "enumerate", return_value=rows), \
         patch.object(battery_hid.hid, "device", return_value=dev) as factory:
        return provider.poll(), factory


class KeyboardBatteryTests(unittest.TestCase):
    def test_nut75_queries_only_the_exact_vendor_collection(self):
        request = bytes.fromhex("00 AA 10 30 00 00 00 01") + bytes(57)
        for level in (1, 49, 100):
            with self.subTest(level=level):
                dev = MagicMock()
                dev.write.return_value = 65
                frame = bytearray(64)
                frame[:2] = b"\x55\x10"
                frame[25] = level
                dev.read.side_effect = [frame, []]
                rows = [nut(path=b"sibling", usage_page=0xFF70, usage=0x71), nut()]
                cards, factory = poll(Nut75Provider(), rows, dev)
                card, = cards
                factory.assert_called_once()
                dev.open_path.assert_called_once_with(b"nut")
                dev.write.assert_called_once_with(request)
                dev.close.assert_called_once()
                self.assertEqual((card.level, card.kind, card.charging_known), (level, "keyboard", False))

    def test_unproven_nut75_never_opens(self):
        for rows in ([nut(product_string="NUT65 2.4G")], [nut(usage=1)], [nut(input_report_length=64)],
                     [nut(physical_id=r"pnp:usb\vid_0c45&pid_fefe\fixture")],
                     [nut(output_report_sizes=None)], [nut(input_report_sizes={"0": 65, "1": 65})],
                     [nut(), nut(path=b"other")]):
            with self.subTest(rows=rows):
                cards, factory = poll(Nut75Provider(), rows, MagicMock())
                self.assertEqual(cards, [])
                factory.assert_not_called()

    def test_nut75_rejects_silence_bad_prefix_and_out_of_range_levels(self):
        for prefix, level, length in ((b"\x55\x10", 0, 64), (b"\x55\x10", 101, 64),
                                      (b"\x55\x11", 62, 64), (b"\x55\x10", 58, 27),
                                      (b"", 0, 0)):
            with self.subTest(prefix=prefix, level=level, length=length):
                dev = MagicMock()
                dev.write.return_value = 65
                frame = (prefix + bytes(23) + bytes([level]) + bytes(38))[:length]
                dev.read.side_effect = [frame, []]
                self.assertEqual(poll(Nut75Provider(), [nut()], dev)[0], [])
                dev.close.assert_called_once()

    def test_shine60_reads_only_the_descriptor_proven_status_report(self):
        for report_id, level in ((0x83, 61), (0x83, 48), (0, 48), (0, 0), (0x83, 100)):
            with self.subTest(report_id=report_id, level=level):
                dev = MagicMock()
                dev.get_feature_report.return_value = bytes([report_id, 0, level, 100]) + bytes(61)
                row = shine(feature_report_sizes={str(report_id): 65})
                cards, _ = poll(Shine60Provider(), [row], dev)
                card, = cards
                self.assertEqual((card.level, card.kind, card.charging_known), (level, "keyboard", False))
                dev.get_feature_report.assert_called_once_with(report_id, 65)
                dev.write.assert_not_called()
                dev.send_feature_report.assert_not_called()
                dev.close.assert_called_once()

    def test_unproven_shine60_never_opens(self):
        for rows in ([shine(interface_number=1)], [shine(product_id=0x5029)], [shine(usage=1)],
                     [shine(product_string="NUT75 2.4G")], [shine(physical_id="collection:fixture")],
                     [shine(input_report_length=65)], [shine(output_report_length=65)],
                     [shine(feature_report_sizes=None)], [shine(feature_report_sizes={"131": 64})],
                     [shine(feature_report_sizes={"1": 65})], [shine(feature_report_sizes={"0": 65, "131": 65})],
                     [shine(), shine(path=b"other")]):
            with self.subTest(rows=rows):
                cards, factory = poll(Shine60Provider(), rows, MagicMock())
                self.assertEqual(cards, [])
                factory.assert_not_called()

    def test_shine60_rejects_mismatched_echo_scale_percent_and_length(self):
        for prefix, length in (("00 00 30 64", 65), ("83 01 30 64", 65), ("83 00 30 63", 65),
                               ("83 00 65 64", 65), ("83 00 30 64", 64)):
            with self.subTest(prefix=prefix, length=length):
                dev = MagicMock()
                dev.get_feature_report.return_value = bytes.fromhex(prefix).ljust(length, b"\0")
                self.assertEqual(poll(Shine60Provider(), [shine()], dev)[0], [])
                dev.get_feature_report.assert_called_once_with(0x83, 65)
                dev.write.assert_not_called()
                dev.close.assert_called_once()
