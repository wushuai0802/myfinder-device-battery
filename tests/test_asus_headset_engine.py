"""Delta II child/manager/registration regressions; synthetic, no real HID access."""
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
sys.path.insert(0, str(ENGINE.parent / "tests"))
import _hidstub  # noqa: F401 -- before provider/server imports
import test_asus_headset as F
import server
from catalog import load_catalog, validate_catalog, verification_for
from device_pack import BRAND_PROVIDERS, parse_pack
from isolation import wire_encode
from known_ids import build_index
from providers.base import DeviceStatus
from providers.profiles import profiles
from test_engine_reliability import FakePool


def disabled_pack():
    return parse_pack(json.dumps({
        "format": "myfinder-device-pack", "loader": 1, "packVersion": 2026111201,
        "keyId": "pack-2026a", "createdAt": "2026-11-12T10:00:00Z", "entries": [],
        "disabled": [{"provider": "asus_headset", "vendorId": "0B05", "productId": "1AFA",
                      "reason": "synthetic test"}]}))


class AsusHeadsetBridgeTests(unittest.TestCase):
    def engine(self, *args, **kwargs):
        engine = server.BatteryEngine(*args, include_native=True, **kwargs)
        self.addCleanup(engine.close)
        return engine

    def child(self, physical, inventory, pack=None):
        request = dict(action="query", sequence=1, provider="asus_headset",
                       physicalId=physical, inventory=inventory)
        if pack is not None:
            request["devicePack"] = pack.document
        data = (json.dumps(request, default=wire_encode) + "\n").encode()
        with patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
            server.reader_child()
        response = json.loads(output.getvalue())
        self.assertFalse(response.get("errorCode"), response)
        return response

    def accept(self, engine, physical, response):
        engine._accept("asus_headset|" + physical, "asus_headset",
                       [DeviceStatus(**s) for s in response["statuses"]],
                       response["checkedAtUtc"], physical, response["candidateCount"],
                       diagnostics=response.get("diagnostics", []))

    def test_registration_grade_c_catalog_and_offline_index(self):
        self.assertIn(("asus_headset", "AsusHeadsetProvider"), server.PROVIDERS)
        self.assertEqual(server.PROVIDER_VIDS["asus_headset"], (0x0B05,))
        self.assertIsInstance(server.provider_class("asus_headset")(), F.P)
        self.assertIn("asus_headset", server.READER_DIAGNOSTICS)
        self.assertIn("asus_headset", BRAND_PROVIDERS)
        self.assertNotIn("asus_headset", server.DPI_PROVIDERS)
        self.assertFalse(any(p.provider == "asus_headset" for p in profiles()))
        self.assertEqual(verification_for("asus_headset")[0], "C")
        self.assertEqual(validate_catalog()["errors"], [])
        row, = [r for r in load_catalog()["readers"] if r["provider"] == "asus_headset"]
        self.assertEqual((row["name"], row["verificationLevel"], row["kinds"]),
                         ("ROG Delta II 耳机（2.4 GHz 接收器）", "C", ["headset"]))
        for boundary in ("仅0B05:1AFA", "FF00:0001", "CC 12 07", "不回退", "冷启动"):
            self.assertIn(boundary, row["matchScope"])
        docs = (ENGINE.parent / "docs/compatibility.md").read_text(encoding="utf-8-sig")
        self.assertIn("ROG Delta II 耳机", docs)
        for index in (build_index(), build_index(engine_root=ENGINE)):
            self.assertNotIn("asus_headset", index["unparsed"])
            self.assertNotIn("asus_headset", index["partial"])
            hit, = [r for r in index["devices"] if r["provider"] == "asus_headset"]
            self.assertEqual((hit["vendorId"], hit["productId"], hit["kind"], hit["name"]),
                             (0x0B05, 0x1AFA, "headset", "ROG Delta II"))
            self.assertEqual(index["vendors"]["asus_headset"], [0x0B05])
        from providers import asus
        self.assertNotIn(0x1AFA, asus.KNOWN)
        self.assertEqual(asus.REQUEST, [0, 0x12, 7])
        self.assertEqual(asus.PACKET_LENGTH, 65)

    def test_child_scopes_one_parent_and_one_literal_query(self):
        infos = [F.entry("one"), F.entry("two")]
        for physical in ("one", "two"):
            with self.subTest(physical=physical), F.fixture(
                    [F.battery(77), F.charging(1)], infos) as bus:
                response = self.child(physical, infos)
            status, = response["statuses"]
            self.assertEqual((status["level"], status["physical_id"], status["kind"]),
                             (77, physical, "headset"))
            self.assertEqual(response["candidateCount"], 1)
            self.assertEqual(bus.writes, [(F.entry(physical)["path"], F.BATTERY_REQUEST),
                                          (F.entry(physical)["path"], F.CHARGING_REQUEST)])
            self.assertEqual(bus.reads[0][0], 64)
            self.assertEqual(bus.opened, bus.closed)

    def test_child_manager_current_card_charging_and_privacy(self):
        engine = self.engine([])
        infos = [F.entry(serial_number="synthetic-private-detail")]
        engine._inventory_groups = {"dongle-a": infos}
        with F.fixture([F.battery(77), F.charging(1)], infos):
            response = self.child("dongle-a", infos)
        self.accept(engine, "dongle-a", response)
        snapshot = engine._snapshot()
        card, = snapshot["devices"]
        self.assertEqual((card["percent"], card["kind"], card["provider"],
                          card["verificationLevel"]),
                         (77, "headset", "asus_headset", "C"))
        self.assertEqual(card["name"], "ROG Delta II")
        self.assertTrue(card["chargingKnown"] and card["charging"] and card["fresh"])
        self.assertEqual(card["measurementState"], "current")
        self.assertEqual((card["vendorId"], card["productId"]), (0x0B05, 0x1AFA))
        self.assertIsNone(card["dpi"])
        self.assertEqual(snapshot["errors"], [])
        self.assertFalse(engine.failures or engine.retry_after)
        public = json.dumps(snapshot)
        for private in ("synthetic-private-detail", "dongle-a", "ff00", "same-serial"):
            self.assertNotIn(private, public)

    def test_diagnostics_allow_only_fixed_messages(self):
        reason = "DELTA II report CC length unproven; not opened"
        provider = F.P()
        provider._diag = ["synthetic-private-detail", reason + "; synthetic-private-detail",
                          None, {}, reason]
        self.assertEqual(server.reader_diagnostics(provider), [reason])
        self.assertTrue(all(len(s) <= 48 for s in server.ASUS_HEADSET_DIAGNOSTICS))
        self.assertTrue(all(len(s) <= 48 for s in server.ASUS_HEADSET_DIAGNOSTICS.values()))
        self.assertEqual(server.ASUS_HEADSET_DIAGNOSTICS[reason],
                         "DELTA II report CC length unproven")


    def test_fixed_diagnostics_reach_health_without_backoff(self):
        cases = [([F.entry(input_report_sizes=None)], [], {}, "DELTA II report CC length unproven"),
                 ([F.entry(usage_page=0xFF13)], [], {}, "DELTA II collection mismatch"),
                 ([F.entry(physical_id="collection:dongle-a")], [], {},
                  "DELTA II USB parent unproven"),
                 ([F.entry(), F.entry(path=b"dongle-a-second")], [], {},
                  "DELTA II collection ambiguous"),
                 ([F.entry()], [], {"error_at": "open"}, "DELTA II open failed"),
                 ([F.entry()], [], {"error_at": "write"},
                  "DELTA II query write failed; no retry"),
                 ([F.entry()], [], {}, "DELTA II no battery echo"),
                 ([F.entry()], [[0xCC, 0xFF, 0xAA] + [0] * 61], {},
                  "DELTA II device error FF AA"),
                 ([F.entry()], [[0] * 64], {}, "DELTA II empty reply or asleep"),
                 ([F.entry()], [F.battery(0), F.charging(0)], {},
                  "DELTA II zero battery; no new reading"),
                 ([F.entry()], [F.battery(101)], {}, "DELTA II battery out of range"),
                 ([F.entry()], [], {"error_at": "read"}, "DELTA II input read failed")]
        for infos, frames, options, summary in cases:
            physical = infos[0]["physical_id"]
            with self.subTest(summary=summary), F.fixture(frames, infos, **options):
                response = self.child(physical, infos)
            engine = self.engine([])
            self.accept(engine, physical, response)
            snapshot = engine._snapshot()
            self.assertEqual(snapshot["devices"], [])
            self.assertEqual(snapshot["health"][0]["errorCode"], summary)
            self.assertEqual(snapshot["errors"], [])
            self.assertFalse(engine.failures or engine.retry_after)
            self.assertTrue(all(line in server.ASUS_HEADSET_DIAGNOSTICS
                                for line in response["diagnostics"]))
            self.assertNotIn("same-serial", json.dumps(snapshot))

    def test_pack_disable_blocks_manager_child_and_direct_without_new_profile(self):
        pack = disabled_pack()
        self.assertTrue(pack.loaded, pack.rejected)
        infos = [F.entry("one"), F.entry("two")]
        self.assertEqual(pack.select_inventory("asus_headset", infos), [])
        self.assertEqual(pack.select_inventory("asus", infos), infos)
        pool = FakePool(infos)
        engine = self.engine(process_factory=pool.factory, device_pack=pack)
        engine.providers = ["asus_headset"]
        with F.fixture([F.battery(77), F.charging(1)], infos) as bus, \
                patch.object(engine._hidlist, "interface_paths", return_value=frozenset()):
            self.assertEqual(engine.poll()["devices"], [])
            self.assertFalse(any(n == "asus_headset" for n, _ in pool.calls))
            self.assertEqual(bus.writes, [])
        with F.fixture([F.battery(77), F.charging(1)], infos) as bus, \
                patch.object(F.P, "poll") as poll:
            response = self.child("one", infos, pack)
            poll.assert_not_called()
            self.assertEqual(response["statuses"], [])
            self.assertEqual(response["candidateCount"], 0)
            self.assertEqual(bus.writes, [])
        self.assertFalse(any(p.provider == "asus_headset" for p in profiles()))


if __name__ == "__main__":
    unittest.main()

