"""Razer devices (headsets, mice, keyboards with a wireless receiver).

Protocol: a 90-byte feature report (report id 0), the same one Synapse and
OpenRazer use.

    [0]  status         (0x00 in a request; 0x02 = OK, 0x01 = busy, 0x03 = failure,
                         0x04 = timeout / device not responding, 0x05 = not supported)
    [1]  transaction id (0x1f for most mice, 0x3f for headsets, 0xff for older ones)
    [2..3] remaining packets
    [4]  protocol type
    [5]  data size
    [6]  command class  (0x07 = power)
    [7]  command id     (0x80 = battery level, 0x84 = charging)
    [8..87] arguments   (arg[1] = value)
    [88] crc = XOR of bytes 2..87
    [89] reserved
"""
from __future__ import annotations

import re
import time
from dataclasses import replace
from typing import Dict, List, Optional, Tuple

import hid

from . import blackshark, blackshark2020, blackshark_hyperspeed, hidlist, razer_v4
from .base import DeviceStatus, Provider, hexdump, log

from .razer_profiles import RAZER_VID, KNOWN, KEYBOARD_INTERFACE
from .razer_v4 import dpi_result, dpi_token, dpi_view
from .profiles import builtins, profiles

TRANSACTION_IDS = (0x1F, 0x3F, 0xFF, 0x9F, 0x08)

# Devices that are not in KNOWN are only polled when their name suggests a
# battery: some wired Razer devices (e.g. the Huntsman V2 keyboard) answer the
# battery command too, with a meaningless value.
WIRELESS_WORDS = ("hyperspeed", "wireless", "receiver", "dongle", "dock",
                  "blackshark", "barracuda", "nari")


def maybe_wireless(pid: int, name: str) -> bool:
    n = name.lower()
    return pid in KNOWN or pid in blackshark.PA_PIDS or any(w in n for w in WIRELESS_WORDS)

STATUS_OK = 0x02
STATUS_BUSY = 0x01
STATUS_TIMEOUT = 0x04     # receiver present, device not responding (off / asleep)
STATUS_NOT_SUPPORTED = 0x05
# Not a Razer status byte (those fit in a byte): every packet read back belonged to
# another command, i.e. to another app that talks to the same collection. RGB software
# sending lighting frames (class 0x0f) does this many times a second, and each of its
# requests overwrites the device's reply buffer (issue #108: 13 reads in a row carried
# "0f:03", OpenRazer's set_custom_frame, with a new transaction id each time).
STATUS_FOREIGN = 0x100
# how many times one request is sent while only foreign packets come back
SENDS = 3

# How long a device that stopped answering keeps its last value, greyed out (the
# translucent icon the README describes for a sleeping mouse), before the icon goes
# away. Same value as the WLmouse provider uses.
ASLEEP_KEEP = 300


def build_request(transaction_id: int, cmd_class: int, cmd_id: int, size: int = 0x02,
                  args: bytes = b"") -> bytes:
    msg = bytearray(90)
    msg[1] = transaction_id
    msg[5] = size
    msg[6] = cmd_class
    msg[7] = cmd_id
    msg[8:8 + len(args)] = args
    crc = 0
    for b in msg[2:88]:
        crc ^= b
    msg[88] = crc
    return bytes(msg)


def normalize_reply(resp) -> Optional[List[int]]:
    """hidapi on Windows returns the reply with the report id as the first byte."""
    if not resp:
        return None
    data = list(resp)
    if len(data) >= 91:
        data = data[1:91]
    if len(data) < 90:
        return None
    return data


def parse_reply(data: List[int], cmd_class: int, cmd_id: int) -> Tuple[int, Optional[int]]:
    """-> (status, arg1). arg1 is None if the reply is not for our command."""
    status = data[0]
    if data[6] != cmd_class or data[7] != cmd_id:
        return status, None
    return status, data[9]


# ---- DPI (mice only) --------------------------------------------------------------------
# Byte layout from OpenMouse mouse-protocol @93c4cc601d448ea481eb59d7b785eec75ae16ec2
# (src/razer/codec.ts RAZER_READ / RAZER_WRITE, src/drivers/razer/hid.ts):
#   read DPI XY    0x04:0x85  data size 0x07  args [store]               reply [store, xHi, xLo, yHi, yLo, 0, 0]
#   read stages    0x04:0x86  data size 0x26  args [0x00]                reply [store, active(1-based), count,
#                                                                                (index, xHi, xLo, yHi, yLo, ?, ?) * count]
#   write DPI XY   0x04:0x05  data size 0x07  args [store, xHi, xLo, yHi, yLo, 0, 0]
# The store byte of the write must be 0x01 (RAZER_STORAGE): without it the mouse answers
# status 0x02 and ignores the command. The data size is 0x07, never 0x04.
# A DPI command is only ever sent to a mouse in DPI_MICE: never to a headset, keyboard,
# charging dock or standalone dongle. The Viper V4 Pro has its own table protocol (razer_v4).
DPI_STORAGE = 0x01
QUICK_DPI = (400, 800, 1600, 3200)
DPI_MIN, DPI_MAX = 50, 65535
# KNOWN rows registered under "mouse" whose USB product is a charging dock.
DPI_EXCLUDED = frozenset({0x00A4, 0x003F})
DPI_MICE = frozenset(d.product_id for d in builtins() if d.provider == "razer" and d.kind == "mouse"
                     and d.vendor_id == RAZER_VID) - DPI_EXCLUDED - razer_v4.PIDS
_NON_MOUSE_WORDS = ("blackshark", "barracuda", "nari", "kraken", "headset",
                    "keyboard", "blackwidow", "deathstalker", "huntsman", "dock")
_READS = 14       # feature-report reads per exchange (~1 s while the device is busy)


def _crc_ok(data: List[int]) -> bool:
    crc = 0
    for b in data[2:88]:
        crc ^= b
    return crc == data[88]


def _exchange(dev, tid: int, cmd_class: int, cmd_id: int, size: int, args=(),
              resend: bool = True) -> Tuple[Optional[int], Optional[bytes]]:
    """One DPI request -> (status, reply args).

    Packets that answer another command (Synapse lighting frames, class 0x0f) or carry a
    bad checksum are skipped; the same read may be sent up to three times while only such
    packets come back. A write (resend=False) is sent exactly once, whatever comes back.
    status None: I/O failure; STATUS_FOREIGN: nothing but foreign packets; the reply args
    are only set for STATUS_OK."""
    req = build_request(tid, cmd_class, cmd_id, size, bytes(args))
    try:
        dev.send_feature_report(b"\x00" + req)
    except (OSError, ValueError):
        return None, None
    sends, foreign, busy = 1, 0, 0
    time.sleep(0.06)
    for _ in range(_READS):
        try:
            data = normalize_reply(dev.get_feature_report(0x00, 91))
        except (OSError, ValueError):
            return None, None
        if data is None:
            return None, None
        status = data[0]
        if data[6] != cmd_class or data[7] != cmd_id or (
                status == STATUS_OK and (data[1] != tid or not _crc_ok(data))):
            foreign += 1
            if resend and foreign % 4 == 0 and sends < SENDS:
                try:
                    dev.send_feature_report(b"\x00" + req)
                except (OSError, ValueError):
                    return None, None
                sends += 1
        elif status == STATUS_BUSY:
            busy += 1
        elif status != STATUS_OK:
            return status, None
        else:
            return STATUS_OK, bytes(data[8:8 + min(data[5], 80)])
        time.sleep(0.08)
    return (STATUS_FOREIGN if foreign else STATUS_BUSY), None


def _dpi_xy(dev, tid: int) -> Tuple[Optional[int], Optional[Tuple[int, int]]]:
    status, args = _exchange(dev, tid, 0x04, 0x85, 0x07, (DPI_STORAGE,), resend=False)
    if status != STATUS_OK or args is None or len(args) < 5:
        return status, None
    x, y = (args[1] << 8) | args[2], (args[3] << 8) | args[4]
    if not (DPI_MIN <= x <= DPI_MAX and DPI_MIN <= y <= DPI_MAX):
        return status, None
    return status, (x, y)


def _dpi_table(dev, tid: int) -> Tuple[Optional[int], List[Tuple[int, int]]]:
    status, args = _exchange(dev, tid, 0x04, 0x86, 0x26, (0x00,), resend=False)
    stages: List[Tuple[int, int]] = []
    if status == STATUS_OK and args is not None and len(args) >= 3 and 1 <= args[2] <= 5:
        count = args[2]
        if len(args) >= 3 + (count - 1) * 7 + 5:
            for index in range(count):
                o = 3 + index * 7
                stages.append(((args[o + 1] << 8) | args[o + 2], (args[o + 3] << 8) | args[o + 4]))
    return status, stages


def _onboard_stages(axes) -> Optional[List[int]]:
    """The onboard table, only when the value-based UI can use it unambiguously."""
    if not axes or len(axes) > 5 or any(x != y or not DPI_MIN <= x <= DPI_MAX for x, y in axes):
        return None
    stages = [x for x, _ in axes]
    return stages if len(set(stages)) == len(stages) else None


def _quick_stages(current: int) -> List[int]:
    return sorted(set(QUICK_DPI) | {current})[:8]


def _dpi_target(info, tid: int) -> dict:
    return {"vendor_id": RAZER_VID, "product_id": info["product_id"], "physical_id": info["physical_id"],
            "path": info["path"], "serial_number": info.get("serial_number"), "provider": "razer",
            "branch": "classic", "tid": tid, "storage": DPI_STORAGE}


def _dpi_eligible(info, pids=DPI_MICE) -> bool:
    product = (info.get("product_string") or "").lower() if isinstance(info, dict) else ""
    return (isinstance(info, dict) and info.get("vendor_id") == RAZER_VID and info.get("product_id") in pids
            and info.get("kind") not in ("headset", "keyboard", "gamepad")
            and not any(w in product for w in _NON_MOUSE_WORDS)
            and info.get("modelSource", info.get("model_source")) != "pack"
            and isinstance(info.get("physical_id"), str) and bool(info["physical_id"])
            and isinstance(info.get("path"), bytes))


def _capture_dpi(dev, info, tid: int, skip_table: bool = False):
    """-> (view, target, table_status) for an opened mouse; view None if the DPI is unreadable."""
    status, xy = _dpi_xy(dev, tid)
    if xy is None:
        return None, None, status
    target = _dpi_target(info, tid)
    if xy[0] != xy[1]:
        return dpi_view(target, None, [], "quick", "dpi_axes_differ"), None, None
    current, stages, source, table_status = xy[0], None, "quick", None
    if not skip_table:
        table_status, axes = _dpi_table(dev, tid)
        stages = _onboard_stages(axes)
    if stages is not None:
        source = "onboard"
    else:
        stages = _quick_stages(current)
    target.update(stages=list(stages), stageSource=source)
    return dpi_view(target, current, stages, source), target, table_status


def set_device_dpi(inventory, target, value, expected_dpi, target_token):
    """Dispatch one identity-bound DPI write: Viper V4 Pro table or classic 0x04:0x05."""
    if isinstance(target, dict) and target.get("branch") == "v4":
        return razer_v4.set_device_dpi(inventory, target, value, expected_dpi, target_token)
    return _set_classic_dpi(inventory, target, value, expected_dpi, target_token)


def _set_classic_dpi(inventory, target, value, expected_dpi, target_token):
    """Re-read, check, then exactly one 0x04:0x05 write and a 0x04:0x85 read-back; never retried."""
    view, dev, attempted = None, None, False

    def result(error=None, success=False, unknown=False):
        return dpi_result(view, error, success, unknown)

    try:
        if (not isinstance(target, dict) or target.get("provider") != "razer"
                or target.get("branch") != "classic" or not isinstance(inventory, (list, tuple))
                or target.get("product_id") not in DPI_MICE or target.get("tid") not in TRANSACTION_IDS
                or target.get("storage") != DPI_STORAGE or not isinstance(target.get("path"), bytes)
                or not isinstance(target.get("physical_id"), str)):
            return result("dpi_invalid_target")
        if (not isinstance(target_token, str) or not re.fullmatch(r"[a-f0-9]{64}", target_token)
                or dpi_token(target) != target_token):
            return result("dpi_stale_target")
        if (type(value) is not int or type(expected_dpi) is not int
                or not DPI_MIN <= value <= DPI_MAX or not DPI_MIN <= expected_dpi <= DPI_MAX):
            return result("dpi_invalid_value")
        offered = target.get("stages")
        if (not isinstance(offered, list) or not all(type(v) is int for v in offered)
                or value not in offered):
            return result("dpi_unsupported_value")
        matches = [info for info in inventory if _dpi_eligible(info)
                   and all(info.get(key) == target.get(key) for key in
                           ("vendor_id", "product_id", "physical_id", "path", "serial_number"))]
        if len(matches) != 1:
            return result("dpi_target_changed")
        tid = target["tid"]
        dev = hid.device()
        dev.open_path(matches[0]["path"])
        view, fresh, _ = _capture_dpi(dev, matches[0], tid)
        if view is None:
            return result("dpi_read_failed")
        if (fresh is None or not view["canSet"] or view["targetToken"] != target_token
                or fresh.get("stageSource") != target.get("stageSource")
                or fresh.get("stageSource") == "onboard" and fresh.get("stages") != offered):
            return result("dpi_target_changed")
        if view["current"] != expected_dpi:
            return result("dpi_current_changed")
        if value not in view["stages"]:
            return result("dpi_unsupported_value")
        if value == expected_dpi:
            return result(success=True)
        # Recheck the live value immediately before the one mutation.
        if _dpi_xy(dev, tid)[1] != (expected_dpi, expected_dpi):
            return result("dpi_current_changed")
        attempted = True   # even a failed or short send may have reached the mouse
        wstatus, _ = _exchange(dev, tid, 0x04, 0x05, 0x07,
                               (DPI_STORAGE, value >> 8, value & 0xFF, value >> 8, value & 0xFF, 0, 0),
                               resend=False)
        if wstatus is None:
            return result("dpi_readback_unconfirmed", unknown=True)
        time.sleep(0.01)   # settle before the read-back; not a resend
        _, confirmed = _dpi_xy(dev, tid)
        if confirmed is None:
            return result("dpi_readback_unconfirmed", unknown=True)
        if confirmed != (value, value):
            if wstatus in (STATUS_NOT_SUPPORTED, 0x03) and confirmed == (expected_dpi, expected_dpi):
                return result("dpi_write_rejected")
            return result("dpi_readback_mismatch", unknown=True)
        stages = (_quick_stages(value) if fresh["stageSource"] == "quick" else fresh["stages"])
        view = dpi_view(fresh, value, stages, fresh["stageSource"])
        return result(success=True)
    except Exception:
        return result("dpi_readback_unconfirmed" if attempted else "dpi_read_failed", unknown=attempted)
    finally:
        if dev is not None:
            try:
                dev.close()
            except Exception:
                pass


class _Cand:
    """Cached working HID interface and transaction id for a PID."""

    def __init__(self, path: bytes, tid: int):
        self.path = path
        self.tid = tid


class RazerProvider(Provider):
    name = "razer"

    def __init__(self, device_pack=None):
        self._cache: Dict[Tuple[int, str], _Cand] = {}
        self._foreign: Dict[Tuple[int, str], int] = {}
        self._rediscovery: Dict[Tuple[int, str], Tuple[bytes, int]] = {}
        self._dead: Dict[bytes, float] = {}   # interfaces known not to respond
        self._diag: List[str] = []
        self._failing: Dict[str, str] = {}    # key -> reason of the ongoing failure
        self._last: Dict[str, Tuple[int, bool, float]] = {}   # last good level per device
        self._dpi_infos: Dict[bytes, dict] = {}      # path -> HID info of the current poll
        self._dpi_result = None                       # (path, view, target) of the last good read
        self._dpi_no_table: set = set()               # (pid, path) that answered 0x04:0x86 "unsupported"
        self._dpi_no_xy: set = set()                  # (pid, path) that answered 0x04:0x85 "unsupported"
        self.configure_pack(device_pack)

    def configure_pack(self, device_pack):
        from device_pack import EMPTY_PACK
        self.device_pack = device_pack or EMPTY_PACK
        self._pack_entries = {e.match.product_id: e for e in self.device_pack.entries
                              if e.provider == self.name}
        self._profiles = {(p.name, p.revision): p for p in profiles() if p.provider == self.name}
        self._known = {d.product_id: (d.name, dict(self._profiles[d.profile, d.profile_rev].parameters)["transaction_id"])
                       for d in builtins() if d.provider == self.name}
        for pid, entry in self._pack_entries.items():
            self._known[pid] = (entry.name, dict(self._profiles[entry.profile, entry.profile_rev].parameters)["transaction_id"])
        self._known = {pid: row for pid, row in self._known.items()
                       if not self.device_pack.is_disabled(self.name, RAZER_VID, pid)}
        self._cache.clear()
        self._foreign.clear()
        self._rediscovery.clear()
        self._dead.clear()
        self._last.clear()
        self._failing.clear()
        self._read_errors = set()
        self._public_errors = set()
        # Mice only: never a headset, keyboard, dock, dongle or pack-defined model.
        self._dpi_pids = frozenset(pid for pid in DPI_MICE
                                   if pid in self._known and pid not in self._pack_entries)
        self._dpi_infos = {}
        self._dpi_result = None
        self._dpi_no_table = set()
        self._dpi_no_xy = set()

    def failure_code(self) -> str:
        """One bounded, identity-free cause for the existing reader-health UI.

        Prefer evidence from a reply over failures opening unrelated collections.
        This reports an observation, not a diagnosis of Synapse or device sleep.
        Legacy diagnostics include raw OS errors and must remain local.
        """
        priorities = {"no_reply": 0, "no_usable_interface": 1,
                      "interfaces_cooling_down": 2, "enumerate_failed": 3,
                      "open_failed": 4, "send_failed": 5, "get_failed": 6,
                      "short_reply": 7, "busy_timeout": 9, "device_timeout_04": 10,
                      "foreign_reply": 11}
        if not self._public_errors:
            return ""
        cause = max(self._public_errors, key=lambda c: (priorities.get(c, 8), c))
        return "razer_" + cause

    # ---- low level -------------------------------------------------------
    def _send(self, dev, req: bytes, tid: int) -> bool:
        try:
            dev.send_feature_report(b"\x00" + req)
        except (OSError, ValueError) as e:
            self._diag.append(f"    send tid={tid:02x}: {e}")
            self._read_errors.add("send_failed")
            return False
        return True

    def _query(self, dev, tid: int, cmd_class: int, cmd_id: int) -> Tuple[Optional[int], Optional[int]]:
        req = build_request(tid, cmd_class, cmd_id)
        if not self._send(dev, req, tid):
            return None, None
        sends, foreign = 1, 0
        # The device does not reply instantly: poll for up to ~1 s while status is "busy".
        deadline = time.time() + 1.0
        time.sleep(0.06)
        while True:
            try:
                resp = dev.get_feature_report(0x00, 91)
            except (OSError, ValueError) as e:
                self._diag.append(f"    get tid={tid:02x}: {e}")
                self._read_errors.add("get_failed")
                return None, None
            data = normalize_reply(resp)
            if data is None:
                self._diag.append(f"    short reply: {hexdump(resp)}")
                self._read_errors.add("short_reply")
                return None, None
            status, value = parse_reply(data, cmd_class, cmd_id)
            self._diag.append(
                f"    tid={tid:02x} cmd={cmd_class:02x}:{cmd_id:02x} -> status={status:02x} "
                f"tid'={data[1]:02x} raw={hexdump(data, 12)}")
            # Keep reading while the device is busy, and also while the packet in hand is
            # not the answer to this command. The status byte alone cannot tell the two
            # apart: Razer Synapse polls the same collection (LED state is class 0x0f) and
            # its replies carry status 0x02 exactly like ours. Giving up on the first reply
            # is how a DeathAdder V2 Pro answered a class 0x07:0x80 battery request with
            # "status=02 tid'=1f cmd=0f:03" and got written off as "off or asleep" - see
            # issue #3. Whether the real reply is behind that packet in the queue is not
            # verified on that hardware; reading on is strictly better than stopping.
            # (only a success status can hide somebody else's packet: 04 asleep and 05
            # not supported are final answers and come back right away)
            is_foreign = status == STATUS_OK and value is None
            if time.time() < deadline and (status == STATUS_BUSY or is_foreign):
                if is_foreign:
                    foreign += 1
                    # The other app's request may have replaced ours before the device
                    # read it, and then our answer never comes: ask again, a few times.
                    if foreign % 4 == 0 and sends < SENDS:
                        if not self._send(dev, req, tid):
                            return None, None
                        sends += 1
                        self._diag.append(f"    sent tid={tid:02x} again ({sends} of {SENDS})")
                time.sleep(0.08)
                continue
            if is_foreign:
                # a success status on somebody else's packet is not an answer to ours
                self._diag.append("    reply command did not match; cause unconfirmed")
                return STATUS_FOREIGN, None
            return status, value

    def _read_dpi(self, dev, path: bytes, tid: int) -> None:
        """Best effort, on the handle that just answered the battery query. Whatever happens
        here, the battery card is unaffected."""
        info = self._dpi_infos.get(path)
        if not _dpi_eligible(info, self._dpi_pids):
            return
        pid = info["product_id"]
        key = (pid, path)
        if key in self._dpi_no_xy:
            return
        try:
            view, target, table_status = _capture_dpi(dev, info, tid, key in self._dpi_no_table)
        except Exception:
            return
        if view is None:
            if table_status == STATUS_NOT_SUPPORTED:
                self._dpi_no_xy.add(key)
            return
        if table_status == STATUS_NOT_SUPPORTED:
            self._dpi_no_table.add(key)
        self._dpi_result = (path, view, target)

    def _read(self, path: bytes, tid: int) -> Tuple[Optional[int], Optional[int], Optional[bool]]:
        """-> (status, level%, charging)"""
        self._dpi_result = None
        dev = hid.device()
        try:
            dev.open_path(path)
        except (OSError, IOError) as e:
            self._diag.append(f"    open: {e}")
            self._read_errors.add("open_failed")
            return None, None, None
        try:
            status, raw = self._query(dev, tid, 0x07, 0x80)
            if status != STATUS_OK or raw is None:
                if status is not None:
                    self._read_errors.add(
                        "foreign_reply" if status == STATUS_FOREIGN else
                        "busy_timeout" if status == STATUS_BUSY else
                        "device_timeout_04" if status == STATUS_TIMEOUT else
                        f"reply_status_{status:02x}")
                return status, None, None
            level = round(raw / 255 * 100)
            cst, craw = self._query(dev, tid, 0x07, 0x84)
            charging = bool(craw) if cst == STATUS_OK and craw is not None else False
            self._read_dpi(dev, path, tid)
            return status, level, charging
        finally:
            try:
                dev.close()
            except Exception:
                pass

    # ---- high level ------------------------------------------------------
    def poll(self) -> List[DeviceStatus]:
        """One icon for a mouse on its cable while its receiver stays plugged in.

        On the cable the mouse is a second PID with the same name in KNOWN, and the
        receiver then answers "not responding". Its greyed copy of the last level stayed
        next to the live icon of the cable for ASLEEP_KEEP. A greyed icon is dropped when
        another PID of the same model answers in the same poll. Two live icons stay: that
        is two mice of the same model, one on the receiver and one on the cable."""
        out = self._poll_devices()
        models = {name for name, _tid in self._known.values()}
        live: Dict[str, set] = {}       # model name -> PIDs that answered (key "razer:<pid>:...")
        for s in out:
            if s.online and s.name in models:
                live.setdefault(s.name, set()).add(s.key.split(":")[1])
        return [s for s in out
                if s.online or not live.get(s.name, set()) - {s.key.split(":")[1]}]

    def _poll_devices(self) -> List[DeviceStatus]:
        self._diag = []
        self._public_errors = set()
        try:
            infos = hidlist.enumerate(RAZER_VID)
        except Exception as e:  # pragma: no cover
            log.warning("hid.enumerate(razer): %s", e)
            self._public_errors.add("enumerate_failed")
            return []

        # group interfaces by device (PID + serial / location)
        groups: Dict[Tuple[int, str], List[dict]] = {}
        hyperspeed_infos = []
        viper_infos = []
        for d in infos:
            pid = d["product_id"]
            if self.device_pack.is_disabled(self.name, RAZER_VID, pid):
                self._diag.append(f"  disabled: razer {RAZER_VID:04x}:{pid:04x}")
                continue
            if pid == blackshark_hyperspeed.PID:
                hyperspeed_infos.append(d)
                continue
            if pid in razer_v4.PIDS:
                viper_infos.append(d)
                continue
            pack_entry = self._pack_entries.get(pid)
            if pack_entry and not pack_entry.match.matches(dict(d, vendor_id=RAZER_VID)):
                continue
            serial = d.get("serial_number") or ""
            groups.setdefault((pid, serial), []).append(d)

        # Windows re-parents some collections: Razer Synapse creates RZVIRTUAL
        # children for a mouse's extra buttons, and hidapi reports those with an
        # empty serial and no strings. Grouped by serial they look like a second,
        # identical device that can never answer, so every poll logs a "poll
        # failed" line for a device that is fine (and if one ever did answer, the
        # same mouse would get a second icon). Attach them to the real device of
        # the same PID when there is exactly one.
        named: Dict[int, List[str]] = {}
        for pid, serial in groups:
            if serial:
                named.setdefault(pid, []).append(serial)
        for (pid, serial) in list(groups):
            if not serial and len(named.get(pid, [])) == 1:
                groups[(pid, named[pid][0])] += groups.pop((pid, serial))

        self._dpi_infos = {d["path"]: d for ifaces in groups.values() for d in ifaces}
        for gkey in set(self._foreign) | set(self._rediscovery):
            if gkey not in groups:
                self._clear_foreign(gkey)

        out = blackshark_hyperspeed.poll_devices(hyperspeed_infos)
        out.extend(razer_v4.poll_devices(viper_infos, build_request))
        for (pid, serial), ifaces in groups.items():
            pack_entry = self._pack_entries.get(pid)
            name, pref_tid = self._known.get(pid, (None, None))
            if not name:
                name = (ifaces[0].get("product_string") or f"Razer {pid:04x}").strip()
            key = f"razer:{pid:04x}:{serial}"
            kind = pack_entry.kind if pack_entry else "keyboard" if pid in KEYBOARD_INTERFACE else ""
            diag_from = len(self._diag)
            self._diag.append(f"[Razer] {name} pid={pid:04x}, interfaces: {len(ifaces)}")
            if pid == blackshark2020.CABLE_PID:
                self._diag.append("  skipped: 2020 charging cable; battery is read from receiver 0528")
                continue
            if not pack_entry and not maybe_wireless(pid, name):
                self._diag.append("  skipped: not a known wireless device")
                continue
            if pid == 0x053A:
                # The Barracuda Pro's receiver does not answer the mouse request this
                # provider sends; it speaks the headset "PA" protocol instead, which
                # providers/barracuda.py reads.
                self._diag.append("  skipped: read by the barracuda provider (own PA protocol)")
                continue
            is_headset = (pack_entry.kind == "headset" if pack_entry
                          else pid in blackshark.PA_PIDS or "blackshark" in name.lower())
            self._read_errors = set()
            self._dpi_result = None
            if pack_entry:
                # Pack metadata cannot activate PA remote mode or another TID.
                st = self._poll_group((pid, serial), ifaces, pref_tid, profile_only=True)
            elif pid == blackshark2020.PID:
                # Deliberately no generic/PA fallback: the 0528 hardware rejects
                # their reads/writes. A missing fresh feature reply means no reading.
                st = self._poll_2020(ifaces)
            elif pid in blackshark.PA_PIDS:
                # 2023 headset: its own protocol first
                st = self._poll_pa((pid, serial), ifaces)
                if st is None:
                    st = self._poll_group((pid, serial), ifaces, pref_tid)
            else:
                st = self._poll_group((pid, serial), ifaces, pref_tid)
                if is_headset and (st is None or st[0] != STATUS_OK):
                    pa = self._poll_pa((pid, serial), ifaces)
                    if pa is not None and (st is None or pa[0] == STATUS_OK):
                        st = pa
            if st is None or st[0] != STATUS_OK:
                self._public_errors.update(self._read_errors or {"no_reply"})
                # failed poll: the details go to the log once, when the device stops
                # answering (or the reason changes), not on every poll while it is off
                reason = ("no reply" if st is None else
                          "reply command mismatch; cause unconfirmed" if st[0] == STATUS_FOREIGN else
                          f"status {st[0]:02x}")
                if self._failing.get(key) != reason:
                    self._failing[key] = reason
                    log.info("[Razer] %s: poll failed (%s); not logged again until it changes",
                             name, reason)
                    for line in self._diag[diag_from:]:
                        log.info("%s", line)
                # STATUS_TIMEOUT covers a switched-off device and one that fell asleep
                # a few seconds after the last movement, and the receiver cannot tell
                # them apart. Keep the last value (greyed out, the translucent icon
                # the README describes) for a while and let the icon go away after
                # that; it comes back as soon as the device answers again. The same
                # thing the WLmouse provider does for a silent mouse.
                #
                # Headsets are left alone: the README says a switched-off headset
                # disappears from the tray, and no Razer headset is on hand to test a
                # sleeping one, so they keep exactly the behaviour they had before.
                last = self._last.get(key)
                if not is_headset and last and time.time() - last[2] < ASLEEP_KEEP:
                    out.append(DeviceStatus(key, name, last[0], last[1], False, "razer", kind=kind))
                continue
            elif self._failing.pop(key, None) is not None:
                log.info("[Razer] %s: answering again", name)
            status, level, charging = st
            if status == STATUS_OK:
                if level is not None:
                    self._last[key] = (level, bool(charging), time.time())
                approx = ""
                if pid == blackshark2020.PID and level is None:
                    approx = "battery level unknown"
                    if charging:
                        approx += ", charging"
                if pid == blackshark2020.PID and charging is None:
                    approx = (approx or f"{level}%") + ", charging state unknown"
                dpi = dpi_target = None
                res = self._dpi_result
                if (res is not None and not is_headset and not pack_entry
                        and any(d["path"] == res[0] for d in ifaces)):
                    dpi, dpi_target = res[1], res[2]
                out.append(DeviceStatus(key, name, level, bool(charging), True, "razer",
                                        approx=approx, kind=kind, dpi=dpi, dpi_target=dpi_target))
        return [replace(status, model_source="pack", pack_version=self.device_pack.version,
                        verification_level=entry.level, model_reference=entry.source,
                        dpi=None, dpi_target=None)
                if (int(status.key.split(":")[1], 16) != blackshark_hyperspeed.PID
                    and (entry := self._pack_entries.get(int(status.key.split(":")[1], 16)))) else status
                for status in out]

    def _clear_foreign(self, gkey):
        self._foreign.pop(gkey, None)
        self._rediscovery.pop(gkey, None)

    def _poll_group(self, gkey, ifaces, pref_tid, profile_only=False):
        tids = [pref_tid] if pref_tid else []
        if not profile_only:
            tids += [t for t in TRANSACTION_IDS if t not in tids]
        cached = self._cache.get(gkey)
        if cached and (cached.path not in {d["path"] for d in ifaces} or cached.tid not in tids):
            self._cache.pop(gkey, None)
            self._clear_foreign(gkey)
            cached = None
        if cached:
            status, level, charging = self._read(cached.path, cached.tid)
            if status in (STATUS_OK, STATUS_TIMEOUT):
                self._clear_foreign(gkey)
                return status, level, charging
            self._cache.pop(gkey, None)
            if status == STATUS_FOREIGN:
                self._foreign[gkey] = self._foreign.get(gkey, 0) + 1
                if self._foreign[gkey] < 3:
                    self._cache[gkey] = cached
                    return status, None, None
                # Three consecutive foreign reads only throttle recovery; they do
                # not prove this interface works, or that a device was cancelled.
                self._rediscovery[gkey] = (cached.path, cached.tid)
            else:
                self._clear_foreign(gkey)

        # Probe order: vendor / main collection interfaces first, then the rest.
        # Collections Windows re-parented (interface number -1) go last.
        want = KEYBOARD_INTERFACE.get(gkey[0])

        def rank(d):
            up = d.get("usage_page", 0)
            iface = d.get("interface_number", 0)
            if iface is None or iface < 0:
                iface = 99
            return (0 if iface == want else 1, 0 if up in (0x0001, 0xFF00) else 1, iface)

        ifaces = sorted(ifaces, key=rank)
        if gkey in self._rediscovery:
            return self._rediscover_group(gkey, ifaces, tids)
        now = time.time()
        timeout_hit = None
        attempted = False
        for d in ifaces:
            path = d["path"]
            if self._dead.get(path, 0) > now:
                continue
            attempted = True
            self._diag.append(
                f"  iface={d.get('interface_number')} usage={d.get('usage_page', 0):04x}:"
                f"{d.get('usage', 0):04x}")
            answered = False
            for tid in tids:
                status, level, charging = self._read(path, tid)
                if status is None:
                    break          # the interface rejects feature reports, next one
                answered = True
                if status == STATUS_OK:
                    self._cache[gkey] = _Cand(path, tid)
                    self._clear_foreign(gkey)
                    return status, level, charging
                if status == STATUS_TIMEOUT:
                    timeout_hit = (status, None, None)
                    self._cache[gkey] = _Cand(path, tid)
                    break
                if status == STATUS_FOREIGN:
                    # Keep a transient mismatch briefly, without treating it as
                    # evidence of a working battery interface/transaction id.
                    self._cache[gkey] = _Cand(path, tid)
                    self._foreign[gkey] = 1
                    return status, None, None
            if not answered:
                self._dead[path] = now + 300   # leave this interface alone for 5 minutes
        if not attempted:
            self._read_errors.add("interfaces_cooling_down" if ifaces else "no_usable_interface")
        return timeout_hit

    def _rediscover_group(self, gkey, ifaces, tids):
        # Rebuild from today's allowed inventory; a cursor is an identity, not
        # an index into yesterday's ranking. Wrap even with just one candidate.
        candidates = [(d["path"], tid) for d in ifaces for tid in tids]
        if not candidates:
            self._clear_foreign(gkey)
            self._read_errors.add("no_usable_interface")
            return None
        cursor = self._rediscovery[gkey]
        start = (candidates.index(cursor) + 1) % len(candidates) if cursor in candidates else 0
        now = time.time()
        skipped = set()
        reads = 0
        foreign_hit = None
        timeout_hit = None
        for offset in range(len(candidates)):
            path, tid = candidates[(start + offset) % len(candidates)]
            if path in skipped or self._dead.get(path, 0) > now:
                continue
            self._rediscovery[gkey] = (path, tid)
            status, level, charging = self._read(path, tid)
            reads += 1
            if status == STATUS_OK:
                self._cache[gkey] = _Cand(path, tid)
                self._clear_foreign(gkey)
                return status, level, charging
            if status is None or status == STATUS_TIMEOUT:
                # As in initial discovery, reject the remaining TIDs on an
                # unusable/asleep interface. Reaching a later TID already means
                # this interface answered during the current walk.
                if status is None and tid == tids[0]:
                    self._dead[path] = now + 300
                if status == STATUS_TIMEOUT:
                    timeout_hit = (status, None, None)
                    self._cache[gkey] = _Cand(path, tid)
                skipped.add(path)
                self._rediscovery[gkey] = (path, tids[-1])
            elif status == STATUS_FOREIGN:
                foreign_hit = (status, None, None)
            if reads >= 2:
                break
        if timeout_hit is not None:
            self._clear_foreign(gkey)
        if not reads:
            self._read_errors.add("interfaces_cooling_down")
        return timeout_hit or foreign_hit

    def _poll_2020(self, ifaces):
        """Only the 0xFF00 collection accepts the 2020 feature report."""
        offline = None
        for d in ifaces:
            if not blackshark2020.is_candidate(d):
                continue
            self._diag.append(
                f"  [2020] iface={d.get('interface_number')} usage=ff00:0001")
            res, level, charging = blackshark2020.read_battery(d["path"], self._diag)
            if res == "ok":
                return STATUS_OK, level, charging
            if res == "offline":
                offline = (STATUS_TIMEOUT, None, None)
        return offline

    def _poll_pa(self, gkey, ifaces):
        """BlackShark V2 Pro 2023: the "PA" protocol over output/input reports."""
        cache_key = ("pa",) + tuple(gkey)
        cached = self._cache.get(cache_key)
        cands = [d for d in ifaces if blackshark.is_candidate(d)] or list(ifaces)
        if cached:
            cands.sort(key=lambda d: 0 if d["path"] == cached.path else 1)
        offline = None
        asleep = None
        for d in cands:
            self._diag.append(
                f"  [PA] iface={d.get('interface_number')} usage={d.get('usage_page', 0):04x}:"
                f"{d.get('usage', 0):04x}")
            res, level, charging = blackshark.read_battery(d["path"], self._diag)
            if res == "ok":
                self._cache[cache_key] = _Cand(d["path"], 0)
                return STATUS_OK, level, charging
            if res == "offline":
                # The interface accepted a command and the headset did not answer: this is the
                # collection that speaks the protocol, with the headset switched off. Remember
                # it, so an off headset costs one probe per poll instead of one per collection
                # (measured: 2 probes -> 1 with two vendor collections, ~0.7 s each), and stop
                # walking the list.
                self._cache[cache_key] = _Cand(d["path"], 0)
                if offline is None:
                    offline = (STATUS_TIMEOUT, None, None)
                break
            if res == "nowake" and asleep is None:
                # The interface opened but never accepted a command - which is also what a
                # wrong collection looks like, so it must NOT be cached. If a wrong path were
                # cached, the sort above would put it first and its "offline" would break the
                # loop every time, so the collection that actually works would never be
                # reached again and a headset switched on later would never be read.
                # Keep it only as the fallback answer, so a receiver that is asleep still
                # shows "no link" (STATUS_TIMEOUT) instead of losing its icon.
                asleep = (STATUS_TIMEOUT, None, None)
        return offline or asleep

    set_device_dpi = staticmethod(set_device_dpi)

    def diagnostics(self) -> List[str]:
        return list(self._diag)
