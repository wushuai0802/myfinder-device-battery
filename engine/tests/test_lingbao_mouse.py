"""Synthetic 088D:052E/052F Feature-0F fixtures; no device access or vendor code."""
from contextlib import contextmanager
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
import battery_hid as H
from providers import lingbao_mouse as L

P = L.LingbaoMouseProvider
REQUEST = [0x0F, 0x01, 0x30, 0x00, 0x01, 0x01] + [0] * 27
WIRED, RECEIVER = b"mouse-wired", b"mouse-receiver"
# Hardware capture while the receiver reported 100 % over 2.4 GHz.
CAPTURE = bytes.fromhex(
    "0F 01 30 00 01 01 10 4D 35 33 36 01 06 4C 58 11 10 01 64 00 FF 01 FF D0 "
    "00 0C 31 79 00 5B 81 2C C8")


def entry(pid=0x052F, physical="receiver-a", **changes):
    info = dict(vendor_id=0x088D, product_id=pid, usage_page=0xFF00, usage=0x01,
                path=WIRED if pid == 0x052E else RECEIVER,
                physical_id=physical, product_string="Wireless-Receiver",
                feature_report_length=33, feature_report_sizes={"15": 33},
                input_report_length=0, output_report_length=0,
                input_report_sizes={}, output_report_sizes={})
    info.update(changes)
    return info


def reply(level=100):
    frame = bytearray(CAPTURE)
    frame[18] = level
    return list(frame)


@contextmanager
def fixture(frames=(), inventory=None, *, short=False, error_at=None, by_path=None):
    bus = SimpleNamespace(queues=dict(by_path or {}), opened=[], closed=[], writes=[],
                          reads=[], sleeps=[])
    if not bus.queues:
        bus.queues[RECEIVER] = list(frames)
    infos = [entry()] if inventory is None else inventory

    class Handle:
        def open_path(self, path):
            self.path = path
            bus.opened.append(path)
            if error_at == "open":
                raise OSError("private-device-path")

        def send_feature_report(self, frame):
            # Strict allowlist: any other request fails the test even in feature form.
            assert list(frame) == REQUEST, "unexpected device command"
            bus.writes.append((self.path, list(frame)))
            if error_at == "write":
                raise OSError("private-device-path")
            return len(frame) - int(short)

        def get_feature_report(self, report_id, length):
            assert (report_id, length) == (0x0F, 33), "unexpected feature query"
            bus.reads.append((self.path, report_id, length))
            if error_at == "read":
                raise OSError("private-device-path")
            queue = bus.queues.setdefault(self.path, [])
            return queue.pop(0) if queue else []

        def close(self):
            bus.closed.append(self.path)

        # No write/read output path: this reader is feature-only.

    bus.device = Handle
    with patch.object(H, "hid", bus), \
            patch.object(H.hidlist, "enumerate", return_value=infos), \
            patch.object(L, "time", SimpleNamespace(sleep=lambda delay: bus.sleeps.append(delay))):
        yield bus


class LingbaoMouseTests(unittest.TestCase):
    def test_byte_exact_request_and_captured_reply(self):
        self.assertEqual(L.make_request(), REQUEST)
        self.assertEqual(L.parse_reply(list(CAPTURE)), (100, False, ""))
        with fixture([reply(96)]) as bus:
            status, = P().poll()
            self.assertEqual((status.level, status.charging, status.charging_known),
                             (96, False, False))
            self.assertEqual((status.kind, status.name), ("mouse", "凌豹 M1Pro 鼠标"))
            self.assertEqual(bus.writes, [(RECEIVER, REQUEST)])
            self.assertEqual(bus.reads, [(RECEIVER, 0x0F, 33)])
            self.assertEqual(bus.sleeps, [0.100])
            self.assertEqual(bus.opened, bus.closed)

    def test_wired_identity_is_preferred_and_suppresses_the_receiver_card(self):
        inventory = [entry(0x052E), entry(0x052F, physical="receiver-b")]
        with fixture(inventory=inventory, by_path={WIRED: [reply(100)]}) as bus:
            status, = P().poll()
            self.assertEqual(status.level, 100)
            self.assertEqual(bus.opened, [WIRED])
            self.assertEqual(bus.writes, [(WIRED, REQUEST)])

    def test_receiver_is_used_when_the_cable_stays_silent(self):
        inventory = [entry(0x052E), entry(0x052F, physical="receiver-b")]
        with fixture(inventory=inventory, by_path={RECEIVER: [reply(88)]}) as bus:
            provider = P()
            status, = provider.poll()
            self.assertEqual(status.level, 88)
            self.assertEqual(bus.opened, [WIRED, RECEIVER])
            self.assertEqual([path for path, _ in bus.writes], [WIRED, RECEIVER])
            # The silent cable is not reported as a fault once the receiver answers.
            self.assertEqual(provider.diagnostics(), [])

    def test_unproven_feature_sizes_send_nothing(self):
        cases = {
            "unknown": [entry(feature_report_sizes=None)],
            "short": [entry(feature_report_length=32, feature_report_sizes={"15": 32})],
            "other id": [entry(feature_report_sizes={"6": 33})],
            "two": [entry(), entry(path=b"mouse-b")],
            "empty": [],
        }
        for name, inventory in cases.items():
            with self.subTest(name), fixture([reply()], inventory) as bus:
                provider = P()
                self.assertEqual(provider.poll(), [])
                if name != "empty":
                    self.assertTrue(provider.diagnostics())
                self.assertEqual((bus.opened, bus.writes), ([], []))

    def test_reply_gates_on_framing_model_and_percent(self):
        invalid = [b"", CAPTURE[:-1], CAPTURE + b"\x00"]
        for offset, value in ((0, 0x0E), (1, 0x00), (2, 0x31), (7, 0x4E), (10, 0x37),
                              (18, 0), (18, 101), (18, 255)):
            frame = bytearray(CAPTURE)
            frame[offset] = value
            invalid.append(bytes(frame))
        for frame in invalid:
            with self.subTest(frame=frame.hex()):
                self.assertIsNone(L.parse_reply(list(frame)))
        # A byte buffer cannot hold a bool; check the value gate directly too.
        for level in (0, 1, 96, 100, 101, -1, 256, True, None, "100"):
            frame = reply(level if type(level) is int and 1 <= level <= 100 else 96)
            frame[18] = level
            with self.subTest(level=level):
                self.assertEqual(L.parse_reply(frame),
                                 (level, False, "") if type(level) is int and 1 <= level <= 100 else None)

    def test_silence_short_write_and_io_errors_never_retry(self):
        with fixture([]) as bus:
            provider = P()
            self.assertEqual(provider.poll(), [])
            self.assertIn("no valid battery reply", " ".join(provider.diagnostics()))
            self.assertEqual(len(bus.writes), 1)
        with fixture([reply()], short=True) as bus:
            provider = P()
            self.assertEqual(provider.poll(), [])
            self.assertIn("short/failed battery query write", " ".join(provider.diagnostics()))
            self.assertEqual(bus.reads, [])
        for error_at in ("open", "write", "read"):
            with self.subTest(error_at), fixture([reply()], error_at=error_at) as bus:
                provider = P()
                self.assertEqual(provider.poll(), [])
                self.assertIn("connection unknown", " ".join(provider.diagnostics()))
                self.assertEqual(bus.opened, bus.closed)


if __name__ == "__main__":
    unittest.main()
