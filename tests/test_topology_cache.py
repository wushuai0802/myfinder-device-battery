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
from providers import audeze, rapoo, zhuaudio
from standardhid import Caps


PATH_A = r"\\?\hid#vid_388d&pid_0025&mi_01#fixture-a"
PATH_B = r"\\?\hid#vid_388d&pid_0025&mi_01#fixture-b"
ZG4_PATH = r"\\?\hid#vid_3654&pid_5055&mi_04#synthetic-zg4"


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

    def a5v2_descriptor(self, reports=None, *, maximum=None, collection=(0xFF01, 1),
                         identity=(0x3837, 0x100C), product="MCHOSE A5 V2 Ultra"):
        # Entirely synthetic Windows metadata; no target descriptor/capture.
        self.a5_path = r"\\?\hid#vid_3837&pid_100c&mi_02&col03#synthetic-a5v2"
        self.a5_cfu = r"\\?\hid#vid_3837&pid_100c&mi_02&col04#synthetic-a5v2"
        self.a5_parent = r"pnp:usb\vid_3837&pid_100c\synthetic-a5v2"
        reports = {17: 21} if reports is None else reports
        for path in (self.a5_path, self.a5_cfu):
            self.identities[path] = identity
            self.products[path] = product
        self.parent.side_effect = lambda path: self.a5_parent if "pid_100c" in path else "physical:" + path
        self.a5_collections = {self.a5_path: collection, self.a5_cfu: (0xFF0B, 0x104)}

        def caps(data, pointer):
            path = self.handles[data.value]
            if path in self.fail_caps:
                return 0
            value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
            value.page, value.usage = self.a5_collections.get(path, (0xFF1C, 0x92))
            value.input, value.output = 8, 32  # Not Feature lengths or gates.
            if self.a5_collections.get(path) == (0xFF01, 1):
                value.feature = max(reports.values(), default=0) if maximum is None else maximum
                value.fv = len(reports)
            return 0x00110000

        def fields(report_type, values, length, data):
            self.assertEqual(report_type, 2)
            for field, (report, size) in zip(values, reports.items()):
                field.report, field.bits, field.count = report, 8, size - 1
            return 0x00110000

        self.hid.HidP_GetCaps.side_effect = caps
        self.hid.HidP_GetValueCaps.side_effect = fields
        return [self.a5_path, self.a5_cfu]

    def assert_a5v2_refused(self, paths, *, full=True):
        from providers.mchose import MchoseProvider
        infos = self.scan(paths, full=full)
        self.assertFalse(any(d.get("a5v2_topology") for d in infos))
        with patch("providers.mchose.hid.device", side_effect=AssertionError("no device I/O")):
            self.assertIsNone(MchoseProvider()._pick_a5v2([
                d for d in infos if (d["vendor_id"], d["product_id"]) == (0x3837, 0x100C)]))
        return infos

    def test_a5v2_feature_type2_exact_lengths_multiple_ids_and_different_directions(self):
        from providers.mchose import MchoseProvider
        for size in (21, 65):
            with self.subTest(size=size), patch.object(topology, "_read_report_descriptor") as raw:
                self.cache.clear()
                paths = self.a5v2_descriptor({17: size, 18: 13}, product="  MCHOSE A5 V2 Ultra  ")
                infos = self.scan(paths)
                info = MchoseProvider()._pick_a5v2(infos)
                self.assertIsNotNone(info)
                self.assertEqual((info["feature_report_length"], info["feature_report_sizes"]),
                                 (size, {"17": size, "18": 13}))
                self.assertEqual((info["input_report_length"], info["output_report_length"]), (8, 32))
                self.assertEqual(info["a5v2_topology"],
                                 {"paths": sorted(paths), "parent": self.a5_parent, "collection": self.a5_path})
                self.assertTrue(all(call.args[0] == 2 for call in self.hid.HidP_GetValueCaps.call_args_list))
                raw.assert_not_called()
        self.hid.HidD_GetFeature.assert_not_called()
        self.hid.HidD_SetFeature.assert_not_called()
        self.kernel.ReadFile.assert_not_called()
        self.kernel.WriteFile.assert_not_called()
        self.assertTrue(all(call.args[1] == 0 for call in self.kernel.CreateFileW.call_args_list))
        self.assertEqual(self.kernel.CloseHandle.call_count, self.kernel.CreateFileW.call_count)

    def test_a5v2_missing_id_l_below_f_or_wrong_maximum_stays_uncached(self):
        for reports, maximum in (({18: 21}, 21), ({17: 21, 18: 65}, 65),
                                 ({17: 21}, 65), ({17: 20}, 20), ({17: 66}, 66), ({}, 21)):
            with self.subTest(reports=reports, maximum=maximum):
                paths = self.a5v2_descriptor(reports, maximum=maximum)
                self.assert_a5v2_refused(paths)
                self.assertNotIn(self.a5_path, self.cache)

    def test_a5v2_bad_feature_caps_fail_closed_then_retry_without_raw_fallback(self):
        for defect in ("failure", "partial", "extra", "alias", "bits", "count", "split", "buttons", "fixed"):
            with self.subTest(defect=defect), patch.object(topology, "_read_report_descriptor") as raw:
                paths = self.a5v2_descriptor({17: 21, 18: 13})
                original = self.hid.HidP_GetValueCaps.side_effect
                original_caps = self.hid.HidP_GetCaps.side_effect

                def caps(data, pointer):
                    result = original_caps(data, pointer)
                    value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
                    if self.handles[data.value] == self.a5_path:
                        if defect == "buttons":
                            value.fb = 1
                        elif defect == "fixed":
                            value.fb, value.fv = 1, 0
                    return result

                def fields(report_type, values, length, data):
                    result = original(report_type, values, length, data)
                    if defect == "failure":
                        return 0
                    if defect in ("partial", "extra"):
                        ctypes.cast(length, ctypes.POINTER(ctypes.c_ushort))[0] = 1 if defect == "partial" else 3
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
                # Fixed usage must not opt into the generic helper's exception.
                def buttons(kind, values, length, data):
                    values[0].report, values[0].report_count = 17, 20
                    values[0].usage_min = values[0].usage_max = 1
                    return 0x00110000
                self.hid.HidP_GetButtonCaps.side_effect = buttons
                self.assert_a5v2_refused(paths)
                self.assertNotIn(self.a5_path, self.cache)
                paths = self.a5v2_descriptor()
                infos = self.scan(paths)
                self.assertTrue(infos[0].get("a5v2_topology"))
                raw.assert_not_called()

    def test_a5v2_snapshot_failures_cannot_hide_behind_one_returned_ff01(self):
        for full in (True, False):
            for failure in (self.fail_open, self.fail_attrs, self.fail_preparsed, self.fail_caps):
                with self.subTest(full=full, failure=id(failure)):
                    paths = self.a5v2_descriptor()
                    self.scan([PATH_A])
                    unrelated = self.cache[PATH_A]
                    self.scan(paths + [PATH_A], full=full)
                    self.kernel.CreateFileW.reset_mock()
                    failure.add(self.a5_cfu)
                    self.assert_a5v2_refused(paths + [PATH_A], full=full)
                    self.assertNotIn(self.a5_path, self.cache)
                    # Target failures do not invalidate a different cached device.
                    self.assertEqual(self.cache[PATH_A], unrelated)
                    if full:
                        self.assertNotIn(PATH_A, [call.args[0] for call in self.kernel.CreateFileW.call_args_list])
                    failure.clear()
                    infos = self.scan(paths + [PATH_A], full=full)
                    self.assertTrue(next(i for i in infos if i["path"] == self.a5_path.encode()).get("a5v2_topology"))

    def test_a5v2_cached_candidate_rechecks_unknown_usage_attrs_name_and_parent(self):
        for defect in ("usage", "attrs", "name", "collection_parent", "parent_conflict", "wrong_parent"):
            with self.subTest(defect=defect):
                paths = self.a5v2_descriptor()
                self.assertTrue(self.scan(paths)[0].get("a5v2_topology"))
                if defect == "usage":
                    self.a5_collections[self.a5_cfu] = (0, 0)
                elif defect == "attrs":
                    self.identities[self.a5_cfu] = (0x3837, 0x100B)
                elif defect == "name":
                    self.products[self.a5_cfu] = "MCHOSE A5 ultra"
                elif defect == "collection_parent":
                    self.parent.side_effect = lambda path: None
                elif defect == "parent_conflict":
                    self.parent.side_effect = lambda path: self.a5_parent + ("-other" if path == self.a5_cfu else "")
                else:
                    self.parent.side_effect = lambda path: self.a5_parent.replace("100c", "100b")
                self.assert_a5v2_refused(paths)

    def test_a5v2_duplicate_paths_tlcs_multiple_parents_and_512_truncation(self):
        for full in (True, False):
            for defect in ("duplicate", "case_duplicate", "tlc", "parents", "truncated", "target_truncated"):
                with self.subTest(full=full, defect=defect):
                    paths = self.a5v2_descriptor()
                    if defect == "duplicate":
                        paths.append(paths[0])
                    elif defect == "case_duplicate":
                        paths.append(paths[0].upper())
                    elif defect in ("tlc", "parents"):
                        other = self.a5_path + "-other"
                        paths.append(other)
                        self.identities[other] = (0x3837, 0x100C)
                        self.products[other] = "MCHOSE A5 V2 Ultra"
                        self.a5_collections[other] = (0xFF01, 1)
                        if defect == "parents":
                            self.parent.side_effect = lambda path: self.a5_parent + ("-other" if path == other else "")
                    elif defect == "truncated":
                        paths += [f"zz-unused-{i}" for i in range(511)]
                    else:
                        paths += [f"000-unused-{i}" for i in range(513)]
                    self.assert_a5v2_refused(paths, full=full)

    def test_a5v2_conflicting_sibling_parent_or_path_attrs_cannot_authorize(self):
        for defect in ("extra_sibling", "path_mismatch"):
            with self.subTest(defect=defect):
                paths = self.a5v2_descriptor()
                if defect == "extra_sibling":
                    # An otherwise non-target identity under the target USB
                    # parent is contradictory evidence, not a second device.
                    paths.append(PATH_A)
                    self.parent.side_effect = lambda path: self.a5_parent
                else:
                    wrong = self.a5_path.replace("100c", "100b")
                    self.identities[wrong] = (0x3837, 0x100C)
                    self.products[wrong] = "MCHOSE A5 V2 Ultra"
                    self.a5_collections[wrong] = (0xFF01, 1)
                    paths[0] = wrong
                self.assert_a5v2_refused(paths)

    def test_a5v2_plain_non_windows_inventory_has_no_new_proof_and_exclusions_still_win(self):
        paths = self.a5v2_descriptor()
        plain = [{"vendor_id": 0x3837, "product_id": 0x100C, "path": paths[0].encode(),
                  "product_string": "MCHOSE A5 V2 Ultra", "usage_page": 0xFF01, "usage": 1}]
        from providers.mchose import MchoseProvider
        with patch.object(topology, "os", SimpleNamespace(name="posix")), \
             patch("providers.hidlist.enumerate", return_value=plain):
            infos = topology.discover(paths)
            self.assertIsNone(MchoseProvider()._pick_a5v2(infos))
        self.assertFalse(any(d.get("a5v2_topology") for d in infos))
        self.kernel.CreateFileW.assert_not_called()
        self.assertEqual(topology.discover(paths, excluded=((0x3837, 0x100C),), full=True), [])
        self.kernel.CreateFileW.assert_not_called()

    def test_a5v2_proof_is_recomputed_and_never_reused_from_descriptor_cache(self):
        for full in (True, False):
            with self.subTest(full=full):
                paths = self.a5v2_descriptor()
                first = self.scan(paths, full=full)
                self.assertTrue(first[0].get("a5v2_topology"))
                self.assertFalse(any(d.get("a5v2_topology") for d in self.cache.values()))
                self.fail_attrs.add(self.a5_cfu)
                self.assert_a5v2_refused(paths, full=full)
                self.fail_attrs.clear()
                recovered = self.scan(paths, full=full)
                self.assertTrue(recovered[0].get("a5v2_topology"))
                first[0]["a5v2_topology"]["paths"].clear()
                self.assertEqual(recovered[0]["a5v2_topology"]["paths"], sorted(paths))

    def test_a5v2_proof_scope_and_native_preopen_exclusion_do_not_expand(self):
        for identity, collection, name in (((0x3837, 0x100B), (0xFF01, 1), "MCHOSE A5 V2 Ultra"),
                                          ((0x5253, 0x100C), (0xFF01, 1), "MCHOSE A5 V2 Ultra"),
                                          ((0x3837, 0x100C), (0xFF01, 1), "MCHOSE A5 ultra"),
                                          ((0x3837, 0x100C), (0xFF01, 2), "MCHOSE A5 V2 Ultra"),
                                          ((0x3837, 0x100C), (0xFF0B, 0x104), "MCHOSE A5 V2 Ultra")):
            with self.subTest(identity=identity, collection=collection, name=name):
                self.hid.HidP_GetValueCaps.reset_mock()
                paths = self.a5v2_descriptor(identity=identity, collection=collection, product=name)
                self.assert_a5v2_refused(paths)
                self.hid.HidP_GetValueCaps.assert_not_called()
        self.kernel.CreateFileW.reset_mock()
        native = r"\\?\hid#vid_373b&pid_1278&mi_01#native"
        self.scan([native])
        self.kernel.CreateFileW.assert_not_called()

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

    def cherry_descriptor(self, *, identity=(0x046A, 0x01E0), collection=(0xFF1C, 0x0092),
                          input_buttons=(4,), input_values=(), output_buttons=(4,), output_values=(),
                          lengths=(64, 64), counts=None):
        self.identities[PATH_A] = identity
        self.products[PATH_A] = "MX 2.0S Pro Dongle" if identity[1] == 0x01E0 else "MX 2.0S WL Pro"
        reports = {"ib": input_buttons, "iv": input_values, "ob": output_buttons, "ov": output_values}

        def caps(data, pointer):
            value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
            value.page, value.usage = collection
            value.input, value.output = lengths
            for field, ids in reports.items():
                setattr(value, field, (counts or {}).get(field, len(ids)))
            return 0x00110000

        def fields(kind, report_type, values, length, data):
            key = ("i" if report_type == 0 else "o") + kind
            for value, report_id in zip(values, reports[key]):
                value.report = report_id
            return 0x00110000

        self.hid.HidP_GetCaps.side_effect = caps
        self.hid.HidP_GetButtonCaps.side_effect = lambda *args: fields("b", *args)
        self.hid.HidP_GetValueCaps.side_effect = lambda *args: fields("v", *args)

    def assert_cherry_descriptor_only(self):
        self.assertTrue(all(call.args[1] == 0 for call in self.kernel.CreateFileW.call_args_list))
        self.hid.HidD_GetFeature.assert_not_called()
        self.hid.HidD_SetFeature.assert_not_called()
        self.kernel.ReadFile.assert_not_called()
        self.kernel.WriteFile.assert_not_called()
        self.assertEqual(self.kernel.CloseHandle.call_count, self.kernel.CreateFileW.call_count)
        self.assertEqual(self.hid.HidD_FreePreparsedData.call_count, self.kernel.CreateFileW.call_count)

    def test_cherry_both_pids_all_button_reports_are_proven_and_cached_without_io(self):
        for pid in (0x01E0, 0x01DE):
            with self.subTest(pid=pid), patch.object(topology, "_read_report_descriptor") as raw:
                self.cache.clear()
                self.cherry_descriptor(identity=(0x046A, pid))
                before = self.kernel.CreateFileW.call_count
                first, = self.scan([PATH_A])
                second, = self.scan([PATH_A])
                self.assertEqual(first, second)
                self.assertEqual(first["input_report_sizes"], {"4": 64})
                self.assertEqual(first["output_report_sizes"], {"4": 64})
                self.assertEqual(self.kernel.CreateFileW.call_count, before + 1)
                self.assertEqual({call.args[0] for call in self.hid.HidP_GetButtonCaps.call_args_list}, {0, 1})
                self.hid.HidP_GetValueCaps.assert_not_called()
                raw.assert_not_called()
        self.assert_cherry_descriptor_only()

    def test_cherry_mixed_button_value_caps_enumerate_every_field_in_both_directions(self):
        self.cherry_descriptor(input_buttons=(4, 4), input_values=(4, 4),
                               output_buttons=(4, 4), output_values=(4, 4))
        first, = self.scan([PATH_A])
        self.assertEqual(first["input_report_sizes"], {"4": 64})
        self.assertEqual(first["output_report_sizes"], {"4": 64})
        for getter in (self.hid.HidP_GetButtonCaps, self.hid.HidP_GetValueCaps):
            self.assertEqual({call.args[0] for call in getter.call_args_list}, {0, 1})
            self.assertTrue(all(len(call.args[1]) == 2 for call in getter.call_args_list))
        self.assert_cherry_descriptor_only()

    def test_cherry_multiple_or_wrong_ids_and_wrong_maxima_never_prove_or_cache(self):
        cases = [dict(input_buttons=(4, 5)), dict(output_buttons=(4, 5)),
                 dict(input_buttons=(5,)), dict(output_buttons=(5,)),
                 dict(input_values=(5,)), dict(output_values=(5,))]
        cases += [dict(lengths=lengths) for lengths in ((63, 64), (65, 64), (64, 63), (64, 65))]
        for changes in cases:
            with self.subTest(changes=changes), patch.object(topology, "_read_report_descriptor") as raw:
                self.cache.clear()
                self.cherry_descriptor(**changes)
                info, = self.scan([PATH_A])
                self.assertTrue(info["input_report_sizes"] is None or info["output_report_sizes"] is None)
                self.assertNotIn(PATH_A, self.cache)
                raw.assert_not_called()
        self.assert_cherry_descriptor_only()

    def test_cherry_each_direction_button_value_api_failure_or_short_count_retries_and_recovers(self):
        for name in ("HidP_GetButtonCaps", "HidP_GetValueCaps"):
            for direction in (0, 1):
                for failure in ("status", "short"):
                    with self.subTest(name=name, direction=direction, failure=failure):
                        self.cache.clear()
                        self.cherry_descriptor(input_values=(4,), output_values=(4,))
                        getter = getattr(self.hid, name)
                        original = getter.side_effect

                        def fail(report_type, values, length, data):
                            result = original(report_type, values, length, data)
                            if report_type == direction:
                                if failure == "status":
                                    return 0
                                ctypes.cast(length, ctypes.POINTER(ctypes.c_ushort))[0] = 0
                            return result

                        getter.side_effect = fail
                        before = self.kernel.CreateFileW.call_count
                        info, = self.scan([PATH_A])
                        key = ("input" if direction == 0 else "output") + "_report_sizes"
                        self.assertIsNone(info[key])
                        self.assertNotIn(PATH_A, self.cache)
                        getter.side_effect = original
                        recovered, = self.scan([PATH_A])
                        self.assertEqual(recovered[key], {"4": 64})
                        self.assertIn(PATH_A, self.cache)
                        self.assertEqual(self.kernel.CreateFileW.call_count, before + 2)
        self.assert_cherry_descriptor_only()

    def test_cherry_zero_or_over_512_caps_stay_unproven_without_partial_api_guess(self):
        for direction in ("i", "o"):
            for count in (0, 513):
                with self.subTest(direction=direction, count=count):
                    self.cache.clear()
                    self.hid.HidP_GetButtonCaps.reset_mock()
                    self.hid.HidP_GetValueCaps.reset_mock()
                    self.cherry_descriptor(counts={direction + "b": count})
                    info, = self.scan([PATH_A])
                    self.assertIsNone(info[("input" if direction == "i" else "output") + "_report_sizes"])
                    self.assertNotIn(PATH_A, self.cache)
                    self.assertNotIn(0 if direction == "i" else 1,
                                     [call.args[0] for call in self.hid.HidP_GetButtonCaps.call_args_list])
                    self.hid.HidP_GetValueCaps.assert_not_called()
        self.assert_cherry_descriptor_only()

    def test_cherry_caps_proof_does_not_expand_vid_pid_usage_or_ble_and_keeps_native_excluded(self):
        cases = [dict(identity=(0x0687, 0x01DE)), dict(identity=(0x1234, 0x01E0)),
                 dict(identity=(0x046A, 0xFFFF)), dict(collection=(1, 0x0092)),
                 dict(collection=(0xFF1C, 1))]
        for changes in cases:
            with self.subTest(changes=changes):
                self.cache.clear()
                self.cherry_descriptor(**changes)
                info, = self.scan([PATH_A])
                self.assertNotIn("input_report_sizes", info)
                self.assertNotIn("output_report_sizes", info)
                self.hid.HidP_GetButtonCaps.assert_not_called()
                self.hid.HidP_GetValueCaps.assert_not_called()
        self.assert_cherry_descriptor_only()
        before = self.kernel.CreateFileW.call_count
        self.assertEqual(self.scan([r"\\?\hid#vid_373b&pid_1278&mi_01#native"]), [])
        self.assertEqual(self.kernel.CreateFileW.call_count, before)

    def zg4_descriptor(self, *, identity=(0x3654, 0x5055), collection=(0xFF05, 1),
                       mode="button", ids=(0xCE, 0xCD), lengths=(64, 64), field_changes=None):
        self.identities[ZG4_PATH] = identity
        self.products[ZG4_PATH] = "ZhuAudio ZG4"

        def caps(data, pointer):
            value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
            value.page, value.usage = collection
            value.input, value.output = lengths
            if mode == "button":
                value.ib = value.ob = 1
            else:
                value.iv = value.ov = 1
            return 0x00110000

        def fields(report_type, values, length, data):
            field = values[0]
            field.report = ids[report_type]
            if mode == "button":
                field.report_count = 63
                field.usage_min = field.usage_max = 2 + report_type
            else:
                field.bits, field.count = 8, 63
            for key, value in (field_changes or {}).items():
                setattr(field, key, value)
            return 0x00110000

        self.hid.HidP_GetCaps.side_effect = caps
        self.hid.HidP_GetButtonCaps.side_effect = fields
        self.hid.HidP_GetValueCaps.side_effect = fields

    def test_zg4_fixed_usage_array_ce_cd_is_metadata_only_and_cached(self):
        for mode in ("button", "value"):
            with self.subTest(mode=mode), patch.object(topology, "_read_report_descriptor") as raw:
                self.cache.clear()
                self.zg4_descriptor(mode=mode)
                first, = self.scan([ZG4_PATH])
                before = self.kernel.CreateFileW.call_count
                second, = self.scan([ZG4_PATH])
                self.assertEqual(first, second)
                self.assertEqual(self.kernel.CreateFileW.call_count, before)
                self.assertEqual(first["input_report_sizes"], {"206": 64})
                self.assertEqual(first["output_report_sizes"], {"205": 64})
                self.assertEqual((first["input_report_proof"], first["output_report_proof"]), ("caps", "caps"))
                self.assertEqual(zhuaudio.ZhuAudioProvider()._pick([first]), first)
                raw.assert_not_called()
        self.assert_cherry_descriptor_only()

    def test_zg4_failed_or_partial_caps_in_each_direction_are_not_cached_and_recover(self):
        for mode, getter in (("button", "HidP_GetButtonCaps"), ("value", "HidP_GetValueCaps")):
            for direction in (0, 1):
                for failure in ("status", "partial"):
                    with self.subTest(mode=mode, direction=direction, failure=failure), \
                            patch.object(topology, "_read_report_descriptor") as raw:
                        self.cache.clear()
                        self.zg4_descriptor(mode=mode)
                        fn = getattr(self.hid, getter)
                        original = fn.side_effect

                        def fail(report_type, values, length, data):
                            result = original(report_type, values, length, data)
                            if report_type == direction:
                                if failure == "status":
                                    return 0
                                ctypes.cast(length, ctypes.POINTER(ctypes.c_ushort))[0] = 0
                            return result

                        fn.side_effect = fail
                        first, = self.scan([ZG4_PATH])
                        key = ("input" if direction == 0 else "output") + "_report_sizes"
                        self.assertIsNone(first[key])
                        self.assertNotIn(ZG4_PATH, self.cache)
                        fn.side_effect = original
                        before = self.kernel.CreateFileW.call_count
                        recovered, = self.scan([ZG4_PATH])
                        self.assertEqual(recovered[key], {str((0xCE, 0xCD)[direction]): 64})
                        self.assertEqual(self.kernel.CreateFileW.call_count, before + 1)
                        self.assertIn(ZG4_PATH, self.cache)
                        raw.assert_not_called()
        self.assert_cherry_descriptor_only()

    def test_zg4_wrong_ids_maxima_and_non_byte_fields_do_not_become_cached_proof(self):
        cases = [dict(ids=(0xCD, 0xCE)), dict(ids=(0, 0)), dict(ids=(0xC0, 0xC0)),
                 dict(lengths=(63, 64)), dict(lengths=(64, 65)),
                 dict(field_changes={"report_count": 62}), dict(field_changes={"field": 2}),
                 dict(field_changes={"alias": 1}), dict(field_changes={"is_range": 1}),
                 dict(mode="value", field_changes={"bits": 16}),
                 dict(mode="value", field_changes={"count": 62})]
        for changes in cases:
            with self.subTest(changes=changes), patch.object(topology, "_read_report_descriptor") as raw:
                self.cache.clear()
                self.zg4_descriptor(**changes)
                self.scan([ZG4_PATH])
                self.assertNotIn(ZG4_PATH, self.cache)
                raw.assert_not_called()
        self.assert_cherry_descriptor_only()

    def test_zg4_metadata_opt_in_never_expands_to_ff02_other_pid_vendor_or_native(self):
        for changes in (dict(identity=(0x3654, 0x5054)), dict(identity=(0x1234, 0x5055)),
                        dict(collection=(0xFF02, 2)), dict(collection=(0xFF05, 2)),
                        dict(collection=(1, 1))):
            with self.subTest(changes=changes), patch.object(topology, "_read_report_descriptor") as raw:
                self.cache.clear()
                self.zg4_descriptor(**changes)
                info, = self.scan([ZG4_PATH])
                self.assertNotIn("input_report_sizes", info)
                self.assertNotIn("output_report_sizes", info)
                self.hid.HidP_GetButtonCaps.assert_not_called()
                self.hid.HidP_GetValueCaps.assert_not_called()
                raw.assert_not_called()
        self.assert_cherry_descriptor_only()
        before = self.kernel.CreateFileW.call_count
        self.assertEqual(self.scan([r"\\?\hid#vid_373b&pid_1278&mi_01#native"]), [])
        self.assertEqual(self.kernel.CreateFileW.call_count, before)

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
                 ((0x3554, 0xFA09), (0xFF02, 2), {19: 20}, {19: 20}),
                 ((0x41E4, 0x2001), (0xFF02, 2), {19: 20}, {19: 20}),
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

    def test_aula_feature_proof_is_cached_without_commands_but_only_by_is_eligible(self):
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
                self.assertEqual(provider._pick([info]), info if by else None)
                if not by:
                    self.assertIn("Feature06 battery path disabled: field does not track battery; nothing sent",
                                  provider.diagnostics())
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
                self.assertEqual(recovered["feature_report_sizes"], {"6": 8})
                self.assertIn(PATH_A, self.cache)
                self.assertIsNone(MchoseByKeyboardProvider()._pick([recovered]))

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

    def test_v9_pro_firmware_feature_proof_is_passive_scoped_and_retried(self):
        for identity, collection, eligible in (
                ((0x291D, 0x385D), (0xFF22, 1), True),
                ((0x291D, 0x385D), (0xFF82, 1), False),
                ((0x291D, 0x385D), (0xFF22, 2), False),
                ((0x3837, 0x600A), (0xFF22, 1), False)):
            with self.subTest(identity=identity, collection=collection):
                self.cache.clear()
                self.identities[PATH_A] = identity

                def caps(data, pointer):
                    value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
                    value.page, value.usage = collection
                    value.feature, value.fv = 64, 1
                    return 0x00110000

                def fields(report_type, values, length, data):
                    self.assertEqual(report_type, 2)
                    values[0].report, values[0].bits, values[0].count = 0xAA, 8, 63
                    return 0x00110000

                self.hid.HidP_GetCaps.side_effect = caps
                self.hid.HidP_GetValueCaps.side_effect = lambda *args: 0
                first, = self.scan([PATH_A])
                if eligible:
                    self.assertIsNone(first["feature_report_sizes"])
                    self.assertNotIn(PATH_A, self.cache)
                    self.hid.HidP_GetValueCaps.side_effect = fields
                    recovered, = self.scan([PATH_A])
                    self.assertEqual(recovered["feature_report_sizes"], {"170": 64})
                    self.kernel.CreateFileW.reset_mock()
                    self.assertEqual(self.scan([PATH_A]), [recovered])
                    self.kernel.CreateFileW.assert_not_called()
                else:
                    self.assertNotIn("feature_report_sizes", first)
        self.hid.HidD_GetFeature.assert_not_called()
        self.hid.HidD_SetFeature.assert_not_called()
        self.kernel.ReadFile.assert_not_called()
        self.kernel.WriteFile.assert_not_called()

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
                                     ((0x41E4, 0x2001), (0xFF02, 1)),
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


class LingbaoDescriptorTests(unittest.TestCase):
    """Exact #171 Feature proof using full fake caps and access-zero handles."""
    def setUp(self):
        self.case = DescriptorCacheTests()
        self.case.setUp()
        self.addCleanup(self.case.doCleanups)
        raw = patch.object(topology, "_read_report_descriptor")
        self.raw = raw.start()
        self.addCleanup(raw.stop)

    def configure(self, *, identity=(0x088D, 0x052F), collection=(0xFF00, 1),
                  product="Wireless-Receiver", path=PATH_A, maximum=33,
                  buttons=None, values=(), counts=None):
        self.case.cache.clear()
        self.path = path
        self.case.identities[path] = identity
        self.case.products[path] = product
        buttons = ((dict(report=15, usage_min=1, usage_max=1, report_count=32),)
                   if buttons is None else buttons)

        def caps(data, pointer):
            if self.case.handles[data.value] in self.case.fail_caps:
                return 0
            value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
            value.page, value.usage = collection
            value.feature = maximum
            value.fb, value.fv = (counts or {}).get("fb", len(buttons)), (counts or {}).get("fv", len(values))
            return 0x00110000

        def fields(definitions, report_type, fields, length, data):
            self.assertEqual(report_type, 2)
            for field, definition in zip(fields, definitions):
                for key, value in definition.items():
                    setattr(field, key, value)
            return 0x00110000

        self.case.hid.HidP_GetCaps.side_effect = caps
        self.case.hid.HidP_GetButtonCaps.side_effect = lambda *args: fields(buttons, *args)
        self.case.hid.HidP_GetValueCaps.side_effect = lambda *args: fields(values, *args)
        self.case.hid.HidP_GetButtonCaps.reset_mock()
        self.case.hid.HidP_GetValueCaps.reset_mock()

    def scan(self):
        result, = self.case.scan([self.path])
        self.assertTrue(all(call.args[1] == 0 for call in self.case.kernel.CreateFileW.call_args_list))
        self.case.hid.HidD_GetFeature.assert_not_called()
        self.case.hid.HidD_SetFeature.assert_not_called()
        self.case.kernel.ReadFile.assert_not_called()
        self.case.kernel.WriteFile.assert_not_called()
        self.raw.assert_not_called()
        self.assertEqual(self.case.kernel.CloseHandle.call_count, self.case.kernel.CreateFileW.call_count)
        self.assertLessEqual(self.case.hid.HidD_FreePreparsedData.call_count, self.case.kernel.CreateFileW.call_count)
        return result

    def assert_refused(self, info):
        import test_lingbao as F
        with F.fixture([F.CAPTURES[0]], [info]) as bus:
            self.assertEqual(F.P().poll(), [])
            self.assertEqual(bus.calls, [])

    def test_lingbao_fixed_usage_and_value_arrays_prove_exact_report_and_cache(self):
        cases = [dict(), dict(buttons=(), values=(dict(report=15, bits=8, count=32),)),
                 dict(buttons=(dict(report=15, is_range=1, usage_min=0, usage_max=255),))]
        for changes in cases:
            with self.subTest(changes=changes):
                self.configure(**changes)
                before = self.case.kernel.CreateFileW.call_count
                first = self.scan()
                self.assertEqual(first["feature_report_length"], 33)
                self.assertEqual(first["feature_report_sizes"], {"15": 33})
                self.assertEqual(self.scan(), first)
                self.assertEqual(self.case.kernel.CreateFileW.call_count, before + 1)
                self.assertIn(self.path, self.case.cache)
                calls = (self.case.hid.HidP_GetButtonCaps.call_args_list
                         + self.case.hid.HidP_GetValueCaps.call_args_list)
                self.assertTrue(calls)
                self.assertEqual({call.args[0] for call in calls}, {2})
                self.assertTrue(all(len(call.args[1]) == 1 for call in calls))
                import test_lingbao as F
                with F.fixture([F.CAPTURES[0]], [first]) as bus:
                    status, = F.P().poll()
                    self.assertEqual(status.level, 65)
                    self.assertEqual(bus.feature_reads, [(first["path"], 15, 33)])

    def test_lingbao_unknown_partial_and_failed_feature_caps_retry_then_recover(self):
        for mode in ("button", "value"):
            for failure in ("status", "short", "extra"):
                with self.subTest(mode=mode, failure=failure):
                    changes = {} if mode == "button" else dict(buttons=(), values=(dict(report=15, bits=8, count=32),))
                    self.configure(**changes)
                    getter = getattr(self.case.hid, "HidP_GetButtonCaps" if mode == "button" else "HidP_GetValueCaps")
                    original = getter.side_effect

                    def fail(report_type, fields, length, data):
                        result = original(report_type, fields, length, data)
                        if failure == "status":
                            return 0
                        ctypes.cast(length, ctypes.POINTER(ctypes.c_ushort))[0] = 0 if failure == "short" else 2
                        return result

                    getter.side_effect = fail
                    before = self.case.kernel.CreateFileW.call_count
                    failed = self.scan()
                    self.assertIsNone(failed["feature_report_sizes"])
                    self.assertNotIn(self.path, self.case.cache)
                    self.assert_refused(failed)
                    getter.side_effect = original
                    self.assertEqual(self.scan()["feature_report_sizes"], {"15": 33})
                    self.assertIn(self.path, self.case.cache)
                    self.assertEqual(self.case.kernel.CreateFileW.call_count, before + 2)

    def test_lingbao_complete_fixed_usage_field_validation_is_not_bypassed(self):
        base = dict(report=15, usage_min=1, usage_max=1, report_count=32)
        for field, value in (("alias", 1), ("field", 1), ("field", 2), ("is_range", 1),
                             ("usage_max", 2), ("report_count", 31), ("report_count", 33)):
            with self.subTest(field=field, value=value):
                self.configure(buttons=(dict(base, **{field: value}),))
                info = self.scan()
                self.assertIsNone(info["feature_report_sizes"])
                self.assertNotIn(self.path, self.case.cache)
                self.assert_refused(info)

    def test_lingbao_value_fields_wrong_report_maps_counts_and_maxima_never_authorize(self):
        cases = [dict(buttons=(), values=(dict(report=15, bits=7, count=32),)),
                 dict(buttons=(), values=(dict(report=15, bits=8, count=32, alias=1),)),
                 dict(buttons=(), values=(dict(report=15, bits=8, count=0),)),
                 dict(buttons=(), values=(dict(report=14, bits=8, count=32),)),
                 dict(buttons=(), values=(dict(report=15, bits=8, count=32), dict(report=16, bits=8, count=1))),
                 dict(buttons=(), values=(dict(report=15, bits=8, count=32), dict(report=15, bits=8, count=1))),
                 dict(values=(dict(report=15, bits=8, count=32),)),
                 dict(counts={"fb": 2}), dict(counts={"fb": 0}),
                 dict(buttons=(), counts={"fv": 33})]
        cases += [dict(maximum=n) for n in (0, 32, 34, 64)]
        for changes in cases:
            with self.subTest(changes=changes):
                self.configure(**changes)
                info = self.scan()
                self.assertNotIn(self.path, self.case.cache)
                self.assert_refused(info)

    def test_lingbao_metadata_proof_does_not_expand_identity_product_interface_or_usage(self):
        cases = [dict(identity=(0x088D, 0x0530)), dict(identity=(0x089D, 0x052F)),
                 dict(identity=(0x1234, 0x5678)), dict(collection=(1, 1)),
                 dict(collection=(0xFF00, 2)), dict(product="Wireless-Receiver "),
                 dict(product=""), dict(path=PATH_A.replace("&mi_01", "&mi_02")),
                 dict(path=PATH_A.replace("&mi_01", ""))]
        for changes in cases:
            with self.subTest(changes=changes):
                self.configure(**changes)
                info = self.scan()
                self.assertNotIn("feature_report_sizes", info)
                self.assertNotIn("feature_report_length", info)
                self.case.hid.HidP_GetButtonCaps.assert_not_called()
                self.case.hid.HidP_GetValueCaps.assert_not_called()
                self.assert_refused(info)

    def test_lingbao_preparsed_and_caps_failure_stay_uncached_and_recover(self):
        for name in ("fail_preparsed", "fail_caps"):
            with self.subTest(name=name):
                self.configure()
                failure = getattr(self.case, name)
                failure.add(self.path)
                info = self.scan()
                self.assertNotIn("feature_report_sizes", info)
                self.assertNotIn(self.path, self.case.cache)
                self.assert_refused(info)
                failure.clear()
                self.assertEqual(self.scan()["feature_report_sizes"], {"15": 33})


class MchoseV3AlternateTopologyTests(unittest.TestCase):
    """Synthetic FF0B 4D layout, not a #493 descriptor or hardware capture."""
    descriptor = bytes.fromhex(
        "06 0b ff 09 c4 a1 01 85 4d 75 08 95 3f 81 02 91 02 c0")

    def setUp(self):
        import battery_hid as H
        self.case = DescriptorCacheTests()
        self.case.setUp()
        self.addCleanup(self.case.doCleanups)
        self.dev = Mock(spec=["open_path", "get_report_descriptor", "close"])
        self.backend = SimpleNamespace(device=Mock(return_value=self.dev))
        patcher = patch.object(H, "hid", self.backend)
        patcher.start()
        self.addCleanup(patcher.stop)

    def configure(self, failed=(0, 1), identity=(0x3837, 0x1018), collection=(0xFF0B, 0xC4),
                  inp=None, raw=None, maxima=None):
        self.case.cache.clear()
        self.dev.reset_mock(side_effect=True)
        self.backend.device.reset_mock()
        self.case.hid.HidP_GetValueCaps.reset_mock()
        self.case.batch3_descriptor(identity, collection, inp or {77: 64}, {77: 64})
        original_fields = self.case.hid.HidP_GetValueCaps.side_effect
        self.case.hid.HidP_GetValueCaps.side_effect = (
            lambda report_type, *args: 0 if report_type in failed else original_fields(report_type, *args))
        if maxima is not None:
            original_caps = self.case.hid.HidP_GetCaps.side_effect

            def caps(data, pointer):
                result = original_caps(data, pointer)
                value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
                value.input, value.output, value.feature = maxima
                return result

            self.case.hid.HidP_GetCaps.side_effect = caps
        self.dev.get_report_descriptor.return_value = self.descriptor if raw is None else raw

    def scan(self):
        info, = self.case.scan([PATH_A])
        self.case.hid.HidD_GetFeature.assert_not_called()
        self.case.hid.HidD_SetFeature.assert_not_called()
        self.case.kernel.ReadFile.assert_not_called()
        self.case.kernel.WriteFile.assert_not_called()
        return info

    def test_alternate_both_caps_and_each_missing_direction_are_proven_and_cached(self):
        for failed in ((), (0,), (1,), (0, 1)):
            with self.subTest(failed=failed):
                self.configure(failed=failed)
                info = self.scan()
                for index, direction in enumerate(("input", "output")):
                    self.assertEqual(info[direction + "_report_sizes"], {"77": 64})
                    self.assertEqual(info[direction + "_report_proof"],
                                     "descriptor" if index in failed else "caps")
                self.assertEqual(self.scan(), info)
                if failed:
                    self.dev.open_path.assert_called_once_with(PATH_A.encode())
                    self.dev.get_report_descriptor.assert_called_once_with()
                    self.dev.close.assert_called_once_with()
                else:
                    self.backend.device.assert_not_called()

    def test_alternate_unknown_or_partial_proof_retries_and_recovers(self):
        self.configure(raw=b"\x85")
        for _ in range(2):
            info = self.scan()
            self.assertIsNone(info["input_report_sizes"])
            self.assertIsNone(info["output_report_sizes"])
            self.assertEqual(info["report_descriptor_error"], "truncated item")
            self.assertNotIn(PATH_A, self.case.cache)
        self.dev.get_report_descriptor.return_value = self.descriptor
        recovered = self.scan()
        self.assertEqual(recovered["input_report_sizes"], {"77": 64})
        self.assertEqual(recovered["output_report_sizes"], {"77": 64})
        self.assertEqual(self.dev.close.call_count, 3)
        self.configure(failed=())
        original = self.case.hid.HidP_GetValueCaps.side_effect

        def partial(report_type, fields, length, data):
            result = original(report_type, fields, length, data)
            ctypes.cast(length, ctypes.POINTER(ctypes.c_ushort))[0] = 0
            return result

        self.case.hid.HidP_GetValueCaps.side_effect = partial
        recovered = self.scan()
        self.assertEqual(recovered["input_report_proof"], "descriptor")
        self.assertEqual(recovered["output_report_proof"], "descriptor")

    def test_alternate_non_64_caps_never_open_raw_descriptor_even_on_repeated_scans(self):
        for maxima in ((41, 41, 0), (41, 64, 0), (64, 41, 0), (0, 0, 0),
                       (0, 64, 0), (64, 0, 0), (63, 64, 0), (64, 65, 0)):
            with self.subTest(maxima=maxima):
                self.configure(maxima=maxima)
                for _ in range(2):
                    info = self.scan()
                    self.assertEqual((info["input_report_length"], info["output_report_length"]), maxima[:2])
                self.backend.device.assert_not_called()

    def test_alternate_fallback_rejects_conflicting_maxima_caps_duplicate_and_shared_tlcs(self):
        shared = self.descriptor + self.descriptor.replace(b"\x06\x0b\xff", b"\x06\x01\xff")
        cases = [(self.descriptor.replace(b"\x95\x3f", b"\x95\x28"),
                  None, (0, 1), "collection maximum mismatch"),
                 (self.descriptor[:-1] + bytes.fromhex("85 06 95 07 b1 02 c0"),
                  None, (0, 1), "collection maximum mismatch"),
                 (self.descriptor, {76: 64}, (1,), "caps/descriptor disagreement"),
                 (self.descriptor * 2, None, (0, 1), "duplicate top-level collection"),
                 (shared, None, (0, 1), "report shared across collections"),
                 (self.descriptor.replace(b"\x09\xc4", b"\x09\xc5"),
                  None, (0, 1), "collection not found")]
        for raw, inp, failed, reason in cases:
            with self.subTest(reason=reason):
                self.configure(raw=raw, inp=inp, failed=failed)
                info = self.scan()
                self.assertEqual(info["report_descriptor_error"], reason)
                self.assertIsNone(info["output_report_sizes"])
                self.assertNotIn(PATH_A, self.case.cache)
                self.dev.close.assert_called_once_with()

    def test_alternate_metadata_failures_close_and_do_not_expose_exceptions(self):
        for method in ("open_path", "get_report_descriptor", "close"):
            with self.subTest(method=method):
                self.configure()
                getattr(self.dev, method).side_effect = OSError("PRIVATE-SERIAL-PATH")
                info = self.scan()
                self.assertIsNone(info["input_report_sizes"])
                self.assertIsNone(info["output_report_sizes"])
                self.assertNotIn("PRIVATE", str(info))
                self.dev.close.assert_called_once_with()

    def test_alternate_does_not_expand_other_pid_vendor_or_usage(self):
        for identity, collection in (((0x3837, 0x1014), (0xFF0B, 0xC4)),
                                     ((0x3837, 0x1019), (0xFF0B, 0xC4)),
                                     ((0x3838, 0x1018), (0xFF0B, 0xC4)),
                                     ((0x5253, 0x1018), (0xFF0B, 0xC4)),
                                     ((0x3837, 0x1018), (0xFF0B, 0x104)),
                                     ((0x3837, 0x1018), (0xFF0B, 1)),
                                     ((0x3837, 0x1018), (0xFF02, 0xC4))):
            with self.subTest(identity=identity, collection=collection):
                self.configure(identity=identity, collection=collection)
                info = self.scan()
                self.assertNotIn("input_report_sizes", info)
                self.assertNotIn("output_report_sizes", info)
                self.case.hid.HidP_GetValueCaps.assert_not_called()
                self.backend.device.assert_not_called()

    def test_both_original_ff01_identities_keep_caps_and_fallback_proof(self):
        descriptor = self.descriptor.replace(b"\x06\x0b\xff\x09\xc4", b"\x06\x01\xff\x09\x01")
        for pid in (0x1014, 0x1018):
            for failed in ((), (0,), (1,), (0, 1)):
                with self.subTest(pid=pid, failed=failed):
                    self.configure(identity=(0x3837, pid), collection=(0xFF01, 1),
                                   failed=failed, raw=descriptor)
                    info = self.scan()
                    self.assertEqual(info["input_report_sizes"], {"77": 64})
                    self.assertEqual(info["output_report_sizes"], {"77": 64})
                    if failed:
                        self.dev.get_report_descriptor.assert_called_once_with()
                    else:
                        self.backend.device.assert_not_called()


class Fa09DescriptorFallbackTests(unittest.TestCase):
    """Exact #476 transport proof using fake DLLs and metadata-only HID."""
    descriptor = bytes.fromhex(
        "06 02 ff 09 02 a1 01 85 13 75 08 95 13 81 02 91 02 c0")

    def setUp(self):
        import battery_hid as H
        self.case = DescriptorCacheTests()
        self.case.setUp()
        self.addCleanup(self.case.doCleanups)
        self.case.parent.side_effect = lambda path: "receiver-a"
        self.dev = Mock(spec=["open_path", "get_report_descriptor", "close"])
        self.backend = SimpleNamespace(device=Mock(return_value=self.dev))
        patcher = patch.object(H, "hid", self.backend)
        patcher.start()
        self.addCleanup(patcher.stop)

    def configure(self, failed=(0, 1), identity=(0x3554, 0xFA09), collection=(0xFF02, 2),
                  inp=None, raw=None):
        self.case.cache.clear()
        self.dev.reset_mock(side_effect=True)
        self.backend.device.reset_mock()
        self.case.products[PATH_A] = "2.4G Wireless Receiver"
        self.case.batch3_descriptor(identity, collection, inp or {19: 20}, {19: 20})
        original = self.case.hid.HidP_GetValueCaps.side_effect

        def fields(report_type, *args):
            return 0 if report_type in failed else original(report_type, *args)

        self.case.hid.HidP_GetValueCaps.side_effect = fields
        self.dev.get_report_descriptor.return_value = self.descriptor if raw is None else raw

    def scan(self):
        info, = self.case.scan([PATH_A])
        self.case.hid.HidD_GetFeature.assert_not_called()
        self.case.hid.HidD_SetFeature.assert_not_called()
        self.case.kernel.ReadFile.assert_not_called()
        self.case.kernel.WriteFile.assert_not_called()
        return info

    def test_fa09_ff02_descriptor_proof_enables_g75pro_by_only(self):
        from test_aula_fa09 import P, aula_entry, aula_fixture, identity, reply
        for failed in ((), (0,), (1,), (0, 1)):
            with self.subTest(failed=failed):
                self.configure(failed=failed)
                info = self.scan()
                for index, direction in enumerate(("input", "output")):
                    self.assertEqual(info[direction + "_report_sizes"], {"19": 20})
                    self.assertEqual(info[direction + "_report_proof"],
                                     "descriptor" if index in failed else "caps")
                self.assertEqual(self.scan(), info)
                if failed:
                    self.dev.open_path.assert_called_once_with(PATH_A.encode())
                    self.dev.get_report_descriptor.assert_called_once_with()
                    self.dev.close.assert_called_once_with()
                else:
                    self.backend.device.assert_not_called()
                with aula_fixture([identity("030000000115"), reply(100, 1)],
                                  [aula_entry(path=b"feature-ff04"), info]) as bus:
                    status, = P().poll()
                    self.assertEqual((status.name, status.level, status.charging),
                                     ("MCHOSE G75 Pro", 100, False))
                    self.assertEqual(bus.opened, [info["path"]])
                    self.assertEqual(bus.opened, bus.closed)
                    self.assertEqual((bus.feature_writes, bus.feature_reads), ([], []))

    def assert_reader_rejected(self, info):
        from test_aula_fa09 import P, aula_entry, aula_fixture, identity, reply
        with aula_fixture([identity("030000000115"), reply()],
                          [aula_entry(path=b"feature-ff04"), info]) as bus:
            self.assertEqual(P().poll(), [])
            self.assertEqual((bus.opened, bus.writes, bus.reads,
                              bus.feature_writes, bus.feature_reads), ([], [], [], [], []))

    def test_fa09_ff02_bad_descriptor_never_uses_maximum_as_report_proof_and_retries(self):
        cases = ((b"\x85", None),
                 (self.descriptor.replace(b"\x06\x02\xff", b"\x06\x04\xff"), "collection not found"),
                 (self.descriptor.replace(b"\x95\x13", b"\x95\x12"), "collection maximum mismatch"),
                 (self.descriptor[:-1] + bytes.fromhex("85 06 95 07 b1 02 c0"),
                  "collection maximum mismatch"))
        for raw, reason in cases:
            with self.subTest(raw=raw):
                self.configure(raw=raw)
                info = self.scan()
                self.assertEqual((info["input_report_length"], info["output_report_length"]), (20, 20))
                self.assertIsNone(info["input_report_sizes"])
                self.assertIsNone(info["output_report_sizes"])
                self.assertEqual(info["input_report_proof"], "failed")
                self.assertIn("report_descriptor_error", info)
                if reason:
                    self.assertEqual(info["report_descriptor_error"], reason)
                self.assertNotIn(PATH_A, self.case.cache)
                self.assert_reader_rejected(info)
                self.dev.get_report_descriptor.return_value = self.descriptor
                recovered = self.scan()
                self.assertEqual(recovered["input_report_sizes"], {"19": 20})
                self.assertEqual(recovered["output_report_sizes"], {"19": 20})
                self.assertIn(PATH_A, self.case.cache)
                self.assertEqual(self.dev.close.call_count, 2)

    def test_fa09_ff02_wrong_or_multiple_reports_still_reject_query(self):
        cases = (self.descriptor.replace(b"\x85\x13", b"\x85\x12"),
                 self.descriptor[:-1] + bytes.fromhex("85 14 81 02 91 02 c0"))
        for raw in cases:
            with self.subTest(raw=raw):
                self.configure(raw=raw)
                info = self.scan()
                self.assertEqual(info["input_report_proof"], "descriptor")
                self.assertNotEqual(info["input_report_sizes"], {"19": 20})
                self.assert_reader_rejected(info)

    def test_fa09_ff02_caps_descriptor_disagreement_cannot_fill_missing_direction(self):
        # Caps prove input 12, descriptor declares 13; equal maxima prove nothing.
        self.configure(failed=(1,), inp={18: 20})
        info = self.scan()
        self.assertEqual(info["input_report_sizes"], {"18": 20})
        self.assertIsNone(info["output_report_sizes"])
        self.assertEqual(info["report_descriptor_error"], "caps/descriptor disagreement")
        self.assertNotIn(PATH_A, self.case.cache)
        self.assert_reader_rejected(info)

    def test_fa09_ff02_descriptor_io_failure_closes_without_commands_or_private_text(self):
        for method in ("open_path", "get_report_descriptor", "close"):
            with self.subTest(method=method):
                self.configure()
                getattr(self.dev, method).side_effect = OSError("private-device-path")
                info = self.scan()
                self.assertIsNone(info["input_report_sizes"])
                self.assertIsNone(info["output_report_sizes"])
                self.assertNotIn(PATH_A, self.case.cache)
                self.assertNotIn("private-device-path", str(info))
                self.dev.close.assert_called_once_with()
                self.assert_reader_rejected(info)

    def test_fa09_ff02_fallback_never_expands_identity_or_collection_scope(self):
        for identity, collection in (((0x3554, 0xFA07), (0xFF02, 2)),
                                     ((0x41E4, 0xFA09), (0xFF02, 2)),
                                     ((0x3554, 0xFA09), (0xFF02, 1)),
                                     ((0x3554, 0xFA09), (0xFF04, 2)),
                                     ((0x3554, 0xFA09), (0xFF04, 1))):
            with self.subTest(identity=identity, collection=collection):
                self.configure(identity=identity, collection=collection)
                self.scan()
                self.backend.device.assert_not_called()


class AsusOmniDescriptorTests(unittest.TestCase):
    """Synthetic OMNI caps prove each direction before query-handle access."""
    path = r"\\?\hid#vid_0b05&pid_1ace&mi_02&col03#synthetic-omni"

    def setUp(self):
        self.case = DescriptorCacheTests()
        self.case.setUp()
        self.addCleanup(self.case.doCleanups)
        raw = patch.object(topology, "_read_report_descriptor")
        self.raw = raw.start()
        self.addCleanup(raw.stop)

    def configure(self, *, identity=(0x0B05, 0x1ACE), collection=(0xFF01, 1), path=None,
                  inp=None, out=None, buttons=False, maxima=None, counts=None):
        self.case.cache.clear()
        self.target = path or self.path
        self.case.identities[self.target] = identity
        self.case.products[self.target] = "Synthetic OMNI receiver"
        reports = {0: {3: 64} if inp is None else inp, 1: {3: 64} if out is None else out}
        maxima = maxima or {}
        counts = counts or {}

        def caps(data, pointer):
            if self.case.handles[data.value] in self.case.fail_caps:
                return 0
            value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
            value.page, value.usage = collection
            value.input = maxima.get(0, max(reports[0].values(), default=0))
            value.output = maxima.get(1, max(reports[1].values(), default=0))
            if buttons:
                value.ib, value.ob = counts.get(0, len(reports[0])), counts.get(1, len(reports[1]))
            else:
                value.iv, value.ov = counts.get(0, len(reports[0])), counts.get(1, len(reports[1]))
            return 0x00110000

        def fields(report_type, fields, length, data):
            self.assertIn(report_type, (0, 1))
            for field, (report_id, size) in zip(fields, reports[report_type].items()):
                field.report = report_id
                if buttons:
                    field.is_range, field.usage_min, field.usage_max = 1, 0, 255
                else:
                    field.bits, field.count = 8, size - 1
            return 0x00110000

        self.case.hid.HidP_GetCaps.side_effect = caps
        self.case.hid.HidP_GetButtonCaps.side_effect = fields
        self.case.hid.HidP_GetValueCaps.side_effect = fields
        self.case.hid.HidP_GetButtonCaps.reset_mock()
        self.case.hid.HidP_GetValueCaps.reset_mock()

    def scan(self):
        result, = self.case.scan([self.target])
        self.assertTrue(all(call.args[1] == 0 for call in self.case.kernel.CreateFileW.call_args_list))
        self.case.hid.HidD_GetFeature.assert_not_called()
        self.case.hid.HidD_SetFeature.assert_not_called()
        self.case.kernel.ReadFile.assert_not_called()
        self.case.kernel.WriteFile.assert_not_called()
        self.raw.assert_not_called()
        self.assertEqual(self.case.kernel.CloseHandle.call_count, self.case.kernel.CreateFileW.call_count)
        return result

    def assert_refused(self, info):
        import test_asus_omni as F
        with F.fixture([F.reply()], [info]) as bus:
            self.assertEqual(F.P().poll(), [])
            self.assertEqual(bus.calls, [])

    def test_exact_both_direction_byte_arrays_prove_id03_and_cache_without_io(self):
        for buttons in (False, True):
            with self.subTest(buttons=buttons):
                self.configure(buttons=buttons)
                before = self.case.kernel.CreateFileW.call_count
                first = self.scan()
                for direction in ("input", "output"):
                    self.assertEqual(first[direction + "_report_sizes"], {"3": 64})
                    self.assertEqual(first[direction + "_report_proof"], "caps")
                self.assertEqual(self.scan(), first)
                self.assertIn(self.target, self.case.cache)
                self.assertEqual(self.case.kernel.CreateFileW.call_count, before + 1)
                import test_asus_omni as F
                with F.fixture([F.reply()], [first]) as bus:
                    self.assertEqual(len(F.P().poll()), 1)
                    self.assertEqual(bus.writes, [(first["path"], F.REQUEST)])

    def test_each_direction_api_failure_partial_or_extra_count_cannot_use_maximum(self):
        for buttons in (False, True):
            for direction in (0, 1):
                for failure in ("status", "short", "extra"):
                    with self.subTest(buttons=buttons, direction=direction, failure=failure):
                        self.configure(buttons=buttons)
                        getter = getattr(self.case.hid, "HidP_GetButtonCaps" if buttons else "HidP_GetValueCaps")
                        original = getter.side_effect

                        def fail(report_type, fields, length, data):
                            status = original(report_type, fields, length, data)
                            if report_type != direction:
                                return status
                            if failure == "status":
                                return 0
                            ctypes.cast(length, ctypes.POINTER(ctypes.c_ushort))[0] = 0 if failure == "short" else 2
                            return status

                        getter.side_effect = fail
                        info = self.scan()
                        key = ("input" if direction == 0 else "output") + "_report_sizes"
                        self.assertIsNone(info[key])
                        self.assertNotIn(self.target, self.case.cache)
                        self.assert_refused(info)
                        getter.side_effect = original
                        self.assertEqual(self.scan()[key], {"3": 64})
                        self.assertIn(self.target, self.case.cache)

    def test_wrong_ids_lengths_missing_directions_and_unproven_fields_never_query(self):
        cases = [dict(inp={4: 64}), dict(out={4: 64}), dict(inp={3: 63}), dict(out={3: 65}),
                 dict(inp={}), dict(out={}), dict(maxima={0: 65}), dict(maxima={1: 63}),
                 dict(counts={0: 0}), dict(counts={1: 33})]
        for changes in cases:
            with self.subTest(changes=changes):
                self.configure(**changes)
                info = self.scan()
                self.assertNotIn(self.target, self.case.cache)
                self.assert_refused(info)
        for field, value in (("bits", 7), ("count", 0), ("alias", 1)):
            with self.subTest(field=field):
                self.configure()
                original = self.case.hid.HidP_GetValueCaps.side_effect

                def bad(report_type, fields, length, data):
                    status = original(report_type, fields, length, data)
                    setattr(fields[0], field, value)
                    return status

                self.case.hid.HidP_GetValueCaps.side_effect = bad
                info = self.scan()
                self.assertIsNone(info["input_report_sizes"])
                self.assertIsNone(info["output_report_sizes"])
                self.assert_refused(info)

    def test_other_ids_are_individually_proven_not_maximum_substitutes_for03(self):
        self.configure(inp={3: 64, 4: 96}, out={3: 64, 5: 96})
        info = self.scan()
        self.assertEqual(info["input_report_sizes"], {"3": 64, "4": 96})
        self.assertEqual(info["output_report_sizes"], {"3": 64, "5": 96})
        import test_asus_omni as F
        with F.fixture([F.reply()], [info]) as bus:
            self.assertEqual(len(F.P().poll()), 1)
            self.assertEqual(bus.reads, [(info["path"], 64, 300)])

    def test_no_fixed_usage_loophole_mixed_fields_or_duplicate_id_inference(self):
        for defect in ("fixed", "mixed", "duplicate"):
            with self.subTest(defect=defect):
                self.configure(buttons=defect != "duplicate", counts={0: 2, 1: 2} if defect == "duplicate" else None)
                if defect == "duplicate":
                    original = self.case.hid.HidP_GetValueCaps.side_effect

                    def fields(report_type, fields, length, data):
                        status = original(report_type, fields, length, data)
                        fields[1].report, fields[1].bits, fields[1].count = 3, 8, 1
                        return status

                    self.case.hid.HidP_GetValueCaps.side_effect = fields
                elif defect == "fixed":
                    def fields(report_type, fields, length, data):
                        fields[0].report, fields[0].is_range = 3, 0
                        fields[0].usage_min = fields[0].usage_max = 1
                        fields[0].report_count = 63
                        return 0x00110000

                    self.case.hid.HidP_GetButtonCaps.side_effect = fields
                else:
                    original = self.case.hid.HidP_GetCaps.side_effect

                    def caps(data, pointer):
                        status = original(data, pointer)
                        value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
                        value.iv = value.ov = 1
                        return status

                    self.case.hid.HidP_GetCaps.side_effect = caps
                info = self.scan()
                self.assertIsNone(info["input_report_sizes"])
                self.assertIsNone(info["output_report_sizes"])
                self.assertNotIn(self.target, self.case.cache)
                self.assert_refused(info)

    def test_proof_does_not_expand_vid_pid_usage_interface_or_collection(self):
        cases = [dict(identity=(0x0B06, 0x1ACE)), dict(identity=(0x0B05, 0x1ACF)),
                 dict(collection=(0xFF01, 2))]
        cases += [dict(collection=(page, 1)) for page in (0xFF00, 0xFF02, 0xFFC0, 0x0059)]
        cases += [dict(path=self.path.replace("mi_02&col03", route)) for route in (
            "mi_01&col03", "mi_02&col01", "mi_02&col02", "mi_02&col030", "mi_02&col03suffix")]
        for changes in cases:
            with self.subTest(changes=changes):
                self.configure(**changes)
                info = self.scan()
                self.assertNotIn("input_report_sizes", info)
                self.assertNotIn("output_report_sizes", info)
                self.case.hid.HidP_GetValueCaps.assert_not_called()
                self.case.hid.HidP_GetButtonCaps.assert_not_called()
                self.assert_refused(info)

    def test_access_zero_discovery_failure_stays_uncached_and_can_recover(self):
        for name in ("fail_preparsed", "fail_caps"):
            with self.subTest(name=name):
                self.configure()
                failure = getattr(self.case, name)
                failure.add(self.target)
                info = self.scan()
                self.assertNotIn("input_report_sizes", info)
                self.assertNotIn(self.target, self.case.cache)
                self.assert_refused(info)
                failure.clear()
                self.assertEqual(self.scan()["input_report_sizes"], {"3": 64})


if __name__ == "__main__":
    unittest.main()
