"""Offline ranking and optional count seeding; all issue data is synthetic."""
import copy
import datetime as dt
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


ranking = load("id_ranking", ROOT / "tools/issue-watch/id_ranking.py")


def issue(number, *devices, **extra):
    return {"number": number, "title": "PRIVATE_TITLE", "user": {"login": "PRIVATE_USER"},
            "body": '**描述**\n```\nPRIVATE_DESCRIPTION\n```\n未取得电量的设备\n'
                    + "\n".join(devices), **extra}


def row(device_id, issues, names=None, vendor=None):
    return {"id": device_id, "issues": issues,
            "names": ["Synthetic Mouse"] if names is None else names,
            "vendor": ["FF01:0001"] if vendor is None else vendor}


class RankTests(unittest.TestCase):
    def test_aggregation_deduplicates_sorts_and_caps_names(self):
        issues = [
            issue(12, '3000:0001 "Gamma" FF03:0003',
                  '1000:0001 "First" FF02:0002 FF01:0001',
                  '1000:0001 "First" FF02:0002'),
            issue(5, '2000:0001 "Beta" FF01:0001', '1000:0001 "Second" FF01:0001'),
            issue(8, '1000:0001 "Third" FF03:0003', '3000:0001 "Gamma" FF03:0003',
                  '2000:0001 "Beta" FF01:0001'),
            issue(2, '1000:0001 "Fourth" FF04:0004'),
            issue(12, '1000:0001 "First" FF02:0002'),
        ]
        original = copy.deepcopy(issues)
        rows = ranking.rank(issues, {})
        self.assertEqual([entry["id"] for entry in rows], ["1000:0001", "2000:0001", "3000:0001"])
        self.assertEqual(rows[0], row("1000:0001", [2, 5, 8, 12], ["First", "Second", "Third"],
                                      ["FF01:0001", "FF02:0002", "FF03:0003", "FF04:0004"]))
        self.assertEqual(rows[1]["issues"], [5, 8])
        self.assertEqual(rows[2]["issues"], [8, 12])
        self.assertEqual(issues, original)

    def test_all_four_priority_filters(self):
        for gated in (False, True):
            with self.subTest(requires_model_match=gated):
                known = {(0x1234, 0x0001): {"name": "Supported Mouse", "requiresModelMatch": gated}}
                rows = ranking.rank([issue(
                    1, '1234:0001 "Supported Mouse" FF01:0001',
                    '1234:0002 "No Private Collection" 0001:0002',
                    '8087:0003 "Internal Radio" FF01:0001',
                    '1234:0004 "USB Audio Device" FF01:0001',
                    '9999:0001 "Allowed Mouse" FF01:0001')], known)
                self.assertEqual([entry["id"] for entry in rows], ["9999:0001"])

    def test_pr_key_is_skipped_even_when_empty_and_empty_bodies_are_safe(self):
        devices = ('9999:0001 "Synthetic Mouse" FF01:0001',)
        rows = ranking.rank([issue(1, *devices), issue(2, *devices, pull_request={}),
                             issue(3, *devices, pull_request={"url": "synthetic"}),
                             {"number": 4, "body": None}, {"number": 5}], {})
        self.assertEqual(rows, [row("9999:0001", [1])])

    def test_working_cards_and_parser_noise_are_not_ranked(self):
        body = ('有卡片的设备\n1234:0001 "Working Mouse" FF01:0001 | 80%\n'
                '已隐藏的重复连接\n未取得电量的设备\n'
                '1234:0002 "Virtual Device" FF01:0001\n'
                '1234:0003 "Unknown Mouse" FF02:0002')
        self.assertEqual(ranking.rank([{"number": 7, "body": body}], {}),
                         [row("1234:0003", [7], ["Unknown Mouse"], ["FF02:0002"])])


class MarkdownTests(unittest.TestCase):
    def test_minimum_filters_table_and_keeps_metadata(self):
        rows = [row("1234:0001", [1, 2]), row("1234:0002", [3])]
        original = copy.deepcopy(rows)
        markdown = ranking.render_md(rows, "2026-11-11T00:00:00Z", 9, 2)
        self.assertIn("# 未支持设备 ID 需求排行", markdown)
        self.assertIn("生成时间（UTC）：2026-11-11T00:00:00Z", markdown)
        self.assertIn("统计的 open issue 数：9", markdown)
        self.assertIn("| 1 | 1234:0001 |", markdown)
        self.assertNotIn("1234:0002", markdown)
        self.assertEqual(rows, original)

    def test_issue_numbers_are_limited_to_twelve_with_total(self):
        for count in (12, 13, 15):
            with self.subTest(count=count):
                markdown = ranking.render_md(
                    [row("1234:0001", list(range(1, count + 1)))], "UTC", count, 1)
                self.assertIn("、".join(f"#{number}" for number in range(1, 13)), markdown)
                self.assertNotIn("#13", markdown)
                if count > 12:
                    self.assertIn(f"#12 等 {count} 条", markdown)
                else:
                    self.assertNotIn("等", markdown)

    def test_device_names_cannot_split_table_cells_or_lines(self):
        markdown = ranking.render_md(
            [row("1234:0001", [1], ["Mouse | Keyboard\r\nName"])], "UTC", 1, 1)
        self.assertIn("Mouse \\| Keyboard  Name", markdown)
        self.assertEqual(len(markdown.splitlines()), 9)


class MergeCountsTests(unittest.TestCase):
    def test_union_flags_names_and_unrelated_entries_without_mutating_inputs(self):
        counts = {
            "1000:0001": {"issues": [5, 1, 1], "flagged": False, "name": "Existing Name"},
            "1000:0002": {"issues": [9], "flagged": True, "name": "Already Flagged"},
            "1000:0003": {"issues": [42], "flagged": False, "name": "Unrelated"},
            "1000:0004": {"issues": [4], "flagged": False, "name": ""},
        }
        rows = [row("1000:0001", [5, 3], ["New Alias"]), row("1000:0002", [9]),
                row("1000:0004", [6, 4], ["Filled Name"]), row("2000:0001", [8, 7, 6])]
        original_counts, original_rows = copy.deepcopy(counts), copy.deepcopy(rows)
        merged = ranking.merge_counts(counts, rows, 3)
        self.assertEqual(merged["1000:0001"],
                         {"issues": [1, 3, 5], "flagged": True, "name": "Existing Name"})
        self.assertEqual(merged["1000:0002"], counts["1000:0002"])
        self.assertEqual(merged["1000:0003"], counts["1000:0003"])
        self.assertEqual(merged["1000:0004"],
                         {"issues": [4, 6], "flagged": False, "name": "Filled Name"})
        self.assertEqual(merged["2000:0001"],
                         {"issues": [6, 7, 8], "flagged": True, "name": "Synthetic Mouse"})
        self.assertEqual(counts, original_counts)
        self.assertEqual(rows, original_rows)

    def test_invalid_count_structures_start_empty(self):
        rows = [row("1234:0001", [1])]
        expected = {"1234:0001": {"issues": [1], "flagged": False, "name": "Synthetic Mouse"}}
        for counts in (None, [], "broken", {"invalid": None}, {"invalid": {}},
                       {"invalid": {"issues": ["bad"], "flagged": False, "name": "Bad"}},
                       {"invalid": {"issues": [1], "flagged": "bad", "name": "Bad"}}):
            with self.subTest(counts=counts):
                self.assertEqual(ranking.merge_counts(counts, rows, 3), expected)

    def test_threshold_uses_unique_issues_and_preserves_flags_without_new_rows(self):
        counts = {"1234:0001": {"issues": [2, 1, 1], "flagged": False, "name": "Old"},
                  "1234:0002": {"issues": [4, 3, 2], "flagged": False, "name": "Hot"},
                  "1234:0003": {"issues": [], "flagged": True, "name": "Flagged"}}
        merged = ranking.merge_counts(counts, [], 3)
        self.assertEqual(merged["1234:0001"]["issues"], [1, 2])
        self.assertFalse(merged["1234:0001"]["flagged"])
        self.assertTrue(merged["1234:0002"]["flagged"])
        self.assertTrue(merged["1234:0003"]["flagged"])


class MainTests(unittest.TestCase):
    def invoke(self, folder, issues, *options, repo=None):
        args = ["--dir", str(folder), *options]
        if repo is not None:
            args.extend(["--repo", repo])
        with patch.object(ranking, "gh", return_value=issues) as gh, \
                patch.object(ranking, "known_ids", return_value={}) as known, \
                patch("subprocess.run", side_effect=AssertionError("Unexpected process in offline test")), \
                patch("builtins.print") as stdout:
            ranking.main(args)
        gh.assert_called_once_with(
            f"repos/{repo or ranking.REPO}/issues?state=open&per_page=100", paginate=True)
        known.assert_called_once_with()
        return stdout

    def test_minimum_only_changes_markdown_and_outputs_exclude_private_fields(self):
        issues = [issue(1, '1234:0001 "Mouse" FF01:0001'),
                  issue(2, '1234:0001 "Mouse" FF01:0001'),
                  issue(3, '1234:0002 "Keyboard" FF02:0002'),
                  issue(4, '1234:0003 "Pull Request" FF03:0003', pull_request={}),
                  issue(5)]
        with tempfile.TemporaryDirectory() as folder:
            stdout = self.invoke(folder, issues)
            json_path = Path(folder) / "id-ranking.json"
            markdown_path = Path(folder) / "id-ranking.md"
            document = json.loads(json_path.read_text(encoding="utf-8"))
            markdown = markdown_path.read_text(encoding="utf-8")
            self.assertEqual(document["totalIssues"], 4)
            self.assertEqual([entry["id"] for entry in document["rows"]], ["1234:0001", "1234:0002"])
            self.assertEqual(document["rows"][0]["issues"], [1, 2])
            self.assertIn("1234:0001", markdown)
            self.assertNotIn("1234:0002", markdown)
            self.assertNotIn("1234:0003", markdown)
            self.assertIn("统计的 open issue 数：4", markdown)
            self.assertTrue(document["generatedAt"].endswith("Z"))
            generated_at = dt.datetime.fromisoformat(document["generatedAt"].replace("Z", "+00:00"))
            self.assertEqual(generated_at.utcoffset(), dt.timedelta(0))
            self.assertIn(document["generatedAt"], markdown)
            stdout.assert_called_once_with(f"ranking: 4 issues, 2 ids, 1 listed -> {folder}")
            for private in ("PRIVATE_TITLE", "PRIVATE_USER", "PRIVATE_DESCRIPTION"):
                self.assertNotIn(private, markdown + json_path.read_text(encoding="utf-8"))

            self.invoke(folder, issues, "--min", "3")
            self.assertNotIn("1234:0001", markdown_path.read_text(encoding="utf-8"))
            updated = json.loads(json_path.read_text(encoding="utf-8"))
            self.assertEqual(updated["rows"], document["rows"])

    def assert_counts_untouched(self, initial):
        with tempfile.TemporaryDirectory() as folder:
            counts_path = Path(folder) / "id-counts.json"
            if initial is not None:
                counts_path.write_bytes(initial)
            original_open = Path.open

            def guarded_open(path, *args, **kwargs):
                self.assertNotEqual(path, counts_path, "Counts must not be read or written without --seed-counts")
                return original_open(path, *args, **kwargs)

            with patch.object(Path, "open", guarded_open):
                self.invoke(folder, [issue(1, '1234:0001 "Mouse" FF01:0001')])
            if initial is None:
                self.assertFalse(counts_path.exists())
            else:
                self.assertEqual(counts_path.read_bytes(), initial)
            self.assertEqual({path.name for path in Path(folder).iterdir()},
                             {"id-ranking.md", "id-ranking.json"}
                             | ({"id-counts.json"} if initial is not None else set()))

    def test_without_seed_counts_missing_file_is_not_accessed_or_created(self):
        self.assert_counts_untouched(None)

    def test_without_seed_counts_existing_file_is_not_accessed_or_modified(self):
        self.assert_counts_untouched(b'{"preserve": "exact bytes"}\n')

    def test_seed_counts_missing_or_corrupt_file_starts_empty(self):
        issues = [issue(number, '1234:0001 "Mouse" FF01:0001')
                  for number in range(1, ranking.HOT_THRESHOLD + 1)]
        for content in (None, b"not json", b"\xff", b"[]", b'{"invalid": {}}'):
            with self.subTest(content=content), tempfile.TemporaryDirectory() as folder:
                counts_path = Path(folder) / "id-counts.json"
                if content is not None:
                    counts_path.write_bytes(content)
                self.invoke(folder, issues, "--seed-counts", "--min", "999")
                self.assertEqual(json.loads(counts_path.read_text(encoding="utf-8")),
                                 {"1234:0001": {"issues": list(range(1, ranking.HOT_THRESHOLD + 1)),
                                                "flagged": True, "name": "Mouse"}})
                self.assertNotIn("1234:0001", (Path(folder) / "id-ranking.md").read_text(encoding="utf-8"))

    def test_seed_counts_merges_existing_history_and_keeps_flagged(self):
        counts = {"1234:0001": {"issues": [20, 10], "flagged": False, "name": "Historical Name"},
                  "1234:0002": {"issues": [40], "flagged": True, "name": "Already Flagged"}}
        with tempfile.TemporaryDirectory() as folder:
            counts_path = Path(folder) / "id-counts.json"
            counts_path.write_text(json.dumps(counts), encoding="utf-8")
            self.invoke(folder, [issue(20, '1234:0001 "New Alias" FF01:0001'),
                                 issue(30, '1234:0001 "New Alias" FF01:0001'),
                                 issue(40, '1234:0002 "Mouse" FF01:0001')], "--seed-counts")
            self.assertEqual(json.loads(counts_path.read_text(encoding="utf-8")), {
                "1234:0001": {"issues": [10, 20, 30], "flagged": True, "name": "Historical Name"},
                "1234:0002": counts["1234:0002"],
            })

    def test_empty_ranking_and_custom_repo_use_one_get(self):
        with tempfile.TemporaryDirectory() as folder:
            stdout = self.invoke(folder, [issue(1, pull_request={})], repo="synthetic/repository")
            self.assertEqual(json.loads((Path(folder) / "id-ranking.json").read_text(encoding="utf-8"))["rows"], [])
            stdout.assert_called_once_with(f"ranking: 0 issues, 0 ids, 0 listed -> {folder}")


if __name__ == "__main__":
    unittest.main()
