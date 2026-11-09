"""Exact CHERRY receiver fixtures; no physical device access."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from providers import cherry as C
from test_new_brands import entry, fixture


def inventory(**changes):
    values = dict(interface_number=1, product_string="MX 2.0S Pro Dongle")
    values.update(changes)
    return [entry(C.CherryProvider, **values)]


def reply(level=58, charging=0):
    frame = C.make_request()
    frame[8] = level
    frame[9] = charging
    return frame


class CherryTests(unittest.TestCase):
    def poll(self, frames, infos=None, **kwargs):
        return fixture(C.CherryProvider, frames, inventory() if infos is None else infos, **kwargs)

    def test_local_58_percent_fixture_and_exact_query(self):
        for level in (1, 58, 100):
            with self.poll([reply(level)]) as bus:
                status, = C.CherryProvider().poll()
                self.assertEqual((status.level, status.kind, status.name),
                                 (level, "keyboard", "CHERRY MX 2.0S WL Pro"))
                self.assertTrue(status.connection_known)
                self.assertTrue(status.charging_known)
                self.assertFalse(status.charging)
                self.assertIsNone(status.dpi)
                self.assertEqual(bus.writes, [(inventory()[0]["path"], C.make_request())])
                self.assertEqual(bus.opened, bus.closed)

    def test_only_verified_identity_and_report_caps_are_opened(self):
        for changes in (dict(vendor_id=0), dict(product_id=0x01AC),
                        dict(product_id=0x00EA), dict(product_id=0x706E),
                        dict(interface_number=0), dict(product_string="Other CHERRY"),
                        dict(usage_page=1), dict(usage=6), dict(path=b""),
                        dict(physical_id="collection:unknown"),
                        dict(input_report_sizes={"4": 64, "5": 32}),
                        dict(output_report_sizes={"4": 64, "5": 32}),
                        dict(input_report_length=None), dict(output_report_length=65),
                        dict(input_report_sizes=None), dict(output_report_sizes={"5": 64}),
                        dict(input_report_sizes={"4": 63})):
            with self.subTest(changes=changes), self.poll([reply()], inventory(**changes)) as bus:
                self.assertEqual(C.CherryProvider().poll(), [])
                self.assertEqual(bus.opened, [])
                self.assertEqual(bus.writes, [])
        duplicate = inventory() + inventory(path=b"other-management-collection")
        with self.poll([reply()], duplicate) as bus:
            self.assertEqual(C.CherryProvider().poll(), [])
            self.assertEqual(bus.writes, [])

    def test_zero_echo_errors_and_unrelated_frames_stay_unknown(self):
        invalid = [[], reply()[:-1], reply() + [0], C.make_request(),
                   reply(0), reply(101), reply(255)]
        for offset, value in ((0, 5), (1, 0), (2, 1), (3, 0x19), (4, 7),
                              (7, 1), (7, 0xFE), (7, 0xFF)):
            frame = reply()
            frame[offset] = value
            invalid.append(frame)
        for frame in invalid:
            with self.subTest(frame=frame), self.poll([frame]) as bus:
                self.assertEqual(C.CherryProvider().poll(), [])
                self.assertEqual(len(bus.writes), 1)
                self.assertEqual(bus.opened, bus.closed)

    def test_wired_and_wireless_cable_state_fixtures(self):
        for pid, level, flag in ((0x01E0, 55, 0), (0x01E0, 58, 1), (0x01DE, 63, 1)):
            infos = inventory(product_id=pid, product_string=C.PRODUCTS[pid])
            with self.subTest(pid=pid, flag=flag), self.poll([reply(level, flag)], infos) as bus:
                status, = C.CherryProvider().poll()
                self.assertEqual((status.level, status.charging, status.charging_known),
                                 (level, bool(flag), True))
                self.assertEqual(bus.writes, [(infos[0]["path"], C.make_request())])
        for flag in (2, 255):
            with self.poll([reply(charging=flag)]):
                self.assertEqual(C.CherryProvider().poll(), [])

    def test_wired_pid_requires_its_own_product_name(self):
        for pid, product in ((0x01DE, C.PRODUCTS[0x01E0]), (0x01E0, C.PRODUCTS[0x01DE])):
            with self.poll([reply()], inventory(product_id=pid, product_string=product)) as bus:
                self.assertEqual(C.CherryProvider().poll(), [])
                self.assertEqual(bus.opened, [])

    def test_silence_noise_and_io_failures_are_bounded(self):
        for frames in ([], [[0] * 64] * 100):
            with self.poll(frames, read_ms=0) as bus:
                self.assertEqual(C.CherryProvider().poll(), [])
                self.assertLessEqual(len(bus.reads), 32)
                self.assertLessEqual(bus.now, 1.001)
        with self.poll([reply()], write_result=63) as bus:
            self.assertEqual(C.CherryProvider().poll(), [])
            self.assertEqual(bus.reads, [])
            self.assertEqual(len(bus.writes), 1)
        for error_at in ("open", "write", "read"):
            with self.poll([], error_at=error_at) as bus:
                provider = C.CherryProvider()
                self.assertEqual(provider.poll(), [])
                self.assertNotIn("private-path", " ".join(provider.diagnostics()))
                self.assertEqual(bus.opened, bus.closed)

    def test_same_model_receivers_keep_separate_identities(self):
        one, two = inventory()[0], inventory()[0]
        two.update(path=b"second-receiver", physical_id="receiver-b")
        with self.poll([reply(58), reply(73)], [one, two]):
            statuses = C.CherryProvider().poll()
            self.assertEqual([s.level for s in statuses], [58, 73])
            self.assertEqual(len({s.key for s in statuses}), 2)


if __name__ == "__main__":
    unittest.main()
