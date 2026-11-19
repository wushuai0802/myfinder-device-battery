"""Offline test of the official M HUB MC GET, response field layout and USB gate."""
import sys
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
import battery_hid
from providers import mchose_v9pro2 as v9


def collection(**changes):
    data = dict(vendor_id=0x3837, product_id=0x6011, product_string="MCHOSE V9 Pro 2",
                interface_number=3, usage_page=0xFF07, usage=0x0222, path=b"vendor",
                physical_id=r"pnp:usb\vid_3837&pid_6011\receiver-test",
                input_report_length=64, output_report_length=64,
                input_report_sizes={"85": 64}, output_report_sizes={"85": 64})
    data.update(changes)
    return data


def frame(hex_data):
    return list(bytes.fromhex(hex_data).ljust(64, b"\0"))


class V9Pro2Tests(unittest.TestCase):
    def test_query_uses_only_mc_battery_get(self):
        request = v9.make_request()
        self.assertEqual(request, bytes.fromhex("55 aa 01 00 01 00 01 0b").ljust(64, b"\0"))
        dev = MagicMock()
        dev.write.return_value = 64
        dev.read.return_value = frame("55 aa 01 00 03 00 01 0b 01 c8 00")
        with patch.object(battery_hid.hidlist, "enumerate", return_value=[collection()]), \
                patch.object(battery_hid.hid, "device", return_value=dev):
            card, = v9.MchoseV9Pro2Provider().poll()
        self.assertEqual((card.level, card.charging, card.charging_known, card.kind), (72, True, True, "headset"))
        dev.write.assert_called_once_with(request)
        dev.close.assert_called_once()

    def test_checked_unchecked_sleep_and_invalid_packets(self):
        # Flag ^ length ^ type ^ subcommand ^ awake ^ level.
        valid = frame("55 aa 01 01 03 00 01 0b 01 28 21")
        self.assertEqual(v9.parse_reply(valid), (40, False, ""))
        asleep = frame("55 aa 01 00 03 00 01 0b 00 ff 00")
        self.assertTrue(v9.MchoseV9Pro2Provider().no_reading_reply(asleep))
        for pos, value in [(0, 84), (2, 3), (3, 2), (4, 1), (4, 61), (6, 4), (7, 34), (9, 101), (10, 0)]:
            changed = valid.copy()
            changed[pos] = value
            self.assertIsNone(v9.parse_reply(changed))
        self.assertIsNone(v9.parse_reply(valid[:-1]))
        self.assertIsNone(v9.parse_reply(v9.make_request()))

    def test_adjacent_products_ambiguous_or_unproven_never_open(self):
        for entries in ([collection(product_id=0x6013)], [collection(product_string="MCHOSE V9 PRO")],
                        [collection(interface_number=1)], [collection(usage_page=0xFF90)],
                        [collection(physical_id=r"pnp:usb\vid_3837&pid_600a\wrong")],
                        [collection(input_report_sizes={})], [collection(output_report_sizes={"85": 63})],
                        [collection(), collection(path=b"duplicate")]):
            with self.subTest(entries=entries), patch.object(battery_hid.hidlist, "enumerate", return_value=entries), \
                    patch.object(battery_hid.hid, "device") as factory:
                self.assertEqual(v9.MchoseV9Pro2Provider().poll(), [])
                factory.assert_not_called()
