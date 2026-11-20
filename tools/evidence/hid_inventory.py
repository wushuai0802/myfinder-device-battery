"""Export Windows HID interface metadata using zero-access handles."""

from __future__ import annotations

import argparse
import ctypes as c
from itertools import count
import json
import os
from pathlib import Path
import re
import tempfile
import unicodedata


WORD = c.c_uint16
BYTE = c.c_uint8
DWORD = c.c_uint32
HANDLE = c.c_void_p
INVALID_HANDLE = HANDLE(-1).value
HIDP_SUCCESS = 0x00110000
REPORT_TYPES = ("Input", "Output", "Feature")


class Guid(c.Structure):
    _fields_ = [("a", DWORD), ("b", WORD), ("c", WORD), ("d", BYTE * 8)]


HID_GUID = Guid(0x4D1E55B2, 0xF16F, 0x11CF,
                (BYTE * 8)(0x88, 0xCB, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30))


class InterfaceData(c.Structure):
    _fields_ = [("size", DWORD), ("guid", Guid), ("flags", DWORD), ("reserved", HANDLE)]


class Attributes(c.Structure):
    _fields_ = [("size", DWORD), ("vid", WORD), ("pid", WORD), ("version", WORD)]


class Caps(c.Structure):
    _fields_ = (
        [(name, WORD) for name in ("usage", "page", "input", "output", "feature")]
        + [("reserved", WORD * 17), ("link_nodes", WORD)]
        + [(kind + "_" + field, WORD) for kind in ("input", "output", "feature")
           for field in ("buttons", "values", "indices")]
    )


COMMON_CAP_FIELDS = [
    ("page", WORD), ("report_id", BYTE), ("is_alias", BYTE), ("bit_field", WORD),
    ("link", WORD), ("link_usage", WORD), ("link_page", WORD), ("is_range", BYTE),
    ("string_range", BYTE), ("designator_range", BYTE), ("absolute", BYTE),
]


class ValueCaps(c.Structure):
    _fields_ = COMMON_CAP_FIELDS + [
        ("has_null", BYTE), ("reserved", BYTE), ("bit_size", WORD), ("report_count", WORD),
        ("reserved2", WORD * 5), ("units_exponent", DWORD), ("units", DWORD),
        ("logical_min", c.c_int32), ("logical_max", c.c_int32),
        ("physical_min", c.c_int32), ("physical_max", c.c_int32), ("bounds", WORD * 8),
    ]


class ButtonCaps(c.Structure):
    _fields_ = COMMON_CAP_FIELDS + [("reserved", DWORD * 10), ("bounds", WORD * 8)]


# Match the focus of FeedbackReport.CleanField, while also removing the
# enumerated instance segment if a descriptor string repeats it.
FIELD_REDACTIONS = (
    (r"""(?:[a-z]:[\\/]|\\\\|(?:hid|usb|bthenum|bthledevice)[\\#]|(?<!\w)/(?:[^\s/]+/))[^\r\n"']*""",
     "[path]"),
    (r"""(?:\b(?:serial(?:[_ -]?number)?|s/n|sn|physical[_ -]?id|process[_ -]?id|pid|instance[_ -]?(?:id|time)|endpoint[_ -]?(?:id|name))\b|序列号|进程号)\s*[:=#：]?\s*(?:"[^"]*"|'[^']*'|[^\s]+)""",
     "[private]"),
    (r"\bdevice-[a-f0-9]{24}\b|\b[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\b|\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b|\b\d{4}-\d{2}-\d{2}T\S+",
     "[device]"),
)


def clean_field(value, instance=""):
    for pattern, replacement in FIELD_REDACTIONS:
        value = re.sub(pattern, replacement, value, flags=re.IGNORECASE)
    if instance:
        value = re.sub(re.escape(instance), "[private]", value, flags=re.IGNORECASE)
    return "".join(" " if unicodedata.category(ch) == "Cc" else ch for ch in value[:80]).strip()


def path_fields(path):
    parts = path.split("#")
    hardware = parts[1] if len(parts) > 1 else ""
    fields = {}
    for label, width in (("vid", 4), ("pid", 4), ("mi", 2), ("col", 2)):
        token = r"col_?" if label == "col" else label + "_"
        match = re.search(rf"(?:^|&){token}([0-9a-f]{{{width}}})(?:&|$)",
                          hardware, re.IGNORECASE)
        fields[label] = match[1].upper() if match else None
    return fields


def report_lengths(kind, maximum, values, buttons, complete=True):
    """Summarize the data bits exposed by the Windows capabilities."""
    bits_by_id = {}
    for field in values:
        bits = bits_by_id.setdefault(field.report_id, 0)
        if not field.is_alias:
            bits_by_id[field.report_id] = bits + field.bit_size * field.report_count
    for field in buttons:
        bits = bits_by_id.setdefault(field.report_id, 0)
        # Variable buttons contribute one bit per data index. Array caps give
        # possible selectors without their element width or number of slots.
        if not field.is_alias and field.bit_field & 2:
            indices = field.bounds[7] - field.bounds[6] + 1 if field.is_range else 1
            bits_by_id[field.report_id] = bits + max(0, indices)

    reports = []
    for report_id, data_bits in sorted(bits_by_id.items()):
        minimum = (data_bits + 7) // 8 + 1 if data_bits else None
        length = None
        basis = "data_bits_only"
        if minimum is not None and minimum > maximum:
            minimum = None
            basis = "inconsistent_caps"
        elif complete and maximum and (minimum == maximum or len(bits_by_id) == 1):
            length = maximum
            basis = "caps_maximum" if minimum == maximum else "single_id_caps_maximum"
        elif not complete:
            basis = "incomplete_caps"
        reports.append({
            "type": kind, "reportId": f"{report_id:02X}",
            "reportByteLength": length, "minimumReportByteLength": minimum,
            "lengthBasis": basis,
        })
    return reports


class WindowsInventory:
    def __init__(self):
        self.setup = c.WinDLL("setupapi", use_last_error=True)
        self.kernel = c.WinDLL("kernel32", use_last_error=True)
        self.hid = c.WinDLL("hid", use_last_error=True)

        def bind(library, name, returns, *args):
            function = getattr(library, name)
            function.restype = returns
            function.argtypes = args

        bind(self.setup, "SetupDiGetClassDevsW", HANDLE, c.POINTER(Guid), c.c_wchar_p,
             HANDLE, DWORD)
        bind(self.setup, "SetupDiEnumDeviceInterfaces", c.c_int, HANDLE, HANDLE,
             c.POINTER(Guid), DWORD, c.POINTER(InterfaceData))
        bind(self.setup, "SetupDiGetDeviceInterfaceDetailW", c.c_int, HANDLE,
             c.POINTER(InterfaceData), HANDLE, DWORD, c.POINTER(DWORD), HANDLE)
        bind(self.setup, "SetupDiDestroyDeviceInfoList", c.c_int, HANDLE)
        bind(self.kernel, "CreateFileW", HANDLE, c.c_wchar_p, DWORD, DWORD, HANDLE,
             DWORD, DWORD, HANDLE)
        bind(self.kernel, "CloseHandle", c.c_int, HANDLE)
        bind(self.hid, "HidD_GetAttributes", BYTE, HANDLE, c.POINTER(Attributes))
        bind(self.hid, "HidD_GetProductString", BYTE, HANDLE, HANDLE, DWORD)
        bind(self.hid, "HidD_GetManufacturerString", BYTE, HANDLE, HANDLE, DWORD)
        bind(self.hid, "HidD_GetPreparsedData", BYTE, HANDLE, c.POINTER(HANDLE))
        bind(self.hid, "HidD_FreePreparsedData", BYTE, HANDLE)
        bind(self.hid, "HidP_GetCaps", c.c_int32, HANDLE, c.POINTER(Caps))
        bind(self.hid, "HidP_GetValueCaps", c.c_int32, c.c_int, c.POINTER(ValueCaps),
             c.POINTER(WORD), HANDLE)
        bind(self.hid, "HidP_GetButtonCaps", c.c_int32, c.c_int, c.POINTER(ButtonCaps),
             c.POINTER(WORD), HANDLE)

    def paths(self):
        flags = 0x02 | 0x10  # Present devices, device-interface mode.
        devices = self.setup.SetupDiGetClassDevsW(c.byref(HID_GUID), None, None, flags)
        if devices in (None, INVALID_HANDLE):
            raise c.WinError(c.get_last_error())
        try:
            for index in count():
                info = InterfaceData(size=c.sizeof(InterfaceData))
                if not self.setup.SetupDiEnumDeviceInterfaces(
                    devices, None, c.byref(HID_GUID), index, c.byref(info)
                ):
                    error = c.get_last_error()
                    if error == 259:  # ERROR_NO_MORE_ITEMS
                        break
                    raise c.WinError(error)
                size = DWORD()
                detail = self.setup.SetupDiGetDeviceInterfaceDetailW
                detail(devices, c.byref(info), None, 0, c.byref(size), None)
                if size.value < 8:
                    raise OSError("Windows did not return an interface path size.")
                buffer = c.create_string_buffer(size.value)
                c.cast(buffer, c.POINTER(DWORD))[0] = 8 if c.sizeof(HANDLE) == 8 else 6
                if not detail(devices, c.byref(info), buffer, size.value, None, None):
                    raise c.WinError(c.get_last_error())
                path = c.wstring_at(c.addressof(buffer) + c.sizeof(DWORD))
                yield path
        finally:
            self.setup.SetupDiDestroyDeviceInfoList(devices)

    @staticmethod
    def _read_caps(function, value_type, report_type, expected, data):
        if not expected:
            return []
        values = (value_type * expected)()
        actual = WORD(expected)
        status = function(report_type, values, c.byref(actual), data)
        if status != HIDP_SUCCESS or actual.value != expected:
            return None
        return list(values)

    def describe(self, path, fields):
        row = {
            **fields, "product": None, "manufacturer": None, "version": None,
            "usagePage": None, "usage": None, "reports": [], "errors": [],
            **{kind + "ReportByteLength": None for kind in REPORT_TYPES},
        }
        errors = row["errors"]
        # Desired access 0, shared read/write, open existing.
        handle = self.kernel.CreateFileW(path, 0, 3, None, 3, 0, None)
        if handle in (None, INVALID_HANDLE):
            errors.append("open_failed")
            return row
        try:
            attrs = Attributes(size=c.sizeof(Attributes))
            if self.hid.HidD_GetAttributes(handle, c.byref(attrs)):
                row["version"] = f"{attrs.version:04X}"
                if ((fields["vid"] and fields["vid"] != f"{attrs.vid:04X}")
                        or (fields["pid"] and fields["pid"] != f"{attrs.pid:04X}")):
                    errors.append("path_attributes_mismatch")
            else:
                errors.append("attributes_unavailable")

            parts = path.split("#")
            instance = parts[2] if len(parts) > 3 else ""
            for key, reader in (("product", self.hid.HidD_GetProductString),
                                ("manufacturer", self.hid.HidD_GetManufacturerString)):
                value = c.create_unicode_buffer(128)
                if reader(handle, value, c.sizeof(value)):
                    row[key] = clean_field(value.value, instance) or None

            data = HANDLE()
            if not self.hid.HidD_GetPreparsedData(handle, c.byref(data)):
                errors.append("preparsed_data_unavailable")
                return row
            try:
                caps = Caps()
                if self.hid.HidP_GetCaps(data, c.byref(caps)) != HIDP_SUCCESS:
                    errors.append("caps_unavailable")
                    return row
                row["usagePage"], row["usage"] = f"{caps.page:04X}", f"{caps.usage:04X}"
                for report_type, name in enumerate(REPORT_TYPES):
                    kind = name.lower()
                    maximum = getattr(caps, kind)
                    row[name + "ReportByteLength"] = maximum
                    values = self._read_caps(self.hid.HidP_GetValueCaps, ValueCaps,
                                             report_type, getattr(caps, kind + "_values"), data)
                    buttons = self._read_caps(self.hid.HidP_GetButtonCaps, ButtonCaps,
                                              report_type, getattr(caps, kind + "_buttons"), data)
                    complete = values is not None and buttons is not None
                    if not complete:
                        errors.append(kind + "_report_caps_unavailable")
                    row["reports"].extend(report_lengths(kind, maximum, values or [],
                                                         buttons or [], complete))
            finally:
                self.hid.HidD_FreePreparsedData(data)
        finally:
            self.kernel.CloseHandle(handle)
        return row


def collect_inventory(api, vid=None, pid=None):
    interfaces = []
    for path in api.paths():
        fields = path_fields(path)
        if (vid is None or fields["vid"] == vid) and (pid is None or fields["pid"] == pid):
            interfaces.append(api.describe(path, fields))
    return {"schema": 1, "filter": {"vid": vid, "pid": pid}, "interfaces": interfaces}


def hex_word(value):
    if not re.fullmatch(r"(?:0x)?[0-9a-f]{1,4}", value, re.IGNORECASE):
        raise argparse.ArgumentTypeError("expected a hexadecimal VID or PID")
    return f"{int(value, 16):04X}"


def output_target(path, data_directory):
    target = path.resolve()
    if target.is_relative_to(data_directory.resolve()):
        raise ValueError("Choose an output file outside the MyFinder application data directory.")
    return target


def save_inventory(target, inventory):
    # Use a separate file so overwriting a destination never changes a sibling
    # hard link and an interrupted scan leaves the previous export intact.
    with tempfile.TemporaryDirectory(prefix=".hid-inventory-", dir=target.parent) as directory:
        pending = Path(directory) / "inventory.txt"
        pending.write_text(json.dumps(inventory, indent=2, ensure_ascii=False) + "\n",
                           encoding="utf-8", newline="\n")
        os.replace(pending, target)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--vid", type=hex_word)
    parser.add_argument("--pid", type=hex_word)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    if os.name != "nt":
        parser.error("Windows SetupAPI is required.")
    data_directory = Path(os.environ["LOCALAPPDATA"]) / "MyFinderDeviceBattery"
    try:
        target = output_target(args.out, data_directory)
        inventory = collect_inventory(WindowsInventory(), args.vid, args.pid)
        save_inventory(target, inventory)
    except (OSError, ValueError) as error:
        parser.error(str(error))
    print(f"Exported {len(inventory['interfaces'])} HID interfaces.")


if __name__ == "__main__":
    main()
