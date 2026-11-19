"""Single-collection NUT75 battery GET using the documented 2.4G frame."""
from battery_hid import BatteryHidProvider

VID = 0x0C45
PIDS = {0xFEF9: "WEIKAV NUT75"}
PRODUCT = "NUT75 2.4G"
_PARENT = "pnp:usb\\vid_0c45&pid_fef9\\"


class Nut75Provider(BatteryHidProvider):
    name = "weikav_nut75"
    vid, pids = VID, PIDS
    collection = (0xFF60, 0x0061)
    input_id = output_id = 0
    input_bounds = (65, 65)
    output_bytes = 65
    kind = "keyboard"

    def _pick(self, infos):
        info = super()._pick(infos)
        if info is None:
            return None
        parent = info.get("physical_id") or ""
        if (info.get("product_string") != PRODUCT or not parent.lower().startswith(_PARENT)
                or not parent[len(_PARENT):] or "\\" in parent[len(_PARENT):]):
            return None
        return info

    @staticmethod
    def make_request():
        return bytes.fromhex("00 AA 10 30 00 00 00 01").ljust(65, b"\x00")

    @staticmethod
    def parse_reply(packet):
        if len(packet) == 64 and list(packet[:2]) == [0x55, 0x10] and 1 <= packet[25] <= 100:
            return packet[25], False, ""
        return None
