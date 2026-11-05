"""Pulsar web-snapshot facts and deferred IDs; all reports/descriptors are synthetic.

No hardware capture is available for 3710:5501. See upstream-protocols.md;
matching a constructed CompX frame does not prove the receiver's descriptor.
"""
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
from catalog import load_catalog
from known_ids import build_index
from providers import pulsar as P
from test_pulsar import FakeBus


# Independently transcribed protocol facts, not frames captured from a device.
POWER_REQUEST = bytes.fromhex("08 04 00 00 00 00 00 00 00 00 00 00 00 00 00 00 49")
POWER_REPLIES = (
    (bytes.fromhex("08 04 00 00 00 02 49 00 0f 5b 00 00 00 00 00 00 94"), False),
    (bytes.fromhex("08 04 00 00 00 02 49 01 0f 5b 00 00 00 00 00 00 93"), True),
)
DEFERRED_RECEIVERS = (
    (0x3710, (0x5502, 0x5501, 0x5504, 0x5407, 0xF517, 0x5601, 0x5603)),
    (0x3554, (0x5406, 0x5502, 0x5501, 0x5504, 0x5407, 0x5601, 0x5603)),
)


class PulsarHubEvidenceTests(unittest.TestCase):
    def test_existing_request_matches_web_battery_frame_byte_for_byte(self):
        self.assertEqual(bytes(P.make_request()), POWER_REQUEST)
        self.assertEqual(sum(POWER_REQUEST) & 0xFF, 0x55)

    def test_existing_parser_matches_normal_web_fields_on_synthetic_replies(self):
        for frame, charging in POWER_REPLIES:
            with self.subTest(charging=charging):
                self.assertEqual(P.parse_power(frame), (73, charging))
                self.assertEqual(P.voltage_mv(frame), 3931)
                self.assertIsNone(P.parse_power(frame[:-1]))
                damaged = bytearray(frame)
                damaged[-1] ^= 1
                self.assertIsNone(P.parse_power(damaged))

    def test_deferred_receivers_never_open_even_with_guessed_compx_descriptors(self):
        # None of these shapes is evidence about the real receiver. Even the
        # plausible FF02:0002 / 17-byte shape must not grant protocol support.
        for vid, pids in DEFERRED_RECEIVERS:
            for pid in pids:
                for page, usage in ((0xFF02, 2), (0xFF02, 1), (0xFF04, 2), (1, 2)):
                    for length in (None, 0, 16, 17, 18, 64):
                        with self.subTest(vid=vid, pid=pid, collection=(page, usage), length=length):
                            bus = FakeBus(vid, pid, shape=[(1, page, usage)])
                            with patch.object(P.hidlist, "enumerate", side_effect=bus.enumerate), \
                                    patch.object(P, "output_length", return_value=length), \
                                    patch.object(P.hid, "device") as device:
                                self.assertEqual(P.PulsarProvider().poll(), [])
                                device.assert_not_called()
                            self.assertEqual(bus.written(), [])

    def test_deferred_ids_are_not_registered_or_promoted_to_level_c(self):
        index = {(entry["vendorId"], entry["productId"])
                 for entry in build_index()["devices"] if entry["provider"] == "pulsar"}
        for vid, pids in DEFERRED_RECEIVERS:
            for pid in pids:
                with self.subTest(vid=vid, pid=pid):
                    self.assertNotIn((vid, pid), index)
                    self.assertNotIn(pid, P.PIDS[vid])
                    self.assertNotIn((vid, pid), P.DPI_MODELS)
        entry, = [entry for entry in load_catalog()["observedUnsupported"]
                  if entry["id"] == "pulsar_link_8k_5501_observed"]
        self.assertEqual(entry["match"], {"vendorId": "3710", "productId": "5501"})
        self.assertEqual(entry["verificationLevel"], "D")
        self.assertIs(entry["compatible"], False)
        self.assertEqual(entry["capabilities"], [])


if __name__ == "__main__":
    unittest.main()
