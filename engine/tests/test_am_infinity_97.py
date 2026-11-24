"""Synthetic AM Infinity .97 fixtures; no device or network access."""
import ast
from contextlib import contextmanager
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ENGINE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ENGINE))
import _hidstub  # noqa: E402,F401
import catalog  # noqa: E402
import known_ids  # noqa: E402
from providers import am_infinity_97 as A  # noqa: E402

P = A.AmInfinity97Provider
PATH = rb"\\?\hid#vid_0e8d&pid_0703&mi_02&col01#synthetic"
PHYSICAL = r"pnp:usb\vid_0e8d&pid_0703\synthetic"
REQUEST = bytes([6, 6, 0x80, 5, 0x5A, 2, 0, 0xCF, 0x30]) + bytes(53)


def entry(**changes):
    info = dict(
        vendor_id=0x0E8D, product_id=0x0703, product_string="AM Infinity .97",
        usage_page=0xFF13, usage=1, interface_number=2,
        path=PATH, physical_id=PHYSICAL,
        input_report_length=62, output_report_length=62,
        input_report_sizes={"7": 62}, output_report_sizes={"6": 62},
    )
    info.update(changes)
    return info


def answer(percent=73, charging=0, exists=1, health=95, channel=5, kind=0x5D,
           command=(0xCF, 0x30)):
    return bytes([channel, kind, 7, 0, *command, 0, charging, percent, health, exists])


ACK = bytes([5, 0x5B, 3, 0, 0xCF, 0x30, 0])


def report(payload=b"", report_id=7, length=None, size=62):
    length = len(payload) if length is None else length
    raw = bytes([report_id, length & 255, length >> 8]) + bytes(payload)
    return list(raw + bytes(size - len(raw)))


class Clock:
    def __init__(self, step=0.0001):
        self.value = 0
        self.step = step

    def monotonic(self):
        self.value += self.step
        return self.value


class FakeHid:
    def __init__(self, replies=(), queued=(), write_result=62, fail=None,
                 endless_queue=False, input_reports=(), no_input_report=False):
        self.replies = list(replies)
        self.queued = list(queued)
        self.input_reports = list(input_reports)
        self.no_input_report = no_input_report
        self.write_result = write_result
        self.fail = fail
        self.endless_queue = endless_queue
        self.opened, self.closed, self.writes, self.reads, self.gets = [], [], [], [], []

    def device(self):
        bus = self

        class Handle:
            def open_path(self, path):
                self.path = path
                self.submitted = False
                bus.opened.append(path)
                if bus.fail == "open":
                    raise OSError("private-path")

            def write(self, packet):
                if type(packet) is not bytes or packet != REQUEST:
                    raise AssertionError("output outside the fixed 62-byte whitelist")
                bus.writes.append((self.path, packet))
                self.submitted = True
                if bus.fail == "write":
                    raise OSError("private-path")
                return bus.write_result

            def read(self, length, timeout):
                if length != 62 or not 1 <= timeout <= max(A.READ_GAP_MS, 1):
                    raise AssertionError("unbounded or unexpected read")
                bus.reads.append((length, timeout))
                phase = "read" if self.submitted else "drain"
                if bus.fail == phase:
                    raise OSError("private-path")
                queue = bus.replies if self.submitted else bus.queued
                if queue:
                    return queue.pop(0)
                if not self.submitted and bus.endless_queue:
                    return [0] * 62
                return []

            def get_input_report(self, report_id, length):
                if (report_id, length) != (7, 62) or not self.submitted:
                    raise AssertionError("unexpected get_input_report")
                bus.gets.append((report_id, length))
                if bus.no_input_report:
                    raise OSError("unsupported")
                return bus.input_reports.pop(0) if bus.input_reports else []

            def send_feature_report(self, *args):
                raise AssertionError("Feature output forbidden")

            def get_feature_report(self, *args):
                raise AssertionError("Feature input forbidden")

            def set_nonblocking(self, *args):
                raise AssertionError("configuration forbidden")

            def close(self):
                bus.closed.append(self.path)

        return Handle()


@contextmanager
def fixture(replies=(), inventory=None, clock_step=0.0001, provider=None, **kwargs):
    bus = FakeHid(replies, **kwargs)
    clock = Clock(clock_step)
    with patch.object(A, "hid", bus), patch.object(
        A.hidlist, "enumerate",
        return_value=[entry()] if inventory is None else inventory,
    ) as enumeration, patch.object(
        A, "time", SimpleNamespace(monotonic=clock.monotonic),
    ):
        yield bus
        enumeration.assert_called_once_with(0x0E8D)


class ProtocolTests(unittest.TestCase):
    def test_fixed_request_bytes(self):
        request = A.make_request()
        self.assertEqual(request, REQUEST)
        self.assertEqual(len(request), 62)
        self.assertEqual(request[:9].hex(), "060680055a0200cf30")

    def test_report_payload_validation(self):
        self.assertEqual(A.report_payload(report(b"\x01\x02")), b"\x01\x02")
        self.assertEqual(A.report_payload(report(bytes(59))), bytes(59))
        self.assertIsNone(A.report_payload(report(b"\x01", report_id=6)))
        self.assertIsNone(A.report_payload(report(b"\x01", report_id=0)))
        self.assertIsNone(A.report_payload(report(b"", length=0)))
        self.assertIsNone(A.report_payload(report(b"\x01", length=60)))
        self.assertIsNone(A.report_payload(report(b"\x01", length=0x100)))
        self.assertIsNone(A.report_payload(report(b"\x01", size=61)))
        self.assertIsNone(A.report_payload(report(b"\x01", size=63)))
        self.assertIsNone(A.report_payload([]))
        self.assertIsNone(A.report_payload(None))
        self.assertIsNone(A.report_payload([7, 1, 0] + [300] + [0] * 58))

    def test_frame_sync_drops_leading_noise(self):
        buffer = bytearray(b"\x00\xff\x05\x00" + answer(50))
        self.assertEqual(A.pop_frame(buffer), answer(50))
        self.assertEqual(buffer, bytearray())

    def test_incomplete_frame_waits(self):
        data = answer(50)
        buffer = bytearray(data[:6])
        self.assertIsNone(A.pop_frame(buffer))
        self.assertEqual(bytes(buffer), data[:6])
        buffer += data[6:]
        self.assertEqual(A.pop_frame(buffer), data)

    def test_implausible_lengths_resynchronise(self):
        buffer = bytearray(b"\x05\x5d\x01\x00" + answer(50))
        self.assertEqual(A.pop_frame(buffer), answer(50))
        buffer = bytearray(b"\x05\x5d\xff\xff" + answer(50))
        self.assertEqual(A.pop_frame(buffer), answer(50))

    def test_parse_response_matrix(self):
        for percent in (0, 1, 73, 100):
            for charging in (0, 1):
                self.assertEqual(A.parse_response(answer(percent, charging)),
                                 (percent, charging == 1, True))
        self.assertIsNone(A.parse_response(answer(101)))
        self.assertIsNone(A.parse_response(answer(255)))
        self.assertIsNone(A.parse_response(answer(50, charging=2)))
        self.assertIsNone(A.parse_response(answer(50, charging=255)))
        self.assertIsNone(A.parse_response(answer(50, command=(0xCF, 0x31))))
        self.assertIsNone(A.parse_response(answer(50, command=(0x30, 0xCF))))
        self.assertIsNone(A.parse_response(ACK))
        self.assertIsNone(A.parse_response(answer(50)[:10]))
        self.assertEqual(A.parse_response(answer(50, exists=7)), (50, False, True))

    def test_absent_mouse_never_yields_a_level(self):
        for percent in (0, 50, 101):
            for charging in (0, 1, 2):
                self.assertEqual(A.parse_response(answer(percent, charging, exists=0)),
                                 (None, False, False))

    def test_ack_detection_is_exact(self):
        self.assertTrue(A.is_ack(ACK))
        self.assertFalse(A.is_ack(bytes([5, 0x5D, 3, 0, 0xCF, 0x30, 0])))
        self.assertFalse(A.is_ack(answer(50)))


class ReaderTests(unittest.TestCase):
    def assert_no_io(self, bus):
        self.assertEqual(bus.opened, [])
        self.assertEqual(bus.writes, [])
        self.assertEqual(bus.reads, [])
        self.assertEqual(bus.gets, [])

    def test_read_with_ack_first_and_boundaries(self):
        for level in (0, 73, 100):
            for charging in (0, 1):
                with self.subTest(level=level, charging=charging):
                    replies = [report(ACK), report(answer(level, charging))]
                    with fixture(replies) as bus:
                        reading, = P().poll()
                    self.assertEqual(
                        (reading.level, reading.charging, reading.online),
                        (level, charging == 1, True))
                    self.assertEqual((reading.kind, reading.source), ("mouse", "am_infinity_97"))
                    self.assertTrue(reading.charging_known)
                    self.assertTrue(reading.connection_known)
                    self.assertEqual(reading.verification_level, "C")
                    self.assertTrue(reading.unconfirmed)
                    self.assertEqual(reading.physical_id, PHYSICAL)
                    self.assertEqual(reading.name, "Angry Miao AM Infinity .97")
                    self.assertEqual(bus.writes, [(PATH, REQUEST)])
                    self.assertEqual(bus.opened, [PATH])
                    self.assertEqual(bus.closed, [PATH])

    def test_ack_and_answer_in_one_report_and_alternate_channel(self):
        with fixture([report(ACK + answer(61, 1, channel=0x15, kind=0x5B))]) as bus:
            reading, = P().poll()
        self.assertEqual((reading.level, reading.charging), (61, True))
        self.assertEqual(len(bus.writes), 1)

    def test_fragmented_answer_is_reassembled(self):
        frame = ACK + answer(42, 1)
        replies = [report(frame[:5]), report(frame[5:12]), report(frame[12:])]
        with fixture(replies):
            reading, = P().poll()
        self.assertEqual((reading.level, reading.charging), (42, True))

    def test_noise_and_unrelated_frames_are_skipped(self):
        other = bytes([5, 0x5D, 4, 0, 0x01, 0x02, 0x03, 0x04])
        replies = [report(b"\x00\x00\x00"), report(report_id=6, payload=b"x"),
                   report(other), report(answer(33, command=(0xCF, 0x31))),
                   report(answer(64))]
        with fixture(replies):
            reading, = P().poll()
        self.assertEqual(reading.level, 64)

    def test_wrong_report_id_wrong_channel_and_wrong_race_type_ignored(self):
        replies = [report(answer(10), report_id=6), report(answer(20), report_id=8),
                   report(answer(30, channel=0x06)), report(answer(40, kind=0x5C))]
        with fixture(replies):
            self.assertEqual(P().poll(), [])

    def test_wrong_command_id_ignored(self):
        with fixture([report(answer(70, command=(0xCE, 0x30)))]):
            self.assertEqual(P().poll(), [])

    def test_invalid_answers_yield_no_reading_then_valid_one_wins(self):
        replies = [report(answer(101)), report(answer(50, charging=2)),
                   report(answer(66, charging=1))]
        with fixture(replies):
            provider = P()
            reading, = provider.poll()
        self.assertEqual((reading.level, reading.charging), (66, True))
        self.assertTrue(any("rejected" in line for line in provider.diagnostics()))

    def test_only_invalid_answers_give_no_reading(self):
        for bad in (answer(101), answer(50, charging=2), answer(50)[:10]):
            with self.subTest(bad=bad):
                with fixture([report(bad)]):
                    self.assertEqual(P().poll(), [])

    def test_absent_mouse_publishes_nothing(self):
        with fixture([report(ACK), report(answer(0, exists=0))]) as bus:
            provider = P()
            self.assertEqual(provider.poll(), [])
        self.assertTrue(any("no mouse" in line for line in provider.diagnostics()))
        self.assertEqual(len(bus.writes), 1)

    def test_absent_mouse_with_nonzero_level_still_publishes_nothing(self):
        with fixture([report(answer(88, exists=0))]):
            self.assertEqual(P().poll(), [])

    def test_silence_is_bounded_and_publishes_nothing(self):
        with fixture([]) as bus:
            provider = P()
            self.assertEqual(provider.poll(), [])
        self.assertLessEqual(len(bus.reads), 16 + A.MAX_READS + 1)
        self.assertLessEqual(len(bus.gets), A.MAX_READS)
        self.assertEqual(len(bus.writes), 1)
        self.assertTrue(any("no valid battery answer" in x for x in provider.diagnostics()))

    def test_time_deadline_bounds_reads(self):
        with fixture([], clock_step=0.5) as bus:
            self.assertEqual(P().poll(), [])
        self.assertLessEqual(len(bus.reads) + len(bus.gets), 4)

    def test_get_input_report_fallback(self):
        with fixture([], input_reports=[report(ACK), report(answer(58, 1))]) as bus:
            reading, = P().poll()
        self.assertEqual((reading.level, reading.charging), (58, True))
        self.assertEqual(bus.gets, [(7, 62)] * 2)

    def test_get_input_report_failure_is_not_fatal(self):
        with fixture([report(answer(59))], no_input_report=True):
            reading, = P().poll()
        self.assertEqual(reading.level, 59)
        with fixture([], no_input_report=True):
            self.assertEqual(P().poll(), [])

    def test_stale_queue_is_drained_before_the_single_write(self):
        stale = [report(answer(11)), report(answer(12))]
        with fixture([report(answer(77))], queued=stale) as bus:
            reading, = P().poll()
        self.assertEqual(reading.level, 77)
        self.assertEqual(bus.queued, [])
        self.assertEqual(len(bus.writes), 1)

    def test_endless_queue_means_no_send(self):
        with fixture([report(answer(50))], endless_queue=True) as bus:
            provider = P()
            self.assertEqual(provider.poll(), [])
        self.assertEqual(bus.writes, [])
        self.assertLessEqual(len(bus.reads), A.MAX_DRAIN_READS)
        self.assertTrue(any("did not drain" in x for x in provider.diagnostics()))

    def test_write_failures_do_not_retry(self):
        for kwargs in (dict(write_result=61), dict(write_result=-1), dict(write_result=True),
                       dict(write_result=None), dict(fail="write")):
            with self.subTest(kwargs=kwargs):
                with fixture([report(answer(50))], **kwargs) as bus:
                    self.assertEqual(P().poll(), [])
                self.assertEqual(len(bus.writes), 1)
                self.assertEqual(bus.closed, [PATH])

    def test_open_and_read_failures_close_and_never_leak_paths(self):
        for phase in ("open", "drain", "read"):
            with self.subTest(phase=phase):
                provider = P()
                with fixture([report(answer(50))], fail=phase) as bus:
                    self.assertEqual(provider.poll(), [])
                self.assertNotIn("private-path", " ".join(provider.diagnostics()))
                self.assertEqual(len(bus.closed), 1)

    def test_rx_stream_is_bounded(self):
        junk = [report(bytes([5, 0x5D, 0xF0, 0x0F]) + bytes(55)) for _ in range(40)]
        with fixture(junk + [report(answer(45))], clock_step=0.00001) as bus:
            provider = P()
            result = provider.poll()
        self.assertLessEqual(len(bus.reads), 16 + A.MAX_READS + 1)
        self.assertIsInstance(result, list)

    def test_only_target_collection_is_opened(self):
        others = [entry(usage_page=0xFF14, path=rb"\\?\hid#vid_0e8d&pid_0703&mi_03#x"),
                  entry(usage_page=0xFF15, path=rb"\\?\hid#vid_0e8d&pid_0703&mi_04#y")]
        with fixture([report(answer(80))], inventory=others + [entry()]) as bus:
            reading, = P().poll()
        self.assertEqual(reading.level, 80)
        self.assertEqual(bus.opened, [PATH])

    def test_only_other_collections_means_no_io(self):
        others = [entry(usage_page=0xFF14), entry(usage_page=0xFF15),
                  entry(usage_page=0xFF13, usage=2), entry(usage_page=0xFFFF)]
        with fixture(inventory=others) as bus:
            self.assertEqual(P().poll(), [])
        self.assert_no_io(bus)

    def test_foreign_ids_are_ignored(self):
        foreign = [entry(vendor_id=0x3151), entry(product_id=0x0704),
                   entry(vendor_id=0x0E8D, product_id=0x0702)]
        with fixture(inventory=foreign) as bus:
            self.assertEqual(P().poll(), [])
        self.assert_no_io(bus)


class FailClosedTests(unittest.TestCase):
    def assert_no_io(self, inventory):
        with fixture(inventory=inventory) as bus:
            self.assertEqual(P().poll(), [])
        self.assertEqual((bus.opened, bus.writes, bus.reads, bus.gets), ([], [], [], []))

    def test_product_string_must_match_exactly_after_trim(self):
        with fixture([report(answer(55))]):
            reading, = P().poll()
        self.assertEqual(reading.level, 55)
        with fixture([report(answer(56))], inventory=[entry(product_string="  AM Infinity .97 ")]):
            self.assertEqual(P().poll()[0].level, 56)
        for value in ("AM Infinity .97 Pro", "AM Infinity 8K", "am infinity .97",
                      "", None, 97, b"AM Infinity .97"):
            with self.subTest(value=value):
                self.assert_no_io([entry(product_string=value)])
        self.assert_no_io([{k: v for k, v in entry().items() if k != "product_string"}])

    def test_unresolved_or_foreign_parent_means_no_io(self):
        for physical in (None, "", "collection:abc", r"pnp:usb\vid_0e8d&pid_0704\x",
                         r"pnp:usb\vid_3151&pid_0703\x", r"pnp:usb\vid_0e8d&pid_0703\x\y",
                         r"usb\vid_0e8d&pid_0703\x", r"pnp:bth\vid_0e8d&pid_0703\x", 5):
            with self.subTest(physical=physical):
                self.assert_no_io([entry(physical_id=physical)])

    def test_unresolved_target_blocks_even_with_resolved_sibling(self):
        self.assert_no_io([entry(), entry(physical_id="", path=rb"\\?\hid#vid_0e8d&pid_0703&mi_05#z")])

    def test_duplicate_target_collections_block_that_parent(self):
        second = entry(path=rb"\\?\hid#vid_0e8d&pid_0703&mi_09#dup")
        self.assert_no_io([entry(), second])
        # even when the duplicate would fail the product string or report proof
        self.assert_no_io([entry(), entry(product_string="Other", path=b"vid_0e8d&pid_0703#a")])
        self.assert_no_io([entry(), entry(input_report_sizes={}, path=b"vid_0e8d&pid_0703#b")])

    def test_second_receiver_is_independent(self):
        other_parent = r"pnp:usb\vid_0e8d&pid_0703\second"
        other_path = rb"\\?\hid#vid_0e8d&pid_0703&mi_02&col01#second"
        inventory = [entry(), entry(), entry(physical_id=other_parent, path=other_path)]
        with fixture([report(answer(44))], inventory=inventory) as bus:
            reading, = P().poll()
        self.assertEqual(reading.physical_id, other_parent)
        self.assertEqual(bus.opened, [other_path])

    def test_two_unique_receivers_each_get_one_write(self):
        second = entry(physical_id=r"pnp:usb\vid_0e8d&pid_0703\second",
                       path=rb"\\?\hid#vid_0e8d&pid_0703&mi_02&col01#second")
        with fixture([report(answer(21)), report(answer(22, 1))], inventory=[entry(), second]) as bus:
            first, other = P().poll()
        self.assertEqual((first.level, other.level), (21, 22))
        self.assertNotEqual(first.key, other.key)
        self.assertEqual(len(bus.writes), 2)

    def test_report_proof_is_required(self):
        bad = [
            dict(input_report_length=None), dict(output_report_length=None),
            dict(input_report_length=64), dict(output_report_length=64),
            dict(input_report_length=61), dict(output_report_length=True),
            dict(input_report_sizes=None), dict(output_report_sizes=None),
            dict(input_report_sizes={}), dict(output_report_sizes={}),
            dict(input_report_sizes={"6": 62}), dict(output_report_sizes={"7": 62}),
            dict(input_report_sizes={"7": 61}), dict(output_report_sizes={"6": 63}),
            dict(input_report_sizes={"7": "62"}), dict(output_report_sizes={"6": True}),
            dict(input_report_sizes=[("7", 62)]),
            dict(input_report_sizes={"0": 62}), dict(output_report_sizes={"0": 62}),
        ]
        for changes in bad:
            with self.subTest(changes=changes):
                self.assert_no_io([entry(**changes)])
        missing = entry()
        for key in ("input_report_length", "output_report_length",
                    "input_report_sizes", "output_report_sizes"):
            with self.subTest(missing=key):
                self.assert_no_io([{k: v for k, v in missing.items() if k != key}])

    def test_path_must_be_text_naming_the_identity(self):
        for path in (None, b"", "", 5, b"\\\\?\\hid#vid_3151&pid_0703#x",
                     b"\\\\?\\hid#vid_0e8d&pid_0704#x", b"vid_0e8d&pid_0703\x00"):
            with self.subTest(path=path):
                self.assert_no_io([entry(path=path)])

    def test_extra_report_ids_do_not_block_proven_ones(self):
        with fixture([report(answer(52))],
                     inventory=[entry(input_report_sizes={"7": 62, "1": 8},
                                      output_report_sizes={"6": 62, "2": 8})]):
            self.assertEqual(P().poll()[0].level, 52)

    def test_hid_error_during_enumeration_is_quiet(self):
        provider = P()
        with patch.object(A, "hid", FakeHid()), patch.object(
                A.hidlist, "enumerate", side_effect=OSError("boom")):
            self.assertEqual(provider.poll(), [])
        self.assertTrue(provider.diagnostics())


class HistoryTests(unittest.TestCase):
    def poll(self, provider, replies=(), inventory=None, **kwargs):
        with fixture(replies, inventory=inventory, **kwargs):
            return provider.poll()

    def test_sleep_keeps_cached_reading_while_receiver_stays(self):
        provider = P()
        fresh, = self.poll(provider, [report(answer(64, 1))])
        self.assertTrue(fresh.online and not fresh.cached)
        for replies in ([], [report(answer(0, exists=0))], [report(answer(101))]):
            old, = self.poll(provider, replies)
            self.assertEqual(old.level, 64)
            self.assertTrue(old.cached)
            self.assertFalse(old.online)
            self.assertFalse(old.connection_known)
            self.assertFalse(old.charging_known)
            self.assertTrue(old.wake_on_input)
            self.assertEqual(old.key, fresh.key)
        again, = self.poll(provider, [report(answer(63))])
        self.assertTrue(again.online and not again.cached)
        self.assertEqual(again.level, 63)
        self.assertFalse(fresh.cached)           # the original object is untouched

    def test_history_is_kept_through_zero_io_gates(self):
        provider = P()
        self.poll(provider, [report(answer(64))])
        for inventory in ([entry(), entry(path=b"vid_0e8d&pid_0703#dup")],
                          [entry(input_report_sizes={"7": 8})],
                          [entry(product_string="Else")]):
            old, = self.poll(provider, inventory=inventory)
            self.assertEqual((old.level, old.cached), (64, True))

    def test_unplug_drops_history(self):
        provider = P()
        self.poll(provider, [report(answer(64))])
        self.assertEqual(self.poll(provider, inventory=[]), [])
        self.assertEqual(self.poll(provider, inventory=[entry()]), [])
        # a different receiver instance does not inherit the old reading
        self.poll(provider, [report(answer(70))])
        other = entry(physical_id=r"pnp:usb\vid_0e8d&pid_0703\other",
                      path=rb"\\?\hid#vid_0e8d&pid_0703&mi_02#other")
        got = self.poll(provider, [report(answer(71))], inventory=[other])
        self.assertEqual([s.level for s in got], [71])
        self.assertEqual(self.poll(provider, inventory=[entry()]), [])

    def test_history_survives_enumeration_error_and_unresolved_parent(self):
        provider = P()
        self.poll(provider, [report(answer(64))])
        with patch.object(A, "hid", FakeHid()), patch.object(
                A.hidlist, "enumerate", side_effect=OSError("boom")):
            old, = provider.poll()
        self.assertEqual((old.level, old.cached), (64, True))
        unresolved = entry(physical_id="collection:xyz")
        old, = self.poll(provider, inventory=[unresolved])
        self.assertEqual((old.level, old.cached), (64, True))
        # the receiver's other collections still prove it is plugged in
        self.assertEqual(self.poll(provider, inventory=[entry(usage_page=0xFF14)])[0].level, 64)
        self.assertEqual(self.poll(provider, inventory=[
            entry(physical_id="collection:xyz", path=b"other")]), [])

    def test_no_history_without_a_first_reading(self):
        provider = P()
        for replies in ([], [report(answer(0, exists=0))], [report(answer(101))]):
            self.assertEqual(self.poll(provider, replies), [])

    def test_absent_never_publishes_zero_percent(self):
        provider = P()
        self.assertEqual(self.poll(provider, [report(answer(0, exists=0))]), [])
        first, = self.poll(provider, [report(answer(37))])
        old, = self.poll(provider, [report(answer(0, exists=0))])
        self.assertEqual(old.level, 37)


class RegistrationTests(unittest.TestCase):
    def test_server_registration(self):
        tree = ast.parse((ENGINE / "server.py").read_text(encoding="utf-8"))
        tuples, mappings = [], []
        for node in ast.walk(tree):
            if isinstance(node, (ast.Tuple, ast.Dict)):
                try:
                    value = ast.literal_eval(node)
                except (ValueError, TypeError, SyntaxError):
                    continue
                (mappings if isinstance(value, dict) else tuples).append(value)
        self.assertIn(("am_infinity_97", "AmInfinity97Provider"), tuples)
        self.assertTrue(any(m.get("am_infinity_97") == (0x0E8D,) for m in mappings))

        def assigned(name):
            return next(ast.literal_eval(n.value) for n in tree.body
                        if isinstance(n, ast.Assign)
                        and any(isinstance(t, ast.Name) and t.id == name for t in n.targets))

        self.assertIn("am_infinity_97", assigned("READER_DIAGNOSTICS"))
        self.assertNotIn("am_infinity_97", assigned("NO_CHARGING_EVIDENCE"))

    def test_recognized_candidate_count_requires_exact_vendor(self):
        import server
        inventory = [entry(), entry(path=b"x"), entry(vendor_id=0x3151),
                     entry(vendor_id=0x1234, product_id=0x0703)]
        self.assertEqual(server.recognized_candidate_count("am_infinity_97", inventory), 1)
        self.assertEqual(server.recognized_candidate_count(
            "am_infinity_97", [entry(vendor_id=0x1234)]), 0)

    def test_provider_constants_are_static_and_exact(self):
        tree = ast.parse(Path(A.__file__).read_text(encoding="utf-8"))
        literals = {n.targets[0].id: ast.literal_eval(n.value) for n in tree.body
                    if isinstance(n, ast.Assign) and isinstance(n.targets[0], ast.Name)
                    and n.targets[0].id in ("VID", "PIDS", "COLLECTION", "PRODUCT_STRING")}
        self.assertEqual(literals["VID"], 0x0E8D)
        self.assertEqual(literals["PIDS"], {0x0703: "Angry Miao AM Infinity .97"})
        self.assertEqual(literals["COLLECTION"], (0xFF13, 0x0001))
        self.assertEqual(literals["PRODUCT_STRING"], "AM Infinity .97")
        self.assertEqual((P.name, P.kind, P.charging_known), ("am_infinity_97", "mouse", True))

    def test_known_ids_and_topology(self):
        self.assertEqual(known_ids.TABLES["am_infinity_97"], ("PIDS",))
        source = (ENGINE / "topology.py").read_text(encoding="utf-8")
        self.assertIn("0x0E8D, 0x0703, 0xFF13, 0x0001", source)
        self.assertIn("from providers.am_infinity_97 import matches_collection", source)

    def test_catalog_row(self):
        rows = [r for r in catalog.load_catalog()["readers"] if r["provider"] == "am_infinity_97"]
        self.assertEqual(len(rows), 1)
        row = rows[0]
        self.assertEqual(row["verificationLevel"], "C")
        self.assertEqual(row["kinds"], ["mouse"])
        self.assertIn("0E8D:0703", row["matchScope"])
        self.assertIn("FF13:0001", row["matchScope"])
        self.assertEqual(catalog.verification_for("am_infinity_97")[0], "C")
        self.assertEqual(catalog.validate_catalog()["errors"], [])

    def test_matches_collection_is_independent_of_parent_and_reports(self):
        self.assertTrue(A.matches_collection(entry()))
        self.assertTrue(A.matches_collection(entry(physical_id=None, input_report_sizes=None)))
        self.assertFalse(A.matches_collection(entry(usage=2)))
        self.assertFalse(A.matches_collection(entry(vendor_id=0x3151)))


if __name__ == "__main__":
    unittest.main()
