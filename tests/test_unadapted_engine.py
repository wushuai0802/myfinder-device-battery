"""Unadapted inventory is diagnostic metadata only; all readers are fake."""
import json
import unittest
from unittest.mock import patch

from test_engine_reliability import FakePool, DeviceStatus, hidlist, poll_fixture, server
from isolation import IsolationError
from providers import pulsar


def collection(group, vid=0x373B, pid=0x120A, page=0xFF02, usage=2, **extra):
    return dict(vendor_id=vid, product_id=pid, usage_page=page, usage=usage,
                product_string="ATK A9 Air", physical_id="private-" + group,
                path=("secret-path-" + group).encode(), serial_number="private-serial", **extra)


class UnadaptedTests(unittest.TestCase):
    def setUp(self):
        mock = patch.object(hidlist, "interface_paths", return_value=frozenset())
        mock.start()
        self.addCleanup(mock.stop)

    def engine(self, inventory, outcomes=None, providers=()):
        pool = FakePool(inventory, outcomes)
        engine = poll_fixture(pool, providers)
        self.addCleanup(engine.close)
        return pool, engine

    def test_unknown_group_is_listed_once_without_private_identifiers(self):
        infos = [collection("a"), collection("a", page=0xFF04)]
        infos[0]["product_string"] = "ATK\n A9 Air private-serial secret-path-a private-a"
        infos[1]["product_string"] = infos[0]["product_string"]
        pool, engine = self.engine(infos)
        with patch.object(pulsar, "output_length", side_effect=AssertionError("no diagnostic probes")):
            result = engine.poll(force=True)
        self.assertEqual(result["schema"], 2)
        item, = result["unadapted"]
        self.assertEqual((item["vendorId"], item["productId"]), (0x373B, 0x120A))
        self.assertEqual(item["collections"], [{"usagePage": 0xFF02, "usage": 2},
                                                {"usagePage": 0xFF04, "usage": 2}])
        self.assertLessEqual(len(item["product"]), 48)
        self.assertNotIn("\n", item["product"])
        encoded = json.dumps(result["unadapted"])
        for secret in ("private-serial", "secret-path-a", "private-a", "serial_number", "physical_id", '"path"'):
            self.assertNotIn(secret, encoded)
        self.assertTrue(all(name in ("topology", "bluetooth") for name, _ in pool.calls))

    def test_cards_and_native_and_plain_keyboards_are_excluded(self):
        infos = [collection("card"), collection("native", pid=0x1278),
                 collection("headset", vid=0x03F0, pid=0x098D),
                 collection("keyboard", page=1, usage=6), collection("mouse", page=1, usage=2)]
        pool, engine = self.engine(infos, {("pulsar", "private-card"):
                                  (0, [DeviceStatus("card", "Card", 70, source="pulsar")])}, ("pulsar",))
        result = engine.poll(force=True)
        self.assertEqual(len(result["devices"]), 1)
        self.assertEqual(len(result["unadapted"]), 1)
        self.assertEqual(result["unadapted"][0]["collections"], [{"usagePage": 1, "usage": 2}])
        self.assertNotIn(("pulsar", "private-native"), pool.calls)

    def test_limits_and_stable_order_include_all_collections(self):
        infos = [collection(str(i), pid=0x2000+i, page=0xFF00+j, usage=j) for i in range(20) for j in range(10)]
        for info in infos:
            info["product_string"] = "A" * 70
        pool, engine = self.engine(infos)
        first = engine.poll(force=True)["unadapted"]
        self.assertEqual(len(first), 16)
        self.assertTrue(all(len(item["collections"]) == 8 and len(item["product"]) == 48 for item in first))
        self.assertEqual([item["productId"] for item in first], list(range(0x2000, 0x2010)))
        pool.inventory.reverse()
        self.assertEqual(engine.poll(force=True)["unadapted"], first)

    def test_partial_targeted_and_failed_discovery_keep_last_full_result(self):
        pool, engine = self.engine([collection("a")], providers=("pulsar",))
        first = engine.poll(force=True)["unadapted"]
        pool.inventory[:] = [collection("b", pid=0x120B)]
        partials = []
        engine.poll(force=True, physical_id=server.public_id("private-b"), progress=partials.append)
        self.assertTrue(partials)
        self.assertTrue(all(frame["unadapted"] == first for frame in partials))
        self.assertEqual(engine._snapshot()["unadapted"], first)
        pool.outcomes[("topology", "")] = (0, IsolationError("timeout"))
        self.assertEqual(engine.poll(force=True)["unadapted"], first)
        pool.outcomes.clear()
        partials.clear()
        last = engine.poll(force=True, progress=partials.append)
        self.assertTrue(all(not frame["complete"] and frame["unadapted"] == first for frame in partials))
        self.assertEqual(last["unadapted"][0]["productId"], 0x120B)
        pool.inventory.clear()
        self.assertEqual(engine.poll(force=True)["unadapted"], [])

    def test_device_target_does_not_recompute_unadapted(self):
        pool, engine = self.engine([collection("card"), collection("unknown", pid=0x120B)],
                                  {("pulsar", "private-card"): (0, [DeviceStatus("c", "Card", 70, source="pulsar")])}, ("pulsar",))
        first = engine.poll(force=True)
        pool.inventory[:] = [collection("card")]
        result = engine.poll(force=True, device_id=first["devices"][0]["id"])
        self.assertEqual(result["unadapted"], first["unadapted"])

    def test_dynamic_atk_is_not_a_recognized_candidate(self):
        self.assertEqual(server.recognized_candidate_count("pulsar", [collection("a")]), 0)
        self.assertEqual(server.recognized_candidate_count("pulsar", [collection("a", pid=0x1085)]), 1)

    def test_native_never_reaches_dynamic_pulsar_probe(self):
        provider = pulsar.PulsarProvider()
        with patch.object(pulsar, "output_length", side_effect=AssertionError("native descriptor probe")), \
             patch.object(provider, "_query", side_effect=AssertionError("native query")):
            self.assertEqual(server.scoped_poll(provider, [collection("native", pid=0x1278)], "private-native"), [])

    def test_previous_card_is_excluded_even_if_no_reader_answers_now(self):
        pool, engine = self.engine([collection("card")], {("pulsar", "private-card"):
                                  (0, [DeviceStatus("c", "Card", 70, source="pulsar")])}, ("pulsar",))
        self.assertEqual(engine.poll(force=True)["unadapted"], [])
        pool.outcomes.clear()
        result = engine.poll(force=True)
        self.assertEqual(len(result["devices"]), 1)
        self.assertEqual(result["unadapted"], [])

    def test_same_model_instances_remain_separate_and_duplicate_collections_remain(self):
        _, engine = self.engine([collection("a"), collection("a"), collection("b")])
        result = engine.poll(force=True)["unadapted"]
        self.assertEqual(len(result), 2)
        self.assertEqual(sorted(len(item["collections"]) for item in result), [1, 2])


if __name__ == "__main__":
    unittest.main()
