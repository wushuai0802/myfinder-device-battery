"""Synthetic WOB Rainy75 V2 (36B0:3071 / 36B0:3075) tests; no device or network access."""
import ast
from contextlib import contextmanager
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ENGINE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ENGINE))
import _hidstub  # noqa: E402,F401
import catalog  # noqa: E402
import device_pack  # noqa: E402
import known_ids  # noqa: E402
import server  # noqa: E402
import topology  # noqa: E402
from providers import rainyrt as R  # noqa: E402

P = R.RainyRTProvider
PATH_24G = rb"\\?\hid#vid_36b0&pid_3071&mi_01&col02#synthetic"
PHYSICAL_24G = r"pnp:usb\vid_36b0&pid_3071\synthetic"
PATH_USB = rb"\\?\hid#vid_36b0&pid_3075&mi_01&col02#synthetic"
PHYSICAL_USB = r"pnp:usb\vid_36b0&pid_3075\synthetic"


def frame(level=97, charge=0, length=6, cmd=0xBB, start=0xBE, end=0xED, pad=256):
    wire_len = 6 if length == 0 else length
    head = bytearray(wire_len)
    head[0] = start
    head[1] = cmd
    head[2] = length
    head[3] = level
    head[4] = charge
    head[wire_len - 1] = end
    return bytes(head) + bytes(max(0, pad - len(head)))


def entry(**changes):
    info = dict(
        vendor_id=0x36B0,
        product_id=0x3071,
        product_string="WOB Rainy75 V2 2.4G",
        usage_page=0xFF60,
        usage=0x0061,
        interface_number=1,
        path=PATH_24G,
        physical_id=PHYSICAL_24G,
        input_report_length=257,
        input_report_sizes={"0": 257},
        input_report_proof="caps",
    )
    info.update(changes)
    return info


class FakeClock:
    def __init__(self, step=0.05):
        self.t = 100.0
        self.step = step

    def monotonic(self):
        self.t += self.step
        return self.t


class FakeHid:
    def __init__(self, chunks=(), error_at=None):
        self.chunks = list(chunks)
        self.error_at = error_at
        self.opened = []
        self.closed = []
        self.reads = []

    def device(self):
        bus = self

        class Handle:
            def open_path(self, path):
                self.path = path
                bus.opened.append(path)
                if bus.error_at == "open":
                    raise OSError("private-path")

            def read(self, length, timeout):
                bus.reads.append((length, timeout))
                if bus.error_at == "read":
                    raise OSError("private-path")
                return bus.chunks.pop(0) if bus.chunks else b""

            def write(self, *args):
                raise AssertionError("passive reader must never write")

            def send_feature_report(self, *args):
                raise AssertionError("passive reader must never send_feature_report")

            def get_feature_report(self, *args):
                raise AssertionError("passive reader must never get_feature_report")

            def close(self):
                bus.closed.append(getattr(self, "path", None))
                if bus.error_at == "close":
                    raise OSError("private-path")

        return Handle()


@contextmanager
def fixture(chunks=(), inventory=None, error_at=None, step=0.05):
    bus = FakeHid(chunks, error_at=error_at)
    inv = [entry()] if inventory is None else list(inventory)
    clock = FakeClock(step=step)
    with patch.object(R, "hid", bus), \
         patch.object(R.hidlist, "enumerate", side_effect=lambda vid=0: list(inv)), \
         patch.object(R.time, "monotonic", side_effect=clock.monotonic):
        yield bus, inv


class RainyRTTests(unittest.TestCase):
    def test_passive_only_ast(self):
        tree = ast.parse((ENGINE / "providers" / "rainyrt.py").read_text(encoding="utf-8"))
        forbidden = {"write", "send_feature_report", "get_feature_report", "send_output_report"}
        calls = {
            node.func.attr
            for node in ast.walk(tree)
            if isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
        }
        self.assertFalse(calls & forbidden)

    def test_parse_battery_valid_lengths_and_charge_states(self):
        self.assertEqual(R.parse_battery(frame(97, 0, length=6)), (97, False))
        self.assertEqual(R.parse_battery(frame(100, 1, length=10)), (100, False))
        self.assertEqual(R.parse_battery(frame(42, 2, length=14)), (42, True))
        self.assertEqual(R.parse_battery(frame(0, 0, length=0)), (0, False))
        self.assertEqual(R.parse_battery(list(frame(66, 2, length=6))), (66, True))

    def test_parse_battery_rejects_malformed_or_non_int(self):
        self.assertIsNone(R.parse_battery(frame(97, 0)[:6]))            # wrong payload length
        self.assertIsNone(R.parse_battery(frame(97, 0, pad=257)))       # extra byte
        self.assertIsNone(R.parse_battery(frame(97, 0, start=0xBF)))
        self.assertIsNone(R.parse_battery(frame(97, 0, cmd=0xAA)))
        self.assertIsNone(R.parse_battery(frame(97, 0, length=8)))
        self.assertIsNone(R.parse_battery(frame(97, 0, end=0xEE)))
        self.assertIsNone(R.parse_battery(frame(101, 0)))
        self.assertIsNone(R.parse_battery(frame(50, 3)))
        bad_bool = list(frame(50, 0))
        bad_bool[3] = True
        self.assertIsNone(R.parse_battery(bad_bool))
        self.assertIsNone(R.parse_battery("not-bytes"))

    def test_initial_listen_persistent_drain_silence_and_removal(self):
        provider = P()
        with fixture([frame(97, 0)], step=0.05) as (bus, inv):
            first = provider.poll()
            self.assertEqual(len(first), 1)
            self.assertEqual(first[0].level, 97)
            self.assertFalse(first[0].charging)
            self.assertTrue(first[0].charging_known)
            self.assertTrue(first[0].online)
            self.assertFalse(first[0].cached)
            self.assertEqual(first[0].verification_level, "C")
            self.assertTrue(first[0].unconfirmed)
            self.assertEqual(first[0].kind, "keyboard")
            self.assertEqual(len(bus.opened), 1)
            self.assertEqual(bus.closed, [])
            # Next poll drains queued reports on the retained open handle and keeps newest.
            bus.chunks.extend([frame(80, 0), frame(84, 2)])
            second = provider.poll()
            self.assertEqual(len(bus.opened), 1)
            self.assertEqual(len(second), 1)
            self.assertEqual(second[0].level, 84)
            self.assertTrue(second[0].charging)
            # Drain calls use 1ms timeout so already-open polls never stall.
            self.assertTrue(any(timeout == R.DRAIN_TIMEOUT_MS for _, timeout in bus.reads))
            # Silent poll retains historical card without closing handle.
            third = provider.poll()
            self.assertEqual(len(third), 1)
            self.assertEqual(third[0].level, 84)
            self.assertTrue(third[0].cached)
            self.assertFalse(third[0].online)
            self.assertFalse(third[0].charging_known)
            self.assertEqual(bus.closed, [])
            # Removal releases handle and clears history.
            inv.clear()
            fourth = provider.poll()
            self.assertEqual(fourth, [])
            self.assertEqual(len(bus.closed), 1)
        provider.close()

    def test_handle_reopens_and_clears_stale_history_when_path_changes(self):
        provider = P()
        alt_path = rb"\\?\hid#vid_36b0&pid_3071&mi_01&col02#synthetic2"
        with fixture([frame(91, 0)]) as (bus, inv):
            self.assertEqual(provider.poll()[0].level, 91)
            inv[0] = entry(path=alt_path)
            # Silent after identity change must NOT leak old cached reading.
            self.assertEqual(provider.poll(), [])
            self.assertEqual(bus.opened, [PATH_24G, alt_path])
            self.assertEqual(bus.closed, [PATH_24G])
            bus.chunks.append(frame(92, 0))
            rows = provider.poll()
            self.assertEqual(rows[0].level, 92)
        provider.close()

    def test_usb_pid_supported(self):
        provider = P()
        inv = [entry(product_id=0x3075, product_string="", path=PATH_USB, physical_id=PHYSICAL_USB)]
        with fixture([frame(55, 2)], inventory=inv) as (bus, _):
            rows = provider.poll()
            self.assertEqual(len(rows), 1)
            self.assertEqual(rows[0].level, 55)
            self.assertTrue(rows[0].charging)
            self.assertEqual(rows[0].name, R.PIDS[0x3075])
            self.assertEqual(bus.opened, [PATH_USB])
        provider.close()

    def test_gates_reject_unproven_or_ambiguous_devices(self):
        bad_inventories = [
            [entry(input_report_sizes=None)],
            [entry(input_report_sizes={"0": 257, "1": 64})],
            [entry(input_report_length=64, input_report_sizes={"0": 64})],
            [entry(physical_id="collection:" + PATH_24G.decode())],
            [entry(physical_id=r"pnp:usb\vid_36b0&pid_3075\wrongpid")],
            [entry(path=rb"\\?\hid#vid_36b0&pid_3002&mi_01#shared")],
            [entry(), entry(path=rb"\\?\hid#vid_36b0&pid_3071&mi_01&col03#dup")],
            [entry(product_id=0x3002)],
        ]
        for inv in bad_inventories:
            provider = P()
            with fixture([frame(90, 0)], inventory=inv) as (bus, _):
                self.assertEqual(provider.poll(), [])
                self.assertEqual(bus.opened, [])
            provider.close()

    def test_enumeration_error_retains_history_and_io_error_closes(self):
        provider = P()
        with fixture([frame(77, 0)]) as (bus, _):
            self.assertEqual(provider.poll()[0].level, 77)
            with patch.object(R.hidlist, "enumerate", side_effect=OSError("bus-busy")):
                kept = provider.poll()
                self.assertEqual(len(kept), 1)
                self.assertTrue(kept[0].cached)
                self.assertEqual(kept[0].level, 77)
            bus.error_at = "read"
            after_err = provider.poll()
            self.assertEqual(len(after_err), 1)
            self.assertTrue(after_err[0].cached)
            self.assertEqual(len(bus.closed), 1)
            self.assertNotIn("private-path", " ".join(provider.diagnostics()))
        provider.close()

    def test_registration_catalog_known_ids_and_server_hooks(self):
        self.assertIn(("rainyrt", "RainyRTProvider"), server.PROVIDERS)
        self.assertEqual(server.PROVIDER_VIDS["rainyrt"], (0x36B0,))
        self.assertIn("rainyrt", server.IDLE_BACKOFF_EXEMPT)
        self.assertIn("rainyrt", server.READER_DIAGNOSTICS)
        self.assertIn("rainyrt", device_pack.BRAND_PROVIDERS)
        self.assertEqual(server.recognized_candidate_count("rainyrt", [entry()]), 1)
        self.assertEqual(server.recognized_candidate_count("rainyrt", [entry(product_id=0x3002)]), 0)
        idx = known_ids.build_index()
        indexed = {(d["vendorId"], d["productId"]) for d in idx["devices"] if d["provider"] == "rainyrt"}
        self.assertEqual(indexed, {(0x36B0, 0x3071), (0x36B0, 0x3075)})
        self.assertEqual(catalog.validate_catalog()["errors"], [])

    def test_topology_descriptor_fallback_and_rejection(self):
        # Raw HID 1.11 descriptor for FF60:0061 with 256-byte unnumbered Input & Output.
        raw_desc = bytes([
            0x06, 0x60, 0xFF,        # Usage Page (0xFF60)
            0x09, 0x61,              # Usage (0x61)
            0xA1, 0x01,              # Collection (Application)
            0x09, 0x62,              # Usage (0x62)
            0x15, 0x00,              # Logical Min (0)
            0x26, 0xFF, 0x00,        # Logical Max (255)
            0x75, 0x08,              # Report Size (8)
            0x96, 0x00, 0x01,        # Report Count (256)
            0x81, 0x02,              # Input (Data,Var,Abs)
            0x09, 0x63,              # Usage (0x63)
            0x91, 0x02,              # Output (Data,Var,Abs)
            0xC0,                    # End Collection
        ])
        caps = SimpleNamespace(page=0xFF60, usage=0x0061,
                               input=257, output=257, feature=0,
                               ib=0, iv=0, ob=0, ov=0, fb=0, fv=0)
        info = {"path": PATH_24G, "input_report_sizes": None}
        with patch.object(topology, "_read_report_descriptor", return_value=(raw_desc, None)):
            topology._descriptor_fallback(info, caps, ("input",))
        self.assertEqual(info["input_report_sizes"], {"0": 257})
        self.assertEqual(info["input_report_proof"], "descriptor")

        # When raw descriptor is unavailable, fallback stays None (no max-only bypass).
        info_fail = {"path": PATH_24G, "input_report_sizes": None}
        with patch.object(topology, "_read_report_descriptor", return_value=(None, "descriptor read failed")):
            topology._descriptor_fallback(info_fail, caps, ("input",))
        self.assertIsNone(info_fail["input_report_sizes"])
        self.assertEqual(info_fail["input_report_proof"], "failed")


if __name__ == "__main__":
    unittest.main()
