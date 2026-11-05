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
    receiver = {}

    def entry(self, physical="receiver-a", **changes):
        return entry(physical, **(self.receiver | changes))

    def fixture(self, frames=(), inventory=None, **kwargs):
        return fixture(frames, [self.entry()] if inventory is None else inventory, **kwargs)

    def test_complete_fixed_requests_and_model_gate(self):
        identify = bytes.fromhex("13 05 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 19")
        battery = bytes.fromhex("13 4a 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 5e")
        self.assertEqual(bytes(M.identity_request()), identify)
        self.assertEqual(bytes(M.make_request()), battery)
        for request in (identify, battery):
            self.assertEqual(request[-1], sum(request[:-1]) & 255)
        with self.fixture([identity(), reply(63, 0x10)]) as bus:
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
            with self.subTest(model=name), self.fixture([identity(code), reply()]):
                status, = P().poll()
                self.assertEqual(status.name, "MCHOSE " + name)

    def test_unknown_model_and_identity_silence_never_send_battery(self):
        for frames in ([], [identity("000000000000")],
                       [identity("0300000001c5"), identity(), reply()],
                       [M.identity_request()], [reply()]):
            with self.subTest(frames=frames), self.fixture(frames) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual([frame for _, frame in bus.writes], [M.identity_request()])
                self.assertEqual(bus.opened, bus.closed)

    def test_non_mchose_unknown_and_wired_names_never_open_or_send(self):
        for product in (None, "", "AULA F87", "NEWMEN X75", "SCC100", "XUNSHE M2",
                        "MCHOSE G75", "MCHOSE K99 Gaming Keyboard", "MCHOSE G98",
                        "2.4G Wireless Receiver-Unknown", "2.4G Wireless Receiver-X75_V2"):
            with self.subTest(product=product), self.fixture([identity(), reply()],
                                                            [self.entry(product_string=product)]) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual((bus.opened, bus.writes), ([], []))

    def test_both_official_generic_wireless_names_require_device_identity(self):
        for product in ("2.4G Wireless Receiver", "MCHOSE 2.4G Wireless"):
            with self.subTest(product=product), self.fixture([identity(), reply()],
                                                            [self.entry(product_string=product)]):
                self.assertEqual(len(P().poll()), 1)

    def test_synthetic_battery_boundaries_and_little_endian_nibbles(self):
        for level in (1, 63, 99, 100):
            for full in (0, 1):
                for charge in (0, 1):
                    with self.subTest(level=level, full=full, charge=charge):
                        self.assertEqual(M.parse_reply(reply(level, full | charge << 4)),
                                         (level, charge == 1 and level < 100, ""))
        for flags in (2, 0x20, 0xF0, 0xFF):
            self.assertIsNone(M.parse_reply(reply(63, flags)))
        # A sleeping keyboard may report zero; that is no reading, not 0 %.
        self.assertIsNone(M.parse_reply(reply(0, 0)))
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
        with self.fixture([identity(), M.make_request(), reply()]) as bus:
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
        inventories = [[self.entry(**defect)] for defect in defects]
        inventories.append([self.entry(), self.entry(path=b"duplicate")])
        for inventory in inventories:
            with self.subTest(inventory=inventory), self.fixture([identity(), reply()], inventory) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual((bus.opened, bus.writes), ([], []))

    def test_silence_bounded_reads_short_writes_and_no_dangerous_commands(self):
        for frames, options in (([identity()], {}), ([identity()] + [reply(255)] * 100, {}),
                                ([identity(), reply()], {"write_result": 19}),
                                ([identity(), reply()], {"error_at": "read"})):
            with self.subTest(options=options), self.fixture(frames, **options) as bus:
                self.assertEqual(P().poll(), [])
                self.assertLessEqual(len(bus.reads), 33)
                self.assertEqual(bus.opened, bus.closed)
                # Exactly these read packets; no writes/feature APIs exist on the fake.
                for _, frame in bus.writes:
                    self.assertIn(frame, (M.identity_request(), M.make_request()))
                    self.assertNotIn(frame[1], (0x01, 0x02, 0x03, 0x04, 0x06, 0x09))

    def test_identify_once_per_plug_in_and_remember_unknown_models(self):
        # Recognized: later polls send only the battery query.
        provider = P()
        with self.fixture([identity(), reply()]):
            self.assertEqual(len(provider.poll()), 1)
        with self.fixture([reply(70)]) as bus:
            self.assertEqual(provider.poll()[0].level, 70)
            self.assertEqual([frame for _, frame in bus.writes], [M.make_request()])
        # Unrecognized: one identity frame, then nothing until the receiver leaves.
        provider = P()
        with self.fixture([identity("000000000000")]) as bus:
            self.assertEqual(provider.poll(), [])
            self.assertEqual([frame for _, frame in bus.writes], [M.identity_request()])
        with self.fixture([identity(), reply()]) as bus:
            self.assertEqual(provider.poll(), [])
            self.assertEqual(bus.writes, [])
        # Receiver absent for one poll: the next plug-in is identified afresh.
        with self.fixture([], inventory=[]):
            self.assertEqual(provider.poll(), [])
        with self.fixture([identity(), reply()]) as bus:
            self.assertEqual(len(provider.poll()), 1)
            self.assertEqual([frame for _, frame in bus.writes], [M.identity_request(), M.make_request()])

    def test_silent_identity_backs_off_exponentially(self):
        provider = P()

        def poll_at(now, frames):
            with self.fixture(frames) as bus:
                bus.now = now
                found = provider.poll()
            return found, [frame for _, frame in bus.writes]

        ask = [M.identity_request()]
        self.assertEqual(poll_at(0, []), ([], ask))              # 1st silence
        self.assertEqual(poll_at(5, []), ([], ask))              # retried once at once
        self.assertEqual(poll_at(60, [identity(), reply()]), ([], []))   # 60 s back-off
        self.assertEqual(poll_at(66, []), ([], ask))             # 3rd silence: 120 s
        self.assertEqual(poll_at(180, [identity(), reply()]), ([], []))
        found, writes = poll_at(187, [identity(), reply()])      # keyboard awake
        self.assertEqual(len(found), 1)
        self.assertEqual(writes, ask + [M.make_request()])
        self.assertEqual(P.IDENTIFY_BACKOFF_SECONDS, (60, 1800))   # cap: 30 minutes


class Mchose41e4KeyboardTests(MchoseByKeyboardTests):
    """Run the complete FA09 safety suite on a synthetic #169-like receiver."""
    receiver = dict(vendor_id=0x41E4, product_id=0x2001,
                    product_string="MCHOSE 2.4G Wireless")

    def test_g87_and_g75_v2_use_device_model_not_pid_default(self):
        for code, name in (("03000000021c", "G87"), ("030000000335", "G75 V2")):
            with self.subTest(model=name), self.fixture([identity(code), reply(49)]) as bus:
                status, = P().poll()
                self.assertEqual((status.name, status.level), ("MCHOSE " + name, 49))
                self.assertEqual([frame for _, frame in bus.writes],
                                 [M.identity_request(), M.make_request()])

    def test_other_41e4_pids_and_crossed_vendor_pid_pairs_never_open(self):
        pairs = [(0x41E4, pid) for pid in (0x2201, 0x2004, 0x2203, 0x1101, 0x1102,
                                          0x1204, 0x1001, 0x1003, 0x2101, 0xFA09)]
        pairs.append((0x3554, 0x2001))
        for vid, pid in pairs:
            for page in (0xFF04, 0xFF00, 0xFF01, 0xFF0B):
                with self.subTest(vid=vid, pid=pid, page=page), self.fixture(
                        [identity(), reply()], [self.entry(vendor_id=vid, product_id=pid,
                                                          usage_page=page)]) as bus:
                    self.assertEqual(P().poll(), [])
                    self.assertEqual((bus.opened, bus.writes), ([], []))

    def test_both_vendors_keep_independent_identity_and_silent_backoff(self):
        fa09, mchose = entry("fa09"), self.entry("mchose")
        provider = P()
        # One known receiver and one unknown receiver share no model state.
        with self.fixture([identity(), reply(), identity("000000000000")], [fa09, mchose]):
            self.assertEqual([s.physical_id for s in provider.poll()], ["fa09"])
        with self.fixture([reply(70)], [fa09, mchose]) as bus:
            self.assertEqual([s.level for s in provider.poll()], [70])
            self.assertEqual(bus.writes, [(fa09["path"], M.make_request())])
        # Removing only the MCHOSE receiver must not forget FA09's model.
        with self.fixture([reply(71)], [fa09]):
            self.assertEqual([s.level for s in provider.poll()], [71])
        with self.fixture([reply(72), identity("03000000021c"), reply(42)], [fa09, mchose]) as bus:
            self.assertEqual([s.name for s in provider.poll()], ["MCHOSE X75 V2", "MCHOSE G87"])
            self.assertEqual(bus.writes, [(fa09["path"], M.make_request()),
                                         (mchose["path"], M.identity_request()),
                                         (mchose["path"], M.make_request())])
        # A silent receiver's back-off survives the other vendor's inventory.
        provider = P()
        for now, frames, expected in ((0, [identity(), reply()], 3),
                                       (5, [reply()], 2), (30, [reply()], 1)):
            with self.subTest(now=now), self.fixture(frames, [fa09, mchose]) as bus:
                bus.now = now
                self.assertEqual(len(provider.poll()), 1)
                self.assertEqual(len(bus.writes), expected)

if __name__ == "__main__":
    unittest.main()
