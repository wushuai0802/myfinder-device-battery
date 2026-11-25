"""GPW DPI protocol/control regressions. Every HID object is an in-memory fake.

Public protocol facts: Solaar settings_templates.py, commit
e7304c4c451cc9bb4f206a914844525e67856a28 (AdjustableDpi/produce_dpi_list).
No Solaar implementation is imported, no real device is opened or written.
"""
import copy
import os
import sys
import types
import unittest
from unittest.mock import patch

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import _hidstub  # noqa: E402, F401
from providers import logitech as L
from test_logitech import FakeBus, FakeHidpp, entry


SHORT = br"\\?\HID#VID_046D&PID_C547&MI_01&Col01#7&gpw-fixture&0&0000#{fixture}"
LONG = br"\\?\HID#VID_046D&PID_C547&MI_01&Col02#7&gpw-fixture&0&0001#{fixture}"
SUPPORTED = [0, 0, 100, 0xE0, 50, 100, 0, 0, 0]  # 100..25600, step 50, terminated


def device(name="PRO X Wireless"):
    return FakeHidpp({L.F_NAME: (3, {2: [3]}), L.F_INFO: (2, {0: [1, 0x12, 0x34, 0x56, 0x78]}),
                      L.F_UNIFIED: (6, {1: [75, 4, 0]}), L.F_DPI: (10, {0: [1], 1: SUPPORTED})}, name=name)


class DpiBus(FakeBus):
    def __init__(self, mouse, slot=1):
        super().__init__({LONG: ({slot: mouse}, SHORT)})
        self.slot = slot
        self.current, self.default = 400, 800
        self.current_reads = 0
        self.set_calls = []
        self.ignore_set = self.missing_ack = self.fail_readback = self.raise_set = False
        self.short_current = False
        self.change_before_write = None
        self.change_unit_after_state = self.change_unit_after_write = False

    def write(self, path, data):
        if data[1] == self.slot and data[2] == 10 and data[3] >> 4 in (2, 3):
            self.writes.append((path, list(data)))
            fn = data[3]
            if fn >> 4 == 2:
                self.current_reads += 1
                if self.fail_readback and self.set_calls:
                    return self._error(path, self.slot, 10, fn, 0x05)
                if self.change_before_write is not None and self.current_reads == 2:
                    self.current = self.change_before_write
                if self.short_current:
                    self.queues[path].append([0x11, self.slot, 10, fn, 0, 0, 0])
                    return
                self._reply(path, self.slot, 10, fn, [0, self.current >> 8, self.current & 255,
                                            self.default >> 8, self.default & 255])
                if self.change_unit_after_state and self.current_reads == 1:
                    self.slots[LONG][self.slot].features[L.F_INFO] = (2, {0: [1, 1, 2, 3, 4]})
                return
            self.set_calls.append(list(data))
            if self.raise_set:
                raise OSError("fixture transport lost during the one write")
            if not self.ignore_set:
                self.current = (data[5] << 8) | data[6]
            if self.change_unit_after_write:
                self.slots[LONG][self.slot].features[L.F_INFO] = (2, {0: [1, 1, 2, 3, 4]})
            if not self.missing_ack:
                return self._reply(path, self.slot, 10, fn, [0])
            return
        return super().write(path, data)


class GpwDpiTests(unittest.TestCase):
    def setUp(self):
        self.mouse = device()
        self.inventory = [dict(entry(0xC547, 0xFF00, 1, SHORT), vendor_id=L.LOGITECH_VID),
                          dict(entry(0xC547, 0xFF00, 2, LONG), vendor_id=L.LOGITECH_VID)]
        self.bus = DpiBus(self.mouse)
        hid = patch.object(L, "hid", types.SimpleNamespace(device=self.bus.device_class()))
        hidlist = patch.object(L, "hidlist", types.SimpleNamespace(enumerate=lambda vid=0: self.inventory))
        original = L._Channel.request
        def bounded(channel, idx, feat, func, params=(), timeout=None):
            return original(channel, idx, feat, func, params, timeout=.01)
        request = patch.object(L._Channel, "request", bounded)
        for replacement in (hid, hidlist, request):
            replacement.start()
            self.addCleanup(replacement.stop)

    def prime(self):
        [self.status] = L.LogitechProvider().poll()
        self.assertTrue(self.status.dpi["canSet"])
        self.target = self.status.dpi_target
        self.token = self.status.dpi["targetToken"]
        self.bus.writes.clear()
        self.bus.current_reads = 0

    def change(self, value=800, expected=400):
        return L.set_device_dpi(self.inventory, self.target, value, expected, self.token)

    def test_exact_gpw_range_is_linear_and_not_a_preset_list(self):
        values = L.parse_dpi_list(SUPPORTED)
        self.assertEqual(len(values), 511)
        self.assertEqual((values[0], values[-1]), (100, 25600))
        self.assertTrue(all(b - a == 50 for a, b in zip(values, values[1:])))
        self.assertTrue({400, 800, 1600, 3200}.issubset(values))
        self.prime()
        self.assertEqual(self.status.dpi["stages"], [400, 800, 1600, 3200])
        self.assertEqual(self.status.dpi["stageSource"], "quick")
        self.assertRegex(self.token, r"^[0-9a-f]{64}$")
        self.assertFalse(any(data[2] == 10 and data[3] >> 4 == 3 for _, data in self.bus.writes))

    def test_quick_choices_include_current_without_fabricating_presets(self):
        self.bus.current = 1200
        self.prime()
        self.assertEqual(self.status.dpi["stages"], [400, 800, 1200, 1600, 3200])
        self.assertLessEqual(len(self.status.dpi["stages"]), 8)

    def test_success_is_one_volatile_write_followed_by_readback(self):
        self.prime()
        before = copy.deepcopy(self.status)
        result = self.change()
        self.assertTrue(result["success"])
        self.assertFalse(result["outcomeUnknown"])
        self.assertEqual(result["confirmedDpi"], 800)
        self.assertEqual(result["dpi"]["current"], 800)
        self.assertEqual(len(self.bus.set_calls), 1)
        self.assertEqual(self.bus.set_calls[0][4:7], [0, 3, 32])
        self.assertGreaterEqual(self.bus.current_reads, 3)
        self.assertEqual(self.status, before, "DPI control cannot rewrite earlier battery evidence")
        self.assertFalse(any(data[2] == 0 and data[4:6] == [0x81, 0] for _, data in self.bus.writes))

    def test_dpi_reads_do_not_advance_battery_measurement_time(self):
        with patch.object(L, "_dpi_now", side_effect=["2026-10-18T01:00:00Z", "2026-10-18T01:00:01Z"]):
            [status] = L.LogitechProvider().poll()
        self.assertEqual(status.measured_at_utc, "2026-10-18T01:00:00Z")
        self.assertEqual(status.queried_at_utc, status.measured_at_utc)
        self.assertEqual(status.dpi["checkedAtUtc"], "2026-10-18T01:00:01Z")
        self.assertEqual(status.level, 75)

    def test_stale_unit_cannot_be_written(self):
        self.prime()
        self.mouse.features[L.F_INFO] = (2, {0: [1, 1, 2, 3, 4]})
        result = self.change()
        self.assertEqual(result["error"], "dpi_target_changed")
        self.assertFalse(result["dpi"]["canSet"])
        self.assertEqual(self.bus.set_calls, [])

    def test_unit_is_rechecked_after_capability_queries(self):
        self.prime()
        self.bus.change_unit_after_state = True
        self.assertEqual(self.change()["error"], "dpi_target_changed")
        self.assertEqual(self.bus.set_calls, [])

    def test_missing_receiver_or_stale_token_has_no_hid_work(self):
        self.prime()
        result = L.set_device_dpi([], self.target, 800, 400, self.token)
        self.assertEqual(result["error"], "dpi_receiver_changed")
        result = L.set_device_dpi(self.inventory, self.target, 800, 400, "0" * 64)
        self.assertEqual(result["error"], "dpi_stale_target")
        self.assertEqual(self.bus.writes, [])

    def test_same_pid_other_receiver_does_not_match_exact_paths(self):
        self.prime()
        other = [dict(value, path=value["path"].replace(b"gpw-fixture", b"other-receiver")) for value in self.inventory]
        self.assertEqual(L.set_device_dpi(other, self.target, 800, 400, self.token)["error"], "dpi_receiver_changed")
        self.assertEqual(self.bus.writes, [])

    def test_unsupported_value_and_invalid_types_never_write(self):
        self.prime()
        self.assertEqual(self.change(555)["error"], "dpi_unsupported_value")
        for value, expected in ((True, 400), (800, False), ("800", 400), (0, 400), (800, None)):
            self.assertEqual(self.change(value, expected)["error"], "dpi_invalid_value")
        self.assertEqual(self.bus.set_calls, [])

    def test_current_mismatch_before_or_after_capability_read_never_writes(self):
        self.prime()
        self.bus.current = 1600
        result = self.change()
        self.assertEqual((result["error"], result["confirmedDpi"]), ("dpi_current_changed", 1600))
        self.bus.current, self.bus.current_reads = 400, 0
        self.bus.change_before_write = 3200
        result = self.change()
        self.assertEqual((result["error"], result["confirmedDpi"]), ("dpi_current_changed", 3200))
        self.assertEqual(self.bus.set_calls, [])

    def test_repeated_current_selection_is_verified_without_a_write(self):
        self.prime()
        result = self.change(400)
        self.assertTrue(result["success"])
        self.assertEqual(result["confirmedDpi"], 400)
        self.assertEqual(self.bus.set_calls, [])

    def test_unknown_readback_is_not_retried_or_reported_successful(self):
        self.prime()
        self.bus.fail_readback = True
        result = self.change()
        self.assertFalse(result["success"])
        self.assertTrue(result["outcomeUnknown"])
        self.assertIsNone(result["confirmedDpi"])
        self.assertIsNone(result["dpi"]["current"])
        self.assertFalse(result["dpi"]["canSet"])
        self.assertEqual(len(self.bus.set_calls), 1)

    def test_missing_ack_with_matching_readback_can_confirm_state(self):
        self.prime()
        self.bus.missing_ack = True
        result = self.change()
        self.assertTrue(result["success"])
        self.assertEqual(result["confirmedDpi"], 800)
        self.assertEqual(len(self.bus.set_calls), 1)

    def test_missing_ack_and_mismatch_remain_unknown_without_retry(self):
        self.prime()
        self.bus.missing_ack = self.bus.ignore_set = True
        result = self.change()
        self.assertFalse(result["success"])
        self.assertTrue(result["outcomeUnknown"])
        self.assertIsNone(result["confirmedDpi"])
        self.assertEqual(len(self.bus.set_calls), 1)

    def test_write_transport_exception_is_uncertain_and_never_retried(self):
        self.prime()
        self.bus.raise_set = True
        result = self.change()
        self.assertFalse(result["success"])
        self.assertTrue(result["outcomeUnknown"])
        self.assertEqual(len(self.bus.set_calls), 1)

    def test_changed_unit_after_write_invalidates_readback_identity(self):
        self.prime()
        self.bus.change_unit_after_write = True
        result = self.change()
        self.assertFalse(result["success"])
        self.assertTrue(result["outcomeUnknown"])
        self.assertIsNone(result["confirmedDpi"])
        self.assertEqual(len(self.bus.set_calls), 1)

    def test_sleeping_mouse_has_no_enabled_cached_controls(self):
        self.prime()
        provider = L.LogitechProvider()
        provider.poll()
        self.mouse.silent = True
        [status] = provider.poll()
        self.assertFalse(status.online)
        self.assertIsNone(status.dpi)
        self.assertEqual(self.change()["error"], "dpi_device_unavailable")
        self.assertEqual(self.bus.set_calls, [])

    def test_short_dpi_payload_does_not_erase_valid_battery(self):
        self.bus.short_current = True
        [status] = L.LogitechProvider().poll()
        self.assertEqual(status.level, 75)
        self.assertFalse(status.dpi["canSet"])
        self.assertEqual(status.dpi["error"], "dpi_invalid_reply")
        self.assertIsNone(status.dpi_target)

    def test_malformed_supported_ranges_are_rejected(self):
        for reply in ([0, 0, 100, 0xE0, 0, 100, 0, 0, 0],
                      [0, 0, 100, 0xE0, 50, 0, 50, 0, 0], [1, 0, 100, 0, 0], [0, 0, 100]):
            with self.assertRaises(ValueError):
                L.parse_dpi_list(reply)

    def configure_g_pro(self, pid=0xC539, slot=1):
        self.mouse.name = "G Pro Wireless Gaming Mouse"
        self.inventory[:] = [dict(value, product_id=pid) for value in self.inventory]
        self.bus.slot = slot
        self.bus.slots[LONG] = {slot: self.mouse}

    def test_g_pro_wireless_exact_name_all_listed_lightspeed_receivers(self):
        for pid in (0xC539, 0xC53A, 0xC53D, 0xC53F, 0xC541, 0xC545, 0xC547, 0xC54D):
            with self.subTest(pid=hex(pid)):
                self.configure_g_pro(pid)
                self.bus.current = 400
                self.bus.set_calls.clear()
                self.prime()
                result = self.change()
                self.assertTrue(result["success"])
                self.assertEqual(result["confirmedDpi"], 800)
                self.assertEqual(len(self.bus.set_calls), 1)

    def test_g_pro_wireless_cable_uses_same_unit_and_volatile_readback(self):
        self.configure_g_pro(0xC088, 0xFF)
        self.prime()
        self.assertEqual(self.target["slot"], 0xFF)
        self.assertEqual(self.target["unit"], "12345678")
        self.assertTrue(self.change()["success"])
        self.assertEqual(len(self.bus.set_calls), 1)
        self.assertEqual(self.bus.set_calls[0][1:3], [0xFF, 10])

    def test_g_pro_wireless_preserves_identity_current_and_readback_guards(self):
        for wired in (False, True):
            for failure in ("unit", "current", "readback", "unit-after-write"):
                with self.subTest(wired=wired, failure=failure):
                    self.configure_g_pro(0xC088 if wired else 0xC539, 0xFF if wired else 1)
                    self.mouse.features[L.F_INFO] = (2, {0: [1, 0x12, 0x34, 0x56, 0x78]})
                    self.bus.current = 400
                    self.bus.set_calls.clear()
                    self.bus.fail_readback = self.bus.change_unit_after_write = False
                    self.prime()
                    if failure == "unit":
                        self.mouse.features[L.F_INFO] = (2, {0: [1, 1, 2, 3, 4]})
                    elif failure == "current":
                        self.bus.current = 1600
                    elif failure == "readback":
                        self.bus.fail_readback = True
                    else:
                        self.bus.change_unit_after_write = True
                    result = self.change()
                    self.assertFalse(result["success"])
                    self.assertEqual(len(self.bus.set_calls), int(failure in ("readback", "unit-after-write")))
                    self.assertEqual(result["outcomeUnknown"], failure in ("readback", "unit-after-write"))

    def test_g_pro_wireless_no_dpi_without_unit_or_long_collection(self):
        self.configure_g_pro(0xC088, 0xFF)
        self.mouse.features[L.F_INFO] = (2, {0: [1, 0, 0, 0, 0]})
        [status] = L.LogitechProvider().poll()
        self.assertFalse(status.dpi and status.dpi["canSet"])
        self.assertIsNone(status.dpi_target)
        self.inventory[:] = self.inventory[:1]
        self.assertEqual(L.LogitechProvider().poll(), [])

    def test_g_pro_wireless_invalid_write_identity_never_touches_hid(self):
        self.configure_g_pro()
        self.prime()
        for change in (dict(name=[]), dict(name="  "), dict(pid=0), dict(pid=0x0AFB),
                       dict(slot=0), dict(slot=7), dict(slot=True), dict(slot=0xFF), dict(unit="00000000"),
                       dict(unit="1234"), dict(unit="1234567G")):
            with self.subTest(change=change):
                target = dict(self.target, **change)
                result = L.set_device_dpi(self.inventory, target, 800, 400, L._target_token(target))
                self.assertEqual(result["error"], "dpi_invalid_target")
                self.assertEqual(self.bus.writes, [])

    def test_g_pro_wireless_name_change_to_another_allowed_model_is_rejected(self):
        self.configure_g_pro(0xC547)
        self.prime()
        self.mouse.name = "PRO X Wireless"
        self.assertEqual(self.change()["error"], "dpi_target_changed")
        self.assertEqual(self.bus.set_calls, [])

    def test_g_pro_wireless_cable_can_use_long_collection_only(self):
        self.configure_g_pro(0xC088, 0xFF)
        self.inventory[:] = self.inventory[1:]
        self.prime()
        self.assertIsNone(self.target["short_path"])
        self.assertTrue(self.change()["success"])
        self.assertEqual(len(self.bus.set_calls), 1)

    def test_g_pro_wireless_acknowledged_mismatch_keeps_actual_readback_without_retry(self):
        for wired in (False, True):
            with self.subTest(wired=wired):
                self.configure_g_pro(0xC088 if wired else 0xC539, 0xFF if wired else 1)
                self.bus.current = 400
                self.bus.set_calls.clear()
                self.prime()
                self.bus.ignore_set = True
                result = self.change()
                self.assertFalse(result["success"])
                self.assertFalse(result["outcomeUnknown"])
                self.assertEqual((result["error"], result["confirmedDpi"]), ("dpi_readback_mismatch", 400))
                self.assertEqual(len(self.bus.set_calls), 1)

    def test_pro_x_partial_cached_identity_can_recover_on_the_next_poll(self):
        provider = L.LogitechProvider()
        self.mouse.features[L.F_NAME] = (3, {2: [255]})
        [first] = provider.poll()
        self.assertFalse(first.dpi and first.dpi["canSet"])
        self.mouse.features[L.F_NAME] = (3, {2: [3]})
        [recovered] = provider.poll()
        self.assertTrue(recovered.dpi and recovered.dpi["canSet"])
        self.assertEqual(self.bus.set_calls, [])

    # ---- beta: every HID++ 2.0 mouse with a live unit id and a readable 0x2201 -------------

    def configure_mouse(self, pid, slot=1, name="MX Master 3S", kind=3, unit=(0x0A, 0x0B, 0x0C, 0x0D)):
        self.mouse.name = name
        self.mouse.features[L.F_NAME] = (3, {2: [kind]})
        self.mouse.features[L.F_INFO] = (2, {0: [1, *unit]})
        self.inventory[:] = [dict(value, product_id=pid) for value in self.inventory]
        self.bus.slot = slot
        self.bus.slots[LONG] = {slot: self.mouse}

    def test_any_named_mouse_on_every_receiver_family_gets_volatile_dpi(self):
        for pid in (0xC548, 0xC52B, 0xC52F, 0xC532, 0xC539, 0xC53F, 0xC54D, 0xC54F, 0xC500):
            for name in ("MX Master 3S", "G502 X LIGHTSPEED", "M720 Triathlon", "Pebble Mouse 2"):
                with self.subTest(pid=hex(pid), name=name):
                    # 0xC54F is a single-slot passthrough; 0xC500 is unknown (no "receiver" product): a cable
                    self.configure_mouse(pid, {0xC54F: 1, 0xC500: 0xFF}.get(pid, 2), name)
                    self.bus.current = 400
                    self.bus.set_calls.clear()
                    self.prime()
                    self.assertEqual(self.target["name"], name)
                    result = self.change()
                    self.assertTrue(result["success"])
                    self.assertEqual(result["confirmedDpi"], 800)
                    self.assertEqual(len(self.bus.set_calls), 1)
                    self.assertEqual(self.bus.set_calls[0][4:7], [0, 3, 32])      # fn 3, sensor 0, 800

    def test_mouse_on_its_own_cable_gets_volatile_dpi(self):
        self.configure_mouse(0xC08D, 0xFF, "G502 X PLUS")
        self.prime()
        self.assertEqual(self.target["slot"], 0xFF)
        self.assertTrue(self.change()["success"])
        self.assertEqual(self.bus.set_calls[0][1:3], [0xFF, 10])

    def test_non_mouse_devices_are_never_probed_for_dpi(self):
        for kind in (0, 2, 1):
            with self.subTest(kind=kind):
                self.configure_mouse(0xC548, 1, "K850", kind=kind)
                self.bus.writes.clear()
                [status] = L.LogitechProvider().poll()
                self.assertIsNone(status.dpi)
                self.assertIsNone(status.dpi_target)
                self.assertFalse(any(data[2] == 10 for _, data in self.bus.writes))
        self.configure_mouse(0x0AFB, 0xFF, "G PRO X 2")       # a headset pid is never a DPI target
        self.assertIsNone(L.LogitechProvider().poll()[0].dpi)

    def test_mouse_without_unit_id_cannot_be_written_but_keeps_battery(self):
        self.configure_mouse(0xC548, 1, unit=(0, 0, 0, 0))
        [status] = L.LogitechProvider().poll()
        self.assertEqual(status.level, 75)
        self.assertFalse(status.dpi and status.dpi["canSet"])
        self.assertIsNone(status.dpi_target)
        self.assertEqual(self.bus.set_calls, [])

    def test_mouse_without_0x2201_has_no_dpi_and_costs_one_lookup(self):
        self.configure_mouse(0xC548, 1)
        del self.mouse.features[L.F_DPI]
        [status] = L.LogitechProvider().poll()
        self.assertEqual(status.level, 75)
        self.assertIsNone(status.dpi)
        self.assertEqual(status.measured_at_utc, None)

    def test_0x2202_extended_dpi_and_0x2205_pointer_speed_are_never_used_as_dpi(self):
        self.configure_mouse(0xC548, 1)
        del self.mouse.features[L.F_DPI]
        self.mouse.features[L.F_EXTENDED_DPI] = (11, {0: [1], 1: SUPPORTED, 2: [0, 4, 0x90, 4, 0x90]})
        self.mouse.features[0x2205] = (12, {0: [5, 15], 1: [0, 10]})
        [status] = L.LogitechProvider().poll()
        self.assertIsNone(status.dpi)
        self.assertFalse(any(data[2] in (11, 12) for _, data in self.bus.writes))

    def test_sensor_count_two_uses_sensor_zero_and_zero_sensors_is_rejected(self):
        self.mouse.features[L.F_DPI] = (10, {0: [2], 1: SUPPORTED})
        self.prime()
        self.assertEqual(self.target["sensor"], 0)
        self.assertEqual(self.change()["confirmedDpi"], 800)
        self.assertEqual(self.bus.set_calls[0][4], 0)
        self.mouse.features[L.F_DPI] = (10, {0: [0], 1: SUPPORTED})
        [status] = L.LogitechProvider().poll()
        self.assertEqual(status.dpi["error"], "dpi_sensor_not_supported")
        self.assertFalse(status.dpi["canSet"])

    def test_discrete_lists_ranges_and_full_unterminated_pages(self):
        self.assertEqual(L.parse_dpi_list([0, 0, 200, 1, 0x90, 3, 0x20, 0, 0]), (200, 400, 800))
        self.assertEqual(L.parse_dpi_list([0, 0, 200, 0xE0, 100, 0x03, 0x20, 0, 0]),
                         (200, 300, 400, 500, 600, 700, 800))
        mixed = L.parse_dpi_list([0, 0, 100, 0xE0, 100, 1, 0x2C, 0x0B, 0xB8, 0, 0])   # 100..300 step 100, 3000
        self.assertEqual(mixed, (100, 200, 300, 3000))
        full = [0] + [b for value in (400, 800, 1200, 1600, 2400, 3200, 4800) for b in (value >> 8, value & 255)] + [0]
        self.assertEqual(len(full), 16)
        self.assertEqual(L.parse_dpi_list(full), (400, 800, 1200, 1600, 2400, 3200, 4800))
        for bad in ([0, 0, 200, 1, 0x90, 3, 0x20], [0, 0, 200, 0xE0], [0, 1, 0x90, 0, 0xC8, 0, 0],
                    [0, 0xE0, 50, 1, 0x90, 0, 0]):
            with self.assertRaises(ValueError):
                L.parse_dpi_list(bad)

    def test_current_dpi_outside_quick_list_is_merged_and_highlighted(self):
        for current in (50, 1250, 1200, 25600):
            with self.subTest(current=current):
                self.mouse.features[L.F_DPI] = (10, {0: [1], 1: [0, 0, 50, 0xE0, 50, 0x64, 0, 0, 0]})   # 50..25600
                self.bus.current = current
                self.prime()
                stages = self.status.dpi["stages"]
                self.assertIn(current, stages)
                self.assertEqual(stages, sorted(stages))
                self.assertLessEqual(len(stages), 8)
                self.assertTrue(all(50 <= value <= 65535 for value in stages))
                self.assertTrue(self.status.dpi["canSet"])

    def test_current_not_in_supported_list_is_read_only(self):
        self.bus.current = 777
        [status] = L.LogitechProvider().poll()
        self.assertEqual(status.dpi["error"], "dpi_current_unsupported")
        self.assertFalse(status.dpi["canSet"])
        self.assertIsNone(status.dpi_target)

    def test_non_gpw_mouse_keeps_every_write_gate(self):
        for failure in ("unit", "current", "readback", "unit-after-write", "token", "expected"):
            with self.subTest(failure=failure):
                self.configure_mouse(0xC548, 2, "MX Anywhere 3")
                self.mouse.features[L.F_INFO] = (2, {0: [1, 0x0A, 0x0B, 0x0C, 0x0D]})
                self.bus.current = 400
                self.bus.set_calls.clear()
                self.bus.fail_readback = self.bus.change_unit_after_write = False
                self.prime()
                token, expected = self.token, 400
                if failure == "unit":
                    self.mouse.features[L.F_INFO] = (2, {0: [1, 1, 2, 3, 4]})
                elif failure == "current":
                    self.bus.current = 1600
                elif failure == "readback":
                    self.bus.fail_readback = True
                elif failure == "unit-after-write":
                    self.bus.change_unit_after_write = True
                elif failure == "token":
                    token = "0" * 64
                else:
                    expected = 800
                result = L.set_device_dpi(self.inventory, self.target, 800, expected, token)
                unknown = failure in ("readback", "unit-after-write")
                self.assertFalse(result["success"])
                self.assertEqual(result["outcomeUnknown"], unknown)
                self.assertEqual(len(self.bus.set_calls), int(unknown))
                # only the volatile 0x2201 fn 3 is ever written; never an onboard profile function
                self.assertTrue(all(data[2] == 10 and data[3] >> 4 == 3 for _, data in self.bus.writes
                                    if data[2] not in (0, 2) and data[3] >> 4 not in (0, 1, 2)))

    def test_mouse_that_changes_to_another_mouse_is_rejected(self):
        self.configure_mouse(0xC548, 1, "MX Master 3S")
        self.prime()
        self.mouse.name = "MX Anywhere 3"
        self.assertEqual(self.change()["error"], "dpi_target_changed")
        self.assertEqual(self.bus.set_calls, [])

    def test_receiver_in_other_family_never_matches_exact_paths(self):
        self.configure_mouse(0xC548, 1)
        self.prime()
        moved = [dict(value, product_id=0xC52B) for value in self.inventory]
        self.assertEqual(L.set_device_dpi(moved, self.target, 800, 400, self.token)["error"], "dpi_receiver_changed")
        self.assertEqual(self.bus.writes, [])


# ---- 0x8100 ONBOARD PROFILES: read-only DPI slots for mice without 0x2201 ---------------------

def crc16(data):
    crc = 0xFFFF
    for byte in data:
        crc ^= byte << 8
        for _ in range(8):
            crc = ((crc << 1) ^ 0x1021) & 0xFFFF if crc & 0x8000 else (crc << 1) & 0xFFFF
    return crc


def profile_sector(fmt, dpis, default, size=255, y=None, corrupt=False):
    data = bytearray(size)
    if fmt in (2, 3, 4):
        data[1] = default
        for index, dpi in enumerate(dpis):
            data[3 + 2 * index:5 + 2 * index] = dpi.to_bytes(2, "little")
    else:
        data[2] = default
        for index, dpi in enumerate(dpis):
            base = 4 + 5 * index
            data[base:base + 2] = dpi.to_bytes(2, "little")
            data[base + 2:base + 4] = (dpi if y is None else y).to_bytes(2, "little")
            data[base + 4] = 1 if dpi else 0
    data[-2:] = crc16(data[:-2]).to_bytes(2, "big")
    if corrupt:
        data[10] ^= 0xFF
    return bytes(data)


class OnboardBus(FakeBus):
    """Feature 13 = 0x8100. Only fn 0/2/4/5 are answered; anything else is recorded as a violation."""

    def __init__(self, mouse, slot=1):
        super().__init__({LONG: ({slot: mouse}, SHORT)})
        self.slot = slot
        self.fmt, self.mode, self.current_sector = 7, 1, 1
        self.sector = profile_sector(7, [400, 800, 1600, 3200, 0], 1)
        self.profile_calls = []
        self.memory_reads = 0

    def write(self, path, data):
        if data[1] == self.slot and data[2] == 13:
            self.writes.append((path, list(data)))
            fn, params = data[3] >> 4, data[4:]
            self.profile_calls.append(fn)
            if fn == 0:      # the #1052 capture, plus the sector size
                return self._reply(path, self.slot, 13, data[3], [1, self.fmt, 1, 5, 1, 5, 16, 0, 255])
            if fn == 2:
                return self._reply(path, self.slot, 13, data[3], [self.mode])
            if fn == 4:
                return self._reply(path, self.slot, 13, data[3], [0, self.current_sector, 0])
            if fn == 5:
                self.memory_reads += 1
                sector, offset = params[0] << 8 | params[1], params[2] << 8 | params[3]
                assert sector == self.current_sector
                return self._reply(path, self.slot, 13, data[3], list(self.sector[offset:offset + 16]))
            return None      # forbidden function: no answer (and recorded in profile_calls)
        return super().write(path, data)


def onboard_mouse():
    return FakeHidpp({L.F_NAME: (3, {2: [3]}), L.F_INFO: (2, {0: [1, 0x12, 0x34, 0x56, 0x78]}),
                      L.F_UNIFIED: (6, {1: [75, 4, 0]}), L.F_PROFILES: (13, {})}, name="PRO X 2")


class OnboardDpiTests(unittest.TestCase):
    def setUp(self):
        self.mouse = onboard_mouse()
        self.inventory = [dict(entry(0xC54D, 0xFF00, 1, SHORT), vendor_id=L.LOGITECH_VID),
                          dict(entry(0xC54D, 0xFF00, 2, LONG), vendor_id=L.LOGITECH_VID)]
        self.bus = OnboardBus(self.mouse)
        hid = patch.object(L, "hid", types.SimpleNamespace(device=self.bus.device_class()))
        hidlist = patch.object(L, "hidlist", types.SimpleNamespace(enumerate=lambda vid=0: self.inventory))
        original = L._Channel.request
        def bounded(channel, idx, feat, func, params=(), timeout=None):
            return original(channel, idx, feat, func, params, timeout=.01)
        request = patch.object(L._Channel, "request", bounded)
        for replacement in (hid, hidlist, request):
            replacement.start()
            self.addCleanup(replacement.stop)

    def poll(self):
        [status] = L.LogitechProvider().poll()
        return status

    def test_crc16_ccitt_false_check_value(self):
        self.assertEqual(L._profile_crc(b"123456789\0\0"), 0x29B1)

    def test_format_7_profile_gives_read_only_onboard_stages(self):
        status = self.poll()
        self.assertEqual(status.level, 75)
        self.assertEqual(status.dpi["stages"], [400, 800, 1600, 3200])
        self.assertEqual((status.dpi["current"], status.dpi["currentSource"]), (800, "profileDefault"))
        self.assertEqual(status.dpi["stageSource"], "onboard")
        self.assertTrue(status.dpi["supported"])
        self.assertFalse(status.dpi["canSet"])
        self.assertIsNone(status.dpi_target)
        self.assertIn(status.dpi["current"], status.dpi["stages"])
        # only getMode / getCurrentProfile / getInfo / memoryRead, 255-byte sector = 16 chunks
        self.assertEqual(set(self.bus.profile_calls), {0, 2, 4, 5})
        self.assertEqual(self.bus.memory_reads, 16)

    def test_never_sends_any_profile_write_function_or_0x2202(self):
        for mode in (1, 2):
            self.bus.mode = mode
            self.poll()
        self.assertFalse(set(self.bus.profile_calls) & {1, 3, 6, 7, 8, 9, 10})

    def test_format_4_scalar_table(self):
        self.bus.fmt = 4
        self.bus.sector = profile_sector(4, [800, 1600, 2400, 0, 0], 2)
        status = self.poll()
        self.assertEqual(status.dpi["stages"], [800, 1600, 2400])
        self.assertEqual(status.dpi["current"], 2400)
        self.assertFalse(status.dpi["canSet"])

    def test_non_quick_onboard_stage_values_are_kept_in_device_order(self):
        self.bus.sector = profile_sector(8, [1200, 400, 0xFFFF, 2400, 800], 3)
        status = self.poll()
        self.assertEqual(status.dpi["stages"], [1200, 400, 2400, 800])
        self.assertEqual(status.dpi["current"], 2400)

    def test_host_mode_claims_no_current_and_is_read_only(self):
        self.bus.mode = 2
        status = self.poll()
        self.assertIsNone(status.dpi["current"])
        self.assertEqual(status.dpi["error"], "dpi_onboard_host_mode")
        self.assertFalse(status.dpi["canSet"])
        self.assertEqual(status.dpi["stages"], [400, 800, 1600, 3200])

    def test_unverified_format_bad_crc_and_odd_stages_report_errors(self):
        cases = ((1, profile_sector(7, [400, 800, 0, 0, 0], 0), "dpi_onboard_format_unverified"),
                 (7, profile_sector(7, [400, 800, 0, 0, 0], 0, corrupt=True), "dpi_onboard_profile_invalid"),
                 (7, profile_sector(7, [0, 0, 0, 0, 0], 0), "dpi_onboard_profile_invalid"),
                 (7, profile_sector(7, [400, 800, 0, 0, 0], 0, y=1600), "dpi_onboard_stage_unsupported"),
                 (7, profile_sector(7, [30, 800, 0, 0, 0], 0), "dpi_onboard_stage_unsupported"))
        for fmt, sector, error in cases:
            with self.subTest(error=error, fmt=fmt):
                self.bus.fmt, self.bus.sector = fmt, sector
                status = self.poll()
                self.assertEqual(status.level, 75)
                self.assertFalse(status.dpi["supported"])
                self.assertEqual(status.dpi["error"], error)
                self.assertFalse(status.dpi["canSet"])

    def test_default_index_on_an_empty_slot_gives_no_current(self):
        self.bus.sector = profile_sector(7, [400, 800, 0, 0, 0], 4)
        status = self.poll()
        self.assertIsNone(status.dpi["current"])
        self.assertEqual(status.dpi["stages"], [400, 800])

    def test_directory_sector_zero_is_never_read_as_a_profile(self):
        self.bus.current_sector = 0
        self.assertEqual(self.poll().dpi["error"], "dpi_onboard_profile_invalid")
        self.assertEqual(self.bus.memory_reads, 0)

    def test_decoded_profile_is_cached_between_polls(self):
        provider = L.LogitechProvider()
        provider.poll()
        reads = self.bus.memory_reads
        [status] = provider.poll()
        self.assertEqual(self.bus.memory_reads, reads)
        self.assertEqual(status.dpi["stages"], [400, 800, 1600, 3200])
        self.bus.current_sector = 2                       # another profile became active
        provider.poll()
        self.assertGreater(self.bus.memory_reads, reads)

    def test_0x2201_wins_when_both_features_exist(self):
        self.mouse.features[L.F_DPI] = (10, {0: [1], 1: SUPPORTED})
        bus = DpiBus(self.mouse)
        replacement = patch.object(L, "hid", types.SimpleNamespace(device=bus.device_class()))
        replacement.start()
        self.addCleanup(replacement.stop)
        status = self.poll()
        self.assertEqual(status.dpi["stageSource"], "quick")
        self.assertTrue(status.dpi["canSet"])
        self.assertFalse(any(data[2] == 13 for _, data in bus.writes))

    def test_set_on_an_onboard_only_mouse_never_writes(self):
        status = self.poll()
        target = {"provider": "logitech", "pid": 0xC54D, "instance": L._instance(self.inventory[1]),
                  "slot": 1, "unit": "12345678", "name": "PRO X 2", "sensor": 0,
                  "long_path": LONG, "short_path": SHORT}
        self.bus.writes.clear()
        self.bus.profile_calls.clear()
        result = L.set_device_dpi(self.inventory, target, 1600, 800, L._target_token(target))
        self.assertFalse(result["success"])
        self.assertFalse(result["outcomeUnknown"])
        self.assertEqual(self.bus.profile_calls, [])
        self.assertFalse(any(data[3] >> 4 == 3 and data[2] != 0 for _, data in self.bus.writes))
        self.assertIsNone(status.dpi_target)

    def test_onboard_view_is_not_writable_through_the_server_normaliser(self):
        import server
        dpi = server.normalise_dpi(self.poll().dpi, usable=True)
        self.assertFalse(dpi["canSet"])
        self.assertEqual((dpi["stageSource"], dpi["current"]), ("onboard", 800))


if __name__ == "__main__":
    unittest.main(verbosity=2)
