"""Angry Miao AM Infinity .97 mouse on its 2.4 GHz receiver (0E8D:0703), issue #1102.

Protocol facts come from static analysis of the vendor's AM Master 1.3.9
Python bytecode and its web driver; nothing from the vendor is included or run.
The receiver relays an Airoha RACE read of the mouse battery (command 0x30CF):

    output report 06 (62 bytes incl. id):
        06 06 80 05 5A 02 00 CF 30 + 53 zero bytes
        (80 = the remote mouse behind the dongle; 05 5A 02 00 CF 30 = RACE
        channel 05, type 5A, length 2, command id CF 30)
    input report 07 (62 bytes incl. id): 07 <len lo> <len hi> <payload ...>
        The payloads of consecutive reports form a byte stream of RACE frames:
        <channel 05|15> <type 5B|5D> <len lo> <len hi> <len bytes>
        An ACK (type 5B, length 3) precedes the answer for command CF 30:
        frame[7] charging (0/1), [8] percent (0..100), [9] health (unused),
        [10] mouse present (0 = mouse absent/asleep behind the receiver).

Only that single read-only query is ever written, to the exact FF13:0001
collection of the exact "AM Infinity .97" receiver. The FF14/FF15 collections,
the dongle's own battery and every FOTA/config/RGB/DPI command stay untouched.
Grade C: implemented from the vendor software, not yet seen on hardware.
"""
from __future__ import annotations

import re
import time
from dataclasses import replace

import hid

from battery_hid import report_proof_summary
from . import hidlist
from .base import DeviceStatus, Provider

VID = 0x0E8D
PIDS = {0x0703: "Angry Miao AM Infinity .97"}
PRODUCT_STRING = "AM Infinity .97"
COLLECTION = (0xFF13, 0x0001)
OUTPUT_REPORT_ID = 0x06
INPUT_REPORT_ID = 0x07
REPORT_BYTES = 62

RESPONSE_TIMEOUT_MS = 800
MAX_READS = 25
READ_GAP_MS = 20
DRAIN_TIMEOUT_MS = 40
MAX_DRAIN_READS = 16
MAX_RX_BYTES = 4096

CHANNELS = (0x05, 0x15)
RACE_TYPES = (0x5B, 0x5D)
RACE_ACK_TYPE = 0x5B
RACE_ACK_LENGTH = 3
COMMAND_ID = b"\xCF\x30"
MIN_RESPONSE_BYTES = 11

_PARENT = re.compile(
    r"pnp:usb\\vid_0e8d&pid_0703(?:&rev_[0-9a-f]{4})?\\[^\\\x00\s]+",
    re.I,
)
_HARDWARE = re.compile(r"(?:^|[#\\&])vid_0e8d&pid_0703(?:$|[#\\&])", re.I)


def make_request():
    return (bytes([OUTPUT_REPORT_ID, 0x06, 0x80, 0x05, 0x5A, 0x02, 0x00, 0xCF, 0x30])
            + bytes(REPORT_BYTES - 9))


def _target(info):
    return (info.get("vendor_id") == VID
            and info.get("product_id") in PIDS
            and (info.get("usage_page"), info.get("usage")) == COLLECTION)


def matches_collection(info):
    """Exact identity, product string and path eligibility (no parent/report proof)."""
    if not _target(info):
        return False
    product = info.get("product_string")
    if not isinstance(product, str) or product.strip() != PRODUCT_STRING:
        return False
    path = info.get("path")
    if not isinstance(path, (str, bytes)):
        return False
    try:
        text = path.decode("utf-8") if isinstance(path, bytes) else path
    except UnicodeError:
        return False
    return "\0" not in text and _HARDWARE.search(text) is not None


def reports_proven(info):
    for direction, report_id in (("output", OUTPUT_REPORT_ID), ("input", INPUT_REPORT_ID)):
        length = info.get(direction + "_report_length")
        sizes = info.get(direction + "_report_sizes")
        if (type(length) is not int or not REPORT_BYTES <= length <= 4096
                or not isinstance(sizes, dict) or not sizes
                or any(not isinstance(key, str) or not key.isascii() or not key.isdecimal()
                       or len(key) > 3 or str(int(key)) != key or not 0 <= int(key) <= 255
                       or type(n) is not int or not 2 <= n <= length
                       for key, n in sizes.items())
                or max(sizes.values()) != length
                or sizes.get(str(report_id)) != REPORT_BYTES):
            return False
    return True


def _is_report(raw):
    return (isinstance(raw, (bytes, bytearray, list, tuple))
            and len(raw) == REPORT_BYTES
            and all(type(value) is int and 0 <= value <= 255 for value in raw))


def report_payload(raw):
    """RACE stream bytes carried by one input report, or None when it is not one."""
    if not _is_report(raw) or raw[0] != INPUT_REPORT_ID:
        return None
    length = raw[1] | (raw[2] << 8)
    if not 0 < length <= len(raw) - 3:
        return None
    return bytes(raw[3:3 + length])


def pop_frame(buffer):
    """Remove and return the next complete RACE frame from a bytearray, else None."""
    while True:
        while len(buffer) >= 2 and not (buffer[0] in CHANNELS and buffer[1] in RACE_TYPES):
            del buffer[0]
        if len(buffer) < 4:
            return None
        race_length = buffer[2] | (buffer[3] << 8)
        if race_length < 2 or 4 + race_length > MAX_RX_BYTES:
            del buffer[0]       # not a plausible frame start: resynchronise
            continue
        if len(buffer) < 4 + race_length:
            return None
        frame = bytes(buffer[:4 + race_length])
        del buffer[:4 + race_length]
        return frame


def is_ack(frame):
    return (frame[1] == RACE_ACK_TYPE
            and (frame[2] | (frame[3] << 8)) == RACE_ACK_LENGTH)


def parse_response(frame):
    """(level, charging, present) for a battery answer; None when unusable.

    present False means the receiver sees no mouse: no level is carried over.
    """
    if (len(frame) < MIN_RESPONSE_BYTES or frame[4:6] != COMMAND_ID
            or is_ack(frame)):
        return None
    if frame[10] == 0:
        return None, False, False
    if frame[7] not in (0, 1) or frame[8] > 100:
        return None
    return frame[8], frame[7] == 1, True


class AmInfinity97Provider(Provider):
    name = "am_infinity_97"
    kind = "mouse"
    charging_known = True

    def __init__(self):
        self._diag = []
        self._last = {}      # physical -> last measured DeviceStatus
        self._paths = {}     # physical -> paths seen for this identity

    def _drain(self, dev):
        """Drain queued interrupt reports; returns (ok, use_interrupt).

        Airoha RACE collections on Windows may expose IN report 07 only via
        HidD_GetInputReport (matching AM Master and audeze.py) without an
        interrupt-IN pipe. If dev.read raises OSError/ValueError, proceed with
        get_input_report instead of aborting before the query is sent.
        """
        deadline = time.monotonic() + DRAIN_TIMEOUT_MS / 1000
        for _ in range(MAX_DRAIN_READS):
            if time.monotonic() >= deadline:
                break
            try:
                # Positive timeout also bounds bindings where zero selects blocking read.
                raw = dev.read(REPORT_BYTES, 1)
            except (OSError, ValueError):
                return True, False
            if not raw:
                return True, True
        self._diag.append("input queue did not drain within bounds: nothing sent")
        return False, True

    def _query(self, dev):
        """(level, charging, present) from one bounded exchange, else None."""
        ok, use_interrupt = self._drain(dev)
        if not ok:
            return None
        request = make_request()
        written = dev.write(request)
        if type(written) is not int or written != REPORT_BYTES:
            self._diag.append("short/failed battery query write; no retry")
            return None
        stream = bytearray()
        last_get_raw = None
        deadline = time.monotonic() + RESPONSE_TIMEOUT_MS / 1000
        for _ in range(MAX_READS):
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            raw = None
            from_get = False
            if use_interrupt:
                try:
                    raw = dev.read(
                        REPORT_BYTES,
                        min(READ_GAP_MS, max(1, int(remaining * 1000))),
                    )
                except (OSError, ValueError):
                    use_interrupt = False
            if not use_interrupt:
                gap = min(READ_GAP_MS / 1000, remaining)
                if gap > 0:
                    time.sleep(gap)
                if time.monotonic() >= deadline:
                    break
                raw = dev.get_input_report(INPUT_REPORT_ID, REPORT_BYTES)
                from_get = True
            elif not raw:
                try:
                    raw = dev.get_input_report(INPUT_REPORT_ID, REPORT_BYTES)
                    from_get = True
                except (OSError, ValueError):
                    raw = None
            if from_get and raw:
                snapshot = bytes(raw) if _is_report(raw) else None
                if snapshot is not None and snapshot == last_get_raw:
                    continue
                last_get_raw = snapshot
            payload = report_payload(raw)
            if payload is None:
                continue
            if len(stream) + len(payload) > MAX_RX_BYTES:
                stream.clear()
            stream += payload
            while True:
                frame = pop_frame(stream)
                if frame is None:
                    break
                if frame[4:6] != COMMAND_ID or is_ack(frame):
                    continue
                reading = parse_response(frame)
                if reading is None:
                    self._diag.append("battery answer rejected (out of range or malformed)")
                    continue
                if not reading[2]:
                    self._diag.append("receiver reports no mouse; level unavailable")
                return reading
        self._diag.append("no valid battery answer within bounds")
        return None

    @staticmethod
    def _historical(status):
        return replace(status, online=False, cached=True, wake_on_input=True,
                       connection_known=False, charging_known=False)

    def _retained(self, groups, present_paths, unresolved):
        """History for receivers still in the USB tree; gone receivers are dropped."""
        kept = []
        for physical in list(self._last):
            if (physical in groups
                    or unresolved and self._paths.get(physical, set()) & present_paths):
                kept.append(self._historical(self._last[physical]))
            else:
                del self._last[physical]
                self._paths.pop(physical, None)
        return kept

    def poll(self):
        self._diag = []
        if hid is None:
            return []
        try:
            inventory = hidlist.enumerate(VID)
        except OSError:
            self._diag.append("enumeration failed; retaining history")
            return [self._historical(s) for s in self._last.values()]

        groups, targets = {}, {}
        present_paths = set()
        unresolved = False
        for info in inventory:
            if info.get("vendor_id") != VID or info.get("product_id") not in PIDS:
                continue
            path = info.get("path")
            physical = info.get("physical_id")
            resolved = isinstance(physical, str) and _PARENT.fullmatch(physical) is not None
            if path:
                present_paths.add(path)
            if resolved:
                groups.setdefault(physical.casefold(), []).append(info)
                if path:
                    self._paths.setdefault(physical.casefold(), set()).add(path)
            if not _target(info):
                continue
            if not resolved:
                unresolved = True
                continue
            # Count before product string, path or descriptor-proof filtering.
            targets.setdefault(physical.casefold(), []).append(info)
        if unresolved:
            self._diag.append("target USB parent unresolved; uniqueness unknown: nothing sent")
            return self._retained(groups, present_paths, True)

        result = []
        for physical, candidates in targets.items():
            if len(candidates) != 1:
                self._diag.append("ambiguous target collection: nothing sent")
                continue
            info = candidates[0]
            if not matches_collection(info):
                self._diag.append("product string/path unproven: nothing sent")
                continue
            if not reports_proven(info):
                out_summary = report_proof_summary(info, "output")
                in_summary = report_proof_summary(info, "input", include_declared=True)
                self._diag.append("numbered OUT 6 / IN 7 report (62 bytes) unproven: nothing sent"
                                  + out_summary + in_summary)
                continue
            dev = None
            try:
                dev = hid.device()
                dev.open_path(info["path"])
                reading = self._query(dev)
            except (OSError, ValueError):
                self._diag.append("battery I/O failed; connection unknown")
                reading = None
            finally:
                if dev is not None:
                    try:
                        dev.close()
                    except OSError:
                        self._diag.append("HID handle close failed")
            if reading is None or not reading[2]:
                continue
            level, charging, _ = reading
            status = DeviceStatus(
                key=f"{self.name}:{physical}",
                name=PIDS[info["product_id"]],
                level=level,
                charging=charging,
                online=True,
                source=self.name,
                kind="mouse",
                physical_id=physical,
                connection_known=True,
                charging_known=True,
                verification_level="C",
                unconfirmed=True,
            )
            self._last[physical] = status
            result.append(status)

        fresh = {s.physical_id for s in result}
        retained = self._retained(groups, present_paths, False)
        result.extend(h for h in retained if h.physical_id not in fresh)
        return result

    def diagnostics(self):
        return list(self._diag)
