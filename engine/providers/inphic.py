"""Passive Inphic IN9 SLE battery notifications (2.4G 7768:642C / wired 7768:6D2C).

Grade-C receive-only reader ported cleanly from Issue #959 (@NJjojoj, real
Windows hardware verification against official Inphic IN9 SLE driver v1.0.1.2):

* Only ``0x7768`` PIDs ``0x642C`` (2.4G SparkLink receiver) and ``0x6D2C``
  (wired USB) on ``MI_01`` / ``FFB2:0001`` with a resolved USB parent, unique
  collection per parent and descriptor-proven 64-byte Input report ``0x06``
  are opened. ``7768:642E`` (AJAZZ AJ179 NLP) is never touched.
* Zero writes / zero Feature calls: packets arrive autonomously roughly every
  30 seconds (`06 FA 03 00 00 03 <percent> <cable_flag> ...`).
* Byte 6 == 0 is emitted transiently during link handoff and ignored so it
  never overwrites cached status or produces a spurious 0% card. Byte 7 is
  not proven to distinguish charging from cable-attached, so ``charging_known``
  remains ``False``.
* Keeps the collection handle open across polls so Windows queues incoming
  notifications; first poll listens up to ``READ_WINDOW_MS`` and subsequent
  polls drain queued frames up to the newest valid percentage.
"""
from __future__ import annotations

from dataclasses import replace
from datetime import datetime, timezone
import re
import time

try:
    import hid
except ImportError:  # pragma: no cover
    hid = None

from battery_hid import report_proof_summary
from providers import hidlist
from providers.base import DeviceStatus, Provider

VID = 0x7768
WIRELESS_PID = 0x642C
WIRED_PID = 0x6D2C
NAME = "Inphic IN9 SLE"
PIDS = {
    WIRELESS_PID: NAME,
    WIRED_PID: NAME,
}
COLLECTION = (0xFFB2, 0x0001)
INTERFACE_NUMBER = 1
INPUT_ID = 0x06
REPORT_BYTES = 64
READ_WINDOW_MS = 1500
MAX_READS = 32
DRAIN_TIMEOUT_MS = 1
DRAIN_BUDGET_MS = 500
MAX_DRAIN = 64

_PARENT_RE = re.compile(
    r"pnp:usb\\vid_7768&pid_(?:642c|6d2c)(?:&rev_[0-9a-f]{4})?\\[^\\\s]+",
    re.I,
)
_HARDWARE_RE = re.compile(
    r"vid_7768&pid_(?:642c|6d2c)(?:&rev_[0-9a-f]{4})?&mi_01(?:&col[0-9a-f]{2})?",
    re.I,
)


def _valid_parent(physical, pid):
    if not isinstance(physical, str) or not physical or physical.startswith("collection:"):
        return False
    expected = f"{pid:04x}"
    return re.fullmatch(
        r"pnp:usb\\vid_7768&pid_" + expected + r"(?:&rev_[0-9a-f]{4})?\\[^\\\s]+",
        physical,
        re.I,
    ) is not None


def _valid_path(path, pid):
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
    expected_prefix = f"vid_7768&pid_{pid:04x}"
    return (bool(text)
            and "\0" not in text
            and len(parts) in (3, 4)
            and parts[0].casefold() == "\\\\?\\hid"
            and parts[1].lower().startswith(expected_prefix)
            and _HARDWARE_RE.fullmatch(parts[1]) is not None
            and bool(parts[2])
            and (len(parts) == 3 or bool(parts[3])))


def matches_collection(info):
    """Exact IN9 SLE MI_01 FFB2:0001 identity/interface/path gate."""
    pid = info.get("product_id")
    return (info.get("vendor_id") == VID
            and pid in PIDS
            and (info.get("usage_page"), info.get("usage")) == COLLECTION
            and type(info.get("interface_number")) is int
            and info["interface_number"] == INTERFACE_NUMBER
            and _valid_path(info.get("path"), pid))


def _valid_raw_frame(raw):
    if not isinstance(raw, (bytes, bytearray, list, tuple)) or len(raw) != REPORT_BYTES:
        return False
    if not isinstance(raw, (bytes, bytearray)):
        if any(type(b) is not int or not 0 <= b <= 255 for b in raw):
            return False
    return (list(raw[:6]) == [INPUT_ID, 0xFA, 0x03, 0x00, 0x00, 0x03]
            and raw[7] in (0, 1))


def is_zero_placeholder(raw):
    """True when the frame is the transient zero battery handoff packet."""
    return _valid_raw_frame(raw) and raw[6] == 0


def parse_battery(raw):
    """Parse a 64-byte IN9 SLE heartbeat report, ignoring 0% handoff placeholders."""
    if not _valid_raw_frame(raw):
        return None
    level = raw[6]
    if not 1 <= level <= 100:
        return None
    return level


class InphicProvider(Provider):
    name = "inphic"
    kind = "mouse"
    verification_level = "C"
    charging_known = False
    unconfirmed = True

    def __init__(self):
        self._diag = []
        self._handles = {}   # physical USB parent -> (identity, open handle)
        self._last = {}      # physical USB parent -> last DeviceStatus
        self._paths = {}     # physical USB parent -> set of known collection paths

    def _inventory_safe(self, inventory):
        paths = set()
        for info in inventory:
            if (info.get("vendor_id") != VID
                    or info.get("product_id") not in PIDS
                    or (info.get("usage_page"), info.get("usage")) != COLLECTION):
                continue
            pid = info["product_id"]
            physical = info.get("physical_id")
            path = info.get("path")
            if not _valid_parent(physical, pid) or not _valid_path(path, pid):
                self._diag.append("IN9 SLE USB parent or path unproven; not opened")
                return False
            if path in paths:
                self._diag.append("ambiguous IN9 SLE collection path; not opened")
                return False
            paths.add(path)
        return True

    def _pick(self, physical, infos):
        candidates = [d for d in infos
                      if d.get("vendor_id") == VID and d.get("product_id") in PIDS
                      and (d.get("usage_page"), d.get("usage")) == COLLECTION]
        if len(candidates) != 1:
            self._diag.append("missing or ambiguous passive input collection; not opened")
            return None
        info = candidates[0]
        pid = info["product_id"]
        if not matches_collection(info) or not _valid_parent(physical, pid):
            self._diag.append("IN9 SLE identity, interface or USB parent unproven; not opened")
            return None
        maximum = info.get("input_report_length")
        sizes = info.get("input_report_sizes")
        key = str(INPUT_ID)
        if (type(maximum) is not int or not REPORT_BYTES <= maximum <= 4096
                or not isinstance(sizes, dict) or not sizes
                or any(type(n) is not int or not 2 <= n <= maximum for n in sizes.values())
                or max(sizes.values()) != maximum
                or sizes.get(key) != REPORT_BYTES):
            self._diag.append("input report 06/64 unproven"
                              + report_proof_summary(info, "input", include_declared=True)
                              + "; not opened")
            return None
        return info

    def _listen(self, dev, info):
        deadline = time.monotonic() + READ_WINDOW_MS / 1000
        saw_zero = False
        read_len = info["input_report_length"]
        for _ in range(MAX_READS):
            remaining_ms = min(READ_WINDOW_MS, int((deadline - time.monotonic()) * 1000))
            if remaining_ms <= 0:
                break
            raw = dev.read(read_len, remaining_ms)
            if not raw:
                break
            if is_zero_placeholder(raw):
                saw_zero = True
                continue
            level = parse_battery(raw)
            if level is not None:
                return level
        if saw_zero:
            self._diag.append("zero heartbeat placeholder ignored; no new measurement")
        else:
            self._diag.append("opened; no valid battery notification"
                              + report_proof_summary(info, "input"))
        return None

    def _drain(self, dev, info):
        deadline = time.monotonic() + DRAIN_BUDGET_MS / 1000
        newest = None
        saw_zero = False
        read_len = info["input_report_length"]
        for _ in range(MAX_DRAIN):
            if time.monotonic() >= deadline:
                break
            raw = dev.read(read_len, DRAIN_TIMEOUT_MS)
            if not raw:
                break
            if is_zero_placeholder(raw):
                saw_zero = True
                continue
            level = parse_battery(raw)
            if level is not None:
                newest = level
        if newest is None:
            if saw_zero:
                self._diag.append("zero heartbeat placeholder ignored; no new measurement")
            else:
                self._diag.append("opened; no valid battery notification"
                                  + report_proof_summary(info, "input"))
        return newest

    def _release(self, physical):
        entry = self._handles.pop(physical, None)
        if entry is not None:
            dev = entry[1]
            try:
                dev.close()
            except OSError:
                self._diag.append("HID handle close failed")

    def close(self):
        for physical in list(self._handles):
            self._release(physical)

    def __del__(self):
        try:
            self.close()
        except Exception:
            pass

    @staticmethod
    def _historical(status):
        return replace(status, online=False, cached=True, wake_on_input=True,
                       connection_known=False, charging_known=False)

    def poll(self):
        self._diag = []
        if hid is None:
            self._diag.append("hidapi unavailable; nothing opened")
            return []
        try:
            inventory = hidlist.enumerate(VID)
        except OSError:
            self._diag.append("enumeration failed; retaining history")
            return [self._historical(s) for s in self._last.values()]
        inventory_safe = self._inventory_safe(inventory)
        groups = {}
        present_paths = set()
        for info in inventory:
            if info.get("vendor_id") != VID or info.get("product_id") not in PIDS:
                continue
            present_paths.add(info.get("path"))
            physical = info.get("physical_id", "")
            if not isinstance(physical, str) or not physical or physical.startswith("collection:"):
                self._diag.append("USB parent unproven; collection uniqueness unknown; not opened")
                continue
            groups.setdefault(physical, []).append(info)
        result = []
        found = set()
        for physical, infos in groups.items():
            self._paths[physical] = {d.get("path") for d in infos if d.get("path")}
            level = None
            info = self._pick(physical, infos) if inventory_safe else None
            if info is None:
                self._release(physical)
            else:
                identity = (info["path"], info["product_id"], info["input_report_length"],
                            tuple(sorted(info["input_report_sizes"].items())),
                            info.get("input_report_proof"))
                entry = self._handles.get(physical)
                if entry is not None and entry[0] != identity:
                    self._release(physical)
                    self._last.pop(physical, None)
                    entry = None
                try:
                    if entry is None:
                        dev = hid.device()
                        try:
                            dev.open_path(info["path"])
                        except (OSError, ValueError):
                            try:
                                dev.close()
                            except OSError:
                                pass
                            raise
                        self._handles[physical] = (identity, dev)
                        level = self._listen(dev, info)
                    else:
                        level = self._drain(entry[1], info)
                except (OSError, ValueError):
                    self._diag.append("input read failed; no new measurement"
                                      + report_proof_summary(info, "input"))
                    self._release(physical)
            if level is not None:
                received = datetime.now(timezone.utc).isoformat(
                    timespec="milliseconds").replace("+00:00", "Z")
                status = DeviceStatus(
                    f"{self.name}:{physical}", NAME, level, False, True,
                    self.name, kind=self.kind, physical_id=physical, connection_known=True,
                    charging_known=False, verification_level=self.verification_level,
                    measured_at_utc=received, queried_at_utc=received, unconfirmed=True)
                self._last[physical] = status
                found.add(physical)
                result.append(status)
        for physical in list(self._paths):
            if physical in found:
                continue
            if physical in groups or self._paths[physical] & present_paths:
                if physical in self._last:
                    result.append(self._historical(self._last[physical]))
            else:
                self._last.pop(physical, None)
                del self._paths[physical]
        for physical in [p for p in self._handles if p not in groups]:
            self._release(physical)
        return result

    def diagnostics(self):
        return list(self._diag)
