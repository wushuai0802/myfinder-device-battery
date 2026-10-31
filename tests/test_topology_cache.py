"""Descriptor-cache regressions using fake Windows DLLs; no real HID access."""
import ctypes
import io
import json
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch

ENGINE = Path(__file__).resolve().parents[1] / "engine"
sys.path.insert(0, str(ENGINE))
sys.path.insert(0, str(ENGINE / "tests"))
import _hidstub
import server
import topology
from standardhid import Caps


PATH_A = r"\\?\hid#vid_388d&pid_0025&mi_01#fixture-a"
PATH_B = r"\\?\hid#vid_388d&pid_0025&mi_01#fixture-b"


class DescriptorCacheTests(unittest.TestCase):
    def setUp(self):
        self.kernel = Mock()
        self.hid = Mock()
        self.handles = {}
        self.fail_open = set()
        self.fail_attrs = set()
        self.fail_preparsed = set()
        self.fail_caps = set()
        self.empty_product = set()
        self.native_attributes = set()
        self.kernel.CreateFileW.side_effect = self.open
        self.hid.HidD_GetAttributes.side_effect = self.attributes
        self.hid.HidD_GetPreparsedData.side_effect = self.preparsed
        self.hid.HidP_GetCaps.side_effect = self.caps
        self.hid.HidD_GetProductString.side_effect = self.product
        self.hid.HidD_GetManufacturerString.side_effect = self.manufacturer
        self.hid.HidD_GetSerialNumberString.side_effect = self.serial
        self.cache = getattr(topology, "_descriptor_cache", {})
        for patcher in (patch.dict(self.cache, clear=True),
                        patch.object(topology, "os", SimpleNamespace(name="nt")),
                        patch.object(topology.c, "WinDLL", side_effect=self.dll, create=True)):
            patcher.start()
            self.addCleanup(patcher.stop)
        parent = patch.object(topology, "usb_parent", side_effect=lambda path: "physical:" + path)
        self.parent = parent.start()
        self.addCleanup(parent.stop)

    def dll(self, name, **kwargs):
        return {"kernel32": self.kernel, "hid": self.hid}[name]

    def open(self, path, *args):
        if path in self.fail_open:
            return ctypes.c_void_p(-1).value
        handle = len(self.handles) + 1
        self.handles[handle] = path
        return handle

    def attributes(self, handle, pointer):
        if self.handles[handle] in self.fail_attrs:
            return 0
        value = ctypes.cast(pointer, ctypes.POINTER(topology._Attributes)).contents
        value.vendor, value.product = ((0x373B, 0x1278) if self.handles[handle] in self.native_attributes
                                       else (0x388D, 0x0025))
        value.release = 1
        return 1

    def preparsed(self, handle, pointer):
        if self.handles[handle] in self.fail_preparsed:
            return 0
        ctypes.cast(pointer, ctypes.POINTER(ctypes.c_void_p))[0] = handle
        return 1

    def caps(self, data, pointer):
        if self.handles[data.value] in self.fail_caps:
            return 0
        value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
        value.page, value.usage = 0xFF1C, 0x0092
        return 0x00110000

    def product(self, handle, buffer, size):
        buffer.value = "" if self.handles[handle] in self.empty_product else "Fixture Mouse"
        return 1

    def manufacturer(self, handle, buffer, size):
        buffer.value = "Fixture Maker"
        return 1

    def serial(self, handle, buffer, size):
        buffer.value = ""
        return 1

    def scan(self, paths):
        return topology.discover(paths, server.NATIVE)

    def test_unchanged_path_is_not_opened_again_even_with_empty_serial(self):
        first = self.scan([PATH_A])
        self.kernel.CreateFileW.reset_mock()
        second = self.scan([PATH_A])
        self.kernel.CreateFileW.assert_not_called()
        self.assertEqual(second, first)
        self.assertEqual(second[0]["serial_number"], "")
        self.assertEqual(second[0]["usage"], 0x0092)

    def test_only_the_new_path_is_opened(self):
        self.scan([PATH_A])
        self.kernel.CreateFileW.reset_mock()
        self.scan([PATH_A, PATH_B])
        self.kernel.CreateFileW.assert_called_once_with(PATH_B, 0, 3, None, 3, 0, None)

    def test_incomplete_descriptors_are_retried_until_complete(self):
        for failure_name in ("fail_open", "fail_attrs", "fail_preparsed", "fail_caps", "empty_product"):
            failure = getattr(self, failure_name)
            with self.subTest(failure=failure_name):
                self.cache.clear()
                self.kernel.CreateFileW.reset_mock()
                failure.add(PATH_A)
                self.scan([PATH_A])
                failure.clear()
                complete = self.scan([PATH_A])
                again = self.scan([PATH_A])
                self.assertEqual(self.kernel.CreateFileW.call_count, 2)
                self.assertEqual(complete, again)
                self.assertEqual(complete[0]["product_string"], "Fixture Mouse")
                self.assertEqual(complete[0]["usage_page"], 0xFF1C)

    def test_physical_identity_is_recomputed_without_reopening_hid(self):
        self.parent.side_effect = ["physical:first", "physical:replacement"]
        first = self.scan([PATH_A])
        second = self.scan([PATH_A])
        self.assertEqual(first[0]["physical_id"], "physical:first")
        self.assertEqual(second[0]["physical_id"], "physical:replacement")
        self.assertEqual(self.parent.call_count, 2)
        self.assertEqual(self.kernel.CreateFileW.call_count, 1)

    def test_mutating_returned_descriptors_does_not_change_cache(self):
        first = self.scan([PATH_A])[0]
        first.update(product_string="changed", vendor_id=1, usage=1, physical_id="changed")
        second = self.scan([PATH_A])[0]
        self.assertEqual(second["product_string"], "Fixture Mouse")
        self.assertEqual(second["vendor_id"], 0x388D)
        self.assertEqual(second["usage"], 0x0092)
        self.assertEqual(second["physical_id"], "physical:" + PATH_A)
        self.assertEqual(self.kernel.CreateFileW.call_count, 1)

    def test_partial_discovery_keeps_other_paths_cached(self):
        self.scan([PATH_A, PATH_B])
        self.scan([PATH_A])
        self.kernel.CreateFileW.reset_mock()
        self.scan([PATH_B])
        self.kernel.CreateFileW.assert_not_called()

    def test_full_discovery_evicts_only_paths_no_longer_selected(self):
        topology.discover([PATH_A, PATH_B], server.NATIVE, full=True)
        topology.discover([PATH_A], server.NATIVE, full=True)
        self.kernel.CreateFileW.reset_mock()
        self.scan([PATH_A, PATH_B])
        self.kernel.CreateFileW.assert_called_once_with(PATH_B, 0, 3, None, 3, 0, None)

    def test_native_paths_are_excluded_before_open_and_never_cached(self):
        native = [r"\\?\hid#vid_373b&pid_1278&mi_02#fixture", r"\\?\hid#vid_03f0&pid_098d#fixture"]
        self.scan(native + [PATH_A])
        self.scan(native + [PATH_A])
        self.assertFalse(any(path in self.cache for path in native))
        self.kernel.CreateFileW.assert_called_once_with(PATH_A, 0, 3, None, 3, 0, None)

    def test_native_attributes_are_not_returned_or_cached(self):
        self.native_attributes.add(PATH_A)
        self.assertEqual(self.scan([PATH_A]), [])
        self.assertNotIn(PATH_A, self.cache)

    def test_discovery_keeps_sorted_512_path_limit(self):
        paths = [PATH_A + str(index).zfill(4) for index in range(513)]
        self.scan(reversed(paths))
        self.assertEqual([call.args[0] for call in self.kernel.CreateFileW.call_args_list], sorted(paths)[:512])
        self.assertLessEqual(len(self.cache), 512)

    def test_partial_discovery_at_capacity_preserves_other_cached_paths(self):
        paths = [PATH_A + str(index).zfill(4) for index in range(513)]
        self.scan(paths[:512])
        self.scan(paths[512:])
        self.assertLessEqual(len(self.cache), 512)
        self.kernel.CreateFileW.reset_mock()
        self.scan([paths[0]])
        self.kernel.CreateFileW.assert_not_called()
        self.scan(paths[512:])
        self.kernel.CreateFileW.assert_called_once()

    def test_unavailable_full_topology_does_not_clear_cache(self):
        self.scan([PATH_A])
        with self.assertRaises(RuntimeError):
            topology.discover(None, server.NATIVE, full=True)
        self.kernel.CreateFileW.reset_mock()
        self.scan([PATH_A])
        self.kernel.CreateFileW.assert_not_called()

    def test_reader_child_marks_full_scans_and_preserves_partial_cache(self):
        commands = [
            {"action": "topology", "paths": [PATH_A, PATH_B]},
            {"action": "topology", "paths": [PATH_A, PATH_B], "physicalId": server.public_id("physical:" + PATH_A)},
            {"action": "topology", "paths": [PATH_A, PATH_B], "physicalId": server.public_id("physical:" + PATH_B)},
            {"action": "topology", "paths": [PATH_A]},
        ]
        data = "".join(json.dumps(dict(command, sequence=index)) + "\n" for index, command in enumerate(commands)).encode()
        with patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
            server.reader_child()
        replies = [json.loads(line) for line in output.getvalue().splitlines()]
        self.assertEqual([len(reply["inventory"]) for reply in replies], [2, 1, 1, 1])
        self.assertEqual(self.kernel.CreateFileW.call_count, 2)
        self.kernel.CreateFileW.reset_mock()
        self.scan([PATH_B])
        self.kernel.CreateFileW.assert_called_once()


if __name__ == "__main__":
    unittest.main()
