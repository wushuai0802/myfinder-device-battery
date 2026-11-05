"""GM01 Pro registration, bridge and fake Windows descriptor checks."""
import ctypes
import io
import json
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ENGINE = Path(__file__).resolve().parents[1] / "engine"
sys.path.insert(0, str(ENGINE))
sys.path.insert(0, str(ENGINE / "tests"))
import test_moondrop as E
import test_topology_cache as T
from catalog import validate_catalog, verification_for
from device_pack import BRAND_PROVIDERS
from isolation import wire_encode
from known_ids import build_index
from standardhid import Caps
import server


class MoondropBridgeTests(unittest.TestCase):
    def test_registration_catalog_offline_index_and_exact_candidate_identity(self):
        self.assertIn(("moondrop", "MoondropProvider"), server.PROVIDERS)
        self.assertEqual(server.PROVIDER_VIDS["moondrop"], (0x35D8,))
        self.assertIn("moondrop", BRAND_PROVIDERS)
        self.assertEqual(verification_for("moondrop")[0], "C")
        self.assertEqual(validate_catalog()["errors"], [])
        for root in (None, ENGINE):
            index = build_index(engine_root=root)
            self.assertNotIn("moondrop", index["unparsed"])
            row, = [r for r in index["devices"] if r["provider"] == "moondrop"]
            self.assertEqual((row["vendorId"], row["productId"], row["name"], row["kind"]),
                             (0x35D8, 0x0133, "MOONDROP GM01 Pro", "headset"))
        wrong = [E.entry(vendor_id=0x35D9), E.entry(product_id=0x012D)]
        self.assertEqual(server.recognized_candidate_count("moondrop", wrong), 0)
        self.assertEqual(server.recognized_candidate_count("moondrop", wrong + [E.entry()]), 1)

    def test_off_silence_and_io_failure_leave_only_unconfirmed_history_without_errors(self):
        engine = server.BatteryEngine([E.P()], include_native=True)
        self.addCleanup(engine.close)
        with E.fixture():
            empty = engine.poll()
        self.assertEqual((empty["devices"], empty["errors"]), ([], []))
        with E.fixture([E.reply()]):
            first, = engine.poll()["devices"]
        self.assertEqual((first["kind"], first["verificationLevel"], first["percent"]), ("headset", "C", 73))
        self.assertIsNone(first["charging"])
        self.assertFalse(first["chargingKnown"])
        for frames, options in (([], {}), ([E.reply(valid=0)], {}), ([], {"error_at": "read"})):
            with E.fixture(frames, **options):
                snapshot = engine.poll()
            stale, = snapshot["devices"]
            self.assertEqual(stale["percent"], 73)
            self.assertEqual(stale["confirmedAtUtc"], first["confirmedAtUtc"])
            self.assertEqual(stale["connectionState"], "unconfirmed")
            self.assertFalse(stale["fresh"])
            self.assertIsNone(stale["charging"])
            self.assertEqual(snapshot["errors"], [])
            self.assertFalse(engine.failures)
            self.assertFalse(engine.retry_after)

    def test_reader_child_preserves_descriptor_proof_and_scopes_physical_receiver(self):
        infos = [E.entry(), E.entry(path=b"other", physical_id="receiver-b")]
        request = dict(action="query", sequence=1, provider="moondrop", physicalId="receiver-a", inventory=infos)
        data = (json.dumps(request, default=wire_encode) + "\n").encode()
        with E.fixture([E.reply()], infos) as bus, \
                patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
            server.reader_child()
        response = json.loads(output.getvalue())
        self.assertFalse(response.get("errorCode"))
        self.assertEqual(response["candidateCount"], 1)
        self.assertEqual(response["statuses"][0]["level"], 73)
        self.assertEqual(bus.opened, [infos[0]["path"]])

    def descriptor_fixture(self, info):
        case = T.DescriptorCacheTests()
        case.setUp()
        self.addCleanup(case.doCleanups)
        case.parent.return_value = "receiver-a"
        case.parent.side_effect = None
        case.identities[T.PATH_A] = (info["vendor_id"], info["product_id"])

        def caps(data, pointer):
            value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
            value.page, value.usage = info["usage_page"], info["usage"]
            value.input, value.output = info["input_report_length"], info["output_report_length"]
            value.iv, value.ov = len(info["input_report_sizes"]), len(info["output_report_sizes"])
            return 0x00110000

        def fields(report_type, values, length, data):
            self.assertIn(report_type, (0, 1))
            sizes = info[("input" if report_type == 0 else "output") + "_report_sizes"]
            for field, (report_id, size) in zip(values, sizes.items()):
                field.report, field.bits, field.count = int(report_id), 8, size - 1
            return 0x00110000

        case.hid.HidP_GetCaps.side_effect = caps
        case.hid.HidP_GetValueCaps.side_effect = fields
        return case

    def test_descriptor_proof_survives_cache_without_any_device_commands(self):
        case = self.descriptor_fixture(E.entry())
        infos = case.scan([T.PATH_A])
        self.assertEqual(infos[0]["input_report_sizes"], {"75": 64})
        self.assertEqual(infos[0]["output_report_sizes"], {"75": 64})
        with E.fixture([E.reply()], infos) as bus:
            self.assertEqual(len(E.P().poll()), 1)
            self.assertEqual(bus.opened, [T.PATH_A.encode()])
        case.kernel.CreateFileW.reset_mock()
        self.assertEqual(case.scan([T.PATH_A]), infos)
        case.kernel.CreateFileW.assert_not_called()
        case.hid.HidD_GetFeature.assert_not_called()
        case.hid.HidD_SetFeature.assert_not_called()
        case.kernel.ReadFile.assert_not_called()
        case.kernel.WriteFile.assert_not_called()

    def test_failed_or_non_byte_proof_is_not_cached_and_never_opens_reader(self):
        for defect in ("failure", "alias", "non_byte", "partial", "wrong_id"):
            with self.subTest(defect=defect):
                case = self.descriptor_fixture(E.entry())
                original = case.hid.HidP_GetValueCaps.side_effect

                def broken(report_type, values, length, data):
                    result = original(report_type, values, length, data)
                    if defect == "failure":
                        return 0
                    if defect == "alias":
                        values[0].alias = 1
                    elif defect == "non_byte":
                        values[0].bits = 16
                    elif defect == "wrong_id":
                        values[0].report = 74
                    else:
                        ctypes.cast(length, ctypes.POINTER(ctypes.c_ushort))[0] = 0
                    return result

                case.hid.HidP_GetValueCaps.side_effect = broken
                infos = case.scan([T.PATH_A])
                with E.fixture([E.reply()], infos) as bus:
                    self.assertEqual(E.P().poll(), [])
                    self.assertEqual(bus.opened, [])
                if defect != "wrong_id":
                    self.assertNotIn(T.PATH_A, case.cache)
                    case.hid.HidP_GetValueCaps.side_effect = original
                    self.assertEqual(case.scan([T.PATH_A])[0]["input_report_sizes"], {"75": 64})
                case.doCleanups()

    def test_descriptor_inspection_does_not_expand_identity_or_collection(self):
        for changes in (dict(vendor_id=0x35D9), dict(product_id=0x012D), dict(usage_page=1, usage=6),
                        dict(usage_page=0xFF04), dict(usage=1)):
            with self.subTest(changes=changes):
                case = self.descriptor_fixture(E.entry(**changes))
                info, = case.scan([T.PATH_A])
                self.assertNotIn("input_report_sizes", info)
                self.assertNotIn("output_report_sizes", info)
                case.hid.HidP_GetValueCaps.assert_not_called()
                case.hid.HidP_GetButtonCaps.assert_not_called()
                case.doCleanups()


if __name__ == "__main__":
    unittest.main()
