"""Offline replay for the FIFINE HT6 USB receiver."""

import unittest

from test_families import FakeDevice, collection, frame, poll


REQUEST = bytes.fromhex("55 65 01 00").ljust(64, b"\x00")


def receiver(**changes):
    row = collection(
        vendor_id=0x3142,
        product_id=0x00C3,
        product_string="FIFINE HT6",
        path=b"fifine-ht6",
        physical_id=r"pnp:usb\vid_3142&pid_00c3\receiver",
    )
    row.update(changes)
    return row


class FifineHt6Tests(unittest.TestCase):
    def test_upstream_state_reply(self):
        # HeadsetControl recorded the discharge fixture on HT6 firmware.
        # The charging variant changes only the state byte.
        for status, charging in (("02", False), ("03", True)):
            with self.subTest(status=status):
                row = receiver()
                reply = frame(f"55 65 50 {status} 02 00 00 ff 00 00 00 00 01 08 00 00 04 02 01 00 01")
                device = FakeDevice([reply])
                result, _ = poll([row], device)
                self.assertEqual(len(result), 1)
                self.assertEqual(result[0].level, 80)
                self.assertEqual(result[0].charging, charging)
                self.assertEqual(result[0].verification_level, "C")
                self.assertEqual(device.opened, [row["path"]])
                self.assertEqual(device.writes, [REQUEST])
                self.assertEqual(device.feature_sets, [])
                self.assertEqual(device.feature_gets, [])

    def test_invalid_or_unrelated_replies_are_not_readings(self):
        replies = (
            frame("55 65 01 00"),
            frame("55 65 50 1A"),
            frame("55 0A 01"),
            frame("55 65 50 08"),
            frame("55 65 65 02"),
            frame("55 65 50 02", length=63),
        )
        for reply in replies:
            with self.subTest(prefix=reply[:4], length=len(reply)):
                device = FakeDevice([reply])
                result, _ = poll([receiver()], device)
                self.assertEqual(result, [])
                self.assertEqual(device.writes, [REQUEST])

    def test_only_exact_identity_with_one_proved_collection_may_open(self):
        rejected = (
            [receiver(product_id=0xEEEE)],
            [receiver(usage_page=0xFF82)],
            [receiver(input_report_sizes={})],
            [receiver(output_report_sizes={"85": 63})],
            [receiver(), receiver(path=b"duplicate")],
        )
        for inventory in rejected:
            with self.subTest(inventory=inventory):
                device = FakeDevice()
                result, _ = poll(inventory, device)
                self.assertEqual(result, [])
                self.assertEqual(device.opened, [])
                self.assertEqual(device.writes, [])
