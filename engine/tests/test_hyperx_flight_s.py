"""Tests for Grade-C HyperX Cloud Flight S (0951:16EA) battery reader."""
from __future__ import annotations

import os
import sys
import unittest
from unittest.mock import patch

ENGINE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
if ENGINE_DIR not in sys.path:
    sys.path.insert(0, ENGINE_DIR)

import _hidstub  # noqa: F401
import battery_hid
import catalog
import known_ids
import server
from providers import hyperx_flight_s as FS
from test_battery_caps import FakeApi, caps


class _FakeBus:
    def __init__(self, replies=None, fail_write=False, short_write=False):
        self.replies = list(replies or [])
        self.fail_write = fail_write
        self.short_write = short_write
        self.opened = []
        self.written = []
        self.closed = 0
        self.forbidden = []

    def __call__(self):
        bus = self

        class _Dev:
            def __init__(self):
                self._path = None

            def open_path(self, path):
                self._path = path
                bus.opened.append(path)

            def write(self, data):
                bus.written.append((self._path, list(data)))
                if bus.fail_write:
                    raise OSError("write error")
                return len(data) - 1 if bus.short_write else len(data)

            def read(self, length, timeout_ms=0):
                return list(bus.replies.pop(0)) if bus.replies else []

            def send_feature_report(self, data):
                bus.forbidden.append("send_feature_report")
                raise AssertionError("Flight S must not send_feature_report")

            def get_feature_report(self, report_id, size):
                bus.forbidden.append("get_feature_report")
                raise AssertionError("Flight S must not get_feature_report")

            def close(self):
                bus.closed += 1

        return _Dev()


def _flight_s_info(**changes):
    return dict({
        "vendor_id": 0x0951,
        "product_id": 0x16EA,
        "usage_page": 0xFF13,
        "usage": 0x0001,
        "physical_id": "pnp:usb\\vid_0951&pid_16ea\\5&1234&0&1",
        "path": "\\\\?\\hid#vid_0951&pid_16ea&mi_03#7&abcd&0&0000",
        "output_report_length": 62,
        "output_report_sizes": {"6": 62},
        "input_report_length": 62,
        "input_report_sizes": {"11": 62, "10": 12},
    }, **changes)


def _battery_reply(percent=82, mv=3910, rid=0x0B, length=62, msg_type=0x02):
    raw = [0] * length
    raw[0] = rid
    raw[3] = msg_type
    raw[5] = (mv >> 8) & 0xFF
    raw[6] = mv & 0xFF
    raw[7] = percent
    return raw


class HyperXFlightSTests(unittest.TestCase):
    def test_make_request_and_parse_reply_variants(self):
        req = FS.make_request(62)
        self.assertEqual(len(req), 62)
        self.assertEqual(
            req[:16],
            [0x06, 0x00, 0x02, 0x00, 0x9A, 0x00, 0x00, 0x68, 0x4A, 0x8E, 0x0A, 0x00, 0x00, 0x00, 0xBB, 0x02],
        )
        self.assertEqual(req[16:], [0] * 46)
        sizes = {"11": 62, "6": 12}
        # Windows 62B reply (rid 0x0B) & compact 12B reply (rid 0x06)
        self.assertEqual(FS.parse_reply(_battery_reply(76, 3890, rid=0x0B, length=62), sizes), (76, False, ""))
        self.assertEqual(FS.parse_reply(_battery_reply(45, 3750, rid=0x06, length=12), sizes), (45, False, ""))
        # Reject startup 0% glitch while voltage >= 3500 mV, but allow genuine 0% at 3328 mV
        self.assertIsNone(FS.parse_reply(_battery_reply(0, 3770), sizes))
        self.assertEqual(FS.parse_reply(_battery_reply(0, 3328), sizes), (0, False, ""))
        # Reject bad IDs, wrong length, zero voltage, out-of-range, bool
        self.assertIsNone(FS.parse_reply(_battery_reply(50, 3800, rid=0x0A, length=12), sizes))
        self.assertIsNone(FS.parse_reply(_battery_reply(50, 3800, rid=0x0B, length=20), sizes))
        self.assertIsNone(FS.parse_reply(_battery_reply(50, 0), sizes))
        self.assertIsNone(FS.parse_reply(_battery_reply(101, 3800), sizes))
        self.assertIsNone(FS.parse_reply([0x0B, 0, 0, 2, 0, 15, 0, True] + [0] * 54, sizes))

    def test_poll_queries_ff13_and_handles_power_off(self):
        info = _flight_s_info()
        bus = _FakeBus(replies=[
            [0x0A, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0],  # unrelated report skipped
            _battery_reply(88, 3940),
        ])
        provider = FS.HyperXFlightSProvider()
        with patch.object(FS.hidlist, "enumerate", return_value=[info]), \
             patch.object(FS.hid, "device", bus):
            statuses = provider.poll()
        self.assertEqual(len(statuses), 1)
        s = statuses[0]
        self.assertEqual(s.name, "HyperX Cloud Flight S")
        self.assertEqual(s.level, 88)
        self.assertFalse(s.charging)
        self.assertFalse(s.charging_known)
        self.assertEqual(s.kind, "headset")
        self.assertEqual(s.verification_level, "C")
        self.assertEqual(len(bus.written), 1)
        self.assertEqual(bus.written[0][1], FS.make_request(62))
        self.assertEqual(bus.closed, 1)

        # Power-off event (byte3=0x01, byte4=0x03) returns no reading
        off_evt = [0x0B, 0x00, 0x00, 0x01, 0x03] + [0] * 57
        bus_off = _FakeBus(replies=[off_evt])
        with patch.object(FS.hidlist, "enumerate", return_value=[info]), \
             patch.object(FS.hid, "device", bus_off):
            self.assertEqual(provider.poll(), [])
        self.assertIn("powered off", provider.diagnostics()[-1])

    def test_unproven_or_ambiguous_collection_never_opens(self):
        cases = [
            [_flight_s_info(physical_id="collection:1")],
            [_flight_s_info(usage_page=0x000C, usage=0x0001)],
            [_flight_s_info(output_report_sizes={})],
            [_flight_s_info(input_report_sizes={"11": 20}, input_report_length=62)],
            [_flight_s_info(), _flight_s_info()],
        ]
        for inv in cases:
            bus = _FakeBus()
            provider = FS.HyperXFlightSProvider()
            with patch.object(FS.hidlist, "enumerate", return_value=inv), \
                 patch.object(FS.hid, "device", bus):
                self.assertEqual(provider.poll(), [])
            self.assertEqual(bus.opened, [])
            self.assertEqual(bus.written, [])

    def test_integration_server_catalog_and_known_ids(self):
        self.assertIn(("hyperx_flight_s", "HyperXFlightSProvider"), server.PROVIDERS)
        self.assertIn("hyperx_flight_s", server.NO_CHARGING_EVIDENCE)
        self.assertNotIn("hyperx_flight_s", server.DPI_PROVIDERS)
        self.assertEqual(catalog.validate_catalog()["errors"], [])
        devices = {(d["vendorId"], d["productId"]): d for d in known_ids.build_index()["devices"]}
        self.assertEqual(devices[(0x0951, 0x16EA)]["provider"], "hyperx_flight_s")


if __name__ == "__main__":
    unittest.main()
