"""Valkyrie VK mouse battery (374A, USB cable and 2.4G receiver), read-only.

Protocol facts come from static analysis of the official ValkyrieVK 1.0.4.5
installer (config.xml PID table and DeviceDriver.exe GetMouseData); no vendor
code was executed. Per proven physical parent exactly one Feature report 03 /
64 bytes on FF00:0001 or FF06:0001 is read: one SET_FEATURE, a 100 ms wait and
one GET_FEATURE, never retried. No configuration, pairing, reset or firmware
command exists in this reader.
"""
import re
import time

from battery_hid import BatteryHidProvider, report_proof_summary

VID = 0x374A
PIDS = {
    0xA301: "Valkyrie M1 PRO", 0xA302: "Valkyrie M1 PRO",
    0xA304: "Valkyrie M2", 0xA305: "Valkyrie M2",
    0xA307: "Valkyrie M2 Pro", 0xA308: "Valkyrie M2 Pro",
    0xA312: "Valkyrie M3", 0xA313: "Valkyrie M3",
    0xA315: "Valkyrie M3 PRO", 0xA316: "Valkyrie M3 PRO",
    0xA324: "Valkyrie M5 PRO", 0xA325: "Valkyrie M5 PRO", 0xA327: "Valkyrie M5 PRO",
}
COLLECTIONS = {(0xFF00, 0x0001), (0xFF06, 0x0001)}
REPORT_ID = 0x03
REPORT_BYTES = 64
_PARENT = re.compile(r"pnp:usb\\vid_374a&pid_([0-9a-f]{4})(?:&rev_[0-9a-f]{4})?\\[^\\\x00\s]+", re.I)


def make_request():
    # buf[1] = (0x50 + 0x00 + 0x02 + 0x4F + 0x81) & 0xFF over buf[2..63].
    return [0x03, 0x22, 0x50, 0x00, 0x02, 0x4F, 0x81] + [0] * 57


def parse_reply(reply):
    if not isinstance(reply, (bytes, bytearray, list, tuple)):
        return None
    frame = list(reply)
    if (len(frame) != REPORT_BYTES
            or any(type(value) is not int or not 0 <= value <= 255 for value in frame)):
        return None
    # Report ID, ACK (EC_SUCCESS == 0) and the command echo 02; a request echo is not telemetry.
    if frame[0] != REPORT_ID or frame[2] != 0 or frame[4] != 2 or frame == make_request():
        return None
    level, charging = frame[6] & 0x7F, bool(frame[6] & 0x80)
    if level > 100 or (level == 0 and not charging):
        return None  # 0% while discharging is the offline/sleep placeholder
    return level, charging, ""


def matches_collection(info):
    return (info.get("vendor_id") == VID and info.get("product_id") in PIDS
            and (info.get("usage_page"), info.get("usage")) in COLLECTIONS)


def _usb_parent(info):
    physical = info.get("physical_id")
    match = _PARENT.fullmatch(physical) if isinstance(physical, str) else None
    return match is not None and int(match[1], 16) == info.get("product_id")


class ValkyrieProvider(BatteryHidProvider):
    name = "valkyrie"
    vid, pids = VID, PIDS
    kind = "mouse"
    charging_known = True
    unconfirmed = True
    verification_level = "C"

    def poll(self):
        result = super().poll()
        if not result and not self._diag:
            self._diag.append("no eligible Valkyrie collection or HID backend; not opened; nothing sent")
        return result

    def _pick(self, infos):
        candidates = []
        for info in infos:
            if not matches_collection(info):
                continue
            maximum = info.get("feature_report_length")
            sizes = info.get("feature_report_sizes")
            if (type(maximum) is int and REPORT_BYTES <= maximum <= 4096
                    and isinstance(sizes, dict) and sizes
                    and all(type(n) is int and 2 <= n <= maximum for n in sizes.values())
                    and max(sizes.values()) == maximum
                    and sizes.get(str(REPORT_ID)) == REPORT_BYTES):
                candidates.append(info)
            else:
                self._diag.append(f"{info['usage_page']:04X}:0001 Feature 03 length unproven"
                                  + report_proof_summary(info, "feature") + "; not opened; nothing sent")
        if len(candidates) != 1:
            self._diag.append("missing or ambiguous proven Feature 03 collection; not opened; nothing sent")
            return None
        info = candidates[0]
        if not isinstance(info.get("path"), (bytes, str)) or not info["path"] or not _usb_parent(info):
            self._diag.append("collection path/USB physical parent unresolved; not opened; nothing sent")
            return None
        self._diag.append("selected unique Feature 03/64 collection"
                          + report_proof_summary(info, "feature"))
        return info

    def _query(self, dev, info):
        request = make_request()
        if dev.send_feature_report(request) != len(request):
            self._diag.append("short/failed battery read request; no retry")
            return None
        time.sleep(0.100)  # vendor driver's receive delay, without a retry policy
        reading = parse_reply(dev.get_feature_report(REPORT_ID, REPORT_BYTES))
        if reading is None:
            self._diag.append("no valid battery reply; no new reading; connection unknown")
        return reading
