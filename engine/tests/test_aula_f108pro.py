"""Synthetic AULA F108 Pro 2.4 GHz readonly battery fixtures."""
from contextlib import contextmanager
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ENGINE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ENGINE))
sys.path.insert(0, str(ENGINE / "tests"))
import _hidstub  # noqa: F401
import battery_hid as H
from providers import aula_keyboard as F
from test_new_brands import FakeHid


def entry(physical="f108-receiver", **changes):
    info = dict(vendor_id=F.F108_VID, product_id=F.F108_PID,
                product_string=F.F108_PRODUCT, interface_number=3,
                usage_page=0xFF60, usage=0x0061,
                input_report_length=33, output_report_length=33,
                input_report_sizes={"0": 33}, output_report_sizes={"0": 33},
                physical_id=physical, path=(physical + "-ff60").encode(),
                serial_number="synthetic-private-serial")
    info.update(changes)
    return info


def reply(level=68):
    payload = [0x20, 0x01, 0x00, level] + [0] * 27
    payload.append(sum(payload) & 0xFF)
    return payload


@contextmanager
def fixture(frames=(), inventory=None, **kwargs):
    bus = FakeHid(frames, **kwargs)
    with patch.object(H, "hid", bus), \
            patch.object(H.hidlist, "enumerate",
                         return_value=[entry()] if inventory is None else inventory), \
            patch.object(H, "time", SimpleNamespace(monotonic=lambda: bus.now)):
        yield bus


class AulaF108ProTests(unittest.TestCase):
    def test_public_owner_reads_one_valid_packet(self):
        with fixture([reply()]) as bus:
            statuses = F.AulaKeyboardProvider().poll()
        status, = statuses
        self.assertEqual((status.name, status.level, status.kind, status.source),
                         ("AULA F108 Pro", 68, "keyboard", "aula_keyboard"))
        self.assertFalse(status.charging_known)
        self.assertEqual([packet for _, packet in bus.writes],
                         [F.F108_REQUEST])
        self.assertEqual(bus.opened, bus.closed)

    def test_sleep_echo_zero_and_malformed_packets_are_ignored(self):
        echo = F.F108_REQUEST[1:]
        sleep = [0x20, 0x01, 0x00, 0] + [0] * 27
        sleep.append(sum(sleep) & 0xFF)
        malformed = [0] * 31
        with fixture([sleep, echo, [0] * 32, malformed, reply(42)]) as bus:
            status, = F.AulaKeyboardProvider().poll()
        self.assertEqual(status.level, 42)
        self.assertEqual(len(bus.writes), 1)

    def test_checksum_and_length_fail_closed(self):
        bad_checksum = reply()
        bad_checksum[-1] ^= 1
        for frame in (bad_checksum, reply()[:-1]):
            with self.subTest(frame=frame), fixture([frame]) as bus:
                self.assertEqual(F.AulaKeyboardProvider().poll(), [])
                self.assertEqual(len(bus.writes), 1)
                self.assertEqual(bus.opened, bus.closed)

    def test_wrong_product_parent_and_collection_proofs_never_write(self):
        cases = [
            [entry(product_string="F108 Pro Dongle")],
            [entry(physical="")],
            [entry(physical="collection:unknown")],
            [entry(), entry(path=b"duplicate")],
            [entry(input_report_sizes=None)],
            [entry(output_report_sizes=None)],
            [entry(input_report_sizes={"0": 32})],
            [entry(output_report_sizes={"0": 33, "1": 33})],
            [entry(input_report_length=32)],
            [entry(output_report_length=32)],
        ]
        for inventory in cases:
            with self.subTest(inventory=inventory), fixture([reply()], inventory) as bus:
                self.assertEqual(F.AulaKeyboardProvider().poll(), [])
                self.assertEqual((bus.opened, bus.writes), ([], []))

    def test_silence_has_no_output_retry(self):
        with fixture([]) as bus:
            self.assertEqual(F.AulaKeyboardProvider().poll(), [])
        self.assertEqual(len(bus.writes), 1)
        self.assertEqual(bus.writes[0][1], F.F108_REQUEST)

    def test_f99_protocol_is_not_sent_to_f108_product(self):
        with fixture([reply()], [entry()]) as bus:
            F.AulaKeyboardProvider().poll()
        self.assertEqual([packet for _, packet in bus.writes],
                         [F.F108_REQUEST])


if __name__ == "__main__":
    unittest.main()
