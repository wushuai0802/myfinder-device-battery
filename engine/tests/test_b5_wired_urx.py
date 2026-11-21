"""Offline replay for the exact EWEADN and URX interfaces."""

import unittest

from known_ids import SHARED_UNINDEXED
from test_families import FakeDevice, collection, frame, poll


def wired(**changes):
    row = collection(vendor_id=0x088D, product_id=0x062E, product_string="EWEADN S9",
                     usage_page=0xFF02, usage=0, path=b"eweadn-wired",
                     physical_id=r"pnp:usb\vid_088d&pid_062e\cable",
                     input_report_length=33, output_report_length=33,
                     input_report_sizes={"0": 33}, output_report_sizes={"0": 33})
    row.update(changes)
    return row


def urx(**changes):
    row = collection(vendor_id=0x36B0, product_id=0x3002, product_string="URX Core75 Wireless",
                     usage_page=0xFF60, usage=0x0061, interface_number=1, path=b"urx-core75",
                     physical_id=r"pnp:usb\vid_36b0&pid_3002\receiver",
                     input_report_length=33, output_report_length=33,
                     input_report_sizes={"0": 33}, output_report_sizes={"0": 33})
    row.update(changes)
    return row


def captured_wired_reply():
    # BrimDeck's S9 protocol sample has M156, 70%, online. Unpublished
    # firmware/state fields and trailing padding are deterministic fixture bytes.
    return frame("30 00 01 0B 4D 31 35 36 00 00 00 00 01 46 01", length=32)


class WiredAndUrxTests(unittest.TestCase):
    def test_wired_uses_exact_identify_report_and_replays_s9_layout(self):
        receiver = wired()
        fake = FakeDevice([captured_wired_reply()])
        statuses, _ = poll([receiver], fake)
        self.assertEqual([(s.level, s.charging, s.verification_level) for s in statuses], [(70, True, "C")])
        self.assertEqual(fake.opened, [receiver["path"]])
        self.assertEqual(fake.writes, [bytes.fromhex("00 30 00 01").ljust(33, b"\0")])
        self.assertEqual(fake.feature_sets, [])

    def test_wired_ignores_incomplete_offline_and_invalid_identity_replies(self):
        base = captured_wired_reply()
        for reply in (base[:-1], base[:4] + [0] + base[5:], base[:14] + [0] + base[15:],
                      base[:12] + [2] + base[13:], frame("30 00 01", length=32)):
            with self.subTest(reply=reply[:15]):
                statuses, _ = poll([wired()], FakeDevice([reply]))
                self.assertEqual(statuses, [])

    def test_urx_reads_percent_and_leaves_charging_unknown(self):
        fake = FakeDevice([frame("08 00 07 4C", length=32)])
        statuses, _ = poll([urx()], fake)
        self.assertEqual([(s.level, s.charging_known, s.verification_level) for s in statuses], [(76, False, "C")])
        self.assertEqual(fake.writes, [bytes.fromhex("00 08 00 07").ljust(33, b"\0")])
        self.assertEqual(fake.feature_sets, [])
        self.assertIn((0x36B0, 0x3002), SHARED_UNINDEXED)

    def test_urx_rejects_echo_wrong_command_and_invalid_level(self):
        for payload in ("08 00 07 00", "08 00 06 4C", "08 00 07 65", "FF 00 07 4C"):
            with self.subTest(payload=payload):
                statuses, _ = poll([urx()], FakeDevice([frame(payload, length=32)]))
                self.assertEqual(statuses, [])

    def test_wrong_identity_or_proof_never_opens_a_collection(self):
        rejected = (
            [wired(vendor_id=0x089D, product_id=0x062F)],
            [wired(usage_page=0xFF01, usage=0x10)],
            [wired(input_report_sizes={"0": 17})],
            [wired(), wired(path=b"duplicate-wired")],
            [urx(product_string="PMO P75")],
            [urx(product_string="URX Core75 Wireless 2")],
            [urx(interface_number=2)],
            [urx(usage_page=0xFF61)],
            [urx(output_report_sizes={})],
            [urx(), urx(path=b"duplicate-urx")],
        )
        for inventory in rejected:
            with self.subTest(inventory=inventory):
                fake = FakeDevice()
                statuses, _ = poll(inventory, fake)
                self.assertEqual(statuses, [])
                self.assertEqual(fake.opened, [])
                self.assertEqual(fake.writes, [])
