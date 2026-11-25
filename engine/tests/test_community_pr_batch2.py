"""Synthetic regressions for community PRs #123 (ROG Scope II 96) and #66 (VGN F3 MasterGT)."""
from contextlib import contextmanager
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

ENGINE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ENGINE))
import _hidstub  # noqa: E402,F401
import battery_hid  # noqa: E402
import catalog  # noqa: E402
import known_ids  # noqa: E402
import server  # noqa: E402
import topology  # noqa: E402
from providers import asus_azoth as A  # noqa: E402
from providers import pulsar as P  # noqa: E402

BS = "\\"
OMNI = 0x1ACE
SCOPE2 = 0x1AB0
F3_PATH = rb"\\?\hid#vid_391d&pid_1a1a&mi_01&col01#synthetic"
F3_PHYSICAL = r"pnp:usb\vid_391d&pid_1a1a\synthetic"
# Contributor live capture (#66): 100%, 4140 mV, checksum 0xA7.
F3_CAPTURE = bytes.fromhex("08 04 00 00 00 02 64 00 10 2c 00 00 00 00 00 00 a7")
F3_REQUEST = bytes.fromhex("08 04 00 00 00 00 00 00 00 00 00 00 00 00 00 00 49")


def omni_port(col="&col02", page=0xFF00, rid="2", **changes):
    parent = f"pnp:usb{BS}vid_0b05&pid_1ace{BS}5&omni"
    path = f"{BS}{BS}?{BS}hid#vid_0b05&pid_1ace&mi_02{col}#7&omni"
    info = {
        "vendor_id": 0x0B05,
        "product_id": OMNI,
        "interface_number": 2,
        "usage_page": page,
        "usage": 0x0001,
        "product_string": "ROG OMNI RECEIVER",
        "physical_id": parent,
        "path": path,
        "input_report_proof": "caps",
        "output_report_proof": "descriptor",
        "input_report_sizes": {rid: 64},
        "output_report_sizes": {rid: 64},
        "input_report_length": 64,
        "output_report_length": 64,
    }
    info.update(changes)
    return info


class FakeOmniDevice:
    def __init__(self, packets=()):
        self.packets = list(packets)
        self.writes = []
        self.closed = False

    def open_path(self, path):
        self.path = path

    def write(self, packet):
        self.writes.append(bytes(packet))
        return len(packet)

    def read(self, size, timeout):
        return list(self.packets.pop(0)) if self.packets else []

    def close(self):
        self.closed = True


def f3_entry(**changes):
    info = {
        "vendor_id": 0x391D,
        "product_id": 0x1A1A,
        "product_string": "Dragonfly 3 MasterGT",
        "usage_page": 0xFF02,
        "usage": 0x0002,
        "interface_number": 1,
        "path": F3_PATH,
        "physical_id": F3_PHYSICAL,
        "input_report_length": 17,
        "output_report_length": 17,
        "input_report_sizes": {"8": 17},
        "output_report_sizes": {"8": 17},
        "input_report_proof": "caps",
        "output_report_proof": "caps",
    }
    info.update(changes)
    return info


class FakePulsarBus:
    def __init__(self, replies=()):
        self.replies = list(replies)
        self.opened = []
        self.closed = []
        self.writes = []

    def device(self):
        bus = self

        class Handle:
            def open_path(self, path):
                self.path = path
                self.written = False
                bus.opened.append(path)

            def write(self, packet):
                bus.writes.append((self.path, bytes(packet)))
                self.written = True
                return len(packet)

            def read(self, length, timeout):
                if not self.written:
                    return []
                return list(bus.replies.pop(0)) if bus.replies else []

            def close(self):
                bus.closed.append(getattr(self, "path", None))

        return Handle()


@contextmanager
def f3_fixture(replies=(), inventory=None):
    bus = FakePulsarBus(replies)
    inv = [f3_entry()] if inventory is None else list(inventory)
    with patch.object(P, "hid", bus), \
         patch.object(P.hidlist, "enumerate", side_effect=lambda vid=0: [d for d in inv if not vid or d.get("vendor_id") == vid]):
        yield bus, inv


class CommunityPRBatch2Tests(unittest.TestCase):
    def test_pr123_scope2_96_reads_level_and_keeps_charging_unknown(self):
        # Contributor capture (#123): paired 1AB0 (chan 2, type 4), battery 46%.
        paired = bytearray(64)
        paired[:9] = bytes([1, 0xA0, 0, 1, 0, 0xB0, 0x1A, 2, 4])
        power = bytearray(64)
        power[:10] = bytes([2, 0x12, 1, 0, 0, 0, 46, 0xFF, 0, 0])
        ident = FakeOmniDevice([paired])
        kbd = FakeOmniDevice([power])
        infos = [omni_port(), omni_port("&col01", page=0xFF02, rid="1")]
        with patch.object(battery_hid.hidlist, "enumerate", return_value=infos), \
             patch.object(battery_hid.hid, "device", Mock(side_effect=[ident, kbd])):
            cards = A.AsusAzothProvider().poll()
        self.assertEqual(len(cards), 1)
        st = cards[0]
        self.assertEqual(st.name, "ROG Strix Scope II 96 Wireless")
        self.assertEqual(st.level, 46)
        self.assertFalse(st.charging)
        self.assertFalse(st.charging_known)
        self.assertEqual(st.verification_level, "C")
        self.assertEqual(ident.writes, [b"\x01\xa0".ljust(64, b"\0")])
        self.assertEqual(kbd.writes, [b"\x02\x12\x01".ljust(64, b"\0")])
        self.assertTrue(ident.closed and kbd.closed)

        # Even if reply[9] == 1, Scope II 96 keeps charging_known=False (untested charging).
        power[9] = 1
        ident2 = FakeOmniDevice([paired])
        kbd2 = FakeOmniDevice([power])
        with patch.object(battery_hid.hidlist, "enumerate", return_value=infos), \
             patch.object(battery_hid.hid, "device", Mock(side_effect=[ident2, kbd2])):
            st2, = A.AsusAzothProvider().poll()
        self.assertEqual(st2.level, 46)
        self.assertFalse(st2.charging)
        self.assertFalse(st2.charging_known)

    def test_pr66_vgn_f3_mastergt_capture_strict_gates_and_history(self):
        provider = P.PulsarProvider()
        with f3_fixture([F3_CAPTURE]) as (bus, inv):
            cards = provider.poll()
            self.assertEqual(len(cards), 1)
            st = cards[0]
            self.assertEqual(st.key, f"pulsar:391d1a1a:{F3_PHYSICAL}")
            self.assertEqual(st.name, "VGN Dragonfly 3 MasterGT")
            self.assertEqual(st.level, 100)
            self.assertFalse(st.charging)
            self.assertFalse(st.charging_known)
            self.assertTrue(st.online)
            self.assertIsNone(st.dpi)
            self.assertEqual(st.physical_id, F3_PHYSICAL)
            self.assertEqual(bus.writes, [(F3_PATH, F3_REQUEST)])
            self.assertEqual(bus.opened, bus.closed)

            # Silent poll retains per-receiver history without charging_known.
            silent = provider.poll()
            self.assertEqual(len(silent), 1)
            self.assertEqual(silent[0].level, 100)
            self.assertFalse(silent[0].online)
            self.assertTrue(silent[0].wake_on_input)
            self.assertEqual(silent[0].physical_id, F3_PHYSICAL)
            self.assertFalse(silent[0].charging_known)

            # Receiver removal clears history.
            inv.clear()
            self.assertEqual(provider.poll(), [])

    def test_pr66_gates_block_unproven_or_neighbour_391d_devices(self):
        bad_cases = [
            [f3_entry(input_report_sizes=None)],
            [f3_entry(output_report_sizes={"8": 64}, output_report_length=64)],
            [f3_entry(interface_number=2)],
            [f3_entry(physical_id="collection:raw")],
            [f3_entry(physical_id=r"pnp:usb\vid_391d&pid_1a01\other")],
            [f3_entry(), f3_entry(path=rb"\\?\hid#vid_391d&pid_1a1a&mi_01&col02#dup")],
            [f3_entry(product_id=0x1A01)],  # VGN Dragonfly3 SE
            [f3_entry(product_id=0x1A04)],  # VGN ryMs
            [f3_entry(product_id=0x2A01)],  # VGN Beiying
        ]
        for inv in bad_cases:
            with f3_fixture([F3_CAPTURE], inventory=inv) as (bus, _):
                self.assertEqual(P.PulsarProvider().poll(), [])
                self.assertEqual((bus.opened, bus.writes), ([], []))

        # Bad checksum rejected.
        bad_crc = bytearray(F3_CAPTURE)
        bad_crc[-1] ^= 0x01
        with f3_fixture([bytes(bad_crc)]) as (bus, _):
            self.assertEqual(P.PulsarProvider().poll(), [])
            self.assertEqual(len(bus.writes), 1)

    def test_registration_known_ids_catalog_and_topology_fallback(self):
        self.assertIn(0x391D, server.PROVIDER_VIDS["pulsar"])
        self.assertEqual(server.recognized_candidate_count("pulsar", [f3_entry()]), 1)
        self.assertEqual(server.recognized_candidate_count("pulsar", [f3_entry(product_id=0x1A01)]), 0)
        self.assertEqual(server.recognized_candidate_count("pulsar", [f3_entry(product_id=0x2A01)]), 0)
        idx = known_ids.build_index()
        pulsar_ids = {(d["vendorId"], d["productId"]) for d in idx["devices"] if d["provider"] == "pulsar"}
        self.assertIn((0x391D, 0x1A1A), pulsar_ids)
        self.assertEqual(catalog.validate_catalog()["errors"], [])

        # Raw HID 1.11 descriptor for FF02:0002 Report 8 (17 bytes in/out).
        raw_desc = bytes([
            0x06, 0x02, 0xFF, 0x09, 0x02, 0xA1, 0x01,
            0x85, 0x08, 0x09, 0x01, 0x15, 0x00, 0x26, 0xFF, 0x00,
            0x75, 0x08, 0x95, 0x10, 0x81, 0x02, 0x09, 0x02, 0x91, 0x02, 0xC0,
        ])
        caps = SimpleNamespace(page=0xFF02, usage=0x0002,
                               input=17, output=17, feature=0,
                               ib=0, iv=0, ob=0, ov=0, fb=0, fv=0)
        info = {"path": F3_PATH, "input_report_sizes": None, "output_report_sizes": None}
        with patch.object(topology, "_read_report_descriptor", return_value=(raw_desc, None)):
            topology._descriptor_fallback(info, caps, ("input", "output"))
        self.assertEqual(info["input_report_sizes"], {"8": 17})
        self.assertEqual(info["output_report_sizes"], {"8": 17})


if __name__ == "__main__":
    unittest.main()
