"""Batch-139 registration, descriptor transport and silence/history semantics."""
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
import test_vgn_dragonfly3 as V
import test_ninjutso_legacy as N
import test_topology_cache as T
from catalog import verification_for
from device_pack import BRAND_PROVIDERS
from isolation import wire_encode
from known_ids import build_index
from standardhid import Caps
import server

CASES = ((V, 0x391D, 0x1A01, "VgnDragonfly3Provider"),
         (N, 0x1915, 0xAE1C, "NinjutsoLegacyProvider"))


def frames(module, level=73):
    return ([N.profile_reply()] if module is N else []) + [module.reply(level)]


class Batch139BridgeTests(unittest.TestCase):
    def test_registration_grade_c_and_only_the_requested_ids(self):
        index = build_index()
        for module, vid, pid, class_name in CASES:
            name = module.P.name
            self.assertIn((name, class_name), server.PROVIDERS)
            self.assertEqual(server.PROVIDER_VIDS[name], (vid,))
            self.assertIn(name, BRAND_PROVIDERS)
            self.assertEqual(verification_for(name)[0], "C")
            self.assertNotIn(name, index["unparsed"])
            row, = [row for row in index["devices"] if row["provider"] == name]
            self.assertEqual((row["vendorId"], row["productId"], row["kind"]), (vid, pid, "mouse"))

    def test_silence_keeps_history_without_errors_and_wake_refreshes(self):
        for module, *_ in CASES:
            with self.subTest(provider=module.P.name):
                engine = server.BatteryEngine([module.P()], include_native=True)
                self.addCleanup(engine.close)
                with module.fixture():
                    empty = engine.poll()
                self.assertEqual((empty["devices"], empty["errors"]), ([], []))
                with module.fixture(frames(module)):
                    first, = engine.poll()["devices"]
                self.assertEqual((first["percent"], first["verificationLevel"]), (73, "C"))
                no_readings = [[], [module.reply(73, 1)]] if module is V else [[], [N.profile_reply(), []]]
                for quiet in no_readings:
                    with module.fixture(quiet):
                        snapshot = engine.poll()
                    stale, = snapshot["devices"]
                    self.assertEqual(stale["percent"], 73)
                    self.assertEqual(stale["confirmedAtUtc"], first["confirmedAtUtc"])
                    self.assertEqual(stale["connectionState"], "unconfirmed")
                    self.assertFalse(stale["fresh"])
                    self.assertEqual(snapshot["errors"], [])
                    self.assertFalse(engine.failures)
                    self.assertFalse(engine.retry_after)
                with module.fixture(frames(module, 72)):
                    awake, = engine.poll()["devices"]
                self.assertEqual(awake["percent"], 72)
                self.assertTrue(awake["fresh"])

    def test_scoped_reader_preserves_descriptor_proof(self):
        for module, *_ in CASES:
            info = module.entry()
            request = dict(action="query", sequence=1, provider=module.P.name,
                           physicalId=info["physical_id"], inventory=[info])
            data = (json.dumps(request, default=wire_encode) + "\n").encode()
            with self.subTest(provider=module.P.name), module.fixture(frames(module)) as bus, \
                    patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                    patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
                server.reader_child()
            response = json.loads(output.getvalue())
            status, = response["statuses"]
            self.assertEqual(status["level"], 73)
            self.assertEqual(response["candidateCount"], 1)
            self.assertFalse(response.get("errorCode"))
            self.assertEqual(bus.opened, [info["path"]])

    def descriptor_fixture(self, module):
        # Reuse the existing fake Windows DLL setup without changing its tests.
        case = T.DescriptorCacheTests()
        case.setUp()
        self.addCleanup(case.doCleanups)
        info = module.entry()
        case.identities[T.PATH_A] = (info["vendor_id"], info["product_id"])

        def caps(data, pointer):
            value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
            value.page, value.usage = info["usage_page"], info["usage"]
            if module is V:
                value.input = value.output = 65
                value.ib = value.ob = 1
            else:
                value.feature, value.fv = 704, 2
            return 0x00110000

        def fields(report_type, values, length, data):
            if module is V:
                values[0].report = 0
                values[0].is_range = 1
                values[0].usage_min, values[0].usage_max = 0, 255
            else:
                self.assertEqual(report_type, 2)
                for value, report_id, count in zip(values, (4, 5), (703, 31)):
                    value.report, value.bits, value.count = report_id, 8, count
            return 0x00110000

        case.hid.HidP_GetCaps.side_effect = caps
        case.hid.HidP_GetValueCaps.side_effect = fields
        case.hid.HidP_GetButtonCaps.side_effect = fields
        return case

    def test_descriptor_sizes_are_discovered_and_cached_without_io(self):
        for module, *_ in CASES:
            with self.subTest(provider=module.P.name):
                case = self.descriptor_fixture(module)
                first, = case.scan([T.PATH_A])
                case.kernel.CreateFileW.reset_mock()
                second, = case.scan([T.PATH_A])
                self.assertEqual(first, second)
                case.kernel.CreateFileW.assert_not_called()
                directions = ("input", "output") if module is V else ("feature",)
                for direction in directions:
                    self.assertEqual(first[direction + "_report_sizes"],
                                     module.entry()[direction + "_report_sizes"])
                case.hid.HidD_GetFeature.assert_not_called()
                case.hid.HidD_SetFeature.assert_not_called()
                case.kernel.ReadFile.assert_not_called()
                case.kernel.WriteFile.assert_not_called()
                case.doCleanups()

    def test_failed_descriptor_proof_is_not_cached_and_other_pids_are_not_inspected(self):
        for module, vid, pid, _ in CASES:
            with self.subTest(provider=module.P.name):
                case = self.descriptor_fixture(module)
                getter = case.hid.HidP_GetButtonCaps if module is V else case.hid.HidP_GetValueCaps
                getter.side_effect = lambda *args: 0
                first, = case.scan([T.PATH_A])
                key = "input_report_sizes" if module is V else "feature_report_sizes"
                self.assertIsNone(first[key])
                self.assertNotIn(T.PATH_A, case.cache)
                case.identities[T.PATH_A] = (vid, 0x1A03 if module is V else 0xAE8C)
                getter.reset_mock()
                other, = case.scan([T.PATH_A])
                getter.assert_not_called()
                self.assertNotIn(key, other)
                case.doCleanups()


if __name__ == "__main__":
    unittest.main()
