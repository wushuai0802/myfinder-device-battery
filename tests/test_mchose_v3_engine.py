"""V3 power and its verification boundary through the public engine bridge."""
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "engine" / "tests"))
from test_mchose_v3 import M, CHARGING, fixture, inventory
import server


class MchoseV3EngineTests(unittest.TestCase):
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
