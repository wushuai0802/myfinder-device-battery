"""Replay the public K99 V3 descriptor and battery captures using fake HID only."""
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import MagicMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
import battery_hid
import topology
from providers import mchose_k99v3 as k99

DESCRIPTOR = bytes.fromhex(
    "05 01 09 00 a1 01 05 01 09 00 15 00 26 ff 00 95 40 75 08 81 02 "
    "05 01 09 00 15 00 26 ff 00 95 40 75 08 91 02 c0"
)


def collection(**changes):
    info = dict(vendor_id=0x3837, product_id=0x3033, product_string=k99.PRODUCT,
                interface_number=1, usage_page=1, usage=0, path=b"k99-fixture",
                physical_id=r"pnp:usb\vid_3837&pid_3033\fixture",
                input_report_length=65, output_report_length=65,
                input_report_sizes={"0": 65}, output_report_sizes={"0": 65})
    info.update(changes)
    return info


def discover_receiver(*, descriptor=DESCRIPTOR, maximum=65, name=k99.PRODUCT,
                      interface=1, feature=0, input_sizes=None, output_sizes=None):
    path = rf"\\?\hid#vid_3837&pid_3033&mi_{interface:02x}#fixture"
    kernel, hid = MagicMock(), MagicMock()
    kernel.CreateFileW.return_value = 1

    def get_attributes(handle, pointer):
        pointer._obj.vendor, pointer._obj.product = 0x3837, 0x3033
        return 1

    def get_caps(data, pointer):
        caps = pointer._obj
        caps.page, caps.usage = 1, 0
        caps.input = caps.output = maximum
        caps.feature = feature
        return 0x00110000

    def get_name(handle, buffer, size):
        buffer.value = name
        return 1

    hid.HidD_GetAttributes.side_effect = get_attributes
    hid.HidD_GetPreparsedData.return_value = 1
    hid.HidP_GetCaps.side_effect = get_caps
    hid.HidD_GetProductString.side_effect = get_name
    sizes = {"input_report_sizes": input_sizes, "output_report_sizes": output_sizes}
    dlls = {"kernel32": kernel, "hid": hid}
    with patch.object(topology, "os", SimpleNamespace(name="nt")), \
         patch.object(topology.c, "WinDLL", side_effect=lambda key, **_: dlls[key], create=True), \
         patch.object(topology, "usb_parent", return_value=collection()["physical_id"]), \
         patch.object(topology, "_descriptor_cache", {}), patch.object(topology, "_descriptor_paths", None), \
         patch.object(battery_hid, "battery_caps", return_value=sizes), \
         patch.object(topology, "_read_report_descriptor", return_value=(descriptor, None)) as getter:
        info, = topology.discover([path])
    return info, getter.call_count


def glw_reply(command, offset, payload):
    header = [0xAA, command, 0, 0, len(payload), offset & 0xFF, offset >> 8, 0]
    raw = bytes(header + list(payload))
    return raw.ljust(64, b"\0")


def glw_exchange_frames(percent, charging=False, active=0, slots=(0, 1, 2, 3)):
    profile = slots[active] if sorted(slots) == [0, 1, 2, 3] else active
    base = [active, 0, *slots] + [0] * 50
    config = [0] * 64
    config[32] = (percent & 0x7F) | (0x80 if charging else 0)
    return [
        b"",  # initial _drain() sees empty queue
        glw_reply(4, 0, base),
        glw_reply(5, 64 * profile, config[:56]),
        glw_reply(5, 64 * profile + 56, config[56:]),
    ]


class K99V3Tests(unittest.TestCase):
    def test_runtime_descriptor_recovers_missing_caps_then_reads_glw_battery(self):
        info, calls = discover_receiver()
        self.assertEqual(calls, 1)
        self.assertEqual((info["input_report_proof"], info["output_report_proof"]),
                         ("descriptor", "descriptor"))
        dev = MagicMock()
        dev.write.return_value = 65
        dev.read.side_effect = glw_exchange_frames(100, charging=False)
        with patch.object(battery_hid.hidlist, "enumerate", return_value=[info]), \
             patch.object(battery_hid.hid, "device", return_value=dev):
            card, = k99.MchoseK99V3Provider().poll()
        self.assertEqual((card.level, card.charging, card.charging_known), (100, False, True))
        self.assertEqual([call.args[0] for call in dev.write.call_args_list], [
            k99.make_request(4, 0, 56),
            k99.make_request(5, 0, 56),
            k99.make_request(5, 56, 8),
        ])
        self.assertEqual(dev.read.call_args.args[0], 64)

    def test_wrong_identity_or_maxima_do_not_fetch_descriptor(self):
        for changes in ({"name": "other keyboard"}, {"interface": 2}, {"maximum": 64}, {"feature": 65}):
            with self.subTest(changes=changes):
                info, calls = discover_receiver(**changes)
                self.assertEqual(calls, 0)
                self.assertIsNone(k99.MchoseK99V3Provider()._pick([info]))

    def test_complete_caps_do_not_fetch_descriptor(self):
        info, calls = discover_receiver(input_sizes={"0": 65}, output_sizes={"0": 65})
        self.assertEqual(calls, 0)
        self.assertIs(k99.MchoseK99V3Provider()._pick([info]), info)

    def test_disagreeing_or_malformed_descriptor_cannot_open(self):
        for changes in ({"descriptor": b"\xc0"}, {"input_sizes": {"1": 65}},
                        {"descriptor": DESCRIPTOR.replace(b"\x95\x40", b"\x95\x3f")}):
            with self.subTest(changes=changes):
                info, calls = discover_receiver(**changes)
                self.assertEqual(calls, 1)
                with patch.object(battery_hid.hidlist, "enumerate", return_value=[info]), \
                     patch.object(battery_hid.hid, "device") as device:
                    self.assertEqual(k99.MchoseK99V3Provider().poll(), [])
                device.assert_not_called()

    def test_glw_charging_and_profile_slot_indirection(self):
        reports = battery_hid.parse_report_descriptor(DESCRIPTOR)[(1, 0)]
        self.assertEqual(reports, {"input": {"0": 65}, "output": {"0": 65}, "feature": {}})
        for expected, charging, active, slots, profile in (
            (85, True, 0, (0, 1, 2, 3), 0),
            (40, False, 1, (2, 3, 0, 1), 3),
        ):
            with self.subTest(expected=expected, charging=charging):
                dev = MagicMock()
                dev.write.return_value = 65
                dev.read.side_effect = glw_exchange_frames(expected, charging=charging, active=active, slots=slots)
                with patch.object(battery_hid.hidlist, "enumerate", return_value=[collection()]), \
                     patch.object(battery_hid.hid, "device", return_value=dev):
                    card, = k99.MchoseK99V3Provider().poll()
                self.assertEqual([call.args[0] for call in dev.write.call_args_list], [
                    k99.make_request(4, 0, 56),
                    k99.make_request(5, 64 * profile, 56),
                    k99.make_request(5, 64 * profile + 56, 8),
                ])
                dev.close.assert_called_once()
                self.assertEqual((card.level, card.charging, card.kind, card.charging_known),
                                 (expected, charging, "keyboard", True))

    def test_rejects_legacy_aa03_getinfo_frame_and_out_of_range_percentage(self):
        legacy_52 = bytearray(64)
        legacy_52[:2] = b"\xaa\x03"
        legacy_52[8] = 52
        dev = MagicMock()
        dev.write.return_value = 65
        dev.read.side_effect = [b"", bytes(legacy_52), b""]
        provider = k99.MchoseK99V3Provider()
        with patch.object(battery_hid.hidlist, "enumerate", return_value=[collection()]), \
             patch.object(battery_hid.hid, "device", return_value=dev):
            self.assertEqual(provider.poll(), [])
        self.assertIn("rejected battery reply", provider.diagnostics())

        dev2 = MagicMock()
        dev2.write.return_value = 65
        dev2.read.side_effect = glw_exchange_frames(101, charging=True)
        provider2 = k99.MchoseK99V3Provider()
        with patch.object(battery_hid.hidlist, "enumerate", return_value=[collection()]), \
             patch.object(battery_hid.hid, "device", return_value=dev2):
            self.assertEqual(provider2.poll(), [])
        self.assertIn("rejected GLW percentage outside 0..100", provider2.diagnostics())

    def test_shared_pid_and_unproven_collection_never_open(self):
        for rows in ([collection(product_string="MCHOSE G87 V2 2.4G")],
                     [collection(product_string="MCHOSE K87S 2.4G")],
                     [collection(interface_number=2)], [collection(usage=2)],
                     [collection(physical_id="collection:fallback")],
                     [collection(input_report_sizes={})], [collection(output_report_sizes={"1": 65})],
                     [collection(), collection(path=b"second")]):
            with self.subTest(rows=rows), patch.object(battery_hid.hidlist, "enumerate", return_value=rows), \
                    patch.object(battery_hid.hid, "device") as factory:
                self.assertEqual(k99.MchoseK99V3Provider().poll(), [])
                factory.assert_not_called()

    def test_rejects_other_commands_or_invalid_glw_headers(self):
        for packet in (bytes(64), bytes.fromhex("aa 03 00 38 00 00 00 00 34").ljust(64, b"\0"),
                       bytes.fromhex("aa 04"), bytes.fromhex("aa 05 00 00 38 00 00 00").ljust(64, b"\0")):
            self.assertIsNone(k99.parse_reply(packet, 4, 0, 56))
