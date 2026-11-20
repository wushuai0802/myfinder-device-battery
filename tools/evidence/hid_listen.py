"""Record unsolicited input from the four INFI75 vendor collections."""

from __future__ import annotations

import argparse
from contextlib import ExitStack
import ctypes as c
from dataclasses import dataclass
import json
import math
import os
from pathlib import Path
import re
import sys
import time


VID, PID = 0x05AC, 0x024F
READ_TIMEOUT_MS = 50


@dataclass(frozen=True)
class Collection:
    connection: str
    product: str
    page: int
    usage: int

    @property
    def label(self):
        return f"{self.page:04X}:{self.usage:04X}"


COLLECTIONS = (
    Collection("2.4G", "2.4G Wireless Keyboard", 0xFFB5, 0x0001),
    Collection("wired", "INFI75-1.1", 0xFFFF, 0x0001),
    Collection("wired", "INFI75-1.1", 0xFF13, 0x0001),
    Collection("wired", "INFI75-1.1", 0xFF68, 0x0061),
)
ALLOWLIST = {(item.product, item.page, item.usage): item for item in COLLECTIONS}


class ListenError(ValueError):
    """A message for the operator that contains no device identifiers."""


def select_collections(api):
    selected = {}
    paths = set()
    for entry in api.enumerate(VID, PID):
        if (entry.get("vendor_id"), entry.get("product_id")) != (VID, PID):
            continue
        product = entry.get("product_string")
        if not isinstance(product, str):
            continue
        target = ALLOWLIST.get((product.strip(), entry.get("usage_page"), entry.get("usage")))
        if target is None:
            continue
        path = entry.get("path")
        if target in selected or (path and path in paths):
            raise ListenError("身份不唯一，未监听")
        selected[target] = entry
        if path:
            paths.add(path)
    return selected


class Attributes(c.Structure):
    _fields_ = [
        ("size", c.c_uint32), ("vendor", c.c_uint16),
        ("product", c.c_uint16), ("version", c.c_uint16),
    ]


class Caps(c.Structure):
    _fields_ = [
        ("usage", c.c_uint16), ("page", c.c_uint16),
        ("input_length", c.c_uint16), ("remaining_caps", c.c_uint16 * 29),
    ]


def windows_input_length(path, target):
    """Confirm the collection and input length with a zero-access handle."""
    kernel = c.WinDLL("kernel32")
    hid = c.WinDLL("hid")
    handle_type = c.c_void_p
    dword = c.c_uint32

    def bind(api, name, result, *parameters):
        function = getattr(api, name)
        function.restype = result
        function.argtypes = parameters

    bind(kernel, "CreateFileW", handle_type, c.c_wchar_p, dword, dword,
         handle_type, dword, dword, handle_type)
    bind(kernel, "CloseHandle", c.c_int, handle_type)
    bind(hid, "HidD_GetAttributes", c.c_ubyte, handle_type, c.POINTER(Attributes))
    bind(hid, "HidD_GetProductString", c.c_ubyte, handle_type, handle_type, dword)
    bind(hid, "HidD_GetPreparsedData", c.c_ubyte, handle_type, c.POINTER(handle_type))
    bind(hid, "HidD_FreePreparsedData", c.c_ubyte, handle_type)
    bind(hid, "HidP_GetCaps", c.c_int32, handle_type, c.POINTER(Caps))

    handle = kernel.CreateFileW(os.fsdecode(path), 0, 3, None, 3, 0, None)
    if handle in (None, handle_type(-1).value):
        return None
    try:
        attrs = Attributes(size=c.sizeof(Attributes))
        product = c.create_unicode_buffer(128)
        if not hid.HidD_GetAttributes(handle, c.byref(attrs)) or (attrs.vendor, attrs.product) != (VID, PID):
            return None
        if not hid.HidD_GetProductString(handle, product, c.sizeof(product)) or product.value.strip() != target.product:
            return None
        preparsed = handle_type()
        if not hid.HidD_GetPreparsedData(handle, c.byref(preparsed)):
            return None
        try:
            caps = Caps()
            if (hid.HidP_GetCaps(preparsed, c.byref(caps)) == 0x00110000
                    and (caps.page, caps.usage) == (target.page, target.usage)):
                return caps.input_length
        finally:
            hid.HidD_FreePreparsedData(preparsed)
    finally:
        kernel.CloseHandle(handle)
    return None


def private_values(devices):
    values = set()
    for entry in devices:
        path = os.fsdecode(entry["path"])
        values.add(path)
        parts = path.split("#")
        if len(parts) > 3 and parts[2]:
            values.add(parts[2])
        for key in ("serial_number", "instance_id"):
            if entry.get(key):
                values.add(os.fsdecode(entry[key]))
    return sorted(values, key=len, reverse=True)


def emit(output, entry):
    print(json.dumps(entry, ensure_ascii=False), file=output, flush=True)


def record(api, output_path, seconds, poll_events, *, input_length, clock=time.monotonic):
    selected = select_collections(api)
    ready = {}
    for target, entry in selected.items():
        path = entry.get("path")
        length = input_length(path, target) if path else None
        if type(length) is int and 0 < length <= 65535:
            ready[target] = (entry, length)
        else:
            print(f"{target.connection} {target.label} 的输入长度无法确认", file=sys.stderr)
    if not ready:
        raise ListenError("未找到输入长度可确认的 INFI75 厂商集合。")

    counts = {target: 0 for target in ready}
    secrets = private_values(entry for entry, _length in ready.values())
    try:
        with output_path.open("x", encoding="utf-8", newline="\n") as output, ExitStack() as cleanup:
            devices = {}
            for target, (entry, length) in ready.items():
                device = api.device()
                cleanup.callback(device.close)
                device.open_path(entry["path"])
                devices[target] = (device, length)

            started = clock()
            deadline = started + seconds
            while clock() < deadline:
                for event in poll_events():
                    label = event.strip()
                    for secret in secrets:
                        label = re.sub(re.escape(secret), "[redacted]", label, flags=re.IGNORECASE)
                    if label:
                        emit(output, {"t": round(clock() - started, 3), "event": label})
                for target, (device, length) in devices.items():
                    remaining = deadline - clock()
                    if remaining <= 0:
                        break
                    timeout = min(READ_TIMEOUT_MS, max(1, int(remaining * 1000)))
                    frame = device.read(length, timeout)
                    if frame:
                        emit(output, {
                            "t": round(clock() - started, 3), "conn": target.connection,
                            "usage": target.label, "hex": bytes(frame).hex(),
                        })
                        counts[target] += 1
    except KeyboardInterrupt:
        pass
    return counts


class ConsoleEvents:
    def __init__(self, console):
        self.console = console
        self.pending = []
        self.skip_extended_key = False

    def __call__(self):
        labels = []
        for _ in range(256):
            if not self.console.kbhit():
                break
            character = self.console.getwch()
            if self.skip_extended_key:
                self.skip_extended_key = False
            elif character in ("\x00", "\xe0"):
                self.skip_extended_key = True
            elif character in ("\r", "\n"):
                print(flush=True)
                labels.append("".join(self.pending))
                self.pending.clear()
            elif character == "\b":
                if self.pending:
                    self.pending.pop()
                    print("\b \b", end="", flush=True)
            elif character.isprintable():
                self.pending.append(character)
                print(character, end="", flush=True)
        return labels


def minutes(value):
    try:
        duration = float(value)
    except ValueError as error:
        raise argparse.ArgumentTypeError("请输入分钟数。") from error
    if not math.isfinite(duration) or not 0 < duration <= 30:
        raise argparse.ArgumentTypeError("监听时间须大于 0 分钟且最多 30 分钟。")
    return duration


def output_path(path, local_app_data):
    resolved = path.resolve()
    if resolved.is_relative_to((local_app_data / "MyFinderDeviceBattery").resolve()):
        raise ListenError("请将输出文件放在 MyFinder 程序数据目录之外。")
    return resolved


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--minutes", type=minutes, default=15)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args(argv)
    if os.name != "nt":
        parser.error("此工具需要 Windows HID 接口。")
    try:
        import hid
        import msvcrt
    except ImportError:
        parser.exit(1, "请用包含 hidapi 的项目 Python 运行时启动。\n")
    try:
        destination = output_path(args.out, Path(os.environ["LOCALAPPDATA"]))
        print("输入事件标签并按回车。按 Ctrl+C 可提前结束。")
        counts = record(hid, destination, args.minutes * 60, ConsoleEvents(msvcrt),
                        input_length=windows_input_length)
    except ListenError as error:
        parser.exit(1, str(error) + "\n")
    except FileExistsError:
        parser.exit(1, "输出文件已存在，请为这轮采集使用新文件名。\n")
    except (OSError, RuntimeError):
        parser.exit(1, "监听未完成，请检查设备连接和输出目录。\n")
    except KeyboardInterrupt:
        parser.exit(130, "监听已取消。\n")
    for target, count in counts.items():
        print(f"{target.connection} {target.label} 收到 {count} 帧")


if __name__ == "__main__":
    main()
