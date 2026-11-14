"""Hardware-free DELUX provider gates and battery frame tests.

Runs without hidapi or hardware, using the common protocol-test stub.
"""
from __future__ import annotations

import sys
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
from providers import delux


def info(**changes):
    row = dict(vendor_id=0x3554, product_id=0xFB26, usage_page=0xFF02,
               usage=2, path=b"mock-delux", physical_id="usb:mock-unit",
               input_report_length=17, output_report_length=17,
               input_report_sizes={"8": 17}, output_report_sizes={"8": 17})
    row.update(changes)
    return row


def reply(level=63, status=0, charging=0, mv=3800, **changes):
    frame = [8, 4, status, 0, 0, 0, level, charging, mv >> 8, mv & 255] + [0] * 6
    for key, value in changes.items():
        frame[int(key)] = value
    frame.append((0x55 - sum(frame)) % 256)
    return frame


class DeluxTests(unittest.TestCase):
    def poll(self, inventory=None, frames=(), write_result=17):
        provider = delux.DeluxProvider()
        backend = MagicMock()
        dev = backend.device.return_value
        dev.write.return_value = write_result
        dev.read.side_effect = [[], *frames, []]
        with patch.object(delux, "hid", backend), patch.object(
                delux.hidlist, "enumerate", return_value=inventory if inventory is not None else [info()]) as enum:
            result = provider.poll()
        enum.assert_called_once_with(0x3554)
        return provider, backend, dev, result

    def test_request_is_only_ordinary_battery_query(self):
        expected = [8, 4] + [0] * 14 + [0x49]
        self.assertEqual(delux.make_request(), expected)
        self.assertEqual(sum(expected) % 256, 0x55)
        one = delux.make_request()
        one[5] = 0x80
        self.assertEqual(delux.make_request(), expected)

    def test_exact_identity_usage_uniqueness_and_proof_gates(self):
        bad = [[], [info(vendor_id=0x373B)], [info(product_id=0xF508)],
               [info(usage_page=0xFF05)], [info(usage=1)],
               [info(), info(path=b"ambiguous")], [info(path=b"")],
               [info(physical_id="collection:unresolved")],
               [info(physical_id=None)]]
        for direction in ("input", "output"):
            for length in (None, 0, 16, 18, 64, True, "17"):
                bad.append([info(**{direction + "_report_length": length})])
            for sizes in (None, {}, {"8": 16}, {"8": 18}, {"0": 17},
                          {8: 17}, {"8": "17"}, {"8": 17, "9": 17}):
                bad.append([info(**{direction + "_report_sizes": sizes})])
        for inventory in bad:
            with self.subTest(inventory=inventory):
                provider, backend, dev, result = self.poll(inventory)
                self.assertEqual(result, [])
                backend.device.assert_not_called()
                dev.open_path.assert_not_called()
                dev.write.assert_not_called()
                self.assertTrue(provider.diagnostics())

    def test_unrelated_inventory_has_no_fallback(self):
        provider, backend, dev, result = self.poll([
            info(vendor_id=0x1234, path=b"other-vendor"),
            info(product_id=0xF508, path=b"other-product"),
            info(usage=1, path=b"sibling"), info()], [reply()])
        self.assertEqual(len(result), 1)
        dev.open_path.assert_called_once_with(b"mock-delux")
        dev.write.assert_called_once_with(delux.make_request())
        dev.close.assert_called_once_with()
        self.assertEqual(result[0].name, "DELUX GH8K/M900 Ultra")

    def test_valid_reply_never_infers_charging(self):
        for charging in (0, 1, 2, 0x80, 255):
            with self.subTest(charging=charging):
                provider, backend, dev, result = self.poll(frames=[reply(charging=charging)])
                self.assertEqual(len(result), 1)
                status = result[0]
                self.assertEqual(status.level, 63)
                self.assertEqual(status.source, "delux")
                self.assertEqual(status.kind, "mouse")
                self.assertTrue(status.online)
                self.assertTrue(status.connection_known)
                self.assertFalse(status.charging)
                self.assertIs(status.charging_known, False)
                self.assertFalse(status.unconfirmed)
                self.assertIsNone(status.dpi)
                self.assertIsNone(status.dpi_target)
                dev.write.assert_called_once_with(delux.make_request())
                self.assertEqual(dev.read.call_count, 2)
                self.assertEqual(dev.read.call_args.args[0], 17)
                dev.close.assert_called_once_with()

    def test_captured_hardware_frames(self):
        for raw in ("08 04 00 00 00 02 5a 00 0f df 00 00 00 00 00 00 ff", "08 04 00 00 00 02 5a 00 0f e3 00 00 00 00 00 00 fb"):
            frame = bytes.fromhex(raw)
            self.assertEqual(delux.parse_reply(frame), (90, False, ""))
            provider, backend, dev, result = self.poll(frames=[frame])
            self.assertEqual(result[0].level, 90)
            self.assertEqual(result[0].verification_level, "A")

    def test_real_zero_percent_requires_valid_voltage_reply(self):
        self.assertEqual(delux.parse_reply(reply(level=0)), (0, False, ""))
        self.assertIsNone(delux.parse_reply(delux.make_request()))

    def test_invalid_reply_echo_and_no_reply_return_no_status(self):
        damaged_checksum = reply()
        damaged_checksum[-1] ^= 1
        bad = [delux.make_request(), [8, 4, 1, 0, 0, 0x80] + [0] * 10 + [0xC8],
               reply(status=1), reply(status=255), reply()[:-1], reply() + [0],
               damaged_checksum, reply(level=101), reply(mv=0), reply(mv=2499),
               reply(mv=5001), reply(mv=65535), reply(**{"0": 9}),
               reply(**{"1": 0x0A}), [0] * 17]
        for frame in bad:
            with self.subTest(frame=frame):
                self.assertIsNone(delux.parse_reply(frame))
                provider, backend, dev, result = self.poll(frames=[frame])
                self.assertEqual(result, [])
                self.assertTrue(provider.diagnostics())
                self.assertTrue(all("mock-delux" not in line for line in provider.diagnostics()))
                dev.write.assert_called_once_with(delux.make_request())
                dev.close.assert_called_once_with()
        provider, backend, dev, result = self.poll()
        self.assertEqual(result, [])
        self.assertEqual(provider.diagnostics(), ["no valid battery reply; connection unknown"])
        dev.write.assert_called_once_with(delux.make_request())
        dev.close.assert_called_once_with()

    def test_invalid_then_valid_reply_and_read_limit(self):
        provider, backend, dev, result = self.poll(frames=[delux.make_request(), reply()])
        self.assertEqual(result[0].level, 63)
        dev.write.assert_called_once_with(delux.make_request())
        provider, backend, dev, result = self.poll(frames=[delux.make_request()] * 40)
        self.assertEqual(result, [])
        self.assertEqual(dev.read.call_count, 33)
        dev.write.assert_called_once_with(delux.make_request())

    def test_no_reply_does_not_reuse_last_reading(self):
        provider = delux.DeluxProvider()
        backend = MagicMock()
        dev = backend.device.return_value
        dev.write.return_value = 17
        dev.read.side_effect = [[], reply(), [], []]
        with patch.object(delux, "hid", backend), patch.object(delux.hidlist, "enumerate", return_value=[info()]):
            self.assertEqual(provider.poll()[0].level, 63)
            self.assertEqual(provider.poll(), [])
        self.assertEqual(provider.diagnostics(), ["no valid battery reply; connection unknown"])
        self.assertEqual(dev.write.call_count, 2)

    def test_partial_write_never_retries_or_reads(self):
        provider, backend, dev, result = self.poll(write_result=16)
        self.assertEqual(result, [])
        dev.write.assert_called_once_with(delux.make_request())
        dev.read.assert_called_once_with(17, 1)
        dev.close.assert_called_once_with()

    def test_io_failures_and_diagnostics_are_fixed(self):
        for method in ("open_path", "write", "read"):
            provider = delux.DeluxProvider()
            backend = MagicMock()
            dev = backend.device.return_value
            dev.write.return_value = 17
            getattr(dev, method).side_effect = OSError("private mock-delux path")
            with patch.object(delux, "hid", backend), patch.object(delux.hidlist, "enumerate", return_value=[info()]):
                self.assertEqual(provider.poll(), [])
            self.assertEqual(provider.diagnostics(), ["battery I/O failed; connection unknown"])
            dev.close.assert_called_once_with()
        provider = delux.DeluxProvider()
        with patch.object(delux.hidlist, "enumerate", side_effect=OSError("private path")):
            self.assertEqual(provider.poll(), [])
        self.assertEqual(provider.diagnostics(), ["enumeration failed: nothing sent"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
