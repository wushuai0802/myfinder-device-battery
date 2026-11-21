"""Architecture v2 families: spec validation, interpreter, read memory, provider gates.

Offline only: a fake hidapi device records every byte that would be sent.
"""
import copy
import json
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
import families
from families import memory as mem, runner, spec
from providers import families as provider_module

XIBERIA = json.loads((Path(families.__file__).with_name("builtin") / "xiberia.json").read_text(encoding="utf-8"))


class FakeDevice:
    def __init__(self, replies=(), fail=False):
        self.replies = list(replies)
        self.fail = fail
        self.writes, self.opened, self.closed = [], [], 0
        self.feature_sets, self.feature_gets = [], []

    def open_path(self, path):
        self.opened.append(path)

    def write(self, data):
        if self.fail:
            raise OSError("device gone")
        self.writes.append(bytes(data))
        return len(data)

    def read(self, length, timeout):
        return self.replies.pop(0) if self.replies else []

    def send_feature_report(self, data):
        self.feature_sets.append(bytes(data))
        return len(data)

    def get_feature_report(self, report_id, length):
        self.feature_gets.append((report_id, length))
        return self.replies.pop(0) if self.replies else []

    def close(self):
        self.closed += 1


def frame(hex_data, length=64):
    return list(bytes.fromhex(hex_data).ljust(length, b"\0"))


def collection(**changes):
    row = dict(vendor_id=0x36F9, product_id=0xF016, product_string="XIBERIA K03S",
               usage_page=0xFF90, usage=0x0001, path=b"xiberia-ff90",
               physical_id=r"pnp:usb\vid_36f9&pid_f016\receiver",
               input_report_length=64, output_report_length=64,
               input_report_sizes={"85": 64}, output_report_sizes={"85": 64})
    row.update(changes)
    return row


def poll(rows, device, provider=None):
    provider = provider or provider_module.FamiliesProvider(running=lambda names: False)
    with patch.object(provider_module.hidlist, "enumerate", side_effect=lambda vid=0: [r for r in rows if r["vendor_id"] == vid]), \
            patch.object(provider_module.hid, "device", return_value=device):
        return provider.poll(), provider


class SpecTests(unittest.TestCase):
    def test_builtin_families_load_and_claim_unique_devices(self):
        loaded = families.load()
        self.assertIn("xiberia", loaded)
        self.assertEqual(loaded["xiberia"].vendor_ids, (0x36F9,))
        self.assertFalse(loaded["xiberia"].probe)

    def reject(self, mutate):
        document = copy.deepcopy(XIBERIA)
        mutate(document)
        with self.assertRaises(spec.SpecError):
            spec.parse_family(document)

    def test_rejects_unsafe_or_malformed_specs(self):
        self.reject(lambda d: d.update(extra=1))
        self.reject(lambda d: d["reads"][0].update(transport="write"))
        self.reject(lambda d: d["reads"][0].update(request="66 65 00 00"))  # not the output report ID
        self.reject(lambda d: d["reads"][0].update(request=" ".join(["55"] * 65)))  # longer than the report
        self.reject(lambda d: d["reads"][0].update(before=["55 00"]))  # no session frames exist
        self.reject(lambda d: d["collections"].append({"usagePage": "0001", "usage": "0006"}))  # keyboard page
        self.reject(lambda d: d["source"].pop("license"))
        self.reject(lambda d: d.pop("devices"))
        self.reject(lambda d: d["devices"].append(dict(d["devices"][0])))
        self.reject(lambda d: d["reads"][0]["reply"]["percent"].update(max=101))
        self.reject(lambda d: d.update(standAsideFor=["C:\\hub.exe"]))

    def test_listen_reads_send_nothing_by_definition(self):
        document = copy.deepcopy(XIBERIA)
        document["reads"] = [{"name": "passive", "transport": "listen", "reply": XIBERIA["reads"][0]["reply"]}]
        family = spec.parse_family(document)
        device = FakeDevice([frame("55 65 00 3c")])
        outcome = runner.run(device, family, family.reads[0])
        self.assertEqual((outcome.kind, outcome.level), ("reading", 60))
        self.assertEqual(device.writes, [])
        document["reads"][0]["request"] = "55"
        with self.assertRaises(spec.SpecError):
            spec.parse_family(document)


class RunnerTests(unittest.TestCase):
    family = families.load()["xiberia"]

    def test_xiberia_request_and_reply(self):
        device = FakeDevice([frame("55 65 00 00"), frame("55 65 00 4b")])  # echo first, then 75%
        outcome = runner.run(device, self.family, self.family.reads[0])
        self.assertEqual((outcome.kind, outcome.level, outcome.charging_known), ("reading", 75, False))
        self.assertEqual(device.writes, [bytes.fromhex("55 65 00 00").ljust(64, b"\0")])

    def test_rejects_other_answers_and_out_of_range(self):
        for raw in (frame("55 66 00 4b"), frame("55 65 01 4b"), frame("55 65 00 65"), frame("55 65 00 00"), frame("55 65 00 4b", 63)):
            with self.subTest(raw=raw[:4]):
                outcome = runner.run(FakeDevice([raw]), self.family, self.family.reads[0])
                self.assertNotEqual(outcome.kind, "reading")

    def test_io_error_is_an_anomaly(self):
        outcome = runner.run(FakeDevice(fail=True), self.family, self.family.reads[0])
        self.assertEqual(outcome.kind, "anomaly")


class MemoryTests(unittest.TestCase):
    def test_trial_rounds_then_remembered_read(self):
        m = mem.ReadMemory()
        for _ in range(mem.MAX_TRIAL_ROUNDS):
            self.assertEqual(m.plan("f", "d", ["a", "b"]), ["a", "b"])
            m.record("f", "d", "a", "silent")
            m.record("f", "d", "b", "silent")
        self.assertEqual(m.plan("f", "d", ["a", "b"]), [])  # trial exhausted: nothing sent
        m2 = mem.ReadMemory()
        m2.plan("f", "d", ["a", "b"])
        m2.record("f", "d", "b", "reading")
        self.assertEqual(m2.plan("f", "d", ["a", "b"]), ["b"])

    def test_anomalies_retire_reads_and_then_the_device(self):
        m = mem.ReadMemory()
        m.plan("f", "d", ["a", "b"])
        m.record("f", "d", "a", "anomaly")
        self.assertEqual(m.plan("f", "d", ["a", "b"]), ["b"])
        m.record("f", "d", "b", "anomaly")
        self.assertEqual(m.plan("f", "d", ["a", "b"]), [])

    def test_user_disable_and_snapshot_roundtrip(self):
        m = mem.ReadMemory()
        m.plan("f", "d", ["a"])
        m.record("f", "d", "a", "reading")
        m.disable("f", "x")
        restored = mem.ReadMemory()
        restored.restore(m.snapshot())
        self.assertEqual(restored.plan("f", "d", ["a"]), ["a"])
        self.assertEqual(restored.plan("f", "x", ["a"]), [])


class ProviderTests(unittest.TestCase):
    def test_reads_one_proven_collection(self):
        device = FakeDevice([frame("55 65 00 3c")])
        statuses, provider = poll([collection(), collection(usage_page=0xFF82, path=b"ff82", input_report_sizes={"1": 64},
                                                           output_report_sizes={"1": 64})], device)
        status, = statuses
        self.assertEqual((status.name, status.level, status.source, status.kind, status.verification_level),
                         ("XIBERIA K03S", 60, "families", "headset", "C"))
        self.assertEqual(status.model_source, "family:xiberia")
        self.assertEqual(device.opened, [b"xiberia-ff90"])
        self.assertEqual(device.closed, 1)
        self.assertEqual(provider.candidate_count, 1)

    def assert_nothing_sent(self, rows):
        device = FakeDevice([frame("55 65 00 3c")])
        statuses, _ = poll(rows, device)
        self.assertEqual(statuses, [])
        self.assertEqual((device.opened, device.writes), ([], []))

    def test_zero_send_gates(self):
        cases = {
            "unlisted pid": [collection(product_id=0xC001)],
            "wrong product string": [collection(product_string="XIBERIA K03S Pro")],
            "unproven reports": [collection(input_report_sizes=None)],
            "wrong report id": [collection(input_report_sizes={"1": 64}, output_report_sizes={"1": 64})],
            "wrong length": [collection(input_report_length=65, input_report_sizes={"85": 65})],
            "keyboard page": [collection(usage_page=0x0001, usage=0x0006)],
            "two proven collections": [collection(), collection(usage_page=0xFF82, path=b"ff82")],
            "unresolved parent": [collection(physical_id="collection:abc")],
            "other vendor": [collection(vendor_id=0x10D6, product_id=0x4801)],
        }
        for name, rows in cases.items():
            with self.subTest(name):
                self.assert_nothing_sent(rows)

    def test_listed_device_without_product_string_rule(self):
        device = FakeDevice([frame("55 65 00 14")])
        statuses, _ = poll([collection(product_id=0xF008, product_string="anything")], device)
        self.assertEqual(statuses[0].level, 20)

    def test_stand_aside_for_vendor_software(self):
        device = FakeDevice([frame("55 65 00 3c")])
        provider = provider_module.FamiliesProvider(running=lambda names: "xiberia hub.exe" in names)
        statuses, _ = poll([collection()], device, provider)
        self.assertEqual((statuses, device.opened), ([], []))

    def test_device_retired_after_repeated_anomalies(self):
        provider = provider_module.FamiliesProvider(running=lambda names: False)
        for _ in range(3):
            poll([collection()], FakeDevice(fail=True), provider)
        device = FakeDevice([frame("55 65 00 3c")])
        statuses, _ = poll([collection()], device, provider)
        self.assertEqual((statuses, device.writes), ([], []))

    def test_probe_families_are_inert_unless_enabled(self):
        document = copy.deepcopy(XIBERIA)
        document.update(id="probe_test", devices=[])
        document["probeVendorIds"] = ["36F9"]
        probe = spec.parse_family(document)
        registry = {"probe_test": probe}
        with patch.object(families, "_families", registry):
            families.set_probe_enabled(False)
            try:
                self.assertFalse(families.wants_caps(0x36F9, 0xC001, 0xFF90, 0x0001))
                self.assert_nothing_sent([collection(product_id=0xC001, product_string="")])
                families.set_probe_enabled(True)
                self.assertTrue(families.wants_caps(0x36F9, 0xC001, 0xFF90, 0x0001))
                statuses, _ = poll([collection(product_id=0xC001, product_string="")], FakeDevice([frame("55 65 00 3c")]))
                self.assertEqual(statuses[0].level, 60)
            finally:
                families.set_probe_enabled(False)


class IntegrationTests(unittest.TestCase):
    def test_server_registration_and_vendor_prefilter(self):
        import server
        self.assertIn(("families", "FamiliesProvider"), server.PROVIDERS)
        self.assertEqual(server.PROVIDER_VIDS["families"], families.vendor_ids())

    def test_topology_asks_for_report_sizes_only_on_family_collections(self):
        self.assertTrue(families.wants_caps(0x36F9, 0xF016, 0xFF90, 0x0001))
        self.assertFalse(families.wants_caps(0x36F9, 0xF016, 0x0001, 0x0006))
        self.assertFalse(families.wants_caps(0x36F9, 0xC001, 0xFF90, 0x0001))

    def test_family_devices_do_not_overlap_other_readers(self):
        from known_ids import build_index
        index = build_index()
        others = {(d["vendorId"], d["productId"]) for d in index["devices"] if d["provider"] != "families"}
        ours = {(d["vendorId"], d["productId"]) for d in index["devices"] if d["provider"] == "families"}
        self.assertTrue(ours)
        self.assertFalse(ours & others)


def sum55(head, length=17):
    frame = list(bytes.fromhex(head).ljust(length - 1, b"\0"))
    return frame + [(0x55 - sum(frame)) & 0xFF]


class BrimDeckPortTests(unittest.TestCase):
    loaded = families.load()

    def lofree_row(self, **changes):
        row = dict(vendor_id=0x05AC, product_id=0x024F, product_string="2.4G Wireless Receiver",
                   interface_number=1, usage_page=0xFF02, usage=0x0002, path=b"lofree",
                   physical_id=r"pnp:usb\vid_05ac&pid_024f\rx",
                   input_report_length=17, output_report_length=17,
                   input_report_sizes={"8": 17}, output_report_sizes={"8": 17})
        row.update(changes)
        return row

    def test_lofree_gate_then_battery_with_checksums(self):
        family = self.loaded["lofree_flow"]
        online = sum55("08 03 00 00 00 00 01")
        battery = sum55("08 04 00 00 00 00 4b 01")
        device = FakeDevice([sum55("08 03 00 00 00 80"), online, battery])  # echo, online, reply
        outcome = runner.run(device, family, family.reads[0])
        self.assertEqual((outcome.kind, outcome.level, outcome.charging), ("reading", 75, True))
        self.assertEqual([w[1] for w in device.writes], [0x03, 0x04])
        self.assertTrue(all(sum(w) & 0xFF == 0x55 for w in device.writes))

    def test_lofree_offline_gate_sends_no_battery_query(self):
        family = self.loaded["lofree_flow"]
        device = FakeDevice([sum55("08 03 00 00 00 00 00")])
        self.assertEqual(runner.run(device, family, family.reads[0]).kind, "asleep")
        self.assertEqual(len(device.writes), 1)
        bad = sum55("08 04 00 00 00 00 4b 01")
        bad[16] ^= 1
        device = FakeDevice([sum55("08 03 00 00 00 00 01"), bad])
        self.assertNotEqual(runner.run(device, family, family.reads[0]).kind, "reading")

    def test_lofree_needs_exact_name_and_interface(self):
        for changes in ({"product_string": "INFI75-1.1"}, {"interface_number": 2}, {"usage_page": 0xFF60, "usage": 0x61}):
            with self.subTest(changes=changes):
                device = FakeDevice()
                statuses, _ = poll([self.lofree_row(**changes)], device)
                self.assertEqual((statuses, device.writes), ([], []))

    def test_eweadn_beiyin_feature_read(self):
        family = self.loaded["eweadn_beiyin"]
        request = family.reads[0].request
        self.assertEqual(request[1], sum(bytes.fromhex("50 00 02 4F 81")) & 0xFF)
        reply = list(bytes.fromhex("03 50 00 0b 02 01 c9").ljust(64, b"\0"))
        device = FakeDevice([reply])
        outcome = runner.run(device, family, family.reads[0])
        self.assertEqual((outcome.kind, outcome.level, outcome.charging), ("reading", 73, True))
        self.assertEqual(device.feature_gets, [(0x03, 64)])
        asleep = list(bytes.fromhex("03 50 00 0b 02 01 00").ljust(64, b"\0"))
        self.assertEqual(runner.run(FakeDevice([asleep]), family, family.reads[0]).kind, "asleep")

    def rongyuan(self, family_id, data):
        family = self.loaded[family_id]
        return runner.run(FakeDevice([list(bytes.fromhex(data).ljust(65, b"\0"))]), family, family.reads[0])

    def test_rongyuan_keyboard_and_mouse_fields(self):
        #            id rl kb ms ko mo rx pa .. upd
        self.assertEqual(self.rongyuan("rongyuan", "00 01 36 00 00 01 01 01 00 00").level, 54)
        self.assertEqual(self.rongyuan("rongyuan", "00 01 36 20 00 00 01 03 00 00").level, 54)
        self.assertEqual(self.rongyuan("rongyuan", "00 01 00 52 01 00 01 02 00 00").level, 82)
        self.assertEqual(self.rongyuan("rongyuan", "00 01 36 00 01 01 01 01 00 00").kind, "asleep")
        self.assertEqual(self.rongyuan("rongyuan", "00 01 36 00 00 01 01 01 00 01").kind, "rejected")  # radio updating
        self.assertEqual(self.rongyuan("rongyuan", "f7 00 00 00 00 00 00 00 00 00").kind, "rejected")  # request echoed
        self.assertEqual(self.rongyuan("rongyuan", "00" * 10).kind, "rejected")                       # just plugged in
        self.assertEqual(self.rongyuan("rongyuan_mouse", "00 01 36 2c 00 00 01 03 00 00").level, 44)
        self.assertEqual(self.rongyuan("rongyuan_mouse", "00 01 36 2c 00 00 01 01 00 00").kind, "rejected")

    def test_rongyuan_requests(self):
        self.assertEqual(self.loaded["rongyuan"].reads[0].request, bytes([0, 0xF7]))
        self.assertEqual(self.loaded["rongyuan_checksum"].reads[0].request, bytes([0, 0xF7, 0, 0, 0, 0, 0, 0, 0x08]))

    def test_product_name_preferred_on_shared_receivers(self):
        row = dict(vendor_id=0x3151, product_id=0x5038, product_string="Skyloong GK75", usage_page=0xFFFF, usage=0x0002,
                   path=b"ry", physical_id=r"pnp:usb\vid_3151&pid_5038\rx", feature_report_length=65,
                   feature_report_sizes={"0": 65})
        statuses, _ = poll([row], FakeDevice([list(bytes.fromhex("00 01 36 00 00 01 01 01 00 00").ljust(65, b"\0"))]))
        self.assertEqual((statuses[0].name, statuses[0].level), ("Skyloong GK75", 54))

    def test_spec_rejects_unsafe_gates_and_layouts(self):
        document = json.loads((Path(families.__file__).with_name("builtin") / "lofree_flow.json").read_text(encoding="utf-8"))
        bad = copy.deepcopy(document)
        bad["reads"][0]["gate"]["request"] = "09 03"
        with self.assertRaises(spec.SpecError):
            spec.parse_family(bad)
        bad = copy.deepcopy(document)
        bad["reads"][0]["reply"]["variants"] = [{"percent": {"at": 6, "min": 1, "max": 100}}]
        with self.assertRaises(spec.SpecError):
            spec.parse_family(bad)  # percent both top-level and in variants


class HostControlTests(unittest.TestCase):
    """C1: probe switch and read memory owned by the engine manager."""

    def engine(self, **kwargs):
        import server
        from test_engine_reliability import FakePool
        engine = server.BatteryEngine(process_factory=FakePool().factory, **kwargs)
        self.addCleanup(engine.close)
        return engine

    def setUp(self):
        sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tests"))
        families.set_probe_enabled(False)
        self.addCleanup(families.set_probe_enabled, False)

    def test_memory_key_hides_device_path(self):
        key = families.memory_key(r"pnp:usb\vid_36f9&pid_f016\receiver")
        self.assertRegex(key, r"^unit-[0-9a-f]{24}$")
        self.assertEqual(key, families.memory_key(r"pnp:usb\vid_36f9&pid_f016\receiver"))
        provider = provider_module.FamiliesProvider(running=lambda names: False)
        poll([collection()], FakeDevice([frame("55 65 00 3c")]), provider)
        self.assertEqual(list(provider.memory.snapshot()), ["xiberia|" + key])

    def test_probe_switch_defaults_on_and_rides_every_child_command(self):
        on, off = self.engine(), self.engine(probe_families=False)
        self.assertIs(on._reader_command({"action": "topology"})["probeFamilies"], True)
        self.assertIs(off._reader_command({"action": "query", "provider": "families"})["probeFamilies"], False)
        self.assertNotIn("familyMemory", on._reader_command({"action": "query", "provider": "razer"}))
        self.assertEqual(on._reader_command({"action": "query", "provider": "families"})["familyMemory"], {})
        self.assertFalse(families.probe_enabled())  # the manager never flips the process switch

    def test_cli_flag(self):
        import io
        import server
        for argv, expected in (([], True), (["--probe-families", "off"], False), (["--probe-families", "on"], True)):
            with self.subTest(argv=argv), patch.object(server, "BatteryEngine") as engine, \
                    patch.object(server.sys, "argv", ["server.py", "--once"] + argv), \
                    patch.object(server.sys, "stdout", new_callable=io.StringIO):
                engine.return_value.poll.return_value = {}
                server.main()
                self.assertIs(engine.call_args.kwargs["probe_families"], expected)

    def test_child_applies_flag_restores_and_returns_memory(self):
        import io
        import types
        import server
        from isolation import wire_encode
        key = "xiberia|" + families.memory_key("fixture-physical")
        memory = {key: {"working": "battery", "rounds": 1, "retired": [], "silentPolls": 5,
                        "relearned": False, "disabled": False}}
        seen = []

        def fake_scoped(provider, inventory, physical, pack):
            seen.append((families.probe_enabled(), provider.memory.snapshot()[key]["silentPolls"]))
            return []
        commands = [{"action": "query", "provider": "families", "physicalId": "fixture-physical", "inventory": [],
                     "probeFamilies": flag, "familyMemory": memory, "sequence": i}
                    for i, flag in enumerate((True, False))]
        data = b"".join((json.dumps(c, default=wire_encode) + "\n").encode() for c in commands)
        output = io.StringIO()
        with patch.object(server, "scoped_poll", side_effect=fake_scoped), \
                patch.object(server.sys, "stdin", types.SimpleNamespace(buffer=io.BytesIO(data))), \
                patch.object(server.sys, "stdout", output):
            server.reader_child()
        responses = [json.loads(line) for line in output.getvalue().splitlines()]
        self.assertEqual(seen, [(True, 5), (False, 5)])
        self.assertEqual(responses[0]["familyMemory"][key]["working"], "battery")

    def test_restored_unknown_read_names_are_forgotten(self):
        memory = mem.ReadMemory()
        memory.restore({"xiberia|unit-x": {"working": "renamed", "retired": ["gone", "battery"], "disabled": "false"}})
        self.assertEqual(memory.plan("xiberia", "unit-x", ["battery", "other"]), ["other"])
        self.assertFalse(memory.snapshot()["xiberia|unit-x"]["disabled"])
        provider = provider_module.FamiliesProvider(running=lambda names: False)
        provider.memory.restore({"xiberia|" + families.memory_key(collection()["physical_id"]): {"working": "renamed"}})
        statuses, _ = poll([collection()], FakeDevice([frame("55 65 00 3c")]), provider)
        self.assertEqual(statuses[0].level, 60)

    def test_stale_relearn_survives_round_trips(self):
        # The manager hands memory to the child each poll; the silence counter must persist.
        provider = provider_module.FamiliesProvider(running=lambda names: False)
        poll([collection()], FakeDevice([frame("55 65 00 3c")]), provider)
        state = provider.memory.snapshot()
        for _ in range(mem.STALE_POLLS):
            fresh = provider_module.FamiliesProvider(running=lambda names: False)
            fresh.memory.restore(state)
            poll([collection()], FakeDevice([]), fresh)
            state = fresh.memory.snapshot()
        self.assertTrue(next(iter(state.values()))["relearned"])

    def test_family_memory_command(self):
        from server import public_id
        from providers.base import DeviceStatus
        engine = self.engine()
        physical = r"pnp:usb\vid_36f9&pid_f016\receiver"
        status = DeviceStatus(f"families:xiberia:{physical}", "XIBERIA K03S", 60, False, True, "families",
                              kind="headset", physical_id=physical, model_source="family:xiberia")
        engine._accept(("families", physical), "families", [status], "2026-11-21T00:00:00Z", physical)
        device_id = public_id(status.key)
        unit = "xiberia|" + families.memory_key(physical)

        self.assertEqual(engine.family_memory_command("get"),
                         {"ok": True, "familyMemory": {}, "probeFamilies": True})
        self.assertEqual(engine.family_memory_command("disable", "device-unknown")["error"], "unknown_device")
        self.assertEqual(engine.family_memory_command("bogus")["error"], "invalid_op")
        reply = engine.family_memory_command("disable", device_id)
        self.assertEqual(reply["memoryKey"], unit)
        self.assertTrue(reply["familyMemory"][unit]["disabled"])
        self.assertTrue(engine._reader_command({"action": "query", "provider": "families"})["familyMemory"][unit]["disabled"])

        # A child poll that started before the click must not undo it.
        stale = {unit: dict(reply["familyMemory"][unit], disabled=False)}
        engine._merge_family_memory(stale)
        self.assertTrue(engine.family_memory[unit]["disabled"])

        self.assertEqual(engine.family_memory_command("enable", memory_key="xiberia|raw-path")["error"], "unknown_device")
        self.assertFalse(engine.family_memory_command("enable", memory_key=unit)["familyMemory"][unit]["disabled"])

        # Restoring a persisted file: malformed entries and non-objects are dropped.
        restored = engine.family_memory_command("set", snapshot={
            unit: {"working": 7, "rounds": "x", "retired": "battery", "silentPolls": True, "disabled": True},
            "nobar": {}, "a|b": [], })
        self.assertEqual(restored["familyMemory"], {unit: {"working": None, "rounds": 0, "retired": [],
                                                            "silentPolls": 0, "relearned": False, "disabled": True}})
        many = {f"xiberia|unit-{i:024x}": {} for i in range(engine.FAMILY_MEMORY_LIMIT + 10)}
        self.assertEqual(len(engine.family_memory_command("set", snapshot=many)["familyMemory"]), engine.FAMILY_MEMORY_LIMIT)
        self.assertTrue(engine.family_memory_command("set", snapshot=["not", "a", "dict"])["ok"])
        self.assertEqual(engine.family_memory, {})

    def test_stdin_family_memory_command(self):
        import io
        import server
        lines = "\n".join(json.dumps(c) for c in (
            {"command": "family_memory", "op": "set", "requestId": 4,
             "familyMemory": {"xiberia|unit-" + "0" * 24: {"disabled": True}}},
            {"command": "family_memory", "op": "get", "requestId": 5})) + "\n"
        from test_engine_reliability import FakePool
        real = server.BatteryEngine
        made = []

        def build(**kw):
            made.append(real(process_factory=FakePool().factory, **kw))
            return made[-1]
        self.addCleanup(lambda: [e.close() for e in made])
        with patch.object(server, "BatteryEngine", side_effect=build), \
                patch.object(server.sys, "argv", ["server.py", "--probe-families", "off"]), \
                patch.object(server.sys, "stdin", io.StringIO(lines)), \
                patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
            server.main()
        replies = [json.loads(line) for line in output.getvalue().splitlines()]
        self.assertEqual({r.get("reply") for r in replies}, {"family_memory"})
        self.assertEqual([r["requestId"] for r in replies], [4, 5])
        self.assertEqual(replies[1]["probeFamilies"], False)
        self.assertTrue(replies[1]["familyMemory"]["xiberia|unit-" + "0" * 24]["disabled"])


if __name__ == "__main__":
    unittest.main()
