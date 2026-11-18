"""Synthetic AA/55 exchange fixtures from the AULA web driver's protocol."""
from contextlib import contextmanager
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
import battery_hid as H
from providers import aula as V

P = V.AulaProvider
PATH = b"aula-collection"


def entry(vid=0x0C45, pid=0x8031, physical="kbd-a", **changes):
    declared = V.DECLARED_COLLECTIONS.get((vid, pid))
    page, usage = declared if declared else (0xFF00, 0x01)
    info = dict(vendor_id=vid, product_id=pid,
                usage_page=page, usage=usage,
                path=PATH, physical_id=physical, product_string="AULA Keyboard",
                input_report_length=33, output_report_length=33,
                input_report_sizes={"0": 33}, output_report_sizes={"0": 33})
    info.update(changes)
    return info


def reply(command=0x10, level=73, charging=0, size=32, **changes):
    frame = [0x55, command, 0x18, 0, 0, 0, 0, 0] + [0] * 24
    frame[8 + 17] = level
    if command == V.MS_INFO:
        frame[8 + 18] = charging
    for offset, value in changes.items():
        frame[int(offset)] = value
    return frame + [0] * (size - 32)


def push(charge=1, level=73):
    return [0x55, 0xFB, 0x02, charge, level]


class FakeHid:
    def __init__(self, frames=(), write_result=None, error_at=None):
        self.frames = list(frames)
        self.write_result, self.error_at = write_result, error_at
        self.now = 0.0
        self.opened, self.writes, self.reads, self.closed = [], [], [], []

    def device(self):
        bus = self

        class Handle:
            def open_path(self, path):
                self.path = path
                bus.opened.append(path)
                if bus.error_at == "open":
                    raise OSError("private-path")

            def write(self, packet):
                bus.writes.append((self.path, list(packet)))
                if bus.error_at == "write":
                    raise OSError("private-path")
                return len(packet) if bus.write_result is None else bus.write_result

            def read(self, length, timeout_ms):
                bus.reads.append((length, timeout_ms))
                bus.now += (0.01 if bus.frames else timeout_ms) / 1000
                if bus.error_at == "read":
                    raise OSError("private-path")
                return bus.frames.pop(0) if bus.frames else []

            def close(self):
                bus.closed.append(self.path)

        return Handle()


@contextmanager
def fixture(frames=(), inventory=None, **kwargs):
    bus = FakeHid(frames, **kwargs)
    with patch.object(H, "hid", bus), \
            patch.object(H.hidlist, "enumerate",
                         side_effect=lambda vid=0: [d for d in ([entry()] if inventory is None else inventory)
                                                    if not vid or d["vendor_id"] == vid]), \
            patch.object(H, "time", SimpleNamespace(monotonic=lambda: bus.now)):
        yield bus


class AulaTests(unittest.TestCase):
    def test_request_frames_use_the_official_layout(self):
        self.assertEqual(list(V.make_request(0x10, 33)),
                         [0x00, 0xAA, 0x10, 0x18, 0, 0, 0, 0] + [0] * 25)
        self.assertEqual(list(V.make_request(0xA0, 65)),
                         [0x00, 0xAA, 0xA0, 0x18, 0, 0, 0, 0] + [0] * 57)

    def test_keyboard_stack_reply_produces_a_keyboard_reading(self):
        with fixture([reply(0x10, 73)]) as bus:
            status, = P().poll()
        self.assertEqual((status.level, status.kind, status.source, status.verification_level),
                         (73, "keyboard", "aula", "C"))
        self.assertEqual(status.name, "AULA Keyboard")
        self.assertFalse(status.charging_known)
        self.assertTrue(status.connection_known)
        self.assertEqual(bus.writes, [(PATH, list(V.make_request(0x10, 33)))])
        self.assertEqual(bus.reads[0][0], 32)
        self.assertEqual(bus.opened, bus.closed)

    def test_mouse_stack_reply_costs_one_silent_keyboard_timeout(self):
        with fixture([[], reply(0xA0, 64, charging=1)]) as bus:
            status, = P().poll()
        self.assertEqual((status.level, status.kind), (64, "mouse"))
        self.assertEqual((status.charging, status.charging_known), (True, True))
        self.assertEqual(bus.writes, [(PATH, list(V.make_request(0x10, 33))),
                                      (PATH, list(V.make_request(0xA0, 33)))])
        self.assertEqual(bus.opened, bus.closed)
        with fixture([[], reply(0xA0, 64)]) as bus:
            status, = P().poll()
        self.assertEqual((status.charging, status.charging_known), (False, True))

    def test_percentages_are_shown_verbatim(self):
        for level in (1, 73, 100):
            with self.subTest(level=level), fixture([reply(0x10, level)]) as bus:
                status, = P().poll()
            self.assertEqual(status.level, level)
            self.assertEqual(bus.writes, [(PATH, list(V.make_request(0x10, 33)))])

    def test_padded_64_byte_echoes_are_accepted(self):
        self.assertEqual(V.parse_reply(reply(size=64), 0x10), (73, False, ""))
        with fixture([reply(0x10, 55, size=64)]) as bus:
            status, = P().poll()
        self.assertEqual(status.level, 55)

    def test_invalid_replies_are_rejected_without_a_reading(self):
        frames = [reply()[:-1], reply() + [0], [0] * 32,
                  reply(**{"0": 0x56}), reply(**{"1": 0x11}), reply(**{"1": 0xFB}),
                  reply(**{"2": 0x19}), reply(**{"3": 1}), reply(**{"4": 1}),
                  reply(level=101), reply(level=255), push()]
        for frame in frames:
            with self.subTest(frame=frame[:5]):
                self.assertIsNone(V.parse_reply(frame, 0x10))
            with self.subTest(frame=frame[:5]), fixture([frame]) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(len(bus.writes), 2)
                self.assertEqual(bus.opened, bus.closed)

    def test_reply_from_the_other_stack_never_produces_a_reading(self):
        with fixture([reply(0xA0)]) as bus:
            self.assertEqual(P().poll(), [])
        self.assertEqual(len(bus.writes), 2)
        self.assertEqual(bus.opened, bus.closed)

    def test_zero_and_all_zero_frames_are_asleep_artifacts_never_readings(self):
        # Live MINI 60 HE Pro evidence: a sleeping keyboard leaves the 0x10
        # stack silent and the receiver stack answers an all-zero payload.
        self.assertIsNone(V.parse_reply(reply(level=0), 0x10))
        with fixture([reply(0x10, 0)]) as bus:
            self.assertEqual(P().poll(), [])
        self.assertEqual(len(bus.writes), 1)
        with fixture([[], reply(0xA0, 0)]) as bus:
            provider = P()
            self.assertEqual(provider.poll(), [])
        self.assertEqual(len(bus.writes), 2)
        self.assertEqual(bus.opened, bus.closed)
        self.assertIn("device asleep/offline; no new reading; connection unknown",
                      provider.diagnostics())

    def test_async_push_between_replies_does_not_break_the_exchange(self):
        with fixture([push(), reply(0x10, 55)]) as bus:
            status, = P().poll()
        self.assertEqual(status.level, 55)
        self.assertEqual(len(bus.writes), 1)
        self.assertEqual(len(bus.reads), 2)

    def test_unproven_reports_or_ambiguous_collections_never_open_or_send(self):
        defects = [dict(input_report_length=None), dict(output_report_length=None),
                   dict(input_report_length=32), dict(input_report_length=34),
                   dict(input_report_length=64), dict(output_report_length=32),
                   dict(output_report_length=34), dict(output_report_length=64),
                   dict(input_report_sizes=None), dict(output_report_sizes=None),
                   dict(input_report_sizes={}), dict(output_report_sizes={}),
                   dict(input_report_sizes={"1": 33}), dict(output_report_sizes={"1": 33}),
                   dict(input_report_sizes={"0": 33, "1": 65}),
                   dict(path=b""), dict(physical_id="collection:unknown")]
        inventories = [[entry(**changes)] for changes in defects]
        inventories.append([entry(), entry(path=b"second-eligible")])
        for inventory in inventories:
            with self.subTest(inventory=inventory), fixture([reply()], inventory) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(bus.opened, [])
                self.assertEqual(bus.writes, [])

    def test_collection_gates_follow_the_official_filters(self):
        # Declared identities need their exact collection.
        for changes in (dict(usage_page=0xFF00, usage=1), dict(usage_page=0xFF08, usage=0x60),
                        dict(usage=0x62), dict(usage_page=1, usage=6)):
            with self.subTest(declared=changes), fixture([reply()], [entry(**changes)]) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(bus.opened, [])
        # Undeclared identities only ever match vendor-defined collections.
        undeclared = entry(0x38A6, 0x2710)
        for changes in (dict(usage_page=1, usage=6), dict(usage_page=None), dict(usage_page=0x0100)):
            with self.subTest(undeclared=changes), fixture([reply()],
                                                           [dict(undeclared, **changes)]) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(bus.opened, [])
        with fixture([reply()], [undeclared]) as bus:
            status, = P().poll()
        self.assertEqual(status.level, 73)
        self.assertEqual(bus.opened, [PATH])

    def test_other_pids_and_vids_never_open_or_send(self):
        defects = [dict(product_id=0x1234), dict(product_id=0xFFFF),
                   dict(vendor_id=0x3554), dict(vendor_id=0x0C46),
                   # The driver's borrowed 05AC:024F stays unenrolled: EPOMAKER
                   # TH108/TH87 and the maintainer's INFI75 share this identity.
                   dict(vendor_id=0x05AC, product_id=0x024F, usage_page=0xFF08, usage=0x61)]
        for changes in defects:
            with self.subTest(changes=changes), fixture([reply()], [entry(**changes)]) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(bus.opened, [])
                self.assertEqual(bus.writes, [])

    def test_multi_vid_identities_stay_separate(self):
        keyboard = entry(0x0C45, 0x8031, physical="kbd-a", path=b"kbd-a-vendor")
        mouse = entry(0x38A6, 0x2710, physical="mouse-a", path=b"mouse-a-vendor")
        with fixture([reply(0x10, 88), [], reply(0xA0, 41, charging=1)],
                     [mouse, keyboard]) as bus:
            statuses = P().poll()
        self.assertEqual([(s.kind, s.level) for s in statuses], [("keyboard", 88), ("mouse", 41)])
        self.assertEqual(len({s.key for s in statuses}), 2)
        self.assertEqual(bus.writes, [(b"kbd-a-vendor", list(V.make_request(0x10, 33))),
                                      (b"mouse-a-vendor", list(V.make_request(0x10, 33))),
                                      (b"mouse-a-vendor", list(V.make_request(0xA0, 33)))])
        self.assertEqual(bus.opened, bus.closed)
        # A PID that exists under one vendor only never matches another's table.
        for changes in (dict(vendor_id=0x38A6, product_id=0x8031, usage_page=0xFF00),
                        dict(vendor_id=0x0C45, product_id=0x2710, usage_page=0xFF00)):
            with self.subTest(changes=changes), fixture([reply()], [entry(**changes)]) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(bus.opened, [])

    def test_silence_is_bounded(self):
        provider = P()
        with fixture() as bus:
            self.assertEqual(provider.poll(), [])
        self.assertLessEqual(bus.now, 1.001)
        self.assertEqual(len(bus.reads), 2)
        self.assertEqual(len(bus.writes), 2)
        self.assertEqual(bus.opened, bus.closed)
        self.assertIn("no valid reply within deadline/read limit; connection unknown",
                      provider.diagnostics())

    def test_io_failures_close_and_short_writes_do_not_retry(self):
        with fixture([reply()], write_result=32) as bus:
            self.assertEqual(P().poll(), [])
            self.assertEqual(len(bus.writes), 2)
            self.assertEqual(bus.reads, [])
            self.assertEqual(bus.opened, bus.closed)
        for error_at in ("open", "write", "read"):
            with self.subTest(error_at=error_at), fixture([reply()], error_at=error_at) as bus:
                provider = P()
                self.assertEqual(provider.poll(), [])
                self.assertEqual(bus.opened, bus.closed)
                self.assertNotIn("private-path", " ".join(provider.diagnostics()))

    def test_product_string_and_empty_name_fallback(self):
        for product in ("AULA F87 Pro", "", None):
            with self.subTest(product=product), fixture([reply()], [entry(product_string=product)]):
                status, = P().poll()
            self.assertEqual(status.name, product or "AULA（0C45:8031）")


if __name__ == "__main__":
    unittest.main()
