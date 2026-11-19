"""Percentage-only K99 V3 query captured on its 3837:3033 USB receiver."""
from battery_hid import BatteryHidProvider

VID = 0x3837
PRODUCT = "MCHOSE K99 V3 2.4G"
PIDS = {0x3033: "MCHOSE K99 V3 2.4G"}
_PNP = 'pnp:usb\\vid_3837&pid_3033\\'


def make_request():
    return bytes.fromhex("00 55 03 00 38 38") + bytes(59)


def parse_reply(reply):
    if len(reply) != 64 or bytes(reply[:2]) != bytes.fromhex("aa 03"):
        return None
    percent = reply[8]
    return (percent, False, "") if type(percent) is int and 0 <= percent <= 100 else None


class MchoseK99V3Provider(BatteryHidProvider):
    name = "mchose_k99v3"
    vid = VID
    pids = PIDS
    collection = (1, 0)
    input_id = output_id = 0
    input_bounds = (65, 65)
    output_bytes = 65
    kind = "keyboard"
    make_request = staticmethod(make_request)
    parse_reply = staticmethod(parse_reply)

    def _pick(self, infos):
        info = super()._pick(infos)
        if info is None:
            return None
        physical = info.get("physical_id")
        if (info.get("product_string") != PRODUCT or info.get("interface_number") != 1
                or not isinstance(physical, str) or not physical.lower().startswith(_PNP)
                or not physical[len(_PNP):] or "\\" in physical[len(_PNP):]):
            return None
        return info
