"""Running-process check for families that step aside for their vendor software.

Windows only (Toolhelp32 snapshot, no extra privileges, names only). Elsewhere,
and on any failure, reports "not running" so the caller's own gates still apply.
The result is cached briefly; a poll must not walk the process list per device.
"""
from __future__ import annotations

import ctypes as c
import sys
import time

_CACHE_SECONDS = 10.0
_cache = (0.0, frozenset())


class _Entry(c.Structure):
    _fields_ = [("size", c.c_uint32), ("usage", c.c_uint32), ("pid", c.c_uint32),
                ("heap", c.c_void_p), ("module", c.c_uint32), ("threads", c.c_uint32),
                ("parent", c.c_uint32), ("priority", c.c_long), ("flags", c.c_uint32),
                ("exe", c.c_wchar * 260)]


def _names():
    if sys.platform != "win32":
        return frozenset()
    kernel = c.WinDLL("kernel32", use_last_error=True)
    kernel.CreateToolhelp32Snapshot.restype = c.c_void_p
    kernel.CreateToolhelp32Snapshot.argtypes = (c.c_uint32, c.c_uint32)
    kernel.Process32FirstW.argtypes = kernel.Process32NextW.argtypes = (c.c_void_p, c.POINTER(_Entry))
    kernel.CloseHandle.argtypes = (c.c_void_p,)
    snapshot = kernel.CreateToolhelp32Snapshot(0x2, 0)  # TH32CS_SNAPPROCESS
    if not snapshot or snapshot == c.c_void_p(-1).value:
        return frozenset()
    names = set()
    try:
        entry = _Entry()
        entry.size = c.sizeof(_Entry)
        ok = kernel.Process32FirstW(snapshot, c.byref(entry))
        while ok and len(names) < 4096:
            names.add(entry.exe.lower())
            ok = kernel.Process32NextW(snapshot, c.byref(entry))
    finally:
        kernel.CloseHandle(snapshot)
    return frozenset(names)


def running(names, clock=time.monotonic) -> bool:
    global _cache
    if not names:
        return False
    now = clock()
    if now - _cache[0] > _CACHE_SECONDS:
        try:
            _cache = (now, _names())
        except (OSError, AttributeError):
            _cache = (now, frozenset())
    return any(name in _cache[1] for name in names)
