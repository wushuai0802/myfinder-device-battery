"""FB3E captures from issues 733 and 802. Every HID operation uses a fake."""
from pathlib import Path
import sys
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub
from providers import pulsar as P

PARENT = r"pnp:usb\vid_3554&pid_fb3e\test"
PATH = rb"\\?\hid#vid_3554&pid_fb3e&mi_01&col02#fb3e-test"
CAPTURES = [
    (90, "08 04 00 00 00 02 5A 00 0F D7 00 00 00 00 00 00 07"),
    (40, "08 04 00 00 00 02 28 00 0E C0 00 00 00 00 00 00 51"),
    (40, "08 04 00 00 00 02 28 00 0E BB 00 00 00 00 00 00 56"),
]


def receiver(**changes):
    row = dict(vendor_id=0x3554, product_id=0xFB3E, physical_id=PARENT,
               path=PATH, interface_number=1, usage_page=0xFF02, usage=2,
               input_report_length=17, output_report_length=17,
               input_report_sizes={"8": 17}, output_report_sizes={"8": 17})
    row.update(changes)
    return row


class Fb3eTests(unittest.TestCase):
    def setUp(self):
        self.provider = P.PulsarProvider()
        self.hid = Mock()
        self.device = self.hid.device.return_value
        self.device.write.return_value = 17
        self.inventory = [receiver()]
        self.enterContext(patch.object(P, "hid", self.hid))
        self.enterContext(patch.object(P.hidlist, "enumerate", side_effect=lambda vid: [
            row for row in self.inventory if row["vendor_id"] == vid]))
        self.enterContext(patch.object(P, "output_length", side_effect=AssertionError("legacy probe")))
        self.enterContext(patch.object(P, "_capture_dpi", side_effect=AssertionError("DPI capture")))

    def read(self, frame=b""):
        self.device.reset_mock()
        self.device.read.side_effect = [b"", frame, b""]
        return self.provider.poll()

    def test_captures_and_sleep_use_the_same_receiver_key(self):
        for level, text in CAPTURES:
            result, = self.read(bytes.fromhex(text))
            self.assertEqual(result.level, level)
            self.assertFalse(result.charging_known)
            self.assertEqual(result.physical_id, PARENT)
            self.assertIsNone(result.dpi_target)
            self.device.open_path.assert_called_once_with(PATH)
            self.device.write.assert_called_once_with(P.make_request())
            self.device.close.assert_called_once()
        old, = self.read()
        self.assertEqual((old.key, old.level, old.online, old.charging_known),
                         (result.key, 40, False, False))
        self.inventory[0] = receiver(physical_id=PARENT.replace("test", "other"))
        self.assertEqual(self.read(), [])

    def test_missing_proof_duplicates_neighbours_and_unresolved_parents_do_not_open(self):
        for rows in ([receiver(interface_number=0)], [receiver(product_id=0xFB3D)],
                     [receiver(physical_id="collection:synthetic")], [receiver(path=b"")],
                     [receiver(usage_page=0xFF01)], [receiver(), receiver()],
                     [receiver(), receiver(usage=1, physical_id="")],
                     [receiver(), receiver(physical_id=PARENT+"2")],
                     [receiver(input_report_sizes={})], [receiver(output_report_sizes={"8": 16})],
                     [receiver(input_report_length=64)], [receiver(output_report_sizes={"8": 17, "9": 17})],
                     [receiver(interface_number=True)], [receiver(input_report_length=17.0)],
                     [receiver(input_report_sizes={"8": 17.0})]):
            with self.subTest(rows=rows):
                self.inventory = rows
                self.assertEqual(self.read(), [])
                self.device.open_path.assert_not_called()
                self.device.write.assert_not_called()

    def test_bad_echo_short_checksum_wrong_command_or_wrong_percentage_never_publish(self):
        frame = bytes.fromhex(CAPTURES[0][1])
        def changed(at, value):
            raw = bytearray(frame)
            raw[at] = value
            raw[-1] = P.checksum(raw[:-1])
            return raw
        for invalid in (P.make_request(), frame[:-1], frame + b"\0", changed(0, 7), changed(1, 10),
                        changed(2, 1), changed(6, 101), frame[:-1] + bytes([frame[-1] ^ 1])):
            with self.subTest(invalid=invalid):
                self.assertEqual(self.read(invalid), [])
                self.device.write.assert_called_once_with(P.make_request())

    def test_short_write_never_accepts_queued_capture(self):
        self.device.write.return_value = 1
        self.assertEqual(self.read(bytes.fromhex(CAPTURES[0][1])), [])
        self.device.write.assert_called_once_with(P.make_request())
        self.device.close.assert_called_once()


FB49_PARENT = r"pnp:usb\vid_3554&pid_fb49\test"
FB49_PATH = rb"\\?\hid#vid_3554&pid_fb49&mi_01&col02#fb49-test"


def fb49_receiver(**changes):
    row = dict(vendor_id=0x3554, product_id=0xFB49, product_string="VGN DragonFly F2 Ultra+",
               physical_id=FB49_PARENT, path=FB49_PATH, interface_number=1,
               usage_page=0xFF02, usage=2, input_report_length=17, output_report_length=17,
               input_report_sizes={"8": 17}, output_report_sizes={"8": 17})
    row.update(changes)
    return row


def fb49_frame(level, length_byte=2, power=0, mv=4055):
    body = [0x08, 0x04, 0x00, 0x00, 0x00, length_byte, level, power, (mv >> 8) & 0xFF, mv & 0xFF] + [0] * 6
    return bytes(body + [P.checksum(body)])


class Fb49Tests(unittest.TestCase):
    """#1217: VGN DragonFly F2 Ultra+ (3554:FB49) official VGN HUB vgn-y2 report-8 path."""

    def setUp(self):
        self.provider = P.PulsarProvider()
        self.hid = Mock()
        self.device = self.hid.device.return_value
        self.device.write.return_value = 17
        self.inventory = [fb49_receiver()]
        self.enterContext(patch.object(P, "hid", self.hid))
        self.enterContext(patch.object(P.hidlist, "enumerate", side_effect=lambda vid: [
            row for row in self.inventory if row["vendor_id"] == vid]))
        self.enterContext(patch.object(P, "output_length", side_effect=AssertionError("legacy probe")))
        self.enterContext(patch.object(P, "_capture_dpi", side_effect=AssertionError("DPI capture")))

    def read(self, frame=b""):
        self.device.reset_mock()
        self.device.read.side_effect = [b"", frame, b""]
        return self.provider.poll()

    def test_accepts_len2_and_len4_vgn_y2_replies_and_retains_on_sleep(self):
        for level, length_byte in ((90, 2), (76, 4)):
            result, = self.read(fb49_frame(level, length_byte=length_byte))
            self.assertEqual((result.name, result.level, result.charging_known, result.physical_id),
                             ("VGN DragonFly F2 Ultra+", level, False, FB49_PARENT))
            self.assertIsNone(result.dpi_target)
            self.device.open_path.assert_called_once_with(FB49_PATH)
            self.device.write.assert_called_once_with(P.make_request())
        old, = self.read()
        self.assertEqual((old.level, old.online, old.charging_known), (76, False, False))

    def test_zero_send_and_frame_validation_gates(self):
        for rows in ([fb49_receiver(interface_number=0)],
                     [fb49_receiver(physical_id="collection:synthetic")],
                     [fb49_receiver(), fb49_receiver()],
                     [fb49_receiver(input_report_sizes={})],
                     [fb49_receiver(output_report_sizes={"8": 16})]):
            with self.subTest(rows=rows):
                self.inventory = rows
                self.assertEqual(self.read(), [])
                self.device.open_path.assert_not_called()
        self.inventory = [fb49_receiver()]
        for invalid in (bytes(P.make_request()), fb49_frame(101), fb49_frame(80, length_byte=3),
                        fb49_frame(80)[:-1] + bytes([fb49_frame(80)[-1] ^ 1])):
            with self.subTest(invalid=invalid):
                self.assertEqual(self.read(invalid), [])
