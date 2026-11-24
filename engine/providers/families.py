"""Architecture v2: one provider that runs every declarative device family.

Families are data (``engine/families``). This provider groups collections by
physical device, lets ``families.select`` pick the single proven collection per
family, applies the per-device read memory and the vendor-software stand-aside,
and turns replies into ``DeviceStatus`` rows (level C unless the family says
otherwise). It sends nothing to a collection no family selected.
"""
from __future__ import annotations

import time

import families
from families import dpi as dpi_framework, processes, runner
from families.memory import ReadMemory

from . import hidlist
from .base import DeviceStatus, Provider

try:
    import hid
except ImportError:          # pragma: no cover
    hid = None

NAME = "families"
DPI_MAX_FAILURES = 3   # consecutive failed DPI reads before DPI is left alone for this device
# Minimum interval between active queries for radios sensitive to rapid polling.
_ACTIVE_COOLDOWN_SECONDS = {
    ("razer_blackshark_v3_pro_rx", "battery-query"): 60.0,
}


class FamiliesProvider(Provider):
    name = NAME

    def __init__(self, memory=None, running=processes.running, clock=None):
        self._diag = []
        self.memory = memory or ReadMemory()
        self._running = running
        self._clock = clock
        self.candidate_count = 0
        self._dpi_failures = {}
        self._active_cooldown = {}

    def _inventory(self):
        rows = []
        for vid in families.vendor_ids():
            try:
                rows.extend(hidlist.enumerate(vid))
            except OSError:
                self._diag.append(f"enumeration failed for {vid:04x}: nothing sent")
        return rows

    def poll(self):
        self._diag = []
        self.candidate_count = 0
        if hid is None:
            return []
        groups = {}
        for info in self._inventory():
            physical = info.get("physical_id", "")
            if not physical or physical.startswith("collection:"):
                continue  # uniqueness on one device cannot be proven: send nothing
            groups.setdefault(physical, []).append(info)
        result = []
        for physical, infos in groups.items():
            chosen, notes = families.select(infos)
            self._diag.extend(notes)
            for family, device, info in chosen:
                self.candidate_count += 1
                status = self._read(family, device, info, physical)
                if status is not None:
                    result.append(status)
        return result

    def _read(self, family, device, info, physical):
        if family.stand_aside_for and self._running(family.stand_aside_for):
            self._diag.append(f"{family.id}: vendor software running; stepping aside")
            return None
        unit = families.memory_key(physical)
        reads = {r.name: r for r in family.reads}
        if family.id == "xiberia":
            # Keep the legacy read/index and memory keys. Only the exact listed
            # T10 gets the status trial first; a remembered legacy read still wins.
            status_read = reads.pop("t10_status", None)
            if status_read is not None and (
                    device.vendor_id, device.product_id, device.product_string
            ) == (0x36F9, 0xF001, "XIBERIA T10 Wireless"):
                reads = {status_read.name: status_read, **reads}
        plan = self.memory.plan(family.id, unit, list(reads))
        if not plan:
            self._diag.append(f"{family.id}: no read left to try for this device; nothing sent")
            return None
        now = self._clock() if self._clock else time.monotonic()
        if len(plan) == 1:
            cooldown = _ACTIVE_COOLDOWN_SECONDS.get((family.id, plan[0]))
            last_at = self._active_cooldown.get((family.id, unit, plan[0]))
            if cooldown is not None and last_at is not None and (now - last_at) < cooldown:
                self._diag.append(f"{family.id}.{plan[0]}: throttled (<{int(cooldown)}s)")
                return None
        dev = hid.device()
        try:
            dev.open_path(info["path"])
        except (OSError, ValueError):
            self._diag.append(f"{family.id}: open failed; nothing sent")
            return None
        try:
            for name in plan:
                cooldown = _ACTIVE_COOLDOWN_SECONDS.get((family.id, name))
                last_at = self._active_cooldown.get((family.id, unit, name))
                if cooldown is not None and last_at is not None and (now - last_at) < cooldown:
                    self._diag.append(f"{family.id}.{name}: throttled (<{int(cooldown)}s)")
                    return None
                if cooldown is not None:
                    # Arm cooldown immediately before the write so timeouts/errors/asleep
                    # all honor the minimum interval.
                    self._active_cooldown[(family.id, unit, name)] = now
                kwargs = {"clock": self._clock} if self._clock else {}
                outcome = runner.run(dev, family, reads[name], **kwargs)
                self.memory.record(family.id, unit, name, outcome.kind)
                self._diag.append(f"{family.id}.{name}: {outcome.kind}" + (f" ({outcome.note})" if outcome.note else ""))
                if outcome.kind == "reading":
                    dpi, dpi_target = self._read_dpi(dev, family, info, unit)
                    return DeviceStatus(
                        f"{NAME}:{family.id}:{physical}",
                        ((info.get("product_string") or "").strip() or device.name) if family.prefer_product_name else device.name, outcome.level, outcome.charging, True, NAME,
                        kind=family.kind, physical_id=physical, connection_known=True,
                        charging_known=outcome.charging_known, verification_level=family.level,
                        model_source="family:" + family.id, dpi=dpi, dpi_target=dpi_target)
                if outcome.kind in ("asleep", "anomaly"):
                    break
                if family.id == "xiberia" and name == "t10_status" and outcome.kind != "silent":
                    break  # Only silence permits the legacy early-firmware fallback.
            return None
        finally:
            try:
                dev.close()
            except OSError:
                self._diag.append("HID handle close failed")

    def _read_dpi(self, dev, family, info, unit):
        """DPI evidence after a battery reading; never affects the battery result.
        Writable (token + target) only through a reviewed adapter (families/dpi)."""
        key = (family.id, unit)
        if (family.dpi is None and dpi_framework.adapter_for(family) is None) or \
                self._dpi_failures.get(key, 0) >= DPI_MAX_FAILURES:
            return None, None
        view, target, note = dpi_framework.read(dev, family, info, self._clock)
        if view is None:
            self._dpi_failures[key] = self._dpi_failures.get(key, 0) + 1
            self._diag.append(f"{family.id}: {note}")
            return None, None
        self._dpi_failures.pop(key, None)
        return view, target

    def set_device_dpi(self, inventory, target, value, expected_dpi, target_token):
        return set_device_dpi(inventory, target, value, expected_dpi, target_token, self._clock, self._running)

    def diagnostics(self):
        return list(self._diag)


def set_device_dpi(inventory, target, value, expected_dpi, target_token, clock=None, running=processes.running):
    """Engine set_dpi entry: one adapter-guarded stage switch on one exact collection."""
    failure = {"dpi": None, "success": False, "outcomeUnknown": False, "confirmedDpi": None}
    family = families.builtin_families().get(target.get("family")) if isinstance(target, dict) else None
    if family is None or dpi_framework.adapter_for(family) is None:
        return dict(failure, error="dpi_invalid_target")
    if family.stand_aside_for and running(family.stand_aside_for):
        return dict(failure, error="dpi_vendor_software")   # never race the vendor tool
    wanted = {key: target.get(key) for key in dpi_framework.IDENTITY_KEYS}
    matches = [info for info in inventory if isinstance(info, dict)
               and dpi_framework.identity(info) == wanted
               and family.device_for(info.get("vendor_id"), info.get("product_id"), info.get("product_string"))
               and families.proven(family, info)]
    if len(matches) != 1 or hid is None:
        return dict(failure, error="dpi_target_changed")
    dev = hid.device()
    try:
        dev.open_path(matches[0]["path"])
    except (OSError, ValueError):
        return dict(failure, error="dpi_read_failed")
    try:
        return dpi_framework.select(dev, family, matches[0], target, value, expected_dpi, target_token, clock)
    finally:
        try:
            dev.close()
        except OSError:
            pass

