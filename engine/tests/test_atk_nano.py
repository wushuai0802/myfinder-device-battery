"""Synthetic BITMOUSE fixtures from ATK Hub 3.2.31; no vendor execution."""
from contextlib import contextmanager
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
import battery_hid as H
from providers import pulsar as P
from test_new_brands import FakeHid

REQUEST = [0x08, 0x7D, 0x72, 0x02, 0x00, 0x01, 0x07, 0x01] + [0] * 56


def entry(physical="nano-a", report_id=8, length=64, **changes):
    info = dict(vendor_id=0x373B, product_id=0x1216, usage_page=0xFF05, usage=1,
                input_report_length=length, output_report_length=64,
                input_report_sizes={str(report_id): length}, output_report_sizes={"8": 64},
                path=(physical + "-ff05").encode(), physical_id=physical,
                product_string="NK mouse NANO dongle", serial_number="shared")
    info.update(changes)
    return info


def reply(level=73, report_id=8, length=64, status=0, command=7, data_length=1):
    payload = [0x72, status, 0, 1, command, data_length, level] + [0] * (length - 8)
    return ([report_id] if report_id else []) + payload


@contextmanager
def fixture(frames=(), inventory=None, **kwargs):
    bus = FakeHid(frames, **kwargs)
    infos = [entry()] if inventory is None else inventory
    with patch.object(H, "hid", bus), patch.object(P, "hid", bus), \
            patch.object(H.hidlist, "enumerate", side_effect=lambda vid=0: [
                d for d in infos if not vid or d["vendor_id"] == vid]), \
            patch.object(H, "time", SimpleNamespace(monotonic=lambda: bus.now)), \
            patch.object(P, "output_length", side_effect=AssertionError("legacy probe")), \
            patch.object(P, "_capture_dpi", side_effect=AssertionError("DPI query")):
        yield bus


class AtkNanoTests(unittest.TestCase):
    def test_exact_request_and_descriptor_selected_input_id(self):
        self.assertEqual(P.make_nano_request(), REQUEST)
        self.assertEqual(REQUEST[1], sum(REQUEST[2:]) & 255)
        for report_id in (0, 1, 8, 85, 255):
            for length in (8, 32, 64):
                for level in (0, 73, 100):
                    info = entry(report_id=report_id, length=length)
                    with self.subTest(id=report_id, length=length, level=level), fixture(
                            [reply(level, report_id, length)], [info]) as bus:
                        status, = P.PulsarProvider().poll()
                    self.assertEqual((status.level, status.kind, status.verification_level),
                                     (level, "mouse", "C"))
                    self.assertEqual(status.name, "ATK / VXE NANO receiver")
                    self.assertFalse(status.charging_known)
                    self.assertTrue(status.connection_known)
                    self.assertIsNone(status.dpi)
                    self.assertIsNone(status.dpi_target)
                    self.assertEqual(bus.writes, [(info["path"], REQUEST)])
                    self.assertEqual(bus.reads[0][0], length - (report_id == 0))
                    self.assertEqual(bus.opened, bus.closed)

    def test_missing_ambiguous_or_incompatible_descriptors_never_open(self):
        changes = [dict(input_report_length=n) for n in (None, True, 0, 7, 65)]
        changes += [dict(output_report_length=n) for n in (None, True, 0, 63, 65)]
        changes += [dict(input_report_sizes=s) for s in (
            None, {}, {"8": 63}, {"8": True}, {"8": 64, "9": 64},
            {"-1": 64}, {"256": 64}, {"08": 64}, {8: 64}, {"x": 64})]
        changes += [dict(output_report_sizes=s) for s in (
            None, {}, {"0": 64}, {"8": 63}, {"8": True}, {"8": 64, "9": 65})]
        for change in changes:
            with self.subTest(change=change), fixture([reply()], [entry(**change)]) as bus:
                self.assertEqual(P.PulsarProvider().poll(), [])
            self.assertEqual(bus.opened, [])
            self.assertEqual(bus.writes, [])

    def test_exact_identity_collection_and_uniqueness_without_fallback(self):
        changes = [dict(vendor_id=0), dict(product_id=0x1217), dict(usage=2),
                   dict(path=b""), dict(physical_id="collection:unresolved")]
        changes += [dict(usage_page=page) for page in (1, 0xFF02, 0xFF04, 0xFFB1, 0xFFB3)]
        inventories = [[entry(**change)] for change in changes]
        inventories += [[entry(), entry(path=b"ambiguous")],
                        [entry(input_report_sizes=None), entry(usage_page=0xFF02, usage=2,
                                                               path=b"legacy-fallback")]]
        for inventory in inventories:
            with self.subTest(inventory=inventory), fixture([reply()], inventory) as bus:
                self.assertEqual(P.PulsarProvider().poll(), [])
            self.assertEqual(bus.opened, [])
            self.assertEqual(bus.writes, [])

    def test_bad_replies_never_become_readings(self):
        frames = [[], REQUEST, reply(report_id=9), reply(length=8), reply(length=65),
                  reply(-1), reply(101), reply(255), reply(command=0), reply(command=15),
                  reply(status=1), reply(status=2), reply(status=0x73), reply(status=255),
                  reply(data_length=0), reply(data_length=58)]
        for frame in frames:
            with self.subTest(frame=frame[:8]), fixture([frame]) as bus:
                self.assertEqual(P.PulsarProvider().poll(), [])
            self.assertEqual(bus.writes, [(entry()["path"], REQUEST)])
            self.assertEqual(bus.opened, bus.closed)

    def test_only_battery_query_despite_other_reports_and_firmware_collections(self):
        commands = (1, 2, 3, 5, 6, 10, 11, 12, 13, 15, 19, 20, 21, 22, 24,
                    27, 31, 33, 34, 37, 39, 0x82, 0x83, 0x85, 0xAA, 0xAB,
                    0xAC, 0xAE, 0xAF, 0xB7)
        infos = [entry(usage_page=0xFFB1, path=b"dfu-control"),
                 entry(usage_page=0xFFB3, path=b"dfu-data"), entry()]
        with fixture([reply(command=cmd) for cmd in commands] + [reply(62)], infos) as bus:
            status, = P.PulsarProvider().poll()
        self.assertEqual(status.level, 62)
        self.assertEqual(bus.opened, [entry()["path"]])
        self.assertEqual(bus.writes, [(entry()["path"], REQUEST)])

    def test_silence_and_streams_are_bounded_without_retry(self):
        for frames in ([], [reply(command=0)] * 100):
            for read_ms in (0, 240):
                with self.subTest(read_ms=read_ms), fixture(frames, read_ms=read_ms) as bus:
                    self.assertEqual(P.PulsarProvider().poll(), [])
                self.assertLessEqual(bus.now, 1.001)
                self.assertLessEqual(len(bus.reads), 32)
                self.assertEqual(bus.writes, [(entry()["path"], REQUEST)])
                self.assertEqual(bus.opened, bus.closed)

    def test_short_writes_and_io_errors_close_without_retry(self):
        cases = [dict(write_result=n) for n in (-1, 0, 63)]
        cases += [dict(error_at=where) for where in ("open", "write", "read")]
        for case in cases:
            with self.subTest(case=case), fixture([reply()], **case) as bus:
                self.assertEqual(P.PulsarProvider().poll(), [])
            self.assertLessEqual(len(bus.writes), 1)
            self.assertEqual(bus.opened, bus.closed)

    def test_two_receivers_keep_separate_identity_and_input_contracts(self):
        infos = [entry("one", report_id=0, length=8), entry("two", report_id=85)]
        with fixture([reply(20, 0, 8), reply(90, 85)], infos):
            first = P.PulsarProvider().poll()
        with fixture([reply(90, 85), reply(20, 0, 8)], list(reversed(infos))):
            second = P.PulsarProvider().poll()
        self.assertEqual([s.level for s in first], [20, 90])
        self.assertEqual(len({s.key for s in first}), 2)
        self.assertEqual({s.key for s in first}, {s.key for s in second})


if __name__ == "__main__":
    unittest.main()
