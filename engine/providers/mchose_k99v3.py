"""MCHOSE K99 V3 2.4G GLW read-only configuration battery reader (3837:3033).

Official M HUB (cizhou CZ_SHARED_DATA) maps 3837:3033 "MCHOSE K99 V3 2.4G" on
MI_01 Generic Desktop 0001:0000 (65-byte unnumbered reports) to the GLW keyboard
protocol: read active profile via command 04 (offset 0, length 56), then read
the 64-byte active profile block via command 05 (56 + 8 bytes), where config[32]
carries percentage (low 7 bits, 0..100) and charging (bit 7). Command 03
(getInfo) returns static board/firmware metadata (e.g. 0x34 = 52) at offset 8,
not battery (#1050, #1220). No setBase or configuration writes are ever sent.
"""
import math
import time

from battery_hid import BatteryHidProvider, MAX_READS
from .mchose_glw_keyboard import (
    active_profile,
    make_request as make_glw_payload,
    parse_reply as parse_glw_payload,
)

VID = 0x3837
PRODUCT = "MCHOSE K99 V3 2.4G"
PIDS = {0x3033: "MCHOSE K99 V3 2.4G"}
_PNP = 'pnp:usb\\vid_3837&pid_3033\\'


def make_request(command=4, offset=0, length=56):
    return bytes([0] + make_glw_payload(command, offset, length))


def parse_reply(reply, command=4, offset=0, length=56):
    if (not isinstance(reply, (bytes, bytearray, list, tuple))
            or len(reply) != 64
            or any(type(b) is not int or not 0 <= b <= 255 for b in reply)):
        return None
    try:
        return parse_glw_payload(list(reply), command, offset, length)
    except ValueError:
        return None


class MchoseK99V3Provider(BatteryHidProvider):
    name = "mchose_k99v3"
    vid = VID
    pids = PIDS
    collection = (1, 0)
    input_id = output_id = 0
    input_bounds = (65, 65)
    output_bytes = 65
    kind = "keyboard"
    charging_known = True
    make_request = staticmethod(make_request)
    parse_reply = staticmethod(parse_reply)

    def _pick(self, infos):
        info = super()._pick(infos)
        if info is None:
            return None
        physical = info.get("physical_id")
        if (info.get("product_string") != PRODUCT or info.get("interface_number") != 1
                or not isinstance(physical, str) or not physical.lower().startswith(_PNP)
                or not physical[len(_PNP):] or "\\" in physical[len(_PNP):]):
            return None
        return info

    def _drain(self, dev, read_length):
        for _ in range(4):
            if not dev.read(read_length, 5):
                return

    def _read_block(self, dev, info, command, offset, length, deadline):
        request = make_request(command, offset, length)
        if dev.write(request) != len(request):
            self._diag.append("short/failed battery query write; no retry")
            return None
        read_length = info["input_report_length"] - (self.input_id == 0)
        for _ in range(MAX_READS):
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            raw = dev.read(read_length, max(1, math.ceil(remaining * 1000)))
            if not raw:
                break
            if len(raw) != read_length:
                self._diag.append("rejected input report length")
                continue
            parsed = parse_reply(raw, command, offset, length)
            if parsed is not None and len(parsed) == length:
                return parsed
            self._diag.append("rejected battery reply")
        self._diag.append("no valid reply within deadline/read limit; connection unknown")
        return None

    def _query(self, dev, info):
        read_length = info["input_report_length"] - (self.input_id == 0)
        self._drain(dev, read_length)
        deadline = time.monotonic() + (self.read_timeout_ms * 1.5) / 1000
        base = self._read_block(dev, info, 4, 0, 56, deadline)
        if base is None:
            return None
        try:
            profile = active_profile(base)
        except ValueError:
            self._diag.append("invalid GLW active profile; no automatic repair")
            return None
        if profile not in range(4):
            self._diag.append("invalid GLW active profile; no automatic repair")
            return None
        if sorted(base[2:6]) != [0, 1, 2, 3]:
            self._diag.append("GLW legacy direct profile index; format needs hardware validation")
        first = self._read_block(dev, info, 5, 64 * profile, 56, deadline)
        if first is None:
            return None
        tail = self._read_block(dev, info, 5, 64 * profile + 56, 8, deadline)
        if tail is None:
            return None
        config = list(first) + list(tail)
        if len(config) != 64:
            self._diag.append("rejected battery reply")
            return None
        level = config[32] & 0x7F
        if level > 100:
            self._diag.append("rejected GLW percentage outside 0..100")
            return None
        return level, bool(config[32] & 0x80), ""
