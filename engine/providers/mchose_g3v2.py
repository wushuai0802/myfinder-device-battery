"""Read the original MCHOSE G3 V2 wireless receiver using its M HUB battery page."""
import math
import re
import time

from battery_hid import BatteryHidProvider


VID = 0x3837
PIDS = {0x4245: "MCHOSE G3 V2"}
USB_PARENT = re.compile(r"pnp:usb\\vid_3837&pid_4245\\[^\\\s\x00-\x1f]{1,256}", re.I | re.ASCII)
HID_PATH = re.compile(
    r"\\\\\?\\hid#vid_3837&pid_4245&mi_([0-9a-f]{2})(?:&col[0-9a-f]{2})?"
    r"(?:#[^#\\\s\x00-\x1f]{1,256}){1,6}", re.I | re.ASCII)


class MchoseG3V2Provider(BatteryHidProvider):
    name = "mchose_g3v2"
    kind = "mouse"
    charging_known = True
    vid, pids = VID, PIDS
    collection = (0xFF01, 0x0010)
    input_id = output_id = 0
    input_bounds = (65, 65)
    output_bytes = 65
    _payload_bytes = 64
    _deadline_seconds = 0.55
    _max_unrelated = 4
    _status_request = bytes.fromhex("00 55 ED").ljust(65, b"\0")
    _battery_request = bytes.fromhex("00 55 30 A5 0B 2E 01 01 01").ljust(65, b"\0")

    @classmethod
    def matches_collection(cls, info):
        pid = info.get("product_id")
        physical = info.get("physical_id")
        interface = info.get("interface_number")
        if (info.get("vendor_id") != VID or pid not in PIDS or info.get("product_string") != PIDS[pid]
                or (info.get("usage_page"), info.get("usage")) != cls.collection
                or type(interface) is not int or not 0 <= interface <= 255
                or not isinstance(physical, str) or not USB_PARENT.fullmatch(physical)):
            return False
        path = info.get("path")
        if isinstance(path, bytes):
            try:
                path = path.decode("utf-8")
            except UnicodeDecodeError:
                return False
        match = HID_PATH.fullmatch(path) if isinstance(path, str) and len(path) <= 1024 else None
        return bool(match and int(match[1], 16) == interface)

    def _pick(self, infos):
        # The common picker rejects duplicate collections and proves both report IDs/sizes.
        info = super()._pick(infos)
        if info is not None and self.matches_collection(info):
            return info
        return None

    def _write_once(self, dev, request, label):
        if dev.write(request) != len(request):
            self._diag.append('short/failed ' + label + ' write; no retry')
            return False
        return True

    def _read_frame(self, dev, header, deadline, label):
        skipped = 0
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                self._diag.append(label + ' deadline expired; connection unknown')
                return None
            raw = dev.read(self._payload_bytes, max(1, int(math.ceil(remaining * 1000))))
            if not raw:
                self._diag.append(label + ' no reply; connection unknown')
                return None
            try:
                frame = bytes(raw)
            except (TypeError, ValueError):
                self._diag.append(label + ' rejected input report type')
                return None
            if len(frame) != self._payload_bytes:
                self._diag.append(label + ' rejected input report length')
                return None
            if frame[:2] != header:
                skipped += 1
                if skipped > self._max_unrelated:
                    self._diag.append(label + ' unrelated-frame limit reached')
                    return None
                continue
            return frame

    def _online_gate(self, dev, deadline):
        if not self._write_once(dev, self._status_request, 'online-status'):
            return False
        frame = self._read_frame(dev, bytes((0xAA, 0xED)), deadline, 'online-status')
        if frame is None:
            return False
        status = frame[8]
        if status not in (0, 2):
            self._diag.append('rejected online status; no battery query')
            return False
        return True

    def _battery_reading(self, dev, deadline):
        if not self._write_once(dev, self._battery_request, 'battery'):
            return None
        frame = self._read_frame(dev, bytes((0xAA, 0x30)), deadline, 'battery')
        if frame is None:
            return None
        raw = frame[8]
        charge = frame[9]
        if raw == 0:
            self._diag.append('battery unavailable; no synthetic level')
            return None
        if not 1 <= raw <= 100:
            self._diag.append('rejected raw battery percentage')
            return None
        if charge not in (0, 1):
            self._diag.append('rejected battery charge flag')
            return None
        level = min(100, max(10, ((raw + 5) // 10) * 10))
        return level, bool(charge), ''

    def _query(self, dev, info):
        deadline = time.monotonic() + self._deadline_seconds
        if not self._online_gate(dev, deadline):
            return None
        return self._battery_reading(dev, deadline)
