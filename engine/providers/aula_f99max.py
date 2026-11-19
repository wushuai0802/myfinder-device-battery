"""AULA F99 MAX 2.4 GHz receiver readonly battery exchange.

The vendor protocol uses two fixed report-0 output requests.  The receiver
returns two equally sized payloads; only the first packet's firmware identity
and battery fields are consumed, while the second packet proves the echoed
page/address terminator.  No other AULA command path is a fallback.
"""
from __future__ import annotations

import math
import time

from battery_hid import BatteryHidProvider, MAX_READS

VID = 0x05AC
PIDS = {0x024F: "AULA F99 MAX"}
PRODUCT = "F99 MAX Dongle"
COLLECTION = (0xFF60, 0x0061)
PACKET = 32
REQUEST_0 = bytes.fromhex(
    "00 AA 10 18 00 00 00 00 " + "00 " * 24)
REQUEST_18 = bytes.fromhex(
    "00 AA 10 18 18 00 00 01 " + "00 " * 24)


def parse_reply(first, second):
    """Return (percent, charging, approx) only for the complete exchange."""
    packets = (first, second)
    if any(not isinstance(packet, (bytes, bytearray, list, tuple))
           or len(packet) != PACKET
           or any(type(value) is not int or not 0 <= value <= 255 for value in packet)
           for packet in packets):
        return None
    if list(first[:4]) != [0x55, 0x10, 0x18, 0x00]:
        return None
    if list(second[:4]) != [0x55, 0x10, 0x18, 0x18]:
        return None
    if list(second[3:7]) != [0x18, 0x00, 0x00, 0x01]:
        return None
    if list(first[12:16]) != [0x45, 0x0C, 0x0A, 0x80]:
        return None
    level, charge = first[25], first[26]
    if not 1 <= level <= 100 or charge not in (0, 1):
        return None
    return level, bool(charge), ""


def valid_first_packet(packet):
    """Reject an obviously foreign first reply before the second write."""
    return (isinstance(packet, (bytes, bytearray, list, tuple))
            and len(packet) == PACKET
            and all(type(value) is int and 0 <= value <= 255 for value in packet)
            and list(packet[:4]) == [0x55, 0x10, 0x18, 0x00]
            and list(packet[12:16]) == [0x45, 0x0C, 0x0A, 0x80])


class AulaF99MaxProvider(BatteryHidProvider):
    # This isolated protocol branch is dispatched by AulaKeyboardProvider, so
    # it shares the existing public reader ownership and device-pack scope.
    name = "aula_keyboard"
    vid, pids = VID, PIDS
    collection = COLLECTION
    input_id = output_id = 0
    input_bounds = (33, 33)
    output_bytes = 33
    kind = "keyboard"
    charging_known = False
    make_request = staticmethod(lambda: list(REQUEST_0))
    parse_reply = staticmethod(parse_reply)

    def _pick(self, infos):
        info = super()._pick(infos)
        if info is None:
            return None
        physical = info.get("physical_id")
        if not isinstance(physical, str) or not physical or physical.startswith("collection:"):
            self._diag.append("physical parent unresolved; uniqueness unknown: nothing sent")
        elif info.get("product_string") != PRODUCT:
            self._diag.append("unverified AULA F99 product: nothing sent")
        else:
            return info
        return None

    def _read_packet(self, dev, info):
        length = info["input_report_length"] - 1
        deadline = time.monotonic() + self.read_timeout_ms / 1000
        for _ in range(MAX_READS):
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            raw = dev.read(length, max(1, math.ceil(remaining * 1000)))
            if not raw:
                break
            if len(raw) == length:
                return raw
            self._diag.append("rejected input report length")
        return None

    def _query(self, dev, info):
        # Each write is a distinct, fixed GET_DEVICE_INFO request.
        for request in (REQUEST_0, REQUEST_18):
            if dev.write(request) != len(request):
                self._diag.append("short/failed battery query write; no retry")
                return None
            packet = self._read_packet(dev, info)
            if packet is None:
                self._diag.append("no valid reply within deadline/read limit; connection unknown")
                return None
            if request is REQUEST_0:
                if not valid_first_packet(packet):
                    self._diag.append("rejected battery reply")
                    return None
                first = packet
            else:
                second = packet
        return parse_reply(first, second)
