"""Synthetic MT560 passive inputs; no device or vendor code execution."""
from contextlib import contextmanager
import unittest

from test_rapoo import (
    R, battery_hid, caps, descriptor as vt_descriptor, FakeApi,
    fixture, report as vt_report, server,
)
from providers import rapoo_mt560 as M


def descriptor(**changes):
    info = vt_descriptor(
        pid=0x1872, physical=r"pnp:usb\vid_24ae&pid_1872\sample",
        usage=0x000E, input_report_length=7,
        input_report_sizes={"187": 7},
    )
    info["path"] = br"\\?\hid#vid_24ae&pid_1872&mi_00#mt560"
    info.update(changes)
    return info


def report(level=73, length=7):
    raw = [0xBB, 0xB0, 0, 0, 0, 1, level]
    return (raw + [0] * max(0, length - 7))[:length]


@contextmanager
def passive(frames=(), inventory=None):
    with fixture(frames, [descriptor()] if inventory is None else inventory) as bus:
        provider = M.RapooMT560Provider()
        try:
            yield provider, bus
        finally:
            provider.close()
        if bus.forbidden:
            raise AssertionError(bus.forbidden)


class RapooMT560Tests(unittest.TestCase):
    def test_offsets_endpoints_and_unknown_charging(self):
        for level in (0, 42, 100):
            raw = report(level)
            raw[2:5] = [255, 100, 0]
            with self.subTest(level=level), passive([raw]) as (provider, bus):
                status, = provider.poll()
                self.assertEqual(
                    (status.name, status.level, status.charging, status.charging_known),
                    ("Rapoo MT560", level, False, False),
                )
                self.assertEqual(
                    (status.kind, status.source, status.verification_level),
                    ("mouse", "rapoo_mt560", "C"),
                )
                self.assertTrue(status.online)
                self.assertFalse(status.cached)
                self.assertIsNotNone(server.valid_time(status.measured_at_utc))
                public = server.normalise(status, None, server.utcnow())
                self.assertIsNone(public["charging"])
                self.assertEqual(len(bus.opened), 1)
                self.assertEqual(bus.forbidden, [])

    def test_invalid_inputs_do_not_become_measurements(self):
        bad = [report(value) for value in (-1, 101, 255, None, "42", 42.0, True)]
        for offset, value in ((0, 7), (0, 0xBA), (1, 0), (5, 0), (5, 2)):
            raw = report()
            raw[offset] = value
            bad.append(raw)
        bad.extend((report(length=6), report(length=8)))
        for raw in bad:
            with self.subTest(raw=raw), passive([raw]) as (provider, bus):
                self.assertEqual(provider.poll(), [])
                self.assertEqual(len(bus.opened), 1)
                self.assertEqual(bus.forbidden, [])
        with passive(bad + [report(39)]) as (provider, bus):
            status, = provider.poll()
            self.assertEqual(status.level, 39)
            self.assertEqual(len(bus.reads), len(bad) + 1)

    def test_exact_descriptor_length_without_assuming_64(self):
        for length in (7, 19, 65):
            info = descriptor(
                input_report_length=length, input_report_sizes={"187": length},
            )
            with self.subTest(length=length), passive(
                [report(length=length - 1), report(51, length)], [info],
            ) as (provider, bus):
                status, = provider.poll()
                self.assertEqual(status.level, 51)
                self.assertEqual([size for size, _ in bus.reads], [length, length])
        info = descriptor(input_report_length=65, input_report_sizes={"187": 7, "9": 65})
        with passive([report(10, 65), report(52)], [info]) as (provider, bus):
            status, = provider.poll()
            self.assertEqual(status.level, 52)
            self.assertEqual([size for size, _ in bus.reads], [65, 65])

    def test_missing_proof_identity_and_ambiguous_candidates_never_open(self):
        invalid = [
            {"input_report_sizes": None},
            {"input_report_sizes": {}},
            {"input_report_sizes": {"7": 7}},
            {"input_report_sizes": {"187": 6}},
            {"input_report_sizes": {"187": True}},
            {"input_report_length": 65},
            {"input_report_length": None},
            {"physical_id": ""},
            {"physical_id": "unidentified-parent"},
            {"path": b"no-hid-identity"},
            {"physical_id": "collection:unresolved"},
            {"path": None},
            {"usage": 2},
            {"vendor_id": 0x25A7, "product_id": 0x2410},
            {"vendor_id": 0x25A7},
            {"product_id": 0x2410},
            {"product_id": 0x1412},
            {"path": br"\\?\hid#vid_25a7&pid_2410#receiver"},
            {"path": br"\\?\hid#vid_24ae&pid_1412#receiver"},
            {"path": br"\\?\hid#vid_24ae&pid_1872#vid_25a7&pid_2410"},
            {"path": br"\\?\hid#vid_24ae#receiver"},
            {"path": br"\\?\hid#vid_24ae&pid_1872z#receiver"},
            {"physical_id": r"usb:vid_25a7&pid_2410"},
        ]
        inventories = [[descriptor(**changes)] for changes in invalid]
        inventories.extend((
            [descriptor(), descriptor()],
            [descriptor(), descriptor(
                path=br"\\?\hid#vid_24ae&pid_1872#other",
                input_report_sizes=None,
            )],
            [descriptor(), descriptor(physical_id="")],
            [descriptor(), descriptor(physical_id="collection:unresolved")],
        ))
        for inventory in inventories:
            with self.subTest(inventory=inventory), passive(
                [report()], inventory,
            ) as (provider, bus):
                self.assertEqual(provider.poll(), [])
                self.assertEqual(bus.opened, [])
                self.assertEqual(bus.reads, [])
                self.assertEqual(bus.forbidden, [])

    def test_windows_fixed_usage_input_proof_and_wrong_report_id(self):
        for report_id in (187, 7):
            item = dict(
                page=0xFF00, report=report_id, field=0, is_range=0,
                usage_min=14, usage_max=14, report_count=6,
            )
            api, descriptor_caps = FakeApi([item]), caps(ib=1, size=7)
            self.assertIsNone(battery_hid.byte_report_sizes(api, None, descriptor_caps, 0))
            sizes = battery_hid.byte_report_sizes(
                api, None, descriptor_caps, 0, allow_fixed_usage=True,
            )
            self.assertEqual(sizes, {str(report_id): 7})
            with self.subTest(report_id=report_id), passive(
                [report(61)], [descriptor(input_report_sizes=sizes)],
            ) as (provider, bus):
                statuses = provider.poll()
                if report_id == 187:
                    status, = statuses
                    self.assertEqual(status.level, 61)
                    self.assertEqual(len(bus.opened), 1)
                else:
                    self.assertEqual(statuses, [])
                    self.assertEqual(bus.opened, [])

    def test_reuses_handle_drains_latest_and_preserves_history(self):
        with passive([report(20)]) as (provider, bus):
            first, = provider.poll()
            bus.frames.extend([report(30), report(40)])
            latest, = provider.poll()
            self.assertEqual(latest.level, 40)
            self.assertEqual(len(bus.opened), 1)
            self.assertTrue(all(timeout == 1 for _, timeout in bus.reads[1:]))
            historical, = provider.poll()
            self.assertEqual(historical.level, 40)
            self.assertTrue(historical.cached)
            self.assertFalse(historical.online)
            self.assertFalse(historical.connection_known)
            self.assertEqual(first.key, latest.key)
            bus.inventory.append(descriptor(physical_id=""))
            retained, = provider.poll()
            self.assertEqual(retained.level, 40)
            self.assertTrue(retained.cached)
            self.assertEqual(len(bus.opened), 1)
            self.assertEqual(bus.closed, bus.opened)
            bus.inventory = []
            self.assertEqual(provider.poll(), [])

    def test_drain_remains_bounded(self):
        with passive([report(1)]) as (provider, bus):
            provider.poll()
            bus.frames.extend([report(2)] * (R.MAX_DRAIN + 1))
            status, = provider.poll()
            self.assertEqual(status.level, 2)
            self.assertEqual(len(bus.reads), 1 + R.MAX_DRAIN)
            self.assertEqual(len(bus.frames), 1)

    def test_vt_and_mt560_never_cross(self):
        with fixture([vt_report(68)], [vt_descriptor()]) as bus:
            provider = R.RapooProvider()
            try:
                status, = provider.poll()
                self.assertEqual((status.name, status.level, status.source),
                                 ("Rapoo VT3", 68, "rapoo"))
            finally:
                provider.close()
            self.assertEqual(bus.forbidden, [])
        with fixture([report()], [descriptor()]) as bus:
            provider = R.RapooProvider()
            try:
                self.assertEqual(provider.poll(), [])
                self.assertEqual(bus.opened, [])
            finally:
                provider.close()
        with passive([vt_report()], [vt_descriptor()]) as (provider, bus):
            self.assertEqual(provider.poll(), [])
            self.assertEqual(bus.opened, [])
        with passive([vt_report(), report(67)]) as (provider, bus):
            status, = provider.poll()
            self.assertEqual(status.level, 67)
            self.assertEqual(len(bus.reads), 2)
            self.assertEqual(bus.forbidden, [])


if __name__ == "__main__":
    unittest.main()
