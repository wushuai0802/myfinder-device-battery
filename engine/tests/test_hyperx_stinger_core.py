"""Synthetic feature-report tests for HyperX Cloud Stinger Core Wireless DTS."""
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
from providers import hyperx_stinger_core as H

P = H.HyperXStingerCoreProvider
PATH = br"\\?\hid#vid_03f0&pid_0a8e&mi_03&col03#synthetic"
PARENT = r"pnp:usb\vid_03f0&pid_0a8e\0001"


def entry(vid=0x03F0, pid=0x0A8E, **changes):
    info = dict(
        vendor_id=vid,
        product_id=pid,
        usage_page=0xFF00,
        usage=0x0001,
        path=rf"\\?\hid#vid_{vid:04x}&pid_{pid:04x}&mi_03&col03#synthetic".encode(),
        physical_id=rf"pnp:usb\vid_{vid:04x}&pid_{pid:04x}\0001",
        product_string="Hid Interface",
        feature_report_length=64,
        feature_report_sizes={"255": 64},
    )
    info.update(changes)
    return info


def conn_reply(connected=True, stale=False, length=64):
    buf = [0] * length
    buf[0] = 0xFF
    if stale:
        buf[1], buf[2] = 0x01, 0x00
    else:
        buf[1], buf[2] = 0x10, 0x02
        buf[11] = 0x01 if connected else 0x00
    return buf


def batt_reply(state=0x03, mv=3785, hp_header=True, length=64):
    buf = [0] * length
    buf[0] = 0xFF
    if hp_header:
        buf[1], buf[2] = 0x12, 0x05
    else:
        buf[1], buf[2] = 0x07, 0x01
    buf[9] = state
    raw_mv = int(mv).to_bytes(2, "little", signed=True)
    buf[11], buf[12] = raw_mv[0], raw_mv[1]
    return buf


class FakeHid:
    def __init__(self, replies=(), write_results=None, fail=None):
        self.replies = list(replies)
        self.write_results = list(write_results) if write_results is not None else None
        self.fail = fail
        self.opened, self.writes, self.reads, self.closed = [], [], [], []

    def device(self):
        bus = self

        class Handle:
            def open_path(self, path):
                self.path = path
                bus.opened.append(path)
                if bus.fail == "open":
                    raise OSError("open-fail")

            def send_feature_report(self, packet):
                bus.writes.append((self.path, list(packet)))
                if bus.fail == "write":
                    raise OSError("write-fail")
                if bus.write_results is not None:
                    return bus.write_results.pop(0)
                return len(packet)

            def get_feature_report(self, report_id, length):
                bus.reads.append((report_id, length))
                if bus.fail == "read":
                    raise OSError("read-fail")
                return bus.replies.pop(0) if bus.replies else []

            def close(self):
                bus.closed.append(self.path)

        return Handle()


def fixture(replies=(), inventory=None, **kwargs):
    inv = [entry()] if inventory is None else inventory

    def enum(vid):
        return [row for row in inv if row.get("vendor_id") == vid]

    class Ctx:
        def __enter__(self):
            self.bus = FakeHid(replies, **kwargs)
            self.patches = [
                patch.object(H, "hid", self.bus),
                patch.object(H.hidlist, "enumerate", side_effect=enum),
                patch.object(H, "time", SimpleNamespace(sleep=lambda _s: None)),
            ]
            for item in self.patches:
                item.start()
            return self.bus

        def __exit__(self, *exc):
            for item in reversed(self.patches):
                item.stop()
            return False

    return Ctx()


class HyperXStingerCoreTests(unittest.TestCase):
    def test_voltage_curves_and_boundaries(self):
        self.assertEqual(H.voltage_to_percent(3517, H.DISCHARGING_MV), 0)
        self.assertEqual(H.voltage_to_percent(3785, H.DISCHARGING_MV), 50)
        self.assertEqual(H.voltage_to_percent(4121, H.DISCHARGING_MV), 100)
        self.assertEqual(H.voltage_to_percent(3400, H.CHARGING_MV), 0)
        self.assertEqual(H.voltage_to_percent(3946, H.CHARGING_MV), 50)
        self.assertEqual(H.voltage_to_percent(4130, H.CHARGING_MV), 100)

    def test_hp_0a8e_skips_stale_connection_and_decodes_discharging(self):
        replies = [
            conn_reply(stale=True),
            conn_reply(connected=True),
            batt_reply(state=0x03, mv=3785, hp_header=False),  # ignored until 12 05
            batt_reply(state=0x03, mv=3785, hp_header=True),
        ]
        with fixture(replies) as bus:
            status, = P().poll()
        self.assertEqual(status.name, "HyperX Cloud Stinger Core Wireless DTS")
        self.assertEqual(status.level, 50)
        self.assertFalse(status.charging)
        self.assertEqual(status.approx, "about 50%")
        self.assertEqual(status.kind, "headset")
        self.assertTrue(status.charging_known)
        self.assertTrue(status.connection_known)
        self.assertEqual(status.verification_level, "C")
        self.assertEqual(len(bus.writes), 2)
        self.assertEqual(bus.writes[0][1], list(H.make_request(H.CONNECTION_PREFIX, 64)))
        self.assertEqual(bus.writes[1][1], list(H.make_request(H.BATTERY_PREFIX, 64)))
        self.assertEqual(bus.opened, bus.closed)

    def test_hp_0a93_charging_and_full_states(self):
        inv = [entry(0x03F0, 0x0A93, feature_report_length=65, feature_report_sizes={"255": 65})]
        with fixture([conn_reply(True, length=65), batt_reply(0x05, 3946, True, 65)], inv):
            status, = P().poll()
        self.assertEqual((status.level, status.charging, status.approx), (50, True, "about 50%"))

        with fixture([conn_reply(True, length=65), batt_reply(0x06, 0, True, 65)], inv):
            full, = P().poll()
        self.assertEqual((full.level, full.charging, full.approx), (100, False, ""))

    def test_kingston_170b_reads_once_without_hp_header_requirement(self):
        inv = [entry(0x0951, 0x170B)]
        with fixture([conn_reply(True), batt_reply(0x03, 3905, hp_header=False)], inv) as bus:
            status, = P().poll()
        self.assertEqual((status.level, status.charging, status.approx), (69, False, "about 69%"))
        self.assertEqual(len(bus.writes), 2)
        self.assertEqual(len(bus.reads), 2)

    def test_disconnected_headset_stops_before_battery_query(self):
        with fixture([conn_reply(connected=False)]) as bus:
            provider = P()
            self.assertEqual(provider.poll(), [])
        self.assertEqual(len(bus.writes), 1)
        self.assertTrue(any("disconnected" in d for d in provider.diagnostics()))
        self.assertEqual(bus.opened, bus.closed)

        # Battery-shaped frame, prefix echo, or unknown raw[11] must be rejected in connection phase.
        bad_conn_echo = list(H.CONNECTION_PREFIX) + [0xAA] * (64 - len(H.CONNECTION_PREFIX))
        unknown_conn = conn_reply(connected=False)
        unknown_conn[11] = 0x02
        for bad_conn in (batt_reply(state=0x03, mv=3841), bad_conn_echo, unknown_conn):
            with self.subTest(bad_conn=bad_conn[:12]), fixture([bad_conn]) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(len(bus.writes), 1)
                self.assertEqual(bus.opened, bus.closed)

    def test_invalid_battery_frames_and_echoes_rejected(self):
        echo_batt = list(H.make_request(H.BATTERY_PREFIX, 64))
        bad_frames = [
            [],
            [0] * 64,
            echo_batt,
            batt_reply(state=0x04, mv=3800),       # unknown state code
            batt_reply(state=0x03, mv=2000),       # below 2500mV
            batt_reply(state=0x03, mv=5500),       # above 5000mV
            batt_reply(state=0x03, mv=-100),       # negative int16
            batt_reply(state=0x03, mv=3800, hp_header=False),
        ]
        for frame in bad_frames:
            with self.subTest(frame=frame[:13]), fixture([conn_reply(True), frame]) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(len(bus.writes), 2)
                self.assertEqual(bus.opened, bus.closed)

    def test_unproven_descriptors_and_non_target_collections_never_open(self):
        bad_inventories = [
            [entry(usage_page=0xFFC0, usage=0x0001)],  # mic/mute collection
            [entry(usage_page=0x000C, usage=0x0001)],  # consumer control
            [entry(product_id=0x098D)],                # other HP headset
            [entry(feature_report_length=31, feature_report_sizes={"255": 31})],
            [entry(feature_report_length=64, feature_report_sizes={"0": 64})],
            [entry(feature_report_length=64, feature_report_sizes=None)],
            [entry(physical_id="collection:unknown")],
            [entry(physical_id=r"pnp:usb\vid_03f0&pid_0a93\0001")],  # mismatched PID parent
            [entry(), entry(path=b"dup")],
        ]
        for inv in bad_inventories:
            with self.subTest(inv=inv[0].get("usage_page")), fixture([conn_reply(True)], inv) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(bus.opened, [])
                self.assertEqual(bus.writes, [])

    def test_write_and_io_failures_close_cleanly_without_retry(self):
        with fixture([], write_results=[63]) as bus:
            self.assertEqual(P().poll(), [])
        self.assertEqual(len(bus.writes), 1)
        self.assertEqual(bus.reads, [])
        self.assertEqual(bus.opened, bus.closed)

        for fail in ("open", "write", "read"):
            with self.subTest(fail=fail), fixture([], fail=fail) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(bus.opened, bus.closed)


if __name__ == "__main__":
    unittest.main()
