"""AULA 2.4G keyboard receivers (05AC:024F): one read-only battery query.

Protocol facts from the public Aula-F75-Max-Driver project (`queryBattery`) and
re-verified on a local keyboard; see upstream-protocols.md. The receiver exposes
two collections on the same page/usage pair, 33 and 65 bytes. Only the 33-byte
one answers `20 01`; the 65-byte one speaks a different command set and is never
opened. No configuration, lighting, macro, reset, pairing or firmware command is
sent, and the wired Sonix identity (0C45:800A) stays disabled: no battery reply
was ever captured from it.
"""
from battery_hid import BatteryHidProvider

VID = 0x05AC
PIDS = {0x024F: "AULA 2.4G 键盘接收器（05AC:024F）"}
COLLECTION = (0xFF60, 0x0061)
REPORT_ID = 0x00
REPORT_BYTES = 33           # Windows length including the report-ID slot
PAYLOAD_BYTES = REPORT_BYTES - 1


def make_request():
    """Report ID 0, command 20 01, running-sum checksum in the last byte."""
    request = [0x00] * REPORT_BYTES
    request[1], request[2] = 0x20, 0x01
    request[-1] = sum(request[:-1]) & 0xFF
    return request


def parse_reply(reply):
    # hidapi returns the unnumbered payload without the report-ID slot.
    if len(reply) != PAYLOAD_BYTES or list(reply[:2]) != [0x20, 0x01]:
        return None
    level = reply[3]
    # A sleeping keyboard behind a live receiver can answer 0: never show 0 %.
    if type(level) is not int or not 1 <= level <= 100:
        return None
    return level, False, ""


class AulaKeyboardProvider(BatteryHidProvider):
    name = "aula_keyboard"
    vid, pids = VID, PIDS
    collection = COLLECTION
    input_id = output_id = REPORT_ID
    input_bounds = (REPORT_BYTES, REPORT_BYTES)
    output_bytes = REPORT_BYTES
    kind = "keyboard"
    charging_known = False      # No proven charging field in this reply.
    read_timeout_ms = 1200
    make_request = staticmethod(make_request)
    parse_reply = staticmethod(parse_reply)

    def _pick(self, infos):
        # The receiver also publishes a 65-byte collection on the same page and
        # usage. Requiring the proven 33-byte pair keeps it closed.
        exact = [info for info in infos
                 if info.get("vendor_id") == VID and info.get("product_id") in PIDS
                 and (info.get("usage_page"), info.get("usage")) == COLLECTION
                 and info.get("input_report_sizes") == {"0": REPORT_BYTES}
                 and info.get("output_report_sizes") == {"0": REPORT_BYTES}]
        if len(exact) != 1:
            self._diag.append("missing or ambiguous proven 33-byte raw collection; nothing sent")
            return None
        return super()._pick(exact)
