"""VGN kc2 registry, known_ids, candidate gating, and health projection tests."""
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
import test_vgn_kc2 as V
from catalog import verification_for
from device_pack import BRAND_PROVIDERS
from isolation import wire_encode
from known_ids import build_index
import server


class VgnKc2EngineTests(unittest.TestCase):
    private_text = (r"C:\private\vgn-path", "secret-serial-1153", "private-raw-1153")

    def assert_safe_health(self, engine, snapshot, summary):
        health, = snapshot["health"]
        self.assertEqual(health.get("errorCode"), summary)
        self.assertLessEqual(len(summary), 48)
        self.assertTrue(health["diagnostics"])
        self.assertNotIn("diagnosticSummary", health)
        self.assertEqual(snapshot["errors"], [])
        self.assertFalse(engine.failures)
        self.assertFalse(engine.retry_after)
        public = json.dumps(snapshot)
        for private in self.private_text:
            self.assertNotIn(private, public)
            self.assertNotIn(json.dumps(private)[1:-1], public)
        return health

    def test_registration_and_grade_c(self):
        self.assertIn(("vgn_kc2", "VgnKc2Provider"), server.PROVIDERS)
        self.assertEqual(server.PROVIDER_VIDS["vgn_kc2"], (0x391D,))
        self.assertIn("vgn_kc2", BRAND_PROVIDERS)
        self.assertIn("vgn_kc2", server.NO_CHARGING_EVIDENCE)
        self.assertIn("vgn_kc2", server.READER_DIAGNOSTICS)
        self.assertEqual(verification_for("vgn_kc2")[0], "C")
        self.assertNotIn("vgn_kc2", server.DPI_PROVIDERS)

    def test_known_ids_index_and_candidate_count(self):
        index = build_index(engine_root=ENGINE)
        rows = [row for row in index["devices"] if row["provider"] == "vgn_kc2"]
        self.assertEqual(len(rows), 9)
        self.assertEqual(index["vendors"]["vgn_kc2"], [0x391D])
        self.assertNotIn("vgn_kc2", index["unparsed"])
        self.assertNotIn("vgn_kc2", index["partial"])
        self.assertTrue(all(row["kind"] == "mouse" for row in rows))

        unsupported = [V.entry(product_id=pid) for pid in (0x1A01, 0x1A04, 0x2A01, 0x9999)]
        self.assertEqual(server.recognized_candidate_count("vgn_kc2", unsupported), 0)
        supported = [V.entry(product_id=0x1A21), V.entry("two", product_id=0x1A20)]
        self.assertEqual(server.recognized_candidate_count("vgn_kc2", unsupported + supported), 2)

    def test_unproven_reports_and_asleep_publish_safe_health_summary(self):
        unproven = V.entry(
            path=self.private_text[0].encode(),
            serial_number=self.private_text[1],
            input_report_sizes=None,
            input_report_proof="failed",
            report_descriptor_error=self.private_text[0],
            declared_report_sizes={"input": {"180": 64, "182": 21}, "output": {"179": 64}, "feature": {}},
        )
        engine = server.BatteryEngine([V.P()], include_native=True)
        self.addCleanup(engine.close)
        with V.fixture([V.reply()], [unproven]) as bus:
            snapshot = engine.poll()
        self.assertEqual((bus.opened, bus.writes, bus.reads), ([], [], []))
        self.assertEqual(snapshot["devices"], [])
        self.assert_safe_health(engine, snapshot, "report sizes unproven")

        with V.fixture([V.reply(level=0)], [V.entry()]) as bus:
            snapshot = engine.poll()
        self.assertEqual(snapshot["devices"], [])
        self.assert_safe_health(engine, snapshot, "device asleep/offline")

    def test_reader_child_roundtrip(self):
        info = V.entry(product_id=0x1A21)
        request = dict(action="query", sequence=5, provider="vgn_kc2",
                       physicalId=info["physical_id"], inventory=[info])
        data = (json.dumps(request, default=wire_encode) + "\n").encode()
        with V.fixture([V.reply(level=69)], [info]), \
                patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
            server.reader_child()
        response = json.loads(output.getvalue())
        self.assertEqual(response["sequence"], 5)
        self.assertEqual([s["level"] for s in response["statuses"]], [69])
        self.assertFalse(response.get("errorCode"))


if __name__ == "__main__":
    unittest.main()
