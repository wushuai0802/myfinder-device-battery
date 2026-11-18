"""VGN receiver battery queries from VGN HUB's ryMs driver.

The contributor tested 391D:1A04 at 92% and 91% against VGN HUB's 92%.
See engine/upstream-protocols.md for the report and evidence scope.
"""
from __future__ import annotations

import re
import time

import hid

from . import hidlist
from .base import DeviceStatus, Provider

VID = 0x391D
PIDS = {
    0x1A04: "VGN 蜻蜓3 Pro Max",
    0x1A08: "VGN Dragonfly Y2 V2 Pro Max",
    0x1A13: "VGN Dragonfly F1 V2 Pro Max",
    0x1A25: "VGN Dragonfly 3 Ultra",
    0x1A2B: "VGN Dragonfly F1 V5 Ultra",
    0x1A41: "VGN Dragonfly F2 V2 Ultra",
}
COLLECTION = (0xFFFF, 0x0002)
REPORT_ID = 0
REPORT_BYTES = 65  # 64-byte Feature payload and one Windows report-ID slot.
REPLY_WAIT = 0.06


def matches_collection(info):
    if (info.get("vendor_id") != VID or info.get("product_id") not in PIDS
            or (info.get("usage_page"), info.get("usage")) != COLLECTION):
        return False
    if info["product_id"] == 0x1A04:
        return True  # Preserve the same path acceptance as the original candidate.
    path = info.get("path")
    if info.get("interface_number") != 1 or not isinstance(path, (str, bytes)):
        return False
    try:
        text = path.decode("utf-8") if isinstance(path, bytes) else path
    except UnicodeError:
        return False
    parts = text.split("#")
    return (len(parts) >= 3 and "\0" not in text
            and re.search(r"&mi_01&col01(?:&|$)", parts[1], re.I) is not None)


def checksum(prefix):
    return 0xFF - (sum(prefix) & 0xFF)


def make_request():
    prefix = bytes([0xF7]) + bytes(6)
    return bytes([REPORT_ID]) + prefix + bytes([checksum(prefix)]) + bytes(56)


def parse_reply(raw):
    if (not isinstance(raw, (bytes, bytearray, list, tuple)) or len(raw) != REPORT_BYTES
            or any(type(byte) is not int or not 0 <= byte <= 255 for byte in raw)):
        return None
    # Require the unnumbered report slot and the HUB's status reply prefix.
    if raw[0] != REPORT_ID or raw[1] != 0 or not any(raw[1:]):
        return None
    level, sleep = raw[3], raw[5]
    if level > 100 or sleep not in (0, 1):
        return None
    return level, sleep == 1


class VgnRyMsProvider(Provider):
    name = "vgn_ry_ms"

    def __init__(self):
        self._diag = []

    def poll(self):
        self._diag = []
        try:
            inventory = hidlist.enumerate(VID)
        except OSError:
            self._diag.append("enumeration failed: nothing sent")
            return []

        groups = {}
        for info in inventory:
            if not matches_collection(info):
                continue
            physical = info.get("physical_id")
            if not isinstance(physical, str) or not physical.strip() or physical.casefold().startswith("collection:"):
                self._diag.append("physical parent unresolved; nothing sent")
                continue
            groups.setdefault(physical, []).append(info)

        result = []
        for physical, candidates in groups.items():
            if len(candidates) != 1 or not candidates[0].get("path"):
                self._diag.append("missing or ambiguous eligible collection: nothing sent")
                continue
            info = candidates[0]
            if (info.get("feature_report_length") != REPORT_BYTES
                    or info.get("feature_report_sizes") != {"0": REPORT_BYTES}):
                self._diag.append("feature report 0/65 unproven; nothing sent")
                continue
            reading = self._read(info["path"])
            if reading is None:
                continue
            level, asleep = reading
            result.append(DeviceStatus(
                f"{self.name}:{physical}", PIDS[info["product_id"]],
                level=None if asleep else level, online=not asleep, source=self.name, kind="mouse",
                physical_id=physical, connection_known=True, charging_known=False,
                verification_level="C", level_unavailable=asleep, wake_on_input=asleep,
            ))
        return result

    def _read(self, path):
        dev = None
        try:
            dev = hid.device()
            dev.open_path(path)
            if dev.send_feature_report(make_request()) != REPORT_BYTES:
                self._diag.append("short/failed battery query write; no retry")
                return None
            time.sleep(REPLY_WAIT)
            reading = parse_reply(dev.get_feature_report(REPORT_ID, REPORT_BYTES))
            if reading is None:
                self._diag.append("rejected battery reply")
            return reading
        except (OSError, ValueError):
            self._diag.append("battery I/O failed; connection unknown")
            return None
        finally:
            if dev is not None:
                try:
                    dev.close()
                except OSError:
                    self._diag.append("HID handle close failed")

    def diagnostics(self):
        return list(self._diag)
