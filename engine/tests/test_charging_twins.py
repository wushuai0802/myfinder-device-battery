"""Charging double cards fold only for declared cable/receiver pairs."""
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
from server import fold_charging_twins

RX, CABLE = (0x089D, 0x062F), (0x088D, 0x062E)


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
