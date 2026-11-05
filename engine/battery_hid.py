"""Bounded output/input battery queries for exact supported identities.

Lengths include the Windows report-ID slot. Unnumbered hidapi input omits it.
Only raw byte-array caps are accepted: collection maxima alone cannot prove
the size of an individual report (APEX5 also has a shorter output report 08).
No descriptor discovery here sends device commands or fetches features.
"""
from __future__ import annotations

import ctypes as c
import math
import time

try:
    import hid
except ImportError:  # pragma: no cover
    hid = None

from providers import hidlist
from providers.base import DeviceStatus, Provider
from standardhid import Values

READ_TIMEOUT_MS = 1000
MAX_READS = 32


def byte_report_sizes(api, data, caps, report_type):
    """Prove sizes of simple, non-aliased byte arrays, or fail closed.

One value-cap per ID supplies the entire payload. A collection maximum must
agree with the largest array; buttons, split fields and padding are not guessed.
String keys survive the reader child's JSON boundary without conversion.
"""
    buttons, count, maximum = ((caps.ib, caps.iv, caps.input) if report_type == 0
                               else (caps.ob, caps.ov, caps.output))
    if buttons or not 1 <= count <= 32 or not 2 <= maximum <= 4096:
        return None
    fn = api.HidP_GetValueCaps
    fn.argtypes = [c.c_int, c.POINTER(Values), c.POINTER(c.c_ushort), c.c_void_p]
    fn.restype = c.c_int32
    fields, actual = (Values * count)(), c.c_ushort(count)
    if fn(report_type, fields, c.byref(actual), data) != 0x00110000 or actual.value != count:
        return None
    sizes = {}
    for field in fields:
        key = str(field.report)
        if field.alias or field.bits != 8 or not field.count or key in sizes:
            return None
        sizes[key] = field.count + 1
    return sizes if max(sizes.values()) == maximum else None


def battery_caps(api, data, caps):
    return {"input_report_sizes": byte_report_sizes(api, data, caps, 0),
            "output_report_sizes": byte_report_sizes(api, data, caps, 1)}


class BatteryHidProvider(Provider):
    """Each subclass supplies one exact identity and one fixed read request."""
    charging_known = False
    verification_level = "C"

    def __init__(self):
        self._diag = []

    def device_name(self, info):
        return self.pids[info["product_id"]]

    def no_reading_reply(self, reply):
        return False

    def _pick(self, infos):
        candidates = [info for info in infos
                      if info.get("vendor_id") == self.vid
                      and info.get("product_id") in self.pids
                      and (info.get("usage_page"), info.get("usage")) == self.collection]
        if len(candidates) != 1 or not candidates[0].get("path"):
            self._diag.append("missing or ambiguous eligible collection: nothing sent")
            return None
        info = candidates[0]
        for direction, report_id, bounds in (
                ("input", self.input_id, self.input_bounds),
                ("output", self.output_id, (self.output_bytes, self.output_bytes))):
            maximum = info.get(direction + "_report_length")
            sizes = info.get(direction + "_report_sizes")
            if (type(maximum) is not int or not bounds[0] <= maximum <= bounds[1]
                    or not isinstance(sizes, dict) or not sizes
                    or any(type(n) is not int or not 2 <= n <= maximum for n in sizes.values())
                    or sizes.get(str(report_id)) != maximum
                    or report_id == 0 and set(sizes) != {"0"}):
                self._diag.append("report IDs/lengths unknown or incompatible: nothing sent")
                return None
        return info

    def _query(self, dev, info):
        request = self.make_request()
        if dev.write(request) != len(request):
            self._diag.append("short/failed battery query write; no retry")
            return None
        length = info["input_report_length"] - (self.input_id == 0)
        deadline = time.monotonic() + READ_TIMEOUT_MS / 1000
        for _ in range(MAX_READS):
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            raw = dev.read(length, max(1, math.ceil(remaining * 1000)))
            if not raw:
                break
            if len(raw) != length:
                self._diag.append("rejected input report length")
                continue
            if self.no_reading_reply(raw):
                self._diag.append("device asleep/offline; no new reading; connection unknown")
                return None
            parsed = self.parse_reply(raw)
            if parsed is not None:
                return parsed
            self._diag.append("rejected battery reply")
        self._diag.append("no valid reply within deadline/read limit; connection unknown")
        return None

    def poll(self):
        self._diag = []
        if hid is None:
            return []
        try:
            inventory = hidlist.enumerate(self.vid)
        except OSError:
            self._diag.append("enumeration failed: nothing sent")
            return []
        groups = {}
        for info in inventory:
            physical = info.get("physical_id", "")
            if physical.startswith("collection:"):
                self._diag.append("physical parent unresolved; uniqueness unknown: nothing sent")
                continue
            groups.setdefault(physical, []).append(info)
        result = []
        for physical, infos in groups.items():
            info = self._pick(infos)
            if info is None:
                continue
            dev = hid.device()
            try:
                dev.open_path(info["path"])
                reading = self._query(dev, info)
                if reading is not None:
                    level, charging, approx = reading
                    result.append(DeviceStatus(
                        f"{self.name}:{physical or repr(info['path'])}",
                        self.device_name(info), level, charging, True, self.name,
                        approx=approx, kind=self.kind, physical_id=physical,
                        connection_known=True, charging_known=self.charging_known,
                        verification_level=self.verification_level, level_unavailable=level is None))
            except (OSError, ValueError):
                # Backend exception text can contain private device paths.
                self._diag.append("battery I/O failed; connection unknown")
            finally:
                try:
                    dev.close()
                except OSError:
                    self._diag.append("HID handle close failed")
        return result

    def diagnostics(self):
        return list(self._diag)
