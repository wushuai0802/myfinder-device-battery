"""FA6x proof diagnostics at both server boundaries, using fake HID only."""
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "engine"))
sys.path.insert(0, str(ROOT / "engine" / "tests"))
from test_fa6x_passive import descriptor, fixture, report
from battery_hid import DESCRIPTOR_FAILURES, report_proof_summary
import server


class Fa6xServerDiagnosticsTests(unittest.TestCase):
    def setUp(self):
        self.engine = server.BatteryEngine([], include_native=True)
        self.addCleanup(self.engine.close)

    def accept(self, diagnostics, statuses=()):
        self.engine._accept("fa6x_passive|usb:a", "fa6x_passive", statuses,
                            server.utcnow(), "usb:a", 1, diagnostics=diagnostics)
        snapshot = self.engine._snapshot()
        self.assertEqual(snapshot["errors"], [])
        self.assertEqual((self.engine.failures, self.engine.retry_after), ({}, {}))
        self.assertNotIn("errorCode", self.engine.health["fa6x_passive|usb:a"])
        health, = snapshot["health"]
        self.assertLessEqual(len(health.get("errorCode", "")), 48)
        return health

    def test_fa65_proof_and_declared_survive_both_whitelists_with_compact_health(self):
        cases = (({"4": 5}, "descriptor", None,
                  "in:03 unproven;declared=out:none,in:04/5,..."),
                 ({"3": 8}, "failed", "collection maximum mismatch",
                  "in:03 unproven;declared=out:none,in:03/8,..."),
                 ({}, "failed", "collection not found",
                  "in:03 unproven;declared=out:none,in:none,..."))
        for declared, proof, error, expected in cases:
            info = descriptor(0xFA65, input_report_length=5, input_report_sizes=None,
                              input_report_proof=proof, report_descriptor_error=error,
                              declared_report_sizes={"input": declared, "output": {}, "feature": {}})
            with self.subTest(proof=proof, error=error), fixture([], [info]) as (bus, provider):
                self.assertEqual(provider.poll(), [])
                self.assertEqual((bus.opened, bus.reads), ([], []))
                full, = provider.diagnostics()
                self.assertIn("proof=", full)
                self.assertIn("; declared=", full)
                self.assertGreater(len(full), 48)
                self.assertEqual(server.reader_diagnostics(provider), [full])
            health = self.accept([full])
            self.assertEqual((health["state"], health["reason"]), ("unavailable", "no_measurement"))
            self.assertEqual(health["diagnostics"], [full])
            self.assertEqual(health["errorCode"], expected)

    def test_fa65_successful_descriptor_proof_can_measure_or_remain_silent(self):
        info = descriptor(0xFA65, input_report_length=5, input_report_sizes={"3": 5},
                          input_report_proof="descriptor",
                          declared_report_sizes={"input": {"3": 5}, "output": {}, "feature": {}})
        with fixture([report(67, state=0, length=5)], [info]) as (bus, provider):
            statuses = provider.poll()
            health = self.accept(server.reader_diagnostics(provider), statuses)
            self.assertEqual(health["state"], "ok")
            self.assertNotIn("errorCode", health)
            statuses = provider.poll()
            health = self.accept(server.reader_diagnostics(provider), statuses)
            self.assertEqual((health["state"], health["errorCode"]),
                             ("unavailable", "no valid battery notification"))
            self.assertEqual(bus.opened, [info["path"]])

    def test_fa6x_arbitrary_or_appended_text_is_rejected_at_both_boundaries(self):
        valid = ("input report 03 length unproven; proof=caps+descriptor failed; "
                 "descriptor=collection maximum mismatch; "
                 "declared=out:none,in:03/8,feature:none; not opened")
        unsafe = [valid + "; PRIVATE-PATH", " " + valid, valid + "\n",
                  valid.replace("proof=caps+descriptor failed", "proof=PRIVATE-PROOF"),
                  valid.replace("collection maximum mismatch", "PRIVATE-DEVICE-TEXT"),
                  valid.replace("in:03/8", "in:PRIVATE-PRODUCT"),
                  valid.replace("in:03/8", "in:03/PRIVATE-SERIAL"),
                  "PRIVATE-PATH PRIVATE-EXCEPTION", valid * 32, None, {"PRIVATE": "value"}]
        provider = SimpleNamespace(name="fa6x_passive", diagnostics=lambda: unsafe + [valid])
        self.assertEqual(server.reader_diagnostics(provider), [valid])
        health = self.accept(unsafe + [valid])
        self.assertEqual(health["diagnostics"], [valid])
        self.assertEqual(health["errorCode"], "in:03 unproven;declared=out:none,in:03/8,...")
        self.assertNotIn("PRIVATE", str(health))
        health = self.accept(unsafe)
        self.assertEqual(health["diagnostics"], [])
        self.assertNotIn("errorCode", health)

    def test_fa6x_all_known_proofs_and_bounded_numeric_declarations_are_accepted(self):
        sizes = {str(n): 4096 for n in range(256)}
        sizes["PRIVATE-KEY"] = "PRIVATE-LENGTH"
        info = {"declared_report_sizes": {direction: sizes for direction in ("input", "output", "feature")}}
        proofs = [(proof, None) for proof in (None, "caps", "descriptor")]
        proofs += [("failed", reason) for reason in sorted(DESCRIPTOR_FAILURES | {"unknown"})]
        for proof, reason in proofs:
            with self.subTest(proof=proof, reason=reason):
                info.update(input_report_proof=proof, report_descriptor_error=reason)
                line = ("input report 03 length unproven"
                        + report_proof_summary(info, "input", include_declared=True) + "; not opened")
                provider = SimpleNamespace(name="fa6x_passive", diagnostics=lambda: [line])
                self.assertEqual(server.reader_diagnostics(provider), [line])
                health = self.accept([line])
                self.assertEqual(health["diagnostics"], [line])
                self.assertIn("declared=", health["errorCode"])
                self.assertNotIn("PRIVATE", str(health))

    def test_existing_mchose_v3_compact_declared_health_is_unchanged(self):
        line = ("V3 input report 4D/64 length unproven; proof=caps+descriptor failed; "
                "descriptor=collection maximum mismatch; "
                "declared=out:4D/64,in:01/16,feature:none; nothing sent")
        self.engine._accept("mchose|usb:a", "mchose", [], server.utcnow(), "usb:a", 1,
                            diagnostics=[line])
        snapshot = self.engine._snapshot()
        health, = snapshot["health"]
        self.assertEqual(health["errorCode"], "in:4D unproven;declared=out:4D/64,in:01/16,...")
        self.assertLessEqual(len(health["errorCode"]), 48)
        self.assertEqual(health["diagnostics"], [line])
        self.assertEqual(snapshot["errors"], [])


if __name__ == "__main__":
    unittest.main()
