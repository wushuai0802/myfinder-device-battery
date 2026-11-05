"""Synthetic BY replies and descriptor fixtures; NOT captures or hardware tests."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
from providers import mchose_by_keyboard as M
from test_new_brands import entry as hid_entry, fixture as hid_fixture

P = M.MchoseByKeyboardProvider


def entry(physical="receiver-a", **changes):
    changes.setdefault("product_string", "2.4G Wireless Receiver")
    return hid_entry(P, physical, **changes)


def packet(command, payload):
    # Synthetic envelope. YY=payload length and additive reply trailer are
    # fixture choices, not verified protocol requirements or a real capture.
    data = [0x13, command, 1, 0, len(payload)] + list(payload)
    data += [0] * (19 - len(data))
    return data + [sum(data) & 255]


def identity(code="0300000001c6"):
    return packet(5, bytes.fromhex(code))


def reply(level=63, flags=0):
    return packet(0x4A, [level, flags])


def fixture(frames=(), inventory=None, **kwargs):
    return hid_fixture(P, frames, [entry()] if inventory is None else inventory, **kwargs)


class MchoseByKeyboardTests(unittest.TestCase):
    def test_complete_fixed_requests_and_model_gate(self):
        identify = bytes.fromhex("13 05 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 19")
        battery = bytes.fromhex("13 4a 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 5e")
        self.assertEqual(bytes(M.identity_request()), identify)
        self.assertEqual(bytes(M.make_request()), battery)
        for request in (identify, battery):
            self.assertEqual(request[-1], sum(request[:-1]) & 255)
        with fixture([identity(), reply(63, 0x10)]) as bus:
            status, = P().poll()
        self.assertEqual((status.name, status.kind, status.level, status.charging),
                         ("MCHOSE X75 V2", "keyboard", 63, True))
        self.assertEqual(status.verification_level, "C")
        self.assertTrue(status.charging_known)
        self.assertEqual([bytes(frame) for _, frame in bus.writes], [identify, battery])
        self.assertEqual(bus.opened, bus.closed)
        self.assertTrue(all(length == 20 for length, _ in bus.reads))

    def test_synthetic_model_identifiers_from_official_table(self):
        cases = {"0300000001df": "K99 V2", "030000000132": "G98",
                 "0300000000d6": "X75", "0300000001c6": "X75 V2",
                 "030000000174": "GX87", "0300000001ed": "GX87 V2",
                 "030000000135": "K87", "030000000115": "G75 Pro"}
        for code, name in cases.items():
            with self.subTest(model=name), fixture([identity(code), reply()]):
                status, = P().poll()
                self.assertEqual(status.name, "MCHOSE " + name)

    def test_unknown_model_and_identity_silence_never_send_battery(self):
        for frames in ([], [identity("000000000000")],
                       [identity("0300000001c5"), identity(), reply()],
                       [M.identity_request()], [reply()]):
            with self.subTest(frames=frames), fixture(frames) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual([frame for _, frame in bus.writes], [M.identity_request()])
                self.assertEqual(bus.opened, bus.closed)

    def test_non_mchose_unknown_and_wired_names_never_open_or_send(self):
        for product in (None, "", "AULA F87", "NEWMEN X75", "SCC100", "XUNSHE M2",
                        "MCHOSE G75", "MCHOSE K99 Gaming Keyboard", "MCHOSE G98",
                        "2.4G Wireless Receiver-Unknown", "2.4G Wireless Receiver-X75_V2"):
            with self.subTest(product=product), fixture([identity(), reply()],
                                                       [entry(product_string=product)]) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual((bus.opened, bus.writes), ([], []))

    def test_both_official_generic_wireless_names_require_device_identity(self):
        for product in ("2.4G Wireless Receiver", "MCHOSE 2.4G Wireless"):
            with self.subTest(product=product), fixture([identity(), reply()],
                                                       [entry(product_string=product)]):
                self.assertEqual(len(P().poll()), 1)

    def test_synthetic_battery_boundaries_and_little_endian_nibbles(self):
        for level in (0, 1, 63, 99, 100):
            for full in (0, 1):
                for charge in (0, 1):
                    with self.subTest(level=level, full=full, charge=charge):
                        self.assertEqual(M.parse_reply(reply(level, full | charge << 4)),
                                         (level, charge == 1 and level < 100, ""))
        for flags in (2, 0x20, 0xF0, 0xFF):
            self.assertIsNone(M.parse_reply(reply(63, flags)))
        for level in (101, 255):
            self.assertIsNone(M.parse_reply(reply(level)))
        self.assertIsNone(M.parse_reply(M.make_request()))

    def test_bad_headers_lengths_and_sequence_do_not_enable_or_return_readings(self):
        for original, parse in ((identity(), M.parse_model), (reply(), M.parse_reply)):
            frames = [original[:-1], original + [0], []]
            for offset, value in ((0, 0), (0, 6), (1, 6), (2, 0), (2, 2), (3, 1), (5, 256)):
                frame = list(original)
                frame[offset] = value
                frames.append(frame)
            for frame in frames:
                with self.subTest(frame=frame, parse=parse.__name__):
                    self.assertIsNone(parse(frame))
        with fixture([identity(), M.make_request(), reply()]) as bus:
            self.assertEqual([s.level for s in P().poll()], [63])
            self.assertEqual(len(bus.writes), 2)

    def test_caps_identity_collection_or_uniqueness_defects_prevent_all_io(self):
        defects = [dict(vendor_id=0x258A), dict(product_id=0xFA07),
                   dict(vendor_id=0x05AC, product_id=0x024F), dict(usage_page=0xFF02),
                   dict(usage=1), dict(path=b""), dict(physical_id="collection:unknown")]
        for direction in ("input", "output"):
            defects += [{direction + "_report_length": size} for size in (None, 19, 21, 64)]
            defects += [{direction + "_report_sizes": sizes} for sizes in
                        (None, {}, {"19": 19}, {"6": 20}, {"19": 20, "6": 20})]
        inventories = [[entry(**defect)] for defect in defects]
        inventories.append([entry(), entry(path=b"duplicate")])
        for inventory in inventories:
            with self.subTest(inventory=inventory), fixture([identity(), reply()], inventory) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual((bus.opened, bus.writes), ([], []))

    def test_silence_bounded_reads_short_writes_and_no_dangerous_commands(self):
        for frames, options in (([identity()], {}), ([identity()] + [reply(255)] * 100, {}),
                                ([identity(), reply()], {"write_result": 19}),
                                ([identity(), reply()], {"error_at": "read"})):
            with self.subTest(options=options), fixture(frames, **options) as bus:
                self.assertEqual(P().poll(), [])
                self.assertLessEqual(len(bus.reads), 33)
                self.assertEqual(bus.opened, bus.closed)
                # Exactly these read packets; no writes/feature APIs exist on the fake.
                for _, frame in bus.writes:
                    self.assertIn(frame, (M.identity_request(), M.make_request()))
                    self.assertNotIn(frame[1], (0x01, 0x02, 0x03, 0x04, 0x06, 0x09))

    def test_model_is_rechecked_after_repairing_or_sleep(self):
        provider = P()
        with fixture([identity(), reply()]):
            self.assertEqual(len(provider.poll()), 1)
        with fixture([identity("000000000000"), reply()]) as bus:
            self.assertEqual(provider.poll(), [])
            self.assertEqual([frame for _, frame in bus.writes], [M.identity_request()])


if __name__ == "__main__":
    unittest.main()
