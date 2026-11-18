"""Dareu EM901 and CM610 Pro battery-only CompX routes: Feature 08 -> Input 09.

The official driver does not validate a reply checksum. Descriptor proof,
report ID, command, status and percentage guard replies here; see
upstream-protocols.md. No discovery handshake or settings commands are sent.
"""
import math
import re
import time

import battery_hid
from battery_hid import BatteryHidProvider, MAX_READS
from .pulsar import make_request

VID = 0x260D
PIDS = {0x1074: "Dareu EM901"}
CM610_STRINGS = ("Compx", "2.4G Wireless Receiver")
CM610_PARENT = re.compile(r"pnp:usb\\vid_260d&pid_1074(?:&rev_[0-9a-f]{4})?\\[^\\\s]+", re.I)


def cm610_identity(info):
    return (info.get("manufacturer_string"), info.get("product_string")) == CM610_STRINGS


def reported_sizes(info, direction):
    maximum = info.get(direction + "_report_length")
    sizes = info.get(direction + "_report_sizes")
    if (type(maximum) is int and 0 <= maximum <= 4096 and isinstance(sizes, dict)
            and all(type(n) is int and 2 <= n <= maximum for n in sizes.values())
            and max(sizes.values(), default=0) == maximum):
        return sizes
    return None


def parse_reply(reply, report_length=17):
    if (not reply or len(reply) != report_length or len(reply) < 8
            or reply[0] != 0x09 or reply[1] != 0x04 or reply[2] != 0
            or not 0 <= reply[6] <= 100):
        return None
    return reply[6], reply[7] != 0, ""


class DareuProvider(BatteryHidProvider):
    name = "dareu"
    vid, pids = VID, PIDS
    collection = (0xFF02, 0x0002)
    kind = "mouse"
    charging_known = True

    def device_name(self, info):
        return "Dareu CM610 Pro" if "rx_path" in info else super().device_name(info)

    def _pick(self, infos):
        candidates = [info for info in infos
                      if info.get("vendor_id") == VID and info.get("product_id") in PIDS
                      and (info.get("usage_page"), info.get("usage")) == self.collection]
        if len(candidates) != 1 or not candidates[0].get("path"):
            self._diag.append("missing or ambiguous eligible collection: nothing sent")
            return None
        info = candidates[0]
        feature = reported_sizes(info, "feature")
        inputs = reported_sizes(info, "input")
        if (not feature or feature.get("8") != 17 or info["feature_report_length"] != 17
                or inputs is None):
            self._diag.append("feature/input IDs or lengths unproven: nothing sent")
            return None
        if "9" in inputs:
            if inputs["9"] >= 8:
                return info
            self._diag.append("input 09 too short: nothing sent")
            return None
        parent = info.get("physical_id") or ""
        siblings = [rx for rx in infos if (rx.get("usage_page"), rx.get("usage")) == (0xFF01, 0)]
        # Only a complete per-ID map can rule out 09 on the sending collection.
        canonical_ids = all(isinstance(key, str) and re.fullmatch(r"0|[1-9][0-9]{0,2}", key)
                            and int(key) <= 255 for key in inputs)
        if (not canonical_ids or ("0" in inputs and len(inputs) != 1)
                or not cm610_identity(info) or not CM610_PARENT.fullmatch(parent) or len(siblings) != 1):
            self._diag.append("split receiver identity or collection unproven: nothing sent")
            return None
        rx = siblings[0]
        if (rx.get("vendor_id") != VID or rx.get("product_id") != info["product_id"]
                or rx.get("physical_id") != parent or not cm610_identity(rx)
                or not rx.get("path") or rx["path"] == info["path"]
                or rx.get("input_report_length") != 17
                or (reported_sizes(rx, "input") or {}).get("9") != 17):
            self._diag.append("split input 09 unproven: nothing sent")
            return None
        return dict(info, rx_path=rx["path"])

    def _query(self, dev, info):
        if "rx_path" not in info:
            return self._read_battery(dev, dev, info["input_report_length"], info["input_report_sizes"]["9"], False)
        rx = battery_hid.hid.device()
        try:
            rx.open_path(info["rx_path"])
            for _ in range(MAX_READS):
                if not rx.read(17, 1):
                    break
            else:
                self._diag.append("receive queue still busy: nothing sent")
                return None
            return self._read_battery(dev, rx, 17, 17, True)
        finally:
            try:
                rx.close()
            except OSError:
                self._diag.append("HID handle close failed")

    def _read_battery(self, dev, rx, maximum, reply_length, check_sum):
        # Reuse only the fixed CompX power frame/checksum, never its transport
        # or DPI/identity paths. A failed send is not retried.
        request = make_request()
        if dev.send_feature_report(request) != len(request):
            self._diag.append("short/failed battery feature request; no retry")
            return None
        deadline = time.monotonic() + self.read_timeout_ms / 1000
        for _ in range(MAX_READS):
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            raw = rx.read(maximum, max(1, math.ceil(remaining * 1000)))
            if not raw:
                break
            parsed = parse_reply(raw, reply_length)
            if parsed is not None and (not check_sum or sum(raw) & 255 == 0x55):
                return parsed
        # Silence/sleep and invalid replies produce no new reading, not an error.
        self._diag.append("no battery reply; connection unknown")
        return None
