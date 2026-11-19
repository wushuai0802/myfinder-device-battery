"""Offline X87 identity, byte-offset, and shared-receiver regressions."""
from pathlib import Path
import sys
import unittest
from unittest.mock import MagicMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
import battery_hid
from providers.eweadn_x87 import EweadnX87Provider


def receiver(length=33, **changes):
    base = dict(vendor_id=0x0C45, product_id=0xFEFE, path=b"x87", interface_number=3,
                product_string="2.4G Dongle", physical_id=r"pnp:usb\vid_0c45&pid_fefe\fixture",
                usage_page=0xFF60, usage=0x61, input_report_length=length, output_report_length=length,
                input_report_sizes={"0": length}, output_report_sizes={"0": length})
    return dict(base, **changes)


def fixture(size=32, percent=37):
    data = bytearray(size)
    data[:8] = bytes.fromhex("55 10 18 00 00 00 01 00")
    data[20:26] = bytes((2, 0, 5, 0, 2, percent))
    return data


class X87Tests(unittest.TestCase):
    def test_one_identity_read_yields_level_on_both_proven_report_sizes(self):
        for length, level in ((33, 37), (65, 100)):
            dev = MagicMock()
            dev.write.return_value = length
            dev.read.return_value = list(fixture(length - 1, level))
            with patch.object(battery_hid.hidlist, "enumerate", return_value=[receiver(length)]), \
                 patch.object(battery_hid.hid, "device", return_value=dev):
                card, = EweadnX87Provider().poll()
            self.assertEqual((card.level, card.kind, card.charging_known), (level, "keyboard", False))
            dev.write.assert_called_once_with(bytes.fromhex("00 aa 10 18 00 00 00 01 00").ljust(length, b"\0"))
            dev.read.assert_called_once_with(length - 1, 600)
            dev.send_feature_report.assert_not_called()
            dev.close.assert_called_once()

    def test_shared_id_with_another_model_or_invalid_page_produces_no_reading(self):
        for pos, value in ((0, 0xAA), (1, 0x20), (3, 24), (4, 1), (20, 3),
                           (22, 6), (24, 1), (25, 0), (25, 101)):
            data = fixture()
            data[pos] = value
            dev = MagicMock()
            dev.read.return_value = data
            dev.write.return_value = 33
            self.assertIsNone(EweadnX87Provider()._query(dev, receiver()))
            self.assertEqual(dev.write.call_count, 1)
        dev.read.return_value = []
        self.assertIsNone(EweadnX87Provider()._query(dev, receiver()))

    def test_other_receivers_or_unproven_interfaces_never_open(self):
        for rows in ([receiver(product_string="AULA F87ProV2D Dongle")], [receiver(product_id=0xFEF9)],
                     [receiver(usage_page=0xFF59)], [receiver(interface_number=1)],
                     [receiver(physical_id=r"pnp:usb\vid_0c45&pid_fef9\fixture")],
                     [receiver(input_report_sizes=None)], [receiver(output_report_sizes={"1": 33})],
                     [receiver(output_report_length=65)], [receiver(), receiver(path=b"ambiguous")]):
            with self.subTest(rows=rows), patch.object(battery_hid.hidlist, "enumerate", return_value=rows), \
                 patch.object(battery_hid.hid, "device") as factory:
                self.assertEqual(EweadnX87Provider().poll(), [])
                factory.assert_not_called()
