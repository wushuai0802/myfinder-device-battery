"""Public Sony captures plus offline USB receiver and bridge regressions."""
import sys
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
from providers import sony_inzone as sony
from server import normalise, recognized_candidate_count, utcnow


def collection(pid, **changes):
    info = dict(vendor_id=sony.VID, product_id=pid, physical_id="usb:sony-one",
                path=b"sony-vendor", interface_number=5, usage_page=0xFF04, usage=1,
                input_report_length=64, output_report_length=64,
                input_report_sizes={"2": 64}, output_report_sizes={"2": 64})
    info.update(changes)
    return info


def capture(hex_bytes):
    return bytes.fromhex(hex_bytes).ljust(64, b"\x00")


BUDS_BOTH = capture("02 12 04 FF 0F 00 96 C3 14 04 10 01 00 00 64 00 64 FF 61 AA")
BUDS_ONE = capture("02 12 04 FF 0F 00 96 C3 14 04 10 01 00 00 FF 00 64 FF 61 45")
H5_CHARGING = capture("02 0E 04 FF 0B 00 96 C3 14 04 A0 01 00 01 64 77")


def for_transaction(sample, tid):
    report = bytearray(sample)
    report[11:13] = tid.to_bytes(2, "little")
    end = report[1] + 1
    report[end] = sum(report[5:end]) & 255
    return bytes(report)


class SonyInzoneTests(unittest.TestCase):
    def run_reader(self, inventory, frames=(), short=False):
        reader = sony.SonyInzoneProvider()
        reader.tid = 7
        handle = MagicMock()
        handle.write.return_value = 63 if short else 64
        handle.read.side_effect = [*frames, []]
        with patch.object(sony.hidlist, "enumerate", return_value=inventory), \
             patch.object(sony.hid, "device", return_value=handle):
            values = reader.poll()
        return values, handle

    def test_public_capture_replay(self):
        self.assertEqual(sony.battery_payload(BUDS_BOTH, 1, 6), bytes.fromhex("00 64 00 64 FF 61"))
        self.assertEqual(sony.battery_payload(BUDS_ONE, 1, 6), bytes.fromhex("00 FF 00 64 FF 61"))
        self.assertEqual(sony.battery_payload(H5_CHARGING, 1, 2), bytes.fromhex("01 64"))

    def test_only_battery_get_and_current_transaction(self):
        sample = for_transaction(BUDS_BOTH, 7)
        devices, handle = self.run_reader([collection(sony.BUDS)], [BUDS_ONE, sample])
        self.assertEqual(handle.read.call_count, 2)
        handle.write.assert_called_once_with(capture("02 0C 01 00 FC 08 96 C3 41 04 01 07 00 A6"))
        handle.close.assert_called_once()
        cards = [normalise(device, None, utcnow()) for device in devices]
        self.assertEqual([card["percent"] for card in cards], [100, 100, 97])
        self.assertEqual(len({card["id"] for card in cards}), 3)
        self.assertEqual(len({card["unitId"] for card in cards}), 3)
        self.assertEqual([card["charging"] for card in cards], [False, False, None])

    def test_unavailable_ear_does_not_reuse_old_percent_or_affect_companions(self):
        devices, _ = self.run_reader([collection(sony.BUDS)], [for_transaction(BUDS_ONE, 7)])
        previous = normalise(sony.SonyInzoneProvider()._devices(sony.BUDS, "usb:sony-one", BUDS_BOTH[13:19])[0],
                             None, utcnow())
        left = normalise(devices[0], previous, utcnow())
        self.assertIsNone(left["percent"])
        self.assertEqual([device.level for device in devices[1:]], [100, 97])

    def test_h5_and_h9ii_each_accept_their_evidenced_collection(self):
        for pid, usage in ((0x0EBF, 1), (0x0EBF, 2), (0x0FA8, 1)):
            with self.subTest(pid=pid, usage=usage):
                devices, handle = self.run_reader([collection(pid, usage=usage)], [for_transaction(H5_CHARGING, 7)])
                self.assertEqual([(d.name, d.level, d.charging) for d in devices], [(sony.PIDS[pid], 100, True)])
                handle.write.assert_called_once()

    def test_unproven_or_ambiguous_endpoint_never_opens(self):
        for entries in ([collection(0x0E53)], [collection(sony.BUDS, usage=2)],
                        [collection(0x0FA8, interface_number=4)], [collection(0x0FA8, usage_page=0xFF13)],
                        [collection(sony.BUDS, physical_id="")], [collection(sony.BUDS, physical_id="collection:path")],
                        [collection(sony.BUDS, input_report_sizes={"3": 64})],
                        [collection(sony.BUDS, output_report_sizes=None)], [collection(sony.BUDS, output_report_length=65)],
                        [collection(0x0EBF), collection(0x0EBF, usage=2, path=b"another")]):
            with self.subTest(entries=entries):
                devices, handle = self.run_reader(entries)
                self.assertEqual(devices, [])
                handle.open_path.assert_not_called()
        self.assertEqual(recognized_candidate_count("sony_inzone", [collection(0x0E53)]), 0)

    def test_malformed_and_stale_replies_are_rejected(self):
        for pos in (0, 1, 2, 3, 4, 5, 6, 8, 9, 10, 11, 19):
            with self.subTest(pos=pos):
                report = bytearray(BUDS_BOTH)
                report[pos] ^= 1
                self.assertIsNone(sony.battery_payload(report, 1, 6))
        self.assertIsNone(sony.battery_payload(BUDS_BOTH[:-1], 1, 6))
        self.assertIsNone(sony.battery_payload(H5_CHARGING, 1, 6))

    def test_silence_and_short_write_yield_no_reading(self):
        for short in (False, True):
            devices, handle = self.run_reader([collection(sony.BUDS)], short=short)
            self.assertEqual(devices, [])
            handle.write.assert_called_once()
            self.assertEqual(handle.read.call_count, 0 if short else 1)
            handle.close.assert_called_once()
