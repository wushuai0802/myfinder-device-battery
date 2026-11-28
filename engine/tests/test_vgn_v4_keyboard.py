"""Synthetic feature-report tests for VGN V98Pro V4 (bykeyboard 521B Feature 0)."""
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
from providers import vgn_v4_keyboard as V

P = V.VgnV4KeyboardProvider


def entry(pid=0x2A03, **changes):
    info = dict(
        vendor_id=V.VID,
        product_id=pid,
        usage_page=0xFF60,
        usage=0x0061,
        path=rf"\\?\hid#vid_391d&pid_{pid:04x}&mi_03#synthetic".encode(),
        physical_id=rf"pnp:usb\vid_391d&pid_{pid:04x}\0001",
        product_string="VGN V98Pro V4 8K 2.4G Dongle",
        feature_report_length=521,
        feature_report_sizes={"0": 521},
    )
    info.update(changes)
    return info


def reply(level=85, header=0x87):
    buf = [0] * 521
    buf[0] = 0x00
    buf[1] = header
    buf[9] = level
    return buf


class FakeHid:
    def __init__(self, replies=(), write_result=None, fail=None):
        self.replies = list(replies)
        self.write_result, self.fail = write_result, fail
        self.opened, self.writes, self.reads, self.closed = [], [], [], []

    def device(self):
        bus = self

        class Handle:
            def open_path(self, path):
                self.path = path
                bus.opened.append(path)
                if bus.fail == "open":
                    raise OSError("open-fail")

            def send_feature_report(self, packet):
                bus.writes.append((self.path, list(packet)))
                if bus.fail == "write":
                    raise OSError("write-fail")
                return len(packet) if bus.write_result is None else bus.write_result

            def get_feature_report(self, report_id, length):
                bus.reads.append((report_id, length))
                if bus.fail == "read":
                    raise OSError("read-fail")
                return bus.replies.pop(0) if bus.replies else []

            def close(self):
                bus.closed.append(self.path)

        return Handle()


def fixture(replies=(), inventory=None, **kwargs):
    inv = [entry()] if inventory is None else inventory

    class Ctx:
        def __enter__(self):
            self.bus = FakeHid(replies, **kwargs)
            self.patches = [
                patch.object(V, "hid", self.bus),
                patch.object(V.hidlist, "enumerate", return_value=inv),
                patch.object(V, "time", SimpleNamespace(sleep=lambda _s: None)),
            ]
            for item in self.patches:
                item.start()
            return self.bus

        def __exit__(self, *exc):
            for item in reversed(self.patches):
                item.stop()
            return False

    return Ctx()


class VgnV4KeyboardTests(unittest.TestCase):
    def test_request_frame_matches_vgnhub_520b_feature_payload(self):
        req = V.make_request()
        self.assertEqual(len(req), 521)
        self.assertEqual(list(req[:8]), [0x00, 0x87, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00])
        self.assertEqual(set(req[8:]), {0})

    def test_supported_pids_and_valid_header_produce_reading(self):
        for pid, name in V.PIDS.items():
            for physical in (
                rf"pnp:usb\vid_391d&pid_{pid:04x}\0001",
                rf"pnp:usb\vid_391d&pid_{pid:04x}&rev_0100\0001",
            ):
                with self.subTest(pid=pid, physical=physical), fixture(
                        [reply(72, header=0x87)], [entry(pid, physical_id=physical)]) as bus:
                    status, = P().poll()
                self.assertEqual((status.name, status.level, status.kind, status.online),
                                 (name, 72, "keyboard", True))
                self.assertFalse(status.charging_known)
                self.assertTrue(status.connection_known)
                self.assertEqual(status.verification_level, "C")
                self.assertEqual(bus.reads, [(0, 521)])
                self.assertEqual(bus.opened, bus.closed)

    def test_invalid_replies_and_unrelated_commands_rejected(self):
        wrong_id = reply(80)
        wrong_id[0] = 1
        for bad in (
            [],
            [0] * 521,
            list(V.make_request()),
            reply(0),
            reply(101),
            reply(80, header=0x00),  # zero-prefixed stale buffer
            reply(80, header=0x07),  # unproven header
            reply(80, header=0x83),  # defKey command header
            reply(80, header=0x84),  # readProfileBuffer header
            wrong_id,
            reply(80)[:520],
        ):
            with self.subTest(bad=bad[:10]), fixture([bad]) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(len(bus.writes), 1)
                self.assertEqual(bus.opened, bus.closed)

    def test_unproven_descriptors_and_wrong_collections_never_open(self):
        for bad_inv in (
            [entry(product_id=0x2A01)],  # vgn_beiying, not v4
            [entry(usage_page=0xFF00, usage=0x0001)],
            [entry(feature_report_length=65, feature_report_sizes={"0": 65})],
            [entry(feature_report_length=521, feature_report_sizes=None)],
            [entry(feature_report_length=521, feature_report_sizes={"1": 521})],
            [entry(physical_id="collection:unknown")],
            [entry(physical_id=r"pnp:usb\vid_391d&pid_2a04\0001")],  # mismatched PID parent
            [entry(), entry(path=b"dup")],
        ):
            with self.subTest(bad=bad_inv[0].get("product_id")), fixture([reply(80)], bad_inv) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(bus.opened, [])
                self.assertEqual(bus.writes, [])

    def test_io_and_short_write_failures_never_retry(self):
        with fixture([reply(80)], write_result=520) as bus:
            self.assertEqual(P().poll(), [])
        self.assertEqual(len(bus.writes), 1)
        self.assertEqual(bus.reads, [])
        self.assertEqual(bus.opened, bus.closed)

        for fail in ("open", "write", "read"):
            with self.subTest(fail=fail), fixture([reply(80)], fail=fail) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(bus.opened, bus.closed)


if __name__ == "__main__":
    unittest.main()
