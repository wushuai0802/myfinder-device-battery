from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
from providers import blackshark_hyperspeed as helper
from providers import razer


def receiver(parent="receiver-a", **changes):
    row = {
        "vendor_id": 0x1532,
        "product_id": 0x0565,
        "product_string": "Razer BlackShark V2 HS 2.4",
        "usage_page": 0xFF14,
        "usage": 1,
        "interface_number": 3,
        "path": br"\\?\hid#vid_1532&pid_0565&mi_03&col01#" + parent.encode(),
        "physical_id": f"pnp:usb\\vid_1532&pid_0565\\{parent}",
        "input_report_length": 64, "output_report_length": 64,
        "input_report_proof": "caps", "output_report_proof": "caps",
        "input_report_sizes": {"2": 64},
        "output_report_sizes": {"2": 64},
    }
    row.update(changes)
    return row


def reply(sequence, percent):
    frame = bytearray(64)
    frame[0:3] = (2, 2, 0x60 | (sequence & 0x1F))
    frame[6] = 5
    frame[8:14] = (0x80, 0x80, 0x21, 1, 1, percent)
    frame[62] = helper._xor(frame[1:62])
    return bytes(frame)


class FakeReader:
    def __init__(self, replies):
        self.replies = list(replies)
        self.opened = []
        self.reads = []
        self.closed = False

    def open_path(self, path):
        self.opened.append(path)

    def read(self, length, timeout):
        self.reads.append((length, timeout))
        return self.replies.pop(0) if self.replies else b""

    def close(self):
        self.closed = True


class BlackSharkHyperSpeedTests(unittest.TestCase):
    def setUp(self):
        helper._sequence = 0

    def test_request_and_bounded_reply_listening(self):
        reader = FakeReader([bytes(64)] * 7 + [reply(0, 0)])
        events = []

        def send(path, frame):
            events.append(("send", path, frame))
            return True

        result = helper.poll_devices(
            [receiver()],
            device_factory=lambda: reader,
            send_output=send,
            clock=lambda: 0.0,
        )

        self.assertEqual([item.level for item in result], [0])
        self.assertTrue(reader.closed)
        self.assertEqual(len(reader.reads), 8)
        self.assertEqual(events[0][0], "send")
        frame = events[0][2]
        self.assertEqual(len(frame), 64)
        self.assertEqual(frame[0], 2)
        self.assertEqual(frame[2], 0x60)
        self.assertEqual(frame[6], 4)
        self.assertEqual(frame[9:11], bytes((0x80, 0x21)))
        self.assertEqual(frame[62], helper._xor(frame[:62]))

    def test_rejection_gates_do_not_open_or_write(self):
        rows = [
            receiver(product_string="Razer BlackShark V3"),
            receiver(physical_id=""),
            receiver(output_report_sizes={"2": 65}),
            receiver(product_id=0x056E),
            receiver(),
            receiver(product_string="wrong"),
        ]
        reader = FakeReader([reply(0, 50)])
        sent = []

        result = helper.poll_devices(
            rows,
            device_factory=lambda: reader,
            send_output=lambda path, frame: sent.append(frame) or True,
            clock=lambda: 0.0,
        )

        self.assertEqual(result, [])
        self.assertEqual(reader.opened, [])
        self.assertEqual(sent, [])

    def test_identity_and_each_direction_individually_fail_closed(self):
        cases = [
            [receiver(product_string="BlackShark V2 Pro")], [receiver(product_id=0x0577)],
            [receiver(interface_number=5)], [receiver(usage_page=0xFF13)],
            [receiver(path=b"invalid")], [receiver(output_report_length=63)],
            [receiver(input_report_sizes=None)], [receiver(output_report_proof="failed")],
            [receiver(report_descriptor_error="collection maximum mismatch")],
            [receiver(), receiver(physical_id="collection:unresolved")],
        ]
        for rows in cases:
            with self.subTest(rows=rows):
                self.assertEqual(helper.poll_devices(rows, device_factory=lambda: self.fail("unexpected HID open")), [])

    def test_stale_echo_status_checksum_and_range_do_not_create_readings(self):
        good = reply(6, 74)
        self.assertEqual(helper.parse_reply(good, 6), 74)
        self.assertEqual(helper.parse_reply(reply(6, 100), 6), 100)
        for frame in (good[:63], helper.build_request(6), reply(5, 74), reply(6, 101), reply(6, 255)):
            self.assertIsNone(helper.parse_reply(frame, 6))
        for index, bad in ((0, 3), (1, 4), (6, 0), (8, 0), (9, 0), (10, 0x2A), (11, 0), (12, 0)):
            changed = bytearray(good)
            changed[index] = bad
            changed[62] = helper._xor(changed[1:62])
            self.assertIsNone(helper.parse_reply(changed, 6))
        changed = bytearray(good)
        changed[62] ^= 0x80
        self.assertIsNone(helper.parse_reply(changed, 6))

    def test_normal_provider_reserves_pid_even_when_proof_fails(self):
        rows = [receiver(input_report_sizes=None)]
        with patch.object(razer.hidlist, "enumerate", return_value=rows), \
                patch.object(razer.hid, "device", side_effect=AssertionError("generic HID open")):
            self.assertEqual(razer.RazerProvider().poll(), [])

    def test_distinct_physical_parents_remain_independent(self):
        readers = iter((FakeReader([reply(0, 10)]), FakeReader([reply(1, 90)])))
        sent = []

        result = helper.poll_devices(
            [receiver("receiver-a"), receiver("receiver-b")],
            device_factory=lambda: next(readers),
            send_output=lambda path, frame: sent.append(frame) or True,
            clock=lambda: 0.0,
        )

        self.assertEqual([item.level for item in result], [10, 90])
        self.assertEqual([frame[2] & 0x1F for frame in sent], [0, 1])
