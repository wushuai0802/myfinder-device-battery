"""Synthetic #224 protocol/descriptor fixtures; no real HID or vendor code."""
from contextlib import contextmanager
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from test_mchose_by_keyboard import M, P, entry, identity, reply
from test_new_brands import FakeHid, H
from providers.vgn_beiying import VgnBeiyingProvider


def aula_entry(physical="receiver-a", **changes):
    info = dict(input_report_length=0, output_report_length=0,
                input_report_sizes={}, output_report_sizes={},
                feature_report_length=8, feature_report_sizes={"6": 8})
    return entry(physical, **(info | changes))


def aula_reply(level=64):
    # Synthetic variant of the eight-byte user-reported frame; unknown bytes
    # intentionally carry no charging or checksum semantics.
    return [6, 0x87, 2, level, 0x64, 0x64, 0xAB, 0x82]


class FeatureHid(FakeHid):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.feature_writes, self.feature_reads = [], []

    def device(self):
        handle = super().device()

        def send(packet):
            self.feature_writes.append((handle.path, bytes(packet)))
            if self.error_at == "feature_write":
                raise OSError("private-device-path")
            return len(packet) if self.write_result is None else self.write_result

        def get(report_id, length):
            self.feature_reads.append((handle.path, report_id, length))
            if self.error_at == "feature_read":
                raise ValueError("private-device-path")
            return self.frames.pop(0) if self.frames else []

        handle.send_feature_report, handle.get_feature_report = send, get
        return handle


@contextmanager
def aula_fixture(frames=(), inventory=None, **kwargs):
    bus = FeatureHid(frames, **kwargs)
    infos = [aula_entry()] if inventory is None else inventory
    with patch.object(H, "hid", bus), \
            patch.object(H.hidlist, "enumerate", side_effect=lambda vid=0: [
                d for d in infos if not vid or d["vendor_id"] == vid]), \
            patch.object(H, "time", SimpleNamespace(monotonic=lambda: bus.now)):
        yield bus


class AulaFa09Tests(unittest.TestCase):
    def assert_feature_only(self, bus, count=1):
        self.assertEqual(bus.writes, [])
        self.assertEqual(bus.reads, [])
        self.assertEqual(len(bus.feature_writes), count)
        self.assertTrue(all(frame == bytes.fromhex("06 87 00 00 01 00 02 00")
                            for _, frame in bus.feature_writes))
        self.assertTrue(all((rid, length) == (6, 8) for _, rid, length in bus.feature_reads))
        self.assertEqual(bus.opened, bus.closed)

    def test_exact_frame_name_unknown_charging_and_only_one_card(self):
        ff04 = aula_entry(path=b"feature-ff04")
        ff02 = entry(path=b"by-ff02", usage_page=0xFF02)
        for factories in ((P, VgnBeiyingProvider), (VgnBeiyingProvider, P)):
            with self.subTest(order=factories), aula_fixture([aula_reply()], [ff02, ff04]) as bus:
                status, = [s for factory in factories for s in factory().poll()]
                self.assertEqual((status.name, status.level, status.kind, status.verification_level),
                                 ("2.4G 无线键盘（3554:FA09）", 64, "keyboard", "C"))
                self.assertEqual(status.key, "mchose_by_keyboard:receiver-a")
                self.assertFalse(status.charging_known)
                self.assertFalse(status.charging)
                self.assertTrue(status.unconfirmed)
                self.assert_feature_only(bus)
                self.assertEqual(bus.feature_reads, [(ff04["path"], 6, 8)])
                self.assertEqual(bus.opened, [ff04["path"]])

    def test_reply_validation_and_zero_suppression(self):
        for level in (1, 64, 99, 100):
            self.assertEqual(M.parse_aula_reply(aula_reply(level)), (level, False, ""))
        invalid = [None, [], aula_reply()[:-1], aula_reply() + [0],
                   M.AULA_REQUEST, M.make_request(), identity()]
        for offset, value in ((0, 0), (0, 0x13), (1, 0x88), (3, 0), (3, -1),
                              (3, 101), (3, 255), (3, True), (3, 64.0), (7, 256)):
            frame = aula_reply()
            frame[offset] = value
            invalid.append(frame)
        for frame in invalid:
            with self.subTest(frame=frame), aula_fixture([frame]) as bus:
                self.assertIsNone(M.parse_aula_reply(frame))
                self.assertEqual(P().poll(), [])
                self.assert_feature_only(bus)

    def test_unproven_feature_or_absent_io_caps_never_open_or_send(self):
        defects = [dict(feature_report_length=n) for n in (None, 0, 7, 8.0, 9, 20)]
        defects += [dict(feature_report_sizes=s) for s in
                    (None, {}, {"6": 7}, {"6": 8.0}, {"6": 9}, {"7": 8}, {6: 8}, {"6": 8, "7": 8})]
        for direction in ("input", "output"):
            defects += [{direction + "_report_length": n} for n in (None, False, 8, 20)]
            defects += [{direction + "_report_sizes": sizes} for sizes in (None, {"19": 20})]
        defects += [dict(vendor_id=0x41E4, product_id=0x2001),
                    dict(vendor_id=0x05AC, product_id=0x024F),
                    dict(product_id=0xFA07), dict(usage_page=0xFF02), dict(usage=1),
                    dict(path=b""), dict(physical_id="collection:unknown"),
                    dict(product_string="AULA F87")]
        inventories = [[aula_entry(**d)] for d in defects]
        inventories += [[aula_entry(), aula_entry(path=b"duplicate")],
                        [aula_entry(), entry(path=b"second-by-endpoint")]]
        for infos in inventories:
            with self.subTest(infos=infos), aula_fixture([aula_reply()], infos) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(bus.opened, [])
                self.assert_feature_only(bus, 0)

    def test_proven_by_wins_if_feature_exists_and_never_falls_back(self):
        mixed = entry(feature_report_length=8, feature_report_sizes={"6": 8})
        for frames, count in (([identity(), reply()], 1), ([], 0), ([identity(), reply(0)], 0)):
            with self.subTest(frames=frames), aula_fixture(frames, [mixed]) as bus:
                statuses = P().poll()
                self.assertEqual(len(statuses), count)
                self.assertTrue(all(not status.unconfirmed for status in statuses))
                self.assertEqual(bus.feature_writes, [])
                self.assertEqual(bus.feature_reads, [])
                self.assertTrue(all(frame in (M.identity_request(), M.make_request())
                                    for _, frame in bus.writes))

    def test_failures_never_send_dangerous_commands_or_retry_in_same_poll(self):
        for options in (dict(error_at="open"), dict(error_at="feature_write"),
                        dict(error_at="feature_read"), dict(write_result=-1),
                        dict(write_result=0), dict(write_result=7)):
            with self.subTest(options=options), aula_fixture([aula_reply()], **options) as bus:
                provider = P()
                self.assertEqual(provider.poll(), [])
                self.assert_feature_only(bus, int(options.get("error_at") != "open"))
                self.assertEqual(len(bus.feature_reads), int(options.get("error_at") == "feature_read"))
                self.assertNotIn("private-device-path", str(provider.diagnostics()))

    def test_silence_backoff_recovery_and_removal(self):
        provider = P()
        for now, frames, sends, levels in ((0, [], 1, []), (5, [], 1, []),
                                           (60, [aula_reply()], 0, []), (66, [], 1, []),
                                           (180, [aula_reply()], 0, []),
                                           (187, [aula_reply()], 1, [64]),
                                           (188, [aula_reply(70)], 1, [70])):
            with self.subTest(now=now), aula_fixture(frames) as bus:
                bus.now = now
                self.assertEqual([s.level for s in provider.poll()], levels)
                self.assert_feature_only(bus, sends)
        with aula_fixture([]):
            provider.poll()
            provider.poll()
        with aula_fixture([], []):
            provider.poll()
        with aula_fixture([aula_reply()]) as bus:
            self.assertEqual(len(provider.poll()), 1)
            self.assert_feature_only(bus)

    def test_by_and_aula_receivers_do_not_leak_name_or_charging(self):
        for reverse in (False, True):
            infos = [aula_entry("aula"), entry("by")]
            frames = [aula_reply(), identity(), reply(63, 0x10)]
            if reverse:
                infos.reverse()
                frames = [identity(), reply(63, 0x10), aula_reply()]
            with self.subTest(reverse=reverse), aula_fixture(frames, infos) as bus:
                statuses = {s.physical_id: s for s in P().poll()}
                self.assertEqual(statuses["aula"].name, "2.4G 无线键盘（3554:FA09）")
                self.assertFalse(statuses["aula"].charging_known)
                self.assertTrue(statuses["aula"].unconfirmed)
                self.assertEqual(statuses["by"].name, "MCHOSE X75 V2")
                self.assertTrue(statuses["by"].charging)
                self.assertTrue(statuses["by"].charging_known)
                self.assertFalse(statuses["by"].unconfirmed)
                self.assertEqual(len(bus.feature_writes), 1)
                self.assertEqual([f for _, f in bus.writes], [M.identity_request(), M.make_request()])


if __name__ == "__main__":
    unittest.main()
