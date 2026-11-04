"""MCHOSE V9 PRO, 291D:385D / FF90:0001 only (verification level C).

Protocol facts: research/p2-protocols.md section 4, sources S8-S10 (MIT
projects and upstream hardware reports); S14 is corroboration only, no code
copied. Output/input reports include ID 55 and are exactly 64 bytes. Only
the battery query 55 65 01 is sent; no feature, EQ or firmware commands.

00/03 mean external power, including fully charged while plugged in. Their
percentage is frozen and must not be displayed. 1A explicitly means off;
silence is not evidence of power-off. Topology supplies the HID caps and
physical grouping; unknown lengths fail closed before opening for I/O.
"""
from __future__ import annotations

import math
import time

try:
    import hid
except ImportError:              # pragma: no cover
    hid = None

from topology import physical_groups
from . import hidlist
from .base import DeviceStatus, Provider, hexdump, log

VID = 0x291D
PIDS = {0x385D: "MCHOSE V9 PRO"}
USAGE_PAGE, USAGE = 0xFF90, 0x0001
REPORT_LEN = 64
READ_TIMEOUT_MS = 1000
MAX_READS = 32


def make_request():
    return [0x55, 0x65, 0x01] + [0] * 61


def parse_reply(reply):
    """Return (level, external power, online), or reject malformed evidence."""
    if len(reply) != REPORT_LEN or list(reply[:2]) != [0x55, 0x65]:
        return None
    level, state = reply[2:4]
    if state == 0x02 and 0 <= level <= 100:
        return level, False, True
    if state in (0x00, 0x03):
        return None, True, True
    if state == 0x1A:
        return None, False, False
    return None


class MchoseHeadsetProvider(Provider):
    name = "mchose_headset"

    def __init__(self):
        self._diag = []

    def _pick(self, infos):
        for info in infos:
            if (info.get("vendor_id") != VID or info.get("product_id") not in PIDS
                    or (info.get("usage_page"), info.get("usage")) != (USAGE_PAGE, USAGE)):
                continue
            lengths = (info.get("input_report_length"), info.get("output_report_length"))
            if lengths != (REPORT_LEN, REPORT_LEN):
                self._diag.append(f"  input/output lengths {lengths}; require 64/64: nothing sent")
                continue
            return info
        self._diag.append("  no eligible 291d:385d ff90:0001 collection: nothing sent")
        return None

    def _query(self, path):
        dev = hid.device()
        try:
            dev.open_path(path)
            if dev.write(make_request()) != REPORT_LEN:
                self._diag.append("  battery query write failed or short; no retry")
                return None
            deadline = time.monotonic() + READ_TIMEOUT_MS / 1000
            for _ in range(MAX_READS):
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                reply = dev.read(REPORT_LEN, max(1, math.ceil(remaining * 1000)))
                if not reply:
                    break
                if list(reply[:2]) != [0x55, 0x65]:
                    self._diag.append(f"  skipped non-battery frame: {hexdump(reply, 4)}")
                    continue
                if len(reply) != REPORT_LEN:
                    self._diag.append(f"  rejected battery frame length {len(reply)} (expected 64)")
                    continue
                parsed = parse_reply(reply)
                if parsed is None:
                    self._diag.append(f"  rejected battery state=0x{reply[3]:02x} level={reply[2]}")
                    continue
                self._diag.append(f"  battery reply: {hexdump(reply, 4)}")
                return parsed
            self._diag.append("  no valid battery reply within deadline/read limit; connection unknown")
        except (OSError, ValueError) as error:
            self._diag.append(f"  battery I/O: {error}")
        finally:
            try:
                dev.close()
            except Exception:
                pass
        return None

    def poll(self):
        self._diag = []
        if hid is None:
            return []
        try:
            infos = hidlist.enumerate(VID)
        except Exception as error:  # pragma: no cover
            log.warning("hid.enumerate(mchose_headset): %s", error)
            return []
        matched = [info for info in infos if info.get("vendor_id") == VID
                   and info.get("product_id") in PIDS]
        out = []
        for physical, group in physical_groups(matched).items():
            info = self._pick(group)
            if info is None:
                continue
            reading = self._query(info["path"])
            if reading is None:
                continue
            level, charging, online = reading
            out.append(DeviceStatus(
                f"mchose_headset:{physical}", PIDS[info["product_id"]],
                level, charging, online, self.name, kind="headset",
                connection_known=True, charging_known=online, physical_id=physical,
                level_unavailable=level is None))
        return out

    def diagnostics(self):
        return list(self._diag)
