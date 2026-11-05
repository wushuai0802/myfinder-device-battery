"""Synthetic GM01 Pro protocol fixtures; no vendor JavaScript or real HID."""
from contextlib import contextmanager
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
import battery_hid as H
from providers import moondrop as M

P = M.MoondropProvider


def entry(**changes):
    # 64 is illustrative, NOT a claim about the receiver's real descriptor.
    info = dict(vendor_id=0x35D8, product_id=0x0133, usage_page=0xFF02, usage=2,
                path=b"gm01-ff02", physical_id="receiver-a", product_string="MOONDROP GM01 Pro",
                input_report_length=64, output_report_length=64,
                input_report_sizes={"75": 64}, output_report_sizes={"75": 64})
    info.update(changes)
    return info


def reply(level=73, valid=1, length=64):
    # Response OP is deliberately arbitrary: the Hub does not validate it.
    return [0x4B, 0x00, 0x27, 6, 0, 0, 0, 0, valid, level] + [0] * (length - 10)


@contextmanager
def fixture(frames=(), inventory=None, *, short=False, error_at=None):
    bus = SimpleNamespace(frames=list(frames), opened=[], closed=[], writes=[], reads=[])
    infos = [entry()] if inventory is None else inventory

    class Handle:
        def open_path(self, path):
            self.path = path
            bus.opened.append(path)
            self.info = next(i for i in infos if i["path"] == path)
            if error_at == "open":
                raise OSError("private-device-path")

        def write(self, frame):
            expected = [0x4B, 0x80, 0x27, 0] + [0] * (self.info["output_report_length"] - 4)
            # Strict wire allowlist: no ping, flash, EQ, control or OTA traffic.
            assert list(frame) == expected, "non-battery device command"
            bus.writes.append(list(frame))
            if error_at == "write":
                raise OSError("private-device-path")
            return len(frame) - int(short)

        def read(self, length, timeout):
            assert length == self.info["input_report_length"]
            assert 1 <= timeout <= P.read_timeout_ms
            bus.reads.append((length, timeout))
            if error_at == "read":
                raise OSError("private-device-path")
            return bus.frames.pop(0) if bus.frames else []

        def close(self):
            bus.closed.append(self.path)
            if error_at == "close":
                raise OSError("private-device-path")

        # Feature, configuration, initialization and firmware APIs are absent.

    bus.device = Handle
    with patch.object(H, "hid", bus), patch.object(H.hidlist, "enumerate", return_value=infos):
        yield bus


class MoondropTests(unittest.TestCase):
    def test_exact_request_at_each_descriptor_proven_length(self):
        for inp, out in ((10, 4), (64, 64), (65, 33), (128, 65), (4096, 4096)):
            info = entry(input_report_length=inp, output_report_length=out,
                         input_report_sizes={"75": inp}, output_report_sizes={"75": out})
            with self.subTest(inp=inp, out=out), fixture([reply(length=inp)], [info]) as bus:
                status, = P().poll()
            self.assertEqual(bus.writes, [[0x4B, 0x80, 0x27, 0] + [0] * (out - 4)])
            self.assertEqual(bus.opened, bus.closed)
            self.assertEqual((status.name, status.kind, status.source, status.verification_level),
                             ("MOONDROP GM01 Pro", "headset", "moondrop", "C"))
            self.assertEqual((status.level, status.charging_known), (73, False))
        for length in (None, True, 0, 3, 4097, 64.0):
            with self.subTest(length=length), self.assertRaises(ValueError):
                M.make_request(length)

    def test_validity_and_full_percentage_byte_range(self):
        for level in range(256):
            self.assertEqual(M.parse_reply(reply(level)), (level, False, "") if level <= 100 else None)
        for valid in range(256):
            self.assertEqual(M.parse_reply(reply(valid=valid)), (73, False, "") if valid else None)
        frame = reply(0)
        frame[1], frame[4:8], frame[-1] = 0xFF, [0xFF] * 4, 0xFF
        self.assertEqual(M.parse_reply(frame), (0, False, ""))

    def test_invalid_headers_lengths_range_and_off_never_create_readings(self):
        frames = [None, [], [0] * 64, M.make_request(64), reply()[:9], reply(101), reply(valid=0)]
        for index, values in ((0, (0, 0x4A, 0x4C)), (2, (1, 0x31, 0x4F)), (3, (0, 5, 61, 255))):
            for value in values:
                frame = reply()
                frame[index] = value
                frames.append(frame)
        for frame in frames:
            with self.subTest(frame=frame), fixture([frame]) as bus:
                self.assertIsNone(M.parse_reply(frame))
                self.assertEqual(P().poll(), [])
                self.assertEqual(bus.writes, [M.make_request(64)])
                self.assertEqual(bus.opened, bus.closed)
        for frame in (reply()[:-1], reply() + [0]):
            with fixture([frame]) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(len(bus.writes), 1)

    def test_unknown_or_inconsistent_proof_never_opens(self):
        changes = [dict(vendor_id=0x35D9), dict(product_id=0x012D), dict(product_id=0x0130),
                   dict(usage_page=1, usage=6), dict(usage_page=0xFF04), dict(usage=1),
                   dict(path=b""), dict(physical_id=""), dict(physical_id="collection:unknown")]
        for direction, minimum in (("input", 10), ("output", 4)):
            changes += [{direction + "_report_length": n} for n in (None, True, 0, minimum - 1, 63, 65, 4097)]
            changes += [{direction + "_report_sizes": sizes} for sizes in
                        (None, {}, {75: 64}, {"75": 63}, {"75": 65}, {"74": 64},
                         {"75": 64.0}, {"75": 64, "9": True}, {"75": 64, "9": 65})]
        for change in changes:
            with self.subTest(change=change), fixture([reply()], [entry(**change)]) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual((bus.opened, bus.writes, bus.reads), ([], [], []))

    def test_ambiguous_collections_fail_closed_and_physical_receivers_stay_separate(self):
        a, b = entry(), entry(path=b"second")
        for infos in ([], [a, b], [b, a], [a, dict(b, input_report_sizes=None)]):
            with self.subTest(infos=infos), fixture([reply()], infos) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(bus.opened, [])
        b["physical_id"] = "receiver-b"
        with fixture([reply(20), reply(90)], [a, b, entry(path=b"keyboard", usage_page=1, usage=6)]) as bus:
            statuses = P().poll()
            self.assertEqual([s.level for s in statuses], [20, 90])
            self.assertEqual(len({s.key for s in statuses}), 2)
            self.assertEqual(bus.opened, [a["path"], b["path"]])

    def test_unrelated_reports_are_bounded_and_zero_validity_stops(self):
        unrelated = reply()
        unrelated[2] = 0x4F  # OTA/control traffic must never become a battery.
        with fixture([unrelated, reply(100)]) as bus:
            status, = P().poll()
            self.assertEqual(status.level, 100)
            self.assertEqual(len(bus.writes), 1)
        with fixture([unrelated] * 100) as bus:
            self.assertEqual(P().poll(), [])
            self.assertEqual(len(bus.reads), M.MAX_READS)
        with fixture([reply(valid=0), reply()]) as bus:
            self.assertEqual(P().poll(), [])
            self.assertEqual(len(bus.reads), 1)
        with fixture([reply()]) as bus, patch.object(M.time, "monotonic", side_effect=[0, 2]):
            self.assertEqual(P().poll(), [])
            self.assertEqual(bus.reads, [])

    def test_silence_short_write_and_io_errors_never_retry_or_send_other_commands(self):
        for options in ({}, {"short": True}, *({"error_at": s} for s in ("open", "write", "read", "close"))):
            provider = P()
            with self.subTest(options=options), fixture(**options) as bus:
                self.assertEqual(provider.poll(), [])
                self.assertEqual(bus.opened, bus.closed)
                self.assertLessEqual(len(bus.writes), 1)
                if options.get("short") or options.get("error_at") in ("open", "write"):
                    self.assertEqual(bus.reads, [])
                self.assertNotIn("private-device-path", " ".join(provider.diagnostics()))


if __name__ == "__main__":
    unittest.main()
