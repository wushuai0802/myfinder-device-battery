"""Offline protocol and provider checks for Dareu 2.4G JM receiver (260D:1156)."""
import json
import unittest
from pathlib import Path
from unittest.mock import patch

from test_families import FakeDevice, collection, poll
import families
from families import runner, spec


DOCUMENT = json.loads(
    (Path(families.__file__).with_name("builtin") / "dareu_jm_24g.json").read_text(encoding="utf-8")
)
GATE_REQUEST = bytes.fromhex("08 03 00 00 00 00 00 00 00 00 00 00 00 00 00 00 4A")
BATTERY_REQUEST = bytes.fromhex("08 04 00 00 00 00 00 00 00 00 00 00 00 00 00 00 49")


def frame17(head_hex=""):
    head = bytes.fromhex(head_hex) if head_hex else b""
    return list(head + bytes(17 - len(head)))


def receiver(**changes):
    row = collection(
        vendor_id=0x260D, product_id=0x1156,
        product_string="2.4G Wireless Receiver",
        usage_page=0xFF02, usage=0x0002, path=b"dareu-1156-ff02",
        physical_id=r"pnp:usb\vid_260d&pid_1156\receiver",
        input_report_length=17, output_report_length=17,
        input_report_sizes={"8": 17}, output_report_sizes={"8": 17},
    )
    row.update(changes)
    return row


def gate_reply(online=1):
    reply = frame17("08 03 00 00 00 01")
    reply[6] = online
    reply[16] = (0x55 - sum(reply[:16])) & 0xFF
    return reply


def battery_reply(percent=75, status=0):
    reply = frame17("08 04 00 00 00 02")
    reply[6] = percent
    reply[7] = status
    reply[16] = (0x55 - sum(reply[:16])) & 0xFF
    return reply


class DareuJm24gTests(unittest.TestCase):
    def setUp(self):
        self.family = spec.parse_family(DOCUMENT)
        self.read = self.family.reads[0]

    def run_frames(self, replies):
        device = FakeDevice(replies)
        outcome = runner.run(device, self.family, self.read, clock=lambda: 0.0)
        self.assertEqual(device.feature_sets, [])
        self.assertEqual(device.feature_gets, [])
        return outcome, device

    def poll_rows(self, rows, device):
        mouse = receiver(usage_page=1, usage=2, path=b"dareu-1156-mouse")
        with patch.object(families, "families", return_value={self.family.id: self.family}):
            return poll([mouse] + rows, device)

    def test_builtin_identity_and_report_spec(self):
        loaded = families.load()
        self.assertIn("dareu_jm_24g", loaded)
        self.assertEqual(self.family.level, "C")
        self.assertEqual(self.family.kind, "mouse")
        self.assertFalse(self.family.probe)
        self.assertEqual(len(self.family.devices), 1)
        dev = self.family.devices[0]
        self.assertEqual((dev.vendor_id, dev.product_id), (0x260D, 0x1156))
        self.assertEqual(dev.product_string, "2.4G Wireless Receiver")
        self.assertEqual(self.family.collections, ((0xFF02, 0x0002),))
        self.assertEqual((self.family.input_report.report_id, self.family.input_report.length), (8, 17))
        self.assertEqual((self.family.output_report.report_id, self.family.output_report.length), (8, 17))
        self.assertIsNone(self.family.feature_report)
        self.assertEqual(sum(GATE_REQUEST) & 0xFF, 0x55)
        self.assertEqual(sum(BATTERY_REQUEST) & 0xFF, 0x55)

    def test_gate_online_then_battery_and_charging_states(self):
        for percent in (0, 63, 100):
            for status, expected_charging in ((0, False), (1, True), (2, True)):
                with self.subTest(percent=percent, status=status):
                    outcome, device = self.run_frames([gate_reply(1), battery_reply(percent, status)])
                    self.assertEqual(outcome.kind, "reading")
                    self.assertEqual(outcome.level, percent)
                    self.assertEqual(outcome.charging, expected_charging)
                    self.assertTrue(outcome.charging_known)
                    self.assertEqual(device.writes, [GATE_REQUEST, BATTERY_REQUEST])

    def test_skips_0a_push_events_and_echoes(self):
        event_0a = frame17("08 0A 01 02 03 04 55 00")
        outcome, device = self.run_frames([
            list(GATE_REQUEST),
            event_0a,
            gate_reply(1),
            list(BATTERY_REQUEST),
            event_0a,
            battery_reply(88, 1),
        ])
        self.assertEqual(outcome.kind, "reading")
        self.assertEqual(outcome.level, 88)
        self.assertTrue(outcome.charging)
        self.assertTrue(outcome.charging_known)
        self.assertEqual(device.writes, [GATE_REQUEST, BATTERY_REQUEST])

    def test_gate_offline_returns_asleep_without_battery_query(self):
        outcome, device = self.run_frames([gate_reply(0)])
        self.assertEqual(outcome.kind, "asleep")
        self.assertIsNone(outcome.level)
        self.assertEqual(device.writes, [GATE_REQUEST])

    def test_gate_silent_or_malformed_never_sends_battery_query(self):
        corrupted_online = gate_reply(1)
        corrupted_online[16] ^= 0x01
        corrupted_offline = gate_reply(0)
        corrupted_offline[16] ^= 0x01
        bad_gates = [
            [],
            gate_reply(1)[:16],
            gate_reply(1) + [0],
            list(GATE_REQUEST),
            corrupted_online,
            corrupted_offline,
            frame17("09 03 00 00 00 01 01"),
            frame17("08 04 00 00 00 01 01"),
            gate_reply(2),
            gate_reply(0xFF),
        ]
        for idx, bad in enumerate(bad_gates):
            with self.subTest(case=idx):
                outcome, device = self.run_frames([bad])
                self.assertIn(outcome.kind, ("silent", "rejected"))
                self.assertEqual(device.writes, [GATE_REQUEST])

    def test_invalid_battery_replies_fail_closed(self):
        corrupted_battery = battery_reply(50, 0)
        corrupted_battery[6] = 51  # stale checksum
        bad_replies = [
            [],
            battery_reply(50, 0)[:16],
            battery_reply(50, 0) + [0],
            list(BATTERY_REQUEST),
            corrupted_battery,
            frame17("09 04 00 00 00 02 32 00"),
            frame17("08 05 00 00 00 02 32 00"),
            battery_reply(101, 0),
            battery_reply(0xFF, 0),
            battery_reply(50, 3),
            battery_reply(50, 0xFF),
        ]
        for idx, bad in enumerate(bad_replies):
            with self.subTest(case=idx):
                outcome, device = self.run_frames([gate_reply(1), bad])
                self.assertIn(outcome.kind, ("silent", "rejected"))
                self.assertIsNone(outcome.level)
                self.assertEqual(device.writes, [GATE_REQUEST, BATTERY_REQUEST])

    def test_provider_selection_and_fail_closed_descriptor_gates(self):
        good_device = FakeDevice([gate_reply(1), battery_reply(91, 0)])
        statuses, _ = self.poll_rows([receiver()], good_device)
        self.assertEqual(len(statuses), 1)
        self.assertEqual(statuses[0].level, 91)
        self.assertFalse(statuses[0].charging)
        self.assertTrue(statuses[0].charging_known)
        self.assertEqual(good_device.writes, [GATE_REQUEST, BATTERY_REQUEST])

        # Sibling FF04:0002 / FF03:0000 / FF05:0000 collections do not cause ambiguity.
        siblings = [
            receiver(),
            receiver(usage_page=0xFF03, usage=0x0000, path=b"dareu-1156-ff03"),
            receiver(usage_page=0xFF04, usage=0x0002, path=b"dareu-1156-ff04"),
            receiver(usage_page=0xFF05, usage=0x0000, path=b"dareu-1156-ff05"),
        ]
        sibling_dev = FakeDevice([gate_reply(1), battery_reply(64, 2)])
        statuses, _ = self.poll_rows(siblings, sibling_dev)
        self.assertEqual(len(statuses), 1)
        self.assertEqual(statuses[0].level, 64)
        self.assertTrue(statuses[0].charging)

        blocked_cases = [
            [receiver(product_id=0x1074)],
            [receiver(product_string="Other Receiver")],
            [receiver(usage_page=0xFF04, usage=0x0002)],
            [receiver(input_report_sizes={"8": 16})],
            [receiver(output_report_sizes={"8": 64})],
            [receiver(input_report_sizes=None)],
            [receiver(output_report_sizes=None)],
            [receiver(physical_id="path:unparented")],
            [receiver(path=b"a"), receiver(path=b"b")],
        ]
        for idx, rows in enumerate(blocked_cases):
            with self.subTest(blocked=idx):
                dev = FakeDevice([gate_reply(1), battery_reply(50, 0)])
                out, _ = self.poll_rows(rows, dev)
                self.assertEqual(out, [])
                self.assertEqual(dev.writes, [])


if __name__ == "__main__":
    unittest.main()
