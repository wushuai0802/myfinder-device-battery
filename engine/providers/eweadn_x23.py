"""Listen to the X23 Pro receiver's sensor reports. The mouse is never queried."""
from battery_hid import BatteryHidProvider

VID = 0x04F3
PIDS = {0x026F: "EWEADN X23 Pro"}
PRODUCT = "X23 Pro Receiver"
USB_PARENT = "pnp:usb\\vid_04f3&pid_026f\\"
HID_PATH = "\\\\?\\hid#vid_04f3&pid_026f&mi_01&col04#"
WINDOWS_LENGTH = 32


def matches_collection(info):
    path = info.get("path")
    if isinstance(path, bytes):
        path = path.decode("utf-8", errors="replace")
    return (info.get("vendor_id") == VID and info.get("product_id") in PIDS
            and info.get("product_string") == PRODUCT and info.get("interface_number") == 1
            and (info.get("usage_page"), info.get("usage")) == (0x000A, 0)
            and isinstance(path, str) and path.lower().startswith(HID_PATH)
            and len(path) > len(HID_PATH))


def report_id(info):
    sizes = info.get("input_report_sizes")
    if (info.get("input_report_length") != WINDOWS_LENGTH
            or not isinstance(sizes, dict) or len(sizes) != 1):
        return None
    key, size = next(iter(sizes.items()))
    if (type(size) is not int or size != WINDOWS_LENGTH or not isinstance(key, str)
            or not 1 <= len(key) <= 3 or not key.isascii() or not key.isdecimal()):
        return None
    number = int(key)
    return number if number <= 255 and key == str(number) else None


class EweadnX23Provider(BatteryHidProvider):
    name = "eweadn_x23"
    vid, pids = VID, PIDS
    kind = "mouse"

    def _pick(self, infos):
        matches = [info for info in infos if matches_collection(info)]
        if len(matches) != 1:
            return None
        info = matches[0]
        physical = info.get("physical_id") or ""
        if (not physical.lower().startswith(USB_PARENT) or not physical[len(USB_PARENT):]
                or "\\" in physical[len(USB_PARENT):] or report_id(info) is None):
            return None
        return info

    def _query(self, dev, info):
        identifier = report_id(info)
        # Windows ReadFile includes an ID byte even on an unnumbered report.
        # hidapi removes that zero byte. The upstream battery field is byte 4.
        length = WINDOWS_LENGTH - (identifier == 0)
        raw = dev.read(length, 1200)
        if (len(raw) != length or (identifier and raw[0] != identifier)
                or not any(raw)):
            return None
        level = raw[4 if identifier else 3]
        return (level, False, "") if 0 <= level <= 100 else None
