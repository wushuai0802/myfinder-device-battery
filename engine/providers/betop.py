"""BETOP (Beitong) 2.4G/wired gamepad battery candidate, read-only, grade C.

Protocol facts (BrimDeck Betop.cs, Apache-2.0, cross-checked with the MIT
oitsukiii/betop-battery and HaloBattery beitong.py readers and BETOP client
2.6.2 JoypadConnHelper): the vendor collection (usage 0003, page 00FF/FF00)
answers output report 02 / command 15 with report 02 carrying the level and a
state nibble. Newer firmware scrambles every byte after the report ID with a
fixed nibble bijection. Only that one query is ever sent; no mode, profile,
vibration, reset, pairing or firmware command exists in this module.
"""
from __future__ import annotations

from datetime import datetime, timezone
from dataclasses import replace
import math
import re
import time

import hid

from . import hidlist
from .base import DeviceStatus, Provider

BETOP_VIDS = (0x20BC, 0x20DD)
# Exact (VID, PID) rows; every ID here was published by the pinned sources. Names are
# generic unless the source states a model family; the USB product string is preferred.
PIDS = {
    (0x20BC, 0x5125): "BETOP Kunpeng 40",
    (0x20BC, 0x5126): "BETOP Kunpeng 20",
    (0x20BC, 0x5127): "BETOP Kunpeng 40",
    (0x20BC, 0x5128): "BETOP Kunpeng 20",
    (0x20BC, 0x512F): "BETOP gamepad",
    (0x20BC, 0x5130): "BETOP gamepad",
    (0x20BC, 0x5133): "BETOP gamepad",
    (0x20BC, 0x5134): "BETOP gamepad",
    (0x20BC, 0x5145): "BETOP Kunpeng 40",
    (0x20BC, 0x5146): "BETOP Kunpeng 40",
    (0x20BC, 0x5149): "BETOP gamepad",
    (0x20BC, 0x514A): "BETOP gamepad",
    (0x20BC, 0x5150): "BETOP gamepad",
    (0x20BC, 0x5151): "BETOP gamepad",
    (0x20BC, 0x5152): "BETOP gamepad",
    (0x20BC, 0x5153): "BETOP gamepad",
    (0x20BC, 0x5154): "BETOP Kunpeng 40",
    (0x20BC, 0x5155): "BETOP Kunpeng 40",
    (0x20BC, 0x5158): "BETOP gamepad",
    (0x20BC, 0x5159): "BETOP gamepad",
    (0x20BC, 0x515B): "BETOP Kunpeng 40",
    (0x20BC, 0x515C): "BETOP Kunpeng 40",
    (0x20BC, 0x515D): "BETOP Kunpeng 40",
    (0x20BC, 0x515E): "BETOP Kunpeng 40",
    (0x20BC, 0x515F): "BETOP gamepad",
    (0x20BC, 0x5160): "BETOP gamepad",
    (0x20BC, 0x5163): "BETOP Kunpeng 40",
    (0x20BC, 0x5164): "BETOP Kunpeng 40",
    (0x20BC, 0x5165): "BETOP Kunpeng 40",
    (0x20BC, 0x5166): "BETOP Kunpeng 40",
    (0x20BC, 0x5169): "BETOP Kunpeng 40",
    (0x20BC, 0x516A): "BETOP Kunpeng 40",
    (0x20BC, 0x516D): "BETOP gamepad",
    (0x20BC, 0x516E): "BETOP gamepad",
    (0x20BC, 0x516F): "BETOP gamepad",
    (0x20BC, 0x5170): "BETOP gamepad",
    (0x20DD, 0x5125): "BETOP Kunpeng 40",
    (0x20DD, 0x5126): "BETOP Kunpeng 20",
    (0x20DD, 0x5127): "BETOP Kunpeng 40",
    (0x20DD, 0x5128): "BETOP Kunpeng 20",
    (0x20DD, 0x512F): "BETOP gamepad",
    (0x20DD, 0x5130): "BETOP gamepad",
    (0x20DD, 0x5133): "BETOP gamepad",
    (0x20DD, 0x5134): "BETOP gamepad",
    (0x20DD, 0x5145): "BETOP Kunpeng 40",
    (0x20DD, 0x5146): "BETOP Kunpeng 40",
    (0x20DD, 0x5149): "BETOP gamepad",
    (0x20DD, 0x514A): "BETOP gamepad",
    (0x20DD, 0x5150): "BETOP gamepad",
    (0x20DD, 0x5151): "BETOP gamepad",
    (0x20DD, 0x5152): "BETOP gamepad",
    (0x20DD, 0x5153): "BETOP gamepad",
    (0x20DD, 0x5154): "BETOP Kunpeng 40",
    (0x20DD, 0x5155): "BETOP Kunpeng 40",
    (0x20DD, 0x5158): "BETOP gamepad",
    (0x20DD, 0x5159): "BETOP gamepad",
    (0x20DD, 0x515B): "BETOP Kunpeng 40",
    (0x20DD, 0x515C): "BETOP Kunpeng 40",
    (0x20DD, 0x515D): "BETOP Kunpeng 40",
    (0x20DD, 0x515E): "BETOP Kunpeng 40",
    (0x20DD, 0x515F): "BETOP gamepad",
    (0x20DD, 0x5160): "BETOP gamepad",
    (0x20DD, 0x5163): "BETOP Kunpeng 40",
    (0x20DD, 0x5164): "BETOP Kunpeng 40",
    (0x20DD, 0x5165): "BETOP Kunpeng 40",
    (0x20DD, 0x5166): "BETOP Kunpeng 40",
    (0x20DD, 0x5169): "BETOP Kunpeng 40",
    (0x20DD, 0x516A): "BETOP Kunpeng 40",
    (0x20DD, 0x516D): "BETOP gamepad",
    (0x20DD, 0x516E): "BETOP gamepad",
    (0x20DD, 0x516F): "BETOP gamepad",
    (0x20DD, 0x5170): "BETOP gamepad",

}
SCRAMBLED_ONLY = frozenset({
    0x5126, 0x5128, 0x5145, 0x5146, 0x5154, 0x5155, 0x515D, 0x515E, 0x5169, 0x516A,
})
BOTH_MODES = frozenset({0x5125, 0x5127})   # scrambled first, plain once if silent
PLAIN_ONLY = frozenset({
    0x5158, 0x5159, 0x516F, 0x5170, 0x515B, 0x515C, 0x5163, 0x5164, 0x5165, 0x5166,
    0x5133, 0x5134, 0x5149, 0x514A, 0x5150, 0x5151, 0x5152, 0x5153, 0x512F, 0x5130,
    0x515F, 0x5160, 0x516D, 0x516E,
})
COLLECTIONS = ((0x00FF, 0x0003), (0xFF00, 0x0003))   # Windows order / HID spec order
REPORT_ID = 0x02
COMMAND = 0x15
MIN_REPORT, MAX_REPORT = 5, 64
READ_TIMEOUT_MS = 800
MAX_READS = 16

# BETOP client 2.6.2: high nibble through this table, low nibble xor 8.
NIBBLES = (0x0, 0xA, 0x6, 0xF, 0x1, 0xC, 0x2, 0x9, 0xE, 0x3, 0x7, 0xD, 0x4, 0xB, 0x5, 0x8)


def scramble(value):
    return (NIBBLES[value >> 4] << 4) | ((value & 0x0F) ^ 0x08)


_UNSCRAMBLE = bytes(sorted(range(256), key=scramble))


def unscramble(value):
    return _UNSCRAMBLE[value]


_PARENT = re.compile(
    r"pnp:usb\\vid_(20bc|20dd)&pid_([0-9a-f]{4})(?:&rev_[0-9a-f]{4})?\\[^\\\x00\s]+",
    re.I,
)


def modes_for(product_id):
    if product_id in SCRAMBLED_ONLY:
        return ("scrambled",)
    if product_id in BOTH_MODES:
        return ("scrambled", "plain")
    if product_id in PLAIN_ONLY:
        return ("plain",)
    return ()


def _listed(info):
    return (info.get("vendor_id"), info.get("product_id")) in PIDS


def matches_collection(info):
    """Exact identity plus vendor collection; never usage 2/5/6 or unlisted IDs."""
    return (_listed(info)
            and (info.get("usage_page"), info.get("usage")) in COLLECTIONS)


def _report_size(info, direction):
    maximum = info.get(direction + "_report_length")
    sizes = info.get(direction + "_report_sizes")
    if (type(maximum) is not int or not isinstance(sizes, dict) or not sizes
            or any(type(key) is not str or not key.isdecimal() or not key.isascii()
                   or not 0 <= int(key) <= 255 or type(size) is not int
                   for key, size in sizes.items())):
        return None
    size = sizes.get(str(REPORT_ID))
    if size is None or size != maximum or not MIN_REPORT <= size <= MAX_REPORT:
        return None
    return size


def reports_proven(info):
    """(input, output) report-02 sizes proven by the descriptor, else None."""
    sizes = _report_size(info, "input"), _report_size(info, "output")
    return None if None in sizes else sizes


def make_request(mode, length):
    command = scramble(COMMAND) if mode == "scrambled" else COMMAND
    return bytes([REPORT_ID, command]) + bytes(length - 2)


def _frame(raw, length):
    return (isinstance(raw, (bytes, bytearray, list, tuple))
            and len(raw) == length
            and all(type(value) is int and 0 <= value <= 255 for value in raw)
            and raw[0] == REPORT_ID)


def parse_plain(raw, length):
    if (not _frame(raw, length) or raw[1] != COMMAND or raw[3] != 0
            or raw[4] & 0xF0 != 0x50 or not 1 <= raw[2] <= 100):
        return None
    return raw[2], raw[4] & 0x0F != 0


def parse_scrambled(raw, length):
    if (not _frame(raw, length) or unscramble(raw[1]) != COMMAND
            or unscramble(raw[3]) != 0):
        return None
    level = unscramble(raw[2])
    if not 1 <= level <= 100:
        return None
    return level, unscramble(raw[4]) & 0x0F != 0


PARSERS = {"scrambled": parse_scrambled, "plain": parse_plain}


def _device_name(info):
    text = info.get("product_string")
    if isinstance(text, str):
        text = text.strip()
        if text and len(text) <= 64 and text.isprintable():
            return text
    return PIDS[(info["vendor_id"], info["product_id"])]


class BetopProvider(Provider):
    name = "betop"
    kind = "gamepad"
    charging_known = True

    def __init__(self):
        self._diag = []
        self._last = {}   # physical USB parent -> last measured DeviceStatus

    @staticmethod
    def _historical(status):
        return replace(status, online=False, cached=True, wake_on_input=True,
                       connection_known=False, charging_known=False)

    def _exchange(self, dev, mode, in_len, out_len):
        """Returns (reading, fatal). One write per mode; never retried."""
        request = make_request(mode, out_len)
        written = dev.write(request)
        if type(written) is not int or written != len(request):
            self._diag.append("short/failed battery query write; no retry")
            return None, True
        parse = PARSERS[mode]
        deadline = time.monotonic() + READ_TIMEOUT_MS / 1000
        for _ in range(MAX_READS):
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            raw = dev.read(in_len, max(1, math.ceil(remaining * 1000)))
            if not raw:
                break
            reading = parse(raw, in_len)
            if reading is not None:
                return reading, False
        return None, False

    def _query(self, dev, info, sizes):
        in_len, out_len = sizes
        for mode in modes_for(info["product_id"]):
            reading, fatal = self._exchange(dev, mode, in_len, out_len)
            if reading is not None or fatal:
                return reading
        self._diag.append("no valid battery reply within bounds; connection unknown")
        return None

    def poll(self):
        self._diag = []
        if hid is None:
            return []
        inventory = []
        for vid in BETOP_VIDS:
            try:
                inventory.extend(hidlist.enumerate(vid))
            except OSError:
                # A failed enumeration cannot prove that a receiver left the USB tree.
                self._diag.append("enumeration failed; retaining history")
                return [self._historical(s) for s in self._last.values()]

        groups = {}
        present = set()
        unresolved = False
        for info in inventory:
            if not _listed(info):
                continue
            physical = info.get("physical_id")
            parent = _PARENT.fullmatch(physical) if isinstance(physical, str) else None
            if (parent is None or int(parent[1], 16) != info["vendor_id"]
                    or int(parent[2], 16) != info["product_id"]):
                if matches_collection(info):
                    unresolved = True
                continue
            present.add(physical.casefold())
            if matches_collection(info):
                # Count before path or descriptor-proof filtering.
                groups.setdefault(physical.casefold(), []).append(info)
        if unresolved:
            self._diag.append("target USB parent unresolved; uniqueness unknown: nothing sent")

        result, found = [], set()
        for physical, candidates in groups.items() if not unresolved else ():
            if len(candidates) != 1:
                self._diag.append("ambiguous target collection: nothing sent")
                continue
            info = candidates[0]
            path = info.get("path")
            if not isinstance(path, (str, bytes)) or not path:
                self._diag.append("target path unproven: nothing sent")
                continue
            sizes = reports_proven(info)
            if sizes is None:
                self._diag.append("numbered IN/OUT report 02 unproven: nothing sent")
                continue
            dev = None
            try:
                dev = hid.device()
                dev.open_path(path)
                reading = self._query(dev, info, sizes)
                if reading is None:
                    continue
                level, charging = reading
                received = (datetime.now(timezone.utc).isoformat(timespec="milliseconds")
                            .replace("+00:00", "Z"))
                status = DeviceStatus(
                    key=f"{self.name}:{physical}",
                    name=_device_name(info),
                    level=level,
                    charging=charging,
                    online=True,
                    source=self.name,
                    kind=self.kind,
                    physical_id=physical,
                    connection_known=True,
                    charging_known=True,
                    verification_level="C",
                    unconfirmed=True,
                    measured_at_utc=received,
                    queried_at_utc=received,
                )
                self._last[physical] = status
                found.add(physical)
                result.append(status)
            except (OSError, ValueError):
                self._diag.append("battery I/O failed; connection unknown")
            finally:
                if dev is not None:
                    try:
                        dev.close()
                    except OSError:
                        self._diag.append("HID handle close failed")

        # Silence or lost descriptor proof does not expire history; only the
        # receiver leaving the enumerated USB tree does (unresolved parents cannot prove it).
        for physical in list(self._last):
            if physical in found:
                continue
            if physical in present or unresolved:
                result.append(self._historical(self._last[physical]))
            else:
                del self._last[physical]
        return result

    def diagnostics(self):
        return list(self._diag)
