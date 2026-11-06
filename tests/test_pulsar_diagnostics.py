"""Safe Pulsar health through the child wire and parent snapshot; fake HID only."""
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
import test_pulsar as F
from isolation import wire_encode, wire_decode
import server


def child_replies(commands):
    lines = "".join(json.dumps(command, default=wire_encode) + "\n" for command in commands)
    with patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(lines.encode()))), \
            patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
        server.reader_child()
    return [json.loads(line, object_hook=wire_decode) for line in output.getvalue().splitlines()]


class ReplyReader:
    def __init__(self, inventory, replies):
        self.inventory, self.replies = inventory, replies
        self.pending = None

    def start(self, command, budget):
        if self.pending is not None:
            raise AssertionError("reader reentered")
        self.pending = command

    def result(self):
        command, self.pending = self.pending, None
        if command["action"] == "topology":
            return {"inventory": self.inventory}
        if command.get("provider") == "bluetooth":
            return {"statuses": [], "checkedAtUtc": server.utcnow()}
        if command["action"] != "query" or command["provider"] != "pulsar":
            raise AssertionError(command)
        return self.replies.pop(0)

    def close(self):
        self.pending = None


class PulsarDiagnosticBridgeTests(unittest.TestCase):
    def query(self, info, sequence=1, inventory=None):
        return dict(action="query", sequence=sequence, provider="pulsar",
                    physicalId=info["physical_id"], inventory=[info] if inventory is None else inventory)

    def engine(self, infos, replies):
        engine = server.BatteryEngine(process_factory=lambda: ReplyReader(infos, replies))
        engine.providers = ["pulsar"]
        self.addCleanup(engine.close)
        return engine

    def poll(self, engine):
        with patch.object(engine._hidlist, "interface_paths", return_value=frozenset({"PRIVATE-PATH"})):
            return engine.poll(force=True)

    def assert_private_free(self, health):
        text = json.dumps(health)
        secrets = ["PRIVATE", "EWEADN", "Scyrox", "DPI-TARGET"]
        for frame in (F.reply(73), F.reply(73, command=0x0A)):
            secrets.extend((frame.hex(), " ".join(f"{b:02x}" for b in frame)))
        for secret in secrets:
            self.assertNotIn(secret, text)

    def health(self, snapshot):
        health, = [h for h in snapshot["health"] if h["provider"] == "pulsar"]
        return health

    def test_issue323_child_reason_reaches_unavailable_snapshot_without_failure(self):
        info = F.diagnostic_info()
        with F.diagnostic_hid([info], F.length_calls(info, None)):
            response, = child_replies([self.query(info)])
        self.assertEqual(response.get("diagnostics"), ["output=17 unproven; nothing sent"])
        self.assertEqual((response["sequence"], response["candidateCount"], response["statuses"]), (1, 1, []))
        engine = self.engine([info], [response])
        snapshot = self.poll(engine)
        health = self.health(snapshot)
        self.assertEqual((health["state"], health["reason"]), ("unavailable", "no_measurement"))
        self.assertEqual(health["errorCode"], "output=17 unproven; nothing sent")
        self.assertEqual(health["diagnostics"], response["diagnostics"])
        self.assertEqual(snapshot["errors"], [])
        self.assertEqual(engine.failures, {})
        self.assertEqual(engine.retry_after, {})
        internal, = [h for h in engine.health.values() if h["provider"] == "pulsar"]
        self.assertNotIn("errorCode", internal)
        self.assertEqual(engine._snapshot()["health"], snapshot["health"])
        self.assert_private_free(snapshot["health"])

    def test_child_and_snapshot_recovery_then_silence_keep_history_and_refresh_reason(self):
        info = F.diagnostic_info()
        script = F.length_calls(info, None)
        script += F.length_calls(info, 17, 17) + F.battery_calls(info, [F.reply(73, 1)], True)
        script += F.length_calls(info, 17, 17) + F.battery_calls(info)
        commands = [self.query(info, sequence=n) for n in (1, 2, 3)]
        commands.append(self.query(info, sequence=4, inventory=[]))
        with F.diagnostic_hid([info], script):
            replies = child_replies(commands)
        self.assertEqual([r.get("diagnostics") for r in replies], [
            ["output=17 unproven; nothing sent"], ["valid battery reply"],
            ["no valid battery reply; connection unknown"], []])
        engine = self.engine([info], replies)
        self.assertTrue(self.health(self.poll(engine))["errorCode"])
        fresh_snapshot = self.poll(engine)
        health = self.health(fresh_snapshot)
        self.assertEqual(health["state"], "ok")
        self.assertFalse(health.get("errorCode"))
        fresh, = fresh_snapshot["devices"]
        self.assertEqual((fresh["percent"], fresh["fresh"]), (73, True))
        stale_snapshot = self.poll(engine)
        health = self.health(stale_snapshot)
        # The existing cached-card health remains ok; the reason describes this poll.
        self.assertEqual(health["state"], "ok")
        self.assertEqual(health["errorCode"], "no valid battery reply; connection unknown")
        stale, = stale_snapshot["devices"]
        self.assertEqual((stale["percent"], stale["fresh"], stale["chargingKnown"]), (73, False, False))
        self.assertEqual(stale["confirmedAtUtc"], fresh["confirmedAtUtc"])
        self.assertEqual(stale["measuredAtUtc"], fresh["measuredAtUtc"])
        cleared = self.poll(engine)
        self.assertEqual(self.health(cleared)["diagnostics"], [])
        self.assertFalse(self.health(cleared).get("errorCode"))
        for snapshot in (fresh_snapshot, stale_snapshot, cleared):
            self.assertEqual(snapshot["errors"], [])
            self.assert_private_free(snapshot["health"])
        self.assertEqual((engine.failures, engine.retry_after), ({}, {}))

    def test_link8k_long_reason_fits_48_character_projection(self):
        info = F.diagnostic_info(*F.P.LINK8K_ID, input_report_sizes=None)
        with F.diagnostic_hid([info], []):
            response, = child_replies([self.query(info)])
        engine = self.engine([info], [response])
        snapshot = self.poll(engine)
        health = self.health(snapshot)
        self.assertEqual(health["diagnostics"], ["report-8 input/output lengths unproven; nothing sent"])
        self.assertGreater(len(health["diagnostics"][0]), 48)
        self.assertLessEqual(len(health["errorCode"]), 48)
        self.assertIn("lengths unproven", health["errorCode"])
        self.assertEqual(snapshot["errors"], [])
        self.assert_private_free(snapshot["health"])

    def test_public_channel_never_reads_legacy_paths_products_frames_exceptions_or_dpi(self):
        info = F.diagnostic_info()
        for failure, reason in (("open", "battery open failed"), ("write", "battery write failed"),
                                ("read", "battery read failed"), (None, "no valid battery reply; connection unknown")):
            frames = [F.reply(73, command=0x0A)] if failure is None else []
            script = F.length_calls(info, 17, 17) + F.battery_calls(info, frames, failure=failure)
            with self.subTest(failure=failure), F.diagnostic_hid([info], script):
                provider = F.P.PulsarProvider()
                statuses = provider.poll()
            provider._diag.append("PRIVATE-PATH PRIVATE-SERIAL PRIVATE-PRODUCT PRIVATE-EXCEPTION DPI-TARGET")
            with patch.object(provider, "diagnostics", side_effect=AssertionError("legacy log must stay private")):
                diagnostic = server.reader_diagnostics(provider)
            self.assertEqual(diagnostic, [reason])
            self.assertLessEqual(len(diagnostic), 16)
            self.assert_private_free(diagnostic)
            engine = server.BatteryEngine([], include_native=True)
            self.addCleanup(engine.close)
            engine._accept("pulsar|PRIVATE-PHYSICAL", "pulsar", statuses, server.utcnow(),
                           physical=info["physical_id"], candidate_count=1, diagnostics=diagnostic)
            snapshot = engine._snapshot()
            self.assertEqual(self.health(snapshot)["errorCode"], reason)
            self.assertEqual(snapshot["errors"], [])
            self.assertEqual((engine.failures, engine.retry_after), ({}, {}))
            self.assert_private_free(snapshot)


if __name__ == "__main__":
    unittest.main()
