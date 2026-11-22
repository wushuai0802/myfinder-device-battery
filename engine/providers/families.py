"""Architecture v2: one provider that runs every declarative device family.

Families are data (``engine/families``). This provider groups collections by
physical device, lets ``families.select`` pick the single proven collection per
family, applies the per-device read memory and the vendor-software stand-aside,
and turns replies into ``DeviceStatus`` rows (level C unless the family says
otherwise). It sends nothing to a collection no family selected.
"""
from __future__ import annotations

from datetime import datetime, timezone

import families
from families import processes, runner
from families.memory import ReadMemory

from . import hidlist
from .base import DeviceStatus, Provider

try:
    import hid
except ImportError:          # pragma: no cover
    hid = None

NAME = "families"
DPI_MAX_FAILURES = 3   # consecutive failed DPI reads before DPI is left alone for this device


class FamiliesProvider(Provider):
    name = NAME

    def __init__(self, memory=None, running=processes.running, clock=None):
        self._diag = []
        self.memory = memory or ReadMemory()
        self._running = running
        self._clock = clock
        self.candidate_count = 0
        self._dpi_failures = {}

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
        plan = self.memory.plan(family.id, unit, [r.name for r in family.reads])
        if not plan:
            self._diag.append(f"{family.id}: no read left to try for this device; nothing sent")
            return None
        reads = {r.name: r for r in family.reads}
        dev = hid.device()
        try:
            dev.open_path(info["path"])
        except (OSError, ValueError):
            self._diag.append(f"{family.id}: open failed; nothing sent")
            return None
        try:
            for name in plan:
                kwargs = {"clock": self._clock} if self._clock else {}
                outcome = runner.run(dev, family, reads[name], **kwargs)
                self.memory.record(family.id, unit, name, outcome.kind)
                self._diag.append(f"{family.id}.{name}: {outcome.kind}" + (f" ({outcome.note})" if outcome.note else ""))
                if outcome.kind == "reading":
                    dpi = self._read_dpi(dev, family, unit, kwargs)
                    return DeviceStatus(
                        f"{NAME}:{family.id}:{physical}",
                        ((info.get("product_string") or "").strip() or device.name) if family.prefer_product_name else device.name, outcome.level, outcome.charging, True, NAME,
                        kind=family.kind, physical_id=physical, connection_known=True,
                        charging_known=outcome.charging_known, verification_level=family.level,
                        model_source="family:" + family.id, dpi=dpi)
                if outcome.kind in ("asleep", "anomaly"):
                    break
            return None
        finally:
            try:
                dev.close()
            except OSError:
                self._diag.append("HID handle close failed")

    def _read_dpi(self, dev, family, unit, kwargs):
        """Read-only DPI evidence after a battery reading; never affects the battery result."""
        key = (family.id, unit)
        if family.dpi is None or self._dpi_failures.get(key, 0) >= DPI_MAX_FAILURES:
            return None
        value, note = runner.run_dpi(dev, family, **kwargs)
        if value is None:
            self._dpi_failures[key] = self._dpi_failures.get(key, 0) + 1
            self._diag.append(f"{family.id}: {note}")
            return None
        self._dpi_failures.pop(key, None)
        # canSet stays False here: writes exist only through a reviewed adapter (C4).
        return dict(value, supported=True, canSet=False, targetToken=None,
                    checkedAtUtc=datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"))

    def diagnostics(self):
        return list(self._diag)
