"""Xiaomi Mi Mouse 4 Pro 2.4G receiver (2717:50FC, MI_02, FF03:0000).

Grade-C read-only battery candidate ported from PR #91 (isDayu), based on
Xiaomi's official web driver (`web.mieco.net/yuemi--manual/mouse/h/p/`,
route M001):

    host   00 16 00 ...             32-byte unnumbered output (33 with ID 0)
    mouse  00 16 02 01 <level> ...  32-byte unnumbered input (hidapi omits ID 0)

Only exact 2717:50FC on MI_02 / FF03:0000 with a resolved matching USB parent,
unique eligible collection and runtime descriptor proof of single unnumbered
33-byte input/output reports is opened. Level 0 indicates mouse off/asleep and
yields no new reading. Charging state is not reported by this protocol.
"""
from __future__ import annotations

import re

from battery_hid import BatteryHidProvider

VID = 0x2717
PIDS = {0x50FC: "Xiaomi Mi Mouse 4 Pro"}
COLLECTION = (0xFF03, 0x0000)
INTERFACE_NUMBER = 2
CMD_BATTERY = 0x16
REPORT_LEN = 33        # 32-byte unnumbered payload + Windows report-ID 0 slot
PAYLOAD_LEN = 32
QUERY = bytes([0x00, 0x00, CMD_BATTERY]) + bytes(REPORT_LEN - 3)
QUERY_PAYLOAD = QUERY[1:]


def _valid_parent(physical):
    if not isinstance(physical, str) or not physical or physical.startswith("collection:"):
        return False
    return re.fullmatch(
        r"pnp:usb\\vid_2717&pid_50fc(?:&rev_[0-9a-f]{4})?\\[^\\\s]+",
        physical,
        re.I,
    ) is not None


def _valid_path(path):
    if isinstance(path, bytes):
        try:
            text = path.decode("utf-8")
        except UnicodeDecodeError:
            return False
    elif isinstance(path, str):
        text = path
    else:
        return False
    return bool(text) and re.match(r"\\\\\?\\hid#vid_2717&pid_50fc(?:&|#)", text, re.I) is not None


def matches_collection(info):
    return (info.get("vendor_id") == VID
            and info.get("product_id") in PIDS
            and info.get("interface_number") == INTERFACE_NUMBER
            and (info.get("usage_page"), info.get("usage")) == COLLECTION)


def _valid_raw_payload(raw):
    if not isinstance(raw, (bytes, bytearray, list, tuple)) or len(raw) != PAYLOAD_LEN:
        return False
    if not isinstance(raw, (bytes, bytearray)):
        if any(type(b) is not int or not 0 <= b <= 255 for b in raw):
            return False
    return True


def parse_battery(raw):
    """Parse a 32-byte unnumbered input payload from Xiaomi Mi Mouse 4 Pro."""
    if not _valid_raw_payload(raw):
        return None
    if bytes(raw) == QUERY_PAYLOAD:
        return None
    if raw[0] != 0x00 or raw[1] != CMD_BATTERY or raw[2] != 0x02 or raw[3] != 0x01:
        return None
    level = raw[4]
    return level if 1 <= level <= 100 else None


class XiaomiMouseProvider(BatteryHidProvider):
    name = "xiaomi_mouse"
    vid = VID
    pids = PIDS
    kind = "mouse"
    collection = COLLECTION
    input_id = 0
    output_id = 0
    input_bounds = (REPORT_LEN, REPORT_LEN)
    output_bytes = REPORT_LEN
    charging_known = False
    unconfirmed = True
    verification_level = "C"

    def make_request(self):
        return QUERY

    def _pick(self, infos):
        info = super()._pick(infos)
        if info is None:
            return None
        if info.get("interface_number") != INTERFACE_NUMBER:
            self._diag.append("unexpected interface number; expected MI_02: nothing sent")
            return None
        if not _valid_parent(info.get("physical_id")) or not _valid_path(info.get("path")):
            self._diag.append("Xiaomi 4 Pro USB parent or path unproven: nothing sent")
            return None
        return info

    def no_reading_reply(self, reply):
        return (_valid_raw_payload(reply)
                and bytes(reply) != QUERY_PAYLOAD
                and reply[0] == 0x00
                and reply[1] == CMD_BATTERY
                and reply[2] == 0x02
                and reply[4] == 0)

    def parse_reply(self, reply):
        level = parse_battery(reply)
        return (level, False, "") if level is not None else None
