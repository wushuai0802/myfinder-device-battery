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
                path=PATH, physical_id="receiver-a", product_string="fixture",
                feature_report_length=65, feature_report_sizes={"0": 65})
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

    def test_level_boundaries_produce_one_candidate_reading(self):
        for level in (0, 73, 100):
            with self.subTest(level=level), fixture([reply(level)]) as bus:
                status, = P().poll()
            self.assertEqual((status.level, status.kind, status.online), (level, "mouse", True))
            self.assertEqual(status.verification_level, "C")
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
        wrong_id = reply()
        wrong_id[0] = 1
        for frame in ([], reply()[:64], reply() + [0], [0] + reply(), [0] * 65,
                      list(V.make_request()), foreign, wrong_id, reply(255), reply(101),
                      reply(asleep=2)):
            with self.subTest(frame=frame[:6]), fixture([frame]) as bus:
                self.assertIsNone(V.parse_reply(bytes(frame)))
                self.assertEqual(P().poll(), [])
                self.assertEqual(len(bus.writes), 1)
                self.assertEqual(bus.opened, bus.closed)

    def test_short_reply_does_not_trigger_another_request(self):
        with fixture([[], reply(55)]) as bus:
            self.assertEqual(P().poll(), [])
        self.assertEqual(len(bus.writes), 1)
        self.assertEqual(bus.reads, [(0, 65)])

    def test_write_and_read_failures_yield_no_reading(self):
        with fixture([reply()], fail="write") as bus:
            self.assertEqual(P().poll(), [])
        with fixture([reply()], fail="read") as bus:
            self.assertEqual(P().poll(), [])
        with fixture([reply()], write_result=64) as bus:
            self.assertEqual(P().poll(), [])
        self.assertEqual(len(bus.writes), 1)
        self.assertEqual(bus.reads, [])
        self.assertEqual(bus.opened, bus.closed)
        with fixture([reply()], fail="open") as bus:
            self.assertEqual(P().poll(), [])
        self.assertEqual(bus.writes, [])

    def test_other_identities_and_collections_never_open(self):
        changes = [dict(product_id=pid) for pid in (0x1A01, 0x1A02, 0x1001, 0x1A25, 0x1A2B, 0x2A01, 0x1A04F)]
        changes += [dict(vendor_id=0x3554), dict(usage_page=0xFFFF, usage=1),
                    dict(usage_page=0xFF01, usage=0x10), dict(path=b"")]
        for change in changes:
            with self.subTest(change=change), fixture([reply()], [entry(**change)]) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(bus.opened, [])

    def test_feature_proof_is_required_before_opening(self):
        cases = [
            dict(feature_report_length=None),
            dict(feature_report_length=64),
            dict(feature_report_sizes=None),
            dict(feature_report_sizes={"1": 65}),
            dict(feature_report_sizes={"0": 65, "1": 65}),
        ]
        for change in cases:
            with self.subTest(change=change), fixture([reply()], [entry(**change)]) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(bus.opened, [])
                self.assertEqual(bus.writes, [])

    def test_vendor_listed_wireless_receivers_share_the_report_zero_query(self):
        expected = {
            0x1A08: "VGN Dragonfly Y2 V2 Pro Max",
            0x1A13: "VGN Dragonfly F1 V2 Pro Max",
            0x1A25: "VGN Dragonfly 3 Ultra",
            0x1A2B: "VGN Dragonfly F1 V5 Ultra",
            0x1A41: "VGN Dragonfly F2 V2 Ultra",
        }
        for pid, name in expected.items():
            path = (rf"\\?\hid#vid_391d&pid_{pid:04x}&mi_01&col01#synthetic").encode()
            target = entry(product_id=pid, interface_number=1, path=path)
            with self.subTest(pid=pid), fixture([reply(62)], [target]) as bus:
                status, = P().poll()
                self.assertEqual((status.name, status.level, status.verification_level), (name, 62, "C"))
                self.assertEqual(bus.writes, [(path, list(V.make_request()))])
                self.assertEqual(bus.opened, bus.closed)

    def test_new_receivers_refuse_sibling_paths_or_missing_report_proof(self):
        target = entry(product_id=0x1A25, interface_number=1,
                       path=br"\\?\hid#vid_391d&pid_1a25&mi_01&col01#synthetic")
        for changes in (
            {"interface_number": 2},
            {"path": target["path"].replace(b"col01", b"col02")},
            {"path": target["path"].replace(b"col01", b"col010")},
            {"path": b"not-the-collection#also-not-the-collection#&mi_01&col01"},
            {"product_id": 0x1099},
            {"feature_report_sizes": None},
        ):
            with self.subTest(changes=changes), fixture([reply()], [dict(target, **changes)]) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(bus.opened, [])
                self.assertEqual(bus.writes, [])

    def test_wired_ry_ms_mice_use_0x8a_query_and_payload_index_1(self):
        request = V.make_request(wired=True)
        self.assertEqual(len(request), 65)
        self.assertEqual(list(request[:9]), [0x00, 0x8A, 0, 0, 0, 0, 0, 0, 0x75])
        self.assertEqual(set(request[9:]), {0})

        expected = {
            0x1004: "VGN 蜻蜓3 Pro Max",
            0x1008: "VGN Dragonfly Y2 V2 Pro Max",
            0x1015: "VGN Dragonfly F1 V2 Pro Max",
            0x1028: "VGN Dragonfly 3 Ultra",
            0x102F: "VGN Dragonfly F1 V5 Ultra",
            0x1048: "VGN Dragonfly F2 V2 Ultra",
        }
        wired_reply = [0x00, 0x8A, 84] + [0] * 62
        for pid, name in expected.items():
            path = rf"\\?\hid#vid_391d&pid_{pid:04x}&mi_01&col01#synthetic".encode()
            target = entry(product_id=pid, interface_number=1, path=path)
            with self.subTest(pid=pid), fixture([wired_reply], [target]) as bus:
                status, = P().poll()
                self.assertEqual((status.name, status.level, status.online, status.verification_level),
                                 (name, 84, True, "C"))
                self.assertFalse(status.charging_known)
                self.assertEqual(bus.writes, [(path, list(request))])
                self.assertEqual(bus.opened, bus.closed)

    def test_wired_ry_ms_rejects_echo_zero_and_mismatched_paths(self):
        path = br"\\?\hid#vid_391d&pid_1004&mi_01&col01#synthetic"
        target = entry(product_id=0x1004, interface_number=1, path=path)
        for bad_reply in (
            list(V.make_request(wired=True)),
            list(V.make_request(wired=True)[:9]) + [0x55] + [0] * 55,
            [0] * 65,
            [0x00, 0x90, 80] + [0] * 62,  # unrelated command header (e.g. DPI)
            [0x00, 0x8A, 0] + [0] * 62,
            [0x00, 0x8A, 101] + [0] * 62,
            [0x01, 0x8A, 80] + [0] * 62,
        ):
            with self.subTest(bad_reply=bad_reply[:4]), fixture([bad_reply], [target]):
                self.assertEqual(P().poll(), [])
        for bad_entry in (
            entry(product_id=0x1004, interface_number=2, path=path),
            entry(product_id=0x1004, interface_number=1,
                  path=br"\\?\hid#vid_391d&pid_1008&mi_01&col01#synthetic"),
            entry(product_id=0x1004, interface_number=1,
                  path=br"\\?\hid#vid_391d&pid_1004&mi_01&col02#synthetic"),
        ):
            with self.subTest(bad_entry=bad_entry["path"]), fixture([[0x00, 0x8A, 80] + [0] * 62], [bad_entry]) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(bus.opened, [])

    def test_duplicate_and_unresolved_collections_never_open(self):
        inventories = ([entry(), entry(path=b"duplicate")],
                       [entry(physical_id="collection:unknown")],
                       [entry(physical_id="")],
                       [entry(physical_id=None)])
        for inventory in inventories:
            with self.subTest(inventory=inventory), fixture([reply()], inventory) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(bus.opened, [])


if __name__ == "__main__":
    unittest.main()
