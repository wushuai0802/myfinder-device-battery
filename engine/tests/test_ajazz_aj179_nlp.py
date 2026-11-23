"""Synthetic AJ179 NLP fixtures; no device or network access."""
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
import known_ids  # noqa: E402
from providers import ajazz_aj179_nlp as A  # noqa: E402

P = A.AjazzAj179NlpProvider
PATH = rb"\\?\hid#vid_7768&pid_642e&mi_01&col01#synthetic"
PHYSICAL = r"pnp:usb\vid_7768&pid_642e\synthetic"


def entry(**changes):
    info = dict(
        vendor_id=0x7768, product_id=0x642E,
        usage_page=0xFFB2, usage=1, interface_number=1,
        path=PATH, physical_id=PHYSICAL,
        input_report_length=64, output_report_length=64,
        input_report_sizes={"6": 64}, output_report_sizes={"6": 64},
    )
    info.update(changes)
    return info


def status(level=73, charging=0, online=1, firmware=(1, 0)):
    raw = [0] * 64
    raw[0:2] = [6, 5]
    raw[9:11] = firmware
    raw[12:15] = [online, level, charging]
    return raw


def notification(level=73, charging=0, kind=3):
    raw = [0] * 64
    raw[0:2] = [6, 0xFA]
    raw[5:8] = [kind, level, charging]
    return raw


class Clock:
    def __init__(self, step=0.0001):
        self.value = 0
        self.step = step

    def monotonic(self):
        self.value += self.step
        return self.value


class FakeHid:
    def __init__(self, replies=(), queued=(), write_result=64, fail=None,
                 endless_queue=False, endless_reply=False):
        self.replies = list(replies)
        self.queued = list(queued)
        self.write_result = write_result
        self.fail = fail
        self.endless_queue = endless_queue
        self.endless_reply = endless_reply
        self.opened, self.closed, self.writes, self.reads = [], [], [], []

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
                if type(packet) is not bytes or packet != bytes([6, 5]) + bytes(62):
                    raise AssertionError("output outside the fixed 64-byte whitelist")
                bus.writes.append((self.path, packet))
                self.submitted = True
                if bus.fail == "write":
                    raise OSError("private-path")
                return bus.write_result

            def read(self, length, timeout):
                if length != 64 or not 1 <= timeout <= A.READ_TIMEOUT_MS:
                    raise AssertionError("unbounded or unexpected read")
                bus.reads.append((length, timeout))
                phase = "read" if self.submitted else "drain"
                if bus.fail == phase:
                    raise OSError("private-path")
                queue = bus.replies if self.submitted else bus.queued
                if queue:
                    return queue.pop(0)
                if ((not self.submitted and bus.endless_queue)
                        or (self.submitted and bus.endless_reply)):
                    return [0] * 64
                return []

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
def fixture(replies=(), inventory=None, clock_step=0.0001, **kwargs):
    bus = FakeHid(replies, **kwargs)
    clock = Clock(clock_step)
    with patch.object(A, "hid", bus), patch.object(
        A.hidlist, "enumerate",
        return_value=[entry()] if inventory is None else inventory,
    ) as enumeration, patch.object(
        A, "time", SimpleNamespace(monotonic=clock.monotonic),
    ):
        yield bus
        enumeration.assert_called_once_with(0x7768)


class AjazzAj179NlpTests(unittest.TestCase):
    def assert_no_io(self, bus):
        self.assertEqual(bus.opened, [])
        self.assertEqual(bus.writes, [])
        self.assertEqual(bus.reads, [])

    def test_fixed_request_and_percentage_boundaries(self):
        self.assertEqual(A.make_request(), bytes([6, 5]) + bytes(62))
        for level in (0, 73, 100):
            for charging in (0, 1):
                with self.subTest(level=level, charging=charging):
                    with fixture([status(level, charging)]) as bus:
                        reading, = P().poll()
                    self.assertEqual(
                        (reading.level, reading.charging, reading.online),
                        (level, charging == 1, True),
                    )
                    self.assertEqual(reading.kind, "mouse")
                    self.assertEqual(reading.verification_level, "C")
                    self.assertTrue(reading.unconfirmed)
                    self.assertTrue(reading.connection_known)
                    self.assertTrue(reading.charging_known)
                    self.assertFalse(reading.level_unavailable)
                    self.assertEqual(bus.writes, [(PATH, A.make_request())])
                    self.assertEqual(bus.reads[0], (64, 1))
                    self.assertEqual(bus.opened, bus.closed)

    def test_either_firmware_byte_and_nonzero_online_are_valid(self):
        with fixture([status(firmware=(0, 2), online=7)]) as bus:
            reading, = P().poll()
        self.assertEqual(reading.level, 73)
        self.assertEqual(len(bus.writes), 1)

    def test_offline_drops_pending_notification_and_old_reading(self):
        provider = P()
        with fixture([status(92)]):
            self.assertEqual(provider.poll()[0].level, 92)
        with fixture([
            notification(81, 1),
            status(255, charging=255, online=0),
        ]) as bus:
            reading, = provider.poll()
        self.assertIsNone(reading.level)
        self.assertFalse(reading.online)
        self.assertFalse(reading.charging)
        self.assertFalse(reading.charging_known)
        self.assertTrue(reading.connection_known)
        self.assertTrue(reading.level_unavailable)
        self.assertEqual(len(bus.writes), 1)
        with fixture([]):
            self.assertEqual(provider.poll(), [])

    def test_status_is_preferred_over_async_battery(self):
        with fixture([notification(34, 1), status(0, 0)]):
            reading, = P().poll()
        self.assertEqual((reading.level, reading.charging), (0, False))

    def test_battery_notifications_and_unrelated_async(self):
        for level in (1, 73, 100):
            with self.subTest(level=level), fixture([
                notification(99, kind=4), notification(0),
                notification(level, 1),
            ]) as bus:
                reading, = P().poll()
            self.assertEqual((reading.level, reading.charging), (level, True))
            self.assertTrue(reading.charging_known)
            self.assertEqual(len(bus.writes), 1)
        with fixture([notification(0), notification(50, kind=2)]):
            self.assertEqual(P().poll(), [])

    def test_bad_framing_and_invalid_values(self):
        bad_id = status()
        bad_id[0] = 0
        bad_opcode = status()
        bad_opcode[1] = 4
        bad_byte = status()
        bad_byte[20] = 256
        bad_bool = status()
        bad_bool[20] = True
        frames = [
            [], status()[:-1], status() + [0], [0] + status(),
            bad_id, bad_opcode, bad_byte, bad_bool, [0] * 64,
            list(A.make_request()), status(101), status(255),
            status(charging=2), status(firmware=(0, 0)),
            notification(0), notification(101), notification(255),
            notification(charging=2), notification(kind=4),
        ]
        for raw in frames:
            with self.subTest(raw=raw[:15]):
                self.assertIsNone(A.parse_status(raw))
                self.assertIsNone(A.parse_notification(raw))
                with fixture([raw]) as bus:
                    self.assertEqual(P().poll(), [])
                self.assertEqual(len(bus.writes), 1)
                self.assertEqual(bus.opened, bus.closed)
        self.assertIsNone(A.parse_status("x" * 64))
        self.assertIsNone(A.parse_notification(None))
        self.assertEqual(A.parse_status(bytes(status(100))), (100, False, True))

    def test_invalid_then_current_status_without_extra_write(self):
        with fixture([status(101), notification(kind=4), status(64)]) as bus:
            reading, = P().poll()
        self.assertEqual(reading.level, 64)
        self.assertEqual(len(bus.writes), 1)

    def test_stale_queued_input_is_discarded_before_write(self):
        with fixture(
            [status(62)],
            queued=[status(99), notification(88), status(online=0)],
        ) as bus:
            reading, = P().poll()
        self.assertEqual(reading.level, 62)
        self.assertEqual(bus.reads[:4], [(64, 1)] * 4)
        self.assertEqual(len(bus.writes), 1)
        with fixture([], queued=[status(99), notification(88)]):
            self.assertEqual(P().poll(), [])

    def test_never_drained_queue_sends_nothing(self):
        with fixture([status()], endless_queue=True) as bus:
            self.assertEqual(P().poll(), [])
        self.assertEqual(bus.writes, [])
        self.assertEqual(len(bus.reads), A.MAX_DRAIN_READS)
        self.assertEqual(bus.opened, bus.closed)

    def test_drain_and_reply_deadlines_and_count_limits(self):
        with fixture(endless_queue=True, clock_step=0.05) as bus:
            self.assertEqual(P().poll(), [])
        self.assertEqual(bus.writes, [])
        self.assertLess(len(bus.reads), A.MAX_DRAIN_READS)
        with fixture(endless_reply=True) as bus:
            self.assertEqual(P().poll(), [])
        self.assertEqual(len(bus.reads) - 1, A.MAX_READS)
        with fixture(endless_reply=True, clock_step=0.025) as bus:
            self.assertEqual(P().poll(), [])
        self.assertLess(len(bus.reads) - 1, A.MAX_READS)
        self.assertEqual(len(bus.writes), 1)

    def test_short_writes_and_io_failures_never_retry(self):
        for written in (0, 63, 65, None, True):
            with self.subTest(written=written), fixture(
                [status()], write_result=written,
            ) as bus:
                self.assertEqual(P().poll(), [])
            self.assertEqual(len(bus.writes), 1)
            self.assertEqual(bus.reads, [(64, 1)])
            self.assertEqual(bus.opened, bus.closed)
        for phase in ("open", "drain", "write", "read"):
            with self.subTest(phase=phase), fixture(
                [status()], fail=phase,
            ) as bus:
                provider = P()
                self.assertEqual(provider.poll(), [])
                self.assertNotIn("private-path", " ".join(provider.diagnostics()))
            self.assertLessEqual(len(bus.writes), 1)
            if phase in ("open", "drain"):
                self.assertEqual(bus.writes, [])
            self.assertEqual(bus.opened, bus.closed)

    def test_other_identities_and_bad_paths_never_open(self):
        changes = [
            dict(product_id=pid) for pid in (0x6D2E, 0x642F, 0x0001)
        ] + [
            dict(vendor_id=0x258A),
            dict(usage_page=0xFFB1),
            dict(usage=2),
            dict(interface_number=0),
            dict(interface_number=True),
            dict(path=None),
            dict(path=b""),
            dict(path=b"\xff"),
            dict(path=PATH + b"\0"),
            dict(path=PATH.replace(b"mi_01", b"mi_00")),
            dict(path=PATH.replace(b"pid_642e", b"pid_6d2e")),
            dict(path=PATH.replace(b"vid_7768", b"vid_258a")),
        ]
        for change in changes:
            with self.subTest(change=change), fixture(
                [status()], inventory=[entry(**change)],
            ) as bus:
                self.assertEqual(P().poll(), [])
                self.assert_no_io(bus)

    def test_exact_numbered_input_and_output_proof_required(self):
        for direction in ("input", "output"):
            changes = [
                {direction + "_report_length": length}
                for length in (None, 63, 65, "64")
            ] + [
                {direction + "_report_sizes": sizes}
                for sizes in (
                    None, {}, {"0": 64}, {"5": 64}, {"6": 63},
                    {"6": 65}, {6: 64}, {"6": 64, "7": 32},
                )
            ]
            for change in changes:
                with self.subTest(change=change), fixture(
                    [status()], inventory=[entry(**change)],
                ) as bus:
                    self.assertEqual(P().poll(), [])
                    self.assert_no_io(bus)

    def test_duplicates_are_counted_before_path_or_report_proof(self):
        for change in (
            {}, dict(path=None), dict(interface_number=0),
            dict(input_report_sizes=None), dict(output_report_length=65),
            dict(physical_id=PHYSICAL.upper()),
        ):
            with self.subTest(change=change), fixture(
                [status()], inventory=[entry(), entry(**change)],
            ) as bus:
                self.assertEqual(P().poll(), [])
                self.assert_no_io(bus)

    def test_unresolved_target_blocks_resolved_receiver_too(self):
        for physical in (
            None, "", "receiver-a", "collection:unresolved",
            r"pnp:usb\vid_7768&pid_6d2e\synthetic",
            r"pnp:usb\vid_7768&pid_642e&mi_01\synthetic",
        ):
            unresolved = entry(physical_id=physical, input_report_sizes=None)
            for inventory in ([unresolved], [unresolved, unresolved],
                              [entry(), unresolved]):
                with self.subTest(physical=physical, count=len(inventory)):
                    with fixture([status()], inventory=inventory) as bus:
                        self.assertEqual(P().poll(), [])
                        self.assert_no_io(bus)

    def test_non_target_collections_do_not_create_duplicates(self):
        with fixture([status()], inventory=[
            entry(), entry(usage_page=0xFFB1),
            entry(product_id=0x6D2E, physical_id="collection:wired"),
            entry(vendor_id=0x258A, physical_id="collection:foreign"),
        ]) as bus:
            reading, = P().poll()
        self.assertEqual(reading.level, 73)
        self.assertEqual(len(bus.writes), 1)

    def test_topology_proof_is_scoped_and_preserves_failed_candidates(self):
        tree = ast.parse((ENGINE / "topology.py").read_text(encoding="utf-8"))
        blocks = [
            node for node in ast.walk(tree)
            if isinstance(node, ast.If)
            and any(
                isinstance(child, ast.ImportFrom)
                and child.module == "providers.ajazz_aj179_nlp"
                for child in ast.walk(node)
            )
        ]
        block = min(blocks, key=lambda node: node.end_lineno - node.lineno)
        text = ast.unparse(block)
        for value in (0x7768, 0x642E, 0xFFB2, 1):
            self.assertTrue(any(
                isinstance(node, ast.Constant) and node.value == value
                for node in ast.walk(block.test)
            ))
        self.assertIn("battery_caps(hid, data, caps)", text)
        self.assertIn("_descriptor_fallback(info, caps", text)
        self.assertIn("input_report_length=caps.input", text)
        self.assertIn("output_report_length=caps.output", text)
        self.assertFalse(any(
            isinstance(node, ast.Name) and node.id == "caps_complete"
            and isinstance(node.ctx, ast.Store)
            for statement in block.body for node in ast.walk(statement)
        ))

    def test_server_registration_and_static_index(self):
        tree = ast.parse((ENGINE / "server.py").read_text(encoding="utf-8"))
        tuples = []
        mappings = []
        for node in ast.walk(tree):
            if isinstance(node, (ast.Tuple, ast.Dict)):
                try:
                    value = ast.literal_eval(node)
                except (ValueError, TypeError, SyntaxError):
                    continue
                (mappings if isinstance(value, dict) else tuples).append(value)
        self.assertIn(("ajazz_aj179_nlp", "AjazzAj179NlpProvider"), tuples)
        self.assertTrue(any(
            mapping.get("ajazz_aj179_nlp") == (0x7768,)
            for mapping in mappings
        ))
        self.assertIn(
            ("atk_headset", "delux", "vgn_ry_ms", "ajazz_aj179_nlp"),
            tuples,
        )
        diagnostics = next(
            ast.literal_eval(node.value)
            for node in tree.body
            if isinstance(node, ast.Assign)
            and any(isinstance(target, ast.Name)
                    and target.id == "READER_DIAGNOSTICS"
                    for target in node.targets)
        )
        self.assertIn("ajazz_aj179_nlp", diagnostics)
        self.assertEqual(known_ids.TABLES["ajazz_aj179_nlp"], ("PIDS",))
        provider_tree = ast.parse(Path(A.__file__).read_text(encoding="utf-8"))
        literals = {
            node.targets[0].id: ast.literal_eval(node.value)
            for node in provider_tree.body
            if isinstance(node, ast.Assign)
            and isinstance(node.targets[0], ast.Name)
            and node.targets[0].id in ("VID", "PIDS")
        }
        self.assertEqual(literals["VID"], 0x7768)
        self.assertEqual(literals["PIDS"], {0x642E: "AJAZZ AJ179 NLP 2.4G (NearLink)"})


if __name__ == "__main__":
    unittest.main()
