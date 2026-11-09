"""CHERRY registration, public state and isolated JSON bridge."""
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
import test_cherry as fixtures
from catalog import verification_for
from known_ids import build_index
from isolation import wire_encode
from providers.cherry import CherryProvider
from providers.bluetooth import BluetoothProvider, K_BAT, K_CONN
import server


class CherryBridgeTests(unittest.TestCase):
    def test_bluetooth_system_property_and_missing_value_keep_evidence_semantics(self):
        # Sanitized identity; 180F/2A19 supplies a percentage, no charging field.
        root = r"BTHLE\DEV_AABBCCDDEEFF\fixture"
        data = dict(devs=[dict(id=root, name="MX2.0S WL Pro-BT1", status="OK")],
                    props=[dict(id=root, key=K_BAT, data=64), dict(id=root, key=K_CONN, data=True)],
                    aep=[dict(addr="AABBCCDDEEFF", conn=True, le=True, cls=961)])
        provider = BluetoothProvider()
        status, = provider.parse(json.dumps(data))
        self.assertEqual((status.level, status.kind, status.name), (64, "keyboard", "MX2.0S WL Pro-BT1"))
        self.assertEqual(status.source_quality, "system_property")
        self.assertTrue(status.connection_known)
        self.assertFalse(status.charging_known)
        self.assertFalse(status.cached)
        self.assertIsNone(status.measured_at_utc)
        queried = status.queried_at_utc
        data["props"] = [dict(id=root, key=K_CONN, data=True)]
        cached, = provider.parse(json.dumps(data))
        self.assertEqual((cached.level, cached.queried_at_utc), (64, queried))
        self.assertTrue(cached.cached)
        self.assertFalse(cached.charging_known)
        data["aep"][0]["conn"] = False
        data["props"][0]["data"] = False
        self.assertEqual(provider.parse(json.dumps(data)), [])

    def test_registration_catalog_and_offline_index(self):
        self.assertIn(("cherry", "CherryProvider"), server.PROVIDERS)
        self.assertEqual(server.PROVIDER_VIDS["cherry"], (0x046A,))
        self.assertEqual(verification_for("cherry")[0], "C")
        rows = [r for r in build_index()["devices"] if r["provider"] == "cherry"]
        self.assertEqual({(r["vendorId"], r["productId"], r["kind"]) for r in rows},
                         {(0x046A, 0x01E0, "keyboard"), (0x046A, 0x01DE, "keyboard")})

    def test_public_state_and_failed_query_retain_only_history(self):
        engine = server.BatteryEngine([CherryProvider()], include_native=True)
        self.addCleanup(engine.close)
        with fixtures.CherryTests().poll([fixtures.reply(charging=1)]):
            first, = engine.poll()["devices"]
        self.assertEqual((first["percent"], first["kind"]), (58, "keyboard"))
        self.assertTrue(first["charging"])
        with fixtures.CherryTests().poll([]):
            stale, = engine.poll()["devices"]
        self.assertEqual(stale["percent"], 58)
        self.assertEqual(stale["confirmedAtUtc"], first["confirmedAtUtc"])
        self.assertFalse(stale["fresh"])
        self.assertIsNone(stale["charging"])
        self.assertEqual(stale["connectionState"], "unconfirmed")

    def test_isolated_reader_scopes_exact_physical_receiver(self):
        infos = fixtures.inventory()
        command = dict(action="query", sequence=1, provider="cherry",
                       physicalId=infos[0]["physical_id"], inventory=infos)
        wire = (json.dumps(command, default=wire_encode) + "\n").encode()
        with fixtures.CherryTests().poll([fixtures.reply()]) as bus, \
                patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(wire))), \
                patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
            server.reader_child()
        result = json.loads(output.getvalue())
        self.assertEqual(result["statuses"][0]["level"], 58)
        self.assertEqual(bus.opened, [infos[0]["path"]])


if __name__ == "__main__":
    unittest.main()
