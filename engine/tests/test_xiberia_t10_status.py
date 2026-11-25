"""Offline T10 status selection, parsing and per-device read memory."""
import copy
import unittest
from unittest.mock import patch

from test_families import FakeDevice, XIBERIA, collection, frame, poll

import families
from families import memory as mem, runner, spec
from providers import families as provider_module


T10_PARENT = r"pnp:usb\vid_36f9&pid_f001\receiver"
STATUS_REQUEST = bytes(frame("55 10 01"))
LEGACY_REQUEST = bytes(frame("55 65 00 00"))


def t10_collection(**changes):
    row = collection(product_id=0xF001, product_string="XIBERIA T10 Wireless",
                     physical_id=T10_PARENT, path=b"t10-ff90")
    row.update(changes)
    return row


def status_frame(percent=73, state=1):
    reply = frame("55 10 01")
    reply[4], reply[7] = state, percent
    return reply


class T10StatusTests(unittest.TestCase):
    def setUp(self):
        self.family = spec.parse_family(copy.deepcopy(XIBERIA))
        self.status = next(r for r in self.family.reads if r.name == "t10_status")
        active = patch.object(families, "active", return_value=[self.family])
        active.start()
        self.addCleanup(active.stop)

    def assert_not_opened(self, rows):
        dev = FakeDevice([status_frame()])
        statuses, _ = poll(rows, dev)
        self.assertEqual(statuses, [])
        self.assertEqual(dev.opened, [])
        self.assertEqual(dev.writes, [])

    def old_memory(self, disabled=False):
        memory = mem.ReadMemory()
        key = "xiberia|" + families.memory_key(T10_PARENT)
        memory.restore({key: {"working": "battery", "rounds": 3,
                              "disabled": disabled}})
        return memory, key

    def test_legacy_read_keeps_its_index_name_and_semantics(self):
        legacy = self.family.reads[0]
        self.assertEqual(self.family.id, "xiberia")
        self.assertEqual(legacy.name, "battery")
        self.assertEqual(legacy.request, bytes.fromhex("55 65 00 00"))
        self.assertEqual(runner.parse(legacy.reply, frame("55 65 00 00")).kind, "rejected")
        self.assertEqual(runner.parse(legacy.reply, frame("55 65 00 64")).level, 100)

    def test_unique_proved_collection_on_either_page_in_both_orders(self):
        for page, sibling_page in ((0xFF82, 0xFF90), (0xFF90, 0xFF82)):
            good = t10_collection(usage_page=page, path=b"proved")
            sibling = t10_collection(usage_page=sibling_page, path=b"unproved",
                                     output_report_sizes={})
            for rows in ([good, sibling], [sibling, good]):
                with self.subTest(page=page, good_first=rows[0] is good):
                    chosen, _ = families.select(rows)
                    self.assertEqual(len(chosen), 1)
                    self.assertIs(chosen[0][2], good)
                    dev = FakeDevice([status_frame()])
                    statuses, _ = poll(rows, dev)
                    self.assertEqual(len(statuses), 1)
                    self.assertEqual(dev.opened, [b"proved"])
                    self.assertEqual(dev.writes, [STATUS_REQUEST])
                    self.assertEqual(dev.closed, 1)

    def test_both_or_neither_proved_sends_nothing_in_both_orders(self):
        for proved in (True, False):
            rows = [t10_collection(usage_page=0xFF82, path=b"ff82"),
                    t10_collection(usage_page=0xFF90, path=b"ff90")]
            if not proved:
                for row in rows:
                    row["input_report_sizes"] = {}
            for ordered in (rows, list(reversed(rows))):
                with self.subTest(proved=proved, first=ordered[0]["path"]):
                    self.assertEqual(families.select(ordered)[0], [])
                    self.assert_not_opened(ordered)

    def test_identity_and_report_proof_must_be_exact(self):
        changes = (
            {"vendor_id": 0x36FA},
            {"product_id": 0xF003},
            {"product_id": 0xF002},
            {"product_string": None},
            {"product_string": "XIBERIA T10"},
            {"product_string": "XIBERIA T10 Wireless Plus"},
            {"usage_page": 0xFF81},
            {"usage": 2},
            {"input_report_length": 63},
            {"output_report_length": 65},
            {"input_report_length": "64"},
            {"input_report_sizes": {"55": 64}},
            {"output_report_sizes": {"65": 64}},
            {"input_report_sizes": {"85": 63}},
            {"output_report_sizes": {"85": "64"}},
            {"input_report_sizes": {}},
            {"output_report_sizes": {}},
            {"path": b""},
        )
        for change in changes:
            with self.subTest(change=change):
                rows = [t10_collection(**change)]
                self.assertEqual(families.select(rows)[0], [])
                self.assert_not_opened(rows)

    def test_unproved_parent_cannot_borrow_an_unidentified_siblings_proof(self):
        unproved = t10_collection(output_report_sizes={})
        for parent in ("", None, "collection:unidentified", r"pnp:usb\vid_36f9&pid_f002\other"):
            with self.subTest(parent=parent):
                sibling = t10_collection(physical_id=parent, usage_page=0xFF82,
                                         path=b"unidentified-parent")
                self.assert_not_opened([unproved, sibling])

    def test_wireless_boundaries_and_high_flag_have_unknown_charge(self):
        self.assertEqual(len(STATUS_REQUEST), 64)
        self.assertEqual(STATUS_REQUEST[3:], bytes(61))
        for raw in (0, 73, 100, 0x80, 0xE4):
            with self.subTest(raw=raw):
                reply = status_frame(percent=raw)
                outcome = runner.parse(self.status.reply, reply)
                self.assertEqual(outcome.kind, "reading")
                self.assertEqual(outcome.level, raw & 0x7F)
                self.assertFalse(outcome.charging)
                self.assertFalse(outcome.charging_known)
                row = t10_collection(product_string="  xIbErIa T10 wIrElEsS  ")
                dev = FakeDevice([reply])
                statuses, provider = poll([row], dev)
                self.assertEqual(len(statuses), 1)
                self.assertEqual(dev.writes, [STATUS_REQUEST])
                self.assertEqual(statuses[0].verification_level, "C")
                self.assertEqual(statuses[0].model_source, "family:xiberia")
                self.assertFalse(statuses[0].charging_known)
                entry = provider.memory.entry("xiberia", families.memory_key(T10_PARENT))
                self.assertEqual(entry.working, "t10_status")

    def test_explicit_or_invalid_status_never_falls_back_to_legacy(self):
        wrong_header = status_frame()
        wrong_header[2] = 2
        cases = (
            ("offline", status_frame(state=0), "asleep"),
            ("wired", status_frame(state=2), "asleep"),
            ("unknown state", status_frame(state=3), "rejected"),
            ("flagged state", status_frame(state=0x81), "rejected"),
            ("invalid state", status_frame(state=0xFF), "rejected"),
            ("short", status_frame()[:-1], "rejected"),
            ("long", status_frame() + [0], "rejected"),
            ("percent", status_frame(percent=101), "rejected"),
            ("flagged percent", status_frame(percent=0xE5), "rejected"),
            ("invalid percent", status_frame(percent=0xFF), "rejected"),
            ("header", wrong_header, "rejected"),
            ("legacy reply", frame("55 65 00 64"), "rejected"),
        )
        for label, reply, kind in cases:
            with self.subTest(case=label):
                self.assertEqual(runner.parse(self.status.reply, reply).kind, kind)
                dev = FakeDevice([reply, [], frame("55 65 00 64")])
                statuses, _ = poll([t10_collection()], dev)
                self.assertEqual(statuses, [])
                self.assertEqual(dev.writes, [STATUS_REQUEST])

    def test_echoes_are_not_fresh_status(self):
        dev = FakeDevice([list(STATUS_REQUEST), [], frame("55 65 00 64")])
        statuses, provider = poll([t10_collection()], dev)
        self.assertEqual(statuses, [])
        self.assertEqual(dev.writes, [STATUS_REQUEST])
        entry = provider.memory.entry("xiberia", families.memory_key(T10_PARENT))
        self.assertIsNone(entry.working)

    def test_silence_falls_back_once_and_remembers_legacy(self):
        dev = FakeDevice([[], frame("55 65 00 64")])
        statuses, provider = poll([t10_collection()], dev)
        self.assertEqual(len(statuses), 1)
        self.assertEqual(dev.writes, [STATUS_REQUEST, LEGACY_REQUEST])
        key = "xiberia|" + families.memory_key(T10_PARENT)
        self.assertEqual(set(provider.memory.snapshot()), {key})
        self.assertEqual(provider.memory.snapshot()[key]["working"], "battery")
        next_dev = FakeDevice([frame("55 65 00 64")])
        statuses, _ = poll([t10_collection()], next_dev, provider)
        self.assertEqual(len(statuses), 1)
        self.assertEqual(next_dev.writes, [LEGACY_REQUEST])

    def test_historical_working_legacy_key_still_wins(self):
        for percent in (0, 100):
            with self.subTest(percent=percent):
                memory, key = self.old_memory()
                provider = provider_module.FamiliesProvider(
                    memory=memory, running=lambda names: False)
                dev = FakeDevice([frame(f"55 65 00 {percent:02X}")])
                statuses, _ = poll([t10_collection()], dev, provider)
                self.assertEqual(len(statuses), int(percent > 0))
                self.assertEqual(dev.writes, [LEGACY_REQUEST])
                self.assertEqual(set(memory.snapshot()), {key})
                self.assertEqual(memory.snapshot()[key]["working"], "battery")

    def test_historical_disabled_key_only_blocks_that_physical_device(self):
        memory, key = self.old_memory(disabled=True)
        provider = provider_module.FamiliesProvider(
            memory=memory, running=lambda names: False)
        other = t10_collection(physical_id=T10_PARENT + "-other", path=b"other-t10")
        dev = FakeDevice([status_frame()])
        statuses, _ = poll([t10_collection(), other], dev, provider)
        self.assertEqual(len(statuses), 1)
        self.assertEqual(statuses[0].physical_id, other["physical_id"])
        self.assertEqual(dev.opened, [b"other-t10"])
        self.assertEqual(dev.writes, [STATUS_REQUEST])
        self.assertTrue(memory.snapshot()[key]["disabled"])
        self.assertEqual(memory.snapshot()[key]["working"], "battery")

    def test_other_xiberia_receivers_only_plan_and_send_original_read(self):
        devices = (
            (0xF008, "XIBERIA T7"),
            (0xF008, "XIBERIA T10 Wireless"),
            (0xF016, "XIBERIA K03S"),
            (0xF002, "XIBERIA K06S Wireless"),
        )
        for product_id, product_string in devices:
            with self.subTest(product_id=product_id, product_string=product_string):
                row = collection(product_id=product_id, product_string=product_string)
                provider = provider_module.FamiliesProvider(running=lambda names: False)
                dev = FakeDevice([frame("55 65 00 64")])
                with patch.object(provider.memory, "plan", wraps=provider.memory.plan) as plan:
                    statuses, _ = poll([row], dev, provider)
                self.assertEqual(len(statuses), 1)
                self.assertEqual(dev.writes, [LEGACY_REQUEST])
                plan.assert_called_once_with(
                    "xiberia", families.memory_key(row["physical_id"]), ["battery"])

    def test_io_anomalies_do_not_try_legacy(self):
        class ShortWrite(FakeDevice):
            def write(self, data):
                return super().write(data) - 1

        class ReadError(FakeDevice):
            def read(self, length, timeout):
                raise OSError("unavailable")

        for dev in (FakeDevice(fail=True), ShortWrite(), ReadError()):
            with self.subTest(device=type(dev).__name__):
                statuses, provider = poll([t10_collection()], dev)
                self.assertEqual(statuses, [])
                self.assertNotIn(LEGACY_REQUEST, dev.writes)
                entry = provider.memory.entry("xiberia", families.memory_key(T10_PARENT))
                self.assertEqual(entry.retired, {"t10_status"})
                self.assertEqual(dev.closed, 1)

    def test_continuous_invalid_replies_are_bounded(self):
        class Noise(FakeDevice):
            reads = 0

            def read(self, length, timeout):
                self.reads += 1
                return status_frame(state=3)

        dev = Noise()
        provider = provider_module.FamiliesProvider(
            running=lambda names: False, clock=lambda: 0)
        statuses, _ = poll([t10_collection()], dev, provider)
        self.assertEqual(statuses, [])
        self.assertEqual(dev.reads, self.status.max_reads)
        self.assertEqual(dev.writes, [STATUS_REQUEST])

    def test_xiberia_hub_stands_aside(self):
        for process in ("xiberia hub.exe", "xiberia x hub.exe"):
            with self.subTest(process=process):
                provider = provider_module.FamiliesProvider(running=lambda names: process in names)
                dev = FakeDevice([status_frame()])
                statuses, _ = poll([t10_collection()], dev, provider)
                self.assertEqual(statuses, [])
                self.assertEqual(dev.opened, [])
                self.assertEqual(dev.writes, [])

    def test_t10_constant_output_ff90_matches_issue_1070_inventory(self):
        # #1070: real Windows HidP exposes Input 0x55 (64B) + OutputReportByteLength=64
        # with zero output value/button caps (output_constant_only=True) on FF90:0001,
        # while FF82:0001 exposes report 0x41 (64B).
        ff90 = t10_collection(usage_page=0xFF90, path=b"t10-ff90",
                              input_report_sizes={"85": 64},
                              output_report_sizes=None, output_constant_only=True)
        ff82 = t10_collection(usage_page=0xFF82, path=b"t10-ff82",
                              input_report_sizes={"65": 64}, output_report_sizes={"65": 64})
        for rows in ([ff90, ff82], [ff82, ff90]):
            with self.subTest(first=rows[0]["path"]):
                dev = FakeDevice([status_frame(percent=56, state=1)])
                statuses, _ = poll(rows, dev)
                self.assertEqual(len(statuses), 1)
                self.assertEqual(statuses[0].level, 56)
                self.assertEqual(dev.opened, [b"t10-ff90"])
                self.assertEqual(dev.writes, [STATUS_REQUEST])

    def test_k03s_c001_downgraded_to_observed_unsupported_and_opens_nothing(self):
        # #1035: confirmed 36F9:C001 "XIBERIA K03S Wireless 7.1" has no PC battery
        # support even in official X HUB -> must not be opened or queried.
        parent = r"pnp:usb\vid_36f9&pid_c001\receiver"
        base = dict(vendor_id=0x36F9, product_id=0xC001,
                    product_string="XIBERIA K03S Wireless 7.1", physical_id=parent)
        ff90 = collection(**base, usage_page=0xFF90, usage=1, path=b"c001-ff90",
                          input_report_length=64, output_report_length=64,
                          input_report_sizes={"85": 64}, output_report_sizes=None,
                          output_constant_only=True)
        ff82 = collection(**base, usage_page=0xFF82, usage=1, path=b"c001-ff82",
                          input_report_length=64, output_report_length=64,
                          input_report_sizes={"65": 64}, output_report_sizes={"65": 64})
        ff22 = collection(**base, usage_page=0xFF22, usage=1, path=b"c001-ff22",
                          input_report_length=0, output_report_length=0,
                          input_report_sizes=None, output_report_sizes=None)
        self.assertFalse(families.wants_caps(0x36F9, 0xC001, 0xFF90, 0x0001))
        self.assert_not_opened([ff22, ff82, ff90])

    def test_constant_output_gate_is_strictly_fail_closed(self):
        t10_const = t10_collection(usage_page=0xFF90, path=b"t10-ff90",
                                   input_report_sizes={"85": 64},
                                   output_report_sizes=None, output_constant_only=True)
        cases = (
            ("ff82 cannot use constant output", [dict(t10_const, usage_page=0xFF82)]),
            ("missing output_constant_only flag", [dict(t10_const, output_constant_only=False)]),
            ("wrong output length", [dict(t10_const, output_report_length=63)]),
            ("extra input report id", [dict(t10_const, input_report_sizes={"85": 64, "65": 64})]),
            ("non-None wrong output sizes", [dict(t10_const, output_report_sizes={"65": 64})]),
            ("duplicate t10 ff90 with unproved sibling", [
                t10_const, t10_collection(usage_page=0xFF90, path=b"t10-ff90-dup",
                                          input_report_sizes=None, output_report_sizes=None)]),
        )
        for label, rows in cases:
            with self.subTest(label):
                self.assertEqual(families.select(rows)[0], [])
                self.assert_not_opened(rows)
