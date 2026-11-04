"""Offline triage regressions; no GitHub, HID, or third-party Python modules."""
import builtins
import importlib.util
import json
from pathlib import Path
import re
import shutil
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
    def test_issue_29_release_and_main_have_distinct_support(self):
        next_index = {"vendors": {"mchose_headset": [0x291D]}, "devices": [
            {"provider": "mchose_headset", "vendorId": 0x291D, "productId": 0x385D,
             "name": "MCHOSE V9 PRO", "kind": "headset"}]}
        body = in_app(report(unknown='291D:385D "untrusted @name $(command)" FF00:0001'))
        payload = issue(body, labels=("triage:supported", "in-app-feedback"))
        payload["number"] = 29
        result = triage.decide(payload, KNOWN, known_next=next_index)
        self.assertEqual(result["label"], "triage:in-next-release")
        self.assertEqual(result["remove_labels"], ["triage:supported"])
        self.assertEqual(result["reply"], triage.MARKER + "\n" +
                         triage.IN_NEXT_RELEASE.format(models="MCHOSE V9 PRO"))
        self.assertNotIn("untrusted", result["reply"])
        released = triage.decide(payload, next_index, known_next=next_index)
        self.assertEqual(released["label"], "triage:supported")
        self.assertEqual(released["reply"], triage.MARKER + "\n" +
                         triage.SUPPORTED.format(models="MCHOSE V9 PRO"))
        # A release hit wins when a report includes both release and main-only IDs.
        mixed = issue(in_app(report(unknown='291D:385D "V9" FF00:0001\n373B:1278 "ATK" FF02:0002')))
        self.assertEqual(triage.decide(mixed, KNOWN, known_next=next_index)["label"], "triage:supported")
        for bot_comment, suppressed in (
            (comment(triage.MARKER, True), True),
            (comment(triage.MARKER), False),
            ({"body": triage.MARKER, "user": {"type": "Bot", "login": "other[bot]"}}, False),
        ):
            with self.subTest(comment=bot_comment):
                result = triage.decide(payload, KNOWN, [[bot_comment]], next_index)
                self.assertEqual(result["reply"] is None, suppressed)
        locked = issue(body, labels=("triage:locked",))
        self.assertIsNone(triage.decide(locked, KNOWN, known_next=next_index)["label"])
        # Cards, noise and loose mentions must not become a development-support hit.
        for body in (in_app(description='291D:385D "V9" FF00:0001'),
                     in_app(report(card='V9 | headset | mchose_headset | 80% | 当前读数：是 | 291D:385D')),
                     in_app(report(unknown='291D:385D "Touchpad" FF00:0001'))):
            self.assertNotEqual(triage.decide(issue(body), KNOWN, known_next=next_index)["label"],
                                "triage:in-next-release")
        # Keep the existing main-vendor rules when neither index matches an ID.
        for row, expected in (('291D:EEEE "unknown" FF00:0001', "pack-candidate"),
                              ('291D:EEEE "unknown" 0001:0006', "insufficient-evidence"),
                              ('ABCD:1234 "unknown" FF00:0001', "new-vendor")):
            result = triage.decide(issue(in_app(report(unknown=row))), KNOWN, known_next=next_index)
            self.assertEqual(result["label"], "triage:" + expected)
            self.assertIsNone(result["reply"])
        loose = triage.decide(issue(in_app(description='291D:385D "V9" FF00:0001')),
                              KNOWN, known_next=next_index)
        self.assertEqual(loose["label"], "triage:insufficient-evidence")

    def classify(self, body, label, **kwargs):
        result = triage.decide(issue(body, **kwargs), KNOWN)
        self.assertEqual(result["label"], "triage:" + label)
        if label not in ("needs-info", "supported"):
            self.assertIsNone(result["reply"])
        return result

    def test_both_sources_only_match_unadapted_reports(self):
        for wrap in (in_app, template):
            with self.subTest(wrap=wrap.__name__):
                self.classify(wrap(report(card="自定义产品名 | mouse | native_atk | 80% | 当前读数：是 | 373B:1278")),
                              "insufficient-evidence")
                result = self.classify(wrap(report(unknown='373B:1278 "ATK 接收器" 0001:0002 FF02:0002')), "supported")
                self.assertEqual(result["reply"], triage.MARKER + "\n" + triage.SUPPORTED.format(models="ATK A9 Air+"))

    def test_cards_do_not_classify_devices_or_supply_reply_models(self):
        for card_id in ("046D:0AB5", "373B:EEEE", "ABCD:1234"):
            card = "自定义产品名 | mouse | provider | 80% | 当前读数：是 | " + card_id
            for wrap in (in_app, template):
                with self.subTest(card_id=card_id, wrap=wrap.__name__):
                    result = self.classify(wrap(report(card=card, unknown='373B:1278 "ATK" FF02:0002')), "supported")
                    self.assertIsNone(result["reply"])
                    self.assertNotIn("自定义产品名", result["reason"])
                    self.classify(wrap(report(card=card, unknown='373B:EEEE "unknown" 0001:0006')), "insufficient-evidence")
                    self.classify(wrap(report(card=card, unknown='ABCD:1234 "new" FF01:0001')), "new-vendor")

    def test_old_format_percentage_lines_are_cards_anywhere(self):
        card = (None, None, (), "card", "")
        for kind in ("mouse", "KEYBOARD", "Headset", "鼠标", "键盘", "耳机"):
            for colon in ("：", ":"):
                for level in ("0", "90", "100"):
                    line = kind + colon + "Product 373B:1278 " + level + "% · 充电中"
                    for text in (line, report(card=line), report(unknown=line),
                                 "已隐藏的重复连接\n" + line, "读取器健康\n" + line):
                        with self.subTest(kind=kind, colon=colon, level=level, text=text):
                            self.assertEqual(triage.report_devices(text), [card])

    def test_old_format_text_without_a_percentage_is_not_a_card(self):
        for line in ("鼠标：鼠标 --", "耳机：耳机 --", "耳机无线：未确认连接",
                     "鼠标读取：Not yet read", "mouse: Mouse --", "keyboard: Keyboard 80",
                     "headset: Not connected", "mouse: Mouse 1000%", "mouse: 90%",
                     "mouse wireless: Mouse 80%", "耳机无线：耳机 80%"):
            with self.subTest(line=line):
                self.assertEqual(triage.report_devices(line), [])

    def test_card_and_only_support_hits_need_human_review(self):
        for known, next_known, label in ((KNOWN, None, "supported"),
                                         ({"vendors": {}, "devices": []}, KNOWN, "in-next-release")):
            for card in ("Mouse | mouse | provider | 80% | 当前读数：是", "mouse：Mouse 80%"):
                for unknown in ('373B:1278 "ATK" FF02:0002',
                                '373B:1278 "ATK" FF02:0002\n0B05:18A5 "ROG AURA MOTHERBOARD" FF72:00A1'):
                    with self.subTest(label=label, card=card, unknown=unknown):
                        result = triage.decide(issue(in_app(report(card=card, unknown=unknown))),
                                               known, known_next=next_known)
                        self.assertEqual(result["label"], "triage:" + label)
                        self.assertIsNone(result["reply"])
                        for word in ("卡片", "接收器", "人工"):
                            self.assertIn(word, result["reason"])

    def test_card_support_hits_leave_other_rows_to_classify(self):
        for released in (True, False):
            for row, label in (('373B:EEEE "candidate" FF02:0002', "pack-candidate"),
                               ('ABCD:1234 "new" FF00:0001', "new-vendor"),
                               ('046D:EEEE "standard" 0001:0006', "insufficient-evidence"),
                               ('373B:EEEE "unknown"', "insufficient-evidence")):
                for rows in ((row, '373B:1278 "ATK" FF02:0002'),
                             ('373B:1278 "ATK" FF02:0002', row)):
                    with self.subTest(released=released, label=label, rows=rows):
                        known = KNOWN if released else {"vendors": {}, "devices": []}
                        result = triage.decide(issue(in_app(report(card="mouse：Mouse 80%", unknown="\n".join(rows)))),
                                               known, known_next=None if released else KNOWN)
                        self.assertEqual(result["label"], "triage:" + label)
                        self.assertIsNone(result["reply"])

    def test_no_card_still_replies_to_support_hits(self):
        for known, next_known, label, reply in (
                (KNOWN, None, "supported", triage.SUPPORTED),
                ({"vendors": {}, "devices": []}, KNOWN, "in-next-release", triage.IN_NEXT_RELEASE)):
            body = "鼠标：鼠标 --\n耳机无线：未确认连接\n鼠标读取：Not yet read\n"
            body += in_app(report(unknown='373B:1278 "ATK" FF02:0002'))
            with self.subTest(label=label):
                result = triage.decide(issue(body), known, known_next=next_known)
                self.assertEqual(result["label"], "triage:" + label)
                self.assertEqual(result["reply"], triage.MARKER + "\n" + reply.format(models="ATK A9 Air+"))

    def test_card_only_incorrect_reading_is_a_bug(self):
        card = "ATK | mouse | native_atk | 80% | 当前读数：是 | 373B:1278"
        self.classify(in_app(report(card=card), category="电量或 DPI 不对"), "bug")
        self.classify(template("类型：电量或 DPI 不对\n" + report(card=card)), "bug")
        self.classify(in_app(category="电量或 DPI 不对"), "bug", comments=[comment(report(card=card))])
        for row, label in (('373B:1278 "ATK" FF02:0002', "supported"),
                           ('ABCD:1234 "new" FF01:0001', "new-vendor"),
                           ('06CB:CE7D "HIDI2C Device" 0001:0002', "insufficient-evidence")):
            self.classify(in_app(report(card=card, unknown=row), category="电量或 DPI 不对"), label)

    def test_card_presence_does_not_require_a_device_id(self):
        for card in ("Bluetooth Mouse | mouse | bluetooth | 80% | 当前读数：是",
                     "Product 373B:1278 | mouse | standard_hid | 75% | 当前读数：是"):
            with self.subTest(card=card):
                self.classify(in_app(report(card=card)), "insufficient-evidence")
                self.classify(in_app(report(card=card), category="电量或 DPI 不对"), "bug")
                self.classify(in_app(report(card=card), category="界面或操作"), "bug")

    def test_loose_mentions_classify_but_cannot_trigger_supported(self):
        for row, label in (('373B:1278 "ATK" FF02:0002', "insufficient-evidence"),
                           ('373B:EEEE "candidate" FF02:0002', "pack-candidate"),
                           ('ABCD:1234 "new" 0001:0006', "new-vendor")):
            with self.subTest(row=row):
                self.classify(in_app(description=row), label)
                self.classify(in_app(), label, comments=[comment(row)])
        result = self.classify(in_app(report(unknown='373B:1278 "ATK" FF02:0002')),
                               "supported", comments=[comment('046D:0AB5 "G733" FF00:0001')])
        self.assertNotIn("Logitech G733", result["reply"])

    def test_human_comment_cards_are_excluded_and_bot_cards_do_not_satisfy_needs_info(self):
        card_report = report(card="G733 | headset | logitech | 80% | 当前读数：是 | 046D:0AB5")
        self.classify(in_app(), "insufficient-evidence", comments=[comment(card_report)])
        self.classify(in_app(), "needs-info", comments=[comment(card_report, True)])
        result = self.classify(in_app(report(unknown='373B:1278 "ATK" FF02:0002')),
                               "supported", comments=[comment(card_report)])
        self.assertIsNone(result["reply"])

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

    def test_issue_10_new_vendor_precedes_standard_collections(self):
        self.classify(in_app(report(unknown='1D57:FA60 "2.4G Receiver" 0001:0002 0001:0006')), "new-vendor")

    def test_noise_is_ignored_but_retained_in_reasons(self):
        noises = ('04F3:31C0 "HIDI2C Device" 0001:0002',
                  '06CB:CE7D "HIDI2C Device" FF00:0001',
                  '00FF:BACC "Virtual Multitouch Device" FFAA:00BB',
                  'ABCD:1234 "touchPAD" 0001:0006 FF00:0001',
                  'ABCD:1234 Precision Device FF00:0001',
                  'ABCD:1234 "Receiver" 0001:0002',
                  'ABCD:1234 "Receiver" 0001:0002 0001:0002',
                  '0B05:18A5 "ROG AURA MOTHERBOARD" FF72:00A1',
                  '0B05:18A5 "ROG AURA motherboard" FF72:00A1',
                  '373B:1278 "ATK" 0001:0002',
                  '373B:EEEE "Precision Device" FF00:0001')
        for noise in noises:
            with self.subTest(noise=noise):
                for body in (in_app(report(unknown=noise)), in_app(description=noise)):
                    result = self.classify(body, "insufficient-evidence")
                    self.assertIn("标准 HID", result["reason"])
                    self.assertIn("已忽略", result["reason"])
                    self.assertIn(noise[:9], result["reason"])
                # Noise must not turn a known vendor's standard-only row into a
                # support hit, pack candidate, or new-vendor classification.
                result = self.classify(template(report(unknown=noise + '\n046D:EEEE "Receiver" 0001:0006')),
                                       "insufficient-evidence")
                self.assertIn(noise[:9], result["reason"])
                result = self.classify(in_app(), "insufficient-evidence", comments=[comment(noise)])
                self.assertIn("已忽略", result["reason"])
        for noise in ('HIDI2C Device 06CB:CE7D FF00:0001', '"Virtual Multitouch Device" 00FF:BACC FFAA:00BB'):
            for body, comments in ((in_app(description=noise), []), (in_app(), [comment(noise)])):
                result = self.classify(body, "insufficient-evidence", comments=comments)
                self.assertIn("已忽略", result["reason"])

    def test_noise_vendor_ids_are_not_blanket_exclusions(self):
        for device_id in ("04F3:31C0", "06CB:CE7D", "00FF:BACC", "0B05:18A5"):
            self.classify(in_app(report(unknown=device_id + ' "Receiver" 0001:0006 FF00:0001')), "new-vendor")

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
        self.classify(template(body), "new-vendor")
        self.classify(template(report(card="Product 373B:1278 | mouse | standard_hid | 75% | 当前读数：是")), "insufficient-evidence")
        self.classify(template("0001:0002 0001:0006 000C:0001"), "needs-info")

    def test_case_and_old_diagnostic_heading(self):
        body = report(unknown='373b:eeee "Receiver" ff02:0002').replace("未取得电量的设备", "未取得电量的设备（可能未适配，也可能正在休眠）")
        self.classify(in_app(body), "pack-candidate")

    def test_priority_supported_then_pack_then_vendor_then_insufficient(self):
        rows = ['373B:1278 "known" 0001:0002 FF02:0002', '373B:EEEE "candidate" FF02:0002',
                'ABCD:1234 "new" 0001:0006', '046D:EEEE "standard" 0001:0006']
        for offset, label in enumerate(("supported", "pack-candidate", "new-vendor", "insufficient-evidence")):
            for remaining in (rows[offset:], list(reversed(rows[offset:]))):
                with self.subTest(label=label, rows=remaining):
                    self.classify(template(report(unknown="\n".join(remaining))), label)

    def test_real_issue_fixtures_ignore_cards_and_noise(self):
        known = known_ids.build_index()
        for number, unknown_id, ignored_ids, card_ids in (
            (29, "25A7:FA70", ("00FF:BACC", "06CB:CE7D"), ("A8A5:2255",)),
            (30, "19F5:3247", ("04F3:31C0",), ("373B:1031", "046D:C547")),
        ):
            with self.subTest(issue=number):
                body = (ROOT / f"tests/fixtures/issue{number}-body.txt").read_text(encoding="utf-8")
                result = triage.decide(issue(body, labels=("triage:supported", "in-app-feedback")), known)
                # A working card makes a support hit ambiguous; classify the other device.
                self.assertEqual(result["label"], "triage:new-vendor")
                self.assertIsNone(result["reply"])
                self.assertIn(unknown_id, result["reason"])
                self.assertEqual(result["remove_labels"], ["triage:supported"])
                classification, ignored = result["reason"].split(" 已忽略", 1)
                for device_id in ignored_ids:
                    self.assertNotIn(device_id, classification)
                    self.assertIn(device_id, ignored)
                for device_id in card_ids:
                    self.assertNotIn(device_id, result["reason"])

    def test_issue_65_ignores_working_card_receiver_and_motherboard(self):
        body = (ROOT / "tests/fixtures/issue65-body.txt").read_text(encoding="utf-8")
        result = triage.decide(issue(body, labels=("triage:supported",)), known_ids.build_index())
        self.assertEqual(result["label"], "triage:new-vendor")
        self.assertIsNone(result["reply"])
        self.assertEqual(result["remove_labels"], ["triage:supported"])
        classification, ignored = result["reason"].split(" 已忽略", 1)
        self.assertIn("342D:E407", classification)
        self.assertNotIn("046D:C547", result["reason"])
        self.assertNotIn("0B05:18A5", classification)
        self.assertIn("0B05:18A5", ignored)

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
    def test_legacy_headset_snapshot_keeps_its_single_vendor_identity(self):
        with workspace_temp() as folder:
            engine = Path(folder) / "old-engine"
            providers = engine / "providers"
            providers.mkdir(parents=True)
            (engine / "compatibility.json").write_text(
                '{"readers": [{"provider": "mchose_headset", "kinds": ["headset"]}], "nativeProfiles": []}',
                encoding="utf-8")
            (providers / "mchose_headset.py").write_text(
                'raise RuntimeError("old reader executed")\nVID = 0x291D\n'
                'PIDS = {0x385D: "MCHOSE V9 PRO"}\n', encoding="utf-8")
            result = known_ids.build_index(engine_root=engine)
            self.assertEqual(result["devices"], [dict(provider="mchose_headset", vendorId=0x291D,
                                                    productId=0x385D, name="MCHOSE V9 PRO",
                                                    kind="headset", source="builtin")])
            self.assertEqual(result["vendors"], {"mchose_headset": [0x291D]})
            self.assertEqual(result["unparsed"], [])
            self.assertEqual(result["partial"], {})

    def test_external_engine_reads_only_legacy_snapshot_without_imports(self):
        with workspace_temp() as folder:
            engine = Path(folder) / "old-engine"
            providers = engine / "providers"
            providers.mkdir(parents=True)
            (engine / "compatibility.json").write_text('{"readers": [], "nativeProfiles": []}', encoding="utf-8")
            # Any attempt to import snapshot code fails, including package setup.
            for name in ("__init__", "profiles", "razer_profiles"):
                (providers / (name + ".py")).write_text('raise RuntimeError("old code executed")', encoding="utf-8")
            (providers / "razer.py").write_text(
                'raise RuntimeError("old reader executed")\nRAZER_VID = 0x1532\n'
                'KNOWN = {0x001F: ("Old Mouse", 0xFF), 0x025A: ("Old Keyboard", 0x3F), '
                '0x0528: ("Old Headset", 0x3F), 0x0290: ("Interface Keyboard", 0x9F)}\n'
                'KEYBOARD_INTERFACE = {0x0290: 2}\n', encoding="utf-8")
            with patch.object(known_ids, "registered_ids", side_effect=AssertionError("main registry used")), \
                    patch.object(known_ids, "profiles", side_effect=AssertionError("main profiles used")):
                result = known_ids.build_index(engine_root=engine)
            self.assertEqual({d["productId"]: (d["name"], d["kind"]) for d in result["devices"]}, {
                0x001F: ("Old Mouse", "mouse"), 0x025A: ("Old Keyboard", "keyboard"),
                0x0528: ("Old Headset", "headset"), 0x0290: ("Interface Keyboard", "keyboard")})
            self.assertEqual(result["unparsed"], [])
            run = subprocess.run([sys.executable, "-I", "-B", str(ROOT / "engine/known_ids.py"),
                                  "--json", "--engine-root", str(engine)],
                                 check=True, capture_output=True, timeout=10)
            self.assertEqual(json.loads(run.stdout), result)

    def test_external_profile_tables_are_static_and_do_not_leak_main_ids(self):
        with workspace_temp() as folder:
            root = index_fixture(folder)
            result = known_ids.build_index(engine_root=root / "engine")
            self.assertEqual(result, known_ids.build_index())
            profile = root / "engine/providers/razer_profiles.py"
            profile.write_text('raise RuntimeError("must not execute")\nRAZER_VID = 0x1532\n'
                               'KNOWN = {0x001F: ("Snapshot Mouse", 0xFF)}\n', encoding="utf-8")
            result = known_ids.build_index(engine_root=root / "engine")
            self.assertEqual([(d["productId"], d["name"]) for d in result["devices"]
                              if d["provider"] == "razer"], [(0x001F, "Snapshot Mouse")])

    def test_real_index_is_deterministic_and_does_not_import_hardware(self):
        real_import = builtins.__import__

        def guarded(name, *args, **kwargs):
            if name.split(".")[0] in {"hid", "hidapi", "server", "ctypes"}:
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
            ("mchose_headset", 0x291D, 0x385D, "MCHOSE V9 PRO", "headset"),
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

    def test_razer_registry_preserves_80_builtin_ids(self):
        result = known_ids.build_index()
        rows = {d["productId"]: d for d in result["devices"] if d["provider"] == "razer"}
        self.assertEqual(len(rows), 80)
        for pid, name, kind in (
            (0x001F, "Razer Naga Epic", "mouse"),
            (0x00D7, "Razer Basilisk V3 Pro 35K Phantom Green", "mouse"),
            (0x025A, "Razer BlackWidow V3 Pro", "keyboard"),
            (0x025C, "Razer BlackWidow V3 Pro", "keyboard"),
            (0x0528, "Razer BlackShark V2 Pro (2020)", "headset"),
            (0x0557, "Razer BlackShark V2 Pro (2023)", "headset"),
        ):
            with self.subTest(pid=pid):
                self.assertEqual(rows[pid], {"provider": "razer", "vendorId": 0x1532,
                                            "productId": pid, "name": name, "kind": kind,
                                            "source": "builtin"})
        self.assertTrue({0x0095, 0x00CB, 0x052E, 0x053A}.isdisjoint(rows))
        self.assertNotIn("profiles", result["vendors"])
        self.assertNotIn("razer_profiles", result["vendors"])

    def test_fresh_cli_imports_only_offline_provider_modules(self):
        code = '''
from pathlib import Path
import runpy
import sys

class NoHardware:
    def find_spec(self, fullname, path=None, target=None):
        if (fullname.split(".")[0] in {"hid", "hidapi", "server", "ctypes"}
                or (fullname.startswith("providers.")
                    and fullname not in {"providers.base", "providers.profiles"}
                    and not fullname.endswith("_profiles"))):
            raise AssertionError("hardware module imported: " + fullname)

sys.meta_path.insert(0, NoHardware())
target = sys.argv[1]
# Match ordinary Python -I, even when the bundled runtime adds engine via ._pth.
sys.path = [p for p in sys.path if Path(p).resolve() != Path(target).resolve().parent]
sys.argv = [target, "--json"]
runpy.run_path(target, run_name="__main__")
'''
        run = subprocess.run([sys.executable, "-I", "-B", "-c", code,
                              str(ROOT / "engine/known_ids.py")],
                             capture_output=True, text=True, timeout=10)
        self.assertEqual(run.returncode, 0, run.stderr)
        self.assertEqual(json.loads(run.stdout), known_ids.build_index())

    def test_future_profiled_provider_uses_registry_without_parsing_reader(self):
        from types import SimpleNamespace

        with workspace_temp() as folder:
            root = Path(folder)
            providers = root / "engine/providers"
            providers.mkdir(parents=True)
            (root / "engine/compatibility.json").write_text('{"readers": [], "nativeProfiles": []}', encoding="utf-8")
            for name in ("future", "future_profiles", "profiles"):
                (providers / (name + ".py")).write_text('raise RuntimeError("must not execute")', encoding="utf-8")
            row = ("future", 0x1234, 0xABCD, "Future Keyboard", "keyboard", "builtin")
            with patch.object(known_ids, "profiles", return_value=(SimpleNamespace(provider="future", vendor_ids=(0x1234,)),)), \
                    patch.object(known_ids, "registered_ids", return_value=[row]):
                result = known_ids.build_index(root)
            self.assertEqual(result["devices"], [dict(zip(
                ("provider", "vendorId", "productId", "name", "kind", "source"), row))])
            self.assertEqual(result["vendors"], {"future": [0x1234]})
            self.assertEqual(result["unparsed"], [])
            self.assertEqual(result["partial"], {})

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
            (pack / "example.json").write_text(json.dumps(pack_document([pack_entry()])), encoding="utf-8")
            (pack / "bad.json").write_text('{"unexpected": []}', encoding="utf-8")
            result = known_ids.build_index(root)
            self.assertEqual(result["devices"], [{"provider": "razer", "vendorId": 0x1532,
                                                "productId": 0xFE01, "name": "Pack Mouse",
                                                "kind": "mouse", "source": "pack"}])
            self.assertEqual(result["unparsed"], ["devices/pack/bad.json", "future"])

    def test_pack_disables_override_builtin_legacy_and_other_pack_files(self):
        with workspace_temp() as folder:
            root = index_fixture(folder)
            pack_dir = root / "devices/pack"
            baseline = known_ids.build_index(root)
            self.assertEqual(baseline, known_ids.build_index())  # Missing directory is optional.
            pack_dir.mkdir(parents=True)
            additions = pack_document([pack_entry(), pack_entry("FE02")])
            denied = [("razer", "1532", "00B9"), ("razer", "1532", "FE01"),
                      ("logitech", "046D", "0AB5")]
            disables = pack_document(disabled=[
                {"provider": provider, "vendorId": vid, "productId": pid, "reason": "fixture"}
                for provider, vid, pid in denied
            ] + [{"provider": "barracuda", "vendorId": "1532", "productId": "FE02", "reason": "other provider"}])
            for first, second in ((additions, disables), (disables, additions)):
                with self.subTest(disables_first=first is disables):
                    (pack_dir / "a.json").write_text(json.dumps(first), encoding="utf-8")
                    (pack_dir / "z.json").write_text(json.dumps(second), encoding="utf-8")
                    result = known_ids.build_index(root)
                    keys = {(d["provider"], d["vendorId"], d["productId"]): d for d in result["devices"]}
                    for provider, vid, pid in denied:
                        self.assertNotIn((provider, int(vid, 16), int(pid, 16)), keys)
                    self.assertEqual(keys["razer", 0x1532, 0xFE02]["source"], "pack")
                    self.assertEqual(result["unparsed"], baseline["unparsed"])
                    self.assertEqual(result["partial"], baseline["partial"])
                    self.assertEqual(result, known_ids.build_index(root))
            self.assertEqual(known_ids.build_index(), baseline)  # No global pack state.

    def test_pack_validation_matches_engine_acceptance_and_fallback(self):
        from device_pack import MAX_BYTES, load_pack

        with workspace_temp() as folder:
            root = index_fixture(folder)
            baseline = known_ids.build_index(root)
            pack_dir = root / "devices/pack"
            pack_dir.mkdir(parents=True)
            path = pack_dir / "fixture.json"
            invalid_revision = pack_document([pack_entry(profileRev=99)])
            native_disable = {"provider": "native_atk", "vendorId": "373B", "productId": "1278", "reason": "unsupported"}
            cases = [
                "{bad", '{"devices": []}',
                json.dumps(pack_document())[:-1] + ', "loader": 1}',
                " " * (MAX_BYTES + 1),
                json.dumps(invalid_revision),
                json.dumps(pack_document([pack_entry("00B9"), pack_entry("0095"), pack_entry()])),
                json.dumps(pack_document([pack_entry(profile="unknown"), pack_entry("FE02")])),
                json.dumps(pack_document([pack_entry(), pack_entry(name="duplicate"), pack_entry("FE02")])),
                json.dumps(pack_document([pack_entry(kind="keyboard"), pack_entry("FE02")])),
                json.dumps(pack_document([pack_entry()], [native_disable])),
                json.dumps(pack_document([pack_entry(name=" ")])),
            ]
            for document in cases:
                with self.subTest(document=document[:120]):
                    path.write_text(document, encoding="utf-8")
                    validated = load_pack(path)
                    result = known_ids.build_index(root)
                    actual = [d for d in result["devices"] if d["source"] == "pack"]
                    expected = [{"provider": e.provider, "vendorId": e.match.vendor_id,
                                 "productId": e.match.product_id, "name": e.name,
                                 "kind": e.kind, "source": "pack"} for e in validated.entries]
                    self.assertEqual(actual, expected)
                    self.assertEqual([d for d in result["devices"] if d["source"] == "builtin"], baseline["devices"])
                    self.assertEqual(result["unparsed"], sorted(baseline["unparsed"] +
                                     (["devices/pack/fixture.json"] if validated.rejected else [])))

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

    def test_command_line_accepts_release_and_next_indexes(self):
        with workspace_temp() as folder:
            work = Path(folder)
            (work / "released.json").write_text(json.dumps(KNOWN), encoding="utf-8")
            (work / "next.json").write_text(json.dumps(known_ids.build_index()), encoding="utf-8")
            (work / "issue.json").write_text(json.dumps(issue(in_app(
                report(unknown='291D:385D "V9 PRO" FF00:0001')))), encoding="utf-8")
            run = subprocess.run([sys.executable, "-I", "-B", str(ROOT / ".github/scripts/triage.py"),
                                  "--issue", str(work / "issue.json"), "--known-ids", str(work / "released.json"),
                                  "--known-ids-next", str(work / "next.json")],
                                 check=True, capture_output=True, timeout=10)
            self.assertEqual(json.loads(run.stdout)["label"], "triage:in-next-release")


def workspace_temp():
    target = ROOT / "tmp"
    target.mkdir(exist_ok=True)
    return tempfile.TemporaryDirectory(prefix="triage-test-", dir=target)


def index_fixture(folder):
    root = Path(folder)
    providers = root / "engine/providers"
    providers.mkdir(parents=True)
    for path in (ROOT / "engine/providers").glob("*.py"):
        shutil.copyfile(path, providers / path.name)
    shutil.copyfile(ROOT / "engine/compatibility.json", root / "engine/compatibility.json")
    return root


def pack_entry(pid="FE01", **values):
    return dict({"provider": "razer", "profile": "hyperspeed-mouse-1f", "profileRev": 1,
                 "match": {"vendorId": "1532", "productId": pid},
                 "name": "Pack Mouse", "kind": "mouse", "source": "issue-fixture"}, **values)


def pack_document(entries=None, disabled=None):
    return {"format": "myfinder-device-pack", "loader": 1, "packVersion": 2026110401,
            "keyId": "test-key", "createdAt": "2026-11-04T00:00:00Z",
            "entries": entries or [], "disabled": disabled or []}


class WorkflowTests(unittest.TestCase):
    def test_indexes_have_read_only_checkout_and_both_revisions(self):
        text = (ROOT / ".github/workflows/issue-triage.yml").read_text(encoding="utf-8")
        indexing, writing = text.split("\n  triage:\n", 1)
        self.assertNotIn("issues: write", indexing)
        self.assertIn("contents: read", indexing)
        self.assertIn("persist-credentials: false", indexing)
        self.assertIn("ref: main", indexing)
        self.assertIn("fetch-depth: 0", indexing)
        self.assertIn("gh release view --json tagName", indexing)
        self.assertIn("git archive", indexing)
        self.assertIn('test -f "$RUNNER_TEMP/released/engine/known_ids.py"', indexing)
        self.assertIn('--engine-root "$RUNNER_TEMP/released/engine"', indexing)
        self.assertIn("needs: indexes", writing)
        self.assertIn("issues: write", writing)
        self.assertNotIn("uses: actions/checkout", writing)
        self.assertEqual(writing.count('--known-ids-next "$RUNNER_TEMP/triage/known-ids-next.json"'), 2)
        self.assertIn("triage:in-next-release) color=0052CC", writing)

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
