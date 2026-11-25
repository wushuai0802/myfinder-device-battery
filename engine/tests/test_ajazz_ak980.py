"""Synthetic AJAZZ AK980 PRO (wired 0C45:8039 / 2.4G 0C45:FEFE) tests."""
from contextlib import contextmanager
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
from providers import ajazz_ak980 as A  # noqa: E402

P = A.AjazzAk980Provider
WIRED_PATH = rb"\\?\hid#vid_0c45&pid_8039&mi_02&col01#synthetic"
WIRED_LED_PATH = rb"\\?\hid#vid_0c45&pid_8039&mi_03&col01#synthetic"
WIRED_PARENT = r"pnp:usb\vid_0c45&pid_8039\synthetic"
DONGLE_PATH = rb"\\?\hid#vid_0c45&pid_fefe&mi_03&col01#synthetic"
DONGLE_MI04_PATH = rb"\\?\hid#vid_0c45&pid_fefe&mi_04&col01#synthetic"
DONGLE_PARENT = r"pnp:usb\vid_0c45&pid_fefe\synthetic"


def reply(level=100, work_mode=2, charge=0, cmd=0x10, vid=0x0C45, pid=0x8039, length=64):
    head = bytes([0x55, cmd, 0x30, 0x00, 0x00, 0x00, 0x01, 0x00])
    payload = bytearray(48)
    payload[0] = 0x40
    payload[4:6] = vid.to_bytes(2, "little")
    payload[6:8] = pid.to_bytes(2, "little")
    payload[16] = work_mode
    payload[17] = level
    payload[18] = charge
    raw = head + bytes(payload)
    return raw[:length].ljust(length, b"\0")


def wired_entry(**changes):
    info = dict(
        vendor_id=0x0C45,
        product_id=0x8039,
        product_string="AK980 PRO",
        usage_page=0xFF68,
        usage=0x0061,
        interface_number=2,
        path=WIRED_PATH,
        physical_id=WIRED_PARENT,
        input_report_length=65,
        output_report_length=65,
        input_report_sizes={"0": 65},
        output_report_sizes={"0": 65},
    )
    info.update(changes)
    return info


def dongle_entry(**changes):
    info = dict(
        vendor_id=0x0C45,
        product_id=0xFEFE,
        product_string="AJAZZ AK980 PRO 2.4G",
        usage_page=0xFF60,
        usage=0x0061,
        interface_number=3,
        path=DONGLE_PATH,
        physical_id=DONGLE_PARENT,
        input_report_length=65,
        output_report_length=65,
        input_report_sizes={"0": 65},
        output_report_sizes={"0": 65},
    )
    info.update(changes)
    return info


class FakeBus:
    def __init__(self, replies=(), write_result=65, error_at=None):
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
            def __init__(self):
                self.path = None

            def open_path(self, path):
                self.path = path
                bus.opened.append(path)
                if bus.error_at == "open":
                    raise OSError("private-path")

            def write(self, packet):
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
                bus.closed.append(self.path)

        return Handle()


@contextmanager
def fixture(replies=(), inventory=None, write_result=65, error_at=None):
    bus = FakeBus(replies, write_result, error_at)
    with patch.object(battery_hid.hidlist, "enumerate", return_value=list(inventory or ())), \
            patch.object(battery_hid, "hid", bus):
        yield bus


class AjazzAk980Tests(unittest.TestCase):
    def test_wired_reads_mi02_ff68_and_never_opens_mi03_ff67(self):
        ff67 = wired_entry(
            usage_page=0xFF67,
            interface_number=3,
            path=WIRED_LED_PATH,
            output_report_length=4105,
            input_report_sizes={"0": 65},
            output_report_sizes={"0": 4105},
        )
        inv = [ff67, wired_entry()]
        with fixture([reply(level=88, work_mode=0, charge=2)], inv) as bus:
            statuses = P().poll()
        self.assertEqual(len(statuses), 1)
        s, = statuses
        self.assertEqual((s.name, s.level, s.charging_known, s.kind, s.verification_level),
                         ("AJAZZ AK980 PRO", 88, False, "keyboard", "C"))
        self.assertEqual(bus.opened, [WIRED_PATH])
        self.assertEqual(bus.writes, [A.REQUEST])

    def test_dongle_reads_mi03_excluding_mi04_junk_and_ignores_zero_sleep(self):
        mi04 = dongle_entry(interface_number=4, path=DONGLE_MI04_PATH)
        inv = [mi04, dongle_entry()]
        junk = bytes([0x0F, 0x01, 0x01, 0x11]) + bytes(60)
        with fixture([junk, reply(level=100, work_mode=2, charge=0)], inv) as bus:
            statuses = P().poll()
        self.assertEqual(len(statuses), 1)
        self.assertEqual(statuses[0].level, 100)
        self.assertEqual(bus.opened, [DONGLE_PATH])

        p = P()
        with fixture([reply(level=0, work_mode=2)], inv) as bus:
            self.assertEqual(p.poll(), [])
        self.assertIn("device asleep/offline; no new reading; connection unknown", p.diagnostics())

    def test_fail_closed_for_other_fefe_products_duplicates_and_bad_proofs(self):
        inv_x87 = [dongle_entry(product_string="2.4G Dongle")]
        inv_aula = [dongle_entry(product_string="AULA F87ProV2D Dongle")]
        inv_mixed = [dongle_entry(), dongle_entry(product_string="2.4G Dongle", interface_number=4, path=DONGLE_MI04_PATH)]
        inv_dup_mi03 = [dongle_entry(), dongle_entry(path=DONGLE_PATH.replace(b"col01", b"col02"), input_report_sizes=None)]
        inv_bad_size = [wired_entry(output_report_sizes={"0": 33}, output_report_length=33)]
        inv_dup_wired = [wired_entry(), wired_entry(path=WIRED_PATH.replace(b"col01", b"col02"))]
        inv_mixed_parent = [wired_entry(), dongle_entry(physical_id=WIRED_PARENT)]
        for name, inv in (
            ("other 2.4G Dongle", inv_x87),
            ("other AULA Dongle", inv_aula),
            ("mixed FF60 names", inv_mixed),
            ("duplicate MI_03", inv_dup_mi03),
            ("bad report size", inv_bad_size),
            ("duplicate wired", inv_dup_wired),
            ("mixed wired/dongle parent", inv_mixed_parent),
        ):
            with self.subTest(name=name):
                with fixture([reply()], inv) as bus:
                    self.assertEqual(P().poll(), [])
                    self.assertEqual((bus.opened, bus.writes), ([], []))

    def test_rejected_wrong_fingerprint_work_mode_and_echo(self):
        wrong_replies = (
            A.REQUEST[1:],
            reply(vid=0x1234),
            reply(pid=0x9999),
            reply(work_mode=2),  # wrong for wired
            reply(level=101, work_mode=0),
            reply(cmd=0x41, work_mode=0),
        )
        for bad in wrong_replies:
            with self.subTest(bad=bad[:16]):
                with fixture([bad], [wired_entry()]) as bus:
                    self.assertEqual(P().poll(), [])
                    self.assertEqual(len(bus.writes), 1)

    def test_known_ids_candidate_count_and_catalog(self):
        self.assertEqual(server.recognized_candidate_count("ajazz_ak980", [wired_entry()]), 1)
        self.assertEqual(server.recognized_candidate_count("ajazz_ak980", [dongle_entry()]), 1)
        self.assertEqual(server.recognized_candidate_count("ajazz_ak980", [dongle_entry(product_string="2.4G Dongle")]), 0)
        rows = [d for d in known_ids.build_index()["devices"] if d["provider"] == "ajazz_ak980"]
        self.assertEqual(len(rows), 1)
        self.assertEqual((rows[0]["vendorId"], rows[0]["productId"], rows[0].get("requiresModelMatch")),
                         (0x0C45, 0x8039, True))
        self.assertEqual(catalog.validate_catalog()["errors"], [])


if __name__ == "__main__":
    unittest.main()
