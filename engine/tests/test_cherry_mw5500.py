"""Tests for Grade-C CHERRY MW5500 (046A:0133) battery reader."""
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
from providers import cherry_mw5500 as MW
from standardhid import Caps
from test_battery_caps import FakeApi, caps


class _FakeBus:
    def __init__(self, replies=None, fail_feature=False, short_feature=False):
        self.replies = list(replies or [])
        self.fail_feature = fail_feature
        self.short_feature = short_feature
        self.opened = []
        self.features_sent = []
        self.reads = []
        self.closed = 0
        self.writes = []

    def __call__(self):
        bus = self

        class _Dev:
            def __init__(self):
                self._path = None

            def open_path(self, path):
                self._path = path
                bus.opened.append(path)

            def send_feature_report(self, data):
                bus.features_sent.append((self._path, list(data)))
                if bus.fail_feature:
                    raise OSError("feature send failed")
                return len(data) - 1 if bus.short_feature else len(data)

            def read(self, length, timeout_ms=0):
                bus.reads.append((self._path, length, timeout_ms))
                return list(bus.replies.pop(0)) if bus.replies else []

            def write(self, data):
                bus.writes.append(list(data))
                raise AssertionError("MW5500 battery query must not call write()")

            def close(self):
                bus.closed += 1

        return _Dev()


def _mw5500_info(**changes):
    return dict({
        "vendor_id": 0x046A,
        "product_id": 0x0133,
        "usage_page": 0xFF10,
        "usage": 0x0001,
        "physical_id": "pnp:usb\\vid_046a&pid_0133\\5&abcd&0&2",
        "path": "\\\\?\\hid#vid_046a&pid_0133&mi_01#7&1234&0&0000",
        "feature_report_length": 9,
        "feature_report_sizes": {"0": 9},
        "input_report_length": 65,
        "input_report_sizes": {"0": 65},
    }, **changes)


def _mw5500_reply(level=85, charging=False, length=64):
    # Unnumbered input omits report ID 0 -> 64 bytes on wire; raw[8] holds chargeFlag(0x80)|level
    raw = [0] * length
    raw[0] = 0x82
    raw[1] = 0x03  # report rate
    raw[2] = 0x01  # dpi index
    raw[8] = level | (0x80 if charging else 0x00)
    return raw


class CherryMW5500Tests(unittest.TestCase):
    def test_make_request_and_parse_reply(self):
        self.assertEqual(MW.make_request(9), [0x00, 0x82, 0, 0, 0, 0, 0, 0, 0])
        self.assertEqual(MW.parse_reply(_mw5500_reply(85, False), 65), (85, False, ""))
        self.assertEqual(MW.parse_reply(_mw5500_reply(100, True), 65), (100, True, ""))
        # Accept minimum unnumbered length 9 (input_report_length=10)
        self.assertEqual(MW.parse_reply(_mw5500_reply(42, True, length=9), 10), (42, True, ""))
        # Reject wrong length, 0%, >100%, all-zero, echo-only, bool
        self.assertIsNone(MW.parse_reply(_mw5500_reply(85, False, length=63), 65))
        self.assertIsNone(MW.parse_reply(_mw5500_reply(0, False), 65))
        self.assertIsNone(MW.parse_reply(_mw5500_reply(101, False), 65))
        self.assertIsNone(MW.parse_reply([0] * 64, 65))
        self.assertIsNone(MW.parse_reply([0x82] + [0] * 63, 65))
        bad = _mw5500_reply(85, False)
        bad[8] = True
        self.assertIsNone(MW.parse_reply(bad, 65))

    def test_poll_sends_feature_0x82_and_decodes_charging_and_level(self):
        info = _mw5500_info()
        bus = _FakeBus(replies=[[0] * 64, _mw5500_reply(64, True)])
        provider = MW.CherryMW5500Provider()
        with patch.object(MW.hidlist, "enumerate", return_value=[info]), \
             patch.object(MW.hid, "device", bus):
            statuses = provider.poll()
        self.assertEqual(len(statuses), 1)
        s = statuses[0]
        self.assertEqual(s.name, "CHERRY MW5500")
        self.assertEqual(s.level, 64)
        self.assertTrue(s.charging)
        self.assertTrue(s.charging_known)
        self.assertEqual(s.kind, "mouse")
        self.assertEqual(s.verification_level, "C")
        self.assertEqual(bus.features_sent, [(info["path"], [0x00, 0x82, 0, 0, 0, 0, 0, 0, 0])])
        self.assertEqual(bus.reads[0][1], 64)
        self.assertEqual(bus.closed, 1)

    def test_unproven_or_ambiguous_never_opens(self):
        cases = [
            [_mw5500_info(physical_id="collection:2")],
            [_mw5500_info(usage_page=0xFF11)],
            [_mw5500_info(feature_report_sizes={"0": 8}, feature_report_length=8)],
            [_mw5500_info(input_report_sizes={"0": 9}, input_report_length=9)],
            [_mw5500_info(), _mw5500_info()],
        ]
        for inv in cases:
            bus = _FakeBus()
            provider = MW.CherryMW5500Provider()
            with patch.object(MW.hidlist, "enumerate", return_value=inv), \
                 patch.object(MW.hid, "device", bus):
                self.assertEqual(provider.poll(), [])
            self.assertEqual(bus.opened, [])
            self.assertEqual(bus.features_sent, [])

    def test_descriptor_proof_unnumbered_9_and_65(self):
        f_api = FakeApi([{"page": 0xFF10, "report": 0, "field": 0, "is_range": 0,
                          "usage_min": 1, "usage_max": 1, "report_count": 8}])
        fc = Caps()
        fc.fb = 1
        fc.feature = 9
        self.assertEqual(battery_hid.byte_report_sizes(f_api, None, fc, 2, allow_fixed_usage=True), {"0": 9})

        i_api = FakeApi([{"page": 0xFF10, "report": 0, "field": 0, "is_range": 0,
                          "usage_min": 1, "usage_max": 1, "report_count": 64}])
        ic = caps(ib=1, size=65)
        self.assertEqual(battery_hid.byte_report_sizes(i_api, None, ic, 0, allow_fixed_usage=True), {"0": 65})

    def test_integration_server_catalog_and_known_ids(self):
        self.assertIn(("cherry_mw5500", "CherryMW5500Provider"), server.PROVIDERS)
        self.assertNotIn("cherry_mw5500", server.NO_CHARGING_EVIDENCE)
        self.assertNotIn("cherry_mw5500", server.DPI_PROVIDERS)
        self.assertEqual(catalog.validate_catalog()["errors"], [])
        devices = {(d["vendorId"], d["productId"]): d for d in known_ids.build_index()["devices"]}
        self.assertEqual(devices[(0x046A, 0x0133)]["provider"], "cherry_mw5500")


if __name__ == "__main__":
    unittest.main()
