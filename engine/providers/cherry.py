"""CHERRY MX 2.0S WL Pro battery query, exact USB/2.4G identities.

Protocol reference: zcmk123/cherry-battery-state, commit af82d4d98cbad872a586302ba44e6a6b75ba65f4.
Local cable-state comparison: byte 9 is 0 unplugged, 1 charging.
Zero, full-charge and sleep semantics remain unverified. No settings operations.
"""
from battery_hid import BatteryHidProvider

VID = 0x046A
PIDS = {0x01E0: "CHERRY MX 2.0S WL Pro", 0x01DE: "CHERRY MX 2.0S WL Pro"}
PRODUCTS = {0x01E0: "MX 2.0S Pro Dongle", 0x01DE: "MX 2.0S WL Pro"}


def make_request():
    return [4, 0x20, 0, 0x1A, 6] + [0] * 59


def parse_reply(reply):
    if (len(reply) != 64 or list(reply[:5]) != [4, 0x20, 0, 0x1A, 6]
            or reply[7] != 0 or not 1 <= reply[8] <= 100
            or reply[9] not in (0, 1)):
        return None
    return reply[8], reply[9] == 1, ""


class CherryProvider(BatteryHidProvider):
    name = "cherry"
    vid, pids = VID, PIDS
    collection = (0xFF1C, 0x0092)
    input_id = output_id = 4
    input_bounds = (64, 64)
    output_bytes = 64
    kind = "keyboard"
    charging_known = True
    make_request = staticmethod(make_request)
    parse_reply = staticmethod(parse_reply)

    def _pick(self, infos):
        info = super()._pick(infos)
        if info is not None:
            if (info.get("interface_number") == 1
                    and info.get("product_string") == PRODUCTS[info["product_id"]]
                    and info.get("input_report_sizes") == {"4": 64}
                    and info.get("output_report_sizes") == {"4": 64}):
                return info
            self._diag.append("unverified CHERRY interface/product: nothing sent")
        return None
