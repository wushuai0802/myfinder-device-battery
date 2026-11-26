"""Grade-C HyperX Cloud Flight S (0951:16EA) battery query over FF13:0001.

Protocol corroborated across three independent open-source hardware tools:
- TERABAYTYCH/hyperx-cloud-flight-s-macos @ 42a73a93 (Sources/HeadsetMonitor.swift)
- CubE135/hyperx-cloud-flight-s-battery-monitor @ e2271dff (src/assets/js/classes/HyperX.js)
- TheRealRazbi/hyperx-cloud-flight-s-battery-icon @ cfddb9c3 (headset_interface.py)

Only sends the fixed battery status query on proven FF13:0001 Output report 0x06.
Never sends EQ, button remap, mic monitoring, or 7.1 DSP control packets.
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

VID = 0x0951
PIDS = {0x16EA: "HyperX Cloud Flight S"}
COLLECTION = (0xFF13, 0x0001)
OUTPUT_ID = 0x06
INPUT_IDS = (0x0B, 0x06)
MIN_OUTPUT_LEN = 16
MIN_INPUT_LEN = 8
READ_TIMEOUT_MS = 1000
MAX_READS = 32
BOOT_GLITCH_MV = 3500

REQUEST_PREFIX = (
    0x06, 0x00, 0x02, 0x00, 0x9A, 0x00, 0x00, 0x68,
    0x4A, 0x8E, 0x0A, 0x00, 0x00, 0x00, 0xBB, 0x02,
)


def make_request(output_length: int = 62) -> List[int]:
    if type(output_length) is not int or output_length < MIN_OUTPUT_LEN:
        raise ValueError("invalid Flight S output report length")
    return list(REQUEST_PREFIX) + [0x00] * (output_length - len(REQUEST_PREFIX))


def is_power_off_event(raw) -> bool:
    return (
        isinstance(raw, (bytes, bytearray, list, tuple))
        and len(raw) >= 5
        and raw[0] in INPUT_IDS
        and raw[3] == 0x01
        and raw[4] == 0x03
    )


def parse_reply(raw, input_sizes: dict, input_max: Optional[int] = None) -> Optional[tuple]:
    if (not isinstance(raw, (bytes, bytearray, list, tuple))
            or len(raw) < MIN_INPUT_LEN
            or not isinstance(input_sizes, dict)):
        return None
    if any(type(b) is not int or not 0 <= b <= 255 for b in raw):
        return None
    report_id = raw[0]
    if report_id not in INPUT_IDS:
        return None
    expected_len = input_sizes.get(str(report_id))
    if type(expected_len) is not int or expected_len < MIN_INPUT_LEN:
        return None
    if len(raw) != expected_len and not (
            type(input_max) is int and input_max >= expected_len and len(raw) == input_max):
        return None
    if raw[3] != 0x02 or (raw[5] == 0 and raw[6] == 0):
        return None
    millivolts = (raw[5] << 8) | raw[6]
    level = raw[7]
    if not 0 <= level <= 100:
        return None
    # Headset occasionally emits 0% while voltage >= 3.5V right after power-on.
    if level == 0 and millivolts >= BOOT_GLITCH_MV:
        return None
    return level, False, ""


class HyperXFlightSProvider(Provider):
    name = "hyperx_flight_s"
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
            self._diag.append("Flight S FF13:0001 collection missing or ambiguous: nothing sent")
            return None
        info = candidates[0]
        physical = info.get("physical_id")
        if not isinstance(physical, str) or not physical or physical.startswith("collection:"):
            self._diag.append("Flight S physical parent unresolved: nothing sent")
            return None
        out_max = info.get("output_report_length")
        out_sizes = info.get("output_report_sizes")
        out_id_len = out_sizes.get(str(OUTPUT_ID)) if isinstance(out_sizes, dict) else None
        if (type(out_max) is not int or not MIN_OUTPUT_LEN <= out_max <= 4096
                or not isinstance(out_sizes, dict) or not out_sizes
                or any(type(n) is not int or not 2 <= n <= out_max for n in out_sizes.values())
                or max(out_sizes.values()) != out_max
                or type(out_id_len) is not int
                or not MIN_OUTPUT_LEN <= out_id_len <= out_max):
            self._diag.append("Flight S output 06 length unproven"
                              + report_proof_summary(info, "output", include_declared=True) + ": nothing sent")
            return None
        in_max = info.get("input_report_length")
        in_sizes = info.get("input_report_sizes")
        if (type(in_max) is not int or not MIN_INPUT_LEN <= in_max <= 4096
                or not isinstance(in_sizes, dict) or not in_sizes
                or any(type(n) is not int or not 2 <= n <= in_max for n in in_sizes.values())
                or max(in_sizes.values()) != in_max
                or not any(type(in_sizes.get(str(rid))) is int and MIN_INPUT_LEN <= in_sizes[str(rid)] <= in_max for rid in INPUT_IDS)):
            self._diag.append("Flight S input report length unproven"
                              + report_proof_summary(info, "input", include_declared=True) + ": nothing sent")
            return None
        return info

    def _query(self, dev, info: dict) -> Optional[tuple]:
        request = make_request(info["output_report_length"])
        if dev.write(request) != len(request):
            self._diag.append("Flight S battery write short/failed; no retry")
            return None
        in_len = info["input_report_length"]
        in_sizes = info["input_report_sizes"]
        seen = set()
        deadline = time.monotonic() + READ_TIMEOUT_MS / 1000
        for _ in range(MAX_READS):
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            raw = dev.read(in_len, max(1, int(remaining * 1000)))
            if not raw:
                break
            if (isinstance(raw, (bytes, bytearray, list, tuple))
                    and len(raw) >= 1 and type(raw[0]) is int and 0 <= raw[0] <= 255
                    and len(seen) < 32):
                seen.add((raw[0], len(raw)))
            if is_power_off_event(raw):
                self._diag.append("Flight S headset powered off; no reading")
                return None
            parsed = parse_reply(raw, in_sizes, input_max=in_len)
            if parsed is not None:
                return parsed
        msg = "Flight S no valid battery reply within timeout"
        if seen:
            ordered = sorted(seen)
            summary = ",".join(f"{rid:02X}/{length}" for rid, length in ordered[:6])
            msg += "; seen=" + summary + (",..." if len(ordered) > 6 else "")
        self._diag.append(msg)
        return None

    def poll(self) -> List[DeviceStatus]:
        self._diag = []
        if hid is None:
            return []
        try:
            inventory = hidlist.enumerate(self.vid)
        except OSError:
            self._diag.append("Flight S enumeration failed: nothing sent")
            return []
        groups = {}
        for info in inventory:
            if info.get("vendor_id") != self.vid or info.get("product_id") not in self.pids:
                continue
            physical = info.get("physical_id", "")
            if not isinstance(physical, str) or not physical or physical.startswith("collection:"):
                self._diag.append("Flight S physical parent unresolved: nothing sent")
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
                        kind="headset", physical_id=physical,
                        connection_known=True, charging_known=False,
                        verification_level="C",
                    ))
            except (OSError, ValueError):
                self._diag.append("Flight S battery I/O failed; connection unknown")
            finally:
                try:
                    dev.close()
                except OSError:
                    self._diag.append("HID handle close failed")
        return result

    def diagnostics(self) -> List[str]:
        return list(self._diag)
