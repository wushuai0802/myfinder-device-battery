"""V3 power and its verification boundary through the public engine bridge."""
from pathlib import Path
import io
import json
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "engine" / "tests"))
from test_mchose_v3 import M, CHARGING, ULTRA_42, REQUEST, alternate_inventory, fixture, inventory
from device_pack import parse_pack
from isolation import wire_encode
from known_ids import build_index
import server


class MchoseV3EngineTests(unittest.TestCase):
    def test_two_usb_parents_choose_their_own_proven_alternate_and_keep_c_level(self):
        first, second = alternate_inventory("synthetic-a"), alternate_inventory("synthetic-b")
        infos = first + second
        public_ids = set()
        for collections in (first, second):
            parent = collections[0]["physical_id"]
            provider = M.MchoseProvider()
            with self.subTest(parent=parent), fixture([CHARGING], infos) as bus:
                status, = server.scoped_poll(provider, infos, parent)
                public = server.normalise(status, None, server.utcnow())
                self.assertEqual(bus.opened, [collections[1]["path"]])
                self.assertEqual(bus.sent, [REQUEST])
                self.assertEqual((public["percent"], public["verificationLevel"]), (85, "C"))
                self.assertIsNone(public["dpi"])
                self.assertEqual(status.physical_id, parent)
                public_ids.add(public["id"])
        self.assertEqual(len(public_ids), 2)
        # A provider given mixed parents cannot borrow one parent's collection
        # even if the other parent's alternate was omitted from the inventory.
        mixed = [first[0], second[1]]
        with fixture([CHARGING], mixed) as bus:
            self.assertEqual(M.MchoseProvider().poll(), [])
            self.assertEqual((bus.opened, bus.sent, bus.reads), ([], [], []))

    def test_disabled_1018_blocks_both_parents_without_affecting_original_1014(self):
        pack = parse_pack(json.dumps({
            "format": "myfinder-device-pack", "loader": 1, "packVersion": 2026110401,
            "keyId": "pack-2026a", "createdAt": "2026-11-04T10:00:00Z", "entries": [],
            "disabled": [{"provider": "mchose", "vendorId": "3837", "productId": "1018",
                          "reason": "synthetic test"}]}))
        self.assertTrue(pack.loaded, pack.rejected)
        infos = alternate_inventory("synthetic-a") + alternate_inventory("synthetic-b")
        for parent in {d["physical_id"] for d in infos}:
            provider = M.MchoseProvider()
            with self.subTest(parent=parent), fixture([CHARGING], infos) as bus, \
                    patch.object(provider, "poll", side_effect=AssertionError("disabled reader polled")):
                self.assertEqual(server.scoped_poll(provider, infos, parent, pack), [])
                self.assertEqual((bus.opened, bus.sent, bus.reads), ([], [], []))
        original = [dict(d, physical_id="old-1014") for d in inventory(0x1014)]
        with fixture([ULTRA_42], infos + original) as bus:
            status, = server.scoped_poll(M.MchoseProvider(), infos + original, "old-1014", pack)
            self.assertEqual(status.level, 42)
            self.assertEqual(bus.opened, [next(d["path"] for d in original if d["usage_page"] == 0xFF01)])
            self.assertEqual(bus.sent, [REQUEST])

    def test_child_alternate_proof_crosses_json_boundary_without_private_identity(self):
        infos = alternate_inventory("PRIVATE-SERIAL")
        infos[1]["input_report_sizes"] = None
        parent = infos[0]["physical_id"]
        request = dict(action="query", sequence=1, provider="mchose", physicalId=parent, inventory=infos)
        data = (json.dumps(request, default=wire_encode) + "\n").encode()
        with fixture([], infos) as bus, \
                patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
            server.reader_child()
            response = json.loads(output.getvalue())
            self.assertEqual((response["statuses"], response["candidateCount"]), ([], 1))
            self.assertIn("collection=FF0B:00C4", " ".join(response["diagnostics"]))
            self.assertNotIn("PRIVATE", output.getvalue())
            self.assertEqual((bus.opened, bus.sent, bus.reads), ([], [], []))

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
