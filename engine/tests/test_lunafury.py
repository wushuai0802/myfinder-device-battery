"""Tests for Grade-C Lunafury TYPE33EVO (373E:0084) battery reader."""
from __future__ import annotations

import os
import sys
import unittest
from unittest.mock import patch

ENGINE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
if ENGINE_DIR not in sys.path:
    sys.path.insert(0, ENGINE_DIR)

import _hidstub  # noqa: F401
import catalog
import device_pack
import known_ids
import server
from providers import lunafury as LF


def _col(page, usage, feature_len=0, feature_sizes=None, suffix="col04"):
    return {
        "vendor_id": 0x373E,
        "product_id": 0x0084,
        "product_string": "TYPE33EL",
        "usage_page": page,
        "usage": usage,
        "physical_id": r"pnp:usb\vid_373e&pid_0084\5&1234&0&1",
        "path": f"\\\\?\\hid#vid_373e&pid_0084&{suffix}#7&abcd&0&0000",
        "feature_report_length": feature_len,
        "feature_report_sizes": {} if feature_sizes is None and feature_len == 0 else feature_sizes,
    }


def _reply(percent=85, charging=0, report_id=True):
    frame = [0x00, 0xA1, 0x00, 0x00, 0x02, 0x00, 0x83, charging, percent] + [0] * 56
    return frame if report_id else frame[1:]


class _FakeBus:
    def __init__(self, feature_replies=None, write_len=65, fail_send=False):
        self.feature_replies = list(feature_replies or [])
        self.write_len = write_len
        self.fail_send = fail_send
        self.opened = []
        self.sent = []
        self.gets = []
        self.closed = 0

    def __call__(self):
        bus = self

        class _Dev:
            def open_path(self, path):
                bus.opened.append(path)

            def send_feature_report(self, data):
                bus.sent.append(bytes(data))
                if bus.fail_send:
                    raise OSError("send error")
                return bus.write_len

            def get_feature_report(self, rid, size):
                bus.gets.append((rid, size))
                return bus.feature_replies.pop(0) if bus.feature_replies else []

            def write(self, data):
                raise AssertionError("lunafury must not call write()")

            def close(self):
                bus.closed += 1

        return _Dev()


class LunafuryTests(unittest.TestCase):
    def test_make_request_and_parse_reply(self):
        req = LF.make_request()
        self.assertEqual(len(req), 65)
        self.assertEqual(req[:7], bytes([0x00, 0x00, 0x00, 0x02, 0x02, 0x00, 0x83]))
        self.assertEqual(req[7:], bytes(58))
        self.assertEqual(LF.parse_reply(_reply(72, 0)), (72, False))
        self.assertEqual(LF.parse_reply(_reply(100, 1, report_id=False)), (100, True))
        # Reject asleep (0%), out of range, bad charging flag, wrong header/length
        self.assertIsNone(LF.parse_reply(_reply(0, 0)))
        self.assertIsNone(LF.parse_reply(_reply(101, 0)))
        self.assertIsNone(LF.parse_reply(_reply(50, 2)))
        self.assertIsNone(LF.parse_reply([0] * 65))
        self.assertIsNone(LF.parse_reply([0] * 63))

    def test_poll_selects_only_65b_feature_collection_and_reads_charging(self):
        inv = [
            _col(0xFFFF, 0x0000, 0, {}, suffix="mi_01&col02"),
            _col(0xFFFF, 0x0001, 0, {}, suffix="mi_01&col03"),
            _col(0xFFA0, 0x0001, 65, {"0": 65}, suffix="mi_01&col04"),
        ]
        bus = _FakeBus(feature_replies=[[0x00, 0xA2] + [0] * 63, _reply(68, 1)])
        with patch.object(LF.hidlist, "enumerate", return_value=inv), \
             patch.object(LF.hid, "device", bus), \
             patch.object(LF.time, "sleep"):
            statuses = LF.LunafuryProvider().poll()
        self.assertEqual(len(statuses), 1)
        s = statuses[0]
        self.assertEqual((s.name, s.level, s.charging, s.charging_known, s.verification_level, s.kind),
                         ("Lunafury TYPE33EVO", 68, True, True, "C", "mouse"))
        self.assertTrue(s.unconfirmed)
        self.assertEqual(bus.opened, [inv[2]["path"]])
        self.assertEqual(bus.sent, [LF.make_request()])
        self.assertEqual(bus.closed, 1)

    def test_fail_closed_on_unproven_sibling_or_ambiguous_feature_or_unresolved_parent(self):
        col_ok = _col(0xFFA0, 0x0001, 65, {"0": 65}, suffix="mi_01&col04")
        col_other = _col(0xFFFF, 0x0000, 0, {}, suffix="mi_01&col02")
        bad_inventories = [
            [col_ok, dict(col_other, feature_report_sizes=None)],
            [col_ok, _col(0xFFFF, 0x0001, 65, {"0": 65}, suffix="mi_01&col03")],
            [dict(col_ok, physical_id="collection:unresolved")],
            [_col(0xFFA0, 0x0001, 64, {"0": 64}, suffix="mi_01&col04")],
        ]
        for inv in bad_inventories:
            bus = _FakeBus()
            p = LF.LunafuryProvider()
            with patch.object(LF.hidlist, "enumerate", return_value=inv), \
                 patch.object(LF.hid, "device", bus):
                self.assertEqual(p.poll(), [])
            self.assertEqual((bus.opened, bus.sent, bus.gets), ([], [], []))

    def test_registration_catalog_and_known_ids(self):
        self.assertIn(("lunafury", "LunafuryProvider"), server.PROVIDERS)
        self.assertEqual(server.PROVIDER_VIDS["lunafury"], (0x373E,))
        self.assertIn("lunafury", device_pack.BRAND_PROVIDERS)
        self.assertIn("lunafury", server.READER_DIAGNOSTICS)
        self.assertNotIn("lunafury", server.NO_CHARGING_EVIDENCE)
        self.assertNotIn("lunafury", server.DPI_PROVIDERS)
        self.assertEqual(catalog.verification_for("lunafury")[0], "C")
        self.assertEqual(catalog.validate_catalog()["errors"], [])
        self.assertEqual(server.recognized_candidate_count("lunafury", [_col(0xFFA0, 0x0001, 65, {"0": 65})]), 1)
        by_key = {(d["vendorId"], d["productId"]): d for d in known_ids.build_index()["devices"]}
        self.assertEqual(by_key[(0x373E, 0x0084)]["provider"], "lunafury")
        self.assertEqual(by_key[(0x373E, 0x0084)]["name"], "Lunafury TYPE33EVO")


if __name__ == "__main__":
    unittest.main()
