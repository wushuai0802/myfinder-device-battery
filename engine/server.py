"""Private, local-only battery-query worker. No GUI, sockets or package installs.

The parent sends a JSON poll command on stdin. One bounded snapshot is returned
on stdout. EOF terminates the worker, and the parent also owns a kill-on-close
Windows job. Device keys are hashed before leaving this process.
"""
from __future__ import annotations

import hashlib
import importlib
import json
import logging
import os
import re
import sys
import time
from datetime import datetime, timezone

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
NATIVE = {(0x373B, 0x1278), (0x03F0, 0x098D)}
MAX_DEVICES = 64


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


def normalise(status, previous, now, cached=False):
    value = status.level
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not 0 <= value <= 100:
        value = None
    elif int(value) != value:
        value = round(value)
    else:
        value = int(value)
    online = bool(status.online)
    fresh = online and value is not None and not cached
    if not fresh and previous:
        value = previous.get("percent") if value is None or not online else value
    return {
        "id": public_id(status.key), "name": clean_name(status.name),
        "kind": kind_of(status), "percent": value,
        "charging": bool(status.charging) if fresh else False,
        "online": online, "fresh": fresh,
        "approximate": bool(status.approx), "provider": status.source or "unknown",
        "transport": "Bluetooth" if status.via == "bluetooth" or status.source == "bluetooth" else "USB / 2.4G",
        "confirmedAtUtc": now if fresh else (previous or {}).get("confirmedAtUtc"),
    }


class BatteryEngine:
    def __init__(self, providers=None, include_native=False):
        from providers import hidlist
        self._hidlist = hidlist
        self.previous = {}
        self.by_provider = {}
        self.providers = []
        self.errors = []
        self.last_poll = {}
        if not include_native:
            original = hidlist.enumerate

            def filtered(vid=0):
                return [d for d in original(vid) if (d.get("vendor_id"), d.get("product_id")) not in NATIVE]
            hidlist.enumerate = filtered
        if providers is not None:
            self.providers = providers
        else:
            for module, name in PROVIDERS:
                try:
                    self.providers.append(getattr(importlib.import_module("providers." + module), name)())
                except Exception:
                    self.errors.append(module + ": load failed")
            from standardhid import StandardHidProvider
            self.providers.append(StandardHidProvider())

    def poll(self, force=False):
        now = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
        errors = list(self.errors)
        paths = self._hidlist.interface_paths()
        native_mouse = None if paths is None else any("vid_373b&pid_1278" in p for p in paths)
        live = {}
        for provider in self.providers:
            minimum = 60 if provider.name == "bluetooth" else 30 if provider.name == "standard_hid" else 0
            if not force and provider.name in self.last_poll and time.monotonic()-self.last_poll[provider.name] < minimum:
                live.update(self.by_provider.get(provider.name, {}))
                continue
            try:
                statuses = provider.poll()
                self.last_poll[provider.name] = time.monotonic()
                group = {}
                for status in statuses:
                    if status.kind == "gamepad":
                        continue
                    key = public_id(status.key)
                    cached = bool(getattr(provider, "_misses", {}).get(status.key, 0))
                    reading = normalise(status, self.previous.get(key), now, cached)
                    group[key] = reading
                self.by_provider[provider.name] = group
            except Exception:
                errors.append(provider.name + ": read failed")
                for value in self.by_provider.get(provider.name, {}).values():
                    value["fresh"] = False
                    value["charging"] = False
            live.update(self.by_provider.get(provider.name, {}))
        # A query can time out while a receiver is still present. Keep its last
        # reading briefly, but never refresh the original confirmation time.
        for key, value in self.previous.items():
            if key not in live:
                last = value.get("confirmedAtUtc")
                age = float("inf")
                if last:
                    age = (datetime.now(timezone.utc) - datetime.fromisoformat(last.replace("Z", "+00:00"))).total_seconds()
                if age < 120:
                    live[key] = dict(value, fresh=False, online=False, charging=False)
        self.previous = dict(list(live.items())[:MAX_DEVICES])
        devices = sorted(self.previous.values(), key=lambda d: ({"mouse": 0, "keyboard": 1, "headset": 2}.get(d["kind"], 3), d["name"], d["id"]))
        return {"schema": 1, "devices": devices, "nativeMousePresent": native_mouse,
                "providerCount": len(self.providers), "errors": errors[:32], "checkedAtUtc": now}


def main():
    logging.getLogger("halo_battery").addHandler(logging.NullHandler())
    logging.getLogger("halo_battery").propagate = False
    engine = BatteryEngine()
    if "--once" in sys.argv:
        print(json.dumps(engine.poll(), ensure_ascii=True), flush=True)
        return
    for line in sys.stdin:
        if len(line) > 4096:
            break
        try:
            command = json.loads(line)
            if command.get("command") == "exit":
                break
            if command.get("command") != "poll":
                continue
            print(json.dumps(engine.poll(force=bool(command.get("force"))), ensure_ascii=True), flush=True)
        except Exception:
            print(json.dumps({"schema": 1, "error": "Battery query did not complete", "devices": []}), flush=True)


if __name__ == "__main__":
    main()
