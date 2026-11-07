"""MCHOSE V3/S9 PRO proof gates with synthetic descriptors and fake HID only."""
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
import test_mchose_v3 as M
import test_eweadn_s9pro as E
from isolation import wire_decode, wire_encode
import json
import server
from standardhid import Caps


V3_DESCRIPTOR = bytes.fromhex("06 01 ff 09 01 a1 01 85 4d 75 08 95 3f 81 02 91 02 c0")
S9_DESCRIPTOR = bytes.fromhex("06 00 ff 09 01 a1 01 85 03 75 08 95 3f b1 02 c0")


class ProofFallback2Tests(unittest.TestCase):
    def setUp(self):
        self.case = T.DescriptorCacheTests()
        self.case.setUp()
        self.addCleanup(self.case.doCleanups)
        self.case.parent.side_effect = lambda path: "receiver-a"
        self.dev = Mock(spec=["open_path", "get_report_descriptor", "close"])
        self.backend = SimpleNamespace(device=Mock(return_value=self.dev))
        patcher = patch.object(H, "hid", self.backend)
        patcher.start()
        self.addCleanup(patcher.stop)

    def configure(self, *, pid=0x1014, s9=False, failed=(0, 1), collection=None,
                  identity=None, report_id=None):
        self.case.cache.clear()
        self.dev.reset_mock(side_effect=True)
        self.backend.device.reset_mock()
        self.backend.device.return_value = self.dev
        self.case.identities[T.PATH_A] = identity or ((0x372E, 0x1026) if s9 else (0x3837, pid))
        page, usage = collection or ((0xFF00, 1) if s9 else (0xFF01, 1))
        report_id = report_id if report_id is not None else (3 if s9 else 0x4D)

        def caps(data, pointer):
            value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
            value.page, value.usage = page, usage
            if s9:
                value.feature, value.fv = 64, 1
            else:
                value.input, value.output, value.iv, value.ov = 64, 64, 1, 1
            return 0x00110000

        def fields(report_type, values, length, data):
            if report_type in failed:
                return 0
            values[0].report, values[0].bits, values[0].count = report_id, 8, 63
            return 0x00110000

        self.case.hid.HidP_GetCaps.side_effect = caps
        self.case.hid.HidP_GetValueCaps.side_effect = fields
        raw = S9_DESCRIPTOR if s9 else V3_DESCRIPTOR
        self.dev.get_report_descriptor.return_value = raw[:1] + page.to_bytes(2, "little") + raw[3:]

    def scan(self):
        info, = self.case.scan([T.PATH_A])
        self.case.hid.HidD_GetFeature.assert_not_called()
        self.case.hid.HidD_SetFeature.assert_not_called()
        self.case.kernel.ReadFile.assert_not_called()
        self.case.kernel.WriteFile.assert_not_called()
        return info

    def assert_reader(self, infos, *, s9=False, accepted=False):
        fixture = E.fixture if s9 else M.fixture
        frame = E.reply() if s9 else M.ULTRA_42
        with fixture([frame], infos) as bus:
            provider = E.P() if s9 else M.M.MchoseProvider()
            statuses = server.scoped_poll(provider, infos, "receiver-a")
            self.assertEqual(len(statuses), int(accepted))
            self.assertEqual(bus.opened, [infos[0]["path"]] if accepted else [])
            self.assertEqual(bus.writes if s9 else bus.sent,
                             [E.REQUEST if s9 else M.REQUEST] if accepted else [])
            return " ".join(server.reader_diagnostics(provider))

    def test_v3_both_ids_caps_and_descriptor_proof_survive_json_and_cache(self):
        for pid in (0x1018, 0x1014):
            for failed in ((), (0,), (1,), (0, 1)):
                with self.subTest(pid=pid, failed=failed):
                    self.configure(pid=pid, failed=failed)
                    info = self.scan()
                    for index, direction in enumerate(("input", "output")):
                        self.assertEqual(info[direction + "_report_sizes"], {"77": 64})
                        self.assertEqual(info[direction + "_report_proof"],
                                         "descriptor" if index in failed else "caps")
                    self.assertEqual(self.scan(), info)
                    self.assertEqual(self.backend.device.call_count, int(bool(failed)))
                    if failed:
                        self.assertEqual(self.dev.mock_calls, [call.open_path(T.PATH_A.encode()),
                                                              call.get_report_descriptor(), call.close()])
                    decoded = json.loads(json.dumps(info, default=wire_encode), object_hook=wire_decode)
                    diagnostic = self.assert_reader([decoded], accepted=True)
                    self.assertIn("proof=descriptor" if failed else "proof=caps", diagnostic)

    def test_s9_both_pages_fallback_requires_one_proven_feature_collection(self):
        for page in (0xFF00, 0xFF06):
            for failed in ((), (2,)):
                with self.subTest(page=page, failed=failed):
                    self.configure(s9=True, failed=failed, collection=(page, 1))
                    info = self.scan()
                    self.assertEqual(info["feature_report_sizes"], {"3": 64})
                    self.assertEqual(info["feature_report_proof"], "descriptor" if failed else "caps")
                    self.assertEqual(self.scan(), info)
                    self.assertEqual(self.backend.device.call_count, int(bool(failed)))
                    other = dict(info, path=b"other", usage_page=0xFF06 if page == 0xFF00 else 0xFF00,
                                 feature_report_sizes={"4": 64})
                    diagnostic = self.assert_reader([info, other], s9=True, accepted=True)
                    self.assertIn("proof=descriptor" if failed else "proof=caps", diagnostic)
                    other["feature_report_sizes"] = {"3": 64}
                    self.assertIn("ambiguous proven Feature 03", self.assert_reader([info, other], s9=True))

    def test_bad_descriptor_or_unavailable_method_never_enables_io_and_retries(self):
        for s9 in (False, True):
            for defect, reason in (("malformed", "long item"), ("length", "collection maximum mismatch"),
                                   ("collection", "collection not found"),
                                   ("unavailable", "get_report_descriptor unavailable")):
                with self.subTest(s9=s9, defect=defect):
                    self.configure(s9=s9, failed=(2,) if s9 else (0, 1))
                    raw = self.dev.get_report_descriptor.return_value
                    if defect == "unavailable":
                        self.backend.device.return_value = Mock(spec=["open_path", "close"])
                    else:
                        self.dev.get_report_descriptor.return_value = (
                            b"\xfe\x00\x00" if defect == "malformed" else
                            raw.replace(b"\x95\x3f", b"\x95\x3e") if defect == "length" else
                            raw.replace(b"\x09\x01", b"\x09\x02"))
                    info = self.scan()
                    self.assertEqual(info["report_descriptor_error"], reason)
                    self.assertNotIn(T.PATH_A, self.case.cache)
                    diagnostic = self.assert_reader([info], s9=s9)
                    self.assertIn("descriptor=" + reason, diagnostic)
                    if defect == "unavailable":
                        self.backend.device.return_value.open_path.assert_not_called()
                    self.backend.device.return_value = self.dev
                    self.dev.get_report_descriptor.return_value = raw
                    self.assert_reader([self.scan()], s9=s9, accepted=True)

    def test_wrong_report_id_is_not_proof_even_with_correct_maxima(self):
        for s9 in (False, True):
            for fallback in (False, True):
                with self.subTest(s9=s9, fallback=fallback):
                    self.configure(s9=s9, failed=((2,) if s9 else (0, 1)) if fallback else (), report_id=9)
                    raw = self.dev.get_report_descriptor.return_value
                    self.dev.get_report_descriptor.return_value = raw.replace(
                        b"\x85\x03" if s9 else b"\x85\x4d", b"\x85\x09")
                    info = self.scan()
                    self.assertEqual(self.backend.device.call_count, int(fallback))
                    diagnostic = self.assert_reader([info], s9=s9)
                    self.assertIn("length unproven", diagnostic)

    def test_other_id_usage_and_failed_caps_never_fetch_raw_descriptor(self):
        for identity, collection in (
                ((0x3837, 0x1013), (0xFF01, 1)), ((0x3837, 0x100B), (0xFF01, 1)),
                ((0x3838, 0x1014), (0xFF01, 1)), ((0x3837, 0x1014), (0xFF0B, 0xC4)),
                ((0x3837, 0x1018), (0xFF01, 2)), ((0x3837, 0x1014), (1, 6)),
                ((0x372E, 0x1027), (0xFF00, 1)), ((0x372F, 0x1026), (0xFF06, 1)),
                ((0x372E, 0x1026), (0xFF06, 2)), ((0x372E, 0x1026), (0xFF60, 1)),
                ((0x1915, 0xAE1C), (0xFFA0, 1)), ((0x089D, 0x062F), (0xFF01, 0x10))):
            with self.subTest(identity=identity, collection=collection):
                self.configure(identity=identity, collection=collection, failed=(0, 1, 2))
                self.scan()
                self.backend.device.assert_not_called()
        for s9 in (False, True):
            self.configure(s9=s9, failed=(0, 1, 2))
            self.case.hid.HidP_GetCaps.side_effect = lambda *args: 0
            info = self.scan()
            self.backend.device.assert_not_called()
            self.assert_reader([info], s9=s9)

    def test_41e4_by_keyboard_collections_use_descriptor_fallback_only_for_2001(self):
        by_descriptor = bytes.fromhex(
            "06 04 ff 09 02 a1 01 85 13 75 08 95 13 81 02 91 02 c0")
        for page in (0xFF04, 0xFF02):
            with self.subTest(page=page):
                self.configure(identity=(0x41E4, 0x2001), collection=(page, 2),
                               failed=(0, 1), report_id=0x13)

                def caps(data, pointer):
                    value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
                    value.page, value.usage = page, 2
                    value.input, value.output, value.iv, value.ov = 20, 20, 1, 1
                    return 0x00110000

                self.case.hid.HidP_GetCaps.side_effect = caps
                self.dev.get_report_descriptor.return_value = (
                    by_descriptor[:1] + page.to_bytes(2, "little") + by_descriptor[3:])
                info = self.scan()
                self.assertEqual(info["input_report_sizes"], {"19": 20})
                self.assertEqual(info["output_report_sizes"], {"19": 20})
                self.assertEqual(info["input_report_proof"], "descriptor")
                self.assertEqual(info["output_report_proof"], "descriptor")
        self.configure(identity=(0x41E4, 0x2004), collection=(0xFF04, 2), failed=(0, 1))
        self.scan()
        self.backend.device.assert_not_called()


if __name__ == "__main__":
    unittest.main()
