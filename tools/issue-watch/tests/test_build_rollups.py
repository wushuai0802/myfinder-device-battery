import importlib.util
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location("build_rollups", ROOT / "tools/issue-watch/build_rollups.py")
rollups = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(rollups)


class RollupTests(unittest.TestCase):
    def test_single_and_multi_actions_stay_distinct(self):
        rows = [{"编号": "1", "目标ID": "25A7:FA70", "标题": "one"}, {"编号": "2", "目标ID": "25A7:FA70;0001:0001", "标题": "two"}]
        text = rollups.render_actions("25A7:FA70", rows)
        self.assertIn("#1：评论", text)
        self.assertIn("#2：仅评论", text)

    def test_f99_is_excluded_and_infi75_is_separate(self):
        raw = {"1": {"body": "F99 MAX"}, "2": {"body": "other"}}
        rows = [{"编号": "1", "标题": "F99"}, {"编号": "2", "标题": "Other"}]
        text = rollups.render_rollup("05AC:024F", rows, raw, {})
        self.assertNotIn("#1 F99", text)
        self.assertIn("#2 Other", text)
        self.assertIn("## INFI75", text)

    def test_evidence_is_bounded_and_cites_issue(self):
        body = "\n".join(["请求 report 04"] * 30)
        entries = rollups.evidence("9", body, "25A7:FA70", [])
        self.assertLessEqual(len(entries), 20)
        self.assertTrue(all(item.startswith("#9:") for item in entries))


    def test_card_targets_and_titles(self):
        expected = ["05AC:024F", "320F:5055", "10D6:4801", "25A7:FA70", "1A2C:7FFF"]
        self.assertEqual(list(rollups.TARGETS), expected)
        for target in expected:
            self.assertEqual(rollups.render_rollup(target, [], {}, {}).splitlines()[0],
                             f"# [设备汇总] {rollups.TARGETS[target]}（{target}）")

    def test_non_devices_do_not_turn_a_keyboard_into_a_multi_device_issue(self):
        row = {"编号": "5", "标题": "键盘", "目标ID": "25A7:FA70;187C:0551;3837:1012;3837:6008"}
        self.assertEqual(rollups.device_ids(row), {"25A7:FA70"})
        self.assertIn("#5：评论", rollups.render_actions("25A7:FA70", [row]))

    def test_second_device_described_without_a_usb_id_keeps_original_open(self):
        row = {"编号": "692", "标题": "无线键盘识别不了", "目标ID": "05AC:024F"}
        issue = {"body": "腹灵 FL980 键盘无电量，鼠标的 DPI 也不正确。"}
        text = rollups.render_actions("05AC:024F", [row], {"692": issue})
        self.assertIn("#692：仅评论", text)
        self.assertNotIn("加 duplicate", text)

    def test_both_outputs_use_the_same_originals_and_target_product(self):
        target = "05AC:024F"
        rows = [{"编号": str(n), "标题": "设备" + str(n), "目标ID": target} for n in range(1, 8)]
        raw = {
            "1": {"body": '05AC:024F "F99 MAX Dongle" FF60:0061'},
            "2": {"body": '05AC:024F "INFI75-1.1" FF13:0001\n提到 F99 MAX 作为对照'},
            "3": {"body": '05AC:024F "Other" FF01:0001\n05AC:024F "F99 MAX Dongle" FF60:0061'},
            "4": {"state": "closed"},
            "5": {"pull_request": {}},
            "6": {"title": "[设备汇总] 已归档"},
            "7": {"labels": [{"name": "device-rollup"}]},
        }
        draft = rollups.render_rollup(target, rows, raw, {})
        actions = rollups.render_actions(target, rows, raw, {})
        for number in ("1", "4", "5", "6", "7"):
            self.assertNotIn("#" + number + " 设备", draft)
            self.assertNotIn("#" + number + "：", actions)
        self.assertIn("### INFI75-1.1", draft)
        self.assertIn("#2 设备2", draft)
        self.assertIn("#3 设备3", draft)
        self.assertIn("#2：评论", actions)
        self.assertIn("#3：仅评论", actions)

    def test_existing_reference_must_be_from_maintainer_and_match_this_rollup(self):
        target = "320F:5055"
        raw = {
            "953": {"state": "open", "title": "[设备汇总] 3837:1012", "body": target,
                    "labels": [{"name": "device-rollup"}]},
            "777": {"state": "open", "title": "[设备汇总] VGN（320F:5055）", "labels": [{"name": "device-rollup"}]},
            "888": {"state": "open", "title": "[设备汇总] 320F:5055", "author_association": "NONE"},
        }
        rows = [{"编号": str(n), "标题": "原帖", "目标ID": target} for n in range(1, 6)]
        pointer = {"author_association": "OWNER", "body": "其中 320F:5055 的进展请看 #777。"}
        comments = {
            "1": [{"author_association": "OWNER", "body": "其中 3837:1012 的进展请看 #953。"}],
            "2": [dict(pointer, author_association="NONE")],
            "3": [pointer, {"author_association": "OWNER", "body": "<!-- myfinder-supplement:v1 -->\n实测不符"}],
            "4": [pointer],
            "5": [dict(pointer, body="其中 320F:5055 的进展请看 #7771。")],
        }
        text = rollups.render_actions(target, rows, raw, comments)
        self.assertIn("#1：评论“已合并到 #777", text)
        self.assertIn("#2：评论", text)
        self.assertIn("#3：已有指向，后有用户回复", text)
        self.assertIn("#4：已有指向，跳过", text)
        self.assertIn("#5：评论", text)

    def test_source_evidence_and_closed_original_do_not_become_written_actions(self):
        target = "10D6:4801"
        rows = [{"编号": "1", "标题": "耳机及键盘", "目标ID": target + ";1234:ABCD"}]
        raw = {
            "1": {"body": '10D6:4801 "K02BS" FF90:0001'},
            "2": {"state": "closed", "body": '10D6:4801 "K03S" FF82:0001'},
            "954": {"state": "open", "title": "[设备汇总] 10D6:4801", "labels": [{"name": "device-rollup"}]},
        }
        comments = {
            "1": [{"user": {"type": "User"}, "body": "<!-- myfinder-triage:v1 -->\n10D6:4801 官方驱动 99%"},
                  {"author_association": "OWNER", "body": "10D6:4801 请告诉我们是否官方驱动显示 98%"},
                  {"author_association": "OWNER", "body": "<!-- myfinder-supplement:v1 -->\n10D6:4801 官方软件显示 82%\n其他键盘的官方软件显示 22%"}],
            "2": [{"author_association": "OWNER", "body": "已合并到 #954，后续进展会在那里更新。"}],
        }
        draft = rollups.render_rollup(target, rows, raw, comments)
        self.assertIn("82%", draft)
        self.assertIn("#2: 产品串 K03S", draft)
        for wrong in ["22%", "98%", "99%"]:
            self.assertNotIn(wrong, draft)
        self.assertNotIn("#2：", rollups.render_actions(target, rows, raw, comments))


if __name__ == "__main__":
    unittest.main()
