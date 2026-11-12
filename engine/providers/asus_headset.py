"""Grade-C ROG Delta II headset candidate, pending project hardware validation.

Protocol facts only. ASUS GearLink "pattern 2" as recorded by dvalfrid/rigstats
(MIT), `sensor-sidecar/Control/Lighting/AsusHeadsetDevice.cs` and
`Control/Lighting/Battery.cs`, whose Delta II (0x1AFA) read is verified on
hardware; and the unsolicited report layout in mbekkelund/rog-delta-ii issue #1
(MIT). No upstream implementation is copied, imported or executed.

Reports use ID 0xCC on the vendor collection FF00:0001. A get frame is
    CC 12 <key> 00 00 <zeros>
with 0x12 = get. Battery is key 07 and the reply repeats `CC 12 07`; the
percentage is byte 6 (indexes include the report ID). Charging is key 08 and
byte 5 of that reply is 1 while the cable is connected. Only these two reads
are sent: no lighting, sidetone, EQ, microphone, pairing, reset or firmware
command. A reply of 0 % without charging is treated as standby, not as empty.
"""
import math
import time

try:
    import hid
except ImportError:  # pragma: no cover
    hid = None

from . import hidlist
from .base import DeviceStatus, Provider

VID = 0x0B05
PIDS = {0x1AFA: "ROG Delta II"}
COLLECTION = (0xFF00, 0x0001)
REPORT_ID = 0xCC
GET = 0x12
BATTERY_KEY = 0x07
CHARGING_KEY = 0x08
PERCENT_INDEX = 6
CHARGING_INDEX = 5
MIN_INPUT_BYTES = 7               # CC 12 07 00 00 timer percent
MIN_OUTPUT_BYTES = 5              # CC 12 07 00 00
MAX_REPORT_BYTES = 4096
MAX_READS = 8                     # Per key; the shared deadline still bounds all reads.
READ_TIMEOUT_MS = 500
QUERY_SECONDS = 2.0


def make_request(key, length):
    """One GearLink get frame zero-filled to the proven output capacity."""
    if (key not in (BATTERY_KEY, CHARGING_KEY) or type(length) is not int
            or not MIN_OUTPUT_BYTES <= length <= MAX_REPORT_BYTES):
        raise ValueError("unproven output length")
    return [REPORT_ID, GET, key, 0x00, 0x00] + [0] * (length - MIN_OUTPUT_BYTES)


def _frame(reply, length):
    return (isinstance(reply, (bytes, bytearray, list, tuple))
            and len(reply) == length
            and all(type(n) is int and 0 <= n <= 255 for n in reply))


def parse_battery(reply, length):
    """Percentage from a `CC 12 07` reply; None for anything else."""
    if (not _frame(reply, length) or length < MIN_INPUT_BYTES
            or (reply[0], reply[1], reply[2]) != (REPORT_ID, GET, BATTERY_KEY)):
        return None
    percent = reply[PERCENT_INDEX]
    return percent if percent <= 100 else None


def parse_charging(reply, length):
    """True/False from a `CC 12 08` reply; None when it is not that reply."""
    if (not _frame(reply, length) or length <= CHARGING_INDEX
            or (reply[0], reply[1], reply[2]) != (REPORT_ID, GET, CHARGING_KEY)):
        return None
    return reply[CHARGING_INDEX] == 1


def _reports(info):
    """Report CC, with a proven byte-array length in both directions."""
    for direction, minimum in (("input", MIN_INPUT_BYTES), ("output", MIN_OUTPUT_BYTES)):
        maximum = info.get(direction + "_report_length")
        sizes = info.get(direction + "_report_sizes")
        if (type(maximum) is not int or not minimum <= maximum <= MAX_REPORT_BYTES
                or info.get(direction + "_report_proof") not in ("caps", "descriptor")
                or not isinstance(sizes, dict) or not sizes
                or any(not isinstance(key, str) or not key.isascii() or not key.isdecimal()
                       or len(key) > 3 or str(int(key)) != key or not 0 <= int(key) <= 255
                       or type(size) is not int or not 2 <= size <= maximum
                       for key, size in sizes.items())
                or max(sizes.values()) != maximum
                or sizes.get(str(REPORT_ID)) != maximum):
            return False
    return True


def _collection(info):
    path = info.get("path")
    return ((info.get("usage_page"), info.get("usage")) == COLLECTION
            and isinstance(path, (bytes, str)) and bool(path))


class AsusHeadsetProvider(Provider):
    name = "asus_headset"

    def __init__(self):
        self._diag = []

    def _pick(self, infos):
        candidates = [info for info in infos if _collection(info)]
        if not candidates:
            self._diag.append("DELTA II collection mismatch; not opened")
        elif len(candidates) != 1:
            self._diag.append("DELTA II collection ambiguous; not opened")
        elif not _reports(candidates[0]):
            self._diag.append("DELTA II report CC length unproven; not opened")
        else:
            return candidates[0]
        return None

    def _read(self, dev, key, length, deadline):
        """One matching reply, skipping this dongle's other reports."""
        for _ in range(MAX_READS):
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            try:
                raw = dev.read(length, max(1, min(READ_TIMEOUT_MS, math.ceil(remaining * 1000))))
            except Exception:
                self._diag.append("DELTA II input read failed")
                return None
            if raw is None or isinstance(raw, (bytes, bytearray, list, tuple)) and not raw:
                break
            if not _frame(raw, length):
                continue
            if not any(raw):
                self._diag.append("DELTA II empty reply or asleep; no new reading")
                return None
            if raw[0] != REPORT_ID:
                continue
            if tuple(raw[1:3]) == (0xFF, 0xAA):
                self._diag.append("DELTA II device error FF AA")
                return None
            if raw[2] != key:
                continue                       # another GearLink report, e.g. 12 09
            return list(raw)
        self._diag.append("DELTA II no " + ("battery" if key == BATTERY_KEY else "charging")
                          + " echo within read limit")
        return None

    def _ask(self, dev, info, key, deadline):
        length = info["output_report_length"]
        request = make_request(key, length)
        try:
            written = dev.write(request)
            if type(written) is not int or written != length:
                self._diag.append("DELTA II query write failed; no retry")
                return None
        except Exception:
            self._diag.append("DELTA II query write failed; no retry")
            return None
        return self._read(dev, key, info["input_report_length"], deadline)

    def _query(self, dev, info):
        deadline = time.monotonic() + QUERY_SECONDS
        battery = self._ask(dev, info, BATTERY_KEY, deadline)
        if battery is None:
            return None
        percent = parse_battery(battery, info["input_report_length"])
        if percent is None:
            self._diag.append("DELTA II battery out of range")
            return None
        if percent == 0 and not any(battery[5:]):
            # A bare echo or a sleeping headset, not a measured empty battery.
            self._diag.append("DELTA II empty reply or asleep; no new reading")
            return None
        charging_reply = self._ask(dev, info, CHARGING_KEY, deadline)
        charging = parse_charging(charging_reply, info["input_report_length"])
        charging_known = charging is not None
        if not charging_known:
            charging = False
            self._diag.append("DELTA II charging state unavailable")
        if percent == 0 and not charging:
            self._diag.append("DELTA II zero battery; no new reading")
            return None
        return percent, charging, charging_known

    def poll(self):
        self._diag = []
        if hid is None:
            self._diag.append("DELTA II HID backend unavailable")
            return []
        try:
            inventory = hidlist.enumerate(VID)
        except Exception:
            self._diag.append("DELTA II enumeration failed")
            return []
        groups = {}
        for info in inventory:
            if (any(type(info.get(field)) is not int for field in ("vendor_id", "product_id"))
                    or (info["vendor_id"], info["product_id"]) != (VID, 0x1AFA)):
                continue                       # other ASUS collections are not this model
            physical = info.get("physical_id")
            if (not isinstance(physical, str) or not physical.strip()
                    or physical.casefold().startswith("collection:")):
                self._diag.append("DELTA II USB parent unproven; not opened")
                continue
            groups.setdefault(physical, []).append(info)
        result = []
        for physical, infos in groups.items():
            info = self._pick(infos)
            if info is None:
                continue
            dev = None
            try:
                dev = hid.device()
                dev.open_path(info["path"])
            except Exception:
                self._diag.append("DELTA II open failed")
            else:
                reading = self._query(dev, info)
                if reading is not None:
                    level, charging, charging_known = reading
                    result.append(DeviceStatus(
                        key=f"{self.name}:{physical}", name=PIDS[0x1AFA], level=level,
                        charging=charging, source=self.name, kind="headset",
                        physical_id=physical, connection_known=True,
                        charging_known=charging_known, verification_level="C"))
            finally:
                if dev is not None:
                    try:
                        dev.close()
                    except Exception:
                        self._diag.append("DELTA II HID handle close failed")
        return result

    def diagnostics(self):
        return list(self._diag)
