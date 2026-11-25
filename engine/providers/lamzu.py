"""LAMZU Maya X on its 8K dongle (373E:001E) or on the cable (373E:001C).

Source: Sheroune/lamzu-battery-monitory (MIT), and its Linux port in
Quitsoon/gnome-shell-extension-lamzu-battery (MIT); anany1702/lamzu-ctl (MIT) reads
it the same way ("Aurora", the newer revision). The exchange is the one the WLmouse
and G-Wolves providers use - the same firmware family - on the vendor collection
(usage page 0xFFFF) of interface 2:

    request   feature report 0, 64 bytes: 00 00 02 02 00 83 00 ...
    reply     feature report 0:           a1 00 02 02 00 83 <charging> <battery %>

Nothing else is ever written to the mouse. The request and the reply parser are
the ones in wlmouse.py. The dongle cannot tell a sleeping mouse from one that is
switched off, so a silent mouse keeps its last level, greyed out, for a while.

Only the Maya X ids are listed: they are the ones the sources and a diagnostics
report from a real Maya X 8K dongle agree on. Other Lamzu mice use other vendor ids
(0x3554, 0x37B0) and, according to lamzu-ctl, other protocols.
"""
from __future__ import annotations

import time
from typing import Dict, List, Optional, Tuple

try:
    import hid
except ImportError:              # pragma: no cover
    hid = None

from . import hidlist
from .base import DeviceStatus, Provider, hexdump, log
from .wlmouse import (
    QUERY,
    _dpi_token,
    compx_dpi_state,
    compx_set_device_dpi,
    parse_feature,
)

LAMZU_VID = 0x373E
DONGLE = 0x001E
WIRED = 0x001C
PIDS = {DONGLE: "dongle", WIRED: "cable"}
NAME = "LAMZU Maya X"

INTERFACE = 2                    # the vendor collection is on interface 2 ...
USAGE_PAGE = 0xFFFF              # ... with usage page 0xFFFF

REPLY_TRIES = 10                 # get the reply up to 10 times, 50 ms apart
ASLEEP_KEEP = 300                # s, as in the WLmouse and G-Wolves providers

Reading = Tuple[int, bool]


def vendor_collection(ifaces: List[dict]) -> Optional[dict]:
    """The one collection the request may go to: interface 2, usage page 0xFFFF."""
    for d in ifaces:
        if d.get("interface_number") == INTERFACE and d.get("usage_page") == USAGE_PAGE:
            return d
    return None


def _dpi_eligible(info: dict) -> bool:
    return (
        isinstance(info, dict)
        and info.get("vendor_id") == LAMZU_VID
        and info.get("product_id") in PIDS
        and info.get("interface_number") == INTERFACE
        and info.get("usage_page") == USAGE_PAGE
        and isinstance(info.get("physical_id"), str)
        and bool(info["physical_id"])
        and info.get("modelSource", info.get("model_source")) != "pack"
        and info.get("feature_report_length") in (None, 65)
        and bool(info.get("path"))
    )


def _capture_dpi(info: Optional[dict]) -> Tuple[Optional[dict], Optional[dict]]:
    if not info or not _dpi_eligible(info) or hid is None:
        return None, None
    dev = None
    try:
        dev = hid.device()
        dev.open_path(info["path"])
        view, target, _ = compx_dpi_state(dev, info, "lamzu")
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
        provider_name="lamzu", eligible_fn=_dpi_eligible,
        open_device_fn=(lambda: hid.device()) if hid is not None else None,
    )


class LamzuProvider(Provider):
    name = "lamzu"

    def set_device_dpi(self, inventory, target, value, expected_dpi, target_token):
        return set_device_dpi(inventory, target, value, expected_dpi, target_token)

    def __init__(self):
        self._diag: List[str] = []
        self._last: Optional[Tuple[int, bool, float]] = None

    def _read(self, path: bytes) -> Optional[Reading]:
        dev = hid.device()
        try:
            dev.open_path(path)
        except (OSError, IOError) as e:
            self._diag.append(f"    open: {e}")
            return None
        try:
            try:
                dev.send_feature_report([0x00] + QUERY + [0x00] * (64 - len(QUERY)))
            except (OSError, ValueError) as e:
                self._diag.append(f"    send: {e}")
                return None
            for _ in range(REPLY_TRIES):
                time.sleep(0.05)
                try:
                    resp = dev.get_feature_report(0, 65)
                except (OSError, ValueError):
                    resp = None
                batt, chg = parse_feature(resp)
                if batt is not None:
                    self._diag.append(f"    reply: {hexdump(resp, 12)}")
                    return batt, bool(chg)
            self._diag.append("    no a1 reply to request 0x83 (mouse asleep or off)")
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
            infos = hidlist.enumerate(LAMZU_VID)
        except Exception as e:  # pragma: no cover
            log.warning("hid.enumerate(lamzu): %s", e)
            return []
        groups: Dict[int, List[dict]] = {}
        for d in infos:
            if d["product_id"] in PIDS:
                groups.setdefault(d["product_id"], []).append(d)
        if not groups:
            return []

        readings: List[Reading] = []
        winning_info: Optional[dict] = None
        # the cable first: it charges, and the dongle has nothing to add then
        for pid in sorted(groups, key=lambda p: p != WIRED):
            self._diag.append(f"[Lamzu] pid={pid:04x} ({PIDS[pid]}) "
                              f"'{(groups[pid][0].get('product_string') or '').strip()}'")
            d = vendor_collection(groups[pid])
            if d is None:
                self._diag.append(f"  no interface {INTERFACE} collection with usage page "
                                  f"{USAGE_PAGE:04x}: nothing sent")
                continue
            self._diag.append(f"  iface={d.get('interface_number')} usage="
                              f"{d.get('usage_page', 0):04x}:{d.get('usage', 0):04x}")
            res = self._read(d["path"])
            if res is not None:
                readings.append(res)
                winning_info = d
                break

        key = "lamzu"
        if readings:
            batt, chg = readings[0]
            self._last = (batt, chg, time.time())
            dpi, dpi_target = _capture_dpi(winning_info)
            return [DeviceStatus(key, NAME, batt, chg, True, "lamzu", kind="mouse",
                                 dpi=dpi, dpi_target=dpi_target)]
        if self._last and time.time() - self._last[2] < ASLEEP_KEEP:
            return [DeviceStatus(key, NAME, self._last[0], self._last[1], False, "lamzu",
                                 kind="mouse")]
        return []

    def diagnostics(self) -> List[str]:
        return list(self._diag)
