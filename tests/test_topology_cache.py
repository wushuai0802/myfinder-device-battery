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
from providers import audeze, rapoo
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
        self.products = {}
        self.identities = {}
        self.kernel.CreateFileW.side_effect = self.open
        self.hid.HidD_GetAttributes.side_effect = self.attributes
        self.hid.HidD_GetPreparsedData.side_effect = self.preparsed
        self.hid.HidP_GetCaps.side_effect = self.caps
        self.hid.HidD_GetProductString.side_effect = self.product
        self.hid.HidD_GetManufacturerString.side_effect = self.manufacturer
        self.hid.HidD_GetSerialNumberString.side_effect = self.serial
        self.cache = getattr(topology, "_descriptor_cache", {})
        for patcher in (patch.dict(self.cache, clear=True),
                        patch("battery_hid.hid", SimpleNamespace()),
                        patch.object(topology, "_descriptor_paths", None, create=True),
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
                                       else self.identities.get(self.handles[handle], (0x388D, 0x0025)))
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
        buffer.value = ("" if self.handles[handle] in self.empty_product
                        else self.products.get(self.handles[handle], "Fixture Mouse"))
        return 1

    def manufacturer(self, handle, buffer, size):
        buffer.value = "Fixture Maker"
        return 1

    def serial(self, handle, buffer, size):
        buffer.value = ""
        return 1

    def scan(self, paths, *, full=True):
        return topology.discover(paths, server.NATIVE, full=full)

    def test_unchanged_full_topology_is_not_opened_again_even_with_empty_serial(self):
        first = self.scan([PATH_A])
        self.kernel.CreateFileW.reset_mock()
        second = self.scan([PATH_A])
        self.kernel.CreateFileW.assert_not_called()
        self.assertEqual(second, first)
        self.assertEqual(second[0]["serial_number"], "")
        self.assertEqual(second[0]["usage"], 0x0092)

    def test_report_lengths_come_from_caps_and_survive_cache(self):
        def caps(data, pointer):
            result = self.caps(data, pointer)
            value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
            value.input, value.output = 64, 64
            return result

        self.hid.HidP_GetCaps.side_effect = caps
        first = self.scan([PATH_A])[0]
        second = self.scan([PATH_A])[0]
        for info in (first, second):
            self.assertEqual((info["input_report_length"], info["output_report_length"]), (64, 64))
        self.assertEqual(self.kernel.CreateFileW.call_count, 1)

    def test_failed_caps_leave_report_lengths_unknown(self):
        self.fail_caps.add(PATH_A)
        info, = self.scan([PATH_A])
        self.assertNotIn("input_report_length", info)
        self.assertNotIn("output_report_length", info)

    def keyboard_descriptor(self, protocol="qhw", input_ids=(0x4D,), output_ids=(0x4D,)):
        self.identities[PATH_A] = (0x3837, 0x1015 if protocol == "qhw" else 0x3033)
        self.products[PATH_A] = "MCHOSE G98 V3 2.4G" if protocol == "qhw" else "MCHOSE G87 V2 2.4G"

        def caps(data, pointer):
            value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
            value.page, value.usage = (0xFF80, 1) if protocol == "qhw" else (1, 0)
            value.input, value.output = 65, 65
            value.iv, value.ob = len(input_ids), len(output_ids)
            return 0x00110000

        def fields(report_type, values, length, data):
            for value, report_id in zip(values, input_ids if report_type == 0 else output_ids):
                value.report = report_id
            return 0x00110000

        self.hid.HidP_GetCaps.side_effect = caps
        self.hid.HidP_GetValueCaps.side_effect = fields
        self.hid.HidP_GetButtonCaps.side_effect = fields

    def test_keyboard_report_ids_use_both_value_and_button_caps_and_survive_cache(self):
        for protocol, inp, out in (("qhw", (0x4D,), (0x4D,)), ("glw", (7,), (0,))):
            with self.subTest(protocol=protocol):
                self.cache.clear()
                self.keyboard_descriptor(protocol, inp, out)
                first, = self.scan([PATH_A])
                self.kernel.CreateFileW.reset_mock()
                second, = self.scan([PATH_A])
                self.kernel.CreateFileW.assert_not_called()
                self.assertEqual(first, second)
                self.assertEqual(first["input_report_ids"], inp)
                self.assertEqual(first["output_report_ids"], out)
                self.hid.HidP_GetValueCaps.assert_called()
                self.hid.HidP_GetButtonCaps.assert_called()
                self.hid.HidD_GetFeature.assert_not_called()
                self.hid.HidD_SetFeature.assert_not_called()
                self.kernel.ReadFile.assert_not_called()
                self.kernel.WriteFile.assert_not_called()

    def test_multiple_declared_ids_are_preserved_instead_of_guessing_from_max_length(self):
        self.keyboard_descriptor(input_ids=(0x4D, 0x4E))
        info, = self.scan([PATH_A])
        self.assertEqual(info["input_report_ids"], (0x4D, 0x4E))

    def test_keyboard_failed_or_partial_id_discovery_stays_unknown_and_is_retried(self):
        self.keyboard_descriptor()
        self.hid.HidP_GetValueCaps.side_effect = lambda *args: 0
        info, = self.scan([PATH_A])
        self.assertIsNone(info["input_report_ids"])
        self.assertNotIn(PATH_A, self.cache)

        def partial(report_type, values, length, data):
            ctypes.cast(length, ctypes.POINTER(ctypes.c_ushort))[0] = 0
            return 0x00110000

        self.hid.HidP_GetValueCaps.side_effect = partial
        info, = self.scan([PATH_A])
        self.assertIsNone(info["input_report_ids"])
        self.keyboard_descriptor()
        self.assertEqual(self.scan([PATH_A])[0]["input_report_ids"], (0x4D,))
        self.assertIn(PATH_A, self.cache)

    def test_report_id_inspection_does_not_expand_to_other_models_or_collections(self):
        self.scan([PATH_A])
        self.hid.HidP_GetValueCaps.assert_not_called()
        self.hid.HidP_GetButtonCaps.assert_not_called()
        self.cache.clear()
        self.keyboard_descriptor()
        self.products[PATH_A] = "MCHOSE K99 V3 2.4G"
        self.scan([PATH_A])
        self.hid.HidP_GetValueCaps.assert_not_called()
        self.cache.clear()
        self.keyboard_descriptor()
        self.hid.HidP_GetCaps.side_effect = self.caps  # wrong collection
        self.scan([PATH_A])
        self.hid.HidP_GetValueCaps.assert_not_called()

    def test_full_topology_change_reopens_all_selected_paths(self):
        self.scan([PATH_A])
        self.kernel.CreateFileW.reset_mock()
        self.scan([PATH_A, PATH_B])
        self.assertEqual([call.args[0] for call in self.kernel.CreateFileW.call_args_list], [PATH_A, PATH_B])

    def batch3_descriptor(self, identity, collection, inp, out):
        self.identities[PATH_A] = identity

        def caps(data, pointer):
            value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
            value.page, value.usage = collection
            value.input, value.output = max(inp.values()), max(out.values())
            value.iv, value.ov = len(inp), len(out)
            return 0x00110000

        def fields(report_type, values, length, data):
            for value, (report_id, size) in zip(values, (inp if report_type == 0 else out).items()):
                value.report, value.bits, value.count = report_id, 8, size - 1
            return 0x00110000

        self.hid.HidP_GetCaps.side_effect = caps
        self.hid.HidP_GetValueCaps.side_effect = fields

    def dareu_descriptor(self, identity=(0x260D, 0x1074), collection=(0xFF02, 2)):
        self.identities[PATH_A] = identity

        def caps(data, pointer):
            value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
            value.page, value.usage = collection
            value.input, value.feature = 17, 17
            value.iv, value.fv = 1, 1
            return 0x00110000

        def fields(report_type, values, length, data):
            self.assertIn(report_type, (0, 2))  # Never inspect output reports.
            values[0].report = 9 if report_type == 0 else 8
            values[0].bits, values[0].count = 8, 16
            return 0x00110000

        self.hid.HidP_GetCaps.side_effect = caps
        self.hid.HidP_GetValueCaps.side_effect = fields

    def test_dareu_feature_and_input_proof_is_descriptor_only_and_cached(self):
        from providers import dareu
        self.dareu_descriptor()
        first, = self.scan([PATH_A])
        self.assertEqual(first["feature_report_sizes"], {"8": 17})
        self.assertEqual(first["input_report_sizes"], {"9": 17})
        self.assertEqual((first["feature_report_length"], first["input_report_length"]), (17, 17))
        self.assertEqual(dareu.DareuProvider()._pick([first]), first)
        self.assertEqual([call.args[0] for call in self.hid.HidP_GetValueCaps.call_args_list], [2, 0])
        # Only a zero-access descriptor handle is opened, never a query handle.
        self.assertEqual(self.kernel.CreateFileW.call_args.args[1], 0)
        self.kernel.CreateFileW.reset_mock()
        self.assertEqual(self.scan([PATH_A]), [first])
        self.kernel.CreateFileW.assert_not_called()
        self.hid.HidD_GetFeature.assert_not_called()
        self.hid.HidD_SetFeature.assert_not_called()
        self.kernel.ReadFile.assert_not_called()
        self.kernel.WriteFile.assert_not_called()

    def test_dareu_incomplete_feature_or_input_proof_is_retried_without_query(self):
        from providers import dareu
        for failed_type in (0, 2):
            for defect in ("failure", "nonbyte", "alias", "partial"):
                with self.subTest(report_type=failed_type, defect=defect):
                    self.cache.clear()
                    self.dareu_descriptor()
                    original = self.hid.HidP_GetValueCaps.side_effect

                    def fields(report_type, values, length, data):
                        result = original(report_type, values, length, data)
                        if report_type == failed_type:
                            if defect == "failure":
                                return 0
                            if defect == "nonbyte":
                                values[0].bits = 16
                            elif defect == "alias":
                                values[0].alias = 1
                            elif defect == "partial":
                                ctypes.cast(length, ctypes.POINTER(ctypes.c_ushort))[0] = 0
                        return result

                    self.hid.HidP_GetValueCaps.side_effect = fields
                    info, = self.scan([PATH_A])
                    self.assertIsNone(dareu.DareuProvider()._pick([info]))
                    self.assertNotIn(PATH_A, self.cache)
                    self.dareu_descriptor()
                    recovered, = self.scan([PATH_A])
                    self.assertEqual(dareu.DareuProvider()._pick([recovered]), recovered)
        self.hid.HidD_GetFeature.assert_not_called()
        self.hid.HidD_SetFeature.assert_not_called()
        self.kernel.ReadFile.assert_not_called()
        self.kernel.WriteFile.assert_not_called()

    def test_dareu_descriptor_proof_is_not_enabled_for_other_ids_or_collections(self):
        for identity, collection in (((0x260D, 0x1090), (0xFF02, 2)),
                                     ((0x260D, 0x1075), (0xFF02, 2)),
                                     ((0x3554, 0x1074), (0xFF02, 2)),
                                     ((0x260D, 0x1074), (0xFF04, 2)),
                                     ((0x260D, 0x1074), (0xFF02, 1))):
            with self.subTest(identity=identity, collection=collection):
                self.cache.clear()
                self.hid.HidP_GetValueCaps.reset_mock()
                self.dareu_descriptor(identity, collection)
                info, = self.scan([PATH_A])
                self.assertNotIn("feature_report_sizes", info)
                self.assertNotIn("input_report_sizes", info)
                self.hid.HidP_GetValueCaps.assert_not_called()

    def test_pulsar_link8k_report_proof_is_descriptor_only_and_cached(self):
        self.batch3_descriptor((0x3710, 0x5501), (0xFF02, 2), {8: 17}, {8: 17})
        first, = self.scan([PATH_A])
        self.assertEqual(first["input_report_sizes"], {"8": 17})
        self.assertEqual(first["output_report_sizes"], {"8": 17})
        self.assertEqual((first["input_report_length"], first["output_report_length"]), (17, 17))
        self.assertEqual([call.args[0] for call in self.hid.HidP_GetValueCaps.call_args_list], [0, 1])
        self.kernel.CreateFileW.reset_mock()
        self.assertEqual(self.scan([PATH_A]), [first])
        self.kernel.CreateFileW.assert_not_called()
        self.hid.HidD_GetFeature.assert_not_called()
        self.hid.HidD_SetFeature.assert_not_called()
        self.kernel.ReadFile.assert_not_called()
        self.kernel.WriteFile.assert_not_called()

    def test_pulsar_link8k_failed_report_proof_is_not_cached_and_recovers(self):
        from providers import pulsar
        self.batch3_descriptor((0x3710, 0x5501), (0xFF02, 2), {8: 17}, {8: 17})
        self.hid.HidP_GetValueCaps.side_effect = lambda *args: 0
        info, = self.scan([PATH_A])
        self.assertIsNone(info["input_report_sizes"])
        self.assertIsNone(info["output_report_sizes"])
        self.assertNotIn(PATH_A, self.cache)
        provider = pulsar.PulsarProvider()
        self.assertIsNone(provider._pick_link8k([info]))
        self.batch3_descriptor((0x3710, 0x5501), (0xFF02, 2), {8: 17}, {8: 17})
        recovered, = self.scan([PATH_A])
        self.assertEqual(provider._pick_link8k([recovered]), recovered)

    def test_pulsar_link8k_proof_does_not_expand_to_siblings_or_other_ids(self):
        for identity, collection in (((0x3710, 0x5501), (0xFF04, 2)),
                                     ((0x3710, 0x5501), (0xFF02, 1)),
                                     ((0x3710, 0x5502), (0xFF02, 2)),
                                     ((0x3554, 0x5501), (0xFF02, 2))):
            with self.subTest(identity=identity, collection=collection):
                self.cache.clear()
                self.hid.HidP_GetValueCaps.reset_mock()
                self.batch3_descriptor(identity, collection, {8: 17}, {8: 17})
                info, = self.scan([PATH_A])
                self.assertNotIn("input_report_sizes", info)
                self.assertNotIn("output_report_sizes", info)
                self.hid.HidP_GetValueCaps.assert_not_called()

    def test_atk_nano_input_report_is_proven_without_hardware_commands(self):
        from providers import pulsar
        for report_id, length in ((0, 8), (8, 64), (85, 32)):
            self.cache.clear()
            self.batch3_descriptor((0x373B, 0x1216), (0xFF05, 1), {report_id: length}, {8: 64})
            info, = self.scan([PATH_A])
            self.assertEqual(info["input_report_sizes"], {str(report_id): length})
            self.assertEqual(info["output_report_sizes"], {"8": 64})
            self.assertEqual(pulsar.AtkNanoProvider()._pick([info]), info)
            self.assertEqual(self.scan([PATH_A]), [info])
        self.hid.HidD_GetFeature.assert_not_called()
        self.hid.HidD_SetFeature.assert_not_called()
        self.kernel.ReadFile.assert_not_called()
        self.kernel.WriteFile.assert_not_called()

    def test_atk_nano_bad_descriptor_recovers_and_siblings_are_not_probed(self):
        from providers import pulsar
        self.batch3_descriptor((0x373B, 0x1216), (0xFF05, 1), {8: 64}, {8: 64})
        self.hid.HidP_GetValueCaps.side_effect = lambda *args: 0
        info, = self.scan([PATH_A])
        self.assertNotIn(PATH_A, self.cache)
        self.assertIsNone(pulsar.AtkNanoProvider()._pick([info]))
        self.batch3_descriptor((0x373B, 0x1216), (0xFF05, 1), {8: 64}, {8: 64})
        recovered, = self.scan([PATH_A])
        self.assertEqual(pulsar.AtkNanoProvider()._pick([recovered]), recovered)
        for identity, collection in (((0x373B, 0x1216), (0xFFB1, 1)),
                                     ((0x373B, 0x1216), (0xFFB3, 1)),
                                     ((0x373B, 0x1216), (0xFF05, 2)),
                                     ((0x373B, 0x1217), (0xFF05, 1)),
                                     ((0x3554, 0x1216), (0xFF05, 1))):
            self.cache.clear()
            self.hid.HidP_GetValueCaps.reset_mock()
            self.batch3_descriptor(identity, collection, {8: 64}, {8: 64})
            info, = self.scan([PATH_A])
            self.assertNotIn("input_report_sizes", info)
            self.hid.HidP_GetValueCaps.assert_not_called()

    def test_atk_headset_report_proof_is_descriptor_only_and_cached(self):
        from providers import atk_headset
        for pid in (0x1262, 0x12D6):
            for length in (22, 64):
                with self.subTest(pid=pid, input_length=length):
                    self.cache.clear()
                    self.hid.HidP_GetValueCaps.reset_mock()
                    self.batch3_descriptor((0x373B, pid), (0xFF90, 1), {0x55: length}, {0x55: 64})
                    first, = self.scan([PATH_A])
                    self.assertEqual(first["input_report_sizes"], {"85": length})
                    self.assertEqual(first["output_report_sizes"], {"85": 64})
                    self.assertEqual(atk_headset.AtkHeadsetProvider()._pick([first]), first)
                    self.assertEqual([call.args[0] for call in self.hid.HidP_GetValueCaps.call_args_list], [0, 1])
                    self.assertEqual(self.kernel.CreateFileW.call_args.args[1], 0)
                    self.kernel.CreateFileW.reset_mock()
                    self.assertEqual(self.scan([PATH_A]), [first])
                    self.kernel.CreateFileW.assert_not_called()
        self.hid.HidD_GetFeature.assert_not_called()
        self.hid.HidD_SetFeature.assert_not_called()
        self.kernel.ReadFile.assert_not_called()
        self.kernel.WriteFile.assert_not_called()

    def test_atk_headset_failed_report_proof_is_retried_and_wrong_ids_rejected(self):
        from providers import atk_headset
        for direction in (0, 1):
            for defect in ("failure", "nonbyte", "alias", "partial"):
                with self.subTest(direction=direction, defect=defect):
                    self.cache.clear()
                    self.batch3_descriptor((0x373B, 0x1262), (0xFF90, 1), {0x55: 64}, {0x55: 64})
                    original = self.hid.HidP_GetValueCaps.side_effect

                    def fields(report_type, values, length, data):
                        result = original(report_type, values, length, data)
                        if report_type == direction:
                            if defect == "failure":
                                return 0
                            if defect == "nonbyte":
                                values[0].bits = 16
                            elif defect == "alias":
                                values[0].alias = 1
                            elif defect == "partial":
                                ctypes.cast(length, ctypes.POINTER(ctypes.c_ushort))[0] = 0
                        return result

                    self.hid.HidP_GetValueCaps.side_effect = fields
                    info, = self.scan([PATH_A])
                    self.assertIsNone(atk_headset.AtkHeadsetProvider()._pick([info]))
                    self.assertNotIn(PATH_A, self.cache)
                    self.batch3_descriptor((0x373B, 0x1262), (0xFF90, 1), {0x55: 64}, {0x55: 64})
                    recovered, = self.scan([PATH_A])
                    self.assertEqual(atk_headset.AtkHeadsetProvider()._pick([recovered]), recovered)
        self.cache.clear()
        self.batch3_descriptor((0x373B, 0x1262), (0xFF90, 1), {0x41: 64}, {0x55: 64})
        wrong_id, = self.scan([PATH_A])
        self.assertIsNone(atk_headset.AtkHeadsetProvider()._pick([wrong_id]))
        self.hid.HidD_GetFeature.assert_not_called()
        self.hid.HidD_SetFeature.assert_not_called()
        self.kernel.ReadFile.assert_not_called()
        self.kernel.WriteFile.assert_not_called()

    def test_atk_headset_proof_never_expands_to_firmware_or_other_devices(self):
        for identity, collection in (((0x373B, 0x1262), (0xFF82, 1)),
                                     ((0x373B, 0x12D6), (0xFF82, 1)),
                                     ((0x373B, 0x1262), (0xFF90, 2)),
                                     ((0x373B, 0x1204), (0xFF90, 1)),
                                     ((0x373B, 0x1258), (0xFF60, 0x61)),
                                     ((0x373B, 0x12D4), (0xFF90, 1)),
                                     ((0x291D, 0x1262), (0xFF90, 1))):
            with self.subTest(identity=identity, collection=collection):
                self.cache.clear()
                self.hid.HidP_GetValueCaps.reset_mock()
                self.batch3_descriptor(identity, collection, {0x55: 64}, {0x55: 64})
                info, = self.scan([PATH_A])
                self.assertNotIn("input_report_sizes", info)
                self.assertNotIn("output_report_sizes", info)
                self.hid.HidP_GetValueCaps.assert_not_called()

    def test_batch3_report_sizes_are_per_id_cached_and_descriptor_only(self):
        cases = (((0x320F, 0x706E), (0xFF1C, 0x92), {4: 64}, {4: 64}),
                 ((0x3554, 0xFA09), (0xFF04, 2), {19: 20}, {19: 20}),
                 ((0x41E4, 0x2001), (0xFF04, 2), {19: 20}, {19: 20}),
                 ((0x320F, 0x5088), (0xFF1C, 0x92), {4: 64}, {4: 64}),
                 ((0x089D, 0x062F), (0xFF01, 0x10), {0: 33}, {0: 33}),
                 ((0x37D7, 0x2501), (0xFFA0, 1), {4: 32}, {3: 32, 8: 13}))
        for identity, collection, inp, out in cases:
            with self.subTest(identity=identity):
                self.cache.clear()
                self.batch3_descriptor(identity, collection, inp, out)
                first, = self.scan([PATH_A])
                self.kernel.CreateFileW.reset_mock()
                second, = self.scan([PATH_A])
                self.assertEqual(first, second)
                self.assertEqual(first["input_report_sizes"], {str(k): v for k, v in inp.items()})
                self.assertEqual(first["output_report_sizes"], {str(k): v for k, v in out.items()})
                self.kernel.CreateFileW.assert_not_called()
                self.hid.HidD_GetFeature.assert_not_called()
                self.hid.HidD_SetFeature.assert_not_called()
                self.kernel.ReadFile.assert_not_called()
                self.kernel.WriteFile.assert_not_called()

    def test_fa09_single_byte_selector_arrays_are_proven_without_device_commands(self):
        self.by_single_byte_selector_arrays((0x3554, 0xFA09))

    def aula_descriptor(self, identity=(0x3554, 0xFA09), collection=(0xFF04, 2), by=False):
        self.identities[PATH_A] = identity
        self.products[PATH_A] = "2.4G Wireless Receiver"

        def caps(data, pointer):
            value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
            value.page, value.usage = collection
            value.feature, value.fv = 8, 1
            if by:
                value.input = value.output = 20
                value.iv = value.ov = 1
            return 0x00110000

        def fields(report_type, values, length, data):
            values[0].report, values[0].bits, values[0].count = (6, 8, 7) if report_type == 2 else (19, 8, 19)
            return 0x00110000

        self.hid.HidP_GetCaps.side_effect = caps
        self.hid.HidP_GetValueCaps.side_effect = fields

    def test_aula_feature_proof_coexists_with_by_and_is_cached_without_commands(self):
        from providers.mchose_by_keyboard import MchoseByKeyboardProvider
        for by in (False, True):
            with self.subTest(by=by):
                self.cache.clear()
                self.aula_descriptor(by=by)
                info, = self.scan([PATH_A])
                self.assertEqual(info["feature_report_sizes"], {"6": 8})
                self.assertEqual(info["feature_report_length"], 8)
                self.assertEqual(info["input_report_sizes"], {"19": 20} if by else {})
                self.assertEqual(info["output_report_sizes"], {"19": 20} if by else {})
                provider = MchoseByKeyboardProvider()
                self.assertEqual(provider._pick([info]), info)
                self.assertEqual(provider._aula, not by)
                self.kernel.CreateFileW.reset_mock()
                self.assertEqual(self.scan([PATH_A]), [info])
                self.kernel.CreateFileW.assert_not_called()
        self.hid.HidD_GetFeature.assert_not_called()
        self.hid.HidD_SetFeature.assert_not_called()
        self.kernel.ReadFile.assert_not_called()
        self.kernel.WriteFile.assert_not_called()

    def test_aula_failed_feature_proof_stays_uncached_and_recovers(self):
        from providers.mchose_by_keyboard import MchoseByKeyboardProvider
        for defect in ("failure", "nonbyte", "alias", "partial", "padding"):
            with self.subTest(defect=defect):
                self.cache.clear()
                self.aula_descriptor()
                original = self.hid.HidP_GetValueCaps.side_effect

                def fields(report_type, values, length, data):
                    result = original(report_type, values, length, data)
                    if defect == "failure":
                        return 0
                    if defect == "nonbyte":
                        values[0].bits = 16
                    elif defect == "alias":
                        values[0].alias = 1
                    elif defect == "partial":
                        ctypes.cast(length, ctypes.POINTER(ctypes.c_ushort))[0] = 0
                    elif defect == "padding":
                        values[0].count = 6
                    return result

                self.hid.HidP_GetValueCaps.side_effect = fields
                info, = self.scan([PATH_A])
                self.assertIsNone(info["feature_report_sizes"])
                self.assertIsNone(MchoseByKeyboardProvider()._pick([info]))
                self.assertNotIn(PATH_A, self.cache)
                self.aula_descriptor()
                recovered, = self.scan([PATH_A])
                self.assertEqual(MchoseByKeyboardProvider()._pick([recovered]), recovered)

    def test_aula_zero_io_length_with_nonzero_field_counts_is_not_absence(self):
        from providers.mchose_by_keyboard import MchoseByKeyboardProvider
        for field in ("ib", "iv", "ob", "ov"):
            with self.subTest(field=field):
                self.cache.clear()
                self.aula_descriptor()
                original = self.hid.HidP_GetCaps.side_effect

                def caps(data, pointer):
                    result = original(data, pointer)
                    setattr(ctypes.cast(pointer, ctypes.POINTER(Caps)).contents, field, 1)
                    return result

                self.hid.HidP_GetCaps.side_effect = caps
                info, = self.scan([PATH_A])
                self.assertIsNone(MchoseByKeyboardProvider()._pick([info]))
                self.assertNotIn(PATH_A, self.cache)

    def test_aula_feature_proof_is_not_enabled_for_other_ids_or_collections(self):
        for identity, collection in (((0x05AC, 0x024F), (0xFF04, 2)),
                                     ((0x41E4, 0x2001), (0xFF04, 2)),
                                     ((0x3554, 0xFA09), (0xFF02, 2)),
                                     ((0x3554, 0xFA09), (0xFF04, 1))):
            with self.subTest(identity=identity, collection=collection):
                self.cache.clear()
                self.aula_descriptor(identity, collection)
                self.hid.HidP_GetValueCaps.reset_mock()
                info, = self.scan([PATH_A])
                self.assertNotIn("feature_report_sizes", info)
                self.assertNotIn(2, [c.args[0] for c in self.hid.HidP_GetValueCaps.call_args_list])

    def test_41e4_single_byte_selector_arrays_are_proven_without_device_commands(self):
        self.by_single_byte_selector_arrays((0x41E4, 0x2001))

    def by_single_byte_selector_arrays(self, identity):
        self.batch3_descriptor(identity, (0xFF04, 2), {19: 20}, {19: 20})
        original = self.hid.HidP_GetCaps.side_effect

        def caps(data, pointer):
            result = original(data, pointer)
            value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
            value.iv, value.ov, value.ib, value.ob = 0, 0, 1, 1
            return result

        def buttons(report_type, values, length, data):
            values[0].report, values[0].is_range = 19, 1
            values[0].usage_min, values[0].usage_max = 0, 255
            return 0x00110000

        self.hid.HidP_GetCaps.side_effect = caps
        self.hid.HidP_GetButtonCaps.side_effect = buttons
        first, = self.scan([PATH_A])
        self.assertEqual(first["input_report_sizes"], {"19": 20})
        self.assertEqual(first["output_report_sizes"], {"19": 20})
        self.kernel.CreateFileW.reset_mock()
        self.assertEqual(self.scan([PATH_A]), [first])
        self.kernel.CreateFileW.assert_not_called()
        self.hid.HidP_GetValueCaps.assert_not_called()
        self.hid.HidD_GetFeature.assert_not_called()
        self.hid.HidD_SetFeature.assert_not_called()
        self.kernel.ReadFile.assert_not_called()
        self.kernel.WriteFile.assert_not_called()

    def test_batch3_size_discovery_refuses_partial_alias_split_or_non_byte_caps(self):
        for defect in ("failure", "partial", "alias", "bits", "count", "split", "padding", "buttons"):
            with self.subTest(defect=defect):
                self.cache.clear()
                self.batch3_descriptor((0x37D7, 0x2501), (0xFFA0, 1), {4: 32}, {3: 32, 8: 13})
                original = self.hid.HidP_GetValueCaps.side_effect
                original_caps = self.hid.HidP_GetCaps.side_effect

                def caps(data, pointer):
                    result = original_caps(data, pointer)
                    value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
                    if defect == "padding":
                        value.output += 1
                    elif defect == "buttons":
                        value.ob = 1
                    return result

                def fields(report_type, values, length, data):
                    result = original(report_type, values, length, data)
                    if report_type == 1:
                        if defect == "failure":
                            return 0
                        if defect == "partial":
                            ctypes.cast(length, ctypes.POINTER(ctypes.c_ushort))[0] = 1
                        elif defect == "alias":
                            values[0].alias = 1
                        elif defect == "bits":
                            values[0].bits = 16
                        elif defect == "count":
                            values[0].count = 0
                        elif defect == "split":
                            values[1].report = values[0].report
                    return result

                self.hid.HidP_GetCaps.side_effect = caps
                self.hid.HidP_GetValueCaps.side_effect = fields
                info, = self.scan([PATH_A])
                self.assertIsNone(info["output_report_sizes"])
                self.assertNotIn(PATH_A, self.cache)
                self.batch3_descriptor((0x37D7, 0x2501), (0xFFA0, 1), {4: 32}, {3: 32, 8: 13})
                recovered, = self.scan([PATH_A])
                self.assertEqual(recovered["output_report_sizes"], {"3": 32, "8": 13})

    def test_batch3_size_discovery_does_not_expand_to_nearby_devices(self):
        for identity, collection in (((0x320F, 0x5055), (0xFF1C, 0x92)),
                                     ((0x3554, 0xFA09), (0xFF02, 1)),
                                     ((0x41E4, 0x2001), (0xFF02, 2)),
                                     ((0x41E4, 0x2001), (0xFF04, 1)),
                                     ((0x41E4, 0x2201), (0xFF00, 2)),
                                     ((0x41E4, 0xFA09), (0xFF04, 2)),
                                     ((0x3554, 0x2001), (0xFF04, 2)),
                                     ((0x3554, 0xFA07), (0xFF04, 2)),
                                     ((0x320F, 0x5088), (0xFF1C, 0x91)),
                                     ((0x089D, 0x062F), (0xFF00, 0)),
                                     ((0x37D7, 0x2501), (0xFFEE, 1)),
                                     ((0x37D7, 0x6001), (0xFFA0, 1))):
            with self.subTest(identity=identity, collection=collection):
                self.cache.clear()
                self.hid.HidP_GetValueCaps.reset_mock()
                self.batch3_descriptor(identity, collection, {4: 32}, {3: 32})
                info, = self.scan([PATH_A])
                self.assertNotIn("input_report_sizes", info)
                self.hid.HidP_GetValueCaps.assert_not_called()

    def test_rapoo_input_proof_is_descriptor_only_numeric_or_single_byte_array(self):
        for pid in rapoo.PIDS:
            for shape in ("numeric", "selector", "fixed_usage"):
                with self.subTest(pid=pid, shape=shape):
                    self.cache.clear()
                    self.hid.HidP_GetValueCaps.reset_mock()
                    self.hid.HidP_GetButtonCaps.reset_mock()
                    self.batch3_descriptor((0x24AE, pid), (0xFF00, 2), {7: 19}, {6: 65})
                    if shape != "numeric":
                        original = self.hid.HidP_GetCaps.side_effect

                        def caps(data, pointer):
                            result = original(data, pointer)
                            value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
                            value.iv, value.ib = 0, 1
                            return result

                        def buttons(report_type, values, length, data):
                            self.assertEqual(report_type, 0)
                            values[0].page, values[0].report = 0xFF00, 7
                            values[0].is_range = int(shape == "selector")
                            values[0].usage_min = 0 if shape == "selector" else 2
                            values[0].usage_max = 255 if shape == "selector" else 2
                            values[0].report_count = 18
                            return 0x00110000

                        self.hid.HidP_GetCaps.side_effect = caps
                        self.hid.HidP_GetButtonCaps.side_effect = buttons
                    first, = self.scan([PATH_A])
                    self.assertEqual(first["input_report_sizes"], {"7": 19})
                    self.assertNotIn("output_report_sizes", first)
                    self.assertNotIn("feature_report_sizes", first)
                    calls = (self.hid.HidP_GetValueCaps if shape == "numeric"
                             else self.hid.HidP_GetButtonCaps).call_args_list
                    self.assertEqual([call.args[0] for call in calls], [0, 0] if shape == "fixed_usage" else [0])
                    self.kernel.CreateFileW.reset_mock()
                    self.assertEqual(self.scan([PATH_A]), [first])
                    self.kernel.CreateFileW.assert_not_called()
                    self.hid.HidD_GetFeature.assert_not_called()
                    self.hid.HidD_SetFeature.assert_not_called()
                    self.kernel.ReadFile.assert_not_called()
                    self.kernel.WriteFile.assert_not_called()

    def test_rapoo_unproven_input_is_retried_and_nearby_devices_are_not_probed(self):
        for pid in rapoo.PIDS:
            for defect in ("no_07", "not_bytes", "alias", "partial", "failure"):
                with self.subTest(pid=pid, defect=defect):
                    self.cache.clear()
                    self.batch3_descriptor((0x24AE, pid), (0xFF00, 2), {7: 19}, {6: 65})
                    original = self.hid.HidP_GetValueCaps.side_effect

                    def fields(report_type, values, length, data):
                        result = original(report_type, values, length, data)
                        self.assertEqual(report_type, 0)
                        if defect == "no_07":
                            values[0].report = 6
                        elif defect == "not_bytes":
                            values[0].bits = 16
                        elif defect == "alias":
                            values[0].alias = 1
                        elif defect == "partial":
                            ctypes.cast(length, ctypes.POINTER(ctypes.c_ushort))[0] = 0
                        elif defect == "failure":
                            return 0
                        return result

                    self.hid.HidP_GetValueCaps.side_effect = fields
                    info, = self.scan([PATH_A])
                    self.assertNotIn(PATH_A, self.cache)
                    self.assertNotIn("7", info["input_report_sizes"] or {})
                    self.batch3_descriptor((0x24AE, pid), (0xFF00, 2), {7: 19}, {6: 65})
                    recovered, = self.scan([PATH_A])
                    self.assertEqual(recovered["input_report_sizes"], {"7": 19})
        for identity, collection in (((0x24AE, 0x1601), (0xFF00, 2)),
                                     ((0x24AE, 0x1419), (0xFF00, 2)),
                                     ((0x24AE, 0x4611), (0xFF00, 2)),
                                     ((0x24AE, 0x4612), (0xFF00, 2)),
                                     ((0x24AE, 0x1411), (0xFF00, 1)),
                                     ((0x24AE, 0x1411), (0xFF00, 14)),
                                     ((0x24AE, 0x1411), (0xFF01, 2)),
                                     ((0x24AE, 0x1412), (0xFF00, 1)),
                                     ((0x24AE, 0x1421), (0xFF00, 14)),
                                     ((0x24AF, 0x14A1), (0xFF00, 2))):
            with self.subTest(identity=identity, collection=collection):
                self.cache.clear()
                self.hid.HidP_GetValueCaps.reset_mock()
                self.hid.HidP_GetButtonCaps.reset_mock()
                self.batch3_descriptor(identity, collection, {7: 19}, {6: 65})

                def fixed_caps(data, pointer):
                    value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
                    value.page, value.usage = collection
                    value.input, value.output = 19, 65
                    value.ib, value.iv = 1, 0
                    return 0x00110000

                self.hid.HidP_GetCaps.side_effect = fixed_caps
                info, = self.scan([PATH_A])
                self.assertNotIn("input_report_sizes", info)
                self.hid.HidP_GetValueCaps.assert_not_called()
                self.hid.HidP_GetButtonCaps.assert_not_called()

    def test_reordered_full_path_set_uses_cache(self):
        first = self.scan([PATH_A, PATH_B])
        self.kernel.CreateFileW.reset_mock()
        self.assertEqual(self.scan([PATH_B, PATH_A]), first)
        self.kernel.CreateFileW.assert_not_called()

    def test_full_path_set_change_refreshes_product_string_on_existing_path(self):
        self.products[PATH_A] = "Receiver before"
        topology.discover([PATH_A], server.NATIVE, full=True)
        self.products[PATH_A] = "Receiver after"
        self.kernel.CreateFileW.reset_mock()
        result = topology.discover([PATH_A, PATH_B], server.NATIVE, full=True)
        self.assertEqual(result[0]["product_string"], "Receiver after")
        self.assertEqual([call.args[0] for call in self.kernel.CreateFileW.call_args_list], [PATH_A, PATH_B])

    def test_audeze_link_state_refreshes_when_full_path_set_changes(self):
        path = r"\\?\hid#vid_3329&pid_4b19&mi_01#fixture-maxwell"
        self.identities[path] = (audeze.AUDEZE_VID, 0x4B19)
        self.products[path] = "Audeze Maxwell Dongle"
        disconnected = topology.discover([path], server.NATIVE, full=True)
        self.assertTrue(audeze.no_headset_linked(disconnected))
        self.products[path] = "Audeze Maxwell HID"
        connected = topology.discover([path, PATH_B], server.NATIVE, full=True)
        self.assertFalse(audeze.no_headset_linked(connected))
        self.assertEqual(next(info["product_string"] for info in connected if info["vendor_id"] == audeze.AUDEZE_VID),
                         "Audeze Maxwell HID")
        self.products[path] = "Audeze Maxwell Dongle"
        disconnected = topology.discover([path], server.NATIVE, full=True)
        self.assertTrue(audeze.no_headset_linked(disconnected))
        self.assertEqual(self.kernel.CreateFileW.call_count, 4)

    def test_partial_refresh_updates_selected_product_without_invalidating_other_paths(self):
        self.products.update({PATH_A: "Target before", PATH_B: "Other before"})
        topology.discover([PATH_A, PATH_B], server.NATIVE, full=True)
        self.products.update({PATH_A: "Target after", PATH_B: "Other after"})
        self.kernel.CreateFileW.reset_mock()
        selected = server.physical_topology([PATH_A, PATH_B], server.public_id("physical:" + PATH_A))
        self.assertEqual(selected[0]["product_string"], "Target after")
        self.kernel.CreateFileW.assert_called_once_with(PATH_A, 0, 3, None, 3, 0, None)
        self.kernel.CreateFileW.reset_mock()
        full = topology.discover([PATH_A, PATH_B], server.NATIVE, full=True)
        self.assertEqual([info["product_string"] for info in full], ["Target after", "Other before"])
        self.kernel.CreateFileW.assert_not_called()

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

    def test_partial_discovery_reopens_target_without_clearing_other_cached_paths(self):
        self.scan([PATH_A, PATH_B])
        self.kernel.CreateFileW.reset_mock()
        self.scan([PATH_A], full=False)
        self.scan([PATH_A], full=False)
        self.assertEqual([call.args[0] for call in self.kernel.CreateFileW.call_args_list], [PATH_A, PATH_A])
        self.kernel.CreateFileW.reset_mock()
        self.scan([PATH_A, PATH_B])
        self.kernel.CreateFileW.assert_not_called()

    def test_full_topology_change_removes_stale_paths_and_refreshes_survivors(self):
        self.scan([PATH_A, PATH_B])
        self.kernel.CreateFileW.reset_mock()
        self.scan([PATH_A])
        self.kernel.CreateFileW.assert_called_once_with(PATH_A, 0, 3, None, 3, 0, None)
        self.assertNotIn(PATH_B, self.cache)
        self.kernel.CreateFileW.reset_mock()
        self.scan([PATH_A])
        self.kernel.CreateFileW.assert_not_called()
        self.scan([PATH_B], full=False)
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
        self.scan(paths[512:], full=False)
        self.assertLessEqual(len(self.cache), 512)
        self.kernel.CreateFileW.reset_mock()
        self.scan(paths[:512])
        self.kernel.CreateFileW.assert_not_called()
        self.scan(paths[512:], full=False)
        self.kernel.CreateFileW.assert_called_once()

    def test_full_path_change_outside_selected_512_paths_invalidates_cache(self):
        paths = [PATH_A + str(index).zfill(4) for index in range(513)]
        self.scan(paths[:512])
        self.products[paths[0]] = "Changed product"
        self.kernel.CreateFileW.reset_mock()
        result = self.scan(paths)
        self.assertEqual(result[0]["product_string"], "Changed product")
        self.assertEqual([call.args[0] for call in self.kernel.CreateFileW.call_args_list], paths[:512])
        self.assertLessEqual(len(self.cache), 512)

    def test_partial_failure_invalidates_only_selected_path(self):
        for failure_name in ("fail_open", "empty_product"):
            with self.subTest(failure=failure_name):
                self.cache.clear()
                self.products[PATH_A] = "Before"
                self.scan([PATH_A, PATH_B])
                self.products[PATH_A] = "Recovered"
                failure = getattr(self, failure_name)
                failure.add(PATH_A)
                self.scan([PATH_A], full=False)
                failure.clear()
                self.assertNotIn(PATH_A, self.cache)
                self.assertIn(PATH_B, self.cache)
                self.kernel.CreateFileW.reset_mock()
                result = self.scan([PATH_A, PATH_B])
                self.assertEqual(result[0]["product_string"], "Recovered")
                self.kernel.CreateFileW.assert_called_once_with(PATH_A, 0, 3, None, 3, 0, None)

    def test_unavailable_full_topology_does_not_clear_cache(self):
        self.scan([PATH_A])
        for full in (True, False):
            with self.subTest(full=full):
                with self.assertRaises(RuntimeError):
                    self.scan(None, full=full)
                self.kernel.CreateFileW.reset_mock()
                self.scan([PATH_A])
                self.kernel.CreateFileW.assert_not_called()

    def test_reader_child_refreshes_partial_descriptors_and_invalidates_full_cache(self):
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
        self.assertEqual([call.args[0] for call in self.kernel.CreateFileW.call_args_list],
                         [PATH_A, PATH_B, PATH_A, PATH_B, PATH_A])
        self.kernel.CreateFileW.reset_mock()
        self.scan([PATH_A])
        self.kernel.CreateFileW.assert_not_called()
        self.scan([PATH_B], full=False)
        self.kernel.CreateFileW.assert_called_once()


if __name__ == "__main__":
    unittest.main()
