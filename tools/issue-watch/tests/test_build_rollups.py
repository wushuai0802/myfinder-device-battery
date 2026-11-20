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


if __name__ == "__main__":
    unittest.main()
