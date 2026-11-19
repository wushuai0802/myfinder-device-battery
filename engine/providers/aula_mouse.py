"""Exact AULA V9Pro+ 2.4 GHz receiver battery query, mouse only.

Protocol from the vendor app's USB traffic (USBPcap) and this project's own
hardware. Collection FF1C:0092, report id 04, 64 bytes in both directions.

    request  04 00 00 1A 00 ...   command in [3], no checksum, no handshake
    reply    04 <echo of 1..7> <percentage in [8]> <charging in [9]> ...

The echo repeats the command bytes but not the parameter byte ([4] stays 00),
so only [3] may be compared; comparing [4] rejects every real reply.

320F is a shared vendor id (HECATE 706E, VGN 5088 and others use it), so the
exact product string, interface number and report sizes are required before
any write. Battery only: no configuration, RGB, DPI or firmware command.

[9] is the charging state: 1 while the charging cable is connected, 0 otherwise.
Measured 2026-11-18 over 27 samples - nine unplugged, nine charging, nine
unplugged again - where [9] read 0/1/0 for every sample of its phase and the
percentage rose from 80 to 82 and stayed there after unplugging. Only 0 and 1
are accepted, so an unknown value drops the reply instead of guessing.
"""
from battery_hid import BatteryHidProvider

VID = 0x320F
PIDS = {0x22BA: "AULA V9Pro+"}
PRODUCT = "AULA-V9Pro+"
INTERFACE = 1


def make_request():
    return [0x04, 0x00, 0x00, 0x1A, 0x00] + [0] * 59


def parse_reply(reply):
    # Do not coerce integers, strings, floats or bools into synthetic bytes.
    if (not isinstance(reply, (bytes, bytearray, list, tuple)) or len(reply) != 64
            or any(type(n) is not int or not 0 <= n <= 255 for n in reply)
            or list(reply[:4]) != [0x04, 0x00, 0x00, 0x1A]
            or list(reply) == make_request()
            or not 0 <= reply[8] <= 100
            or reply[9] not in (0, 1)):
        return None
    return reply[8], reply[9] == 1, ""


class AulaMouseProvider(BatteryHidProvider):
    name = "aula_mouse"
    vid, pids = VID, PIDS
    collection = (0xFF1C, 0x0092)
    input_id = output_id = 0x04
    input_bounds = (64, 64)
    output_bytes = 64
    kind = "mouse"
    charging_known = True
    make_request = staticmethod(make_request)
    parse_reply = staticmethod(parse_reply)

    def _pick(self, infos):
        # The collection must stay unique for the whole parent before the
        # identity gates; a second eligible collection is never discarded to
        # make one write possible.
        info = super()._pick(infos)
        if info is None:
            return None
        physical = info.get("physical_id")
        if not isinstance(physical, str) or not physical or physical.startswith("collection:"):
            self._diag.append("physical parent unresolved; uniqueness unknown: nothing sent")
        elif (type(info.get("interface_number")) is not int
              or info["interface_number"] != INTERFACE
              or info.get("product_string") != PRODUCT):
            self._diag.append("unverified AULA mouse interface/product: nothing sent")
        elif (info["input_report_sizes"] != {"4": 64}
              or info["output_report_sizes"] != {"4": 64}):
            self._diag.append("single report 04/64 not proven: nothing sent")
        else:
            return info
        return None
