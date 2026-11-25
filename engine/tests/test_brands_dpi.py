"""Unit tests for post-1.4.0 beta DPI read and onboard stage switching across brands:
  * MCHOSE V2 (0x5253 / 0x3837 feature 0x12) and A5 Pro Max Gen1 (0x2023 feature 0x00)
  * ASUS ROG Harpe Ace Aim Lab Edition (0x0B05:1A92 / 1A94)
  * WLmouse CompX (#405, 0x36A7 excluding 0xA887)
  * Lamzu Maya X (0x373E:001C / 001E)
  * G-Wolves XVI / XVI-new (0x33E4 MODELS)
  * Architecture v2 C4 adapter: Dareu 2.4G JM (families/dpi/dareu_jm_24g.py, 0x260D:1156)
"""
from __future__ import annotations

import ast
from pathlib import Path
import sys
import unittest
from unittest.mock import MagicMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.modules.setdefault("hid", MagicMock())

import families
from families import dpi as family_dpi
from families.dpi import dareu_jm_24g as dareu_adapter
from providers import asus as A
from providers import families as FP
from providers import gwolves as G
from providers import lamzu as L
from providers import mchose as M
from providers import wlmouse as W


# ---------------------------------------------------------------------------
# Helpers for MCHOSE V2 & A5 Gen1
# ---------------------------------------------------------------------------

def mchose_v2_status_reply(level=85, charging=0, model=0x0031, vid=0x3837, report=0x11):
    length = 20 if report == 0x11 else 64
    pay = bytearray(length)
    pay[0:2] = vid.to_bytes(2, "little")
    pay[2:4] = model.to_bytes(2, "little")
    pay[8] = 0x09
    pay[9] = level
    pay[10] = charging
    return bytes([report, 0x06 ^ 0xFF]) + M._invert(bytes(pay))


def mchose_v2_config_payload(stages=(400, 800, 1600, 3200), wired_active=1, wireless_active=2):
    pay = bytearray(63)
    pay[0] = 0x01
    pay[1] = 0x20 | (wired_active & 0x0F)
    pay[2] = 0x10 | (wireless_active & 0x0F)
    pay[3] = 0x03
    padded = list(stages) + [800] * (6 - len(stages))
    for i, val in enumerate(padded[:6]):
        pay[4 + i * 2:6 + i * 2] = int(val).to_bytes(2, "little")
    pay[16] = len(stages)
    pay[17] = 0x01
    return bytes(pay)


def mchose_v2_config_reply(stages=(400, 800, 1600, 3200), wired_active=1, wireless_active=2):
    pay = mchose_v2_config_payload(stages, wired_active=wired_active, wireless_active=wireless_active)
    return bytes([0x12, 0x67 ^ 0xFF]) + M._invert(pay)


class FakeMchoseV2Device:
    def __init__(self, stages=(400, 800, 1600, 3200), wired_active=1, wireless_active=2,
                 level=85, charging=0, short_write=False, fail_readback=False,
                 ignore_write=False):
        self.stages = list(stages)
        self.wired_active = wired_active
        self.wireless_active = wireless_active
        self.level = level
        self.charging = charging
        self.short_write = short_write
        self.fail_readback = fail_readback
        self.ignore_write = ignore_write
        self.last_cmd = None
        self.sent_features = []
        self.wrote_57 = False

    def open_path(self, path):
        self.path = path

    def close(self):
        pass

    def send_feature_report(self, data):
        raw = bytes(data)
        self.sent_features.append(raw)
        cmd = raw[1] ^ 0xFF
        self.last_cmd = (raw[0], cmd)
        if raw[0] == 0x12 and cmd == 0x57:
            self.wrote_57 = True
            if self.short_write:
                return 12
            if not self.ignore_write:
                decoded = M._invert(raw[2:65])
                self.wired_active = decoded[1] & 0x0F
                self.wireless_active = decoded[2] & 0x0F
        return len(raw)

    def get_feature_report(self, report_id, length):
        if report_id == 0x11 and self.last_cmd == (0x11, 0x06):
            return mchose_v2_status_reply(self.level, self.charging)
        if report_id == 0x12 and self.last_cmd == (0x12, 0x67):
            if self.wrote_57 and self.fail_readback:
                raise OSError("readback timeout")
            return mchose_v2_config_reply(
                self.stages,
                wired_active=self.wired_active,
                wireless_active=self.wireless_active,
            )
        return bytes(length)


# ---------------------------------------------------------------------------
# Helpers for CompX Feature-Report-0 Mice (WLmouse, Lamzu, G-Wolves, MCHOSE A5)
# ---------------------------------------------------------------------------

def compx_battery_reply(level=78, charging=0):
    frame = bytearray(65)
    frame[1] = 0xA1
    frame[3] = 0x02
    frame[4] = 0x02
    frame[5] = 0x00
    frame[6] = 0x83
    frame[7] = charging
    frame[8] = level
    return bytes(frame)


def gwolves_old_battery_reply(level=64, charging=0):
    frame = bytearray(65)
    frame[1] = 0xA1
    frame[2] = 0x02
    frame[3] = 0x8F
    frame[5] = charging
    frame[6] = level
    return bytes(frame)


def compx_profile_reply(profile=1):
    frame = bytearray(65)
    frame[1] = 0xA1
    frame[3] = 0x02
    frame[4] = 0x01
    frame[5] = 0x00
    frame[6] = 0x85
    frame[7] = profile
    return bytes(frame)


def compx_stages_reply(stages=(400, 800, 1600, 3200), profile=1):
    frame = bytearray(65)
    frame[1] = 0xA1
    frame[3] = 0x02
    frame[4] = 2 + len(stages) * 4
    frame[5] = 0x01
    frame[6] = 0x81
    frame[7] = profile
    frame[8] = len(stages)
    for i, item in enumerate(stages):
        x, y = item if isinstance(item, tuple) else (item, item)
        off = 9 + i * 4
        frame[off] = (x >> 8) & 0xFF
        frame[off + 1] = x & 0xFF
        frame[off + 2] = (y >> 8) & 0xFF
        frame[off + 3] = y & 0xFF
    return bytes(frame)


def compx_active_stage_reply(active_index=1, profile=1):
    frame = bytearray(65)
    frame[1] = 0xA1
    frame[3] = 0x02
    frame[4] = 0x02
    frame[5] = 0x01
    frame[6] = 0x82
    frame[7] = profile
    frame[8] = active_index + 1
    return bytes(frame)


def compx_set_stage_ack(active_index=1, profile=1):
    frame = bytearray(65)
    frame[1] = 0xA1
    frame[3] = 0x02
    frame[4] = 0x02
    frame[5] = 0x01
    frame[6] = 0x02
    frame[7] = profile
    frame[8] = active_index + 1
    return bytes(frame)


class FakeCompXDevice:
    def __init__(self, stages=(400, 800, 1600, 3200), active=1, profile=1,
                 level=78, charging=0, old_battery=False,
                 short_write=False, fail_readback=False, ignore_write=False):
        self.stages = list(stages)
        self.active = active
        self.profile = profile
        self.level = level
        self.charging = charging
        self.old_battery = old_battery
        self.short_write = short_write
        self.fail_readback = fail_readback
        self.ignore_write = ignore_write
        self.sent_features = []
        self.last_req = None
        self.wrote_set = False

    def open_path(self, path):
        self.path = path

    def close(self):
        pass

    def send_feature_report(self, data):
        raw = bytes(data)
        self.sent_features.append(raw)
        if self.old_battery and raw[2] == 0x02 and raw[3] == 0x8F:
            self.last_req = ("old_batt",)
            return len(raw)
        page, cmd = raw[5], raw[6]
        self.last_req = (page, cmd)
        if (page, cmd) == (0x01, 0x02):
            self.wrote_set = True
            if self.short_write:
                return 10
            if not self.ignore_write:
                self.active = raw[8] - 1
        return len(raw)

    def get_feature_report(self, report_id, length):
        if self.last_req == ("old_batt",):
            return gwolves_old_battery_reply(self.level, self.charging)
        if self.last_req == (0x00, 0x83):
            return compx_battery_reply(self.level, self.charging)
        if self.last_req == (0x00, 0x85):
            if self.wrote_set and self.fail_readback:
                raise OSError("readback failed")
            return compx_profile_reply(self.profile)
        if self.last_req == (0x01, 0x81):
            if self.wrote_set and self.fail_readback:
                raise OSError("readback failed")
            return compx_stages_reply(self.stages, self.profile)
        if self.last_req == (0x01, 0x82):
            return compx_active_stage_reply(self.active, self.profile)
        if self.last_req == (0x01, 0x02):
            if self.fail_readback:
                return bytes(length)
            return compx_set_stage_ack(self.active, self.profile)
        return bytes(length)


# ---------------------------------------------------------------------------
# Helpers for ASUS ROG (0x0B05:1A92 / 1A94)
# ---------------------------------------------------------------------------

def asus_battery_reply(level=90, charging=0):
    r = [0x00, 0x12, 0x07, 0x00, 0x00, level, 0x00, 0x00, 0x00, 0x00, charging]
    return r + [0x00] * (65 - len(r))


def asus_profile_reply(active_index=1, profile=1):
    r = [0x00, 0x12, 0x00, 0x00] + [0x00] * 7 + [profile - 1, active_index + 1]
    return r + [0x00] * (65 - len(r))


def asus_dpi_xy_reply(stages=(400, 800, 1600, 3200)):
    r = [0x00, 0x12, 0x04, 0x02, 0x00]
    for item in stages:
        x, y = item if isinstance(item, tuple) else (item, item)
        x_raw = (x - 50) // 50
        y_raw = (y - 50) // 50
        r.extend([x_raw & 0xFF, (x_raw >> 8) & 0xFF, y_raw & 0xFF, (y_raw >> 8) & 0xFF])
    return r + [0x00] * (65 - len(r))


def asus_set_stage_reply(active_index=1):
    r = [0x00, 0x51, 0x31, 0x09, 0x00, active_index + 1]
    return r + [0x00] * (65 - len(r))


class FakeAsusDevice:
    def __init__(self, stages=(400, 800, 1600, 3200), active=1, profile=1,
                 level=90, charging=0, short_write=False, fail_readback=False,
                 ignore_write=False):
        self.stages = list(stages)
        self.active = active
        self.profile = profile
        self.level = level
        self.charging = charging
        self.short_write = short_write
        self.fail_readback = fail_readback
        self.ignore_write = ignore_write
        self.writes = []
        self.queue = []
        self.nonblocking = False
        self.wrote_set = False

    def open_path(self, path):
        self.path = path

    def close(self):
        pass

    def set_nonblocking(self, flag):
        self.nonblocking = bool(flag)

    def write(self, data):
        raw = list(data)
        self.writes.append(bytes(raw))
        if raw[:3] == [0x00, 0x12, 0x07]:
            self.queue.append(asus_battery_reply(self.level, self.charging))
        elif raw[:3] == [0x00, 0x12, 0x00]:
            if not (self.wrote_set and self.fail_readback):
                self.queue.append(asus_profile_reply(self.active, self.profile))
        elif raw[:4] == [0x00, 0x12, 0x04, 0x02]:
            if not (self.wrote_set and self.fail_readback):
                self.queue.append(asus_dpi_xy_reply(self.stages))
        elif raw[:4] == [0x00, 0x51, 0x31, 0x09]:
            self.wrote_set = True
            if self.short_write:
                return 8
            if not self.ignore_write:
                self.active = raw[5] - 1
            if not self.fail_readback:
                self.queue.append(asus_set_stage_reply(self.active))
        return len(raw)

    def read(self, length, timeout_ms=0):
        if self.nonblocking:
            return []
        return self.queue.pop(0) if self.queue else []


# ---------------------------------------------------------------------------
# Helpers for Dareu JM 2.4G C4 Adapter (0x260D:1156)
# ---------------------------------------------------------------------------

def dareu_dpi_id(dpi):
    idx = (dpi // 50) - 1
    if idx < 256:
        return idx & 0xFF, 0x00
    if idx < 512:
        return (idx - 256) & 0xFF, 0x44
    return (idx - 512) & 0xFF, 0x88


def dareu_stage_record(x_dpi, y_dpi=None):
    y_dpi = x_dpi if y_dpi is None else y_dpi
    x_low, x_hi = dareu_dpi_id(x_dpi)
    y_low, y_hi = dareu_dpi_id(y_dpi)
    if x_hi != y_hi:
        raise ValueError("mismatched high byte")
    rec = [x_low, y_low, x_hi]
    rec.append((0x55 - sum(rec)) & 0xFF)
    return rec


class FakeDareuJmDevice:
    def __init__(self, stages=(400, 800, 1600, 6400, 26000), active=1,
                 level=82, charging=1, short_write=False, fail_readback=False,
                 ignore_write=False):
        self.stages = list(stages)
        self.active = active
        self.level = level
        self.charging = charging
        self.short_write = short_write
        self.fail_readback = fail_readback
        self.ignore_write = ignore_write
        self.writes = []
        self.queue = []
        self.wrote_set = False

    def open_path(self, path):
        self.path = path

    def close(self):
        pass

    def _stage_bytes(self):
        buf = []
        for item in self.stages:
            x, y = item if isinstance(item, tuple) else (item, item)
            buf.extend(dareu_stage_record(x, y))
        return buf

    def write(self, data):
        raw = list(bytes(data))
        self.writes.append(bytes(raw))
        cmd = raw[1]
        addr = (raw[3] << 8) | raw[4]
        count = raw[5]
        if cmd == 0x03:  # gate
            rep = [0x08, 0x03, 0x00, 0x00, 0x00, 0x01, 0x01] + [0x00] * 9
            rep.append((0x55 - sum(rep)) & 0xFF)
            self.queue.append(rep)
        elif cmd == 0x04:  # battery
            rep = [0x08, 0x04, 0x00, 0x00, 0x00, 0x02, self.level, self.charging] + [0x00] * 8
            rep.append((0x55 - sum(rep)) & 0xFF)
            self.queue.append(rep)
        elif cmd == 0x08:  # read buffer
            if self.wrote_set and self.fail_readback:
                return len(raw)
            if addr == 0x0002 and count == 4:
                c = len(self.stages)
                a = self.active
                payload = [c, (0x55 - c) & 0xFF, a, (0x55 - a) & 0xFF]
            else:
                off = addr - 0x000C
                payload = self._stage_bytes()[off:off + count]
            rep = [0x08, 0x08, 0x00, (addr >> 8) & 0xFF, addr & 0xFF, count] + payload
            rep += [0x00] * (16 - len(rep))
            rep.append((0x55 - sum(rep)) & 0xFF)
            self.queue.append(rep)
        elif cmd == 0x07 and addr == 0x0004:  # write active stage
            self.wrote_set = True
            if self.short_write:
                return 5
            if not self.ignore_write:
                self.active = raw[6]
            if not self.fail_readback:
                rep = [0x08, 0x07, 0x00, 0x00, 0x04, 0x02, raw[6], raw[7]] + [0x00] * 8
                rep.append((0x55 - sum(rep)) & 0xFF)
                self.queue.append(rep)
        return len(raw)

    def read(self, length, timeout_ms=0):
        return self.queue.pop(0) if self.queue else []


# ===========================================================================
# Test Cases
# ===========================================================================

class MchoseDpiTests(unittest.TestCase):
    def _v2_entry(self, pid=0x100B, **overrides):
        row = {
            "vendor_id": 0x3837,
            "product_id": pid,
            "path": b"mchose-v2-ff01",
            "physical_id": r"pnp:usb\vid_3837&pid_100b\rx1",
            "interface_number": 2,
            "usage_page": 0xFF01,
            "usage": 1,
            "product_string": "MCHOSE A7 V2 Ultra",
            "serial_number": "",
            "feature_report_length": 65,
            "feature_report_sizes": {"17": 21, "18": 65},
        }
        row.update(overrides)
        return row

    def _a5_entry(self, pid=0xF013, **overrides):
        row = {
            "vendor_id": 0x2023,
            "product_id": pid,
            "path": b"mchose-a5-ffff",
            "physical_id": r"pnp:usb\vid_2023&pid_f013\rx1",
            "interface_number": 1,
            "usage_page": 0xFFFF,
            "usage": 1,
            "product_string": "MCHOSE A5 2.4G",
            "serial_number": "",
            "feature_report_length": 65,
        }
        row.update(overrides)
        return row

    def test_v2_wireless_and_wired_poll_and_set_device_dpi(self):
        for pid, is_wired, expected_initial in ((0x100B, False, 1600), (0x4019, True, 800)):
            with self.subTest(pid=hex(pid)):
                entry = self._v2_entry(pid=pid)
                fake = FakeMchoseV2Device(stages=(400, 800, 1600, 3200), wired_active=1, wireless_active=2)
                with patch.object(M.time, "sleep", lambda *_: None), \
                     patch.object(M.hid, "device", side_effect=lambda: fake), \
                     patch.object(M.hidlist, "enumerate", side_effect=lambda vid: [entry] if vid == 0x3837 else []):
                    statuses = M.MchoseProvider().poll()
                    self.assertEqual(len(statuses), 1)
                    st = statuses[0]
                    self.assertEqual(st.level, 85)
                    self.assertIsNotNone(st.dpi)
                    self.assertTrue(st.dpi["canSet"])
                    self.assertEqual(st.dpi["current"], expected_initial)
                    self.assertEqual(st.dpi["stages"], [400, 800, 1600, 3200])
                    self.assertEqual(st.dpi["stageSource"], "onboard")
                    token = st.dpi["targetToken"]
                    self.assertEqual(len(token), 64)

                    res = M.MchoseProvider().set_device_dpi([entry], st.dpi_target, 3200, expected_initial, token)
                    self.assertTrue(res["success"])
                    self.assertFalse(res["outcomeUnknown"])
                    self.assertIsNone(res["error"])
                    self.assertEqual(res["confirmedDpi"], 3200)
                    self.assertEqual(res["dpi"]["current"], 3200)
                    self.assertEqual(res["dpi"]["targetToken"], token)
                    self.assertEqual(fake.wired_active if is_wired else fake.wireless_active, 3)

    def test_v2_pre_write_refusals_and_post_write_unknown_outcomes(self):
        entry = self._v2_entry()
        fake = FakeMchoseV2Device(stages=(400, 800, 1600, 3200), wireless_active=2)
        with patch.object(M.time, "sleep", lambda *_: None), \
             patch.object(M.hid, "device", side_effect=lambda: fake):
            dpi, target = M._capture_dpi(entry)
            token = dpi["targetToken"]

            self.assertEqual(M.set_device_dpi([entry], {"provider": "other"}, 800, 1600, token)["error"], "dpi_invalid_target")
            self.assertEqual(M.set_device_dpi([entry], target, 800, 1600, "0" * 64)["error"], "dpi_stale_target")
            self.assertEqual(M.set_device_dpi([entry], target, True, 1600, token)["error"], "dpi_invalid_value")
            self.assertEqual(M.set_device_dpi([entry], target, 1200, 1600, token)["error"], "dpi_unsupported_value")
            self.assertEqual(M.set_device_dpi([], target, 800, 1600, token)["error"], "dpi_target_changed")
            self.assertEqual(M.set_device_dpi([entry], target, 800, 400, token)["error"], "dpi_current_changed")
            self.assertFalse(fake.wrote_57)

            # Duplicate stages mark canSet=False
            dup_fake = FakeMchoseV2Device(stages=(800, 800, 1600), wireless_active=0)
            with patch.object(M.hid, "device", side_effect=lambda: dup_fake):
                dup_dpi, dup_target = M._capture_dpi(entry)
                self.assertFalse(dup_dpi["canSet"])
                self.assertIsNone(dup_target)
                self.assertEqual(dup_dpi["error"], "dpi_ambiguous_stages")

            # Short write -> outcomeUnknown=True
            short_fake = FakeMchoseV2Device(stages=(400, 800, 1600, 3200), wireless_active=2, short_write=True)
            with patch.object(M.hid, "device", side_effect=lambda: short_fake):
                res = M.set_device_dpi([entry], target, 800, 1600, token)
                self.assertFalse(res["success"])
                self.assertTrue(res["outcomeUnknown"])
                self.assertEqual(res["error"], "dpi_readback_unconfirmed")

            # Readback mismatch -> outcomeUnknown=True
            ign_fake = FakeMchoseV2Device(stages=(400, 800, 1600, 3200), wireless_active=2, ignore_write=True)
            with patch.object(M.hid, "device", side_effect=lambda: ign_fake):
                res = M.set_device_dpi([entry], target, 800, 1600, token)
                self.assertFalse(res["success"])
                self.assertTrue(res["outcomeUnknown"])
                self.assertEqual(res["error"], "dpi_readback_mismatch")

    def test_a5_gen1_poll_and_set_device_dpi(self):
        entry = self._a5_entry()
        fake = FakeCompXDevice(stages=(400, 800, 1600, 3200), active=1, profile=2, level=66, charging=1)
        with patch.object(M.time, "sleep", lambda *_: None), \
             patch.object(M.hid, "device", side_effect=lambda: fake), \
             patch.object(M.hidlist, "enumerate", side_effect=lambda vid: [entry] if vid == 0x2023 else []):
            statuses = M.MchoseProvider().poll()
            self.assertEqual(len(statuses), 1)
            st = statuses[0]
            self.assertEqual((st.level, st.charging), (66, True))
            self.assertEqual(st.dpi["current"], 800)
            self.assertEqual(st.dpi["stages"], [400, 800, 1600, 3200])
            token = st.dpi["targetToken"]

            res = M.set_device_dpi([entry], st.dpi_target, 3200, 800, token)
            self.assertTrue(res["success"])
            self.assertFalse(res["outcomeUnknown"])
            self.assertEqual(res["confirmedDpi"], 3200)
            self.assertEqual(fake.active, 3)


class AsusDpiTests(unittest.TestCase):
    def _entry(self, pid=0x1A94, **overrides):
        row = {
            "vendor_id": 0x0B05,
            "product_id": pid,
            "path": b"asus-harpe-ff01",
            "physical_id": r"pnp:usb\vid_0b05&pid_1a94\rx1",
            "interface_number": 0,
            "usage_page": 0xFF01,
            "usage": 1,
            "product_string": "ROG Harpe Ace Aim Lab Edition",
            "serial_number": "",
        }
        row.update(overrides)
        return row

    def test_harpe_ace_poll_and_set_device_dpi_without_flash_save(self):
        entry = self._entry()
        fake = FakeAsusDevice(stages=(400, 800, 1600, 3200), active=1, level=92, charging=0)
        with patch.object(A.hid, "device", side_effect=lambda: fake), \
             patch.object(A.hidlist, "enumerate", return_value=[entry]):
            statuses = A.AsusProvider().poll()
            self.assertEqual(len(statuses), 1)
            st = statuses[0]
            self.assertEqual(st.level, 92)
            self.assertIsNotNone(st.dpi)
            self.assertTrue(st.dpi["canSet"])
            self.assertEqual(st.dpi["current"], 800)
            self.assertEqual(st.dpi["stages"], [400, 800, 1600, 3200])
            token = st.dpi["targetToken"]

            res = A.AsusProvider().set_device_dpi([entry], st.dpi_target, 1600, 800, token)
            self.assertTrue(res["success"])
            self.assertFalse(res["outcomeUnknown"])
            self.assertEqual(res["confirmedDpi"], 1600)
            # Verify 51 31 09 00 03 was sent once and 50 03 (flash save) was NEVER sent
            set_writes = [w for w in fake.writes if w[:4] == bytes([0x00, 0x51, 0x31, 0x09])]
            self.assertEqual(len(set_writes), 1)
            self.assertEqual(set_writes[0][5], 3)
            self.assertFalse(any(w[:3] == bytes([0x00, 0x50, 0x03]) for w in fake.writes))

    def test_asus_safety_refusals_and_readback_failures(self):
        entry = self._entry()
        fake = FakeAsusDevice(stages=(400, 800, 1600, 3200), active=1)
        with patch.object(A.hid, "device", side_effect=lambda: fake):
            dpi, target = A._capture_dpi(entry)
            token = dpi["targetToken"]

            self.assertEqual(A.set_device_dpi([entry], target, 1600, 400, token)["error"], "dpi_current_changed")
            self.assertFalse(fake.wrote_set)

            # Asymmetric X/Y stages -> canSet=False
            asym_fake = FakeAsusDevice(stages=(400, (800, 1600), 1600, 3200), active=0)
            with patch.object(A.hid, "device", side_effect=lambda: asym_fake):
                asym_dpi, asym_target = A._capture_dpi(entry)
                self.assertFalse(asym_dpi["canSet"])
                self.assertIsNone(asym_target)

            # Unconfirmed readback -> outcomeUnknown=True
            fail_fake = FakeAsusDevice(stages=(400, 800, 1600, 3200), active=1, fail_readback=True)
            with patch.object(A.hid, "device", side_effect=lambda: fail_fake):
                res = A.set_device_dpi([entry], target, 1600, 800, token)
                self.assertFalse(res["success"])
                self.assertTrue(res["outcomeUnknown"])
                self.assertEqual(res["error"], "dpi_readback_unconfirmed")


class WLmouseLamzuGWolvesDpiTests(unittest.TestCase):
    def test_wlmouse_poll_and_set_device_dpi_and_4k_exclusion(self):
        entry = {
            "vendor_id": 0x36A7,
            "product_id": 0xA880,
            "path": b"wlmouse-a880-ffff",
            "physical_id": r"pnp:usb\vid_36a7&pid_a880\rx1",
            "interface_number": 2,
            "usage_page": 0xFFFF,
            "usage": 0,
            "product_string": "WLmouse Beast X Max",
            "serial_number": "",
        }
        fake = FakeCompXDevice(stages=(400, 800, 1600, 3200), active=2, level=74, charging=0)
        with patch.object(W.time, "sleep", lambda *_: None), \
             patch.object(W.hid, "device", side_effect=lambda: fake), \
             patch.object(W.hidlist, "enumerate", return_value=[entry]):
            statuses = W.WLmouseProvider().poll()
            self.assertEqual(len(statuses), 1)
            st = statuses[0]
            self.assertEqual(st.level, 74)
            self.assertEqual(st.dpi["current"], 1600)
            token = st.dpi["targetToken"]

            res = W.WLmouseProvider().set_device_dpi([entry], st.dpi_target, 400, 1600, token)
            self.assertTrue(res["success"])
            self.assertFalse(res["outcomeUnknown"])
            self.assertEqual(res["confirmedDpi"], 400)

        # 0xA887 Beast X 4K is excluded from CompX page-command DPI
        entry_4k = dict(entry, product_id=0xA887)
        self.assertFalse(W._dpi_eligible(entry_4k))

    def test_lamzu_maya_x_poll_and_set_device_dpi(self):
        entry = {
            "vendor_id": 0x373E,
            "product_id": 0x001E,
            "path": b"lamzu-001e-ffff",
            "physical_id": r"pnp:usb\vid_373e&pid_001e\rx1",
            "interface_number": 2,
            "usage_page": 0xFFFF,
            "usage": 1,
            "product_string": "LAMZU Maya X 8K Dongle",
            "serial_number": "",
        }
        fake = FakeCompXDevice(stages=(400, 800, 1600, 3200, 6400), active=1, level=88, charging=0)
        with patch.object(W.time, "sleep", lambda *_: None), \
             patch.object(L.time, "sleep", lambda *_: None), \
             patch.object(L.hid, "device", side_effect=lambda: fake), \
             patch.object(L.hidlist, "enumerate", return_value=[entry]):
            statuses = L.LamzuProvider().poll()
            self.assertEqual(len(statuses), 1)
            st = statuses[0]
            self.assertEqual(st.level, 88)
            self.assertEqual(st.dpi["current"], 800)
            self.assertEqual(st.dpi["stages"], [400, 800, 1600, 3200, 6400])
            token = st.dpi["targetToken"]

            res = L.LamzuProvider().set_device_dpi([entry], st.dpi_target, 6400, 800, token)
            self.assertTrue(res["success"])
            self.assertFalse(res["outcomeUnknown"])
            self.assertEqual(res["confirmedDpi"], 6400)

    def test_gwolves_xvi_new_and_old_models_poll_and_set_device_dpi(self):
        for pid, old_proto in ((0x3817, False), (0x5803, True)):
            with self.subTest(pid=hex(pid)):
                entry = {
                    "vendor_id": 0x33E4,
                    "product_id": pid,
                    "path": b"gwolves-xvi-ffff",
                    "physical_id": f"pnp:usb\\vid_33e4&pid_{pid:04x}\\rx1",
                    "interface_number": 2,
                    "usage_page": 0xFFFF,
                    "usage": 1,
                    "product_string": "G-Wolves Mouse",
                    "serial_number": "",
                }
                stages_7 = (400, 800, 1200, 1600, 2400, 3200, 6400)
                fake = FakeCompXDevice(
                    stages=stages_7, active=3, profile=1,
                    level=61, charging=0, old_battery=old_proto,
                )
                with patch.object(W.time, "sleep", lambda *_: None), \
                     patch.object(G.time, "sleep", lambda *_: None), \
                     patch.object(G, "feature_length", return_value=65), \
                     patch.object(G.hid, "device", side_effect=lambda: fake), \
                     patch.object(G.hidlist, "enumerate", return_value=[entry]):
                    statuses = G.GWolvesProvider().poll()
                    self.assertEqual(len(statuses), 1)
                    st = statuses[0]
                    self.assertEqual(st.level, 61)
                    self.assertIsNotNone(st.dpi)
                    self.assertEqual(st.dpi["current"], 1600)
                    self.assertEqual(st.dpi["stages"], list(stages_7))
                    token = st.dpi["targetToken"]

                    res = G.GWolvesProvider().set_device_dpi([entry], st.dpi_target, 3200, 1600, token)
                    self.assertTrue(res["success"])
                    self.assertFalse(res["outcomeUnknown"])
                    self.assertEqual(res["confirmedDpi"], 3200)

        # Shared receiver 0x3854 is excluded from feature-report DPI
        shared = {
            "vendor_id": 0x33E4, "product_id": 0x3854, "path": b"gw-shared",
            "physical_id": r"pnp:usb\vid_33e4&pid_3854\rx1",
        }
        with patch.object(G, "feature_length", return_value=65):
            self.assertFalse(G._dpi_eligible(shared))

    def test_battery_isolation_and_pack_exclusion_across_brands(self):
        # 1. Pack models are excluded from DPI across all brands
        self.assertFalse(M._dpi_eligible({
            "vendor_id": 0x3837, "product_id": 0x100B, "path": b"p",
            "physical_id": "rx", "usage_page": 0xFF01, "usage": 1, "modelSource": "pack",
        }))
        self.assertFalse(A._dpi_eligible({
            "vendor_id": 0x0B05, "product_id": 0x1A94, "path": b"p",
            "physical_id": "rx", "interface_number": 0, "usage_page": 0xFF01, "modelSource": "pack",
        }))
        self.assertFalse(W._dpi_eligible({
            "vendor_id": 0x36A7, "product_id": 0xA880, "path": b"p",
            "physical_id": "rx", "usage_page": 0xFFFF, "modelSource": "pack",
        }))
        self.assertFalse(L._dpi_eligible({
            "vendor_id": 0x373E, "product_id": 0x001E, "path": b"p",
            "physical_id": "rx", "interface_number": 2, "usage_page": 0xFFFF, "modelSource": "pack",
        }))
        with patch.object(G, "feature_length", return_value=65):
            self.assertFalse(G._dpi_eligible({
                "vendor_id": 0x33E4, "product_id": 0x3817, "path": b"p",
                "physical_id": "rx", "modelSource": "pack",
            }))

        # 2. Failed DPI read never drops battery status
        lamzu_entry = {
            "vendor_id": 0x373E, "product_id": 0x001E, "path": b"lamzu-001e-ffff",
            "physical_id": r"pnp:usb\vid_373e&pid_001e\rx1", "interface_number": 2,
            "usage_page": 0xFFFF, "usage": 1, "product_string": "LAMZU Maya X 8K Dongle",
        }
        fake = FakeCompXDevice(stages=(), active=0, level=77, charging=1)
        with patch.object(W.time, "sleep", lambda *_: None), \
             patch.object(L.time, "sleep", lambda *_: None), \
             patch.object(L.hid, "device", side_effect=lambda: fake), \
             patch.object(L.hidlist, "enumerate", return_value=[lamzu_entry]):
            statuses = L.LamzuProvider().poll()
            self.assertEqual(len(statuses), 1)
            self.assertEqual((statuses[0].level, statuses[0].charging), (77, True))
            self.assertIsNone(statuses[0].dpi)


class DareuJm24gC4AdapterTests(unittest.TestCase):
    def _inventory(self):
        base = {
            "vendor_id": 0x260D,
            "product_id": 0x1156,
            "product_string": "2.4G Wireless Receiver",
            "serial_number": "",
            "physical_id": r"pnp:usb\vid_260d&pid_1156\receiver",
            "interface_number": 1,
            "release_number": 1,
        }
        mouse = dict(base, usage_page=1, usage=2, path=b"dareu-1156-mouse",
                     input_report_length=8, output_report_length=0, feature_report_length=0,
                     input_report_sizes={"1": 8}, output_report_sizes={}, feature_report_sizes={})
        vendor = dict(base, usage_page=0xFF02, usage=0x0002, path=b"dareu-1156-ff02",
                      input_report_length=17, output_report_length=17, feature_report_length=0,
                      input_report_sizes={"8": 17}, output_report_sizes={"8": 17}, feature_report_sizes={})
        return [mouse, vendor]

    def test_adapter_ast_has_no_hid_or_ctypes_imports(self):
        src = Path(dareu_adapter.__file__).read_text(encoding="utf-8")
        tree = ast.parse(src)
        imported = {
            alias.name.split(".")[0]
            for node in ast.walk(tree)
            if isinstance(node, ast.Import)
            for alias in node.names
        } | {
            (node.module or "").split(".")[0]
            for node in ast.walk(tree)
            if isinstance(node, ast.ImportFrom)
        }
        self.assertNotIn("hid", imported)
        self.assertNotIn("ctypes", imported)
        self.assertEqual(family_dpi.ADAPTERS.get("dareu_jm_24g"), "dareu_jm_24g")
        self.assertIn(0x260D, family_dpi.vendor_ids())

    def test_families_poll_and_set_device_dpi_for_dareu_jm_24g(self):
        inv = self._inventory()
        # Includes high-byte 0x00 (400, 800, 1600), 0x44 (14000), and 0x88 (26000) stages
        fake = FakeDareuJmDevice(stages=(400, 800, 1600, 14000, 26000), active=2, level=84, charging=0)
        with patch.object(FP.hid, "device", side_effect=lambda: fake), \
             patch.object(FP.hidlist, "enumerate", side_effect=lambda vid: inv if vid == 0x260D else []):
            provider = FP.FamiliesProvider(running=lambda *_: False, clock=lambda: 0.0)
            statuses = provider.poll()
            self.assertEqual(len(statuses), 1)
            st = statuses[0]
            self.assertEqual(st.level, 84)
            self.assertIsNotNone(st.dpi)
            self.assertTrue(st.dpi["canSet"])
            self.assertEqual(st.dpi["current"], 1600)
            self.assertEqual(st.dpi["stages"], [400, 800, 1600, 14000, 26000])
            token = st.dpi["targetToken"]

            res = provider.set_device_dpi(inv, st.dpi_target, 26000, 1600, token)
            self.assertTrue(res["success"])
            self.assertFalse(res["outcomeUnknown"])
            self.assertIsNone(res["error"])
            self.assertEqual(res["confirmedDpi"], 26000)
            self.assertEqual(res["dpi"]["current"], 26000)
            # Check exact 0x07 write frame at 0x0004 for index 4
            write_07 = [w for w in fake.writes if w[1] == 0x07]
            self.assertEqual(len(write_07), 1)
            self.assertEqual(write_07[0], bytes.fromhex("08 07 00 00 04 02 04 51 00 00 00 00 00 00 00 00 EB"))

            # Pre-write safety refusal when expected_dpi is stale
            stale = provider.set_device_dpi(inv, st.dpi_target, 800, 1600, token)
            self.assertFalse(stale["success"])
            self.assertFalse(stale["outcomeUnknown"])
            self.assertEqual(stale["error"], "dpi_current_changed")

            # Post-write unconfirmed readback -> outcomeUnknown=True
            fail_fake = FakeDareuJmDevice(stages=(400, 800, 1600, 14000, 26000), active=4, fail_readback=True)
            with patch.object(FP.hid, "device", side_effect=lambda: fail_fake):
                unconf = provider.set_device_dpi(inv, st.dpi_target, 800, 26000, token)
                self.assertFalse(unconf["success"])
                self.assertTrue(unconf["outcomeUnknown"])
                self.assertEqual(unconf["error"], "dpi_readback_unconfirmed")


if __name__ == "__main__":
    unittest.main()
