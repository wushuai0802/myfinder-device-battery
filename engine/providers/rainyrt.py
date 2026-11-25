"""WOB Rainy75 V2 (36B0:3071 2.4G receiver, 36B0:3075 USB): passive reader.

Only the listed identities on FF60:0061 are opened, at verification grade C.
The receiver pushes a BE BB battery frame about every five seconds on the
unnumbered 256-byte input report (257 bytes with the Windows report-ID slot);
nothing is ever written to the device.

Codec and field offsets come from the official WOB HUB bundle
(`rainy75v2-wireless-test.html`, #943): frames start with `BE BB`, length code
`00`/`06`/`0A`/`0E` (where `00` means 6 bytes) ending with `ED`, byte 3 is the
percentage `0..100`, and byte 4 is the charge state (`0` idle, `1` full,
`2` charging).

The collection handle stays open between polls so Windows queues notifications
for this handle; the first poll after opening listens briefly (`READ_WINDOW_MS`)
and later polls drain already-queued frames (`DRAIN_TIMEOUT_MS`) and keep the
newest valid reading. Silence retains history without marking a fresh reading.
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

VID = 0x36B0
PIDS = {
    0x3071: "WOB Rainy75 V2（2.4G 接收器）",
    0x3075: "WOB Rainy75 V2（USB）",
}
COLLECTION = (0xFF60, 0x0061)
REPORT_LENGTH = 257        # Windows HidP length including implicit report-ID 0 slot
PAYLOAD_LENGTH = 256       # hidapi input buffer without implicit ID 0
FRAME_END = 0xED
REPLY_LENGTHS = (6, 10, 14)
READ_WINDOW_MS = 1500      # bounded initial listen; never approaches child timeout
MAX_READS = 32
DRAIN_TIMEOUT_MS = 1       # drain reports already queued by Windows on the open handle
DRAIN_BUDGET_MS = 500      # hard monotonic deadline even on coarse OS timer ticks
MAX_DRAIN = 256


def _valid_parent(physical, pid):
    """Require an exact Windows USB parent matching VID 36B0 and the candidate PID."""
    if not isinstance(physical, str) or not physical or physical.startswith("collection:"):
        return False
    expected = f"{pid:04x}"
    return re.fullmatch(
        r"pnp:usb\\vid_36b0&pid_" + expected + r"(?:&rev_[0-9a-f]{4})?\\[^\\\s]+",
        physical,
        re.I,
    ) is not None


def _valid_path(path, pid):
    """Require a non-empty HID path consistent with VID 36B0 and the candidate PID."""
    if isinstance(path, bytes):
        try:
            text = path.decode("utf-8")
        except UnicodeDecodeError:
            return False
    elif isinstance(path, str):
        text = path
    else:
        return False
    if not text:
        return False
    expected = f"{pid:04x}"
    return re.match(
        r"\\\\\?\\hid#vid_36b0&pid_" + expected + r"(?:&|#)",
        text,
        re.I,
    ) is not None


def parse_battery(raw):
    """Parse a 256-byte unnumbered Rainy75 V2 input payload, or return None."""
    if not isinstance(raw, (bytes, bytearray, list, tuple)) or len(raw) != PAYLOAD_LENGTH:
        return None
    if not isinstance(raw, (bytes, bytearray)):
        if any(type(b) is not int or not 0 <= b <= 255 for b in raw):
            return None
    if raw[0] != 0xBE or raw[1] != 0xBB:
        return None
    length = 6 if raw[2] == 0 else raw[2]
    if length not in REPLY_LENGTHS or raw[length - 1] != FRAME_END:
        return None
    level, charging_state = raw[3], raw[4]
    if not 0 <= level <= 100 or charging_state not in (0, 1, 2):
        return None
    return level, charging_state == 2


class RainyRTProvider(Provider):
    name = "rainyrt"
    kind = "keyboard"
    verification_level = "C"
    charging_known = True
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
                self._diag.append("Rainy75 V2 USB parent or path unproven; not opened")
                return False
            if path in paths:
                self._diag.append("ambiguous Rainy75 V2 collection path; not opened")
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
        if not _valid_parent(physical, pid) or not _valid_path(info.get("path"), pid):
            self._diag.append("Rainy75 V2 USB parent or path unproven; not opened")
            return None
        maximum = info.get("input_report_length")
        sizes = info.get("input_report_sizes")
        if (type(maximum) is not int or maximum != REPORT_LENGTH
                or not isinstance(sizes, dict) or sizes != {"0": REPORT_LENGTH}):
            self._diag.append("input report 00/257 unproven"
                              + report_proof_summary(info, "input", include_declared=True)
                              + "; not opened")
            return None
        return info

    def _listen(self, dev, info):
        """Bounded initial listen immediately after opening a collection handle."""
        deadline = time.monotonic() + READ_WINDOW_MS / 1000
        for _ in range(MAX_READS):
            remaining_ms = min(READ_WINDOW_MS, int((deadline - time.monotonic()) * 1000))
            if remaining_ms <= 0:
                break
            raw = dev.read(PAYLOAD_LENGTH, remaining_ms)
            if not raw:
                break
            parsed = parse_battery(raw)
            if parsed is not None:
                return parsed
        self._diag.append("opened; no valid battery notification"
                          + report_proof_summary(info, "input"))
        return None

    def _drain(self, dev, info):
        """Drain queued input reports on an already-open handle; keep newest valid."""
        deadline = time.monotonic() + DRAIN_BUDGET_MS / 1000
        newest = None
        for _ in range(MAX_DRAIN):
            if time.monotonic() >= deadline:
                break
            raw = dev.read(PAYLOAD_LENGTH, DRAIN_TIMEOUT_MS)
            if not raw:
                break
            parsed = parse_battery(raw)
            if parsed is not None:
                newest = parsed
        if newest is None:
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
            reading = None
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
                        reading = self._listen(dev, info)
                    else:
                        reading = self._drain(entry[1], info)
                except (OSError, ValueError):
                    self._diag.append("input read failed; no new measurement"
                                      + report_proof_summary(info, "input"))
                    self._release(physical)
            if reading is not None:
                level, charging = reading
                received = datetime.now(timezone.utc).isoformat(
                    timespec="milliseconds").replace("+00:00", "Z")
                status = DeviceStatus(
                    f"{self.name}:{physical}", self.device_name(info), level, charging, True,
                    self.name, kind=self.kind, physical_id=physical, connection_known=True,
                    charging_known=True, verification_level=self.verification_level,
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

    def device_name(self, info):
        product = info.get("product_string")
        if isinstance(product, str):
            cleaned = re.sub(r"[\x00-\x1f\x7f]+", " ", product).strip()[:64]
            if cleaned:
                return cleaned
        return PIDS.get(info.get("product_id"), "WOB Rainy75 V2")
