"""Read the confirmed Scope II 96 Wireless on a ROG Omni receiver.

Only the tested 0B05:1ACE receiver, its paired 1AB0 keyboard on report 2,
and the observed two MI_02 collections are supported. No setting is changed.
Protocol: RIGStats OmniMouse.cs, AsusKeyboardDevice.cs and Battery.cs (MIT).
"""
from __future__ import annotations

import time

try:
    import hid
except ImportError:  # pragma: no cover
    hid = None

from . import hidlist
from .base import DeviceStatus, Provider

VID, RECEIVER, KEYBOARD_PID = 0x0B05, 0x1ACE, 0x1AB0
NAME = "ROG Strix Scope II 96 Wireless"
PIDS = {0x1ACE: "ROG Strix Scope II 96 Wireless via Omni"}
SIZE = 64
TIMEOUT = 1.0
MAX_READS = 16


def expected_descriptor(page, report):
    """Exact byte-array input/output descriptors seen on the tested receiver."""
    return bytes([0x06, page & 0xFF, page >> 8, 0x09, 1, 0xA1, 1, 0x85, report,
                  0x09, 2, 0x15, 0, 0x26, 0xFF, 0, 0x75, 8, 0x95, 0x3F, 0x81, 2,
                  0x09, 3, 0x15, 0, 0x26, 0xFF, 0, 0x75, 8, 0x95, 0x3F, 0x91, 2, 0xC0])


def paired_channel(reply):
    if len(reply) != SIZE or reply[:3] != bytes((1, 0xA0, 0)):
        return None
    count = reply[3]
    if not 1 <= count <= 10 or 5 + count * 4 > len(reply):
        return None
    entries = [(reply[i] | reply[i + 1] << 8, reply[i + 2], reply[i + 3])
               for i in range(5, 5 + count * 4, 4)]
    # Exactly one target; report 2 is the only channel observed on hardware.
    return 2 if entries.count((KEYBOARD_PID, 2, 4)) == 1 and sum(e[1] == 2 for e in entries) == 1 else None


def battery_value(reply):
    if (len(reply) != SIZE or reply[:3] != bytes((2, 0x12, 1))
            or not 1 <= reply[6] <= 100 or reply[9] not in (0, 1)):
        return None
    # A single discharging sample is the local evidence; charging is not claimed.
    return reply[6]


def query(dev, report, command):
    prefix = bytes((report,) + command)
    request = prefix + bytes(SIZE - len(prefix))
    if dev.write(request) != SIZE:
        return None
    deadline = time.monotonic() + TIMEOUT
    for _ in range(MAX_READS):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            break
        response = bytes(dev.read(SIZE, max(1, int(remaining * 1000))))
        if not response:
            break
        if len(response) == SIZE and response.startswith(prefix) and response != request:
            return response
    return None


class RogOmniKeyboardProvider(Provider):
    name = "rog_omni_keyboard"

    def __init__(self):
        self._diag = []
        self.candidate_count = 0

    def diagnostics(self):
        return list(self._diag)

    def poll(self):
        self._diag = []
        self.candidate_count = 0
        if hid is None:
            return []
        try:
            infos = hidlist.enumerate(VID)
        except OSError:
            self._diag.append("enumeration unavailable")
            return []
        groups = {}
        for d in infos:
            if (d.get("vendor_id"), d.get("product_id"), d.get("interface_number"), d.get("usage")) != (VID, RECEIVER, 2, 1):
                continue
            physical = d.get("physical_id") or ""
            if not physical or physical.startswith("collection:"):
                continue
            if d.get("usage_page") in (0xFF00, 0xFF02):
                groups.setdefault(physical, []).append(d)
        results = []
        for physical, group in groups.items():
            receiver = [d for d in group if d.get("usage_page") == 0xFF02]
            keyboard = [d for d in group if d.get("usage_page") == 0xFF00]
            if len(receiver) != 1 or len(keyboard) != 1:
                self._diag.append("missing or ambiguous collections; nothing sent")
                continue
            pair = [receiver[0], keyboard[0]]
            if any(not x.get("path") or x.get("input_report_length") != SIZE or x.get("output_report_length") != SIZE for x in pair):
                self._diag.append("report size unavailable or incompatible; nothing sent")
                continue
            handles = []
            try:
                for info, report in zip(pair, (1, 2)):
                    dev = hid.device()
                    handles.append(dev)
                    dev.open_path(info["path"])
                    if bytes(dev.get_report_descriptor()) != expected_descriptor(info["usage_page"], report):
                        break
                else:
                    paired = query(handles[0], 1, (0xA0, 0))
                    if paired and paired_channel(paired) == 2:
                        self.candidate_count += 1
                        raw = query(handles[1], 2, (0x12, 1))
                        level = battery_value(raw) if raw else None
                        if level is not None:
                            results.append(DeviceStatus("rog_omni:1ab0:2", NAME, level, False, True, self.name,
                                                        kind="keyboard", physical_id=physical,
                                                        connection_known=True, charging_known=False,
                                                        verification_level="C"))
            except (OSError, ValueError, TypeError, AttributeError):
                self._diag.append("device unavailable or descriptor unsupported")
            finally:
                for dev in handles:
                    try:
                        dev.close()
                    except OSError:
                        pass
        return results
