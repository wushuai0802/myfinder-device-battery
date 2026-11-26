"""Tests for Rapoo VT9 PRO read-only status (Feature 0x2A) and passive 0xBB input."""
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
from providers import rapoo_vt9 as VT9
from standardhid import Caps
from test_battery_caps import FakeApi, caps


class _FakeBus:
    def __init__(self, responses=None, fail_open=False, fail_feature=False, fail_read=False):
        self.responses = dict(responses or {})
        self.fail_open = fail_open
        self.fail_feature = fail_feature
        self.fail_read = fail_read
        self.opened_paths = []
        self.closed_count = 0
        self.feature_calls = []
        self.read_calls = []
        self.writes = []

    def __call__(self):
        bus = self

        class _Dev:
            def __init__(self):
                self._path = None

            def open_path(self, path):
                if bus.fail_open:
                    raise OSError("open failed")
                self._path = path
                bus.opened_paths.append(path)

            def get_feature_report(self, report_id, size):
                bus.feature_calls.append((self._path, report_id, size))
                if bus.fail_feature:
                    raise OSError("get_feature_report failed")
                queue = bus.responses.get(("feature", self._path))
                if not queue:
                    return []
                if isinstance(queue[0], int):
                    return list(queue)
                return list(queue.pop(0))

            def read(self, length, timeout_ms=0):
                bus.read_calls.append((self._path, length, timeout_ms))
                if bus.fail_read:
                    raise OSError("read failed")
                queue = bus.responses.get(("input", self._path), [])
                if queue:
                    return list(queue.pop(0))
                return []

            def write(self, data):
                bus.writes.append(("write", self._path, bytes(data)))
                raise AssertionError("rapoo_vt9 must never call write()")

            def send_feature_report(self, data):
                bus.writes.append(("send_feature_report", self._path, bytes(data)))
                raise AssertionError("rapoo_vt9 must never call send_feature_report()")

            def close(self):
                bus.closed_count += 1

        return _Dev()


def _vt9_feature_info(pid=0x1221, suffix="col08", sizes=None, maximum=11,
                      physical=None, path=None):
    phys = physical if physical is not None else f"pnp:usb\\vid_24ae&pid_{pid:04x}\\5&1234&0&1"
    dev_path = path if path is not None else f"\\\\?\\hid#vid_24ae&pid_{pid:04x}&{suffix}#7&abcd&0&0000"
    return {
        "vendor_id": 0x24AE,
        "product_id": pid,
        "usage_page": 0xFF0B,
        "usage": 0x0104,
        "path": dev_path,
        "physical_id": phys,
        "feature_report_length": maximum,
        "feature_report_sizes": {"42": 11} if sizes is None else sizes,
    }


def _vt9_input_info(pid=0x1221, suffix="col07", sizes=None, maximum=7,
                    physical=None, path=None):
    phys = physical if physical is not None else f"pnp:usb\\vid_24ae&pid_{pid:04x}\\5&1234&0&1"
    dev_path = path if path is not None else f"\\\\?\\hid#vid_24ae&pid_{pid:04x}&{suffix}#7&abcd&0&0000"
    return {
        "vendor_id": 0x24AE,
        "product_id": pid,
        "usage_page": 0xFF00,
        "usage": 0x000E,
        "path": dev_path,
        "physical_id": phys,
        "input_report_length": maximum,
        "input_report_sizes": {"187": 7} if sizes is None else sizes,
    }


class RapooVT9ParserTests(unittest.TestCase):
    def test_parse_feature_battery_valid_and_rejects(self):
        # OpenMouse captures: 2a 01 00 00 04 11 20 00 00 62 00 (wireless 98%) & 21 50 ... (wired 98%)
        wireless = [0x2A, 0x01, 0x00, 0x00, 0x04, 0x11, 0x20, 0x00, 0x00, 0x62, 0x00]
        wired = [0x2A, 0x01, 0x00, 0x00, 0x04, 0x21, 0x50, 0x00, 0x00, 0x62, 0x00]
        self.assertEqual(VT9.parse_feature_battery(wireless, 11), (98, False, False))
        self.assertEqual(VT9.parse_feature_battery(wired, 11), (98, False, False))
        self.assertEqual(VT9.parse_feature_battery([0x2A, 0x01, 0, 0, 0, 0, 0, 0, 0, 0], 10), (0, False, False))
        self.assertEqual(VT9.parse_feature_battery([0x2A, 0x01, 0, 0, 0, 0, 0, 0, 0, 100], 10), (100, False, False))
        # Reject wrong ID, all-zero header, wrong length, out of range, bool
        self.assertIsNone(VT9.parse_feature_battery([], 11))
        self.assertIsNone(VT9.parse_feature_battery(wireless[:9], 9))
        self.assertIsNone(VT9.parse_feature_battery(wireless[:10], 11))
        self.assertIsNone(VT9.parse_feature_battery([0x2B] + wireless[1:], 11))
        self.assertIsNone(VT9.parse_feature_battery([0x2A] + [0] * 10, 11))
        self.assertIsNone(VT9.parse_feature_battery([0x2A, 0x01, 0, 0, 0, 0, 0, 0, 0, 101, 0], 11))
        self.assertIsNone(VT9.parse_feature_battery([0x2A, 0x01, 0, 0, 0, 0, 0, 0, 0, True, 0], 11))

    def test_parse_input_battery_valid_and_rejects(self):
        self.assertEqual(VT9.parse_input_battery([0xBB, 0xB0, 0x51, 0x20, 0x03, 0x01, 0x62]), (98, False, False))
        self.assertEqual(VT9.parse_input_battery([0xBB, 0xB0, 0x51, 0x20, 0x03, 0x02, 0x00]), (0, False, False))
        self.assertIsNone(VT9.parse_input_battery([0xBB, 0xB0, 0x51, 0x20, 0x03, 0x01]))
        self.assertIsNone(VT9.parse_input_battery([0xBA, 0xB0, 0x51, 0x20, 0x03, 0x01, 0x62]))
        self.assertIsNone(VT9.parse_input_battery([0xBB, 0xB1, 0x51, 0x20, 0x03, 0x01, 0x62]))
        self.assertIsNone(VT9.parse_input_battery([0xBB, 0xB0, 0x51, 0x20, 0x03, 0x03, 0x62]))
        self.assertIsNone(VT9.parse_input_battery([0xBB, 0xB0, 0x51, 0x20, 0x03, 0x01, 101]))
        self.assertIsNone(VT9.parse_input_battery([0xBB, 0xB0, 0x51, 0x20, 0x03, 0x01, False]))

    def test_descriptor_proof_computes_exact_report_sizes(self):
        f_api = FakeApi([{"page": 0xFF0B, "report": 0x2A, "field": 0, "is_range": 0,
                          "usage_min": 0x0104, "usage_max": 0x0104, "report_count": 10}])
        f_caps = Caps()
        f_caps.fb = 1
        f_caps.feature = 11
        self.assertEqual(
            battery_hid.byte_report_sizes(f_api, None, f_caps, 2, allow_fixed_usage=True),
            {"42": 11},
        )
        i_api = FakeApi([{"page": 0xFF00, "report": 0xBB, "field": 0, "is_range": 0,
                          "usage_min": 0x000E, "usage_max": 0x000E, "report_count": 6}])
        i_caps = caps(ib=1, size=7)
        self.assertEqual(
            battery_hid.byte_report_sizes(i_api, None, i_caps, 0, allow_fixed_usage=True),
            {"187": 7},
        )
        # Strict raw HID descriptor fallback: FF0B:0104 Feature 0x2A (10 bytes + ID = 11)
        raw_desc = bytes([
            0x06, 0x0B, 0xFF,  # Usage Page (0xFF0B)
            0x0A, 0x04, 0x01,  # Usage (0x0104)
            0xA1, 0x01,        # Collection (Application)
            0x85, 0x2A,        #   Report ID (0x2A)
            0x0A, 0x04, 0x01,  #   Usage (0x0104)
            0x15, 0x00,        #   Logical Minimum (0)
            0x26, 0xFF, 0x00,  #   Logical Maximum (255)
            0x75, 0x08,        #   Report Size (8)
            0x95, 0x0A,        #   Report Count (10)
            0xB1, 0x02,        #   Feature (Data,Var,Abs)
            0xC0,              # End Collection
        ])
        parsed = battery_hid.parse_report_descriptor(raw_desc)
        self.assertEqual(parsed[(0xFF0B, 0x0104)]["feature"], {"42": 11})


class RapooVT9ProviderTests(unittest.TestCase):
    def test_primary_feature_read_all_verified_pids_zero_writes(self):
        for pid, model_name in ((0x1221, "Rapoo VT9 PRO Dual 8K"), (0x1205, "Rapoo VT9 PRO"), (0x4405, "Rapoo VT9 PRO")):
            with self.subTest(pid=hex(pid)):
                f_info = _vt9_feature_info(pid=pid)
                i_info = _vt9_input_info(pid=pid)
                bus = _FakeBus({
                    ("feature", f_info["path"]): [0x2A, 0x01, 0x00, 0x00, 0x04, 0x11, 0x20, 0x00, 0x00, 0x5A, 0x00],
                })
                provider = VT9.RapooVT9Provider()
                with patch.object(VT9.hidlist, "enumerate", return_value=[i_info, f_info]), \
                     patch.object(VT9.hid, "device", bus):
                    statuses = provider.poll()
                self.assertEqual(len(statuses), 1)
                s = statuses[0]
                self.assertEqual(s.name, model_name)
                self.assertEqual(s.level, 90)
                self.assertTrue(s.online)
                self.assertTrue(s.connection_known)
                self.assertFalse(s.charging)
                self.assertFalse(s.charging_known)
                self.assertEqual(s.kind, "mouse")
                self.assertEqual(s.verification_level, "C")
                self.assertEqual(s.source, "rapoo_vt9")
                self.assertEqual(bus.writes, [])
                self.assertEqual(len(bus.feature_calls), 1)
                self.assertEqual(bus.read_calls, [])
                self.assertEqual(bus.closed_count, 1)

    def test_passive_0xbb_fallback_and_releases_handle_when_feature_recovers(self):
        f_info = _vt9_feature_info(pid=0x1221)
        i_info = _vt9_input_info(pid=0x1221)
        bus = _FakeBus({
            # First poll: feature returns all-zero -> falls back to passive 0xBB
            ("feature", f_info["path"]): [
                [0x2A] + [0] * 10,
                # Second poll: feature succeeds cleanly -> should release passive handle
                [0x2A, 0x01, 0x00, 0x00, 0x04, 0x11, 0x20, 0x00, 0x00, 0x55, 0x00],
            ],
            ("input", i_info["path"]): [
                [0x01, 0x00],  # unrelated report ignored
                [0xBB, 0xB0, 0x51, 0x20, 0x03, 0x01, 0x62],
            ],
        })
        provider = VT9.RapooVT9Provider()
        try:
            with patch.object(VT9.hidlist, "enumerate", return_value=[f_info, i_info]), \
                 patch.object(VT9.hid, "device", bus):
                first = provider.poll()
                self.assertEqual([s.level for s in first], [98])
                self.assertEqual(len(provider._handles), 1)

                second = provider.poll()
                self.assertEqual([s.level for s in second], [85])
                self.assertEqual(provider._handles, {})
                self.assertEqual(bus.writes, [])
        finally:
            provider.close()

    def test_multi_report_feature_collection_uses_maximum_buffer(self):
        f_info = _vt9_feature_info(pid=0x1221, sizes={"42": 11, "43": 65}, maximum=65)
        padded = [0x2A, 0x01, 0x00, 0x00, 0x04, 0x11, 0x20, 0x00, 0x00, 0x62, 0x00] + [0] * 54
        bus = _FakeBus({("feature", f_info["path"]): [padded]})
        provider = VT9.RapooVT9Provider()
        with patch.object(VT9.hidlist, "enumerate", return_value=[f_info]), \
             patch.object(VT9.hid, "device", bus):
            statuses = provider.poll()
        self.assertEqual([s.level for s in statuses], [98])
        self.assertEqual(bus.feature_calls, [(f_info["path"], 0x2A, 65)])
        self.assertEqual(bus.writes, [])

    def test_case_variant_duplicate_path_rejected(self):
        upper = _vt9_feature_info(pid=0x1221, path="\\\\?\\HID#VID_24AE&PID_1221&COL08#7&ABCD&0&0000")
        lower = _vt9_input_info(pid=0x1221, path="\\\\?\\hid#vid_24ae&pid_1221&col08#7&abcd&0&0000")
        bus = _FakeBus()
        provider = VT9.RapooVT9Provider()
        with patch.object(VT9.hidlist, "enumerate", return_value=[upper, lower]), \
             patch.object(VT9.hid, "device", bus):
            statuses = provider.poll()
        self.assertEqual(statuses, [])
        self.assertEqual(bus.opened_paths, [])

    def test_unproven_identity_descriptor_and_excluded_pids_not_opened(self):
        bad_cases = [
            ("excluded_pid_185a", [_vt9_feature_info(pid=0x185A, path="\\\\?\\hid#vid_24ae&pid_185a&col08#1")]),
            ("collection_physical", [_vt9_feature_info(physical="collection:123")]),
            ("wrong_usb_pid", [_vt9_feature_info(physical="pnp:usb\\vid_24ae&pid_185a\\5&1&0&1")]),
            ("wrong_hid_path", [_vt9_feature_info(path="\\\\?\\hid#vid_24ae&pid_185a&col08#1")]),
            ("unproven_sizes", [_vt9_feature_info(sizes={})]),
            ("wrong_max_length", [_vt9_feature_info(sizes={"42": 11}, maximum=12)]),
            ("short_feature_size", [_vt9_feature_info(sizes={"42": 9}, maximum=9)]),
            ("duplicate_collection", [_vt9_feature_info(), _vt9_feature_info()]),
            ("mixed_pids", [
                _vt9_feature_info(pid=0x1221),
                _vt9_input_info(pid=0x1205, physical="pnp:usb\\vid_24ae&pid_1221\\5&1234&0&1"),
            ]),
        ]
        for label, inv in bad_cases:
            with self.subTest(label=label):
                bus = _FakeBus()
                provider = VT9.RapooVT9Provider()
                with patch.object(VT9.hidlist, "enumerate", return_value=inv), \
                     patch.object(VT9.hid, "device", bus):
                    statuses = provider.poll()
                self.assertEqual(statuses, [])
                self.assertEqual(bus.opened_paths, [])
                self.assertEqual(bus.writes, [])
                for line in provider.diagnostics():
                    self.assertNotIn("\\\\?\\", line)

    def test_history_retained_while_present_and_cleared_on_unplug(self):
        f_info = _vt9_feature_info(pid=0x1221)
        bus = _FakeBus({
            ("feature", f_info["path"]): [
                [0x2A, 0x01, 0x00, 0x00, 0x04, 0x11, 0x20, 0x00, 0x00, 0x4B, 0x00],
                [],  # second poll: short/empty feature reply -> retains history
            ],
        })
        provider = VT9.RapooVT9Provider()
        try:
            with patch.object(VT9.hidlist, "enumerate", return_value=[f_info]), \
                 patch.object(VT9.hid, "device", bus):
                live = provider.poll()
                self.assertEqual(len(live), 1)
                self.assertEqual(live[0].level, 75)
                self.assertTrue(live[0].online)

                cached = provider.poll()
                self.assertEqual(len(cached), 1)
                self.assertEqual(cached[0].level, 75)
                self.assertFalse(cached[0].online)

            with patch.object(VT9.hidlist, "enumerate", return_value=[]), \
                 patch.object(VT9.hid, "device", bus):
                gone = provider.poll()
                self.assertEqual(gone, [])
                self.assertEqual(provider._last, {})
        finally:
            provider.close()

    def test_1601_passive_ff00_0002_opens_unique_bb_ignores_bc_and_never_writes(self):
        col05 = _vt9_input_info(pid=0x1601, suffix="mi_01&col05", sizes={"187": 7}, maximum=7)
        col05["usage_page"], col05["usage"] = 0xFF00, 0x0002
        col06 = _vt9_input_info(pid=0x1601, suffix="mi_01&col06", sizes={"188": 12}, maximum=12)
        col06["usage_page"], col06["usage"] = 0xFF00, 0x0002
        col04 = _vt9_input_info(pid=0x1601, suffix="mi_01&col04", sizes={"186": 32}, maximum=32)
        f_col = _vt9_feature_info(pid=0x1601)
        bus = _FakeBus({
            ("input", col05["path"]): [[0xBB, 0xB0, 0x43, 0xDC, 0x05, 0x01, 0x60]],
        })
        provider = VT9.RapooVT9Provider()
        try:
            with patch.object(VT9.hidlist, "enumerate", return_value=[col04, col06, col05, f_col]), \
                 patch.object(VT9.hid, "device", bus):
                statuses = provider.poll()
            self.assertEqual(len(statuses), 1)
            self.assertEqual((statuses[0].name, statuses[0].level, statuses[0].charging_known, statuses[0].kind),
                             ("Rapoo VT9 PRO", 96, False, "mouse"))
            self.assertEqual(bus.opened_paths, [col05["path"]])
            self.assertEqual(bus.feature_calls, [])
            self.assertEqual(bus.writes, [])
        finally:
            provider.close()

        # Fail closed when sibling FF00:0002 has unproven sizes or duplicates BB
        for bad in (
            [col05, dict(col06, input_report_sizes=None)],
            [col05, dict(col06, input_report_sizes={"187": 7}, input_report_length=7)],
        ):
            bad_bus = _FakeBus()
            bad_provider = VT9.RapooVT9Provider()
            with patch.object(VT9.hidlist, "enumerate", return_value=bad), \
                 patch.object(VT9.hid, "device", bad_bus):
                self.assertEqual(bad_provider.poll(), [])
            self.assertEqual(bad_bus.opened_paths, [])
            self.assertEqual(bad_bus.writes, [])


class RapooVT9IntegrationTests(unittest.TestCase):
    def test_server_recognition_and_no_dpi(self):
        self.assertNotIn("rapoo_vt9", server.DPI_PROVIDERS)
        self.assertIn("rapoo_vt9", server.NO_CHARGING_EVIDENCE)
        self.assertEqual(
            server.recognized_candidate_count(
                "rapoo_vt9",
                [
                    {"vendor_id": 0x24AE, "product_id": 0x1221},
                    {"vendor_id": 0x24AE, "product_id": 0x1205},
                    {"vendor_id": 0x24AE, "product_id": 0x4405},
                    {"vendor_id": 0x24AE, "product_id": 0x1601},
                    {"vendor_id": 0x24AE, "product_id": 0x185A},
                    {"vendor_id": 0x24AE, "product_id": 0x1411},
                    {"vendor_id": 0x24AE, "product_id": 0x1872},
                ],
            ),
            4,
        )

    def test_catalog_and_device_pack_include_vt9_only(self):
        self.assertEqual(catalog.validate_catalog()["errors"], [])
        index = known_ids.build_index()
        by_key = {(r["vendorId"], r["productId"]): r for r in index["devices"]}
        for pid in (0x1221, 0x1205, 0x4405, 0x1601):
            self.assertEqual(by_key[(0x24AE, pid)]["provider"], "rapoo_vt9")
        self.assertNotIn((0x24AE, 0x185A), by_key)


if __name__ == "__main__":
    unittest.main()
