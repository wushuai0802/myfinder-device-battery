"""Grade-C Rapoo VT9 PRO read-only status (Feature 0x2A) and passive 0xBB input.

Evidence: OpenMouse 93c4cc601d448ea481eb59d7b785eec75ae16ec2 (docs/rapoo-vt9-pro-testing.md,
src/rapoo/index.ts for 24AE:1205 receiver & 24AE:4405 cable) plus user diagnostics for
24AE:1221 (Rapoo VT9 PRO Dual 8K exposing FF0B:0104 + FF00:000E). Pending maintainer
hardware verification.

Strictly read-only / zero writes: never calls write(), send_feature_report(), or 0xBA.
- Primary channel: FF0B:0104 GET_FEATURE report 0x2A (ID 42), proven >= 10 bytes.
- Fallback channel: FF00:000E passive Input report 0xBB (ID 187), proven >= 7 bytes.
Unverified older VT9 PIDs (e.g. 24AE:1601, 185A, 1849) remain strictly excluded.
"""
from __future__ import annotations

from dataclasses import replace
from datetime import datetime, timezone
import re
import time

import hid

from battery_hid import report_proof_summary
from . import hidlist
from .base import DeviceStatus, Provider
from .rapoo import (
    DRAIN_TIMEOUT_MS,
    MAX_DRAIN,
    MAX_READS,
    READ_WINDOW_MS,
    RapooProvider,
    _sizes_summary,
)

VID = 0x24AE
PIDS = {
    0x1221: "Rapoo VT9 PRO Dual 8K",
    0x1205: "Rapoo VT9 PRO",
    0x4405: "Rapoo VT9 PRO",
}
FEATURE_COLLECTION = (0xFF0B, 0x0104)
FEATURE_ID = 0x2A
MIN_FEATURE_LEN = 10

INPUT_COLLECTION = (0xFF00, 0x000E)
INPUT_ID = 0xBB
MIN_INPUT_LEN = 7


def _identity_consistent(value, prefix: str, pid: int) -> bool:
    """Accept only the Windows topology builder's exact USB parent and HID path."""
    if isinstance(value, bytes):
        try:
            value = value.decode("ascii")
        except UnicodeDecodeError:
            return False
    if not isinstance(value, str) or not value:
        return False
    value = value.lower()
    if not value.startswith(prefix) or len(value) <= len(prefix):
        return False
    expected_pid = f"{pid:04x}"
    for key, expected in (("vid", "24ae"), ("pid", expected_pid)):
        values = re.findall(key + r"_([0-9a-f]{4})(?![0-9a-z])", value)
        if (len(values) != value.count(key + "_")
                or any(item != expected for item in values)):
            return False
    return True


def parse_feature_battery(raw, expected_size: int):
    """Decode GET_FEATURE 0x2A on FF0B:0104 -> (percent, False, False) or None."""
    if not raw or len(raw) != expected_size or len(raw) < MIN_FEATURE_LEN:
        return None
    if raw[0] != FEATURE_ID or not any(raw[1:9]):
        return None
    level = raw[9]
    if type(level) is not int or not 0 <= level <= 100:
        return None
    return level, False, False


def parse_input_battery(raw):
    """Decode passive Input 0xBB on FF00:000E -> (percent, False, False) or None."""
    if (len(raw) < MIN_INPUT_LEN or raw[0] != INPUT_ID or raw[1] != 0xB0
            or raw[5] not in (1, 2) or type(raw[6]) is not int
            or not 0 <= raw[6] <= 100):
        return None
    return raw[6], False, False


class RapooVT9Provider(Provider):
    name = "rapoo_vt9"
    product_ids = PIDS
    collection = INPUT_COLLECTION
    input_id = INPUT_ID
    minimum_input_length = MIN_INPUT_LEN
    parse_input = staticmethod(parse_input_battery)

    def __init__(self):
        self._diag = []
        self._last = {}
        self._paths = {}
        self._handles = {}
        self._seen_inputs = {}

    def _valid_identity(self, info) -> bool:
        pid = info.get("product_id")
        if pid not in self.product_ids:
            return False
        physical = info.get("physical_id")
        path = info.get("path")
        if (not isinstance(physical, str) or not physical
                or physical.startswith("collection:")
                or not _identity_consistent(physical, f"pnp:usb\\vid_24ae&pid_{pid:04x}\\", pid)):
            self._diag.append("VT9 USB parent unproven; not opened")
            return False
        if not (_identity_consistent(path, f"\\\\?\\hid#vid_24ae&pid_{pid:04x}&", pid)
                or _identity_consistent(path, f"\\\\?\\hid#vid_24ae&pid_{pid:04x}#", pid)):
            self._diag.append("VT9 HID path unproven; not opened")
            return False
        return True

    def _pick_proven(self, infos, collection, direction: str, report_id: int, min_len: int):
        candidates = [d for d in infos if (d.get("usage_page"), d.get("usage")) == collection]
        if not candidates:
            return None
        if len(candidates) != 1 or not candidates[0].get("path"):
            page, usage = collection
            self._diag.append(f"ambiguous {page:04X}:{usage:04X} collection; not opened")
            return None
        info = candidates[0]
        if not self._valid_identity(info):
            return None
        maximum = info.get(f"{direction}_report_length")
        sizes = info.get(f"{direction}_report_sizes")
        key = str(report_id)
        if (type(maximum) is not int or not min_len <= maximum <= 4096
                or not isinstance(sizes, dict) or not sizes
                or any(type(n) is not int or not 2 <= n <= maximum for n in sizes.values())
                or max(sizes.values()) != maximum
                or type(sizes.get(key)) is not int
                or not min_len <= sizes[key] <= maximum):
            self._diag.append(
                f"report {report_id:02X} unproven; sizes={_sizes_summary(sizes)}"
                + report_proof_summary(info, direction, include_declared=True) + "; not opened")
            return None
        return info

    def _read_feature(self, info):
        size = info["feature_report_sizes"][str(FEATURE_ID)]
        maximum = info["feature_report_length"]
        dev = hid.device()
        try:
            dev.open_path(info["path"])
            raw = list(dev.get_feature_report(FEATURE_ID, maximum) or [])
        except (OSError, ValueError):
            self._diag.append("feature read failed; no new measurement"
                              + report_proof_summary(info, "feature"))
            return None, None
        finally:
            try:
                dev.close()
            except OSError:
                self._diag.append("HID handle close failed")
        if len(raw) not in (size, maximum):
            self._diag.append("feature report length mismatch; rejected"
                              + report_proof_summary(info, "feature"))
            return None, info
        parsed = parse_feature_battery(raw[:size], size)
        if parsed is None:
            self._diag.append("feature 2A empty or out of range; no new measurement")
            return None, info
        return parsed, info

    def _observe_input(self, info, raw):
        seen = self._seen_inputs.setdefault(info["path"], set())
        if len(seen) < 32:
            seen.add((raw[0], len(raw)))

    def _no_measurement(self, info):
        seen = sorted(self._seen_inputs.get(info["path"], ()))
        if not seen:
            self._diag.append("opened; no input reports; no new measurement"
                              + report_proof_summary(info, "input"))
        else:
            summary = ",".join(f"{report_id:02X}/{length}" for report_id, length in seen[:6])
            self._diag.append(f"opened; no valid report {INPUT_ID:02X}; seen=" + summary
                              + (",..." if len(seen) > 6 else "") + report_proof_summary(info, "input"))

    def _listen(self, dev, info):
        deadline = time.monotonic() + READ_WINDOW_MS / 1000
        expected_len = info["input_report_sizes"][str(INPUT_ID)]
        for _ in range(MAX_READS):
            remaining_ms = min(READ_WINDOW_MS, int((deadline - time.monotonic()) * 1000))
            if remaining_ms <= 0:
                break
            raw = dev.read(info["input_report_length"], remaining_ms)
            if not raw:
                break
            self._observe_input(info, raw)
            if raw[0] != INPUT_ID or len(raw) != expected_len:
                continue
            parsed = parse_input_battery(raw)
            if parsed is not None:
                self._seen_inputs.pop(info["path"], None)
                return parsed
        self._no_measurement(info)
        return None

    def _drain(self, dev, info):
        newest = None
        expected_len = info["input_report_sizes"][str(INPUT_ID)]
        for _ in range(MAX_DRAIN):
            raw = dev.read(info["input_report_length"], DRAIN_TIMEOUT_MS)
            if not raw:
                break
            self._observe_input(info, raw)
            if raw[0] != INPUT_ID or len(raw) != expected_len:
                continue
            parsed = parse_input_battery(raw)
            if parsed is not None:
                newest = parsed
                self._seen_inputs.pop(info["path"], None)
        if newest is None:
            self._no_measurement(info)
        return newest

    def _read_passive(self, physical: str, infos):
        info = self._pick_proven(infos, INPUT_COLLECTION, "input", INPUT_ID, MIN_INPUT_LEN)
        if info is None:
            self._release(physical)
            return None, None
        entry = self._handles.get(physical)
        if entry is not None and entry[0] != info["path"]:
            self._release(physical)
            entry = None
        try:
            if entry is None:
                dev = hid.device()
                try:
                    dev.open_path(info["path"])
                except (OSError, ValueError):
                    try:
                        dev.close()
                    except OSError:
                        pass
                    raise
                self._handles[physical] = (info["path"], dev)
                reading = self._listen(dev, info)
            else:
                reading = self._drain(entry[1], info)
            return reading, info
        except (OSError, ValueError):
            self._diag.append("input read failed; no new measurement"
                              + report_proof_summary(info, "input"))
            self._release(physical)
            return None, info

    def _release(self, physical: str):
        entry = self._handles.pop(physical, None)
        if entry is not None:
            self._seen_inputs.pop(entry[0], None)
            try:
                entry[1].close()
            except OSError:
                self._diag.append("HID handle close failed")

    def close(self):
        for physical in list(self._handles):
            self._release(physical)

    def __del__(self):
        try:
            self.close()
        except Exception:
            pass

    def poll(self):
        self._diag = []
        try:
            inventory = hidlist.enumerate(VID)
        except OSError:
            self._diag.append("enumeration failed; retaining history")
            return [RapooProvider._historical(s) for s in self._last.values()]

        groups = {}
        present_paths = set()
        seen_paths = set()
        ambiguous_paths = False
        for info in inventory:
            if info.get("vendor_id") != VID or info.get("product_id") not in self.product_ids:
                continue
            path = info.get("path")
            present_paths.add(path)
            if isinstance(path, bytes):
                norm_path = path.decode("ascii", "surrogateescape").lower()
            elif isinstance(path, str):
                norm_path = path.lower()
            else:
                norm_path = ""
            if norm_path:
                if norm_path in seen_paths:
                    ambiguous_paths = True
                seen_paths.add(norm_path)
            physical = info.get("physical_id", "")
            if not physical or physical.startswith("collection:"):
                self._diag.append("VT9 USB parent unproven; collection uniqueness unknown; not opened")
                continue
            groups.setdefault(physical, []).append(info)

        result = []
        found = set()
        for physical, infos in groups.items():
            self._paths[physical] = {d.get("path") for d in infos if d.get("path")}
            if ambiguous_paths or len({d.get("product_id") for d in infos}) != 1:
                self._diag.append("ambiguous VT9 collection path or mixed PIDs; not opened")
                self._release(physical)
                continue

            reading = None
            winning = None
            feature_info = self._pick_proven(infos, FEATURE_COLLECTION, "feature", FEATURE_ID, MIN_FEATURE_LEN)
            if feature_info is not None:
                reading, winning = self._read_feature(feature_info)

            if reading is None:
                passive_reading, passive_info = self._read_passive(physical, infos)
                if passive_reading is not None:
                    reading, winning = passive_reading, passive_info
            else:
                self._release(physical)

            if reading is not None and winning is not None:
                level, charging, charging_known = reading
                received = datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")
                status = DeviceStatus(
                    f"{self.name}:{physical}", self.product_ids[winning["product_id"]],
                    level, charging, True, self.name, kind="mouse",
                    physical_id=physical, connection_known=True,
                    charging_known=charging_known, verification_level="C",
                    measured_at_utc=received, queried_at_utc=received,
                )
                self._last[physical] = status
                found.add(physical)
                result.append(status)

        for physical in list(self._paths):
            if physical in found:
                continue
            if physical in groups or self._paths[physical] & present_paths:
                if physical in self._last:
                    result.append(RapooProvider._historical(self._last[physical]))
            else:
                self._last.pop(physical, None)
                del self._paths[physical]
        for physical in [p for p in self._handles if p not in groups]:
            self._release(physical)
        return result

    def diagnostics(self):
        return list(self._diag)
