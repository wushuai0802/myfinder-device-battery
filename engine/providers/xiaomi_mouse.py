"""Xiaomi Mi Mouse 4 Pro on its 2.4 GHz receiver (MI_02, usage page 0xFF03).

Source: Xiaomi's own web driver, https://web.mieco.net/yuemi--manual/mouse/h/p/
(route M001). Its WebHID code opens exactly the collection usage page 0xFF03 /
usage 0x0000 - the receiver's second interface (MI_02, 33-byte in/out reports) -
and sends one output report, then waits for the mouse's input report:

    host   00 16 00 ...             32 bytes, data[1] = 0x16 (battery query)
    mouse  00 16 02 01 <level> ...  data[1] = 0x16, data[4] = level in %

The level is 0 when the mouse is off or asleep; the web driver ignores it too.
This model reports no charging state, so this provider shows none.
"""
from __future__ import annotations

import time
from typing import List, Optional

try:
    import hid
except ImportError:              # pragma: no cover
    hid = None

from . import hidlist
from .base import DeviceStatus, Provider, hexdump, log

XIAOMI_VID = 0x2717

KNOWN = {
    0x50FC: "Xiaomi Mi Mouse 4 Pro",     # the 2.4 GHz receiver, confirmed on hardware
}

RECEIVERS = {0x50FC}

USAGE_PAGE, USAGE = 0xFF03, 0x0000
CMD_BATTERY = 0x16
REPORT_LEN = 33                       # 32-byte unnumbered output/input report
TIMEOUT = 3.0                         # s
READ_MS = 100

# hidapi's write buffer starts with the report id (0 = no numbered report).
QUERY = [0x00, 0x00, CMD_BATTERY] + [0x00] * 30


def parse_battery(report) -> Optional[int]:
    """The level of a battery answer, or None.

    Windows hidapi returns an unnumbered report without a report id (command at
    byte 1); a build that prefixes the id puts the command at byte 2 instead.
    """
    r = list(report or [])
    for at in (1, 2):
        if len(r) >= at + 4 and r[at] == CMD_BATTERY:
            level = r[at + 3]
            if 0 < level <= 100:
                return level
    return None


class XiaomiMouseProvider(Provider):
    name = "xiaomi_mouse"

    def __init__(self):
        self._diag: List[str] = []

    def _read(self, path) -> Optional[int]:
        dev = hid.device()
        try:
            dev.open_path(path)
        except (OSError, IOError) as e:
            self._diag.append(f"    open: {e}")
            return None
        try:
            try:
                dev.write(QUERY)
            except (OSError, IOError, ValueError) as e:
                self._diag.append(f"    write: {e}")
                return None
            deadline = time.time() + TIMEOUT
            while time.time() < deadline:
                try:
                    report = dev.read(REPORT_LEN, READ_MS)
                except (OSError, ValueError) as e:
                    self._diag.append(f"    read: {e}")
                    return None
                level = parse_battery(report)
                if level is not None:
                    self._diag.append(f"    reply: {hexdump(report, 6)}")
                    return level
            self._diag.append("    no reply to the battery query (mouse off or asleep)")
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
            infos = hidlist.enumerate(XIAOMI_VID)
        except Exception as e:  # pragma: no cover
            log.warning("hid.enumerate(xiaomi): %s", e)
            return []
        out: List[DeviceStatus] = []
        for d in infos:
            pid = d["product_id"]
            if pid not in RECEIVERS:
                continue
            if (d.get("usage_page") or 0) != USAGE_PAGE or (d.get("usage") or 0) != USAGE:
                continue
            product = (d.get("product_string") or "").replace("Receiver", "").strip()
            name = KNOWN.get(pid) or product or "Xiaomi mouse"
            self._diag.append(f"[Xiaomi] {name} pid={pid:04x}")
            level = self._read(d["path"])
            if level is not None:
                self._diag.append(f"  -> {level}%")
                out.append(DeviceStatus(f"xiaomi:{pid:04x}", name, level, False, True,
                                        "xiaomi_mouse", kind="mouse"))
        return out

    def diagnostics(self) -> List[str]:
        return list(self._diag)
