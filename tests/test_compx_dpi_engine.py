"""CompX parent/child routing and leases with fake HID only."""
import io
import json
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "engine"))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "engine" / "tests"))
import _hidstub  # noqa: F401
from test_compx_dpi import Mouse, fake
from providers import pulsar as P
from device_pack import DevicePack, Entry, Match
from isolation import IsolationError, wire_encode, wire_decode
import server


def child(command):
    output = io.StringIO()
    incoming = types.SimpleNamespace(buffer=io.BytesIO((json.dumps(command, default=wire_encode) + "\n").encode()))
    with patch.object(server.sys, "stdin", incoming), patch.object(server.sys, "stdout", output):
        server.reader_child()
    return json.loads(output.getvalue(), object_hook=wire_decode)


class NoReader:
    def close(self):
        pass


class Engine(server.BatteryEngine):
    def __init__(self, mouse):
        super().__init__(process_factory=NoReader)
        self.mouse = mouse
        self.commands = []
        self.present = True
        self.timeout = False
        self.physical = mouse.info["physical_id"]
        self.refresh()

    def refresh(self):
        status, = server.scoped_poll(P.PulsarProvider(), [self.mouse.info], self.physical)
        self.status = status
        self.device_id = server.public_id(status.key)
        self._accept("pulsar|" + self.physical, "pulsar", [status], server.utcnow(), self.physical)
        self._snapshot()

    def _single_reader(self, slot, command, budget):
        self.commands.append(command)
        if command["action"] == "topology":
            # An unrelated receiver and another provider must never be dispatched.
            return {"inventory": ([self.mouse.info] if self.present else []) + [
                dict(self.mouse.info, physical_id="other"), dict(self.mouse.info, vendor_id=0x046D)]}
        if self.timeout:
            raise IsolationError("timeout")
        return child(command)

    def change(self, value=800, expected=400, token=None):
        if token is None:
            token = self.status.dpi["targetToken"]
        with patch.object(self._hidlist, "interface_paths", return_value=frozenset()):
            return self.set_dpi(self.device_id, value, token, expected, 7, 2)


class CompXDpiEngineTests(unittest.TestCase):
    def test_parent_and_child_dispatch_only_selected_pulsar_instance(self):
        with fake(Mouse()) as mouse:
            engine = Engine(mouse)
            before = engine.previous[engine.device_id].copy()
            reply = engine.change()
            self.assertTrue(reply["dpiResult"]["success"], reply)
            self.assertEqual((reply["requestId"], reply["generation"]), (7, 2))
            self.assertEqual([c["action"] for c in engine.commands], ["topology", "set_dpi"])
            self.assertEqual(engine.commands[1]["provider"], "pulsar")
            self.assertEqual(engine.commands[1]["inventory"], [mouse.info])
            self.assertEqual(len(mouse.writes()), 1)
            device, = reply["devices"]
            self.assertEqual(device["dpi"]["stageSource"], "onboard")
            self.assertEqual(device["dpi"]["current"], 800)
            for key in ("measuredAtUtc", "confirmedAtUtc", "queriedAtUtc", "chargingCheckedAtUtc"):
                self.assertEqual(device[key], before[key])
            self.assertNotIn("fixture-serial", str(reply))
            self.assertNotIn("fixture-compx", str(reply))
            self.assertTrue(engine.change(400, 800)["dpiResult"]["success"])
            self.assertEqual(len(mouse.writes()), 2)

    def test_token_expected_value_offline_and_pack_reject_without_io(self):
        for condition in ("token", "expected", "value", "offline", "stale", "pack", "quick", "no_cap"):
            with self.subTest(condition=condition), fake(Mouse()) as mouse:
                engine = Engine(mouse)
                options = {}
                if condition == "token":
                    options["token"] = "f" * 64
                elif condition == "expected":
                    options["expected"] = 800
                elif condition == "value":
                    options["value"] = 450
                else:
                    status = engine.status
                    if condition == "offline":
                        status.online = False
                    elif condition == "pack":
                        status.model_source = "pack"
                    elif condition == "quick":
                        status.dpi["stageSource"] = "quick"
                    elif condition == "no_cap":
                        status.dpi["canSet"] = False
                    else:
                        status.dpi["checkedAtUtc"] = "2020-01-01T00:00:00Z"
                    engine._accept("pulsar|" + engine.physical, "pulsar", [status], server.utcnow(), engine.physical)
                mouse.frames.clear()
                result = engine.change(**options)
                self.assertFalse(result["dpiResult"]["success"])
                self.assertEqual(engine.commands, [])
                self.assertEqual(mouse.frames, [])

    def test_unknown_disables_write_until_new_poll_without_retries(self):
        for fault in ("timeout", "mismatch", "child_timeout"):
            with self.subTest(fault=fault), fake(Mouse()) as mouse:
                engine = Engine(mouse)
                if fault == "child_timeout":
                    engine.timeout = True
                else:
                    mouse.fault = fault
                result = engine.change()
                self.assertTrue(result["dpiResult"]["outcomeUnknown"])
                self.assertFalse(result["devices"][0]["dpi"]["canSet"])
                self.assertIsNone(result["devices"][0]["dpi"]["current"])
                self.assertNotIn(engine.device_id, engine.dpi_targets)
                self.assertFalse(engine.change()["dpiResult"]["success"])
                self.assertEqual(len(engine.commands), 2)
                mouse.fault, engine.timeout, mouse.after_write = None, False, False
                engine.refresh()
                expected = engine.status.dpi["current"]
                self.assertTrue(engine.change(400 if expected == 800 else 800, expected)["dpiResult"]["success"])

    def test_disconnect_and_failed_poll_remove_lease(self):
        with fake(Mouse()) as mouse:
            engine = Engine(mouse)
            engine.present = False
            self.assertEqual(engine.change()["dpiResult"]["error"], "disconnected")
            self.assertEqual(mouse.writes(), [])
            engine.refresh()
            engine._failure("pulsar|" + engine.physical, "pulsar", "timeout")
            engine.commands.clear()
            self.assertFalse(engine.change()["dpiResult"]["success"])
            self.assertEqual(engine.commands, [])

    def test_pack_query_and_child_write_are_blocked(self):
        with fake(Mouse()) as mouse:
            pack = DevicePack(version=1, loaded=True, document="fixture-pack", entries=(
                Entry("pulsar", "fixture", 1, Match(0x373B, 0x12A7), "Pack Mouse", "mouse"),))
            query = dict(action="query", provider="pulsar", physicalId=mouse.info["physical_id"],
                         inventory=[mouse.info], devicePack="fixture-pack")
            with patch.object(server, "parse_pack", return_value=pack):
                reply = child(query)
                self.assertIsNone(reply["statuses"][0]["dpi"])
                self.assertEqual(reply["statuses"][0]["model_source"], "pack")
                self.assertEqual([f[1] for f in mouse.frames], [4])
                mouse.frames.clear()
                with patch.object(P, "set_device_dpi", side_effect=AssertionError("pack must not dispatch")) as setter:
                    reply = child(dict(query, action="set_dpi"))
                    self.assertEqual(reply["errorCode"], "read_failed")
                    setter.assert_not_called()
                self.assertEqual(mouse.frames, [])


if __name__ == "__main__":
    unittest.main()
