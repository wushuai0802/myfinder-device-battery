"""Strict, immutable device-pack snapshot; signature verification is host-owned.

The bundled schema is the authoritative structural contract. This deliberately
small validator implements exactly its keywords without a runtime dependency.
All validation precedes semantic row selection, so a malformed rejected row
cannot hide a whole-pack error. No pack ever changes protocol parameters of an
existing reader; loader 2 may add declarative families (architecture v2), each
validated by the same parser and safety rules as the builtin family files.
"""
from __future__ import annotations

from collections import Counter
from dataclasses import dataclass
from datetime import datetime
from functools import lru_cache
import json
from pathlib import Path
import re

from providers.profiles import builtin_ids, profiles

MAX_BYTES = 256 * 1024
MAX_ENTRIES = 2000
MAX_FAMILIES = 64
BRAND_PROVIDERS = frozenset((
    "families",
    "atk_headset",
    "razer", "audeze", "wlmouse", "mchose", "mchose_headset", "hyperx_alpha2", "hyperx_cloud3",
    "mchose_qhw_keyboard", "mchose_glw_keyboard",
    "mchose_by_keyboard",
    "moondrop",
    "sony_inzone", "mchose_k99v3", "mchose_v9pro2",
    "zhuaudio",
    "cherry",
    "aula_mouse", "aula_keyboard", "weikav_nut75", "veekos_shine60",
    "lingbao",
    "g3m", "mchose_g3v2", "eweadn", "eweadn_s9pro", "eweadn_x23", "eweadn_x87", "flydigi",
    "vgn_keyboard", "vgn_beiying",
    "vgn_dragonfly3", "ninjutso_legacy",
    "dareu", "delux", "vgn_ry_ms",
    "fa6x_passive",
    "hyperx", "keychron", "pulsar", "rapoo", "jbl", "logitech", "steelseries", "barracuda",
    "asus", "asus_omni", "asus_azoth", "asus_headset", "gwolves", "lofree", "astro", "corsair", "lamzu", "am_infinity",
    "steelseries_elite", "logitech_centurion", "hyperx_cloud3s", "nzxt",
))


@dataclass(frozen=True)
class Match:
    vendor_id: int
    product_id: int
    interface_number: int | None = None
    usage_page: int | None = None
    usage: int | None = None

    def matches(self, info):
        return all(value is None or info.get(field) == value for field, value in (
            ("vendor_id", self.vendor_id), ("product_id", self.product_id),
            ("interface_number", self.interface_number), ("usage_page", self.usage_page),
            ("usage", self.usage)))


@dataclass(frozen=True)
class Entry:
    provider: str
    profile: str
    profile_rev: int
    match: Match
    name: str
    kind: str
    level: str = "C"
    source: str = ""


@dataclass(frozen=True)
class Disabled:
    provider: str
    vendor_id: int
    product_id: int
    reason: str

    def diagnostic(self):
        return {"provider": self.provider, "vendorId": f"{self.vendor_id:04X}",
                "productId": f"{self.product_id:04X}", "reason": self.reason}


@dataclass(frozen=True)
class DevicePack:
    version: int | None = None
    entries: tuple[Entry, ...] = ()
    disabled: tuple[Disabled, ...] = ()
    rejected: tuple[str, ...] = ()
    loaded: bool = False
    # Original validated bytes decoded once. Children receive this snapshot,
    # never a pathname that might change after the manager's ready handshake.
    document: str = ""
    # Loader 2: accepted families.spec.Family objects, never overriding builtins.
    families: tuple = ()

    def is_disabled(self, provider, vendor_id, product_id):
        return self.loaded and any((d.provider, d.vendor_id, d.product_id) ==
                                  (provider, vendor_id, product_id) for d in self.disabled)

    def diagnostic(self):
        return {"version": self.version, "entries": len(self.entries),
                "families": [f.id for f in self.families],
                "disabled": [d.diagnostic() for d in self.disabled],
                "rejected": list(self.rejected), "loaded": self.loaded}

    def select_inventory(self, provider, inventory):
        """Exact provider-scoped deny rules plus hard pack match constraints."""
        if not self.loaded:
            return list(inventory)
        entries = {(d.match.vendor_id, d.match.product_id): d for d in self.entries
                   if d.provider == provider}
        selected = []
        for info in inventory:
            pair = (info.get("vendor_id"), info.get("product_id"))
            if self.is_disabled(provider, *pair):
                continue
            entry = entries.get(pair)
            if entry is None or entry.match.matches(info):
                selected.append(info)
        return selected


EMPTY_PACK = DevicePack()


def _object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate_key")
        result[key] = value
    return result


def _constant(_value):
    raise ValueError("non_json_number")


@lru_cache(maxsize=1)
def _schema():
    return json.loads(Path(__file__).with_name("device_pack_schema.json").read_text(encoding="utf-8"))


def _validate(value, rule, root, path="pack"):
    if "$ref" in rule:
        rule = root["$defs"][rule["$ref"].removeprefix("#/$defs/")]
    expected = {"object": dict, "array": list, "string": str, "integer": int}[rule["type"]]
    if type(value) is not expected:
        raise ValueError(path + ": invalid_type")
    if rule.get("x-family-spec"):
        return  # structure is owned by families.spec.parse_family (see _pack_families)
    if "const" in rule and value != rule["const"] or "enum" in rule and value not in rule["enum"]:
        raise ValueError(path + ": unsupported_value")
    if expected is dict:
        props = rule["properties"]
        if set(value) - set(props):
            raise ValueError(path + ": unknown_field")
        if set(rule.get("required", ())) - set(value):
            raise ValueError(path + ": missing_field")
        for key, item in value.items():
            _validate(item, props[key], root, path + "." + key)
    elif expected is list:
        if len(value) > rule["maxItems"]:
            raise ValueError(path + ": too_many_entries")
        for index, item in enumerate(value):
            _validate(item, rule["items"], root, f"{path}[{index}]")
    elif expected is str:
        if not rule.get("minLength", 0) <= len(value) <= rule.get("maxLength", MAX_BYTES):
            raise ValueError(path + ": invalid_length")
        if "pattern" in rule and re.fullmatch(rule["pattern"], value) is None:
            raise ValueError(path + ": invalid_format")
        if rule.get("x-printable") and value and not value.isprintable():
            raise ValueError(path + ": non_printable")
        if rule.get("format") == "date-time":
            datetime.strptime(value, "%Y-%m-%dT%H:%M:%SZ")
    elif not rule.get("minimum", 0) <= value <= rule.get("maximum", 10**10):
        raise ValueError(path + ": out_of_range")


def parse_pack(document):
    """Parse bytes/text. Whole-pack errors return an inert fallback, never raise."""
    try:
        if not isinstance(document, (bytes, str)):
            raise ValueError("invalid_document_type")
        raw = document if isinstance(document, bytes) else document.encode("utf-8")
        if len(raw) > MAX_BYTES:
            raise ValueError("file_too_large")
        text = raw.decode("utf-8")
        data = json.loads(text, object_pairs_hook=_object, parse_constant=_constant)
        schema = _schema()
        _validate(data, schema, schema)
        if len(data["entries"]) + len(data["disabled"]) > MAX_ENTRIES:
            raise ValueError("too_many_entries")
        if "families" in data and data["loader"] < 2:
            raise ValueError("families_require_loader_2")
        catalog = profiles()
        # Check revisions/field permissions even on rows later rejected for a
        # duplicate ID. Unknown profiles are row errors; known revisions aren't.
        for index, entry in enumerate(data["entries"]):
            choices = [p for p in catalog if (p.provider, p.name) ==
                       (entry["provider"], entry["profile"])]
            if choices and entry["profileRev"] not in {p.revision for p in choices}:
                raise ValueError(f"entries[{index}]: unsupported_profileRev")
            for profile in choices:
                if profile.revision == entry["profileRev"] and set(entry["match"]) - profile.match_fields:
                    raise ValueError(f"entries[{index}]: disallowed_match_field")
        counts = Counter((int(e["match"]["vendorId"], 16), int(e["match"]["productId"], 16))
                         for e in data["entries"])
        builtins = builtin_ids()
        accepted, disabled, rejected = [], [], []
        for index, entry in enumerate(data["entries"]):
            match = entry["match"]
            vid, pid = int(match["vendorId"], 16), int(match["productId"], 16)
            choices = [p for p in catalog if (p.provider, p.name, p.revision) ==
                       (entry["provider"], entry["profile"], entry["profileRev"])]
            reason = ("native_provider_not_supported" if entry["provider"].startswith("native_") else
                      "builtin_id_conflict" if (vid, pid) in builtins else
                      "duplicate_id_or_multiple_profiles" if counts[vid, pid] > 1 else
                      "unknown_profile" if not choices else
                      "multiple_profiles" if len(choices) != 1 else
                      "profile_vendor_mismatch" if vid not in choices[0].vendor_ids else
                      "profile_kind_mismatch" if entry["kind"] != choices[0].kind else "")
            if reason:
                rejected.append(f"entries[{index}]: {reason}")
                continue
            accepted.append(Entry(entry["provider"], entry["profile"], entry["profileRev"],
                                  Match(vid, pid, match.get("interfaceNumber"),
                                        int(match["usagePage"], 16) if "usagePage" in match else None,
                                        int(match["usage"], 16) if "usage" in match else None),
                                  entry["name"], entry["kind"], entry.get("level", "C"), entry.get("source", "")))
        for index, item in enumerate(data["disabled"]):
            if item["provider"] not in BRAND_PROVIDERS:
                rejected.append(f"disabled[{index}]: unsupported_provider")
                continue
            disabled.append(Disabled(item["provider"], int(item["vendorId"], 16),
                                     int(item["productId"], 16), item["reason"]))
        families = _pack_families(data.get("families", []), accepted, rejected)
        return DevicePack(data["packVersion"], tuple(accepted), tuple(disabled), tuple(rejected), True,
                          document=text, families=families)
    except (ValueError, TypeError, UnicodeError, RecursionError, OSError) as error:
        # No untrusted names, paths or JSON payloads in diagnostic exceptions.
        reason = str(error) if type(error) is ValueError else type(error).__name__
        return DevicePack(rejected=("pack: " + reason[:160],))


@lru_cache(maxsize=1)
def _claimed_ids():
    """(vid, pid) already read by any builtin reader or builtin family."""
    from known_ids import build_index
    import families as registry
    claimed = {(d["vendorId"], d["productId"]) for d in build_index()["devices"]}
    claimed |= {(vid, pid) for vid, pid, *_ in registry.builtin_listed_ids()}
    return frozenset(claimed) | builtin_ids()


def _pack_families(rows, entries, rejected):
    """Row-level semantics for loader-2 families; any doubt rejects the row."""
    import families as registry
    from families.spec import SpecError, parse_family
    try:
        claimed = _claimed_ids()
        builtin = set(registry.builtin_families())
    except Exception:  # an index we cannot build proves nothing: add no family
        rejected.extend(f"families[{i}]: claimed_ids_unavailable" for i in range(len(rows)))
        return ()
    claimed = claimed | {(e.match.vendor_id, e.match.product_id) for e in entries}
    parsed = []
    for index, row in enumerate(rows):
        try:
            parsed.append((index, parse_family(row)))
        except (SpecError, ValueError, TypeError, KeyError):
            rejected.append(f"families[{index}]: invalid_family")
    ids = Counter(f.id for _, f in parsed)
    owners = Counter(p for _, f in parsed for p in {(d.vendor_id, d.product_id) for d in f.devices})
    claimed_vendors = {vid for vid, _ in claimed}
    accepted = []
    for index, family in parsed:
        pairs = {(d.vendor_id, d.product_id) for d in family.devices}
        reason = ("builtin_family_conflict" if family.id in builtin else
                  "duplicate_family_id" if ids[family.id] > 1 else
                  "builtin_id_conflict" if pairs & claimed else
                  # Remote packs may not open whole-vendor probing on a brand an
                  # existing reader owns; that needs a reviewed builtin family.
                  "probe_vendor_claimed" if set(family.probe_vendor_ids) & claimed_vendors else
                  "duplicate_id" if any(owners[p] > 1 for p in pairs) else "")
        if reason:
            rejected.append(f"families[{index}]: {reason}")
        else:
            accepted.append(family)
    return tuple(accepted)


def load_pack(path=None):
    if path is None:
        return EMPTY_PACK
    try:
        with open(path, "rb") as stream:
            return parse_pack(stream.read(MAX_BYTES + 1))
    except (OSError, ValueError):
        return DevicePack(rejected=("pack: file_unreadable",))
