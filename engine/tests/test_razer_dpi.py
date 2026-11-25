"""Razer mouse DPI read/write/readback tests (classic 90B & Viper V4 Pro).

Protocol byte layouts verified against OpenMouse mouse-protocol commit
93c4cc601d448ea481eb59d7b785eec75ae16ec2 (src/razer/codec.ts, src/razer/v4.ts,
src/drivers/razer/hid.ts, src/drivers/razer/viper-v4-pro-hid.ts) and hardware
feedback from issues #558 (Viper V3 Pro 1532:00C1) and #872 (Viper V4 Pro
1532:00E5/00E6). Every HID object is an in-memory fake.
"""
from __future__ import annotations

import sys
import types
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))
if str(ROOT / "tests") not in sys.path:
    sys.path.insert(0, str(ROOT / "tests"))

import _hidstub  # noqa: E402, F401
from device_pack import DevicePack, Entry, Match  # noqa: E402
from providers import razer as R, razer_v4 as V4  # noqa: E402

SEP = "\\"


def _crc(buf) -> int:
    c = 0
    for b in buf[2:88]:
        c ^= b
    return c


def _packet(status: int, tid: int, size: int, cls: int, cid: int, args: bytes = b"",
            bad_crc: bool = False) -> bytes:
    msg = bytearray(90)
    msg[0] = status
    msg[1] = tid
    msg[5] = size
    msg[6] = cls
    msg[7] = cid
    msg[8:8 + len(args)] = args
    msg[88] = _crc(msg) ^ (0xFF if bad_crc else 0x00)
    return b"\x00" + bytes(msg)


def _stage_payload(stages, active: int = 1, store: int = 1, tail: bytes = b"\x00\x00") -> bytes:
    out = bytearray([store, active, len(stages)])
    for idx, pair in enumerate(stages):
        x, y = (pair, pair) if isinstance(pair, int) else pair
        out.extend([idx, (x >> 8) & 0xFF, x & 0xFF, (y >> 8) & 0xFF, y & 0xFF, tail[0], tail[1]])
    return bytes(out)


def classic_info(pid: int = 0x00C1, path: bytes = b"\\\\?\\hid#vid_1532&pid_00c1&mi_00#1",
                 name: str = "Razer Viper V3 Pro", physical: str = "pnp:usb\\vid_1532&pid_00c1\\1",
                 serial: str = "SN00C1", **extra) -> dict:
    data = {
        "vendor_id": 0x1532,
        "product_id": pid,
        "interface_number": 0,
        "usage_page": 0x0001,
        "usage": 0x0002,
        "path": path,
        "product_string": name,
        "serial_number": serial,
        "physical_id": physical,
    }
    data.update(extra)
    return data


def v4_info(pid: int = 0x00E6, physical: str | None = None, **extra) -> dict:
    path = f"{SEP}{SEP}?{SEP}hid#vid_1532&pid_{pid:04x}&mi_03#8&fake#{{x}}".encode()
    data = {
        "vendor_id": 0x1532,
        "product_id": pid,
        "interface_number": 3,
        "usage_page": 0x000C,
        "usage": 1,
        "path": path,
        "physical_id": physical or f"pnp:usb{SEP}vid_1532&pid_{pid:04x}{SEP}5&fake&0&1",
        "serial_number": "V4SERIAL",
        "feature_report_length": 91,
        "feature_report_sizes": {"0": 91},
    }
    data.update(extra)
    return data


class ClassicDpiMouse:
    """In-memory Razer 90-byte mouse enforcing OpenMouse & #872 wire rules."""

    def __init__(self, tid: int = 0x1F, dpi=(1600, 1600), stages=(400, 800, 1600, 3200, 6400),
                 active: int = 3, raw_level: int = 0xCC, charging: int = 0):
        self.tid = tid
        self.dpi = (dpi, dpi) if isinstance(dpi, int) else tuple(dpi)
        self.stages = list(stages) if stages is not None else None
        self.active = active
        self.raw_level = raw_level
        self.charging = charging
        self.requests: list[bytes] = []
        self.dpi_writes: list[bytes] = []
        self.opens = 0
        self.closes = 0
        self.queue: list[bytes] = []
        self.fake_ack_without_change = False
        self.reject_write_status: int | None = None
        self.fail_readback = False
        self.raise_on_write = False
        self.mutate_before_second_xy: int | None = None
        self.xy_reads = 0
        self.foreign_before_reply = 0

    def handle_send(self, raw: bytes) -> int:
        assert len(raw) == 91 and raw[0] == 0x00
        req = bytes(raw[1:])
        assert req[88] == _crc(req), "outbound request CRC mismatch"
        self.requests.append(req)
        tid, size, cls, cid = req[1], req[5], req[6], req[7]
        args = req[8:8 + size]

        for _ in range(self.foreign_before_reply):
            self.queue.append(_packet(R.STATUS_OK, tid, 0x03, 0x0F, 0x03, b"\x01\x04\x00"))

        if tid != self.tid:
            self.queue.append(_packet(R.STATUS_NOT_SUPPORTED, tid, size, cls, cid))
            return 91

        if (cls, cid) == (0x07, 0x80):
            self.queue.append(_packet(R.STATUS_OK, tid, 0x02, cls, cid, bytes([0x00, self.raw_level])))
        elif (cls, cid) == (0x07, 0x84):
            self.queue.append(_packet(R.STATUS_OK, tid, 0x02, cls, cid, bytes([0x00, self.charging])))
        elif (cls, cid) == (0x04, 0x85):
            assert size == 0x07, f"0x04:0x85 data_size must be 0x07, got 0x{size:02x}"
            assert args[0] == R.DPI_STORAGE, f"0x04:0x85 varstore must be 0x01, got 0x{args[0]:02x}"
            self.xy_reads += 1
            if self.mutate_before_second_xy is not None and self.xy_reads == 2:
                self.dpi = (self.mutate_before_second_xy, self.mutate_before_second_xy)
            if self.fail_readback and self.dpi_writes:
                self.queue.append(_packet(R.STATUS_TIMEOUT, tid, 0x07, cls, cid))
            else:
                x, y = self.dpi
                payload = bytes([R.DPI_STORAGE, (x >> 8) & 0xFF, x & 0xFF, (y >> 8) & 0xFF, y & 0xFF, 0, 0])
                self.queue.append(_packet(R.STATUS_OK, tid, 0x07, cls, cid, payload))
        elif (cls, cid) == (0x04, 0x86):
            assert size == 0x26, f"0x04:0x86 data_size must be 0x26, got 0x{size:02x}"
            if self.stages is None:
                self.queue.append(_packet(R.STATUS_NOT_SUPPORTED, tid, 0x26, cls, cid))
            else:
                payload = _stage_payload(self.stages, self.active, store=0x01)
                padded = payload + bytes(max(0, 0x26 - len(payload)))
                self.queue.append(_packet(R.STATUS_OK, tid, 0x26, cls, cid, padded))
        elif (cls, cid) == (0x04, 0x05):
            self.dpi_writes.append(req)
            if self.raise_on_write:
                raise OSError("simulated transport error after write dispatch")
            if self.reject_write_status is not None:
                self.queue.append(_packet(self.reject_write_status, tid, size, cls, cid))
                return 91
            # Issue #872 hardware pitfall: data_size!=0x07 or varstore!=0x01 returns
            # fake ACK 0x02 without changing the mouse's actual DPI.
            if size == 0x07 and args[0] == 0x01 and not self.fake_ack_without_change:
                x = (args[1] << 8) | args[2]
                y = (args[3] << 8) | args[4]
                self.dpi = (x, y)
            self.queue.append(_packet(R.STATUS_OK, tid, size, cls, cid, args))
        else:
            self.queue.append(_packet(R.STATUS_NOT_SUPPORTED, tid, size, cls, cid))
        return 91

    def handle_get(self) -> list[int]:
        if not self.queue:
            return list(_packet(R.STATUS_TIMEOUT, self.tid, 2, 0, 0))
        return list(self.queue.pop(0))


class V4DpiMouse:
    """In-memory Viper V4 Pro MI_03 91-byte Feature-report mouse."""

    def __init__(self, stages=(400, 800, 1600, 3200, 6400), active: int = 3,
                 raw_level: int = 184, tail: bytes = b"\xAA\x55"):
        self.stages = [(s, s) if isinstance(s, int) else tuple(s) for s in stages]
        self.active = active
        self.raw_level = raw_level
        self.tail = tail
        self.requests: list[bytes] = []
        self.writes: list[bytes] = []
        self.queue: list[bytes] = []
        self.opens = 0
        self.closes = 0
        self.fake_ack_without_change = False
        self.fail_dpi_read = False
        self.fail_readback = False
        self.reject_write_status: int | None = None
        self.busy_before_reply = 0
        self.foreign_before_reply = 0

    def handle_send(self, raw: bytes) -> int:
        assert len(raw) == 91 and raw[0] == 0x00
        req = bytes(raw[1:])
        assert req[88] == _crc(req), "V4 request CRC mismatch"
        self.requests.append(req)
        tid, size, cls, cid = req[1], req[5], req[6], req[7]
        args = req[8:8 + size]

        if cls == 0x04:
            for _ in range(self.busy_before_reply):
                self.queue.append(_packet(0x01, tid, size, cls, cid))
            for _ in range(self.foreign_before_reply):
                self.queue.append(_packet(0x02, tid, 0x03, 0x0F, 0x03, b"\x01\x02\x03"))

        if (cls, cid) == (0x07, 0x80):
            self.queue.append(_packet(0x02, 0x1F, 0x02, 0x07, 0x80, bytes([0x00, self.raw_level])))
        elif (cls, cid) == (0x04, 0x86):
            assert size == 0x50, f"V4 0x04:0x86 data_size must be 0x50 (80), got 0x{size:02x}"
            if self.fail_dpi_read or (self.fail_readback and self.writes):
                self.queue.append(_packet(0x05, 0x1F, 0x50, 0x04, 0x86))
            else:
                payload = _stage_payload(self.stages, self.active, store=0x01, tail=self.tail)
                padded = payload + bytes(max(0, 0x50 - len(payload)))
                self.queue.append(_packet(0x02, 0x1F, 0x50, 0x04, 0x86, padded))
        elif (cls, cid) == (0x04, 0x06):
            self.writes.append(req)
            expected_len = 3 + len(self.stages) * 7
            assert size == expected_len, f"V4 0x04:0x06 data_size must be {expected_len}, got {size}"
            if self.reject_write_status is not None:
                self.queue.append(_packet(self.reject_write_status, 0x1F, size, 0x04, 0x06))
                return 91
            if args[0] == 0x01 and not self.fake_ack_without_change:
                self.active = args[1]
                parsed = []
                for idx in range(args[2]):
                    o = 3 + idx * 7
                    parsed.append(((args[o + 1] << 8) | args[o + 2], (args[o + 3] << 8) | args[o + 4]))
                self.stages = parsed
            self.queue.append(_packet(0x02, 0x1F, size, 0x04, 0x06, args))
        else:
            self.queue.append(_packet(0x05, tid, size, cls, cid))
        return 91

    def handle_get(self) -> bytes:
        if not self.queue:
            return _packet(0x04, 0x1F, 2, 0, 0)
        return self.queue.pop(0)


class FakeHidBus:
    def __init__(self, devices_by_path):
        self.devices_by_path = dict(devices_by_path)

    def factory(self):
        bus = self

        class _Dev:
            def __init__(self):
                self.dev = None

            def open_path(self, path):
                if path not in bus.devices_by_path:
                    raise OSError("cannot open path")
                self.dev = bus.devices_by_path[path]
                self.dev.opens += 1

            def send_feature_report(self, data):
                return self.dev.handle_send(bytes(data))

            def get_feature_report(self, report_id, size):
                return self.dev.handle_get()

            def close(self):
                if self.dev is not None:
                    self.dev.closes += 1

        return _Dev


class RazerDpiTests(unittest.TestCase):
    def setUp(self):
        self._saved_r = (R.hid, R.hidlist, R.time)
        self._saved_v4 = (V4.hid, V4.time)
        R.time = types.SimpleNamespace(time=self._saved_r[2].time, sleep=lambda s: None)
        V4.time = types.SimpleNamespace(sleep=lambda s: None)

    def tearDown(self):
        R.hid, R.hidlist, R.time = self._saved_r
        V4.hid, V4.time = self._saved_v4

    def _bind(self, infos, by_path):
        bus = FakeHidBus(by_path)
        cls = bus.factory()
        R.hid = types.SimpleNamespace(device=cls)
        V4.hid = types.SimpleNamespace(device=cls)
        R.hidlist = types.SimpleNamespace(enumerate=lambda vid=0: list(infos))
        return bus

    # ---- 1. Protocol byte layout & OpenMouse 93c4cc6 verification -------------
    def test_classic_requests_match_openmouse_codec_bytes(self):
        read_xy = R.build_request(0x1F, 0x04, 0x85, 0x07, bytes([0x01]))
        self.assertEqual(len(read_xy), 90)
        self.assertEqual(list(read_xy[:9]), [0x00, 0x1F, 0x00, 0x00, 0x00, 0x07, 0x04, 0x85, 0x01])
        self.assertEqual(read_xy[88], _crc(read_xy))

        read_stages = R.build_request(0x1F, 0x04, 0x86, 0x26, bytes([0x00]))
        self.assertEqual(list(read_stages[:9]), [0x00, 0x1F, 0x00, 0x00, 0x00, 0x26, 0x04, 0x86, 0x00])
        self.assertEqual(read_stages[88], _crc(read_stages))

        write_xy = R.build_request(0x1F, 0x04, 0x05, 0x07,
                                   bytes([0x01, 0x06, 0x40, 0x06, 0x40, 0x00, 0x00]))
        self.assertEqual(list(write_xy[:15]),
                         [0x00, 0x1F, 0x00, 0x00, 0x00, 0x07, 0x04, 0x05,
                          0x01, 0x06, 0x40, 0x06, 0x40, 0x00, 0x00])
        self.assertNotEqual(write_xy[5], 0x04)  # #872 pitfall: data_size must be 0x07, never 0x04
        self.assertEqual(write_xy[8], 0x01)     # #872 pitfall: varstore must be 0x01
        self.assertEqual(write_xy[88], _crc(write_xy))

    # ---- 2. Classic mouse poll: onboard vs quick vs non-mouse isolation -------
    def test_viper_v3_pro_poll_reads_onboard_dpi_on_same_handle(self):
        info = classic_info(pid=0x00C1)
        mouse = ClassicDpiMouse(tid=0x1F, dpi=1600, stages=(400, 800, 1600, 3200, 6400), active=3)
        self._bind([info], {info["path"]: mouse})

        cards = R.RazerProvider().poll()
        self.assertEqual(len(cards), 1)
        card = cards[0]
        self.assertEqual((card.level, card.online), (80, True))
        self.assertEqual((mouse.opens, mouse.closes), (1, 1))
        self.assertIsNotNone(card.dpi)
        self.assertEqual(card.dpi["supported"], True)
        self.assertEqual(card.dpi["canSet"], True)
        self.assertEqual(card.dpi["current"], 1600)
        self.assertEqual(card.dpi["stages"], [400, 800, 1600, 3200, 6400])
        self.assertEqual(card.dpi["stageSource"], "onboard")
        self.assertRegex(card.dpi["targetToken"], r"^[a-f0-9]{64}$")
        self.assertIsNone(card.dpi["error"])
        self.assertIsInstance(card.dpi_target, dict)
        self.assertEqual([(r[6], r[7]) for r in mouse.requests],
                         [(0x07, 0x80), (0x07, 0x84), (0x04, 0x85), (0x04, 0x86)])

    def test_classic_mouse_falls_back_to_quick_stages_when_04_86_unsupported(self):
        info = classic_info(pid=0x0083, name="Razer Basilisk X HyperSpeed",
                            path=b"\\\\?\\hid#vid_1532&pid_0083#1",
                            physical="pnp:usb\\vid_1532&pid_0083\\1")
        mouse = ClassicDpiMouse(tid=0xFF, dpi=1200, stages=None)
        self._bind([info], {info["path"]: mouse})

        provider = R.RazerProvider()
        card, = provider.poll()
        self.assertEqual(card.dpi["stageSource"], "quick")
        self.assertEqual(card.dpi["canSet"], True)
        self.assertEqual(card.dpi["current"], 1200)
        self.assertEqual(card.dpi["stages"], [400, 800, 1200, 1600, 3200])
        # Second poll skips 0x04:0x86 once marked unsupported for (pid, path).
        mouse.requests.clear()
        card2, = provider.poll()
        self.assertEqual(card2.dpi["stageSource"], "quick")
        self.assertEqual([(r[6], r[7]) for r in mouse.requests],
                         [(0x07, 0x80), (0x07, 0x84), (0x04, 0x85)])

    def test_classic_mouse_asymmetric_xy_reports_read_only_error_without_dropping_battery(self):
        info = classic_info(pid=0x00C1)
        mouse = ClassicDpiMouse(tid=0x1F, dpi=(1600, 1800))
        self._bind([info], {info["path"]: mouse})

        card, = R.RazerProvider().poll()
        self.assertEqual(card.level, 80)
        self.assertEqual(card.dpi["supported"], True)
        self.assertEqual(card.dpi["canSet"], False)
        self.assertIsNone(card.dpi["current"])
        self.assertIsNone(card.dpi["targetToken"])
        self.assertEqual(card.dpi["error"], "dpi_axes_differ")
        self.assertIsNone(card.dpi_target)

    def test_dpi_failure_never_affects_battery_card(self):
        info = classic_info(pid=0x00C1)
        mouse = ClassicDpiMouse(tid=0x1F, dpi=1600)
        # Force 0x04:0x85 to return unsupported / out-of-range (0, 0).
        mouse.dpi = (0, 0)
        self._bind([info], {info["path"]: mouse})

        card, = R.RazerProvider().poll()
        self.assertEqual((card.level, card.online), (80, True))
        self.assertIsNone(card.dpi)
        self.assertIsNone(card.dpi_target)

    def test_headsets_keyboards_and_docks_never_receive_04_dpi_commands(self):
        non_mice = [
            classic_info(pid=0x025C, name="Razer BlackWidow V3 Pro",
                         path=b"kbd1", physical="pnp:usb\\vid_1532&pid_025c\\1"),
            classic_info(pid=0x0290, name="Razer DeathStalker V2 Pro", interface_number=2,
                         path=b"kbd2", physical="pnp:usb\\vid_1532&pid_0290\\1"),
            classic_info(pid=0x00A4, name="Razer Mouse Dock Pro",
                         path=b"dock1", physical="pnp:usb\\vid_1532&pid_00a4\\1"),
            classic_info(pid=0x003F, name="Razer Naga Epic Chroma",
                         path=b"dock2", physical="pnp:usb\\vid_1532&pid_003f\\1"),
            classic_info(pid=0x0555, name="Razer BlackShark V2 Pro (2023)",
                         path=b"hs1", physical="pnp:usb\\vid_1532&pid_0555\\1"),
            classic_info(pid=0x0999, name="Razer Nari Ultimate Wireless Headset",
                         path=b"hs2", physical="pnp:usb\\vid_1532&pid_0999\\1"),
            classic_info(pid=0x0998, name="Razer Barracuda Wireless",
                         path=b"hs3", physical="pnp:usb\\vid_1532&pid_0998\\1"),
        ]
        tids = {0x025C: 0x9F, 0x0290: 0x9F, 0x00A4: 0x1F, 0x003F: 0xFF,
                0x0555: 0x3F, 0x0999: 0x3F, 0x0998: 0x3F}
        by_path = {d["path"]: ClassicDpiMouse(tid=tids[d["product_id"]]) for d in non_mice}
        self._bind(non_mice, by_path)

        with patch.object(R.blackshark, "read_battery", return_value=("offline", None, None)):
            cards = R.RazerProvider().poll()
        self.assertGreaterEqual(len(cards), 4)
        for d in non_mice:
            dev = by_path[d["path"]]
            dpi_cmds = [r for r in dev.requests if r[6] == 0x04]
            self.assertEqual(dpi_cmds, [], f"non-mouse {d['product_string']} received DPI command!")

    def test_pack_mouse_never_polls_or_writes_dpi(self):
        pack = DevicePack(version=7, loaded=True, entries=(
            Entry("razer", "hyperspeed-mouse-1f", 1,
                  Match(0x1532, 0x00F9, interface_number=0, usage_page=1, usage=2),
                  "Razer Pack Mouse", "mouse", level="C", source="issue-999"),
        ))
        info = classic_info(pid=0x00F9, name="Razer Pack Mouse",
                            path=b"pack0", physical="pnp:usb\\vid_1532&pid_00f9\\1")
        mouse = ClassicDpiMouse(tid=0x1F)
        self._bind([info], {info["path"]: mouse})

        card, = R.RazerProvider(device_pack=pack).poll()
        self.assertEqual(card.model_source, "pack")
        self.assertIsNone(card.dpi)
        self.assertIsNone(card.dpi_target)
        self.assertEqual([r for r in mouse.requests if r[6] == 0x04], [])

    # ---- 3. Classic mouse set_device_dpi write + readback + safety gates ------
    def test_classic_set_device_dpi_onboard_and_quick_roundtrip(self):
        for stages, initial, target_dpi, expected_source in (
            ((400, 800, 1600, 3200, 6400), 1600, 3200, "onboard"),
            (None, 1200, 1600, "quick"),
        ):
            with self.subTest(source=expected_source):
                info = classic_info(pid=0x00C1)
                mouse = ClassicDpiMouse(tid=0x1F, dpi=initial, stages=stages)
                self._bind([info], {info["path"]: mouse})

                card, = R.RazerProvider().poll()
                self.assertEqual(card.dpi["stageSource"], expected_source)
                token = card.dpi["targetToken"]

                res = R.set_device_dpi([info], card.dpi_target, target_dpi, initial, token)
                self.assertTrue(res["success"])
                self.assertFalse(res["outcomeUnknown"])
                self.assertIsNone(res["error"])
                self.assertEqual(res["confirmedDpi"], target_dpi)
                self.assertEqual(res["dpi"]["current"], target_dpi)
                self.assertEqual(res["dpi"]["targetToken"], token)
                self.assertEqual(len(mouse.dpi_writes), 1)
                w = mouse.dpi_writes[0]
                self.assertEqual((w[5], w[6], w[7], w[8]), (0x07, 0x04, 0x05, 0x01))

    def test_classic_set_device_dpi_validation_and_prewrite_gates(self):
        info = classic_info(pid=0x00C1)
        mouse = ClassicDpiMouse(tid=0x1F, dpi=1600, stages=(400, 800, 1600, 3200))
        self._bind([info], {info["path"]: mouse})
        card, = R.RazerProvider().poll()
        target = card.dpi_target
        token = card.dpi["targetToken"]

        # Invalid target
        self.assertEqual(R.set_device_dpi([info], None, 800, 1600, token)["error"], "dpi_invalid_target")
        bad_target = dict(target, product_id=0x0528)  # headset PID rejected
        self.assertEqual(R.set_device_dpi([info], bad_target, 800, 1600, R.dpi_token(bad_target))["error"],
                         "dpi_invalid_target")

        # Stale / malformed targetToken
        self.assertEqual(R.set_device_dpi([info], target, 800, 1600, "bad")["error"], "dpi_stale_target")
        self.assertEqual(R.set_device_dpi([info], target, 800, 1600, "0" * 64)["error"], "dpi_stale_target")

        # Invalid / bool / out-of-range / unoffered value
        self.assertEqual(R.set_device_dpi([info], target, True, 1600, token)["error"], "dpi_invalid_value")
        self.assertEqual(R.set_device_dpi([info], target, 40, 1600, token)["error"], "dpi_invalid_value")
        self.assertEqual(R.set_device_dpi([info], target, 1200, 1600, token)["error"], "dpi_unsupported_value")

        # Inventory mismatch (disconnected or ambiguous duplicate)
        self.assertEqual(R.set_device_dpi([], target, 800, 1600, token)["error"], "dpi_target_changed")
        self.assertEqual(R.set_device_dpi([info, dict(info)], target, 800, 1600, token)["error"],
                         "dpi_target_changed")

        # Same value -> immediate success with zero writes
        noop = R.set_device_dpi([info], target, 1600, 1600, token)
        self.assertTrue(noop["success"])
        self.assertEqual(len(mouse.dpi_writes), 0)

        # Current changed on device before write
        mouse.dpi = (800, 800)
        changed = R.set_device_dpi([info], target, 3200, 1600, token)
        self.assertFalse(changed["success"])
        self.assertFalse(changed["outcomeUnknown"])
        self.assertEqual(changed["error"], "dpi_current_changed")
        self.assertEqual(len(mouse.dpi_writes), 0)

        # Current changed between table read and final pre-write XY check
        mouse.dpi = (1600, 1600)
        mouse.xy_reads = 0
        mouse.mutate_before_second_xy = 400
        race = R.set_device_dpi([info], target, 3200, 1600, token)
        self.assertFalse(race["success"])
        self.assertEqual(race["error"], "dpi_current_changed")
        self.assertEqual(len(mouse.dpi_writes), 0)
        mouse.mutate_before_second_xy = None

        # Onboard stage table changed in Synapse before write
        mouse.dpi = (1600, 1600)
        mouse.stages = [500, 1000, 1600, 4000]
        table_changed = R.set_device_dpi([info], target, 3200, 1600, token)
        self.assertFalse(table_changed["success"])
        self.assertEqual(table_changed["error"], "dpi_target_changed")
        self.assertEqual(len(mouse.dpi_writes), 0)

    def test_classic_write_fake_ack_or_readback_failure_sets_outcome_unknown_without_retry(self):
        info = classic_info(pid=0x00C1)
        mouse = ClassicDpiMouse(tid=0x1F, dpi=1600, stages=(400, 800, 1600, 3200))
        self._bind([info], {info["path"]: mouse})
        card, = R.RazerProvider().poll()
        target, token = card.dpi_target, card.dpi["targetToken"]

        # Fake ACK without changing DPI (#872 varstore/firmware pitfall)
        mouse.fake_ack_without_change = True
        res = R.set_device_dpi([info], target, 800, 1600, token)
        self.assertFalse(res["success"])
        self.assertTrue(res["outcomeUnknown"])
        self.assertEqual(res["error"], "dpi_readback_mismatch")
        self.assertIsNone(res["confirmedDpi"])
        self.assertIsNone(res["dpi"]["current"])
        self.assertEqual(len(mouse.dpi_writes), 1)  # never retried!

        # Readback timeout after write
        mouse.fake_ack_without_change = False
        mouse.dpi = (1600, 1600)
        mouse.dpi_writes.clear()
        mouse.fail_readback = True
        res2 = R.set_device_dpi([info], target, 800, 1600, token)
        self.assertFalse(res2["success"])
        self.assertTrue(res2["outcomeUnknown"])
        self.assertEqual(res2["error"], "dpi_readback_unconfirmed")
        self.assertEqual(len(mouse.dpi_writes), 1)

        # Exception during write dispatch still marks outcomeUnknown=True
        mouse.fail_readback = False
        mouse.dpi = (1600, 1600)
        mouse.dpi_writes.clear()
        mouse.raise_on_write = True
        res3 = R.set_device_dpi([info], target, 800, 1600, token)
        self.assertFalse(res3["success"])
        self.assertTrue(res3["outcomeUnknown"])
        self.assertEqual(res3["error"], "dpi_readback_unconfirmed")
        self.assertEqual(len(mouse.dpi_writes), 1)

    def test_classic_skips_synapse_foreign_packets_without_retrying_write(self):
        info = classic_info(pid=0x00C1)
        mouse = ClassicDpiMouse(tid=0x1F, dpi=1600, stages=(400, 800, 1600, 3200))
        mouse.foreign_before_reply = 3
        self._bind([info], {info["path"]: mouse})

        card, = R.RazerProvider().poll()
        self.assertEqual(card.dpi["current"], 1600)

        res = R.set_device_dpi([info], card.dpi_target, 3200, 1600, card.dpi["targetToken"])
        self.assertTrue(res["success"])
        self.assertEqual(res["confirmedDpi"], 3200)
        self.assertEqual(len(mouse.dpi_writes), 1)

    # ---- 4. Viper V4 Pro (1532:00E5 / 00E6, razer_v4.py) DPI read + write -----
    def test_viper_v4_pro_both_pids_poll_and_set_dpi_via_razer_entry(self):
        for pid in V4.PIDS:
            with self.subTest(pid=f"{pid:04x}"):
                info = v4_info(pid=pid)
                mouse = V4DpiMouse(stages=(400, 800, 1600, 3200, 6400), active=2, tail=b"\x12\x34")
                mouse.busy_before_reply = 1
                mouse.foreign_before_reply = 2
                self._bind([info], {info["path"]: mouse})

                card, = R.RazerProvider().poll()
                self.assertEqual((card.name, card.level, card.kind), ("Razer Viper V4 Pro", 72, "mouse"))
                self.assertEqual((mouse.opens, mouse.closes), (1, 1))
                self.assertEqual(card.dpi["stageSource"], "onboard")
                self.assertEqual(card.dpi["canSet"], True)
                self.assertEqual(card.dpi["current"], 800)
                self.assertEqual(card.dpi["stages"], [400, 800, 1600, 3200, 6400])
                token = card.dpi["targetToken"]
                self.assertRegex(token, r"^[a-f0-9]{64}$")

                res = R.set_device_dpi([info], card.dpi_target, 3200, 800, token)
                self.assertTrue(res["success"])
                self.assertFalse(res["outcomeUnknown"])
                self.assertEqual(res["confirmedDpi"], 3200)
                self.assertEqual(res["dpi"]["current"], 3200)
                self.assertEqual(res["dpi"]["targetToken"], token)
                self.assertEqual(len(mouse.writes), 1)
                w = mouse.writes[0]
                self.assertEqual((w[1], w[5], w[6], w[7], w[8], w[9], w[10]),
                                 (0x1F, 3 + 5 * 7, 0x04, 0x06, 0x01, 4, 5))
                # Trailing per-stage bytes are preserved byte-for-byte.
                self.assertEqual(w[8 + 3 + 5:8 + 3 + 7], b"\x12\x34")

    def test_viper_v4_pro_ambiguous_or_failed_dpi_preserves_battery_card(self):
        info = v4_info(pid=0x00E6)
        # Asymmetric XY stage -> read-only error on DPI, battery intact
        asym_mouse = V4DpiMouse(stages=[(800, 800), (1600, 1800)], active=1)
        self._bind([info], {info["path"]: asym_mouse})
        card, = R.RazerProvider().poll()
        self.assertEqual(card.level, 72)
        self.assertFalse(card.dpi["canSet"])
        self.assertEqual(card.dpi["error"], "dpi_ambiguous_stages")
        self.assertIsNone(card.dpi_target)

        # Total DPI read failure -> dpi is None, battery intact
        fail_mouse = V4DpiMouse()
        fail_mouse.fail_dpi_read = True
        self._bind([info], {info["path"]: fail_mouse})
        card2, = R.RazerProvider().poll()
        self.assertEqual(card2.level, 72)
        self.assertIsNone(card2.dpi)
        self.assertIsNone(card2.dpi_target)

    def test_viper_v4_pro_write_safety_gates_and_readback_mismatch(self):
        info = v4_info(pid=0x00E6)
        mouse = V4DpiMouse(stages=(400, 800, 1600, 3200), active=2)
        self._bind([info], {info["path"]: mouse})
        card, = R.RazerProvider().poll()
        target, token = card.dpi_target, card.dpi["targetToken"]

        # Unoffered stage
        self.assertEqual(R.set_device_dpi([info], target, 1200, 800, token)["error"],
                         "dpi_unsupported_value")
        # Stale expected_dpi
        self.assertEqual(R.set_device_dpi([info], target, 1600, 400, token)["error"],
                         "dpi_unsupported_value" if 400 not in target["stages"] else "dpi_current_changed")

        # Fake ACK without switching stage -> outcomeUnknown=True, 1 write only
        mouse.fake_ack_without_change = True
        mismatch = R.set_device_dpi([info], target, 1600, 800, token)
        self.assertFalse(mismatch["success"])
        self.assertTrue(mismatch["outcomeUnknown"])
        self.assertEqual(mismatch["error"], "dpi_readback_mismatch")
        self.assertEqual(len(mouse.writes), 1)

        # Readback failure after write -> outcomeUnknown=True, 1 write only
        mouse.fake_ack_without_change = False
        mouse.active = 2
        mouse.writes.clear()
        mouse.fail_readback = True
        unconfirmed = R.set_device_dpi([info], target, 1600, 800, token)
        self.assertFalse(unconfirmed["success"])
        self.assertTrue(unconfirmed["outcomeUnknown"])
        self.assertEqual(unconfirmed["error"], "dpi_readback_unconfirmed")
        self.assertEqual(len(mouse.writes), 1)


if __name__ == "__main__":
    unittest.main()
