"""Issue #51: reported collection shapes and actionable, private reader health.

The report gives usages, not interface numbers or feature replies. Interface
numbers below are synthetic; these tests do not establish a hardware fix.
"""
from contextlib import contextmanager
import io
import json
from pathlib import Path
import re
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ENGINE = Path(__file__).resolve().parents[1] / "engine"
sys.path.insert(0, str(ENGINE))
sys.path.insert(0, str(ENGINE / "tests"))
import _hidstub  # noqa: F401
from providers import razer as R
from isolation import wire_encode
import server


def issue_inventory(pid):
    text = (Path(__file__).parent / "fixtures" / "issue51-body.txt").read_text(encoding="utf-8")
    match = re.search(rf'^1532:{pid:04X} "([^"]+)" (.+)$', text, re.M)
    return [dict(vendor_id=0x1532, product_id=pid, product_string=match[1],
                 path=f"private-path-{pid:04x}-{i}".encode(), serial_number="private-serial",
                 physical_id="private-parent", interface_number=i if i % 2 == 0 else -1,
                 usage_page=int(page, 16), usage=int(usage, 16))
            for i, (page, usage) in enumerate(re.findall(r"([0-9A-F]{4}):([0-9A-F]{4})", match[2]))]


class Clock:
    now = 1000.0

    def time(self):
        return self.now

    def sleep(self, seconds):
        self.now += seconds


class Bus:
    def __init__(self, modes):
        self.modes = modes
        self.opens = []
        self.requests = []

    def device(self):
        bus = self

        class Device:
            def open_path(self, path):
                bus.opens.append(path)
                self.mode = bus.modes.get(path, "open_failed")
                if self.mode == "open_failed":
                    raise OSError("private-path private-serial private-os-error")

            def send_feature_report(self, report):
                if self.mode == "send_failed":
                    raise OSError("private-os-error")
                self.request = bytes(report[1:])
                bus.requests.append(self.request)
                return len(report)

            def get_feature_report(self, report_id, size):
                if self.mode == "get_failed":
                    raise OSError("private-os-error")
                if self.mode == "short_reply":
                    return [0, 2]
                reply = bytearray(90)
                reply[1:8] = self.request[1:8]
                reply[0] = 2
                reply[9] = 181 if self.request[7] == 0x80 else 0
                if isinstance(self.mode, int):
                    reply[0] = self.mode
                elif self.mode == "foreign_reply":
                    reply[6:8] = b"\x0f\x03"
                elif self.mode == "charging_failed" and self.request[7] == 0x84:
                    raise OSError("private-os-error")
                return [0] + list(reply)

            def close(self):
                pass

        return Device()


@contextmanager
def fixture(inventory, modes):
    bus, clock = Bus(modes), Clock()
    with patch.object(R, "hid", SimpleNamespace(device=bus.device)), \
            patch.object(R.hidlist, "enumerate", return_value=inventory), \
            patch.object(R, "time", clock):
        yield bus, clock


class RazerUnavailableTests(unittest.TestCase):
    def test_every_reported_collection_can_be_reached(self):
        for pid in (0x00BF, 0x00C1):
            infos = issue_inventory(pid)
            self.assertEqual([(d["usage_page"], d["usage"]) for d in infos],
                             [(1, u) for u in (0, 0, 2, 2, 6, 6, 128, 128)])
            self.assertEqual(R.KNOWN[pid][1], 0x1F)
            for working in infos:
                with self.subTest(pid=pid, collection=working["path"]), \
                        fixture(infos, {working["path"]: "ok"}) as (bus, _):
                    provider = R.RazerProvider()
                    status, = provider.poll()
                    self.assertEqual((status.level, status.online), (71, True))
                    self.assertEqual(provider.failure_code(), "")
                    self.assertEqual([(r[1], r[6], r[7]) for r in bus.requests],
                                     [(0x1F, 7, 0x80), (0x1F, 7, 0x84)])

    def test_failure_stages_and_status_codes_for_both_models(self):
        cases = [(s, s) for s in ("open_failed", "send_failed", "get_failed", "short_reply", "foreign_reply")]
        cases += [(1, "busy_timeout"), (3, "reply_status_03"), (4, "device_timeout_04"),
                  (5, "reply_status_05"), (0x42, "reply_status_42")]
        for pid in (0x00BF, 0x00C1):
            infos = issue_inventory(pid)
            for mode, code in cases:
                with self.subTest(pid=pid, mode=mode), fixture(infos, {infos[-1]["path"]: mode}):
                    provider = R.RazerProvider()
                    self.assertEqual(provider.poll(), [])
                    self.assertEqual(provider.failure_code(), "razer_" + code)
                    self.assertLessEqual(len(provider.failure_code()), 48)

    def test_cooldown_recovery_disconnect_and_charging_failure(self):
        infos = issue_inventory(0x00BF)
        modes = {}
        with fixture(infos, modes) as (bus, clock):
            provider = R.RazerProvider()
            self.assertEqual(provider.poll(), [])
            self.assertEqual(provider.failure_code(), "razer_open_failed")
            count = len(bus.opens)
            self.assertEqual(provider.poll(), [])
            self.assertEqual(provider.failure_code(), "razer_interfaces_cooling_down")
            self.assertEqual(len(bus.opens), count)
            clock.sleep(301)
            modes[infos[-1]["path"]] = "charging_failed"
            self.assertEqual(provider.poll()[0].level, 71)
            self.assertEqual(provider.failure_code(), "")
            modes[infos[-1]["path"]] = 4
            self.assertFalse(provider.poll()[0].online)  # retained history, not a new reading
            self.assertEqual(provider.failure_code(), "razer_device_timeout_04")
            infos.clear()
            self.assertEqual(provider.poll(), [])
            self.assertEqual(provider.failure_code(), "")

    def test_no_interface_and_enumeration_failure(self):
        provider = R.RazerProvider()
        self.assertIsNone(provider._poll_group((0x00BF, ""), [], 0x1F))
        self.assertIn("no_usable_interface", provider._read_errors)
        with patch.object(R.hidlist, "enumerate", side_effect=OSError("private-os-error")), \
                self.assertLogs("halo_battery", level="WARNING"):
            self.assertEqual(provider.poll(), [])
        self.assertEqual(provider.failure_code(), "razer_enumerate_failed")

    def test_reader_child_and_manager_export_only_safe_failure_code(self):
        for pid in (0x00BF, 0x00C1):
            infos = issue_inventory(pid)
            command = dict(action="query", sequence=1, provider="razer",
                           physicalId="private-parent", inventory=infos)
            data = (json.dumps(command, default=wire_encode) + "\n").encode()
            with self.subTest(pid=pid), fixture(infos, {infos[-1]["path"]: 5}), \
                    patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                    patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
                server.reader_child()
            result = json.loads(output.getvalue())
            self.assertEqual(result["statuses"], [])
            self.assertEqual(result["candidateCount"], 1)
            self.assertEqual(result["failureCode"], "razer_reply_status_05")

            class Reader:
                def start(self, command, budget):
                    self.command = command

                def result(self):
                    if self.command["action"] == "topology":
                        return {"inventory": infos}
                    return result if self.command["provider"] == "razer" else {"statuses": []}

                def close(self):
                    pass

            engine = server.BatteryEngine(process_factory=Reader)
            self.addCleanup(engine.close)
            engine.providers = ["razer"]
            with patch.object(engine._hidlist, "interface_paths", return_value=frozenset()):
                snapshot = engine.poll()
            health, = [h for h in snapshot["health"] if h["provider"] == "razer"]
            self.assertEqual(health["state"], "unavailable")
            self.assertEqual(health["errorCode"], "razer_reply_status_05")
            self.assertIn("razer: razer_reply_status_05", snapshot["errors"])
            for private in ("private-path", "private-serial", "private-parent", "private-os-error"):
                self.assertNotIn(private, output.getvalue())
                self.assertNotIn(private, json.dumps(snapshot))

    def test_health_rejects_raw_errors_and_clears_previous_failure(self):
        engine = server.BatteryEngine([], include_native=True)
        self.addCleanup(engine.close)
        for code in ("razer_open_failed", "razer_reply_status_05", "razer_interfaces_cooling_down"):
            engine._accept("r", "razer", [], server.utcnow(), "p", 1, failure_code=code)
            self.assertEqual(engine.health["r"]["errorCode"], code)
        for code in ("", None, [], "razer_open_failed private-path", "razer_reply_status_100"):
            engine._accept("r", "razer", [], server.utcnow(), "p", 1, failure_code=code)
            self.assertNotIn("errorCode", engine.health["r"])
        engine._accept("r", "other", [], server.utcnow(), "p", 1, failure_code="razer_open_failed")
        self.assertNotIn("errorCode", engine.health["r"])

    def test_direct_engine_reports_failure_and_clears_it_on_success(self):
        infos = issue_inventory(0x00C1)
        modes = {infos[-1]["path"]: 4}
        engine = server.BatteryEngine([R.RazerProvider()], include_native=True)
        self.addCleanup(engine.close)
        with fixture(infos, modes):
            self.assertEqual(engine.poll()["health"][0]["errorCode"], "razer_device_timeout_04")
            modes[infos[-1]["path"]] = "ok"
            snapshot = engine.poll()
            self.assertNotIn("errorCode", snapshot["health"][0])
            self.assertTrue(snapshot["devices"][0]["fresh"])


if __name__ == "__main__":
    unittest.main()
