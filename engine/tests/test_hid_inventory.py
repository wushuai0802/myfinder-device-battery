"""Offline tests for the metadata-only export and its output."""

import ctypes as c
from importlib.util import module_from_spec, spec_from_file_location
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch


TOOL = Path(__file__).resolve().parents[1] / "hid_inventory.py"
SPEC = spec_from_file_location("hid_inventory", TOOL)
inventory = module_from_spec(SPEC)
SPEC.loader.exec_module(inventory)

INSTANCE = "9&FIXTUREINSTANCE&0&0000"
PATH = r"\\?\hid#vid_05ac&pid_024f&mi_03&col01#" + INSTANCE + "#{4d1e55b2-f16f-11cf-88cb-001111000030}"
OTHER = r"\\?\hid#vid_1234&pid_abcd#DIFFERENT#{4d1e55b2-f16f-11cf-88cb-001111000030}"


def value(report_id, bits, count, *, alias=False):
    return inventory.ValueCaps(report_id=report_id, bit_size=bits, report_count=count, is_alias=alias)


def button(report_id, *, variable=False, first=0, last=0):
    return inventory.ButtonCaps(
        report_id=report_id, bit_field=2 if variable else 0,
        is_range=True, bounds=(inventory.WORD * 8)(0, 0, 0, 0, 0, 0, first, last),
    )


class InventoryTests(unittest.TestCase):
    def test_source_excludes_every_forbidden_device_api(self):
        source = TOOL.read_text(encoding="utf-8")
        for name in (
            "write(", "send_feature_report", "get_feature_report", "get_input_report",
            "HidD_SetFeature", "HidD_GetFeature", "HidD_SetOutputReport",
            "HidD_GetInputReport", "WriteFile", "DeviceIoControl",
        ):
            self.assertNotIn(name, source)
        self.assertEqual(c.sizeof(inventory.Caps), 64)
        self.assertEqual(c.sizeof(inventory.ValueCaps), 72)
        self.assertEqual(c.sizeof(inventory.ButtonCaps), 72)

    def test_setupapi_uses_present_hid_interfaces_and_releases_the_list(self):
        api = inventory.WindowsInventory.__new__(inventory.WindowsInventory)
        expected_path_bytes = (len(PATH) + 1) * c.sizeof(c.c_wchar)
        header_bytes = c.sizeof(inventory.DWORD)

        def enum(device_list, device_info, guid, index, interface):
            self.assertEqual((device_list, device_info, guid._obj, interface._obj.size),
                             (11, None, inventory.HID_GUID, c.sizeof(inventory.InterfaceData)))
            return index == 0

        def detail(device_list, _interface, buffer, size, required, _device_info):
            self.assertEqual(device_list, 11)
            if buffer is None:
                required._obj.value = header_bytes + expected_path_bytes
                return False
            self.assertEqual(size, header_bytes + expected_path_bytes)
            self.assertEqual(c.cast(buffer, c.POINTER(inventory.DWORD))[0],
                             8 if c.sizeof(inventory.HANDLE) == 8 else 6)
            c.memmove(c.addressof(buffer) + header_bytes, c.create_unicode_buffer(PATH), expected_path_bytes)
            return True

        api.setup = SimpleNamespace(
            SetupDiGetClassDevsW=Mock(return_value=11),
            SetupDiEnumDeviceInterfaces=enum,
            SetupDiGetDeviceInterfaceDetailW=detail,
            SetupDiDestroyDeviceInfoList=Mock(),
        )
        with patch.object(c, "get_last_error", return_value=259, create=True):
            self.assertEqual(list(api.paths()), [PATH])
        _, enumerator, parent, flags = api.setup.SetupDiGetClassDevsW.call_args.args
        self.assertEqual((enumerator, parent, flags), (None, None, 0x12))
        api.setup.SetupDiDestroyDeviceInfoList.assert_called_once_with(11)

    def test_mocked_caps_filter_format_access_and_redaction(self):
        api = inventory.WindowsInventory.__new__(inventory.WindowsInventory)
        api.paths = lambda: [OTHER, PATH]
        api.kernel = SimpleNamespace(CreateFileW=Mock(return_value=17), CloseHandle=Mock())
        values = {
            0: [value(0x11, 8, 6), value(0x11, 8, 6, alias=True)],
            1: [value(2, 16, 2), value(5, 8, 2)],
            2: [value(0, 8, 64)],
        }
        buttons = {0: [button(0x11, variable=True, first=10, last=17)]}

        def attrs(_handle, ptr):
            ptr._obj.vid, ptr._obj.pid, ptr._obj.version = 0x05AC, 0x024F, 0x0110
            return True

        def string(text):
            def read(_handle, buffer, _length):
                buffer.value = text
                return True
            return read

        def preparsed(_handle, ptr):
            ptr._obj.value = 23
            return True

        def caps(_data, ptr):
            report_caps = ptr._obj
            report_caps.usage, report_caps.page = 1, 0xFF13
            report_caps.input, report_caps.output, report_caps.feature = 8, 5, 65
            report_caps.input_values, report_caps.input_buttons = 2, 1
            report_caps.output_values, report_caps.feature_values = 2, 1
            return inventory.HIDP_SUCCESS

        def capabilities(source):
            def read(report_type, fields, count, _data):
                entries = source[report_type]
                self.assertEqual(count._obj.value, len(entries))
                for index, field in enumerate(entries):
                    fields[index] = field
                return inventory.HIDP_SUCCESS
            return read

        api.hid = SimpleNamespace(
            HidD_GetAttributes=attrs,
            HidD_GetProductString=string("INFI75-1.1 SN: UNIT-PRIVATE"),
            HidD_GetManufacturerString=string("Fixture Makers " + INSTANCE),
            HidD_GetPreparsedData=preparsed,
            HidP_GetCaps=caps,
            HidP_GetValueCaps=capabilities(values),
            HidP_GetButtonCaps=capabilities(buttons),
            HidD_FreePreparsedData=Mock(),
        )
        export = inventory.collect_inventory(api, inventory.hex_word("05ac"), inventory.hex_word("0x024f"))
        self.assertEqual(export["filter"], {"vid": "05AC", "pid": "024F"})
        row, = export["interfaces"]
        self.assertEqual({key: row[key] for key in ("vid", "pid", "mi", "col", "usagePage", "usage", "version")},
                         {"vid": "05AC", "pid": "024F", "mi": "03", "col": "01",
                          "usagePage": "FF13", "usage": "0001", "version": "0110"})
        self.assertEqual([row[kind + "ReportByteLength"] for kind in inventory.REPORT_TYPES], [8, 5, 65])
        self.assertEqual([(r["type"], r["reportId"], r["reportByteLength"], r["minimumReportByteLength"])
                          for r in row["reports"]],
                         [("input", "11", 8, 8), ("output", "02", 5, 5),
                          ("output", "05", None, 3), ("feature", "00", 65, 65)])
        self.assertEqual((row["product"], row["manufacturer"], row["errors"]),
                         ("INFI75-1.1 [private]", "Fixture Makers [private]", []))
        encoded = json.dumps(export)
        for fragment in (INSTANCE, "UNIT-PRIVATE", "4d1e55b2"):
            self.assertNotIn(fragment, encoded)
        api.kernel.CreateFileW.assert_called_once_with(PATH, 0, 3, None, 3, 0, None)
        api.kernel.CloseHandle.assert_called_once_with(17)
        api.hid.HidD_FreePreparsedData.assert_called_once()
        self.assertEqual(api.hid.HidD_FreePreparsedData.call_args.args[0].value, 23)

    def test_partial_and_array_caps_leave_unproven_lengths_explicit(self):
        report, = inventory.report_lengths("input", 8, [value(1, 8, 2)], [], complete=False)
        self.assertEqual((report["reportByteLength"], report["minimumReportByteLength"], report["lengthBasis"]),
                         (None, 3, "incomplete_caps"))
        report, = inventory.report_lengths("feature", 65, [], [button(0, first=0, last=255)])
        self.assertEqual((report["reportId"], report["reportByteLength"], report["minimumReportByteLength"]),
                         ("00", 65, None))
        self.assertEqual(report["lengthBasis"], "single_id_caps_maximum")
        report, = inventory.report_lengths("output", 3, [value(1, 8, 8)], [])
        self.assertEqual((report["reportByteLength"], report["minimumReportByteLength"], report["lengthBasis"]),
                         (None, None, "inconsistent_caps"))

    def test_output_stays_outside_application_data(self):
        with tempfile.TemporaryDirectory(prefix="inventory-output-") as temp:
            directory = Path(temp)
            app_data = directory / "MyFinderDeviceBattery"
            with self.assertRaisesRegex(ValueError, "outside"):
                inventory.output_target(app_data / "infi75.txt", app_data)
            output = inventory.output_target(directory / "infi75.txt", app_data)
            sample = {"schema": 1, "interfaces": []}
            inventory.save_inventory(output, sample)
            self.assertEqual(json.loads(output.read_text(encoding="utf-8")), sample)
            self.assertFalse(app_data.exists())
