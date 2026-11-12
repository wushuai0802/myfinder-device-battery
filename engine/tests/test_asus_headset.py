"""ROG Delta II fixtures from recorded GearLink facts; no hardware or vendor code."""
from contextlib import contextmanager
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
from providers import asus_headset as A
from test_new_brands import FakeHid

P = A.AsusHeadsetProvider
BATTERY_REQUEST = [0xCC, 0x12, 0x07] + [0] * 61
CHARGING_REQUEST = [0xCC, 0x12, 0x08] + [0] * 61


def entry(physical="dongle-a", **changes):
    info = dict(vendor_id=0x0B05, product_id=0x1AFA, usage_page=0xFF00, usage=1,
                input_report_length=64, output_report_length=64,
                input_report_sizes={"204": 64}, output_report_sizes={"204": 64},
                input_report_proof="caps", output_report_proof="caps",
                path=(physical + "-ff00").encode(), physical_id=physical,
                product_string="ROG DELTA II (2.4GHz)", serial_number="same-serial")
    info.update(changes)
    return info


def battery(level=87, length=64, timer=3):
    return [0xCC, 0x12, 0x07, 0x00, 0x00, timer, level] + [0] * (length - 7)


def charging(flag=0, length=64):
    return [0xCC, 0x12, 0x08, 0x00, 0x00, flag] + [0] * (length - 6)


def other(key=0x09, value=50, length=64):
    return [0xCC, 0x12, key, 0x00, 0x00, value] + [0] * (length - 6)


@contextmanager
def fixture(frames=(), inventory=None, **kwargs):
    bus = FakeHid(frames, **kwargs)
    infos = [entry()] if inventory is None else inventory
    with patch.object(A, "hid", bus), \
            patch.object(A.hidlist, "enumerate", side_effect=lambda vid=0: [
                d for d in infos if not vid or d["vendor_id"] == vid]), \
            patch.object(A, "time", SimpleNamespace(monotonic=lambda: bus.now)):
        yield bus


class ParseTests(unittest.TestCase):
    def test_battery_percentage_is_byte_six_and_zero_is_representable(self):
        self.assertEqual(A.parse_battery(battery(87), 64), 87)
        self.assertEqual(A.parse_battery(battery(0), 64), 0)
        self.assertEqual(A.parse_battery(battery(100), 64), 100)
        self.assertEqual(A.parse_battery(battery(88, timer=0), 64), 88)

    def test_charging_byte_five_is_one_or_zero(self):
        self.assertIs(A.parse_charging(charging(1), 64), True)
        self.assertIs(A.parse_charging(charging(0), 64), False)

    def test_other_keys_lengths_and_ranges_are_not_readings(self):
        self.assertIsNone(A.parse_battery(other(0x09), 64))
        self.assertIsNone(A.parse_battery(charging(1), 64))
        self.assertIsNone(A.parse_battery(battery(87)[:6], 64))
        self.assertIsNone(A.parse_battery(battery(101), 64))
        self.assertIsNone(A.parse_battery(battery(255), 64))
        self.assertIsNone(A.parse_charging(battery(87), 64))
        self.assertIsNone(A.parse_charging(charging(1)[:5], 64))
        self.assertIsNone(A.parse_battery([0] * 64, 64))
        self.assertIsNone(A.parse_charging([0] * 64, 64))

    def test_only_the_two_get_frames_can_be_built(self):
        self.assertEqual(A.make_request(0x07, 5), [0xCC, 0x12, 0x07, 0, 0])
        self.assertEqual(len(A.make_request(0x08, 64)), 64)
        for key in (0x00, 0x06, 0x09, 0x51, 0xFF):
            with self.subTest(key=key), self.assertRaises(ValueError):
                A.make_request(key, 64)
        for length in (4, 4097, None, True, 5.0):
            with self.subTest(length=length), self.assertRaises(ValueError):
                A.make_request(0x07, length)


class PollTests(unittest.TestCase):
    def test_battery_then_charging_with_exactly_two_writes(self):
        for level in (1, 50, 87, 100):
            for flag in (0, 1):
                with self.subTest(level=level, flag=flag), fixture(
                        [battery(level), charging(flag)]) as bus:
                    status, = P().poll()
                self.assertEqual((status.name, status.level, status.charging, status.kind),
                                 ("ROG Delta II", level, bool(flag), "headset"))
                self.assertEqual(status.source, "asus_headset")
                self.assertTrue(status.online and status.connection_known)
                self.assertTrue(status.charging_known)
                self.assertEqual(status.verification_level, "C")
                self.assertEqual(status.approx, "")
                self.assertEqual(bus.writes, [(entry()["path"], BATTERY_REQUEST),
                                              (entry()["path"], CHARGING_REQUEST)])
                self.assertEqual(bus.reads[0][0], 64)
                self.assertEqual(bus.opened, bus.closed)

    def test_unrelated_reports_are_skipped_before_each_answer(self):
        frames = [other(0x09, 50), [0x01, 0x02, 0x03], other(0x00, 1),
                  battery(64), other(0x2D, 0), charging(1)]
        with fixture(frames) as bus:
            status, = P().poll()
        self.assertEqual((status.level, status.charging), (64, True))
        self.assertEqual([w[1] for w in bus.writes], [BATTERY_REQUEST, CHARGING_REQUEST])

    def test_missing_charging_reply_leaves_charging_unknown(self):
        provider = P()
        with fixture([battery(55)]) as bus:
            status, = provider.poll()
        self.assertEqual((status.level, status.charging, status.charging_known), (55, False, False))
        self.assertIn("DELTA II charging state unavailable", provider.diagnostics())
        self.assertEqual(len(bus.writes), 2)

    def test_zero_percent_is_standby_unless_charging(self):
        with fixture([battery(0), charging(0)]) as bus:
            self.assertEqual(P().poll(), [])
        self.assertEqual(len(bus.writes), 2)
        provider = P()
        with fixture([battery(0), charging(0)]):
            provider.poll()
        self.assertIn("DELTA II zero battery; no new reading", provider.diagnostics())
        with fixture([battery(0), charging(1)]):
            status, = P().poll()
        self.assertEqual((status.level, status.charging), (0, True))

    def test_all_zero_battery_payload_is_not_a_charged_empty_battery(self):
        echo = [0xCC, 0x12, 0x07] + [0] * 61
        provider = P()
        with fixture([echo, charging(1)]) as bus:
            self.assertEqual(provider.poll(), [])
        self.assertEqual(len(bus.writes), 1)
        self.assertIn("DELTA II empty reply or asleep; no new reading", provider.diagnostics())
        # A zero charging flag after the header is a valid "not charging" answer.
        with fixture([battery(50), charging(0)]):
            status, = P().poll()
        self.assertEqual((status.level, status.charging, status.charging_known),
                         (50, False, True))

    def test_out_of_range_or_wrong_length_never_becomes_a_reading(self):
        for frames in ([battery(101)], [battery(255)], [battery(87, length=6)],
                       [[0] * 64], [[0xCC, 0xFF, 0xAA] + [0] * 61]):
            with self.subTest(frames=frames):
                provider = P()
                with fixture(frames) as bus:
                    self.assertEqual(provider.poll(), [])
                self.assertEqual(len(bus.writes), 1)
                self.assertEqual(bus.opened, bus.closed)

    def test_silence_and_continuous_reports_stay_bounded(self):
        for frames, read_ms in (([], 10), ([other(0x09)] * 100, 10), ([other(0x09)] * 100, 0)):
            with self.subTest(read_ms=read_ms), fixture(frames, read_ms=read_ms) as bus:
                provider = P()
                self.assertEqual(provider.poll(), [])
            self.assertLessEqual(bus.now, A.QUERY_SECONDS + 0.001)
            self.assertLessEqual(len(bus.reads), 2 * A.MAX_READS)
            self.assertEqual(len(bus.writes), 1)
            self.assertIn("DELTA II no battery echo within read limit", provider.diagnostics())

    def test_open_write_and_read_failures_close_without_new_readings(self):
        for stage, summary in (("open", "DELTA II open failed"),
                               ("write", "DELTA II query write failed; no retry"),
                               ("read", "DELTA II input read failed")):
            provider = P()
            with self.subTest(stage=stage), fixture([battery(87)], error_at=stage) as bus:
                self.assertEqual(provider.poll(), [])
            self.assertEqual(bus.opened, bus.closed)
            self.assertLessEqual(len(bus.writes), 1)
            self.assertIn(summary, provider.diagnostics())
            self.assertNotIn("private-path", " ".join(provider.diagnostics()))

    def test_short_write_is_not_retried(self):
        provider = P()
        with fixture([battery(87)], write_result=63) as bus:
            self.assertEqual(provider.poll(), [])
        self.assertEqual(len(bus.writes), 1)
        self.assertEqual(bus.reads, [])
        self.assertIn("DELTA II query write failed; no retry", provider.diagnostics())

    def test_unproven_or_incompatible_reports_never_open(self):
        changes = [dict(product_id=pid) for pid in (0x1ACE, 0x1D41, 0x1E6B, 0x1B82, 0x1B84)]
        changes += [dict(vendor_id=0x0B06), dict(usage_page=0xFF13), dict(usage_page=0x000C),
                    dict(usage=2), dict(path=b""), dict(physical_id=""),
                    dict(physical_id="collection:dongle")]
        changes += [dict(input_report_length=n) for n in (None, True, 6, 4097)]
        changes += [dict(output_report_length=n) for n in (None, True, 4, 4097)]
        for direction in ("input", "output"):
            changes += [{direction + "_report_sizes": sizes} for sizes in
                        (None, {}, {"203": 64}, {"204": 63}, {"204": True},
                         {"204": 64, "205": 65})]
            changes += [{direction + "_report_proof": proof}
                        for proof in (None, "", "failed", "caps ")]
        for change in changes:
            with self.subTest(change=change), fixture([battery(87), charging(1)],
                                                      [entry(**change)]) as bus:
                self.assertEqual(P().poll(), [])
            self.assertEqual(bus.opened, [])
            self.assertEqual(bus.writes, [])

    def test_ambiguous_target_collection_never_opens(self):
        infos = [entry(), entry(path=b"dongle-a-second")]
        with fixture([battery(87), charging(1)], infos) as bus:
            self.assertEqual(P().poll(), [])
        self.assertEqual(bus.opened, [])
        self.assertEqual(bus.writes, [])

    def test_other_collections_do_not_block_the_single_target(self):
        infos = [entry(usage_page=0xFF13, path=b"dongle-a-ff13"),
                 entry(usage_page=0x000C, path=b"dongle-a-000c"), entry()]
        with fixture([battery(72), charging(0)], infos) as bus:
            status, = P().poll()
        self.assertEqual(status.level, 72)
        self.assertEqual(bus.opened, [entry()["path"]])
        self.assertEqual(bus.writes, [(entry()["path"], BATTERY_REQUEST),
                                      (entry()["path"], CHARGING_REQUEST)])

    def test_same_model_dongles_stay_separate_despite_shared_serial(self):
        infos = [entry("one"), entry("two")]
        with fixture([battery(20), charging(0)], infos):
            first = P().poll()
        with fixture([battery(90), charging(1)], list(reversed(infos))):
            second = P().poll()
        self.assertEqual([s.level for s in first], [20])
        self.assertEqual([s.level for s in second], [90])
        self.assertEqual(len({s.key for s in first + second}), 2)

    def test_unrelated_asus_collections_are_ignored_silently(self):
        infos = [entry(product_id=0x1ACE, path=b"omni"), entry(product_id=0x1A72, path=b"mouse")]
        provider = P()
        with fixture([battery(87), charging(1)], infos) as bus:
            self.assertEqual(provider.poll(), [])
        self.assertEqual(bus.opened, [])
        self.assertEqual(bus.writes, [])
        self.assertEqual(provider.diagnostics(), [])


if __name__ == "__main__":
    unittest.main()

