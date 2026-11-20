import importlib.util
import json
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location("snapshot", ROOT / "tools/issue-watch/snapshot.py")
snapshot = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(snapshot)
FIXTURES = Path(__file__).parent / "fixtures/snapshot"


class SnapshotTests(unittest.TestCase):
    def fixture(self, name):
        return json.loads((FIXTURES / name).read_text(encoding="utf-8"))

    def test_description_ids_take_priority_over_diagnostics(self):
        issue = self.fixture("description-priority.json")
        self.assertEqual(snapshot.target_ids(issue["body"]), [(0x1234, 0x5678)])
        self.assertEqual(snapshot.parse_body(issue["body"])[:2], ("1.3.10", "设备不识别"))

    def test_diagnostic_filter_and_groups(self):
        issue = self.fixture("diagnostic-filter.json")
        self.assertEqual(snapshot.target_ids(issue["body"]), [(0x1A2C, 0x7FFF)])
        released = {(0x1111, 0x0001)}
        main = released | {(0x2222, 0x0002)}
        self.assertEqual(snapshot.group_for([(0x1111, 0x0001)], released, main), "A")
        self.assertEqual(snapshot.group_for([(0x2222, 0x0002)], released, main), "B")
        self.assertEqual(snapshot.group_for([(0x3333, 0x0003)], released, main), "C")
        self.assertEqual(snapshot.group_for([], released, main), "D")

    def test_last_speaker_ignores_bots_and_keeps_supplements_as_user(self):
        issue = {"created_at": "2026-11-20T00:00:00Z"}
        comments = [
            {"created_at": "2026-11-20T00:01:00Z", "user": {"type": "Bot"}},
            {"created_at": "2026-11-20T00:02:00Z", "author_association": "OWNER",
             "body": "<!-- myfinder-supplement:v1 x -->", "user": {"type": "User"}},
        ]
        self.assertEqual(snapshot.last_speaker(issue, comments), ("用户", "2026-11-20T00:02:00Z"))

    def test_flattened_pages_and_system_device_rules(self):
        self.assertEqual(snapshot.flatten_pages([[{"number": 1}], [{"number": 2}]]), [{"number": 1}, {"number": 2}])
        self.assertTrue(snapshot.system_device("04F3", "HIDI2C Device"))
        self.assertFalse(snapshot.system_device("04F3", "Keyboard Receiver"))

    def test_no_id_ui_feedback_is_group_d(self):
        issue = self.fixture("no-id-ui.json")
        self.assertEqual(snapshot.target_ids(issue["body"]), [])
        self.assertEqual(snapshot.group_for(snapshot.target_ids(issue["body"]), set(), set()), "D")


if __name__ == "__main__":
    unittest.main()
