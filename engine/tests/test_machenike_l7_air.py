"""Offline protocol and provider checks; no real HID devices are opened."""
import copy
import json
import unittest
from pathlib import Path
from unittest.mock import patch

from test_families import FakeDevice, collection, frame, poll
import families
from families import runner, spec
from providers import families as provider_module


DOCUMENT = json.loads(
    (Path(families.__file__).with_name("builtin") / "machenike_l7_air.json").read_text(encoding="utf-8")
)
GATE_REQUEST = bytes.fromhex("04 AA 00 AA") + bytes(60)
BATTERY_REQUEST = bytes.fromhex("04 1C 00 1A 02") + bytes(59)


def receiver(**changes):
    row = collection(
        vendor_id=0x320F, product_id=0x226F,
        product_string="2.4G Wireless Receiver",
        usage_page=0xFF1C, usage=0x0092, path=b"l7-air-ff1c",
        physical_id=r"pnp:usb\vid_320f&pid_226f\receiver",
        input_report_length=64, output_report_length=64,
        input_report_sizes={"4": 64}, output_report_sizes={"4": 64},
    )
    row.update(changes)
    return row


def gate_frame():
    return frame("04 00 00 AA 00 00 00 00 FF")


def battery_frame(percent=42, state=0):
    reply = frame("04 00 00 1A")
    reply[8], reply[9] = percent, state
    return reply


class RecordingDevice(FakeDevice):
    def __init__(self, replies=(), *, short_write_at=0, write_error_at=0,
                 read_error_at=0, error_type=OSError):
        super().__init__(replies)
        self.read_calls = []
        self.short_write_at = short_write_at
        self.write_error_at = write_error_at
        self.read_error_at = read_error_at
        self.error_type = error_type

    def write(self, data):
        length = super().write(data)
        if len(self.writes) == self.write_error_at:
            raise self.error_type("fake write error")
        return length - 1 if len(self.writes) == self.short_write_at else length

    def read(self, length, timeout):
        self.read_calls.append((length, timeout))
        if len(self.read_calls) == self.read_error_at:
            raise self.error_type("fake read error")
        return super().read(length, timeout)


class MachenikeL7AirTests(unittest.TestCase):
    def setUp(self):
        self.family = spec.parse_family(DOCUMENT)
        self.read = self.family.reads[0]

    def run_frames(self, replies, **faults):
        device = RecordingDevice(replies, **faults)
        outcome = runner.run(device, self.family, self.read, clock=lambda: 0.0)
        self.assertEqual(device.feature_sets, [])
        self.assertEqual(device.feature_gets, [])
        return outcome, device

    def poll_rows(self, rows, device, provider=None):
        mouse = receiver(usage_page=1, usage=2, path=b"l7-air-mouse")
        with patch.object(families, "families", return_value={self.family.id: self.family}):
            return poll([mouse] + rows, device, provider)

    def assert_no_sends(self, rows, provider=None):
        device = RecordingDevice([gate_frame(), battery_frame()])
        self.poll_rows(rows, device, provider)
        self.assertEqual(device.writes, [])
        self.assertEqual(device.feature_sets, [])
        self.assertEqual(device.feature_gets, [])
        self.assertEqual(device.read_calls, [])

    def test_builtin_identity_and_read_limits(self):
        self.assertIn(self.family.id, families.load())
        self.assertEqual(self.family.level, "C")
        self.assertEqual(self.family.kind, "mouse")
        self.assertFalse(self.family.probe)
        self.assertEqual(len(self.family.devices), 1)
        device = self.family.devices[0]
        self.assertEqual((device.vendor_id, device.product_id), (0x320F, 0x226F))
        self.assertEqual(device.product_string, "2.4G Wireless Receiver")
        self.assertEqual(self.family.collections, ((0xFF1C, 0x0092),))
        for report in (self.family.input_report, self.family.output_report):
            self.assertEqual((report.report_id, report.length), (4, 64))
        self.assertIsNone(self.family.feature_report)
        self.assertIsNone(self.family.dpi)
        self.assertEqual(len(self.family.reads), 1)
        self.assertEqual((self.read.transport, self.read.max_reads), ("output", 1))
        self.assertEqual((self.read.gate.length, self.read.reply.length), (64, 64))
        self.assertEqual(self.family.stand_aside_for, ("machenike l7 air 1k gaming mouse.exe",))

    def test_exact_output_and_input_for_zero_full_and_charging_states(self):
        for percent in (0, 42, 100):
            for state in (0, 1, 2):
                with self.subTest(percent=percent, state=state):
                    outcome, device = self.run_frames([gate_frame(), battery_frame(percent, state)])
                    self.assertEqual(outcome.kind, "reading")
                    self.assertEqual(outcome.level, percent)
                    self.assertEqual(outcome.charging, state == 1)
                    self.assertEqual(outcome.charging_known, state == 1)
                    self.assertEqual(device.writes, [GATE_REQUEST, BATTERY_REQUEST])
                    self.assertEqual(device.read_calls, [(64, 800), (64, 800)])

    def test_silent_malformed_and_echoed_gates_never_send_battery_request(self):
        good = gate_frame()
        bad = [[], good[:8], good[:9], good[:63], good + [0], list(GATE_REQUEST)]
        for at, value in ((0, 5), (3, 0xAB), (7, 1), (7, 0xFE), (7, 0xFF), (8, 0), (8, 0xFE)):
            changed = good.copy()
            changed[at] = value
            bad.append(changed)
        for index, reply in enumerate(bad):
            with self.subTest(case=index):
                battery = battery_frame()
                outcome, device = self.run_frames([reply, good, battery])
                self.assertEqual(outcome.kind, "silent")
                self.assertIsNone(outcome.level)
                self.assertEqual(device.writes, [GATE_REQUEST])
                self.assertEqual(device.read_calls, [(64, 800)])
                self.assertEqual(device.replies, [good, battery])

    def test_battery_length_headers_ranges_states_and_echo_fail_closed(self):
        good = battery_frame()
        bad = [[], good[:9], good[:63], good + [0], list(BATTERY_REQUEST)]
        for at, value in (
            (0, 5), (3, 0xAA), (7, 1), (7, 0xFE), (7, 0xFF),
            (8, 101), (8, 0xFF), (9, 3), (9, 0xFE), (9, 0xFF),
        ):
            changed = good.copy()
            changed[at] = value
            bad.append(changed)
        for index, reply in enumerate(bad):
            with self.subTest(case=index):
                outcome, device = self.run_frames([gate_frame(), reply, good])
                self.assertIn(outcome.kind, ("silent", "rejected"))
                self.assertIsNone(outcome.level)
                self.assertFalse(outcome.charging_known)
                self.assertEqual(device.writes, [GATE_REQUEST, BATTERY_REQUEST])
                self.assertEqual(device.read_calls, [(64, 800), (64, 800)])
                self.assertEqual(device.replies, [good])

    def test_short_writes_stop_without_retry(self):
        for at in (1, 2):
            with self.subTest(at=at):
                outcome, device = self.run_frames(
                    [gate_frame(), battery_frame()], short_write_at=at
                )
                self.assertEqual(outcome.kind, "anomaly")
                self.assertEqual(device.writes, [GATE_REQUEST, BATTERY_REQUEST][:at])
                self.assertEqual(len(device.read_calls), at - 1)

    def test_gate_and_query_io_errors_stop_without_retry(self):
        for operation in ("write", "read"):
            for at in (1, 2):
                for error_type in (OSError, ValueError):
                    with self.subTest(operation=operation, at=at, error=error_type):
                        outcome, device = self.run_frames(
                            [gate_frame(), battery_frame()],
                            **{operation + "_error_at": at, "error_type": error_type},
                        )
                        self.assertEqual(outcome.kind, "anomaly")
                        self.assertEqual(outcome.note, "I/O error")
                        self.assertEqual(device.writes, [GATE_REQUEST, BATTERY_REQUEST][:at])

    def test_gate_length_type_and_bounds_are_validated(self):
        for length in (None, True, "64", 0, 1, 65, 4097):
            with self.subTest(length=length):
                document = copy.deepcopy(DOCUMENT)
                document["reads"][0]["gate"]["length"] = length
                with self.assertRaises(spec.SpecError):
                    spec.parse_family(document)
        for update in (
            {"match": [{"at": 64, "eq": "04"}]},
            {"online": {"at": 64, "eq": "FF"}},
            {"offline": {"at": 64, "eq": "00"}},
            {"checksum": {"sum": [0, 65], "equals": "00"}},
        ):
            with self.subTest(update=update):
                document = copy.deepcopy(DOCUMENT)
                document["reads"][0]["gate"].update(update)
                with self.assertRaises(spec.SpecError):
                    spec.parse_family(document)
        document = copy.deepcopy(DOCUMENT)
        gate = document["reads"][0]["gate"]
        gate["match"].append({"at": 63, "eq": "00"})
        gate["online"] = {"at": 63, "eq": "00"}
        gate["offline"] = {"at": 63, "eq": "01"}
        gate["checksum"] = {"sum": [0, 64], "equals": "00"}
        self.assertEqual(spec.parse_family(document).reads[0].gate.length, 64)

    def test_omitted_gate_length_preserves_existing_behavior(self):
        document = copy.deepcopy(DOCUMENT)
        del document["reads"][0]["gate"]["length"]
        legacy = spec.parse_family(document).reads[0].gate
        self.assertIsNone(legacy.length)
        self.assertIs(runner.parse_gate(legacy, gate_frame()[:9]), True)
        self.assertIsNone(runner.parse_gate(self.read.gate, gate_frame()[:9]))

    def test_provider_selects_one_collection_on_the_joint_physical_device(self):
        device = RecordingDevice([gate_frame(), battery_frame()])
        self.poll_rows([receiver()], device)
        self.assertEqual(device.opened, [b"l7-air-ff1c"])
        self.assertEqual(device.closed, 1)
        self.assertEqual(device.writes, [GATE_REQUEST, BATTERY_REQUEST])
        self.assertEqual(device.feature_sets, [])
        self.assertEqual(device.feature_gets, [])

    def test_wrong_identity_parent_or_collection_sends_nothing(self):
        for changes in (
            {"vendor_id": 0x320E},
            {"product_id": 0x226E},
            {"product_string": "MACHENIKE L7 Air 4K"},
            {"product_string": None},
            {"usage_page": 0xFF1D},
            {"usage": 0x0093},
            {"physical_id": None},
            {"physical_id": r"pnp:usb\vid_320f&pid_226e\receiver"},
            {"physical_id": r"pnp:usb\vid_320e&pid_226f\receiver"},
            {"path": b""},
        ):
            with self.subTest(changes=changes):
                self.assert_no_sends([receiver(**changes)])

    def test_each_direction_requires_exact_report_proof(self):
        for direction in ("input", "output"):
            for changes in (
                {direction + "_report_length": None},
                {direction + "_report_length": 63},
                {direction + "_report_length": 65},
                {direction + "_report_sizes": {}},
                {direction + "_report_sizes": {"5": 64}},
                {direction + "_report_sizes": {"4": 63}},
            ):
                with self.subTest(changes=changes):
                    self.assert_no_sends([receiver(**changes)])

    def test_duplicate_matching_collections_send_nothing(self):
        self.assert_no_sends([receiver(), receiver(path=b"l7-air-duplicate")])

    def test_identity_and_report_proof_are_not_borrowed_from_other_rows(self):
        unproved = receiver(input_report_sizes={}, output_report_sizes={})
        self.assert_no_sends([
            unproved,
            receiver(path=b"other-name", product_string="Other receiver"),
        ])
        self.assert_no_sends([
            unproved,
            receiver(path=b"other-parent", product_id=0x226E,
                     physical_id=r"pnp:usb\vid_320f&pid_226e\receiver"),
        ])

    def test_official_driver_active_sends_nothing(self):
        provider = provider_module.FamiliesProvider(
            running=lambda names: "machenike l7 air 1k gaming mouse.exe" in names
        )
        self.assert_no_sends([receiver()], provider)


if __name__ == "__main__":
    unittest.main()
