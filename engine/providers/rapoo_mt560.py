"""Grade-C MT560 candidate: receive independently dispatched BB inputs only."""
import re

from providers.rapoo import RapooProvider

VID = 0x24AE
PIDS = {0x1872: "Rapoo MT560"}
COLLECTION = (0xFF00, 0x000E)
INPUT_ID = 0xBB


def parse_battery(raw):
    """Offsets include the report ID; charging is not established."""
    if (len(raw) < 7 or raw[0] != INPUT_ID or raw[1] != 0xB0
            or raw[5] != 1 or type(raw[6]) is not int
            or not 0 <= raw[6] <= 100):
        return None
    return raw[6], False, False


def _identity_consistent(value, prefix):
    """Accept the Windows topology builder's exact receiver identity."""
    if isinstance(value, bytes):
        try:
            value = value.decode("ascii")
        except UnicodeDecodeError:
            return False
    if not isinstance(value, str) or not value:
        return False
    value = value.lower()
    if not value.startswith(prefix) or len(value) <= len(prefix):
        return False
    for key, expected in (("vid", "24ae"), ("pid", "1872")):
        values = re.findall(key + r"_([0-9a-f]{4})(?![0-9a-z])", value)
        if (len(values) != value.count(key + "_")
                or any(item != expected for item in values)):
            return False
    return True


class RapooMT560Provider(RapooProvider):
    name = "rapoo_mt560"
    product_ids = PIDS
    collection = COLLECTION
    input_id = INPUT_ID
    minimum_input_length = 7
    parse_input = staticmethod(parse_battery)

    def _inventory_safe(self, inventory):
        # An unparented duplicate cannot safely be assigned to another receiver.
        # Keep the inventory for the inherited history/removal bookkeeping.
        paths = set()
        for info in inventory:
            if (info.get("vendor_id") != VID
                    or info.get("product_id") not in PIDS
                    or (info.get("usage_page"), info.get("usage")) != COLLECTION):
                continue
            physical = info.get("physical_id")
            path = info.get("path")
            if (not isinstance(physical, str) or not physical
                    or physical.startswith("collection:")
                    or not _identity_consistent(physical, "pnp:usb\\vid_24ae&pid_1872\\")
                    or not (_identity_consistent(path, "\\\\?\\hid#vid_24ae&pid_1872&")
                            or _identity_consistent(path, "\\\\?\\hid#vid_24ae&pid_1872#"))):
                self._diag.append("MT560 USB parent or path unproven; not opened")
                return False
            if path in paths:
                self._diag.append("ambiguous MT560 collection path; not opened")
                return False
            paths.add(path)
        return True
