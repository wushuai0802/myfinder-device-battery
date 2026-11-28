"""VGN V98Pro V4 / TIMI keyboard battery queries from VGN HUB's bykeyboard driver.

Protocol derived from official VGN HUB bundle (.ai/evidence/vgnhub/index-ZngoA_qG.js,
customDeviceType "bykeyboard", vendorId 0x391D, FF60:0061, reportId 0, 520-byte
Feature payload / 521 bytes on Windows including the unnumbered report-ID slot):
- 2.4G receivers: 391D:2A03 (#530 V98Pro V4 8K), 391D:2A04, 391D:2A0A (TIMI)
- Wired USB: 391D:200B, 391D:200C, 391D:201C (TIMI)

Sends one read-only getBattery Feature report (`00 87 00 00 01 00 02 00` zero-padded
to 521 bytes) without write retries, waits 60 ms, and reads Feature report 0 (521B).
WebHID `A.getUint8(8)` maps to Windows `raw[9]` (1..100%). Charging is not called
by official `getBattery()`, so `charging_known` stays False until hardware-verified.
"""
from __future__ import annotations

import re
import time
from typing import List, Optional

try:
    import hid
except ImportError:  # pragma: no cover
    hid = None

from battery_hid import report_proof_summary
from . import hidlist
from .base import DeviceStatus, Provider

VID = 0x391D
PIDS = {
    0x2A03: "VGN V98Pro V4",
    0x2A04: "VGN V98Pro V4",
    0x2A0A: "VGN V98Pro V4 TIMI",
    0x200B: "VGN V98Pro V4",
    0x200C: "VGN V98Pro V4",
    0x201C: "VGN V98Pro V4 TIMI",
}
COLLECTION = (0xFF60, 0x0061)
REPORT_ID = 0
REPORT_BYTES = 521  # 1-byte Windows unnumbered report ID slot + 520-byte Feature payload
CMD_BATTERY = 0x87
REPLY_WAIT = 0.06
REQUEST = bytes((REPORT_ID, CMD_BATTERY, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00)) + bytes(REPORT_BYTES - 8)


def _valid_parent(physical: object, pid: int) -> bool:
    if not isinstance(physical, str) or not physical or physical.casefold().startswith("collection:"):
        return False
    return re.fullmatch(
        rf"pnp:usb\\vid_391d&pid_{pid:04x}(?:&rev_[0-9a-f]{{4}})?\\[^\\\s]+",
        physical,
        re.I | re.ASCII,
    ) is not None


def matches_collection(info: dict) -> bool:
    return (
        info.get("vendor_id") == VID
        and info.get("product_id") in PIDS
        and (info.get("usage_page"), info.get("usage")) == COLLECTION
    )


def make_request() -> bytes:
    return REQUEST


def parse_reply(raw: object) -> Optional[int]:
    if (not isinstance(raw, (bytes, bytearray, list, tuple))
            or len(raw) != REPORT_BYTES):
        return None
    if not isinstance(raw, (bytes, bytearray)):
        if any(type(b) is not int or not 0 <= b <= 255 for b in raw):
            return None
    if raw[0] != REPORT_ID or not any(raw[1:]):
        return None
    if bytes(raw) == REQUEST:
        return None
    # Require battery opcode 0x87 to block zero-prefixed stale buffers and unrelated commands.
    if raw[1] != CMD_BATTERY:
        return None
    level = raw[9]  # WebHID 520B DataView.getUint8(8) -> Windows 521B raw[9]
    if not 1 <= level <= 100:
        return None
    return level


class VgnV4KeyboardProvider(Provider):
    name = "vgn_v4_keyboard"

    def __init__(self):
        self._diag: List[str] = []

    def poll(self) -> List[DeviceStatus]:
        self._diag = []
        if hid is None:
            return []
        try:
            inventory = hidlist.enumerate(VID)
        except OSError:
            self._diag.append("VGN V4 keyboard enumeration failed: nothing sent")
            return []

        groups = {}
        for info in inventory:
            if not matches_collection(info):
                continue
            pid = info["product_id"]
            physical = info.get("physical_id")
            if not _valid_parent(physical, pid):
                self._diag.append("VGN V4 USB parent unresolved: nothing sent")
                continue
            groups.setdefault((physical, pid), []).append(info)

        result = []
        for (physical, pid), candidates in groups.items():
            if (len(candidates) != 1
                    or not isinstance(candidates[0].get("path"), (bytes, str))
                    or not candidates[0].get("path")):
                self._diag.append("VGN V4 FF60:0061 collection missing or ambiguous: nothing sent")
                continue
            info = candidates[0]
            if (info.get("feature_report_length") != REPORT_BYTES
                    or info.get("feature_report_sizes") != {"0": REPORT_BYTES}):
                self._diag.append(
                    "VGN V4 feature report 0/521 unproven"
                    + report_proof_summary(info, "feature", include_declared=True)
                    + ": nothing sent"
                )
                continue
            level = self._read(info["path"])
            if level is None:
                continue
            result.append(DeviceStatus(
                f"{self.name}:{physical}",
                PIDS[pid],
                level=level,
                charging=False,
                online=True,
                source=self.name,
                kind="keyboard",
                physical_id=physical,
                connection_known=True,
                charging_known=False,
                verification_level="C",
            ))
        return result

    def _read(self, path) -> Optional[int]:
        dev = None
        try:
            dev = hid.device()
            dev.open_path(path)
            if dev.send_feature_report(make_request()) != REPORT_BYTES:
                self._diag.append("VGN V4 short/failed battery query write; no retry")
                return None
            time.sleep(REPLY_WAIT)
            level = parse_reply(dev.get_feature_report(REPORT_ID, REPORT_BYTES))
            if level is None:
                self._diag.append("VGN V4 rejected battery reply")
            return level
        except (OSError, ValueError):
            self._diag.append("VGN V4 battery I/O failed; connection unknown")
            return None
        finally:
            if dev is not None:
                try:
                    dev.close()
                except OSError:
                    self._diag.append("HID handle close failed")

    def diagnostics(self) -> List[str]:
        return list(self._diag)
