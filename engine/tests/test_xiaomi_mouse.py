"""Synthetic Xiaomi Mi Mouse 4 Pro (2717:50FC) tests; no device or network access."""
from contextlib import contextmanager
from pathlib import Path
import sys
from types import SimpleNamespace
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
from providers import xiaomi_mouse as X  # noqa: E402

P = X.XiaomiMouseProvider
PATH = rb"\\?\hid#vid_2717&pid_50fc&mi_02&col01#synthetic"
PHYSICAL = r"pnp:usb\vid_2717&pid_50fc\synthetic"


def reply(level=85, state=0x01, length_byte=0x02, cmd=X.CMD_BATTERY, lead=0x00, size=32):
    head = bytes([lead, cmd, length_byte, state, level])
    return head + bytes(max(0, size - len(head)))


def entry(**changes):
    info = dict(
        vendor_id=0x2717,
        product_id=0x50FC,
        product_string="Xiaomi Mi Mouse 4 Pro Receiver",
        usage_page=0xFF03,
        usage=0x0000,
        interface_number=2,
        path=PATH,
        physical_id=PHYSICAL,
        input_report_length=33,
        output_report_length=33,
        input_report_sizes={"0": 33},
        output_report_sizes={"0": 33},
        input_report_proof="caps",
        output_report_proof="caps",
    )
    info.update(changes)
    return info


class FakeHid:
    def __init__(self, replies=(), write_result=33, error_at=None):
        self.replies = list(replies)
        self.write_result = write_result
        self.error_at = error_at
        self.opened = []
        self.closed = []
        self.writes = []
        self.reads = []

    def device(self):
        bus = self

        class Handle:
            def open_path(self, path):
                self.path = path
                bus.opened.append(path)
                if bus.error_at == "open":
                    raise OSError("private-path")

            def write(self, packet):
                if type(packet) is not bytes or packet != X.QUERY:
                    raise AssertionError("unexpected output write")
                bus.writes.append(packet)
                if bus.error_at == "write":
                    raise OSError("private-path")
                return bus.write_result

            def read(self, length, timeout):
                bus.reads.append((length, timeout))
                if bus.error_at == "read":
                    raise OSError("private-path")
                return bus.replies.pop(0) if bus.replies else b""

            def send_feature_report(self, *args):
                raise AssertionError("feature write forbidden")

            def get_feature_report(self, *args):
                raise AssertionError("feature read forbidden")

            def close(self):
                bus.closed.append(getattr(self, "path", None))
                if bus.error_at == "close":
                    raise OSError("private-path")

        return Handle()


@contextmanager
def fixture(replies=(), inventory=None, write_result=33, error_at=None):
    bus = FakeHid(replies, write_result=write_result, error_at=error_at)
    inv = [entry()] if inventory is None else list(inventory)
    with patch.object(battery_hid, "hid", bus), \
         patch.object(battery_hid.hidlist, "enumerate", side_effect=lambda vid=0: list(inv)):
        yield bus


class XiaomiMouseTests(unittest.TestCase):
    def test_parse_battery_valid_and_rejects_malformed(self):
        self.assertEqual(X.parse_battery(reply(85)), 85)
        self.assertEqual(X.parse_battery(list(reply(1))), 1)
        self.assertEqual(X.parse_battery(reply(100)), 100)
        self.assertIsNone(X.parse_battery(reply(0)))                  # asleep/off
        self.assertIsNone(X.parse_battery(reply(101)))                # out of range
        self.assertIsNone(X.parse_battery(X.QUERY_PAYLOAD))           # request echo
        self.assertIsNone(X.parse_battery(reply(85, cmd=0x15)))
        self.assertIsNone(X.parse_battery(reply(85, length_byte=0x01)))
        self.assertIsNone(X.parse_battery(reply(85, state=0x00)))
        self.assertIsNone(X.parse_battery(reply(85, lead=0x01)))
        self.assertIsNone(X.parse_battery(reply(85, size=31)))
        self.assertIsNone(X.parse_battery(reply(85, size=33)))
        bad_bool = list(reply(85))
        bad_bool[4] = True
        self.assertIsNone(X.parse_battery(bad_bool))

    def test_poll_reads_battery_skips_noise_and_closes(self):
        noise = bytes([0x00, 0x05]) + bytes(30)
        with fixture([noise, reply(85)]) as bus:
            statuses = P().poll()
        self.assertEqual(len(statuses), 1)
        st = statuses[0]
        self.assertEqual(st.key, f"xiaomi_mouse:{PHYSICAL}")
        self.assertEqual(st.name, "Xiaomi Mi Mouse 4 Pro")
        self.assertEqual(st.level, 85)
        self.assertFalse(st.charging)
        self.assertFalse(st.charging_known)
        self.assertTrue(st.online)
        self.assertEqual(st.kind, "mouse")
        self.assertEqual(st.verification_level, "C")
        self.assertTrue(st.unconfirmed)
        self.assertEqual(bus.writes, [X.QUERY])
        self.assertEqual(len(bus.reads), 2)
        self.assertEqual(bus.reads[0][0], 32)
        self.assertEqual(bus.opened, bus.closed)

    def test_asleep_zero_reply_stops_without_reading(self):
        for state in (0x00, 0x01):
            with self.subTest(state=state), fixture([reply(0, state=state), reply(85)]) as bus:
                provider = P()
                self.assertEqual(provider.poll(), [])
                self.assertEqual(len(bus.reads), 1)
                self.assertIn("device asleep/offline; no new reading; connection unknown",
                              provider.diagnostics())

    def test_gates_block_unproven_wrong_interface_or_duplicate(self):
        bad_inventories = [
            [entry(input_report_sizes=None)],
            [entry(output_report_sizes=None)],
            [entry(input_report_sizes={"0": 33, "1": 33})],
            [entry(input_report_length=65, input_report_sizes={"0": 65})],
            [entry(interface_number=1)],
            [entry(usage_page=0xFF02)],
            [entry(physical_id="collection:" + PATH.decode())],
            [entry(physical_id=r"pnp:usb\vid_2717&pid_5111\other")],
            [entry(path=rb"\\?\hid#vid_2717&pid_5111&mi_02#other")],
            [entry(), entry(path=rb"\\?\hid#vid_2717&pid_50fc&mi_02&col02#dup")],
            [entry(product_id=0x5111)],
        ]
        for inv in bad_inventories:
            with fixture([reply(85)], inventory=inv) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(bus.opened, [])
                self.assertEqual(bus.writes, [])

    def test_io_and_short_write_failures_close_cleanly(self):
        with fixture([reply(85)], write_result=16) as bus:
            self.assertEqual(P().poll(), [])
            self.assertEqual(len(bus.writes), 1)
            self.assertEqual(bus.reads, [])
            self.assertEqual(bus.opened, bus.closed)
        for error_at in ("open", "write", "read"):
            with self.subTest(error_at=error_at), fixture([reply(85)], error_at=error_at) as bus:
                provider = P()
                self.assertEqual(provider.poll(), [])
                self.assertEqual(bus.opened, bus.closed)
                self.assertNotIn("private-path", " ".join(provider.diagnostics()))

    def test_registration_catalog_known_ids_and_pre_dispatch(self):
        self.assertIn(("xiaomi_mouse", "XiaomiMouseProvider"), server.PROVIDERS)
        self.assertEqual(server.PROVIDER_VIDS["xiaomi_mouse"], (0x2717,))
        self.assertIn("xiaomi_mouse", server.NO_CHARGING_EVIDENCE)
        self.assertIn("xiaomi_mouse", server.READER_DIAGNOSTICS)
        self.assertIn("xiaomi_mouse", device_pack.BRAND_PROVIDERS)
        self.assertEqual(server.recognized_candidate_count("xiaomi_mouse", [entry()]), 1)
        self.assertEqual(server.recognized_candidate_count("xiaomi_mouse", [entry(product_id=0x5111)]), 0)
        idx = known_ids.build_index()
        indexed = {(d["vendorId"], d["productId"]) for d in idx["devices"] if d["provider"] == "xiaomi_mouse"}
        self.assertEqual(indexed, {(0x2717, 0x50FC)})
        self.assertEqual(catalog.validate_catalog()["errors"], [])

    def test_topology_descriptor_fallback_proves_33_byte_unnumbered(self):
        raw_desc = bytes([
            0x06, 0x03, 0xFF,        # Usage Page (0xFF03)
            0x09, 0x00,              # Usage (0x00)
            0xA1, 0x01,              # Collection (Application)
            0x09, 0x01,              # Usage (0x01)
            0x15, 0x00,              # Logical Min (0)
            0x26, 0xFF, 0x00,        # Logical Max (255)
            0x75, 0x08,              # Report Size (8)
            0x95, 0x20,              # Report Count (32)
            0x81, 0x02,              # Input (Data,Var,Abs)
            0x09, 0x02,              # Usage (0x02)
            0x91, 0x02,              # Output (Data,Var,Abs)
            0xC0,                    # End Collection
        ])
        caps = SimpleNamespace(page=0xFF03, usage=0x0000,
                               input=33, output=33, feature=0,
                               ib=0, iv=0, ob=0, ov=0, fb=0, fv=0)
        info = {"path": PATH, "input_report_sizes": None, "output_report_sizes": None}
        with patch.object(topology, "_read_report_descriptor", return_value=(raw_desc, None)):
            topology._descriptor_fallback(info, caps, ("input", "output"))
        self.assertEqual(info["input_report_sizes"], {"0": 33})
        self.assertEqual(info["output_report_sizes"], {"0": 33})
        self.assertEqual(info["input_report_proof"], "descriptor")
        self.assertEqual(info["output_report_proof"], "descriptor")


if __name__ == "__main__":
    unittest.main()
