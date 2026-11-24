"""Private offline manager; bounded readers and request-tagged partial snapshots.

No GUI, socket, updater or persistent service. Vendor
readers see only one proven physical instance at a time. Two lazy child slots
are controlled by kill-on-close jobs, including nested Bluetooth PowerShell.
Verified Logitech DPI controls share the same bounded, identity-scoped readers.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib
import json
import logging
import math
import re
import sys
import time
from collections import Counter
from dataclasses import asdict, replace
from datetime import datetime, timezone, timedelta

from device_pack import EMPTY_PACK, load_pack, parse_pack

PROVIDERS = (
    ("asus_azoth", "AsusAzothProvider"), ("mchose_g3v2", "MchoseG3V2Provider"),
    ("eweadn_x23", "EweadnX23Provider"), ("eweadn_x87", "EweadnX87Provider"),
    ("weikav_nut75", "Nut75Provider"), ("veekos_shine60", "Shine60Provider"),
    ("aula_mouse", "AulaMouseProvider"), ("aula_keyboard", "AulaKeyboardProvider"),
    ("razer", "RazerProvider"), ("audeze", "AudezeProvider"),
    ("wlmouse", "WLmouseProvider"), ("mchose", "MchoseProvider"),
    ("mchose_headset", "MchoseHeadsetProvider"),
    ("atk_headset", "AtkHeadsetProvider"),
    ("moondrop", "MoondropProvider"),
    ("sony_inzone", "SonyInzoneProvider"),
    ("zhuaudio", "ZhuAudioProvider"),
    ("g3m", "G3mProvider"), ("eweadn", "EweadnProvider"),
    ("eweadn_s9pro", "EweadnS9ProProvider"),
    ("vgn_keyboard", "VgnKeyboardProvider"),
    ("cherry", "CherryProvider"),
    ("lingbao", "LingbaoProvider"),
    ("vgn_beiying", "VgnBeiyingProvider"),
    ("vgn_dragonfly3", "VgnDragonfly3Provider"),
    ("vgn_ry_ms", "VgnRyMsProvider"),
    ("ajazz_aj179_nlp", "AjazzAj179NlpProvider"),
    ("am_infinity_97", "AmInfinity97Provider"),
    ("ninjutso_legacy", "NinjutsoLegacyProvider"),
    ("flydigi", "FlydigiProvider"),
    ("betop", "BetopProvider"),
    ("mchose_qhw_keyboard", "MchoseQhwKeyboardProvider"),
    ("mchose_glw_keyboard", "MchoseGlwKeyboardProvider"),
    ("mchose_k99v3", "MchoseK99V3Provider"), ("mchose_v9pro2", "MchoseV9Pro2Provider"),
    ("mchose_by_keyboard", "MchoseByKeyboardProvider"),
    ("hyperx_alpha2", "HyperXAlpha2Provider"), ("hyperx_cloud3", "HyperXCloud3Provider"),
    ("hyperx", "HyperXProvider"), ("keychron", "KeychronProvider"),
    ("pulsar", "PulsarProvider"), ("jbl", "JblProvider"),
    ("dareu", "DareuProvider"),
    ("delux", "DeluxProvider"),
    ("rapoo", "RapooProvider"),
    ("rapoo_mt560", "RapooMT560Provider"),
    ("fa6x_passive", "Fa6xPassiveProvider"),
    ("logitech", "LogitechProvider"), ("steelseries", "SteelSeriesProvider"),
    ("barracuda", "BarracudaProvider"), ("asus", "AsusProvider"),
    ("asus_omni", "AsusOmniProvider"), ("asus_headset", "AsusHeadsetProvider"),
    ("gwolves", "GWolvesProvider"), ("lofree", "LofreeProvider"),
    ("astro", "AstroProvider"), ("corsair", "CorsairProvider"),
    ("lamzu", "LamzuProvider"), ("am_infinity", "AmInfinityProvider"),
    ("steelseries_elite", "SteelSeriesEliteProvider"),
    ("logitech_centurion", "LogitechCenturionProvider"),
    ("hyperx_cloud3s", "HyperXCloud3SProvider"), ("nzxt", "NzxtProvider"),
    ("bluetooth", "BluetoothProvider"),
    ("xinput", "XInputProvider"),
    ("families", "FamiliesProvider"),
)
PROVIDER_VIDS = {
    "asus_azoth": (0x0B05,), "mchose_g3v2": (0x3837,),
    "eweadn_x23": (0x04F3,), "eweadn_x87": (0x0C45,),
    "weikav_nut75": (0x0C45,), "veekos_shine60": (0x3151,),
    "aula_mouse": (0x320F,), "aula_keyboard": (0x0C45, 0x05AC),
    "razer": (0x1532,), "audeze": (0x3329,), "wlmouse": (0x36A7,),
    "mchose": (0x5253, 0x3837, 0xA8A5, 0x2023), "hyperx_alpha2": (0x03F0,),
    "mchose_headset": (0x291D, 0x3837),
    "atk_headset": (0x373B,),
    "moondrop": (0x35D8,),
    "sony_inzone": (0x054C,),
    "zhuaudio": (0x3654,),
    "g3m": (0x320F,), "eweadn": (0x089D,), "flydigi": (0x37D7,),
    "betop": (0x20BC, 0x20DD),
    "eweadn_s9pro": (0x372E,),
    "vgn_keyboard": (0x320F,),
    "cherry": (0x046A,),
    "lingbao": (0x088D,),
    "vgn_beiying": (0x258A, 0x391D),
    "vgn_dragonfly3": (0x391D,), "vgn_ry_ms": (0x391D,), "ninjutso_legacy": (0x1915,),
    "ajazz_aj179_nlp": (0x7768,),
    "am_infinity_97": (0x0E8D,),
    "mchose_qhw_keyboard": (0x3837,), "mchose_glw_keyboard": (0x3837,), "mchose_k99v3": (0x3837,),
    "mchose_v9pro2": (0x3837,),
    "mchose_by_keyboard": (0x3554, 0x41E4),
    "hyperx_cloud3": (0x03F0,), "hyperx": (0x03F0,), "keychron": (0x3434,),
    "pulsar": (0x3554, 0x373B, 0x3770, 0x3710), "jbl": (0x0ECB,),
    "dareu": (0x260D,),
    "delux": (0x3554,),
    "rapoo": (0x24AE,),
    "rapoo_mt560": (0x24AE,),
    "fa6x_passive": (0x1D57,),
    "logitech": (0x046D,), "steelseries": (0x1038,), "barracuda": (0x1532,),
    "asus": (0x0B05,), "gwolves": (0x33E4,), "lofree": (0x388D,),
    "asus_omni": (0x0B05,), "asus_headset": (0x0B05,),
    "astro": (0x046D,), "corsair": (0x1B1C,), "lamzu": (0x373E,),
    "am_infinity": (0x3151,), "steelseries_elite": (0x1038,),
    "logitech_centurion": (0x046D,), "hyperx_cloud3s": (0x03F0,), "nzxt": (0x1E71,),
}
# Architecture v2: declarative families (engine/families) declare their own vendors.
from families import vendor_ids as _family_vendor_ids  # noqa: E402
PROVIDER_VIDS["families"] = _family_vendor_ids()
NATIVE = {(0x373B, 0x1278), (0x03F0, 0x098D)}
MAX_DEVICES = 64
MAX_RETAIN_SECONDS = 300
MAX_ROUND_SECONDS = 36
MAX_PARTIALS = 96
MAX_READER_TASKS = 96
BLUETOOTH_EMPTY_POLL_THRESHOLD = 3
BLUETOOTH_POLL_SECONDS = 60
BLUETOOTH_EMPTY_POLL_SECONDS = 300
SYSTEM_PROVIDERS = frozenset({"bluetooth", "xinput"})
# Automatic polls of a device-scoped reader that keeps finding its device but no
# reading (asleep, off, out of range) back off: 60, 120, then 300 seconds, so an
# idle receiver is not asked again every round (#588). Any reading, a forced
# sync, a targeted (device/physical) refresh or a topology change resumes at once.
# Passive and system readers send nothing or run their own schedule.
IDLE_BACKOFF_SECONDS = (60, 120, 300)
IDLE_BACKOFF_AFTER = 2
IDLE_BACKOFF_EXEMPT = frozenset({"rapoo", "rapoo_mt560", "fa6x_passive", "jbl", "eweadn_x23", "standard_hid"}) | SYSTEM_PROVIDERS
NO_CHARGING_EVIDENCE = {"delux", "vgn_ry_ms", "bluetooth", "xinput", "standard_hid", "lofree", "keychron", "am_infinity", "astro"}
READER_DIAGNOSTICS = {"families", "betop", "sony_inzone", "delux", "vgn_ry_ms", "ajazz_aj179_nlp", "am_infinity_97", "mchose_qhw_keyboard", "mchose_glw_keyboard", "mchose_by_keyboard", "mchose_headset", "atk_headset", "vgn_keyboard", "vgn_beiying", "rapoo", "rapoo_mt560", "moondrop", "mchose", "eweadn_s9pro", "pulsar", "fa6x_passive", "cherry", "lingbao", "zhuaudio", "asus_omni"}
READER_DIAGNOSTIC_LIMIT = 1024  # Full diagnostic in engine JSON.
READER_HEALTH_LIMIT = 48  # Existing desktop ReaderHealth.ErrorCode bound.
FA6X_DIAGNOSTICS = {
    "missing or ambiguous passive input collection; not opened": "passive input collection missing or ambiguous",
    "input report 03 length unproven; not opened": "input report 03 length unproven",
    "no battery notification; no new measurement": "no valid battery notification",
    "USB parent unproven; collection uniqueness unknown; not opened": "USB parent unproven",
    "enumeration failed; retaining history": "enumeration failed; retaining history",
    "input read failed; no new measurement": "input read failed; no new measurement",
    "HID handle close failed": "HID handle close failed",
    "legacy FA65 companion missing or ambiguous; not opened": "legacy FA65 companion missing or ambiguous",
    "legacy FA65 state collection missing or ambiguous; not opened": "legacy FA65 state input missing or ambiguous",
    "legacy FA65 physical parent unproven; not opened": "legacy FA65 physical parent unproven",
    "legacy FA65 release unproven; not opened": "legacy FA65 release unproven",
    "legacy FA65 collection path unproven; not opened": "legacy FA65 collection path unproven",
}
CHERRY_DIAGNOSTICS = {
    "report IDs/lengths unknown or incompatible: nothing sent": "report sizes unproven",
    "single report 04/64 not proven: nothing sent": "single report 04/64 unproven",
    "unverified CHERRY interface/product: nothing sent": "CHERRY interface/product unverified",
    "physical parent unresolved; uniqueness unknown: nothing sent": "physical parent unresolved",
    "missing or ambiguous eligible collection: nothing sent": "collection missing or ambiguous",
    "enumeration failed: nothing sent": "enumeration failed",
    "battery I/O failed; connection unknown": "battery I/O failed",
    "short/failed battery query write; no retry": "battery query write short/failed",
    "rejected input report length": "input report length rejected",
    "rejected battery reply": "battery reply rejected",
    "no valid reply within deadline/read limit; connection unknown": "no valid battery reply",
    "HID handle close failed": "HID handle close failed",
}
LINGBAO_DIAGNOSTICS = {
    "HID handle close failed": "HID handle close failed",
    "HID backend unavailable": "HID backend unavailable",
    "enumeration failed": "enumeration failed",
    "physical parent unproven; not opened": "physical parent unproven",
    "collection missing or ambiguous; not opened": "collection missing or ambiguous",
    "identity/interface unverified; not opened": "identity/interface unverified",
    "collection path unproven; not opened": "collection path unproven",
    "Feature 0F/33 unproven; not opened": "Feature 0F/33 unproven",
    "battery I/O failed": "battery I/O failed",
    "charging placeholder; no new reading": "charging placeholder; no new reading",
    "invalid battery reply; no new reading": "invalid battery reply; no new reading",
}
ASUS_OMNI_DIAGNOSTICS = {
    "OMNI identity mismatch; not opened": "OMNI identity mismatch",
    "OMNI collection mismatch; not opened": "OMNI collection mismatch",
    "OMNI USB parent unproven; not opened": "OMNI USB parent unproven",
    "OMNI collection ambiguous; not opened": "OMNI collection ambiguous",
    "OMNI report 03/64 unproven; not opened": "OMNI report 03/64 unproven",
    "OMNI open failed": "OMNI open failed",
    "OMNI battery query write failed; no retry": "OMNI battery query write failed; no retry",
    "OMNI input read failed": "OMNI input read failed",
    "OMNI no echo within read limit": "OMNI no echo within read limit",
    "OMNI device error FF AA": "OMNI device error FF AA",
    "OMNI empty reply or asleep; no new reading": "OMNI empty reply or asleep; no new reading",
    "OMNI zero battery; no new reading": "OMNI zero battery; no new reading",
    "OMNI battery out of range": "OMNI battery out of range",
    "OMNI HID backend unavailable": "OMNI HID backend unavailable",
    "OMNI enumeration failed": "OMNI enumeration failed",
    "OMNI HID handle close failed": "OMNI HID handle close failed",
}
ZHUAUDIO_DIAGNOSTICS = frozenset({
    "ZG4 collection missing or ambiguous", "ZG4 parent/path unproven",
    "ZG4 interface/product unverified", "ZG4 CE/CD byte reports unproven",
    "ZG4 all 256 FE sequences unresolved",
    "ZG4 query already pending", "ZG4 inventory invalid",
    "ZG4 write short/failed; no retry", "ZG4 no valid reply within limits",
    "ZG4 headset disconnected", "ZG4 input rejected",
    "ZG4 parent unresolved", "ZG4 enumeration failed", "ZG4 battery I/O failed",
    "HID handle close failed",
})
DPI_PROVIDERS = {"logitech": (0x046D,), "pulsar": (0x3554, 0x373B)}
# Architecture v2 (C4): families join only when a reviewed adapter is listed in
# families.dpi.ADAPTERS; with none, the families write path does not exist.
from families import dpi as _family_dpi  # noqa: E402
if _family_dpi.vendor_ids():
    DPI_PROVIDERS["families"] = _family_dpi.vendor_ids()


def fa6x_diagnostic_summary(line):
    """Accept only fixed FA6x reasons and the bounded numeric proof grammar."""
    if not isinstance(line, str) or len(line) > READER_DIAGNOSTIC_LIMIT:
        return None
    if line in FA6X_DIAGNOSTICS:
        return FA6X_DIAGNOSTICS[line]
    from battery_hid import DESCRIPTOR_FAILURES
    reasons = "|".join(re.escape(reason) for reason in sorted(DESCRIPTOR_FAILURES | {"unknown"}))
    proof = (r"(?:; proof=(?:caps|descriptor)|; proof=caps\+descriptor failed; descriptor=(?:"
             + reasons + r"))?")
    entry = r"(?:out|in|feature):(?:[0-9A-F]{2}/[0-9]{1,4}|\+[0-9]{1,3}|none|unknown)"
    if re.fullmatch(r"input report 03 length unproven" + proof + r"; declared="
                    + entry + r"(?:," + entry + r"){2,29}; not opened", line) is None:
        return None
    return reader_health_summary(line)


def reader_diagnostics(provider):
    # These new readers emit only bounded, protocol-owned messages (no paths,
    # serials, product strings or raw exception text). Do not expose legacy logs.
    if provider.name not in READER_DIAGNOSTICS:
        return []
    lines = provider.reader_diagnostics() if provider.name in ("mchose", "pulsar") else provider.diagnostics()
    if provider.name == "zhuaudio":
        lines = [line for line in lines if isinstance(line, str) and line in ZHUAUDIO_DIAGNOSTICS]
    if provider.name == "cherry":
        lines = [reason for line in lines if isinstance(line, str)
                 for reason in CHERRY_DIAGNOSTICS if line == reason or line.startswith(reason + ";")]
    if provider.name == "fa6x_passive":
        lines = [line for line in lines if fa6x_diagnostic_summary(line) is not None]
    if provider.name == "lingbao":
        lines = [line for line in lines if isinstance(line, str) and line in LINGBAO_DIAGNOSTICS]
    if provider.name == "asus_omni":
        lines = [line for line in lines if isinstance(line, str) and line in ASUS_OMNI_DIAGNOSTICS]
    if provider.name == "mchose_headset":
        # Expose only fixed firmware reasons, never this legacy reader's
        # raw I/O exceptions or packet log.
        for reason in ("接收器固件低于 0008，官方驱动也不支持电量", "接收器固件版本未知，按原方式查询"):
            if any(line.strip() == reason for line in lines):
                return [reason]
        return []
    return lines if len(lines) <= 16 else lines[:6] + lines[-10:]


def reader_health_summary(value):
    """Fit the existing desktop bound; keep full details in diagnostics JSON."""
    text = str(value)
    if len(text) <= READER_HEALTH_LIMIT:
        return text
    failure = re.match(r"(?:(input|output|feature) )?report ([0-9A-F]{2}) (?:length )?unproven;", text)
    declared = re.search(r"; declared=([^;]+)", text)
    if failure and declared:
        direction, report_id = failure.groups()
        label = {"input": "in:", "output": "out:", "feature": "feature:", None: ""}[direction]
        prefix = label + report_id + " unproven;declared="
        entries = declared.group(1).split(",")
        # Show one declaration per direction before spending space on extras.
        first = [next((item for item in entries if item.startswith(d + ":")), None)
                 for d in ("out", "in", "feature")]
        entries = [item for item in first if item is not None] + [item for item in entries if item not in first]
        for count in range(len(entries), -1, -1):
            summary = prefix + ",".join(entries[:count] + (["..."] if count < len(entries) else []))
            if len(summary) <= READER_HEALTH_LIMIT:
                return summary
    # Retain whole reason clauses, never half a word or report declaration.
    clauses = text.split("; ")
    for count in range(len(clauses), 0, -1):
        summary = "; ".join(clauses[:count] + (["..."] if count < len(clauses) else []))
        if len(summary) <= READER_HEALTH_LIMIT:
            return summary
    return "reader unavailable; details in engine JSON"


def utcnow():
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def clean_name(value):
    return "".join(c for c in str(value or "Wireless device") if c.isprintable())[:80]


def public_id(key):
    return "device-" + hashlib.sha256(str(key).encode("utf-8")).hexdigest()[:24]


def suppress_bluetooth_xinput(devices):
    """Prefer Windows Bluetooth percentages over duplicate XInput tiers."""
    count = sum(v.get("provider") == "bluetooth" and v.get("kind") == "gamepad"
                and v.get("online") and v.get("percent") is not None
                and "xbox" in v.get("name", "").casefold() for v in devices.values())
    hidden = set()
    # XInput provides no Bluetooth identity. This is a heuristic: skip the
    # lowest connected wireless slots, one per Bluetooth Xbox gamepad. Only
    # ALKALINE/NIMH tiers have approximate=True; wired/unknown slots are kept.
    for slot in range(4):
        key = public_id(f"xinput:{slot}")
        value = devices.get(key, {})
        if count and value.get("provider") == "xinput" and value.get("online") and value.get("approximate"):
            hidden.add(key)
            count -= 1
    return {key: value for key, value in devices.items() if key not in hidden}


# The same device seen twice while it charges: (cable VID, PID) -> (receiver VID, PID).
# Only declared pairs fold. Guessing by vendor merged two different mice of one
# brand (Codex replay, 2026-11-22), so a pair is added only from a user's
# diagnostics showing both cards. The native ATK receiver is paired in C#
# (WirelessLayout.NativeWiredTwins), not here.
CHARGING_TWINS = {
    (0x088D, 0x062E): (0x089D, 0x062F),   # EWEADN mouse: cable / receiver
    (0x3554, 0xF58F): (0x373B, 0x1085),   # ATK VXE R1 SE+: cable / receiver (#1090)
}


def fold_charging_twins(devices):
    """Presentation only: fold a declared cable/receiver pair into the receiver card.

    Exactly one cable card and one receiver card of the pair must be present
    (a second unit of the same model makes it ambiguous: keep both), on
    different connections, the cable reporting a current charging reading, and
    either the receiver reading stale or both readings within 3 points.
    """
    def model(d):
        vid, pid = d.get("vendorId"), d.get("productId")
        return (vid, pid) if type(vid) is int and type(pid) is int else None

    folded, hidden = {}, set()
    for cable_id, receiver_id in CHARGING_TWINS.items():
        cables = [d for d in devices if model(d) == cable_id]
        receivers = [d for d in devices if model(d) == receiver_id]
        if len(cables) != 1 or len(receivers) != 1:
            continue
        cable, receiver = cables[0], receivers[0]
        if (cable.get("kind") != receiver.get("kind") or not cable.get("online")
                or not cable.get("physicalId") or cable.get("physicalId") == receiver.get("physicalId")
                or cable.get("charging") is not True or cable.get("measurementState") != "current"
                or cable.get("percent") is None):
            continue
        if (receiver.get("measurementState") == "current" and receiver.get("percent") is not None
                and abs(receiver["percent"] - cable["percent"]) > 3):
            continue   # two current, different readings: two devices
        merged = dict(receiver)
        merged.update({k: cable.get(k) for k in ("percent", "charging", "chargingKnown", "fresh", "approximate",
                                                "confirmedAtUtc", "measuredAtUtc", "queriedAtUtc",
                                                "chargingCheckedAtUtc", "measurementState", "sourceQuality")})
        merged["chargingTwinId"] = cable["id"]
        folded[receiver["id"]] = merged
        hidden.add(cable["id"])
    return ([folded.get(d["id"], d) for d in devices if d["id"] not in hidden],
            [{"name": d["name"], "kind": d["kind"]} for d in devices if d["id"] in hidden])


def suppress_old_connections(devices):
    """Presentation only. Complete-link groups protect current cards and slots."""
    def same(a, b):
        if (a.get("provider") in ("cherry", "lingbao", "zhuaudio", "asus_omni", "asus_headset",
                                  "weikav_nut75", "veekos_shine60", "eweadn_x23", "eweadn_x87", "mchose_g3v2", "asus_azoth")
                or a.get("provider") != b.get("provider") or a.get("kind") != b.get("kind")
                or not a.get("physicalId") or not b.get("physicalId")
                or a["physicalId"] == b["physicalId"]
                or a.get("percent") is None or b.get("percent") is None
                or abs(a["percent"] - b["percent"]) > 5
                or a.get("measurementState") == b.get("measurementState") == "current"):
            return False
        names = [clean_name(d.get("name")).casefold() for d in (a, b)]
        return names[0] == names[1] or any(re.search(r"dongle|receiver|接收器|wireless mouse|2\.4g", n) for n in names)

    def rank(d):
        stamp = valid_time(d.get("confirmedAtUtc"))
        time_value = datetime.fromisoformat(stamp.replace("Z", "+00:00")).timestamp() if stamp else 0
        return (d.get("measurementState") != "current", -time_value, d["id"])

    groups = []
    hidden = set()
    for device in sorted(devices, key=rank):
        group = next((group for group in groups if all(same(device, member) for member in group)), None)
        if group is None:
            groups.append([device])
        else:
            group.append(device)
            hidden.add(device["id"])
    return ([d for d in devices if d["id"] not in hidden],
            [{"name": d["name"], "kind": d["kind"]} for d in devices if d["id"] in hidden][:16])


def inventory_safe_name(name, inventory):
    # Strip known private strings before clean_name truncates them. A product
    # can echo its serial/path; these are never useful in a public report.
    name = str(name or "Wireless device")
    secrets = set()
    for info in inventory:
        for field in ("serial_number", "path", "physical_id"):
            value = info.get(field)
            if value:
                secrets.add(value.decode("utf-8", "replace") if isinstance(value, bytes) else str(value))
    for secret in sorted(secrets, key=lambda value: (-len(value), value)):
        name = re.sub(re.escape(secret), "[redacted]", name, flags=re.I)
    return name


# Exact devices observed in feedback, not whole vendors (ASUS/ITE also make peripherals).
UNADAPTED_NOISE_IDS = frozenset({
    (0xFFAA, 0xAAFF),  # Tecent Virtual Hid Device
    (0xDEED, 0xFEED),  # UMDF Virtual vhidev device
    (0xBACD, 0x001F),  # Virtual Multitouch Device
    (0x048D, 0xC100),  # ITE Device(8910), lighting controller
    (0x0B05, 0x19AF),  # AURA LED Controller
    (0x0CF2, 0x7750),  # MS-ARGB
})
UNADAPTED_NOISE_NAME = re.compile(r"\b(?:virtual|argb|led\s+controller|aura)\b", re.I)


def unadapted_devices(inventory, previous):
    """Allowlisted model metadata from existing inventory only; never probe HID."""
    from topology import physical_groups
    known = {value.get("physicalId") for value in previous.values()}
    out = []
    for physical, infos in physical_groups(inventory).items():
        if public_id(physical) in known or any((d.get("vendor_id"), d.get("product_id")) in NATIVE for d in infos):
            continue
        # Filter only diagnostic rows, leaving discovery and provider routing intact.
        # A lighting function must not hide a different peripheral in the same group.
        infos = [d for d in infos
                 if (d.get("vendor_id"), d.get("product_id")) not in UNADAPTED_NOISE_IDS
                 and not UNADAPTED_NOISE_NAME.search(str(d.get("product_string") or ""))]
        if not any(d.get("usage_page", 0) >= 0xFF00 or
                   (d.get("usage_page"), d.get("usage")) == (1, 2) for d in infos):
            continue
        # Product strings are untrusted, too. Remove any private inventory
        # values before truncation, so a shortened serial cannot escape cleanup.
        secrets = {physical}
        for info in infos:
            for field in ("serial_number", "path", "physical_id"):
                value = info.get(field)
                if value:
                    secrets.add(value.decode("utf-8", "replace") if isinstance(value, bytes) else str(value))
        names = []
        for info in infos:
            name = str(info.get("product_string") or "")
            for secret in sorted(secrets, key=lambda value: (-len(value), value)):
                name = re.sub(re.escape(secret), "[redacted]", name, flags=re.I)
            name = re.sub(r'(?i)(?:[a-z]:\\|\\\\|(?:hid|usb|bthenum|bthledevice)[\\#])[^\s"\']+', "[path]", name)
            name = re.sub(r'(?i)(?:\b(?:serial(?:[_ -]?number)?|s/n|sn)|序列号)\s*[:=#：]?\s*[^\s"\']+', "[serial]", name)
            name = re.sub(r"(?i)\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b", "[device]", name)
            name = "".join(c for c in name if c.isprintable()).replace('"', "'").strip()[:48]
            if name:
                names.append(name)
        models = sorted({(d["vendor_id"], d["product_id"]) for d in infos})
        vid, pid = models[0]
        # topology.discover currently supplies usage only, not report lengths.
        collections = sorted((d.get("usage_page", 0), d.get("usage", 0)) for d in infos)[:8]
        out.append({"vendorId": vid, "productId": pid, "product": min(names) if names else "",
                    "collections": [{"usagePage": page, "usage": usage} for page, usage in collections]})
    return sorted(out, key=lambda item: (item["vendorId"], item["productId"], item["product"],
                                        [(c["usagePage"], c["usage"]) for c in item["collections"]]))[:16]


STDIN_LINE_LIMIT = 262144   # UTF-8 bytes per command line; a full 256-entry familyMemory is ~40 KiB


def stdin_lines(stream, limit):
    """Yield command lines; an over-limit line is drained and yielded as None."""
    while True:
        line = stream.readline(limit + 1)
        if not line:
            return
        if not line.endswith("\n") and len(line) > limit:
            while line and not line.endswith("\n"):
                line = stream.readline(65536)    # discard the rest of the oversized line
            yield None
            continue
        if len(line.rstrip("\r\n").encode("utf-8", "replace")) > limit:
            yield None
        else:
            yield line


def valid_public_target(value):
    return value is None or isinstance(value, str) and re.fullmatch(r"device-[a-f0-9]{24}", value) is not None


def physical_topology(paths, physical_id):
    """Resolve a public physical ID before opening only its HID descriptors."""
    from topology import discover, physical_instance
    if paths is None:
        raise ValueError("topology_unavailable")
    selected = []
    for path in paths:
        text = path.decode("utf-8", "replace") if isinstance(path, bytes) else path
        if re.search(r"bthenum|bthledevice|vid&[0-9a-f]{8}", text, re.I):
            continue
        native = re.search(r"vid[_&]([0-9a-f]{4,8}).*?pid[_&]([0-9a-f]{4})", text, re.I)
        if native and (int(native[1][-4:], 16), int(native[2], 16)) in NATIVE:
            continue
        if public_id(physical_instance({"path": text})) == physical_id:
            selected.append(text)
    return discover(selected, NATIVE)


def kind_of(status):
    if status.kind in ("mouse", "headset", "keyboard", "gamepad"):
        return status.kind
    name = status.name.lower()
    if any(s in name for s in ("keyboard", "blackwidow", "deathstalker", "lofree")):
        return "keyboard"
    if status.source in ("bluetooth", "standard_hid"):
        return "device"
    if status.source in ("razer", "logitech"):
        return "headset" if any(s in name for s in ("headset", "blackshark", "kraken", "barracuda", "g733", "g pro x")) else "mouse"
    return "mouse" if status.source in ("wlmouse", "mchose", "pulsar", "asus", "gwolves", "lamzu", "am_infinity", "nzxt") else "headset"


def valid_time(value, now=None):
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        if parsed.tzinfo is None:
            return None
        delta = (parsed - (now or datetime.now(timezone.utc))).total_seconds()
        return value if delta <= 5 else None
    except (TypeError, ValueError, AttributeError, OverflowError):
        return None


def normalise_dpi(value, usable=False):
    """DPI evidence is independent of battery evidence and never cache-authorises writes."""
    if not isinstance(value, dict) or value.get("supported") is not True:
        return None
    current = value.get("current")
    if type(current) is not int or not 50 <= current <= 65535:
        current = None
    stages = value.get("stages")
    stages = list(dict.fromkeys(v for v in stages if type(v) is int and 50 <= v <= 65535))[:8] if isinstance(stages, list) else []
    stamp = valid_time(value.get("checkedAtUtc"))
    recent = bool(stamp and (datetime.now(timezone.utc) - datetime.fromisoformat(stamp.replace("Z", "+00:00"))).total_seconds() <= 90)
    token = value.get("targetToken")
    writable = bool(usable and recent and current is not None and stages and
                    value.get("canSet") is True and isinstance(token, str) and re.fullmatch(r"[a-f0-9]{64}", token))
    return {"supported": True, "canSet": writable, "current": current, "stages": stages,
            "stageSource": "onboard" if value.get("stageSource") == "onboard" else "quick",
            "checkedAtUtc": stamp, "targetToken": token if writable else None,
            "error": clean_name(value.get("error")) if value.get("error") else None}


def normalise(status, previous, now, cached=False):
    """Keep measurement evidence: querying history cannot renew sample time."""
    old = previous or {}
    value = status.level
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or not 0 <= value <= 100:
        value = None
    else:
        value = int(round(value))
    cached = bool(cached or status.cached)
    system = status.source_quality == "system_property" or status.source == "bluetooth"
    online = bool(status.online)
    connection_known = online if status.connection_known is None else bool(status.connection_known)
    # A current explicit state (e.g. plugged-in V9 with a frozen percentage)
    # invalidates history without pretending to have measured a new level.
    # A Buds reply can explicitly mark one component's percentage unknown (FF)
    # while the receiver and the other components still report current values.
    part_unavailable = status.source == "sony_inzone" and status.unit_id in ("left", "right", "case")
    unavailable = (status.level_unavailable and not cached and (status.connection_known is True or part_unavailable)
                   and ((status.source_quality == "device_reply" and not system)
                        or (status.source == "xinput" and system)))
    if unavailable:
        value = None
    acquired = online and value is not None and not cached
    fresh = acquired and not system
    if not acquired and old and not unavailable:
        value = old.get("percent")
    measured = (valid_time(status.measured_at_utc) or now) if fresh else old.get("measuredAtUtc")
    queried = status.queried_at_utc or (now if acquired else old.get("queriedAtUtc"))
    # Legacy confirmedAt is the property query time for Bluetooth, not a
    # fabricated device measurement timestamp. measuredAt remains null there.
    confirmed = measured if fresh else (queried if acquired and system else old.get("confirmedAtUtc"))
    if unavailable:
        measured = confirmed = None
        queried = status.queried_at_utc or now
    charging_known = (status.charging_known if status.charging_known is not None
                      else status.source not in NO_CHARGING_EVIDENCE and isinstance(status.charging, bool))
    charging = bool(status.charging) if (fresh or unavailable and online) and charging_known else None
    quality = "system_property" if system else "device_reply"
    if not unavailable and not acquired:
        quality = "cached"
    from catalog import verification_for
    verification_level, verification_label = verification_for(status.source)
    pack_model = status.model_source == "pack"
    if pack_model:
        from catalog import load_catalog
        verification_level = status.verification_level or "C"
        verification_label = load_catalog()["levels"].get(verification_level, "")
    return {
        "id": public_id(status.key), "legacyId": public_id(status.legacy_key) if status.legacy_key else None,
        "physicalId": public_id(status.physical_id) if status.physical_id else None,
        "unitId": public_id(status.physical_id + ":" + status.unit_id) if status.physical_id and status.unit_id else None,
        "name": clean_name(status.name), "kind": kind_of(status), "percent": value,
        "charging": charging, "chargingKnown": charging is not None,
        "online": online, "connectionState": ("connected" if online else "disconnected") if connection_known else "unconfirmed",
        "fresh": fresh, "approximate": bool(status.approx), "provider": status.source or "unknown",
        "familyProbe": bool(getattr(status, "family_probe", False)), "canDisable": False,
        **({"unconfirmed": True} if status.unconfirmed or not acquired and old.get("unconfirmed") else {}),
        "transport": "Bluetooth" if status.via == "bluetooth" or system and status.source != "xinput" else "USB / 2.4G",
        "confirmedAtUtc": confirmed, "measuredAtUtc": measured if not system else None,
        "queriedAtUtc": queried,
        "connectionCheckedAtUtc": now if connection_known else old.get("connectionCheckedAtUtc"),
        "chargingCheckedAtUtc": now if charging is not None else None,
        "sourceQuality": quality,
        "verificationLevel": verification_level, "verificationLabel": verification_label,
        "measurementState": "current" if acquired else "cached" if value is not None else "unknown",
        "refreshScope": (status.refresh_scope if status.refresh_scope in ("device", "provider")
                         else "provider" if system or status.source in ("logitech", "standard_hid")
                         else "device" if status.physical_id else "provider"),
        **({"modelSource": "pack", "packVersion": status.pack_version,
            "modelReference": status.model_reference} if pack_model else {}),
        "dpi": None if pack_model else normalise_dpi(getattr(status, "dpi", None) or old.get("dpi"),
                             usable=bool(getattr(status, "dpi", None)) and online and not cached
                             and status.source in DPI_PROVIDERS and kind_of(status) == "mouse"
                             and (status.source != "pulsar" or status.dpi.get("stageSource") == "onboard")),
    }


def historical(value):
    return dict(value, online=False, fresh=False, charging=None, chargingKnown=False,
                chargingCheckedAtUtc=None, connectionState="unconfirmed",
                sourceQuality="cached", measurementState="cached" if value.get("percent") is not None else "unknown",
                dpi=normalise_dpi(value.get("dpi")))


def provider_class(name):
    if name == "standard_hid":
        from standardhid import StandardHidProvider
        return StandardHidProvider
    return getattr(importlib.import_module("providers." + name), dict(PROVIDERS)[name])


def scoped_poll(provider, inventory, physical_id, device_pack=EMPTY_PACK):
    """Restrict the existing protocol to one physical receiver; retain slots."""
    from providers import hidlist
    original, paths = hidlist.enumerate, hidlist.interface_paths
    selected = [d for d in inventory if d.get("physical_id") == physical_id
                and (d.get("vendor_id"), d.get("product_id")) not in NATIVE]
    selected = device_pack.select_inventory(getattr(provider, "name", ""), selected)
    if device_pack.loaded and not selected:
        return []
    hidlist.enumerate = lambda vid=0: [d for d in selected if not vid or d.get("vendor_id") == vid]
    hidlist.interface_paths = lambda: frozenset((d["path"].decode("utf-8", "replace") if isinstance(d["path"], bytes) else d["path"]).lower() for d in selected)
    try:
        result = []
        misses = getattr(provider, "_misses", {})
        statuses = provider.poll()
        scope = "provider" if getattr(provider, "name", "") in ("logitech", "standard_hid") or len(statuses) > 1 else "device"
        for status in statuses:
            result.append(replace(status, key=status.key + ":instance:" + physical_id,
                                  physical_id=physical_id, legacy_key=status.key,
                                  cached=status.cached or bool(misses.get(status.key, 0)), refresh_scope=scope))
        return result
    finally:
        hidlist.enumerate, hidlist.interface_paths = original, paths


def reader_child():
    from isolation import MAX_LINE, wire_encode, wire_decode
    from providers import hidlist
    from topology import discover
    instances = {}
    topology_paths = object()
    topology_at = -1e20
    inventory = []
    device_pack = EMPTY_PACK
    for line in sys.stdin.buffer:
        if len(line) > MAX_LINE:
            break
        sequence = None
        try:
            command = json.loads(line, object_hook=wire_decode)
            sequence = command.get("sequence")
            action = command.get("action")
            # Architecture v2: the manager owns probe policy and read memory;
            # every command carries them so restarted children stay consistent.
            import families as family_registry
            family_registry.set_probe_enabled(command.get("probeFamilies") is True)
            document = command.get("devicePack", "")
            if document != device_pack.document:
                candidate_pack = parse_pack(document) if document else EMPTY_PACK
                if document and not candidate_pack.loaded:
                    raise ValueError("invalid child pack")
                device_pack = candidate_pack
                family_registry.set_pack_families(device_pack.families)
                instances.clear()
            if action == "smoke":
                from catalog import validate_catalog
                validate_catalog()
                imported = 0
                for name, _ in PROVIDERS:
                    provider_class(name)()
                    imported += 1
                provider_class("standard_hid")()
                imported += 1
                response = {"providerCount": len(PROVIDERS) + 1,
                            "importedProviderCount": imported, "devices": []}
            elif action == "topology":
                current = command.get("paths")
                physical = command.get("physicalId")
                if physical is not None:
                    if not valid_public_target(physical):
                        raise ValueError("invalid physical target")
                    selected = physical_topology(current, physical)
                    present = {d["physical_id"] for d in selected}
                    # A partial discovery cannot replace the full topology cache
                    # or evict another receiver's provider/identity instance.
                    instances = {k: v for k, v in instances.items()
                                 if k[0] in SYSTEM_PROVIDERS or public_id(k[1]) != physical or k[1] in present}
                    response = {"inventory": selected}
                else:
                    # Retry inaccessible descriptors at a bounded rate. Ordinary
                    # polls share one topology and do not enumerate repeatedly.
                    if current != topology_paths or time.monotonic() - topology_at >= 30:
                        inventory = discover(current, NATIVE, full=True)
                        topology_paths, topology_at = current, time.monotonic()
                    present = {d["physical_id"] for d in inventory}
                    instances = {k: v for k, v in instances.items() if k[1] in present or k[0] in SYSTEM_PROVIDERS}
                    response = {"inventory": inventory}
            elif action == "query":
                name, physical = command["provider"], command.get("physicalId", "")
                key = (name, physical)
                if key not in instances:
                    if len(instances) >= 128:
                        instances.clear()
                    instances[key] = (provider_class(name)(device_pack=device_pack) if name in ("razer", "pulsar")
                                      else provider_class(name)())
                provider = instances[key]
                if name == "families" and isinstance(command.get("familyMemory"), dict):
                    provider.memory.restore(command["familyMemory"])
                if name in SYSTEM_PROVIDERS:
                    statuses = provider.poll()
                    misses = getattr(provider, "_misses", {})
                    statuses = [replace(s, cached=s.cached or bool(misses.get(s.key, 0))) for s in statuses]
                else:
                    statuses = scoped_poll(provider, command.get("inventory", []), physical, device_pack)
                candidates = getattr(provider, "candidate_count", None)
                if candidates is None:
                    candidates = recognized_candidate_count(name, command.get("inventory", []), device_pack)
                response = {"statuses": [asdict(s) for s in statuses[:MAX_DEVICES]], "checkedAtUtc": utcnow(),
                            "candidateCount": candidates}
                if name == "razer":
                    response["failureCode"] = provider.failure_code()
                if name in READER_DIAGNOSTICS:
                    response["diagnostics"] = reader_diagnostics(provider)
                if name == "families":
                    response["familyMemory"] = provider.memory.snapshot()
            elif action == "set_dpi":
                # Only the selected physical receiver reaches the provider. The
                # provider revalidates identity/current/stages before one write.
                name = command.get("provider")
                if name not in DPI_PROVIDERS:
                    raise ValueError("invalid dpi provider")
                module = importlib.import_module("providers." + name)
                provider = instances.get((name, command.get("physicalId", "")))
                set_device_dpi = getattr(provider, "set_device_dpi", module.set_device_dpi)
                selected = [d for d in command.get("inventory", [])
                            if d.get("physical_id") == command.get("physicalId")
                            and d.get("vendor_id") in DPI_PROVIDERS[name]]
                selected = device_pack.select_inventory(name, selected)
                if device_pack.loaded and (not selected or any(
                        e.provider == name and any(e.match.matches(d) for d in selected)
                        for e in device_pack.entries)):
                    raise ValueError("pack writes forbidden")
                response = set_device_dpi(selected, command.get("target"), command.get("dpi"),
                                          command.get("expectedDpi"), command.get("targetToken"))
            else:
                raise ValueError("invalid action")
            response["sequence"] = sequence
        except Exception:
            response = {"sequence": sequence, "errorCode": "read_failed"}
        print(json.dumps(response, ensure_ascii=True, default=wire_encode), flush=True)


def recognized_candidate_count(name, inventory, device_pack=EMPTY_PACK):
    """Only explicit existing maps are evidence of a supported candidate.

    Unknown PID within a brand is not a broken supported device. This affects
    health text only; the existing provider retains its command-match guards.
    """
    if name in SYSTEM_PROVIDERS or name == "standard_hid":
        return 0
    module = importlib.import_module("providers." + name)
    inventory = device_pack.select_inventory(name, inventory)
    if name in ("atk_headset", "delux", "vgn_ry_ms", "ajazz_aj179_nlp"):
        return len({d.get("product_id") for d in inventory
                    if d.get("vendor_id") == module.VID and d.get("product_id") in module.PIDS})
    if name == "am_infinity_97":
        return len({d.get("product_id") for d in inventory
                    if d.get("vendor_id") == module.VID and d.get("product_id") in module.PIDS})
    if name in ("moondrop", "asus_omni", "sony_inzone", "asus_headset", "veekos_shine60",
                "weikav_nut75", "eweadn_x23", "eweadn_x87", "mchose_g3v2", "asus_azoth"):
        return len({(d.get("vendor_id"), d.get("product_id")) for d in inventory
                    if d.get("vendor_id") == module.VID and d.get("product_id") in module.PIDS})
    if name in ("mchose_headset", "mchose_by_keyboard", "vgn_beiying", "betop"):
        return len({(d.get("vendor_id"), d.get("product_id")) for d in inventory
                    if (d.get("vendor_id"), d.get("product_id")) in module.PIDS})
    pids = set()
    nano_candidates = set()
    if name == "pulsar":
        nano_candidates = {(d.get("vendor_id"), d.get("product_id")) for d in inventory
                           if d.get("product_id") in module.NANO_PIDS.get(d.get("vendor_id"), {})}
    v3_candidates = ({(d.get("vendor_id"), d.get("product_id")) for d in inventory
                      if d.get("vendor_id") == module.V3_VID and d.get("product_id") in module.V3_PIDS}
                     if name == "mchose" else set())
    for field in ("PIDS", "KNOWN", "DONGLES", "RECEIVERS", "HEADSETS", "WIRED", "BRAGI_PIDS", "MOUSE_MODELS", "MODELS", "CLASSIC_MODELS"):
        value = getattr(module, field, ())
        if name == "pulsar" and field == "PIDS":
            for entry in inventory:
                pids.update(value.get(entry.get("vendor_id"), {}))
        elif isinstance(value, (dict, set, frozenset, tuple, list)):
            pids.update(k for k in value if type(k) is int)
    for field in ("PID", "RECEIVER", "DONGLE"):
        value = getattr(module, field, None)
        if type(value) is int:
            pids.add(value)
    pids.update(e.match.product_id for e in device_pack.entries if e.provider == name)
    return len(nano_candidates | v3_candidates | {(d.get("vendor_id"), d.get("product_id"))
                                  for d in inventory if d.get("product_id") in pids})


class BatteryEngine:
    def __init__(self, providers=None, include_native=False, package_smoke=False, process_factory=None,
                 device_pack=None, probe_families=True):
        from providers import hidlist
        from isolation import ReaderProcess
        self._hidlist = hidlist
        self._direct = providers is not None
        self.providers = providers if providers is not None else [name for name, _ in PROVIDERS] + ["standard_hid"]
        self.package_smoke = package_smoke
        self.imported_provider_count = 0
        self.device_pack = device_pack or EMPTY_PACK
        # Whole-vendor probe families are on unless the user switched them off.
        # The manager never reads devices; each child command carries the flag
        # (see _reader_command), so this process-wide switch stays untouched here.
        self.probe_families = bool(probe_families)
        self.family_memory = {}
        self._family_user_disabled = {}   # memory key -> user's choice; outranks child snapshots
        self._family_units = {}   # public device id -> (family id, memory unit)
        if self._direct and self.device_pack.loaded:
            for provider in self.providers:
                if provider.name in ("razer", "pulsar"):
                    provider.configure_pack(self.device_pack)
        self.previous = {}
        self.unadapted = []
        self._inventory_groups = {}
        self.by_provider = {}
        self.health = {}
        self.last_poll = {}
        self.failures = {}
        self.retry_after = {}
        self.idle_misses = {}
        self.idle_until = {}
        self.physical_by_id = {}
        self.dpi_targets = {}
        self.provider_by_id = {}
        self.errors = []
        self._affinity = {}
        self._deferred = set()
        self._ambiguous_providers = set()
        self._bluetooth_empty_polls = 0
        self._bluetooth_paths = None
        self._bluetooth_slot = None
        self.children = [(process_factory or ReaderProcess)(), (process_factory or ReaderProcess)()]
        self.native_mouse = None
        if self._direct and not include_native:
            original = hidlist.enumerate
            hidlist.enumerate = lambda vid=0: [d for d in original(vid) if (d.get("vendor_id"), d.get("product_id")) not in NATIVE]

    def close(self):
        self._bluetooth_slot = None
        for child in self.children:
            child.close()

    def _provider_vids(self, name):
        vids = PROVIDER_VIDS.get(name, ())
        if name == "families" and self.device_pack.families:
            # Loader-2 pack families may add vendors; builtin vids stay included.
            vids = tuple(set(vids).union(*(f.vendor_ids for f in self.device_pack.families)))
        return vids

    def _reader_command(self, command):
        # Carry the immutable startup snapshot, including after child restarts.
        command = dict(command, devicePack=self.device_pack.document) if self.device_pack.loaded else dict(command)
        command["probeFamilies"] = self.probe_families
        if command.get("provider") == "families":
            command["familyMemory"] = self.family_memory
        return command

    FAMILY_MEMORY_LIMIT = 256   # keeps the snapshot line far below the host's 512 KiB frame cap

    def _merge_family_memory(self, snapshot):
        if not isinstance(snapshot, dict) or len(snapshot) > 4 * self.FAMILY_MEMORY_LIMIT:
            return
        from families.memory import ReadMemory
        checked = ReadMemory()
        checked.restore(snapshot)               # drops malformed entries
        merged = checked.snapshot()
        for key, value in merged.items():
            if key not in self.family_memory and len(self.family_memory) >= self.FAMILY_MEMORY_LIMIT:
                continue
            if key in self._family_user_disabled:
                value["disabled"] = self._family_user_disabled[key]
            self.family_memory[key] = value

    def family_memory_command(self, op, device_id=None, memory_key=None, snapshot=None):
        """Host API for read memory.
        get      -> current snapshot (persist it as family-memory.json)
        set      -> replace the snapshot at startup (malformed entries are dropped)
        disable  -> stop reading one family device by its public deviceId; the reply
                    carries memoryKey, which the host keeps to undo it later
        enable   -> resume a device by memoryKey (a disabled device has no card)"""
        result = {}
        if op == "set":
            if not isinstance(snapshot, dict) or len(snapshot) > 4 * self.FAMILY_MEMORY_LIMIT:
                return {"ok": False, "error": "invalid_snapshot"}   # keep the memory we already hold
            self.family_memory, self._family_user_disabled = {}, {}
            self._merge_family_memory(snapshot)
            self._family_user_disabled = {k: True for k, v in self.family_memory.items() if v.get("disabled")}
            for unit_key in self._family_user_disabled:
                family_id, _, device_key = unit_key.partition("|")
                if family_id and device_key:
                    self._forget_family_unit((family_id, device_key))
        elif op in ("disable", "enable"):
            if op == "disable":
                unit = self._family_units.get(device_id)
            else:
                family_id, _, device_key = str(memory_key or "").partition("|")
                unit = (family_id, device_key) if family_id and device_key.startswith("unit-") else None
            if unit is None:
                return {"ok": False, "error": "unknown_device"}
            from families.memory import ReadMemory
            memory = ReadMemory()
            memory.restore(self.family_memory)
            memory.disable(unit[0], unit[1], op == "disable")
            self.family_memory = memory.snapshot()
            result["memoryKey"] = unit[0] + "|" + unit[1]
            self._family_user_disabled[result["memoryKey"]] = op == "disable"
            if op == "disable":
                self._forget_family_unit(unit)
        elif op != "get":
            return {"ok": False, "error": "invalid_op"}
        result.update(ok=True, familyMemory=dict(self.family_memory), probeFamilies=self.probe_families)
        return result

    def _family_stopped(self, status):
        source = getattr(status, "model_source", "") or ""
        if not source.startswith("family:") or not status.physical_id:
            return False
        from families import memory_key
        unit_key = source[len("family:"):] + "|" + memory_key(status.physical_id)
        return (self._family_user_disabled.get(unit_key) is True
                or (self.family_memory.get(unit_key) or {}).get("disabled") is True)

    def _forget_family_unit(self, unit):
        """Drop every trace of a stopped family device so no zombie card survives."""
        ids = {key for key, value in self._family_units.items() if value == tuple(unit)}
        for group in self.by_provider.values():
            for key in ids:
                group.pop(key, None)
        for table in (self.previous, self.dpi_targets, self.physical_by_id, self.provider_by_id):
            for key in ids:
                table.pop(key, None)

    def _snapshot(self, request_id=0, generation=0, complete=True, unadapted_inventory=None):
        live = {}
        legacy_counts = Counter(v.get("legacyId") for group in self.by_provider.values() for v in group.values() if v.get("legacyId"))
        dedicated = {v.get("unitId") for group in self.by_provider.values() for v in group.values()
                     if v.get("provider") != "standard_hid" and v.get("unitId") and v.get("measurementState") == "current"}
        for group in self.by_provider.values():
            for key, value in group.items():
                if value.get("provider") == "standard_hid" and value.get("unitId") in dedicated:
                    continue
                value = dict(value)
                if legacy_counts.get(value.get("legacyId"), 0) > 1:
                    value["legacyId"] = None
                live[key] = value
        now = datetime.now(timezone.utc)
        for key, value in self.previous.items():
            if key in live:
                continue
            if value.get("provider") == "standard_hid" and value.get("unitId") in dedicated:
                continue
            stamp = value.get("confirmedAtUtc")
            if valid_time(stamp, now):
                age = (now - datetime.fromisoformat(stamp.replace("Z", "+00:00"))).total_seconds()
                if 0 <= age < MAX_RETAIN_SECONDS:
                    live[key] = historical(value)
        for key, value in list(live.items()):
            if value.get("dpi"):
                live[key] = value = dict(value, dpi=normalise_dpi(value["dpi"], usable=value["dpi"].get("canSet") is True))
            stamp = value.get("confirmedAtUtc")
            valid = valid_time(stamp, now)
            if value.get("measurementState") == "cached" and (not valid or
                    (now - datetime.fromisoformat(valid.replace("Z", "+00:00"))).total_seconds() >= MAX_RETAIN_SECONDS):
                live[key] = dict(value, percent=None, measurementState="unknown", fresh=False,
                                 charging=None, chargingKnown=False, chargingCheckedAtUtc=None)
        self.previous = dict(list(live.items())[:MAX_DEVICES])
        self.provider_by_id = {key: value for key, value in self.provider_by_id.items() if key in self.previous}
        self.physical_by_id = {key: value for key, value in self.physical_by_id.items() if key in self.previous}
        self.dpi_targets = {key: value for key, value in self.dpi_targets.items() if key in self.previous}
        # Presentation only, after history is merged: no cached duplicate, and
        # retain refresh routing if the Bluetooth counterpart later disappears.
        visible = suppress_bluetooth_xinput(self.previous)
        devices = sorted(visible.values(), key=lambda d: ({"mouse": 0, "keyboard": 1, "headset": 2}.get(d["kind"], 3), d["name"], d["id"]))
        # Metadata is derived only from the already discovered inventory, and
        # added to outgoing copies. Ambiguous models have no public VID:PID.
        public_devices = []
        for device in devices:
            device = dict(device)
            infos = self._inventory_groups.get(self.physical_by_id.get(device["id"]), [])
            models = {(d.get("vendor_id"), d.get("product_id")) for d in infos}
            if len(models) == 1:
                vid, pid = next(iter(models))
                if all(type(v) is int and 0 <= v <= 65535 for v in (vid, pid)):
                    device.update(vendorId=vid, productId=pid)
            public_devices.append(device)
        public_devices, twins = fold_charging_twins(public_devices)
        devices, suppressed = suppress_old_connections(public_devices)
        suppressed = (suppressed + twins)[:16]
        health = [dict(value, physicalId=public_id(key.split("|", 1)[1]) if "|" in key else
                       key[len("topology:"):] if key.startswith("topology:device-")
                       and valid_public_target(key[len("topology:"):]) else None)
                  for key, value in self.health.items()][:128]
        errors = [h["provider"] + ": " + h["errorCode"] for h in health if h.get("errorCode")]
        # The existing UI renders errorCode in reader health. Project the safe
        # diagnostic only onto outgoing copies, after collecting real errors;
        # reader state, retries and the errors list must not treat it as failure.
        for item in health:
            summary = item.pop("diagnosticSummary", "")
            if item["provider"] in ("mchose_by_keyboard", "mchose_glw_keyboard", "mchose_qhw_keyboard", "mchose_headset", "atk_headset", "mchose", "rapoo", "rapoo_mt560", "moondrop", "pulsar", "fa6x_passive", "cherry", "lingbao", "zhuaudio", "asus_omni") and summary and not item.get("errorCode"):
                item["errorCode"] = summary
        if complete and unadapted_inventory is not None:
            self.unadapted = unadapted_devices(unadapted_inventory, self.previous)
        return {"schema": 2, "requestId": request_id, "generation": generation, "complete": complete,
                "ready": True, "devicePack": self.device_pack.diagnostic(),
                "devices": devices, "nativeMousePresent": self.native_mouse,
                "unadapted": self.unadapted,
                "suppressed": suppressed,
                "providerCount": len(self.providers), "errors": (list(self.errors) + errors)[:32],
                "importedProviderCount": self.imported_provider_count,
                "health": health, "checkedAtUtc": utcnow(), "packageSmoke": self.package_smoke,
                "probeFamilies": self.probe_families,
                "familyMemory": dict(self.family_memory) if complete else None}

    def _accept(self, reader_key, name, statuses, now, physical="", candidate_count=0, diagnostics=(), failure_code=""):
        group = {}
        for key in self.by_provider.get(reader_key, {}):
            self.dpi_targets.pop(key, None)
        for status in statuses:
            # Accept only the exact APEX5 reader and read-only system readers;
            # the reporting provider and source must agree.
            if status.kind == "gamepad" and (name not in ("flydigi", "betop", "bluetooth", "xinput") or status.source != name):
                continue
            if name == "families" and self._family_stopped(status):
                continue   # the user stopped this device; a poll already in flight must not revive it
            status = replace(status, name=inventory_safe_name(status.name, self._inventory_groups.get(physical, [])))
            key = public_id(status.key)
            if name in self._ambiguous_providers:
                status = replace(status, legacy_key="")
            previous = self.previous.get(key)
            if previous is None and status.legacy_key:
                previous = self.previous.get(public_id(status.legacy_key))
            group[key] = normalise(status, previous, now)
            if group[key].get("dpi", {}) and group[key]["dpi"].get("canSet") and isinstance(getattr(status, "dpi_target", None), dict):
                self.dpi_targets[key] = dict(status.dpi_target)
            self.provider_by_id[key] = name
            source = getattr(status, "model_source", "") or ""
            if name == "families" and source.startswith("family:") and status.physical_id:
                from families import memory_key
                self._family_units[key] = (source[len("family:"):], memory_key(status.physical_id))
                group[key]["canDisable"] = True
            self.physical_by_id[key] = physical
        self.by_provider[reader_key] = group
        if name == "bluetooth":
            self._bluetooth_empty_polls = (0 if any(v["percent"] is not None for v in group.values())
                                           else min(BLUETOOTH_EMPTY_POLL_THRESHOLD, self._bluetooth_empty_polls + 1))
        self.last_poll[reader_key] = time.monotonic()
        self.failures.pop(reader_key, None)
        self.retry_after.pop(reader_key, None)
        self._idle_backoff(reader_key, name, physical, candidate_count, statuses)
        # Passive readers can return history without a new notification.
        # Keep the card, but expose the same idle health as a first silent poll.
        measured = bool(group) and (name not in ("rapoo", "rapoo_mt560", "fa6x_passive") or any(
            s.online and not s.cached and s.level is not None for s in statuses))
        self.health[reader_key] = {"provider": name, "readerId": public_id(reader_key),
                                   "state": "ok" if measured else "unavailable" if physical and candidate_count > 0 else "unsupported" if physical else "no_devices", "deviceCount": len(group),
                                   "reason": "" if measured else "no_measurement" if candidate_count > 0 else "no_declared_battery" if name == "standard_hid" else "no_matched_model" if physical else "no_device",
                                   "scope": "device" if physical else "provider", "checkedAtUtc": now}
        if name in READER_DIAGNOSTICS and isinstance(diagnostics, (list, tuple)):
            if name == "zhuaudio":
                diagnostics = [line for line in diagnostics if isinstance(line, str) and line in ZHUAUDIO_DIAGNOSTICS]
                if not measured and diagnostics:
                    self.health[reader_key]["diagnosticSummary"] = diagnostics[0]
            if name == "cherry":
                diagnostics = [reason for line in diagnostics if isinstance(line, str)
                               for reason in CHERRY_DIAGNOSTICS if line == reason or line.startswith(reason + ";")]
                if not measured:
                    for reason, summary in CHERRY_DIAGNOSTICS.items():
                        if reason in diagnostics:
                            self.health[reader_key]["diagnosticSummary"] = summary
                            break
            if name == "fa6x_passive":
                diagnostics = [line for line in diagnostics if fa6x_diagnostic_summary(line) is not None]
                if not measured and diagnostics:
                    self.health[reader_key]["diagnosticSummary"] = fa6x_diagnostic_summary(diagnostics[0])
            if name == "lingbao":
                diagnostics = [line for line in diagnostics if isinstance(line, str) and line in LINGBAO_DIAGNOSTICS]
                for reason, summary in LINGBAO_DIAGNOSTICS.items():
                    if reason in diagnostics:
                        self.health[reader_key]["diagnosticSummary"] = summary
                        break
            if name == "asus_omni":
                diagnostics = [line for line in diagnostics if isinstance(line, str) and line in ASUS_OMNI_DIAGNOSTICS]
                if diagnostics and not measured:
                    self.health[reader_key]["diagnosticSummary"] = ASUS_OMNI_DIAGNOSTICS[diagnostics[0]]
            self.health[reader_key]["diagnostics"] = [str(line)[:READER_DIAGNOSTIC_LIMIT] for line in diagnostics[-16:]]
            if name == "mchose" and not measured:
                # V3-only protocol-owned failures outrank the identity/proof notes.
                # The full safe proof stays in diagnostics, the first clause fits feedback.
                for line in diagnostics:
                    if isinstance(line, str) and line.startswith((
                            "V3 missing or ambiguous FF01:0001 collection;",
                            "V3 collection path unresolved;",
                            "V3 input report 4D/64 length unproven;",
                            "V3 output report 4D/64 length unproven;",
                            "V3 power query write failed or short;",
                            "V3 power I/O failed;",
                            "no valid V3 power reply;")):
                        if "; declared=" in line and line.startswith("V3 "):
                            compact = line[len("V3 "):].replace(
                                " report 4D/64 length unproven;", " report 4D length unproven;", 1)
                            self.health[reader_key]["diagnosticSummary"] = reader_health_summary(compact)
                        else:
                            self.health[reader_key]["diagnosticSummary"] = reader_health_summary(line.split(";", 1)[0])
                        break
            if name == "pulsar":
                # A cached card can keep the existing health state ok while this
                # poll has no reading. Project its safe result without a failure.
                for line in reversed(diagnostics):
                    if line != "valid battery reply":
                        self.health[reader_key]["diagnosticSummary"] = reader_health_summary(line)
                        break
            if name == "mchose_headset" and not measured:
                for reason in ("接收器固件低于 0008，官方驱动也不支持电量", "接收器固件版本未知，按原方式查询"):
                    if reason in diagnostics:
                        self.health[reader_key]["diagnosticSummary"] = reason
                        break
            if name in ("rapoo", "rapoo_mt560", "moondrop") and not measured and diagnostics:
                # These readers emit only fixed text, numeric descriptor sizes and
                # input report IDs/lengths. No paths, payloads or exceptions.
                self.health[reader_key]["diagnosticSummary"] = reader_health_summary(diagnostics[0])
            if name == "mchose_by_keyboard" and not measured:
                # Feedback needs a short, fixed reason. Keep it separate from
                # errorCode, which would also populate snapshot errors.
                for messages, summary in (
                    (("report IDs/lengths unknown or incompatible: nothing sent",
                      "single byte-array report not proven: nothing sent"), "report sizes unproven"),
                    (("unrecognized wireless receiver name: nothing sent",), "receiver name unrecognized"),
                    (("physical parent unresolved; uniqueness unknown: nothing sent",), "physical parent unresolved"),
                    (("missing or ambiguous eligible collection: nothing sent",), "collection missing or ambiguous"),
                    (("battery I/O failed; connection unknown",), "battery I/O failed"),
                ):
                    if any(line == message or line.startswith(message + ";")
                           for message in messages for line in diagnostics if isinstance(line, str)):
                        self.health[reader_key]["diagnosticSummary"] = summary
                        break
            if name == "atk_headset" and not measured:
                # BatteryHidProvider emits fixed reasons and numeric report proof.
                # Project only known reasons, prioritizing them over the trailing
                # no-valid-reply note. Never copy arbitrary text into feedback.
                for message, summary in (
                    ("report IDs/lengths unknown or incompatible: nothing sent", "report sizes unproven"),
                    ("physical parent unresolved; uniqueness unknown: nothing sent", "physical parent unresolved"),
                    ("missing or ambiguous eligible collection: nothing sent", "collection missing or ambiguous"),
                    ("enumeration failed: nothing sent", "enumeration failed"),
                    ("battery I/O failed; connection unknown", "battery I/O failed"),
                    ("short/failed battery query write; no retry", "battery query write short/failed"),
                    ("device asleep/offline; no new reading; connection unknown", "device asleep/offline"),
                    ("rejected input report length", "input report length rejected"),
                    ("rejected battery reply", "battery reply rejected"),
                    ("no valid reply within deadline/read limit; connection unknown", "no valid battery reply"),
                    ("HID handle close failed", "HID handle close failed"),
                ):
                    if any(line == message or line.startswith(message + ";")
                           for line in diagnostics if isinstance(line, str)):
                        self.health[reader_key]["diagnosticSummary"] = summary
                        break
            if name in ("mchose_glw_keyboard", "mchose_qhw_keyboard") and not measured:
                for message, summary in (
                    ("exact product name mismatch or missing: nothing sent", "product name mismatch"),
                    ("input/output descriptor lengths unknown or incompatible: nothing sent", "report sizes unproven"),
                    ("report IDs unknown, ambiguous or incompatible: nothing sent", "report IDs unproven"),
                    ("missing or ambiguous eligible collection: nothing sent", "collection missing or ambiguous"),
                    ("missing collection path: nothing sent", "collection path unresolved"),
                    ("physical parent unresolved; collection uniqueness unknown: nothing sent", "physical parent unresolved"),
                    ("enumeration failed: nothing sent", "enumeration failed"),
                    ("hidapi unavailable: nothing sent", "hidapi unavailable"),
                    ("battery I/O failed; no measurement, connection unknown", "battery I/O failed"),
                    ("short/failed output write; no retry", "battery query write short/failed"),
                    ("rejected input report length", "input report length rejected"),
                    ("rejected input report ID", "input report ID rejected"),
                    ("no valid reply within deadline/read limit; connection unknown", "no valid battery reply"),
                    ("HID handle close failed", "HID handle close failed"),
                ):
                    if any(line == message or line.startswith(message + ";")
                           for line in diagnostics if isinstance(line, str)):
                        self.health[reader_key]["diagnosticSummary"] = summary
                        break
        # The feedback UI already displays errorCode (up to 48 characters).
        # Never export the provider's legacy log, raw exceptions or HID identity.
        if (name == "razer" and not any(s.online for s in statuses)
                and isinstance(failure_code, str)
                and re.fullmatch(r"razer_(?:no_reply|no_usable_interface|interfaces_cooling_down|"
                                 r"enumerate_failed|open_failed|send_failed|get_failed|short_reply|"
                                 r"busy_timeout|device_timeout_04|foreign_reply|reply_status_[0-9a-f]{2})", failure_code)):
            self.health[reader_key]["errorCode"] = failure_code

    def _idle_backoff(self, reader_key, name, physical, candidate_count, statuses):
        measured = any(s.online and not s.cached and s.level is not None for s in statuses)
        if measured or not physical or candidate_count <= 0 or name in IDLE_BACKOFF_EXEMPT:
            self.idle_misses.pop(reader_key, None)
            self.idle_until.pop(reader_key, None)
            return
        misses = self.idle_misses.get(reader_key, 0) + 1
        self.idle_misses[reader_key] = misses
        if misses >= IDLE_BACKOFF_AFTER:
            step = IDLE_BACKOFF_SECONDS[min(misses - IDLE_BACKOFF_AFTER, len(IDLE_BACKOFF_SECONDS) - 1)]
            self.idle_until[reader_key] = time.monotonic() + step

    def _failure(self, reader_key, name, code):
        for key, value in self.by_provider.get(reader_key, {}).items():
            self.by_provider[reader_key][key] = historical(value)
            self.dpi_targets.pop(key, None)
        count = self.failures.get(reader_key, 0) + 1
        self.failures[reader_key] = count
        wait = min(300, 5 * (2 ** min(count - 1, 6)))
        self.retry_after[reader_key] = time.monotonic() + wait
        self.health[reader_key] = {"provider": name, "readerId": public_id(reader_key), "state": "timeout" if code == "timeout" else "error",
                                  "deviceCount": len(self.by_provider.get(reader_key, {})), "errorCode": code,
                                  "scope": "provider", "checkedAtUtc": utcnow(),
                                  "nextRetryAtUtc": (datetime.now(timezone.utc) + timedelta(seconds=wait)).isoformat().replace("+00:00", "Z")}

    def _dpi_update(self, device_id, dpi):
        """Change only DPI evidence; a control read never renews battery time."""
        if device_id in self.previous:
            self.previous[device_id] = dict(self.previous[device_id], dpi=dpi)
        for group in self.by_provider.values():
            if device_id in group:
                group[device_id] = dict(group[device_id], dpi=dpi)

    def _single_reader(self, slot, command, budget):
        from isolation import IsolationError
        child = self.children[slot]
        try:
            child.start(self._reader_command(command), budget)
            while True:
                result = child.result()
                if result is not None:
                    if not isinstance(result, dict):
                        raise IsolationError("invalid_response")
                    return result
                time.sleep(.01)
        except Exception:
            child.close()
            raise

    def _collect_bluetooth(self):
        """Harvest a bounded query without waiting or changing its evidence."""
        from isolation import IsolationError
        from providers.base import DeviceStatus
        if self._bluetooth_slot is None:
            return False
        try:
            result = self.children[self._bluetooth_slot].result()
            if result is None:
                return False
            self._bluetooth_slot = None
            raw = result.get("statuses", [])
            if not isinstance(raw, list) or len(raw) > MAX_DEVICES:
                raise IsolationError("invalid_response")
            statuses = [DeviceStatus(**value) for value in raw]
            self._accept("bluetooth", "bluetooth", statuses,
                         valid_time(result.get("checkedAtUtc")) or utcnow(),
                         candidate_count=max(0, min(512, int(result.get("candidateCount", 0)))))
        except (IsolationError, TypeError, ValueError, AttributeError) as error:
            self._bluetooth_slot = None
            self._failure("bluetooth", "bluetooth", error.code if isinstance(error, IsolationError) else "invalid_response")
        return True

    def set_dpi(self, device_id, value, target_token, expected_dpi, request_id=0, generation=0):
        """One identity-bound provider write with readback, never a retry."""
        self._collect_bluetooth()
        result = {"deviceId": device_id, "targetToken": target_token,
                  "requestedDpi": value, "confirmedDpi": None, "success": False,
                  "outcomeUnknown": False, "error": "invalid_target", "checkedAtUtc": utcnow()}

        def finish(error=None, unknown=False):
            if error:
                result.update(error=error, outcomeUnknown=unknown)
            if not result["success"]:
                old = self.previous.get(device_id, {}).get("dpi") if isinstance(device_id, str) else None
                if old:
                    stale = dict(old, canSet=False, targetToken=None, error=result["error"])
                    if result["outcomeUnknown"]:
                        stale.update(current=None, checkedAtUtc=None)
                    self._dpi_update(device_id, stale)
                self.dpi_targets.pop(device_id, None)
            snapshot = self._snapshot(request_id, generation)
            snapshot["dpiResult"] = result
            return snapshot

        if (not isinstance(device_id, str) or not re.fullmatch(r"device-[a-f0-9]{24}", device_id)
                or type(value) is not int or not 50 <= value <= 65535
                or type(expected_dpi) is not int or not 50 <= expected_dpi <= 65535
                or not isinstance(target_token, str) or not re.fullmatch(r"[a-f0-9]{64}", target_token)):
            return finish("invalid_request")
        live = self._snapshot(request_id, generation)
        device = next((d for d in live["devices"] if d["id"] == device_id), {})
        dpi = device.get("dpi") or {}
        target = self.dpi_targets.get(device_id)
        physical = self.physical_by_id.get(device_id)
        provider = self.provider_by_id.get(device_id)
        if (self._direct or self.package_smoke or provider not in DPI_PROVIDERS
                or device.get("modelSource") == "pack"
                or provider in ("pulsar", "families") and dpi.get("stageSource") != "onboard"
                or not physical or not target or not device.get("online")
                or dpi.get("canSet") is not True or dpi.get("targetToken") != target_token):
            return finish("stale_target")
        if dpi.get("current") != expected_dpi:
            return finish("current_changed")
        if value not in dpi.get("stages", []):
            return finish("unsupported_dpi")
        reader_key = provider + "|" + physical
        slot = self._affinity.get(reader_key, 0)
        if slot == self._bluetooth_slot:
            # Reclaim only an unfinished read, before any DPI command. This
            # cancellation is not a provider failure or a completed poll.
            self.children[slot].close()
            self._bluetooth_slot = None
        try:
            paths = self._hidlist.interface_paths()
            topology = self._single_reader(slot, {"action": "topology", "paths": sorted(paths) if paths is not None else None}, 6)
            inventory = topology.get("inventory", [])
            if not isinstance(inventory, list) or len(inventory) > 512:
                return finish("invalid_topology")
            selected = [d for d in inventory if d.get("physical_id") == physical
                        and d.get("vendor_id") in DPI_PROVIDERS[provider]]
            selected = self.device_pack.select_inventory(provider, selected)
            if not selected:
                return finish("disconnected")
        except Exception:
            return finish("topology_unavailable")
        try:
            response = self._single_reader(slot, {"action": "set_dpi", "provider": provider,
                    "physicalId": physical, "inventory": selected, "target": dict(target),
                    "dpi": value, "expectedDpi": expected_dpi, "targetToken": target_token}, 12)
            updated = normalise_dpi(response.get("dpi"), usable=True)
            confirmed = response.get("confirmedDpi")
            success = (response.get("success") is True and response.get("outcomeUnknown") is not True
                       and updated is not None and updated.get("current") == value
                       and updated.get("targetToken") == target_token and confirmed == value)
            if updated:
                self._dpi_update(device_id, updated)
            result.update(success=success, confirmedDpi=confirmed if type(confirmed) is int else None,
                          outcomeUnknown=response.get("outcomeUnknown") is True,
                          error=None if success else clean_name(response.get("error") or "readback_failed"),
                          checkedAtUtc=utcnow())
            return finish()
        except Exception:
            # A pipe/driver timeout after dispatch cannot prove whether the one
            # command reached the mouse. Kill the child, drop capability, and
            # require a fresh read before another user-initiated command.
            return finish("outcome_unknown", unknown=True)

    def poll(self, force=False, device_id=None, request_id=0, generation=0, progress=None, park_empty_readers=False,
             physical_id=None, wait_bluetooth=False):
        self._collect_bluetooth()
        if (not valid_public_target(device_id) or not valid_public_target(physical_id)
                or device_id is not None and physical_id is not None
                or physical_id is not None and (self._direct or self.package_smoke)):
            self._failure("selection", "selection", "unknown_target")
            return self._snapshot(request_id, generation)
        if self._direct:
            for provider in self.providers:
                if device_id and self.provider_by_id.get(device_id) != provider.name:
                    continue
                key = provider.name
                try:
                    original = self._hidlist.enumerate
                    if self.device_pack.loaded:
                        self._hidlist.enumerate = lambda vid=0: self.device_pack.select_inventory(provider.name, original(vid))
                    try:
                        statuses = provider.poll()
                    finally:
                        self._hidlist.enumerate = original
                    misses = getattr(provider, "_misses", {})
                    self._accept(key, provider.name, [replace(s, cached=s.cached or bool(misses.get(s.key, 0))) for s in statuses], utcnow(),
                                 diagnostics=reader_diagnostics(provider),
                                 failure_code=provider.failure_code() if provider.name == "razer" else "")
                except Exception:
                    self._failure(key, provider.name, "read_failed")
                if progress:
                    progress(self._snapshot(request_id, generation, False))
            return self._snapshot(request_id, generation)
        try:
            result = self._isolated_poll(force, device_id, request_id, generation, progress, physical_id, wait_bluetooth)
            # Closed-panel, native-only PCs do not need two idle Python readers.
            # Never discard protocol grace/cached state or a known candidate
            # waiting for a usable reply. Foreground polls retain discovery
            # caches, and package smoke keeps children for lifecycle checks.
            if (park_empty_readers and device_id is None and physical_id is None and not self.package_smoke
                    and self._bluetooth_slot is None
                    and not self._deferred
                    and not any(self.by_provider.values()) and self.health
                    and not result.get("errors")
                    and all(h.get("state") in ("no_devices", "unsupported") for h in self.health.values())):
                self.close()
            return result
        except Exception:
            self._failure("manager", "manager", "read_failed")
            self.close()
            return self._snapshot(request_id, generation)

    def _isolated_poll(self, force, device_id, request_id, generation, progress, physical_id=None, wait_bluetooth=False):
        from isolation import IsolationError
        from providers.base import DeviceStatus
        from topology import physical_groups
        if device_id and device_id not in self.provider_by_id:
            self._failure("selection", "selection", "unknown_target")
            return self._snapshot(request_id, generation)
        if physical_id is None:
            self.health.pop("selection", None)
            self.health.pop("manager", None)
        paths = frozenset() if self.package_smoke else self._hidlist.interface_paths()
        if paths != self._bluetooth_paths:
            self._bluetooth_paths = paths
            self._bluetooth_empty_polls = 0
        if physical_id is None:
            self.native_mouse = False if self.package_smoke else None if paths is None else any("vid_373b&pid_1278" in p for p in paths)
        deadline = time.monotonic() + MAX_ROUND_SECONDS
        target_name = self.provider_by_id.get(device_id) if device_id else None
        target_physical = self.physical_by_id.get(device_id) if device_id else None
        wait_bluetooth = wait_bluetooth or target_name == "bluetooth"
        pending, active = [], {}
        priority = set(self._deferred)
        partial_count = 0
        full_round = device_id is None and physical_id is None
        full_inventory = None

        def task_key(item):
            action, name, physical, command, _budget = item
            if action == "topology" and command.get("physicalId") is not None:
                return "topology:" + command["physicalId"]
            return name + ("|" + physical if physical else "")

        def defer(item):
            reader_key = task_key(item)
            self._deferred.add(reader_key)
            self.health[reader_key] = {"provider": item[1], "readerId": public_id(reader_key),
                                      "state": "deferred", "deviceCount": len(self.by_provider.get(reader_key, {})),
                                      "reason": "round_budget", "scope": "device" if item[2] else "provider",
                                      "checkedAtUtc": utcnow()}

        if self.package_smoke:
            self.imported_provider_count = 0
            pending.append(("smoke", "package", "", {"action": "smoke"}, 5))
        else:
            if not device_id or target_name not in SYSTEM_PROVIDERS:
                command = {"action": "topology", "paths": sorted(paths) if paths is not None else None}
                if physical_id is not None:
                    command["physicalId"] = physical_id
                pending.append(("topology", "topology", "", command, 6))
            if "xinput" in self.providers and physical_id is None and (not device_id or target_name == "xinput"):
                pending.append(("query", "xinput", "", {"action": "query", "provider": "xinput"}, 5))
            if self._bluetooth_slot is None and physical_id is None and (not device_id or target_name == "bluetooth"):
                pending.append(("query", "bluetooth", "", {"action": "query", "provider": "bluetooth"}, 14))
        while pending or active or wait_bluetooth and self._bluetooth_slot is not None:
            # Choose tasks before slots so affinity cannot let a new query
            # overtake an eligible query deferred from the previous round.
            for item in sorted(pending, key=lambda item: (item[0] == "query", task_key(item) not in priority)):
                action, name, physical, command, budget = item
                reader_key = task_key(item)
                minimum = 30 if name == "standard_hid" else 0
                if name == "bluetooth":
                    minimum = (BLUETOOTH_EMPTY_POLL_SECONDS if self._bluetooth_empty_polls >= BLUETOOTH_EMPTY_POLL_THRESHOLD
                               else BLUETOOTH_POLL_SECONDS)
                if action == "query" and not force:
                    if (device_id is None and physical_id is None
                            and time.monotonic() < self.idle_until.get(reader_key, 0)):
                        # Idle back-off keeps the last health; it is not a failure.
                        pending.remove(item)
                        continue
                    if time.monotonic() < self.retry_after.get(reader_key, 0):
                        self.health[reader_key] = dict(self.health.get(reader_key, {}), provider=name, state="backoff")
                        pending.remove(item)
                        continue
                    if time.monotonic() - self.last_poll.get(reader_key, -1e20) < minimum:
                        pending.remove(item)
                        continue
                if action == "query" and deadline - time.monotonic() < budget:
                    pending.remove(item)
                    defer(item)
                    continue
                for slot, child in enumerate(self.children):
                    if slot in active or slot == self._bluetooth_slot:
                        continue
                    if reader_key in self._affinity and self._affinity[reader_key] != slot:
                        continue
                    if name == "bluetooth":
                        if slot != self._affinity.get(reader_key, 1):
                            continue
                        if any(other[0] != "query" for other in pending) or any(other[0] != "query" for other in active.values()):
                            continue
                        # Ordinary Bluetooth queries follow HID; a deferred
                        # query gets the same next-round priority as HID.
                        if reader_key not in priority and (active or any(other[1] != "bluetooth" for other in pending)):
                            continue
                    if physical and any(v[2] == physical for v in active.values()):
                        continue
                    pending.remove(item)
                    if action == "query" and deadline - time.monotonic() < budget:
                        defer(item)
                        break
                    self._deferred.discard(reader_key)
                    try:
                        child.start(self._reader_command(command), budget if action == "query" else min(budget, max(0.01, deadline - time.monotonic())))
                        self._affinity[reader_key] = slot
                        if name == "bluetooth":
                            self._bluetooth_slot = slot
                        else:
                            active[slot] = item
                    except IsolationError as error:
                        self._failure(reader_key, name, error.code)
                    break
            if self._collect_bluetooth() and progress and partial_count < MAX_PARTIALS:
                partial_count += 1
                progress(self._snapshot(request_id, generation, False))
            for slot in list(active):
                item = active[slot]
                action, name, physical, command, budget = item
                reader_key = task_key(item)
                try:
                    result = self.children[slot].result()
                    if result is None:
                        continue
                    active.pop(slot)
                    if action == "topology":
                        inventory = result.get("inventory", [])
                        if not isinstance(inventory, list) or len(inventory) > 512:
                            raise IsolationError("invalid_response")
                        inventory = [d for d in inventory if (d.get("vendor_id"), d.get("product_id")) not in NATIVE]
                        if full_round:
                            full_inventory = inventory
                        groups = dict(list(physical_groups(inventory).items())[:MAX_DEVICES])
                        if physical_id is not None:
                            groups = {key: infos for key, infos in groups.items() if public_id(key) == physical_id}
                        if full_round:
                            if set(groups) != set(self._inventory_groups):
                                # A plugged or removed receiver resumes idle readers at once.
                                self.idle_misses.clear()
                                self.idle_until.clear()
                            self._inventory_groups = dict(groups)
                        else:
                            if physical_id is not None:
                                self._inventory_groups = {key: infos for key, infos in self._inventory_groups.items()
                                                          if public_id(key) != physical_id}
                            self._inventory_groups.update(groups)
                        # A successful new topology supersedes its own previous
                        # failure. Do not clear independent reader faults.
                        self.health.pop(reader_key, None)
                        self.failures.pop(reader_key, None)
                        self.retry_after.pop(reader_key, None)
                        if physical_id is not None:
                            self._affinity.pop(reader_key, None)
                        elif full_round:
                            # A complete successful discovery also supersedes
                            # earlier failures of our partial topology requests.
                            for state in (self.health, self.failures, self.retry_after, self._affinity):
                                for key in list(state):
                                    if key.startswith("topology:device-"):
                                        state.pop(key, None)
                        live_readers = set(SYSTEM_PROVIDERS)
                        for provider_name in self.providers:
                            if provider_name in SYSTEM_PROVIDERS or (target_name and provider_name != target_name):
                                continue
                            matches = []
                            for group_id, infos in groups.items():
                                if target_physical and group_id != target_physical:
                                    continue
                                infos = self.device_pack.select_inventory(provider_name, infos)
                                if not infos:
                                    continue
                                vids = {d.get("vendor_id") for d in infos}
                                if provider_name != "standard_hid" and not vids.intersection(self._provider_vids(provider_name)):
                                    continue
                                if (provider_name in ("delux", "vgn_ry_ms", "sony_inzone", "asus_headset",
                                                      "weikav_nut75", "veekos_shine60", "eweadn_x23", "eweadn_x87", "mchose_g3v2", "asus_azoth")
                                        and not recognized_candidate_count(provider_name, infos)):
                                    continue
                                matches.append((group_id, infos))
                            if not matches and full_round:
                                self.health[provider_name] = {"provider": provider_name, "readerId": public_id(provider_name), "state": "no_devices", "deviceCount": 0, "scope": "provider"}
                            elif matches and physical_id is None:
                                self.health.pop(provider_name, None)
                            for group_id, infos in matches:
                                key = provider_name + "|" + group_id
                                live_readers.add(key)
                                if len(pending) + len(active) + int(self._bluetooth_slot is not None) >= MAX_READER_TASKS:
                                    self._failure(key, provider_name, "capacity_limit")
                                    continue
                                seconds = 10 if provider_name == "lofree" else 12 if provider_name == "logitech" else 5 if provider_name == "standard_hid" else 7
                                pending.append(("query", provider_name, group_id,
                                                {"action": "query", "provider": provider_name, "physicalId": group_id, "inventory": infos}, seconds))
                            if len(matches) > 1 and physical_id is None:
                                self._ambiguous_providers.add(provider_name)
                            elif full_round:
                                self._ambiguous_providers.discard(provider_name)
                        # Reconcile physical readers even if their first query
                        # failed and never produced a by_provider entry. A
                        # targeted round only supersedes its selected reader;
                        # independent manager/provider faults retain ownership.
                        states = (self.by_provider, self.health, self.last_poll,
                                  self._affinity, self.retry_after, self.failures)
                        if physical_id is not None:
                            removed = {key for state in states for key in state
                                       if "|" in key and public_id(key.split("|", 1)[1]) == physical_id
                                       and key not in live_readers}
                        elif target_name:
                            removed = ({target_name + "|" + target_physical} - live_readers
                                       if target_physical else set())
                        else:
                            removed = {key for state in states for key in state
                                       if "|" in key and key not in live_readers}
                        for key in removed:
                            for device_key in self.by_provider.get(key, {}):
                                self.dpi_targets.pop(device_key, None)
                        for state in states:
                            for key in removed:
                                state.pop(key, None)
                        self._deferred.difference_update(removed)
                    elif action == "smoke":
                        count = result.get("importedProviderCount")
                        if (type(count) is not int or count != len(self.providers)
                                or result.get("providerCount") != count):
                            raise IsolationError("invalid_response")
                        self.imported_provider_count = count
                        self.health["package"] = {"provider": "package", "state": "ok", "scope": "provider", "deviceCount": 0}
                    else:
                        raw = result.get("statuses", [])
                        if not isinstance(raw, list) or len(raw) > MAX_DEVICES:
                            raise IsolationError("invalid_response")
                        statuses = [DeviceStatus(**value) for value in raw]
                        if physical_id is not None:
                            # One receiver cannot establish a unique global
                            # legacy identity or authorise a cache migration.
                            statuses = [replace(status, legacy_key="") for status in statuses]
                        if name == "families":
                            self._merge_family_memory(result.get("familyMemory"))
                        self._accept(reader_key, name, statuses, valid_time(result.get("checkedAtUtc")) or utcnow(), physical,
                                     candidate_count=max(0, min(512, int(result.get("candidateCount", 0)))),
                                     diagnostics=result.get("diagnostics", ()),
                                     failure_code=result.get("failureCode", ""))
                    if progress and partial_count < MAX_PARTIALS:
                        partial_count += 1
                        progress(self._snapshot(request_id, generation, False))
                except IsolationError as error:
                    active.pop(slot, None)
                    self._failure(reader_key, name, error.code)
                    if physical_id is not None and action == "topology":
                        self._affinity.pop(reader_key, None)
                    if progress and partial_count < MAX_PARTIALS:
                        partial_count += 1
                        progress(self._snapshot(request_id, generation, False))
            if time.monotonic() >= deadline:
                for slot, item in active.items():
                    self.children[slot].close()
                    self._failure(task_key(item), item[1], "timeout")
                if self._bluetooth_slot is not None:
                    self.children[self._bluetooth_slot].close()
                    self._bluetooth_slot = None
                    self._failure("bluetooth", "bluetooth", "timeout")
                for item in pending:
                    if item[0] == "query":
                        defer(item)
                    else:
                        self._failure(task_key(item), item[1], "round_budget")
                break
            if pending or active or wait_bluetooth and self._bluetooth_slot is not None:
                time.sleep(0.01)
        return self._snapshot(request_id, generation, unadapted_inventory=full_inventory)


def main():
    logging.getLogger("halo_battery").addHandler(logging.NullHandler())
    logging.getLogger("halo_battery").propagate = False
    # Pipe commands are UTF-8 regardless of the launching Windows console's
    # legacy code page. utf-8-sig also accepts a host's optional initial BOM.
    if hasattr(sys.stdin, "reconfigure"):
        sys.stdin.reconfigure(encoding="utf-8-sig")
    parser = argparse.ArgumentParser(description="Private battery reader")
    parser.add_argument("--device-pack")
    parser.add_argument("--reader-child", action="store_true")
    parser.add_argument("--package-smoke", action="store_true")
    parser.add_argument("--once", action="store_true")
    parser.add_argument("--probe-families", choices=("on", "off"), default="on",
                        help="whole-vendor family probing (default on; the user can switch it off)")
    args = parser.parse_args()
    if args.reader_child:
        reader_child()
        return
    engine = BatteryEngine(package_smoke=args.package_smoke, device_pack=load_pack(args.device_pack),
                           probe_families=args.probe_families == "on")
    try:
        if args.once:
            print(json.dumps(engine.poll(wait_bluetooth=True), ensure_ascii=True), flush=True)
            return
        for line in stdin_lines(sys.stdin, STDIN_LINE_LIMIT):
            if line is None:
                # Too large to parse; answer and keep serving instead of exiting.
                print(json.dumps({"reply": "family_memory", "requestId": 0, "ok": False,
                                  "error": "payload_too_large"}, ensure_ascii=True), flush=True)
                continue
            try:
                # Some Windows .NET hosts prefix redirected UTF-8 stdin with
                # a BOM. It is stream metadata, not part of the JSON command.
                command = json.loads(line.lstrip("\ufeff"))
                if command.get("command") == "exit":
                    break
                action = command.get("command")
                if action == "family_memory":
                    # Host API for probe read memory (C1). Replies carry "reply" so
                    # the host never mistakes them for a battery snapshot.
                    request_id = command.get("requestId", 0)
                    if type(request_id) is not int or request_id < 0 or not valid_public_target(command.get("deviceId")):
                        continue
                    result = engine.family_memory_command(command.get("op"), command.get("deviceId"),
                                                          command.get("memoryKey"), command.get("familyMemory"))
                    result.update(reply="family_memory", requestId=request_id)
                    echo = command.get("generation")
                    if type(echo) is int and echo >= 0:
                        result["generation"] = echo
                    print(json.dumps(result, ensure_ascii=True), flush=True)
                    continue
                if action not in ("poll", "set_dpi", "ready"):
                    continue
                request_id, generation = command.get("requestId", 0), command.get("generation", 0)
                if type(request_id) is not int or type(generation) is not int or min(request_id, generation) < 0:
                    continue
                target = command.get("deviceId")
                physical = command.get("physicalId")
                if (not valid_public_target(target) or not valid_public_target(physical)
                        or target is not None and physical is not None
                        or "physicalId" in command and (physical is None or "deviceId" in command or action != "poll")):
                    continue
                emit = lambda result: print(json.dumps(result, ensure_ascii=True), flush=True)
                if action == "ready":
                    # Explicit startup probe; does not enumerate/open hardware.
                    result = engine._snapshot(request_id, generation)
                elif action == "set_dpi":
                    result = engine.set_dpi(target, command.get("dpi"), command.get("targetToken"),
                                            command.get("expectedDpi"), request_id, generation)
                else:
                    result = engine.poll(force=command.get("force") is True, device_id=target,
                                         request_id=request_id, generation=generation, progress=emit,
                                         park_empty_readers=command.get("parkEmptyReaders") is True, physical_id=physical,
                                         wait_bluetooth=command.get("parkEmptyReaders") is True)
                emit(result)
            except Exception:
                print(json.dumps({"schema": 2, "requestId": 0, "generation": 0, "complete": True,
                                  "ready": True, "devicePack": engine.device_pack.diagnostic(),
                                  "error": "Battery query did not complete", "devices": [],
                                  "errors": ["manager: invalid_request"]}), flush=True)
    finally:
        engine.close()


if __name__ == "__main__":
    main()
