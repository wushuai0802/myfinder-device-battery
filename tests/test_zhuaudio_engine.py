"""ZG4 registration/manager/reader-child contracts, all synthetic and offline."""
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
import _hidstub  # noqa: F401 -- before any provider/server import
import test_zhuaudio as F
import server
from catalog import load_catalog, verification_for
from device_pack import BRAND_PROVIDERS, parse_pack
from isolation import wire_encode
from known_ids import build_index
from providers.base import DeviceStatus
from providers.profiles import profiles
from test_engine_reliability import FakePool


def disabled_pack():
    # Unsigned validator fixture, never a downloadable profile or publication.
    return parse_pack(json.dumps({
        "format": "myfinder-device-pack", "loader": 1, "packVersion": 2026111001,
        "keyId": "pack-2026a", "createdAt": "2026-11-10T00:00:00Z", "entries": [],
        "disabled": [{"provider": "zhuaudio", "vendorId": "3654", "productId": "5055",
                      "reason": "synthetic test"}]}))


class ZhuAudioEngineTests(unittest.TestCase):
    def engine(self, *args, **kwargs):
        engine = server.BatteryEngine(*args, include_native=True, **kwargs)
        self.addCleanup(engine.close)
        return engine

    def child_many(self, targets, inventory, pack=None):
        commands = []
        for seq, physical in enumerate(targets, 1):
            command = dict(action="query", sequence=seq, provider="zhuaudio",
                           physicalId=physical, inventory=inventory)
            if pack is not None:
                command["devicePack"] = pack.document
            commands.append(json.dumps(command, default=wire_encode))
        data = ("\n".join(commands) + "\n").encode()
        with patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
            server.reader_child()
        responses = [json.loads(line) for line in output.getvalue().splitlines()]
        self.assertEqual(len(responses), len(targets))
        for response in responses:
            self.assertFalse(response.get("errorCode"), response)
        return responses

    def child(self, physical, inventory, pack=None):
        return self.child_many([physical], inventory, pack)[0]

    def accept(self, engine, physical, response):
        engine._accept("zhuaudio|" + physical, "zhuaudio",
                       [DeviceStatus(**s) for s in response["statuses"]],
                       response["checkedAtUtc"], physical, response["candidateCount"],
                       diagnostics=response.get("diagnostics", []))

    def test_registration_grade_c_single_headset_row_and_static_ids(self):
        self.assertIn(("zhuaudio", "ZhuAudioProvider"), server.PROVIDERS)
        self.assertEqual(server.PROVIDER_VIDS["zhuaudio"], (0x3654,))
        self.assertIn("zhuaudio", BRAND_PROVIDERS)
        self.assertIn("zhuaudio", server.READER_DIAGNOSTICS)
        self.assertNotIn("zhuaudio", server.DPI_PROVIDERS)
        self.assertFalse(any(p.provider == "zhuaudio" for p in profiles()))
        self.assertEqual(verification_for("zhuaudio")[0], "C")
        row, = [r for r in load_catalog()["readers"] if r["provider"] == "zhuaudio"]
        self.assertEqual((row["kinds"], row["verificationLevel"]), (["headset"], "C"))
        for index in (build_index(), build_index(engine_root=ENGINE)):
            rows = [r for r in index["devices"] if r["provider"] == "zhuaudio"]
            self.assertEqual(len(rows), 1)
            self.assertEqual((rows[0]["vendorId"], rows[0]["productId"], rows[0]["kind"]),
                             (0x3654, 0x5055, "headset"))
            self.assertTrue(rows[0]["requiresModelMatch"])
            self.assertEqual(index["vendors"]["zhuaudio"], [0x3654])
            self.assertNotIn("zhuaudio", index["unparsed"])
            self.assertNotIn("zhuaudio", index["partial"])

    def test_child_targets_parent_and_preserves_per_instance_sequences(self):
        infos = [F.entry("one"), F.entry("two"), F.entry("wrong", product_id=0x5054),
                 F.entry("ble", path=b"bthledevice#private")]
        packets = {infos[0]["path"]: [F.connected(), F.reply(70), F.connected(), F.reply(69, seq=2)],
                   infos[1]["path"]: [F.connected(), F.reply(72)]}
        with F.fixture(packets, infos) as bus:
            responses = self.child_many(["one", "two", "one"], infos)
        self.assertEqual([r["statuses"][0]["physical_id"] for r in responses], ["one", "two", "one"])
        self.assertEqual([r["statuses"][0]["level"] for r in responses], [70, 72, 69])
        self.assertEqual(bus.opened, [infos[0]["path"], infos[1]["path"], infos[0]["path"]])
        self.assertEqual([p[7] for _, p in bus.writes if p[1] == 0xEE], [1, 1, 2])
        self.assertEqual(bus.closed, bus.opened)

    def test_child_keeps_timeout_pending_and_does_not_consume_later_old_reply(self):
        # The child keeps the provider object between requests. Reopening is
        # not used as a cancellation claim, even when old frames are available.
        with F.fixture([F.connected(), [], F.connected(), F.reply(seq=1)]) as bus:
            first, second = self.child_many(["receiver-a", "receiver-a"], [F.entry()])
        self.assertEqual((first["statuses"], second["statuses"]), ([], []))
        self.assertIn("ZG4 previous transaction unresolved", second["diagnostics"])
        self.assertEqual((len(bus.opened), len(bus.writes)), (1, 2))

    def test_public_percent_charging_evidence_and_history_times_remain_separate(self):
        for charging in (False, True):
            engine = self.engine([F.P()])
            with F.fixture([F.connected(), F.reply(70, charging)]):
                first, = engine.poll()["devices"]
            self.assertEqual((first["percent"], first["kind"], first["transport"], first["verificationLevel"]),
                             (70, "headset", "USB / 2.4G", "C"))
            self.assertIs(first["charging"], charging)
            self.assertTrue(first["fresh"] and first["chargingKnown"])
            self.assertEqual(first["confirmedAtUtc"], first["measuredAtUtc"])
            self.assertIsNone(first["dpi"])
            for packets in ([], [F.connected(), F.reply(0, seq=2)], [F.connected(0)]):
                with F.fixture(packets):
                    snapshot = engine.poll()
                stale, = snapshot["devices"]
                self.assertEqual((stale["percent"], stale["measurementState"], stale["connectionState"]),
                                 (70, "cached", "unconfirmed"))
                for field in ("confirmedAtUtc", "measuredAtUtc", "queriedAtUtc"):
                    self.assertEqual(stale[field], first[field])
                self.assertFalse(stale["fresh"] or stale["chargingKnown"])
                self.assertIsNone(stale["charging"])
                self.assertIsNone(stale["chargingCheckedAtUtc"])
                self.assertIsNone(stale["dpi"])
                self.assertEqual(snapshot["errors"], [])
                self.assertFalse(engine.failures or engine.retry_after)

    def test_refusals_and_io_have_fixed_diagnostics_without_backoff_errors_or_secrets(self):
        cases = [(dict(product_string="private-product"), [], {}, "ZG4 interface/product unverified"),
                 (dict(input_report_sizes=None), [], {}, "ZG4 CE/CD byte reports unproven"),
                 ({}, [], {}, "ZG4 no valid reply within limits"),
                 ({}, [F.connected(0)], {}, "ZG4 headset disconnected"),
                 ({}, [], {"write_results": [63]}, "ZG4 write short/failed; no retry"),
                 ({}, [], {"error_at": "read"}, "ZG4 battery I/O failed")]
        for changes, packets, options, expected in cases:
            with self.subTest(expected=expected):
                engine = self.engine([])
                infos = [F.entry(**changes)]
                with F.fixture(packets, infos, **options) as bus:
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
                self.assertNotIn("errorCode", engine.health["zhuaudio|receiver-a"])
                for secret in ("receiver-a", "private-product", "synthetic-private-serial", "mi04-ff05"):
                    self.assertNotIn(secret, json.dumps(snapshot))
                if changes:
                    self.assertEqual((bus.opened, bus.writes), ([], []))

    def test_child_and_manager_reject_non_allowlisted_diagnostic_text(self):
        self.assertEqual(F.Z.DIAGNOSTICS, server.ZHUAUDIO_DIAGNOSTICS)
        self.assertTrue(all(len(d) <= 48 for d in server.ZHUAUDIO_DIAGNOSTICS))
        provider = F.P()
        provider._diag = ["private-path", "ZG4 input rejected; private-path", "ZG4 input rejected"]
        self.assertEqual(server.reader_diagnostics(provider), ["ZG4 input rejected"])
        engine = self.engine([])
        engine._accept("zhuaudio|one", "zhuaudio", [], server.utcnow(), "one", 1,
                       diagnostics=provider._diag)
        result = engine._snapshot()
        self.assertEqual(result["health"][0]["errorCode"], "ZG4 input rejected")
        self.assertNotIn("private", json.dumps(result))
        self.assertEqual(result["errors"], [])

    def test_manager_and_child_disable_before_poll_or_any_device_open(self):
        infos = [F.entry("one"), F.entry("two")]
        pack = disabled_pack()
        self.assertTrue(pack.loaded, pack.rejected)
        self.assertEqual(pack.select_inventory("zhuaudio", infos), [])
        self.assertEqual(pack.select_inventory("bluetooth", infos), infos)
        pool = FakePool(infos)
        engine = self.engine(process_factory=pool.factory, device_pack=pack)
        engine.providers = ["zhuaudio"]
        with patch.object(engine._hidlist, "interface_paths", return_value=frozenset()):
            result = engine.poll()
        self.assertFalse(any(name == "zhuaudio" for name, _ in pool.calls))
        self.assertEqual(result["errors"], [])
        with F.fixture([F.connected(), F.reply()], infos) as bus, patch.object(F.P, "poll") as poll:
            response = self.child("one", infos, pack)
            poll.assert_not_called()
            self.assertEqual((response["statuses"], response["candidateCount"]), ([], 0))
            self.assertEqual((bus.opened, bus.writes), ([], []))
        direct = self.engine([F.P()], device_pack=pack)
        with F.fixture([F.connected(), F.reply()], infos) as bus:
            self.assertEqual(direct.poll()["devices"], [])
            self.assertEqual(bus.opened, [])

    def test_independent_parents_keep_cards_with_equal_or_similar_cached_percentages(self):
        for levels in ((70, 70), (70, 72)):
            for current in ((True, True), (True, False), (False, True), (False, False)):
                with self.subTest(levels=levels, current=current):
                    engine = self.engine([])
                    infos = [F.entry("one"), F.entry("two")]
                    engine._inventory_groups = {i["physical_id"]: [i] for i in infos}
                    for info, level in zip(infos, levels):
                        with F.fixture([F.connected(), F.reply(level, True)], infos):
                            self.accept(engine, info["physical_id"], self.child(info["physical_id"], infos))
                    before = {d["physicalId"]: d for d in engine._snapshot()["devices"]}
                    for info, fresh in zip(infos, current):
                        if not fresh:
                            with F.fixture([], infos):
                                self.accept(engine, info["physical_id"], self.child(info["physical_id"], infos))
                    result = engine._snapshot()
                    self.assertEqual(len(result["devices"]), 2)
                    self.assertEqual(result["suppressed"], [])
                    self.assertEqual(len({d["id"] for d in result["devices"]}), 2)
                    for info, fresh in zip(infos, current):
                        card = next(d for d in result["devices"] if d["physicalId"] == server.public_id(info["physical_id"]))
                        self.assertEqual(card["measurementState"], "current" if fresh else "cached")
                        self.assertEqual(card["confirmedAtUtc"], before[card["physicalId"]]["confirmedAtUtc"])
                        self.assertEqual((engine.provider_by_id[card["id"]], card["refreshScope"]), ("zhuaudio", "device"))

    def test_other_brand_presentation_heuristics_are_unchanged(self):
        cards = [dict(id=str(i), provider="moondrop", kind="headset", physicalId=str(i), percent=70,
                      name="same receiver", measurementState="cached") for i in range(2)]
        kept, hidden = server.suppress_old_connections(cards)
        self.assertEqual((len(kept), len(hidden)), (1, 1))

    def test_default_isolation_budgets_and_two_slots_still_bound_multiple_parents(self):
        infos = [F.entry("one"), F.entry("two")]
        with F.fixture([F.connected(), F.reply()]):
            status, = F.P().poll()
        pool = FakePool(infos, {("zhuaudio", "one"): (.01, [status]),
                                ("zhuaudio", "two"): (.2, [status])})
        engine = self.engine(process_factory=pool.factory)
        engine.providers = ["zhuaudio", "standard_hid"]
        frames = []
        with patch.object(engine._hidlist, "interface_paths", return_value=frozenset()), \
                patch("server.time.monotonic", return_value=1000.0) as clock, \
                patch("server.time.sleep", side_effect=lambda seconds: setattr(clock, "return_value", clock.return_value + seconds)):
            result = engine.poll(progress=frames.append)
        self.assertTrue(result["complete"])
        self.assertTrue(any(not f["complete"] and f["devices"] for f in frames))
        self.assertEqual(len(result["devices"]), 2)
        self.assertEqual(server.MAX_ROUND_SECONDS, 36)
        self.assertTrue(all(b == 7 for n, p, b in pool.budgets if n == "zhuaudio"))
        self.assertEqual({p for n, p in pool.calls if n == "zhuaudio"}, {"one", "two"})
        self.assertLessEqual(pool.max_active, 2)
        self.assertFalse(pool.physical_overlap or pool.active)

    def test_history_expires_at_existing_global_limit(self):
        engine = self.engine([])
        with F.fixture([F.connected(), F.reply()]):
            statuses = F.P().poll()
        engine._accept("zhuaudio|receiver-a", "zhuaudio", statuses, "2000-01-01T00:00:00Z", "receiver-a", 1)
        engine._snapshot()
        engine._accept("zhuaudio|receiver-a", "zhuaudio", [], server.utcnow(), "receiver-a", 1)
        self.assertEqual(engine._snapshot()["devices"], [])


if __name__ == "__main__":
    unittest.main()
