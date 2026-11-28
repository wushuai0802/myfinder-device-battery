from pathlib import Path
import sys
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import _hidstub  # noqa: F401
import battery_hid
from providers import asus_azoth

BS = chr(92)
AZOTH = 0x1A85
OMNI = 0x1ACE


def make(pid, **changes):
    if pid == AZOTH:
        mi, col, product, rid, length = '01', '', 'ROG AZOTH', '0', 65
    else:
        mi, col, product, rid, length = '02', '&col02', 'ROG OMNI RECEIVER', '2', 64
    parent = 'pnp:usb' + BS + 'vid_0b05&pid_{:04x}'.format(pid)
    parent += BS + '5&receiver'
    path = BS * 2 + '?' + BS + 'hid#vid_0b05&pid_{:04x}'.format(pid)
    path += '&mi_' + mi + col + '#7&receiver'
    data = {
        'vendor_id': 0x0B05,
        'product_id': pid,
        'interface_number': int(mi),
        'usage_page': 0xFF00,
        'usage': 0x0001,
        'product_string': product,
        'physical_id': parent,
        'path': path,
        'input_report_proof': 'caps',
        'output_report_proof': 'descriptor',
        'input_report_sizes': {rid: length},
        'output_report_sizes': {rid: length},
        'input_report_length': length,
        'output_report_length': length,
    }
    data.update(changes)
    return data


def reply(pid, level, charge):
    data = bytearray(64)
    if pid == AZOTH:
        data[0], data[1] = 0x12, 0x01
        level_at, charge_at = 5, 8
    else:
        data[0], data[1], data[2] = 0x02, 0x12, 0x01
        level_at, charge_at = 6, 9
    data[level_at] = level
    data[charge_at] = charge
    return data


class FakeDevice:
    def __init__(self, packets=(), short=False):
        self.packets = list(packets)
        self.short = short
        self.path = None
        self.writes = []
        self.reads = []
        self.closed = False

    def open_path(self, path):
        self.path = path

    def write(self, packet):
        self.writes.append(bytes(packet))
        return len(packet) - 1 if self.short else len(packet)

    def read(self, size, timeout):
        assert size == 64 and 0 < timeout <= 350
        self.reads.append(size)
        return list(self.packets.pop(0)) if self.packets else []

    def close(self):
        self.closed = True


def run(infos, devices=()):
    factory = Mock(side_effect=devices)
    provider = asus_azoth.AsusAzothProvider()
    with patch.object(battery_hid.hidlist, 'enumerate', return_value=infos):
        with patch.object(battery_hid.hid, 'device', factory):
            statuses = provider.poll()
    return statuses, provider, factory


def identity_port(**changes):
    row = make(OMNI, usage_page=0xFF02, input_report_sizes={"1": 64},
               output_report_sizes={"1": 64})
    row["path"] = row["path"].replace("&col02", "&col01")
    return dict(row, **changes)


def slots(*pids):
    frame = bytearray(64)
    frame[:2] = b"\x01\xa0"
    for pos, pid in enumerate(pids):
        start = 5 + pos * 4
        frame[start:start + 2] = pid.to_bytes(2, "little")
    return frame


class AsusAzothProviderTests(unittest.TestCase):
    def test_omni_matches_exact_model_before_interpreting_percentage(self):
        # rogctl captured 52% from a Falchion. Its level is at 11; byte 6
        # may be a coarse gauge on this family and must not become a percent.
        power = reply(OMNI, 5, 1)
        power[11] = 52
        ident = FakeDevice([slots(0x1B06, 0x1B65)])
        keyboard = FakeDevice([power])
        cards, _, _ = run([make(OMNI), identity_port()], [ident, keyboard])
        self.assertEqual([(c.name, c.level, c.charging) for c in cards],
                         [('ROG Falchion RX Low Profile', 52, True)])
        self.assertEqual(ident.writes, [bytes([1, 0xA0]).ljust(64, b'\0')])
        self.assertEqual(keyboard.writes, [bytes([2, 0x12, 1]).ljust(64, b'\0')])
        self.assertTrue(ident.closed and keyboard.closed)

    def test_omni_azoth_extreme_se_and_scope_rx_use_offset_6_and_unverified_charging(self):
        for model_pid, name in (
            (0x1B42, "ROG Azoth Extreme"),
            (0x1CF1, "ROG Azoth Extreme SE"),
            (0x1B7A, "ROG Strix Scope II 96 RX Wireless"),
        ):
            with self.subTest(model_pid=model_pid):
                ident = FakeDevice([slots(model_pid, 0x1C0E)])
                keyboard = FakeDevice([reply(OMNI, 84, 1)])
                cards, _, _ = run([make(OMNI), identity_port()], [ident, keyboard])
                self.assertEqual(
                    [(c.name, c.level, c.charging, c.charging_known) for c in cards],
                    [(name, 84, False, False)],
                )
                self.assertTrue(ident.closed and keyboard.closed)

    def test_unproven_identity_or_power_port_sends_nothing(self):
        for ports in ([make(OMNI)], [make(OMNI), identity_port(input_report_proof=None)],
                      [make(OMNI, input_report_sizes={"3":64}), identity_port()],
                      [make(OMNI), identity_port(), identity_port()],
                      [make(OMNI), identity_port(physical_id=identity_port()["physical_id"] + "other")]):
            with self.subTest(ports=ports):
                cards, _, factory = run(ports)
                self.assertEqual(cards, [])
                factory.assert_not_called()

    def test_unknown_identity_cannot_send_keyboard_battery_request(self):
        for frame in (slots(), slots(0x1C25), slots(0x1B06, AZOTH), slots(AZOTH, 0xFFFF), []):
            with self.subTest(frame=frame):
                ident = FakeDevice([frame])
                cards, _, factory = run([make(OMNI), identity_port()], [ident])
                self.assertEqual(cards, [])
                self.assertEqual(len(ident.writes), 1)
                self.assertEqual(factory.call_count, 1)
                self.assertTrue(ident.closed)

    def test_exact_packets_offsets_labels_and_closures(self):
        cases = (
            (AZOTH, 100, 0, False),
            (OMNI, 1, 1, True),
        )
        for pid, level, charge, as_bytes in cases:
            info = make(pid)
            if as_bytes:
                info['path'] = info['path'].encode('utf-8')
            device = FakeDevice([reply(pid, level, charge)])
            inventory, devices = [info], [device]
            if pid == OMNI:
                inventory.append(identity_port())
                devices.insert(0, FakeDevice([slots(AZOTH)]))
            statuses, _, factory = run(inventory, devices)
            with self.subTest(pid=pid):
                expected = bytearray(65 if pid == AZOTH else 64)
                expected[0] = 0 if pid == AZOTH else 2
                expected[1], expected[2] = 0x12, 0x01
                self.assertEqual(device.writes, [bytes(expected)])
                self.assertEqual(len(statuses), 1)
                self.assertEqual(statuses[0].level, level)
                self.assertEqual(statuses[0].charging, bool(charge))
                self.assertEqual(statuses[0].name, 'ROG Azoth')
                self.assertEqual(statuses[0].source, 'asus_azoth')
                self.assertTrue(device.closed)
                self.assertEqual(factory.call_count, len(devices))

    def test_identity_gate(self):
        wrong_iface = make(AZOTH, interface_number=0)
        wrong_usage = make(AZOTH, usage_page=0xFF01)
        wrong_product = make(AZOTH, product_string='ROG AZOTH EXTRA')
        wrong_parent = make(AZOTH)
        wrong_parent['physical_id'] = make(OMNI)['physical_id']
        wrong_mi = make(AZOTH)
        wrong_mi['path'] = wrong_mi['path'].replace('&mi_01', '&mi_02')
        col03 = make(OMNI)
        col03['path'] = col03['path'].replace('&col02', '&col03')
        id3 = make(OMNI, input_report_sizes={'3': 64}, output_report_sizes={'3': 64})
        bad_bytes = make(AZOTH, path=bytes((255,)))
        suffix = make(OMNI)
        suffix['path'] = make(AZOTH)['path'].replace('pid_1a85', 'pid_1ace')
        suffix['path'] += '&mi_02&col02'
        rows = (
            wrong_iface, wrong_usage, wrong_product, wrong_parent,
            wrong_mi, col03, id3, bad_bytes, suffix,
        )
        for row in rows:
            statuses, _, factory = run([row])
            self.assertEqual(statuses, [])
            factory.assert_not_called()

    def test_report_evidence_gate(self):
        trimmed = make(AZOTH, input_report_proof='caps ')
        missing = make(AZOTH, output_report_proof=None)
        bad_map = make(AZOTH, input_report_sizes={'0': 64})
        extra = make(AZOTH, output_report_sizes={'0': 65, '1': 64})
        bad_limit = make(AZOTH, output_report_length=64)
        bad_proof = make(AZOTH, output_report_proof='guess')
        for row in (trimmed, missing, bad_map, extra, bad_limit, bad_proof):
            statuses, _, factory = run([row])
            self.assertEqual(statuses, [])
            factory.assert_not_called()

    def test_duplicate_routes_and_multiple_parents(self):
        statuses, _, factory = run([make(AZOTH), make(AZOTH)])
        self.assertEqual(statuses, [])
        factory.assert_not_called()

        first = make(AZOTH)
        second = make(AZOTH)
        parent = 'pnp:usb' + BS + 'vid_0b05&pid_1a85' + BS
        first['physical_id'] = parent + '5&a'
        second['physical_id'] = parent + '5&b'
        first['path'] = first['path'].replace('7&receiver', '7&a')
        second['path'] = second['path'].replace('7&receiver', '7&b')
        devices = [FakeDevice([reply(AZOTH, 1, 0)]), FakeDevice([reply(AZOTH, 50, 1)])]
        statuses, _, factory = run([first, second], devices)
        self.assertEqual(sorted(item.level for item in statuses), [1, 50])
        self.assertEqual(factory.call_count, 2)
        self.assertTrue(all(device.closed for device in devices))

    def test_stale_reads_and_read_limit(self):
        device = FakeDevice([bytes(64), reply(AZOTH, 73, 1)])
        statuses, _, _ = run([make(AZOTH)], [device])
        self.assertEqual(statuses[0].level, 73)
        self.assertEqual(device.reads, [64, 64])

        device = FakeDevice([bytes(64) for _ in range(6)])
        statuses, _, _ = run([make(AZOTH)], [device])
        self.assertEqual(statuses, [])
        self.assertEqual(len(device.reads), 4)
        self.assertTrue(device.closed)

    def test_malformed_missing_and_invalid_values(self):
        cases = {
            'missing': [],
            'short': bytes(reply(AZOTH, 50, 0))[:-1],
            'zero': reply(AZOTH, 0, 0),
            'high': reply(AZOTH, 101, 0),
            'charge': reply(AZOTH, 50, 2),
            'echo': bytearray(64),
        }
        for name, packet in cases.items():
            device = FakeDevice([packet])
            statuses, _, _ = run([make(AZOTH)], [device])
            with self.subTest(name=name):
                self.assertEqual(statuses, [])
                self.assertEqual(len(device.writes), 1)
                self.assertTrue(device.closed)

    def test_short_write_never_reads(self):
        device = FakeDevice([reply(AZOTH, 42, 0)], short=True)
        statuses, _, _ = run([make(AZOTH)], [device])
        self.assertEqual(statuses, [])
        self.assertEqual(len(device.writes), 1)
        self.assertEqual(device.reads, [])
        self.assertTrue(device.closed)

    def test_matches_collection_is_identity_only(self):
        info = make(AZOTH)
        info['input_report_proof'] = 'bad'
        info['input_report_sizes'] = {'wrong': 1}
        self.assertEqual(asus_azoth.collection_role(info), 'direct')
        self.assertEqual(asus_azoth.VID, 0x0B05)
        self.assertEqual(asus_azoth.PIDS[AZOTH], 'ROG Azoth')
        self.assertEqual(asus_azoth.PIDS[OMNI], 'ROG OMNI 接收器（键盘）')


    def test_read_echo_one_write_skips_wrong_header_and_bounds_reads(self):
        packet = b'q' * 64
        good = b'\x01\xa0' + b'x' * 62
        dev = Mock()
        dev.write.return_value = len(packet)
        dev.read.side_effect = [b'\x09' + b'x' * 63, good]
        self.assertEqual(asus_azoth.read_echo(dev, packet, b'\x01\xa0'), good)
        dev.write.assert_called_once_with(packet)
        self.assertEqual(dev.read.call_count, 2)
        for call in dev.read.call_args_list:
            self.assertEqual(call.args[0], 64)
            self.assertGreaterEqual(call.args[1], 1)
            self.assertLessEqual(call.args[1], 350)

    def test_read_echo_rejections_and_malformed_lengths_stop(self):
        packet = b'q' * 64
        responses = (b'\x02\xff\xaa' + b'x' * 61, b'\x01\xff\xaa' + b'x' * 61, b'', b'x' * 63)
        for response in responses:
            dev = Mock()
            dev.write.return_value = len(packet)
            dev.read.return_value = response
            self.assertIsNone(asus_azoth.read_echo(dev, packet, b'\x01\xa0'))
            dev.write.assert_called_once_with(packet)
            self.assertEqual(dev.read.call_count, 1)

    def test_read_echo_skips_at_most_four_stale_reads(self):
        packet = b'q' * 64
        dev = Mock()
        dev.write.return_value = len(packet)
        dev.read.side_effect = [b'\x09' + b'x' * 63] * 4
        self.assertIsNone(asus_azoth.read_echo(dev, packet, b'\x01\xa0'))
        dev.write.assert_called_once_with(packet)
        self.assertEqual(dev.read.call_count, 4)

    def test_read_echo_write_error_read_error_and_short_write(self):
        packet = b'q' * 64
        dev = Mock()
        dev.write.side_effect = OSError('write')
        self.assertIsNone(asus_azoth.read_echo(dev, packet, b'\x01\xa0'))
        dev.write.assert_called_once_with(packet)
        dev.read.assert_not_called()

        dev = Mock()
        dev.write.return_value = len(packet) - 1
        self.assertIsNone(asus_azoth.read_echo(dev, packet, b'\x01\xa0'))
        dev.write.assert_called_once_with(packet)
        dev.read.assert_not_called()

        dev = Mock()
        dev.write.return_value = len(packet)
        dev.read.side_effect = OSError('read')
        self.assertIsNone(asus_azoth.read_echo(dev, packet, b'\x01\xa0'))
        dev.write.assert_called_once_with(packet)
        self.assertEqual(dev.read.call_count, 1)

    def test_read_echo_timeout_returns_without_read(self):
        from unittest.mock import patch
        packet = b'q' * 64
        dev = Mock()
        dev.write.return_value = len(packet)
        with patch.object(asus_azoth.time, 'monotonic', side_effect=[10.0, 10.4]):
            self.assertIsNone(asus_azoth.read_echo(dev, packet, b'\x01\xa0'))
        dev.write.assert_called_once_with(packet)
        dev.read.assert_not_called()

    def test_omni_known_falchion_capture(self):
        frame = bytes([0x01, 0xA0, 0x00, 0x01, 0x00, 0x06, 0x1B, 0x02, 0x04]) + bytes(55)
        self.assertEqual(asus_azoth.omni_keyboard_pid(frame), 0x1B06)

    def test_omni_keyboard_with_mouse_in_either_order(self):
        def frame(pids):
            result = bytearray(64)
            result[0:2] = b'\x01\xa0'
            result[3] = 0xFF
            for index, pid in enumerate(pids):
                start = 5 + 4 * index
                result[start:start + 2] = pid.to_bytes(2, 'little')
            return bytes(result)

        for pids in ((0x1A85, 0x1C0E), (0x1C0E, 0x1A85)):
            self.assertEqual(asus_azoth.omni_keyboard_pid(frame(pids)), 0x1A85)

    def test_omni_rejects_unknown_duplicate_and_wrong_record_sets(self):
        def frame(pids):
            result = bytearray(64)
            result[0:2] = b'\x01\xa0'
            for index, pid in enumerate(pids):
                start = 5 + 4 * index
                result[start:start + 2] = pid.to_bytes(2, 'little')
            return bytes(result)

        for pids in ((0x9999,), (0x1A85, 0x9999), (0x1A85, 0x1A85), (0x1C0E, 0x1C0E), (0x1C0E, 0x1A94), (0x1A85, 0x1B06), (0x1A85, 0x1C0E, 0x1A94)):
            self.assertIsNone(asus_azoth.omni_keyboard_pid(frame(pids)))

    def test_omni_rejects_bad_header_length_and_no_terminator(self):
        frame = bytearray(64)
        frame[0:2] = b'\x01\xa0'
        frame[5:7] = (0x1B06).to_bytes(2, 'little')
        self.assertIsNone(asus_azoth.omni_keyboard_pid(b''))

        bad = bytearray(frame)
        bad[1] = 0xA1
        self.assertIsNone(asus_azoth.omni_keyboard_pid(bytes(bad)))
        self.assertIsNone(asus_azoth.omni_keyboard_pid(bytes(frame[:63])))
        self.assertIsNone(asus_azoth.omni_keyboard_pid(bytes([0x01, 0xA0]) + bytes([0xFF]) * 62))

        empty_records = bytearray(64)
        empty_records[0:2] = b'\x01\xa0'
        self.assertIsNone(asus_azoth.omni_keyboard_pid(bytes(empty_records)))


if __name__ == '__main__':
    unittest.main()
