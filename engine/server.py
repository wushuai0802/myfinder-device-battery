"""Private offline manager; bounded readers and request-tagged partial snapshots.

No GUI, socket, updater or persistent service. Vendor
readers see only one proven physical instance at a time. Two lazy child slots
are controlled by kill-on-close jobs, including nested Bluetooth PowerShell.
Verified Logitech DPI controls share the same bounded, identity-scoped readers.
"""
from __future__ import annotations

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

PROVIDERS = (
    ("razer", "RazerProvider"), ("audeze", "AudezeProvider"),
    ("wlmouse", "WLmouseProvider"), ("mchose", "MchoseProvider"),
    ("hyperx_alpha2", "HyperXAlpha2Provider"), ("hyperx_cloud3", "HyperXCloud3Provider"),
    ("hyperx", "HyperXProvider"), ("keychron", "KeychronProvider"),
    ("pulsar", "PulsarProvider"), ("jbl", "JblProvider"),
    ("logitech", "LogitechProvider"), ("steelseries", "SteelSeriesProvider"),
    ("barracuda", "BarracudaProvider"), ("asus", "AsusProvider"),
    ("gwolves", "GWolvesProvider"), ("lofree", "LofreeProvider"),
    ("astro", "AstroProvider"), ("corsair", "CorsairProvider"),
    ("lamzu", "LamzuProvider"), ("am_infinity", "AmInfinityProvider"),
    ("steelseries_elite", "SteelSeriesEliteProvider"),
    ("logitech_centurion", "LogitechCenturionProvider"),
    ("hyperx_cloud3s", "HyperXCloud3SProvider"), ("nzxt", "NzxtProvider"),
    ("bluetooth", "BluetoothProvider"),
)
PROVIDER_VIDS = {
    "razer": (0x1532,), "audeze": (0x3329,), "wlmouse": (0x36A7,),
    "mchose": (0x5253, 0x3837, 0xA8A5, 0x2023), "hyperx_alpha2": (0x03F0,),
    "hyperx_cloud3": (0x03F0,), "hyperx": (0x03F0,), "keychron": (0x3434,),
    "pulsar": (0x3554, 0x373B, 0x3770, 0x3710), "jbl": (0x0ECB,),
    "logitech": (0x046D,), "steelseries": (0x1038,), "barracuda": (0x1532,),
    "asus": (0x0B05,), "gwolves": (0x33E4,), "lofree": (0x388D,),
    "astro": (0x046D,), "corsair": (0x1B1C,), "lamzu": (0x373E,),
    "am_infinity": (0x3151,), "steelseries_elite": (0x1038,),
    "logitech_centurion": (0x046D,), "hyperx_cloud3s": (0x03F0,), "nzxt": (0x1E71,),
}
NATIVE = {(0x373B, 0x1278), (0x03F0, 0x098D)}
MAX_DEVICES = 64
MAX_RETAIN_SECONDS = 300
MAX_ROUND_SECONDS = 36
MAX_PARTIALS = 96
MAX_READER_TASKS = 96
NO_CHARGING_EVIDENCE = {"bluetooth", "standard_hid", "lofree", "keychron", "am_infinity", "astro"}


def utcnow():
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def clean_name(value):
    return "".join(c for c in str(value or "Wireless device") if c.isprintable())[:80]


def public_id(key):
    return "device-" + hashlib.sha256(str(key).encode("utf-8")).hexdigest()[:24]


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
    if status.kind in ("mouse", "headset", "keyboard"):
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
    acquired = online and value is not None and not cached
    fresh = acquired and not system
    if not acquired and old:
        value = old.get("percent")
    measured = (valid_time(status.measured_at_utc) or now) if fresh else old.get("measuredAtUtc")
    queried = status.queried_at_utc or (now if acquired else old.get("queriedAtUtc"))
    # Legacy confirmedAt is the property query time for Bluetooth, not a
    # fabricated device measurement timestamp. measuredAt remains null there.
    confirmed = measured if fresh else (queried if acquired and system else old.get("confirmedAtUtc"))
    charging_known = (status.charging_known if status.charging_known is not None
                      else status.source not in NO_CHARGING_EVIDENCE and isinstance(status.charging, bool))
    charging = bool(status.charging) if fresh and charging_known else None
    quality = "cached" if not acquired else "system_property" if system else "device_reply"
    from catalog import verification_for
    verification_level, verification_label = verification_for(status.source)
    return {
        "id": public_id(status.key), "legacyId": public_id(status.legacy_key) if status.legacy_key else None,
        "physicalId": public_id(status.physical_id) if status.physical_id else None,
        "unitId": public_id(status.physical_id + ":" + status.unit_id) if status.physical_id and status.unit_id else None,
        "name": clean_name(status.name), "kind": kind_of(status), "percent": value,
        "charging": charging, "chargingKnown": charging is not None,
        "online": online, "connectionState": ("connected" if online else "disconnected") if connection_known else "unconfirmed",
        "fresh": fresh, "approximate": bool(status.approx), "provider": status.source or "unknown",
        "transport": "Bluetooth" if status.via == "bluetooth" or system else "USB / 2.4G",
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
        "dpi": normalise_dpi(getattr(status, "dpi", None) or old.get("dpi"),
                             usable=bool(getattr(status, "dpi", None)) and online and not cached
                             and status.source == "logitech" and kind_of(status) == "mouse"),
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


def scoped_poll(provider, inventory, physical_id):
    """Restrict the existing protocol to one physical receiver; retain slots."""
    from providers import hidlist
    original, paths = hidlist.enumerate, hidlist.interface_paths
    selected = [d for d in inventory if d.get("physical_id") == physical_id
                and (d.get("vendor_id"), d.get("product_id")) not in NATIVE]
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
    for line in sys.stdin.buffer:
        if len(line) > MAX_LINE:
            break
        sequence = None
        try:
            command = json.loads(line, object_hook=wire_decode)
            sequence = command.get("sequence")
            action = command.get("action")
            if action == "smoke":
                from catalog import validate_catalog
                validate_catalog()
                for name, _ in PROVIDERS:
                    provider_class(name)()
                provider_class("standard_hid")()
                response = {"providerCount": len(PROVIDERS) + 1, "devices": []}
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
                                 if k[0] == "bluetooth" or public_id(k[1]) != physical or k[1] in present}
                    response = {"inventory": selected}
                else:
                    # Retry inaccessible descriptors at a bounded rate. Ordinary
                    # polls share one topology and do not enumerate 26 times.
                    if current != topology_paths or time.monotonic() - topology_at >= 30:
                        inventory = discover(current, NATIVE)
                        topology_paths, topology_at = current, time.monotonic()
                    present = {d["physical_id"] for d in inventory}
                    instances = {k: v for k, v in instances.items() if k[1] in present or k[0] == "bluetooth"}
                    response = {"inventory": inventory}
            elif action == "query":
                name, physical = command["provider"], command.get("physicalId", "")
                key = (name, physical)
                if key not in instances:
                    if len(instances) >= 128:
                        instances.clear()
                    instances[key] = provider_class(name)()
                provider = instances[key]
                if name == "bluetooth":
                    statuses = provider.poll()
                    misses = getattr(provider, "_misses", {})
                    statuses = [replace(s, cached=s.cached or bool(misses.get(s.key, 0))) for s in statuses]
                else:
                    statuses = scoped_poll(provider, command.get("inventory", []), physical)
                candidates = getattr(provider, "candidate_count", None)
                if candidates is None:
                    candidates = recognized_candidate_count(name, command.get("inventory", []))
                response = {"statuses": [asdict(s) for s in statuses[:MAX_DEVICES]], "checkedAtUtc": utcnow(),
                            "candidateCount": candidates}
            elif action == "set_dpi":
                # Only the selected physical receiver reaches the provider. The
                # provider re-reads slot/unit, current value and supported range
                # immediately before its one volatile write.
                if command.get("provider") != "logitech":
                    raise ValueError("invalid dpi provider")
                from providers.logitech import set_device_dpi
                selected = [d for d in command.get("inventory", [])
                            if d.get("physical_id") == command.get("physicalId")
                            and d.get("vendor_id") == 0x046D]
                response = set_device_dpi(selected, command.get("target"), command.get("dpi"),
                                          command.get("expectedDpi"), command.get("targetToken"))
            else:
                raise ValueError("invalid action")
            response["sequence"] = sequence
        except Exception:
            response = {"sequence": sequence, "errorCode": "read_failed"}
        print(json.dumps(response, ensure_ascii=True, default=wire_encode), flush=True)


def recognized_candidate_count(name, inventory):
    """Only explicit existing maps are evidence of a supported candidate.

    Unknown PID within a brand is not a broken supported device. This affects
    health text only; the existing provider retains its command-match guards.
    """
    if name in ("bluetooth", "standard_hid"):
        return 0
    module = importlib.import_module("providers." + name)
    pids = set()
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
    return len({(d.get("vendor_id"), d.get("product_id")) for d in inventory if d.get("product_id") in pids})


class BatteryEngine:
    def __init__(self, providers=None, include_native=False, package_smoke=False, process_factory=None):
        from providers import hidlist
        from isolation import ReaderProcess
        self._hidlist = hidlist
        self._direct = providers is not None
        self.providers = providers if providers is not None else [name for name, _ in PROVIDERS] + ["standard_hid"]
        self.package_smoke = package_smoke
        self.previous = {}
        self.by_provider = {}
        self.health = {}
        self.last_poll = {}
        self.failures = {}
        self.retry_after = {}
        self.physical_by_id = {}
        self.dpi_targets = {}
        self.provider_by_id = {}
        self.errors = []
        self._affinity = {}
        self._ambiguous_providers = set()
        self.children = [(process_factory or ReaderProcess)(), (process_factory or ReaderProcess)()]
        self.native_mouse = None
        self._generation = 0
        if self._direct and not include_native:
            original = hidlist.enumerate
            hidlist.enumerate = lambda vid=0: [d for d in original(vid) if (d.get("vendor_id"), d.get("product_id")) not in NATIVE]

    def close(self):
        for child in self.children:
            child.close()

    def _snapshot(self, request_id=0, generation=0, complete=True):
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
        devices = sorted(self.previous.values(), key=lambda d: ({"mouse": 0, "keyboard": 1, "headset": 2}.get(d["kind"], 3), d["name"], d["id"]))
        health = [dict(value, physicalId=public_id(key.split("|", 1)[1]) if "|" in key else
                       key[len("topology:"):] if key.startswith("topology:device-")
                       and valid_public_target(key[len("topology:"):]) else None)
                  for key, value in self.health.items()][:128]
        errors = [h["provider"] + ": " + h["errorCode"] for h in health if h.get("errorCode")]
        return {"schema": 2, "requestId": request_id, "generation": generation, "complete": complete,
                "devices": devices, "nativeMousePresent": self.native_mouse,
                "providerCount": len(self.providers), "errors": (list(self.errors) + errors)[:32],
                "health": health, "checkedAtUtc": utcnow(), "packageSmoke": self.package_smoke}

    def _accept(self, reader_key, name, statuses, now, physical="", candidate_count=0):
        group = {}
        for key in self.by_provider.get(reader_key, {}):
            self.dpi_targets.pop(key, None)
        for status in statuses:
            if status.kind == "gamepad":
                continue
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
            self.physical_by_id[key] = physical
        self.by_provider[reader_key] = group
        self.last_poll[reader_key] = time.monotonic()
        self.failures.pop(reader_key, None)
        self.retry_after.pop(reader_key, None)
        self.health[reader_key] = {"provider": name, "readerId": public_id(reader_key),
                                   "state": "ok" if group else "unavailable" if physical and candidate_count > 0 else "unsupported" if physical else "no_devices", "deviceCount": len(group),
                                   "reason": "" if group else "no_measurement" if candidate_count > 0 else "no_declared_battery" if name == "standard_hid" else "no_matched_model" if physical else "no_device",
                                   "scope": "device" if physical else "provider", "checkedAtUtc": now}

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
            child.start(command, budget)
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

    def set_dpi(self, device_id, value, target_token, expected_dpi, request_id=0, generation=0):
        """One identity-bound volatile write with readback, never a retry."""
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
        if (self._direct or self.package_smoke or self.provider_by_id.get(device_id) != "logitech"
                or not physical or not target or not device.get("online")
                or dpi.get("canSet") is not True or dpi.get("targetToken") != target_token):
            return finish("stale_target")
        if dpi.get("current") != expected_dpi:
            return finish("current_changed")
        if value not in dpi.get("stages", []):
            return finish("unsupported_dpi")
        reader_key = "logitech|" + physical
        slot = self._affinity.get(reader_key, 0)
        try:
            paths = self._hidlist.interface_paths()
            topology = self._single_reader(slot, {"action": "topology", "paths": sorted(paths) if paths is not None else None}, 6)
            inventory = topology.get("inventory", [])
            if not isinstance(inventory, list) or len(inventory) > 512:
                return finish("invalid_topology")
            selected = [d for d in inventory if d.get("physical_id") == physical
                        and d.get("vendor_id") == 0x046D]
            if not selected:
                return finish("disconnected")
        except Exception:
            return finish("topology_unavailable")
        try:
            response = self._single_reader(slot, {"action": "set_dpi", "provider": "logitech",
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
             physical_id=None):
        if (not valid_public_target(device_id) or not valid_public_target(physical_id)
                or device_id is not None and physical_id is not None
                or physical_id is not None and (self._direct or self.package_smoke)):
            self._failure("selection", "selection", "unknown_target")
            return self._snapshot(request_id, generation)
        self._generation += 1
        token = self._generation
        if self._direct:
            for provider in self.providers:
                if device_id and self.provider_by_id.get(device_id) != provider.name:
                    continue
                key = provider.name
                try:
                    statuses = provider.poll()
                    misses = getattr(provider, "_misses", {})
                    self._accept(key, provider.name, [replace(s, cached=s.cached or bool(misses.get(s.key, 0))) for s in statuses], utcnow())
                except Exception:
                    self._failure(key, provider.name, "read_failed")
                if progress:
                    progress(self._snapshot(request_id, generation, False))
            return self._snapshot(request_id, generation)
        try:
            result = self._isolated_poll(force, device_id, request_id, generation, progress, token, physical_id)
            # Closed-panel, native-only PCs do not need two idle Python readers.
            # Never discard protocol grace/cached state or a known candidate
            # waiting for a usable reply. Foreground polls retain discovery
            # caches, and package smoke keeps children for lifecycle checks.
            if (park_empty_readers and device_id is None and physical_id is None and not self.package_smoke
                    and not any(self.by_provider.values()) and self.health
                    and not result.get("errors")
                    and all(h.get("state") in ("no_devices", "unsupported") for h in self.health.values())):
                self.close()
            return result
        except Exception:
            self._failure("manager", "manager", "read_failed")
            self.close()
            return self._snapshot(request_id, generation)

    def _isolated_poll(self, force, device_id, request_id, generation, progress, token, physical_id=None):
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
        if physical_id is None:
            self.native_mouse = False if self.package_smoke else None if paths is None else any("vid_373b&pid_1278" in p for p in paths)
        deadline = time.monotonic() + MAX_ROUND_SECONDS
        target_name = self.provider_by_id.get(device_id) if device_id else None
        target_physical = self.physical_by_id.get(device_id) if device_id else None
        pending, active = [], {}
        partial_count = 0
        full_round = device_id is None and physical_id is None

        def task_key(item):
            action, name, physical, command, _budget = item
            if action == "topology" and command.get("physicalId") is not None:
                return "topology:" + command["physicalId"]
            return name + ("|" + physical if physical else "")

        if self.package_smoke:
            pending.append(("smoke", "package", "", {"action": "smoke"}, 5))
        else:
            if not device_id or target_name != "bluetooth":
                command = {"action": "topology", "paths": sorted(paths) if paths is not None else None}
                if physical_id is not None:
                    command["physicalId"] = physical_id
                pending.append(("topology", "topology", "", command, 6))
            if physical_id is None and (not device_id or target_name == "bluetooth"):
                pending.append(("query", "bluetooth", "", {"action": "query", "provider": "bluetooth"}, 14))
        while pending or active:
            if self._generation != token:
                self.close()
                break
            for slot, child in enumerate(self.children):
                if slot in active:
                    continue
                for index, item in enumerate(pending):
                    action, name, physical, command, budget = item
                    reader_key = task_key(item)
                    if reader_key in self._affinity and self._affinity[reader_key] != slot:
                        continue
                    if physical and any(v[2] == physical for v in active.values()):
                        continue
                    pending.pop(index)
                    minimum = 60 if name == "bluetooth" else 30 if name == "standard_hid" else 0
                    if action == "query" and not force:
                        if time.monotonic() < self.retry_after.get(reader_key, 0):
                            self.health[reader_key] = dict(self.health.get(reader_key, {}), provider=name, state="backoff")
                            break
                        if time.monotonic() - self.last_poll.get(reader_key, -1e20) < minimum:
                            break
                    try:
                        child.start(command, min(budget, max(0.01, deadline - time.monotonic())))
                        self._affinity[reader_key] = slot
                        active[slot] = item
                    except IsolationError as error:
                        self._failure(reader_key, name, error.code)
                    break
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
                        groups = dict(list(physical_groups(inventory).items())[:MAX_DEVICES])
                        if physical_id is not None:
                            groups = {key: infos for key, infos in groups.items() if public_id(key) == physical_id}
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
                        live_readers = {"bluetooth"}
                        for provider_name in self.providers:
                            if provider_name == "bluetooth" or (target_name and provider_name != target_name):
                                continue
                            matches = []
                            for group_id, infos in groups.items():
                                if target_physical and group_id != target_physical:
                                    continue
                                vids = {d.get("vendor_id") for d in infos}
                                if provider_name != "standard_hid" and not vids.intersection(PROVIDER_VIDS.get(provider_name, ())):
                                    continue
                                matches.append((group_id, infos))
                            if not matches and full_round:
                                self.health[provider_name] = {"provider": provider_name, "readerId": public_id(provider_name), "state": "no_devices", "deviceCount": 0, "scope": "provider"}
                            elif matches and physical_id is None:
                                self.health.pop(provider_name, None)
                            for group_id, infos in matches:
                                key = provider_name + "|" + group_id
                                live_readers.add(key)
                                if len(pending) + len(active) >= MAX_READER_TASKS:
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
                    elif action == "smoke":
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
                        self._accept(reader_key, name, statuses, valid_time(result.get("checkedAtUtc")) or utcnow(), physical,
                                     candidate_count=max(0, min(512, int(result.get("candidateCount", 0)))))
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
                for item in pending:
                    self._failure(task_key(item), item[1], "round_budget")
                break
            if pending or active:
                time.sleep(0.01)
        return self._snapshot(request_id, generation)


def main():
    logging.getLogger("halo_battery").addHandler(logging.NullHandler())
    logging.getLogger("halo_battery").propagate = False
    # Pipe commands are UTF-8 regardless of the launching Windows console's
    # legacy code page. utf-8-sig also accepts a host's optional initial BOM.
    if hasattr(sys.stdin, "reconfigure"):
        sys.stdin.reconfigure(encoding="utf-8-sig")
    if "--reader-child" in sys.argv:
        reader_child()
        return
    engine = BatteryEngine(package_smoke="--package-smoke" in sys.argv)
    try:
        if "--once" in sys.argv:
            print(json.dumps(engine.poll(), ensure_ascii=True), flush=True)
            return
        for line in sys.stdin:
            if len(line) > 4096:
                break
            try:
                # Some Windows .NET hosts prefix redirected UTF-8 stdin with
                # a BOM. It is stream metadata, not part of the JSON command.
                command = json.loads(line.lstrip("\ufeff"))
                if command.get("command") == "exit":
                    break
                action = command.get("command")
                if action not in ("poll", "set_dpi"):
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
                if action == "set_dpi":
                    result = engine.set_dpi(target, command.get("dpi"), command.get("targetToken"),
                                            command.get("expectedDpi"), request_id, generation)
                else:
                    result = engine.poll(force=command.get("force") is True, device_id=target,
                                         request_id=request_id, generation=generation, progress=emit,
                                         park_empty_readers=command.get("parkEmptyReaders") is True, physical_id=physical)
                emit(result)
            except Exception:
                print(json.dumps({"schema": 2, "requestId": 0, "generation": 0, "complete": True,
                                  "error": "Battery query did not complete", "devices": [],
                                  "errors": ["manager: invalid_request"]}), flush=True)
    finally:
        engine.close()


if __name__ == "__main__":
    main()
