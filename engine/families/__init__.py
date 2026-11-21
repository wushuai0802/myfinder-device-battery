"""Registry of declarative device families (architecture v2).

Families ship as JSON in ``engine/families/builtin/`` (and, later, in the
signed device pack). This module loads and validates them, answers the
offline questions the rest of the engine asks (which IDs, which collections
need report proof) and selects the one collection a family may read on a
physical device. Importing it never opens a device.

Probe families (whole-vendor identity) stay inert unless ``set_probe_enabled``
was called, which the host does only for users who opted in to beta probing.
"""
from __future__ import annotations

import json
from pathlib import Path
from typing import Dict, List, Optional, Tuple

from .spec import Family, SpecError, parse_family

BUILTIN = Path(__file__).with_name("builtin")
_families: Optional[Dict[str, Family]] = None   # merged view: builtin + pack
_builtin: Optional[Dict[str, Family]] = None
_pack: Tuple[Family, ...] = ()
_probe_enabled = False


def load(directory: Path = BUILTIN) -> Dict[str, Family]:
    result = {}
    for path in sorted(directory.glob("*.json")):
        try:
            family = parse_family(json.loads(path.read_text(encoding="utf-8")))
        except (OSError, json.JSONDecodeError, SpecError) as error:
            raise SpecError(f"{path.name}: {error}") from None
        if path.stem != family.id:
            raise SpecError(f"{path.name}: file name must equal the family id")
        if family.id in result:
            raise SpecError(f"duplicate family {family.id}")
        result[family.id] = family
    owned = {}
    for family in result.values():
        for device in family.devices:
            key = (device.vendor_id, device.product_id, device.product_string)
            if key in owned:
                raise SpecError(f"{family.id}: device also claimed by {owned[key]}")
            owned[key] = family.id
    return result


def builtin_families() -> Dict[str, Family]:
    global _builtin
    if _builtin is None:
        _builtin = load()
    return _builtin


def families() -> Dict[str, Family]:
    global _families
    if _families is None:
        merged = dict(builtin_families())
        for family in _pack:
            merged.setdefault(family.id, family)   # a pack never overrides a builtin
        _families = merged
    return _families


def set_pack_families(pack_families) -> None:
    """Install the families of the current signed device pack (already validated
    by device_pack.parse_pack); an empty tuple removes them again."""
    global _pack, _families
    _pack = tuple(pack_families or ())
    _families = None


def builtin_listed_ids():
    return sorted((d.vendor_id, d.product_id, d.name, f.kind, f.id)
                  for f in builtin_families().values() for d in f.devices)


def memory_key(physical: str) -> str:
    """Stable per-device key for read memory; never the raw (private) device path."""
    import hashlib
    return "unit-" + hashlib.sha256(str(physical).encode("utf-8")).hexdigest()[:24]


def set_probe_enabled(enabled: bool):
    global _probe_enabled
    _probe_enabled = bool(enabled)


def probe_enabled() -> bool:
    return _probe_enabled


def active() -> List[Family]:
    return [f for f in families().values() if not f.probe or _probe_enabled]


def vendor_ids() -> Tuple[int, ...]:
    """Every vendor any family could read; the dispatcher's prefilter."""
    return tuple(sorted({vid for f in families().values() for vid in f.vendor_ids}))


def listed_ids():
    """(vid, pid, name, kind, family id) rows for exact listed devices; offline index."""
    return sorted((d.vendor_id, d.product_id, d.name, f.kind, f.id)
                  for f in families().values() for d in f.devices)


def wants_caps(vendor_id: int, product_id: int, usage_page: int, usage: int) -> bool:
    """Whether topology should compute per-report byte sizes for this collection."""
    for family in active():
        if (usage_page, usage) not in family.collections:
            continue
        if any((d.vendor_id, d.product_id) == (vendor_id, product_id) for d in family.devices):
            return True
        if family.probe and vendor_id in family.probe_vendor_ids:
            return True
    return False


def _proves(info: dict, direction: str, report) -> bool:
    if report is None:
        return True
    maximum = info.get(direction + "_report_length")
    sizes = info.get(direction + "_report_sizes")
    if type(maximum) is not int or maximum != report.length or not isinstance(sizes, dict):
        return False
    if sizes.get(str(report.report_id)) != report.length:
        return False
    return report.report_id != 0 or set(sizes) == {"0"}


def proven(family: Family, info: dict) -> bool:
    return (_proves(info, "input", family.input_report)
            and _proves(info, "output", family.output_report)
            and _proves(info, "feature", family.feature_report))


def select(infos: List[dict]):
    """Per physical device: [(family, device row, collection info)] that may be read.

    A family gets a collection only if exactly one collection on this physical
    device matches its identity and shape and proves its reports; ambiguity
    sends nothing. Returns also human-readable reasons for diagnostics.
    """
    chosen, notes = [], []
    for family in active():
        matches = []
        for info in infos:
            if (info.get("usage_page"), info.get("usage")) not in family.collections:
                continue
            if family.interface is not None and info.get("interface_number") != family.interface:
                continue
            device = family.device_for(info.get("vendor_id"), info.get("product_id"), info.get("product_string"))
            if device is None:
                continue
            matches.append((device, info))
        if not matches:
            continue
        proved = [(d, i) for d, i in matches if proven(family, i) and i.get("path")]
        if len(proved) != 1:
            notes.append(f"{family.id}: {len(matches)} candidate collection(s), {len(proved)} with report proof; nothing sent")
            continue
        chosen.append((family, proved[0][0], proved[0][1]))
    return chosen, notes
