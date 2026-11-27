"""Grade-C HyperX Cloud Stinger Core Wireless DTS battery reader over FF00:0001.

Protocol derived from MIT-licensed davidsantanaalves/HyperBatteryMonitor
@ 999d40bc1502d6e932c5c3df4e6a32d5f00c7158 (Devices/CloudStingerCoreWirelessDevice.cs):
- Kingston original 0951:170B
- HP revision 03F0:0A93 (HyperBatteryMonitor PR #6)
- HP revision 03F0:0A8E (HyperBatteryMonitor PR #16; also observed in MyFinder user report)

Only opens the proven FF00:0001 vendor collection with Feature report 0xFF (255)
in [32..128] bytes (never opens FFC0:0001 mic/mute or 000C:0001 consumer control).
Sends only the read-only connection check and battery voltage Feature queries.
"""
from __future__ import annotations

import math
import re
import time
from typing import List, Optional, Tuple

try:
    import hid
except ImportError:  # pragma: no cover
    hid = None

from battery_hid import report_proof_summary
from . import hidlist
from .base import DeviceStatus, Provider

HP_VID = 0x03F0
KINGSTON_VID = 0x0951
PIDS = {
    (HP_VID, 0x0A8E): "HyperX Cloud Stinger Core Wireless DTS",
    (HP_VID, 0x0A93): "HyperX Cloud Stinger Core Wireless DTS",
    (KINGSTON_VID, 0x170B): "HyperX Cloud Stinger Core Wireless DTS",
}
COLLECTION = (0xFF00, 0x0001)
REPORT_ID = 0xFF
MIN_FEATURE_LEN = 32
MAX_FEATURE_LEN = 128

POLL_WAIT_S = 0.02
HP_MAX_READS = 10
KINGSTON_MAX_READS = 1

MIN_VALID_MV = 2500
MAX_VALID_MV = 5000

CONNECTION_PREFIX = bytes((
    0xFF, 0x07, 0x00, 0xFD, 0x04, 0x00, 0x00, 0x02,
    0x82, 0x03, 0x05, 0x29, 0x0F, 0x00, 0x00, 0x03,
    0x68, 0x10, 0x00, 0x00,
))
BATTERY_PREFIX = bytes((
    0xFF, 0x07, 0x00, 0xFD, 0x04, 0x0C, 0xF1, 0x02,
    0x01, 0x04, 0xF0, 0x0C,
))

# Piecewise-linear 10%-knot mV curves from HyperBatteryMonitor (MIT).
CHARGING_MV = (
    3400.0, 3830.9, 3870.7, 3916.2, 3929.9, 3945.6,
    3967.4, 4000.0, 4049.7, 4090.0, 4130.0,
)
DISCHARGING_MV = (
    3517.4, 3650.8, 3699.8, 3738.1, 3763.3, 3784.7,
    3838.1, 3905.2, 3954.2, 4042.3, 4120.5,
)


def _valid_parent(physical: object, vid: int, pid: int) -> bool:
    if not isinstance(physical, str) or not physical or physical.casefold().startswith("collection:"):
        return False
    return re.fullmatch(
        rf"pnp:usb\\vid_{vid:04x}&pid_{pid:04x}(?:&rev_[0-9a-f]{{4}})?\\[^\\\s]+",
        physical,
        re.I,
    ) is not None


def _valid_bytes(raw: object, expected_length: int) -> bool:
    if (not isinstance(raw, (bytes, bytearray, list, tuple))
            or len(raw) != expected_length):
        return False
    if not isinstance(raw, (bytes, bytearray)):
        if any(type(b) is not int or not 0 <= b <= 255 for b in raw):
            return False
    return True


def make_request(prefix: bytes, report_length: int) -> bytes:
    if (type(report_length) is not int
            or not MIN_FEATURE_LEN <= report_length <= MAX_FEATURE_LEN
            or len(prefix) > report_length):
        raise ValueError("invalid Stinger Core feature report length")
    return bytes(prefix) + bytes(report_length - len(prefix))


def voltage_to_percent(millivolts: int, table: Tuple[float, ...]) -> int:
    if millivolts <= table[0]:
        return 0
    if millivolts >= table[-1]:
        return 100
    for idx in range(len(table) - 1):
        low, high = table[idx], table[idx + 1]
        if low <= millivolts <= high:
            span = high - low
            frac = 0.0 if span <= 0 else (millivolts - low) / span
            pct = int(math.floor(idx * 10 + frac * 10.0))
            return max(0, min(100, pct))
    return 100


def is_stale_hp_connection_reply(raw: object, expected_length: int) -> bool:
    return (
        _valid_bytes(raw, expected_length)
        and raw[0] == REPORT_ID
        and raw[1] == 0x01
        and raw[2] == 0x00
    )


def parse_connection_reply(raw: object, expected_length: int) -> Optional[bool]:
    if not _valid_bytes(raw, expected_length):
        return None
    if raw[0] != REPORT_ID or not any(raw[1:]):
        return None
    if bytes(raw[:len(CONNECTION_PREFIX)]) == CONNECTION_PREFIX:
        return None
    # Reject stale HP frames and battery-shaped replies that cannot answer connection state.
    if (raw[1] == 0x01 and raw[2] == 0x00) or (raw[1] == 0x12 and raw[2] == 0x05):
        return None
    if raw[11] == 0x01:
        return True
    if raw[11] == 0x00:
        return False
    return None


def parse_battery_reply(
    raw: object,
    expected_length: int,
    *,
    require_hp_header: bool = False,
) -> Optional[Tuple[int, bool, str]]:
    if not _valid_bytes(raw, expected_length):
        return None
    if raw[0] != REPORT_ID or not any(raw[1:]):
        return None
    if bytes(raw[:len(BATTERY_PREFIX)]) == BATTERY_PREFIX:
        return None
    if require_hp_header and (raw[1] != 0x12 or raw[2] != 0x05):
        return None
    state_code = raw[9]
    if state_code == 0x06:
        return 100, False, ""
    if state_code not in (0x03, 0x05):
        return None
    mv = int.from_bytes(bytes((raw[11], raw[12])), "little", signed=True)
    if not MIN_VALID_MV <= mv <= MAX_VALID_MV:
        return None
    charging = state_code == 0x05
    level = voltage_to_percent(mv, CHARGING_MV if charging else DISCHARGING_MV)
    return level, charging, f"about {level}%"


class HyperXStingerCoreProvider(Provider):
    name = "hyperx_stinger_core"
    collection = COLLECTION

    def __init__(self):
        self._diag: List[str] = []

    def _pick(self, physical: str, vid: int, pid: int, infos: List[dict]) -> Optional[dict]:
        candidates = [
            d for d in infos
            if (d.get("vendor_id"), d.get("product_id")) == (vid, pid)
            and (d.get("usage_page"), d.get("usage")) == self.collection
        ]
        if (len(candidates) != 1
                or not isinstance(candidates[0].get("path"), (bytes, str))
                or not candidates[0].get("path")):
            self._diag.append("Stinger Core FF00:0001 collection missing or ambiguous: nothing sent")
            return None
        info = candidates[0]
        if not _valid_parent(physical, vid, pid):
            self._diag.append("Stinger Core USB parent unproven: nothing sent")
            return None
        feat_len = info.get("feature_report_length")
        feat_sizes = info.get("feature_report_sizes")
        feat_id_len = feat_sizes.get(str(REPORT_ID)) if isinstance(feat_sizes, dict) else None
        if (type(feat_len) is not int
                or not MIN_FEATURE_LEN <= feat_len <= MAX_FEATURE_LEN
                or not isinstance(feat_sizes, dict)
                or type(feat_id_len) is not int
                or feat_id_len != feat_len):
            self._diag.append(
                "Stinger Core feature report FF length unproven"
                + report_proof_summary(info, "feature", include_declared=True)
                + ": nothing sent"
            )
            return None
        return info

    def _query(self, dev, info: dict) -> Optional[Tuple[int, bool, str]]:
        vid = info["vendor_id"]
        feat_len = info["feature_report_length"]
        is_hp = vid == HP_VID
        max_reads = HP_MAX_READS if is_hp else KINGSTON_MAX_READS

        conn_req = make_request(CONNECTION_PREFIX, feat_len)
        if dev.send_feature_report(conn_req) != len(conn_req):
            self._diag.append("Stinger Core connection query write short/failed; no retry")
            return None

        conn_raw = None
        for _ in range(max_reads):
            time.sleep(POLL_WAIT_S)
            raw = dev.get_feature_report(REPORT_ID, feat_len)
            if not _valid_bytes(raw, feat_len):
                continue
            if is_hp and is_stale_hp_connection_reply(raw, feat_len):
                continue
            conn_raw = raw
            break

        connected = parse_connection_reply(conn_raw, feat_len)
        if connected is None:
            self._diag.append("Stinger Core connection reply invalid or timed out")
            return None
        if not connected:
            self._diag.append("Stinger Core headset disconnected/off; no battery query sent")
            return None

        batt_req = make_request(BATTERY_PREFIX, feat_len)
        if dev.send_feature_report(batt_req) != len(batt_req):
            self._diag.append("Stinger Core battery query write short/failed; no retry")
            return None

        for _ in range(max_reads):
            time.sleep(POLL_WAIT_S)
            raw = dev.get_feature_report(REPORT_ID, feat_len)
            parsed = parse_battery_reply(raw, feat_len, require_hp_header=is_hp)
            if parsed is not None:
                return parsed

        self._diag.append("Stinger Core no valid battery reply within read budget")
        return None

    def poll(self) -> List[DeviceStatus]:
        self._diag = []
        if hid is None:
            return []
        inventory = []
        for vid in (HP_VID, KINGSTON_VID):
            try:
                inventory.extend(hidlist.enumerate(vid))
            except OSError:
                self._diag.append("Stinger Core enumeration failed: nothing sent")
                return []

        groups = {}
        for info in inventory:
            pair = (info.get("vendor_id"), info.get("product_id"))
            if pair not in PIDS:
                continue
            physical = info.get("physical_id", "")
            if not _valid_parent(physical, pair[0], pair[1]):
                self._diag.append("Stinger Core USB parent unproven: nothing sent")
                continue
            groups.setdefault((physical, pair[0], pair[1]), []).append(info)

        result = []
        for (physical, vid, pid), infos in groups.items():
            info = self._pick(physical, vid, pid, infos)
            if info is None:
                continue
            dev = hid.device()
            try:
                dev.open_path(info["path"])
                reading = self._query(dev, info)
                if reading is not None:
                    level, charging, approx = reading
                    result.append(DeviceStatus(
                        f"{self.name}:{physical}",
                        PIDS[(vid, pid)],
                        level,
                        charging,
                        True,
                        self.name,
                        approx=approx,
                        kind="headset",
                        physical_id=physical,
                        connection_known=True,
                        charging_known=True,
                        verification_level="C",
                    ))
            except (OSError, ValueError):
                self._diag.append("Stinger Core battery I/O failed; connection unknown")
            finally:
                try:
                    dev.close()
                except OSError:
                    self._diag.append("HID handle close failed")
        return result

    def diagnostics(self) -> List[str]:
        return list(self._diag)
