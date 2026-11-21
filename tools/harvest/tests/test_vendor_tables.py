from hashlib import sha256
from pathlib import Path
import runpy
from unittest import TestCase


TOOLS = runpy.run_path(str(Path(__file__).parents[1] / "vendor_tables.py"))
parse = TOOLS["PARSERS"]


class TableTests(TestCase):
    def test_mchose(self):
        text = 'createAudioHidProduct({modelKey:"MCHOSE R9",vendorId:14391,productId:24595,productName:"MCHOSE R9",connectionType:CONNECTION_TYPE.WIRELESS,protocolProfile:AUDIO_PROTOCOL_PROFILE.MC,protocolOptions:{}})'
        self.assertEqual(list(parse["mchose"](text)), [
            (text.index("{"), 0x3837, 0x6013, "MCHOSE R9", "AUDIO_PROTOCOL_PROFILE.MC"),
        ])

    def test_vgn_does_not_combine_adjacent_objects(self):
        text = '[{vendorId:39297,productId:24578,usagePage:65296,usage:16,customDeviceType:"vgn-king",productName:"VGN Dragonfly King",reportId:32},{vendorId:1,productId:7},{vendorId:1,productName:"other",customDeviceType:"bad"}]'
        self.assertEqual(list(parse["vgn"](text)), [
            (1, 0x9981, 0x6002, "VGN Dragonfly King", "vgn-king"),
        ])

    def test_aula_requires_explicit_protocol(self):
        text = '[{vid:0x258A,pid:0x0138,modelName:"SC380 Pro",driverType:"mouse"},{vid:0x258A,pid:0x0139,modelName:"unknown"}]'
        self.assertEqual(list(parse["aula"](text)), [(1, 0x258A, 0x0138, "SC380 Pro", "mouse")])

    def test_epomaker_exponent_and_nested_unknown_wireless(self):
        text = '[{vendorId:14e3,productId:12605,name:`EPOMAKER Bamboo75`,vendor:`QMK`,protocolVersion:`1.0.0`,wirelessForm:{vendorId:12625,productId:20536}}]'
        self.assertEqual(list(parse["epomaker"](text)), [(1, 0x36B0, 12605, "EPOMAKER Bamboo75", "QMK 1.0.0")])

    def test_zhuaudio_requires_pair_binding(self):
        code = 'B.apQ=new A.aF(13908,20565)\nB.kq=s([B.apQ],t.gX) assets/zhulinniao/zg4/dock_black.png'
        self.assertEqual(list(parse["zhuaudio"](code)), [(0, 13908, 20565, "ZhuAudio ZG4", "ZG4 (FF05)")])
        self.assertEqual(list(parse["zhuaudio"](code.replace("[B.apQ]", "[B.abc]"))), [])

    def test_fixture_provenance_and_char_offset(self):
        origin = TOOLS["ENTRYPOINTS"]["vgn"]
        asset = origin + "assets/index-fixture.js"
        content = '/*型号*/[{vendorId:13652,productId:62737,customDeviceType:"vgn-f1-pm",productName:"VGN F1 SE"}]'
        pages = {origin: b'<script type="module" src="/assets/index-fixture.js"></script>',
                 asset: content.encode()}
        result = TOOLS["harvest"]("vgn", {"3554:F511"}, lambda url, host: (url, pages[url]))
        self.assertEqual(result["status"], "ok")
        self.assertEqual(result["unknownIds"], 0)
        self.assertEqual(result["records"], [{
            "id": "3554:F511", "model": "VGN F1 SE", "protocol": "vgn-f1-pm",
            "url": asset, "sha256": sha256(pages[asset]).hexdigest(), "offset": content.index("{"),
        }])

    def test_blocked_entry_is_reported(self):
        def denied(url, host):
            raise OSError("HTTP 403")
        result = TOOLS["harvest"]("aula", set(), denied)
        self.assertEqual(result["status"], "failed")
        self.assertEqual(result["errors"][0]["error"], "HTTP 403")

    def test_off_host_redirect_rejected(self):
        with self.assertRaises(ValueError):
            TOOLS["require_host"]("https://evil.example/assets/index-a.js", "hub.aulacn.com")
        with self.assertRaises(ValueError):
            TOOLS["VendorRedirect"]("hub.aulacn.com").redirect_request(
                None, None, 302, "", {}, "http://hub.aulacn.com/"
            )
