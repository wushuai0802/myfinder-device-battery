"""Synthetic AULA F99 MAX 2.4G battery protocol regression fixtures.

These tests use only the local fake hidapi backend.  They deliberately assert
the complete readonly exchange so unsupported AULA receivers cannot inherit it.
"""
from contextlib import contextmanager
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ENGINE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ENGINE))
sys.path.insert(0, str(ENGINE / "tests"))
import _hidstub  # noqa: F401 -- before importing a HID provider
import battery_hid as H
from providers import aula_f99max as F
from test_new_brands import FakeHid

REQUEST_0 = bytes.fromhex(
    "00 AA 10 18 00 00 00 00 " + "00 " * 24)
REQUEST_18 = bytes.fromhex(
    "00 AA 10 18 18 00 00 01 " + "00 " * 24)


def entry(physical="f99-receiver", **changes):
    info = dict(vendor_id=0x05AC, product_id=0x024F,
                product_string="F99 MAX Dongle", interface_number=3,
                usage_page=0xFF60, usage=0x0061,
                input_report_length=33, output_report_length=33,
                input_report_sizes={"0": 33}, output_report_sizes={"0": 33},
                physical_id=physical, path=(physical + "-ff60").encode(),
                serial_number="synthetic-private-serial")
    info.update(changes)
    return info


def replies(level=68, charge=0, *, firmware=(0x0C, 0x45, 0x80, 0x0A)):
    first = [0x55, 0x10, 0x18, 0x00] + [0] * 28
    second = [0x55, 0x10, 0x18, 0x18, 0x00, 0x00, 0x01, 0x00] + [0] * 24
    first[12:16] = [firmware[1], firmware[0], firmware[3], firmware[2]]
    first[25], first[26] = level, charge
    return [first, second]


@contextmanager
def fixture(frames=(), inventory=None, **kwargs):
    bus = FakeHid(frames, **kwargs)
    with patch.object(H, "hid", bus), \
            patch.object(H.hidlist, "enumerate",
                         return_value=[entry()] if inventory is None else inventory), \
            patch.object(H, "time", SimpleNamespace(monotonic=lambda: bus.now)):
        yield bus


class AulaF99MaxTests(unittest.TestCase):
    def test_two_literal_get_device_info_packets_and_68_percent(self):
        with fixture(replies()) as bus:
            statuses = F.AulaF99MaxProvider().poll()
        status, = statuses
        self.assertEqual((status.name, status.level, status.kind, status.source),
                         ("AULA F99 MAX", 68, "keyboard", "aula_keyboard"))
        self.assertEqual([packet for _, packet in bus.writes],
                         [list(REQUEST_0), list(REQUEST_18)])
        self.assertEqual(bus.opened, bus.closed)
        self.assertFalse(status.charging_known)

    def test_charge_status_is_retained_but_not_hardware_verified(self):
        with fixture(replies(charge=1)) as bus:
            status, = F.AulaF99MaxProvider().poll()
        self.assertTrue(status.charging)
        self.assertFalse(status.charging_known)
        self.assertEqual(len(bus.writes), 2)

    def test_bad_reply_never_yields_card_or_sends_other_command(self):
        bad = []
        bad += [[replies()[0][:-1], replies()[1]], [replies()[0], replies()[1][:-1]]]
        bad += [[list(REQUEST_0[1:]), replies()[1]]]
        wrong_echo = replies(); wrong_echo[1][6] = 0
        bad.append(wrong_echo)
        wrong_identity = replies(firmware=(0x05, 0xAC, 0x02, 0x4F))
        bad.append(wrong_identity)
        zero = replies(); zero[0] = [0] * 32
        bad.append(zero)
        for level in (0, 101, 255):
            bad.append(replies(level=level))
        for charge in (2, 255):
            bad.append(replies(charge=charge))
        bad.append([])
        for frames in bad:
            with self.subTest(frames=frames), fixture(frames) as bus:
                self.assertEqual(F.AulaF99MaxProvider().poll(), [])
                self.assertTrue(all(packet in (list(REQUEST_0), list(REQUEST_18))
                                    for _, packet in bus.writes))
                self.assertLessEqual(len(bus.writes), 2)
                self.assertEqual(bus.opened, bus.closed)

    def test_shared_usb_id_other_product_never_opens(self):
        for changes in (dict(product_string="AULA F99 Dongle"),
                        dict(product_string=""),
                        dict(usage_page=0xFF02), dict(usage=2)):
            with self.subTest(changes=changes), fixture(replies(), [entry(**changes)]) as bus:
                self.assertEqual(F.AulaF99MaxProvider().poll(), [])
                self.assertEqual((bus.opened, bus.writes), ([], []))

    def test_duplicate_collection_and_unproven_reports_never_open(self):
        cases = [[entry(), entry(path=b"duplicate")],
                 [entry(physical="")], [entry(physical="collection:unknown")],
                 [entry(input_report_sizes=None)], [entry(output_report_sizes=None)],
                 [entry(input_report_sizes={"0": 32})],
                 [entry(output_report_sizes={"0": 33, "1": 33})],
                 [entry(input_report_length=32)], [entry(output_report_length=32)]]
        for inventory in cases:
            with self.subTest(inventory=inventory), fixture(replies(), inventory) as bus:
                self.assertEqual(F.AulaF99MaxProvider().poll(), [])
                self.assertEqual((bus.opened, bus.writes), ([], []))

    def test_short_write_stops_and_dangerous_commands_are_absent(self):
        with fixture(replies(), write_result=31) as bus:
            self.assertEqual(F.AulaF99MaxProvider().poll(), [])
        self.assertEqual([packet for _, packet in bus.writes], [list(REQUEST_0)])
        self.assertEqual(bus.reads, [])
        self.assertEqual(bus.opened, bus.closed)


if __name__ == "__main__":
    unittest.main()
