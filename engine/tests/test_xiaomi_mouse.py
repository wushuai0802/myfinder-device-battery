"""Tests for providers/xiaomi_mouse.py. No hardware is needed.

The fake receiver mirrors the official web driver's transaction on the MI_02
collection (usage page 0xFF03 / usage 0): one 32-byte output report with
data[1] = 0x16, answered by an input report with data[1] = 0x16 and the level
in data[4]. hidapi's write buffer carries the report id first, as on Windows.

Run from the repository root:

    python -m unittest discover -s tests
"""
import os
import sys
import types
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import _hidstub  # noqa: E402, F401

from providers import xiaomi_mouse as F  # noqa: E402


def reply(level, prefix_id=False, cmd=F.CMD_BATTERY):
    """The answer of the official query: data[1] = 0x16, data[3] = state, data[4] = level."""
    p = [0x00, cmd, 0x02, 0x01, level]
    p = ([0x00] + p) if prefix_id else p
    return p + [0x00] * (F.REPORT_LEN - len(p))


class FakeMouse:
    def __init__(self, level=85, silent=False, prefix_id=False, noise=()):
        self.level, self.silent, self.prefix_id = level, silent, prefix_id
        self.noise = [list(n) for n in noise]
        self.writes, self.queue, self.opened = [], [], 0

    def on_write(self, data):
        data = list(data)
        self.writes.append(data)
        if self.silent:
            return
        self.queue += [list(n) for n in self.noise]
        if data[:2] == [0x00, 0x00] and data[2] == F.CMD_BATTERY:
            self.queue.append(reply(self.level, self.prefix_id))

    def on_read(self):
        return self.queue.pop(0) if self.queue else []


class FakeBus:
    def __init__(self, mice):
        self.mice = mice

    def device_class(self):
        bus = self

        class FakeDevice:
            def open_path(self, path):
                self.m = bus.mice[path]
                self.m.opened += 1

            def write(self, data):
                self.m.on_write(data)
                return len(data)

            def read(self, n, timeout=None):
                return self.m.on_read()

            def close(self):
                pass

        return FakeDevice


def receiver_entries(pid=0x50FC):
    """The MI_02 collection plus the other collections of 2717:50FC."""
    shape = [(0, 0x0001, 0x02), (1, 0x0001, 0x06), (1, 0x000C, 0x01), (1, 0x0001, 0x80),
             (2, 0xFF03, 0x00), (2, 0xFF03, 0x02), (0, 0xFFEF, 0x00)]
    return [{"product_id": pid, "interface_number": i, "usage_page": p, "usage": u,
             "path": b"%04x-%d-%04x-%d" % (pid, i, p, n),
             "product_string": "Mi Mouse 4 Pro Receiver"}
            for n, (i, p, u) in enumerate(shape)]


VENDOR = b"50fc-2-ff03-4"


class ProviderTest(unittest.TestCase):
    def setUp(self):
        self._saved = (F.hid, F.hidlist, F.time)
        self.clock = [1000.0]
        F.time = types.SimpleNamespace(time=lambda: self.clock[0],
                                       sleep=lambda s: None)

    def tearDown(self):
        F.hid, F.hidlist, F.time = self._saved

    def poll(self, entries, mice):
        bus = FakeBus(mice)

        class Clocked(bus.device_class()):
            def read(inner, n, timeout=None):
                self.clock[0] += (timeout or 0) / 1000.0
                return super().read(n, timeout)

        F.hid = types.SimpleNamespace(device=Clocked)
        F.hidlist = types.SimpleNamespace(enumerate=lambda vid=0: list(entries))
        return F.XiaomiMouseProvider().poll()


class PollTest(ProviderTest):
    def test_receiver_reports_the_verified_level(self):
        mouse = FakeMouse(level=85)
        res = self.poll(receiver_entries(), {VENDOR: mouse})
        self.assertEqual([(r.name, r.level, r.charging, r.kind, r.source) for r in res],
                         [("Xiaomi Mi Mouse 4 Pro", 85, False, "mouse", "xiaomi_mouse")])
        self.assertEqual(mouse.writes, [[0x00, 0x00, F.CMD_BATTERY] + [0x00] * 30])
        self.assertEqual(len(mouse.writes[0]), F.REPORT_LEN)

    def test_only_the_mi02_collection_is_opened(self):
        mice = {e["path"]: FakeMouse() for e in receiver_entries()}
        self.poll(receiver_entries(), mice)
        self.assertEqual([p for p, m in mice.items() if m.opened], [VENDOR])

    def test_other_usage_on_the_same_page_is_not_touched(self):
        entries = [e for e in receiver_entries() if e["usage_page"] == 0xFF03]
        mice = {e["path"]: FakeMouse() for e in entries}
        self.poll(entries, mice)
        self.assertEqual([p for p, m in mice.items() if m.opened], [VENDOR])

    def test_level_zero_means_unknown(self):
        self.assertEqual(self.poll(receiver_entries(), {VENDOR: FakeMouse(level=0)}), [])

    def test_level_out_of_range_is_refused(self):
        self.assertEqual(self.poll(receiver_entries(), {VENDOR: FakeMouse(level=200)}), [])

    def test_silent_receiver_gives_no_icon(self):
        mouse = FakeMouse(silent=True)
        self.assertEqual(self.poll(receiver_entries(), {VENDOR: mouse}), [])
        self.assertEqual(len(mouse.writes), 1)

    def test_other_reports_are_skipped(self):
        noise = [[0x01, 0x00, 0x16, 0x00, 0x30, 0x00],          # a mouse report, no 0x16 at 1
                 [0x00, 0x02, 0x01, 0x50] + [0x00] * 29]        # a different vendor answer
        res = self.poll(receiver_entries(), {VENDOR: FakeMouse(level=40, noise=noise)})
        self.assertEqual([r.level for r in res], [40])

    def test_prefixing_report_id_also_parses(self):
        res = self.poll(receiver_entries(), {VENDOR: FakeMouse(level=62, prefix_id=True)})
        self.assertEqual([r.level for r in res], [62])

    def test_another_product_id_is_not_read(self):
        entries = receiver_entries(0x1234)
        mouse = FakeMouse()
        self.assertEqual(self.poll(entries, {b"1234-2-ff03-4": mouse}), [])
        self.assertEqual(mouse.opened, 0)


if __name__ == "__main__":
    unittest.main()
