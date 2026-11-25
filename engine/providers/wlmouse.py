"""WLmouse mice (Beast X, Beast X Max, Beast X Mini Pro; 8K and 1K receivers).

Protocol (reverse-engineered by @len0c, see incconutwo/mouse-battery-tray, MIT):
  * request: feature report id 0, payload 00 00 02 02 00 83 ...
  * reply:   a1|a2 00 02 02 00 83 <charging> <battery%>
  * fallback: the mouse periodically sends an input report 03 00 <battery%> <charging>

A receiver whose mouse is switched off is walked at most every fifth poll once
nothing is shown (or it never answered after the first tries): each walk sends the
feature request to every 0xFFFF collection - each refusal costs ~0.75 s - and then
listens ~2 s, and a switched-off mouse used to pay that on every poll forever
(#62 item 5, review by @ahmedkhursheed23). A change in the receiver's device set -
the charging cable appearing - is walked at once.
"""
from __future__ import annotations

from datetime import datetime, timezone
import hashlib
import json
import time
from typing import Dict, List, Optional, Tuple

import hid

from . import hidlist
from .base import DeviceStatus, Provider, hexdump, log

WLMOUSE_VIDS = (0x36A7,)

KNOWN = {
    0xA887: "WLmouse Beast X",
    0xA868: "WLmouse Beast X Mini Pro",
    0xA880: "WLmouse Beast X Max",          # 8K receiver, confirmed on hardware
}

# receiver PIDs (a mouse on the cable shows up in Windows with its own PID)
RECEIVERS = {0xA887, 0xA868, 0xA880}

QUERY = [0x00, 0x00, 0x02, 0x02, 0x00, 0x83]

# how long a silent mouse keeps its (greyed-out) icon before it is hidden, s
ASLEEP_KEEP = 300

# (#62 item 5) the first few walks of a receiver are always made, so a mouse that
# is just waking up is found at full rate; once nothing is shown (the level stale,
# or never seen) the walk runs every BACKOFF_EVERY polls instead - at once when the
# receiver's device set changes.
BACKOFF_FIRST = 3
BACKOFF_EVERY = 5


def parse_feature(resp) -> Tuple[Optional[int], Optional[bool]]:
    if not resp:
        return None, None
    r = list(resp)
    for i in range(5, len(r) - 2):
        if r[i] == 0x83 and r[i - 1] == 0x00 and r[i - 2] == 0x02 and r[i - 5] in (0xA1, 0xA2):
            charge, batt = r[i + 1], r[i + 2]
            if 0 <= batt <= 100:
                return batt, bool(charge)
    return None, None


def parse_heartbeat(data) -> Tuple[Optional[int], Optional[bool]]:
    d = list(data or [])
    for off in (0, 1):
        if len(d) >= off + 4 and d[off] == 0x03 and d[off + 1] == 0x00:
            batt = d[off + 2]
            if 0 <= batt <= 100:
                return batt, bool(d[off + 3])
    return None, None


# OpenMouse mouse-protocol 93c4cc601d448ea481eb59d7b785eec75ae16ec2,
# src/compx/codec.ts and src/drivers/wlmouse/hid.ts (also shared with Lamzu Maya X
# and G-Wolves XVI / XVI-new feature-report-0 page-command firmware).
WLMOUSE_4K_PID = 0xA887  # Beast X 4K uses separate report-1 transport without active-stage read.
COMPX_TARGET_MOUSE = 0x02
COMPX_PAGE_DEVICE = 0x00
COMPX_PAGE_PROFILE = 0x01
COMPX_CMD_ACTIVE_PROFILE = 0x85
COMPX_CMD_READ_STAGES = 0x81
COMPX_CMD_READ_ACTIVE_STAGE = 0x82
COMPX_CMD_SET_ACTIVE_STAGE = 0x02
COMPX_MAX_STAGES = 6
COMPX_MIN_DPI = 50
COMPX_MAX_DPI = 30000
COMPX_DPI_STEP = 50
COMPX_DPI_TRIES = 6
COMPX_DPI_PAUSE_S = 0.035

IDENTITY_KEYS = ("vendor_id", "product_id", "physical_id", "path", "serial_number")


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


def make_compx_request(page: int, command: int, length: int, args: Tuple[int, ...] = (),
                       target: int = COMPX_TARGET_MOUSE) -> List[int]:
    head = [0x00, 0x00, 0x00, target & 0xFF, length & 0xFF, page & 0xFF, command & 0xFF, *(a & 0xFF for a in args)]
    return head + [0x00] * (65 - len(head))


def parse_compx_reply(resp, page: int, command: int) -> Optional[bytes]:
    if not resp or len(resp) < 7:
        return None
    r = bytes(resp)
    for off in (0, 1):
        if (len(r) >= off + 7
                and r[off] == 0xA1
                and r[off + 2] == COMPX_TARGET_MOUSE
                and r[off + 4] == page
                and r[off + 5] == command):
            return r[off + 6:min(len(r), off + 64)]
    return None


def _compx_exchange(dev, page: int, command: int, length: int, args: Tuple[int, ...] = ()) -> bytes:
    req = make_compx_request(page, command, length, args)
    written = dev.send_feature_report(req)
    if written is not None and (type(written) is not int or written not in (64, 65)):
        raise ValueError("dpi_write_failed")
    for _ in range(COMPX_DPI_TRIES):
        time.sleep(COMPX_DPI_PAUSE_S)
        resp = dev.get_feature_report(0, 65)
        payload = parse_compx_reply(resp, page, command)
        if payload is not None:
            return payload
    raise ValueError("dpi_read_failed")


def compx_dpi_state(dev, info: dict, provider_name: str, *,
                    max_stages: int = COMPX_MAX_STAGES,
                    max_dpi: int = COMPX_MAX_DPI,
                    fixed_profile: Optional[int] = None) -> Tuple[dict, dict, int]:
    if fixed_profile is not None:
        profile = fixed_profile
    else:
        prof_pay = _compx_exchange(dev, COMPX_PAGE_DEVICE, COMPX_CMD_ACTIVE_PROFILE, 0x01)
        if not prof_pay or not 1 <= prof_pay[0] <= 4:
            raise ValueError("dpi_invalid_profile")
        profile = prof_pay[0]
    stg_pay = _compx_exchange(dev, COMPX_PAGE_PROFILE, COMPX_CMD_READ_STAGES, 0x0A, (profile, max_stages))
    if len(stg_pay) < 2 or stg_pay[0] != profile or not 1 <= stg_pay[1] <= max_stages:
        raise ValueError("dpi_invalid_stages")
    count = stg_pay[1]
    if len(stg_pay) < 2 + count * 4:
        raise ValueError("dpi_invalid_stages")
    axes: List[List[int]] = []
    for i in range(count):
        off = 2 + i * 4
        x = (stg_pay[off] << 8) | stg_pay[off + 1]
        y = (stg_pay[off + 2] << 8) | stg_pay[off + 3]
        if y == 0:
            y = x
        if not (COMPX_MIN_DPI <= x <= max_dpi and x % COMPX_DPI_STEP == 0
                and COMPX_MIN_DPI <= y <= max_dpi and y % COMPX_DPI_STEP == 0):
            raise ValueError("dpi_invalid_stage")
        axes.append([x, y])
    act_pay = _compx_exchange(dev, COMPX_PAGE_PROFILE, COMPX_CMD_READ_ACTIVE_STAGE, 0x02, (profile,))
    if len(act_pay) < 2 or act_pay[0] != profile or not 1 <= act_pay[1] <= count:
        raise ValueError("dpi_invalid_active_stage")
    active = act_pay[1] - 1
    target = {key: info.get(key) for key in IDENTITY_KEYS}
    target.update(provider=provider_name, profile=profile, stages=axes)
    return _dpi_view(target, active), target, active


def compx_set_device_dpi(inventory, target, value, expected_dpi, target_token, *,
                         provider_name: str, eligible_fn,
                         max_stages: int = COMPX_MAX_STAGES,
                         max_dpi: int = COMPX_MAX_DPI,
                         fixed_profile: Optional[int] = None,
                         open_device_fn=None):
    """Switch one existing CompX stage via page 1 command 0x02 with mandatory readback; never retry."""
    view, dev, attempted = None, None, False
    make_dev = open_device_fn or hid.device

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
                or target.get("provider") != provider_name
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
            and eligible_fn(info)
            and all(info.get(key) == target.get(key) for key in IDENTITY_KEYS)
        ]
        if len(matches) != 1:
            return result("dpi_target_changed")
        info = matches[0]
        dev = make_dev()
        dev.open_path(info["path"])
        view, fresh, _ = compx_dpi_state(
            dev, info, provider_name,
            max_stages=max_stages, max_dpi=max_dpi, fixed_profile=fixed_profile,
        )
        if not view["canSet"] or view["targetToken"] != target_token:
            return result("dpi_target_changed")
        if view["current"] != expected_dpi:
            return result("dpi_current_changed")
        index = offered.index(value)
        if value == expected_dpi:
            return result(success=True)
        profile = fresh["profile"]
        req = make_compx_request(COMPX_PAGE_PROFILE, COMPX_CMD_SET_ACTIVE_STAGE, 0x02, (profile, index + 1))
        attempted = True
        written = dev.send_feature_report(req)
        if written not in (None, 64, 65):
            return result("dpi_readback_unconfirmed", unknown=True)
        ack = None
        for _ in range(COMPX_DPI_TRIES):
            time.sleep(COMPX_DPI_PAUSE_S)
            resp = dev.get_feature_report(0, 65)
            ack = parse_compx_reply(resp, COMPX_PAGE_PROFILE, COMPX_CMD_SET_ACTIVE_STAGE)
            if ack is not None:
                break
        if ack is None:
            return result("dpi_readback_unconfirmed", unknown=True)
        after_view, after_target, after_active = compx_dpi_state(
            dev, info, provider_name,
            max_stages=max_stages, max_dpi=max_dpi, fixed_profile=fixed_profile,
        )
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


def _dpi_eligible(info: dict) -> bool:
    return (
        isinstance(info, dict)
        and info.get("vendor_id") in WLMOUSE_VIDS
        and info.get("product_id") != WLMOUSE_4K_PID
        and info.get("usage_page") == 0xFFFF
        and isinstance(info.get("physical_id"), str)
        and bool(info["physical_id"])
        and info.get("modelSource", info.get("model_source")) != "pack"
        and info.get("feature_report_length") in (None, 65)
        and bool(info.get("path"))
    )


def _capture_dpi(info: Optional[dict]) -> Tuple[Optional[dict], Optional[dict]]:
    if not info or not _dpi_eligible(info):
        return None, None
    dev = None
    try:
        dev = hid.device()
        dev.open_path(info["path"])
        view, target, _ = compx_dpi_state(dev, info, "wlmouse")
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
    return compx_set_device_dpi(
        inventory, target, value, expected_dpi, target_token,
        provider_name="wlmouse", eligible_fn=_dpi_eligible,
    )


class WLmouseProvider(Provider):
    name = "wlmouse"

    def __init__(self):
        self._diag: List[str] = []
        self._last: Dict[str, Tuple[int, bool, float]] = {}
        self._name: Optional[str] = None      # mouse name (remembered from a known PID)
        self._silent = 0                      # polls in a row with no reading (#62 item 5)
        self._silent_sig = None               # the device set of the last silent poll
        self._skip = 0                        # silent polls skipped since the last walk
        self._winning_info: Dict[int, dict] = {}

    def set_device_dpi(self, inventory, target, value, expected_dpi, target_token):
        return set_device_dpi(inventory, target, value, expected_dpi, target_token)

    def _read_feature(self, path: bytes) -> Tuple[Optional[int], Optional[bool]]:
        dev = hid.device()
        try:
            dev.open_path(path)
        except (OSError, IOError) as e:
            self._diag.append(f"    open: {e}")
            return None, None
        try:
            report = [0x00] + QUERY + [0x00] * (64 - len(QUERY))
            try:
                dev.send_feature_report(report)
            except (OSError, ValueError) as e:
                self._diag.append(f"    send: {e}")
            for _ in range(15):
                time.sleep(0.05)
                for length in (65, 64):
                    try:
                        resp = dev.get_feature_report(0, length)
                    except (OSError, ValueError):
                        resp = None
                    if resp:
                        batt, chg = parse_feature(resp)
                        if batt is not None:
                            self._diag.append(f"    reply: {hexdump(resp, 12)}")
                            return batt, chg
            self._diag.append("    no reply to request 0x83")
            return None, None
        finally:
            try:
                dev.close()
            except Exception:
                pass

    def _read_passive(self, path: bytes, seconds: float) -> Tuple[Optional[int], Optional[bool]]:
        dev = hid.device()
        try:
            dev.open_path(path)
            dev.set_nonblocking(True)
        except (OSError, IOError):
            return None, None
        deadline = time.time() + seconds
        try:
            while time.time() < deadline:
                try:
                    data = dev.read(64)
                except (OSError, ValueError):
                    break
                if data:
                    batt, chg = parse_heartbeat(data)
                    if batt is not None:
                        self._diag.append(f"    heartbeat: {hexdump(data, 8)}")
                        return batt, chg
                time.sleep(0.02)
        finally:
            try:
                dev.close()
            except Exception:
                pass
        return None, None

    def _read_group(self, ifaces: List[dict]) -> Tuple[Optional[int], Optional[bool]]:
        vendor = [d for d in ifaces if d.get("usage_page") == 0xFFFF]
        vendor.sort(key=lambda d: 0 if d.get("usage") == 0 else 1)
        for d in vendor:
            self._diag.append(f"  iface={d.get('interface_number')} usage="
                              f"{d.get('usage_page', 0):04x}:{d.get('usage', 0):04x}")
            batt, chg = self._read_feature(d["path"])
            if batt is not None:
                self._winning_info[d["product_id"]] = d
                return batt, chg
        # fallback: listen for the heartbeat, but only on vendor interfaces and
        # for ~2 s in total (listening on all 7 interfaces took 2 s each)
        listen = [d for d in ifaces if d.get("usage_page", 0) >= 0xFF00][:2]
        for d in listen:
            batt, chg = self._read_passive(d["path"], 2.0 / max(1, len(listen)))
            if batt is not None:
                return batt, chg
        return None, None

    def poll(self) -> List[DeviceStatus]:
        """One icon per mouse, however it is connected.

        On the cable the mouse shows up in Windows as a separate USB device with
        its own PID while the receiver stays plugged in. Treating every PID as a
        device gave a second icon on plugging in the charger while the old one
        lingered until it timed out. Now every WLmouse PID is just a data source
        for a single icon; the source that reports charging (the cable) wins.
        """
        self._diag = []
        self._winning_info = {}
        infos = []
        for vid in WLMOUSE_VIDS:
            try:
                infos += hidlist.enumerate(vid)
            except Exception as e:  # pragma: no cover
                log.warning("hid.enumerate(wlmouse): %s", e)
        if not infos:
            return []

        groups: Dict[int, List[dict]] = {}
        for d in infos:
            groups.setdefault(d["product_id"], []).append(d)

        key = "wlmouse"
        readings = []           # (batt, charging, pid)

        # #62 item 5: a walk of a silent receiver is expensive (each vendor
        # collection's refusal ~0.75 s, then the ~2 s listen), and a switched-off
        # mouse paid it on every poll forever. While something is shown, or while
        # the first tries run, every poll walks; once nothing is shown, only every
        # BACKOFF_EVERY-th does - and a change in the receiver's device set (the
        # charging cable appearing) walks at once.
        last_v = self._last.get(key)
        fresh = last_v is not None and time.time() - last_v[2] < ASLEEP_KEEP
        sig = tuple(sorted((pid, len(ifaces)) for pid, ifaces in groups.items()))
        if not fresh:
            if self._silent < BACKOFF_FIRST:
                pass                     # the first few tries are always made
            elif self._silent_sig == sig and self._skip < BACKOFF_EVERY - 1:
                self._skip += 1
                self._diag.append(f"[WLmouse] silent for {self._silent} poll(s); next walk "
                                  f"in up to {BACKOFF_EVERY - self._skip} poll(s)")
                return []
            else:
                self._skip = 0           # the setup changed, or it is time: walk now

        def is_receiver(item):
            pid, ifaces = item
            return "RECEIVER" in (ifaces[0].get("product_string") or "").upper() or pid in RECEIVERS

        # the mouse on the cable first, then the receiver
        for pid, ifaces in sorted(groups.items(), key=is_receiver):
            if readings and readings[0][1] and is_receiver((pid, ifaces)):
                self._diag.append(f"[WLmouse] pid={pid:04x}: mouse is on the cable, skipping the receiver")
                continue
            product = (ifaces[0].get("product_string") or "").strip()
            if pid in KNOWN:
                self._name = KNOWN[pid]
            elif not getattr(self, "_name", None) and product:
                self._name = product if "wl" in product.lower() else f"WLmouse {product}"
            self._diag.append(f"[WLmouse] pid={pid:04x} product='{product}'")
            batt, chg = self._read_group(ifaces)
            if batt is not None:
                readings.append((batt, bool(chg), pid))
        name = getattr(self, "_name", None) or "WLmouse"

        if readings:
            # the cable (charging) beats the receiver: a wired mouse may be silent on the radio
            readings.sort(key=lambda r: (not r[1],))
            batt, chg, pid = readings[0]
            self._diag.append(f"  -> using pid={pid:04x}: {batt}%{' (charging)' if chg else ''}")
            self._last[key] = (batt, chg, time.time())
            self._silent = 0
            self._silent_sig = None
            self._skip = 0
            dpi, dpi_target = _capture_dpi(self._winning_info.get(pid))
            return [DeviceStatus(key, name, batt, chg, True, "wlmouse", dpi=dpi, dpi_target=dpi_target)]

        # The receiver is silent. It cannot tell a switched-off mouse from one that
        # fell asleep a few seconds after the last movement, so keep the last value
        # (greyed out) for a while and then hide the icon; it comes back as soon as
        # the mouse answers again.
        self._silent += 1
        self._silent_sig = sig
        last = self._last.get(key)
        if last and time.time() - last[2] < ASLEEP_KEEP:
            return [DeviceStatus(key, name, last[0], last[1], False, "wlmouse")]
        return []

    def diagnostics(self) -> List[str]:
        return list(self._diag)
