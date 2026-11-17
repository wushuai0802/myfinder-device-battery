"""VGN Dragonfly3 Pro Max and its "ryMs" siblings: one vendor feature report.

Protocol from the vendor's own web HUB (VGN HUB, the "vgn-ry-mouse" driver in
its JavaScript bundle). Every command is one 64-byte feature report with report
ID 0:

    [command, argument, 0, 0, 0, 0, 0, checksum] + zero padding
    checksum = 0xFF - (sum of the first seven bytes & 0xFF)

and the answer is read back with a feature GET on the same report ID. Only the
read-only receiver status query 0xF7 is sent: payload[2] is the level in
percent, payload[4] == 1 means the mouse is asleep and yielded no new reading.
The official HUB follows the query with 0xFC (its own handshake) and, when it
connects, 0xF6 05; neither is needed for a reading, so neither is sent here.

No DPI, button, lighting, profile, pairing, firmware or reset command is used,
so a poll can never alter the mouse's stored configuration.

Verified on hardware (patch author, 2026-11-17): 391D:1A04 replied 92 % and then
91 % to two consecutive queries, one point below the vendor HUB moments earlier.
The other product IDs use the same collection and driver but were not tested.
"""
from __future__ import annotations

import time
from typing import List, Optional

import hid

from . import hidlist
from .base import DeviceStatus, Provider, log

VID = 0x391D
PIDS = {
    0x1A04: "VGN 蜻蜓3 Pro Max",
    0x1004: "VGN 蜻蜓3 Pro Max（有线）",
    0x1A25: "VGN 蜻蜓3 Ultra",
    0x1028: "VGN 蜻蜓3 Ultra（有线）",
    0x1A08: "VGN 蜻蜓 Y2 V2 Pro Max",
    0x1008: "VGN 蜻蜓 Y2 V2 Pro Max（有线）",
    0x1A13: "VGN 蜻蜓 F1 V2 Pro Max",
    0x1015: "VGN 蜻蜓 F1 V2 Pro Max（有线）",
    0x1A2B: "VGN 蜻蜓 F1 V5 Ultra",
    0x102F: "VGN 蜻蜓 F1 V5 Ultra（有线）",
    0x1A41: "VGN 蜻蜓 F2 V2 Ultra",
    0x1048: "VGN 蜻蜓 F2 V2 Ultra（有线）",
}

COLLECTION = (0xFFFF, 0x0002)     # the vendor collection with the feature report
REPORT_ID = 0
REPORT_BYTES = 64                 # payload; Windows adds the report-ID slot
CMD_STATUS = 0xF7
READ_ATTEMPTS = 3
REPLY_WAIT = 0.06                 # the receiver answers a feature GET immediately

# Receiver PIDs: the dongle carries the level while the mouse is in sleep.
WIRELESS = frozenset({0x1A04, 0x1A25, 0x1A08, 0x1A13, 0x1A2B, 0x1A41})
# Product IDs read on hardware; the others share the collection but are untested.
VERIFIED = frozenset({0x1A04})


def checksum(prefix):
    return 0xFF - (sum(prefix) & 0xFF)


def make_request(command=CMD_STATUS):
    body = bytearray([command, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00])
    body.append(checksum(body))
    # hidapi needs the ID-0 slot; WebHID sends the 64-byte payload alone.
    return bytes([REPORT_ID]) + bytes(body) + bytes(REPORT_BYTES - len(body))


def requested_payload():
    return make_request()[1:]


def parse_reply(raw):
    """Level, asleep, or None when this is not a trustworthy status reply."""
    if not isinstance(raw, (bytes, bytearray)) or len(raw) != REPORT_BYTES + 1:
        return None
    payload = list(raw[1:])
    if payload == list(requested_payload()) or payload[0] != 0x00:
        return None                                   # echo or foreign report
    if payload[4] not in (0, 1) or not 0 <= payload[2] <= 100:
        return None
    return payload[2], payload[4] == 1


class VgnRyMsProvider(Provider):
    name = "vgn_ry_ms"

    def __init__(self):
        self._diag: List[str] = []

    def device_name(self, info):
        return PIDS[info["product_id"]]

    def _read(self, dev):
        for attempt in range(READ_ATTEMPTS):
            if attempt:
                time.sleep(0.03)
            try:
                written = dev.send_feature_report(make_request())
            except (OSError, ValueError):
                self._diag.append("battery query write failed; no retry")
                return None
            if written != REPORT_BYTES + 1:
                self._diag.append("short/failed battery query write; no retry")
                return None
            time.sleep(REPLY_WAIT)
            try:
                raw = dev.get_feature_report(REPORT_ID, REPORT_BYTES + 1)
            except (OSError, ValueError):
                self._diag.append("battery reply read failed")
                continue
            parsed = parse_reply(bytes(raw))
            if parsed is not None:
                return parsed
            self._diag.append("rejected battery reply")
        return None

    def poll(self) -> List[DeviceStatus]:
        self._diag = []
        try:
            infos = hidlist.enumerate(VID)
        except OSError:  # pragma: no cover - defensive
            log.warning("hid.enumerate(vgn_ry_ms) failed")
            self._diag.append("enumeration failed: nothing sent")
            return []
        groups = {}
        for info in infos:
            if info.get("vendor_id") != VID or info.get("product_id") not in PIDS:
                continue
            physical = info.get("physical_id", "")
            if physical.startswith("collection:"):
                self._diag.append("physical parent unresolved; uniqueness unknown: nothing sent")
                continue
            if (info.get("usage_page"), info.get("usage")) != COLLECTION:
                continue
            groups.setdefault(physical or f"pid:{info['product_id']}", []).append(info)
        out = []
        for physical, candidates in groups.items():
            if len(candidates) != 1 or not candidates[0].get("path"):
                self._diag.append("missing or ambiguous eligible collection: nothing sent")
                continue
            info = candidates[0]
            name = self.device_name(info)
            dev = hid.device()
            try:
                dev.open_path(info["path"])
            except OSError:
                self._diag.append("device open failed; nothing sent")
                continue
            try:
                reading = self._read(dev)
            finally:
                try:
                    dev.close()
                except OSError:
                    self._diag.append("HID handle close failed")
            if reading is None:
                continue
            level, asleep = reading
            out.append(DeviceStatus(
                f"{self.name}:{physical or repr(info['path'])}", name,
                None if asleep else level, False, not asleep, self.name,
                kind="mouse", physical_id=physical,
                connection_known=True, charging_known=False,
                verification_level="A" if info["product_id"] in VERIFIED else "C",
                level_unavailable=asleep, unconfirmed=False, wake_on_input=asleep))
            if asleep:
                self._diag.append("device asleep/offline; no new reading; connection unknown")
        return out

    def diagnostics(self) -> List[str]:
        return list(self._diag)
