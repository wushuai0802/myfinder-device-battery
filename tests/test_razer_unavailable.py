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
        self.sent = []
        self.closes = []

    def device(self):
        bus = self

        class Device:
            def open_path(self, path):
                bus.opens.append(path)
                self.path = path
                self.mode = bus.modes.get(path, "open_failed")
                if self.mode == "open_failed":
                    raise OSError("private-path private-serial private-os-error")

            def current_mode(self):
                return (self.mode.get(self.request[1], "foreign_reply")
                        if isinstance(self.mode, dict) else self.mode)

            def send_feature_report(self, report):
                self.request = bytes(report[1:])
                if self.current_mode() == "send_failed":
                    raise OSError("private-os-error")
                bus.requests.append(self.request)
                bus.sent.append((self.path, self.request))
                return len(report)

            def get_feature_report(self, report_id, size):
                mode = self.current_mode()
                if mode == "get_failed":
                    raise OSError("private-os-error")
                if mode == "short_reply":
                    return [0, 2]
                reply = bytearray(90)
                reply[1:8] = self.request[1:8]
                reply[0] = 2
                reply[9] = 181 if self.request[7] == 0x80 else 0
                if mode == "ok_charging" and self.request[7] == 0x84:
                    reply[9] = 1
                if isinstance(mode, int):
                    reply[0] = mode
                elif mode == "foreign_reply":
                    reply[6:8] = b"\x0f\x03"
                elif mode == "charging_failed" and self.request[7] == 0x84:
                    raise OSError("private-os-error")
                return [0] + list(reply)

            def close(self):
                bus.closes.append(self.path)

        return Device()


@contextmanager
def fixture(inventory, modes):
    bus, clock = Bus(modes), Clock()
    with patch.object(R, "hid", SimpleNamespace(device=bus.device)), \
            patch.object(R.hidlist, "enumerate", return_value=inventory), \
            patch.object(R, "time", clock):
        yield bus, clock


class RazerForeignRecoveryTests(unittest.TestCase):
    @staticmethod
    def inventory(paths=(b"a", b"b")):
        return [dict(vendor_id=0x1532, product_id=0x007D,
                     product_string="Razer DeathAdder V2 Pro", path=path,
                     serial_number="fake", physical_id="fake-parent",
                     interface_number=i, usage_page=1, usage=0)
                for i, path in enumerate(paths)]

    @staticmethod
    def pack(profile="hyperspeed-mouse-1f", match=None, denies=None):
        from test_device_pack import document, entry, pack
        values = {"profile": profile}
        if match is not None:
            values["match"] = match
        result = pack(document([entry(**values)], denies))
        if not result.loaded or result.rejected:
            raise AssertionError(result.rejected)
        return result

    def test_persistent_foreign_reaches_healthy_approved_interface(self):
        infos = self.inventory()
        with fixture(infos, {b"a": "foreign_reply", b"b": "ok"}) as (bus, _):
            provider = R.RazerProvider()
            result = []
            for _ in range(8):
                result = provider.poll()
                if result:
                    break
            self.assertIn(b"b", bus.opens)
            status, = result
            self.assertEqual((status.level, status.online), (71, True))
            self.assertEqual(provider._cache[(0x007D, "fake")].path, b"b")
            self.assertEqual(provider.failure_code(), "")
            opened, sent = len(bus.opens), len(bus.requests)
            self.assertTrue(provider.poll()[0].online)
            self.assertEqual(bus.opens[opened:], [b"b"])
            self.assertEqual(len(bus.requests) - sent, 2)
            self.assertEqual((provider._foreign, provider._rediscovery, provider._failing), ({}, {}, {}))

    def test_persistent_foreign_reaches_next_existing_tid(self):
        infos = self.inventory((b"a",))
        modes = {b"a": {0x3F: "foreign_reply", 0x1F: "ok"}}
        with fixture(infos, modes) as (bus, _):
            provider = R.RazerProvider()
            result = []
            for _ in range(6):
                result = provider.poll()
                if result:
                    break
            self.assertIn(0x1F, [r[1] for r in bus.requests])
            status, = result
            self.assertEqual((status.level, status.online), (71, True))
            self.assertEqual(provider._cache[(0x007D, "fake")].tid, 0x1F)

    def test_all_foreign_walks_existing_space_with_bounded_cost(self):
        infos = self.inventory()
        with fixture(infos, {b"a": "foreign_reply", b"b": "foreign_reply"}) as (bus, clock):
            provider = R.RazerProvider()
            for poll in range(14):
                opened, sent, before = len(bus.opens), len(bus.requests), clock.time()
                self.assertEqual(provider.poll(), [])
                reads = len(bus.opens) - opened
                self.assertGreater(reads, 0)
                self.assertLessEqual(reads, 1 if poll < 2 else 3 if poll == 2 else 2)
                self.assertEqual(len(bus.requests) - sent, reads * 3)
                self.assertLessEqual(clock.time() - before, reads * 1.1)
                self.assertLess(clock.time() - before, 7)
                self.assertEqual(provider.failure_code(), "razer_foreign_reply")
                self.assertEqual(provider._last, {})
                if poll >= 2:
                    self.assertNotIn((0x007D, "fake"), provider._cache)
                clock.sleep(101)
            self.assertEqual({(path, r[1]) for path, r in bus.sent},
                             {(path, tid) for path in (b"a", b"b") for tid in R.TRANSACTION_IDS})
            for request in bus.requests:
                self.assertEqual(request, R.build_request(request[1], 7, 0x80))
            self.assertEqual(bus.closes, bus.opens)

    def test_normal_cache_and_transient_foreign_cost_one_read(self):
        infos = self.inventory()
        modes = {b"a": "ok_charging", b"b": "ok"}
        with fixture(infos, modes) as (bus, _):
            provider = R.RazerProvider()
            for _ in range(3):
                opened, sent = len(bus.opens), len(bus.requests)
                status, = provider.poll()
                self.assertEqual((status.level, status.charging, status.online), (71, True, True))
                self.assertEqual(bus.opens[opened:], [b"a"])
                self.assertEqual([r[7] for r in bus.requests[sent:]], [0x80, 0x84])
            modes[b"a"] = "foreign_reply"
            for _ in range(2):
                opened = len(bus.opens)
                self.assertFalse(provider.poll()[0].online)
                self.assertEqual(bus.opens[opened:], [b"a"])
                self.assertFalse(provider._rediscovery)
            modes[b"a"] = "ok_charging"
            opened, sent = len(bus.opens), len(bus.requests)
            self.assertTrue(provider.poll()[0].online)
            self.assertEqual(bus.opens[opened:], [b"a"])
            self.assertEqual(len(bus.requests) - sent, 2)
            self.assertEqual((provider._foreign, provider._rediscovery, provider._failing), ({}, {}, {}))

    def test_single_interface_recovers_after_bounded_walk(self):
        infos = self.inventory((b"a",))
        modes = {b"a": "foreign_reply"}
        with fixture(infos, modes) as (bus, _):
            provider = R.RazerProvider()
            for _ in range(4):
                self.assertEqual(provider.poll(), [])
            modes[b"a"] = {0x3F: "ok"}
            result = []
            for _ in range(4):
                opened = len(bus.opens)
                result = provider.poll()
                self.assertGreater(len(bus.opens) - opened, 0)
                self.assertLessEqual(len(bus.opens) - opened, 2)
                if result:
                    break
            self.assertTrue(result[0].online)
            self.assertEqual(provider._cache[(0x007D, "fake")].tid, 0x3F)

    def test_profile_only_retries_single_candidate_and_reaches_other_interface(self):
        for paths in ((b"a",), (b"a", b"b")):
            infos = [dict(d, product_id=0xFE01) for d in self.inventory(paths)]
            modes = {path: "foreign_reply" for path in paths}
            with self.subTest(paths=paths), fixture(infos, modes) as (bus, _):
                provider = R.RazerProvider(self.pack())
                for _ in range(10):
                    opened = len(bus.opens)
                    self.assertEqual(provider.poll(), [])
                    self.assertGreater(len(bus.opens) - opened, 0)
                    self.assertLessEqual(len(bus.opens) - opened, 3)
                modes[paths[-1]] = "ok"
                result = []
                for _ in range(2):
                    result = provider.poll()
                    if result:
                        break
                self.assertTrue(result[0].online)
                self.assertEqual(result[0].model_source, "pack")
                self.assertEqual({r[1] for r in bus.requests}, {0x1F})
                self.assertEqual((provider._foreign, provider._rediscovery), ({}, {}))

    def test_foreign_never_refreshes_history_or_queries_charging(self):
        infos = self.inventory()
        modes = {b"a": "ok_charging", b"b": "foreign_reply"}
        with fixture(infos, modes) as (bus, clock):
            provider = R.RazerProvider()
            provider.poll()
            last = dict(provider._last)
            modes[b"a"] = "foreign_reply"
            sent = len(bus.requests)
            for _ in range(10):
                clock.sleep(5)
                status, = provider.poll()
                self.assertEqual((status.level, status.charging, status.online), (71, True, False))
                self.assertEqual(provider._last, last)
            clock.sleep(301)
            self.assertEqual(provider.poll(), [])
            self.assertEqual(provider._last, last)
            self.assertEqual({r[7] for r in bus.requests[sent:]}, {0x80})

    def test_timeout_resets_foreign_streak_without_scanning(self):
        infos = self.inventory()
        modes = {b"a": "ok", b"b": "ok"}
        with fixture(infos, modes) as (bus, _):
            provider = R.RazerProvider()
            provider.poll()
            last = dict(provider._last)
            for mode in ("foreign_reply", "foreign_reply", 4, 4, "foreign_reply", "foreign_reply"):
                modes[b"a"] = mode
                opened = len(bus.opens)
                self.assertFalse(provider.poll()[0].online)
                self.assertEqual(bus.opens[opened:], [b"a"])
                self.assertEqual(provider._last, last)
                self.assertFalse(provider._rediscovery)
            self.assertEqual(provider.failure_code(), "razer_foreign_reply")

    def test_busy_alone_preserves_legacy_walk_without_foreign_recovery(self):
        with fixture(self.inventory((b"a",)), {b"a": 1}) as (bus, clock):
            provider = R.RazerProvider()
            before = clock.time()
            self.assertEqual(provider.poll(), [])
            self.assertEqual(len(bus.requests), len(R.TRANSACTION_IDS))
            self.assertLess(clock.time() - before, 7)
            self.assertEqual(provider.failure_code(), "razer_busy_timeout")
            self.assertEqual((provider._foreign, provider._rediscovery), ({}, {}))

    def test_timeout_during_recovery_returns_to_sleeping_cache(self):
        infos = self.inventory()
        modes = {b"a": "foreign_reply", b"b": "foreign_reply"}
        with fixture(infos, modes) as (bus, _):
            provider = R.RazerProvider()
            for _ in range(3):
                provider.poll()
            modes.update({b"a": 4, b"b": 5})
            opened = len(bus.opens)
            self.assertEqual(provider.poll(), [])
            self.assertLessEqual(len(bus.opens) - opened, 2)
            self.assertEqual((provider._foreign, provider._rediscovery), ({}, {}))
            for _ in range(3):
                opened, sent = len(bus.opens), len(bus.requests)
                self.assertEqual(provider.poll(), [])
                self.assertEqual(bus.opens[opened:], [b"a"])
                self.assertEqual(len(bus.requests) - sent, 1)
                self.assertEqual(provider.failure_code(), "razer_device_timeout_04")

    def test_recovery_handles_busy_short_replies_and_io_failures(self):
        for mode in (1, "open_failed", "send_failed", "get_failed", "short_reply"):
            infos = self.inventory()
            modes = {b"a": "foreign_reply", b"b": "foreign_reply"}
            with self.subTest(mode=mode), fixture(infos, modes) as (bus, clock):
                provider = R.RazerProvider()
                for _ in range(3):
                    self.assertEqual(provider.poll(), [])
                modes.update({b"a": mode, b"b": "ok"})
                result = []
                for _ in range(4):
                    opened, before = len(bus.opens), clock.time()
                    result = provider.poll()
                    self.assertLessEqual(len(bus.opens) - opened, 2)
                    self.assertLess(clock.time() - before, 7)
                    if result:
                        break
                self.assertEqual((result[0].level, result[0].online), (71, True))
                self.assertEqual(provider.failure_code(), "")

    def test_recovery_respects_cooldown_and_preserves_unrelated_dead_entries(self):
        infos = self.inventory()
        modes = {b"a": "foreign_reply", b"b": "foreign_reply"}
        with fixture(infos, modes) as (bus, clock):
            provider = R.RazerProvider()
            for _ in range(3):
                provider.poll()
            deadline = clock.time() + 300
            provider._dead.update({b"a": deadline, b"b": deadline, b"unrelated": deadline + 1000})
            opened = len(bus.opens)
            self.assertEqual(provider.poll(), [])
            self.assertEqual(len(bus.opens), opened)
            self.assertEqual(provider.failure_code(), "razer_interfaces_cooling_down")
            clock.sleep(301)
            modes[b"b"] = "ok"
            result = []
            for _ in range(4):
                result = provider.poll()
                if result:
                    break
            self.assertTrue(result[0].online)
            self.assertEqual(provider._dead[b"unrelated"], deadline + 1000)

    def test_unresponsive_interface_enters_cooldown_during_recovery(self):
        infos = self.inventory()
        modes = {b"a": "foreign_reply", b"b": "foreign_reply"}
        with fixture(infos, modes) as (bus, clock):
            provider = R.RazerProvider()
            for _ in range(3):
                provider.poll()
            modes[b"a"] = "open_failed"
            for _ in range(6):
                opened = len(bus.opens)
                self.assertEqual(provider.poll(), [])
                self.assertLessEqual(len(bus.opens) - opened, 2)
                if b"a" in provider._dead:
                    break
            self.assertGreater(provider._dead[b"a"], clock.time())
            opened = len(bus.opens)
            modes[b"a"] = "ok"
            for _ in range(6):
                self.assertEqual(provider.poll(), [])
            self.assertNotIn(b"a", bus.opens[opened:])
            clock.sleep(301)
            result = []
            for _ in range(6):
                result = provider.poll()
                if result:
                    break
            self.assertTrue(result[0].online)

    def test_foreign_streaks_are_independent_for_device_groups(self):
        infos = self.inventory()
        infos[1]["serial_number"] = "second"
        modes = {b"a": "foreign_reply", b"b": "foreign_reply"}
        with fixture(infos, modes) as (bus, _):
            provider = R.RazerProvider()
            provider.poll()
            modes[b"a"] = "ok"
            for _ in range(2):
                opened = len(bus.opens)
                status, = provider.poll()
                self.assertTrue(status.online)
                self.assertEqual(status.key, "razer:007d:fake")
                self.assertEqual(bus.opens[opened:].count(b"a"), 1)
                self.assertLessEqual(bus.opens[opened:].count(b"b"), 3)
            self.assertNotIn((0x007D, "fake"), provider._foreign)
            self.assertIn((0x007D, "second"), provider._rediscovery)
            self.assertNotIn("razer:007d:second", provider._last)

    def test_inventory_removal_and_replacement_do_not_open_stale_paths(self):
        for polls in (1, 3):
            infos = self.inventory()
            modes = {b"a": "foreign_reply", b"b": "foreign_reply", b"new": "ok"}
            with self.subTest(polls=polls), fixture(infos, modes) as (bus, _):
                provider = R.RazerProvider()
                for _ in range(polls):
                    provider.poll()
                opened = len(bus.opens)
                infos[:] = self.inventory((b"new",))
                self.assertTrue(provider.poll()[0].online)
                self.assertEqual(bus.opens[opened:], [b"new"])
                modes[b"new"] = "foreign_reply"
                for _ in range(3):
                    provider.poll()
                infos.clear()
                opened = len(bus.opens)
                self.assertEqual(provider.poll(), [])
                self.assertEqual(len(bus.opens), opened)
                self.assertEqual((provider._foreign, provider._rediscovery), ({}, {}))

    def test_reordered_inventory_uses_candidate_identity(self):
        infos = self.inventory()
        modes = {b"a": "foreign_reply", b"b": "foreign_reply"}
        with fixture(infos, modes) as (bus, _):
            provider = R.RazerProvider()
            for _ in range(3):
                provider.poll()
            # Move a behind b without removing the cursor's path/TID.
            infos[0]["interface_number"] = 3
            modes[b"b"] = "ok"
            result = []
            for _ in range(4):
                opened = len(bus.opens)
                result = provider.poll()
                self.assertLessEqual(len(bus.opens) - opened, 2)
                if result:
                    break
            self.assertTrue(result[0].online)
            self.assertEqual(provider._cache[(0x007D, "fake")].path, b"b")

    def test_pack_reconfiguration_clears_cursor_and_enforces_new_match_and_tid(self):
        infos = [dict(d, product_id=0xFE01) for d in self.inventory()]
        match = dict(vendorId="1532", productId="FE01", interfaceNumber=0)
        modes = {b"a": "foreign_reply", b"b": "ok"}
        with fixture(infos, modes) as (bus, _):
            provider = R.RazerProvider(self.pack(match=match))
            for _ in range(3):
                self.assertEqual(provider.poll(), [])
            self.assertTrue(provider._rediscovery)
            self.assertEqual(set(bus.opens), {b"a"})
            provider.configure_pack(self.pack("hyperspeed-mouse-3f", dict(match, interfaceNumber=1)))
            for state in (provider._cache, provider._foreign, provider._rediscovery, provider._dead,
                          provider._last, provider._failing):
                self.assertEqual(state, {})
            opened, sent = len(bus.opens), len(bus.requests)
            self.assertTrue(provider.poll()[0].online)
            self.assertEqual(bus.opens[opened:], [b"b"])
            self.assertEqual({r[1] for r in bus.requests[sent:]}, {0x3F})
            from test_device_pack import disabled
            provider.configure_pack(self.pack(denies=[disabled("FE01")]))
            opened = len(bus.opens)
            for _ in range(4):
                self.assertEqual(provider.poll(), [])
            self.assertEqual(len(bus.opens), opened)
            self.assertEqual((provider._foreign, provider._rediscovery), ({}, {}))

    def test_reader_child_keeps_recovery_isolated_by_physical_parent(self):
        infos = self.inventory((b"private-path-a", b"private-path-b"))
        for d, parent in zip(infos, ("private-parent-a", "private-parent-b")):
            d.update(physical_id=parent, serial_number="private-serial")
        commands = [dict(action="query", sequence=i, provider="razer", inventory=infos,
                         physicalId=infos[i % 2]["physical_id"]) for i in range(10)]
        data = "".join(json.dumps(c, default=wire_encode) + "\n" for c in commands).encode()
        instances = []

        def create(device_pack=None):
            provider = R.RazerProvider(device_pack)
            instances.append(provider)
            return provider

        with fixture(infos, {infos[0]["path"]: "foreign_reply", infos[1]["path"]: "ok"}) as (bus, _), \
                patch.object(server, "provider_class", return_value=create), \
                patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
            server.reader_child()
        responses = [json.loads(line) for line in output.getvalue().splitlines()]
        self.assertEqual(len(responses), 10)
        self.assertEqual(len(instances), 2)
        self.assertTrue(instances[0]._rediscovery)
        self.assertEqual((instances[1]._foreign, instances[1]._rediscovery), ({}, {}))
        for i, result in enumerate(responses):
            self.assertEqual(result["sequence"], i)
            if i % 2 == 0:
                self.assertEqual(result["statuses"], [])
                self.assertEqual(result["failureCode"], "razer_foreign_reply")
                self.assertNotIn("private-", json.dumps(result))
            else:
                self.assertEqual(result["failureCode"], "")
                self.assertEqual(result["statuses"][0]["level"], 71)
        self.assertEqual(instances[0]._last, {})
        self.assertEqual(len([p for p in bus.opens if p == infos[1]["path"]]), 5)


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
