from pathlib import Path
import sys
import unittest
from unittest.mock import MagicMock, call, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
import battery_hid
import server
from providers import mchose
from providers.mchose_g3v2 import MchoseG3V2Provider


PATH_TAIL = '#7&ABC&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}'
PATH_PREFIX = (chr(92) * 2) + '?' + chr(92) + 'hid#vid_3837&pid_4245&mi_'
PHYSICAL_PREFIX = 'pnp:usb' + chr(92) + 'vid_3837&pid_4245' + chr(92)
STATUS_REQUEST = bytes((0x00, 0x55, 0xED)) + bytes(62)
BATTERY_REQUEST = bytes((0x00, 0x55, 0x30, 0xA5, 0x0B, 0x2E, 0x01, 0x01, 0x01)) + bytes(56)


def hid_path(interface, collection=None):
    token = format(interface, '02x')
    if collection is not None:
        token += '&col' + format(collection, '02x')
    return PATH_PREFIX + token + PATH_TAIL


def receiver(**changes):
    base = dict(
        vendor_id=0x3837,
        product_id=0x4245,
        path=hid_path(2).upper(),
        interface_number=2,
        product_string='MCHOSE G3 V2',
        physical_id=PHYSICAL_PREFIX + 'FIXTURE',
        usage_page=0xFF01,
        usage=0x0010,
        input_report_length=65,
        output_report_length=65,
        input_report_sizes={'0': 65},
        output_report_sizes={'0': 65},
    )
    base.update(changes)
    return base


def status_frame(status, header=bytes((0xAA, 0xED)), size=64):
    data = bytearray(64)
    data[:2] = header
    data[8] = status
    if size > len(data):
        data.extend(bytes(size - len(data)))
    return list(data[:size])


def battery_frame(raw, charge, header=bytes((0xAA, 0x30)), size=64):
    data = bytearray(64)
    data[:2] = header
    data[8] = raw
    data[9] = charge
    if size > len(data):
        data.extend(bytes(size - len(data)))
    return list(data[:size])


def stale_frame():
    return status_frame(0x7F, header=bytes((0xAA, 0xFA)))


class MchoseG3V2Tests(unittest.TestCase):
    def _poll(self, reads, rows=None, writes=(65, 65)):
        if rows is None:
            rows = [receiver()]
        dev = MagicMock()
        dev.write.side_effect = list(writes)
        dev.read.side_effect = list(reads)
        with patch.object(battery_hid.hidlist, 'enumerate', return_value=rows), \
             patch.object(battery_hid.hid, 'device', return_value=dev) as factory:
            cards = MchoseG3V2Provider().poll()
        return cards, dev, factory

    def _assert_reads(self, dev, count):
        self.assertEqual(dev.read.call_count, count)
        for args, kwargs in dev.read.call_args_list:
            self.assertEqual(args[0], 64)
            self.assertGreaterEqual(args[1], 1)
            self.assertLessEqual(args[1], 550)
            self.assertEqual(kwargs, {})

    def test_exact_requests_bucket_boundaries_and_charge(self):
        cases = (
            (1, 0, 10), (14, 1, 10), (15, 0, 20), (24, 1, 20),
            (25, 0, 30), (34, 1, 30), (35, 0, 40), (44, 1, 40),
            (45, 0, 50), (54, 1, 50), (55, 0, 60), (64, 1, 60),
            (65, 0, 70), (74, 1, 70), (75, 0, 80), (84, 1, 80),
            (85, 0, 90), (94, 1, 90), (95, 0, 100), (100, 1, 100),
        )
        for raw, charge, expected in cases:
            with self.subTest(raw=raw, charge=charge):
                cards, dev, factory = self._poll([
                    status_frame(0 if raw < 50 else 2),
                    battery_frame(raw, charge),
                ])
                card, = cards
                self.assertEqual(card.level, expected)
                self.assertEqual(card.charging, bool(charge))
                self.assertEqual(card.kind, 'mouse')
                self.assertEqual((card.name, card.source), ('MCHOSE G3 V2', 'mchose_g3v2'))
                self.assertTrue(card.charging_known)
                self.assertFalse(card.level_unavailable)
                self.assertEqual(dev.write.call_args_list, [call(STATUS_REQUEST), call(BATTERY_REQUEST)])
                self._assert_reads(dev, 2)
                dev.send_feature_report.assert_not_called()
                dev.get_feature_report.assert_not_called()
                dev.close.assert_called_once_with()
                factory.assert_called_once_with()

    def test_identity_attributes_and_case_insensitive_path(self):
        provider = MchoseG3V2Provider()
        self.assertEqual(provider.name, 'mchose_g3v2')
        self.assertEqual(provider.kind, 'mouse')
        self.assertTrue(provider.charging_known)
        self.assertEqual(server.recognized_candidate_count(provider.name, [receiver()]), 1)
        self.assertEqual(server.recognized_candidate_count(provider.name, [receiver(product_id=0x418C)]), 0)
        self.assertTrue(provider.matches_collection(receiver()))
        self.assertTrue(MchoseG3V2Provider.matches_collection(receiver()))
        for interface in (0, 1, 2, 7):
            self.assertTrue(provider.matches_collection(
                receiver(path=hid_path(interface), interface_number=interface)))
        self.assertTrue(provider.matches_collection(
            receiver(path=hid_path(2, 4), interface_number=2)))

    def test_windows_hid_byte_and_text_paths_use_the_same_device(self):
        for path in (hid_path(2), hid_path(2).encode('ascii')):
            with self.subTest(path=path):
                cards, dev, _ = self._poll([status_frame(0), battery_frame(57, 1)],
                                           rows=[receiver(path=path)])
                self.assertEqual([(c.name, c.level) for c in cards], [('MCHOSE G3 V2', 60)])
                dev.open_path.assert_called_once_with(path)

    def test_legacy_mchose_never_queries_g3_receiver_or_its_siblings(self):
        rows = [receiver(input_report_sizes=None), receiver(usage_page=0xFF05, usage=0x61),
                receiver(usage_page=0xFF0B, usage=0x104)]
        with patch.object(mchose.hidlist, 'enumerate', side_effect=lambda vid: rows if vid == 0x3837 else []), \
             patch.object(mchose.hid, 'device') as factory:
            self.assertEqual(mchose.MchoseProvider().poll(), [])
            factory.assert_not_called()

    def test_status_zero_and_two_proceed_but_one_and_unknown_do_not(self):
        for status in (0, 2):
            with self.subTest(status=status):
                cards, dev, _ = self._poll([
                    status_frame(status),
                    battery_frame(37, 0),
                ])
                self.assertEqual(len(cards), 1)
                self.assertEqual(dev.write.call_args_list, [call(STATUS_REQUEST), call(BATTERY_REQUEST)])
        for status in (1, 3, 255):
            with self.subTest(status=status):
                cards, dev, _ = self._poll([status_frame(status)], writes=(65,))
                self.assertEqual(cards, [])
                self.assertEqual(dev.write.call_args_list, [call(STATUS_REQUEST)])
                self._assert_reads(dev, 1)

    def test_missing_unknown_short_or_wrong_status_frames_gate_battery_query(self):
        cases = (
            ([[]], 1),
            ([status_frame(1)], 1),
            ([status_frame(3)], 1),
            ([status_frame(0, size=63)], 1),
            ([status_frame(0, size=65)], 1),
            ([status_frame(0, header=bytes((0xAA, 0xFA))), []], 2),
        )
        for reads, read_count in cases:
            with self.subTest(reads=reads):
                cards, dev, _ = self._poll(reads, writes=(65,))
                self.assertEqual(cards, [])
                self.assertEqual(dev.write.call_args_list, [call(STATUS_REQUEST)])
                self._assert_reads(dev, read_count)

    def test_battery_zero_invalid_values_and_malformed_frames_yield_no_data(self):
        cases = (
            ([status_frame(2), battery_frame(0, 0)], 2),
            ([status_frame(2), battery_frame(101, 0)], 2),
            ([status_frame(2), battery_frame(37, 2)], 2),
            ([status_frame(2), battery_frame(37, 255)], 2),
            ([status_frame(2), battery_frame(37, 0, size=63)], 2),
            ([status_frame(2), battery_frame(37, 0, size=65)], 2),
            ([status_frame(2), battery_frame(37, 0, header=bytes((0xAA, 0xFA))), []], 3),
            ([status_frame(2), []], 2),
        )
        for reads, read_count in cases:
            with self.subTest(reads=reads):
                cards, dev, _ = self._poll(reads)
                self.assertEqual(cards, [])
                self.assertEqual(dev.write.call_args_list, [call(STATUS_REQUEST), call(BATTERY_REQUEST)])
                self._assert_reads(dev, read_count)

    def test_partial_writes_are_not_retried(self):
        cards, dev, _ = self._poll([], writes=(64,))
        self.assertEqual(cards, [])
        self.assertEqual(dev.write.call_args_list, [call(STATUS_REQUEST)])
        self._assert_reads(dev, 0)

        cards, dev, _ = self._poll([status_frame(2)], writes=(65, 64))
        self.assertEqual(cards, [])
        self.assertEqual(dev.write.call_args_list, [call(STATUS_REQUEST), call(BATTERY_REQUEST)])
        self._assert_reads(dev, 1)

    def test_stale_frames_are_filtered_with_four_per_operation_cap(self):
        reads = [stale_frame() for _ in range(4)]
        reads.append(status_frame(2))
        reads.extend(stale_frame() for _ in range(4))
        reads.append(battery_frame(37, 1))
        cards, dev, _ = self._poll(reads)
        card, = cards
        self.assertEqual((card.level, card.charging), (40, True))
        self.assertEqual(dev.write.call_args_list, [call(STATUS_REQUEST), call(BATTERY_REQUEST)])
        self._assert_reads(dev, 10)

        cards, dev, _ = self._poll([stale_frame() for _ in range(5)], writes=(65,))
        self.assertEqual(cards, [])
        self.assertEqual(dev.write.call_args_list, [call(STATUS_REQUEST)])
        self._assert_reads(dev, 5)

    def test_silence_never_sends_battery_query_without_valid_status(self):
        cards, dev, _ = self._poll([[]], writes=(65,))
        self.assertEqual(cards, [])
        self.assertEqual(dev.write.call_args_list, [call(STATUS_REQUEST)])
        self._assert_reads(dev, 1)

    def test_duplicate_and_short_shapes_fail_before_hid_instance(self):
        duplicate_path = hid_path(2).replace('7&ABC', '7&DEF')
        cases = {
            'duplicate collection': [receiver(), receiver(path=duplicate_path)],
            'unrelated duplicate interface': [
                receiver(), receiver(path=hid_path(3), interface_number=3)
            ],
            'missing input map': [receiver(input_report_sizes=None)],
            'missing output map': [receiver(output_report_sizes=None)],
            'wrong input report id': [receiver(input_report_sizes={'1': 65})],
            'extra input report id': [receiver(input_report_sizes={'0': 65, '1': 65})],
            'short input report': [receiver(input_report_length=64)],
            'long output report': [receiver(output_report_length=66)],
            'wrong input size': [receiver(input_report_sizes={'0': 64})],
            'wrong output size': [receiver(output_report_sizes={'0': 64})],
        }
        for name, rows in cases.items():
            with self.subTest(name=name):
                cards, _, factory = self._poll([], rows=rows)
                self.assertEqual(cards, [])
                factory.assert_not_called()

    def test_invalid_identity_parent_and_path_shapes_never_open(self):
        long_component = PATH_PREFIX + '02#' + ('a' * 257) + '#{fixture}'
        too_many_segments = hid_path(2) + '#a#b#c#d#e#f'
        invalid = {
            'other product': receiver(product_string='MCHOSE G3 V2 Pro'),
            'other wired pid': receiver(product_id=0x418C),
            'pro pid': receiver(product_id=0x4042),
            'wrong vendor': receiver(vendor_id=0x1234),
            'wrong physical parent': receiver(
                physical_id=PHYSICAL_PREFIX + 'fixture' + chr(92) + 'extra'),
            'empty physical instance': receiver(physical_id=PHYSICAL_PREFIX),
            'ff05 sibling': receiver(usage_page=0xFF05),
            'wrong usage in ff01': receiver(usage=0x0011),
            'wrong path vendor': receiver(
                path=hid_path(2).replace('vid_3837', 'vid_1111')),
            'wrong path pid': receiver(
                path=hid_path(2).replace('pid_4245', 'pid_418c')),
            'interface index mismatch': receiver(path=hid_path(1), interface_number=2),
            'missing path tail': receiver(path=PATH_PREFIX + '02'),
            'oversized path component': receiver(path=long_component),
            'too many path components': receiver(path=too_many_segments),
            'malformed byte path': receiver(path=hid_path(2).encode('ascii') + bytes([255])),
        }
        for name, row in invalid.items():
            with self.subTest(name=name):
                self.assertFalse(MchoseG3V2Provider.matches_collection(row))
                cards, _, factory = self._poll([], rows=[row])
                self.assertEqual(cards, [])
                factory.assert_not_called()

    def test_ff05_siblings_do_not_make_valid_collection_ambiguous(self):
        rows = [
            receiver(),
            receiver(path=hid_path(3), interface_number=3, usage_page=0xFF05, usage=0x0061),
            receiver(path=hid_path(4), interface_number=4, usage=0x0011),
        ]
        cards, dev, factory = self._poll(
            [status_frame(2), battery_frame(95, 0)], rows=rows)
        card, = cards
        self.assertEqual((card.level, card.charging), (100, False))
        self.assertEqual(dev.write.call_args_list, [call(STATUS_REQUEST), call(BATTERY_REQUEST)])
        factory.assert_called_once_with()


if __name__ == '__main__':
    unittest.main()
