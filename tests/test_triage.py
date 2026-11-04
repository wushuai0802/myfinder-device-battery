"""Offline triage regressions; no GitHub, HID, or third-party Python modules."""
import builtins
import importlib.util
import json
from pathlib import Path
import re
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


triage = load("issue_triage", ROOT / ".github/scripts/triage.py")
known_ids = load("offline_known_ids", ROOT / "engine/known_ids.py")
KNOWN = {"vendors": {"native_atk": [0x373B], "logitech": [0x046D]}, "devices": [
    {"provider": "native_atk", "vendorId": 0x373B, "productId": 0x1278, "name": "ATK A9 Air+", "kind": "mouse"},
    {"provider": "logitech", "vendorId": 0x046D, "productId": 0x0AB5, "name": "Logitech G733", "kind": "headset"},
], "unparsed": [], "partial": {}}


def report(card="无记录", unknown="无记录"):
    return ("MyFinder Device Battery 1.3.4\nWindows 10.0.26200\n\n有卡片的设备\n" + card
            + "\n\n已隐藏的重复连接\n无记录\n\n未取得电量的设备\n" + unknown
            + "\n\n读取器健康\nrazer | timeout | read_failed")


def in_app(diagnostics="", category="设备不识别", description="设备电量没有显示"):
    body = "来自程序内反馈 · 版本 1.3.4\n\n**类型**：" + category + "\n\n**描述**\n```\n" + description + "\n```"
    if diagnostics:
        body += "\n\n<details><summary>诊断信息</summary>\n\n````\n" + diagnostics + "\n````\n\n</details>"
    return body


def template(diagnostics):
    return ("### 品牌与完整型号\n测试设备\n\n### 本次连接方式\n2.4G 接收器\n\n"
            "### 官方软件显示的电量\n75%\n\n### 反馈报告\n```text\n" + diagnostics + "\n```")


def issue(body, labels=(), title="[设备适配] 测试设备", comments=()):
    return {"number": 10, "title": title, "body": body,
            "labels": [{"name": name} for name in labels], "user": {"login": "tester"},
            "comments": list(comments)}


def comment(body, bot=False):
    return {"body": body, "user": {"login": "github-actions[bot]" if bot else "tester",
                                  "type": "Bot" if bot else "User"}}


class TriageTests(unittest.TestCase):
    def classify(self, body, label, **kwargs):
        result = triage.decide(issue(body, **kwargs), KNOWN)
        self.assertEqual(result["label"], "triage:" + label)
        if label not in ("needs-info", "supported"):
            self.assertIsNone(result["reply"])
        return result

    def test_both_sources_match_card_and_unadapted_reports(self):
        for wrap in (in_app, template):
            for contents in (report(card="自定义产品名 | mouse | native_atk | 80% | 当前读数：是 | 373B:1278"),
                             report(unknown='373B:1278 "ATK 接收器" 0001:0002 FF02:0002')):
                with self.subTest(wrap=wrap.__name__, contents=contents):
                    result = self.classify(wrap(contents), "supported")
                    self.assertEqual(result["reply"], triage.MARKER + "\n" + triage.SUPPORTED.format(models="ATK A9 Air+"))
                    self.assertNotIn("自定义产品名", result["reply"])

    def test_supported_without_battery_explains_the_difference(self):
        result = self.classify(in_app(report(unknown='373B:1278 "ATK"')), "supported")
        self.assertIn("内置支持", result["reason"])
        self.assertIn("本次未取得电量", result["reason"])

    def test_needs_info_for_both_sources(self):
        for body in (in_app(), template("_No response_")):
            result = self.classify(body, "needs-info")
            self.assertEqual(result["reply"], triage.MARKER + "\n" + triage.NEEDS_INFO)

    def test_pack_candidate_requires_unknown_pid_and_same_row_vendor_page(self):
        self.classify(template(report(unknown='373B:EEEE "unknown" 0001:0002 FF00:0001')), "pack-candidate")
        self.classify(template(report(unknown='373B:1278 "known" FF00:0001')), "supported")
        self.classify(template(report(unknown='373B:EEEE "unknown" 0001:0002\nABCD:1234 "new" FF00:0001')), "new-vendor")

    def test_issue_10_standard_collections_win_over_new_vendor(self):
        self.classify(in_app(report(unknown='1D57:FA60 "2.4G Receiver" 0001:0002 0001:0006')), "insufficient-evidence")

    def test_new_vendor_with_vendor_collection(self):
        self.classify(template(report(unknown='ABCD:1234 "unknown" FF13:0001')), "new-vendor")

    def test_known_vendor_without_collections_is_insufficient(self):
        self.classify(template(report(unknown='373B:EEEE "unknown"')), "insufficient-evidence")

    def test_both_non_device_types_are_bugs_without_ids(self):
        for category in ("界面或操作", "其他"):
            self.classify(in_app(category=category), "bug")
            self.classify(in_app(report(unknown='ABCD:1234 "unknown" FF01:0001'), category), "new-vendor")

    def test_category_in_browser_fallback(self):
        self.classify(template("类型：界面或操作\n描述：\n窗口不显示"), "bug")
        self.classify(template("类型：电量或 DPI 不对\n描述：\n没有电量"), "needs-info")

    def test_reply_marker_only_counts_in_comments_and_supports_pages(self):
        for pages in ([comment("old\n" + triage.MARKER, True)], [[comment("old")], [comment(triage.MARKER, True)]]):
            result = triage.decide(issue(in_app(), labels=("triage:bug", "in-app-feedback")), KNOWN, pages)
            self.assertEqual(result["label"], "triage:needs-info")
            self.assertEqual(result["remove_labels"], ["triage:bug"])
            self.assertIsNone(result["reply"])
        self.assertIsNotNone(self.classify(in_app(description=triage.MARKER), "needs-info")["reply"])

    def test_untrusted_markers_do_not_suppress_replies(self):
        for diagnostics, label in (("", "needs-info"), (report(unknown='046D:0AB5 "G733"'), "supported")):
            body = in_app(diagnostics, description=triage.MARKER)
            for author in ({"login": "tester", "type": "User"},
                           {"login": "another[bot]", "type": "Bot"}, {}, None):
                forged = {"body": triage.MARKER, "user": author}
                with self.subTest(label=label, author=author):
                    embedded = triage.decide(issue(body, comments=[forged]), KNOWN)
                    paged = triage.decide(issue(body), KNOWN, [[forged]])
                    for result in (embedded, paged):
                        self.assertEqual(result["label"], "triage:" + label)
                        self.assertIsNotNone(result["reply"])
                        self.assertTrue(result["reply"].startswith(triage.MARKER + "\n"))

    def test_human_supplemental_report_updates_conclusion(self):
        self.classify(in_app(), "supported", comments=[comment(report(unknown='373B:1278 "ATK" FF02:0002'))])
        self.classify(in_app(), "needs-info", comments=[comment(report(unknown='373B:1278 "ATK" FF02:0002'), True)])

    def test_locked_is_complete_noop(self):
        result = triage.decide(issue(in_app(), labels=("triage:locked", "triage:bug")), KNOWN)
        self.assertIsNone(result["label"])
        self.assertIsNone(result["reply"])
        self.assertEqual(result["remove_labels"], [])

    def test_replace_only_other_triage_labels(self):
        result = self.classify(in_app(), "needs-info", labels=("triage:supported", "triage:bug", "triage:old,custom", "triage:needs-info", "in-app-feedback", "help wanted"))
        self.assertEqual(result["remove_labels"], ["triage:bug", "triage:old,custom", "triage:supported"])

    def test_noop_for_pr_or_unrelated_issue(self):
        for data in (issue("Documentation spelling", title="Docs"), dict(issue(in_app()), pull_request={})):
            result = triage.decide(data, KNOWN)
            self.assertIsNone(result["label"])
            self.assertIsNone(result["reply"])

    def test_usage_pairs_and_product_strings_are_not_device_ids(self):
        # A known ID in a product name/collection must not trigger supported.
        body = report(unknown='1D57:FA60 "pretend 373B:1278" 0001:0002 373B:1278')
        self.classify(template(body), "insufficient-evidence")
        self.classify(template(report(card="Product 373B:1278 | mouse | standard_hid | 75% | 当前读数：是")), "needs-info")
        self.classify(template("0001:0002 0001:0006 000C:0001"), "needs-info")

    def test_case_and_old_diagnostic_heading(self):
        body = report(unknown='373b:eeee "Receiver" ff02:0002').replace("未取得电量的设备", "未取得电量的设备（可能未适配，也可能正在休眠）")
        self.classify(in_app(body), "pack-candidate")

    def test_priority_supported_then_pack_then_standard_then_vendor(self):
        rows = ['373B:1278 "known" 0001:0002', '373B:EEEE "candidate" FF02:0002',
                '1D57:FA60 "standard" 0001:0006', 'ABCD:1234 "new" FF00:0001']
        self.classify(template(report(unknown="\n".join(rows))), "supported")
        self.classify(template(report(unknown="\n".join(rows[1:]))), "pack-candidate")
        self.classify(template(report(unknown=rows[2])), "insufficient-evidence")

    def test_malicious_and_long_body_stays_data(self):
        attack = '`$(touch SHOULD_NOT_EXIST)` @someone ${{ secrets.GITHUB_TOKEN }}\n' + triage.MARKER
        body = in_app(report(unknown='373B:1278 "' + attack.replace('\n', ' ') + '" FF02:0002'), description=attack + "x" * 300000)
        result = self.classify(body, "supported", title=attack)
        self.assertNotIn("@", result["reply"])
        self.assertNotIn("touch", result["reply"])
        self.assertEqual(result["reply"].splitlines()[0], triage.MARKER)
        self.assertLess(len(result["reply"]), 400)
        # Long descriptions must not hide a valid report at the end.
        self.assertIn("ATK A9 Air+", result["reply"])


class KnownIdsTests(unittest.TestCase):
    def test_real_index_is_deterministic_and_does_not_import_hardware(self):
        real_import = builtins.__import__

        def guarded(name, *args, **kwargs):
            if name.split(".")[0] in {"hid", "hidapi", "providers", "server", "ctypes"}:
                raise AssertionError("hardware module imported: " + name)
            return real_import(name, *args, **kwargs)

        with patch("builtins.__import__", side_effect=guarded):
            first = known_ids.build_index()
            self.assertEqual(first, known_ids.build_index())
        entries = {(d["provider"], d["vendorId"], d["productId"]): d for d in first["devices"]}
        for provider, vid, pid, name, kind in (
            ("native_atk", 0x373B, 0x1278, "ATK A9 Air+ / 用户已验证接收器变体", "mouse"),
            ("native_hyperx", 0x03F0, 0x098D, "HyperX Cloud Alpha Wireless", "headset"),
            ("pulsar", 0x3554, 0xF58A, "VXE R1 Pro Max (2.4 GHz)", "mouse"),
            ("steelseries", 0x1038, 0x12E0, "Arctis Nova Pro Wireless", "headset"),
            ("steelseries", 0x1038, 0x1830, "SteelSeries Rival 3 Wireless", "mouse"),
            ("asus", 0x0B05, 0x1A72, "ROG Gladius III Aimpoint", "mouse"),
            ("lamzu", 0x373E, 0x001E, "LAMZU Maya X", "mouse"),
            ("razer", 0x1532, 0x025A, "Razer BlackWidow V3 Pro", "keyboard"),
            ("logitech", 0x046D, 0x0AB5, "Logitech G733", "headset"),
            ("logitech", 0x046D, 0xC548, "Logitech HID++ 接收器", "device"),
            ("mchose", 0x2023, 0xF013, "MCHOSE A5 Pro Max (1K receiver)", "mouse"),
            ("mchose", 0xA8A5, 0x2255, "MCHOSE mouse", "mouse"),
        ):
            with self.subTest(provider=provider, pid=pid):
                row = entries[(provider, vid, pid)]
                self.assertEqual((row["name"], row["kind"]), (name, kind))
        self.assertEqual(first["unparsed"], ["bluetooth"])
        self.assertEqual(set(first["partial"]), {"logitech", "mchose"})
        for module, reason in first["partial"].items():
            self.assertTrue(reason.strip())
            self.assertTrue(any(d["provider"] == module for d in first["devices"]))
        self.assertFalse(any(d["provider"] in first["unparsed"] for d in first["devices"]))
        self.assertEqual(first["vendors"]["mchose"], sorted([0x5253, 0x3837, 0xA8A5, 0x2023]))
        self.assertNotIn(("mchose", 0x5253, 0x0031), entries)  # Reply model code.
        self.assertNotIn(("lofree", 0x388D, 0x0024), entries)  # Cable is deliberately not read.
        self.assertFalse(any(d["vendorId"] == 0x05AC and d["productId"] == 0x024F for d in first["devices"]))
        self.assertGreater(len(entries), 250)

    def test_real_logitech_index_drives_triage(self):
        known = known_ids.build_index()
        for wrap in (in_app, template):
            for row, label in (('046D:0AB5 "G733"', "supported"),
                               ('046D:EEEE "unknown" FF00:0001', "pack-candidate")):
                with self.subTest(wrap=wrap.__name__, label=label):
                    result = triage.decide(issue(wrap(report(unknown=row))), known)
                    self.assertEqual(result["label"], "triage:" + label)
                    if label == "supported":
                        self.assertIn("Logitech G733", result["reply"])
                    else:
                        self.assertIsNone(result["reply"])

    def test_incomplete_source_is_unparsed_only_without_indexed_ids(self):
        with workspace_temp() as folder:
            root = Path(folder)
            providers = root / "engine/providers"
            providers.mkdir(parents=True)
            (root / "engine/compatibility.json").write_text('{"readers": [], "nativeProfiles": []}', encoding="utf-8")
            for headsets in ('{}', '{0x0AB5: "Logitech G733"}'):
                with self.subTest(headsets=headsets):
                    (providers / "logitech.py").write_text(
                        'LOGITECH_VID=0x046D\nHEADSETS=' + headsets + '\nRECEIVERS=discover_devices()\n', encoding="utf-8")
                    result = known_ids.build_index(root)
                    if result["devices"]:
                        self.assertEqual(result["unparsed"], [])
                        self.assertEqual(set(result["partial"]), {"logitech"})
                        self.assertTrue(result["partial"]["logitech"].strip())
                    else:
                        self.assertEqual(result["unparsed"], ["logitech"])
                        self.assertEqual(result["partial"], {})

    def test_unknown_source_and_pack_hook_without_executing_source(self):
        with workspace_temp() as folder:
            root = Path(folder)
            providers = root / "engine/providers"
            providers.mkdir(parents=True)
            (root / "engine/compatibility.json").write_text('{"readers": [], "nativeProfiles": []}', encoding="utf-8")
            (providers / "future.py").write_text('raise RuntimeError("must not execute")\nFUTURE_VID=0x1234\nPIDS=discover_devices()\n', encoding="utf-8")
            initial = known_ids.build_index(root)
            self.assertEqual(initial["unparsed"], ["future"])
            self.assertEqual(initial["devices"], [])
            pack = root / "devices/pack"
            pack.mkdir(parents=True)
            (pack / "example.json").write_text(json.dumps({"devices": [
                {"provider": "future", "vendorId": "1234", "productId": "ABCD", "name": "Fixture", "kind": "mouse"}
            ]}), encoding="utf-8")
            (pack / "bad.json").write_text('{"unexpected": []}', encoding="utf-8")
            result = known_ids.build_index(root)
            self.assertEqual(result["devices"][0]["productId"], 0xABCD)
            self.assertEqual(result["unparsed"], ["devices/pack/bad.json", "future"])

    def test_command_line_json_contract(self):
        with workspace_temp() as folder:
            work = Path(folder)
            index = subprocess.run([sys.executable, "-I", "-B", str(ROOT / "engine/known_ids.py"), "--json"],
                                   check=True, capture_output=True)
            self.assertTrue(json.loads(index.stdout)["devices"])
            (work / "known.json").write_bytes(index.stdout)
            payload = issue(in_app(), comments=[])
            (work / "issue.json").write_text(json.dumps(payload), encoding="utf-8")
            (work / "comments.json").write_text(json.dumps([[comment(report(unknown='373B:1278 "ATK" FF02:0002'))]]), encoding="utf-8")
            run = subprocess.run([sys.executable, "-I", "-B", str(ROOT / ".github/scripts/triage.py"),
                                  "--issue", str(work / "issue.json"), "--comments", str(work / "comments.json"),
                                  "--known-ids", str(work / "known.json")], check=True, capture_output=True)
            self.assertEqual(json.loads(run.stdout)["label"], "triage:supported")


def workspace_temp():
    target = ROOT / "tmp"
    target.mkdir(exist_ok=True)
    return tempfile.TemporaryDirectory(prefix="triage-test-", dir=target)


class WorkflowTests(unittest.TestCase):
    def test_run_blocks_never_interpolate_issue_text(self):
        text = (ROOT / ".github/workflows/issue-triage.yml").read_text(encoding="utf-8")
        # Scan every expression, including multiline expressions. This is stricter
        # than checking only run blocks, and does not need a YAML dependency.
        for expression in re.findall(r"\$\{\{(.*?)\}\}", text, re.DOTALL):
            self.assertNotRegex(expression, r"github\s*\.\s*event\s*\.\s*(?:issue\s*\.\s*(?:title|body)|comment\s*\.\s*body)")
        self.assertIn("cancel-in-progress: false", text)
        self.assertIn("!github.event.issue.pull_request", text)
        self.assertIn("github.event.comment.user.type != 'Bot'", text)
        self.assertIn("--paginate --slurp", text)
        self.assertIn("--body-file", text)
        self.assertIn(".remove_labels[] | @uri", text)


if __name__ == "__main__":
    unittest.main()
