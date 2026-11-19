"""M HUB MC battery GET for the V9 Pro 2 2.4 GHz headset. See upstream-protocols."""
from battery_hid import BatteryHidProvider

VID = 0x3837
PIDS = {0x6011: "MCHOSE V9 Pro 2"}
_PARENT = "pnp:usb\\vid_3837&pid_6011\\"


def make_request() -> bytes:
    return bytes((0x55, 0xAA, 0x01, 0x00, 0x01, 0x00, 0x01, 0x0B, 0x00)).ljust(64, b"\0")


def parse_reply(frame: bytes) -> tuple[int | None, bool, str] | None:
    if len(frame) != 64 or frame[0] != 0x55:
        return None

    payload = bytes(frame[1:])
    if payload[0] != 0xAA or payload[1] != 0x01:
        return None

    flags = payload[2]
    length = payload[3]
    checksum_index = 6 + length

    if flags not in (0, 1) or length < 3 or checksum_index >= len(payload):
        return None
    if payload[4:7] != b"\x00\x01\x0B":
        return None

    if flags == 0:
        checksum_valid = payload[checksum_index] == 0
    else:
        checksum = 0
        for value in payload[2:checksum_index]:
            checksum ^= value
        checksum_valid = payload[checksum_index] == checksum

    if not checksum_valid:
        return None

    body = payload[7:checksum_index]
    if body[0] == 0:
        return None, False, ""

    battery = body[1]
    level = battery & 0x7F
    if level > 100:
        return None

    return level, bool(battery & 0x80), ""


class MchoseV9Pro2Provider(BatteryHidProvider):
    name = "mchose_v9pro2"
    vid, pids = VID, PIDS
    collection = (0xFF07, 0x0222)
    input_id = output_id = 0x55
    input_bounds = (64, 64)
    output_bytes = 64
    kind = "headset"
    charging_known = True
    make_request = staticmethod(make_request)
    parse_reply = staticmethod(parse_reply)

    def _pick(self, infos):
        info = super()._pick(infos)
        if info is None:
            return None
        parent = info.get("physical_id") or ""
        if (info.get("interface_number") != 3 or (info.get("product_string") or "").casefold() != "mchose v9 pro 2"
                or not parent.lower().startswith(_PARENT)
                or not parent[len(_PARENT):] or "\\" in parent[len(_PARENT):]):
            return None
        return info

    def no_reading_reply(self, reply):
        result = parse_reply(reply)
        return result is not None and result[0] is None
