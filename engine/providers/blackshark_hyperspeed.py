"""Dedicated reader for the Razer BlackShark V2 HyperSpeed receiver."""
from __future__ import annotations

import ctypes
import hashlib
import os
import re
import threading
import time
from typing import Callable, Iterable, Mapping, Optional

import hid

from .base import DeviceStatus
from .razer_profiles import HYPERSPEED_RX_PID as PID, HYPERSPEED_RX_NAME as NAME

VID = 0x1532
USAGE_PAGE = 0xFF14
USAGE = 0x0001
INTERFACE = 3
REPORT_ID = 0x02
REPORT_LENGTH = 64
READ_LIMIT = 8
READ_WINDOW = 0.6

_sequence_lock = threading.Lock()
_sequence = 0


def _next_sequence() -> int:
    global _sequence
    with _sequence_lock:
        value = _sequence & 0x1F
        _sequence = (value + 1) & 0x1F
        return value


def _xor(data: Iterable[int]) -> int:
    value = 0
    for item in data:
        value ^= int(item) & 0xFF
    return value


def build_request(sequence: int) -> bytes:
    frame = bytearray(REPORT_LENGTH)
    frame[0] = REPORT_ID
    frame[2] = 0x60 | (int(sequence) & 0x1F)
    frame[6] = 0x04
    frame[9] = 0x80
    frame[10] = 0x21
    frame[62] = _xor(frame[:62])
    return bytes(frame)


def parse_reply(reply: Iterable[int], sequence: int) -> Optional[int]:
    try:
        frame = bytes(reply)
    except (TypeError, ValueError):
        return None
    if len(frame) != REPORT_LENGTH:
        return None
    if frame[0] != REPORT_ID or frame[1] != 0x02:
        return None
    if (frame[2] & 0x1F) != (int(sequence) & 0x1F):
        return None
    if frame[8:12] != bytes((0x80, 0x80, 0x21, 0x01)):
        return None
    count = frame[12]
    if not 1 <= count <= 48 or frame[6] != 4 + count:
        return None
    if frame[62] not in (_xor(frame[1:62]), _xor(frame[:62])):
        return None
    percent = frame[13]
    if percent > 100:
        return None
    return percent


def matches_collection(info: Mapping[str, object]) -> bool:
    return (
        isinstance(info, Mapping)
        and type(info.get("vendor_id")) is int
        and info["vendor_id"] == VID
        and type(info.get("product_id")) is int
        and info["product_id"] == PID
        and info.get("product_string") == NAME
        and type(info.get("interface_number")) is int
        and info["interface_number"] == INTERFACE
        and type(info.get("usage_page")) is int
        and info["usage_page"] == USAGE_PAGE
        and type(info.get("usage")) is int
        and info["usage"] == USAGE
    )


def physical_id(info: Mapping[str, object]) -> Optional[str]:
    value = info.get("physical_id")
    if isinstance(value, str) and re.fullmatch(
            r"pnp:usb\\vid_1532&pid_0565(?:&rev_[0-9a-f]{4})?\\[^\\\s]+", value, re.I):
        return value.lower()
    return None


def usable(info: Mapping[str, object]) -> bool:
    if info.get("report_descriptor_error"):
        return False
    for direction in ("input", "output"):
        sizes = info.get(direction + "_report_sizes")
        if (info.get(direction + "_report_length") != REPORT_LENGTH
                or info.get(direction + "_report_proof") not in ("caps", "descriptor")
                or not isinstance(sizes, dict) or sizes.get("2") != REPORT_LENGTH):
            return False
    return True


def device_path(info: Mapping[str, object]) -> Optional[bytes]:
    path = info.get("path")
    if isinstance(path, bytes) and re.fullmatch(
            rb"\\\\\?\\hid#vid_1532&pid_0565&mi_03(?:&[a-z0-9_]+)*#[^\\\s\x00]+", path, re.I):
        return path
    return None


def _set_output_report(path: bytes, frame: bytes) -> bool:
    if os.name != "nt" or len(frame) != REPORT_LENGTH or frame != build_request(frame[2] & 0x1F):
        return False

    try:
        from ctypes import wintypes

        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        hid_dll = ctypes.WinDLL("hid", use_last_error=True)


        kernel32.CreateFileW.argtypes = (
            wintypes.LPCWSTR,
            wintypes.DWORD,
            wintypes.DWORD,
            wintypes.LPVOID,
            wintypes.DWORD,
            wintypes.DWORD,
            wintypes.HANDLE,
        )
        kernel32.CreateFileW.restype = wintypes.HANDLE
        kernel32.CloseHandle.argtypes = (wintypes.HANDLE,)
        kernel32.CloseHandle.restype = wintypes.BOOL

        handle = kernel32.CreateFileW(
            os.fsdecode(path),
            0x40000000,  # GENERIC_WRITE for the output-only second handle
            0x00000001 | 0x00000002,
            None,  # default security, non-inheritable handle
            3,
            0x00000080,
            None,
        )
        handle_value = getattr(handle, "value", handle)
        invalid = ctypes.c_void_p(-1).value
        if not handle_value or handle_value == invalid:
            return False

        try:
            hid_dll.HidD_SetOutputReport.argtypes = (
                wintypes.HANDLE,
                ctypes.POINTER(ctypes.c_ubyte),
                wintypes.ULONG,
            )
            hid_dll.HidD_SetOutputReport.restype = ctypes.c_ubyte  # HID BOOLEAN
            buffer_type = ctypes.c_ubyte * REPORT_LENGTH
            buffer = buffer_type.from_buffer_copy(frame)
            return bool(
                hid_dll.HidD_SetOutputReport(handle, buffer, REPORT_LENGTH)
            )
        finally:
            kernel32.CloseHandle(handle)
    except (OSError, TypeError, ValueError, AttributeError):
        return False


def _reader_factory():
    return hid.device()


def _read_one(
    info: Mapping[str, object],
    sequence: int,
    device_factory: Callable[[], object],
    send_output: Callable[[bytes, bytes], bool],
    clock: Callable[[], float],
) -> Optional[int]:
    path = device_path(info)
    if path is None:
        return None
    reader = None
    try:
        reader = device_factory()
        reader.open_path(path)
        request = build_request(sequence)
        if not send_output(path, request):
            return None
        deadline = clock() + READ_WINDOW
        for _ in range(READ_LIMIT):
            remaining = deadline - clock()
            if remaining <= 0:
                break
            timeout_ms = max(1, min(100, int(remaining * 1000)))
            percent = parse_reply(reader.read(REPORT_LENGTH, timeout_ms), sequence)
            if percent is not None:
                return percent
        return None
    except Exception:
        return None
    finally:
        if reader is not None:
            try:
                reader.close()
            except Exception:
                pass


def poll_devices(
    infos: Iterable[Mapping[str, object]],
    device_factory: Optional[Callable[[], object]] = None,
    send_output: Optional[Callable[[bytes, bytes], bool]] = None,
    clock: Optional[Callable[[], float]] = None,
) -> list[DeviceStatus]:
    if device_factory is None:
        device_factory = _reader_factory
    if send_output is None:
        send_output = _set_output_report
    if clock is None:
        clock = time.monotonic

    groups: dict[str, list[Mapping[str, object]]] = {}
    for info in infos:
        if not isinstance(info, Mapping):
            continue
        if info.get("vendor_id") != VID or info.get("product_id") != PID:
            continue
        parent = physical_id(info)
        if parent is None:
            if (info.get("usage_page"), info.get("usage")) == (USAGE_PAGE, USAGE):
                return []  # Cannot establish which receiver owns this second collection.
            continue
        groups.setdefault(parent, []).append(info)

    results: list[DeviceStatus] = []
    for parent, candidates in groups.items():
        collections = [
            item for item in candidates
            if item.get("usage_page") == USAGE_PAGE and item.get("usage") == USAGE
        ]
        if len(collections) != 1:
            continue
        info = collections[0]
        if not matches_collection(info) or not usable(info):
            continue

        sequence = _next_sequence()
        level = _read_one(info, sequence, device_factory, send_output, clock)
        if level is None:
            continue
        results.append(
            DeviceStatus(
                key=f"razer:{PID:04x}:" + hashlib.sha256(parent.encode("utf-8")).hexdigest()[:20],
                name=NAME,
                level=level,
                charging=False,
                online=True,
                source="razer",
                kind="headset",
                charging_known=False,
                connection_known=True,
                physical_id=parent,
                verification_level="C",
            )
        )
    return results
