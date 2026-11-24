"""ATK / VGN / VXE wired EVISION keyboards on 320F:5055, read-only battery query.

Static facts from the official ATK HUB bundle (chunks/keyboard-info.util and
base-hid-device): DEVICE_PATHS["0x320f"]["0x5055"] lists the keyboard models
below, the HID class sends Report ID 4 with a 16-bit request code 0x0001 (LE at
bytes 1..2) and the power-info command 0x1A at byte 3, zero padded to 64 bytes,
and createBattery() reads percentage and a charging state of 2. Nothing was
executed; no vendor code is copied.

320F:5055 is shared with models that are NOT in that table (Rainy75, WOB), so
the whitespace-stripped, lower-cased product string must be an exact member of
the table. A missing, empty or unknown product string never opens a handle.
Battery only: no configuration, RGB, pairing or firmware command. Grade C.
Evidence and limits: engine/upstream-protocols.md.
"""
import re

from battery_hid import BatteryHidProvider

VID = 0x320F
PIDS = {0x5055: "VGN / VXE Wired Keyboard"}
COLLECTION = (0xFF1C, 0x0092)
REPORT_ID = 0x04
REPORT_BYTES = 64
CHARGING = 2
# ATK HUB DEVICE_PATHS["0x320f"]["0x5055"] keys, already whitespace-free lower case.
PRODUCTS = frozenset({
    "atkv75x", "vxev75x", "vgnvxe75v2", "vgnvxe75", "vxev87", "vxev87rgb",
    "kokomikeyboard108", "acheronkeyboard108", "acheronkeyboard87",
    "shenhekeyboard108", "kafka87", "kafka108", "vgnv98prov2", "vgnv98pro",
    "vxev75pro", "vgns99", "s99", "vgnn75pro",
})
_PARENT = re.compile(r"pnp:usb\\vid_320f&pid_5055(?:&rev_[0-9a-f]{4})?\\[^\\\x00\s]+", re.I)


def normalize_product(value):
    """ATK HUB key form of a USB product string, or None for non-text."""
    if not isinstance(value, str):
        return None
    return "".join(value.split()).lower()


def product_allowed(value):
    return normalize_product(value) in PRODUCTS


def make_request():
    return [REPORT_ID, 0x01, 0x00, 0x1A, 0x00] + [0] * 59


def _frame(reply):
    # Do not coerce integers, strings, floats or bools into synthetic bytes.
    return (isinstance(reply, (bytes, bytearray, list, tuple)) and len(reply) == REPORT_BYTES
            and all(type(n) is int and 0 <= n <= 255 for n in reply))


def no_reading(reply):
    """Sleeping/offline reply: command byte FF, otherwise the request header."""
    return _frame(reply) and list(reply[:4]) == [REPORT_ID, 0x01, 0x00, 0xFF]


def parse_reply(reply):
    if (not _frame(reply) or list(reply[:3]) != [REPORT_ID, 0x01, 0x00]
            or list(reply) == make_request() or reply[3] != 0x1A
            or not 0 <= reply[8] <= 100):
        return None
    charging = reply[9] == CHARGING
    if reply[8] == 0 and not charging:
        return None  # an uninitialised all-zero frame is never shown as 0%
    return reply[8], charging, ""


class VgnKeyboardWiredProvider(BatteryHidProvider):
    name = "vgn_keyboard_wired"
    vid, pids = VID, PIDS
    collection = COLLECTION
    input_id = output_id = REPORT_ID
    input_bounds = (REPORT_BYTES, REPORT_BYTES)
    output_bytes = REPORT_BYTES
    kind = "keyboard"
    verification_level = "C"
    unconfirmed = True
    charging_known = True
    make_request = staticmethod(make_request)
    parse_reply = staticmethod(parse_reply)

    def _pick(self, infos):
        # Count the whole parent's eligible collections before the identity
        # gates: a second collection is never discarded to make a write possible.
        info = super()._pick(infos)
        if info is None:
            return None
        physical = info.get("physical_id")
        if (not isinstance(physical, str) or _PARENT.fullmatch(physical) is None
                or not isinstance(info.get("path"), (bytes, str)) or not info["path"]):
            self._diag.append("physical parent unresolved; uniqueness unknown: nothing sent")
        elif not product_allowed(info.get("product_string")):
            self._diag.append("unlisted keyboard product name: nothing sent")
        elif (info["input_report_sizes"] != {"4": REPORT_BYTES}
              or info["output_report_sizes"] != {"4": REPORT_BYTES}):
            self._diag.append("single report 04/64 not proven: nothing sent")
        else:
            return info
        return None

    def device_name(self, info):
        return str(info.get("product_string") or "").strip() or super().device_name(info)

    def no_reading_reply(self, reply):
        return no_reading(reply)
