"""Keyboard registration, diagnostics and cache behavior through the bridge."""
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
from test_mchose_keyboards import CASES, entry, fixture, glw_frames, qhw_reply
from catalog import verification_for
from isolation import wire_encode
from known_ids import build_index
import server


class KeyboardBridgeTests(unittest.TestCase):
    def test_registration_kind_level_c_and_offline_index(self):
        index = build_index()
        for protocol, factory in CASES:
            name = factory.name
            with self.subTest(protocol=protocol):
                self.assertIn((name, factory.__name__), server.PROVIDERS)
                self.assertEqual(server.PROVIDER_VIDS[name], (0x3837,))
                self.assertEqual(verification_for(name)[0], "C")
                self.assertNotIn(name, index["unparsed"])
                row, = [row for row in index["devices"] if row["provider"] == name]
                self.assertEqual((row["vendorId"], row["productId"], row["kind"]),
                                 (0x3837, entry(protocol)["product_id"], "keyboard"))

    def test_good_read_then_silence_keeps_only_unconfirmed_history(self):
        for protocol, factory in CASES:
            engine = server.BatteryEngine([factory()], include_native=True)
            self.addCleanup(engine.close)
            frames = [qhw_reply(73, 1)] if protocol == "qhw" else glw_frames(73, True)
            with self.subTest(protocol=protocol), fixture(protocol, frames):
                first, = engine.poll()["devices"]
                self.assertEqual((first["percent"], first["charging"], first["kind"], first["verificationLevel"]),
                                 (73, True, "keyboard", "C"))
                self.assertTrue(first["fresh"])
            with fixture(protocol):
                snapshot = engine.poll()
            stale, = snapshot["devices"]
            self.assertEqual(stale["percent"], 73)
            self.assertEqual(stale["confirmedAtUtc"], first["confirmedAtUtc"])
            self.assertEqual(stale["connectionState"], "unconfirmed")
            self.assertFalse(stale["fresh"])
            self.assertIsNone(stale["charging"])
            self.assertIn("connection unknown", " ".join(snapshot["health"][0]["diagnostics"]))

    def test_scoped_reader_retains_private_diagnostics_without_exporting_identity(self):
        for protocol, factory in CASES:
            info = entry(protocol, "private-parent", path=b"private-path", input_report_ids=None)
            command = dict(action="query", sequence=1, provider=factory.name,
                           physicalId="private-parent", inventory=[info])
            data = (json.dumps(command, default=wire_encode) + "\n").encode()
            with self.subTest(protocol=protocol), fixture(protocol, inventory=[info]) as bus, \
                    patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                    patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
                server.reader_child()
                self.assertEqual(bus.writes, [])
            result = json.loads(output.getvalue())
            self.assertEqual(result["statuses"], [])
            self.assertEqual(result["candidateCount"], 1)
            self.assertIn("report IDs unknown", " ".join(result["diagnostics"]))
            self.assertNotIn("private-parent", output.getvalue())
            self.assertNotIn("private-path", output.getvalue())
            engine = server.BatteryEngine([], include_native=True)
            self.addCleanup(engine.close)
            engine._accept(factory.name, factory.name, [], server.utcnow(), "private-parent", 1,
                           diagnostics=result["diagnostics"])
            self.assertEqual(engine.health[factory.name]["diagnostics"], result["diagnostics"])

    def test_scoped_reader_refuses_unproven_parent_even_with_one_selected_path(self):
        for protocol, factory in CASES:
            infos = [entry(protocol, "collection:one"), entry(protocol, "collection:two")]
            with self.subTest(protocol=protocol), fixture(protocol, inventory=infos) as bus:
                for info in infos:
                    provider = factory()
                    self.assertEqual(server.scoped_poll(provider, infos, info["physical_id"]), [])
                    self.assertIn("uniqueness unknown", " ".join(provider.diagnostics()))
                self.assertEqual(bus.writes, [])


if __name__ == "__main__":
    unittest.main()
