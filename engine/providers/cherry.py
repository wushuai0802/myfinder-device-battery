"""CHERRY 2.4 GHz keyboards behind their own receiver, without CHERRY Utility.

Protocol facts were reverse-engineered from CHERRY Utility and published by
zcmk123/cherry-battery-state (MIT), independently implemented here:

  * the vendor collection on interface 1 ("Col04") declares usage page 0xFF1C;
  * request: a 64-byte *output* report `04 20 00 1A 06` followed by zeroes
    (CHERRY Utility answers over output reports, not feature reports);
  * reply: a 64-byte input report whose byte 1 is 0x20; byte 8 is the level,
    accepted only in 0..100, and byte 9 is non-zero while charging;
  * the receiver also emits unrelated 0xAA heartbeat reports. They carry no
    level, so they are ignored rather than parsed as a percentage.

Only the exact receiver ids below are queried. An unknown CHERRY receiver is
never sent a vendor command.
"""
from __future__ import annotations

import time
from typing import List, Optional, Tuple

import hid

from . import hidlist
from .base import DeviceStatus, Provider, hexdump, log

CHERRY_VID = 0x046A

USAGE_PAGE = 0xFF1C             # the vendor collection CHERRY Utility talks to
REPORT_SIZE = 64

MSG_STATUS = 0x20               # byte 1 of a battery/status reply
LEVEL_OFFSET = 8
CHARGING_OFFSET = 9

REPLY_WINDOW = 1.2              # seconds to wait for the 0x20 reply
READ_TIMEOUT_MS = 250
FLUSH_TIMEOUT_MS = 20
FLUSH_READS = 4

PIDS = {
    0x01AC: "CHERRY MX 2.0S",
    0x01E0: "CHERRY MX 2.0S Wireless PRO",
}


def make_request() -> List[int]:
    return [0x04, MSG_STATUS, 0x00, 0x1A, 0x06] + [0x00] * (REPORT_SIZE - 5)


def parse_reply(reply) -> Optional[Tuple[int, bool]]:
    """(level, charging) from a status reply, or None for anything else."""
    if not reply or len(reply) <= CHARGING_OFFSET:
        return None
    if reply[1] != MSG_STATUS:
        return None
    level = reply[LEVEL_OFFSET]
    if not 0 <= level <= 100:
        return None
    return level, bool(reply[CHARGING_OFFSET])


class CherryProvider(Provider):
    name = "cherry"

    def __init__(self):
        self._diag: List[str] = []

    def _pick(self, infos: List[dict]) -> Optional[dict]:
        for d in infos:
            if d.get("usage_page") == USAGE_PAGE:
                return d
        self._diag.append(f"  no 0x{USAGE_PAGE:04X} vendor collection in {len(infos)} entries")
        return None

    def _query(self, path: bytes) -> Optional[Tuple[int, bool]]:
        dev = hid.device()
        try:
            dev.open_path(path)
        except (OSError, IOError) as e:
            self._diag.append("  open failed")
            return None
        try:
            for _ in range(FLUSH_READS):
                if not dev.read(REPORT_SIZE, FLUSH_TIMEOUT_MS):
                    break
            written = dev.write(make_request())
            if written != REPORT_SIZE:
                self._diag.append(f"  short write: {written}")
                return None
            deadline = time.monotonic() + REPLY_WINDOW
            while time.monotonic() < deadline:
                reply = dev.read(REPORT_SIZE, READ_TIMEOUT_MS)
                if not reply:
                    continue
                self._diag.append(f"  reply: {hexdump(reply)}")
                parsed = parse_reply(reply)
                if parsed is not None:
                    return parsed
            self._diag.append("  no 0x20 status reply (keyboard asleep, off or on its cable)")
            return None
        except (OSError, IOError, ValueError) as e:
            self._diag.append("  io error")
            return None
        finally:
            try:
                dev.close()
            except Exception:
                pass

    def poll(self) -> List[DeviceStatus]:
        self._diag = []
        try:
            infos = hidlist.enumerate(CHERRY_VID)
        except Exception as e:  # pragma: no cover
            log.warning("hid.enumerate(cherry): %s", e)
            return []
        out: List[DeviceStatus] = []
        seen = set()
        for pid in PIDS:
            mine = [d for d in infos if d.get("product_id") == pid and d.get("path") not in seen]
            v = self._pick(mine)
            if v is None:
                continue
            seen.add(v["path"])
            name = PIDS[pid]
            self._diag.append(f"[CHERRY] pid={pid:04x} '{name}' "
                              f"iface={v.get('interface_number')} "
                              f"{v.get('usage_page', 0):04x}:{v.get('usage', 0):04x}")
            parsed = self._query(v["path"])
            if parsed is None:
                continue
            level, charging = parsed
            self._diag.append(f"  -> {level}%{' charging' if charging else ''}")
            out.append(DeviceStatus(f"cherry:{pid:04x}", name, level, charging, True, "cherry",
                                    kind="keyboard"))
        return out

    def diagnostics(self) -> List[str]:
        return list(self._diag)
