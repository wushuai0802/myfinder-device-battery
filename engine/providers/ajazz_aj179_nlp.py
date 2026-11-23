"""AJ179 NLP receiver battery candidate from pinned public protocol facts."""
from __future__ import annotations

import math
import re
import time

import hid

from . import hidlist
from .base import DeviceStatus, Provider

VID = 0x7768
PIDS = {0x642E: "AJAZZ AJ179 NLP 2.4G (NearLink)"}
COLLECTION = (0xFFB2, 0x0001)
REPORT_ID = 0x06
REPORT_BYTES = 64
READ_TIMEOUT_MS = 120
MAX_READS = 16
DRAIN_TIMEOUT_MS = 40
MAX_DRAIN_READS = 16

_PARENT = re.compile(
    r"pnp:usb\\vid_7768&pid_642e(?:&rev_[0-9a-f]{4})?\\[^\\\x00\s]+",
    re.I,
)
_HARDWARE = re.compile(
    r"vid_7768&pid_642e(?:&rev_[0-9a-f]{4})?&mi_01(?:&col[0-9a-f]{2})?",
    re.I,
)


def _target(info):
    return (info.get("vendor_id") == VID
            and info.get("product_id") in PIDS
            and (info.get("usage_page"), info.get("usage")) == COLLECTION)


def matches_collection(info):
    """Exact interface/path eligibility, independent of parent/report proof."""
    if (not _target(info) or type(info.get("interface_number")) is not int
            or info["interface_number"] != 1):
        return False
    path = info.get("path")
    if not isinstance(path, (str, bytes)):
        return False
    try:
        text = path.decode("utf-8") if isinstance(path, bytes) else path
    except UnicodeError:
        return False
    parts = text.split("#")
    return ("\0" not in text and len(parts) in (3, 4)
            and parts[0].casefold() == "\\\\?\\hid"
            and _HARDWARE.fullmatch(parts[1]) is not None
            and bool(parts[2])
            and (len(parts) == 3 or bool(parts[3])))


def reports_proven(info):
    return all(
        type(info.get(direction + "_report_length")) is int
        and info[direction + "_report_length"] == REPORT_BYTES
        and info.get(direction + "_report_sizes") == {"6": REPORT_BYTES}
        for direction in ("input", "output")
    )


def make_request():
    return bytes([REPORT_ID, 0x05]) + bytes(62)


def _frame(raw):
    return (isinstance(raw, (bytes, bytearray, list, tuple))
            and len(raw) == REPORT_BYTES
            and all(type(value) is int and 0 <= value <= 255 for value in raw)
            and raw[0] == REPORT_ID)


def parse_status(raw):
    if (not _frame(raw) or raw[1] != 0x05
            or (raw[9] == 0 and raw[10] == 0)):
        return None
    if raw[12] == 0:
        # Offline invalidates battery data even if those fields are unusable.
        return None, False, False
    if raw[13] > 100 or raw[14] not in (0, 1):
        return None
    return raw[13], raw[14] == 1, True


def parse_notification(raw):
    if (not _frame(raw) or raw[1] != 0xFA or raw[5] != 0x03
            or not 1 <= raw[6] <= 100 or raw[7] not in (0, 1)):
        return None
    return raw[6], raw[7] == 1, True


class AjazzAj179NlpProvider(Provider):
    name = "ajazz_aj179_nlp"

    def __init__(self):
        self._diag = []

    def _drain(self, dev):
        deadline = time.monotonic() + DRAIN_TIMEOUT_MS / 1000
        for _ in range(MAX_DRAIN_READS):
            if time.monotonic() >= deadline:
                break
            # Positive timeout also bounds bindings where zero selects blocking read.
            if not dev.read(REPORT_BYTES, 1):
                return True
        self._diag.append("input queue did not drain within bounds: nothing sent")
        return False

    def _query(self, dev):
        if not self._drain(dev):
            return None
        written = dev.write(make_request())
        if type(written) is not int or written != REPORT_BYTES:
            self._diag.append("short/failed battery query write; no retry")
            return None

        pending = None
        deadline = time.monotonic() + READ_TIMEOUT_MS / 1000
        for _ in range(MAX_READS):
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            raw = dev.read(REPORT_BYTES, max(1, math.ceil(remaining * 1000)))
            if not raw:
                break
            status = parse_status(raw)
            if status is not None:
                if not status[2]:
                    self._diag.append("device asleep/offline; current level unavailable")
                return status
            notification = parse_notification(raw)
            if notification is not None:
                pending = notification
        if pending is None:
            self._diag.append("no valid battery reply within bounds; connection unknown")
        return pending

    def poll(self):
        self._diag = []
        if hid is None:
            return []
        try:
            inventory = hidlist.enumerate(VID)
        except OSError:
            self._diag.append("enumeration failed: nothing sent")
            return []

        groups = {}
        unresolved = False
        for info in inventory:
            if not _target(info):
                continue
            physical = info.get("physical_id")
            if not isinstance(physical, str) or _PARENT.fullmatch(physical) is None:
                unresolved = True
                continue
            # Count before path, interface, or descriptor-proof filtering.
            groups.setdefault(physical.casefold(), []).append(info)
        if unresolved:
            self._diag.append("target USB parent unresolved; uniqueness unknown: nothing sent")
            return []

        result = []
        for physical, candidates in groups.items():
            if len(candidates) != 1:
                self._diag.append("ambiguous target collection: nothing sent")
                continue
            info = candidates[0]
            if not matches_collection(info):
                self._diag.append("target interface/path unproven: nothing sent")
                continue
            if not reports_proven(info):
                self._diag.append("numbered IN/OUT report 6/64 unproven: nothing sent")
                continue
            dev = None
            try:
                dev = hid.device()
                dev.open_path(info["path"])
                reading = self._query(dev)
                if reading is None:
                    continue
                level, charging, online = reading
                result.append(DeviceStatus(
                    key=f"{self.name}:{physical}",
                    name=PIDS[info["product_id"]],
                    level=level,
                    charging=charging,
                    online=online,
                    source=self.name,
                    kind="mouse",
                    physical_id=physical,
                    connection_known=True,
                    charging_known=online,
                    verification_level="C",
                    level_unavailable=not online,
                    unconfirmed=True,
                ))
            except (OSError, ValueError):
                self._diag.append("battery I/O failed; connection unknown")
            finally:
                if dev is not None:
                    try:
                        dev.close()
                    except OSError:
                        self._diag.append("HID handle close failed")
        return result

    def diagnostics(self):
        return list(self._diag)
