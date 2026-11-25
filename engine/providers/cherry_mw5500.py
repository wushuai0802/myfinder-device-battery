"""Grade-C CHERRY MW5500 (046A:0133) read-only battery query over FF10:0001.

Protocol evidence: CHERRY official web driver https://utilitywebmw5500.cherry.cn/
(static decode of _next/static/chunks/app/page-2d87df2859342c64.js) + Issue #131.
- Communication collection: 0xFF10:0x0001 under exact 046A:0133 USB parent.
- Read-only query `getParam1Info` sends 9-byte unnumbered Feature report 0x00:
  [0x00, 0x82, 0, 0, 0, 0, 0, 0, 0], then reads one Input report on FF10:0001.
- Status slice: `batt_byte = raw[8]`, `level = batt_byte & 0x7F`,
  `charging = bool(batt_byte & 0x80)` (1..100%; 0 rejected as asleep/invalid).
"""
from __future__ import annotations

import time
from typing import List, Optional

try:
    import hid
except ImportError:  # pragma: no cover
    hid = None

from battery_hid import report_proof_summary
from . import hidlist
from .base import DeviceStatus, Provider

VID = 0x046A
PIDS = {0x0133: "CHERRY MW5500"}
COLLECTION = (0xFF10, 0x0001)
CMD_PARAM1 = 0x82
FEATURE_BYTES = 9
MIN_INPUT_BYTES = 10  # Windows ID slot (0) + >= 9 payload bytes (raw[0..8])
READ_TIMEOUT_MS = 1000
MAX_READS = 32


def make_request(feature_length: int = FEATURE_BYTES) -> List[int]:
    if type(feature_length) is not int or feature_length != FEATURE_BYTES:
        raise ValueError("invalid MW5500 feature report length")
    return [0x00, CMD_PARAM1] + [0x00] * (feature_length - 2)


def parse_reply(raw, expected_input_length: int) -> Optional[tuple]:
    """Parse unnumbered FF10:0001 input reply -> (level, charging, '') or None."""
    if (not isinstance(raw, (bytes, bytearray, list, tuple))
            or type(expected_input_length) is not int
            or expected_input_length < MIN_INPUT_BYTES):
        return None
    if len(raw) != expected_input_length - 1:
        return None
    if any(type(b) is not int or not 0 <= b <= 255 for b in raw):
        return None
    if not any(raw):
        return None
    # Reject echoed command bytes [0x82, 0, ...].
    if raw[0] == CMD_PARAM1 and not any(raw[1:]):
        return None
    batt_byte = raw[8]
    level = batt_byte & 0x7F
    if not 1 <= level <= 100:
        return None
    charging = bool(batt_byte & 0x80)
    return level, charging, ""


class CherryMW5500Provider(Provider):
    name = "cherry_mw5500"
    vid = VID
    pids = PIDS
    collection = COLLECTION

    def __init__(self):
        self._diag: List[str] = []

    def _pick(self, infos: List[dict]) -> Optional[dict]:
        candidates = [
            d for d in infos
            if d.get("vendor_id") == self.vid
            and d.get("product_id") in self.pids
            and (d.get("usage_page"), d.get("usage")) == self.collection
        ]
        if len(candidates) != 1 or not isinstance(candidates[0].get("path"), (bytes, str)) or not candidates[0].get("path"):
            self._diag.append("MW5500 FF10:0001 collection missing or ambiguous: nothing sent")
            return None
        info = candidates[0]
        physical = info.get("physical_id")
        if not isinstance(physical, str) or not physical or physical.startswith("collection:"):
            self._diag.append("MW5500 physical parent unresolved: nothing sent")
            return None
        feat_max = info.get("feature_report_length")
        feat_sizes = info.get("feature_report_sizes")
        if feat_max != FEATURE_BYTES or feat_sizes != {"0": FEATURE_BYTES}:
            self._diag.append("MW5500 feature 00/9 length unproven"
                              + report_proof_summary(info, "feature", include_declared=True) + ": nothing sent")
            return None
        in_max = info.get("input_report_length")
        in_sizes = info.get("input_report_sizes")
        if (type(in_max) is not int or not MIN_INPUT_BYTES <= in_max <= 4096
                or in_sizes != {"0": in_max}):
            self._diag.append("MW5500 input 00 length unproven"
                              + report_proof_summary(info, "input", include_declared=True) + ": nothing sent")
            return None
        return info

    def _query(self, dev, info: dict) -> Optional[tuple]:
        request = make_request(info["feature_report_length"])
        if dev.send_feature_report(request) != len(request):
            self._diag.append("MW5500 feature query short/failed; no retry")
            return None
        in_len = info["input_report_length"]
        read_len = in_len - 1
        deadline = time.monotonic() + READ_TIMEOUT_MS / 1000
        for _ in range(MAX_READS):
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            raw = dev.read(read_len, max(1, int(remaining * 1000)))
            if not raw:
                break
            parsed = parse_reply(raw, in_len)
            if parsed is not None:
                return parsed
        self._diag.append("MW5500 no valid battery reply within timeout")
        return None

    def poll(self) -> List[DeviceStatus]:
        self._diag = []
        if hid is None:
            return []
        try:
            inventory = hidlist.enumerate(self.vid)
        except OSError:
            self._diag.append("MW5500 enumeration failed: nothing sent")
            return []
        groups = {}
        for info in inventory:
            if info.get("vendor_id") != self.vid or info.get("product_id") not in self.pids:
                continue
            physical = info.get("physical_id", "")
            if not isinstance(physical, str) or not physical or physical.startswith("collection:"):
                self._diag.append("MW5500 physical parent unresolved: nothing sent")
                continue
            groups.setdefault(physical, []).append(info)
        result = []
        for physical, infos in groups.items():
            info = self._pick(infos)
            if info is None:
                continue
            dev = hid.device()
            try:
                dev.open_path(info["path"])
                reading = self._query(dev, info)
                if reading is not None:
                    level, charging, approx = reading
                    result.append(DeviceStatus(
                        f"{self.name}:{physical}", self.pids[info["product_id"]],
                        level, charging, True, self.name, approx=approx,
                        kind="mouse", physical_id=physical,
                        connection_known=True, charging_known=True,
                        verification_level="C",
                    ))
            except (OSError, ValueError):
                self._diag.append("MW5500 battery I/O failed; connection unknown")
            finally:
                try:
                    dev.close()
                except OSError:
                    self._diag.append("HID handle close failed")
        return result

    def diagnostics(self) -> List[str]:
        return list(self._diag)
