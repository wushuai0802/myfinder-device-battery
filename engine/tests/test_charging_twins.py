"""Charging double cards fold only for declared cable/receiver pairs."""
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
from server import fold_charging_twins

RX, CABLE = (0x089D, 0x062F), (0x088D, 0x062E)
R1SE_RX, R1SE_CABLE = (0x373B, 0x1085), (0x3554, 0xF58F)


def dev(id_, physical, model, percent=80, charging=False, state="current", **extra):
    row = {"id": id_, "physicalId": physical, "name": "EWEADN S9", "kind": "mouse", "provider": "eweadn",
           "vendorId": model[0], "productId": model[1], "online": True, "percent": percent, "charging": charging,
           "chargingKnown": True, "measurementState": state}
    row.update(extra)
    return row


class ChargingTwinTests(unittest.TestCase):
    def test_declared_pair_folds_into_receiver_card(self):
        shown, hidden = fold_charging_twins([dev("rx", "p1", RX, 70), dev("cable", "p2", CABLE, 71, charging=True)])
        card, = shown
        self.assertEqual((card["id"], card["percent"], card["charging"], card["chargingTwinId"]), ("rx", 71, True, "cable"))
        self.assertEqual(len(hidden), 1)

    def test_stale_receiver_takes_cable_reading(self):
        shown, _ = fold_charging_twins([dev("rx", "p1", RX, 40, state="cached"), dev("cable", "p2", CABLE, 55, charging=True)])
        self.assertEqual([(d["id"], d["percent"], d["measurementState"]) for d in shown], [("rx", 55, "current")])

    def test_atk_r1_se_plus_issue_1090_folds_charging_cable_into_receiver(self):
        # #1090: stale 2.4G receiver (100%) + live charging wired cable (95%).
        rx = dev("r1-rx", "usb-rx", R1SE_RX, 100, charging=False, state="cached",
                 name="ATK VXE R1 SE+ (2.4 GHz)", provider="pulsar")
        cable = dev("r1-wired", "usb-wired", R1SE_CABLE, 95, charging=True, state="current",
                    name="ATK VXE R1 SE+ (wired)", provider="pulsar")
        shown, hidden = fold_charging_twins([rx, cable])
        card, = shown
        self.assertEqual((card["id"], card["percent"], card["charging"], card["measurementState"], card["chargingTwinId"]),
                         ("r1-rx", 95, True, "current", "r1-wired"))
        self.assertEqual(hidden, [{"name": "ATK VXE R1 SE+ (wired)", "kind": "mouse"}])

    def test_atk_r1_se_plus_does_not_fold_ambiguous_or_non_charging_cards(self):
        rx = dev("r1-rx", "usb-rx", R1SE_RX, 100, charging=False, state="current",
                 name="ATK VXE R1 SE+ (2.4 GHz)", provider="pulsar")
        cable_not_charging = dev("r1-wired", "usb-wired", R1SE_CABLE, 100, charging=False, state="current",
                                 name="ATK VXE R1 SE+ (wired)", provider="pulsar")
        cable_divergent = dev("r1-wired", "usb-wired", R1SE_CABLE, 90, charging=True, state="current",
                              name="ATK VXE R1 SE+ (wired)", provider="pulsar")
        for label, devices in (
            ("not charging", [rx, cable_not_charging]),
            ("both current and readings diverge > 3", [rx, cable_divergent]),
            ("two receivers", [rx, dev("r1-rx-2", "usb-rx-2", R1SE_RX, 100, state="cached", provider="pulsar"), cable_divergent]),
            ("two wired devices", [rx, cable_divergent, dev("r1-wired-2", "usb-wired-2", R1SE_CABLE, 95, charging=True, state="current", provider="pulsar")]),
            ("receiver only", [rx]),
            ("wired only", [cable_divergent]),
        ):
            with self.subTest(label):
                shown, hidden = fold_charging_twins(devices)
                self.assertEqual((len(shown), hidden), (len(devices), []))

    def test_never_folds_without_proof(self):
        other_mouse = (0x089D, 0x0700)
        cases = {
            # Codex 2026-11-22: two different mice of one brand, one charging, same level.
            "same vendor, undeclared models": [dev("a", "p1", RX, 80), dev("b", "p2", other_mouse, 80, charging=True)],
            "same vendor, both undeclared": [dev("a", "p1", (0x36F9, 1), 80), dev("b", "p2", (0x36F9, 2), 80, charging=True)],
            "cable not charging": [dev("a", "p1", RX, 80), dev("b", "p2", CABLE, 80)],
            "readings disagree": [dev("a", "p1", RX, 80), dev("b", "p2", CABLE, 60, charging=True)],
            "two receivers": [dev("a", "p1", RX, 80), dev("c", "p3", RX, 80), dev("b", "p2", CABLE, 80, charging=True)],
            "same connection": [dev("a", "p1", RX, 80), dev("b", "p1", CABLE, 80, charging=True)],
            "cable reading cached": [dev("a", "p1", RX, 80), dev("b", "p2", CABLE, 80, charging=True, state="cached")],
            "unknown model ids": [dev("a", "p1", RX, 80, vendorId=None), dev("b", "p2", CABLE, 80, charging=True)],
        }
        for name, devices in cases.items():
            with self.subTest(name):
                shown, hidden = fold_charging_twins(devices)
                self.assertEqual((len(shown), hidden), (len(devices), []))


if __name__ == "__main__":
    unittest.main()
