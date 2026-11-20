import importlib.util
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location("confirm_supported", ROOT / "tools/issue-watch/confirm_supported.py")
confirm_supported = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(confirm_supported)


class ConfirmSupportedTests(unittest.TestCase):
    def test_classify_keeps_model_gates_and_user_replies_out_of_notify(self):
        rows = [
            {"编号": "1", "分组": "A", "含型号门禁ID": "否", "最后发言": "维护者", "目标ID": "1234:5678", "标题": "A", "最后发言时间": "x"},
            {"编号": "2", "分组": "A", "含型号门禁ID": "是", "最后发言": "维护者", "目标ID": "1234:5679", "标题": "B", "最后发言时间": "x"},
            {"编号": "3", "分组": "A", "含型号门禁ID": "否", "最后发言": "用户", "目标ID": "1234:5680", "标题": "C", "最后发言时间": "x"},
            {"编号": "4", "分组": "B", "含型号门禁ID": "否", "最后发言": "维护者", "目标ID": "1234:5681", "标题": "D", "最后发言时间": "x"},
        ]
        notify, model_check, manual = confirm_supported.classify(rows)
        self.assertEqual([row["编号"] for row in notify], ["1"])
        self.assertEqual([row["编号"] for row in model_check], ["2"])
        self.assertEqual([row["编号"] for row in manual], ["3"])

    def test_table_uses_known_reader_names(self):
        text = confirm_supported.table([{"编号": "1", "标题": "x", "目标ID": "1234:5678", "最后发言时间": "now"}],
                                       {"1234:5678": "Reader"})
        self.assertIn("Reader", text)


if __name__ == "__main__":
    unittest.main()
