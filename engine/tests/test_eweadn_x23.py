"""The X23 Pro Win32 report offset differs for unnumbered hidapi input."""
from pathlib import Path
import sys
import unittest
from unittest.mock import MagicMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
import battery_hid
from providers import eweadn_x23 as x23

PATH = rb"\\?\HID#VID_04F3&PID_026F&MI_01&Col04#fixture#{test}"


def row(**changes):
    info = dict(path=PATH, vendor_id=0x04F3, product_id=0x026F, interface_number=1,
                usage_page=0x0A, usage=0, product_string="X23 Pro Receiver",
                physical_id=r"pnp:usb\vid_04f3&pid_026f\receiver", input_report_length=32,
                input_report_sizes={"4": 32})
    return dict(info, **changes)


class X23Tests(unittest.TestCase):
    def test_mouse_only_listens_and_normalizes_the_windows_offset(self):
        for identifier, level in ((4, 73), (0, 64), (4, 0), (0, 100)):
            with self.subTest(identifier=identifier, level=level):
                dev = MagicMock()
                windows_frame = bytes((identifier, 1, 2, 3, level)) + bytes(27)
                dev.read.return_value = list(windows_frame if identifier else windows_frame[1:])
                target = row(input_report_sizes={str(identifier): 32})
                neighbor = row(path=PATH.replace(b"Col04", b"Col03"))
                with patch.object(battery_hid.hidlist, "enumerate", return_value=[neighbor, target]), \
                     patch.object(battery_hid.hid, "device", return_value=dev):
                    card, = x23.EweadnX23Provider().poll()
                self.assertEqual((card.level, card.charging_known, card.kind), (level, False, "mouse"))
                dev.open_path.assert_called_once_with(PATH)
                dev.read.assert_called_once_with(32 if identifier else 31, 1200)
                dev.write.assert_not_called()
                dev.get_feature_report.assert_not_called()
                dev.send_feature_report.assert_not_called()
                dev.close.assert_called_once()

    def test_similar_collections_and_unproven_report_ids_are_never_opened(self):
        for rows in ([row(path=PATH.replace(b"Col04", b"Col040"))], [row(usage_page=1)],
                     [row(product_string="X23 Receiver")], [row(interface_number=2)],
                     [row(physical_id=r"pnp:usb\vid_04f3&pid_ffff\fixture")],
                     [row(input_report_sizes=None)], [row(input_report_sizes={"4": 32, "5": 32})],
                     [row(input_report_sizes={"04": 32})], [row(input_report_length=31)],
                     [row(), row(input_report_sizes=None)]):
            with self.subTest(rows=rows), patch.object(battery_hid.hidlist, "enumerate", return_value=rows), \
                 patch.object(battery_hid.hid, "device") as factory:
                self.assertEqual(x23.EweadnX23Provider().poll(), [])
                factory.assert_not_called()

    def test_invalid_percent_silence_and_other_ids_produce_no_new_reading(self):
        provider = x23.EweadnX23Provider()
        for raw in ([], [4]*31, [5]*32, [4,1,2,3,101] + [0]*27):
            dev = MagicMock()
            dev.read.return_value = raw
            self.assertIsNone(provider._query(dev, row()))
            dev.write.assert_not_called()
        dev = MagicMock()
        dev.read.return_value = bytes(31)
        self.assertIsNone(provider._query(dev, row(input_report_sizes={"0": 32})))
