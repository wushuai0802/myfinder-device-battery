"""Offline AULA 2.4 GHz receiver contracts; zero tails below are real padding."""
from contextlib import contextmanager
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

ENGINE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ENGINE))
sys.path.insert(0, str(ENGINE / "tests"))
import _hidstub  # noqa: F401 -- before importing any provider
import battery_hid as H
from providers import aula_keyboard as K, aula_mouse as M
from test_new_brands import FakeHid
from types import SimpleNamespace

# Independent complete requests: no use of make_request().
MOUSE_REQUEST = bytes.fromhex(
    "04 00 00 1A 00 00 00 00  00 00 00 00 00 00 00 00 "
    "00 00 00 00 00 00 00 00  00 00 00 00 00 00 00 00 "
    "00 00 00 00 00 00 00 00  00 00 00 00 00 00 00 00 "
    "00 00 00 00 00 00 00 00  00 00 00 00 00 00 00 00")
KEYBOARD_REQUEST = bytes.fromhex(
    "00 AA 10 18 00 00 00 00  00 00 00 00 00 00 00 00 "
    "00 00 00 00 00 00 00 00  00 00 00 00 00 00 00 00 "
    "00")
# Hardware captures, 2026-11-18: complete frames as received.
MOUSE_CAPTURE = bytes.fromhex(
    "04 00 00 1A 00 00 00 00  50 00 00 00 00 00 00 00 "
    "00 00 00 00 00 00 00 00  00 00 00 00 00 00 00 00 "
    "00 00 00 00 00 00 00 00  00 00 00 00 00 00 00 00 "
    "00 00 00 00 00 00 00 00  00 00 00 00 00 00 00 00")
# Same mouse on the charging cable: [9] = 01, percentage raised to 82.
MOUSE_CHARGING = bytes.fromhex(
    "04 00 00 1A 00 00 00 00  52 01 00 00 00 00 00 00 "
    "00 00 00 00 00 00 00 00  00 00 00 00 00 00 00 00 "
    "00 00 00 00 00 00 00 00  00 00 00 00 00 00 00 00 "
    "00 00 00 00 00 00 00 00  00 00 00 00 00 00 00 00")
KEYBOARD_CAPTURE = bytes.fromhex(
    "55 10 18 00 00 00 00 00  00 00 00 12 A6 38 11 27 "
    "66 01 00 00 66 01 00 00  02 30 00 00 00 00 00 00")
MODULES = {M.AulaMouseProvider: M, K.AulaKeyboardProvider: K}


def entry(factory, physical="receiver-a", **changes):
    info = dict(vendor_id=factory.vid, product_id=next(iter(factory.pids)),
                interface_number=MODULES[factory].INTERFACE,
                product_string=MODULES[factory].PRODUCT,
                usage_page=factory.collection[0], usage=factory.collection[1],
                input_report_length=factory.input_bounds[0],
                output_report_length=factory.output_bytes,
                input_report_sizes={str(factory.input_id): factory.input_bounds[0]},
                output_report_sizes={str(factory.output_id): factory.output_bytes},
                physical_id=physical, path=(physical + "-vendor").encode(),
                serial_number="shared-private-serial")
    info.update(changes)
    return info


def reply(factory, level=50, charging=0, **changes):
    frame = list(MOUSE_CAPTURE if factory is M.AulaMouseProvider else KEYBOARD_CAPTURE)
    frame[8 if factory is M.AulaMouseProvider else 25] = level
    if factory is M.AulaMouseProvider:
        frame[9] = charging
    for offset, value in changes.items():
        frame[int(offset)] = value
    return frame


@contextmanager
def fixture(factory, frames=(), inventory=None, **kwargs):
    bus = FakeHid(frames, **kwargs)
    with patch.object(H, "hid", bus), \
            patch.object(H.hidlist, "enumerate",
                         return_value=[entry(factory)] if inventory is None else inventory), \
            patch.object(H, "time", SimpleNamespace(monotonic=lambda: bus.now)):
        yield bus


class AulaTests(unittest.TestCase):
    def test_only_exact_request_is_written(self):
        for factory, request, length in (
                (M.AulaMouseProvider, MOUSE_REQUEST, 64),
                (K.AulaKeyboardProvider, KEYBOARD_REQUEST, 33)):
            with self.subTest(provider=factory.name), fixture(
                    factory, [reply(factory)]) as bus:
                status, = factory().poll()
                self.assertEqual(status.level, 50)
                self.assertEqual(bus.writes, [(entry(factory)["path"], list(request))])
                self.assertEqual(bus.reads[0][0], length - (factory.input_id == 0))
                self.assertEqual((status.source, status.kind, status.verification_level),
                                 (factory.name, "mouse" if factory is M.AulaMouseProvider else "keyboard", "C"))
                charging_known = factory is M.AulaMouseProvider
                self.assertEqual((status.charging, status.charging_known), (False, charging_known))
                self.assertTrue(status.online and status.connection_known)
                self.assertEqual(bus.opened, bus.closed)

    def test_hardware_captures_parse_for_every_byte_container(self):
        self.assertEqual(M.parse_reply(MOUSE_CAPTURE), (80, False, ""))
        self.assertEqual(M.parse_reply(MOUSE_CHARGING), (82, True, ""))
        self.assertEqual(K.parse_reply(KEYBOARD_CAPTURE), (48, False, ""))
        for container in (bytes, bytearray, list, tuple):
            self.assertEqual(M.parse_reply(container(MOUSE_CAPTURE)), (80, False, ""))
            self.assertEqual(M.parse_reply(container(MOUSE_CHARGING)), (82, True, ""))
            self.assertEqual(K.parse_reply(container(KEYBOARD_CAPTURE)), (48, False, ""))

    def test_mouse_charging_follows_the_cable_and_rejects_unknown_states(self):
        # Nine unplugged, nine charging, nine unplugged again, 2026-11-18.
        for _ in range(9):
            with fixture(M.AulaMouseProvider, [MOUSE_CAPTURE]) as bus:
                status, = M.AulaMouseProvider().poll()
                self.assertEqual((status.level, status.charging, status.charging_known),
                                 (80, False, True))
                self.assertEqual(len(bus.writes), 1)
            with fixture(M.AulaMouseProvider, [MOUSE_CHARGING]) as bus:
                status, = M.AulaMouseProvider().poll()
                self.assertEqual((status.level, status.charging, status.charging_known),
                                 (82, True, True))
                self.assertEqual(len(bus.writes), 1)
        for value in (2, 3, 255):
            self.assertIsNone(M.parse_reply(reply(M.AulaMouseProvider, charging=value)))

    def test_identical_identity_outside_the_exact_product_never_opens(self):
        for factory in (M.AulaMouseProvider, K.AulaKeyboardProvider):
            module = MODULES[factory]
            other = module.PRODUCT[:-1]
            bad = [dict(product_string=other), dict(product_string=""),
                   dict(product_string=(" " + module.PRODUCT)),
                   dict(interface_number=module.INTERFACE + 1), dict(interface_number=-1),
                   dict(interface_number=True), dict(interface_number=str(module.INTERFACE)),
                   dict(vendor_id=0), dict(product_id=0), dict(usage_page=1), dict(usage=1)]
            for changes in bad:
                with self.subTest(provider=factory.name, changes=changes), fixture(
                        factory, [reply(factory)], [entry(factory, **changes)]) as bus:
                    self.assertEqual(factory().poll(), [])
                    self.assertEqual((bus.opened, bus.writes), ([], []))

    def test_missing_or_ambiguous_parent_and_duplicate_collection_never_open(self):
        for factory in (M.AulaMouseProvider, K.AulaKeyboardProvider):
            bad = [dict(physical_id=""), dict(physical_id="collection:private-path"),
                   dict(path=b""), dict(path=None)]
            for changes in bad:
                with self.subTest(provider=factory.name, changes=changes), fixture(
                        factory, [reply(factory)], [entry(factory, **changes)]) as bus:
                    self.assertEqual(factory().poll(), [])
                    self.assertEqual(bus.opened, [])
            infos = [entry(factory), entry(factory, path=b"second-collection")]
            with fixture(factory, [reply(factory)], infos) as bus:
                self.assertEqual(factory().poll(), [])
                self.assertEqual((bus.opened, bus.writes), ([], []))

    def test_unproven_report_map_never_sends(self):
        for factory in (M.AulaMouseProvider, K.AulaKeyboardProvider):
            key = str(factory.input_id)
            for changes in (dict(input_report_sizes=None), dict(output_report_sizes=None),
                            dict(input_report_sizes={}), dict(output_report_sizes={}),
                            dict(input_report_sizes={key: 1}), dict(output_report_sizes={key: 1}),
                            dict(output_report_sizes={key: 64, "5": 64}),
                            dict(input_report_length=32), dict(output_report_length=32)):
                with self.subTest(provider=factory.name, changes=changes), fixture(
                        factory, [reply(factory)], [entry(factory, **changes)]) as bus:
                    self.assertEqual(factory().poll(), [])
                    self.assertEqual((bus.opened, bus.writes), ([], []))

    def test_wrong_frames_never_become_a_reading(self):
        invalid = [None, True, 64, "x", {i: 0 for i in range(40)}, object(),
                   [], [0] * 64, MOUSE_CAPTURE[:-1], MOUSE_CAPTURE + b"\x00", list(MOUSE_REQUEST)]
        for frame in invalid:
            with self.subTest(frame=frame):
                self.assertIsNone(M.parse_reply(frame))
        for offset, value in ((0, 0), (1, 1), (3, 0x1B), (8, 101), (8, 255), (8, -1), (8, True),
                              (9, 2), (9, 255), (9, -1), (9, True)):
            self.assertIsNone(M.parse_reply(reply(M.AulaMouseProvider, **{str(offset): value})))

        invalid = [None, True, 32, "x", [], [0] * 32, KEYBOARD_CAPTURE[:-1],
                   KEYBOARD_CAPTURE + b"\x00"]
        for frame in invalid:
            with self.subTest(frame=frame):
                self.assertIsNone(K.parse_reply(frame))
        for offset, value in ((0, 0x54), (1, 0x11), (2, 0x19), (3, 0x18),
                              (25, 101), (25, 255), (25, -1), (25, True)):
            self.assertIsNone(K.parse_reply(reply(K.AulaKeyboardProvider, **{str(offset): value})))

    def test_stale_page_reply_is_rejected_and_not_repeated(self):
        stale = reply(K.AulaKeyboardProvider, **{"3": 0x18})
        self.assertIsNone(K.parse_reply(stale))
        with fixture(K.AulaKeyboardProvider, [stale]) as bus:
            self.assertEqual(K.AulaKeyboardProvider().poll(), [])
            self.assertEqual(len(bus.writes), 1)

    def test_silence_repeats_once_for_keyboard_and_never_for_mouse(self):
        with fixture(K.AulaKeyboardProvider, []) as bus:
            self.assertEqual(K.AulaKeyboardProvider().poll(), [])
            self.assertEqual(len(bus.writes), 2)
            self.assertEqual(bus.opened, bus.closed)
        with fixture(M.AulaMouseProvider, []) as bus:
            self.assertEqual(M.AulaMouseProvider().poll(), [])
            self.assertEqual(len(bus.writes), 1)

    def test_silence_then_reply_uses_the_repeat_without_a_stale_diagnostic(self):
        # An empty frame ends the first read loop without consuming the reply,
        # so the repeat is the only attempt that can see it.
        frames = [[], reply(K.AulaKeyboardProvider, 47)]
        with fixture(K.AulaKeyboardProvider, frames) as bus:
            provider = K.AulaKeyboardProvider()
            status, = provider.poll()
            self.assertEqual(status.level, 47)
            self.assertEqual(len(bus.writes), 2)
            self.assertEqual(provider.diagnostics(), [])
            self.assertEqual(bus.opened, bus.closed)

    def test_short_write_stops_before_reading(self):
        for factory in (M.AulaMouseProvider, K.AulaKeyboardProvider):
            with self.subTest(provider=factory.name), fixture(
                    factory, [reply(factory)], write_result=32) as bus:
                self.assertEqual(factory().poll(), [])
                self.assertEqual((len(bus.writes), len(bus.reads)), (1, 0))
                self.assertEqual(bus.opened, bus.closed)

    def test_io_errors_close_without_exposing_the_path(self):
        for factory in (M.AulaMouseProvider, K.AulaKeyboardProvider):
            for stage in ("open", "write", "read"):
                with self.subTest(provider=factory.name, stage=stage), fixture(
                        factory, [reply(factory)], error_at=stage) as bus:
                    provider = factory()
                    self.assertEqual(provider.poll(), [])
                    self.assertEqual(bus.opened, bus.closed)
                    self.assertIn("battery I/O failed; connection unknown", provider.diagnostics())
                    self.assertNotIn("private-path", " ".join(provider.diagnostics()))

    def test_close_failure_keeps_the_measurement(self):
        for factory in (M.AulaMouseProvider, K.AulaKeyboardProvider):
            with self.subTest(provider=factory.name), fixture(factory, [reply(factory)]) as bus:
                original = bus.device

                def device():
                    handle = original()
                    close = handle.close

                    def failed_close():
                        close()
                        raise OSError("private-path")

                    handle.close = failed_close
                    return handle

                with patch.object(bus, "device", side_effect=device):
                    provider = factory()
                    self.assertEqual([s.level for s in provider.poll()], [50])
                self.assertEqual(provider.diagnostics(), ["HID handle close failed"])
                self.assertEqual(bus.opened, bus.closed)

    def test_two_receivers_keep_separate_keys(self):
        factory = M.AulaMouseProvider
        infos = [entry(factory, "one"), entry(factory, "two", path=b"other-receiver")]
        with fixture(factory, [reply(factory, 40), reply(factory, 70)], infos) as bus:
            statuses = factory().poll()
            self.assertEqual(sorted(s.level for s in statuses), [40, 70])
            self.assertEqual(len({s.key for s in statuses}), 2)
            self.assertEqual(bus.opened, [infos[0]["path"], infos[1]["path"]])


if __name__ == "__main__":
    unittest.main()
