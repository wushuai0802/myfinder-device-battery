"""Offline triage index. Parse provider source; never import/run a HID reader.

IDs in the JSON are integers. Optional devices/pack/*.json files use
{"devices": [{"provider": str, "vendorId": int or hex str,
              "productId": int or hex str, "name": str, "kind": str}]}.
Unrecognised pack schemas/modules are reported, never inferred as supported.
"""
from __future__ import annotations

import argparse
import ast
import json
from pathlib import Path


# Explicit table meanings prevent protocol opcodes/model numbers becoming PIDs.
# Only the name (first tuple item) is needed; parser function references are inert.
TABLES = {
    "astro": ("PIDS",), "asus": ("KNOWN",), "audeze": ("KNOWN",),
    "corsair": ("PIDS", "BRAGI_PIDS"), "gwolves": ("WIRED", "MODELS"),
    "hyperx": ("PIDS",), "hyperx_cloud3": ("PIDS",),
    "hyperx_cloud3s": ("PIDS",), "jbl": ("PIDS",),
    "keychron": ("PIDS",), "lamzu": ("PIDS",), "lofree": ("DONGLES",),
    "logitech": ("HEADSETS",), "logitech_centurion": ("PIDS",),
    "mchose": ("A5_PIDS",), "nzxt": ("PIDS",), "pulsar": ("PIDS",),
    "razer": ("KNOWN",), "steelseries": ("MODELS", "MOUSE_MODELS", "CLASSIC_MODELS"),
    "steelseries_elite": ("PIDS",), "wlmouse": ("KNOWN",),
}
SINGLE = {"am_infinity", "hyperx_alpha2", "barracuda"}
HELPERS = {"__init__", "base", "hidlist", "blackshark", "blackshark2020"}


class StaticSource:
    def __init__(self, path):
        self.tree = ast.parse(path.read_text(encoding="utf-8-sig"))
        self.values = {}
        for node in self.tree.body:
            targets = node.targets if isinstance(node, ast.Assign) else (
                [node.target] if isinstance(node, ast.AnnAssign) else [])
            for target in targets:
                self._bind(target, node.value)

    def _bind(self, target, value):
        if isinstance(target, ast.Name):
            self.values[target.id] = value
        elif isinstance(target, (ast.Tuple, ast.List)) and isinstance(value, (ast.Tuple, ast.List)):
            for child, item in zip(target.elts, value.elts):
                self._bind(child, item)

    def resolve(self, node, seen=frozenset()):
        if isinstance(node, ast.Name):
            if node.id in seen or node.id not in self.values:
                raise ValueError("not a static constant")
            return self.resolve(self.values[node.id], seen | {node.id})
        if node is None:
            raise ValueError("missing constant")
        return node

    def literal(self, node):
        node = self.resolve(node)
        if isinstance(node, (ast.List, ast.Tuple, ast.Set)):
            return [self.literal(item) for item in node.elts]
        return ast.literal_eval(node)

    def get(self, key):
        return self.literal(self.values[key])

    def table(self, key):
        node = self.resolve(self.values[key])
        if not isinstance(node, ast.Dict) or any(k is None for k in node.keys):
            raise ValueError("not a literal table")
        return zip(node.keys, node.values)

    def name(self, node):
        node = self.resolve(node)
        if isinstance(node, (ast.Tuple, ast.List)):
            node = node.elts[0]
        value = self.literal(node)
        if not isinstance(value, str) or not value.strip():
            raise ValueError("missing model name")
        return value


def word(value):
    result = int(value, 16) if isinstance(value, str) else value
    if type(result) is not int or not 0 <= result <= 0xFFFF:
        raise ValueError("ID must be an unsigned 16-bit integer")
    return result


def build_index(root=None):
    root = Path(root) if root else Path(__file__).resolve().parent.parent
    catalog = json.loads((root / "engine/compatibility.json").read_text(encoding="utf-8-sig"))
    readers = {item["provider"]: item for item in catalog["readers"]}
    devices, vendors, unparsed = set(), {}, set()

    def add(provider, vid, pid, name, kind):
        vid, pid = word(vid), word(pid)
        if not all(isinstance(s, str) and s.strip() for s in (provider, name, kind)):
            raise ValueError("missing device metadata")
        devices.add((provider, vid, pid, name, kind))
        vendors.setdefault(provider, set()).add(vid)

    for path in sorted((root / "engine/providers").glob("*.py")):
        module = path.stem
        if module in HELPERS:
            continue  # Transport helpers are already represented by their owner.
        vendors[module] = set()
        try:
            source = StaticSource(path)
            for key in source.values:
                if key == "VID" or key.endswith(("_VID", "_VIDS")):
                    value = source.get(key)
                    vendors[module].update(word(v) for v in (value if isinstance(value, list) else [value]))
            kinds = readers.get(module, {}).get("kinds", [])
            default_kind = kinds[0] if len(kinds) == 1 else None
            for table in TABLES.get(module, ()):
                # Each table has an explicit VID relation, not a cross product.
                if module == "mchose":
                    vid = source.get("A5_VID")
                elif module != "pulsar":
                    if len(vendors[module]) != 1:
                        raise ValueError("ambiguous vendor")
                    vid = next(iter(vendors[module]))
                for key, value in source.table(table):
                    if module == "pulsar":
                        vid = word(source.literal(key))
                        vendors[module].add(vid)
                        value = source.resolve(value)
                        if not isinstance(value, ast.Dict):
                            raise ValueError("invalid nested device table")
                        for pid, name in zip(value.keys, value.values):
                            add(module, vid, source.literal(pid), source.name(name), "mouse")
                        continue
                    pid, name = word(source.literal(key)), source.name(value)
                    kind = default_kind
                    if module == "lamzu":
                        name = source.get("NAME")  # PIDS values describe transport.
                    elif module == "razer":
                        # These are the same model-name distinctions as kind_of.
                        kind = ("keyboard" if any(s in name.lower() for s in ("blackwidow", "deathstalker"))
                                else "headset" if "blackshark" in name.lower() else "mouse")
                    elif module == "steelseries":
                        kind = "mouse" if table == "MOUSE_MODELS" else "headset"
                    elif module == "corsair":
                        kind = "mouse" if table == "BRAGI_PIDS" and pid == 0x1B7F else "headset"
                    elif module == "logitech":
                        kind = "headset"
                    add(module, vid, pid, name, kind)
            if module in SINGLE:
                vid = next(iter(vendors[module]))
                name = (readers[module]["name"] if module == "barracuda" else source.get("NAME"))
                add(module, vid, source.get("PID"), name, default_kind)
            elif module == "gwolves":
                add(module, source.get("GWOLVES_VID"), source.get("RECEIVER"), source.get("DEFAULT_NAME"), "mouse")
            elif module == "logitech":
                for pid in source.get("RECEIVERS"):
                    add(module, source.get("LOGITECH_VID"), pid, "Logitech HID++ 接收器", "device")
                # Paired model names/kinds and other HID++ identities are dynamic.
                unparsed.add(module)
            elif module == "mchose":
                add(module, source.get("G7_VID"), source.get("G7_PID"), "MCHOSE mouse", "mouse")
                # MODEL_NAMES uses reply model codes, NOT USB product IDs.
                unparsed.add(module)
            elif module not in TABLES:
                unparsed.add(module)  # Includes runtime-only Bluetooth identities.
        except (KeyError, ValueError, TypeError, SyntaxError):
            unparsed.add(module)

    for profile in catalog["nativeProfiles"]:
        add(profile["provider"], profile["match"]["vendorId"], profile["match"]["productId"],
            profile["name"], profile["kind"])
    for path in sorted((root / "devices/pack").glob("*.json")):
        try:
            pack = json.loads(path.read_text(encoding="utf-8-sig"))
            rows = pack["devices"]
            if not isinstance(rows, list):
                raise ValueError("invalid pack")
            for row in rows:
                add(row["provider"], row["vendorId"], row["productId"], row["name"], row["kind"])
        except (KeyError, ValueError, TypeError):
            unparsed.add(path.relative_to(root).as_posix())
    fields = ("provider", "vendorId", "productId", "name", "kind")
    return {"vendors": {key: sorted(value) for key, value in sorted(vendors.items())},
            "devices": [dict(zip(fields, row)) for row in sorted(devices)],
            "unparsed": sorted(unparsed)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--json", action="store_true", help="print the stable JSON index")
    parser.parse_args()
    # ASCII escapes also make redirected Windows output independent of its codepage.
    print(json.dumps(build_index(), ensure_ascii=True, sort_keys=True, indent=2))


if __name__ == "__main__":
    main()
