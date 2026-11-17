"""Synthetic feature-report fixtures derived from VGN HUB's ryMs driver."""
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
from providers import vgn_ry_ms as V

P = V.VgnRyMsProvider
PATH = b"receiver-vendor"


def entry(**changes):
    info = dict(vendor_id=V.VID, product_id=0x1A04,
                usage_page=V.COLLECTION[0], usage=V.COLLECTION[1],
                path=PATH, physical_id="receiver-a", product_string="fixture")
    info.update(changes)
    return info


def reply(level=73, asleep=0, prefix=0):
    # HUB reads payload[2] as the level and payload[4] as the sleep flag.
    return [0x00, prefix, 0x00, level, 0x01, asleep, 0x01, 0x02] + [0] * 57


class FakeHid:
    def __init__(self, replies=(), write_result=None, fail=None):
        self.replies = list(replies)
        self.write_result, self.fail = write_result, fail
        self.opened, self.writes, self.reads, self.closed = [], [], [], []

    def enumerate(self, *args, **kwargs):
        return [entry()]

    def device(self):
        bus = self

        class Handle:
            def open_path(self, path):
                self.path = path
                bus.opened.append(path)
                if bus.fail == "open":
                    raise OSError("private-path")

            def send_feature_report(self, packet):
                bus.writes.append((self.path, list(packet)))
                if bus.fail == "write":
                    raise OSError("private-path")
                return len(packet) if bus.write_result is None else bus.write_result

            def get_feature_report(self, report_id, length):
                bus.reads.append((report_id, length))
                if bus.fail == "read":
                    raise OSError("private-path")
                return bus.replies.pop(0) if bus.replies else []

            def close(self):
                bus.closed.append(self.path)

        return Handle()


def fixture(replies=(), inventory=None, **kwargs):
    class Ctx:
        def __enter__(self):
            self.bus = FakeHid(replies, **kwargs)
            self.patches = [
                patch.object(V, "hid", self.bus),
                patch.object(V.hidlist, "enumerate",
                             return_value=[entry()] if inventory is None else inventory),
                patch.object(V, "time", SimpleNamespace(sleep=lambda _seconds: None)),
            ]
            for item in self.patches:
                item.start()
            return self.bus

        def __exit__(self, *exc):
            for item in reversed(self.patches):
                item.stop()
            return False

    return Ctx()


class VgnRyMsTests(unittest.TestCase):
    def test_request_uses_hub_framing_and_checksum(self):
        request = V.make_request()
        self.assertEqual(len(request), 65)
        self.assertEqual(list(request[:9]), [0x00, 0xF7, 0, 0, 0, 0, 0, 0, 0x08])
        self.assertEqual(set(request[9:]), {0})
        self.assertEqual(V.checksum([0xF7, 0, 0, 0, 0, 0, 0]), 0x08)

    def test_level_boundaries_produce_one_verified_reading(self):
        for level in (0, 73, 100):
            with self.subTest(level=level), fixture([reply(level)]) as bus:
                status, = P().poll()
            self.assertEqual((status.level, status.kind, status.online), (level, "mouse", True))
            self.assertEqual(status.verification_level, "A")
            self.assertFalse(status.charging_known)
            self.assertFalse(status.wake_on_input)
            self.assertEqual(bus.writes, [(PATH, list(V.make_request()))])
            self.assertEqual(bus.reads, [(0, 65)])
            self.assertEqual(bus.opened, bus.closed)

    def test_sleeping_receiver_reports_no_level_and_wakes_on_input(self):
        with fixture([reply(73, asleep=1)]) as bus:
            status, = P().poll()
        self.assertIsNone(status.level)
        self.assertFalse(status.online)
        self.assertTrue(status.level_unavailable)
        self.assertTrue(status.wake_on_input)
        self.assertEqual(len(bus.writes), 1)
        self.assertEqual(bus.opened, bus.closed)

    def test_invalid_replies_are_rejected_without_a_reading(self):
        foreign = reply()
        foreign[1] = 0x11
        for frame in ([], reply()[:64], reply() + [0], [0] + reply(),
                      list(V.make_request()), foreign, reply(255), reply(101),
                      reply(asleep=2)):
            with self.subTest(frame=frame[:6]), fixture([frame] * V.READ_ATTEMPTS) as bus:
                self.assertIsNone(V.parse_reply(bytes(frame)))
                self.assertEqual(P().poll(), [])
                self.assertEqual(len(bus.writes), V.READ_ATTEMPTS)
                self.assertEqual(bus.opened, bus.closed)

    def test_retries_a_short_reply_then_succeeds(self):
        with fixture([[], reply(55)]) as bus:
            status, = P().poll()
        self.assertEqual(status.level, 55)
        self.assertEqual(len(bus.writes), 2)

    def test_write_and_read_failures_yield_no_reading(self):
        with fixture([reply()], fail="write") as bus:
            self.assertEqual(P().poll(), [])
        with fixture([reply()], fail="read") as bus:
            self.assertEqual(P().poll(), [])
        with fixture([reply()], write_result=64) as bus:
            self.assertEqual(P().poll(), [])
        with fixture([reply()], fail="open") as bus:
            self.assertEqual(P().poll(), [])
        self.assertEqual(bus.writes, [])

    def test_other_identities_and_collections_never_open(self):
        changes = [dict(product_id=pid) for pid in (0x1A01, 0x1A02, 0x2A01, 0x1A04F)]
        changes += [dict(vendor_id=0x3554), dict(usage_page=0xFFFF, usage=1),
                    dict(usage_page=0xFF01, usage=0x10), dict(path=b"")]
        for change in changes:
            with self.subTest(change=change), fixture([reply()], [entry(**change)]) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(bus.opened, [])

    def test_duplicate_and_unresolved_collections_never_open(self):
        inventories = ([entry(), entry(path=b"duplicate")],
                       [entry(physical_id="collection:unknown")])
        for inventory in inventories:
            with self.subTest(inventory=inventory), fixture([reply()], inventory) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(bus.opened, [])


if __name__ == "__main__":
    unittest.main()
