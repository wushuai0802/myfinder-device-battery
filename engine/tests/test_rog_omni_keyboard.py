"""One observed receiver layout, exercising read-only and fail-closed paths."""
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
from providers import rog_omni_keyboard as R


def entry(page, path, physical="pnp:test", **kwargs):
    return dict(vendor_id=R.VID, product_id=R.RECEIVER, interface_number=2, usage_page=page,
                usage=1, input_report_length=64, output_report_length=64,
                physical_id=physical, path=path, **kwargs)


def paired(entries=((0x1AB0, 2, 4),)):
    data = [1, 0xA0, 0, len(entries), 0]
    for pid, report, kind in entries:
        data.extend([pid & 0xFF, pid >> 8, report, kind])
    return bytes(data + [0] * (64 - len(data)))


def battery(level=46, charging=0):
    return bytes([2, 0x12, 1, 0, 0, 0, level, 0xFF, 0, charging] + [0] * 54)


class Bus:
    def __init__(self, response=None, descriptors=None):
        self.response = response or {b"receiver": paired(), b"keyboard": battery()}
        self.descriptors = descriptors or {b"receiver": R.expected_descriptor(0xFF02, 1),
                                            b"keyboard": R.expected_descriptor(0xFF00, 2)}
        self.writes, self.opened, self.closed = [], [], []

    def device(self):
        bus = self
        class Device:
            def open_path(self, path):
                self.path = path
                self.used = False
                bus.opened.append(path)
            def get_report_descriptor(self):
                return bus.descriptors[self.path]
            def write(self, value):
                bus.writes.append((self.path, bytes(value)))
                return len(value)
            def read(self, length, timeout):
                if self.used:
                    return []
                self.used = True
                return bus.response.get(self.path, b"")
            def close(self):
                bus.closed.append(self.path)
        return Device()


class RogOmniKeyboardTests(unittest.TestCase):
    def setUp(self):
        self.infos = [entry(0xFF02, b"receiver"), entry(0xFF00, b"keyboard")]

    def run_poll(self, infos=None, bus=None):
        bus = bus or Bus()
        provider = R.RogOmniKeyboardProvider()
        with patch.object(R.hidlist, "enumerate", return_value=self.infos if infos is None else infos), \
             patch.object(R.hid, "device", side_effect=bus.device):
            result = provider.poll()
        return result, provider, bus

    def test_observed_device_and_only_two_get_requests(self):
        result, provider, bus = self.run_poll()
        self.assertEqual(len(result), 1)
        self.assertEqual((result[0].name, result[0].level, result[0].kind), (R.NAME, 46, "keyboard"))
        self.assertTrue(result[0].connection_known)
        self.assertFalse(result[0].charging_known)
        self.assertEqual(provider.candidate_count, 1)
        self.assertEqual(bus.writes, [(b"receiver", bytes([1, 0xA0, 0] + [0] * 61)),
                                      (b"keyboard", bytes([2, 0x12, 1] + [0] * 61))])
        self.assertEqual(bus.opened, bus.closed)

    def test_unresolved_wrong_or_ambiguous_collections_do_not_open(self):
        cases = [[entry(0xFF02, b"r", physical="collection:unknown"), self.infos[1]],
                 [self.infos[0], entry(0xFF00, b"k", physical="pnp:other")],
                 self.infos + [entry(0xFF00, b"other")],
                 [self.infos[0], dict(self.infos[1], output_report_length=63)],
                 [self.infos[0], dict(self.infos[1], interface_number=3)],
                 [self.infos[0], dict(self.infos[1], vendor_id=0x1234)]]
        for infos in cases:
            with self.subTest(infos=infos):
                result, _, bus = self.run_poll(infos)
                self.assertEqual(result, [])
                self.assertEqual(bus.opened, [])
                self.assertEqual(bus.writes, [])

    def test_unproven_descriptor_never_sends(self):
        for path in (b"receiver", b"keyboard"):
            bus = Bus()
            bus.descriptors[path] = bus.descriptors[path][:-1]
            result, _, bus = self.run_poll(bus=bus)
            self.assertEqual(result, [])
            self.assertEqual(bus.writes, [])
            self.assertEqual(bus.opened, bus.closed)

    def test_other_model_wrong_slot_duplicate_or_type_never_get_battery(self):
        cases = [(), ((0x1A85, 2, 4),), ((0x1AB0, 3, 4),), ((0x1AB0, 2, 5),),
                 ((0x1AB0, 2, 4), (0x1AB0, 2, 4)), ((0x1AB0, 2, 4), (0x1A94, 2, 5))]
        for entries in cases:
            with self.subTest(entries=entries):
                bus = Bus({b"receiver": paired(entries), b"keyboard": battery()})
                result, provider, bus = self.run_poll(bus=bus)
                self.assertEqual(result, [])
                self.assertEqual(provider.candidate_count, 0)
                self.assertEqual(len(bus.writes), 1)
                self.assertEqual(bus.opened, bus.closed)

    def test_foreign_short_echo_standby_and_invalid_replies_are_not_measurements(self):
        for invalid in (b"", battery()[:-1], bytes([2, 0x12, 1] + [0] * 61),
                        battery(101), battery(0), battery(0, 1), battery(46, 2), bytes([3, 0x12, 1] + [0] * 61)):
            with self.subTest(invalid=invalid):
                bus = Bus({b"receiver": paired(), b"keyboard": invalid})
                result, provider, bus = self.run_poll(bus=bus)
                self.assertEqual(result, [])
                self.assertEqual(provider.candidate_count, 1)
                self.assertEqual(len(bus.writes), 2)
                self.assertEqual(bus.opened, bus.closed)


if __name__ == "__main__":
    unittest.main()
