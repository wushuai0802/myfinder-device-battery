"""Offline test of the official M HUB MC GET, response field layout and USB gate."""
import sys
import unittest
from types import SimpleNamespace
from pathlib import Path
from unittest.mock import MagicMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
import battery_hid
from providers import mchose_v9pro2 as v9
import topology
from test_battery_caps import FakeApi, OK


def collection(**changes):
    data = dict(vendor_id=0x3837, product_id=0x6011, product_string="MCHOSE V9 Pro 2",
                interface_number=3, usage_page=0xFF07, usage=0x0222, path=b"vendor",
                physical_id=r"pnp:usb\vid_3837&pid_6011\receiver-test",
                input_report_length=64, output_report_length=64,
                input_report_sizes={"85": 64}, output_report_sizes={"85": 64})
    data.update(changes)
    return data


def frame(hex_data):
    return list(bytes.fromhex(hex_data).ljust(64, b"\0"))


class V9Pro2Tests(unittest.TestCase):
    def test_query_uses_only_mc_battery_get(self):
        request = v9.make_request()
        self.assertEqual(request, bytes.fromhex("55 aa 01 00 01 00 01 0b").ljust(64, b"\0"))
        dev = MagicMock()
        dev.write.return_value = 64
        dev.read.return_value = frame("55 aa 01 00 03 00 01 0b 01 c8 00")
        with patch.object(battery_hid.hidlist, "enumerate", return_value=[collection()]), \
                patch.object(battery_hid.hid, "device", return_value=dev):
            card, = v9.MchoseV9Pro2Provider().poll()
        self.assertEqual((card.level, card.charging, card.charging_known, card.kind), (72, True, True, "headset"))
        dev.write.assert_called_once_with(request)
        dev.close.assert_called_once()

    def test_checked_unchecked_sleep_and_invalid_packets(self):
        # Flag ^ length ^ type ^ subcommand ^ awake ^ level.
        valid = frame("55 aa 01 01 03 00 01 0b 01 28 21")
        self.assertEqual(v9.parse_reply(valid), (40, False, ""))
        asleep = frame("55 aa 01 00 03 00 01 0b 00 ff 00")
        self.assertTrue(v9.MchoseV9Pro2Provider().no_reading_reply(asleep))
        for pos, value in [(0, 84), (2, 3), (3, 2), (4, 1), (4, 61), (6, 4), (7, 34), (9, 101), (10, 0)]:
            changed = valid.copy()
            changed[pos] = value
            self.assertIsNone(v9.parse_reply(changed))
        self.assertIsNone(v9.parse_reply(valid[:-1]))
        self.assertIsNone(v9.parse_reply(v9.make_request()))

    def test_adjacent_products_ambiguous_or_unproven_never_open(self):
        for entries in ([collection(product_id=0x6013)], [collection(product_string="MCHOSE V9 PRO")],
                        [collection(interface_number=1)], [collection(usage_page=0xFF90)],
                        [collection(physical_id=r"pnp:usb\vid_3837&pid_600a\wrong")],
                        [collection(input_report_sizes={})], [collection(output_report_sizes={"85": 63})],
                        [collection(), collection(path=b"duplicate")]):
            with self.subTest(entries=entries), patch.object(battery_hid.hidlist, "enumerate", return_value=entries), \
                    patch.object(battery_hid.hid, "device") as factory:
                self.assertEqual(v9.MchoseV9Pro2Provider().poll(), [])
                factory.assert_not_called()



def r9_collection(**changes):
    identity = dict(product_id=0x6013, product_string="MCHOSE R9", interface_number=2,
                    physical_id=r"pnp:usb\vid_3837&pid_6013\receiver-test")
    return collection(**(identity | changes))


def discover_mc_collection(pid, interface, name, *, full=True):
    path = rf"\\?\hid#vid_3837&pid_{pid:04x}&mi_{interface:02x}#mc-fixture"
    parent = rf"pnp:usb\vid_3837&pid_{pid:04x}\receiver-test"
    kernel, hid = MagicMock(), MagicMock()
    kernel.CreateFileW.return_value = 1

    def get_attributes(handle, pointer):
        pointer._obj.vendor, pointer._obj.product = 0x3837, pid
        return 1

    def get_data(handle, pointer):
        pointer._obj.value = handle
        return 1

    def get_caps(data, pointer):
        caps = pointer._obj
        caps.page, caps.usage = 0xFF07, 0x0222
        caps.input = caps.output = 64
        caps.iv = caps.ov = 1
        return OK

    def get_name(handle, buffer, length):
        buffer.value = name
        return 1

    hid.HidD_GetAttributes.side_effect = get_attributes
    hid.HidD_GetPreparsedData.side_effect = get_data
    hid.HidP_GetCaps.side_effect = get_caps
    hid.HidD_GetProductString.side_effect = get_name
    hid.HidP_GetValueCaps.side_effect = FakeApi(values=[dict(report=0x55, bits=8, count=63)]).HidP_GetValueCaps
    dlls = {"kernel32": kernel, "hid": hid}
    with patch.object(topology, "os", SimpleNamespace(name="nt")), \
            patch.object(topology.c, "WinDLL", side_effect=lambda key, **_: dlls[key], create=True), \
            patch.object(topology, "usb_parent", return_value=parent), \
            patch.object(topology, "_descriptor_cache", {}), patch.object(topology, "_descriptor_paths", None):
        info, = topology.discover([path], full=full)
    kernel.CreateFileW.assert_called_once_with(path, 0, 3, None, 3, 0, None)
    return info, hid.HidP_GetValueCaps.call_count


class R9Tests(unittest.TestCase):
    def assert_not_queried(self, entries):
        with patch.object(battery_hid.hidlist, "enumerate", return_value=entries), \
                patch.object(battery_hid.hid, "device") as factory:
            self.assertEqual(v9.MchoseV9Pro2Provider().poll(), [])
            factory.assert_not_called()

    def test_r9_proves_and_reads_at_multiple_interface_numbers(self):
        for interface, full in ((0, True), (2, False), (4, True)):
            with self.subTest(interface=interface, full=full):
                info, proof_calls = discover_mc_collection(0x6013, interface, "MCHOSE R9", full=full)
                self.assertEqual(proof_calls, 2)
                self.assertEqual(info["interface_number"], interface)
                self.assertEqual(info["input_report_sizes"], {"85": 64})
                self.assertEqual(info["output_report_sizes"], {"85": 64})
                dev = MagicMock()
                dev.write.return_value = 64
                dev.read.return_value = frame("55 aa 01 00 03 00 01 0b 01 c8 00")
                with patch.object(battery_hid.hidlist, "enumerate", return_value=[info]), \
                        patch.object(battery_hid.hid, "device", return_value=dev):
                    card, = v9.MchoseV9Pro2Provider().poll()
                self.assertEqual((card.name, card.level, card.charging, card.kind),
                                 ("MCHOSE R9", 72, True, "headset"))
                dev.open_path.assert_called_once_with(info["path"])
                dev.write.assert_called_once_with(v9.make_request())
                dev.close.assert_called_once()

    def test_r9_wrong_product_cannot_gain_proof_or_query(self):
        for name in ("MCHOSE R9 Pro", "", "MCHOSE V9 Pro 2"):
            with self.subTest(name=name):
                info, proof_calls = discover_mc_collection(0x6013, 2, name)
                self.assertEqual(proof_calls, 0)
                self.assertNotIn("input_report_sizes", info)
                self.assertNotIn("output_report_sizes", info)
                self.assert_not_queried([r9_collection(product_string=name)])

    def test_r9_requires_report_55_at_64_bytes_in_both_directions(self):
        for direction in ("input", "output"):
            for sizes in (None, {"84": 64}, {"85": 63, "84": 64}):
                with self.subTest(direction=direction, sizes=sizes):
                    self.assert_not_queried([r9_collection(**{direction + "_report_sizes": sizes})])

    def test_r9_requires_an_exact_usb_parent(self):
        for parent in (r"pnp:usb\vid_3837&pid_6011\wrong",
                       r"pnp:usb\vid_3837&pid_6013\parent\nested", ""):
            with self.subTest(parent=parent):
                self.assert_not_queried([r9_collection(physical_id=parent)])

    def test_r9_requires_one_battery_collection_on_the_usb_parent(self):
        for other in (r9_collection(path=b"alternate"),
                      r9_collection(path=b"unknown", product_id=0x6014)):
            with self.subTest(other=other["product_id"]):
                self.assert_not_queried([r9_collection(), other])

    def test_v9_pro2_wrong_interface_cannot_gain_proof_or_query(self):
        info, proof_calls = discover_mc_collection(0x6011, 2, "MCHOSE V9 Pro 2")
        self.assertEqual(proof_calls, 0)
        self.assertNotIn("input_report_sizes", info)
        self.assertNotIn("output_report_sizes", info)
        self.assert_not_queried([collection(interface_number=2)])
