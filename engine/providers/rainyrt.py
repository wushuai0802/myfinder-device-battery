"""WOB Rainy75 V2 (36B0:3071 2.4G receiver, 36B0:3075 wired): passive reader.

The dongle pushes a battery frame on the FF60:0061 collection about every five
seconds; nothing is ever written. A frame is BE cmd len ... ED (codec and reply
parsers taken from the official WOB HUB build, which ships
rainy75v2-wireless-test.html): a battery frame is BE BB with 06/0A/0E bytes,
byte 3 is the percentage 0-100 and byte 4 the charge state (0 idle, 1 complete,
2 charging). Report id 0, 256-byte payload.

Silence proves neither sleep nor absence: the receiver keeps answering as long
as the keyboard is linked, so a quiet poll only retains the last reading.
"""
from __future__ import annotations

import time
from datetime import datetime, timezone

from providers import hidlist
from providers.base import DeviceStatus, Provider

try:
    import hid
except ImportError:  # pragma: no cover
    hid = None

VID = 0x36B0
PIDS = {0x3071: "WOB Rainy75 V2（2.4G 接收器）",
        0x3075: "WOB Rainy75 V2（USB）"}
COLLECTION = (0xFF60, 0x0061)
FRAME_END = 0xED
REPLY_LENGTHS = (6, 10, 14)
READ_WINDOW_MS = 6500      # one push every ~5 s, plus slack
DRAIN_TIMEOUT_MS = 400
MAX_DRAIN = 32


def parse_battery(raw):
    """Only a well-formed battery frame inside the padded input report counts."""
    if len(raw) < 6 or raw[0] != 0xBE or raw[1] != 0xBB:
        return None
    length = 6 if raw[2] == 0 else raw[2]
    if length not in REPLY_LENGTHS or length > len(raw) or raw[length - 1] != FRAME_END:
        return None
    level, charging_state = raw[3], raw[4]
    if not 0 <= level <= 100 or charging_state not in (0, 1, 2):
        return None
    return level, charging_state == 2


class RainyRTProvider(Provider):
    name = "rainyrt"
    kind = "keyboard"
    verification_level = "C"
    charging_known = True
    unconfirmed = True

    def __init__(self):
        self._diag = []
        self._handles = {}   # physical id -> (identity, hid device)
        self._last = {}

    def _pick(self, infos):
        candidates = [d for d in infos
                      if d.get("vendor_id") == VID and d.get("product_id") in PIDS
                      and (d.get("usage_page"), d.get("usage")) == COLLECTION]
        if len(candidates) != 1 or not candidates[0].get("path"):
            self._diag.append("missing or ambiguous passive input collection; not opened")
            return None
        info = candidates[0]
        maximum, sizes = info.get("input_report_length"), info.get("input_report_sizes")
        if (type(maximum) is not int or maximum != 257 or not isinstance(sizes, dict)
                or sizes.get("0") != maximum):
            self._diag.append("input report length unproven; not opened")
            return None
        return info

    def _receive(self, dev, newly_opened):
        """Listen within one bounded window; keep the newest frame of the window."""
        deadline = time.monotonic() + READ_WINDOW_MS / 1000
        newest = None
        for index in range(MAX_DRAIN):
            remaining_ms = int((deadline - time.monotonic()) * 1000)
            if remaining_ms <= 0:
                break
            waiting = newly_opened and newest is None
            if waiting and index >= MAX_DRAIN:
                break
            raw = dev.read(256, remaining_ms if waiting else DRAIN_TIMEOUT_MS)
            if not raw:
                break
            if len(raw) != 256:
                continue
            parsed = parse_battery(raw)
            if parsed is not None:
                newest = parsed
        if newest is None:
            self._diag.append("no battery notification; no new measurement")
        return newest

    def _release(self, physical):
        entry = self._handles.pop(physical, None)
        if entry is not None:
            dev = entry[1]
            try:
                dev.close()
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

    @staticmethod
    def _historical(status):
        return DeviceStatus(
            status.key, status.name, status.level, status.charging, False, status.source,
            kind=status.kind, cached=True, measured_at_utc=status.measured_at_utc,
            queried_at_utc=status.queried_at_utc, connection_known=False,
            charging_known=False, physical_id=status.physical_id,
            verification_level=status.verification_level, unconfirmed=status.unconfirmed)

    def poll(self):
        self._diag = []
        if hid is None:
            self._diag.append("hidapi unavailable; nothing opened")
            return []
        try:
            inventory = hidlist.enumerate(VID)
        except OSError:
            self._diag.append("enumeration failed; retaining history")
            return [self._historical(s) for s in self._last.values()]
        groups, present_paths = {}, set()
        for info in inventory:
            if info.get("vendor_id") != VID or info.get("product_id") not in PIDS:
                continue
            present_paths.add(info.get("path"))
            physical = info.get("physical_id", "")
            if not physical or physical.startswith("collection:"):
                self._diag.append("USB parent unproven; collection uniqueness unknown; not opened")
                continue
            groups.setdefault(physical, []).append(info)
        result, found = [], set()
        for physical, infos in groups.items():
            reading, info = None, self._pick(infos)
            if info is None:
                self._release(physical)
            else:
                identity = (info.get("product_id"), tuple(sorted(info["input_report_sizes"].items()))
                            if isinstance(info.get("input_report_sizes"), dict) else None)
                entry = self._handles.get(physical)
                if entry is not None and entry[0] != identity:
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
                        self._handles[physical] = (identity, dev)
                    else:
                        dev = entry[1]
                    reading = self._receive(dev, newly_opened=entry is None)
                except (OSError, ValueError):
                    self._diag.append("input read failed; no new measurement")
                    self._release(physical)
            if reading is not None:
                level, charging = reading
                received = datetime.now(timezone.utc).isoformat(
                    timespec="milliseconds").replace("+00:00", "Z")
                status = DeviceStatus(
                    f"{self.name}:{physical}", self.device_name(info), level, charging, True,
                    self.name, kind=self.kind, physical_id=physical, connection_known=True,
                    charging_known=True, verification_level=self.verification_level,
                    measured_at_utc=received, queried_at_utc=received, unconfirmed=True)
                self._last[physical] = status
                found.add(physical)
                result.append(status)
        for physical in list(self._handles):
            if physical not in groups:
                self._release(physical)
        for physical in list(self._last):
            if physical not in found and physical not in groups:
                self._last.pop(physical, None)
        return result

    def diagnostics(self):
        return list(self._diag)

    def device_name(self, info):
        return info.get("product_string") or PIDS.get(info.get("product_id"), "WOB Rainy75 V2")
