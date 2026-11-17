"""SKN 青龙 4.0 Ultra: the vendor's read-only "NAPE" battery query.

The keyboard and its 2.4 GHz receiver are engineered by Keychron (渴创技术): the
SKN Launcher is the Keychron Launcher build with an SKN skin, so the protocol
comes from that bundle rather than from a capture.

    channel : the raw-HID vendor collection (0xFF60:0x0061, VIA's raw HID) or
              the 0x008C:0x0001 collection some models expose instead: one
              32-byte unnumbered report, report ID 0.
    request : A7 31 00 ... (0xA7 = the user-command wrapper, 0x31 =
              KC_USER_CMD_NAPE_GET_BAT_REPORT; the sibling 0x30 is the
              asynchronous notification the firmware sends by itself).
    reply   : A7 31 <level> <state> ..., level 0..100, state 1 while charging.

A receiver that does not implement the command echoes the query unchanged; the
echo is rejected, so no level is invented. A genuine 0 % / not-charging reply is
byte-identical to that echo and is therefore rejected too: the reader never
reports 0 % from this channel.

What this hardware does, measured here: the 362D:D028 "Ultra-Link 8K" receiver
answers the NAPE query on neither 0xFF60:0x0061 (echo) nor 0x008C:0x0001 (that
collection has no host-writable output report), and its own 0xFFC1:0x0001 bridge
channel never answers, so nothing is reported over 2.4 GHz. Connected by cable
the same keyboard (362D:132B) echoes it as well: its firmware answers only the
low user-command ids (0xA7 0x01 and 0xA7 0x05 return real data) and echoes every
NAPE code, 0x30/0x31 included. This model therefore exposes no battery level
over HID at all, and the provider stays silent on it rather than inventing one.
It is kept because the protocol is the family's read-only query, and a model
that implements it would answer; no such model has been measured yet.

Only the battery query is sent: no keymap, lighting, macro, profile, pairing,
firmware or reset command is used.
"""
from __future__ import annotations

import time
from typing import List, Optional

import hid

from . import hidlist
from .base import DeviceStatus, Provider, log

VID = 0x362D
PIDS = {
    0x132B: "SKN 青龙 4.0 Ultra 8K",
    0xD028: "SKN Ultra-Link 8K 接收器",
}

# (usage page, usage) of the vendor collections that can carry the query.
COLLECTIONS = ((0xFF60, 0x0061), (0x008C, 0x0001))

REPORT_ID = 0
REPORT_BYTES = 32                 # payload; Windows adds the report-ID slot
CMD_WRAPPER = 0xA7
CMD_GET_BATTERY = 0x31
CMD_BATTERY_REPORT = 0x30
READ_TIMEOUT_MS = 1500            # a receiver answers well inside this
MAX_READS = 16


def make_request(command=CMD_GET_BATTERY):
    body = bytearray([CMD_WRAPPER, command]) + bytearray(REPORT_BYTES - 2)
    return bytes([REPORT_ID]) + bytes(body)


def parse_reply(raw):
    """Level and charging state, or None when this is not a battery reply."""
    if not isinstance(raw, (bytes, bytearray)) or len(raw) != REPORT_BYTES:
        return None
    if raw[0] != CMD_WRAPPER or raw[1] not in (CMD_GET_BATTERY, CMD_BATTERY_REPORT):
        return None
    if list(raw) == list(make_request(raw[1])[1:]):
        return None                                   # the receiver's echo
    if raw[3] not in (0, 1) or not 0 <= raw[2] <= 100:
        return None
    return raw[2], raw[3] == 1


class SknNapeKeyboardProvider(Provider):
    name = "skn_nape_keyboard"

    def __init__(self):
        self._diag: List[str] = []

    def device_name(self, info):
        return PIDS[info["product_id"]]

    def _query(self, info):
        dev = hid.device()
        try:
            dev.open_path(info["path"])
        except OSError:
            self._diag.append("device open failed; nothing sent")
            return None
        try:
            request = make_request()
            try:
                written = dev.write(request)
            except (OSError, ValueError):
                self._diag.append("battery query write failed; no retry")
                return None
            if written != len(request):
                self._diag.append("short/failed battery query write; no retry")
                return None
            deadline = time.monotonic() + READ_TIMEOUT_MS / 1000
            for _ in range(MAX_READS):
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                try:
                    raw = dev.read(REPORT_BYTES, max(1, int(remaining * 1000)))
                except (OSError, ValueError):
                    self._diag.append("battery reply read failed")
                    break
                if not raw:
                    continue
                parsed = parse_reply(bytes(raw))
                if parsed is not None:
                    return parsed
                self._diag.append("rejected battery reply")
            self._diag.append("no valid reply within deadline/read limit; connection unknown")
            return None
        finally:
            try:
                dev.close()
            except OSError:
                self._diag.append("HID handle close failed")

    def poll(self) -> List[DeviceStatus]:
        self._diag = []
        try:
            infos = hidlist.enumerate(VID)
        except OSError:  # pragma: no cover - defensive
            log.warning("hid.enumerate(skn_nape_keyboard) failed")
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
            groups.setdefault(physical or f"pid:{info['product_id']}", []).append(info)
        out = []
        for physical, candidates in groups.items():
            reading = None
            tried = set()
            for collection in COLLECTIONS:
                eligible = [info for info in candidates
                            if (info.get("usage_page"), info.get("usage")) == collection
                            and info.get("path") and info["path"] not in tried]
                if len(eligible) != 1:
                    continue
                tried.add(eligible[0]["path"])
                reading = self._query(eligible[0])
                if reading is not None:
                    info = eligible[0]
                    break
            if reading is None:
                continue
            level, charging = reading
            name = self.device_name(info)
            out.append(DeviceStatus(
                f"{self.name}:{physical or repr(info['path'])}", name, level, charging,
                True, self.name, kind="keyboard", physical_id=physical,
                connection_known=True, charging_known=True,
                verification_level="C", level_unavailable=False, unconfirmed=True))
        return out

    def diagnostics(self) -> List[str]:
        return list(self._diag)
