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
    def test_issue127_consumer_reading_keeps_headset_grade_and_unknown_charging(self):
        engine = server.BatteryEngine([E.P()], include_native=True)
        self.addCleanup(engine.close)
        infos = E.issue127_inventory()
        with E.fixture([E.issue127_reply()], infos) as bus:
            snapshot = engine.poll()
        self.assertEqual(len(snapshot["devices"]), 1)
        first, = snapshot["devices"]
        self.assertEqual((first["percent"], first["kind"], first["verificationLevel"]),
                         (50, "headset", "C"))
        self.assertIsNone(first["charging"])
        self.assertFalse(first["chargingKnown"])
        self.assertEqual(bus.opened, [infos[1]["path"]])
        self.assertEqual(bus.writes, [E.M.make_request(64)])
        self.assertEqual(snapshot["errors"], [])
        for frames, options in (([], {}), ([E.reply(valid=0)], {}), ([], {"error_at": "read"})):
            with E.fixture(frames, infos, **options):
                snapshot = engine.poll()
            stale, = snapshot["devices"]
            self.assertEqual(stale["percent"], 50)
            self.assertEqual(stale["confirmedAtUtc"], first["confirmedAtUtc"])
            self.assertEqual(stale["connectionState"], "unconfirmed")
            self.assertFalse(stale["fresh"])
            self.assertIsNone(stale["charging"])
            self.assertEqual(snapshot["errors"], [])
            self.assertFalse(engine.failures)
            self.assertFalse(engine.retry_after)

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

    def test_not_opened_reasons_reach_worker_and_feedback_without_errors(self):
        cases = [([E.entry(usage=1)], "missing eligible GM01 Pro collection"),
                 ([E.entry(), E.entry(path=b"private-duplicate")], "missing or ambiguous FF02:0002 collection"),
                 ([E.entry(physical_id="collection:private")], "physical parent unresolved"),
                 ([E.entry(path=b"")], "collection path/physical parent unresolved"),
                 ([E.entry(input_report_sizes=None, input_report_proof="failed",
                           report_descriptor_error="get_report_descriptor unavailable")],
                  "input report 4B length unproven; proof=caps+descriptor failed; "
                  "descriptor=get_report_descriptor unavailable"),
                 ([E.entry(output_report_sizes=None, output_report_proof="failed",
                           report_descriptor_error="collection maximum mismatch")],
                  "output report 4B length unproven; proof=caps+descriptor failed; "
                  "descriptor=collection maximum mismatch"),
                 ([E.entry(input_report_sizes={"76": 64}, input_report_proof="descriptor",
                           declared_report_sizes={"input": {"76": 64}, "output": {"75": 64},
                                                  "feature": {}})],
                  "input report 4B length unproven; proof=descriptor; "
                  "declared=out:4B/64,in:4C/64,feature:none; not opened; nothing sent")]
        for infos, expected in cases:
            request = dict(action="query", sequence=1, provider="moondrop", inventory=infos,
                           physicalId=infos[0]["physical_id"])
            data = (json.dumps(request, default=wire_encode) + "\n").encode()
            with self.subTest(expected=expected), E.fixture(inventory=infos) as bus, \
                    patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                    patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
                server.reader_child()
                response = json.loads(output.getvalue())
                self.assertEqual(response["statuses"], [])
                self.assertIn(expected, response["diagnostics"][0])
                self.assertEqual((bus.opened, bus.writes, bus.reads), ([], [], []))
                self.assertNotIn("private", json.dumps(response))
            engine = server.BatteryEngine([E.P()], include_native=True)
            self.addCleanup(engine.close)
            engine._accept("reader", "moondrop", [], server.utcnow(), "receiver-a", 1,
                           diagnostics=response["diagnostics"])
            health, = engine._snapshot()["health"]
            self.assertEqual(health["state"], "unavailable")
            self.assertIn(expected, health["diagnostics"][0])
            self.assertLessEqual(len(health["errorCode"]), 48)
            if "input report" in expected:
                self.assertTrue(health["errorCode"].startswith("in:4B unproven;declared="))
            elif "output report" in expected:
                self.assertTrue(health["errorCode"].startswith("out:4B unproven;declared="))
            else:
                self.assertIn(expected, health["errorCode"])
            self.assertEqual(engine._snapshot()["errors"], [])
            self.assertFalse(engine.failures)
            self.assertFalse(engine.retry_after)
            with E.fixture([E.reply()]) as bus:
                engine.poll()
            recovered = next(h for h in engine._snapshot()["health"] if h["provider"] == "moondrop"
                             and h["state"] == "ok")
            self.assertNotIn("errorCode", recovered)

    def test_long_bounded_declarations_survive_health_and_keep_safety_suffix(self):
        info = E.entry(input_report_sizes=None, input_report_proof="failed",
                       report_descriptor_error="caps/descriptor disagreement",
                       declared_report_sizes={d: {str(i): 4096 for i in range(256)}
                                              for d in ("input", "output", "feature")})
        with E.fixture(inventory=[info]):
            provider = E.P()
            self.assertEqual(provider.poll(), [])
        diagnostic, = provider.diagnostics()
        self.assertGreater(len(diagnostic), 200)
        self.assertLess(len(diagnostic), server.READER_DIAGNOSTIC_LIMIT)
        engine = server.BatteryEngine([], include_native=True)
        self.addCleanup(engine.close)
        for name in ("moondrop", "rapoo"):
            engine._accept(name, name, [], server.utcnow(), "receiver-a", 1, diagnostics=[diagnostic])
        snapshot = engine._snapshot()
        self.assertEqual(snapshot["errors"], [])
        for health in snapshot["health"]:
            self.assertTrue(health["errorCode"].startswith("in:4B unproven;declared="))
            self.assertLessEqual(len(health["errorCode"]), 48)
            self.assertTrue(health["errorCode"].endswith("..."))
            self.assertEqual(health["diagnostics"], [diagnostic])
            self.assertTrue(health["diagnostics"][0].endswith("not opened; nothing sent"))

    def descriptor_fixture(self, info):
        case = T.DescriptorCacheTests()
        case.setUp()
        self.addCleanup(case.doCleanups)
        by_path = {T.PATH_A: info} if "vendor_id" in info else info
        case.parent.side_effect = lambda path: by_path[path]["physical_id"]
        for path, row in by_path.items():
            case.identities[path] = (row["vendor_id"], row["product_id"])

        def caps(data, pointer):
            info = by_path[case.handles[data.value]]
            value = ctypes.cast(pointer, ctypes.POINTER(Caps)).contents
            value.page, value.usage = info["usage_page"], info["usage"]
            value.input, value.output = info["input_report_length"], info["output_report_length"]
            value.iv, value.ov = len(info["input_report_sizes"]), len(info["output_report_sizes"])
            return 0x00110000

        def fields(report_type, values, length, data):
            self.assertIn(report_type, (0, 1))
            info = by_path[case.handles[data.value]]
            sizes = info[("input" if report_type == 0 else "output") + "_report_sizes"]
            for field, (report_id, size) in zip(values, sizes.items()):
                field.report, field.bits, field.count = int(report_id), 8, size - 1
            return 0x00110000

        case.hid.HidP_GetCaps.side_effect = caps
        case.hid.HidP_GetValueCaps.side_effect = fields
        return case

    def test_consumer_caps_cache_and_reader_child_keep_collection_and_receiver_proofs_separate(self):
        battery = E.consumer_entry()
        for sibling in (E.consumer_entry(input_report_sizes={"2": 64}, output_report_sizes={"2": 64}),
                        E.consumer_entry(physical_id="receiver-b")):
            with self.subTest(sibling=sibling):
                case = self.descriptor_fixture({T.PATH_A: battery, T.PATH_B: sibling})
                infos = case.scan([T.PATH_A, T.PATH_B])
                first, second = infos
                self.assertEqual(first["input_report_sizes"], {"75": 64, "84": 64})
                self.assertEqual(first["output_report_sizes"], {"75": 64, "84": 64})
                self.assertEqual(first["input_report_proof"], "caps")
                self.assertEqual(first["output_report_proof"], "caps")
                self.assertEqual(second["input_report_sizes"], sibling["input_report_sizes"])
                self.assertEqual(second["output_report_sizes"], sibling["output_report_sizes"])
                case.kernel.CreateFileW.reset_mock()
                self.assertEqual(case.scan([T.PATH_A, T.PATH_B]), infos)
                case.kernel.CreateFileW.assert_not_called()
                # No descriptor discovery operation may exchange reports.
                case.hid.HidD_GetFeature.assert_not_called()
                case.hid.HidD_SetFeature.assert_not_called()
                case.kernel.ReadFile.assert_not_called()
                case.kernel.WriteFile.assert_not_called()
                request = dict(action="query", sequence=1, provider="moondrop",
                               physicalId="receiver-a", inventory=infos)
                data = (json.dumps(request, default=wire_encode) + "\n").encode()
                with E.fixture([E.issue127_reply()], infos) as bus, \
                        patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                        patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
                    server.reader_child()
                response = json.loads(output.getvalue())
                self.assertFalse(response.get("errorCode"))
                self.assertEqual(len(response["statuses"]), 1)
                self.assertEqual(response["statuses"][0]["level"], 50)
                self.assertEqual(bus.opened, [T.PATH_A.encode()])
                self.assertEqual(bus.closed, bus.opened)
                self.assertEqual(bus.writes, [E.M.make_request(64)])
                case.doCleanups()

    def test_two_consumer_caps_do_not_share_input_and_output_proofs(self):
        case = self.descriptor_fixture({
            T.PATH_A: E.consumer_entry(output_report_sizes={"2": 64}),
            T.PATH_B: E.consumer_entry(input_report_sizes={"2": 64})})
        infos = case.scan([T.PATH_A, T.PATH_B])
        self.assertEqual(infos[0]["output_report_sizes"], {"2": 64})
        self.assertEqual(infos[1]["input_report_sizes"], {"2": 64})
        self.assertEqual(case.scan([T.PATH_A, T.PATH_B]), infos)
        with E.fixture([E.issue127_reply()], infos) as bus:
            self.assertEqual(E.P().poll(), [])
            self.assertEqual((bus.opened, bus.writes, bus.reads), ([], [], []))

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
