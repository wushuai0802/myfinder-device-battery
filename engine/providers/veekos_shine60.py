"""Read the 2.4G Shine 60 receiver's existing status page. No device writes."""
from battery_hid import BatteryHidProvider

VID = 0x3151
PIDS = {0x5026: "Veekos Shine 60"}
PRODUCT = "Veekos 2.4G Wireless Keyboard"
_PARENT = "pnp:usb\\vid_3151&pid_5026\\"


class Shine60Provider(BatteryHidProvider):
    name = "veekos_shine60"
    vid, pids = VID, PIDS
    kind = "keyboard"

    def _pick(self, infos):
        candidates = [d for d in infos if d.get("vendor_id") == VID and d.get("product_id") in PIDS
                      and (d.get("usage_page"), d.get("usage")) == (0xFFFF, 2)]
        if len(candidates) != 1:
            return None
        info = candidates[0]
        parent = info.get("physical_id") or ""
        sizes = info.get("feature_report_sizes")
        if (info.get("product_string") != PRODUCT or info.get("interface_number") != 2
                or not info.get("path") or not parent.lower().startswith(_PARENT)
                or not parent[len(_PARENT):] or "\\" in parent[len(_PARENT):]
                or info.get("input_report_length") != 0 or info.get("output_report_length") != 0
                or info.get("feature_report_length") != 65 or not isinstance(sizes, dict)
                or not (sizes == {"0": 65} or ("0" not in sizes and sizes.get("131") == 65))):
            return None
        return info

    def _query(self, dev, info):
        # The captured state page echoes the requested ID, including zero on an
        # unnumbered collection. Use only the ID proven by this descriptor.
        report_id = 0 if "0" in info["feature_report_sizes"] else 0x83
        reply = dev.get_feature_report(report_id, 65)
        if (len(reply) == 65 and list(reply[:2]) == [report_id, 0] and reply[3] == 100
                and 0 <= reply[2] <= 100):
            return reply[2], False, ""
        return None
