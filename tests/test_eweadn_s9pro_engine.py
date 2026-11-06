"""S9 PRO registration, descriptor-only discovery and isolated-reader behavior."""
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
import test_eweadn_s9pro as E
import test_topology_cache as T
from catalog import validate_catalog, verification_for
from isolation import wire_encode
from known_ids import build_index
from standardhid import Caps
import server


class EweadnS9ProBridgeTests(unittest.TestCase):
    def test_registration_catalog_generated_docs_and_exact_offline_identity(self):
        self.assertIn((E.P.name, "EweadnS9ProProvider"), server.PROVIDERS)
        self.assertEqual(server.PROVIDER_VIDS[E.P.name], (0x372E,))
        self.assertEqual(verification_for(E.P.name)[0], "C")
        self.assertEqual(validate_catalog()["errors"], [])
        index = build_index()
        self.assertNotIn(E.P.name, index["unparsed"])
        row, = [r for r in index["devices"] if r["provider"] == E.P.name]
        self.assertEqual((row["vendorId"], row["productId"], row["name"], row["kind"]),
                         (0x372E, 0x1026, "EWEADN S9 PRO 8K", "mouse"))
        legacy, = [r for r in index["devices"] if r["provider"] == "eweadn"]
        self.assertEqual((legacy["vendorId"], legacy["productId"]), (0x089D, 0x062F))

    def test_silence_and_io_failure_keep_unconfirmed_history_without_errors(self):
        engine = server.BatteryEngine([E.P()], include_native=True)
        self.addCleanup(engine.close)
        with E.fixture():
            empty = engine.poll()
        self.assertEqual((empty["devices"], empty["errors"]), ([], []))
        with E.fixture([E.reply(73, True)]):
            first, = engine.poll()["devices"]
        self.assertTrue(first["charging"])
        for frames, options in (([], {}), ([[0] * 64], {}), ([], {"error_at": "get"})):
            with self.subTest(options=options), E.fixture(frames, **options):
                snapshot = engine.poll()
            stale, = snapshot["devices"]
            self.assertEqual(stale["percent"], first["percent"])
            self.assertEqual(stale["confirmedAtUtc"], first["confirmedAtUtc"])
            self.assertEqual(stale["connectionState"], "unconfirmed")
            self.assertFalse(stale["fresh"])
            self.assertIsNone(stale["charging"])
            self.assertEqual(snapshot["errors"], [])
            self.assertFalse(engine.failures)
            self.assertFalse(engine.retry_after)
        with E.fixture([E.reply(72)]):
            awake, = engine.poll()["devices"]
        self.assertEqual(awake["percent"], 72)
        self.assertTrue(awake["fresh"])
        self.assertFalse(awake["charging"])

    def test_scoped_reader_preserves_both_collections_and_feature_proof(self):
        for ambiguous in (False, True):
            infos = [E.entry(), E.entry(path=b"ff06", usage_page=0xFF06,
                                       feature_report_sizes={"3" if ambiguous else "4": 64}),
                     E.entry(path=b"second-receiver", physical_id="receiver-b")]
            request = dict(action="query", sequence=1, provider=E.P.name,
                           physicalId="receiver-a", inventory=infos)
            data = (json.dumps(request, default=wire_encode) + "\n").encode()
            with self.subTest(ambiguous=ambiguous), E.fixture([E.reply()], infos) as bus, \
                    patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                    patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
                server.reader_child()
            response = json.loads(output.getvalue())
            self.assertEqual(response["candidateCount"], 1)
            self.assertFalse(response.get("errorCode"))
            self.assertEqual(len(response["statuses"]), 0 if ambiguous else 1)
            self.assertEqual(bus.opened, [] if ambiguous else [infos[0]["path"]])
            if not ambiguous:
                self.assertEqual(response["statuses"][0]["level"], 73)
            else:
                self.assertIn("ambiguous proven Feature 03 collection", " ".join(response["diagnostics"]))

    def test_short_replies_and_absence_preserve_history_without_errors(self):
        engine = server.BatteryEngine([E.P()], include_native=True)
        self.addCleanup(engine.close)
        absent_frames = []
        for presence in (0, 0x35, 0xFF):
            for level in (0, 73):
                frame = E.short_reply(level)
                frame[5] = presence
                absent_frames.append(frame)
        for frame in absent_frames:
            with self.subTest(frame=frame), E.fixture([frame]):
                snapshot = engine.poll()
            self.assertEqual((snapshot["devices"], snapshot["errors"]), ([], []))
        for level, charging in ((23, False), (98, True)):
            with self.subTest(level=level, charging=charging), E.fixture([E.short_reply(level, charging)]):
                snapshot = engine.poll()
            first, = snapshot["devices"]
            self.assertEqual((first["percent"], first["charging"]), (level, charging))
            self.assertEqual(first["measurementState"], "current")
            self.assertTrue(first["fresh"])
            self.assertEqual(snapshot["errors"], [])
        for frame in absent_frames:
            with self.subTest(frame=frame), E.fixture([frame]):
                snapshot = engine.poll()
            stale, = snapshot["devices"]
            self.assertEqual(stale["percent"], 98)
            self.assertEqual(stale["confirmedAtUtc"], first["confirmedAtUtc"])
            self.assertEqual(stale["connectionState"], "unconfirmed")
            self.assertFalse(stale["fresh"])
            self.assertIsNone(stale["charging"])
            self.assertEqual(snapshot["errors"], [])
            self.assertFalse(engine.failures)
            self.assertFalse(engine.retry_after)
        with E.fixture([E.short_reply(97)]):
            awake, = engine.poll()["devices"]
        self.assertEqual(awake["percent"], 97)
        self.assertTrue(awake["fresh"])
        self.assertFalse(awake["charging"])

    def test_unavailable_reader_exports_descriptor_failure_reason(self):
        info = E.entry(feature_report_sizes=None, feature_report_proof="failed",
                       report_descriptor_error="get_report_descriptor unavailable")
        request = dict(action="query", sequence=1, provider=E.P.name,
                       physicalId="receiver-a", inventory=[info])
        data = (json.dumps(request, default=wire_encode) + "\n").encode()
        with E.fixture(inventory=[info]) as bus, \
                patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
            server.reader_child()
        response = json.loads(output.getvalue())
        self.assertEqual(response["statuses"], [])
        self.assertIn("descriptor=get_report_descriptor unavailable", " ".join(response["diagnostics"]))
        self.assertEqual(bus.opened, [])

    def descriptor_fixture(self, rows):
        # Reuse the fake Windows environment, without changing its framework.
        case = T.DescriptorCacheTests()
        case.setUp()
        self.addCleanup(case.doCleanups)
        case.parent.side_effect = lambda path: "receiver-a"
        for path, info in rows.items():
            case.identities[path] = (info["vendor_id"], info["product_id"])

        def caps(data, pointer):
            info = rows[case.handles[data.value]]
            value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
            value.page, value.usage = info["usage_page"], info["usage"]
            value.feature = info["feature_report_length"]
            value.fv = len(info["feature_report_sizes"])
            return 0x00110000

        def fields(report_type, values, length, data):
            self.assertEqual(report_type, 2)
            info = rows[case.handles[data.value]]
            for field, (report_id, size) in zip(values, info["feature_report_sizes"].items()):
                field.report, field.bits, field.count = int(report_id), 8, size - 1
            return 0x00110000

        case.hid.HidP_GetCaps.side_effect = caps
        case.hid.HidP_GetValueCaps.side_effect = fields
        return case

    def test_runtime_descriptor_discovery_selects_only_the_proven_collection(self):
        for a, b in (({"3": 64}, {"4": 64}), ({"4": 64}, {"3": 64}),
                     ({"3": 64}, {"3": 64}), ({"4": 64}, {"4": 64})):
            rows = {T.PATH_A: E.entry(feature_report_sizes=a),
                    T.PATH_B: E.entry(usage_page=0xFF06, feature_report_sizes=b)}
            with self.subTest(a=a, b=b):
                case = self.descriptor_fixture(rows)
                infos = case.scan(rows)
                self.assertEqual([i["feature_report_sizes"] for i in infos], [a, b])
                expected = [path.encode() for path, info in rows.items()
                            if info["feature_report_sizes"].get("3") == 64]
                with E.fixture([E.reply()], infos) as bus:
                    statuses = E.P().poll()
                    self.assertEqual(bus.opened, expected if len(expected) == 1 else [])
                    self.assertEqual(len(statuses), int(len(expected) == 1))
                # Descriptor reuse never GET/SETs features or reads input.
                case.kernel.CreateFileW.reset_mock()
                self.assertEqual(case.scan(rows), infos)
                case.kernel.CreateFileW.assert_not_called()
                case.hid.HidD_GetFeature.assert_not_called()
                case.hid.HidD_SetFeature.assert_not_called()
                case.kernel.ReadFile.assert_not_called()
                case.kernel.WriteFile.assert_not_called()
                self.assertTrue(all(call.args[0] == 2 for call in case.hid.HidP_GetValueCaps.call_args_list))
                case.doCleanups()

    def test_failed_feature_proof_is_not_cached_and_recovers(self):
        case = self.descriptor_fixture({T.PATH_A: E.entry()})
        original = case.hid.HidP_GetValueCaps.side_effect
        for defect in ("failure", "alias", "non_byte", "partial"):
            with self.subTest(defect=defect):
                case.cache.clear()

                def broken(report_type, values, length, data):
                    result = original(report_type, values, length, data)
                    if defect == "failure":
                        return 0
                    if defect == "alias":
                        values[0].alias = 1
                    elif defect == "non_byte":
                        values[0].bits = 16
                    else:
                        ctypes.cast(length, ctypes.POINTER(ctypes.c_ushort))[0] = 0
                    return result

                case.hid.HidP_GetValueCaps.side_effect = broken
                first, = case.scan([T.PATH_A])
                self.assertIsNone(first["feature_report_sizes"])
                self.assertNotIn(T.PATH_A, case.cache)
                with E.fixture([E.reply()], [first]) as bus:
                    self.assertEqual(E.P().poll(), [])
                    self.assertEqual(bus.opened, [])
                case.hid.HidP_GetValueCaps.side_effect = original
                recovered, = case.scan([T.PATH_A])
                self.assertEqual(recovered["feature_report_sizes"], {"3": 64})

    def test_feature_discovery_does_not_expand_to_other_id_or_collection(self):
        for changes in (dict(product_id=0x103D), dict(product_id=0x106E), dict(product_id=0x1029),
                        dict(vendor_id=0x372F), dict(usage_page=1, usage=2),
                        dict(usage_page=1, usage=6), dict(usage_page=0xFF60), dict(usage=2)):
            with self.subTest(changes=changes):
                case = self.descriptor_fixture({T.PATH_A: E.entry(**changes)})
                info, = case.scan([T.PATH_A])
                self.assertNotIn("feature_report_sizes", info)
                case.hid.HidP_GetValueCaps.assert_not_called()
                case.hid.HidP_GetButtonCaps.assert_not_called()
                case.hid.HidD_GetFeature.assert_not_called()
                case.hid.HidD_SetFeature.assert_not_called()
                case.doCleanups()


if __name__ == "__main__":
    unittest.main()
