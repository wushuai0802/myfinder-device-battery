"""Lingbao (凌豹) M1Pro mice: one read-only Feature-0F battery query.

The wired identity 088D:052E and the wireless receiver 088D:052F expose the same
FF00:0001 collection whose only reports are Feature 0F, 33 bytes. The vendor
utility sends `0F 01 30 00 <page> 01` and reads the same report back; the reply
carries the model signature `M536` at bytes 7..10 and the percentage at byte 18.
See upstream-protocols.md for the capture. The wired identity is queried first so
a cabled mouse produces one card instead of a duplicate receiver card; the
receiver is only queried when the cable produced no reading. No configuration,
DPI, macro, pairing or firmware command is sent, and the reply's mode/charge
bytes have no proven meaning here, so charging stays unknown.
"""
import time

from battery_hid import BatteryHidProvider, report_proof_summary

VID = 0x088D
WIRED_PID = 0x052E
RECEIVER_PID = 0x052F
NAME = "凌豹 M1Pro 鼠标"
PIDS = {WIRED_PID: NAME, RECEIVER_PID: NAME}
COLLECTION = (0xFF00, 0x0001)
REPORT_ID = 0x0F
REPORT_BYTES = 33
MODEL = b"M536"             # Identify=4d,35,33,36 in the vendor device ini


def make_request():
    request = [0] * REPORT_BYTES
    request[0], request[1], request[2], request[4], request[5] = 0x0F, 0x01, 0x30, 0x01, 0x01
    return request


def parse_reply(reply):
    if (len(reply) != REPORT_BYTES or list(reply[:3]) != [REPORT_ID, 0x01, 0x30]
            or bytes(reply[7:11]) != MODEL):
        return None
    level = reply[18]
    if type(level) is not int or not 1 <= level <= 100:
        return None
    return level, False, ""


class LingbaoMouseProvider(BatteryHidProvider):
    name = "lingbao_mouse"
    vid, pids = VID, PIDS
    kind = "mouse"
    charging_known = False      # Mode/charge bytes are captured but unproven.
    make_request = staticmethod(make_request)
    parse_reply = staticmethod(parse_reply)

    def poll(self):
        result, diagnostics = [], []
        original = self.pids
        try:
            for pid in (WIRED_PID, RECEIVER_PID):
                self.pids = {pid: NAME}
                result = super().poll()
                if result:
                    # Report only the identity that answered: the absent cable
                    # is not a fault while the receiver supplies the reading.
                    diagnostics = list(self._diag)
                    break
                diagnostics.extend(self._diag)
        finally:
            self.pids = original
            self._diag = diagnostics
        return result

    def _pick(self, infos):
        # Groups of the other identity (cable while the receiver is queried and
        # vice versa) are expected neighbours, not diagnostics.
        if not any(info.get("vendor_id") == VID and info.get("product_id") in self.pids
                   for info in infos):
            return None
        candidates = []
        for info in infos:
            if (info.get("vendor_id") != VID or info.get("product_id") not in self.pids
                    or (info.get("usage_page"), info.get("usage")) != COLLECTION):
                continue
            maximum = info.get("feature_report_length")
            sizes = info.get("feature_report_sizes")
            if (type(maximum) is int and maximum == REPORT_BYTES
                    and isinstance(sizes, dict) and sizes.get(str(REPORT_ID)) == REPORT_BYTES
                    and all(type(n) is int and 2 <= n <= maximum for n in sizes.values())):
                candidates.append(info)
            else:
                self._diag.append("FF00:0001 Feature 0F length unproven"
                                  + report_proof_summary(info, "feature")
                                  + "; not opened; nothing sent")
        if len(candidates) != 1 or not candidates[0].get("path"):
            self._diag.append("missing or ambiguous proven Feature 0F collection; not opened; nothing sent")
            return None
        return candidates[0]

    def _query(self, dev, info):
        request = make_request()
        if dev.send_feature_report(request) != len(request):
            self._diag.append("short/failed battery query write; no retry")
            return None
        time.sleep(0.100)   # Vendor utility waits before reading the answer.
        reading = parse_reply(dev.get_feature_report(REPORT_ID, REPORT_BYTES))
        if reading is None:
            self._diag.append("no valid battery reply; no new reading; connection unknown")
        return reading
