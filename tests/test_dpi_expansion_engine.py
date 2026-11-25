"""Integration routing & normalisation regressions for 1.4.1-beta DPI expansion."""
import io
import json
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "engine"))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "engine" / "tests"))
import _hidstub  # noqa: F401
from providers.base import DeviceStatus
import server


class DpiExpansionServerTests(unittest.TestCase):
    def test_dpi_providers_registry_covers_all_expanded_vendors(self):
        expected = {
            "logitech": (0x046D,),
            "pulsar": (0x3554, 0x373B, 0x3770, 0x3710),
            "razer": (0x1532,),
            "mchose": (0x5253, 0x3837, 0x2023),
            "asus": (0x0B05,),
            "wlmouse": (0x36A7,),
            "lamzu": (0x373E,),
            "gwolves": (0x33E4,),
            "families": (0x260D,),
        }
        self.assertEqual(server.DPI_PROVIDERS, expected)

    def test_normalise_readonly_and_writable_provider_contracts(self):
        token = "a" * 64
        now = server.utcnow()
        gpw3 = DeviceStatus(
            key="logitech:5617941E", name="PRO X 2", level=55, charging=False,
            online=True, source="logitech", kind="mouse",
            dpi={"supported": True, "canSet": False, "current": 1600,
                 "stages": [400, 800, 1600, 3200], "stageSource": "onboard",
                 "checkedAtUtc": now, "targetToken": None, "error": None},
        )
        gpw3_view = server.normalise(gpw3, None, now)
        self.assertTrue(gpw3_view["dpi"]["supported"])
        self.assertFalse(gpw3_view["dpi"]["canSet"])
        self.assertEqual(gpw3_view["dpi"]["current"], 1600)
        self.assertEqual(gpw3_view["dpi"]["stages"], [400, 800, 1600, 3200])
        self.assertIsNone(gpw3_view["dpi"]["targetToken"])

        for source, stage_source in (
            ("razer", "quick"), ("razer", "onboard"),
            ("mchose", "onboard"), ("asus", "onboard"),
            ("wlmouse", "onboard"), ("lamzu", "onboard"),
            ("gwolves", "onboard"), ("families", "onboard"),
        ):
            with self.subTest(source=source, stage_source=stage_source):
                st = DeviceStatus(
                    key=f"{source}:unit", name=f"{source} mouse", level=80,
                    charging=False, online=True, source=source, kind="mouse",
                    dpi={"supported": True, "canSet": True, "current": 800,
                         "stages": [400, 800, 1600], "stageSource": stage_source,
                         "checkedAtUtc": now, "targetToken": token, "error": None},
                )
                view = server.normalise(st, None, now)
                self.assertTrue(view["dpi"]["canSet"])
                self.assertEqual(view["dpi"]["targetToken"], token)

        families_quick = DeviceStatus(
            key="families:dareu", name="Dareu", level=80, charging=False,
            online=True, source="families", kind="mouse",
            dpi={"supported": True, "canSet": True, "current": 800,
                 "stages": [400, 800], "stageSource": "quick",
                 "checkedAtUtc": now, "targetToken": token, "error": None},
        )
        self.assertFalse(server.normalise(families_quick, None, now)["dpi"]["canSet"])

        pack_mouse = DeviceStatus(
            key="razer:pack", name="Pack Mouse", level=80, charging=False,
            online=True, source="razer", kind="mouse", model_source="pack",
            dpi={"supported": True, "canSet": True, "current": 800,
                 "stages": [400, 800], "stageSource": "onboard",
                 "checkedAtUtc": now, "targetToken": token, "error": None},
        )
        self.assertIsNone(server.normalise(pack_mouse, None, now)["dpi"])

    def test_reader_child_set_dpi_routes_and_rejects_pack_across_new_providers(self):
        token = "b" * 64
        for provider, vid, pid in (
            ("razer", 0x1532, 0x00C1),
            ("mchose", 0x5253, 0x1011),
            ("families", 0x260D, 0x1156),
        ):
            with self.subTest(provider=provider):
                inv = [{"vendor_id": vid, "product_id": pid, "physical_id": "pnp:usb\\v"}]
                cmd = {"action": "set_dpi", "sequence": 1, "provider": provider,
                       "physicalId": "pnp:usb\\v", "inventory": inv,
                       "target": {"provider": provider}, "dpi": 800,
                       "expectedDpi": 400, "targetToken": token}
                lines = json.dumps(cmd) + "\n"
                out = io.StringIO()
                fake_mod = types.SimpleNamespace(set_device_dpi=lambda sel, t, v, e, tok: {
                    "success": True, "outcomeUnknown": False, "confirmedDpi": v, "count": len(sel)
                })
                real_import = server.importlib.import_module

                def fake_import(name, *args, **kwargs):
                    if name == "providers." + provider:
                        return fake_mod
                    return real_import(name, *args, **kwargs)

                incoming = types.SimpleNamespace(buffer=io.BytesIO(lines.encode()))
                with patch.object(server.sys, "stdin", incoming), \
                     patch.object(server.sys, "stdout", out), \
                     patch.object(server.importlib, "import_module", side_effect=fake_import):
                    server.reader_child()
                reply = json.loads(out.getvalue().strip())
                self.assertTrue(reply["success"])
                self.assertEqual(reply["confirmedDpi"], 800)
                self.assertEqual(reply["count"], 1)


if __name__ == "__main__":
    unittest.main()
