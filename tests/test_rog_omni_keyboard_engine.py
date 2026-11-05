"""Registration, scoped transport and device-state integration for the Omni reader."""
import io
import json
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ENGINE = Path(__file__).resolve().parents[1] / "engine"
sys.path[:0] = [str(ENGINE), str(ENGINE / "tests")]
import _hidstub  # noqa: F401
from providers import rog_omni_keyboard as R
from test_rog_omni_keyboard import Bus, battery, entry, paired
from catalog import verification_for
from isolation import wire_encode
from known_ids import build_index
import server


class RogOmniEngineTests(unittest.TestCase):
    def test_registry_and_catalog_are_scoped(self):
        self.assertIn(("rog_omni_keyboard", "RogOmniKeyboardProvider"), server.PROVIDERS)
        self.assertEqual(server.PROVIDER_VIDS["rog_omni_keyboard"], (0x0B05,))
        self.assertEqual(verification_for("rog_omni_keyboard")[0], "C")
        index = build_index()
        self.assertNotIn("rog_omni_keyboard", index["unparsed"])
        row, = [row for row in index["devices"] if row["provider"] == "rog_omni_keyboard"]
        self.assertEqual((row["vendorId"], row["productId"], row["kind"]), (0x0B05, 0x1ACE, "keyboard"))

    def test_child_scopes_a_receiver_and_preserves_unknown_charging(self):
        inventory = [entry(0xFF02, b"receiver"), entry(0xFF00, b"keyboard"),
                     entry(0xFF02, b"other-r", physical="pnp:other"),
                     entry(0xFF00, b"other-k", physical="pnp:other")]
        command = dict(action="query", sequence=1, provider="rog_omni_keyboard",
                       physicalId="pnp:test", inventory=inventory)
        wire = (json.dumps(command, default=wire_encode) + "\n").encode()
        bus = Bus()
        with patch.object(R.hid, "device", side_effect=bus.device), \
             patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(wire))), \
             patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
            server.reader_child()
        response = json.loads(output.getvalue())
        status, = response["statuses"]
        self.assertEqual((status["name"], status["level"], status["kind"]), (R.NAME, 46, "keyboard"))
        self.assertFalse(status["charging_known"])
        self.assertEqual(response["candidateCount"], 1)
        self.assertEqual(bus.opened, [b"receiver", b"keyboard"])
        self.assertEqual(bus.opened, bus.closed)

    def test_different_paired_keyboard_is_not_claimed(self):
        provider = R.RogOmniKeyboardProvider()
        bus = Bus({b"receiver": paired(((0x1A85, 2, 4),)), b"keyboard": battery()})
        inventory = [entry(0xFF02, b"receiver"), entry(0xFF00, b"keyboard")]
        with patch.object(R.hidlist, "enumerate", return_value=inventory), \
             patch.object(R.hid, "device", side_effect=bus.device):
            self.assertEqual(provider.poll(), [])
        self.assertEqual(provider.candidate_count, 0)
        self.assertEqual(len(bus.writes), 1)


if __name__ == "__main__":
    unittest.main()
