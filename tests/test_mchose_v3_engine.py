"""V3 power and its verification boundary through the public engine bridge."""
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "engine" / "tests"))
from test_mchose_v3 import M, CHARGING, ULTRA_42, REQUEST, fixture, inventory
from known_ids import build_index
import server


class MchoseV3EngineTests(unittest.TestCase):
    def test_ultra_identity_c_level_and_safe_reader_diagnostics(self):
        row, = [r for r in build_index()["devices"] if r["provider"] == "mchose"
                and (r["vendorId"], r["productId"]) == (0x3837, 0x1014)]
        self.assertEqual(row["name"], "MCHOSE A7 V3 Ultra+")
        infos = [dict(d, physical_id="v3-test", product_string="private-product")
                 for d in inventory(0x1014)]
        provider = M.MchoseProvider()
        with fixture([ULTRA_42], infos):
            status, = server.scoped_poll(provider, infos, "v3-test")
            public = server.normalise(status, None, server.utcnow())
        self.assertEqual((public["name"], public["percent"], public["verificationLevel"]), ("private-product", 42, "C"))
        provider._diag.append("private-path legacy log")
        diagnostic = " ".join(server.reader_diagnostics(provider))
        self.assertIn("proof=caps", diagnostic)
        self.assertIn("C / 待实机验证", diagnostic)
        self.assertNotIn("#276", diagnostic)
        self.assertNotIn("private", diagnostic)
        with fixture([], infos) as bus, patch.object(bus, "open_path", side_effect=OSError("private-path")):
            self.assertEqual(provider.poll()[0].online, False)
        diagnostic = " ".join(server.reader_diagnostics(provider))
        self.assertIn("V3 power I/O failed", diagnostic)
        self.assertNotIn("private", diagnostic)
        with fixture([], []):
            provider.poll()
        self.assertEqual(server.reader_diagnostics(provider), [])

    def test_reported_models_and_empty_name_fallbacks_remain_c_level(self):
        for pid, product, expected in (
                (0x1014, "MCHOSE K7 V2 Ultra+", "MCHOSE K7 V2 Ultra+"),
                (0x1018, "MCHOSE A5 V3 Ultra+(3955)", "MCHOSE A5 V3 Ultra+(3955)"),
                (0x1014, "", "MCHOSE A7 V3 Ultra+"),
                (0x1018, "", "MCHOSE V3 8K 接收器")):
            infos = [dict(d, physical_id="v3-test", product_string=product) for d in inventory(pid)]
            provider = M.MchoseProvider()
            with self.subTest(pid=pid, product=product), fixture([CHARGING], infos) as bus:
                status, = server.scoped_poll(provider, infos, "v3-test")
                public = server.normalise(status, None, server.utcnow())
                self.assertEqual(bus.sent, [REQUEST])
            self.assertEqual((public["name"], public["percent"], public["verificationLevel"]), (expected, 85, "C"))
            self.assertIn("待本项目实机验证", public["verificationLabel"])
            self.assertIsNone(public["dpi"])
            self.assertNotIn("#276", " ".join(server.reader_diagnostics(provider)))

    def test_scoped_reader_keeps_c_level_and_no_dpi_and_marks_stale_power(self):
        infos = [dict(d, physical_id="v3-test") for d in inventory()]
        provider = M.MchoseProvider()
        with fixture([CHARGING], infos):
            status, = server.scoped_poll(provider, infos, "v3-test")
            public = server.normalise(status, None, server.utcnow())
            cached, = server.scoped_poll(provider, infos, "v3-test")
            historical = server.normalise(cached, public, server.utcnow())
        self.assertEqual((public["percent"], public["charging"], public["verificationLevel"]),
                         (85, True, "C"))
        self.assertIn("待本项目实机验证", public["verificationLabel"])
        self.assertIsNone(public["dpi"])
        self.assertFalse(historical["fresh"])
        self.assertFalse(historical["chargingKnown"])
        self.assertEqual(historical["measuredAtUtc"], public["measuredAtUtc"])
        self.assertEqual(status.physical_id, "v3-test")


if __name__ == "__main__":
    unittest.main()
