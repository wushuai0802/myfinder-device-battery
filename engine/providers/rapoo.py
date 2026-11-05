"""Passive Rapoo VT nrf54L battery notifications; no device requests.

Only the three wireless IDs below are supported, at evidence level C. The
official A HUB's Ik/dy parser receives report 07 on the default FF00:0002
channel. Report length must be proven from that collection's descriptor.
See upstream-protocols.md for the host route, UI flags and remaining unknowns.
"""
from __future__ import annotations

from dataclasses import replace
from datetime import datetime, timezone
import time

import hid

from . import hidlist
from .base import DeviceStatus, Provider

VID = 0x24AE
PIDS = {
    0x1412: "Rapoo VT3",
    0x1421: "Rapoo VT3S MAX",
    0x14A1: "Rapoo VT0 Air MAX",
}
COLLECTION = (0xFF00, 0x0002)
INPUT_ID = 0x07
READ_WINDOW_MS = 1500
MAX_READS = 32


def parse_battery(raw):
    """Offsets include the ID; never default missing battery bytes to zero.

    A HUB's wireless UI marks states 2 and 3 as charging, 1 as not charging.
    It does not distinguish full from charging. State 0 and other values do
    not establish a charging state; a valid percentage is still kept.
    """
    if len(raw) < 9 or raw[0] != INPUT_ID or not 0 <= raw[8] <= 100:
        return None
    state = raw[7]
    return raw[8], state in (2, 3), state in (1, 2, 3)


class RapooProvider(Provider):
    name = "rapoo"

    def __init__(self):
        self._diag = []
        self._last = {}  # physical USB parent -> last DeviceStatus and receive time
        self._paths = {}  # keep history if parent resolution temporarily fails

    def _pick(self, infos):
        candidates = [d for d in infos
                      if d.get("vendor_id") == VID and d.get("product_id") in PIDS
                      and (d.get("usage_page"), d.get("usage")) == COLLECTION]
        if len(candidates) != 1 or not candidates[0].get("path"):
            self._diag.append("missing or ambiguous FF00:0002 collection; not opened")
            return None
        info = candidates[0]
        maximum = info.get("input_report_length")
        sizes = info.get("input_report_sizes")
        if (type(maximum) is not int or not 9 <= maximum <= 4096
                or not isinstance(sizes, dict) or not sizes
                or any(type(n) is not int or not 2 <= n <= maximum for n in sizes.values())
                or max(sizes.values()) != maximum
                or type(sizes.get("7")) is not int or not 9 <= sizes["7"] <= maximum):
            self._diag.append("input report 07 length unproven; not opened")
            return None
        return info

    def _listen(self, dev, info):
        deadline = time.monotonic() + READ_WINDOW_MS / 1000
        for _ in range(MAX_READS):
            # Floor the timeout so even the last read fits the remaining budget.
            remaining_ms = min(READ_WINDOW_MS, int((deadline - time.monotonic()) * 1000))
            if remaining_ms <= 0:
                break
            raw = dev.read(info["input_report_length"], remaining_ms)
            if not raw:
                break
            if raw[0] != INPUT_ID or len(raw) != info["input_report_sizes"]["7"]:
                continue
            parsed = parse_battery(raw)
            if parsed is not None:
                return parsed
        self._diag.append("no battery notification; no new measurement")
        return None

    @staticmethod
    def _historical(status):
        return replace(status, online=False, cached=True, wake_on_input=True,
                       connection_known=False, charging_known=False)

    def poll(self):
        self._diag = []
        try:
            inventory = hidlist.enumerate(VID)
        except OSError:
            # Failed enumeration cannot prove that a receiver left the USB tree.
            self._diag.append("enumeration failed; retaining history")
            return [self._historical(s) for s in self._last.values()]
        groups = {}
        present_paths = set()
        for info in inventory:
            if info.get("vendor_id") != VID or info.get("product_id") not in PIDS:
                continue
            present_paths.add(info.get("path"))
            physical = info.get("physical_id", "")
            if not physical or physical.startswith("collection:"):
                self._diag.append("USB parent unproven; collection uniqueness unknown; not opened")
                continue
            groups.setdefault(physical, []).append(info)
        result = []
        found = set()
        for physical, infos in groups.items():
            self._paths[physical] = {d.get("path") for d in infos if d.get("path")}
            reading = None
            info = self._pick(infos)
            if info is not None:
                dev = hid.device()
                try:
                    dev.open_path(info["path"])
                    reading = self._listen(dev, info)
                except (OSError, ValueError):
                    self._diag.append("input read failed; no new measurement")
                finally:
                    try:
                        dev.close()
                    except OSError:
                        self._diag.append("HID handle close failed")
            if reading is not None:
                level, charging, charging_known = reading
                received = datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")
                status = DeviceStatus(
                    f"{self.name}:{physical}", PIDS[info["product_id"]],
                    level, charging, True, self.name, kind="mouse",
                    physical_id=physical, connection_known=True,
                    charging_known=charging_known, verification_level="C",
                    measured_at_utc=received, queried_at_utc=received)
                self._last[physical] = status
                found.add(physical)
                result.append(status)
        # Descriptor loss/ambiguity above does not expire a receiver's history.
        # Only absence from the enumerated USB parents removes it.
        for physical in list(self._paths):
            if physical in found:
                continue
            if physical in groups or self._paths[physical] & present_paths:
                if physical in self._last:
                    result.append(self._historical(self._last[physical]))
            else:
                self._last.pop(physical, None)
                del self._paths[physical]
        return result

    def diagnostics(self):
        return list(self._diag)
