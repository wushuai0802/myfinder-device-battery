"""Exact AULA F87ProV2 2.4 GHz receiver battery query, keyboard only.

Protocol from the vendor app's USB traffic (USBPcap) and this project's own
hardware. Collection FF60:0061, 32-byte reports without a report id, so the
Windows buffer is 33 bytes and hidapi returns 32.

    request  00 AA 10 18 00 ...   mark, command group, sub-command, page
    reply    55 10 18 00 <status page ... percentage in [25]>

The reply echoes [2..4] of the request into [1..3]. The vendor app also polls
page 18 with the same group and sub-command, and that reply differs only in
the echoed byte; a stale one would otherwise be read as this page's level, so
all three echoed bytes are compared.

0C45 and FEFE both appear on other brands' receivers, so the exact product
string, interface number and report sizes are required before any write.
Battery only: no configuration, RGB or key-mapping command, and no charging
field is claimed.
"""
from battery_hid import BatteryHidProvider, hidlist
from providers.aula_f99max import AulaF99MaxProvider

VID = 0x0C45
PIDS = {0xFEFE: "AULA F87ProV2"}
# Static offline ID index data for the isolated F99 MAX branch below.
F99_VID = 0x05AC
F99_PIDS = {0x024F: "AULA F99 MAX"}
PRODUCT = "AULA F87ProV2D Dongle"
INTERFACE = 3


def make_request():
    return [0x00, 0xAA, 0x10, 0x18, 0x00] + [0] * 28


def parse_reply(reply):
    # Do not coerce integers, strings, floats or bools into synthetic bytes.
    if (not isinstance(reply, (bytes, bytearray, list, tuple)) or len(reply) != 32
            or any(type(n) is not int or not 0 <= n <= 255 for n in reply)
            or list(reply[:4]) != [0x55, 0x10, 0x18, 0x00]
            or not 0 <= reply[25] <= 100):
        return None
    return reply[25], False, ""


class AulaKeyboardProvider(BatteryHidProvider):
    name = "aula_keyboard"
    vid, pids = VID, PIDS
    collection = (0xFF60, 0x0061)
    input_id = output_id = 0
    input_bounds = (33, 33)
    output_bytes = 33
    kind = "keyboard"
    make_request = staticmethod(make_request)
    parse_reply = staticmethod(parse_reply)

    def poll(self):
        """Run the F87 reader and the isolated F99 branch under one owner."""
        result = super().poll()
        try:
            f99_inventory = hidlist.enumerate(F99_VID)
        except OSError:
            return result
        # Avoid emitting an F99-specific diagnostic during ordinary F87 polls.
        # The F99 branch performs its own complete enumeration/proof before I/O.
        if not any(info.get("vendor_id") == F99_VID for info in f99_inventory):
            return result
        f99 = AulaF99MaxProvider()
        result.extend(f99.poll())
        self._diag.extend(f99.diagnostics())
        return result

    def _pick(self, infos):
        info = super()._pick(infos)
        if info is None:
            return None
        physical = info.get("physical_id")
        if not isinstance(physical, str) or not physical or physical.startswith("collection:"):
            self._diag.append("physical parent unresolved; uniqueness unknown: nothing sent")
        elif (type(info.get("interface_number")) is not int
              or info["interface_number"] != INTERFACE
              or info.get("product_string") != PRODUCT):
            self._diag.append("unverified AULA keyboard interface/product: nothing sent")
        elif (info["input_report_sizes"] != {"0": 33}
              or info["output_report_sizes"] != {"0": 33}):
            self._diag.append("single unnumbered report 0/33 not proven: nothing sent")
        else:
            return info
        return None

    def _query(self, dev, info):
        """Repeat once when the query is ignored.

        Bring-up observation: an idle keyboard sometimes accepts the output
        report but never answers, and the immediately repeated query returns
        the status page. Only a silent attempt is repeated - a rejected reply
        keeps its diagnostic and waits for the next poll, so this can cost one
        extra 33-byte write and never an extra read loop.
        """
        before = len(self._diag)
        parsed = super()._query(dev, info)
        if parsed is not None:
            return parsed
        if (self._diag[before:]
                != ["no valid reply within deadline/read limit; connection unknown"]):
            return None
        self._diag.pop()
        return super()._query(dev, info)
