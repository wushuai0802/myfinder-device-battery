"""Offline Rainy75 V2 contract tests; zero tails below are synthetic padding."""
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

ENGINE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ENGINE))
sys.path.insert(0, str(ENGINE / "tests"))
import _hidstub  # noqa: F401 -- before importing any provider
from providers import rainyrt as R


def pad(frame):
    return list(frame) + [0] * (256 - len(frame))


def info(pid=0x3071, physical="pnp:usb\\vid_36b0&pid_3071\\capture", **changes):
    value = dict(vendor_id=0x36B0, product_id=pid, interface_number=2,
                 product_string="Rainy75 V2 - 2.4G",
                 usage_page=0xFF60, usage=0x0061,
                 input_report_length=257, output_report_length=257,
                 input_report_sizes={"0": 257}, output_report_sizes={"0": 257},
                 physical_id=physical, path=(physical + "-vendor").encode())
    value.update(changes)
    return value


class FakeDevice:
    """Reads only: any write attempt would raise AttributeError, proving passivity."""

    def __init__(self, frames):
        self.frames = list(frames)
        self.closed = False
        self.opened = False

    def open_path(self, path):
        self.opened = True

    def read(self, length, timeout_ms):
        return self.frames.pop(0) if self.frames else b""

    def close(self):
        self.closed = True


class ParseTests(unittest.TestCase):
    def test_accepts_documented_lengths(self):
        for size in (6, 10, 14):
            frame = [0xBE, 0xBB, size, 55, 0] + [0] * (size - 6) + [0xED]
            self.assertEqual(R.parse_battery(pad(frame)), (55, False))

    def test_zero_length_byte_means_six(self):
        self.assertEqual(R.parse_battery(pad([0xBE, 0xBB, 0, 55, 0, 0xED])), (55, False))

    def test_charge_states(self):
        # 0 idle, 1 complete (not charging), 2 charging.
        for raw, charging in ((0, False), (1, False), (2, True)):
            self.assertEqual(R.parse_battery(pad([0xBE, 0xBB, 6, 40, raw, 0xED])),
                             (40, charging))

    def test_rejects_malformed(self):
        for raw in (
                [0xBE, 0xBB, 6, 55, 0],                 # truncated, no terminator
                [0xBE, 0x65, 6, 55, 0, 0xED],           # other command
                [0xBE, 0xBB, 7, 55, 0, 0x00, 0x00],     # unsupported length
                [0xBE, 0xBB, 6, 101, 0, 0xED],          # level out of 0..100
                [0xBE, 0xBB, 6, 55, 3, 0xED],           # unknown charge state
                [0x00, 0xBB, 6, 55, 0, 0xED],           # wrong header
        ):
            self.assertIsNone(R.parse_battery(pad(raw)))


class PollTests(unittest.TestCase):
    def test_passive_frame_becomes_reading(self):
        dev = FakeDevice([pad([0xBE, 0xBB, 6, 97, 0, 0xED])])
        provider = R.RainyRTProvider()
        with patch.object(R.hidlist, "enumerate", lambda vid=0: [info()]), \
                patch.object(R, "hid", type("H", (), {"device": staticmethod(lambda: dev)})):
            statuses = provider.poll()
        self.assertTrue(dev.opened)
        self.assertEqual(len(statuses), 1)
        status = statuses[0]
        self.assertEqual((status.level, status.charging, status.online,
                          status.source, status.kind), (97, False, True, "rainyrt", "keyboard"))
        self.assertTrue(status.connection_known)
        self.assertTrue(status.charging_known)
        self.assertTrue(status.measured_at_utc)
        self.assertEqual(provider.diagnostics(), [])
        provider.close()
        self.assertTrue(dev.closed)

    def test_charging_frame_sets_charging(self):
        dev = FakeDevice([pad([0xBE, 0xBB, 6, 62, 2, 0xED])])
        provider = R.RainyRTProvider()
        with patch.object(R.hidlist, "enumerate", lambda vid=0: [info()]), \
                patch.object(R, "hid", type("H", (), {"device": staticmethod(lambda: dev)})):
            statuses = provider.poll()
        self.assertEqual((statuses[0].level, statuses[0].charging), (62, True))
        provider.close()

    def test_silence_produces_no_new_reading(self):
        dev = FakeDevice([])
        provider = R.RainyRTProvider()
        with patch.object(R.hidlist, "enumerate", lambda vid=0: [info()]), \
                patch.object(R, "hid", type("H", (), {"device": staticmethod(lambda: dev)})):
            self.assertEqual(provider.poll(), [])
        self.assertIn("no battery notification", provider.diagnostics()[-1])
        provider.close()

    def test_ambiguous_or_unproven_collection_is_never_opened(self):
        dev = FakeDevice([])
        provider = R.RainyRTProvider()
        for inventory in (
                [info(), info()],  # duplicate collection on one USB parent
                [info(input_report_sizes={"0": 65}, output_report_sizes={"0": 65},
                      input_report_length=65, output_report_length=65)],
                [info(product_id=0x1234)],
        ):
            with patch.object(R.hidlist, "enumerate", lambda vid=0, i=inventory: list(i)), \
                    patch.object(R, "hid", type("H", (), {"device": staticmethod(lambda: dev)})):
                self.assertEqual(provider.poll(), [])
            self.assertFalse(dev.opened)


if __name__ == "__main__":
    unittest.main()
