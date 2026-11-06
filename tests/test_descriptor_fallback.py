"""Opt-in descriptor fallback with fake Windows DLLs and a metadata-only HID."""
import ctypes
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, call, patch

ENGINE = Path(__file__).resolve().parents[1] / "engine"
sys.path.insert(0, str(ENGINE))
sys.path.insert(0, str(ENGINE / "tests"))
import battery_hid as H
import topology
import test_topology_cache as T
from standardhid import Caps
from test_report_descriptor import RAPOO_DESCRIPTOR
from test_rapoo import R, fixture as rapoo_fixture, report
import test_moondrop as M


# Illustrative only; the real GM01 Pro descriptor has not been supplied.
MOONDROP_DESCRIPTOR = bytes.fromhex(
    "06 02 ff 09 02 a1 01 85 4b 75 08 95 3f 81 02 91 02 c0")


class DescriptorFallbackTests(unittest.TestCase):
    def setUp(self):
        self.case = T.DescriptorCacheTests()
        self.case.setUp()
        self.addCleanup(self.case.doCleanups)
        self.dev = Mock(spec=["open_path", "get_report_descriptor", "close"])
        self.backend = SimpleNamespace(device=Mock(return_value=self.dev))
        patcher = patch.object(H, "hid", self.backend)
        patcher.start()
        self.addCleanup(patcher.stop)
        self.configure()

    def configure(self, *, moondrop=False, failed=(0,), identity=None, collection=None):
        self.case.cache.clear()
        self.case.parent.side_effect = None
        self.case.parent.return_value = "receiver-a" if moondrop else "usb:receiver-a"
        self.case.batch3_descriptor(
            identity or ((0x35D8, 0x0133) if moondrop else (0x24AE, 0x1412)),
            collection or ((0xFF02, 2) if moondrop else (0xFF00, 2)),
            {75: 64} if moondrop else {7: 19}, {75: 64} if moondrop else {7: 19})
        original = self.case.hid.HidP_GetValueCaps.side_effect

        def fields(report_type, *args):
            return 0 if report_type in failed else original(report_type, *args)

        self.case.hid.HidP_GetValueCaps.side_effect = fields
        self.dev.get_report_descriptor.return_value = MOONDROP_DESCRIPTOR if moondrop else RAPOO_DESCRIPTOR

    def scan(self):
        info, = self.case.scan([T.PATH_A])
        # Both descriptor paths must remain metadata-only.
        self.case.hid.HidD_GetFeature.assert_not_called()
        self.case.hid.HidD_SetFeature.assert_not_called()
        self.case.kernel.ReadFile.assert_not_called()
        self.case.kernel.WriteFile.assert_not_called()
        return info

    def test_caps_success_never_creates_descriptor_handle_for_either_provider(self):
        for moondrop in (False, True):
            with self.subTest(moondrop=moondrop):
                self.configure(moondrop=moondrop, failed=())
                info = self.scan()
                self.assertEqual(info["input_report_proof"], "caps")
                if moondrop:
                    self.assertEqual(info["output_report_proof"], "caps")
                self.backend.device.assert_not_called()

    def test_rapoo_fallback_caches_proof_and_leaves_reader_passive(self):
        self.dev.get_report_descriptor.return_value = list(RAPOO_DESCRIPTOR)
        info = self.scan()
        self.assertEqual(info["input_report_sizes"], {"7": 19})
        self.assertEqual(info["input_report_proof"], "descriptor")
        self.assertNotIn("output_report_sizes", info)
        self.assertNotIn("feature_report_sizes", info)
        self.assertEqual(self.dev.mock_calls, [call.open_path(T.PATH_A.encode()),
                                              call.get_report_descriptor(), call.close()])
        self.assertEqual(self.scan(), info)
        self.backend.device.assert_called_once_with()
        self.assertNotIn("report_descriptor_error", info)
        with rapoo_fixture([report()], [info]) as bus:
            provider = R.RapooProvider()
            self.addCleanup(provider.close)
            self.assertEqual(provider.poll()[0].level, 73)
            self.assertEqual(bus.opened, [info["path"]])

    def test_moondrop_fills_only_missing_directions_with_one_descriptor_read(self):
        for failed in ((0,), (1,), (0, 1)):
            with self.subTest(failed=failed):
                self.configure(moondrop=True, failed=failed)
                self.dev.reset_mock()
                info = self.scan()
                for index, direction in enumerate(("input", "output")):
                    self.assertEqual(info[direction + "_report_sizes"], {"75": 64})
                    self.assertEqual(info[direction + "_report_proof"],
                                     "descriptor" if index in failed else "caps")
                self.dev.get_report_descriptor.assert_called_once_with()
                with M.fixture([M.reply()], [info]) as bus:
                    self.assertEqual(M.P().poll()[0].level, 73)
                    self.assertEqual(bus.writes, [M.M.make_request(64)])

    def test_exact_identity_collection_caps_and_native_exclusion_gates(self):
        for identity, collection in (
                ((0x24AE, 0xFFFF), (0xFF00, 2)), ((0x24AF, 0x1412), (0xFF00, 2)),
                ((0x24AE, 0x1412), (0xFF00, 1)), ((0x24AE, 0x1412), (1, 6)),
                ((0x35D8, 0x0133), (0xFF02, 1)), ((0x35D8, 0x0134), (0xFF02, 2)),
                ((0x373B, 0x1262), (0xFF90, 1)), ((0x320F, 0x5088), (0xFF1C, 0x92)),
                ((0x258A, 0x024F), (0xFF02, 2)), ((0x3554, 0xFA09), (0xFF04, 2))):
            with self.subTest(identity=identity, collection=collection):
                self.configure(identity=identity, collection=collection)
                self.scan()
                self.backend.device.assert_not_called()
        self.configure()
        self.case.hid.HidP_GetCaps.side_effect = lambda *args: 0
        self.scan()
        self.backend.device.assert_not_called()
        self.case.native_attributes.add(T.PATH_A)
        self.assertEqual(self.case.scan([T.PATH_A]), [])
        self.backend.device.assert_not_called()

    def test_non_none_caps_wrong_id_does_not_trigger_fallback(self):
        self.configure(failed=())
        original = self.case.hid.HidP_GetValueCaps.side_effect

        def wrong_id(report_type, values, *args):
            result = original(report_type, values, *args)
            values[0].report = 8
            return result

        self.case.hid.HidP_GetValueCaps.side_effect = wrong_id
        info = self.scan()
        self.backend.device.assert_not_called()
        with rapoo_fixture(inventory=[info]) as bus:
            provider = R.RapooProvider()
            self.assertEqual(provider.poll(), [])
            self.assertIn("proof=caps", provider.diagnostics()[0])
            self.assertEqual(bus.opened, [])

    def test_unavailable_method_skips_open_and_failed_proof_retries(self):
        self.backend.device.return_value = Mock(spec=["open_path", "close"])
        info = self.scan()
        self.backend.device.return_value.open_path.assert_not_called()
        self.assertEqual(info["report_descriptor_error"], "get_report_descriptor unavailable")
        self.assertNotIn(T.PATH_A, self.case.cache)
        self.backend.device.return_value = self.dev
        self.assertEqual(self.scan()["input_report_sizes"], {"7": 19})
        self.assertEqual(self.backend.device.call_count, 2)
        with patch.object(H, "hid", None):
            self.assertEqual(topology._read_report_descriptor(b"unused"),
                             (None, "get_report_descriptor unavailable"))

    def test_failed_metadata_operations_close_and_never_echo_exception_text(self):
        for operation in ("open_path", "get_report_descriptor", "close"):
            with self.subTest(operation=operation):
                self.configure()
                self.dev.reset_mock(side_effect=True)
                getattr(self.dev, operation).side_effect = OSError("private-path-and-bytes")
                info = self.scan()
                self.assertIsNone(info["input_report_sizes"])
                self.assertEqual(info["input_report_proof"], "failed")
                self.assertEqual(info["report_descriptor_error"],
                                 "descriptor close failed" if operation == "close" else "descriptor read failed")
                self.dev.close.assert_called_once_with()
                self.assertNotIn(T.PATH_A, self.case.cache)
                with rapoo_fixture(inventory=[info]) as bus:
                    provider = R.RapooProvider()
                    self.assertEqual(provider.poll(), [])
                    self.assertIn("caps+descriptor failed", provider.diagnostics()[0])
                    self.assertNotIn("private", " ".join(provider.diagnostics()))
                    self.assertEqual(bus.opened, [])

    def test_malformed_raw_missing_collection_and_length_disagreement_fail_closed(self):
        cases = [(b"\xfe\x00\x00", "long item"),
                 (RAPOO_DESCRIPTOR[:-1], "unbalanced collection"),
                 (RAPOO_DESCRIPTOR.replace(b"\x09\x02", b"\x09\x03", 1), "collection not found"),
                 (RAPOO_DESCRIPTOR.replace(b"\x95\x12", b"\x95\x11"), "collection maximum mismatch"),
                 (RAPOO_DESCRIPTOR * 2, "duplicate top-level collection"),
                 (123, "invalid descriptor bytes"), ([False], "invalid descriptor bytes"),
                 ([256], "invalid descriptor bytes"), ([], "descriptor length out of bounds")]
        for raw, reason in cases:
            with self.subTest(reason=reason):
                self.configure()
                self.dev.get_report_descriptor.return_value = raw
                info = self.scan()
                self.assertIsNone(info["input_report_sizes"])
                self.assertEqual(info["report_descriptor_error"], reason)
                self.assertNotIn(T.PATH_A, self.case.cache)

    def test_all_collection_maxima_must_agree_even_for_rapoo_input_only(self):
        for direction in ("input", "output", "feature"):
            with self.subTest(direction=direction):
                self.configure()
                original = self.case.hid.HidP_GetCaps.side_effect

                def caps(data, pointer):
                    result = original(data, pointer)
                    value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
                    setattr(value, direction, getattr(value, direction) + 1)
                    return result

                self.case.hid.HidP_GetCaps.side_effect = caps
                info = self.scan()
                self.assertIsNone(info["input_report_sizes"])
                self.assertEqual(info["report_descriptor_error"], "collection maximum mismatch")

    def test_parsed_direction_cannot_conflict_with_a_successful_caps_proof(self):
        self.configure(moondrop=True, failed=(1,))
        self.dev.get_report_descriptor.return_value = MOONDROP_DESCRIPTOR.replace(b"\x81\x02", b"\x81\x02\x85\x4c\x81\x02\x85\x4b")
        info = self.scan()
        self.assertEqual(info["input_report_sizes"], {"75": 64})
        self.assertEqual(info["input_report_proof"], "caps")
        self.assertIsNone(info["output_report_sizes"])
        self.assertEqual(info["report_descriptor_error"], "caps/descriptor disagreement")


if __name__ == "__main__":
    unittest.main()
