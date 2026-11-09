"""CHERRY public evidence, child scoping and manager isolation; all synthetic."""
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
import _hidstub  # noqa: F401 -- standalone discovery must not load a real reader
import test_cherry as F
from catalog import load_catalog, verification_for
from device_pack import BRAND_PROVIDERS, parse_pack
from isolation import wire_encode
from known_ids import build_index
from providers.base import DeviceStatus
from providers.bluetooth import BluetoothProvider, K_BAT, K_CONN
from providers.profiles import profiles
from test_engine_reliability import FakePool
import server


def disabled_pack(pid):
    # Validator input only, not a signed/published pack or a new protocol profile.
    return parse_pack(json.dumps({
        "format": "myfinder-device-pack", "loader": 1, "packVersion": 2026110401,
        "keyId": "pack-2026a", "createdAt": "2026-11-04T10:00:00Z", "entries": [],
        "disabled": [{"provider": "cherry", "vendorId": "046A", "productId": f"{pid:04X}",
                      "reason": "synthetic test"}]}))


class CherryBridgeTests(unittest.TestCase):
    def engine(self, *args, **kwargs):
        engine = server.BatteryEngine(*args, include_native=True, **kwargs)
        self.addCleanup(engine.close)
        return engine

    def child(self, physical, inventory, pack=None):
        request = dict(action="query", sequence=1, provider="cherry",
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
        engine._accept("cherry|" + physical, "cherry",
                       [DeviceStatus(**s) for s in response["statuses"]],
                       response["checkedAtUtc"], physical, response["candidateCount"],
                       diagnostics=response.get("diagnostics", []))

    def test_registration_single_grade_c_keyboard_catalog_and_exact_offline_ids(self):
        self.assertIn(("cherry", "CherryProvider"), server.PROVIDERS)
        self.assertEqual(server.PROVIDER_VIDS["cherry"], (0x046A,))
        self.assertIn("cherry", server.READER_DIAGNOSTICS)
        self.assertIn("cherry", BRAND_PROVIDERS)
        self.assertNotIn("cherry", server.DPI_PROVIDERS)
        self.assertFalse(any(p.provider == "cherry" for p in profiles()))
        self.assertEqual(verification_for("cherry")[0], "C")
        row, = [r for r in load_catalog()["readers"] if r["provider"] == "cherry"]
        self.assertEqual((row["verificationLevel"], row["kinds"]), ("C", ["keyboard"]))
        for index in (build_index(), build_index(engine_root=ENGINE)):
            self.assertNotIn("cherry", index["unparsed"])
            self.assertNotIn("cherry", index["partial"])
            rows = [r for r in index["devices"] if r["provider"] == "cherry"]
            self.assertEqual({(r["vendorId"], r["productId"], r["kind"]) for r in rows},
                             {(0x046A, 0x01E0, "keyboard"), (0x046A, 0x01DE, "keyboard")})
            self.assertTrue(all(r.get("requiresModelMatch") is True for r in rows))
            self.assertEqual(index["vendors"]["cherry"], [0x046A])

    def test_child_targets_each_parent_without_touching_other_receiver_ble_or_unknown_model(self):
        infos = [F.entry("one"), F.entry("two"),
                 F.entry("ble", vendor_id=0x0687, pid=0x01DE), F.entry("unknown", pid=0xFFFF)]
        for physical in ("one", "two"):
            with self.subTest(physical=physical), F.fixture([F.reply(58, 1)], infos) as bus:
                response = self.child(physical, infos)
            status, = response["statuses"]
            self.assertEqual((status["level"], status["physical_id"], status["charging_known"]),
                             (58, physical, True))
            self.assertEqual(bus.opened, [F.entry(physical)["path"]])
            self.assertEqual(bus.writes, [(F.entry(physical)["path"], list(F.REQUEST))])
            self.assertEqual(bus.closed, bus.opened)

    def test_public_usb_charging_and_history_preserve_timestamps_without_dpi(self):
        for pid in (0x01E0, 0x01DE):
            for flag in (0, 1):
                with self.subTest(pid=pid, flag=flag):
                    engine = self.engine([F.P()])
                    infos = [F.entry(pid=pid)]
                    with F.fixture([F.reply(58, flag)], infos):
                        first, = engine.poll()["devices"]
                    self.assertEqual((first["percent"], first["kind"], first["verificationLevel"]),
                                     (58, "keyboard", "C"))
                    self.assertEqual(first["transport"], "USB / 2.4G")
                    self.assertIs(first["charging"], bool(flag))
                    self.assertTrue(first["fresh"] and first["chargingKnown"])
                    self.assertEqual(first["measuredAtUtc"], first["confirmedAtUtc"])
                    self.assertIsNotNone(first["chargingCheckedAtUtc"])
                    self.assertIsNone(first["dpi"])
                    for frames in ([], [F.reply(0)], [F.reply(101)], [F.reply(58, 2)]):
                        with F.fixture(frames, infos):
                            snapshot = engine.poll()
                        stale, = snapshot["devices"]
                        self.assertEqual((stale["percent"], stale["connectionState"], stale["measurementState"]),
                                         (58, "unconfirmed", "cached"))
                        for field in ("confirmedAtUtc", "measuredAtUtc", "queriedAtUtc"):
                            self.assertEqual(stale[field], first[field])
                        self.assertFalse(stale["fresh"] or stale["chargingKnown"])
                        self.assertIsNone(stale["charging"])
                        self.assertIsNone(stale["chargingCheckedAtUtc"])
                        self.assertIsNone(stale["dpi"])
                        self.assertEqual(snapshot["errors"], [])
                        self.assertFalse(engine.failures or engine.retry_after)

    def test_child_to_manager_fixed_diagnostics_distinguish_refusal_and_no_reply(self):
        cases = ((dict(product_string="private-product"), [], {}, "CHERRY interface/product unverified"),
                 (dict(input_report_sizes=None), [], {}, "report sizes unproven"),
                 (dict(output_report_sizes={"4": 64, "5": 2}), [], {}, "single report 04/64 unproven"),
                 ({}, [], {}, "no valid battery reply"),
                 ({}, [], {"write_result": 63}, "battery query write short/failed"),
                 ({}, [], {"error_at": "read"}, "battery I/O failed"))
        for changes, frames, options, expected in cases:
            with self.subTest(expected=expected):
                infos = [F.entry(**changes)]
                engine = self.engine([])
                with F.fixture(frames, infos, **options) as bus:
                    response = self.child("receiver-a", infos)
                self.accept(engine, "receiver-a", response)
                snapshot = engine._snapshot()
                health, = snapshot["health"]
                self.assertEqual((health["state"], health["reason"], health["errorCode"]),
                                 ("unavailable", "no_measurement", expected))
                self.assertLessEqual(len(health["errorCode"]), 48)
                self.assertEqual(snapshot["devices"], [])
                self.assertEqual(snapshot["errors"], [])
                self.assertFalse(engine.failures or engine.retry_after)
                self.assertNotIn("errorCode", engine.health["cherry|receiver-a"])
                for secret in ("receiver-a", "private-product", "shared-private-serial", "-vendor"):
                    self.assertNotIn(secret, json.dumps(snapshot))
                if changes:
                    self.assertEqual((bus.opened, bus.writes), ([], []))

    def test_diagnostic_allowlist_rejects_private_text_and_strips_untrusted_suffix(self):
        reason = "report IDs/lengths unknown or incompatible: nothing sent"
        provider = F.P()
        provider._diag = ["C:/private/secret", reason + "; private-path; serial"]
        self.assertEqual(server.reader_diagnostics(provider), [reason])
        engine = self.engine([])
        engine._accept("cherry|one", "cherry", [], server.utcnow(), "one", 1,
                       diagnostics=provider._diag)
        snapshot = engine._snapshot()
        self.assertEqual(snapshot["health"][0]["errorCode"], "report sizes unproven")
        self.assertNotIn("private", json.dumps(snapshot))
        self.assertEqual(snapshot["errors"], [])
        self.assertTrue(all(len(s) <= 48 for s in server.CHERRY_DIAGNOSTICS.values()))

    def test_charging_history_and_confirmation_survive_rejected_child_query(self):
        engine = self.engine([])
        infos = [F.entry()]
        with F.fixture([F.reply(58, 1)], infos):
            self.accept(engine, "receiver-a", self.child("receiver-a", infos))
        first, = engine._snapshot()["devices"]
        bad = [F.entry(product_string="wrong")]
        with F.fixture([], bad):
            self.accept(engine, "receiver-a", self.child("receiver-a", bad))
        snapshot = engine._snapshot()
        stale, = snapshot["devices"]
        self.assertEqual((stale["percent"], stale["confirmedAtUtc"], stale["measuredAtUtc"]),
                         (58, first["confirmedAtUtc"], first["measuredAtUtc"]))
        self.assertFalse(stale["chargingKnown"])
        self.assertIsNone(stale["charging"])
        self.assertEqual(snapshot["errors"], [])
        self.assertFalse(engine.failures or engine.retry_after)

    def test_independent_near_percent_cards_survive_all_current_cached_combinations(self):
        for second_pid in (0x01E0, 0x01DE):
            for current in ((True, True), (True, False), (False, False)):
                with self.subTest(pid=second_pid, current=current):
                    engine = self.engine([])
                    infos = [F.entry("one"), F.entry("two", pid=second_pid)]
                    engine._inventory_groups = {info["physical_id"]: [info] for info in infos}
                    for info, level in zip(infos, (58, 60)):
                        with F.fixture([F.reply(level, 1)], infos):
                            self.accept(engine, info["physical_id"], self.child(info["physical_id"], infos))
                    before = {d["physicalId"]: d for d in engine._snapshot()["devices"]}
                    for info, is_current in zip(infos, current):
                        if not is_current:
                            with F.fixture([], infos):
                                self.accept(engine, info["physical_id"], self.child(info["physical_id"], infos))
                    result = engine._snapshot()
                    self.assertEqual(len(result["devices"]), 2)
                    self.assertEqual(result["suppressed"], [])
                    self.assertEqual(len({d["id"] for d in result["devices"]}), 2)
                    self.assertEqual(len({d["physicalId"] for d in result["devices"]}), 2)
                    for info, is_current in zip(infos, current):
                        card = next(d for d in result["devices"] if d["physicalId"] == server.public_id(info["physical_id"]))
                        self.assertEqual(card["measurementState"], "current" if is_current else "cached")
                        self.assertEqual(card["confirmedAtUtc"], before[card["physicalId"]]["confirmedAtUtc"])
                        self.assertEqual((card["vendorId"], card["productId"]), (0x046A, info["product_id"]))
                        self.assertEqual(engine.physical_by_id[card["id"]], info["physical_id"])
                        self.assertEqual((engine.provider_by_id[card["id"]], card["refreshScope"]), ("cherry", "device"))

    def test_history_still_expires_at_existing_global_limit(self):
        engine = self.engine([])
        with F.fixture([F.reply()]):
            statuses = F.P().poll()
        engine._accept("cherry|receiver-a", "cherry", statuses,
                       "2000-01-01T00:00:00Z", "receiver-a", 1)
        engine._snapshot()
        engine._accept("cherry|receiver-a", "cherry", [], server.utcnow(), "receiver-a", 1)
        self.assertEqual(engine._snapshot()["devices"], [])

    def test_disable_either_pid_leaves_other_usb_and_system_bluetooth_independent(self):
        infos = [F.entry("dongle"), F.entry("wired", pid=0x01DE),
                 F.entry("ble", pid=0x01DE, vendor_id=0x0687)]
        for denied, blocked, allowed in ((0x01E0, "dongle", "wired"), (0x01DE, "wired", "dongle")):
            with self.subTest(denied=denied):
                pack = disabled_pack(denied)
                self.assertTrue(pack.loaded, pack.rejected)
                self.assertEqual(len(pack.select_inventory("bluetooth", infos)), 3)
                pool = FakePool(infos)
                engine = self.engine(process_factory=pool.factory, device_pack=pack)
                engine.providers = ["cherry"]
                with patch.object(engine._hidlist, "interface_paths", return_value=frozenset()):
                    engine.poll()
                self.assertNotIn(("cherry", blocked), pool.calls)
                self.assertIn(("cherry", allowed), pool.calls)
                self.assertNotIn(("cherry", "ble"), pool.calls)
                with F.fixture([F.reply()], infos) as bus, patch.object(F.P, "poll") as poll:
                    # Production manager commands carry only this parent's inventory.
                    response = self.child(blocked, [i for i in infos if i["physical_id"] == blocked], pack)
                    poll.assert_not_called()
                    self.assertEqual(response["statuses"], [])
                    self.assertEqual(response["candidateCount"], 0)
                    self.assertEqual((bus.opened, bus.writes), ([], []))
                with F.fixture([F.reply(63, 1)], infos) as bus:
                    response = self.child(allowed, infos, pack)
                    self.assertEqual(len(response["statuses"]), 1)
                    self.assertEqual(bus.opened, [F.entry(allowed)["path"]])
                direct = self.engine([F.P()], device_pack=pack)
                with F.fixture([F.reply()], infos) as bus:
                    self.assertEqual(len(direct.poll()["devices"]), 1)
                    self.assertEqual(bus.opened, [F.entry(allowed)["path"]])

    def test_offline_index_disable_wins_without_registering_new_pack_profile(self):
        original_glob = Path.glob

        def glob(path, pattern):
            return iter([path / "synthetic.json"]) if path.name == "pack" else original_glob(path, pattern)

        for denied, allowed in ((0x01E0, 0x01DE), (0x01DE, 0x01E0)):
            with self.subTest(denied=denied), patch.object(Path, "glob", glob), \
                    patch("known_ids.load_pack", return_value=disabled_pack(denied)):
                rows = [r for r in build_index()["devices"] if r["provider"] == "cherry"]
            self.assertEqual([r["productId"] for r in rows], [allowed])
            self.assertTrue(rows[0]["requiresModelMatch"])

    def test_manager_keeps_default_budget_two_slots_partial_results_and_parent_serialisation(self):
        with F.fixture([F.reply(58)]):
            status, = F.P().poll()
        infos = [F.entry("one"), F.entry("two")]
        pool = FakePool(infos, {("cherry", "one"): (.01, [status]),
                                ("cherry", "two"): (.2, [status])})
        engine = self.engine(process_factory=pool.factory)
        engine.providers = ["cherry", "standard_hid"]
        frames = []
        with patch.object(engine._hidlist, "interface_paths", return_value=frozenset()), \
                patch("server.time.monotonic", return_value=1000.0) as clock:
            with patch("server.time.sleep", side_effect=lambda seconds: setattr(clock, "return_value", clock.return_value + seconds)):
                result = engine.poll(progress=frames.append)
        self.assertTrue(result["complete"])
        self.assertTrue(any(not f["complete"] and f["devices"] for f in frames))
        self.assertEqual(len(result["devices"]), 2)
        self.assertTrue(all(b == 7 for n, p, b in pool.budgets if n == "cherry"))
        self.assertEqual({p for n, p in pool.calls if n == "cherry"}, {"one", "two"})
        self.assertLessEqual(pool.max_active, 2)
        self.assertFalse(pool.physical_overlap or pool.active)

    def test_many_slow_cherry_readers_respect_unchanged_round_budget_and_keep_fast_partial(self):
        infos = [F.entry(f"parent-{i:02}") for i in range(14)]
        with F.fixture([F.reply()]):
            status, = F.P().poll()
        outcomes = {("cherry", info["physical_id"]): (100, []) for info in infos}
        outcomes[("cherry", "parent-00")] = (.01, [status])
        pool = FakePool(infos, outcomes)
        engine = self.engine(process_factory=pool.factory)
        engine.providers = ["cherry"]
        frames = []
        with patch.object(engine._hidlist, "interface_paths", return_value=frozenset()), \
                patch("server.time.monotonic", return_value=1000.0) as clock:
            with patch("server.time.sleep", side_effect=lambda seconds: setattr(clock, "return_value", clock.return_value + seconds)):
                result = engine.poll(progress=frames.append)
            self.assertLessEqual(clock.return_value - 1000, server.MAX_ROUND_SECONDS + .1)
        self.assertEqual(server.MAX_ROUND_SECONDS, 36)
        self.assertTrue(all(b == 7 for n, p, b in pool.budgets if n == "cherry"))
        self.assertLess(len([n for n, p in pool.calls if n == "cherry"]), len(infos))
        self.assertTrue(any(h["state"] == "deferred" for h in result["health"]))
        self.assertTrue(any(f["devices"] for f in frames))
        self.assertIn("cherry: timeout", result["errors"])
        self.assertLessEqual(pool.max_active, 2)
        self.assertFalse(pool.physical_overlap or pool.active)

    def test_ble_system_property_and_missing_value_keep_original_evidence_semantics(self):
        root = r"BTHLE\DEV_AABBCCDDEEFF\fixture"
        data = dict(devs=[dict(id=root, name="MX2.0S WL Pro-BT1", status="OK")],
                    props=[dict(id=root, key=K_BAT, data=64), dict(id=root, key=K_CONN, data=True)],
                    aep=[dict(addr="AABBCCDDEEFF", conn=True, le=True, cls=961)])
        provider = BluetoothProvider()
        status, = provider.parse(json.dumps(data))
        first = server.normalise(status, None, "2026-11-09T00:00:00Z")
        self.assertEqual((first["percent"], first["kind"], first["sourceQuality"]), (64, "keyboard", "system_property"))
        self.assertFalse(first["fresh"] or first["chargingKnown"])
        self.assertIsNone(first["measuredAtUtc"])
        self.assertIsNone(first["charging"])
        data["props"] = [dict(id=root, key=K_CONN, data=True)]
        cached, = provider.parse(json.dumps(data))
        again = server.normalise(cached, first, "2026-11-09T01:00:00Z")
        for field in ("confirmedAtUtc", "queriedAtUtc", "measuredAtUtc"):
            self.assertEqual(again[field], first[field])
        self.assertEqual(again["measurementState"], "cached")
        self.assertIsNone(again["charging"])
        data["aep"][0]["conn"] = data["props"][0]["data"] = False
        self.assertEqual(provider.parse(json.dumps(data)), [])


if __name__ == "__main__":
    unittest.main()
