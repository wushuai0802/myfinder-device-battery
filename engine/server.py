"""Private offline manager; bounded readers and request-tagged partial snapshots.

No new protocol probes, GUI, socket, updater or persistent service. Vendor
readers see only one proven physical instance at a time. Two lazy child slots
are controlled by kill-on-close jobs, including nested Bluetooth PowerShell.
"""
from __future__ import annotations

import hashlib
import importlib
import json
import logging
import math
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
    }


def historical(value):
    return dict(value, fresh=False, charging=None, chargingKnown=False,
                chargingCheckedAtUtc=None, connectionState="unconfirmed",
                sourceQuality="cached", measurementState="cached" if value.get("percent") is not None else "unknown")


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
                # Retry inaccessible descriptors at a bounded rate. Ordinary
                # polls share a single topology and do not enumerate 26 times.
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
            stamp = value.get("confirmedAtUtc")
            valid = valid_time(stamp, now)
            if value.get("measurementState") == "cached" and (not valid or
                    (now - datetime.fromisoformat(valid.replace("Z", "+00:00"))).total_seconds() >= MAX_RETAIN_SECONDS):
                live[key] = dict(value, percent=None, measurementState="unknown", fresh=False,
                                 charging=None, chargingKnown=False, chargingCheckedAtUtc=None)
        self.previous = dict(list(live.items())[:MAX_DEVICES])
        self.provider_by_id = {key: value for key, value in self.provider_by_id.items() if key in self.previous}
        self.physical_by_id = {key: value for key, value in self.physical_by_id.items() if key in self.previous}
        devices = sorted(self.previous.values(), key=lambda d: ({"mouse": 0, "keyboard": 1, "headset": 2}.get(d["kind"], 3), d["name"], d["id"]))
        health = list(self.health.values())[:128]
        errors = [h["provider"] + ": " + h["errorCode"] for h in health if h.get("errorCode")]
        return {"schema": 2, "requestId": request_id, "generation": generation, "complete": complete,
                "devices": devices, "nativeMousePresent": self.native_mouse,
                "providerCount": len(self.providers), "errors": (list(self.errors) + errors)[:32],
                "health": health, "checkedAtUtc": utcnow(), "packageSmoke": self.package_smoke}

    def _accept(self, reader_key, name, statuses, now, physical="", candidate_count=0):
        group = {}
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
        count = self.failures.get(reader_key, 0) + 1
        self.failures[reader_key] = count
        wait = min(300, 5 * (2 ** min(count - 1, 6)))
        self.retry_after[reader_key] = time.monotonic() + wait
        self.health[reader_key] = {"provider": name, "readerId": public_id(reader_key), "state": "timeout" if code == "timeout" else "error",
                                  "deviceCount": len(self.by_provider.get(reader_key, {})), "errorCode": code,
                                  "scope": "provider", "checkedAtUtc": utcnow(),
                                  "nextRetryAtUtc": (datetime.now(timezone.utc) + timedelta(seconds=wait)).isoformat().replace("+00:00", "Z")}

    def poll(self, force=False, device_id=None, request_id=0, generation=0, progress=None):
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
            return self._isolated_poll(force, device_id, request_id, generation, progress, token)
        except Exception:
            self._failure("manager", "manager", "read_failed")
            self.close()
            return self._snapshot(request_id, generation)

    def _isolated_poll(self, force, device_id, request_id, generation, progress, token):
        from isolation import IsolationError
        from providers.base import DeviceStatus
        from topology import physical_groups
        if device_id and device_id not in self.provider_by_id:
            self._failure("selection", "selection", "unknown_target")
            return self._snapshot(request_id, generation)
        self.health.pop("selection", None)
        self.health.pop("manager", None)
        paths = frozenset() if self.package_smoke else self._hidlist.interface_paths()
        self.native_mouse = False if self.package_smoke else None if paths is None else any("vid_373b&pid_1278" in p for p in paths)
        deadline = time.monotonic() + MAX_ROUND_SECONDS
        target_name = self.provider_by_id.get(device_id) if device_id else None
        target_physical = self.physical_by_id.get(device_id) if device_id else None
        pending, active = [], {}
        partial_count = 0
        if self.package_smoke:
            pending.append(("smoke", "package", "", {"action": "smoke"}, 5))
        else:
            if not device_id or target_name != "bluetooth":
                pending.append(("topology", "topology", "", {"action": "topology", "paths": sorted(paths) if paths is not None else None}, 6))
            if not device_id or target_name == "bluetooth":
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
                    reader_key = name + ("|" + physical if physical else "")
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
                action, name, physical, command, budget = active[slot]
                reader_key = name + ("|" + physical if physical else "")
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
                            if not matches and not target_name:
                                self.health[provider_name] = {"provider": provider_name, "readerId": public_id(provider_name), "state": "no_devices", "deviceCount": 0, "scope": "provider"}
                            elif matches:
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
                            if len(matches) > 1:
                                self._ambiguous_providers.add(provider_name)
                            elif not target_name:
                                self._ambiguous_providers.discard(provider_name)
                        if not target_name:
                            for key in list(self.by_provider):
                                if key not in live_readers:
                                    self.by_provider.pop(key, None)
                                    self.health.pop(key, None)
                            self._affinity = {key: value for key, value in self._affinity.items()
                                              if key in live_readers or key in ("topology", "package")}
                            self.retry_after = {key: value for key, value in self.retry_after.items() if key in live_readers}
                            self.failures = {key: value for key, value in self.failures.items() if key in live_readers}
                    elif action == "smoke":
                        self.health["package"] = {"provider": "package", "state": "ok", "scope": "provider", "deviceCount": 0}
                    else:
                        raw = result.get("statuses", [])
                        if not isinstance(raw, list) or len(raw) > MAX_DEVICES:
                            raise IsolationError("invalid_response")
                        statuses = [DeviceStatus(**value) for value in raw]
                        self._accept(reader_key, name, statuses, valid_time(result.get("checkedAtUtc")) or utcnow(), physical,
                                     candidate_count=max(0, min(512, int(result.get("candidateCount", 0)))))
                    if progress and partial_count < MAX_PARTIALS:
                        partial_count += 1
                        progress(self._snapshot(request_id, generation, False))
                except IsolationError as error:
                    active.pop(slot, None)
                    self._failure(reader_key, name, error.code)
                    if progress and partial_count < MAX_PARTIALS:
                        partial_count += 1
                        progress(self._snapshot(request_id, generation, False))
            if time.monotonic() >= deadline:
                for slot, item in active.items():
                    self.children[slot].close()
                    self._failure(item[1] + ("|" + item[2] if item[2] else ""), item[1], "timeout")
                for item in pending:
                    self._failure(item[1] + ("|" + item[2] if item[2] else ""), item[1], "round_budget")
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
                if command.get("command") != "poll":
                    continue
                request_id, generation = command.get("requestId", 0), command.get("generation", 0)
                if type(request_id) is not int or type(generation) is not int or min(request_id, generation) < 0:
                    continue
                target = command.get("deviceId")
                if target is not None and (not isinstance(target, str) or not target.startswith("device-") or len(target) > 64):
                    continue
                emit = lambda result: print(json.dumps(result, ensure_ascii=True), flush=True)
                result = engine.poll(force=command.get("force") is True, device_id=target,
                                     request_id=request_id, generation=generation, progress=emit)
                emit(result)
            except Exception:
                print(json.dumps({"schema": 2, "requestId": 0, "generation": 0, "complete": True,
                                  "error": "Battery query did not complete", "devices": [],
                                  "errors": ["manager: invalid_request"]}), flush=True)
    finally:
        engine.close()


if __name__ == "__main__":
    main()
