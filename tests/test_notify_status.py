"""Synthetic notification plans and mocked gh transports; never access GitHub."""
from copy import deepcopy
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import call, patch


ROOT = Path(__file__).resolve().parents[1]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


notify = load("notify_status", ROOT / "tools/issue-watch/notify_status.py")
ID, VERSION = "ABCD:1234", "1.2.3-beta.4"
STATUS = {"schema": 1, "devices": {ID: {"status": "beta", "version": VERSION, "note": "仅鼠标"}}}
REPORT = '未取得电量的设备\nabcd:1234 "untrusted @user $(command)" FF00:0001\n读取器健康\n无记录'
MARKER = f"<!-- myfinder-status:v1 {ID} {VERSION} -->"


def issue(number=1, body=REPORT, **values):
    return dict({"number": number, "body": body, "state": "open", "labels": []}, **values)


def comment(body, user=None):
    return {"body": body, "user": {"type": "User", "login": "tester"} if user is None else user}


class NotifyStatusTests(unittest.TestCase):
    def plan(self, issues, comments=None, limit=30):
        return notify.select_issues(issues, comments or {}, ID, VERSION, limit)

    def run_main(self, argv=(), issues=None, comments=None, post_effect=None, status=STATUS):
        issues = [issue()] if issues is None else issues
        comments = {} if comments is None else comments
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "status.json"
            path.write_text(json.dumps(status), encoding="utf-8")

            def get(endpoint):
                if "/comments?" in endpoint:
                    number = int(endpoint.split("/issues/", 1)[1].split("/", 1)[0])
                    return comments.get(number, [])
                self.assertTrue(endpoint.endswith("/issues?state=open&per_page=100"))
                return issues

            with patch.object(notify, "gh_get", side_effect=get) as reads, \
                    patch.object(notify, "post_comment", side_effect=post_effect) as writes, \
                    patch.object(notify.subprocess, "run", side_effect=AssertionError("Unexpected gh process")), \
                    patch("builtins.print") as printed:
                result = notify.main(["--id", ID, "--status", str(path)] + list(argv))
            return result, reads, writes, printed

    def test_fixed_body_and_default_repo_match_issue_digest(self):
        self.assertEqual(notify.REPO, notify._digest.REPO)
        expected = (MARKER + "\n自动通知：测试版 1.2.3-beta.4 已加入 ABCD:1234 的电量读取，待实机验证（仅鼠标）。"
                    "可在程序设置里打开「提醒测试版更新」后升级，或到发布页下载："
                    f"https://github.com/{notify.REPO}/releases/tag/v{VERSION} 。"
                    "读到后请对照官方软件的电量；读不到请在程序里用「我的反馈 → 补充资料」再提交一次诊断。")
        self.assertEqual(notify.make_body("abcd:1234", STATUS), expected)
        self.assertNotIn("@user", expected)
        self.assertNotIn("$(command)", expected)

    def test_optional_note_is_omitted_without_empty_parentheses(self):
        status = deepcopy(STATUS)
        del status["devices"][ID]["note"]
        body = notify.make_body(ID, status, "example/project")
        self.assertIn("待实机验证。可在程序设置", body)
        self.assertIn("https://github.com/example/project/releases/tag/v", body)
        self.assertNotIn("（）", body)

    def test_body_and_plan_are_pure(self):
        issues, comments, status = [issue()], {1: [comment("ordinary text")]}, deepcopy(STATUS)
        before = deepcopy((issues, comments, status))
        notify.make_body(ID, status)
        self.assertEqual(self.plan(issues, comments)["notify"], [1])
        self.assertEqual((issues, comments, status), before)

    def test_matches_body_and_human_comment_reports_including_pages(self):
        actual = self.plan([issue(1), issue(2, "No diagnostics"), issue(3, "No diagnostics")],
                           {2: [[comment("ordinary text")], [comment(REPORT)]], 3: [comment(REPORT)]})
        self.assertEqual(actual, {"notify": [1, 2, 3], "skipped": []})

    def test_cards_descriptions_names_collections_and_title_do_not_match(self):
        bodies = ["Description ABCD:1234", 'ABCD:1234 "loose inventory" FF00:0001',
                  '有卡片的设备\nReceiver | mouse | test | 80% | 当前读数：是 | ABCD:1234',
                  '未取得电量的设备\n9999:0001 "pretend ABCD:1234" FF00:0001',
                  '未取得电量的设备\n9999:0001 "Receiver" ABCD:1234',
                  '已隐藏的重复连接\nABCD:1234 "Hidden" FF00:0001',
                  '读取器健康\nABCD:1234 "error" FF00:0001']
        issues = [issue(number, body, title=REPORT) for number, body in enumerate(bodies, 1)]
        actual = self.plan(issues)
        self.assertEqual(actual["notify"], [])
        self.assertEqual(len(actual["skipped"]), len(issues))
        self.assertTrue(all("未在" in item["reason"] for item in actual["skipped"]))

    def test_bot_comments_never_supply_device_evidence(self):
        for user in ({"type": "Bot", "login": "automation"}, {"type": "User", "login": "other[bot]"}):
            with self.subTest(user=user):
                self.assertEqual(self.plan([issue(body="No diagnostics")], {1: [comment(REPORT, user)]})["notify"], [])

    def test_all_four_skip_reasons(self):
        actual = self.plan([issue(1, pull_request={}), issue(2, labels=[{"name": "triage:locked"}]), issue(3), issue(4)],
                           {3: [comment(MARKER, {"type": "Bot", "login": "automation[bot]"})],
                            4: [comment(f"Already notified: https://github.com/example/project/releases/tag/v{VERSION}")]})
        self.assertEqual(actual["notify"], [])
        self.assertEqual(actual["skipped"], [
            {"number": 1, "reason": "Pull Request"}, {"number": 2, "reason": "triage:locked"},
            {"number": 3, "reason": "已有本版本通知标记"}, {"number": 4, "reason": "已有本版本发布地址"}])

    def test_deduplication_is_version_specific_and_only_checks_comments(self):
        comments = {1: [comment(MARKER.replace(VERSION, "1.2.3-beta.3"))],
                    2: [comment(MARKER.replace(ID, "FFFF:FFFF"))],
                    3: [comment(f"/releases/tag/v{VERSION}0")]}
        issues = [issue(1), issue(2), issue(3), issue(4, REPORT + "\n" + MARKER)]
        self.assertEqual(self.plan(issues, comments)["notify"], [1, 2, 3, 4])

    def test_limit_and_duplicates_count_each_issue_once(self):
        actual = self.plan([issue(1), issue(1), issue(2), issue(3)], limit=2)
        self.assertEqual(actual, {"notify": [1, 2], "skipped": [{"number": 3, "reason": "超过 --limit 2"}]})
        self.assertEqual(self.plan([issue()], limit=0)["notify"], [])
        with self.assertRaises(ValueError):
            self.plan([issue()], limit=-1)

    def test_default_limit_is_thirty(self):
        actual = self.plan([issue(number) for number in range(1, 33)])
        self.assertEqual(actual["notify"], list(range(1, 31)))
        self.assertEqual([item["number"] for item in actual["skipped"]], [31, 32])

    def test_closed_issue_is_not_notified(self):
        self.assertEqual(self.plan([issue(state="closed")])["notify"], [])

    def test_preview_prints_body_once_and_never_writes(self):
        result, reads, writes, printed = self.run_main(issues=[issue(1), issue(2), issue(3)],
                                                      comments={2: [comment(MARKER)]})
        self.assertEqual(result, 0)
        writes.assert_not_called()
        self.assertEqual(reads.call_count, 4)
        texts = [str(entry.args[0]) for entry in printed.call_args_list]
        self.assertEqual(texts.count(notify.make_body(ID, STATUS)), 1)
        self.assertIn("预览：将通知 #1", texts)
        self.assertIn("预览：将通知 #3", texts)
        self.assertIn("跳过 #2：已有本版本通知标记", texts)

    def test_preview_uses_only_explicit_get_requests_at_transport_boundary(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "status.json"
            path.write_text(json.dumps(STATUS), encoding="utf-8")

            def run(command, **kwargs):
                self.assertEqual(command[:5], ["gh", "api", "--method", "GET", "--paginate"])
                self.assertIn("--slurp", command)
                data = [[issue()]] if "/comments?" not in command[-1] else [[]]
                return subprocess.CompletedProcess(command, 0, stdout=json.dumps(data))

            with patch.object(notify.subprocess, "run", side_effect=run) as transport, patch("builtins.print"):
                self.assertEqual(notify.main(["--id", ID, "--status", str(path)]), 0)
            self.assertEqual(transport.call_count, 2)

    def test_post_sends_each_eligible_issue_once_and_obeys_limit(self):
        result, reads, writes, printed = self.run_main(["--post", "--limit", "2", "--repo", "example/project"],
                                                      issues=[issue(1), issue(1), issue(2), issue(3), issue(4)],
                                                      comments={2: [comment(MARKER)]})
        self.assertEqual(result, 0)
        body = notify.make_body(ID, STATUS, "example/project")
        self.assertEqual(writes.call_args_list, [call("example/project", 1, body), call("example/project", 3, body)])
        self.assertIn(call("跳过 #4：超过 --limit 2"), printed.call_args_list)
        self.assertEqual(reads.call_count, 5)

    def test_post_failure_stops_immediately_with_nonzero_exit(self):
        failure = subprocess.CalledProcessError(1, ["gh", "issue", "comment"])
        for error in (failure, OSError("cannot start gh")):
            with self.subTest(error=error):
                result, _, writes, printed = self.run_main(["--post"], issues=[issue(1), issue(2), issue(3)],
                                                           post_effect=[None, error])
                self.assertNotEqual(result, 0)
                self.assertEqual([entry.args[1] for entry in writes.call_args_list], [1, 2])
                self.assertTrue(any("#2 失败，已停止" in str(entry.args[0]) for entry in printed.call_args_list))

    def test_invalid_or_non_beta_ids_are_refused_before_gh(self):
        for status in ({"schema": 1, "devices": {}}, {"schema": 1, "devices": {ID: {"status": "researching"}}},
                       {"schema": 1, "devices": {ID: {"status": "no-evidence"}}},
                       {"schema": 1, "devices": {ID: {"status": "not-a-device"}}}):
            result, reads, writes, _ = self.run_main(["--post"], status=status)
            self.assertNotEqual(result, 0)
            reads.assert_not_called()
            writes.assert_not_called()
        for options in (["--id", "bad"], ["--limit", "-1"], ["--repo", "@bad/project"]):
            result, reads, writes, _ = self.run_main(options)
            self.assertNotEqual(result, 0)
            reads.assert_not_called()
            writes.assert_not_called()

    def test_unsafe_note_or_version_cannot_reach_body(self):
        for entry in ({"status": "beta", "version": "1.2.3-beta.4\n@user"},
                      {"status": "beta", "version": VERSION, "note": "@user"},
                      {"status": "beta", "version": VERSION, "note": "https://example.invalid"}):
            with self.subTest(entry=entry), self.assertRaises(ValueError):
                notify.make_body(ID, {"schema": 1, "devices": {ID: entry}})

    def test_fetch_failure_never_writes(self):
        with patch.object(notify._status, "load", return_value=STATUS), \
                patch.object(notify, "gh_get", side_effect=subprocess.CalledProcessError(1, ["gh", "api"])), \
                patch.object(notify, "post_comment") as writes, patch("builtins.print"):
            self.assertEqual(notify.main(["--id", ID, "--post"]), 1)
        writes.assert_not_called()

    def test_post_transport_uses_utf8_body_file_and_removes_it(self):
        paths = []
        body = notify.make_body(ID, STATUS)

        def run(command, **kwargs):
            self.assertEqual(command[:7], ["gh", "issue", "comment", "7", "-R", notify.REPO, "--body-file"])
            path = Path(command[-1])
            paths.append(path)
            self.assertEqual(path.read_bytes(), (body + "\n").encode("utf-8"))
            self.assertTrue(kwargs["check"])
            return subprocess.CompletedProcess(command, 0, stdout="")

        with patch.object(notify.subprocess, "run", side_effect=run):
            notify.post_comment(notify.REPO, 7, body)
        self.assertEqual(len(paths), 1)
        self.assertFalse(paths[0].exists())


if __name__ == "__main__":
    unittest.main()
