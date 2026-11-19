"""Read one documented X87 device-info page and verify its model before use."""
from battery_hid import BatteryHidProvider

VID = 0x0C45
PIDS = {0xFEFE: "前行者 EWEADN X87"}
USB_PARENT = "pnp:usb\\vid_0c45&pid_fefe\\"
PRODUCT = "2.4G Dongle"
INFO_REQUEST = bytes.fromhex("00 AA 10 18 00 00 00 01 00")


def matches_collection(info):
    return (info.get("vendor_id") == VID and info.get("product_id") in PIDS
            and info.get("product_string") == PRODUCT and info.get("interface_number") == 3
            and (info.get("usage_page"), info.get("usage")) == (0xFF60, 0x0061))


class EweadnX87Provider(BatteryHidProvider):
    name = "eweadn_x87"
    vid, pids = VID, PIDS
    kind = "keyboard"

    def _pick(self, infos):
        targets = [d for d in infos if matches_collection(d)]
        if len(targets) != 1 or not targets[0].get("path"):
            return None
        info = targets[0]
        physical = info.get("physical_id") or ""
        length = info.get("output_report_length")
        if (not physical.lower().startswith(USB_PARENT) or not physical[len(USB_PARENT):]
                or "\\" in physical[len(USB_PARENT):] or length not in (33, 65)
                or info.get("input_report_length") != length
                or info.get("input_report_sizes") != {"0": length}
                or info.get("output_report_sizes") != {"0": length}):
            return None
        return info

    def _query(self, dev, info):
        size = info["output_report_length"]
        # The vendor-compatible ping reads the first 24 bytes in one exchange.
        # The same page contains the exact keyboard identity and the battery.
        if dev.write(INFO_REQUEST.ljust(size, b"\0")) != size:
            return None
        reply = dev.read(size - 1, 600)
        if (len(reply) != size - 1 or tuple(reply[:2]) != (0x55, 0x10)
                or tuple(reply[3:5]) != (0, 0)
                or tuple(reply[20:25]) != (2, 0, 5, 0, 2)):
            return None
        level = reply[25]
        return (level, False, "") if 1 <= level <= 100 else None
