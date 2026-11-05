"""Offline digest parsing and support labels; all tracker data is synthetic."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


digest = load("issue_digest", ROOT / "tools/issue-watch/issue_digest.py")
known_ids = load("digest_known_ids", ROOT / "engine/known_ids.py")
BODY = ('来自程序内反馈 · 版本 1.2.3\n**描述**\n```\n无电量\n```\n'
        '有卡片的设备\nWorking | mouse | example | 80%\n已隐藏的重复连接\n无记录\n'
        '未取得电量的设备\n'
        '1234:ABCD "Shared Receiver" FF04:0002 FF04:0002\n'
        '1234:ABCE "Ordinary Mouse" FF02:0002\n'
        '9999:0001 "Unknown Keyboard"\n'
        '9999:0002 "Virtual Device" FF00:0001\n')


def fake_index():
    return {"devices": [
        {"vendorId": 0x1234, "productId": 0xABCD, "name": "Gated Keyboard",
         "requiresModelMatch": True},
        {"vendorId": 0x1234, "productId": 0xABCE, "name": "Ordinary Mouse"},
    ]}


class DigestTests(unittest.TestCase):
    def read_index(self, index):
        with patch.object(digest.subprocess, "run", return_value=subprocess.CompletedProcess(
                [], 0, stdout=json.dumps(index))) as run:
            result = digest.known_ids()
        self.assertNotIn("gh", run.call_args.args[0])
        self.assertIn("--json", run.call_args.args[0])
        return result

    def test_known_ids_preserves_gate_and_defaults_legacy_rows_to_false(self):
        known = self.read_index(fake_index())
        self.assertEqual(known[0x1234, 0xABCD], {"name": "Gated Keyboard", "requiresModelMatch": True})
        self.assertEqual(known[0x1234, 0xABCE], {"name": "Ordinary Mouse", "requiresModelMatch": False})

    def test_duplicate_id_cannot_discard_a_gate(self):
        rows = fake_index()["devices"][:1] + [
            {"vendorId": 0x1234, "productId": 0xABCD, "name": "Other Provider"}]
        for devices in (rows, list(reversed(rows))):
            with self.subTest(devices=devices):
                known = self.read_index({"devices": devices})
                self.assertTrue(known[0x1234, 0xABCD]["requiresModelMatch"])

    def test_parse_and_annotate_gated_ordinary_unknown_and_noise(self):
        info = digest.parse_body(BODY)
        self.assertEqual(info["version"], "1.2.3")
        self.assertEqual(info["description"], "无电量")
        self.assertEqual(len(info["cards"]), 1)
        self.assertEqual([d["id"] for d in info["unknown"]], ["1234:ABCD", "1234:ABCE", "9999:0001"])
        self.assertEqual(info["unknown"][0]["vendor"], ["FF04:0002"])
        digest.annotate_unknown(info["unknown"], self.read_index(fake_index()))
        self.assertEqual([digest.support_tag(d) for d in info["unknown"]], [
            "仅部分型号（Gated Keyboard）", "已支持（Ordinary Mouse）", "未支持"])
        self.assertEqual([d["requiresModelMatch"] for d in info["unknown"]], [True, False, False])
        self.assertIsNone(info["unknown"][-1]["knownInMain"])

    def test_empty_body_and_index(self):
        self.assertEqual(digest.parse_body(None)["unknown"], [])
        info = digest.parse_body(BODY)
        digest.annotate_unknown(info["unknown"], self.read_index({}))
        self.assertTrue(all(digest.support_tag(d) == "未支持" for d in info["unknown"]))

    def test_main_writes_matching_markdown_and_json_without_gh(self):
        issue = {"number": 1, "title": "Synthetic feedback", "body": BODY, "state": "open",
                 "created_at": "2026-11-05T01:00:00Z", "html_url": "https://example.invalid/1"}
        known = self.read_index(fake_index())
        with tempfile.TemporaryDirectory() as folder:
            with patch.object(sys, "argv", ["issue_digest.py", "--dir", folder,
                                           "--since", "2026-11-05T00:00:00Z"]), \
                    patch.object(digest, "known_ids", return_value=known), \
                    patch.object(digest, "gh", side_effect=[[issue], []]), \
                    patch.object(digest.subprocess, "run", return_value=subprocess.CompletedProcess(
                        [], 0, stdout="1")), patch("builtins.print"):
                digest.main()
            markdown = (Path(folder) / "latest.md").read_text(encoding="utf-8")
            result = json.loads((Path(folder) / "latest.json").read_text(encoding="utf-8"))
            self.assertIn("仅部分型号（Gated Keyboard）", markdown)
            self.assertNotIn("已支持（Gated Keyboard）", markdown)
            self.assertIn("已支持（Ordinary Mouse）", markdown)
            self.assertIn('9999:0001 "Unknown Keyboard"  — 未支持', markdown)
            self.assertEqual([d["requiresModelMatch"] for d in result["newIssues"][0]["unknown"]],
                             [True, False, False])


class IndexGateTests(unittest.TestCase):
    def test_static_metadata_gates_arbitrary_ids_without_executing_providers(self):
        with tempfile.TemporaryDirectory() as folder:
            engine = Path(folder) / "engine"
            providers = engine / "providers"
            providers.mkdir(parents=True)
            catalog = {"readers": [
                {"provider": "astro", "kinds": ["headset"]},
                {"provider": "keychron", "kinds": ["mouse"], "matchScope": "仅产品全名 Test Mouse"},
                {"provider": "jbl", "kinds": ["headset"], "matchScope": "限既有型号表及HID集合"},
            ], "nativeProfiles": []}
            (engine / "compatibility.json").write_text(json.dumps(catalog), encoding="utf-8")
            for module, name in (("astro", "Test (model identification required)"),
                                 ("keychron", "Test Mouse"), ("jbl", "Test Headset")):
                (providers / (module + ".py")).write_text(
                    'raise AssertionError("provider executed")\nVID=0x1234\nPIDS={0xABCD: '
                    + repr(name) + '}\n', encoding="utf-8")
            rows = {d["provider"]: d for d in known_ids.build_index(engine_root=engine)["devices"]}
            self.assertTrue(rows["astro"]["requiresModelMatch"])
            self.assertTrue(rows["keychron"]["requiresModelMatch"])
            self.assertFalse(rows["jbl"].get("requiresModelMatch", False))


if __name__ == "__main__":
    unittest.main()
