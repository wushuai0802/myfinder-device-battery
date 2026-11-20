"""Offline tests for the passive INFI75 recorder."""

import argparse
import ctypes as c
from importlib.util import module_from_spec, spec_from_file_location
import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch


TOOL = Path(__file__).resolve().parents[1] / "hid_listen.py"
SPEC = spec_from_file_location("hid_listen", TOOL)
listener = module_from_spec(SPEC)
sys.modules[SPEC.name] = listener
SPEC.loader.exec_module(listener)


def entry(target, path, **changes):
    return {
        "path": path, "vendor_id": listener.VID, "product_id": listener.PID,
        "product_string": target.product, "usage_page": target.page, "usage": target.usage,
        **changes,
    }


class FakeHid:
    def __init__(self, entries, frames=None):
        self.enumerate = Mock(return_value=entries)
        self.frames = frames or {}
        self.opened = []
        self.closed = []
        self.reads = []
        self.time = 0.0
        self.device = Mock(side_effect=self._new_device)

    def clock(self):
        return self.time

    def _new_device(self):
        return FakeDevice(self)


class FakeDevice:
    def __init__(self, api):
        self.api = api
        self.path = None

    def open_path(self, path):
        self.path = path
        self.api.opened.append(path)

    def read(self, length, timeout):
        self.api.reads.append((self.path, length, timeout))
        self.api.time += timeout / 1000
        waiting = self.api.frames.get(self.path, [])
        if waiting:
            result = waiting.pop(0)
            if isinstance(result, BaseException):
                raise result
            return result
        return []

    def close(self):
        self.api.closed.append(self.path)


class ListenTests(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.directory = Path(folder.name)
        self.log = self.directory / "frames.jsonl"

    def run_fixture(self, api, *, seconds=0.12, events=lambda: (), input_length=lambda _path, _target: 8):
        return listener.record(api, self.log, seconds, events, input_length=input_length, clock=api.clock)

    def entries(self):
        return [json.loads(line) for line in self.log.read_text(encoding="utf-8").splitlines()]

    def test_forbidden_device_apis_are_absent(self):
        code = TOOL.read_text(encoding="utf-8")
        for name in (
            "write(", "send_feature_report", "get_feature_report", "get_input_report",
            "send_output_report", "HidD_SetFeature", "HidD_GetFeature", "HidD_SetOutputReport",
            "HidD_GetInputReport", "WriteFile", "DeviceIoControl",
        ):
            self.assertNotIn(name, code)

    def test_only_exact_allowlisted_collections_are_opened(self):
        targets = listener.COLLECTIONS
        paths = [f"path-{index}".encode("ascii") for index in range(len(targets))]
        allowed = [entry(target, path, product_string=" " + target.product + "\t")
                   for target, path in zip(targets, paths)]
        sample = targets[0]
        excluded = [
            entry(sample, b"keyboard", usage_page=1, usage=6),
            entry(sample, b"mouse", usage_page=1, usage=2),
            entry(sample, b"system", usage_page=1, usage=0x80),
            entry(sample, b"media", usage_page=0x0C),
            entry(sample, b"unknown-usage", usage=2),
            entry(sample, b"other-product", product_string=sample.product.lower()),
            entry(sample, b"other-vendor", vendor_id=1),
            entry(sample, b"other-id", product_id=1),
        ]
        api = FakeHid(excluded + allowed)
        lengths = dict(zip(targets, (8, 4, 65, 65)))
        proof = Mock(side_effect=lambda _path, target: lengths[target])
        self.assertEqual(self.run_fixture(api, seconds=0.22, input_length=proof),
                         {target: 0 for target in targets})
        api.enumerate.assert_called_once_with(0x05AC, 0x024F)
        self.assertEqual(api.opened, paths)
        self.assertEqual(api.closed, list(reversed(paths)))
        self.assertEqual([(path, length) for path, length, _timeout in api.reads[:4]],
                         list(zip(paths, (8, 4, 65, 65))))
        self.assertEqual(proof.call_count, 4)

    def test_duplicate_target_aborts_the_entire_session(self):
        a, b = listener.COLLECTIONS[:2]
        api = FakeHid([entry(a, b"first"), entry(b, b"allowed"), entry(a, b"second")])
        proof = Mock(return_value=8)
        with self.assertRaisesRegex(listener.ListenError, "身份不唯一，未监听"):
            self.run_fixture(api, input_length=proof)
        proof.assert_not_called()
        api.device.assert_not_called()
        self.assertFalse(self.log.exists())

    def test_frames_and_labels_have_relative_time_and_close_on_deadline(self):
        target = listener.COLLECTIONS[0]
        instance = "PRIVATEINSTANCE"
        path = (r"\\?\hid#vid_05ac&pid_024f#" + instance + "#class").encode()
        api = FakeHid([entry(target, path, serial_number="SERIALSECRET")],
                      {path: [[0xB5, 0x00, 0xFF]]})
        events = iter([["静置"], ["插线 " + instance + " SERIALSECRET " + path.decode()]])
        counts = self.run_fixture(api, events=lambda: next(events, []))
        self.assertEqual(self.entries(), [
            {"t": 0.0, "event": "静置"},
            {"t": 0.05, "conn": "2.4G", "usage": "FFB5:0001", "hex": "b500ff"},
            {"t": 0.05, "event": "插线 [redacted] [redacted] [redacted]"},
        ])
        self.assertEqual(counts, {target: 1})
        self.assertEqual(api.closed, [path])
        self.assertLess(abs(api.time - 0.12), 0.0011)

    def test_interrupt_closes_the_handle_and_keeps_collected_data(self):
        target = listener.COLLECTIONS[1]
        api = FakeHid([entry(target, b"vendor")], {b"vendor": [[5, 80], KeyboardInterrupt()]})
        self.assertEqual(self.run_fixture(api), {target: 1})
        self.assertEqual(self.entries()[0]["hex"], "0550")
        self.assertEqual(api.closed, [b"vendor"])

    def test_windows_caps_proof_uses_zero_access_and_checks_identity(self):
        target = listener.COLLECTIONS[0]
        kernel = SimpleNamespace(CreateFileW=Mock(return_value=10), CloseHandle=Mock())

        def attributes(_handle, pointer):
            pointer._obj.vendor, pointer._obj.product = 0x05AC, 0x024F
            return True

        def product(_handle, text, _size):
            text.value = target.product
            return True

        def preparsed(_handle, pointer):
            pointer._obj.value = 20
            return True

        def caps(_preparsed, pointer):
            pointer._obj.page, pointer._obj.usage, pointer._obj.input_length = target.page, target.usage, 8
            return 0x00110000

        hid = SimpleNamespace(
            HidD_GetAttributes=Mock(side_effect=attributes), HidD_GetProductString=Mock(side_effect=product),
            HidD_GetPreparsedData=Mock(side_effect=preparsed), HidP_GetCaps=Mock(side_effect=caps),
            HidD_FreePreparsedData=Mock(),
        )
        with patch.object(c, "WinDLL", side_effect=[kernel, hid, kernel, hid], create=True):
            self.assertEqual(listener.windows_input_length(b"vendor", target), 8)
            self.assertIsNone(listener.windows_input_length(b"vendor", listener.COLLECTIONS[1]))
        kernel.CreateFileW.assert_called_with("vendor", 0, 3, None, 3, 0, None)
        self.assertEqual(kernel.CloseHandle.call_count, 2)
        hid.HidD_FreePreparsedData.assert_called_once()
        self.assertEqual(c.sizeof(listener.Caps), 64)

    def test_unconfirmed_input_length_keeps_hidapi_closed(self):
        target = listener.COLLECTIONS[0]
        api = FakeHid([entry(target, b"vendor")])
        with self.assertRaisesRegex(listener.ListenError, "输入长度可确认"):
            self.run_fixture(api, input_length=lambda _path, _target: None)
        api.device.assert_not_called()
        self.assertFalse(self.log.exists())

    def test_console_enter_yields_complete_labels(self):
        characters = list("静置\r开始打字\r")
        console = SimpleNamespace(kbhit=lambda: bool(characters), getwch=lambda: characters.pop(0))
        with patch("builtins.print"):
            self.assertEqual(listener.ConsoleEvents(console)(), ["静置", "开始打字"])

    def test_app_data_and_invalid_durations_are_rejected(self):
        local = self.directory / "Local"
        with self.assertRaisesRegex(listener.ListenError, "程序数据目录之外"):
            listener.output_path(local / "MyFinderDeviceBattery" / "frames.jsonl", local)
        self.assertEqual(listener.output_path(self.log, local), self.log.resolve())
        self.assertEqual(listener.minutes("30"), 30)
        for value in ("0", "-1", "30.1", "NaN", "inf"):
            with self.assertRaises(argparse.ArgumentTypeError):
                listener.minutes(value)
