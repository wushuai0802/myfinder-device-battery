"""The exact JSON documents exercised by DevicePackTests.cs; no hardware."""
import json
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "engine"))
from device_pack import parse_pack


class DevicePackContractTests(unittest.TestCase):
    def test_shared_schema_documents(self):
        corpus = json.loads((ROOT / "tests/fixtures/device-pack-contract.json").read_text(encoding="utf-8"))
        self.assertEqual(corpus["schema"], "engine/device_pack_schema.json")
        for case in corpus["cases"]:
            with self.subTest(case=case["name"]):
                result = parse_pack(case["json"].encode("utf-8"))
                self.assertEqual(result.loaded, case["loaded"], result.rejected)

    def test_csharp_unicode_table_matches_runtime(self):
        import re
        source = (ROOT / "scripts/DevicePackUnicode.cs").read_text(encoding="utf-8")
        table = source.split("Ranges={", 1)[1].split("};", 1)[0]
        values = [int(value, 16) for value in re.findall(r"0x[0-9A-F]+", table)]
        ranges = iter(zip(values[::2], values[1::2]))
        low, high = next(ranges)
        for code in range(0x110000):
            while code > high:
                low, high = next(ranges, (0x110000, 0x110000))
            self.assertEqual(low <= code <= high, chr(code).isprintable(), hex(code))

    def test_csharp_profile_revisions_match_engine(self):
        import re
        from providers.profiles import profiles
        source = (ROOT / "scripts/DevicePackValidation.cs").read_text(encoding="utf-8")
        names = set(re.findall(r'"([a-z0-9-]+)"', source.split("RazerProfiles=", 1)[1].split(";", 1)[0]))
        self.assertEqual({(p.provider, p.name, p.revision) for p in profiles()}, {("razer", name, 1) for name in names})
        for profile in profiles():
            self.assertEqual(profile.match_fields, {"vendorId", "productId", "interfaceNumber", "usagePage", "usage"})
