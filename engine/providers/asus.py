"""ASUS ROG / TUF wireless mice, on their 2.4 GHz receiver or on the cable.

Source: G-Helper (seerge/g-helper, app/Peripherals/Mouse/AsusMouse.cs and the model
files in app/Peripherals/Mouse/Models). Only the protocol facts are used here (ids,
command, byte positions); the code is written for this app.

The exchange, on the vendor collection of interface 0 ("mi_00" in G-Helper):

    request   00 12 07          report id 0, command 12 07 (battery), zeros up to 65 bytes
    reply     00 12 07 ...      the echo of the command; after the report id:
                  byte 4        battery: a percentage, or a level 0..4 on older models
                  byte 9        charging when not 0
    error     00 ff aa ...      the mouse does not know the command
    zeros     00 00 00 ...      no reply (the mouse is off or asleep)

hidapi on Windows leaves out report id 0, so a reply starts with 12 07. A reply with
the report id in front is accepted too.

G-Helper takes a battery value of 0 without charging as "the mouse is in standby",
not as an empty battery. This provider does the same: no reading.

Not included (they use a different collection, report id or byte position in
G-Helper, and none was on hand to test): the OMNI receiver (0B05:1ACE), the Harpe II
Ace, Keris II Ace / Origin, Harpe Ace Mini / Extreme, Strix Carry, Gladius II Wireless
and the MD200.
"""
from __future__ import annotations

from datetime import datetime, timezone
import hashlib
import json
import time
from typing import Dict, List, Optional, Tuple

try:
    import hid
except ImportError:              # pragma: no cover
    hid = None

from . import hidlist
from .base import DeviceStatus, Provider, hexdump, log

ASUS_VID = 0x0B05
INTERFACE = 0

REQUEST = [0x00, 0x12, 0x07]         # report id 0, battery command
ECHO = (0x12, 0x07)
ERROR = (0xFF, 0xAA)
PACKET_LENGTH = 65                   # G-Helper's default packet size (report id + 64)
LEVEL_BYTE = 4                       # after the echo is found at 0 and 1
CHARGING_BYTE = 9

READ_TIMEOUT_MS = 300                # G-Helper's USB timeout
READ_ATTEMPTS = 8                    # other reports on the collection are skipped
WRITE_ATTEMPTS = 3

PERCENT, STEPS = 1, 25               # scale of the battery byte

# pid -> (name, scale). Wired and wireless pids of one mouse share the name, so they
# share one icon.
KNOWN: Dict[int, Tuple[str, int]] = {
    0x1A72: ("ROG Gladius III Aimpoint", PERCENT),
    0x1A70: ("ROG Gladius III Aimpoint", PERCENT),
    0x1B0C: ("ROG Gladius III Eva 2", PERCENT),
    0x1B0A: ("ROG Gladius III Eva 2", PERCENT),
    0x197F: ("ROG Gladius III Wireless", PERCENT),
    0x197D: ("ROG Gladius III Wireless", PERCENT),
    0x1A1A: ("ROG Chakram X", PERCENT),
    0x1A18: ("ROG Chakram X", PERCENT),
    0x1A94: ("ROG Harpe Ace Aim Lab Edition", PERCENT),
    0x1A92: ("ROG Harpe Ace Aim Lab Edition", PERCENT),
    0x1A68: ("ROG Keris Wireless Aimpoint", PERCENT),
    0x1A66: ("ROG Keris Wireless Aimpoint", PERCENT),
    0x1979: ("ROG Spatha X", PERCENT),
    0x1977: ("ROG Spatha X", PERCENT),
    0x19F4: ("TUF Gaming M4 Wireless", PERCENT),
    0x1A8D: ("TX Gaming Mouse", PERCENT),
    0x1AF5: ("TX Gaming Mouse Mini", PERCENT),
    0x1AF3: ("TX Gaming Mouse Mini", PERCENT),
    0x1C57: ("TUF Gaming Mini Miku Edition", PERCENT),
    0x1C56: ("TUF Gaming Mini Miku Edition", PERCENT),
    # older models: the battery byte is a level 0..4, G-Helper multiplies it by 25
    0x18E5: ("ROG Chakram", STEPS),
    0x18E3: ("ROG Chakram", STEPS),
    0x1960: ("ROG Keris Wireless", STEPS),
    0x195E: ("ROG Keris Wireless", STEPS),
    0x1A59: ("ROG Keris EVA Edition", STEPS),
    0x1A57: ("ROG Keris EVA Edition", STEPS),
    0x1908: ("ROG Pugio II", STEPS),
    0x1906: ("ROG Pugio II", STEPS),
    0x1949: ("ROG Strix Impact II Wireless", STEPS),
    0x1947: ("ROG Strix Impact II Wireless", STEPS),
}

Reading = Tuple[int, bool, str]     # level %, charging, approximate text ("" = exact)


def _offset(r: List[int], pair: Tuple[int, int]) -> Optional[int]:
    """Where `pair` starts in a reply: 0 (hidapi left out report id 0) or 1 (report id
    in front). None when the reply does not start with it."""
    if len(r) > 1 and (r[0], r[1]) == pair:
        return 0
    if len(r) > 2 and r[0] == 0x00 and (r[1], r[2]) == pair:
        return 1
    return None


def has_echo(r: List[int]) -> bool:
    return _offset(r, ECHO) is not None


def is_error(r: List[int]) -> bool:
    return _offset(r, ERROR) is not None


def parse_reply(r: List[int], scale: int) -> Optional[Reading]:
    """A reply that echoes 12 07 -> (level, charging, text). None for anything else,
    for the standby value (0 and not charging) and for a value out of range."""
    m = _offset(r, ECHO)
    if m is None or len(r) < m + CHARGING_BYTE + 1:
        return None
    raw = r[m + LEVEL_BYTE]
    charging = r[m + CHARGING_BYTE] != 0
    if raw == 0 and not charging:
        return None                                  # standby, not empty
    if scale == STEPS:
        if raw > 4:
            return None
        level = raw * STEPS
        text = f"about {level}%" + (", charging" if charging else "")
        return level, charging, text
    if raw > 100:
        return None
    return raw, charging, ""


# OpenMouse mouse-protocol 93c4cc601d448ea481eb59d7b785eec75ae16ec2,
# src/asus/devices.ts, src/asus/index.ts, and src/drivers/asus/hid.ts.
# pid -> (stage_count, min_dpi, max_dpi, dpi_step, dpi_xy)
ASUS_DPI_MODELS: Dict[int, Tuple[int, int, int, int, bool]] = {
    0x1A92: (4, 50, 36000, 50, True),  # ROG Harpe Ace Aim Lab Edition (wired)
    0x1A94: (4, 50, 36000, 50, True),  # ROG Harpe Ace Aim Lab Edition (wireless)
}
IDENTITY_KEYS = ("vendor_id", "product_id", "physical_id", "path", "serial_number")


def _dpi_eligible(info: dict) -> bool:
    return (
        isinstance(info, dict)
        and info.get("vendor_id") == ASUS_VID
        and info.get("product_id") in ASUS_DPI_MODELS
        and info.get("interface_number") == INTERFACE
        and (info.get("usage_page") or 0) >= 0xFF00
        and isinstance(info.get("physical_id"), str)
        and bool(info["physical_id"])
        and info.get("modelSource", info.get("model_source")) != "pack"
        and bool(info.get("path"))
    )


def _dpi_token(target: dict) -> str:
    identity = dict(target)
    path = identity.get("path")
    if isinstance(path, bytes):
        identity["path"] = path.hex()
    return hashlib.sha256(
        json.dumps(identity, sort_keys=True, separators=(",", ":")).encode()
    ).hexdigest()


def _dpi_view(target: dict, active: int, error: Optional[str] = None) -> dict:
    axes = target["stages"]
    stages = [pair[0] for pair in axes]
    writable = (
        not error
        and 1 <= len(stages) <= 8
        and len(set(stages)) == len(stages)
        and all(x == y for x, y in axes)
        and 0 <= active < len(stages)
    )
    return {
        "supported": True,
        "canSet": writable,
        "current": stages[active],
        "stages": stages,
        "stageSource": "onboard",
        "checkedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "targetToken": _dpi_token(target) if writable else None,
        "error": error or (None if writable else "dpi_ambiguous_stages"),
    }


def _prefix_offset(r: List[int], prefix: Tuple[int, ...]) -> Optional[int]:
    n = len(prefix)
    if len(r) >= n and tuple(r[:n]) == prefix:
        return 0
    if len(r) >= n + 1 and r[0] == 0x00 and tuple(r[1:1 + n]) == prefix:
        return 1
    return None


def _asus_read_reply(dev, prefix: Tuple[int, ...]) -> Tuple[List[int], int]:
    for _ in range(READ_ATTEMPTS):
        r = list(dev.read(PACKET_LENGTH, READ_TIMEOUT_MS) or [])
        if not r:
            break
        if is_error(r) or not any(r):
            raise ValueError("dpi_read_failed")
        m = _prefix_offset(r, prefix)
        if m is not None:
            return r, m
    raise ValueError("dpi_read_failed")


def _asus_exchange(dev, request_head: List[int], echo_prefix: Tuple[int, ...]) -> Tuple[List[int], int]:
    frame = request_head + [0x00] * (PACKET_LENGTH - len(request_head))
    written = dev.write(frame)
    if written not in (None, PACKET_LENGTH):
        raise ValueError("dpi_write_failed")
    return _asus_read_reply(dev, echo_prefix)


def _dpi_state(dev, info: dict) -> Tuple[dict, dict, int]:
    count, min_dpi, max_dpi, step, dpi_xy = ASUS_DPI_MODELS[info["product_id"]]
    r_prof, m_prof = _asus_exchange(dev, [0x00, 0x12, 0x00], (0x12, 0x00, 0x00))
    if len(r_prof) < m_prof + 12:
        raise ValueError("dpi_invalid_profile")
    profile = r_prof[m_prof + 10] + 1
    stage_raw = r_prof[m_prof + 11]
    if not 1 <= stage_raw <= count:
        raise ValueError("dpi_invalid_active_stage")
    active = stage_raw - 1
    axes: List[List[int]] = []
    if dpi_xy:
        r_dpi, m_dpi = _asus_exchange(dev, [0x00, 0x12, 0x04, 0x02], (0x12, 0x04, 0x02))
        if len(r_dpi) < m_dpi + 4 + count * 4:
            raise ValueError("dpi_invalid_stages")
        for i in range(count):
            base = m_dpi + 4 + i * 4
            x_raw = r_dpi[base] | (r_dpi[base + 1] << 8)
            y_raw = r_dpi[base + 2] | (r_dpi[base + 3] << 8)
            x_dpi = x_raw * step + min_dpi
            y_dpi = y_raw * step + min_dpi
            if not (min_dpi <= x_dpi <= max_dpi and min_dpi <= y_dpi <= max_dpi):
                raise ValueError("dpi_invalid_stage")
            axes.append([x_dpi, y_dpi])
    else:
        r_dpi, m_dpi = _asus_exchange(dev, [0x00, 0x12, 0x04, 0x00], (0x12, 0x04, 0x00))
        if len(r_dpi) < m_dpi + 4 + count * 2:
            raise ValueError("dpi_invalid_stages")
        for i in range(count):
            base = m_dpi + 4 + i * 2
            raw_val = r_dpi[base] | (r_dpi[base + 1] << 8)
            val = raw_val * step + min_dpi
            if not (min_dpi <= val <= max_dpi):
                raise ValueError("dpi_invalid_stage")
            axes.append([val, val])
    target = {key: info.get(key) for key in IDENTITY_KEYS}
    target.update(provider="asus", profile=profile, stages=axes)
    return _dpi_view(target, active), target, active


def _capture_dpi(info: Optional[dict]) -> Tuple[Optional[dict], Optional[dict]]:
    if not info or not _dpi_eligible(info):
        return None, None
    dev = None
    try:
        dev = hid.device()
        dev.open_path(info["path"])
        view, target, _ = _dpi_state(dev, info)
        return view, target if view["canSet"] else None
    except Exception:
        return None, None
    finally:
        if dev is not None:
            try:
                dev.close()
            except Exception:
                pass


def set_device_dpi(inventory, target, value, expected_dpi, target_token):
    """Switch one existing stage via 51 31 09 00 <stage> without flash save; never retry."""
    view, dev, attempted = None, None, False

    def result(error=None, success=False, unknown=False):
        answer = dict(view) if view else None
        if answer and error:
            answer.update(canSet=False, targetToken=None, error=error)
        if answer and unknown:
            answer.update(current=None, checkedAtUtc=None)
        return {
            "dpi": answer,
            "success": success,
            "outcomeUnknown": unknown,
            "error": error,
            "confirmedDpi": answer["current"] if answer and not unknown else None,
        }

    try:
        if (not isinstance(target, dict)
                or target.get("provider") != "asus"
                or not isinstance(inventory, (list, tuple))
                or not isinstance(target.get("stages"), list)):
            return result("dpi_invalid_target")
        if not isinstance(target_token, str) or _dpi_token(target) != target_token:
            return result("dpi_stale_target")
        if type(value) is not int or type(expected_dpi) is not int:
            return result("dpi_invalid_value")
        offered = [pair[0] for pair in target["stages"] if isinstance(pair, (list, tuple)) and len(pair) == 2]
        if value not in offered or expected_dpi not in offered:
            return result("dpi_unsupported_value")
        matches = [
            info for info in inventory
            if isinstance(info, dict)
            and _dpi_eligible(info)
            and all(info.get(key) == target.get(key) for key in IDENTITY_KEYS)
        ]
        if len(matches) != 1:
            return result("dpi_target_changed")
        info = matches[0]
        dev = hid.device()
        dev.open_path(info["path"])
        view, fresh, _ = _dpi_state(dev, info)
        if not view["canSet"] or view["targetToken"] != target_token:
            return result("dpi_target_changed")
        if view["current"] != expected_dpi:
            return result("dpi_current_changed")
        index = offered.index(value)
        if value == expected_dpi:
            return result(success=True)
        frame = [0x00, 0x51, 0x31, 0x09, 0x00, index + 1] + [0x00] * (PACKET_LENGTH - 6)
        attempted = True
        written = dev.write(frame)
        if written not in (None, PACKET_LENGTH):
            return result("dpi_readback_unconfirmed", unknown=True)
        _asus_read_reply(dev, (0x51, 0x31, 0x09))
        after_view, after_target, after_active = _dpi_state(dev, info)
        if (after_target["stages"] != fresh["stages"]
                or after_target["profile"] != fresh["profile"]
                or after_active != index):
            view = after_view
            return result("dpi_readback_mismatch", unknown=True)
        view = after_view
        return result(success=True)
    except Exception:
        return result("dpi_readback_unconfirmed" if attempted else "dpi_read_failed", unknown=attempted)
    finally:
        if dev is not None:
            try:
                dev.close()
            except Exception:
                pass


class AsusProvider(Provider):
    name = "asus"

    def set_device_dpi(self, inventory, target, value, expected_dpi, target_token):
        return set_device_dpi(inventory, target, value, expected_dpi, target_token)

    def __init__(self):
        self._diag: List[str] = []
        self._failing: Dict[str, bool] = {}

    def _read(self, path, scale: int) -> Optional[Reading]:
        dev = hid.device()
        try:
            dev.open_path(path)
        except (OSError, IOError) as e:
            self._diag.append(f"    open: {e}")
            return None
        try:
            # drop reports that are already waiting, as G-Helper does before a request
            try:
                dev.set_nonblocking(True)
                for _ in range(16):
                    if not dev.read(PACKET_LENGTH):
                        break
                dev.set_nonblocking(False)
            except (OSError, IOError, ValueError):
                pass
            for _ in range(WRITE_ATTEMPTS):
                try:
                    dev.write(REQUEST + [0x00] * (PACKET_LENGTH - len(REQUEST)))
                except (OSError, IOError, ValueError) as e:
                    self._diag.append(f"    write: {e}")
                    return None
                for _ in range(READ_ATTEMPTS):
                    r = list(dev.read(PACKET_LENGTH, READ_TIMEOUT_MS) or [])
                    if not r:
                        break                        # timeout: send the request again
                    if is_error(r):
                        self._diag.append(f"    reply: error {hexdump(r, 12)}")
                        return None
                    if not any(r):
                        self._diag.append("    reply: all zeros (off or asleep)")
                        return None
                    if not has_echo(r):
                        continue                     # a button or profile event: skip it
                    self._diag.append(f"    reply: {hexdump(r, 12)}")
                    res = parse_reply(r, scale)
                    if res is None:
                        self._diag.append("    battery 0 and not charging (standby), or out of range")
                    return res
            self._diag.append("    no reply with the 12 07 echo")
            return None
        finally:
            try:
                dev.close()
            except Exception:
                pass

    def poll(self) -> List[DeviceStatus]:
        self._diag = []
        if hid is None:
            return []
        try:
            infos = hidlist.enumerate(ASUS_VID)
        except Exception as e:  # pragma: no cover
            log.warning("hid.enumerate(asus): %s", e)
            return []

        found: Dict[str, Tuple[Reading, dict]] = {}
        for d in infos:
            pid = d["product_id"]
            if pid not in KNOWN:
                continue
            if d.get("interface_number") != INTERFACE or d.get("usage_page", 0) < 0xFF00:
                continue                                 # only the vendor collection
            name, scale = KNOWN[pid]
            self._diag.append(f"[ASUS] {name} pid={pid:04x} usage="
                              f"{d.get('usage_page', 0):04x}:{d.get('usage', 0):04x}")
            res = self._read(d["path"], scale)
            if res is None:
                continue
            self._diag.append(f"  -> {res[2] or str(res[0]) + '%'}{' charging' if res[1] else ''}")
            # the same mouse on the cable and on the receiver: keep the charging reading
            if name not in found or (res[1] and not found[name][0][1]):
                found[name] = (res, d)

        out: List[DeviceStatus] = []
        for name, ((level, charging, text), winning_info) in found.items():
            key = "asus:" + name.lower().replace(" ", "-")
            dpi, dpi_target = _capture_dpi(winning_info)
            out.append(DeviceStatus(key, name, level, charging, True, "asus", text, kind="mouse",
                                    dpi=dpi, dpi_target=dpi_target))
        return out

    def diagnostics(self) -> List[str]:
        return list(self._diag)
