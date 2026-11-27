"""HyperX Cloud Flight Wireless battery reader over its 2.4 GHz receiver.

The original Cloud Flight (not Flight S) uses a 20-byte ``21 ff 05`` output
report. The reply contains battery voltage in big-endian bytes 3-4 and is
either 15 or 20 bytes long. This is a read-only protocol; charging is inferred
from the voltage threshold used by HeadsetControl.
"""
from __future__ import annotations

import time
from typing import List, Optional, Tuple

try:
    import hid
except ImportError:  # pragma: no cover
    hid = None

from . import hidlist
from .base import DeviceStatus, Provider

VID = 0x0951
PIDS = {0x16C4: "HyperX Cloud Flight Wireless", 0x1723: "HyperX Cloud Flight Wireless"}
USAGE = 0x0303
USAGE_PAGES = {0xFF53, 0xFF90}
REQUEST = [0x21, 0xFF, 0x05] + [0x00] * 17
REPLY_LENGTHS = {15, 20}
READ_TIMEOUT_MS = 2000
MAX_READS = 32
CHARGING_THRESHOLD_MV = 0x100B


def estimate_level(voltage: int) -> int:
    if voltage <= 3648:
        return max(0, min(100, round(0.00125 * voltage)))
    if voltage > 3975:
        return 100
    return max(0, min(100, round(
        0.00000002547505 * voltage**4
        - 0.0003900299 * voltage**3
        + 2.238321 * voltage**2
        - 5706.256 * voltage
        + 5452299.0
    )))


def parse_reply(reply) -> Optional[Tuple[Optional[int], bool]]:
    if (not isinstance(reply, (bytes, bytearray, list, tuple))
            or len(reply) not in REPLY_LENGTHS
            or any(type(byte) is not int or not 0 <= byte <= 255 for byte in reply)):
        return None
    voltage = (reply[3] << 8) | reply[4]
    if list(reply[:3]) != REQUEST[:3] or not 2500 <= voltage <= 5000:
        return None
    if voltage > CHARGING_THRESHOLD_MV:
        return None, True
    return estimate_level(voltage), False


class HyperXCloudFlightProvider(Provider):
    name = "hyperx_cloud_flight"

    def __init__(self):
        self._diag: List[str] = []

    def _pick(self, infos: List[dict]) -> Optional[dict]:
        candidates = [info for info in infos
                      if info.get("usage") == USAGE
                      and info.get("usage_page") in USAGE_PAGES]
        if len(candidates) != 1 or not isinstance(candidates[0].get("path"), (bytes, str)) or not candidates[0].get("path"):
            self._diag.append("Cloud Flight battery collection missing or ambiguous: nothing sent")
            return None
        info = candidates[0]
        if (info.get("output_report_sizes") != {"33": 20}
                or info.get("input_report_sizes") != {"33": 20}
                or info.get("output_report_length") != 20
                or info.get("input_report_length") != 20):
            self._diag.append("Cloud Flight report sizes unproven: nothing sent")
            return None
        return info

    def _query(self, info: dict) -> Optional[Tuple[Optional[int], bool]]:
        dev = hid.device()
        try:
            dev.open_path(info["path"])
            if dev.write(REQUEST) != len(REQUEST):
                self._diag.append("Cloud Flight battery write short/failed; no retry")
                return None
            deadline = time.monotonic() + READ_TIMEOUT_MS / 1000
            for _ in range(MAX_READS):
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                reply = dev.read(20, max(1, int(remaining * 1000)))
                if not reply:
                    break
                parsed = parse_reply(reply)
                if parsed is not None:
                    return parsed
            self._diag.append("Cloud Flight no valid battery reply within timeout; connection unknown")
            return None
        except (OSError, ValueError):
            self._diag.append("Cloud Flight battery I/O failed; connection unknown")
            return None
        finally:
            try:
                dev.close()
            except OSError:
                self._diag.append("Cloud Flight HID handle close failed")

    def poll(self) -> List[DeviceStatus]:
        self._diag = []
        if hid is None:
            return []
        try:
            infos = hidlist.enumerate(VID)
        except OSError:
            self._diag.append("Cloud Flight enumeration failed: nothing sent")
            return []
        groups = {}
        for info in infos:
            if info.get("vendor_id") != VID or info.get("product_id") not in PIDS:
                continue
            physical = info.get("physical_id", "")
            if not isinstance(physical, str) or not physical or physical.startswith("collection:"):
                self._diag.append("Cloud Flight physical parent unresolved: nothing sent")
                continue
            groups.setdefault(physical, []).append(info)
        out = []
        for physical, mine in groups.items():
            info = self._pick(mine)
            if info is None:
                continue
            parsed = self._query(info)
            if parsed is None:
                continue
            level, charging = parsed
            out.append(DeviceStatus(f"{self.name}:{physical}", PIDS[info["product_id"]], level, charging, True,
                                    self.name,
                                    approx="voltage estimate",
                                    kind="headset", charging_known=True,
                                    connection_known=True, level_unavailable=charging,
                                    physical_id=physical,
                                    verification_level="C"))
        return out

    def diagnostics(self) -> List[str]:
        return list(self._diag)
