"""Tests for providers/cherry.py. No hardware is needed.

The fake receiver answers the documented read-only transaction: a 64-byte output
report `04 20 00 1A 06 ...` is followed by a 64-byte input report whose byte 1 is
0x20, byte 8 is the level and byte 9 the charging flag. The receiver's 0xAA
heartbeat must never be parsed as a level.

Run from the repository root:

    python -m unittest discover -s engine/tests
"""
import os
import sys
import types
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import _hidstub  # noqa: E402, F401

from providers import cherry as C  # noqa: E402


def reply(level=81, charging=False):
    report = [0x00] * C.REPORT_SIZE
    report[0:5] = [0x04, C.MSG_STATUS, 0x00, 0x1A, 0x06]
    report[C.LEVEL_OFFSET] = level
    report[C.CHARGING_OFFSET] = 0x01 if charging else 0x00
    return report


def heartbeat(level=0xFF):
    report = [0x00] * C.REPORT_SIZE
    report[0:4] = [0x04, 0xAA, 0x00, 0xAA]
    report[C.LEVEL_OFFSET] = level
    return report


class FakeReceiver:
    def __init__(self, replies=(), silent=False):
        self.pending = [list(r) for r in replies]
        self.queue = []
        self.writes = []
        self.silent = silent

    def on_write(self, data):
        self.writes.append(list(data))
        if not self.silent:
            self.queue.extend(list(r) for r in self.pending)

    def on_read(self):
        return self.queue.pop(0) if self.queue else []


class FakeBus:
    def __init__(self, receivers):
        self.receivers = receivers

    def device_class(self):
        bus = self.receivers

        class FakeDevice:
            def open_path(self, path):
                self.r = bus[path]

            def write(self, data):
                self.r.on_write(data)
                return len(data)

            def read(self, n, timeout=None):
                return self.r.on_read()

            def close(self):
                pass

        return FakeDevice


class CherryTests(unittest.TestCase):
    def patch(self, infos, receivers):
        self.saved_hid = C.hid
        self.saved_enumerate = C.hidlist.enumerate
        C.hid = types.SimpleNamespace(device=FakeBus(receivers).device_class())
        C.hidlist.enumerate = lambda vid=0: [dict(i) for i in infos]

    def setUp(self):
        # Keep the reply windows short so a silent device does not stall the test.
        self.saved_timing = (C.REPLY_WINDOW, C.READ_TIMEOUT_MS, C.FLUSH_TIMEOUT_MS)
        C.REPLY_WINDOW, C.READ_TIMEOUT_MS, C.FLUSH_TIMEOUT_MS = 0.05, 5, 0

    def tearDown(self):
        C.REPLY_WINDOW, C.READ_TIMEOUT_MS, C.FLUSH_TIMEOUT_MS = self.saved_timing
        if hasattr(self, "saved_hid"):
            C.hid = self.saved_hid
            C.hidlist.enumerate = self.saved_enumerate

    def dongle(self, pid=0x01E0, usage_page=C.USAGE_PAGE, path=b"p1"):
        return {"path": path, "vendor_id": C.CHERRY_VID, "product_id": pid,
                "usage_page": usage_page, "usage": 0x0092, "interface_number": 1}

    def test_reads_level_and_charging(self):
        receiver = FakeReceiver([reply(81)])
        self.patch([self.dongle()], {b"p1": receiver})
        statuses = C.CherryProvider().poll()
        self.assertEqual(len(statuses), 1)
        self.assertEqual(statuses[0].level, 81)
        self.assertFalse(statuses[0].charging)
        self.assertEqual(statuses[0].kind, "keyboard")
        self.assertEqual(statuses[0].source, "cherry")
        self.assertEqual(statuses[0].name, "CHERRY MX 2.0S Wireless PRO")
        self.assertEqual(receiver.writes, [C.make_request()])

    def test_charging_flag_comes_from_byte_nine(self):
        self.patch([self.dongle()], {b"p1": FakeReceiver([reply(42, charging=True)])})
        statuses = C.CherryProvider().poll()
        self.assertEqual(statuses[0].charging, True)

    def test_heartbeat_is_not_a_percentage(self):
        self.patch([self.dongle()], {b"p1": FakeReceiver([heartbeat()])})
        self.assertEqual(C.CherryProvider().poll(), [])

    def test_heartbeat_before_reply_is_skipped(self):
        receiver = FakeReceiver([heartbeat(), heartbeat(), reply(100)])
        self.patch([self.dongle()], {b"p1": receiver})
        statuses = C.CherryProvider().poll()
        self.assertEqual(len(statuses), 1)
        self.assertEqual(statuses[0].level, 100)

    def test_out_of_range_level_is_rejected(self):
        self.patch([self.dongle()], {b"p1": FakeReceiver([reply(255)])})
        self.assertEqual(C.CherryProvider().poll(), [])

    def test_silent_receiver_reports_nothing(self):
        self.patch([self.dongle()], {b"p1": FakeReceiver(silent=True)})
        self.assertEqual(C.CherryProvider().poll(), [])

    def test_unknown_pid_is_never_queried(self):
        receiver = FakeReceiver([reply(70)])
        self.patch([self.dongle(pid=0x0FAC)], {b"p1": receiver})
        self.assertEqual(C.CherryProvider().poll(), [])
        self.assertEqual(receiver.writes, [])

    def test_collection_must_declare_the_vendor_page(self):
        receiver = FakeReceiver([reply(70)])
        self.patch([self.dongle(usage_page=0x0001)], {b"p1": receiver})
        self.assertEqual(C.CherryProvider().poll(), [])
        self.assertEqual(receiver.writes, [])

    def test_two_receivers_are_read_once_each(self):
        receiver_a = FakeReceiver([reply(60)])
        receiver_b = FakeReceiver([reply(61)])
        self.patch([self.dongle(pid=0x01E0, path=b"p1"), self.dongle(pid=0x01AC, path=b"p2")],
                   {b"p1": receiver_a, b"p2": receiver_b})
        statuses = C.CherryProvider().poll()
        self.assertEqual(sorted(s.level for s in statuses), [60, 61])
        self.assertEqual(sorted(s.key for s in statuses), ["cherry:01ac", "cherry:01e0"])


if __name__ == "__main__":
    unittest.main()
