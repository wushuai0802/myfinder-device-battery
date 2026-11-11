"""OMNI child/manager/registration regressions; synthetic, no real HID access."""
from datetime import datetime, timedelta, timezone
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
import test_asus_omni as F
import server
from catalog import load_catalog, verification_for
from device_pack import BRAND_PROVIDERS, parse_pack
from isolation import IsolationError, wire_encode
from known_ids import build_index
from providers.base import DeviceStatus
from providers.profiles import profiles
from test_engine_reliability import FakePool


def disabled_pack():
    return parse_pack(json.dumps({
        "format": "myfinder-device-pack", "loader": 1, "packVersion": 2026111101,
        "keyId": "pack-2026a", "createdAt": "2026-11-11T10:00:00Z", "entries": [],
        "disabled": [{"provider": "asus_omni", "vendorId": "0B05", "productId": "1ACE",
                      "reason": "synthetic test"}]}))


class AsusOmniBridgeTests(unittest.TestCase):
    def engine(self, *args, **kwargs):
        engine = server.BatteryEngine(*args, include_native=True, **kwargs)
        self.addCleanup(engine.close)
        return engine

    def child(self, physical, inventory, pack=None, checked=None):
        request = dict(action="query", sequence=1, provider="asus_omni",
                       physicalId=physical, inventory=inventory)
        if pack is not None:
            request["devicePack"] = pack.document
        data = (json.dumps(request, default=wire_encode) + "\n").encode()
        with patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                patch.object(server.sys, "stdout", new_callable=io.StringIO) as output, \
                patch.object(server, "utcnow", return_value=checked or server.utcnow()):
            server.reader_child()
        response = json.loads(output.getvalue())
        self.assertFalse(response.get("errorCode"), response)
        return response

    def accept(self, engine, physical, response):
        engine._accept("asus_omni|" + physical, "asus_omni",
                       [DeviceStatus(**s) for s in response["statuses"]],
                       response["checkedAtUtc"], physical, response["candidateCount"],
                       diagnostics=response.get("diagnostics", []))

    def test_registration_grade_c_exact_offline_index_and_partial_model_flag(self):
        self.assertIn(("asus_omni", "AsusOmniProvider"), server.PROVIDERS)
        self.assertEqual(server.PROVIDER_VIDS["asus_omni"], (0x0B05,))
        self.assertIsInstance(server.provider_class("asus_omni")(), F.P)
        self.assertIn("asus_omni", server.READER_DIAGNOSTICS)
        self.assertIn("asus_omni", BRAND_PROVIDERS)
        self.assertNotIn("asus_omni", server.DPI_PROVIDERS)
        self.assertFalse(any(p.provider == "asus_omni" for p in profiles()))
        self.assertEqual(verification_for("asus_omni")[0], "C")
        row, = [r for r in load_catalog()["readers"] if r["provider"] == "asus_omni"]
        self.assertEqual((row["name"], row["verificationLevel"], row["kinds"]),
                         ("ROG OMNI 接收器（鼠标）", "C", ["mouse"]))
        for boundary in ("仅鼠标", "经 OMNI 连接的键盘暂不支持", "型号名不区分", "待实机验证"):
            self.assertIn(boundary, row["matchScope"])
        for index in (build_index(), build_index(engine_root=ENGINE)):
            self.assertNotIn("asus_omni", index["unparsed"])
            self.assertNotIn("asus_omni", index["partial"])
            row, = [r for r in index["devices"] if r["provider"] == "asus_omni"]
            self.assertEqual((row["vendorId"], row["productId"], row["kind"], row["name"]),
                             (0x0B05, 0x1ACE, "mouse", "ROG OMNI 接收器（鼠标）"))
            self.assertTrue(row["requiresModelMatch"])
            self.assertEqual(index["vendors"]["asus_omni"], [0x0B05])

    def test_child_scopes_one_parent_and_one_literal_query(self):
        infos = [F.entry("one"), F.entry("two"), F.entry("unknown", product_id=0x1ACF),
                 F.entry("other", vendor_id=0x0B06)]
        for physical in ("one", "two"):
            with self.subTest(physical=physical), F.fixture([F.reply()], infos) as bus:
                response = self.child(physical, infos)
                status, = response["statuses"]
                self.assertEqual((status["level"], status["physical_id"], status["kind"]),
                                 (77, physical, "mouse"))
                self.assertEqual(response["candidateCount"], 1)
                self.assertEqual(bus.writes, [(F.entry(physical)["path"], F.REQUEST)])
                self.assertEqual(bus.reads, [(F.entry(physical)["path"], 64, 300)])
                self.assertEqual(bus.opened, bus.closed)

    def test_child_manager_current_card_charging_and_privacy(self):
        engine = self.engine([])
        infos = [F.entry(serial_number="synthetic-private-detail")]
        engine._inventory_groups = {"receiver-a": infos}
        with F.fixture([F.reply(77, 2)], infos):
            response = self.child("receiver-a", infos)
        self.accept(engine, "receiver-a", response)
        snapshot = engine._snapshot()
        card, = snapshot["devices"]
        self.assertEqual((card["percent"], card["kind"], card["provider"], card["verificationLevel"]),
                         (77, "mouse", "asus_omni", "C"))
        self.assertEqual(card["name"], "ROG OMNI 接收器（鼠标）")
        self.assertTrue(card["chargingKnown"] and card["charging"] and card["fresh"])
        self.assertEqual(card["measurementState"], "current")
        self.assertEqual((card["vendorId"], card["productId"]), (0x0B05, 0x1ACE))
        self.assertEqual(card["confirmedAtUtc"], response["checkedAtUtc"])
        self.assertIsNone(card["dpi"])
        self.assertEqual(snapshot["errors"], [])
        self.assertFalse(engine.failures or engine.retry_after)
        public = json.dumps(snapshot)
        for private in ("synthetic-private-detail", "receiver-a", "vid_0b05", "mi_02"):
            self.assertNotIn(private, public)

    def test_fixed_diagnostic_cases_reach_child_manager_and_health_without_backoff(self):
        error, empty = F.reply(), F.reply()
        error[1:3], empty[1:4] = [0xFF, 0xAA], [0, 0, 0]
        cases = [([F.entry(input_report_sizes=None)], [], {}, "OMNI report 03/64 unproven"),
                 ([F.entry(usage_page=0xFF00)], [], {}, "OMNI collection mismatch"),
                 ([F.entry(physical_id="collection:receiver-a")], [], {}, "OMNI USB parent unproven"),
                 ([F.entry(), F.entry(path=F.entry()["path"] + b"-second")], [], {}, "OMNI collection ambiguous"),
                 ([F.entry()], [], {"error_at": "open"}, "OMNI open failed"),
                 ([F.entry()], [], {"error_at": "write"}, "OMNI battery query write failed; no retry"),
                 ([F.entry()], [], {}, "OMNI no echo within read limit"),
                 ([F.entry()], [error], {}, "OMNI device error FF AA"),
                 ([F.entry()], [empty], {}, "OMNI empty reply or asleep; no new reading"),
                 ([F.entry()], [F.reply(0, 1)], {}, "OMNI zero battery; no new reading"),
                 ([F.entry()], [F.reply(101)], {}, "OMNI battery out of range"),
                 ([F.entry()], [], {"error_at": "read"}, "OMNI input read failed")]
        for infos, frames, options, summary in cases:
            # scoped_poll filters by parent; supply the unresolved parent to
            # exercise its own refusal rather than an empty scoped inventory.
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
            self.assertTrue(all(line in server.ASUS_OMNI_DIAGNOSTICS for line in response["diagnostics"]))
            self.assertNotIn("synthetic-private-detail", json.dumps(snapshot))

    def test_diagnostics_allow_only_fixed_messages_no_private_suffix_or_nonstrings(self):
        reason = "OMNI report 03/64 unproven; not opened"
        provider = F.P()
        provider._diag = ["synthetic-private-detail", reason + "; synthetic-private-detail", None, {}, reason]
        self.assertEqual(server.reader_diagnostics(provider), [reason])
        engine = self.engine([])
        engine._accept("asus_omni|one", "asus_omni", [], server.utcnow(), "one", 1,
                       diagnostics=provider._diag)
        snapshot = engine._snapshot()
        self.assertEqual(snapshot["health"][0]["errorCode"], "OMNI report 03/64 unproven")
        self.assertNotIn("synthetic-private-detail", json.dumps(snapshot))
        self.assertTrue(all(len(s) <= 48 for s in server.ASUS_OMNI_DIAGNOSTICS))
        self.assertTrue(all(len(s) <= 48 for s in server.ASUS_OMNI_DIAGNOSTICS.values()))

    def test_invalid_silent_or_refused_queries_preserve_old_times_not_new_measurements(self):
        origin = datetime.now(timezone.utc) - timedelta(seconds=30)
        stamps = [(origin + timedelta(seconds=i)).isoformat().replace("+00:00", "Z") for i in range(10)]
        engine = self.engine([])
        with F.fixture([F.reply(77, 1)]):
            self.accept(engine, "receiver-a", self.child("receiver-a", [F.entry()], checked=stamps[0]))
        first, = engine._snapshot()["devices"]
        cases = [({}, []), ({}, [F.reply(0)]), ({}, [F.reply(0, 1)]), ({}, [F.reply(101)]),
                 ({}, [[0] * 64]), ({}, [F.reply()[:-1]]),
                 (dict(input_report_sizes=None), [F.reply()]), (dict(usage_page=0xFF00), [F.reply()])]
        for i, (changes, frames) in enumerate(cases, 1):
            infos = [F.entry(**changes)]
            with self.subTest(changes=changes, frames=frames), F.fixture(frames, infos):
                self.accept(engine, "receiver-a", self.child("receiver-a", infos, checked=stamps[i]))
            snapshot = engine._snapshot()
            old, = snapshot["devices"]
            self.assertEqual((old["percent"], old["measurementState"]), (77, "cached"))
            for field in ("confirmedAtUtc", "measuredAtUtc", "queriedAtUtc"):
                self.assertEqual(old[field], first[field])
            self.assertFalse(old["fresh"] or old["chargingKnown"])
            self.assertIsNone(old["charging"])
            self.assertEqual(snapshot["errors"], [])
            self.assertFalse(engine.failures or engine.retry_after)

    def test_two_parent_cards_survive_identical_percentages_and_cached_combinations(self):
        infos = [F.entry("one"), F.entry("two")]
        for current in ((True, True), (True, False), (False, True), (False, False)):
            with self.subTest(current=current):
                engine = self.engine([])
                engine._inventory_groups = {i["physical_id"]: [i] for i in infos}
                for info in infos:
                    with F.fixture([F.reply(77)], infos):
                        self.accept(engine, info["physical_id"], self.child(info["physical_id"], infos))
                before = {d["physicalId"]: d for d in engine._snapshot()["devices"]}
                for info, present in zip(infos, current):
                    if not present:
                        with F.fixture([], infos):
                            self.accept(engine, info["physical_id"], self.child(info["physical_id"], infos))
                snapshot = engine._snapshot()
                self.assertEqual(len(snapshot["devices"]), 2)
                self.assertEqual(snapshot["suppressed"], [])
                self.assertEqual(len({d["id"] for d in snapshot["devices"]}), 2)
                for info, present in zip(infos, current):
                    card = next(d for d in snapshot["devices"] if d["physicalId"] == server.public_id(info["physical_id"]))
                    self.assertEqual(card["measurementState"], "current" if present else "cached")
                    self.assertEqual(card["confirmedAtUtc"], before[card["physicalId"]]["confirmedAtUtc"])
                    self.assertEqual(card["refreshScope"], "device")

    def test_pack_disable_blocks_manager_child_and_direct_without_new_profile(self):
        pack = disabled_pack()
        self.assertTrue(pack.loaded, pack.rejected)
        infos = [F.entry("one"), F.entry("two")]
        self.assertEqual(pack.select_inventory("asus_omni", infos), [])
        self.assertEqual(pack.select_inventory("asus", infos), infos)
        pool = FakePool(infos)
        engine = self.engine(process_factory=pool.factory, device_pack=pack)
        engine.providers = ["asus_omni"]
        with F.fixture([F.reply()], infos) as bus, \
                patch.object(engine._hidlist, "interface_paths", return_value=frozenset()):
            self.assertEqual(engine.poll()["devices"], [])
            self.assertFalse(any(n == "asus_omni" for n, _ in pool.calls))
            self.assertEqual(bus.calls, [])
        with F.fixture([F.reply()], infos) as bus, patch.object(F.P, "poll") as poll:
            response = self.child("one", infos, pack)
            poll.assert_not_called()
            self.assertEqual(response["statuses"], [])
            self.assertEqual(response["candidateCount"], 0)
            self.assertEqual(bus.calls, [])
        direct = self.engine([F.P()], device_pack=pack)
        with F.fixture([F.reply()], infos) as bus:
            self.assertEqual(direct.poll()["devices"], [])
            self.assertEqual(bus.calls, [])
        original_glob = Path.glob

        def glob(path, pattern):
            return iter([path / "synthetic.json"]) if path.name == "pack" else original_glob(path, pattern)

        with patch.object(Path, "glob", glob), patch("known_ids.load_pack", return_value=pack):
            self.assertFalse(any(r["provider"] == "asus_omni" for r in build_index()["devices"]))
        self.assertFalse(any(p.provider == "asus_omni" for p in profiles()))

    def test_existing_child_budget_and_failure_backoff_are_unchanged(self):
        info = F.entry("one")
        pool = FakePool([info], {("asus_omni", "one"): (0, IsolationError("timeout"))})
        engine = self.engine(process_factory=pool.factory)
        engine.providers = ["asus_omni"]
        with patch.object(engine._hidlist, "interface_paths", return_value=frozenset()):
            engine.poll()
            self.assertIn(("asus_omni", "one", 7), pool.budgets)
            self.assertEqual(engine.failures["asus_omni|one"], 1)
            self.assertIn("asus_omni|one", engine.retry_after)
            count = pool.calls.count(("asus_omni", "one"))
            engine.poll()
            self.assertEqual(pool.calls.count(("asus_omni", "one")), count)
        self.assertLessEqual(pool.max_active, 2)
        self.assertFalse(pool.physical_overlap)

    def test_packaging_includes_protocol_analysis_without_changing_existing_asus_ids(self):
        package = (ENGINE.parent / "scripts/Package.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("'rog-omni-ghelper.md'", package)
        from providers import asus
        self.assertNotIn(0x1ACE, asus.KNOWN)
        self.assertEqual(asus.REQUEST, [0, 0x12, 7])
        self.assertEqual(asus.PACKET_LENGTH, 65)


if __name__ == "__main__":
    unittest.main()
