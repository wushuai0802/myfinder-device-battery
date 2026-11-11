"""Offline status validation, ranking updates and file-format regressions."""
from copy import deepcopy
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


device_status = load("device_status", ROOT / "tools/issue-watch/device_status.py")
BASE = {"schema": 1, "devices": {
    "ABCD:1234": {"status": "beta", "version": "1.2.3-beta.4", "note": "仅鼠标"},
    "1234:ABCD": {"status": "researching", "reports": 9, "rank": 2},
}}


class DeviceStatusTests(unittest.TestCase):
    def read_document(self, document):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "status.json"
            path.write_text(json.dumps(document, ensure_ascii=False), encoding="utf-8")
            return device_status.load(path)

    def test_repository_initial_table_is_valid_and_has_no_counts(self):
        actual = device_status.load(ROOT / ".github/triage/device-status.json")
        self.assertEqual(actual, {"schema": 1, "devices": {
            "0B05:1ACE": {"status": "beta", "version": "1.3.11-beta.2",
                          "note": "仅鼠标；经 OMNI 接收器连接的键盘暂不支持"},
            "3837:1012": {"status": "not-a-device", "note": "充电底座，本身没有电量"},
            "248A:5B2F": {"status": "no-evidence"}, "3837:6011": {"status": "no-evidence"},
            "3554:F503": {"status": "no-evidence"}}})

    def test_valid_statuses_and_optional_fields(self):
        for status in ("beta", "researching", "no-evidence", "not-a-device"):
            entry = {"status": status, "note": "字" * 60, "reports": 0, "rank": 1}
            if status == "beta":
                entry["version"] = "1.2.3-beta.4"
            value = {"schema": 1, "devices": {"ABCD:1234": entry}}
            with self.subTest(status=status):
                self.assertEqual(self.read_document(value), value)
        self.assertEqual(self.read_document({"schema": 1, "devices": {}})["devices"], {})

    def test_invalid_document_schema_and_devices(self):
        for value, field in (([], "document"), ({}, "schema"),
                             ({"schema": True, "devices": {}}, "schema"),
                             ({"schema": 1.0, "devices": {}}, "schema"),
                             ({"schema": 2, "devices": {}}, "schema"),
                             ({"schema": 1}, "devices"),
                             ({"schema": 1, "devices": []}, "devices"),
                             (dict(BASE, unexpected=1), "unexpected")):
            with self.subTest(value=value), self.assertRaisesRegex(ValueError, field):
                self.read_document(value)

    def test_invalid_ids(self):
        for device_id in ("abcd:1234", "ABCD:123", "0xABCD:1234", "ABCD:ZZZZ", "ABCD:1234\n", "@ABCD:1234"):
            with self.subTest(device_id=device_id), self.assertRaises(ValueError) as raised:
                self.read_document({"schema": 1, "devices": {device_id: {"status": "researching"}}})
            self.assertIn(device_id, str(raised.exception))
            self.assertIn(".id", str(raised.exception))

    def test_invalid_entries_and_status_values(self):
        for entry, field in ((None, "entry"), ([], "entry"), ({}, "status"),
                             ({"status": []}, "status"), ({"status": "supported"}, "status"),
                             ({"status": "beta", "unknown": 1}, "unknown")):
            with self.subTest(entry=entry), self.assertRaisesRegex(ValueError, "ABCD:1234." + field):
                self.read_document({"schema": 1, "devices": {"ABCD:1234": entry}})

    def test_beta_requires_a_valid_beta_version(self):
        for version in (None, 1, "1.2.3", "v1.2.3-beta.4", "1.2.3-beta", "1.2.3-beta.4\n", "@user"):
            entry = {"status": "beta"}
            if version is not None:
                entry["version"] = version
            with self.subTest(version=version), self.assertRaisesRegex(ValueError, "ABCD:1234.version"):
                self.read_document({"schema": 1, "devices": {"ABCD:1234": entry}})
        with self.assertRaisesRegex(ValueError, "ABCD:1234.version"):
            self.read_document({"schema": 1, "devices": {"ABCD:1234": {"status": "researching", "version": "bad"}}})

    def test_unsafe_and_oversize_notes(self):
        for note in (None, 1, "字" * 61, "@someone", "a\nb", "a\rb", "`code`", "<tag", "tag>",
                     "http", "HTTPS://example.invalid", "a\u2028b", "a\x85b", "a\vb"):
            with self.subTest(note=note), self.assertRaisesRegex(ValueError, "ABCD:1234.note"):
                self.read_document({"schema": 1, "devices": {"ABCD:1234": {"status": "no-evidence", "note": note}}})

    def test_counts_require_non_boolean_integers_and_bounds(self):
        for field, values in (("reports", (-1, True, False, 1.0, "3", None)),
                              ("rank", (0, -1, True, 1.0, "1", None))):
            for value in values:
                with self.subTest(field=field, value=value), self.assertRaisesRegex(ValueError, "ABCD:1234." + field):
                    self.read_document({"schema": 1, "devices": {"ABCD:1234": {"status": "researching", field: value}}})

    def test_missing_malformed_and_duplicate_documents_fail_as_value_error(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "status.json"
            with self.assertRaises(ValueError):
                device_status.load(path)
            for data in (b"{bad", b"\xff", b'{"schema":1,"schema":1,"devices":{}}',
                         b'{"schema":1,"devices":{"ABCD:1234":{},"ABCD:1234":{}}}'):
                path.write_bytes(data)
                with self.subTest(data=data), self.assertRaises(ValueError):
                    device_status.load(path)

    def test_update_counts_preserves_input_membership_and_other_fields(self):
        original = deepcopy(BASE)
        rows = [{"id": "FFFF:FFFF", "issues": [1, 2, 3]},
                {"id": "ABCD:1234", "issues": [4, 5]}]
        before_rows = deepcopy(rows)
        actual = device_status.update_counts(original, rows)
        self.assertEqual(original, BASE)
        self.assertEqual(rows, before_rows)
        self.assertEqual(set(actual["devices"]), set(BASE["devices"]))
        self.assertEqual(actual["devices"]["ABCD:1234"], dict(BASE["devices"]["ABCD:1234"], reports=2, rank=2))
        self.assertEqual(actual["devices"]["1234:ABCD"], {"status": "researching", "reports": 0})
        self.assertIsNot(actual["devices"]["ABCD:1234"], original["devices"]["ABCD:1234"])

    def test_empty_ranking_zeros_counts_and_removes_all_ranks(self):
        actual = device_status.update_counts(BASE, [])
        for entry in actual["devices"].values():
            self.assertEqual(entry["reports"], 0)
            self.assertNotIn("rank", entry)

    def test_invalid_ranking_is_rejected_without_mutating_status(self):
        for rows in (None, {}, [None], [{"id": "abcd:1234", "issues": []}],
                     [{"id": "ABCD:1234", "issues": "bad"}],
                     [{"id": "ABCD:1234", "issues": [True]}],
                     [{"id": "ABCD:1234", "issues": []}] * 2):
            before = deepcopy(BASE)
            with self.subTest(rows=rows), self.assertRaises(ValueError):
                device_status.update_counts(before, rows)
            self.assertEqual(before, BASE)

    def test_main_writes_utf8_lf_two_space_json_sorted_by_id(self):
        with tempfile.TemporaryDirectory() as folder:
            path, ranking = Path(folder) / "status.json", Path(folder) / "ranking.json"
            path.write_text(json.dumps(BASE), encoding="utf-8")
            ranking.write_text(json.dumps({"rows": [{"id": "ABCD:1234", "issues": [1, 2, 3]}]}), encoding="utf-8")
            self.assertEqual(device_status.main(["--status", str(path), "--ranking", str(ranking)]), 0)
            expected = device_status.update_counts(BASE, [{"id": "ABCD:1234", "issues": [1, 2, 3]}])
            expected["devices"] = dict(sorted(expected["devices"].items()))
            self.assertEqual(path.read_bytes(), (json.dumps(expected, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))
            self.assertEqual(device_status.load(path), expected)

    def test_check_only_and_no_ranking_never_rewrite(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "status.json"
            original = json.dumps(BASE).encode("utf-8")
            path.write_bytes(original)
            for extra in ([], ["--check"], ["--check", "--ranking", str(path.with_name("missing.json"))]):
                self.assertEqual(device_status.main(["--status", str(path)] + extra), 0)
                self.assertEqual(path.read_bytes(), original)

    def test_main_invalid_input_returns_one_and_does_not_overwrite(self):
        with tempfile.TemporaryDirectory() as folder, patch("builtins.print"):
            path, ranking = Path(folder) / "status.json", Path(folder) / "ranking.json"
            self.assertEqual(device_status.main(["--status", str(path), "--check"]), 1)
            path.write_bytes(b"broken")
            self.assertEqual(device_status.main(["--status", str(path), "--check"]), 1)
            original = json.dumps(BASE).encode("utf-8")
            path.write_bytes(original)
            for value in ([], {}, {"rows": None}):
                ranking.write_text(json.dumps(value), encoding="utf-8")
                self.assertEqual(device_status.main(["--status", str(path), "--ranking", str(ranking)]), 1)
                self.assertEqual(path.read_bytes(), original)


if __name__ == "__main__":
    unittest.main()
