"""Lingbao child/manager/public evidence regressions; all HID access is fake."""
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
import _hidstub  # noqa: F401 -- before provider/server, including standalone discovery
import test_lingbao as F
import server
from catalog import load_catalog, verification_for
from device_pack import BRAND_PROVIDERS, parse_pack
from isolation import wire_encode
from known_ids import build_index
from providers.base import DeviceStatus
from providers.profiles import profiles
from test_engine_reliability import FakePool


def disabled_pack():
    # Validator input only; never signed, published or used as a private profile.
    return parse_pack(json.dumps({
        "format": "myfinder-device-pack", "loader": 1, "packVersion": 2026110401,
        "keyId": "pack-2026a", "createdAt": "2026-11-04T10:00:00Z", "entries": [],
        "disabled": [{"provider": "lingbao", "vendorId": "088D", "productId": "052F",
                      "reason": "synthetic test"}]}))


class LingbaoBridgeTests(unittest.TestCase):
    def engine(self, *args, **kwargs):
        engine = server.BatteryEngine(*args, include_native=True, **kwargs)
        self.addCleanup(engine.close)
        return engine

    def child(self, physical, inventory, pack=None, checked=None):
        request = dict(action="query", sequence=1, provider="lingbao",
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
        engine._accept("lingbao|" + physical, "lingbao",
                       [DeviceStatus(**s) for s in response["statuses"]],
                       response["checkedAtUtc"], physical, response["candidateCount"],
                       diagnostics=response.get("diagnostics", []))

    def test_registration_grade_c_mouse_catalog_and_exact_offline_index(self):
        self.assertIn(("lingbao", "LingbaoProvider"), server.PROVIDERS)
        self.assertEqual(server.PROVIDER_VIDS["lingbao"], (0x088D,))
        self.assertIn("lingbao", server.READER_DIAGNOSTICS)
        self.assertIn("lingbao", BRAND_PROVIDERS)
        self.assertNotIn("lingbao", server.DPI_PROVIDERS)
        self.assertFalse(any(p.provider == "lingbao" for p in profiles()))
        self.assertEqual(verification_for("lingbao")[0], "C")
        row, = [r for r in load_catalog()["readers"] if r["provider"] == "lingbao"]
        self.assertEqual((row["name"], row["verificationLevel"], row["kinds"]),
                         ("Lingbao M1 Pro", "C", ["mouse"]))
        for index in (build_index(), build_index(engine_root=ENGINE)):
            self.assertNotIn("lingbao", index["unparsed"])
            self.assertNotIn("lingbao", index["partial"])
            row, = [r for r in index["devices"] if r["provider"] == "lingbao"]
            self.assertEqual((row["vendorId"], row["productId"], row["kind"], row["name"]),
                             (0x088D, 0x052F, "mouse", "Lingbao M1 Pro"))
            self.assertTrue(row["requiresModelMatch"])
            self.assertEqual(index["vendors"]["lingbao"], [0x088D])

    def test_child_targets_one_parent_and_gets_only_one_literal_report(self):
        infos = [F.entry("one"), F.entry("two"), F.entry("unknown", product_id=0x0530),
                 F.entry("other", vendor_id=0x089D)]
        for physical in ("one", "two"):
            with self.subTest(physical=physical), F.fixture([F.CAPTURES[0]], infos) as bus:
                response = self.child(physical, infos)
                status, = response["statuses"]
                self.assertEqual((status["level"], status["physical_id"], status["kind"]),
                                 (65, physical, "mouse"))
                self.assertFalse(status["charging_known"] or status["connection_known"])
                self.assertIsNone(status["measured_at_utc"])
                self.assertIsNone(status["dpi"])
                self.assertEqual(bus.feature_reads, [(F.entry(physical)["path"], 15, 33)])
                self.assertEqual(bus.calls, ["device", "open_path", "get_feature_report", "close"])
                self.assertEqual(bus.opened, bus.closed)

    def test_child_to_manager_current_host_read_has_unknown_connection_charging_and_no_dpi(self):
        engine = self.engine([])
        infos = [F.entry()]
        with F.fixture([F.CAPTURES[0]], infos):
            response = self.child("receiver-a", infos)
        self.accept(engine, "receiver-a", response)
        snapshot = engine._snapshot()
        card, = snapshot["devices"]
        self.assertEqual((card["percent"], card["kind"], card["provider"], card["verificationLevel"]),
                         (65, "mouse", "lingbao", "C"))
        self.assertEqual((card["connectionState"], card["transport"]), ("unconfirmed", "USB / 2.4G"))
        self.assertFalse(card["chargingKnown"])
        self.assertIsNone(card["charging"])
        self.assertIsNone(card["connectionCheckedAtUtc"])
        self.assertIsNone(card["chargingCheckedAtUtc"])
        self.assertIsNone(card["dpi"])
        # Existing schema uses host query time here, not a device sample clock.
        self.assertTrue(card["fresh"])
        self.assertEqual(card["confirmedAtUtc"], response["checkedAtUtc"])
        self.assertEqual(card["measuredAtUtc"], response["checkedAtUtc"])
        self.assertEqual(card["queriedAtUtc"], response["checkedAtUtc"])
        self.assertEqual(snapshot["errors"], [])

    def test_placeholder_invalid_silent_and_refused_queries_never_refresh_history(self):
        origin = datetime.now(timezone.utc) - timedelta(seconds=30)
        stamps = [(origin + timedelta(seconds=i)).isoformat().replace("+00:00", "Z") for i in range(10)]
        engine = self.engine([])
        infos = [F.entry()]
        with F.fixture([F.CAPTURES[0]], infos):
            self.accept(engine, "receiver-a", self.child("receiver-a", infos, checked=stamps[0]))
        first, = engine._snapshot()["devices"]
        cases = [({}, [F.reply(100, 1)]), ({}, []), ({}, [F.reply(0)]), ({}, [F.reply(101)]),
                 ({}, [F.reply(flag=2)]), ({}, [F.reply(**{"7": 0})]),
                 (dict(feature_report_sizes=None), [F.reply()]), (dict(interface_number=2), [F.reply()])]
        for i, (changes, frames) in enumerate(cases, 1):
            with self.subTest(changes=changes, frames=frames), F.fixture(frames, [F.entry(**changes)]):
                self.accept(engine, "receiver-a", self.child("receiver-a", [F.entry(**changes)], checked=stamps[i]))
            snapshot = engine._snapshot()
            old, = snapshot["devices"]
            self.assertEqual((old["percent"], old["measurementState"], old["connectionState"]),
                             (65, "cached", "unconfirmed"))
            for field in ("confirmedAtUtc", "measuredAtUtc", "queriedAtUtc"):
                self.assertEqual(old[field], first[field])
            self.assertFalse(old["fresh"] or old["chargingKnown"])
            self.assertIsNone(old["charging"])
            self.assertIsNone(old["dpi"])
            self.assertEqual(snapshot["errors"], [])
            self.assertFalse(engine.failures or engine.retry_after)

    def test_first_charging_placeholder_creates_no_card_or_percentage(self):
        engine = self.engine([])
        with F.fixture([F.reply(100, 1)]):
            self.accept(engine, "receiver-a", self.child("receiver-a", [F.entry()]))
        snapshot = engine._snapshot()
        self.assertEqual(snapshot["devices"], [])
        self.assertEqual(snapshot["health"][0]["errorCode"], "charging placeholder; no new reading")
        self.assertEqual(snapshot["errors"], [])

    def test_fixed_diagnostics_reach_child_manager_and_compact_projection_without_backoff(self):
        cases = [(dict(feature_report_sizes=None), [], {}, "Feature 0F/33 unproven"),
                 (dict(feature_report_sizes={"15": 33, "16": 2}), [], {}, "Feature 0F/33 unproven"),
                 (dict(product_string="synthetic-private-detail"), [], {}, "identity/interface unverified"),
                 ({}, [F.reply()[:-1]], {}, "invalid battery reply; no new reading"),
                 ({}, [F.reply(100, 1)], {}, "charging placeholder; no new reading"),
                 ({}, [], {"error_at": "get"}, "battery I/O failed"),
                 ({}, [F.reply()], {"error_at": "close"}, "HID handle close failed")]
        for changes, frames, options, summary in cases:
            with self.subTest(summary=summary):
                infos = [F.entry(**changes)]
                engine = self.engine([])
                with F.fixture(frames, infos, **options):
                    response = self.child("receiver-a", infos)
                self.assertTrue(response["diagnostics"])
                self.assertTrue(all(line in server.LINGBAO_DIAGNOSTICS for line in response["diagnostics"]))
                self.accept(engine, "receiver-a", response)
                snapshot = engine._snapshot()
                self.assertEqual(snapshot["health"][0]["errorCode"], summary)
                self.assertLessEqual(len(summary), 48)
                self.assertEqual(snapshot["errors"], [])
                self.assertNotIn("errorCode", engine.health["lingbao|receiver-a"])
                self.assertFalse(engine.failures or engine.retry_after)
                self.assertNotIn("synthetic-private-detail", json.dumps(snapshot))

    def test_diagnostic_allowlist_rejects_private_text_suffixes_and_nonstrings(self):
        reason = "Feature 0F/33 unproven; not opened"
        provider = F.P()
        provider._diag = ["synthetic-private-detail", reason + "; synthetic-private-detail", None, {}, reason]
        self.assertEqual(server.reader_diagnostics(provider), [reason])
        engine = self.engine([])
        engine._accept("lingbao|one", "lingbao", [], server.utcnow(), "one", 1,
                       diagnostics=provider._diag)
        snapshot = engine._snapshot()
        self.assertEqual(snapshot["health"][0]["errorCode"], "Feature 0F/33 unproven")
        self.assertNotIn("synthetic-private-detail", json.dumps(snapshot))
        self.assertTrue(all(len(s) <= 48 for s in server.LINGBAO_DIAGNOSTICS.values()))
        self.assertTrue(all(len(s) <= 48 for s in server.LINGBAO_DIAGNOSTICS))

    def test_independent_parent_cards_survive_current_and_cached_combinations(self):
        infos = [F.entry("one"), F.entry("two")]
        for current in ((True, True), (True, False), (False, True), (False, False)):
            with self.subTest(current=current):
                engine = self.engine([])
                engine._inventory_groups = {i["physical_id"]: [i] for i in infos}
                for info, level in zip(infos, (65, 68)):
                    with F.fixture([F.reply(level)], infos):
                        self.accept(engine, info["physical_id"], self.child(info["physical_id"], infos))
                before = {d["physicalId"]: d for d in engine._snapshot()["devices"]}
                for info, present in zip(infos, current):
                    if not present:
                        with F.fixture([F.reply(100, 1)], infos):
                            self.accept(engine, info["physical_id"], self.child(info["physical_id"], infos))
                snapshot = engine._snapshot()
                self.assertEqual(len(snapshot["devices"]), 2)
                self.assertEqual(snapshot["suppressed"], [])
                self.assertEqual(len({d["id"] for d in snapshot["devices"]}), 2)
                for info, present in zip(infos, current):
                    card = next(d for d in snapshot["devices"] if d["physicalId"] == server.public_id(info["physical_id"]))
                    self.assertEqual(card["measurementState"], "current" if present else "cached")
                    self.assertEqual(card["confirmedAtUtc"], before[card["physicalId"]]["confirmedAtUtc"])
                    self.assertEqual((card["vendorId"], card["productId"], card["refreshScope"]), (0x088D, 0x052F, "device"))
                    self.assertEqual(engine.physical_by_id[card["id"]], info["physical_id"])
                    self.assertEqual(engine.provider_by_id[card["id"]], "lingbao")

    def test_disabled_pack_blocks_manager_direct_and_child_queries(self):
        pack = disabled_pack()
        self.assertTrue(pack.loaded, pack.rejected)
        infos = [F.entry("one"), F.entry("two")]
        self.assertEqual(pack.select_inventory("lingbao", infos), [])
        self.assertEqual(pack.select_inventory("bluetooth", infos), infos)
        pool = FakePool(infos)
        engine = self.engine(process_factory=pool.factory, device_pack=pack)
        engine.providers = ["lingbao"]
        with F.fixture([F.reply()], infos) as bus, patch.object(engine._hidlist, "interface_paths", return_value=frozenset()):
            snapshot = engine.poll()
            self.assertFalse(any(n == "lingbao" for n, _ in pool.calls))
            self.assertEqual(snapshot["devices"], [])
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

    def test_offline_index_honors_disable_without_new_downloadable_profile(self):
        original_glob = Path.glob

        def glob(path, pattern):
            return iter([path / "synthetic.json"]) if path.name == "pack" else original_glob(path, pattern)

        with patch.object(Path, "glob", glob), patch("known_ids.load_pack", return_value=disabled_pack()):
            self.assertFalse(any(r["provider"] == "lingbao" for r in build_index()["devices"]))
        self.assertFalse(any(p.provider == "lingbao" for p in profiles()))


if __name__ == "__main__":
    unittest.main()
