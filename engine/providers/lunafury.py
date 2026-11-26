"""Grade-C Lunafury TYPE33EVO wireless mouse (373E:0084) via Feature Report 0x00.

Evidence: Static analysis of Lunafury's official WebHID driver
(`https://mouse.lunafury.games/`, `js/app.1700b96d.js` + `js/output.xvi3.min.js`,
model config `TYPE33` / `TYPE33_54H` binding `VIDWireless4K8K = 373E`,
`PIDWireless4K8K = 0084`). Pending owner hardware confirmation (#1176).

Reads battery only via the collection whose runtime descriptor proves Feature
report ID 0 = 65 bytes (`FEATURE_BYTES = 65`, ID `0x00` + 64-byte payload).
Never sends bootloader (`0xB0`), firmware update, DPI, LED, or config commands:
- Request Feature 0 (65 bytes): `00 00 00 02 02 00 83` + 58 zero bytes
- Reply (65 bytes w/ ID 0, or 64 bytes w/o ID 0): `[0x00, 0xA1, ..., 0x02, ..., 0x83, charge, percent, ...]`
  (`1 <= percent <= 100`, `charge in (0, 1)`; `0` indicates asleep/offline).
"""
from __future__ import annotations

import re
import time
from typing import List, Optional, Tuple

try:
    import hid
except ImportError:  # pragma: no cover
    hid = None

from . import hidlist
from .base import DeviceStatus, Provider

VID = 0x373E
PIDS = {0x0084: "Lunafury TYPE33EVO"}
COLLECTIONS = ((0xFFA0, 0x0001), (0xFFFF, 0x0000), (0xFFFF, 0x0001))
FEATURE_ID = 0x00
FEATURE_BYTES = 65      # Report ID 0 + 64-byte payload
PAYLOAD_BYTES = 64

READ_ATTEMPTS = 8
RESEND_AT = 4
WRITE_PAUSE_S = 0.05
READ_GAP_S = 0.035

_PARENT = re.compile(
    r"pnp:usb\\vid_373e&pid_0084(?:&rev_[0-9a-f]{4})?\\[^\\\x00\s]+",
    re.I,
)
_HARDWARE = re.compile(r"^\\\\\?\\hid#vid_373e&pid_0084(?:&|#)", re.I)


def make_request() -> bytes:
    """Fixed read-only XVI 0x83 battery status Feature report 0 (65 bytes)."""
    return bytes([FEATURE_ID, 0x00, 0x00, 0x02, 0x02, 0x00, 0x83]) + bytes(FEATURE_BYTES - 7)


def parse_reply(raw) -> Optional[Tuple[int, bool]]:
    """Decode `(percent, charging)` from a Feature 0 battery response, else None."""
    if not isinstance(raw, (bytes, bytearray, list, tuple)):
        return None
    if not isinstance(raw, (bytes, bytearray)):
        if any(type(b) is not int or not 0 <= b <= 255 for b in raw):
            return None
        raw = bytes(raw)
    if len(raw) == FEATURE_BYTES and raw[0] == FEATURE_ID and raw[1] == 0xA1 and raw[4] == 0x02 and raw[6] == 0x83:
        charge_raw, level = raw[7], raw[8]
    elif len(raw) == PAYLOAD_BYTES and raw[0] == 0xA1 and raw[3] == 0x02 and raw[5] == 0x83:
        charge_raw, level = raw[6], raw[7]
    else:
        return None
    if charge_raw not in (0, 1) or not 1 <= level <= 100:
        return None
    return level, charge_raw == 1


def _need_resend(raw) -> bool:
    if not isinstance(raw, (bytes, bytearray, list, tuple)):
        return False
    if len(raw) == FEATURE_BYTES:
        status = raw[1]
    elif len(raw) == PAYLOAD_BYTES:
        status = raw[0]
    else:
        return False
    return type(status) is int and status > 0xA1


def _valid_parent(physical) -> bool:
    return isinstance(physical, str) and not physical.startswith("collection:") and _PARENT.fullmatch(physical) is not None


def _valid_path(path) -> bool:
    if isinstance(path, bytes):
        try:
            text = path.decode("utf-8")
        except UnicodeDecodeError:
            return False
    elif isinstance(path, str):
        text = path
    else:
        return False
    return bool(text) and "\0" not in text and _HARDWARE.match(text) is not None


class LunafuryProvider(Provider):
    name = "lunafury"
    vid = VID
    pids = PIDS
    kind = "mouse"

    def __init__(self):
        self._diag: List[str] = []

    def _pick(self, infos: List[dict]) -> Optional[dict]:
        candidates = [
            d for d in infos
            if d.get("vendor_id") == self.vid
            and d.get("product_id") in self.pids
            and (d.get("usage_page"), d.get("usage")) in COLLECTIONS
        ]
        if not candidates:
            return None
        for d in candidates:
            if not _valid_parent(d.get("physical_id")) or not _valid_path(d.get("path")):
                self._diag.append("physical parent or HID path unproven: nothing sent")
                return None
            if not isinstance(d.get("feature_report_sizes"), dict):
                self._diag.append("report IDs/lengths unknown or incompatible: nothing sent")
                return None
        matches = [
            d for d in candidates
            if d.get("feature_report_length") == FEATURE_BYTES
            and d.get("feature_report_sizes") == {str(FEATURE_ID): FEATURE_BYTES}
        ]
        if len(matches) != 1:
            self._diag.append("missing or ambiguous eligible collection: nothing sent")
            return None
        return matches[0]

    def _query(self, dev) -> Optional[Tuple[int, bool]]:
        req = make_request()
        written = dev.send_feature_report(req)
        if type(written) is int and written != len(req):
            self._diag.append("short/failed battery query write; no retry")
            return None
        time.sleep(WRITE_PAUSE_S)
        resent = False
        for attempt in range(READ_ATTEMPTS):
            try:
                raw = dev.get_feature_report(FEATURE_ID, FEATURE_BYTES)
            except (OSError, ValueError):
                raw = None
            parsed = parse_reply(raw) if raw else None
            if parsed is not None:
                return parsed
            if not resent and attempt == RESEND_AT - 1 and _need_resend(raw):
                resent = True
                written = dev.send_feature_report(req)
                if type(written) is int and written != len(req):
                    self._diag.append("short/failed battery query write; no retry")
                    return None
                time.sleep(WRITE_PAUSE_S)
                continue
            time.sleep(READ_GAP_S)
        self._diag.append("no valid reply within deadline/read limit; connection unknown")
        return None

    def poll(self) -> List[DeviceStatus]:
        self._diag = []
        if hid is None:
            return []
        try:
            inventory = hidlist.enumerate(self.vid)
        except OSError:
            self._diag.append("enumeration failed: nothing sent")
            return []
        groups = {}
        unresolved = False
        for info in inventory:
            if info.get("vendor_id") != self.vid or info.get("product_id") not in self.pids:
                continue
            physical = info.get("physical_id")
            if not _valid_parent(physical):
                unresolved = True
                continue
            groups.setdefault(physical.casefold(), []).append(info)
        if unresolved:
            self._diag.append("physical parent unresolved; uniqueness unknown: nothing sent")
            return []
        result: List[DeviceStatus] = []
        for physical, infos in groups.items():
            info = self._pick(infos)
            if info is None:
                continue
            dev = hid.device()
            try:
                dev.open_path(info["path"])
                reading = self._query(dev)
                if reading is not None:
                    level, charging = reading
                    result.append(DeviceStatus(
                        f"{self.name}:{physical}",
                        self.pids[info["product_id"]],
                        level,
                        charging,
                        True,
                        self.name,
                        kind=self.kind,
                        physical_id=physical,
                        connection_known=True,
                        charging_known=True,
                        verification_level="C",
                        unconfirmed=True,
                    ))
            except (OSError, ValueError):
                self._diag.append("battery I/O failed; connection unknown")
            finally:
                try:
                    dev.close()
                except OSError:
                    self._diag.append("HID handle close failed")
        return result

    def diagnostics(self) -> List[str]:
        return list(self._diag)
