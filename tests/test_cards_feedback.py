"""UI-only connection suppression and model metadata; fake readers only."""
import copy
import itertools
import unittest
from pathlib import Path
from datetime import datetime, timedelta, timezone
from unittest.mock import patch

from test_engine_reliability import FakePool, DeviceStatus, hidlist, poll_fixture, server
from test_unadapted_engine import collection


def card(key, name="Mouse", provider="logitech", current=False, **extra):
    value = dict(id=server.public_id(key), name=name, kind="mouse", provider=provider,
                 physicalId=server.public_id("physical-" + key), percent=90,
                 measurementState="current" if current else "cached", fresh=current,
                 confirmedAtUtc=server.utcnow(), online=True)
    value.update(extra)
    return value


class ConnectionCardsTests(unittest.TestCase):
    def engine(self, cards):
        engine = poll_fixture(FakePool(), ())
        self.addCleanup(engine.close)
        engine.by_provider = {"fixture": {d["id"]: copy.deepcopy(d) for d in cards}}
        return engine

    def test_reported_four_cards_and_internal_state_are_preserved(self):
        cards = [card("atk", "ATK A9 Air", "pulsar", True, charging=True),
                 card("dongle", "Wireless mouse 8k dongle-L", "pulsar"),
                 card("old", "G Pro Wireless Gaming Mouse", percent=100),
                 card("new", "G Pro Wireless Gaming Mouse", current=True, percent=100)]
        engine = self.engine(cards)
        engine.native_mouse = True
        for state in (engine.provider_by_id, engine.physical_by_id, engine.dpi_targets):
            state.update({d["id"]: "unchanged" for d in cards})
        before = copy.deepcopy(engine.by_provider)
        result = engine._snapshot()
        self.assertEqual({d["id"] for d in result["devices"]}, {cards[0]["id"], cards[3]["id"]})
        self.assertEqual(result["suppressed"], [{"name": d["name"], "kind": "mouse"} for d in (cards[2], cards[1])])
        self.assertEqual(engine.previous, {d["id"]: d for d in cards})
        self.assertEqual(engine.by_provider, before)
        for state in (engine.provider_by_id, engine.physical_by_id, engine.dpi_targets):
            self.assertEqual(len(state), 4)
        self.assertTrue(result["nativeMousePresent"])

    def test_all_pair_guards(self):
        a, b = card("a", current=True), card("b")
        for change in [dict(measurementState="current"), dict(physicalId=a["physicalId"]),
                       dict(physicalId=""), dict(provider="pulsar"), dict(kind="keyboard"),
                       dict(percent=84), dict(percent=None), dict(name="Other Mouse")]:
            with self.subTest(change=change):
                self.assertEqual(len(self.engine([a, dict(b, **change)])._snapshot()["devices"]), 2)

    def test_generic_names_cleaning_and_five_point_boundary(self):
        for name in ["mOuSe\n", "USB Receiver", "接收器", "2.4G", "Wireless Mouse", "dongle-L"]:
            with self.subTest(name=name):
                result = self.engine([card("a", current=True), card("b", name, percent=85)])._snapshot()
                self.assertEqual(len(result["devices"]), 1)

    def test_three_stale_cards_latest_then_id_and_order_independent(self):
        older = (datetime.now(timezone.utc) - timedelta(seconds=30)).isoformat()
        cards = [card("a", confirmedAtUtc=older), card("b"), card("c")]
        cards[2]["confirmedAtUtc"] = cards[1]["confirmedAtUtc"]
        expected = min(cards[1]["id"], cards[2]["id"])
        for order in itertools.permutations(cards):
            result = self.engine(order)._snapshot()
            self.assertEqual([d["id"] for d in result["devices"]], [expected])
            self.assertEqual(len(result["suppressed"]), 2)

    def test_bridge_cannot_merge_current_cards_or_receiver_slots(self):
        a, b, c = card("a", current=True), card("b", current=True), card("c")
        for cards in ([a, b, c], [a, dict(b, measurementState="cached", physicalId=a["physicalId"]), c]):
            for order in itertools.permutations(cards):
                self.assertGreaterEqual(len(self.engine(order)._snapshot()["devices"]), 2)
        result = self.engine([a, b, c])._snapshot()
        self.assertEqual({d["id"] for d in result["devices"]}, {a["id"], b["id"]})

    def test_battery_distance_does_not_transitively_hide_unrelated_card(self):
        cards = [card("a", percent=90), card("b", percent=85), card("c", percent=80)]
        results = [{d["id"] for d in self.engine(order)._snapshot()["devices"]} for order in itertools.permutations(cards)]
        self.assertTrue(all(result == results[0] and len(result) == 2 for result in results))

    def test_recovery_and_removal_restore_card(self):
        a, b = card("a", current=True), card("b")
        engine = self.engine([a, b])
        self.assertEqual(len(engine._snapshot()["devices"]), 1)
        engine.by_provider["fixture"][b["id"]]["measurementState"] = "current"
        self.assertEqual(len(engine._snapshot()["devices"]), 2)
        engine.by_provider["fixture"][b["id"]]["measurementState"] = "cached"
        engine.by_provider["fixture"].pop(a["id"])
        engine.previous[a["id"]]["confirmedAtUtc"] = "2000-01-01T00:00:00Z"
        self.assertEqual([d["id"] for d in engine._snapshot()["devices"]], [b["id"]])

    def test_suppressed_is_bounded(self):
        result = self.engine([card(str(i)) for i in range(30)])._snapshot()
        self.assertEqual(len(result["devices"]), 1)
        self.assertEqual(len(result["suppressed"]), 16)
        self.assertTrue(all(set(d) == {"name", "kind"} for d in result["suppressed"]))


class CardInventoryTests(unittest.TestCase):
    def setUp(self):
        mock = patch.object(hidlist, "interface_paths", return_value=frozenset())
        mock.start()
        self.addCleanup(mock.stop)

    def test_unique_inventory_model_without_extra_reads_and_partial_retention(self):
        pool = FakePool([collection("a"), collection("a", page=0xFF04)], {
            ("pulsar", "private-a"): (0, [DeviceStatus("a", "ATK A9 Air", 90, source="pulsar")])})
        engine = poll_fixture(pool, ("pulsar",))
        self.addCleanup(engine.close)
        frames = []
        result = engine.poll(force=True, progress=frames.append)
        device, = result["devices"]
        self.assertEqual((device.get("vendorId"), device.get("productId")), (0x373B, 0x120A))
        self.assertEqual(pool.calls, [("topology", ""), ("pulsar", "private-a"), ("bluetooth", "")])
        self.assertTrue(all(d.get("vendorId") == 0x373B for f in frames for d in f["devices"]))
        self.assertEqual(engine._snapshot()["devices"][0]["productId"], 0x120A)
        pool.inventory.append(collection("a", pid=0x120B))
        device, = engine.poll(force=True)["devices"]
        self.assertNotIn("vendorId", device)
        self.assertNotIn("productId", device)

    def test_inventory_secrets_in_product_are_scrubbed_before_name_truncation(self):
        secret = "PRIVATE-SERIAL-" + "x" * 100
        info = collection("a")
        info["serial_number"] = secret
        name = "ATK " + secret + " secret-path-a private-a"
        pool = FakePool([info], {("pulsar", "private-a"): (0, [DeviceStatus("a", name, 90, source="pulsar")])})
        engine = poll_fixture(pool, ("pulsar",))
        self.addCleanup(engine.close)
        device, = engine.poll(force=True)["devices"]
        for private in ("PRIVATE-SERIAL", "xxxxx", "secret-path-a", "private-a"):
            self.assertNotIn(private, device["name"])

    def test_native_inventory_never_enters_suppression_or_generic_cards(self):
        pool = FakePool([collection("native-atk", pid=0x1278), collection("native-headset", vid=0x03F0, pid=0x098D)])
        engine = poll_fixture(pool, ("pulsar", "hyperx"))
        self.addCleanup(engine.close)
        engine.native_mouse = True
        result = engine.poll(force=True)
        self.assertEqual(result["devices"], [])
        self.assertEqual(result["suppressed"], [])
        self.assertFalse(any(name in ("pulsar", "hyperx") for name, _ in pool.calls))

    def test_issue_template_field_matches_prefill(self):
        root = Path(__file__).resolve().parents[1]
        template = (root / ".github/ISSUE_TEMPLATE/device-request.yml").read_text(encoding="utf-8")
        source = (root / "scripts/GenericBattery.cs").read_text(encoding="utf-8")
        self.assertIn("id: diagnostics", template)
        self.assertIn("title:", template)
        self.assertIn("template=device-request.yml", source)
        self.assertIn("&title=", source)
        self.assertIn("&diagnostics=", source)


if __name__ == "__main__":
    unittest.main()
