"""Offline passive-receiver candidate checks; no Windows delivery claim."""
import copy
import json
import unittest
from pathlib import Path

from test_families import (
    FakeDevice, collection, families, poll, provider_module, runner, spec,
)


FAMILY_ID = "razer_blackshark_v3_pro_rx"
DOCUMENT = json.loads(
    (Path(families.__file__).with_name("builtin") / (FAMILY_ID + ".json"))
    .read_text(encoding="utf-8")
)


def seal(data):
    data = list(data)
    data[62] = 0
    for value in data[:62]:
        data[62] ^= value
    return data


def battery_frame(percent=73, sub=1, argc=1, status_flag=0x80):
    data = [0] * 64
    data[:3] = [0x02, 0x02, 0x60]
    data[6] = min(255, 4 + argc)
    data[9] = status_flag
    data[10:14] = [0x21, sub, argc, percent]
    return seal(data)


QUERY_REQUEST = bytes(seal(
    [0x02, 0x00, 0x60, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x80, 0x21] + [0] * 53
))


def receiver(**changes):
    row = collection(
        vendor_id=0x1532,
        product_id=0x0577,
        product_string="Razer BlackShark V3 Pro",
        usage_page=0xFF14,
        usage=1,
        interface_number=5,
        path=b"blackshark-v3-pro-mi_05-ff14",
        physical_id=r"pnp:usb\vid_1532&pid_0577\receiver",
        input_report_length=64,
        output_report_length=64,
        input_report_sizes={"2": 64},
        output_report_sizes={"2": 64},
    )
    row.update(changes)
    return row


class ObservedDevice(FakeDevice):
    def __init__(self, replies=(), advance=None):
        super().__init__(replies)
        self.read_calls = []
        self.advance = advance

    def read(self, length, timeout):
        self.read_calls.append((length, timeout))
        if self.advance is not None:
            self.advance(timeout)
        return super().read(length, timeout)


class BlackSharkPassiveTests(unittest.TestCase):
    def setUp(self):
        self.family = spec.parse_family(copy.deepcopy(DOCUMENT))
        self.read = self.family.reads[0]

    def assert_receive_only(self, device):
        self.assertEqual(device.writes, [])
        self.assertEqual(device.feature_sets, [])
        self.assertEqual(device.feature_gets, [])

    def selected(self, rows):
        chosen, _ = families.select(rows)
        return [item for item in chosen if item[0].id == FAMILY_ID]

    def test_registered_exact_identity_and_passive_contract(self):
        self.assertIn(FAMILY_ID, families.load())
        self.assertEqual(
            [(d.vendor_id, d.product_id) for d in self.family.devices],
            [(0x1532, 0x0577)],
        )
        self.assertEqual(self.family.level, "C")
        self.assertEqual(self.family.kind, "headset")
        self.assertFalse(self.family.probe)
        self.assertEqual(self.family.interface, 5)
        self.assertEqual(self.family.collections, ((0xFF14, 1),))
        for report in (self.family.input_report, self.family.output_report):
            self.assertEqual((report.report_id, report.length), (2, 64))
        self.assertIsNone(self.family.feature_report)
        self.assertIsNone(self.family.dpi)
        self.assertEqual(self.family.stand_aside_for, ())
        self.assertEqual([r.name for r in self.family.reads], ["battery-listen", "battery-query"])
        self.assertEqual(self.read.transport, "listen")
        self.assertEqual(self.read.request, b"")
        self.assertIsNone(self.read.gate)
        self.assertGreaterEqual(self.read.timeout_ms, 120)
        self.assertLessEqual(self.read.timeout_ms, 250)
        self.assertEqual(self.read.max_reads, 8)
        query = self.family.reads[1]
        self.assertEqual(query.transport, "output")
        self.assertEqual(query.request, QUERY_REQUEST)
        self.assertEqual(len(query.request), 64)
        self.assertEqual(query.request[62], 0xC7)
        self.assertIsNone(query.gate)

    def test_reply_and_push_accept_percent_boundaries_without_charging(self):
        # Published V3-family layout sample from BrimDeck. PID 0577 hardware validation is pending.
        sample = bytes.fromhex("02 02 60 00 00 00 05 00 00 80 21 01 01 61") + bytes(48) + bytes.fromhex("A5 00")
        self.assertEqual(runner.parse(self.read.reply, sample).level, 97)
        for sub in (1, 2):
            for argc in (1, 49):
                for percent in (0, 1, 73, 100):
                    with self.subTest(sub=sub, argc=argc, percent=percent):
                        device = ObservedDevice([battery_frame(percent, sub, argc)])
                        result = runner.run(device, self.family, self.read, clock=lambda: 0)
                        self.assertEqual(result.kind, "reading")
                        self.assertEqual(result.level, percent)
                        self.assertFalse(result.charging)
                        self.assertFalse(result.charging_known)
                        self.assertEqual(len(device.read_calls), 1)
                        self.assert_receive_only(device)

    def test_invalid_frames_never_produce_readings_or_sends(self):
        cases = []
        for at in (0, 1, 2):
            data = battery_frame()
            data[at] ^= 1
            cases.append((f"header-{at}", seal(data)))
        cases.extend([
            ("short", battery_frame()[:-1]),
            ("long", battery_frame() + [0]),
        ])
        data = battery_frame()
        data[62] ^= 1
        cases.append(("checksum", data))
        for argc in (0, 50, 255):
            cases.append((f"argc-{argc}", battery_frame(argc=argc)))
        for percent in (101, 254, 255):
            cases.append((f"percent-{percent}", battery_frame(percent=percent)))
        for sub in (0, 3, 255):
            cases.append((f"non-battery-sub-{sub}", battery_frame(sub=sub)))
        charging_only = battery_frame()
        charging_only[10] = 0x2A
        cases.append(("charging-only", seal(charging_only)))
        link = battery_frame()
        link[10] = 0x20
        cases.append(("link-class", seal(link)))
        other_class = battery_frame()
        other_class[10] = 0x22
        cases.append(("other-class", seal(other_class)))
        rotated = battery_frame()
        rotated[10:13] = [1, 0x21, 1]
        cases.append(("rotated-class-sub-count", seal(rotated)))
        for name, data in cases:
            with self.subTest(name=name):
                self.assertEqual(runner.parse(self.read.reply, data).kind, "rejected")
                device = ObservedDevice([data])
                result = runner.run(device, self.family, self.read, clock=lambda: 0)
                self.assertNotEqual(result.kind, "reading")
                self.assertIsNone(result.level)
                self.assertFalse(result.charging_known)
                self.assert_receive_only(device)

    def test_checksum_covers_zero_through_sixty_two_only(self):
        data = battery_frame()
        data[61] ^= 0x80
        self.assertEqual(runner.parse(self.read.reply, data).kind, "rejected")
        data = seal(data)
        self.assertEqual(runner.parse(self.read.reply, data).kind, "reading")
        data[63] = 0xFF
        self.assertEqual(runner.parse(self.read.reply, data).kind, "reading")

    def test_silence_returns_no_reading_and_sends_nothing(self):
        device = ObservedDevice()
        result = runner.run(device, self.family, self.read, clock=lambda: 0)
        self.assertEqual(result.kind, "silent")
        self.assertIsNone(result.level)
        self.assertFalse(result.charging_known)
        self.assertEqual(device.read_calls, [(64, self.read.timeout_ms)])
        self.assert_receive_only(device)

    def test_passive_silence_falls_back_to_throttled_active_query_issue_1023(self):
        # Poll 1 (t=100): passive listen returns empty -> sends one 0x21 query -> 74%.
        # Poll 2 (t=120, within 60s): throttled -> zero opens, zero writes, cached 74%.
        # Poll 3 (t=165, >=60s): sends one 0x21 query -> refreshed 72%.
        now = [100.0]
        provider = provider_module.FamiliesProvider(running=lambda names: False, clock=lambda: now[0])

        class QueryDevice(ObservedDevice):
            def __init__(self, reply):
                super().__init__([])
                self._reply = reply

            def write(self, data):
                self.writes.append(bytes(data))
                if self._reply is not None:
                    self.replies.append(self._reply)
                return len(data)

        dev1 = QueryDevice(battery_frame(percent=74, sub=1, argc=1))
        readings1, _ = poll([receiver()], dev1, provider=provider)
        self.assertEqual(len(readings1), 1)
        self.assertEqual((readings1[0].level, readings1[0].charging_known, readings1[0].verification_level),
                         (74, False, "C"))
        self.assertEqual(dev1.writes, [QUERY_REQUEST])

        # Within 60s cooldown: zero opens, zero writes, and returns no fresh reading
        # so server.py preserves the prior reading as cached without faking freshness.
        now[0] = 120.0
        dev2 = QueryDevice(battery_frame(percent=70, sub=1, argc=1))
        readings2, _ = poll([receiver()], dev2, provider=provider)
        self.assertEqual(readings2, [])
        self.assertEqual(dev2.opened, [])
        self.assertEqual(dev2.writes, [])
        self.assertTrue(any("throttled" in line for line in provider.diagnostics()))

        # After 60s cooldown: sends one single 0x21 query and refreshes.
        now[0] = 165.0
        dev3 = QueryDevice(battery_frame(percent=72, sub=1, argc=1))
        readings3, _ = poll([receiver()], dev3, provider=provider)
        self.assertEqual(len(readings3), 1)
        self.assertEqual(readings3[0].level, 72)
        self.assertEqual(dev3.writes, [QUERY_REQUEST])

    def test_active_query_rejects_direct_request_echo_and_arms_cooldown_on_write_error(self):
        # Direct echo of the request must never become a reading.
        query = self.family.reads[1]
        self.assertEqual(runner.parse(query.reply, QUERY_REQUEST).kind, "rejected")

        # Write error arms cooldown immediately.
        now = [300.0]
        provider = provider_module.FamiliesProvider(running=lambda names: False, clock=lambda: now[0])

        class FailingWrite(ObservedDevice):
            def write(self, data):
                self.writes.append(bytes(data))
                raise OSError("write failed")

        dev1 = FailingWrite()
        readings1, _ = poll([receiver()], dev1, provider=provider)
        self.assertEqual(readings1, [])
        self.assertEqual(dev1.writes, [QUERY_REQUEST])

        now[0] = 320.0
        dev2 = FailingWrite()
        readings2, _ = poll([receiver()], dev2, provider=provider)
        self.assertEqual(readings2, [])
        self.assertEqual(dev2.writes, [])

    def test_active_query_ff_percent_is_asleep_and_still_throttled(self):
        now = [200.0]
        provider = provider_module.FamiliesProvider(running=lambda names: False, clock=lambda: now[0])

        class AsleepDevice(ObservedDevice):
            def write(self, data):
                self.writes.append(bytes(data))
                self.replies.append(battery_frame(percent=0xFF, sub=1, argc=1))
                return len(data)

        dev1 = AsleepDevice()
        readings1, _ = poll([receiver()], dev1, provider=provider)
        self.assertEqual(readings1, [])
        self.assertEqual(dev1.writes, [QUERY_REQUEST])
        self.assertTrue(any("asleep" in line for line in provider.diagnostics()))

        # Still throttled within 60s when asleep (protects wireless link).
        now[0] = 220.0
        dev2 = AsleepDevice()
        readings2, _ = poll([receiver()], dev2, provider=provider)
        self.assertEqual(readings2, [])
        self.assertEqual(dev2.writes, [])

    def test_continuous_unrelated_frames_hit_read_bound(self):
        device = ObservedDevice([battery_frame(sub=3)] * 32)
        result = runner.run(device, self.family, self.read, clock=lambda: 0)
        self.assertNotEqual(result.kind, "reading")
        self.assertEqual(len(device.read_calls), self.read.max_reads)
        self.assertEqual(len(device.replies), 32 - self.read.max_reads)
        self.assertTrue(all(
            length == 64 and 1 <= timeout <= 250
            for length, timeout in device.read_calls
        ))
        self.assert_receive_only(device)

    def test_deadline_stops_before_read_bound(self):
        now = [0.0]

        def advance(timeout):
            now[0] += timeout / 1000

        device = ObservedDevice([battery_frame(sub=3)] * 32, advance=advance)
        result = runner.run(device, self.family, self.read, clock=lambda: now[0])
        self.assertNotEqual(result.kind, "reading")
        self.assertEqual(len(device.read_calls), 1)
        self.assertLessEqual(now[0], 0.250)
        self.assert_receive_only(device)

    def test_matching_provider_can_coexist_with_vendor_app(self):
        provider = provider_module.FamiliesProvider(running=lambda names: True)
        device = ObservedDevice([battery_frame()])
        readings, _ = poll([receiver()], device, provider=provider)
        self.assertTrue(readings)
        self.assertEqual(device.opened, [receiver()["path"]])
        self.assert_receive_only(device)

    def test_both_direction_proofs_are_required_despite_zero_output(self):
        self.assertEqual(len(self.selected([receiver()])), 1)
        for direction in ("input", "output"):
            changes = [
                {direction + "_report_length": None},
                {direction + "_report_length": 63},
                {direction + "_report_length": 65},
                {direction + "_report_sizes": None},
                {direction + "_report_sizes": {}},
                {direction + "_report_sizes": {"3": 64}},
                {direction + "_report_sizes": {"2": 63}},
            ]
            for change in changes:
                with self.subTest(direction=direction, change=change):
                    rows = [receiver(**change)]
                    self.assertEqual(self.selected(rows), [])
                    device = ObservedDevice([battery_frame()])
                    readings, _ = poll(rows, device)
                    self.assertFalse(readings)
                    self.assertEqual(device.opened, [])
                    self.assertEqual(device.read_calls, [])
                    self.assert_receive_only(device)

    def test_pid_interface_and_collection_mismatches(self):
        changes = [
            {"product_id": pid} for pid in (0x0576, 0x0579, 0x057A, 0x0555)
        ] + [
            {"vendor_id": 0x1533},
            {"interface_number": None},
            {"interface_number": 4},
            {"interface_number": 6},
            {"usage_page": 0xFF13},
            {"usage": 2},
            {"path": b""},
        ]
        for change in changes:
            with self.subTest(change=change):
                self.assertEqual(self.selected([receiver(**change)]), [])

    def test_missing_physical_identity_and_duplicate_collection_do_not_open(self):
        rows_cases = []
        missing = receiver()
        missing.pop("physical_id")
        rows_cases.append([missing])
        rows_cases.extend([[receiver(physical_id=value)] for value in (
            None, "", r"pnp:usb\vid_1532&pid_057a\other", "collection:unknown")])
        duplicate = [
            receiver(),
            receiver(path=b"blackshark-v3-pro-mi_05-ff14-duplicate"),
        ]
        self.assertEqual(self.selected(duplicate), [])
        rows_cases.append(duplicate)
        for rows in rows_cases:
            with self.subTest(rows=rows):
                device = ObservedDevice([battery_frame()])
                readings, _ = poll(rows, device)
                self.assertFalse(readings)
                self.assertEqual(device.opened, [])
                self.assertEqual(device.read_calls, [])
                self.assert_receive_only(device)


class ChecksumCompatibilityTests(unittest.TestCase):
    def test_existing_sum_constructor_and_parser_keep_modulo_semantics(self):
        legacy = spec.Checksum(0, 3, 1)
        parsed = spec._checksum({"sum": [0, 3], "equals": "01"}, "checksum")
        self.assertEqual(legacy, parsed)
        self.assertEqual(parsed.kind, "sum")
        self.assertTrue(runner._checksum_ok(bytes([255, 1, 1]), parsed))
        self.assertFalse(runner._checksum_ok(bytes([255, 1, 2]), parsed))
        self.assertTrue(runner._checksum_ok(b"", None))

    def test_xor_uses_exclusive_end_and_selected_range(self):
        checksum = spec._checksum({"xor": [1, 4], "equals": "00"}, "checksum")
        self.assertEqual(checksum.kind, "xor")
        self.assertTrue(runner._checksum_ok(bytes([99, 1, 2, 3, 88]), checksum))
        self.assertFalse(runner._checksum_ok(bytes([99, 1, 2, 4, 88]), checksum))

    def test_exactly_one_checksum_kind_and_valid_range_required(self):
        invalid = [
            {"equals": "00"},
            {"sum": [0, 63], "xor": [0, 63], "equals": "00"},
            {"xor": [0, 63], "equals": "00", "other": 1},
            {"xor": [0, 63], "equals": "100"},
        ]
        for kind in ("sum", "xor"):
            for span in (
                None, "0 63", [], [0], [0, 1, 2],
                [-1, 63], [0, 0], [2, 1], [0, spec.MAX_REPORT + 1],
                [spec.MAX_REPORT, spec.MAX_REPORT + 1],
                [True, 63], [0, True], ["0", 63], [0, 1.5],
            ):
                invalid.append({kind: span, "equals": "00"})
        for document in invalid:
            with self.subTest(document=document):
                with self.assertRaises(spec.SpecError):
                    spec._checksum(document, "checksum")

    def test_checksum_range_requires_complete_reply(self):
        reply = spec.parse_family(copy.deepcopy(DOCUMENT)).reads[0].reply
        self.assertEqual(runner.parse(reply, battery_frame()[:62]).kind, "rejected")


if __name__ == "__main__":
    unittest.main()
