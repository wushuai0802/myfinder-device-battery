import csv
from contextlib import redirect_stdout
from io import StringIO
import json
from pathlib import Path
import runpy
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[3]
script = runpy.run_path(str(ROOT / "tools/issue-watch/old_version_notice.py"))


def row(number, version="1.3.9", group="C", title="测试设备"):
    return {"编号": number, "版本": version, "分组": group, "标题": title, "目标ID": "1234:5678",
            "最后发言": "用户"}


def comment(number, timestamp, identity, association="NONE", kind="User", body=""):
    return {
        "issue_url": f"https://api.github.com/repos/example/project/issues/{number}",
        "id": identity, "created_at": timestamp, "updated_at": "2026-11-22T00:00:00Z",
        "author_association": association, "user": {"type": kind}, "body": body,
    }


def rollup(number, body, state="open"):
    return {"number": number, "labels": [{"name": "device-rollup"}], "body": body, "state": state}


class OldVersionNoticeTests(unittest.TestCase):
    def prepare_snapshot(self, path, rows, issues, comments):
        fields = ("编号", "版本", "分组", "标题", "目标ID", "最后发言")
        with (path / "issues.csv").open("w", encoding="utf-8-sig", newline="") as stream:
            writer = csv.DictWriter(stream, fieldnames=fields)
            writer.writeheader()
            writer.writerows(rows)
        (path / "issues-raw.json").write_text(json.dumps(issues, ensure_ascii=False), encoding="utf-8")
        (path / "comments-raw.json").write_text(json.dumps(comments, ensure_ascii=False), encoding="utf-8")

    def test_version_uses_stable_cutoff(self):
        check = script["older_than_supported"]
        for version in ("1.3.5", "v1.3.9", "1.3.9+windows", "1.3.10-beta.2", "1.3.10-rc.1"):
            with self.subTest(version=version):
                self.assertTrue(check(version))
        for version in ("1.3.10", "1.3.10+package", "1.3.11-beta.1", "2.0.0",
                        "1.3.10-beta.01", "", "not-a-version"):
            with self.subTest(version=version):
                self.assertFalse(check(version))

    def test_rollup_only_uses_member_list_and_keeps_subheadings(self):
        body = ("## 设备\r\n- #201 参考\r\n## 涉及的反馈（列表)\r\n"
                "### 子型号\r\n- #12 成员\r\n2. #13 成员\r\n"
                "## 已收集的资料\r\n- #14 证据\r\n")
        ordinary = {"number": 999, "body": body, "labels": []}
        self.assertEqual(script["rollup_members"]([rollup(952, body, "closed"), ordinary]), {12, 13, 952})

    def test_unrecognized_rollup_is_reported(self):
        with self.assertRaisesRegex(ValueError, "#952"):
            script["rollup_members"]([rollup(952, "## 设备\n- #12")])

    def test_comment_order_uses_creation_and_id(self):
        early = comment(7, "2026-11-20T00:00:00Z", 100, association="OWNER")
        late = comment(7, "2026-11-20T00:00:01Z", 200, kind="Bot")
        same = comment(7, "2026-11-20T00:00:01Z", 199)
        self.assertEqual(script["latest_comments"]([late, early, same]), {7: late})

    def test_selects_only_old_c_rows_and_routes_last_user_to_manual(self):
        rows = [row(8), row(4, "1.3.10"), row(5, group="A"), row(1), row(2), row(3), row(6), row(7)]
        issues = [rollup(999, "## 涉及的反馈\n- #7 汇总\n\n## 资料\n- #8 仅引用")]
        datetime = "2026-11-20T01:00:00Z"
        comments = [
            comment(1, datetime, 1, association="OWNER"),
            comment(2, datetime, 2, kind="Bot"),
            comment(3, datetime, 3, association="OWNER", body="<!-- myfinder-supplement:v1 -->"),
            comment(7, datetime, 7, association="OWNER"),
            comment(8, datetime, 8, association="OWNER"),
        ]
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp)
            self.prepare_snapshot(path, rows, issues, comments)
            notices, manual = script["build_plan"](path)
        self.assertEqual([entry[0] for entry in notices], [1, 2, 8])
        self.assertEqual([entry[0] for entry in manual], [3, 6])

    def test_cli_creates_one_report_with_template_and_escaped_titles(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp)
            self.prepare_snapshot(path, [row(7, title="键盘 | <型号>\nUSB")], [], [])
            with redirect_stdout(StringIO()) as output:
                script["main"](["--snapshot-dir", str(path)])
            out = path / "old-version"
            self.assertEqual([item.name for item in out.iterdir()], ["notices.md"])
            text = (out / "notices.md").read_text(encoding="utf-8")
        self.assertIn("候选 0 条，转人工 1 条", output.getvalue())
        self.assertIn(r"| #7 | 键盘 \| &lt;型号&gt; USB | 1.3.9 | 1234:5678 |", text)
        self.assertIn("## 转人工", text)
        self.assertIn("> " + script["TEMPLATE"], text)


if __name__ == "__main__":
    unittest.main()
