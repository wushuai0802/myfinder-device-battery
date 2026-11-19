import sys
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / 'tests'))
import _hidstub
from providers import razer, razer_v4 as v4

SEP = '\\'


def info(pid=0x00E6, physical=None, **extra):
    path = f'{SEP}{SEP}?{SEP}hid#vid_1532&pid_{pid:04x}&mi_03#8&fake#{{x}}'.encode()
    data = {
        'vendor_id': 0x1532, 'product_id': pid, 'interface_number': 3,
        'usage_page': 0x000C, 'usage': 1, 'path': path,
        'physical_id': physical or f'pnp:usb{SEP}vid_1532&pid_{pid:04x}{SEP}5&fake&0&1',
        'feature_report_length': 91, 'feature_report_sizes': {'0': 91},
    }
    data.update(extra)
    return data


def reply(raw, tid=0x1F, status=2):
    data = bytearray(91)
    data[1] = status
    data[2] = tid
    data[6:9] = bytes.fromhex("02 07 80")
    data[10] = raw
    return data


class Device:
    def __init__(self, replies=(), sent=91, error=None, open_error=None):
        self.replies = replies
        self.sent = sent
        self.error = error
        self.open_error = open_error
        self.path = None
        self.sent_data = None
        self.gets = 0
        self.closed = False

    def open_path(self, path):
        if self.open_error:
            raise self.open_error
        self.path = path

    def send_feature_report(self, data):
        self.sent_data = data
        return self.sent

    def get_feature_report(self, report_id, length):
        self.gets += 1
        if self.error:
            raise self.error
        return self.replies[(self.gets - 1) % len(self.replies)]

    def close(self):
        self.closed = True


class RazerV4Tests(unittest.TestCase):
    def test_success_for_both_pids(self):
        for pid in v4.PIDS:
            with self.subTest(pid=pid):
                fake = Device([reply(184)])
                devices = [info(pid=pid)]
                physical = devices[0]['physical_id']
                with patch.object(v4.hid, 'device', return_value=fake), patch.object(v4.time, 'sleep') as sleep:
                    with patch.object(razer.hidlist, 'enumerate', return_value=devices):
                        card, = razer.RazerProvider().poll()
                self.assertEqual(card.key, f'razer:{pid:04x}:{physical}')
                self.assertEqual((card.name, card.level, card.online), ('Razer Viper V4 Pro', 72, True))
                self.assertEqual((card.source, card.kind, card.physical_id), ('razer', 'mouse', physical))
                self.assertEqual((card.charging_known, card.verification_level), (False, 'C'))
                self.assertEqual(fake.sent_data, bytes(1) + razer.build_request(0x1F, 7, 0x80))
                self.assertEqual(len(fake.sent_data), 91)
                self.assertEqual(fake.path, devices[0]['path'])
                self.assertEqual(fake.gets, 1)
                self.assertTrue(fake.closed)
                sleep.assert_called_once_with(0.065)

    def test_invalid_and_ambiguous_never_open(self):
        missing_parent = info()
        missing_parent.pop('physical_id')
        wrong_parent = info(physical=f'pnp:usb{SEP}vid_1532&pid_00e5{SEP}5&fake&0&1')
        wrong_path = info(path=f'{SEP}{SEP}?{SEP}hid#vid_1532&pid_00e5&mi_03#8&fake#{{x}}'.encode())
        collection = info(path=f'{SEP}{SEP}?{SEP}hid#vid_1532&pid_00e6&mi_03&col01#8&fake#{{x}}'.encode())
        cases = {
            'missing parent': [missing_parent],
            'wrong identity': [info(usage=2)],
            'unknown pid': [info(pid=0x00E4)],
            'short endpoint': [info(feature_report_length=90)],
            'wrong sizes': [info(feature_report_sizes={'0': 90})],
            'borrowed parent': [wrong_parent],
            'cross parent': [wrong_path],
            'collection suffix': [collection],
            'duplicate': [info(), info()],
            'duplicate before filter': [info(), info(feature_report_length=90)],
        }
        for name, devices in cases.items():
            with self.subTest(name), patch.object(v4.hid, 'device') as factory:
                self.assertEqual(v4.poll_devices(devices, razer.build_request), [])
                factory.assert_not_called()

    def test_foreign_zero_and_truncated_retry(self):
        cases = {
            'foreign': [reply(10, tid=0x20), reply(10, status=3)],
            'zero': [reply(0)],
            'truncated': [bytes(90)],
        }
        for name, replies in cases.items():
            with self.subTest(name):
                fake = Device(replies)
                with patch.object(v4.hid, 'device', return_value=fake), patch.object(v4.time, 'sleep') as sleep:
                    self.assertEqual(v4.poll_devices([info()], razer.build_request), [])
                self.assertEqual(fake.gets, 3)
                self.assertTrue(fake.closed)
                self.assertEqual(sleep.call_count, 3)

    def test_short_set_and_errors_close(self):
        cases = (
            ('short set', {'sent': 90}, 0),
            ('get error', {'replies': [reply(184)], 'error': OSError('gone')}, 1),
            ('open error', {'replies': [reply(184)], 'open_error': OSError('gone')}, 0),
        )
        for name, kwargs, gets in cases:
            with self.subTest(name):
                fake = Device(**kwargs)
                with patch.object(v4.hid, 'device', return_value=fake), patch.object(v4.time, 'sleep'):
                    self.assertEqual(v4.poll_devices([info()], razer.build_request), [])
                self.assertTrue(fake.closed)
                self.assertEqual(fake.gets, gets)


if __name__ == '__main__':
    unittest.main()
