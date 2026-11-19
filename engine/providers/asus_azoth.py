"""ROG keyboard battery pages for the original Azoth receiver and typed OMNI slots."""
import re
import time

import battery_hid
from battery_hid import BatteryHidProvider

VID = 0x0B05
PIDS = {0x1A85: "ROG Azoth", 0x1ACE: "ROG OMNI 接收器（键盘）"}
KEYBOARDS = {0x1A85: "ROG Azoth", 0x1B06: "ROG Falchion RX Low Profile"}
_PRODUCTS = {0x1A85: "ROG AZOTH", 0x1ACE: "ROG OMNI RECEIVER"}
_MOUSE_PIDS = {0x1C0E, 0x1A94, 0x1B68, 0x1B69, 0x1B1A, 0x1B18,
               0x1AD7, 0x1B65, 0x1A68, 0x1A6A, 0x1A72}


def collection_role(info):
    """Windows exposes each top-level collection on a distinct HID path."""
    pid = info.get("product_id")
    if (info.get("vendor_id") != VID or pid not in _PRODUCTS
            or not isinstance(info.get("product_string"), str)
            or info["product_string"].upper() != _PRODUCTS[pid]):
        return None
    path = info.get("path")
    if isinstance(path, bytes):
        try:
            path = path.decode("utf-8")
        except UnicodeDecodeError:
            return None
    if not isinstance(path, str) or len(path) > 1024 or "\0" in path:
        return None
    prefix = rf"\\?\hid#vid_0b05&pid_{pid:04x}&mi_"
    path = path.lower()
    if not path.startswith(prefix):
        return None
    head, sep, instance = path[len(prefix):].partition("#")
    if not sep or not instance or instance.startswith("#") or "\\" in instance:
        return None
    usage = (info.get("usage_page"), info.get("usage"))
    if pid == 0x1A85:
        if (info.get("interface_number") == 1 and usage == (0xFF00, 1)
                and re.fullmatch(r"01(?:&col[0-9a-f]{2})?", head)):
            return "direct"
    elif info.get("interface_number") == 2:
        if head == "02&col02" and usage == (0xFF00, 1):
            return "keyboard"
        if head == "02&col01" and type(usage[0]) is int and 0xFF00 <= usage[0] <= 0xFFFF:
            return "identity"
    return None


def proven(info, report_id, length):
    for direction in ("input", "output"):
        sizes = info.get(direction + "_report_sizes")
        maximum = info.get(direction + "_report_length")
        if (info.get(direction + "_report_proof") not in ("caps", "descriptor")
                or type(maximum) is not int or maximum != length
                or not isinstance(sizes, dict) or sizes != {str(report_id): length}
                or type(sizes[str(report_id)]) is not int):
            return False
    return True


def read_echo(dev, packet, echo, *, timeout_ms=350):
    try:
        if dev.write(packet) != len(packet):
            return None
    except (OSError, TypeError, ValueError):
        return None
    deadline = time.monotonic() + timeout_ms / 1000.0
    for _ in range(4):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return None
        try:
            data = dev.read(64, max(1, int(remaining * 1000)))
        except (OSError, TypeError, ValueError):
            return None
        if not isinstance(data, (bytes, bytearray, list, tuple, memoryview)) or len(data) != 64:
            return None
        try:
            data = bytes(data)
        except (TypeError, ValueError):
            return None
        if data.startswith(b'\x02\xff\xaa') or data.startswith(b'\x01\xff\xaa'):
            return None
        if data.startswith(echo):
            return data
    return None


def omni_keyboard_pid(frame):
    if not isinstance(frame, (bytes, bytearray, memoryview)):
        return None
    frame = bytes(frame)
    if len(frame) != 64 or frame[0] != 0x01 or frame[1] != 0xA0:
        return None
    pids = []
    pos = 5
    terminated = False
    while pos + 1 < len(frame):
        pid = frame[pos] | (frame[pos + 1] << 8)
        if pid == 0:
            terminated = True
            break
        pids.append(pid)
        if len(pids) > 2:
            return None
        pos += 4
    if not terminated or not pids:
        return None
    if len(pids) == 2 and pids[0] == pids[1]:
        return None
    keyboards = [pid for pid in pids if pid in KEYBOARDS]
    if len(keyboards) != 1 or any(pid not in _MOUSE_PIDS and pid not in KEYBOARDS for pid in pids):
        return None
    return keyboards[0]


class AsusAzothProvider(BatteryHidProvider):
    name = "asus_azoth"
    vid, pids = VID, PIDS
    kind = "keyboard"
    charging_known = True

    def _pick(self, infos):
        routes = [(info, collection_role(info)) for info in infos]
        keyboard = [info for info, role in routes if role in ("direct", "keyboard")]
        if len(keyboard) != 1:
            return None
        info = keyboard[0]
        pid = info["product_id"]
        parent = info.get("physical_id")
        if not isinstance(parent, str) or not re.fullmatch(
                rf"pnp:usb\\vid_0b05&pid_{pid:04x}\\[^\\/\s\x00-\x1f]+", parent, re.I):
            return None
        if pid == 0x1A85:
            return info if proven(info, 0, 65) else None

        # FF00's battery offset depends on the downstream keyboard, even for
        # receivers with identical USB descriptors. Confirm it on this parent.
        ids = [row for row, role in routes if role == "identity" and row.get("physical_id") == parent]
        if len(ids) != 1 or not proven(info, 2, 64) or not proven(ids[0], 1, 64):
            return None
        dev = None
        try:
            dev = battery_hid.hid.device()
            dev.open_path(ids[0]["path"])
            reply = read_echo(dev, b"\x01\xa0".ljust(64, b"\0"), b"\x01\xa0")
            model = omni_keyboard_pid(reply)
        except (OSError, ValueError):
            return None
        finally:
            if dev is not None:
                try:
                    dev.close()
                except OSError:
                    pass
        return dict(info, keyboard_pid=model) if model in KEYBOARDS else None

    def device_name(self, info):
        return KEYBOARDS[info.get("keyboard_pid", info["product_id"])]

    def _query(self, dev, info):
        omni = info["product_id"] == 0x1ACE
        request = (b"\x02\x12\x01" if omni else b"\x00\x12\x01").ljust(64 if omni else 65, b"\0")
        reply = read_echo(dev, request, request[:3] if omni else request[1:3])
        if reply is None:
            return None
        if omni:
            offset = 11 if info["keyboard_pid"] == 0x1B06 else 6
            level, charge = reply[offset], reply[9]
        else:
            level, charge = reply[5], reply[8]
        return (level, bool(charge), "") if 1 <= level <= 100 and charge in (0, 1) else None
