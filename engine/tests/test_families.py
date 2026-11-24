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
    def k06s_collection(self, **changes):
        row = collection(
            product_id=0xF002,
            product_string="XIBERIA K06S Wireless",
            physical_id=r"pnp:usb\vid_36f9&pid_f002\receiver",
            path=b"k06s-ff90",
        )
        row.update(changes)
        return row

    def test_k06s_queries_only_ff90(self):
        ff90 = self.k06s_collection()
        ff82 = self.k06s_collection(usage_page=0xFF82, path=b"k06s-ff82")
        for rows in ([ff90], [ff90, ff82], [ff82, ff90],
                     [dict(ff90, product_string="  xiberia k06s wireless  "), ff82]):
            with self.subTest(paths=[row["path"] for row in rows]):
                device = FakeDevice([frame("55 65 00 64")])
                statuses, provider = poll(rows, device)
                status, = statuses
                self.assertEqual(
                    (status.level, status.source, status.kind, status.verification_level),
                    (100, "families", "headset", "C"),
                )
                self.assertFalse(status.charging_known)
                self.assertEqual(status.model_source, "family:xiberia")
                self.assertEqual(device.opened, [b"k06s-ff90"])
                self.assertEqual(
                    device.writes,
                    [bytes.fromhex("55 65 00 00").ljust(64, b"\0")],
                )
                self.assertEqual(device.closed, 1)
                self.assertEqual(provider.candidate_count, 1)

    def test_k06s_zero_send_gates(self):
        ff90 = self.k06s_collection()
        ff82 = self.k06s_collection(usage_page=0xFF82, path=b"k06s-ff82")
        cases = {
            "ff82 only": [ff82],
            "normalized name still excludes ff82": [dict(ff82, product_string="  xiberia k06s wireless  ")],
            "wrong product": [
                self.k06s_collection(product_string="XIBERIA K06S"), ff82],
            "missing product": [
                self.k06s_collection(product_string=None), ff82],
            "wrong usage page": [
                self.k06s_collection(usage_page=0xFF91), ff82],
            "wrong usage": [self.k06s_collection(usage=2), ff82],
            "missing path": [self.k06s_collection(path=None), ff82],
            "two proven ff90": [
                ff90, self.k06s_collection(path=b"other"), ff82],
            "second ff90 unproved": [
                ff90, self.k06s_collection(
                    path=b"other", input_report_sizes=None), ff82],
            "second ff90 missing product": [
                ff90, self.k06s_collection(
                    path=b"other", product_string=None), ff82],
            "second ff90 missing path": [
                ff90, self.k06s_collection(path=None), ff82],
            "second ff90 missing product and path": [
                ff90, self.k06s_collection(
                    product_string=None, path=None), ff82],
        }
        for direction in ("input", "output"):
            for name, changes in (
                ("missing sizes", {direction + "_report_sizes": None}),
                ("missing maximum", {direction + "_report_length": None}),
                ("wrong id", {direction + "_report_sizes": {"1": 64}}),
                ("wrong per-id size", {direction + "_report_sizes": {"85": 63}}),
                ("wrong length", {
                    direction + "_report_length": 65,
                    direction + "_report_sizes": {"85": 65},
                }),
            ):
                cases[f"{direction}: {name}"] = [
                    self.k06s_collection(**changes), ff82]
        for name, rows in cases.items():
            for ordered in (rows, list(reversed(rows))):
                with self.subTest(name=name, paths=[r.get("path") for r in ordered]):
                    self.assert_nothing_sent(ordered)

    def test_k06s_saved_disabled_opens_nothing(self):
        ff90 = self.k06s_collection()
        ff82 = self.k06s_collection(usage_page=0xFF82, path=b"k06s-ff82")
        provider = provider_module.FamiliesProvider(running=lambda names: False)
        provider.memory.disable(
            "xiberia", provider_module.families.memory_key(ff90["physical_id"]))
        restored = provider_module.FamiliesProvider(running=lambda names: False)
        restored.memory.restore(provider.memory.snapshot())
        device = FakeDevice([frame("55 65 00 64")])
        statuses, _ = poll([ff90, ff82], device, restored)
        self.assertEqual(statuses, [])
        self.assertEqual((device.opened, device.writes), ([], []))

    def test_k06s_uniqueness_is_per_physical_device(self):
        first = self.k06s_collection()
        second = self.k06s_collection(
            path=b"second-ff90",
            physical_id=r"pnp:usb\vid_36f9&pid_f002\second",
        )
        device = FakeDevice([frame("55 65 00 64"), frame("55 65 00 64")])
        statuses, provider = poll([first, second], device)
        self.assertEqual(len(statuses), 2)
        self.assertCountEqual(device.opened, [first["path"], second["path"]])
        self.assertEqual(provider.candidate_count, 2)

    def test_f016_still_reads_ff82_alone(self):
        device = FakeDevice([frame("55 65 00 3c")])
        statuses, _ = poll(
            [collection(usage_page=0xFF82, path=b"f016-ff82")], device)
        status, = statuses
        self.assertEqual((status.level, status.verification_level), (60, "C"))
        self.assertEqual(device.opened, [b"f016-ff82"])

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
        self.assertTrue(families.wants_caps(0x36F9, 0xC001, 0xFF90, 0x0001))
        self.assertFalse(families.wants_caps(0x36F9, 0xF016, 0x0001, 0x0006))
        self.assertFalse(families.wants_caps(0x36F9, 0xC002, 0xFF90, 0x0001))

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


class PackFamilyTests(unittest.TestCase):
    """C2: loader-2 signed device packs may add families, never override them."""

    def document(self, families_rows, loader=2, entries=()):
        return json.dumps({"format": "myfinder-device-pack", "loader": loader, "packVersion": 2026112101,
                           "keyId": "test", "createdAt": "2026-11-21T00:00:00Z", "entries": list(entries),
                           "disabled": [], "families": families_rows})

    def family(self, family_id="pack_test", pid="F0F0", vid="36F9", **changes):
        row = copy.deepcopy(XIBERIA)
        row.update(id=family_id, devices=[{"vendorId": vid, "productId": pid, "name": "Pack Test"}])
        row.update(changes)
        return row

    def setUp(self):
        self.addCleanup(families.set_pack_families, ())

    def test_accepts_new_family_and_reports_it(self):
        from device_pack import parse_pack
        result = parse_pack(self.document([self.family()]))
        self.assertTrue(result.loaded, result.rejected)
        self.assertEqual([f.id for f in result.families], ["pack_test"])
        self.assertEqual(result.diagnostic()["families"], ["pack_test"])
        families.set_pack_families(result.families)
        self.assertIn("pack_test", families.families())
        self.assertIn((0x36F9, 0xF0F0), {(d.vendor_id, d.product_id) for d in families.families()["pack_test"].devices})
        families.set_pack_families(())
        self.assertNotIn("pack_test", families.families())

    def test_row_rejections_keep_the_rest(self):
        from device_pack import parse_pack
        bad_spec = self.family("bad_spec", "F0F1")
        bad_spec["reads"][0]["transport"] = "session"
        rows = [
            self.family("xiberia", "F0F2"),                       # builtin family id
            self.family("dup_a", "F0F3"), self.family("dup_a", "F0F4"),
            self.family("claims_builtin", "F016"),                # XIBERIA builtin device
            self.family("claims_reader", "1278", vid="373B"),     # pulsar reader device
            self.family("pid_a", "F0F5"), self.family("pid_b", "F0F5"),
            self.family("probe_claimed", "F0F6", probeVendorIds=["046D"]),
            bad_spec,
            self.family("good", "F0F7"),
        ]
        result = parse_pack(self.document(rows))
        self.assertTrue(result.loaded)
        self.assertEqual([f.id for f in result.families], ["good"])
        reasons = sorted(r.split(": ", 1)[1] for r in result.rejected)
        self.assertEqual(reasons, sorted(["builtin_family_conflict", "duplicate_family_id", "duplicate_family_id",
                                          "builtin_id_conflict", "builtin_id_conflict", "duplicate_id", "duplicate_id",
                                          "probe_vendor_claimed", "invalid_family"]))

    def test_family_may_repeat_its_own_pid_with_product_strings(self):
        from device_pack import parse_pack
        row = self.family()
        row["devices"] = [{"vendorId": "36F9", "productId": "F0F0", "name": "A", "productString": "A"},
                          {"vendorId": "36F9", "productId": "F0F0", "name": "B", "productString": "B"}]
        self.assertEqual(len(parse_pack(self.document([row])).families), 1)

    def test_whole_pack_errors(self):
        from device_pack import parse_pack
        cases = {"loader 1 with families": self.document([self.family()], loader=1),
                 "loader 3": self.document([], loader=3),
                 "family not an object": self.document(["xiberia"]),
                 "too many": self.document([self.family(f"f{i}", f"{0xE000 + i:04X}") for i in range(65)])}
        for name, document in cases.items():
            with self.subTest(name):
                self.assertFalse(parse_pack(document).loaded)
        legacy = json.loads(self.document([], loader=1))
        del legacy["families"]
        self.assertTrue(parse_pack(json.dumps(legacy)).loaded)

    def test_child_installs_pack_families_and_manager_prefilters_their_vendors(self):
        import io
        import types
        import server
        from device_pack import parse_pack
        from isolation import wire_encode
        result = parse_pack(self.document([self.family("pack_vendor", "0001", vid="ABCD")]))
        self.assertEqual([f.id for f in result.families], ["pack_vendor"])
        seen = []
        command = {"action": "query", "provider": "families", "physicalId": "p", "inventory": [],
                   "devicePack": result.document, "sequence": 0}
        data = (json.dumps(command, default=wire_encode) + "\n").encode()
        with patch.object(server, "scoped_poll", side_effect=lambda *a: seen.append(families.vendor_ids()) or []), \
                patch.object(server.sys, "stdin", types.SimpleNamespace(buffer=io.BytesIO(data))), \
                patch.object(server.sys, "stdout", io.StringIO()):
            server.reader_child()
        self.assertIn(0xABCD, seen[0])
        sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tests"))
        from test_engine_reliability import FakePool
        engine = server.BatteryEngine(process_factory=FakePool().factory, device_pack=result)
        self.addCleanup(engine.close)
        self.assertIn(0xABCD, engine._provider_vids("families"))
        self.assertIn(0x36F9, engine._provider_vids("families"))
        self.assertNotIn(0xABCD, server.PROVIDER_VIDS["families"])


def dpi_family(**dpi_changes):
    row = copy.deepcopy(XIBERIA)
    row.update(id="dpi_test", kind="mouse")
    dpi = {"exchanges": [{"name": "table", "transport": "output", "request": "55 70",
                          "reply": {"match": [{"at": 0, "eq": "55"}, {"at": 1, "eq": "70"}]}}],
           "stages": {"exchange": "table", "count": {"at": 2}, "first": 4, "stride": 2, "width": 2},
           "currentStage": {"exchange": "table", "at": 3}}
    dpi.update(dpi_changes)
    row["dpi"] = dpi
    return row


DPI_REPLY = "55 70 03 01 9001 2003 4006"   # 3 stages: 400, 800, 1600; current index 1


class DpiReadTests(unittest.TestCase):
    """C3: read-only DPI evidence from family data."""

    def test_spec_accepts_and_rejects(self):
        parsed = spec.parse_family(dpi_family()).dpi
        self.assertEqual((parsed.fixed_count, parsed.stride, parsed.stage_first.width, parsed.stage_source), (0, 2, 2, "onboard"))
        bad = {
            "not a mouse": dict(dpi_family(), kind="headset"),
            "listen transport": dpi_family(exchanges=[{"name": "table", "transport": "listen", "request": "",
                                                       "reply": {"match": [{"at": 0, "eq": "55"}]}}]),
            "request without report id": dpi_family(exchanges=[{"name": "table", "transport": "output", "request": "01 70",
                                                                "reply": {"match": [{"at": 0, "eq": "55"}]}}]),
            "both current fields": dpi_family(current={"exchange": "table", "at": 4, "width": 2}),
            "no current field": {k: v for k, v in dpi_family().items()},
            "unknown exchange": dpi_family(currentStage={"exchange": "other", "at": 3}),
            "write-ish field": dpi_family(set={"exchange": "table", "at": 3}),
            "too many stages": dpi_family(stages={"exchange": "table", "count": 9, "first": 4, "stride": 2, "width": 2}),
            "stride below width": dpi_family(stages={"exchange": "table", "count": 3, "first": 4, "stride": 1, "width": 2}),
            "four exchanges": dpi_family(exchanges=[{"name": f"e{i}", "transport": "output", "request": "55 70",
                                                     "reply": {"match": [{"at": 0, "eq": "55"}]}} for i in range(4)]),
        }
        del bad["no current field"]["dpi"]["currentStage"]
        for name, document in bad.items():
            with self.subTest(name), self.assertRaises(spec.SpecError):
                spec.parse_family(document)

    def run_dpi(self, reply, **changes):
        family = spec.parse_family(dpi_family(**changes))
        device = FakeDevice([frame(reply)])
        return runner.run_dpi(device, family), device

    def test_runner_decodes_stage_table(self):
        (value, note), device = self.run_dpi(DPI_REPLY)
        self.assertEqual((value, note), ({"current": 800, "stages": [400, 800, 1600], "stageSource": "onboard"}, ""))
        self.assertEqual(device.writes, [bytes.fromhex("5570").ljust(64, b"\0")])

    def test_runner_value_fields_and_fixed_count(self):
        row = dpi_family(stages={"exchange": "table", "count": 3, "first": 4, "stride": 1, "scale": 100},
                         current={"exchange": "table", "at": 7, "width": 2, "endian": "big"})
        del row["dpi"]["currentStage"]
        family = spec.parse_family(row)
        value, note = runner.run_dpi(FakeDevice([frame("55 70 00 00 08 10 20 0c 80")]), family)
        self.assertEqual(value, {"current": 3200, "stages": [800, 1600, 3200], "stageSource": "onboard"}, note)

    def test_runner_rejects_bad_evidence(self):
        cases = {"header": "55 71 03 01 9001 2003 4006", "count 0": "55 70 00 01 9001",
                 "count 9": "55 70 09 01 9001", "index out of range": "55 70 03 03 9001 2003 4006",
                 "stage below 50": "55 70 02 00 3000 2003"}
        for name, reply in cases.items():
            with self.subTest(name):
                (value, note), _ = self.run_dpi(reply)
                self.assertIsNone(value)
                self.assertTrue(note)
        (value, note), _ = self.run_dpi(DPI_REPLY, currentStage={"exchange": "table", "at": 3, "base": 1})
        self.assertEqual(value["current"], 400)

    def poll_dpi(self, replies, provider=None):
        family = spec.parse_family(dpi_family())
        with patch.object(families, "_families", {"dpi_test": family}):
            return poll([collection()], FakeDevice([frame(r) for r in replies]), provider)

    def test_provider_attaches_dpi_without_write_capability(self):
        statuses, _ = self.poll_dpi(["55 65 00 3c", DPI_REPLY])
        status, = statuses
        self.assertEqual(status.level, 60)
        self.assertEqual((status.dpi["current"], status.dpi["stages"], status.dpi["canSet"], status.dpi["targetToken"]),
                         (800, [400, 800, 1600], False, None))
        import server
        public = server.normalise_dpi(status.dpi, usable=True)
        self.assertEqual((public["current"], public["canSet"], public["stageSource"]), (800, False, "onboard"))

    def test_dpi_failure_keeps_battery_and_backs_off(self):
        provider = provider_module.FamiliesProvider(running=lambda names: False)
        for _ in range(3):
            statuses, _ = self.poll_dpi(["55 65 00 3c"], provider)
            self.assertEqual((statuses[0].level, statuses[0].dpi), (60, None))
        family = spec.parse_family(dpi_family())
        device = FakeDevice([frame("55 65 00 3c"), frame(DPI_REPLY)])
        with patch.object(families, "_families", {"dpi_test": family}):
            statuses, _ = poll([collection()], device, provider)
        self.assertIsNone(statuses[0].dpi)
        self.assertEqual(len(device.writes), 1)  # battery only; DPI no longer asked


class DpiAdapterTests(unittest.TestCase):
    """C4: reviewed adapters switch an existing stage; the framework owns every guard."""

    def setUp(self):
        import types
        from families import dpi as framework
        self.framework = framework
        self.family = spec.parse_family(dpi_family())
        self.adapter = types.ModuleType("families.dpi._fixture")
        self.adapter_writes = []

        def select_stage(dev, info, index):
            self.adapter_writes.append(index)
            dev.write(list(bytes([0x55, 0x71, index]).ljust(64, b"\0")))
        self.adapter.select_stage = select_stage
        for target, value in ((sys.modules, {"families.dpi._fixture": self.adapter}),):
            patcher = patch.dict(target, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        for obj, name, value in ((framework, "ADAPTERS", {"dpi_test": "_fixture"}),
                                 (families, "_builtin", {"dpi_test": self.family}),
                                 (families, "_families", {"dpi_test": self.family})):
            patcher = patch.object(obj, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)

    def capture(self, reply=DPI_REPLY):
        view, target, note = self.framework.read(FakeDevice([frame(reply)]), self.family, collection())
        return view, target

    def test_writable_only_with_reviewed_adapter_on_builtin_family(self):
        view, target = self.capture()
        self.assertTrue(view["canSet"])
        self.assertEqual(view["targetToken"], self.framework.token(target))
        self.assertEqual((target["family"], target["stages"], target["path"]), ("dpi_test", [400, 800, 1600], b"xiberia-ff90".hex()))
        self.assertNotIn("current", target)  # the token survives a stage change
        self.assertEqual(self.framework.vendor_ids(), (0x36F9,))
        with patch.object(families, "_builtin", {"dpi_test": spec.parse_family(dpi_family())}):
            self.assertIsNone(self.framework.adapter_for(self.family))   # e.g. a pack family of the same id
        with patch.object(self.framework, "ADAPTERS", {}):
            self.assertFalse(self.capture()[0]["canSet"])
        for name, reply in (("duplicate stages", "55 70 03 01 9001 9001 4006"),):
            with self.subTest(name):
                self.assertFalse(self.capture(reply)[0]["canSet"])
        quick = spec.parse_family(dpi_family(stageSource="quick"))
        with patch.object(families, "_builtin", {"dpi_test": quick}):
            view, target, _ = self.framework.read(FakeDevice([frame(DPI_REPLY)]), quick, collection())
            self.assertEqual((view["canSet"], target), (False, None))

    def select(self, replies, value=1600, expected=800, token=None, target=None):
        _, captured = self.capture()
        target = target or captured
        device = FakeDevice([frame(r) for r in replies])
        result = self.framework.select(device, self.family, collection(), target, value, expected,
                                       token or self.framework.token(target))
        return result, device

    def test_switch_with_readback(self):
        result, device = self.select([DPI_REPLY, "55 70 03 02 9001 2003 4006"])
        self.assertEqual((result["success"], result["confirmedDpi"], result["error"], result["outcomeUnknown"]),
                         (True, 1600, None, False))
        self.assertEqual(self.adapter_writes, [2])
        self.assertEqual([w[:3] for w in device.writes], [b"\x55\x70\x00", b"\x55\x71\x02", b"\x55\x70\x00"])
        self.assertTrue(result["dpi"]["canSet"])

    def test_refusals_send_no_switch(self):
        cases = {
            "stale token": dict(token="0" * 64),
            "not a stage": dict(value=1200),
            "current changed": dict(replies=["55 70 03 00 9001 2003 4006"]),
            "table changed": dict(replies=["55 70 03 01 9001 2003 8007"]),
            "no live answer": dict(replies=[]),
        }
        for name, kwargs in cases.items():
            with self.subTest(name):
                self.adapter_writes.clear()
                result, device = self.select(kwargs.pop("replies", [DPI_REPLY]), **kwargs)
                self.assertFalse(result["success"])
                self.assertFalse(result["outcomeUnknown"])
                self.assertEqual(self.adapter_writes, [])
                self.assertFalse(any(w[:2] == b"\x55\x71" for w in device.writes))

    def test_same_value_needs_no_write(self):
        result, device = self.select([DPI_REPLY], value=800)
        self.assertTrue(result["success"])
        self.assertEqual(self.adapter_writes, [])

    def test_unconfirmed_outcomes_are_unknown_and_never_retried(self):
        for name, replies in (("readback differs", [DPI_REPLY, DPI_REPLY]), ("no readback", [DPI_REPLY])):
            with self.subTest(name):
                self.adapter_writes.clear()
                result, device = self.select(replies)
                self.assertEqual((result["success"], result["outcomeUnknown"]), (False, True))
                self.assertEqual(self.adapter_writes, [2])
                self.assertEqual(sum(w[:2] == b"\x55\x71" for w in device.writes), 1)

    def test_write_budget(self):
        def greedy(dev, info, index):
            dev.write(list(bytes([0x55, 0x71, index]).ljust(64, b"\0")))
            dev.send_feature_report([0x55, 0x72])
        self.adapter.select_stage = greedy
        result, device = self.select([DPI_REPLY, "55 70 03 02 9001 2003 4006"])
        self.assertEqual((result["success"], result["outcomeUnknown"]), (False, True))
        self.assertEqual(device.feature_sets, [])
        self.adapter.MAX_WRITES = 3
        self.assertIsNone(self.framework.adapter_for(self.family))
        self.adapter.MAX_WRITES = 1

        def sneaky(dev, info, index):
            self.assertFalse(hasattr(dev, "_dev") or hasattr(dev, "__dict__"))
            dev.write(list(bytes([0x55, 0x71, index]).ljust(64, b"\0")))
        self.adapter.select_stage = sneaky
        result, _ = self.select([DPI_REPLY, "55 70 03 02 9001 2003 4006"])
        self.assertTrue(result["success"])

    def test_read_stages_queries_are_capped(self):
        def chatty(dev, info):
            for _ in range(3):
                dev.write([0x55, 0x70])
            return {"current": 800, "stages": [400, 800]}
        self.adapter.read_stages = chatty
        device = FakeDevice()
        view, target, note = self.framework.read(device, self.family, collection())
        self.assertIsNone(view)
        self.assertEqual(len(device.writes), 2)   # MAX_QUERY_WRITES default

    def test_vendor_software_blocks_switch(self):
        _, target = self.capture()
        device = FakeDevice([frame(DPI_REPLY)])
        family = spec.parse_family(dict(dpi_family(), standAsideFor=["vendor.exe"]))
        with patch.object(families, "_builtin", {"dpi_test": family}), \
                patch.object(provider_module.hid, "device", return_value=device):
            result = provider_module.set_device_dpi([collection()], target, 1600, 800, self.framework.token(target),
                                                    running=lambda names: True)
        self.assertEqual(result["error"], "dpi_vendor_software")
        self.assertEqual(device.writes, [])

    def test_provider_publishes_lease_and_engine_entry_matches_exact_collection(self):
        statuses, _ = poll([collection()], FakeDevice([frame("55 65 00 3c"), frame(DPI_REPLY)]))
        status, = statuses
        self.assertTrue(status.dpi["canSet"])
        self.assertEqual(status.dpi["targetToken"], self.framework.token(status.dpi_target))
        import server
        self.assertTrue(server.normalise_dpi(status.dpi, usable=True)["canSet"])
        target, token = status.dpi_target, status.dpi["targetToken"]
        device = FakeDevice([frame(DPI_REPLY), frame("55 70 03 02 9001 2003 4006")])
        with patch.object(provider_module.hid, "device", return_value=device):
            result = provider_module.set_device_dpi([collection(), collection(path=b"other")], target, 1600, 800, token)
            self.assertTrue(result["success"], result)
            twins = [collection(), collection()]
            self.assertEqual(provider_module.set_device_dpi(twins, target, 1600, 800, token)["error"], "dpi_target_changed")
            forged = dict(target, family="xiberia")
            self.assertEqual(provider_module.set_device_dpi([collection()], forged, 1600, 800, token)["error"],
                             "dpi_invalid_target")


if __name__ == "__main__":
    unittest.main()
