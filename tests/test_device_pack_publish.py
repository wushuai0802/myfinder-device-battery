"""Publication rejects rows the runtime would safely ignore."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("publish_validation", ROOT / "scripts/validate_device_pack.py")
publish = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publish)


class PublishValidationTests(unittest.TestCase):
    def validate(self, data):
        with tempfile.TemporaryDirectory() as work:
            path = Path(work) / "source.json"
            path.write_text(json.dumps(data), encoding="utf-8")
            return publish.build(path, 2026110401)

    def test_empty_and_registered_profile(self):
        self.validate({"entries": [], "disabled": []})
        self.validate(self.source())

    @staticmethod
    def source():
        return {"entries": [{"provider": "razer", "profile": "hyperspeed-mouse-1f", "profileRev": 1,
                             "match": {"vendorId": "1532", "productId": "FE01", "usagePage": "FF00"},
                             "name": "Test", "kind": "mouse"}], "disabled": []}

    def test_row_rejections_block_publication(self):
        for field, value in (("provider", "native_atk"), ("provider", "unknown"),
                             ("profile", "missing"), ("profileRev", 2), ("kind", "headset")):
            with self.subTest(field=field, value=value):
                source = self.source();source["entries"][0][field] = value
                with self.assertRaises(ValueError):
                    self.validate(source)
        for pid in ("00B9", "053A"):
            source = self.source();source["entries"][0]["match"]["productId"] = pid
            with self.assertRaisesRegex(ValueError, "builtin_id_conflict"):
                self.validate(source)
        source = self.source();source["entries"].append(copy.deepcopy(source["entries"][0]))
        with self.assertRaisesRegex(ValueError, "duplicate_id"):
            self.validate(source)

    def test_disabled_native_rejected_but_builtin_disable_allowed(self):
        source = {"entries": [], "disabled": [{"provider": "razer", "vendorId": "1532", "productId": "00B9", "reason": "test"}]}
        self.validate(source);source["disabled"][0]["provider"] = "native_atk"
        with self.assertRaisesRegex(ValueError, "unsupported_provider"):
            self.validate(source)
