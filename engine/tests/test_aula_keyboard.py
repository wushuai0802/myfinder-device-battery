"""Synthetic AULA 05AC:024F frames and caps; no device access or vendor code."""
from contextlib import contextmanager
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
import battery_hid as H
from providers import aula_keyboard as A
from test_new_brands import fixture as _fixture

P = A.AulaKeyboardProvider
REQUEST = [0x00, 0x20, 0x01] + [0] * 29 + [0x21]


def entry(physical="receiver-a", **changes):
    info = dict(vendor_id=0x05AC, product_id=0x024F, usage_page=0xFF60, usage=0x61,
                input_report_length=33, output_report_length=33,
                input_report_sizes={"0": 33}, output_report_sizes={"0": 33},
                path=b"keyboard-33", physical_id=physical, product_string="2.4G Dongle")
    info.update(changes)
    return info


def reply(level=33):
    return [0x20, 0x01, 0x00, level] + [0] * 28


@contextmanager
def fixture(frames=(), inventory=None, **kwargs):
    with _fixture(P, frames, [entry()] if inventory is None else inventory, **kwargs) as bus:
        yield bus


class AulaKeyboardTests(unittest.TestCase):
    def test_byte_exact_request_and_captured_reply(self):
        self.assertEqual(A.make_request(), REQUEST)
        self.assertEqual(sum(REQUEST[:32]) & 0xFF, REQUEST[32])
        # Hardware capture: 20 01 00 21 followed by zero padding, 32 payload bytes.
        self.assertEqual(A.parse_reply(reply(0x21)), (33, False, ""))
        with fixture([reply(0x21)]) as bus:
            status, = P().poll()
            self.assertEqual((status.level, status.charging, status.charging_known),
                             (33, False, False))
            self.assertEqual((status.kind, status.name),
                             ("keyboard", "AULA 2.4G 键盘接收器（05AC:024F）"))
            self.assertEqual(bus.writes, [(entry()["path"], REQUEST)])
            self.assertEqual(bus.reads[0][0], 32)
            self.assertEqual(bus.opened, bus.closed)

    def test_only_the_proven_33_byte_collection_is_opened(self):
        # Same page/usage pair, 65-byte sibling: the receiver answers it with a
        # different command set, so it must never be opened.
        sibling = entry(path=b"keyboard-65", input_report_length=65,
                        output_report_length=65,
                        input_report_sizes={"0": 65}, output_report_sizes={"0": 65})
        for name, inventory in (("sibling", [entry(), sibling]),
                                ("sibling-first", [sibling, entry()])):
            with self.subTest(name), fixture([reply()], inventory) as bus:
                status, = P().poll()
                self.assertEqual(status.level, 33)
                self.assertEqual(bus.opened, [entry()["path"]])
                self.assertEqual(bus.writes, [(entry()["path"], REQUEST)])

    def test_unproven_or_ambiguous_collections_send_nothing(self):
        cases = {
            "unknown sizes": [entry(input_report_sizes=None, output_report_sizes=None)],
            "partial sizes": [entry(input_report_sizes={"0": 33}, output_report_sizes=None)],
            "wrong id": [entry(input_report_sizes={"1": 33}, output_report_sizes={"1": 33})],
            "short": [entry(input_report_length=32, input_report_sizes={"0": 32},
                            output_report_length=32, output_report_sizes={"0": 32})],
            "duplicate": [entry(), entry(path=b"keyboard-b")],
            "nothing": [],
        }
        for name, inventory in cases.items():
            with self.subTest(name), fixture([reply()], inventory) as bus:
                provider = P()
                self.assertEqual(provider.poll(), [])
                if name != "nothing":
                    self.assertTrue(provider.diagnostics())
                self.assertEqual((bus.opened, bus.writes), ([], []))

    def test_invalid_percent_header_and_echo_are_rejected(self):
        invalid = [[], reply()[:-1], reply() + [0], REQUEST, reply()[:4]]
        # Byte 2 is an unknown status byte: every capture carries 0, but its
        # meaning is unproven, so it is not used as a validity gate.
        for offset, value in ((0, 0), (1, 0), (1, 0x21), (3, 0), (3, 101),
                              (3, 255), (3, -1), (3, 256), (3, True)):
            frame = reply()
            frame[offset] = value
            invalid.append(frame)
        for frame in invalid:
            with self.subTest(frame=frame):
                self.assertIsNone(A.parse_reply(frame))
        for level in (1, 33, 100):
            self.assertEqual(A.parse_reply(reply(level)), (level, False, ""))

    def test_silence_keeps_connection_unknown(self):
        for frames, reason in (([], "no valid reply within deadline/read limit"),
                               ([reply(0)], "rejected battery reply")):
            with self.subTest(reason), fixture(frames) as bus:
                provider = P()
                self.assertEqual(provider.poll(), [])
                self.assertIn(reason.split()[0], " ".join(provider.diagnostics()))
                self.assertEqual(bus.opened, bus.closed)

    def test_short_write_or_io_error_never_retries(self):
        with fixture([reply()], write_result=0) as bus:
            provider = P()
            self.assertEqual(provider.poll(), [])
            self.assertIn("short/failed battery query write", " ".join(provider.diagnostics()))
            self.assertEqual(len(bus.writes), 1)
        for error_at in ("open", "write", "read"):
            with self.subTest(error_at), fixture([reply()], error_at=error_at) as bus:
                provider = P()
                self.assertEqual(provider.poll(), [])
                self.assertIn("connection unknown", " ".join(provider.diagnostics()))
                self.assertEqual(bus.opened, bus.closed)
        # A closed handle never leaves the read result behind.
        with fixture([reply()]) as bus:
            status, = P().poll()
            self.assertEqual(status.level, 33)
            self.assertEqual(bus.opened, bus.closed)


if __name__ == "__main__":
    unittest.main()
