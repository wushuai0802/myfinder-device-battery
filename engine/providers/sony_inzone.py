"""Battery-only Sony HCI GET for three INZONE USB receivers. See upstream-protocols."""
import math
import secrets
import time

import hid

from battery_hid import MAX_READS, READ_TIMEOUT_MS
from providers import hidlist
from providers.base import DeviceStatus, Provider

VID = 0x054C
PIDS = {0x0EBF: "Sony INZONE H5", 0x0FA8: "Sony INZONE H9 II", 0x0EC2: "Sony INZONE Buds"}
BUDS = 0x0EC2
REPORT_ID = 2
REPORT_BYTES = 64


def matches_collection(info):
    if info.get("vendor_id") != VID or info.get("usage_page") != 0xFF04:
        return False
    pid, usage = info.get("product_id"), info.get("usage")
    if pid == 0x0EBF:
        # Windows #813 reports usage 1. HeadsetControl's H5 uses usage 2.
        return usage in (1, 2)
    if pid == 0x0FA8:
        return usage == 1 and info.get("interface_number") == 5
    return pid == BUDS and usage == 1


def reports_proven(info):
    for direction in ("input", "output"):
        sizes = info.get(direction + "_report_sizes")
        if (info.get(direction + "_report_length") != REPORT_BYTES
                or not isinstance(sizes, dict) or sizes.get(str(REPORT_ID)) != REPORT_BYTES):
            return False
    return True


def battery_get(tid):
    if type(tid) is not int or not 2 <= tid <= 65535:
        raise ValueError("invalid Sony transaction ID")
    report = bytearray(REPORT_BYTES)
    report[:11] = bytes.fromhex("02 0C 01 00 FC 08 96 C3 41 04 01")
    report[11:13] = tid.to_bytes(2, "little")
    report[13] = sum(report[6:13]) & 255
    return bytes(report)


def battery_payload(report, tid, minimum):
    if len(report) != REPORT_BYTES or report[0] != REPORT_ID:
        return None
    length = report[1]
    if not 12 + minimum <= length <= REPORT_BYTES - 2:
        return None
    packet = bytes(report)
    if (packet[2:4] != b"\x04\xff" or packet[4] != length - 3
            or packet[5:10] != b"\x00\x96\xc3\x14\x04"
            or packet[10] not in (0x10, 0xA0)
            or int.from_bytes(packet[11:13], "little") != tid
            or (sum(packet[5:length + 1]) & 255) != packet[length + 1]):
        return None
    return packet[13:length + 1]


class SonyInzoneProvider(Provider):
    name = "sony_inzone"

    def __init__(self):
        # TID 1 is also used for unsolicited pushes. Each GET has its own ID.
        self.tid = secrets.randbelow(65534) + 2
        self._diag = []

    def diagnostics(self):
        return list(self._diag)

    def _read(self, dev, pid):
        tid = self.tid
        self.tid = tid + 1 if tid < 65535 else 2
        if dev.write(battery_get(tid)) != REPORT_BYTES:
            self._diag.append("short Sony battery query write")
            return None
        deadline = time.monotonic() + READ_TIMEOUT_MS / 1000
        for _ in range(MAX_READS):
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            report = dev.read(REPORT_BYTES, max(1, math.ceil(remaining * 1000)))
            if not report:
                break
            payload = battery_payload(report, tid, 6 if pid == BUDS else 2)
            if payload is not None:
                return payload
        self._diag.append("no matching Sony battery reply")
        return None

    def _devices(self, pid, physical, payload):
        parts = (("left", "左耳"), ("right", "右耳"), ("case", "充电盒")) if pid == BUDS else (("", ""),)
        devices = []
        for index, (unit, label) in enumerate(parts):
            charger, level = payload[index * 2:index * 2 + 2]
            if level > 100 and level != 255:
                continue
            known = level != 255
            key = f"{self.name}:{physical}"
            name = PIDS[pid]
            if unit:
                key += ":" + unit
                name += " " + label
            devices.append(DeviceStatus(
                key=key, name=name, kind="headset", source=self.name, physical_id=physical,
                unit_id=unit, level=level if known else None, level_unavailable=not known,
                online=known, connection_known=known or pid != BUDS, charging=charger == 1 and known,
                charging_known=known and unit != "case" and charger in (0, 1), verification_level="C"))
        return devices

    def poll(self):
        self._diag = []
        result = []
        groups = {}
        try:
            for info in hidlist.enumerate(VID):
                if matches_collection(info):
                    groups.setdefault(info.get("physical_id") or "", []).append(info)
        except OSError:
            self._diag.append("Sony enumeration failed")
            return result
        for physical, entries in groups.items():
            if not physical or physical.startswith("collection:") or len(entries) != 1:
                self._diag.append("Sony receiver or battery collection ambiguous")
                continue
            info = entries[0]
            if not info.get("path") or not reports_proven(info):
                self._diag.append("Sony battery report ID or size unconfirmed")
                continue
            dev = hid.device()
            try:
                dev.open_path(info["path"])
                payload = self._read(dev, info["product_id"])
                if payload is not None:
                    result.extend(self._devices(info["product_id"], physical, payload))
            except (OSError, ValueError):
                self._diag.append("Sony battery I/O failed")
            finally:
                try:
                    dev.close()
                except OSError:
                    self._diag.append("Sony HID handle close failed")
        return result
