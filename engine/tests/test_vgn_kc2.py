"""Tests for VGN kc2 wireless mouse receiver battery reader."""
from contextlib import contextmanager
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
import battery_hid as H
import topology
from providers import vgn_kc2 as V
from test_new_brands import FakeHid

P = V.VgnKc2Provider
REQUEST = [0xB3, 0x06] + [0] * 62

# Exact 68-byte FFC1:0001 descriptor from Issue #856 (391D:1A20 interface 1).
ISSUE856_DESCRIPTOR = bytes.fromhex(
    "06c1ff0901a101"
    "85b40902150026ff007508953f8102"
    "85b60907150026ff00750895148102"
    "85b30903150026ff007508953f9102"
    "85b50904150026ff00750895149102c0"
)

# Exact 64-byte live B4 reply from Issue #856 (raw[20] == 0x3F == 63%).
ISSUE856_REPLY = list(bytes.fromhex(
    "b4060022226290017805400608070019"
    "9106040a3f10a400000000bb00000104"
    "04040404040404040410a40100010203"
    "04050705ff0197010000060000db0100"
))


def entry(physical="receiver-a", product_id=0x1A21, **changes):
    iface = 2 if product_id == 0x1A3E else 1
    info = dict(
        vendor_id=0x391D,
        product_id=product_id,
        interface_number=iface,
        usage_page=0xFFC1,
        usage=0x0001,
        input_report_length=64,
        output_report_length=64,
        input_report_sizes={"180": 64, "182": 21},
        output_report_sizes={"179": 64, "181": 21},
        path=(physical + "-ffc1").encode(),
        physical_id=physical,
        product_string="Dragonfly 3 Turbo+",
        serial_number="same-serial",
    )
    info.update(changes)
    return info


def reply(level=69, cmd=0x06, report_id=0xB4, length=64, bit7=False):
    frame = [report_id, cmd] + [0] * (length - 2)
    if length > V.BATTERY_OFFSET:
        frame[V.BATTERY_OFFSET] = (level & 0xFF) | (0x80 if bit7 else 0x00)
    return frame


@contextmanager
def fixture(frames=(), inventory=None, **kwargs):
    bus = FakeHid(frames, **kwargs)
    infos = [entry()] if inventory is None else inventory
    with patch.object(H, "hid", bus), \
            patch.object(H.hidlist, "enumerate", side_effect=lambda vid=0: [
                d for d in infos if not vid or d["vendor_id"] == vid]), \
            patch.object(H, "time", SimpleNamespace(monotonic=lambda: bus.now)):
        yield bus


class VgnKc2Tests(unittest.TestCase):
    def test_issue856_descriptor_proof_and_live_reply(self):
        parsed, error = H.report_descriptor_proof(ISSUE856_DESCRIPTOR)
        self.assertIsNone(error)
        self.assertEqual(
            parsed,
            {(0xFFC1, 0x0001): {"input": {"180": 64, "182": 21}, "output": {"179": 64, "181": 21}, "feature": {}}},
        )
        self.assertEqual(V.parse_reply(ISSUE856_REPLY), (63, False, ""))

        info = entry(product_id=0x1A20, product_string="Dragonfly 3 Turbo",
                     input_report_sizes=None, output_report_sizes=None,
                     input_report_proof="failed", output_report_proof="failed")
        with patch.object(topology, "_read_report_descriptor", return_value=(ISSUE856_DESCRIPTOR, None)):
            topology._descriptor_fallback(
                info,
                SimpleNamespace(input=64, output=64, feature=0, ib=1, iv=0, ob=1, ov=0, fb=0, fv=0, page=0xFFC1, usage=1),
                ("input", "output"),
            )
        self.assertEqual(info["input_report_sizes"], {"180": 64, "182": 21})
        self.assertEqual(info["output_report_sizes"], {"179": 64, "181": 21})

        with fixture([ISSUE856_REPLY], [info]) as bus:
            status, = P().poll()
        self.assertEqual((status.name, status.level, status.kind, status.verification_level),
                         ("VGN Dragonfly 3 Turbo", 63, "mouse", "C"))
        self.assertFalse(status.charging_known)
        self.assertEqual(bus.writes, [(info["path"], REQUEST)])
        self.assertEqual(bus.opened, bus.closed)

    def test_all_documented_pids_and_bit7_mask(self):
        self.assertEqual(V.make_request(), REQUEST)
        for pid, expected_name in V.PIDS.items():
            for level, bit7 in ((1, False), (69, False), (100, True)):
                with self.subTest(pid=pid, level=level, bit7=bit7), \
                        fixture([reply(level=level, bit7=bit7)], [entry(product_id=pid)]) as bus:
                    status, = P().poll()
                self.assertEqual((status.name, status.level, status.charging, status.charging_known),
                                 (expected_name, level, False, False))
                self.assertEqual((status.kind, status.verification_level, status.online),
                                 ("mouse", "C", True))
                self.assertEqual(bus.writes, [(entry()["path"], REQUEST)])
                self.assertEqual(bus.reads[0][0], 64)
                self.assertEqual(bus.opened, bus.closed)

    def test_skips_b6_sibling_input_frame_before_b4_06(self):
        b6_short = [0xB6] + [0] * 20
        other_b4 = reply(level=50, cmd=0x03)
        with fixture([b6_short, other_b4, reply(level=82)]) as bus:
            status, = P().poll()
        self.assertEqual(status.level, 82)
        self.assertEqual(len(bus.writes), 1)
        self.assertEqual(len(bus.reads), 3)

    def test_zero_asleep_and_invalid_replies_are_rejected(self):
        for frame in ([], reply(level=0), reply(level=0, bit7=True), reply(level=101),
                      reply(level=127), reply(cmd=0x04), reply(report_id=0xB6),
                      reply(length=21), reply(length=63), REQUEST):
            with self.subTest(frame=frame[:4], length=len(frame)), fixture([frame]) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(bus.writes, [(entry()["path"], REQUEST)])
                self.assertEqual(bus.opened, bus.closed)
        self.assertTrue(V.no_reading_reply(reply(level=0)))
        self.assertTrue(V.no_reading_reply(reply(level=0, bit7=True)))
        self.assertFalse(V.no_reading_reply(reply(level=1)))

    def test_unproven_reports_wrong_interface_or_ambiguous_parent_never_open(self):
        changes = [
            dict(vendor_id=0x3554),
            dict(product_id=0x1A01),
            dict(usage_page=0xFF01, usage=0x0010),
            dict(usage=0x0002),
            dict(interface_number=2),  # 1A21 must be on mi_01
            dict(product_id=0x1A3E, interface_number=1),  # 1A3E must be on mi_02
            dict(input_report_length=63),
            dict(output_report_length=63),
            dict(input_report_sizes=None),
            dict(output_report_sizes=None),
            dict(input_report_sizes={"182": 64}),
            dict(output_report_sizes={"181": 64}),
            dict(input_report_sizes={"180": 63, "182": 21}),
            dict(output_report_sizes={"179": 63, "181": 21}),
            dict(input_report_sizes={180: 64}),
            dict(output_report_sizes={179: 64}),
            dict(input_report_sizes={"180": 64, "182": 65}),
            dict(output_report_sizes={"179": 64, "181": 1}),
            dict(physical_id=""),
            dict(physical_id="collection:unresolved"),
            dict(path=b""),
        ]
        inventories = [[entry(**c)] for c in changes]
        inventories.append([entry(), entry(path=b"second-ffc1")])
        # One proven + one unproven matching collection on same parent must still abort as ambiguous.
        inventories.append([entry(), entry(path=b"second-ffc1", input_report_sizes=None, output_report_sizes=None)])
        for inv in inventories:
            with self.subTest(inv=inv), fixture([reply()], inv) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual((bus.opened, bus.writes, bus.reads), ([], [], []))

    def test_open_write_read_errors_close_cleanly(self):
        for err in ("open", "write", "read"):
            with self.subTest(err=err), fixture([], error_at=err) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(bus.opened, bus.closed)


if __name__ == "__main__":
    unittest.main()
