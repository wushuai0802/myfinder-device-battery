"""V9 state transitions through the public bridge, without physical HID access."""
from dataclasses import replace
from pathlib import Path
import sys
import unittest

ENGINE = Path(__file__).resolve().parents[1] / "engine"
sys.path.insert(0, str(ENGINE))
sys.path.insert(0, str(ENGINE / "tests"))
from test_mchose_headset import M, entry, fixture, reply, turbo_inventory
from catalog import verification_for
from known_ids import build_index
from server import BatteryEngine, PROVIDERS, PROVIDER_VIDS, normalise, recognized_candidate_count, scoped_poll

T1, T2 = "2026-11-04T00:00:00Z", "2026-11-04T00:01:00Z"


class MchoseBridgeTests(unittest.TestCase):
    def status(self, frame):
        with fixture([frame]):
            status, = M.MchoseHeadsetProvider().poll()
        return status

    def test_registration_and_level_c(self):
        self.assertIn(("mchose_headset", "MchoseHeadsetProvider"), PROVIDERS)
        self.assertEqual(PROVIDER_VIDS["mchose_headset"], (0x291D, 0x3837))
        self.assertEqual(verification_for("mchose_headset")[0], "C")

    def test_offline_index_contains_only_the_exact_headset_identities(self):
        for engine_root in (None, ENGINE):
            with self.subTest(engine_root=engine_root):
                index = build_index(engine_root=engine_root)
                rows = [d for d in index["devices"] if d["provider"] == "mchose_headset"]
                self.assertEqual([(d["vendorId"], d["productId"], d["name"], d["kind"]) for d in rows],
                                 [(0x291D, 0x385D, "MCHOSE V9 PRO", "headset"),
                                  (0x3837, 0x600A, "MCHOSE V9 Turbo+", "headset")])
                self.assertEqual(index["vendors"]["mchose_headset"], [0x291D, 0x3837])
                self.assertNotIn("mchose_headset", index["unparsed"])
                self.assertNotIn("mchose_headset", index["partial"])
                self.assertFalse(any((d["vendorId"], d["productId"]) == (0x3837, 0x6008)
                                     for d in index["devices"]))

    def test_candidate_count_uses_vendor_pid_pairs_and_excludes_lighting(self):
        rejected = [entry(vendor_id=0x3837, product_id=0x6008), entry(product_id=0x600A),
                    entry(vendor_id=0x3837)]
        self.assertEqual(recognized_candidate_count("mchose_headset", rejected), 0)
        self.assertEqual(recognized_candidate_count("mchose_headset", turbo_inventory() + rejected), 1)
        self.assertEqual(recognized_candidate_count("mchose_headset", [entry()] + turbo_inventory()), 2)

    def test_turbo_scoped_poll_produces_fresh_level_c_headset(self):
        inventory = [entry()] + turbo_inventory()
        with fixture([reply(80)], inventory) as bus:
            status, = scoped_poll(M.MchoseHeadsetProvider(), inventory, "turbo")
            self.assertEqual(bus.opened, [b"turbo-ff90"])
        result = normalise(status, None, T1)
        self.assertEqual((result["name"], result["percent"], result["kind"], result["verificationLevel"]),
                         ("MCHOSE V9 Turbo+", 80, "headset", "C"))
        self.assertTrue(result["fresh"])
        self.assertFalse(result["charging"])

    def test_power_transition_clears_old_percentage_and_preserves_power_evidence(self):
        for state in (0x00, 0x03):
            with self.subTest(state=state):
                old = normalise(self.status(reply(90)), None, T1)
                current = normalise(self.status(reply(90, state)), old, T2)
                self.assertIsNone(current["percent"])
                self.assertEqual(current["measurementState"], "unknown")
                self.assertEqual(current["connectionState"], "connected")
                self.assertEqual(current["sourceQuality"], "device_reply")
                self.assertTrue(current["charging"])
                self.assertTrue(current["chargingKnown"])
                self.assertFalse(current["fresh"])
                self.assertEqual(current["chargingCheckedAtUtc"], T2)
                self.assertEqual(current["queriedAtUtc"], T2)
                self.assertIsNone(current["measuredAtUtc"])
                self.assertIsNone(current["confirmedAtUtc"])
                recovered = normalise(self.status(reply(100)), current, T2)
                self.assertEqual(recovered["percent"], 100)
                self.assertTrue(recovered["fresh"])
                self.assertIs(recovered["charging"], False)

    def test_off_clears_old_percentage_without_claiming_zero_or_charging(self):
        old = normalise(self.status(reply(20)), None, T1)
        current = normalise(self.status(reply(0, 0x1A)), old, T2)
        self.assertIsNone(current["percent"])
        self.assertEqual(current["connectionState"], "disconnected")
        self.assertFalse(current["online"])
        self.assertIsNone(current["charging"])
        self.assertEqual(current["connectionCheckedAtUtc"], T2)

    def test_cached_status_cannot_clear_history_or_resurrect_power_flag(self):
        old = normalise(self.status(reply(20)), None, T1)
        stale = replace(self.status(reply(20, 0)), cached=True)
        current = normalise(stale, old, T2)
        self.assertEqual(current["percent"], 20)
        self.assertEqual(current["confirmedAtUtc"], T1)
        self.assertIsNone(current["charging"])

    def test_silent_poll_keeps_only_historical_unconfirmed_battery(self):
        provider = M.MchoseHeadsetProvider()
        engine = BatteryEngine([provider], include_native=True)
        self.addCleanup(engine.close)
        with fixture([reply(90)]):
            first, = engine.poll()["devices"]
        with fixture():
            silent, = engine.poll()["devices"]
        self.assertEqual(silent["percent"], 90)
        self.assertEqual(silent["confirmedAtUtc"], first["confirmedAtUtc"])
        self.assertEqual(silent["connectionState"], "unconfirmed")
        self.assertFalse(silent["fresh"])
        self.assertIsNone(silent["charging"])

    def test_existing_scoped_poll_keeps_two_receivers_separate_and_hides_identity(self):
        infos = [entry("one"), entry("two")]
        statuses = []
        with fixture([reply(20), reply(90)], infos) as bus:
            for physical in ("one", "two"):
                statuses.extend(scoped_poll(M.MchoseHeadsetProvider(), infos, physical))
            self.assertEqual(bus.opened, [entry("one")["path"], entry("two")["path"]])
        self.assertEqual([s.physical_id for s in statuses], ["one", "two"])
        values = [normalise(s, None, T1) for s in statuses]
        self.assertEqual(len({v["id"] for v in values}), 2)
        self.assertEqual(len({v["physicalId"] for v in values}), 2)
        self.assertTrue(all(v["physicalId"] not in ("one", "two") for v in values))


if __name__ == "__main__":
    unittest.main()
