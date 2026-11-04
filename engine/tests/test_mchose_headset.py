"""V9 PRO / Turbo+ protocol fixtures; HID, topology and time are simulated."""
from contextlib import contextmanager
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
from providers import mchose_headset as M
from providers import mchose as Mouse


def entry(physical="receiver-a", **changes):
    info = dict(vendor_id=0x291D, product_id=0x385D, usage_page=0xFF90, usage=1,
                input_report_length=64, output_report_length=64,
                path=(physical + "-ff90").encode(), physical_id=physical,
                product_string="V9 PRO", serial_number="")
    info.update(changes)
    return info


def reply(level=20, state=0x02, command=0x65):
    return [0x55, command, level, state] + [0] * 60


def turbo_inventory():
    """The six interface-0 collections in the user's #61 hardware capture."""
    return [entry("turbo", vendor_id=0x3837, product_id=0x600A,
                  product_string="MCHOSE V9 Turbo+", manufacturer_string="C-Media Electronics Inc",
                  interface_number=0, usage_page=page, input_report_length=inp,
                  output_report_length=out, path=f"turbo-{page:04x}".encode())
            for page, inp, out in ((0xFF21, 2, 2), (0xFF22, 0, 0), (0x000C, 3, 0),
                                   (0x000B, 3, 3), (0xFF90, 64, 64), (0xFF82, 64, 64))]


class FakeHid:
    def __init__(self, frames=(), write_result=64, read_ms=10):
        self.frames = list(frames)
        self.write_result = write_result
        self.read_ms = read_ms
        self.now = 0.0
        self.opened, self.writes, self.reads, self.closed = [], [], [], []

    def device(self):
        bus = self

        class Handle:
            def open_path(self, path):
                self.path = path
                bus.opened.append(path)

            def write(self, packet):
                bus.writes.append((self.path, list(packet)))
                return bus.write_result

            def read(self, length, timeout_ms):
                bus.reads.append((self.path, length, timeout_ms))
                bus.now += (min(bus.read_ms, timeout_ms) if bus.frames else timeout_ms) / 1000
                value = bus.frames.pop(0) if bus.frames else []
                if isinstance(value, Exception):
                    raise value
                return value

            def close(self):
                bus.closed.append(self.path)

        # Deliberately no feature API: a feature call fails the test.
        return Handle()


@contextmanager
def fixture(frames=(), inventory=None, **kwargs):
    bus = FakeHid(frames, **kwargs)
    infos = [entry()] if inventory is None else inventory
    with patch.object(M, "hid", bus), \
            patch.object(M.hidlist, "enumerate", side_effect=lambda vid=0: [
                d for d in infos if not vid or d["vendor_id"] == vid]), \
            patch.object(M, "time", SimpleNamespace(monotonic=lambda: bus.now)), \
            patch("topology.usb_parent", return_value=None):
        yield bus


class MchoseHeadsetTests(unittest.TestCase):
    def setUp(self):
        self.provider = M.MchoseHeadsetProvider()

    def test_discharge_and_exact_output_request(self):
        for level in (0, 20, 90, 100):
            with self.subTest(level=level), fixture([reply(level)]) as bus:
                status, = self.provider.poll()
                self.assertEqual((status.level, status.charging, status.online, status.kind),
                                 (level, False, True, "headset"))
                self.assertTrue(status.charging_known)
                self.assertEqual(bus.writes, [(entry()["path"], [0x55, 0x65, 1] + [0] * 61)])
                self.assertEqual(bus.reads[0][:2], (entry()["path"], 64))
                self.assertEqual(bus.closed, bus.opened)

    def test_external_power_hides_even_frozen_invalid_percentage(self):
        for state in (0x00, 0x03):
            for level in (20, 90, 100, 255):
                with self.subTest(state=state, level=level), fixture([reply(level, state)]):
                    status, = self.provider.poll()
                    self.assertIsNone(status.level)
                    self.assertTrue(status.level_unavailable)
                    self.assertTrue(status.charging)
                    self.assertTrue(status.charging_known)
                    self.assertTrue(status.online)

    def test_off_is_explicitly_disconnected_not_zero(self):
        with fixture([reply(0, 0x1A)]):
            status, = self.provider.poll()
        self.assertIsNone(status.level)
        self.assertFalse(status.online)
        self.assertTrue(status.connection_known)
        self.assertFalse(status.charging_known)

    def test_unknown_state_and_invalid_percentage_are_diagnosed(self):
        for frame, expected in ((reply(90, 0x04), "state=0x04"), (reply(101), "level=101")):
            with self.subTest(frame=frame[:4]), fixture([frame]):
                self.assertEqual(self.provider.poll(), [])
                self.assertIn(expected, " ".join(self.provider.diagnostics()))

    def test_unrelated_and_malformed_reports_are_skipped_on_same_handle(self):
        for invalid in (reply(command=0x11), [0x65, 90, 2] + [0] * 61,
                        reply()[:4], reply()[:-1], reply() + [0]):
            with self.subTest(frame=invalid[:4], size=len(invalid)), fixture([invalid, reply(90)]) as bus:
                self.assertEqual([s.level for s in self.provider.poll()], [90])
                self.assertEqual(len(bus.opened), 1)
                self.assertEqual(len(bus.writes), 1)
                self.assertEqual(len(bus.reads), 2)

    def test_timeout_has_no_connection_evidence(self):
        with fixture() as bus:
            self.assertEqual(self.provider.poll(), [])
            self.assertLessEqual(bus.now, 1.001)
            self.assertEqual(bus.opened, bus.closed)
        self.assertIn("connection unknown", " ".join(self.provider.diagnostics()))

    def test_continuous_noise_cannot_extend_deadline(self):
        with fixture([reply(command=0x11)] * 100, read_ms=240) as bus:
            self.assertEqual(self.provider.poll(), [])
            self.assertLessEqual(bus.now, 1.001)
            self.assertLessEqual(len(bus.reads), 5)
            self.assertTrue(all(0 < timeout <= 1000 for _, _, timeout in bus.reads))
            self.assertLess(bus.reads[-1][2], bus.reads[0][2])

    def test_fast_noise_is_bounded_too(self):
        with fixture([reply(command=0x11)] * 100, read_ms=0) as bus:
            self.assertEqual(self.provider.poll(), [])
            self.assertLessEqual(len(bus.reads), 32)

    def test_unknown_or_wrong_caps_never_open_or_send(self):
        for inp, out in ((None, None), (None, 64), (64, None), (0, 64), (64, 0),
                         (63, 64), (64, 63), (65, 64), (64, 65)):
            with self.subTest(input=inp, output=out), fixture(
                    [reply()], [entry(input_report_length=inp, output_report_length=out)]) as bus:
                self.assertEqual(self.provider.poll(), [])
                self.assertEqual(bus.opened, [])
                self.assertEqual(bus.writes, [])
                self.assertIn("nothing sent", " ".join(self.provider.diagnostics()))

    def test_wrong_identity_or_collection_never_selected(self):
        for changes in (dict(usage_page=0xFF22), dict(usage_page=0xFF82), dict(usage=2),
                        dict(vendor_id=0x3837, product_id=0x385D), dict(product_id=0x600A),
                        dict(vendor_id=0x3837, product_id=0x6008), dict(product_id=0x6008),
                        dict(vendor_id=0x5253)):
            with self.subTest(changes=changes), fixture([reply()], [entry(**changes)]) as bus:
                self.assertEqual(self.provider.poll(), [])
                self.assertEqual(bus.opened, [])
                self.assertEqual(bus.writes, [])

    def test_wrong_collections_do_not_hide_eligible_one(self):
        inventory = [entry(path=b"ff22", usage_page=0xFF22),
                     entry(path=b"ff82", usage_page=0xFF82), entry()]
        with fixture([reply()], inventory) as bus:
            self.assertEqual(len(self.provider.poll()), 1)
            self.assertEqual(bus.opened, [entry()["path"]])

    def test_two_same_model_receivers_have_distinct_stable_keys(self):
        inventory = [entry("one"), entry("two")]
        with fixture([reply(20), reply(90)], inventory):
            statuses = self.provider.poll()
        self.assertEqual([s.level for s in statuses], [20, 90])
        self.assertEqual(len({s.key for s in statuses}), 2)
        with fixture([reply(90), reply(20)], list(reversed(inventory))):
            self.assertEqual({s.key for s in self.provider.poll()}, {s.key for s in statuses})

    def test_missing_physical_parent_uses_exact_path_not_shared_serial(self):
        with fixture([reply(20), reply(90)], [entry("one", physical_id=""), entry("two", physical_id="")]):
            self.assertEqual(len({s.key for s in self.provider.poll()}), 2)

    def test_failed_or_short_write_does_not_read_or_retry(self):
        for written in (-1, 0, 63):
            with self.subTest(written=written), fixture([reply()], write_result=written) as bus:
                self.assertEqual(self.provider.poll(), [])
                self.assertEqual(len(bus.writes), 1)
                self.assertEqual(bus.reads, [])
                self.assertEqual(bus.closed, bus.opened)

    def test_io_failure_closes_handle(self):
        with fixture([OSError("fixture failure")]) as bus:
            self.assertEqual(self.provider.poll(), [])
            self.assertEqual(bus.closed, bus.opened)
            self.assertIn("fixture failure", " ".join(self.provider.diagnostics()))

    def test_turbo_capture_reads_80_only_on_ff90_with_lighting_present(self):
        # Lighting caps here are adversarial mock values, not claimed capture facts.
        lighting = entry("lighting", vendor_id=0x3837, product_id=0x6008,
                         product_string="V9 Turbo", manufacturer_string="wch.cn")
        inventory = [lighting] + list(reversed(turbo_inventory()))
        with fixture([reply(80), reply(80)], inventory) as bus, patch.object(Mouse, "hid", bus):
            mouse = Mouse.MchoseProvider()
            for _ in range(2):
                self.assertEqual(mouse.poll(), [])
                status, = self.provider.poll()
                self.assertEqual((status.name, status.level, status.charging, status.online, status.kind),
                                 ("MCHOSE V9 Turbo+", 80, False, True, "headset"))
            self.assertEqual(bus.opened, [b"turbo-ff90"] * 2)
            self.assertEqual(bus.writes, [(b"turbo-ff90", [0x55, 0x65, 1] + [0] * 61)] * 2)
            self.assertEqual(bus.closed, bus.opened)
        diagnostics = " ".join(mouse.diagnostics())
        self.assertIn("600a V9 Turbo+ headset reserved", diagnostics)
        self.assertIn("6008 V9 Turbo lighting device", diagnostics)

    def test_turbo_wrong_caps_or_collection_never_fall_back_to_mouse_or_siblings(self):
        for changes in (dict(input_report_length=None), dict(output_report_length=None),
                        dict(input_report_length=0), dict(output_report_length=0),
                        dict(input_report_length=63), dict(output_report_length=63),
                        dict(input_report_length=65), dict(output_report_length=65),
                        dict(usage_page=0xFF01), dict(usage=2)):
            inventory = turbo_inventory()
            next(d for d in inventory if d["usage_page"] == 0xFF90).update(changes)
            with self.subTest(changes=changes), fixture([reply(80)], inventory) as bus, \
                    patch.object(Mouse, "hid", bus):
                self.assertEqual(Mouse.MchoseProvider().poll(), [])
                self.assertEqual(self.provider.poll(), [])
                self.assertEqual(bus.opened, [])
                self.assertEqual(bus.writes, [])
                self.assertIn("nothing sent", " ".join(self.provider.diagnostics()))

    def test_lighting_never_receives_commands_even_with_mouse_or_headset_caps(self):
        for page in (0xFF01, 0xFF90, 0xFF82):
            inventory = [entry("lighting", vendor_id=0x3837, product_id=0x6008, usage_page=page)]
            with self.subTest(page=page), fixture([reply(80)], inventory) as bus, \
                    patch.object(Mouse, "hid", bus):
                self.assertEqual(Mouse.MchoseProvider().poll(), [])
                self.assertEqual(self.provider.poll(), [])
                self.assertEqual(bus.opened, [])
                self.assertEqual(bus.writes, [])

    def test_turbo_inherits_unverified_external_power_and_off_semantics(self):
        for state in (0x00, 0x03, 0x1A):
            with self.subTest(state=state), fixture([reply(80, state)], turbo_inventory()):
                status, = self.provider.poll()
                self.assertIsNone(status.level)
                self.assertTrue(status.level_unavailable)
                self.assertTrue(status.connection_known)
                self.assertEqual(status.charging, state != 0x1A)
                self.assertEqual(status.charging_known, state != 0x1A)
                self.assertEqual(status.online, state != 0x1A)

    def test_pro_and_turbo_keep_distinct_names_and_identities(self):
        with fixture([reply(20), reply(80)], [entry()] + turbo_inventory()):
            statuses = self.provider.poll()
        self.assertEqual([(s.name, s.level) for s in statuses],
                         [("MCHOSE V9 PRO", 20), ("MCHOSE V9 Turbo+", 80)])
        self.assertEqual(len({s.key for s in statuses}), 2)


if __name__ == "__main__":
    unittest.main()
