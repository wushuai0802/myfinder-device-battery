"""FA6x Input 03 proof using existing fake Windows descriptor fixtures only."""
import ctypes
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "engine"))
sys.path.insert(0, str(ROOT / "engine" / "tests"))
import _hidstub  # noqa: F401
from providers import fa6x_passive as F
import topology
import test_topology_cache as fixtures
import battery_hid as H
from standardhid import Caps
from test_fa6x_passive import fixture


class Fa6xTopologyTests(unittest.TestCase):
    def setUp(self):
        self.fixture = fixtures.DescriptorCacheTests()
        self.addCleanup(self.fixture.doCleanups)
        self.fixture.setUp()

    def assert_descriptor_only(self):
        f = self.fixture
        f.hid.HidD_GetFeature.assert_not_called()
        f.hid.HidD_SetFeature.assert_not_called()
        f.kernel.ReadFile.assert_not_called()
        f.kernel.WriteFile.assert_not_called()
        self.assertTrue(all(call.args[1] == 0 for call in f.kernel.CreateFileW.call_args_list))
        self.assertTrue(all(call.args[0] == 0 for call in f.hid.HidP_GetValueCaps.call_args_list))

    def legacy_descriptor(self, release=0x0116, identity=(0x1D57, 0xFA65), collection=(0x000A, 0), sizes=None):
        f = self.fixture
        f.batch3_descriptor(identity, collection, sizes or {3: 8}, {4: 65})

        def attributes(handle, pointer):
            result = f.attributes(handle, pointer)
            ctypes.cast(pointer, ctypes.POINTER(topology._Attributes)).contents.release = release
            return result

        f.hid.HidD_GetAttributes.side_effect = attributes

    def test_legacy_exact_release_and_state_collection_prove_only_input_and_reuse_cache(self):
        f = self.fixture
        with patch.object(topology, "_descriptor_fallback", side_effect=AssertionError("no fallback")):
            for release in (0x0016, 0x0116, 0xFF16):
                for sizes in ({3: 5}, {3: 8}, {3: 19, 9: 65}):
                    with self.subTest(release=release, sizes=sizes):
                        f.cache.clear()
                        self.legacy_descriptor(release=release, sizes=sizes)
                        info, = f.scan([fixtures.PATH_A])
                        self.assertEqual(info["input_report_sizes"], {str(k): v for k, v in sizes.items()})
                        self.assertNotIn("output_report_sizes", info)
                        self.assertNotIn("feature_report_sizes", info)
                        companion = dict(info, usage_page=0xFF00, usage=1, path=b"metadata-only",
                                         input_report_sizes=None)
                        selected = F.Fa6xPassiveProvider()._pick([info, companion])
                        self.assertEqual(selected["path"], info["path"])
                        before = f.kernel.CreateFileW.call_count
                        self.assertEqual(f.scan([fixtures.PATH_A]), [info])
                        self.assertEqual(f.kernel.CreateFileW.call_count, before)
        self.assert_descriptor_only()

    def test_legacy_other_releases_identities_and_usages_do_not_add_descriptor_probes(self):
        f = self.fixture
        cases = [(release, (0x1D57, 0xFA65), (0x000A, 0))
                 for release in (0, 1, 0x15, 0x18, 0x1618, 0xFFFF)]
        cases += [(0x16, identity, collection) for identity, collection in (
            ((0x1D58, 0xFA65), (0x000A, 0)), ((0x1D57, 0xFA93), (0x000A, 0)),
            ((0x1D57, 0xFA61), (0x000A, 0)), ((0x1D57, 0xFA65), (0x000B, 0)),
            ((0x1D57, 0xFA65), (0x000A, 1)), ((0x1D57, 0xFA65), (0xFF01, 2)))]
        for release, identity, collection in cases:
            with self.subTest(release=release, identity=identity, collection=collection):
                f.cache.clear()
                self.legacy_descriptor(release, identity, collection)
                info, = f.scan([fixtures.PATH_A])
                self.assertNotIn("input_report_sizes", info)
        f.hid.HidP_GetValueCaps.assert_not_called()
        f.hid.HidP_GetButtonCaps.assert_not_called()
        self.assert_descriptor_only()

    def test_legacy_incomplete_byte_array_proof_retries_when_raw_proof_is_unavailable(self):
        f = self.fixture
        with patch.object(topology, "_read_report_descriptor",
                          return_value=(None, "get_report_descriptor unavailable")):
            for defect in ("no_03", "short", "not_bytes", "alias", "partial", "failure"):
                with self.subTest(defect=defect):
                    f.cache.clear()
                    self.legacy_descriptor(sizes={3: 4} if defect == "short" else {3: 8})
                    original = f.hid.HidP_GetValueCaps.side_effect

                    def fields(report_type, values, length, data):
                        result = original(report_type, values, length, data)
                        if defect == "no_03":
                            values[0].report = 4
                        elif defect == "not_bytes":
                            values[0].bits = 16
                        elif defect == "alias":
                            values[0].alias = 1
                        elif defect == "partial":
                            ctypes.cast(length, ctypes.POINTER(ctypes.c_ushort))[0] = 0
                        elif defect == "failure":
                            return 0
                        return result

                    f.hid.HidP_GetValueCaps.side_effect = fields
                    info, = f.scan([fixtures.PATH_A])
                    self.assertNotIn(fixtures.PATH_A, f.cache)
                    companion = dict(info, usage_page=0xFF00, usage=1, path=b"metadata-only")
                    self.assertIsNone(F.Fa6xPassiveProvider()._pick([info, companion]))
                    self.legacy_descriptor()
                    recovered, = f.scan([fixtures.PATH_A])
                    self.assertEqual(recovered["input_report_sizes"], {"3": 8})
                    self.assertIn(fixtures.PATH_A, f.cache)
        self.assert_descriptor_only()

    def test_exact_collections_prove_input_only_and_cache_complete_descriptors(self):
        f = self.fixture
        for pid, collection in ((0xFA60, (0x000A, 0)), (0xFA65, (0xFF00, 1))):
            for sizes in ({3: 5}, {3: 8}, {3: 19, 9: 65}):
                with self.subTest(pid=pid, sizes=sizes):
                    f.cache.clear()
                    f.batch3_descriptor((0x1D57, pid), collection, sizes, {4: 65})
                    info, = f.scan([fixtures.PATH_A])
                    self.assertEqual(info["input_report_sizes"], {str(k): v for k, v in sizes.items()})
                    self.assertNotIn("output_report_sizes", info)
                    self.assertNotIn("feature_report_sizes", info)
                    self.assertEqual(F.Fa6xPassiveProvider()._pick([info]), info)
                    before = f.kernel.CreateFileW.call_count
                    self.assertEqual(f.scan([fixtures.PATH_A]), [info])
                    self.assertEqual(f.kernel.CreateFileW.call_count, before)
        self.assert_descriptor_only()

    def test_incomplete_proof_is_not_cached_and_retries_without_io(self):
        f = self.fixture
        for pid, collection in F.COLLECTIONS.items():
            for defect in ("no_03", "short", "not_bytes", "alias", "partial", "failure"):
                with self.subTest(pid=pid, defect=defect):
                    f.cache.clear()
                    sizes = {3: 4} if defect == "short" else {3: 8}
                    f.batch3_descriptor((0x1D57, pid), collection, sizes, {4: 65})
                    original = f.hid.HidP_GetValueCaps.side_effect

                    def fields(report_type, values, length, data):
                        result = original(report_type, values, length, data)
                        if defect == "no_03":
                            values[0].report = 4
                        elif defect == "not_bytes":
                            values[0].bits = 16
                        elif defect == "alias":
                            values[0].alias = 1
                        elif defect == "partial":
                            ctypes.cast(length, ctypes.POINTER(ctypes.c_ushort))[0] = 0
                        elif defect == "failure":
                            return 0
                        return result

                    f.hid.HidP_GetValueCaps.side_effect = fields
                    info, = f.scan([fixtures.PATH_A])
                    self.assertIsNone(F.Fa6xPassiveProvider()._pick([info]))
                    self.assertNotIn(fixtures.PATH_A, f.cache)
                    f.batch3_descriptor((0x1D57, pid), collection, {3: 8}, {4: 65})
                    recovered, = f.scan([fixtures.PATH_A])
                    self.assertEqual(F.Fa6xPassiveProvider()._pick([recovered]), recovered)
        self.assert_descriptor_only()

    def test_neighboring_ids_and_communication_collections_are_not_probed(self):
        f = self.fixture
        cases = [((0x1D57, 0xFA60), (0x000B, 0)), ((0x1D57, 0xFA60), (0xFF00, 1)),
                 ((0x1D57, 0xFA65), (0xFF01, 2)), ((0x1D57, 0xFA65), (0x000A, 0)),
                 ((0x1D57, 0xFA65), (0xFF00, 2)), ((0x1D57, 0xFA61), (0x000A, 0)),
                 ((0x1D57, 0x1013), (0x000A, 0)), ((0x1D58, 0xFA65), (0xFF00, 1))]
        for identity, collection in cases:
            with self.subTest(identity=identity, collection=collection):
                f.cache.clear()
                f.batch3_descriptor(identity, collection, {3: 8}, {4: 65})
                info, = f.scan([fixtures.PATH_A])
                self.assertNotIn("input_report_sizes", info)
                self.assertIsNone(F.Fa6xPassiveProvider()._pick([info]))
        f.hid.HidP_GetValueCaps.assert_not_called()
        f.hid.HidP_GetButtonCaps.assert_not_called()
        self.assert_descriptor_only()


class Sc650DescriptorFallbackTests(unittest.TestCase):
    """All Windows DLLs, raw metadata getters and listening handles are fake."""
    raw = bytes.fromhex("05 0a 09 00 a1 01 85 03 75 08 95 04 81 02 c0")

    def setUp(self):
        self.case = fixtures.DescriptorCacheTests()
        self.addCleanup(self.case.doCleanups)
        self.case.setUp()
        self.dev = Mock(spec=["open_path", "get_report_descriptor", "close"])
        self.backend = SimpleNamespace(device=Mock(return_value=self.dev))
        patcher = patch.object(H, "hid", self.backend)
        patcher.start()
        self.addCleanup(patcher.stop)

    def configure(self, identity=(0x1D57, 0xFA60), collection=(0x000A, 0),
                  maxima=(5, 0, 0), raw=None, counts=(0, 0, 0), release=1):
        f = self.case
        f.cache.clear()
        self.dev.reset_mock(side_effect=True)
        self.backend.device.reset_mock()
        f.identities[fixtures.PATH_A] = identity
        self.dev.get_report_descriptor.return_value = self.raw if raw is None else raw

        def attributes(handle, pointer):
            result = f.attributes(handle, pointer)
            ctypes.cast(pointer, ctypes.POINTER(topology._Attributes)).contents.release = release
            return result

        def caps(data, pointer):
            value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
            value.page, value.usage = collection
            value.input, value.output, value.feature = maxima
            value.iv, value.ov, value.fv = counts
            return 0x00110000

        f.hid.HidD_GetAttributes.side_effect = attributes
        f.hid.HidP_GetCaps.side_effect = caps

    def scan(self):
        f = self.case
        info, = f.scan([fixtures.PATH_A])
        f.hid.HidD_GetFeature.assert_not_called()
        f.hid.HidD_SetFeature.assert_not_called()
        f.hid.HidD_GetPhysicalDescriptor.assert_not_called()
        f.kernel.ReadFile.assert_not_called()
        f.kernel.WriteFile.assert_not_called()
        self.assertTrue(all(call.args[1] == 0 for call in f.kernel.CreateFileW.call_args_list))
        return info

    def assert_no_listener(self, info, inventory=None):
        with fixture([[3, 0x8E, 0x40, 1, 49]], [info] if inventory is None else inventory) as (bus, provider):
            self.assertEqual(provider.poll(), [])
            self.assertEqual((bus.opened, bus.reads), ([], []))
            return provider.diagnostics()

    def test_sc650_caps_none_raw_input03_recovers_listens_and_caches(self):
        self.configure()
        info = self.scan()
        self.assertEqual(info["input_report_sizes"], {"3": 5})
        self.assertEqual(info["input_report_proof"], "descriptor")
        self.assertEqual(info["declared_report_sizes"], {"input": {"3": 5}, "output": {}, "feature": {}})
        self.assertNotIn("output_report_sizes", info)
        self.assertNotIn("feature_report_sizes", info)
        self.assertIn(fixtures.PATH_A, self.case.cache)
        before = self.case.kernel.CreateFileW.call_count
        self.assertEqual(self.scan(), info)
        self.assertEqual(self.case.kernel.CreateFileW.call_count, before)
        self.dev.open_path.assert_called_once_with(fixtures.PATH_A.encode())
        self.dev.get_report_descriptor.assert_called_once_with()
        self.dev.close.assert_called_once_with()
        with fixture([[3, 0x8E, 0x40, 1, 91], [3, 0x8E, 0x40, 1, 49]], [info]) as (bus, provider):
            status, = provider.poll()
            self.assertEqual((status.level, status.charging_known, status.verification_level), (49, False, "C"))
            self.assertEqual(bus.opened, [info["path"]])

    def test_sc650_bad_raw_proof_rejects_and_retries_until_recovered(self):
        cases = (self.raw.replace(b"\x85\x03", b"\x85\x04"),
                 self.raw.replace(b"\x05\x0a", b"\x05\x0b"),
                 self.raw.replace(b"\x95\x04", b"\x95\x03"),
                 self.raw[:-1], b"\x85", self.raw + self.raw,
                 self.raw + self.raw.replace(b"\x09\x00", b"\x09\x01"),
                 self.raw + b"\x85")
        for raw in cases:
            with self.subTest(raw=raw):
                self.configure(raw=raw)
                info = self.scan()
                self.assertNotIn(fixtures.PATH_A, self.case.cache)
                self.assertEqual(self.assert_no_listener(info),
                                 ["input report 03 length unproven"
                                  + H.report_proof_summary(info, "input", include_declared=True)
                                  + "; not opened"])
                self.dev.get_report_descriptor.return_value = self.raw
                recovered = self.scan()
                self.assertEqual(recovered["input_report_sizes"], {"3": 5})
                self.assertIn(fixtures.PATH_A, self.case.cache)
                self.assertEqual(self.dev.close.call_count, 2)

    def test_sc650_all_maxima_and_partial_caps_must_agree(self):
        # Some caps fields exist, but input per-ID proof fails. Neither input
        # nor other-direction maxima may be contradicted by a raw descriptor.
        for maxima in ((8, 0, 0), (5, 7, 0), (5, 0, 7)):
            with self.subTest(maxima=maxima):
                self.configure(maxima=maxima, counts=(2, 1, 1))
                self.case.hid.HidP_GetValueCaps.side_effect = lambda *args: 0
                info = self.scan()
                self.assertIsNone(info["input_report_sizes"])
                self.assertEqual(info["report_descriptor_error"], "collection maximum mismatch")
                self.assertNotIn(fixtures.PATH_A, self.case.cache)
                self.assert_no_listener(info)

    def test_sc650_proven_caps_are_never_overridden_by_raw_descriptor(self):
        for report_id in (3, 4):
            with self.subTest(report_id=report_id):
                self.configure()
                self.case.batch3_descriptor((0x1D57, 0xFA60), (0x000A, 0), {report_id: 5}, {4: 7})
                info = self.scan()
                self.assertEqual(info["input_report_sizes"], {str(report_id): 5})
                self.backend.device.assert_not_called()
                if report_id == 4:
                    self.assertNotIn(fixtures.PATH_A, self.case.cache)
                    self.assert_no_listener(info)

    def test_sc650_missing_or_failing_raw_getter_never_opens_listener_or_caches(self):
        for failure in ("missing", "open_path", "get_report_descriptor", "close"):
            with self.subTest(failure=failure):
                self.configure()
                dev = (Mock(spec=["open_path", "close"]) if failure == "missing" else self.dev)
                self.backend.device.return_value = dev
                if failure != "missing":
                    getattr(dev, failure).side_effect = OSError("private-device-path")
                info = self.scan()
                self.assertIsNone(info["input_report_sizes"])
                self.assertNotIn(fixtures.PATH_A, self.case.cache)
                self.assertNotIn("private-device-path", str(info))
                self.assertEqual(self.assert_no_listener(info),
                                 ["input report 03 length unproven"
                                  + H.report_proof_summary(info, "input", include_declared=True)
                                  + "; not opened"])
                dev.close.assert_called_once_with()
                if failure == "missing":
                    dev.open_path.assert_not_called()
                self.backend.device.return_value = self.dev
                self.dev.reset_mock(side_effect=True)
                self.dev.get_report_descriptor.return_value = self.raw
                self.assertEqual(self.scan()["input_report_sizes"], {"3": 5})

    def test_sc650_raw_fallback_does_not_expand_identity_or_collection_scope(self):
        cases = (((0x1D57, 0xFA65), (0x000A, 0)),
                 ((0x1D58, 0xFA60), (0x000A, 0)),
                 ((0x1D57, 0xFA61), (0x000A, 0)),
                 ((0x1D57, 0xFA60), (0x000B, 0)),
                 ((0x1D57, 0xFA60), (0x000A, 1)),
                 ((0x1D57, 0xFA60), (0xFF00, 1)))
        for identity, collection in cases:
            with self.subTest(identity=identity, collection=collection):
                self.configure(identity=identity, collection=collection)
                self.assert_no_listener(self.scan())
                self.backend.device.assert_not_called()

    @staticmethod
    def fa65_inventory(info):
        if (info["usage_page"], info["usage"]) == (0x000A, 0):
            return [info, dict(info, usage_page=0xFF00, usage=1, path=b"metadata-only",
                               input_report_sizes=None)]
        return [info]

    def fa65_routes(self):
        web_raw = bytes.fromhex("06 00 ff 09 01 a1 01 85 03 75 08 95 04 81 02 c0")
        return (((0xFF00, 1), 1, web_raw), ((0x000A, 0), 0x0116, self.raw))

    def test_fa65_caps_failure_raw_input03_recovers_web_and_legacy_and_caches(self):
        routes = list(self.fa65_routes())
        routes += [((0x000A, 0), release, self.raw) for release in (0x0016, 0xFF16)]
        for collection, release, raw in routes:
            with self.subTest(collection=collection, release=release):
                self.configure(identity=(0x1D57, 0xFA65), collection=collection,
                               release=release, raw=raw, counts=(1, 0, 0))
                self.case.hid.HidP_GetValueCaps.side_effect = lambda *args: 0
                info = self.scan()
                self.assertEqual(info["input_report_sizes"], {"3": 5})
                self.assertEqual(info["input_report_proof"], "descriptor")
                self.assertEqual(info["declared_report_sizes"],
                                 {"input": {"3": 5}, "output": {}, "feature": {}})
                self.assertNotIn("report_descriptor_error", info)
                self.assertNotIn("output_report_sizes", info)
                self.assertNotIn("feature_report_sizes", info)
                self.assertIn(fixtures.PATH_A, self.case.cache)
                before = self.case.kernel.CreateFileW.call_count
                self.assertEqual(self.scan(), info)
                self.assertEqual(self.case.kernel.CreateFileW.call_count, before)
                self.dev.open_path.assert_called_once_with(info["path"])
                self.dev.get_report_descriptor.assert_called_once_with()
                self.dev.close.assert_called_once_with()
                model = 0x93 if collection == (0x000A, 0) else 0x78
                with fixture([[3, model, 0x40, 0, 67]], self.fa65_inventory(info)) as (bus, provider):
                    status, = provider.poll()
                    self.assertEqual((status.level, status.charging_known, status.verification_level),
                                     (67, False, "C"))
                    self.assertEqual(bus.opened, [info["path"]])
                    self.assertEqual(provider.diagnostics(), [])

    def test_fa65_raw_missing_or_short_input03_still_never_opens_or_caches(self):
        for collection, release, raw in self.fa65_routes():
            cases = ((raw.replace(b"\x85\x03", b"\x85\x04"), 5, {"4": 5}),
                     (raw.replace(b"\x95\x04", b"\x95\x03"), 4, {"3": 4}))
            for descriptor_raw, maximum, sizes in cases:
                with self.subTest(collection=collection, sizes=sizes):
                    self.configure(identity=(0x1D57, 0xFA65), collection=collection,
                                   release=release, raw=descriptor_raw, maxima=(maximum, 0, 0))
                    info = self.scan()
                    self.assertEqual(info["input_report_sizes"], sizes)
                    self.assertEqual(info["input_report_proof"], "descriptor")
                    # A valid declaration is not a parser error or permission for Input 03.
                    self.assertNotIn("report_descriptor_error", info)
                    self.assertEqual(info["declared_report_sizes"]["input"], sizes)
                    self.assertNotIn(fixtures.PATH_A, self.case.cache)
                    self.assert_no_listener(info, self.fa65_inventory(info))

    def test_fa65_raw_maxima_mismatch_or_missing_collection_never_opens(self):
        for collection, release, raw in self.fa65_routes():
            other_raw = self.raw if collection == (0xFF00, 1) else self.fa65_routes()[0][2]
            cases = [(raw, maxima, "collection maximum mismatch")
                     for maxima in ((8, 0, 0), (5, 7, 0), (5, 0, 7))]
            cases += [(other_raw, (5, 0, 0), "collection not found")]
            for descriptor_raw, maxima, error in cases:
                with self.subTest(collection=collection, maxima=maxima, error=error):
                    self.configure(identity=(0x1D57, 0xFA65), collection=collection,
                                   release=release, raw=descriptor_raw, maxima=maxima)
                    info = self.scan()
                    self.assertIsNone(info["input_report_sizes"])
                    self.assertEqual(info["input_report_proof"], "failed")
                    self.assertEqual(info["report_descriptor_error"], error)
                    if error == "collection maximum mismatch":
                        self.assertEqual(info["declared_report_sizes"]["input"], {"3": 5})
                    self.assertNotIn(fixtures.PATH_A, self.case.cache)
                    self.assert_no_listener(info, self.fa65_inventory(info))

    def test_fa65_proven_caps_are_never_overridden_by_raw_descriptor(self):
        for collection, release, raw in self.fa65_routes():
            for sizes in ({3: 5}, {4: 5}, {3: 4}):
                with self.subTest(collection=collection, sizes=sizes):
                    self.configure(identity=(0x1D57, 0xFA65), collection=collection,
                                   release=release, raw=raw)
                    self.dev.get_report_descriptor.side_effect = AssertionError("caps must win")
                    self.case.batch3_descriptor((0x1D57, 0xFA65), collection, sizes, {4: 7})
                    info = self.scan()
                    self.assertEqual(info["input_report_sizes"], {str(k): v for k, v in sizes.items()})
                    self.assertEqual(info["input_report_proof"], "caps")
                    self.backend.device.assert_not_called()
                    if sizes != {3: 5}:
                        self.assert_no_listener(info, self.fa65_inventory(info))
                        self.assertNotIn(fixtures.PATH_A, self.case.cache)
                    else:
                        self.assertIsNotNone(F.Fa6xPassiveProvider()._pick(self.fa65_inventory(info)))

    def test_fa65_legacy_raw_fallback_requires_release_low_byte_16(self):
        for release in (0, 1, 0x15, 0x18, 0x1618, 0xFFFF):
            with self.subTest(release=release):
                self.configure(identity=(0x1D57, 0xFA65), release=release)
                info = self.scan()
                self.assertNotIn("input_report_sizes", info)
                self.assert_no_listener(info, self.fa65_inventory(info))
                self.backend.device.assert_not_called()

    def test_fa65_raw_getter_failure_is_fixed_and_does_not_open_listener(self):
        for collection, release, raw in self.fa65_routes():
            with self.subTest(collection=collection):
                self.configure(identity=(0x1D57, 0xFA65), collection=collection,
                               release=release, raw=raw)
                self.dev.get_report_descriptor.side_effect = OSError("PRIVATE-DEVICE-TEXT")
                info = self.scan()
                self.assertEqual(info["report_descriptor_error"], "descriptor read failed")
                self.assertNotIn("PRIVATE", str(info))
                self.assertNotIn(fixtures.PATH_A, self.case.cache)
                self.assert_no_listener(info, self.fa65_inventory(info))

    def test_sc650_native_exclusion_still_precedes_all_handle_opens(self):
        self.configure()
        native = r"\\?\hid#vid_373b&pid_1278&mi_01#native"
        self.assertEqual(self.case.scan([native]), [])
        self.case.kernel.CreateFileW.assert_not_called()
        self.backend.device.assert_not_called()
        with fixture([], []) as (bus, provider):
            self.assertEqual(provider.poll(), [])
            self.assertEqual(bus.opened, [])


if __name__ == "__main__":
    unittest.main()
