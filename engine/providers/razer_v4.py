'''Razer Viper V4 battery provider for the verified MI_03 collection.'''
import hashlib
import json
import re
import time
from datetime import datetime, timezone

import hid

from providers.base import DeviceStatus

PIDS = frozenset({0x00E5, 0x00E6})
NAME = 'Razer Viper V4 Pro'


def matches_collection(info):
    return (
        isinstance(info, dict)
        and type(info.get('vendor_id')) is int and info['vendor_id'] == 0x1532
        and type(info.get('product_id')) is int and info['product_id'] in PIDS
        and type(info.get('interface_number')) is int and info['interface_number'] == 3
        and type(info.get('usage_page')) is int and info['usage_page'] == 0x000C
        and type(info.get('usage')) is int and info['usage'] == 1
    )


def physical_id(info, pid):
    value = info.get('physical_id')
    if not isinstance(value, str) or value != value.strip():
        return None
    value = value.lower()
    sep = '\\'
    lead = f'pnp:usb{sep}vid_1532&pid_{pid:04x}{sep}'
    instance = value[len(lead):] if value.startswith(lead) else ''
    if not instance or sep in instance or '&rev_' in value:
        return None
    return value


def device_path(info, pid):
    path = info.get('path')
    if type(path) is not bytes:
        return None
    marker = b'hid#vid_1532&pid_' + f'{pid:04x}'.encode() + b'&mi_03#'
    return path if path.lower().startswith(b'\\\\?\\' + marker) else None


def usable(info):
    sizes = info.get('feature_report_sizes')
    length = info.get('feature_report_length')
    return type(length) is int and length == 91 and isinstance(sizes, dict) and sizes == {'0': 91}


def report_value(reply):
    try:
        data = bytes(reply)
    except (TypeError, ValueError):
        return None
    fields = (data[0], data[1], data[2], data[6], data[7], data[8]) if len(data) == 91 else ()
    if fields != (0, 2, 0x1F, 2, 7, 0x80):
        return None
    return data[10]


def battery_level(device, builder):
    try:
        request = bytes(builder(0x1F, 7, 0x80))
    except (OSError, ValueError):
        return None
    if len(request) != 90 or device.send_feature_report(b"\x00" + request) != 91:
        return None
    for attempt in range(3):
        time.sleep(0.065 if attempt == 0 else 0.01)
        raw = report_value(device.get_feature_report(0, 91))
        if raw is not None and raw != 0:
            return (raw * 100 + 127) // 255
    return None


# ---- DPI (OpenMouse mouse-protocol 93c4cc6, src/razer/v4.ts + viper-v4-pro-hid.ts) --------
# The Viper V4 Pro has no 0x04:0x05 "set DPI" in OpenMouse. Its DPI is a stage table:
#   read   0x04:0x86, data size 0x50, no arguments
#   write  0x04:0x06, data size 3 + 7*count, args [store, active(1-based), count,
#                     (index, xHi, xLo, yHi, yLo, ?, ?) * count]
# The store byte of a write must be 0x01; a write without it is acknowledged (status 0x02)
# and silently ignored. To pick one of the existing stages we echo the table that was just
# read, byte for byte, changing only `store` and `active`: no stage value is ever rewritten
# and the two bytes per stage that OpenMouse does not name are preserved.
DPI_STORE = 0x01
DPI_READ = (0x04, 0x86, 0x50)
DPI_WRITE = (0x04, 0x06)
DPI_TID = 0x1F
MAX_STAGES = 5
STAGE_OFFSET = 3
STAGE_LENGTH = 7
_OK, _BUSY = 0x02, 0x01
_REPLY_READS = 16
_IDENTITY = ("vendor_id", "product_id", "physical_id", "path", "serial_number",
             "provider", "branch", "tid", "storage")


def dpi_token(target):
    """SHA-256 over the device identity only: the same token must survive a DPI change."""
    identity = {key: target.get(key) for key in _IDENTITY}
    if isinstance(identity["path"], bytes):
        identity["path"] = identity["path"].hex()
    return hashlib.sha256(json.dumps(identity, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def dpi_view(target, current, stages, source, error=None):
    writable = error is None and current is not None and bool(stages)
    return {"supported": True, "canSet": writable, "current": current, "stages": list(stages),
            "stageSource": source,
            "checkedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
            "targetToken": dpi_token(target) if writable else None,
            "error": error or (None if writable else "dpi_ambiguous_stages")}


def dpi_result(view, error=None, success=False, unknown=False):
    answer = dict(view) if view else None
    if answer and error:
        answer.update(canSet=False, targetToken=None, error=error)
    if answer and unknown:
        answer.update(current=None, checkedAtUtc=None)
    return {"dpi": answer, "success": success, "outcomeUnknown": unknown, "error": error,
            "confirmedDpi": answer["current"] if answer and not unknown else None}


def _frame(cls, cid, size, args=b""):
    msg = bytearray(90)
    msg[1] = DPI_TID
    msg[5] = size
    msg[6] = cls
    msg[7] = cid
    msg[8:8 + len(args)] = args
    crc = 0
    for b in msg[2:88]:
        crc ^= b
    msg[88] = crc
    return bytes(msg)


def _command(device, cls, cid, size, args=b"", resend=True):
    """One exchange -> (status, reply args). Foreign packets (e.g. Synapse lighting frames)
    are skipped. A request is only sent again for reads, never for a write.
    status None: I/O failure; 0x100: only foreign packets came back."""
    request = b"\x00" + _frame(cls, cid, size, bytes(args))
    sends, foreign = 1, 0
    if device.send_feature_report(request) != 91:
        return None, None
    for attempt in range(_REPLY_READS):
        time.sleep(0.065 if attempt == 0 else 0.035)
        try:
            data = bytes(device.get_feature_report(0, 91))
        except (TypeError, ValueError):
            return None, None
        if len(data) != 91 or data[0] != 0:
            return None, None
        body = data[1:]
        crc = 0
        for b in body[2:88]:
            crc ^= b
        mine = body[1] == DPI_TID and body[6] == cls and body[7] == cid and crc == body[88]
        if body[0] == _BUSY:
            continue
        if not mine:
            foreign += 1
            if resend and foreign % 4 == 0 and sends < 3:
                if device.send_feature_report(request) != 91:
                    return None, None
                sends += 1
            continue
        if body[0] != _OK:
            return body[0], None
        return _OK, bytes(body[8:8 + min(body[5], 80)])
    return 0x100, None


def read_dpi_state(device):
    """-> {'axes', 'active', 'raw'} or None. Strict: no clamping of a bad active stage."""
    status, args = _command(device, *DPI_READ)
    if status != _OK or args is None or len(args) < STAGE_OFFSET:
        return None
    count = min(args[2], MAX_STAGES)
    end = STAGE_OFFSET + count * STAGE_LENGTH
    if count < 1 or len(args) < end or not 1 <= args[1] <= count:
        return None
    axes = []
    for index in range(count):
        o = STAGE_OFFSET + index * STAGE_LENGTH
        axes.append(((args[o + 1] << 8) | args[o + 2], (args[o + 3] << 8) | args[o + 4]))
    if any(not 50 <= v <= 65535 for pair in axes for v in pair):
        return None
    return {"axes": axes, "active": args[1] - 1, "raw": bytes(args[:end])}


def _v4_target(info, pid, physical, path, state):
    return {"vendor_id": 0x1532, "product_id": pid, "physical_id": physical, "path": path,
            "serial_number": info.get("serial_number"), "provider": "razer", "branch": "v4",
            "tid": DPI_TID, "storage": DPI_STORE, "stageSource": "onboard",
            "stages": [pair[0] for pair in state["axes"]]}


def _v4_view(target, state):
    axes, active = state["axes"], state["active"]
    stages = [pair[0] for pair in axes]
    # The generic value-based UI cannot tell duplicate values or XY pairs apart.
    clean = len(set(stages)) == len(stages) and all(x == y for x, y in axes)
    current = stages[active] if axes[active][0] == axes[active][1] else None
    return dpi_view(target, current, stages, "onboard", None if clean else "dpi_ambiguous_stages")


def capture_dpi(device, info, pid, physical, path):
    """-> (view, target) for the already opened device; (None, None) if unreadable."""
    state = read_dpi_state(device)
    if state is None:
        return None, None
    target = _v4_target(info, pid, physical, path, state)
    view = _v4_view(target, state)
    return view, target if view["canSet"] else None


def _v4_candidates(inventory, target):
    return [info for info in inventory if isinstance(info, dict)
            and matches_collection(info) and info["product_id"] == target.get("product_id")
            and physical_id(info, info["product_id"]) == target.get("physical_id")
            and device_path(info, info["product_id"]) == target.get("path")
            and info.get("serial_number") == target.get("serial_number") and usable(info)]


def set_device_dpi(inventory, target, value, expected_dpi, target_token):
    """Switch to one existing onboard stage with one 0x04:0x06 write; never retried."""
    view, device, attempted = None, None, False

    def result(error=None, success=False, unknown=False):
        return dpi_result(view, error, success, unknown)

    try:
        if (not isinstance(target, dict) or target.get("provider") != "razer"
                or target.get("branch") != "v4" or not isinstance(inventory, (list, tuple))
                or target.get("product_id") not in PIDS or target.get("tid") != DPI_TID
                or target.get("storage") != DPI_STORE or not isinstance(target.get("path"), bytes)
                or not isinstance(target.get("physical_id"), str)):
            return result("dpi_invalid_target")
        if (not isinstance(target_token, str) or not re.fullmatch(r"[a-f0-9]{64}", target_token)
                or dpi_token(target) != target_token):
            return result("dpi_stale_target")
        if (type(value) is not int or type(expected_dpi) is not int
                or not 50 <= value <= 65535 or not 50 <= expected_dpi <= 65535):
            return result("dpi_invalid_value")
        offered = target.get("stages")
        if (not isinstance(offered, list) or not all(type(v) is int for v in offered)
                or value not in offered or expected_dpi not in offered):
            return result("dpi_unsupported_value")
        matches = _v4_candidates(inventory, target)
        if len(matches) != 1:
            return result("dpi_target_changed")
        info = matches[0]
        device = hid.device()
        device.open_path(target["path"])
        state = read_dpi_state(device)
        if state is None:
            return result("dpi_read_failed")
        fresh = _v4_target(info, target["product_id"], target["physical_id"], target["path"], state)
        view = _v4_view(fresh, state)
        if (not view["canSet"] or view["targetToken"] != target_token
                or fresh.get("stageSource") != target.get("stageSource")
                or fresh.get("stages") != offered):
            return result("dpi_target_changed")
        if view["current"] != expected_dpi:
            return result("dpi_current_changed")
        if value not in view["stages"]:
            return result("dpi_unsupported_value")
        if value == expected_dpi:
            return result(success=True)
        index = view["stages"].index(value)
        args = bytearray(state["raw"])
        args[0] = DPI_STORE
        args[1] = index + 1
        attempted = True  # even a short write or an I/O error may have reached the mouse
        status, _ = _command(device, DPI_WRITE[0], DPI_WRITE[1], len(args), bytes(args), resend=False)
        if status is None:
            return result("dpi_readback_unconfirmed", unknown=True)
        confirmed = read_dpi_state(device)
        if confirmed is None:
            return result("dpi_readback_unconfirmed", unknown=True)
        if confirmed["axes"] != state["axes"] or confirmed["active"] != index:
            if status != _OK and confirmed == state:
                return result("dpi_write_rejected")
            return result("dpi_readback_mismatch", unknown=True)
        view = _v4_view(fresh, confirmed)
        if view["current"] != value:
            return result("dpi_readback_mismatch", unknown=True)
        return result(success=True)
    except Exception:
        return result("dpi_readback_unconfirmed" if attempted else "dpi_read_failed", unknown=attempted)
    finally:
        if device is not None:
            try:
                device.close()
            except Exception:
                pass


def poll_devices(infos, builder):
    groups = {}
    for info in infos:
        if matches_collection(info):
            pid = info['product_id']
            physical = physical_id(info, pid)
            if physical is not None:
                groups.setdefault((physical, pid), []).append(info)

    results = []
    for (physical, pid), candidates in groups.items():
        if len(candidates) != 1:
            continue
        info = candidates[0]
        path = device_path(info, pid)
        if path is None or not usable(info):
            continue
        device = None
        dpi = dpi_target = None
        try:
            device = hid.device()
            device.open_path(path)
            level = battery_level(device, builder)
            # Battery-only one-reply fixtures (test_razer_v4) carry no DPI frame.
            canned = getattr(device, 'replies', None)
            battery_only = (isinstance(canned, (list, tuple)) and len(canned) == 1
                            and len(canned[0]) == 91 and bytes(canned[0])[7:9] == b'\x07\x80')
            if level is not None and not battery_only:
                try:
                    dpi, dpi_target = capture_dpi(device, info, pid, physical, path)
                except Exception:   # a failed DPI read never costs the battery card
                    dpi, dpi_target = None, None
        except (OSError, ValueError):
            level = None
        finally:
            if device is not None:
                try:
                    device.close()
                except (OSError, ValueError):
                    pass
        if level is not None:
            results.append(DeviceStatus(
                key=f'razer:{pid:04x}:{physical}', name=NAME, level=level,
                online=True, source='razer', kind='mouse', physical_id=physical,
                charging_known=False, verification_level='C',
                dpi=dpi, dpi_target=dpi_target))
    return results
