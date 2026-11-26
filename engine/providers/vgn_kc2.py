"""VGN kc2 wireless mouse receivers from VGN HUB's vgn-kc2 (class mr) driver.

Maps the 9 documented 391D receivers exposing FFC1:0001 and numbered 64-byte
reports (output 0xB3, input 0xB4). Only the read-only getDeviceInfo() query
(0xB3 0x06 ...) is sent; level is decoded from input byte[20] & 0x7F.
"""
from __future__ import annotations

from battery_hid import BatteryHidProvider, report_proof_summary

VID = 0x391D
PIDS = {
    0x1A20: "VGN Dragonfly 3 Turbo",
    0x1A21: "VGN Dragonfly 3 Turbo+",
    0x1A22: "VGN Dragonfly F1 V5 Turbo",
    0x1A23: "VGN Dragonfly F1 V5 Turbo+",
    0x1A35: "VGN Dragonfly 3 MINI Turbo",
    0x1A36: "VGN Dragonfly 3 MINI Turbo+",
    0x1A3B: "VGN Dragonfly F1 V5 Air Turbo",
    0x1A3C: "VGN Dragonfly F1 V5 Air Turbo+",
    0x1A3E: "VGN Dragonfly 3 RT+",
}
COLLECTION = (0xFFC1, 0x0001)
INTERFACES = {0x1A3E: 2}
REPORT_ID_CMD = 0xB3
REPORT_ID_RESP = 0xB4
CMD_INFO = 0x06
REPORT_BYTES = 64
BATTERY_OFFSET = 20


def _expected_interface(product_id):
    return INTERFACES.get(product_id, 1)


def matches_collection(info):
    pid = info.get("product_id")
    return (
        info.get("vendor_id") == VID
        and pid in PIDS
        and (info.get("usage_page"), info.get("usage")) == COLLECTION
        and info.get("interface_number") == _expected_interface(pid)
    )


def _valid_info_frame(reply):
    return (
        isinstance(reply, (bytes, bytearray, list, tuple))
        and len(reply) == REPORT_BYTES
        and all(type(byte) is int and 0 <= byte <= 255 for byte in reply)
        and reply[0] == REPORT_ID_RESP
        and reply[1] == CMD_INFO
    )


def make_request():
    """64-byte Output Report 0xB3 getDeviceInfo() request."""
    return [REPORT_ID_CMD, CMD_INFO] + [0] * (REPORT_BYTES - 2)


def no_reading_reply(reply):
    """Treat battery field 0 (asleep/unready placeholder) as unavailable, not 0%."""
    return _valid_info_frame(reply) and (reply[BATTERY_OFFSET] & 0x7F) == 0


def parse_reply(reply):
    """Decode 1..100% battery from Input Report 0xB4 (command 0x06); charging unknown."""
    if not _valid_info_frame(reply):
        return None
    level = reply[BATTERY_OFFSET] & 0x7F
    if not 1 <= level <= 100:
        return None
    return level, False, ""


class VgnKc2Provider(BatteryHidProvider):
    name = "vgn_kc2"
    vid = VID
    pids = PIDS
    collection = COLLECTION
    input_id = REPORT_ID_RESP
    output_id = REPORT_ID_CMD
    input_bounds = (REPORT_BYTES, REPORT_BYTES)
    output_bytes = REPORT_BYTES
    kind = "mouse"
    charging_known = False
    verification_level = "C"
    make_request = staticmethod(make_request)
    parse_reply = staticmethod(parse_reply)
    no_reading_reply = staticmethod(no_reading_reply)

    def _pick(self, infos):
        candidates = [info for info in infos if matches_collection(info)]
        if len(candidates) != 1 or not candidates[0].get("path"):
            self._diag.append("missing or ambiguous eligible collection: nothing sent")
            return None
        info = candidates[0]
        physical = info.get("physical_id")
        if (
            not isinstance(physical, str)
            or not physical.strip()
            or physical.casefold().startswith("collection:")
        ):
            self._diag.append("physical parent unresolved; uniqueness unknown: nothing sent")
            return None
        # Validate report sizes before handing off to BatteryHidProvider._pick so
        # diagnostics match our fixed reason table consistently.
        for direction, report_id in (("input", self.input_id), ("output", self.output_id)):
            maximum = info.get(direction + "_report_length")
            sizes = info.get(direction + "_report_sizes")
            if (
                type(maximum) is not int
                or maximum != REPORT_BYTES
                or not isinstance(sizes, dict)
                or not sizes
                or any(not isinstance(k, str) or type(n) is not int or not 2 <= n <= maximum
                       for k, n in sizes.items())
                or sizes.get(str(report_id)) != REPORT_BYTES
            ):
                self._diag.append(
                    "report IDs/lengths unknown or incompatible: nothing sent"
                    + report_proof_summary(info, direction, include_declared=True)
                )
                return None
        return info
