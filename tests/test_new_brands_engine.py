"""Batch-3 registration, public evidence and isolated-reader JSON boundary."""
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
from test_new_brands import CASES, E, F, G, entry, fixture, reply
from catalog import verification_for
from isolation import wire_encode
from known_ids import build_index
from providers.base import DeviceStatus
import server

T1, T2 = "2026-11-04T00:00:00Z", "2026-11-04T00:01:00Z"


class NewBrandsBridgeTests(unittest.TestCase):
    def test_registration_catalog_and_offline_ids(self):
        index = build_index()
        for _, factory in CASES:
            with self.subTest(provider=factory.name):
                self.assertIn((factory.name, factory.__name__), server.PROVIDERS)
                self.assertEqual(server.PROVIDER_VIDS[factory.name], (factory.vid,))
                self.assertEqual(verification_for(factory.name)[0], "C")
                self.assertNotIn(factory.name, index["unparsed"])
                row, = [r for r in index["devices"] if r["provider"] == factory.name]
                self.assertEqual((row["vendorId"], row["productId"], row["kind"]),
                                 (factory.vid, next(iter(factory.pids)), factory.kind))

    def test_public_approximation_charging_and_verification(self):
        for module, factory in CASES:
            for level in range(0, 101, 20):
                with fixture(factory, [reply(module, level)]):
                    status, = factory().poll()
                public = server.normalise(status, None, T1)
                self.assertEqual(public["approximate"], module is F)
                self.assertEqual((public["percent"], public["kind"], public["verificationLevel"]),
                                 (level, factory.kind, "C"))
                self.assertEqual(public["charging"], False if module is F else None)

    def test_apex_charging_clears_old_percentage_then_recovers(self):
        factory = F.FlydigiProvider
        with fixture(factory, [reply(F, 80)]):
            status, = factory().poll()
        old = server.normalise(status, None, T1)
        with fixture(factory, [reply(F, **{"12": 0x16})]):
            status, = factory().poll()
        charge = server.normalise(status, old, T2)
        self.assertIsNone(charge["percent"])
        self.assertTrue(charge["charging"])
        self.assertTrue(charge["chargingKnown"])
        self.assertIsNone(charge["confirmedAtUtc"])
        with fixture(factory, [reply(F, 100)]):
            status, = factory().poll()
        fresh = server.normalise(status, charge, T2)
        self.assertEqual(fresh["percent"], 100)
        self.assertTrue(fresh["approximate"])
        self.assertFalse(fresh["charging"])

    def test_failed_poll_retains_only_unconfirmed_history(self):
        for module, factory in CASES:
            engine = server.BatteryEngine([factory()], include_native=True)
            self.addCleanup(engine.close)
            with fixture(factory, [reply(module)]):
                first, = engine.poll()["devices"]
            with fixture(factory, [[0] * 32]):
                stale, = engine.poll()["devices"]
            self.assertEqual(stale["percent"], first["percent"])
            self.assertEqual(stale["confirmedAtUtc"], first["confirmedAtUtc"])
            self.assertEqual(stale["connectionState"], "unconfirmed")
            self.assertFalse(stale["fresh"])
            self.assertIsNone(stale["charging"])

    def test_scoped_reader_receives_report_sizes_through_json(self):
        for module, factory in CASES:
            infos = [entry(factory, "private-one"), entry(factory, "private-two")]
            request = dict(action="query", sequence=1, provider=factory.name,
                           physicalId="private-one", inventory=infos)
            data = (json.dumps(request, default=wire_encode) + "\n").encode()
            with fixture(factory, [reply(module)], infos) as bus, \
                    patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                    patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
                server.reader_child()
            response = json.loads(output.getvalue())
            self.assertEqual(len(response["statuses"]), 1)
            self.assertEqual(bus.opened, [infos[0]["path"]])
            self.assertEqual(response["statuses"][0]["level"], 60)

    def test_unrelated_controller_paths_remain_excluded(self):
        engine = server.BatteryEngine([], include_native=True)
        self.addCleanup(engine.close)
        for source in ("pulsar", "g3m", "fake"):
            engine._accept(source, source, [DeviceStatus("pad", "Other controller", 60,
                                                       source=source, kind="gamepad")], T1)
            self.assertEqual(engine.by_provider[source], {})


if __name__ == "__main__":
    unittest.main()
