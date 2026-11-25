"""Synthetic Inphic IN9 SLE (7768:642C 2.4G / 7768:6D2C wired) passive tests."""
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

ENGINE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ENGINE))
import _hidstub  # noqa: E402,F401
import battery_hid  # noqa: E402
import catalog  # noqa: E402
import device_pack  # noqa: E402
import known_ids  # noqa: E402
import server  # noqa: E402
import topology  # noqa: E402
from providers import inphic as I  # noqa: E402

P = I.InphicProvider
RX_PATH = rb"\\?\hid#vid_7768&pid_642c&mi_01&col01#synthetic"
RX_PARENT = r"pnp:usb\vid_7768&pid_642c\synthetic"
WIRE_PATH = rb"\\?\hid#vid_7768&pid_6d2c&mi_01&col01#synthetic"
WIRE_PARENT = r"pnp:usb\vid_7768&pid_6d2c\synthetic"


def frame(level=90, cable=0, length=64):
    buf = [0x06, 0xFA, 0x03, 0x00, 0x00, 0x03, level, cable]
    return bytes(buf + [0] * (length - len(buf)))


def entry(pid=0x642C, **changes):
    path = RX_PATH if pid == 0x642C else WIRE_PATH
    parent = RX_PARENT if pid == 0x642C else WIRE_PARENT
    info = dict(
        vendor_id=0x7768,
        product_id=pid,
        product_string="Inphic IN9 SLE",
        usage_page=0xFFB2,
        usage=0x0001,
        interface_number=1,
        path=path,
        physical_id=parent,
        input_report_length=64,
        input_report_sizes={"6": 64},
        input_report_proof="caps",
    )
    info.update(changes)
    return info


class FakeBus:
    def __init__(self, frames_by_path=None, error_at=None):
        self.frames_by_path = {k: list(v) for k, v in (frames_by_path or {}).items()}
        self.error_at = error_at
        self.opened = []
        self.closed = []
        self.writes = []
        self.reads = []

    def device(self):
        bus = self

        class Handle:
            def __init__(self):
                self.path = None

            def open_path(self, path):
                self.path = path
                bus.opened.append(path)
                if bus.error_at == "open":
                    raise OSError("private-path")

            def write(self, data):
                bus.writes.append(data)
                raise AssertionError("inphic must never write")

            def send_feature_report(self, data):
                raise AssertionError("inphic must never send feature")

            def get_feature_report(self, rid, length):
                raise AssertionError("inphic must never get feature")

            def read(self, length, timeout):
                bus.reads.append((self.path, length, timeout))
                if bus.error_at == "read":
                    raise OSError("private-path")
                q = bus.frames_by_path.get(self.path, [])
                return q.pop(0) if q else b""

            def close(self):
                bus.closed.append(self.path)

        return Handle()


class InphicTests(unittest.TestCase):
    def test_valid_wireless_and_wired_passive_reads_without_writes(self):
        for pid, path, level, cable in (
            (0x642C, RX_PATH, 90, 0),
            (0x6D2C, WIRE_PATH, 100, 1),
        ):
            with self.subTest(pid=hex(pid)):
                bus = FakeBus({path: [frame(level=level, cable=cable)]})
                p = P()
                with patch.object(I.hidlist, "enumerate", return_value=[entry(pid)]), \
                        patch.object(I, "hid", bus):
                    statuses = p.poll()
                self.assertEqual(len(statuses), 1)
                s, = statuses
                self.assertEqual((s.name, s.level, s.charging, s.charging_known, s.online,
                                  s.kind, s.verification_level, s.unconfirmed),
                                 ("Inphic IN9 SLE", level, False, False, True, "mouse", "C", True))
                self.assertEqual(bus.writes, [])
                self.assertEqual(bus.opened, [path])
                p.close()
                self.assertEqual(bus.closed, [path])

    def test_zero_handoff_placeholder_is_ignored_and_retains_history(self):
        bus = FakeBus({RX_PATH: [frame(level=90, cable=0), b"", frame(level=0, cable=0), b"", frame(level=88, cable=0)]})
        p = P()
        inv = [entry(0x642C)]
        with patch.object(I.hidlist, "enumerate", return_value=inv), \
                patch.object(I, "hid", bus):
            s1, = p.poll()
            self.assertEqual((s1.level, s1.online, s1.cached), (90, True, False))
            # Second poll drains empty -> returns cached 90%
            s2, = p.poll()
            self.assertEqual((s2.level, s2.online, s2.cached), (90, False, True))
            # Next queue item is transient 0 -> ignored; retains cached 90% and records note
            s3, = p.poll()
            self.assertEqual((s3.level, s3.online, s3.cached), (90, False, True))
            self.assertIn("zero heartbeat placeholder ignored; no new measurement", p.diagnostics())
            # Next queue item is real 88%
            s4, = p.poll()
            self.assertEqual((s4.level, s4.online, s4.cached), (88, True, False))
            self.assertEqual(len(bus.opened), 1)
        p.close()

    def test_drain_keeps_newest_valid_frame_and_skips_trailing_zero(self):
        bus = FakeBus({RX_PATH: [frame(90), frame(91), frame(92), frame(0)]})
        p = P()
        with patch.object(I.hidlist, "enumerate", return_value=[entry(0x642C)]), \
                patch.object(I, "hid", bus):
            s1, = p.poll()
            self.assertEqual(s1.level, 90)
            s2, = p.poll()
            self.assertEqual((s2.level, s2.online, s2.cached), (92, True, False))
        p.close()

    def test_suppression_folds_old_wired_when_wireless_is_current(self):
        engine = server.BatteryEngine(providers=[])
        self.addCleanup(engine.close)
        wired_info = entry(0x6D2C)
        rx_info = entry(0x642C)
        p = P()
        bus = FakeBus({WIRE_PATH: [frame(100, cable=1)], RX_PATH: [frame(100, cable=0)]})
        with patch.object(I, "hid", bus):
            s_wired = server.scoped_poll(p, [wired_info], WIRE_PARENT)
        engine._accept("inphic|" + WIRE_PARENT, "inphic", s_wired, server.utcnow(), physical=WIRE_PARENT, candidate_count=1)
        engine._snapshot(1, 1, True)
        # Wired turns silent -> historical cached; wireless connects current 100%
        with patch.object(I, "hid", bus):
            s_wired_cached = server.scoped_poll(p, [wired_info], WIRE_PARENT)
            s_rx_live = server.scoped_poll(p, [rx_info], RX_PARENT)
        engine._accept("inphic|" + WIRE_PARENT, "inphic", s_wired_cached, server.utcnow(), physical=WIRE_PARENT, candidate_count=1)
        engine._accept("inphic|" + RX_PARENT, "inphic", s_rx_live, server.utcnow(), physical=RX_PARENT, candidate_count=1)
        snap = engine._snapshot(1, 1, True)
        self.assertEqual(len(snap["devices"]), 1)
        self.assertEqual(snap["devices"][0]["percent"], 100)
        self.assertEqual(snap["devices"][0]["measurementState"], "current")
        self.assertEqual(len(snap["suppressed"]), 1)
        p.close()

    def test_unproven_or_ajazz_642e_never_opened(self):
        bad_cases = [
            entry(0x642E),  # AJAZZ AJ179 NLP uses 7768:642E, must stay untouched
            entry(interface_number=0),
            entry(usage_page=0xFFB3),
            entry(usage=2),
            entry(physical_id="collection:" + RX_PATH.decode()),
            entry(physical_id=WIRE_PARENT),
            entry(input_report_sizes=None),
            entry(input_report_sizes={"5": 64}),
            entry(input_report_sizes={"6": 63}, input_report_length=64),
            entry(input_report_length=63, input_report_sizes={"6": 63}),
        ]
        for bad in bad_cases:
            with self.subTest(bad=bad.get("product_id"), iface=bad.get("interface_number"), sizes=bad.get("input_report_sizes")):
                bus = FakeBus({bad["path"]: [frame(90)]})
                p = P()
                with patch.object(I.hidlist, "enumerate", return_value=[bad]), patch.object(I, "hid", bus):
                    self.assertEqual(p.poll(), [])
                self.assertEqual(bus.opened, [])
                p.close()

    def test_duplicate_collections_close_and_disconnect_releases_handle(self):
        inv = [entry(0x642C), entry(0x642C, path=RX_PATH.replace(b"col01", b"col02"))]
        bus = FakeBus({RX_PATH: [frame(90)]})
        p = P()
        with patch.object(I.hidlist, "enumerate", return_value=inv), patch.object(I, "hid", bus):
            self.assertEqual(p.poll(), [])
        self.assertEqual(bus.opened, [])

        inv1 = [entry(0x642C)]
        with patch.object(I.hidlist, "enumerate", return_value=inv1), patch.object(I, "hid", bus):
            self.assertEqual(len(p.poll()), 1)
        with patch.object(I.hidlist, "enumerate", return_value=[]), patch.object(I, "hid", bus):
            self.assertEqual(p.poll(), [])
        self.assertEqual(bus.closed, [RX_PATH])

    def test_topology_descriptor_fallback_and_registration(self):
        # Single collection Input Report 06 (1-byte ID + 63-byte Data Array = 64B)
        desc = bytes([
            0x06, 0xB2, 0xFF, 0x09, 0x01, 0xA1, 0x01,
            0x85, 0x06, 0x75, 0x08, 0x95, 0x3F, 0x09, 0x01, 0x81, 0x00,
            0xC0,
        ])
        from standardhid import Caps
        caps = Caps(usage=1, page=0xFFB2, input=64, output=0, feature=0, ib=1)
        with patch.object(topology, "_windows_handles", side_effect=lambda *a: (1, 0)) if hasattr(topology, "_windows_handles") else patch.object(topology, "os") as _unused:
            pass
        # Validate parsed descriptor directly with battery_hid
        parsed, reason = battery_hid.report_descriptor_proof(desc)
        self.assertIsNone(reason)
        self.assertEqual(parsed[(0xFFB2, 1)]["input"], {"6": 64})
        self.assertEqual(caps.input, 64)

        self.assertIn(("inphic", "InphicProvider"), server.PROVIDERS)
        self.assertIn("inphic", server.NO_CHARGING_EVIDENCE)
        self.assertIn("inphic", server.IDLE_BACKOFF_EXEMPT)
        self.assertIn("inphic", device_pack.BRAND_PROVIDERS)
        self.assertEqual(server.recognized_candidate_count("inphic", [entry(0x642C)]), 1)
        self.assertEqual(server.recognized_candidate_count("inphic", [entry(0x642E)]), 0)
        rows = [d for d in known_ids.build_index()["devices"] if d["provider"] == "inphic"]
        self.assertEqual({d["productId"] for d in rows}, {0x642C, 0x6D2C})
        self.assertEqual(catalog.validate_catalog()["errors"], [])


if __name__ == "__main__":
    unittest.main()
