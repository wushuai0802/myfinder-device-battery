"""Offline replay of measured Incott input reports."""

import unittest

from known_ids import SHARED_UNINDEXED
from test_families import FakeDevice, collection, frame, poll


def receiver(**changes):
    row = collection(
        vendor_id=0x093A,
        product_id=0x522C,
        product_string="incott 8K wireless mouse",
        interface_number=3,
        usage_page=0xFF05,
        usage=0x0001,
        path=b"incott",
        physical_id=r"pnp:usb\vid_093a&pid_522c\receiver",
        input_report_length=8,
        input_report_sizes={"9": 8},
    )
    row.update(changes)
    return row


def transmissions(device):
    return device.writes + device.feature_sets + device.feature_gets


class IncottPassiveTests(unittest.TestCase):
    def test_captured_reports_and_charging(self):
        samples = (
            ("09 5F 10 04 00 0F 0F 10", 95, False),
            ("09 E1 10 04 00 0F 0F 10", 97, True),
        )
        for report, percent, charging in samples:
            with self.subTest(report=report):
                device = FakeDevice([frame(report, length=8)])
                result, _ = poll([receiver()], device)
                self.assertEqual([(s.level, s.charging, s.verification_level) for s in result],
                                 [(percent, charging, "C")])
                self.assertEqual(device.opened, [b"incott"])
                self.assertEqual(transmissions(device), [])
        self.assertIn((0x093A, 0x522C), SHARED_UNINDEXED)

    def test_invalid_short_and_silent_inputs(self):
        for reports in (
            [], [frame("09 5F", length=7)], [frame("0A 5F", length=8)],
            [frame("09 00", length=8)], [frame("09 80", length=8)], [frame("09 FF", length=8)],
        ):
            with self.subTest(reports=reports):
                device = FakeDevice(reports)
                result, _ = poll([receiver()], device)
                self.assertEqual(result, [])
                self.assertEqual(transmissions(device), [])

    def test_never_open_shared_unproved_or_duplicate_interfaces(self):
        rows = (
            [receiver(product_string="AULA SC900")],
            [receiver(product_string="")],
            [receiver(product_id=0x622C, interface_number=2)],
            [receiver(interface_number=2)],
            [receiver(usage_page=0xFF01)],
            [receiver(input_report_sizes={"9": 9})],
            [receiver(input_report_sizes={"7": 8})],
            [receiver(), receiver(path=b"duplicate")],
        )
        for inventory in rows:
            with self.subTest(inventory=inventory):
                device = FakeDevice()
                result, _ = poll(inventory, device)
                self.assertEqual(result, [])
                self.assertEqual(device.opened, [])
                self.assertEqual(transmissions(device), [])
