"""Declarative device-family specifications (architecture v2).

A family describes which HID collections it may read and how, as data:

* identity  - exact VID/PID rows (``devices``), or, for a probe family, whole
              vendor IDs (``probeVendorIds``, only active when probing is enabled);
* shape     - candidate usage page/usage pairs and the exact report IDs and byte
              lengths the collection must prove (from the Windows HID caps);
* reads     - ordered read-only exchanges: what is sent and how a reply is parsed.

Parsing is strict: unknown fields, wrong types or out-of-range values reject the
whole family. Nothing here touches hardware. Interpretation is in ``runner.py``.

Safety contract (enforced here, not left to reviewers):
* only the transports in ``TRANSPORTS`` exist; there is no "before/after"
  session frame and no write-only transport;
* a request's first byte is the declared output/feature report ID and the request
  never exceeds the proven report length;
* every family records where its protocol facts came from and under which licence.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Optional, Tuple

SCHEMA = 1
TRANSPORTS = ("output", "feature", "featureGet", "listen")
KINDS = ("mouse", "keyboard", "headset", "gamepad", "other")
LEVELS = ("A", "B", "C")
MAX_REPORT = 4096
MAX_READS = 64
MAX_TIMEOUT_MS = 3000


class SpecError(ValueError):
    pass


def _check(condition, message):
    if not condition:
        raise SpecError(message)


def _fields(obj, required, optional=(), where="family"):
    _check(isinstance(obj, dict), f"{where}: object expected")
    keys = set(obj)
    missing = set(required) - keys
    extra = keys - set(required) - set(optional)
    _check(not missing, f"{where}: missing {sorted(missing)}")
    _check(not extra, f"{where}: unknown {sorted(extra)}")


def _word(value, where):
    _check(isinstance(value, str) and len(value) == 4, f"{where}: 4-digit hex expected")
    try:
        result = int(value, 16)
    except ValueError:
        raise SpecError(f"{where}: invalid hex") from None
    return result


def _byte(value, where):
    _check(isinstance(value, str) and len(value) == 2, f"{where}: 2-digit hex expected")
    try:
        return int(value, 16)
    except ValueError:
        raise SpecError(f"{where}: invalid hex") from None


def _hex_bytes(value, where):
    _check(isinstance(value, str), f"{where}: hex string expected")
    parts = value.split()
    _check(parts and all(len(p) == 2 for p in parts), f"{where}: space-separated bytes expected")
    try:
        return bytes(int(p, 16) for p in parts)
    except ValueError:
        raise SpecError(f"{where}: invalid hex") from None


def _int(value, lo, hi, where):
    _check(type(value) is int and lo <= value <= hi, f"{where}: integer {lo}..{hi} expected")
    return value


def _text(value, where, limit=400):
    _check(isinstance(value, str) and value.strip() and len(value) <= limit, f"{where}: text expected")
    return value.strip()


@dataclass(frozen=True)
class Device:
    vendor_id: int
    product_id: int
    name: str
    product_string: Optional[str] = None  # exact, case-insensitive, trimmed


@dataclass(frozen=True)
class Report:
    report_id: int
    length: int        # bytes including the report ID byte, as Windows/hidapi count them


@dataclass(frozen=True)
class ByteRule:
    at: int
    equals: int
    mask: int = 0xFF


@dataclass(frozen=True)
class Reply:
    length: Optional[int]
    match: Tuple[ByteRule, ...]
    percent_at: int
    percent_min: int
    percent_max: int
    percent_mask: int
    charging: Optional[ByteRule]
    asleep: Optional[ByteRule]


@dataclass(frozen=True)
class Read:
    name: str
    transport: str
    request: bytes
    timeout_ms: int
    max_reads: int
    reply: Reply


@dataclass(frozen=True)
class Family:
    id: str
    name: str
    kind: str
    level: str
    probe: bool
    devices: Tuple[Device, ...]
    probe_vendor_ids: Tuple[int, ...]
    collections: Tuple[Tuple[int, int], ...]
    input_report: Optional[Report]
    output_report: Optional[Report]
    feature_report: Optional[Report]
    reads: Tuple[Read, ...]
    stand_aside_for: Tuple[str, ...]
    source: dict = field(compare=False, hash=False)

    @property
    def vendor_ids(self):
        return tuple(sorted({d.vendor_id for d in self.devices} | set(self.probe_vendor_ids)))

    def device_for(self, vendor_id, product_id, product_string):
        """The listed device this identity is, or None. Probe vendors return a generic row."""
        for device in self.devices:
            if (device.vendor_id, device.product_id) != (vendor_id, product_id):
                continue
            if device.product_string is not None:
                actual = (product_string or "").strip().casefold()
                if actual != device.product_string.casefold():
                    continue
            return device
        if self.probe and vendor_id in self.probe_vendor_ids:
            return Device(vendor_id, product_id, (product_string or "").strip() or self.name)
        return None


def _report(obj, where):
    if obj is None:
        return None
    _fields(obj, ("id", "length"), where=where)
    return Report(_byte(obj["id"], where + ".id"), _int(obj["length"], 2, MAX_REPORT, where + ".length"))


def _rule(obj, where):
    _fields(obj, ("at", "eq"), ("mask",), where)
    mask = _byte(obj["mask"], where + ".mask") if "mask" in obj else 0xFF
    equals = _byte(obj["eq"], where + ".eq")
    _check(equals & ~mask == 0, f"{where}: eq has bits outside mask")
    return ByteRule(_int(obj["at"], 0, MAX_REPORT - 1, where + ".at"), equals, mask)


def _reply(obj, where):
    _fields(obj, ("match", "percent"), ("length", "charging", "asleep"), where)
    match = obj["match"]
    _check(isinstance(match, list) and 1 <= len(match) <= 16, f"{where}.match: 1..16 rules expected")
    rules = tuple(_rule(m, f"{where}.match[{i}]") for i, m in enumerate(match))
    percent = obj["percent"]
    _fields(percent, ("at", "min", "max"), ("mask",), where + ".percent")
    lo = _int(percent["min"], 0, 100, where + ".percent.min")
    hi = _int(percent["max"], lo, 100, where + ".percent.max")
    return Reply(
        length=_int(obj["length"], 2, MAX_REPORT, where + ".length") if "length" in obj else None,
        match=rules,
        percent_at=_int(percent["at"], 0, MAX_REPORT - 1, where + ".percent.at"),
        percent_min=lo, percent_max=hi,
        percent_mask=_byte(percent["mask"], where + ".percent.mask") if "mask" in percent else 0xFF,
        charging=_rule(obj["charging"], where + ".charging") if "charging" in obj else None,
        asleep=_rule(obj["asleep"], where + ".asleep") if "asleep" in obj else None)


def _read(obj, where, reports):
    _fields(obj, ("name", "transport", "reply"), ("request", "timeoutMs", "maxReads"), where)
    transport = obj["transport"]
    _check(transport in TRANSPORTS, f"{where}.transport: one of {TRANSPORTS}")
    request = _hex_bytes(obj["request"], where + ".request") if "request" in obj else b""
    if transport == "listen":
        _check(not request, f"{where}: listen sends nothing")
        _check(reports["input"] is not None, f"{where}: listen needs an input report")
    elif transport == "output":
        out = reports["output"]
        _check(out is not None and reports["input"] is not None, f"{where}: output needs input and output reports")
        _check(request and request[0] == out.report_id and len(request) <= out.length,
               f"{where}: request must start with the output report ID and fit its length")
    else:
        feat = reports["feature"]
        _check(feat is not None, f"{where}: feature transports need a feature report")
        _check(request and request[0] == feat.report_id and len(request) <= feat.length,
               f"{where}: request must start with the feature report ID and fit its length")
        if transport == "featureGet":
            _check(len(request) == 1, f"{where}: featureGet request is only the report ID")
    return Read(
        name=_text(obj["name"], where + ".name", 64),
        transport=transport, request=request,
        timeout_ms=_int(obj.get("timeoutMs", 1000), 50, MAX_TIMEOUT_MS, where + ".timeoutMs"),
        max_reads=_int(obj.get("maxReads", 32), 1, MAX_READS, where + ".maxReads"),
        reply=_reply(obj["reply"], where + ".reply"))


def parse_family(document):
    where = "family"
    _fields(document, ("schema", "id", "name", "kind", "verificationLevel", "source", "collections", "reports", "reads"),
            ("devices", "probeVendorIds", "standAsideFor"), where)
    _check(document["schema"] == SCHEMA, "family: unsupported schema")
    family_id = _text(document["id"], "family.id", 48)
    _check(family_id.replace("_", "").isalnum() and family_id.islower(), "family.id: lowercase [a-z0-9_]")
    kind = document["kind"]
    _check(kind in KINDS, f"family.kind: one of {KINDS}")
    level = document["verificationLevel"]
    _check(level in LEVELS, "family.verificationLevel: A/B/C")
    source = document["source"]
    _fields(source, ("project", "url", "revision", "license", "evidence"), ("file", "origin"), "family.source")
    for key in ("project", "url", "revision", "license", "evidence"):
        _text(source[key], f"family.source.{key}", 600)

    devices = []
    for i, row in enumerate(document.get("devices", [])):
        w = f"family.devices[{i}]"
        _fields(row, ("vendorId", "productId", "name"), ("productString",), w)
        devices.append(Device(_word(row["vendorId"], w + ".vendorId"), _word(row["productId"], w + ".productId"),
                              _text(row["name"], w + ".name", 80),
                              _text(row["productString"], w + ".productString", 126) if "productString" in row else None))
    probe_vendors = tuple(_word(v, f"family.probeVendorIds[{i}]")
                          for i, v in enumerate(document.get("probeVendorIds", [])))
    _check(devices or probe_vendors, "family: devices or probeVendorIds required")
    _check(len({(d.vendor_id, d.product_id, d.product_string) for d in devices}) == len(devices), "family: duplicate device")

    collections = []
    for i, row in enumerate(document["collections"]):
        w = f"family.collections[{i}]"
        _fields(row, ("usagePage", "usage"), where=w)
        page = _word(row["usagePage"], w + ".usagePage")
        _check(page >= 0xFF00, f"{w}: only vendor-defined usage pages")
        collections.append((page, _word(row["usage"], w + ".usage")))
    _check(1 <= len(collections) <= 8, "family.collections: 1..8 expected")

    reports = document["reports"]
    _fields(reports, (), ("input", "output", "feature"), "family.reports")
    parsed_reports = {k: _report(reports.get(k), f"family.reports.{k}") for k in ("input", "output", "feature")}

    reads = document["reads"]
    _check(isinstance(reads, list) and 1 <= len(reads) <= 4, "family.reads: 1..4 expected")
    parsed_reads = tuple(_read(r, f"family.reads[{i}]", parsed_reports) for i, r in enumerate(reads))
    _check(len({r.name for r in parsed_reads}) == len(parsed_reads), "family.reads: duplicate name")

    stand = tuple(_text(p, f"family.standAsideFor[{i}]", 64).lower()
                  for i, p in enumerate(document.get("standAsideFor", [])))
    _check(all(p.endswith(".exe") and "\\" not in p and "/" not in p for p in stand),
           "family.standAsideFor: bare .exe names")

    return Family(
        id=family_id, name=_text(document["name"], "family.name", 80), kind=kind, level=level,
        probe=bool(probe_vendors), devices=tuple(devices), probe_vendor_ids=probe_vendors,
        collections=tuple(collections), input_report=parsed_reports["input"],
        output_report=parsed_reports["output"], feature_report=parsed_reports["feature"],
        reads=parsed_reads, stand_aside_for=stand, source=dict(source))
