"""Synthetic fixtures for Keychron's NAPE battery query (SKN 青龙 4.0 Ultra).

The 362D:D028 receiver of this project's test hardware answers 0xB2 (receiver
state) but echoes the NAPE query, so the echo case is part of the fixture: it
must never become a level.
"""
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
from providers import skn_nape_keyboard as S

P = S.SknNapeKeyboardProvider
RAW = b"kbd-raw"          # FF60:0061, the raw-HID channel
ALT = b"kbd-alt"          # 008C:0001, the alternate vendor channel


def entry(path=RAW, usage_page=0xFF60, usage=0x0061, **changes):
    info = dict(vendor_id=S.VID, product_id=0x132B, usage_page=usage_page,
                usage=usage, path=path, physical_id="keyboard-a",
                product_string="fixture")
    info.update(changes)
    return info


def reply(level=64, charging=0, command=S.CMD_GET_BATTERY):
    frame = [S.CMD_WRAPPER, command, level, charging] + [0] * (S.REPORT_BYTES - 4)
    return frame


def echo():
    return list(S.make_request()[1:])


class FakeHid:
    """One handle per path; each path has its own write result and frames."""

    def __init__(self, behaviour):
        self.behaviour = behaviour          # path -> {"write": result, "frames": [...]}
        self.opened, self.writes, self.reads, self.closed = [], [], [], []

    def enumerate(self, *args, **kwargs):
        return self.inventory

    def device(self):
        bus = self

        class Handle:
            def open_path(self, path):
                self.path = path
                bus.opened.append(path)
                if path not in bus.behaviour:
                    raise OSError("private-path")

            def write(self, packet):
                bus.writes.append((self.path, list(packet)))
                result = bus.behaviour[self.path].get("write")
                if isinstance(result, OSError):
                    raise result
                return len(packet) if result is None else result

            def read(self, length, timeout_ms):
                bus.reads.append((self.path, length))
                frames = bus.behaviour[self.path].setdefault("frames", [])
                return frames.pop(0) if frames else []

            def close(self):
                bus.closed.append(self.path)

        return Handle()


def fixture(behaviour, inventory=None):
    class Ctx:
        def __enter__(self):
            self.bus = FakeHid(behaviour)
            self.bus.inventory = inventory if inventory is not None else [entry()]
            self.patches = [patch.object(S, "hid", self.bus),
                            patch.object(S.hidlist, "enumerate",
                                         return_value=self.bus.inventory)]
            for item in self.patches:
                item.start()
            return self.bus

        def __exit__(self, *exc):
            for item in reversed(self.patches):
                item.stop()
            return False

    return Ctx()


class SknNapeTests(unittest.TestCase):
    def test_request_is_the_nape_battery_query_only(self):
        request = S.make_request()
        self.assertEqual(len(request), 33)
        self.assertEqual(list(request), [0x00, 0xA7, 0x31] + [0] * 30)

    def test_level_and_charging_state_are_reported(self):
        for level in (1, 64, 100):
            for charging in (0, 1):
                with self.subTest(level=level, charging=charging), \
                        fixture({RAW: {"frames": [reply(level, charging)]}}) as bus:
                    status, = P().poll()
                self.assertEqual((status.level, status.charging), (level, bool(charging)))
                self.assertEqual((status.kind, status.online), ("keyboard", True))
                self.assertTrue(status.charging_known)
                self.assertTrue(status.unconfirmed)
                self.assertEqual(bus.writes, [(RAW, list(S.make_request()))])
                self.assertEqual(bus.reads[0], (RAW, S.REPORT_BYTES))
                self.assertEqual(bus.opened, bus.closed)

    def test_zero_level_discharging_is_indistinguishable_from_the_echo(self):
        # 0 % and state 0 make the reply byte-identical to the query, so the
        # echo rule wins and no reading is produced at that exact value.
        with fixture({RAW: {"frames": [reply(0, 0)]}}):
            self.assertEqual(P().poll(), [])
            self.assertIsNone(S.parse_reply(bytes(reply(0, 0))))

    def test_the_receiver_echo_never_becomes_a_reading(self):
        # 0xFF60:0061 echoes; 0x008C:0001 rejects host writes - nothing reported.
        behaviour = {RAW: {"frames": [echo()] * 4}, ALT: {"write": OSError("no output report")}}
        with fixture(behaviour, [entry(), entry(path=ALT, usage_page=0x008C, usage=0x0001)]) as bus:
            provider = P()
            self.assertEqual(provider.poll(), [])
            self.assertIn("rejected battery reply", provider.diagnostics())
            self.assertIn("battery query write failed; no retry", provider.diagnostics())
        self.assertEqual([path for path, _ in bus.writes], [RAW, ALT])
        self.assertEqual(bus.opened, bus.closed)

    def test_invalid_replies_are_rejected(self):
        wrong_wrapper = reply()
        wrong_wrapper[0] = 0xA6
        wrong_command = reply(command=0x22)
        bad_state = reply(charging=2)
        for frame in ([], reply()[:-1], reply() + [0], [0] + reply(),
                      wrong_wrapper, wrong_command, reply(level=255), bad_state):
            with self.subTest(frame=frame[:4]), fixture({RAW: {"frames": [frame] * 4}}) as bus:
                self.assertIsNone(S.parse_reply(bytes(frame)))
                self.assertEqual(P().poll(), [])
                self.assertEqual(len(bus.writes), 1)
                self.assertEqual(bus.opened, bus.closed)

    def test_the_asynchronous_notification_is_accepted(self):
        with fixture({RAW: {"frames": [reply(42, 1, S.CMD_BATTERY_REPORT)]}}):
            status, = P().poll()
        self.assertEqual((status.level, status.charging), (42, True))

    def test_short_write_and_missing_reply_yield_no_reading(self):
        with fixture({RAW: {"write": 32}}):
            self.assertEqual(P().poll(), [])
        with fixture({RAW: {"frames": []}}) as bus:
            self.assertEqual(P().poll(), [])
        self.assertEqual(len(bus.writes), 1)

    def test_other_identities_and_collections_never_open(self):
        changes = [dict(product_id=pid) for pid in (0x132C, 0xD029, 0x0001)]
        changes += [dict(vendor_id=0x3434), dict(usage_page=0xFF0A, usage=1),
                    dict(usage_page=0xFFC1, usage=1), dict(path=b"")]
        for change in changes:
            with self.subTest(change=change), \
                    fixture({RAW: {"frames": [reply()]}}, [entry(**change)]) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(bus.opened, [])

    def test_duplicate_and_unresolved_collections_never_open(self):
        inventories = ([entry(), entry(path=RAW, physical_id="keyboard-a")],
                       [entry(physical_id="collection:unknown")])
        for inventory in inventories:
            with self.subTest(inventory=inventory), \
                    fixture({RAW: {"frames": [reply()]}}, inventory) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(bus.opened, [])


if __name__ == "__main__":
    unittest.main()
