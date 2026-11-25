"""AJAZZ AK980 PRO keyboard battery reader (wired 0C45:8039 / 2.4G 0C45:FEFE).

Grade-C read-only GET_DEVICE_INFO candidate ported from Issue #966 (@NJjojoj,
recovered from official web driver `https://ajazz.driveall.cn/custom-keys`,
`layout-classic-CMxiG14P.js`, and hardware-verified on both wired and 2.4G links):

* Wired ``0C45:8039``: requires exact ``product_string == "AK980 PRO"``,
  ``interface_number == 2`` (``MI_02``), resolved matching USB parent, unique
  ``FF68:0061`` collection, and runtime descriptor proof of single unnumbered
  65-byte input/output reports (`{"0": 65}`). ``MI_03 FF67:0061`` (4105B
  LED/firmware channel) is never opened.
* 2.4G ``0C45:FEFE``: shared VID:PID. Never opens unless every ``FF60:0061``
  collection on the receiver carries ``product_string == "AJAZZ AK980 PRO 2.4G"``.
  Narrows strictly to ``interface_number == 3`` (``MI_03``) before uniqueness
  proof so ``MI_04`` junk frames are excluded while duplicate ``MI_03``
  collections still fail closed, plus proven 65-byte input/output reports.
* Sends one single 65-byte GET_DEVICE_INFO query (`00 AA 10 30 00 00 00 01 ...`)
  with no retries, verifies echoed ``0x0C45:0x8039`` hardware fingerprint in
  ``payload[4:8]``, checks ``workMode`` (0 wired, 2 wireless) and extracts
  ``payload[17]`` (1..100%). Charging semantics remain unconfirmed (`False`).
"""
from __future__ import annotations

import math
import re
import time

from battery_hid import BatteryHidProvider, report_proof_summary

VID = 0x0C45
WIRED_PID = 0x8039
DONGLE_PID = 0xFEFE
NAME = "AJAZZ AK980 PRO"
WIRED_PIDS = {WIRED_PID: NAME}
PIDS = {
    WIRED_PID: NAME,
    DONGLE_PID: NAME,
}

WIRED_PRODUCT = "AK980 PRO"
DONGLE_PRODUCT = "AJAZZ AK980 PRO 2.4G"
WIRED_COLLECTION = (0xFF68, 0x0061)
DONGLE_COLLECTION = (0xFF60, 0x0061)
WIRED_INTERFACE = 2
DONGLE_INTERFACE = 3

REPORT_BYTES = 65
PAYLOAD_BYTES = 64
REQUEST = bytes([0x00, 0xAA, 0x10, 0x30, 0x00, 0x00, 0x00, 0x01]) + bytes(REPORT_BYTES - 8)
ECHO_FINGERPRINT = bytes([0x45, 0x0C, 0x39, 0x80])

READ_TIMEOUT_MS = 600


def _valid_parent(physical, pid):
    if not isinstance(physical, str) or not physical or physical.startswith("collection:"):
        return False
    expected = f"{pid:04x}"
    return re.fullmatch(
        r"pnp:usb\\vid_0c45&pid_" + expected + r"(?:&rev_[0-9a-f]{4})?\\[^\\\s]+",
        physical,
        re.I,
    ) is not None


def _valid_path(path, pid, iface):
    if isinstance(path, bytes):
        try:
            text = path.decode("utf-8")
        except UnicodeDecodeError:
            return False
    elif isinstance(path, str):
        text = path
    else:
        return False
    parts = text.split("#")
    hw_re = re.compile(
        rf"vid_0c45&pid_{pid:04x}(?:&rev_[0-9a-f]{{4}})?&mi_{iface:02x}(?:&col[0-9a-f]{{2}})?",
        re.I,
    )
    return (bool(text)
            and "\0" not in text
            and len(parts) in (3, 4)
            and parts[0].casefold() == "\\\\?\\hid"
            and hw_re.fullmatch(parts[1]) is not None
            and bool(parts[2])
            and (len(parts) == 3 or bool(parts[3])))


def _matches_wired(info):
    return (info.get("vendor_id") == VID
            and info.get("product_id") == WIRED_PID
            and info.get("product_string") == WIRED_PRODUCT
            and (info.get("usage_page"), info.get("usage")) == WIRED_COLLECTION
            and type(info.get("interface_number")) is int
            and info["interface_number"] == WIRED_INTERFACE)


def _matches_dongle(info):
    return (info.get("vendor_id") == VID
            and info.get("product_id") == DONGLE_PID
            and info.get("product_string") == DONGLE_PRODUCT
            and (info.get("usage_page"), info.get("usage")) == DONGLE_COLLECTION
            and type(info.get("interface_number")) is int
            and info["interface_number"] == DONGLE_INTERFACE)


def matches_collection(info):
    """Exact wired MI_02 or 2.4G MI_03 candidate gate."""
    return _matches_wired(info) or _matches_dongle(info)


def is_candidate_info(info):
    """Strict candidate check for server health counts (excludes other 0C45:FEFE devices)."""
    vid = info.get("vendor_id")
    pid = info.get("product_id")
    if vid != VID:
        return False
    if pid == WIRED_PID:
        return info.get("product_string") == WIRED_PRODUCT
    if pid == DONGLE_PID:
        return info.get("product_string") == DONGLE_PRODUCT
    return False


def make_request():
    return REQUEST


def _valid_header(raw, pid=None):
    if not isinstance(raw, (bytes, bytearray, list, tuple)) or len(raw) != PAYLOAD_BYTES:
        return False
    if not isinstance(raw, (bytes, bytearray)):
        if any(type(b) is not int or not 0 <= b <= 255 for b in raw):
            return False
    if (raw[0] != 0x55 or raw[1] != 0x10 or raw[3] != 0x00 or raw[4] != 0x00
            or bytes(raw[12:16]) != ECHO_FINGERPRINT):
        return False
    expected_modes = (0,) if pid == WIRED_PID else (2,) if pid == DONGLE_PID else (0, 2)
    return raw[24] in expected_modes


def parse_reply(reply, pid=None):
    """Parse 64-byte unnumbered AK980 PRO GET_DEVICE_INFO reply."""
    if not _valid_header(reply, pid):
        return None
    level = reply[25]
    if not 1 <= level <= 100:
        return None
    return level, False, ""


class AjazzAk980Provider(BatteryHidProvider):
    name = "ajazz_ak980"
    vid = VID
    pids = PIDS
    kind = "keyboard"
    input_id = 0
    output_id = 0
    input_bounds = (REPORT_BYTES, REPORT_BYTES)
    output_bytes = REPORT_BYTES
    charging_known = False
    verification_level = "C"
    read_timeout_ms = READ_TIMEOUT_MS
    make_request = staticmethod(make_request)

    def _pick(self, infos):
        physical_set = {i.get("physical_id") for i in infos}
        if len(physical_set) != 1:
            return None
        physical = next(iter(physical_set))

        wired_rows = [
            i for i in infos
            if i.get("vendor_id") == VID and i.get("product_id") == WIRED_PID
            and (i.get("usage_page"), i.get("usage")) == WIRED_COLLECTION
        ]
        dongle_rows = [
            i for i in infos
            if i.get("vendor_id") == VID and i.get("product_id") == DONGLE_PID
            and (i.get("usage_page"), i.get("usage")) == DONGLE_COLLECTION
        ]
        if wired_rows and dongle_rows:
            return None

        if wired_rows:
            if not any(i.get("product_string") == WIRED_PRODUCT for i in wired_rows):
                return None
            if len(wired_rows) != 1 or not _matches_wired(wired_rows[0]):
                self._diag.append("missing or ambiguous eligible collection: nothing sent")
                return None
            info = wired_rows[0]
            expected_iface = WIRED_INTERFACE
        elif dongle_rows:
            if any(i.get("product_string") != DONGLE_PRODUCT for i in dongle_rows):
                return None
            # Narrow strictly to MI_03 before uniqueness check so MI_04 is excluded
            # while duplicate MI_03 collections (even unproven ones) still fail.
            mi03 = [i for i in dongle_rows if type(i.get("interface_number")) is int and i["interface_number"] == DONGLE_INTERFACE]
            if len(mi03) != 1 or not _matches_dongle(mi03[0]):
                self._diag.append("missing or ambiguous eligible collection: nothing sent")
                return None
            info = mi03[0]
            expected_iface = DONGLE_INTERFACE
        else:
            return None

        pid = info["product_id"]
        if not info.get("path") or not _valid_parent(physical, pid) or not _valid_path(info.get("path"), pid, expected_iface):
            self._diag.append("AK980 PRO USB parent or path unproven: nothing sent")
            return None
        for direction in ("input", "output"):
            maximum = info.get(direction + "_report_length")
            sizes = info.get(direction + "_report_sizes")
            if (type(maximum) is not int or maximum != REPORT_BYTES
                    or not isinstance(sizes, dict) or sizes != {"0": REPORT_BYTES}):
                self._diag.append("report IDs/lengths unknown or incompatible: nothing sent"
                                  + report_proof_summary(info, direction, include_declared=True))
                return None
        return info

    def _query(self, dev, info):
        request = make_request()
        if dev.write(request) != len(request):
            self._diag.append("short/failed battery query write; no retry")
            return None
        pid = info["product_id"]
        deadline = time.monotonic() + self.read_timeout_ms / 1000
        for _ in range(16):
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            raw = dev.read(PAYLOAD_BYTES, max(1, math.ceil(remaining * 1000)))
            if not raw:
                break
            if len(raw) != PAYLOAD_BYTES:
                self._diag.append("rejected input report length")
                continue
            if _valid_header(raw, pid):
                if raw[25] == 0:
                    self._diag.append("device asleep/offline; no new reading; connection unknown")
                    return None
                parsed = parse_reply(raw, pid)
                if parsed is not None:
                    return parsed
            self._diag.append("rejected battery reply")
        self._diag.append("no valid reply within deadline/read limit; connection unknown")
        return None
