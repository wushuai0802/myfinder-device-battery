"""Synthetic HID around OpenMouse's V3 fixtures and captured Ultra+ data block.

The input framing is reconstructed from the pinned codec, not a Pro capture.
No reference JavaScript is imported/executed and no physical HID is opened.
"""
from contextlib import contextmanager
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
from _feedback import feedback_inventory
from providers import mchose as M


# src/drivers/mchose/v3-hid.test.ts: 87%, discharging; 14-byte device info.
DISCHARGING = bytes.fromhex(
    "4d 01 01 0e 00 09 00 00 37 38 33 40 03 01 00 04 20 00 01 00 57 02 08"
).ljust(64, b"\x00")
# docs/mchose-protocol.md: captured Ultra+ payload, 85%, charging. Its 4026
# reply PID is NOT a model name, and the extra two payload bytes are retained.
CHARGING = bytes.fromhex(
    "4d 01 01 10 00 09 00 00 37 38 26 40 04 00 00 00 00 10 02 01 55 00 08 e4 df"
).ljust(64, b"\x00")
# #276's 24-byte prefix plus the reporter's stated XOR A1; zero padding is
# reconstructed for the 64-byte transport, not a complete captured report.
ULTRA_42 = bytes.fromhex(
    "4d 01 01 10 00 09 00 00 37 38 26 40 04 00 00 00 00 10 02 00 2a 00 08 e4 a1"
).ljust(64, b"\x00")
REQUEST = bytes.fromhex("4d 01 01 00 00 09 00 00 08").ljust(64, b"\x00")
BUSY = bytes.fromhex("4d 01 01 01 00 09 00 00 ff f6").ljust(64, b"\x00")
REFUSAL = bytes.fromhex("4d 01 00 00 00 00 00 ff").ljust(64, b"\x00")


def edited(frame=DISCHARGING, **fields):
    data = bytearray(frame)
    for offset, value in fields.items():
        data[int(offset)] = value
    if data[2] == 1 and 8 + data[3] < len(data):
        checksum = 0
        for byte in data[2:8 + data[3]]:
            checksum ^= byte
        data[8 + data[3]] = checksum
    return bytes(data)


def inventory(pid=0x1018):
    return [dict(d, product_id=pid, input_report_length=64, output_report_length=64,
                 input_report_sizes={"77": 64}, output_report_sizes={"77": 64},
                 input_report_proof="caps", output_report_proof="caps")
            for d in feedback_inventory(24)
            if (d["vendor_id"], d["product_id"]) == (0x3837, 0x1018)]


class FakeHid:
    def __init__(self, replies, fail=None):
        self.replies = list(replies)
        self.fail = fail
        self.opened, self.sent, self.reads = [], [], []
        self.closed = 0

    def open_path(self, path):
        self.opened.append(path)
        if self.fail == "open":
            raise OSError("open failed")

    def write(self, frame):
        self.sent.append(bytes(frame))
        if self.fail == "write":
            raise OSError("write failed")
        return 63 if self.fail == "short" else len(frame)

    def read(self, size, timeout):
        self.reads.append((size, timeout))
        if self.fail == "read":
            raise OSError("read failed")
        return self.replies.pop(0) if self.replies else []

    def close(self):
        self.closed += 1

    def send_feature_report(self, frame):
        raise AssertionError("V3 must never send V2/A5 feature reports")

    def get_feature_report(self, *args):
        raise AssertionError("V3 must never read V2/A5 feature reports")


@contextmanager
def fixture(replies=(), infos=None, fail=None):
    infos = inventory() if infos is None else infos
    bus = FakeHid(replies, fail)
    with patch.object(M.hidlist, "enumerate", side_effect=lambda vid: [
            d for d in infos if d["vendor_id"] == vid]), \
            patch.object(M.hid, "device", return_value=bus), \
            patch.object(M.MchoseProvider, "_read_collection", side_effect=AssertionError("no V2")), \
            patch.object(M.MchoseProvider, "_read_g7", side_effect=AssertionError("no G7")), \
            patch.object(M.MchoseProvider, "_read_a5", side_effect=AssertionError("no A5")):
        yield bus


class V3ProtocolTests(unittest.TestCase):
    def test_1014_user_reply_uses_only_v3_and_the_product_name(self):
        infos = [dict(vendor_id=0x3837, product_id=0x1014, path=f"collection-{n}".encode(),
                      usage_page=page, usage=usage, product_string="generic receiver",
                      input_report_length=64, output_report_length=64,
                      input_report_sizes={"77": 64}, output_report_sizes={"77": 64})
                 for n, (page, usage) in enumerate(((1, 2), (1, 6), (12, 1),
                                                    (0xFF0B, 0xC4), (0xFF01, 1)))]
        self.assertEqual(M.parse_v3(ULTRA_42), (42, False))
        bad_xor = bytearray(ULTRA_42)
        bad_xor[24] ^= 1
        invalid = (bytes(bad_xor), edited(ULTRA_42, **{"20": 101}),
                   edited(ULTRA_42, **{"3": 56}), ULTRA_42[:24], b"")
        for frame in (ULTRA_42,) + invalid:
            with self.subTest(frame=frame), fixture([frame], infos) as bus:
                provider = M.MchoseProvider()
                result = provider.poll()
                self.assertEqual(bus.sent, [REQUEST])
                self.assertEqual(bus.opened, [b"collection-4"])
                self.assertIn("C / 待实机验证", " ".join(provider.diagnostics()))
                self.assertNotIn("#276", " ".join(provider.reader_diagnostics()))
                if frame == ULTRA_42:
                    device, = result
                    self.assertEqual((device.name, device.level, device.charging),
                                     ("generic receiver", 42, False))
                else:
                    self.assertEqual(result, [])

    def test_both_v3_ids_require_report_proof_and_explain_refusals(self):
        for pid in (0x1018, 0x1014):
            for direction in ("input", "output"):
                for sizes in (None, {}, {"76": 64}, {"77": 63}, {"77": 64.0},
                              {"77": 64, "78": True}, {"77": 64, "78": 65}):
                    infos = [dict(d, **{direction + "_report_sizes": sizes,
                                       direction + "_report_proof": "failed",
                                       "report_descriptor_error": "get_report_descriptor unavailable"})
                             for d in inventory(pid)]
                    with self.subTest(pid=pid, direction=direction, sizes=sizes), fixture([], infos) as bus:
                        provider = M.MchoseProvider()
                        self.assertEqual(provider.poll(), [])
                        self.assertEqual((bus.opened, bus.sent), ([], []))
                        diagnostic = " ".join(provider.reader_diagnostics())
                        self.assertIn(direction + " report 4D/64 length unproven", diagnostic)
                        self.assertIn("proof=caps+descriptor failed", diagnostic)
                        self.assertIn("descriptor=get_report_descriptor unavailable", diagnostic)
                        self.assertIn("not opened; nothing sent", diagnostic)

    def test_v3_ambiguous_collection_and_missing_path_never_open(self):
        info = next(d for d in inventory(0x1014) if d["usage_page"] == 0xFF01)
        for infos, reason in (([info, dict(info, path=b"duplicate")], "ambiguous"),
                              ([dict(info, path=b"")], "path unresolved")):
            with self.subTest(reason=reason), fixture([], infos) as bus:
                provider = M.MchoseProvider()
                self.assertEqual(provider.poll(), [])
                self.assertIn(reason, " ".join(provider.reader_diagnostics()))
                self.assertEqual((bus.opened, bus.sent), ([], []))

    def test_request_and_upstream_power_vectors(self):
        self.assertEqual(M.make_v3_request(), REQUEST)
        self.assertEqual(M.parse_v3(DISCHARGING), (87, False))
        self.assertEqual(M.parse_v3(CHARGING), (85, True))
        for level in (0, 100):
            self.assertEqual(M.parse_v3(edited(**{"20": level})), (level, False))

    def test_checksum_free_reply_and_nonzero_charging(self):
        self.assertEqual(M.parse_v3(edited(**{"2": 0})), (87, False))
        self.assertEqual(M.parse_v3(edited(**{"19": 2})), (87, True))

    def test_invalid_frames_and_disconnected_mouse_are_not_readings(self):
        broken_checksum = bytearray(DISCHARGING)
        broken_checksum[22] ^= 1
        cases = [b"", bytes(64), DISCHARGING[:63], DISCHARGING + b"\0",
                 DISCHARGING[1:], bytes(broken_checksum), REQUEST, BUSY, REFUSAL]
        cases += [edited(**{str(offset): value}) for offset, value in (
            (0, 0x11), (1, 2), (2, 2), (3, 13), (3, 56), (3, 255),
            (4, 1), (5, 0), (8, 0x53), (9, 0x52), (18, 0), (20, 101))]
        for frame in cases:
            with self.subTest(frame=frame.hex()):
                self.assertIsNone(M.parse_v3(frame))

    def test_only_power_is_sent_to_the_exact_collection(self):
        for reply, expected in ((DISCHARGING, (87, False)), (CHARGING, (85, True))):
            with self.subTest(expected=expected), fixture([reply]) as bus:
                provider = M.MchoseProvider()
                device, = provider.poll()
                self.assertEqual((device.level, device.charging), expected)
                self.assertEqual((device.name, device.online, device.source, device.kind),
                                 ("MCHOSE A7 V3 Pro", True, "mchose", "mouse"))
                self.assertIsNone(device.dpi)
                self.assertEqual(bus.sent, [REQUEST])
                self.assertEqual(bus.opened, [next(d["path"] for d in inventory()
                                                  if d["usage_page"] == 0xFF01)])
                self.assertEqual(bus.closed, 1)
                self.assertTrue(all(size == 64 and 0 < timeout <= 1500 for size, timeout in bus.reads))
                self.assertIn("待实机验证", "\n".join(provider.diagnostics()))

    def test_names_use_sibling_strings_and_never_reply_pid(self):
        for pid, product, fallback in (
                (0x1014, "MCHOSE K7 V2 Ultra+", "MCHOSE A7 V3 Ultra+"),
                (0x1018, "MCHOSE A5 V3 Ultra+(3955)", "MCHOSE V3 8K 接收器")):
            infos = inventory(pid)
            infos[0]["product_string"] = " " + product + " "
            for info in infos[1:]:
                info["product_string"] = " "
            with self.subTest(pid=pid, product=product), fixture([CHARGING], infos) as bus:
                provider = M.MchoseProvider()
                self.assertEqual(provider.poll()[0].name, product)
                self.assertEqual(bus.sent, [REQUEST])
                self.assertNotIn(product, " ".join(provider.reader_diagnostics()))
                self.assertNotIn("#276", " ".join(provider.reader_diagnostics()))
            for blank in ("", "   ", None):
                for info in infos:
                    info["product_string"] = blank
                with self.subTest(pid=pid, blank=blank), fixture([CHARGING], infos) as bus:
                    self.assertEqual(M.MchoseProvider().poll()[0].name, fallback)
                    self.assertEqual(bus.sent, [REQUEST])

    def test_unknown_or_wrong_lengths_never_open_or_send(self):
        for field in ("input_report_length", "output_report_length"):
            for length in (None, 0, 63, 65, 128):
                infos = [dict(d, **{field: length}) for d in inventory()]
                with self.subTest(field=field, length=length), fixture([CHARGING], infos) as bus:
                    self.assertEqual(M.MchoseProvider().poll(), [])
                    self.assertEqual((bus.opened, bus.sent), ([], []))

    def test_wrong_collection_or_vendor_never_uses_v3(self):
        for changes in ({"usage_page": 0xFF0B}, {"usage": 2}, {"vendor_id": 0x9999}):
            infos = [dict(d, **changes) for d in inventory()]
            with self.subTest(changes=changes), fixture([CHARGING], infos) as bus:
                self.assertEqual(M.MchoseProvider().poll(), [])
                self.assertEqual((bus.opened, bus.sent), ([], []))

    def test_bad_or_unrelated_input_can_precede_valid_answer(self):
        with fixture([bytes(64), edited(**{"4": 0xF0}), CHARGING]) as bus:
            self.assertEqual(M.MchoseProvider().poll()[0].level, 85)
            self.assertEqual(bus.sent, [REQUEST])
            self.assertEqual(len(bus.reads), 3)

    def test_invalid_reply_busy_refusal_and_silence_never_try_v2_or_siblings(self):
        for frames in ([], [BUSY], [REFUSAL], [REQUEST], [bytes(64)],
                       [edited(**{"18": 0})], [edited(**{"20": 101})]):
            with self.subTest(frames=frames), fixture(frames) as bus:
                self.assertEqual(M.MchoseProvider().poll(), [])
                self.assertEqual(bus.sent, [REQUEST])
                self.assertEqual(len(bus.opened), 1)
                self.assertEqual(bus.closed, 1)

    def test_io_errors_and_short_writes_close_without_fallback(self):
        for fail in ("open", "write", "short", "read"):
            with self.subTest(fail=fail), fixture([CHARGING], fail=fail) as bus:
                self.assertEqual(M.MchoseProvider().poll(), [])
                self.assertEqual(bus.closed, 1)
                self.assertEqual(bus.sent, [] if fail == "open" else [REQUEST])
                if fail != "read":
                    self.assertEqual(bus.reads, [])

    def test_input_noise_has_a_read_limit_and_a_deadline(self):
        with fixture([bytes(64)] * 100) as bus, patch.object(M.time, "monotonic", return_value=1):
            self.assertEqual(M.MchoseProvider().poll(), [])
            self.assertEqual(len(bus.reads), 32)
            self.assertEqual(bus.sent, [REQUEST])
        with fixture([bytes(64)] * 100) as bus, \
                patch.object(M.time, "monotonic", side_effect=[0, 0.5, 1.6]):
            self.assertEqual(M.MchoseProvider().poll(), [])
            self.assertEqual(bus.reads, [(64, 1000)])

    def test_silence_keeps_only_offline_cache_and_lost_caps_drop_it(self):
        with fixture([CHARGING]) as bus:
            provider = M.MchoseProvider()
            self.assertTrue(provider.poll()[0].online)
            cached, = provider.poll()
            self.assertEqual((cached.level, cached.online), (85, False))
            self.assertEqual(bus.sent, [REQUEST, REQUEST])
        infos = [dict(d, output_report_length=None) for d in inventory()]
        with fixture([], infos) as bus:
            self.assertEqual(provider.poll(), [])
            self.assertEqual(bus.sent, [])

    def test_v3_and_v2_have_separate_keys(self):
        self.assertEqual(len({M.device_key(0x3837, pid) for pid in (0x1018, 0x1014, 0x100B)}), 3)


if __name__ == "__main__":
    unittest.main()
