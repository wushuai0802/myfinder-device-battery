"""Fixtures for the experimental-family confirmation filter."""

from importlib.util import module_from_spec, spec_from_file_location
from pathlib import Path
import unittest


LOCATION = Path(__file__).resolve().parents[1] / "probe_harvest.py"
SPEC = spec_from_file_location("probe_harvest", LOCATION)
PROBE = module_from_spec(SPEC)
SPEC.loader.exec_module(PROBE)

AGREEMENT = "MyFinder 显示的电量和官方软件一致吗？"
ROW = "自动识别（实验） | 093a:522c | Incott 8K wireless mouse | incott_8k_passive | battery-listen | 读数"


def issue(number=5, answer="一致", rows=ROW):
    return {"number": number, "body": f"{AGREEMENT}{answer}\n\n### 反馈报告\n\n```text\n{rows}\n```"}


class ProbeHarvestTests(unittest.TestCase):
    def test_qualifying_raw_and_paginated_exports(self):
        expected = [{
            "issue": 5, "vidPid": "093A:522C", "productString": "Incott 8K wireless mouse",
            "family": "incott_8k_passive",
        }]
        self.assertEqual(PROBE.harvest([issue()]), expected)
        self.assertEqual(PROBE.harvest([[issue()], [issue()]]), expected)

    def test_other_answers_and_quoted_confirmation_are_ignored(self):
        for answer in ("不一致", "官方读不到", "相差 5%", "", "一致 / 相差 X% / 官方读不到"):
            with self.subTest(answer=answer):
                self.assertEqual(PROBE.harvest([issue(answer=answer)]), [])
        quoted = {"number": 7, "body": f"```text\n{AGREEMENT}一致\n{ROW}\n```"}
        self.assertEqual(PROBE.harvest([quoted]), [])

    def test_wrong_result_mixed_devices_and_unusable_identities(self):
        for rows in (
            ROW.replace("读数", "已停用"),
            ROW.replace("自动识别（实验）", "内置读取器"),
            ROW.replace("Incott 8K wireless mouse", "[redacted]"),
            ROW.replace("093a:522c", "FF05:0001") + "\n" + ROW,
            ROW + "\n" + ROW.replace("读数", "无响应"),
        ):
            with self.subTest(rows=rows):
                self.assertEqual(PROBE.harvest([issue(rows=rows)]), [])
        self.assertEqual(PROBE.harvest([dict(issue(), pull_request={}), issue(number="7")]), [])

    def test_conflicting_answers_in_one_report_are_ignored(self):
        issue_with_conflict = issue()
        issue_with_conflict["body"] += f"\n{AGREEMENT}官方读不到"
        self.assertEqual(PROBE.harvest([issue_with_conflict]), [])

    def test_conflicting_snapshots_of_the_same_issue_are_ignored(self):
        another = issue(rows=ROW.replace("093a:522c", "36b0:3002"))
        self.assertEqual(PROBE.harvest([[issue()], [another]]), [])
        self.assertEqual(PROBE.harvest([[issue()], [issue(answer="相差 5%")]]), [])


if __name__ == "__main__":
    unittest.main()
