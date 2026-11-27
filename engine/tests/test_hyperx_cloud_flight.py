"""Synthetic and live-protocol-shape tests for HyperX Cloud Flight Wireless."""
from __future__ import annotations

import os
import sys
import types
import unittest
from unittest.mock import patch

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import _hidstub  # noqa: F401
from providers import hyperx_cloud_flight as P


def response(voltage, length=20):
    return [0x21, 0xFF, 0x05, voltage >> 8, voltage & 0xFF] + [0] * (length - 5)


def receiver(pid=0x1723, page=0xFF90, physical="receiver"):
    return {"vendor_id": P.VID, "product_id": pid, "path": physical.encode(),
            "physical_id": physical, "usage_page": page, "usage": P.USAGE,
            "input_report_length": 20, "output_report_length": 20,
            "input_report_sizes": {"33": 20}, "output_report_sizes": {"33": 20}}


class FakeDevice:
    def __init__(self, reply):
        self.reply = reply
        self.writes = []
        self.opened = []
        self.closed = False

    def open_path(self, path):
        self.opened.append(path)

    def write(self, packet):
        self.writes.append(list(packet))
        return len(packet)

    def read(self, length, timeout_ms):
        return self.reply

    def close(self):
        self.closed = True


class CloudFlightTests(unittest.TestCase):
    def setUp(self):
        self.saved_hid, self.saved_enumerate = P.hid, P.hidlist.enumerate

    def tearDown(self):
        P.hid, P.hidlist.enumerate = self.saved_hid, self.saved_enumerate

    def test_curve_and_charging_threshold(self):
        self.assertEqual(P.estimate_level(3648), 5)
        self.assertEqual(P.estimate_level(3958), 95)
        self.assertEqual(P.parse_reply(response(3800, 15)), (55, False))
        self.assertEqual(P.parse_reply(response(0x100B)), (100, False))
        self.assertEqual(P.parse_reply(response(0x100C)), (None, True))
        self.assertIsNone(P.parse_reply(response(3800, 14)))

    def test_poll_selects_ff90_collection_and_reads_both_pids(self):
        for pid, page in ((0x1723, 0xFF90), (0x16C4, 0xFF53)):
            with self.subTest(pid=pid):
                device = FakeDevice(response(3958))
                P.hid = types.SimpleNamespace(device=lambda: device)
                P.hidlist.enumerate = lambda vid: [receiver(pid, page)]
                found = P.HyperXCloudFlightProvider().poll()
                self.assertEqual([(d.level, d.kind, d.physical_id) for d in found],
                                 [(95, "headset", "receiver")])
                self.assertEqual(device.writes, [P.REQUEST])
                self.assertTrue(device.closed)

    def test_rejects_echo_zero_and_wrong_header(self):
        for raw in (P.REQUEST, response(0), [0x22] + response(3958)[1:], response(6000)):
            self.assertIsNone(P.parse_reply(raw))

    def test_unproven_reports_never_open(self):
        device = FakeDevice(response(3958))
        info = receiver()
        del info["input_report_sizes"]
        with patch.object(P, "hid", types.SimpleNamespace(device=lambda: device)), \
                patch.object(P.hidlist, "enumerate", return_value=[info]):
            self.assertEqual(P.HyperXCloudFlightProvider().poll(), [])
        self.assertEqual(device.opened, [])

    def test_ambiguous_or_unresolved_collections_never_open(self):
        for infos in ([receiver(), receiver()], [receiver(physical="")],
                      [receiver(physical="collection:unresolved")]):
            device = FakeDevice(response(3958))
            with patch.object(P, "hid", types.SimpleNamespace(device=lambda: device)), \
                    patch.object(P.hidlist, "enumerate", return_value=infos):
                self.assertEqual(P.HyperXCloudFlightProvider().poll(), [])
            self.assertEqual(device.opened, [])

    def test_skips_echo_and_unrelated_frame_before_battery_reply(self):
        device = FakeDevice(None)
        with patch.object(device, "read", side_effect=[P.REQUEST, [0x22] * 20, response(3958)]), \
                patch.object(P, "hid", types.SimpleNamespace(device=lambda: device)), \
                patch.object(P.hidlist, "enumerate", return_value=[receiver()]):
            self.assertEqual(P.HyperXCloudFlightProvider().poll()[0].level, 95)
        self.assertEqual(device.writes, [P.REQUEST])

    def test_io_error_is_redacted_and_handle_is_closed(self):
        device = FakeDevice(None)
        provider = P.HyperXCloudFlightProvider()
        with patch.object(device, "open_path", side_effect=OSError("private-device-path")), \
                patch.object(P, "hid", types.SimpleNamespace(device=lambda: device)), \
                patch.object(P.hidlist, "enumerate", return_value=[receiver()]):
            self.assertEqual(provider.poll(), [])
        self.assertTrue(device.closed)
        self.assertNotIn("private-device-path", " ".join(provider.diagnostics()))

    def test_charging_invalidates_previous_percentage(self):
        import server
        from providers.base import DeviceStatus
        status = DeviceStatus("flight", "Flight", None, True, source=P.HyperXCloudFlightProvider.name,
                              connection_known=True, charging_known=True, level_unavailable=True)
        result = server.normalise(status, {"percent": 95}, "2026-11-27T12:00:00Z")
        self.assertIsNone(result["percent"])
        self.assertTrue(result["charging"])

    def test_wrong_collection_is_never_written(self):
        device = FakeDevice(response(3800))
        info = {"product_id": 0x1723, "path": b"wrong", "usage_page": 0x000C, "usage": 1}
        P.hid = types.SimpleNamespace(device=lambda: device)
        P.hidlist.enumerate = lambda vid: [info]
        try:
            self.assertEqual(P.HyperXCloudFlightProvider().poll(), [])
        finally:
            P.hid = None
        self.assertEqual(device.writes, [])


if __name__ == "__main__":
    unittest.main()
