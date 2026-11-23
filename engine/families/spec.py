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
class Span:
    """bytes[at:at+count] all within lo..hi (e.g. printable ASCII device IDs)."""
    at: int
    count: int
    lo: int
    hi: int


@dataclass(frozen=True)
class Checksum:
    """(sum(bytes[start:end]) + add) & 0xFF == equals."""
    start: int
    end: int
    equals: int


@dataclass(frozen=True)
class Variant:
    """One field layout of a reply, chosen by ``when`` (all rules must hold)."""
    when: Tuple[ByteRule, ...]
    percent_at: int
    percent_min: int
    percent_max: int
    percent_mask: int
    charging: Optional[ByteRule]
    charging_always: bool
    asleep: Tuple[ByteRule, ...]
    require: Tuple[ByteRule, ...] = ()   # must hold for a reading (checked after asleep)


@dataclass(frozen=True)
class Reply:
    length: Optional[int]
    match: Tuple[ByteRule, ...]
    spans: Tuple[Span, ...]
    checksum: Optional[Checksum]
    asleep: Tuple[ByteRule, ...]      # checked after match/spans/checksum, before variants
    variants: Tuple[Variant, ...]

    # Single-layout convenience (the first variant), used by older callers and tests.
    @property
    def percent_at(self):
        return self.variants[0].percent_at


@dataclass(frozen=True)
class Gate:
    """A read-only question sent first: online -> go on, offline -> asleep, else nothing more is sent."""
    request: bytes
    match: Tuple[ByteRule, ...]
    checksum: Optional[Checksum]
    online: ByteRule
    offline: Optional[ByteRule]
    length: Optional[int] = None


@dataclass(frozen=True)
class Read:
    name: str
    transport: str
    request: bytes
    timeout_ms: int
    max_reads: int
    reply: Reply
    gate: Optional[Gate] = None


@dataclass(frozen=True)
class DpiExchange:
    """One read-only DPI question; same transports and envelope rules as a battery read.
    Attribute names match ``Read`` so the runner's exchange code is shared."""
    name: str
    transport: str
    request: bytes
    timeout_ms: int
    max_reads: int
    length: Optional[int]
    match: Tuple[ByteRule, ...]
    spans: Tuple[Span, ...]
    checksum: Optional[Checksum]


@dataclass(frozen=True)
class DpiValue:
    """An unsigned field: (bytes[at..at+width) in ``endian`` & mask) * scale."""
    exchange: str
    at: int
    width: int
    big_endian: bool
    mask: int
    scale: int


@dataclass(frozen=True)
class DpiSpec:
    exchanges: Tuple[DpiExchange, ...]
    stage_count: Optional[DpiValue]   # None: fixed_count
    fixed_count: int
    stage_first: DpiValue             # value of stage 0; stage i is at first.at + i * stride
    stride: int
    current: Optional[DpiValue]       # exactly one of current / current_stage
    current_stage: Optional[DpiValue]
    stage_base: int                   # 0 or 1: index of the first stage as the device numbers it
    stage_source: str                 # "onboard" (device stage table) or "quick" (preset list)


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
    interface: Optional[int] = None
    prefer_product_name: bool = False
    dpi: Optional[DpiSpec] = None

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


def _rules(value, where, lo=0, hi=16):
    if isinstance(value, dict):
        value = [value]
    _check(isinstance(value, list) and lo <= len(value) <= hi, f"{where}: {lo}..{hi} rules expected")
    return tuple(_rule(m, f"{where}[{i}]") for i, m in enumerate(value))


def _checksum(obj, where):
    if obj is None:
        return None
    _fields(obj, ("sum", "equals"), where=where)
    span = obj["sum"]
    _check(isinstance(span, list) and len(span) == 2, f"{where}.sum: [start, end] expected")
    start = _int(span[0], 0, MAX_REPORT - 1, where + ".sum[0]")
    end = _int(span[1], start + 1, MAX_REPORT, where + ".sum[1]")
    return Checksum(start, end, _byte(obj["equals"], where + ".equals"))


def _variant(obj, where):
    _fields(obj, ("percent",), ("when", "charging", "chargingAlways", "asleep", "require"), where)
    percent = obj["percent"]
    _fields(percent, ("at", "min", "max"), ("mask",), where + ".percent")
    lo = _int(percent["min"], 0, 100, where + ".percent.min")
    hi = _int(percent["max"], lo, 100, where + ".percent.max")
    always = obj.get("chargingAlways", False)
    _check(always in (True, False), where + ".chargingAlways: boolean")
    _check(not (always and "charging" in obj), where + ": charging and chargingAlways are exclusive")
    return Variant(
        when=_rules(obj.get("when", []), where + ".when", 0, 8),
        percent_at=_int(percent["at"], 0, MAX_REPORT - 1, where + ".percent.at"),
        percent_min=lo, percent_max=hi,
        percent_mask=_byte(percent["mask"], where + ".percent.mask") if "mask" in percent else 0xFF,
        charging=_rule(obj["charging"], where + ".charging") if "charging" in obj else None,
        charging_always=always,
        asleep=_rules(obj.get("asleep", []), where + ".asleep", 0, 4),
        require=_rules(obj.get("require", []), where + ".require", 0, 8))


def _reply(obj, where):
    _fields(obj, ("match",), ("length", "percent", "charging", "chargingAlways", "asleep", "spans", "checksum", "variants"), where)
    rules = _rules(obj["match"], where + ".match", 1, 16)
    spans = []
    for i, span in enumerate(obj.get("spans", [])):
        w = f"{where}.spans[{i}]"
        _fields(span, ("at", "count", "min", "max"), where=w)
        lo, hi = _byte(span["min"], w + ".min"), _byte(span["max"], w + ".max")
        _check(lo <= hi, w + ": min > max")
        spans.append(Span(_int(span["at"], 0, MAX_REPORT - 1, w + ".at"), _int(span["count"], 1, 64, w + ".count"), lo, hi))
    if "variants" in obj:
        _check(not any(k in obj for k in ("percent", "charging", "chargingAlways")), where + ": percent belongs in variants")
        variants = obj["variants"]
        _check(isinstance(variants, list) and 1 <= len(variants) <= 4, where + ".variants: 1..4 expected")
        parsed = tuple(_variant(v, f"{where}.variants[{i}]") for i, v in enumerate(variants))
    else:
        _check("percent" in obj, where + ": percent or variants required")
        parsed = (_variant({k: obj[k] for k in ("percent", "charging", "chargingAlways") if k in obj}, where),)
    return Reply(
        length=_int(obj["length"], 2, MAX_REPORT, where + ".length") if "length" in obj else None,
        match=rules, spans=tuple(spans), checksum=_checksum(obj.get("checksum"), where + ".checksum"),
        asleep=_rules(obj.get("asleep", []), where + ".asleep", 0, 4), variants=parsed)


def _gate(obj, where, transport, reports):
    _fields(obj, ("request", "match", "online"), ("checksum", "offline", "length"), where)
    request = _hex_bytes(obj["request"], where + ".request")
    report = reports["output"] if transport == "output" else reports["feature"]
    _check(request[0] == report.report_id and len(request) <= report.length,
           f"{where}: request must start with the report ID and fit its length")
    gate = Gate(request=request, match=_rules(obj["match"], where + ".match", 1, 16),
                checksum=_checksum(obj.get("checksum"), where + ".checksum"),
                online=_rule(obj["online"], where + ".online"),
                offline=_rule(obj["offline"], where + ".offline") if "offline" in obj else None,
                length=_int(obj["length"], 2, MAX_REPORT, where + ".length") if "length" in obj else None)
    if gate.length is not None:
        incoming = reports["input"] if transport == "output" else report
        maximum = incoming.length - (transport == "output" and incoming.report_id == 0)
        _check(gate.length <= maximum, f"{where}.length: exceeds reply report length")
        rules = gate.match + (gate.online,) + ((gate.offline,) if gate.offline is not None else ())
        _check(all(rule.at < gate.length for rule in rules), f"{where}: rule outside reply length")
        _check(gate.checksum is None or gate.checksum.end <= gate.length,
               f"{where}: checksum outside reply length")
    return gate


def _read(obj, where, reports):
    _fields(obj, ("name", "transport", "reply"), ("request", "timeoutMs", "maxReads", "gate"), where)
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
    gate = None
    if "gate" in obj:
        _check(transport in ("output", "feature"), f"{where}.gate: only for output or feature reads")
        gate = _gate(obj["gate"], where + ".gate", transport, reports)
    return Read(
        name=_text(obj["name"], where + ".name", 64),
        transport=transport, request=request,
        timeout_ms=_int(obj.get("timeoutMs", 1000), 50, MAX_TIMEOUT_MS, where + ".timeoutMs"),
        max_reads=_int(obj.get("maxReads", 32), 1, MAX_READS, where + ".maxReads"),
        reply=_reply(obj["reply"], where + ".reply"), gate=gate)


MAX_DPI_STAGES = 8


def _dpi_value(obj, where, names, wide=True, scaled=True):
    optional = ("width", "endian", "mask", "scale") if wide else ("mask",)
    _fields(obj, ("exchange", "at"), optional if scaled else ("mask",), where)
    _check(obj["exchange"] in names, f"{where}.exchange: one of {sorted(names)}")
    width = _int(obj.get("width", 1), 1, 2, where + ".width")
    endian = obj.get("endian", "little")
    _check(endian in ("little", "big"), where + ".endian: little or big")
    full = (1 << (8 * width)) - 1
    mask = obj.get("mask", f"{full:0{2 * width}X}")
    _check(isinstance(mask, str) and len(mask) == 2 * width and all(c in "0123456789abcdefABCDEF" for c in mask)
           and int(mask, 16) > 0, f"{where}.mask: {2 * width}-digit non-zero hex expected")
    return DpiValue(obj["exchange"], _int(obj["at"], 0, MAX_REPORT - width, where + ".at"), width,
                    endian == "big", int(mask, 16),
                    _int(obj.get("scale", 1), 1, 100, where + ".scale"))


def _dpi(obj, where, reports):
    """Read-only DPI evidence. Writes never live in data (see families/dpi/)."""
    _fields(obj, ("exchanges", "stages"), ("current", "currentStage", "stageSource"), where)
    raw = obj["exchanges"]
    _check(isinstance(raw, list) and 1 <= len(raw) <= 3, where + ".exchanges: 1..3 expected")
    exchanges = []
    for i, item in enumerate(raw):
        w = f"{where}.exchanges[{i}]"
        _fields(item, ("name", "transport", "request", "reply"), ("timeoutMs", "maxReads"), w)
        _check(item["transport"] in ("output", "feature", "featureGet"), w + ".transport: output, feature or featureGet")
        # Reuse the battery read validation for transport, request and report proof.
        probe = _read({"name": item["name"], "transport": item["transport"], "request": item["request"],
                       "reply": {"match": item["reply"].get("match") if isinstance(item["reply"], dict) else None,
                                 "percent": {"at": 0, "min": 0, "max": 100}},
                       **{k: item[k] for k in ("timeoutMs", "maxReads") if k in item}}, w, reports)
        reply = item["reply"]
        _fields(reply, ("match",), ("length", "spans", "checksum"), w + ".reply")
        envelope = _reply(dict(reply, percent={"at": 0, "min": 0, "max": 100}), w + ".reply")
        exchanges.append(DpiExchange(probe.name, probe.transport, probe.request, probe.timeout_ms, probe.max_reads,
                                     envelope.length, envelope.match, envelope.spans, envelope.checksum))
    names = {e.name for e in exchanges}
    _check(len(names) == len(exchanges), where + ".exchanges: duplicate name")

    stages = obj["stages"]
    _fields(stages, ("exchange", "count", "first", "stride"), ("width", "endian", "mask", "scale"), where + ".stages")
    first = _dpi_value({k: stages[k] for k in ("exchange", "width", "endian", "mask", "scale") if k in stages}
                       | {"at": stages["first"]}, where + ".stages", names)
    stride = _int(stages["stride"], first.width, 16, where + ".stages.stride")
    count = stages["count"]
    if isinstance(count, dict):
        _fields(count, ("at",), ("mask",), where + ".stages.count")
        stage_count, fixed = _dpi_value(dict(count, exchange=stages["exchange"]), where + ".stages.count", names, wide=False), 0
    else:
        stage_count, fixed = None, _int(count, 1, MAX_DPI_STAGES, where + ".stages.count")
    _check(first.at + stride * (MAX_DPI_STAGES - 1 if stage_count else fixed - 1) + first.width <= MAX_REPORT,
           where + ".stages: table exceeds the report")

    _check(("current" in obj) != ("currentStage" in obj), where + ": exactly one of current or currentStage")
    current = _dpi_value(obj["current"], where + ".current", names) if "current" in obj else None
    current_stage, base = None, 0
    if "currentStage" in obj:
        item = obj["currentStage"]
        _fields(item, ("exchange", "at"), ("mask", "base"), where + ".currentStage")
        base = _int(item.get("base", 0), 0, 1, where + ".currentStage.base")
        current_stage = _dpi_value({k: item[k] for k in ("exchange", "at", "mask") if k in item},
                                   where + ".currentStage", names, wide=False)
    source = obj.get("stageSource", "onboard")
    _check(source in ("onboard", "quick"), where + ".stageSource: onboard or quick")
    return DpiSpec(tuple(exchanges), stage_count, fixed, first, stride, current, current_stage, base, source)


def parse_family(document):
    where = "family"
    _fields(document, ("schema", "id", "name", "kind", "verificationLevel", "source", "collections", "reports", "reads"),
            ("devices", "probeVendorIds", "standAsideFor", "interface", "preferProductName", "dpi"), where)
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

    _check("dpi" not in document or kind == "mouse", "family.dpi: only mouse families")
    stand = tuple(_text(p, f"family.standAsideFor[{i}]", 64).lower()
                  for i, p in enumerate(document.get("standAsideFor", [])))
    _check(all(p.endswith(".exe") and "\\" not in p and "/" not in p for p in stand),
           "family.standAsideFor: bare .exe names")

    return Family(
        id=family_id, name=_text(document["name"], "family.name", 80), kind=kind, level=level,
        probe=bool(probe_vendors), devices=tuple(devices), probe_vendor_ids=probe_vendors,
        collections=tuple(collections), input_report=parsed_reports["input"],
        output_report=parsed_reports["output"], feature_report=parsed_reports["feature"],
        reads=parsed_reads, stand_aside_for=stand, source=dict(source),
        interface=_int(document["interface"], 0, 255, "family.interface") if "interface" in document else None,
        prefer_product_name=document.get("preferProductName") is True,
        dpi=_dpi(document["dpi"], "family.dpi", parsed_reports) if "dpi" in document else None)
