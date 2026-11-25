"""Synthetic MCHOSE K7 Ultra (3837:1001) read-only V2 Feature 0x11 regressions (#1127)."""
import json
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


PATH = r"\\?\hid#vid_3837&pid_1001&mi_02&col03#synthetic-k7ultra"
FF0B_PATH = r"\\?\hid#vid_3837&pid_1001&mi_02&col04#synthetic-k7ultra"
PARENT = r"pnp:usb\vid_3837&pid_1001\synthetic-k7ultra"
NAME = "MCHOSE K7 Ultra"
KEY = "mchose:3837:1001"


def k7_inventory(sizes=None, *, feat_len=65, proof="caps", product=NAME):
    sizes = {"17": 21, "18": 65} if sizes is None else sizes
    common = dict(vendor_id=0x3837, product_id=0x1001, product_string=product,
                  physical_id=PARENT)
    return [dict(common, path=PATH.encode(), usage_page=0xFF01, usage=1,
                 feature_report_length=feat_len, feature_report_sizes=dict(sizes),
                 feature_report_proof=proof),
            dict(common, path=FF0B_PATH.encode(), usage_page=0xFF0B, usage=0x0104)]


class K7UltraTests(unittest.TestCase):
    def setUp(self):
        self.clock = Clock()
        timer = patch.object(M, "time", self.clock)
        timer.start()
        self.addCleanup(timer.stop)
        self.provider = M.MchoseProvider()

    def poll(self, dev, infos=None):
        infos = k7_inventory() if infos is None else infos
        with patch.object(M.hidlist, "enumerate", side_effect=lambda vid: [
                d for d in infos if d["vendor_id"] == vid]), \
             patch.object(M.hid, "device", return_value=dev), \
             patch.object(M, "_dpi_state", side_effect=AssertionError("K7 Ultra never opens DPI")), \
             patch.object(M.MchoseProvider, "_read_collection", side_effect=AssertionError("legacy")), \
             patch.object(M.MchoseProvider, "_read_a5", side_effect=AssertionError("first A5")), \
             patch.object(M.MchoseProvider, "_read_v3", side_effect=AssertionError("V3")), \
             patch.object(M.MchoseProvider, "_read_g7", side_effect=AssertionError("G7")):
            return self.provider.poll()

    def assert_no_io(self, infos):
        dev = Device([reply(21), reply(21)])
        self.assertEqual(self.poll(dev, infos), [])
        self.assertEqual((dev.opens, dev.sent, dev.gets), ([], [], []))
        self.assertNotIn(KEY, self.provider._last)

    def test_valid_report_17_configs_and_vids_succeed_without_touching_ff0b_or_dpi(self):
        cases = [
            ({"17": 21, "18": 65}, 65, "caps", 0x3837, 74, 0),
            ({"17": 21}, 21, "descriptor", 0x5253, 88, 1),
            ({"17": 65}, 65, "caps", 0x3837, 100, 1),
        ]
        for sizes, feat_len, proof, vid, level, charge in cases:
            rlen = sizes["17"]
            frame = reply(rlen, level=level, charge=charge, vid=vid)
            with self.subTest(sizes=sizes, vid=vid):
                self.clock.now = 0
                dev = Device([frame, frame])
                status, = self.poll(dev, k7_inventory(sizes, feat_len=feat_len, proof=proof))
                self.assertEqual((status.key, status.name, status.level, status.charging,
                                  status.online, status.kind, status.dpi),
                                 (KEY, NAME, level, bool(charge), True, "mouse", None))
                req = b"\x11\xf9" + b"\xff" * 19 + b"\x00" * (rlen - 21)
                self.assertEqual(dev.sent, [req, req])
                self.assertEqual(dev.gets, [(0x11, rlen)] * 2)
                self.assertEqual(dev.opens, [PATH.encode()])
                self.assertEqual(dev.closed, 1)

    def test_unstable_or_rejected_frames_and_wrong_identities_fail_closed(self):
        dev = Device([reply(21, level=50), reply(21, level=51), reply(21, vid=0x41E4)] * 7)
        self.assertEqual(self.poll(dev), [])
        self.assertEqual(self.provider.reader_diagnostics(), ["K7ULTRA no_stable_reply"])

        bad_edits = [
            dict(product_string="MCHOSE A7 V3 Pro"),
            dict(product_string=""),
            dict(feature_report_sizes={"18": 65}),
            dict(feature_report_sizes={"17": 20}),
            dict(feature_report_length=65, feature_report_sizes={"17": 21}),
            dict(feature_report_proof="failed"),
            dict(report_descriptor_error="collection not found"),
            dict(physical_id="collection:" + PATH),
            dict(physical_id=PARENT.replace("1001", "100c")),
            dict(path=PATH.replace("1001", "100c").encode()),
        ]
        for edit in bad_edits:
            with self.subTest(edit=edit):
                infos = k7_inventory()
                infos[0].update(edit)
                self.assert_no_io(infos)
        # Duplicate FF01 collections fail closed.
        infos = k7_inventory()
        self.assert_no_io(infos + [dict(infos[0], path=(PATH + "-dup").encode())])

    def test_topology_descriptor_fallback_and_health_index_integration(self):
        desc = bytes.fromhex(
            "06 01 ff 09 01 a1 01 "
            "85 11 09 01 15 00 26 ff 00 75 08 95 14 b1 00 "
            "85 12 09 01 15 00 26 ff 00 75 08 95 40 b1 00 c0"
        )
        kernel, hid = MagicMock(), MagicMock()
        kernel.CreateFileW.return_value = 1

        def get_attrs(handle, pointer):
            pointer._obj.vendor, pointer._obj.product = 0x3837, 0x1001
            return 1

        def get_caps(data, pointer):
            caps = pointer._obj
            caps.page, caps.usage, caps.feature = 0xFF01, 1, 65
            return 0x00110000

        def get_name(handle, buffer, size):
            buffer.value = NAME
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

        self.assertEqual(server.recognized_candidate_count("mchose", k7_inventory()), 1)
        rows = [d for d in known_ids.build_index()["devices"]
                if (d["provider"], d["vendorId"], d["productId"]) == ("mchose", 0x3837, 0x1001)]
        self.assertEqual((len(rows), rows[0]["name"]), (1, NAME))
        self.assertEqual(catalog.validate_catalog()["errors"], [])

        engine = server.BatteryEngine(providers=[])
        engine._accept("device:k7", "mchose", [], server.utcnow(), physical=PARENT,
                       candidate_count=1, diagnostics=["K7ULTRA feature_unproven"])
        snap = engine._snapshot(1, 1, True)
        row, = [h for h in snap["health"] if h["provider"] == "mchose"]
        self.assertEqual(row["errorCode"], "K7ULTRA feature_unproven")


if __name__ == "__main__":
    unittest.main()
