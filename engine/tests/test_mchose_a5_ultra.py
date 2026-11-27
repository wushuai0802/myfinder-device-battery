"""Synthetic MCHOSE A5 Ultra (0BDA:4762) read-only V2 Feature 0x11 regressions (#1186)."""
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import MagicMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401, E402
import battery_hid  # noqa: E402
import catalog  # noqa: E402
import known_ids  # noqa: E402
import server  # noqa: E402
import topology  # noqa: E402
from providers import mchose as M  # noqa: E402
from test_mchose_a5v2 import Clock, Device, reply  # noqa: E402


PATH = r"\\?\hid#vid_0bda&pid_4762&mi_02&col03#synthetic-a5ultra"
FF0B_PATH = r"\\?\hid#vid_0bda&pid_4762&mi_02&col04#synthetic-a5ultra"
PARENT = r"pnp:usb\vid_0bda&pid_4762\synthetic-a5ultra"
NAME = "MCHOSE A5 Ultra"
KEY = "mchose:0bda:4762"


def a5_ultra_inventory(sizes=None, *, feat_len=65, proof="caps", product="A5 ultra"):
    sizes = {"17": 21, "18": 65} if sizes is None else sizes
    common = dict(vendor_id=0x0BDA, product_id=0x4762, product_string=product,
                  physical_id=PARENT)
    return [dict(common, path=PATH.encode(), usage_page=0xFF01, usage=1,
                 feature_report_length=feat_len, feature_report_sizes=dict(sizes),
                 feature_report_proof=proof),
            dict(common, path=FF0B_PATH.encode(), usage_page=0xFF0B, usage=0x0104)]


class A5UltraTests(unittest.TestCase):
    def setUp(self):
        self.clock = Clock()
        timer = patch.object(M, "time", self.clock)
        timer.start()
        self.addCleanup(timer.stop)
        self.provider = M.MchoseProvider()

    def poll(self, dev, infos=None):
        infos = a5_ultra_inventory() if infos is None else infos
        with patch.object(M.hidlist, "enumerate", side_effect=lambda vid: [
                d for d in infos if d["vendor_id"] == vid]), \
             patch.object(M.hid, "device", return_value=dev), \
             patch.object(M, "_dpi_state", side_effect=AssertionError("A5 Ultra never opens DPI")), \
             patch.object(M.MchoseProvider, "_read_collection", side_effect=AssertionError("legacy")), \
             patch.object(M.MchoseProvider, "_read_a5", side_effect=AssertionError("first A5")), \
             patch.object(M.MchoseProvider, "_read_v3", side_effect=AssertionError("V3")), \
             patch.object(M.MchoseProvider, "_read_g7", side_effect=AssertionError("G7")):
            return self.provider.poll()

    def assert_no_io(self, infos):
        dev = Device([reply(21, vid=0x0001, pid=0x4762), reply(21, vid=0x0001, pid=0x4762)])
        self.assertEqual(self.poll(dev, infos), [])
        self.assertEqual((dev.opens, dev.sent, dev.gets), ([], [], []))
        self.assertNotIn(KEY, self.provider._last)

    def test_valid_report_17_configs_and_hardware_0001_vid_succeed_without_dpi(self):
        # Verify exact little-endian wire layout: pay[0:2]=01 00 (0x0001), pay[2:4]=62 47 (0x4762).
        raw_hw = reply(21, level=95, charge=0, vid=0x0001, pid=0x4762)
        parsed = M.parse_a5v2(raw_hw, 21, valid_vids=M.A5_ULTRA_REPLY_HEADERS, expected_pid=0x4762)
        self.assertEqual(parsed[:4], bytes([0x01, 0x00, 0x62, 0x47]))
        self.assertEqual(parsed[9], 95)
        self.assertIsNone(M.parse_a5v2(
            reply(21, level=95, vid=0x1234, pid=0x4762),
            21, valid_vids=M.A5_ULTRA_REPLY_HEADERS, expected_pid=0x4762))
        self.assertFalse(M._v2_dpi_eligible(a5_ultra_inventory()[0]))

        cases = [
            ({"17": 21, "18": 65}, 65, "caps", "A5 ultra", 0x0001, 95, 0),
            ({"17": 21}, 21, "descriptor", "A5 Ultra", 0x0001, 91, 1),
            ({"17": 65}, 65, "caps", "MCHOSE A5 Ultra", 0x0BDA, 100, 1),
        ]
        for sizes, feat_len, proof, product, vid, level, charge in cases:
            rlen = sizes["17"]
            frame = reply(rlen, level=level, charge=charge, vid=vid, pid=0x4762)
            with self.subTest(sizes=sizes, product=product, vid=vid):
                self.clock.now = 0
                dev = Device([frame, frame])
                status, = self.poll(dev, a5_ultra_inventory(
                    sizes, feat_len=feat_len, proof=proof, product=product))
                self.assertEqual((status.key, status.name, status.level, status.charging,
                                  status.online, status.kind, status.dpi),
                                 (KEY, NAME, level, bool(charge), True, "mouse", None))
                req = b"\x11\xf9" + b"\xff" * 19 + b"\x00" * (rlen - 21)
                self.assertEqual(dev.sent, [req, req])
                self.assertEqual(dev.gets, [(0x11, rlen)] * 2)
                self.assertEqual(dev.opens, [PATH.encode()])
                self.assertEqual(dev.closed, 1)

    def test_wrong_pid_reply_g3_mouse_and_other_realtek_devices_fail_closed(self):
        # Replies whose pay[2:4] != 0x4762 or unstable readings must be rejected.
        dev = Device([
            reply(21, level=95, vid=0x0001, pid=0x1001),
            reply(21, level=95, vid=0x0001, pid=0x0000),
            reply(21, level=95, vid=0x0001, pid=0x4762),
            reply(21, level=94, vid=0x0001, pid=0x4762),
        ] * 5)
        self.assertEqual(self.poll(dev), [])
        self.assertEqual(self.provider.reader_diagnostics(), ["A5ULTRA no_stable_reply"])

        bad_edits = [
            dict(product_string="G3 Mouse"),
            dict(product_string="MCHOSE G3 Ultra 8K"),
            dict(product_string="Realtek USB Device"),
            dict(product_string=""),
            dict(feature_report_sizes={"18": 65}),
            dict(feature_report_sizes={"17": 20}),
            dict(feature_report_length=65, feature_report_sizes={"17": 21}),
            dict(feature_report_proof="failed"),
            dict(report_descriptor_error="collection not found"),
            dict(physical_id="collection:" + PATH),
            dict(physical_id=PARENT.replace("4762", "1001")),
            dict(path=PATH.replace("4762", "1001").encode()),
        ]
        for edit in bad_edits:
            with self.subTest(edit=edit):
                infos = a5_ultra_inventory()
                infos[0].update(edit)
                self.assert_no_io(infos)
        # Duplicate FF01 collections fail closed.
        infos = a5_ultra_inventory()
        self.assert_no_io(infos + [dict(infos[0], path=(PATH + "-dup").encode())])
        # Unrelated RealTek 0BDA:* PIDs must never be opened, scanned, or emit diagnostics/candidates.
        unrelated = [dict(
            vendor_id=0x0BDA, product_id=0x2852, product_string="Realtek Bluetooth",
            physical_id=r"pnp:usb\vid_0bda&pid_2852\other",
            path=rb"\\?\hid#vid_0bda&pid_2852&mi_00#other",
            usage_page=0xFF01, usage=1,
            feature_report_length=65, feature_report_sizes={"17": 21, "18": 65},
            feature_report_proof="caps",
        )]
        self.assert_no_io(unrelated)
        self.assertEqual(self.provider.reader_diagnostics(), [])
        self.assertEqual(server.recognized_candidate_count("mchose", unrelated), 0)

    def test_topology_descriptor_fallback_and_health_index_integration(self):
        desc = bytes.fromhex(
            "06 01 ff 09 01 a1 01 "
            "85 11 09 01 15 00 26 ff 00 75 08 95 14 b1 00 "
            "85 12 09 01 15 00 26 ff 00 75 08 95 40 b1 00 c0"
        )
        kernel, hid = MagicMock(), MagicMock()
        kernel.CreateFileW.return_value = 1

        def get_attrs(handle, pointer):
            pointer._obj.vendor, pointer._obj.product = 0x0BDA, 0x4762
            return 1

        def get_caps(data, pointer):
            caps = pointer._obj
            caps.page, caps.usage, caps.feature = 0xFF01, 1, 65
            return 0x00110000

        def get_name(handle, buffer, size):
            buffer.value = "A5 ultra"
            return 1

        hid.HidD_GetAttributes.side_effect = get_attrs
        hid.HidD_GetPreparsedData.return_value = 1
        hid.HidP_GetCaps.side_effect = get_caps
        hid.HidD_GetProductString.side_effect = get_name
        dlls = {"kernel32": kernel, "hid": hid}
        with patch.object(topology, "os", SimpleNamespace(name="nt")), \
             patch.object(topology.c, "WinDLL", side_effect=lambda k, **_: dlls[k], create=True), \
             patch.object(topology, "usb_parent", return_value=PARENT), \
             patch.object(topology, "_descriptor_cache", {}), patch.object(topology, "_descriptor_paths", None), \
             patch.object(battery_hid, "byte_report_sizes", return_value=None), \
             patch.object(topology, "_read_report_descriptor", return_value=(desc, None)):
            info, = topology.discover([PATH])
        self.assertEqual(info["feature_report_sizes"], {"17": 21, "18": 65})
        self.assertEqual(info["feature_report_proof"], "descriptor")

        self.assertEqual(server.recognized_candidate_count("mchose", a5_ultra_inventory()), 1)
        self.assertEqual(server.recognized_candidate_count(
            "mchose", a5_ultra_inventory(product="G3 Mouse")), 0)
        rows = [d for d in known_ids.build_index()["devices"]
                if (d["provider"], d["vendorId"], d["productId"]) == ("mchose", 0x0BDA, 0x4762)]
        self.assertEqual((len(rows), rows[0]["name"], rows[0].get("requiresModelMatch")),
                         (1, NAME, True))
        self.assertEqual(catalog.validate_catalog()["errors"], [])

        engine = server.BatteryEngine(providers=[])
        engine._accept("device:a5ultra", "mchose", [], server.utcnow(), physical=PARENT,
                       candidate_count=1, diagnostics=["A5ULTRA feature_unproven"])
        snap = engine._snapshot(1, 1, True)
        row, = [h for h in snap["health"] if h["provider"] == "mchose"]
        self.assertEqual(row["errorCode"], "A5ULTRA feature_unproven")


if __name__ == "__main__":
    unittest.main()
