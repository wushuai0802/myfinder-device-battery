"""OpenMouse-derived wire fixtures, with every HID/caps operation replaced."""
from contextlib import contextmanager
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
from providers import pulsar as P
from device_pack import DevicePack, Entry, Match


def response(command, data, address=0):
    frame = [8, command, 0, address >> 8, address & 255, len(data), *data]
    frame += [0] * (16 - len(frame))
    return frame + [(0x55 - sum(frame)) & 255]


_DEFAULT_ROWS = {
    "paw3955": ("8f 01 8f 01 00 35", "1f 03 1f 03 00 11"),
    "sensor3955": ("8f 01 8f 01 00 35", "1f 03 1f 03 00 11"),
    "paw3395se": ("09 09 00 43", "12 12 00 31"),
    "step50": ("07 07 00 47", "0f 0f 00 37"),
    "paw3950": ("07 07 00 47", "0f 0f 00 37"),
    "teevolution": ("07 07 00 47", "0f 0f 00 37"),
    "ultra": ("27 27 00 07", "4f 4f 00 b7"),
    "pulsar": ("27 27 00 07", "4f 4f 00 b7"),
    "paw3315": ("02 02 00 51", "06 06 00 49"),
    "paw3311": ("02 02 00 51", "06 06 00 49"),
}
REPRESENTATIVE_MODELS = (
    (0x373B, 0x12A7),  # ATK F1 V3 Ultimate + (PAW3955Master, 0x1B00, 6B)
    (0x373B, 0x1017),  # ATK X1 Pro Max wired (PAW3950, 0x000C, 4B)
    (0x373B, 0x101B),  # ATK Mouse 8K Dongle (2.4G receiver, 0x10 CID/MID)
    (0x373B, 0x11D9),  # ATK Wireless mouse 8k dongle-L (PAW3950Ultra, 0x000C, 4B)
    (0x373B, 0x1248),  # VXE X3 (PAW3315 stepped, 0x000C, 4B)
    (0x373B, 0x1084),  # VXE R1S+ (PAW3311 stepped, 0x000C, 4B)
    (0x3554, 0xF58C),  # VXE R1 Pro Max wired (PAW3395 step50, 0x000C, 4B)
    (0x3554, 0xF58F),  # VXE R1 SE+ wired (PAW3395SE, 0x000C, 4B)
    (0x3710, 0x5406),  # Pulsar X2 CrazyLight Mini 2.4G (pulsar codec, 0x000C, 4B)
    (0x3710, 0x3414),  # Pulsar X2 CrazyLight Mini wired (pulsar codec, 0x000C, 4B)
    (0x3554, 0xF508),  # Pulsar X2 V2 Mini 2.4G (step50, 0x000C, 4B)
    (0x3554, 0xF507),  # Pulsar X2 V2 Mini wired (step50, 0x000C, 4B)
    (0x3554, 0xF50D),  # Lamzu Atlantis 1K receiver (step50, 0x000C, 4B)
    (0x3554, 0xF50F),  # Lamzu Atlantis wired (step50, 0x000C, 4B)
    (0x3554, 0xF510),  # Lamzu Atlantis 4K receiver (step50, 0x000C, 4B)
    (0x3554, 0xF515),  # Scyrox V8S wired (sensor3955, 0x1B00, 6B)
    (0x3554, 0xF517),  # Scyrox V8S Turbo 8K dongle (sensor3955, 0x1B00, 6B)
    (0x3554, 0xF5F7),  # Scyrox V8 8K dongle (step50, 0x000C, 4B)
    (0x3554, 0xF54B),  # GravaStar Mercury 2.4G receiver (paw3950, 0x000C, 4B)
    (0x3554, 0xF549),  # GravaStar Mercury wired (paw3950, 0x000C, 4B)
    (0x3770, 0x0100),  # Hitscan Hyperlight wired (step50, 0x000C, 4B)
    (0x3770, 0x0200),  # Hitscan Hyperlight 2.4G receiver (step50, 0x000C, 4B)
    (0x3770, 0x0300),  # Hitscan Hyperlight 8K receiver (step50, 0x000C, 4B)
    (0x3554, 0xF500),  # Noir Gear M1-NEX wired (step50, 0x000C, 4B)
    (0x3554, 0xFB56),  # VGN Dragonfly F2 Master+ 2.4G receiver (step50, 0x000C, 4B)
    (0x3554, 0xFB57),  # VGN Dragonfly F2 Master+ wired (step50, 0x000C, 4B)
    (0x3554, 0xF505),  # VGN Dragonfly 4K Receiver (step50, 0x000C, 4B)
    (0x3554, 0xF520),  # Teevolution Terra Pro (teevolution, 0x000C, 4B)
)


class Mouse:
    def __init__(self, vid=0x373B, pid=0x12A7, product_string="Fixture Mouse"):
        self.info = dict(vendor_id=vid, product_id=pid, path=b"fixture-compx",
                         physical_id="fixture-physical", serial_number="fixture-serial",
                         product_string=product_string, usage_page=0xFF02, usage=2)
        spec = P.DPI_MODELS.get((vid, pid), ((1, 62), "paw3955", 0x1B00, 6, 6))
        self.cid = list(spec[0]) if spec[0] is not None else None
        self.base = spec[2]
        self.rows = [list(bytes.fromhex(s)) for s in _DEFAULT_ROWS[spec[1]]]
        self.system = [1, 0x54, 2, 0x53, 0, 0x55]
        self.frames, self.pending, self.opened = [], [], []
        self.closed = 0
        self.fault = None
        self.after_write = False
        self.reply_command = 8
        self.before_reply = None

    def open_path(self, path):
        assert path == self.info["path"]
        self.opened.append(path)

    def write(self, frame):
        frame = list(frame)
        assert len(frame) == 17 and sum(frame) % 256 == 0x55
        self.frames.append(frame)
        command, address, length = frame[1], frame[3] * 256 + frame[4], frame[5]
        if command == 7:
            # The fake refuses any write beyond the two active-stage bytes.
            assert address == 4 and length == 2 and frame[8:16] == [0] * 8
            self.after_write = True
            if self.fault == "write_error":
                raise OSError("fake driver completion unknown")
            if self.fault == "short_write":
                return 8
            if self.fault != "mismatch":
                self.system[4:6] = frame[6:8]
            return 17
        if self.before_reply:
            self.before_reply(self, command, address)
        if self.after_write and self.fault == "timeout":
            return 17
        if command == 4:
            data = [95, 0, 0x0F, 0x10]
        elif command == 0x10:
            data = self.cid
        else:
            assert command == 8
            if address == 0:
                assert length == 6
                data = list(self.system)
                if self.after_write and self.fault == "bad_pair":
                    data[5] ^= 1
            else:
                assert address >= self.base
                index = (address - self.base) // len(self.rows[0])
                assert address == self.base + index * len(self.rows[0])
                data = self.rows[index]
                assert length == len(data)
        answer = response(self.reply_command if command == 8 else command, data, address)
        if self.fault == "bad_frame":
            answer[-1] ^= 1
        self.pending.append(answer)
        return 17

    def read(self, size, timeout=0):
        return self.pending.pop(0) if self.pending else []

    def close(self):
        self.closed += 1

    def writes(self):
        return [frame for frame in self.frames if frame[1] == 7]


@contextmanager
def fake(mouse, length=17):
    with patch.object(P.hid, "device", return_value=mouse), \
            patch.object(P.hidlist, "enumerate", side_effect=lambda vid: [mouse.info] if vid == mouse.info["vendor_id"] else []), \
            patch.object(P, "output_length", return_value=length), \
            patch.object(P.time, "sleep"):
        yield mouse


def capture(mouse, provider=None):
    status, = (provider or P.PulsarProvider()).poll()
    return status.dpi, status.dpi_target


class CompXDpiTests(unittest.TestCase):
    def test_frame_rejects_more_than_ten_data_bytes(self):
        frame = P._dpi_frame(7, 4, 10, list(range(10)))
        self.assertEqual(len(frame), 17)
        self.assertEqual(sum(frame) % 256, 0x55)
        with self.assertRaises(ValueError):
            P._dpi_frame(7, 4, 11, list(range(11)))

    def test_allow_list_and_sensor_specific_read_frames(self):
        self.assertTrue(set(REPRESENTATIVE_MODELS).issubset(P.DPI_MODELS))
        self.assertEqual({vid for vid, _ in P.DPI_MODELS}, {0x3554, 0x373B, 0x3770, 0x3710})
        for vid, pid in P.DPI_MODELS:
            identity, _codec, base, length, _maximum = P.DPI_MODELS[(vid, pid)]
            with self.subTest(vid=vid, pid=pid), fake(Mouse(vid, pid)) as mouse:
                view, target = capture(mouse)
                self.assertEqual((view["current"], view["stages"], view["stageSource"]), (400, [400, 800], "onboard"))
                self.assertTrue(view["canSet"])
                self.assertEqual(target["identity"], mouse.cid)
                offset = 1
                if identity is not None:
                    self.assertEqual(mouse.frames[offset],
                                     list(bytes.fromhex("08 10 00 00 00 00 00 00 00 00 00 00 00 00 00 00 3d")))
                    offset += 1
                self.assertEqual(mouse.frames[offset],
                                 list(bytes.fromhex("08 08 00 00 00 06 00 00 00 00 00 00 00 00 00 00 3f")))
                expected = ("08 08 00 1b 00 06 00 00 00 00 00 00 00 00 00 00 24" if (base, length) == (0x1B00, 6)
                            else "08 08 00 00 0c 04 00 00 00 00 00 00 00 00 00 00 35")
                self.assertEqual(mouse.frames[offset + 1], list(bytes.fromhex(expected)))
                self.assertEqual(mouse.writes(), [])

    def test_switch_only_active_pair_once_and_read_back_system(self):
        for vid, pid in REPRESENTATIVE_MODELS:
            with self.subTest(vid=vid, pid=pid), fake(Mouse(vid, pid)) as mouse:
                view, target = capture(mouse)
                before = [list(row) for row in mouse.rows]
                result = P.set_device_dpi([mouse.info], target, 800, 400, view["targetToken"])
                self.assertTrue(result["success"], result)
                self.assertFalse(result["outcomeUnknown"])
                self.assertEqual(result["confirmedDpi"], 800)
                self.assertEqual(result["dpi"]["targetToken"], view["targetToken"])
                self.assertEqual(mouse.writes(), [list(bytes.fromhex("08 07 00 00 04 02 01 54 00 00 00 00 00 00 00 00 eb"))])
                self.assertEqual(mouse.rows, before)
                self.assertEqual(mouse.system[:4], [1, 0x54, 2, 0x53])
                self.assertEqual(mouse.closed, len(mouse.opened))
                self.assertTrue(any(f[1:6] == [8, 0, 0, 0, 6] for f in mouse.frames[mouse.frames.index(mouse.writes()[0]) + 1:]))

    def test_unknown_completion_never_retries_or_rolls_back(self):
        for fault in ("mismatch", "timeout", "write_error", "short_write", "bad_pair"):
            with self.subTest(fault=fault), fake(Mouse()) as mouse:
                view, target = capture(mouse)
                mouse.fault = fault
                result = P.set_device_dpi([mouse.info], target, 800, 400, view["targetToken"])
                self.assertFalse(result["success"])
                self.assertTrue(result["outcomeUnknown"])
                self.assertFalse(result["dpi"]["canSet"])
                self.assertIsNone(result["dpi"]["current"])
                self.assertIsNone(result["dpi"]["targetToken"])
                self.assertIsNone(result["confirmedDpi"])
                self.assertEqual(len(mouse.writes()), 1)
                self.assertEqual(mouse.closed, len(mouse.opened))

    def test_bad_frames_scalar_pairs_stages_and_counts_fail_closed(self):
        for fault in ("frame", "count_pair", "active_pair", "rate_pair", "stage", "zero_count", "too_many", "active", "identity"):
            with self.subTest(fault=fault), fake(Mouse()) as mouse:
                if fault == "frame":
                    mouse.fault = "bad_frame"
                elif fault.endswith("_pair"):
                    mouse.system[{"count_pair": 3, "active_pair": 5, "rate_pair": 1}[fault]] ^= 1
                elif fault == "stage":
                    mouse.rows[0][-1] ^= 1
                elif fault in ("zero_count", "too_many"):
                    count = 0 if fault == "zero_count" else 7
                    mouse.system[2:4] = [count, 0x55 - count]
                elif fault == "active":
                    mouse.system[4:6] = [2, 0x53]
                else:
                    mouse.cid = [1, 63]
                view, target = P._capture_dpi(mouse.info)
                self.assertIsNone(view)
                self.assertIsNone(target)
                self.assertEqual(mouse.writes(), [])

    def test_r1_eight_stages_and_read_response_with_write_command_id(self):
        with fake(Mouse(0x3554, 0xF58C)) as mouse:
            mouse.reply_command = 7
            mouse.rows = [[n, n, 0, (0x55 - 2 * n) & 255] for n in (1, 3, 7, 15, 23, 31, 63, 127)]
            mouse.system = [1, 0x54, 8, 0x4D, 7, 0x4E]
            view, target = capture(mouse)
            self.assertEqual(view["stages"], [100, 200, 400, 800, 1200, 1600, 3200, 6400])
            self.assertEqual(view["current"], 6400)
            self.assertTrue(P.set_device_dpi([mouse.info], target, 100, 6400, view["targetToken"])["success"])
            self.assertEqual(mouse.writes()[0][6:8], [0, 0x55])

    def test_excluded_ids_and_pack_have_no_dpi_io(self):
        for vid, pid in ((0x373B, 0xFFFF), (0x373B, 0x1085), (0x373B, 0x10C9),
                         (0x373B, 0x1278), (0x3554, 0xF58A), (0x3554, 0xF58E),
                         (0x3554, 0xF503), (0x3554, 0xF506), (0x3710, 0x5501),
                         (0x3554, 0xFB3E), (0x391D, 0x1A1A)):
            with self.subTest(pid=pid), fake(Mouse(vid, pid)) as mouse:
                self.assertEqual(P._capture_dpi(mouse.info), (None, None))
                self.assertEqual(mouse.frames, [])
        for vid, pid, name in ((0x373B, 0x12A7, "ATK Nearlink Mouse Dongle"),
                               (0x373B, 0x11D9, "ATK A9 PLUS 2.0 NK"),
                               (0x3554, 0xF58C, "VXE R1 Pro Max Receiver"),
                               (0x3554, 0xF58F, "VXE Mouse 1K Dongle"),
                               (0x3554, 0xF517, "EWEADN 8K Receiver")):
            with self.subTest(pid=pid, name=name), fake(Mouse(vid, pid, product_string=name)) as mouse:
                self.assertEqual(P._capture_dpi(mouse.info), (None, None))
                self.assertEqual(mouse.frames, [])
        with fake(Mouse()) as mouse:
            pack = DevicePack(version=1, loaded=True, entries=(Entry("pulsar", "fixture", 1,
                Match(0x373B, 0x12A7), "Pack Mouse", "mouse"),))
            device, = P.PulsarProvider(device_pack=pack).poll()
            self.assertIsNone(device.dpi)
            self.assertIsNone(device.dpi_target)
            self.assertEqual(device.model_source, "pack")
            self.assertEqual([f[1] for f in mouse.frames], [4])
            mouse.frames.clear()
            mouse.info["modelSource"] = "pack"
            self.assertEqual(P._capture_dpi(mouse.info), (None, None))
            self.assertEqual(mouse.frames, [])

    def test_wireless_24g_receivers_and_shared_atk_dongles_read_and_switch_stages(self):
        wireless_cases = (
            (0x373B, 0x101A, "ATK Mouse 1K Dongle", [2, 39], "0f 0f 00 37", "17 17 00 27", [800, 1200]),
            (0x373B, 0x101B, "ATK Mouse 8K Dongle", [1, 62], "8f 01 8f 01 00 35", "1f 03 1f 03 00 11", [400, 800]),
            (0x373B, 0x11D9, "Wireless mouse 8k dongle-L", [1, 8], "4f 4f 00 b7", "77 77 22 45", [800, 16000]),
            (0x373B, 0x11D5, "ATK dongle", [2, 121], "06 06 00 49", "0e 0e 00 39", [800, 1600]),
            (0x3710, 0x5406, "Pulsar 4K Dongle", None, "4f 4f 00 b7", "77 77 33 34", [800, 32000]),
            (0x3554, 0xF508, "Pulsar 4K Receiver", None, "07 07 00 47", "0f 0f 00 37", [400, 800]),
            (0x3554, 0xF50D, "LAMZU 1K Receiver", None, "07 07 00 47", "1f 1f 00 17", [400, 1600]),
            (0x3554, 0xF510, "LAMZU 4K Receiver", None, "07 07 00 47", "1f 1f 00 17", [400, 1600]),
            (0x3554, 0xF517, "Scyrox Turbo 8k dongle", None, "3f 06 3f 06 00 cb", "a7 61 a7 61 11 34", [1600, 50000]),
            (0x3554, 0xF5F7, "SCYROX 8K Dongle", None, "07 07 00 47", "1f 1f 00 17", [400, 1600]),
            (0x3554, 0xF54B, "GravaStar 4K Receiver", None, "07 07 00 47", "3f 3f 55 82", [400, 32000]),
            (0x3770, 0x0200, "Hitscan Wireless Dongle", None, "07 07 00 47", "4f 4f 11 a6", [400, 8000]),
            (0x3770, 0x0300, "Hitscan 8K Dongle", None, "07 07 00 47", "4f 4f 11 a6", [400, 8000]),
            (0x3554, 0xF500, "M1-NEX", None, "07 07 00 47", "1f 1f 00 17", [400, 1600]),
            (0x3554, 0xFB56, "VGN Mouse 2.4G Receiver", None, "07 07 00 47", "0f 0f 00 37", [400, 800]),
            (0x3554, 0xF505, "VGN Dragonfly 4K Receiver", None, "07 07 00 47", "0f 0f 00 37", [400, 800]),
            (0x3554, 0xF520, "Teevolution RapidSync 8K Dongle", None, "07 07 00 47", "a3 a3 55 ba", [400, 42000]),
        )
        for vid, pid, name, cid, row0, row1, expected in wireless_cases:
            with self.subTest(vid=vid, pid=pid, name=name), fake(Mouse(vid, pid, product_string=name)) as mouse:
                mouse.cid = cid
                mouse.rows = [list(bytes.fromhex(row0)), list(bytes.fromhex(row1))]
                mouse.base = 0x1B00 if len(mouse.rows[0]) == 6 else 0x000C
                view, target = capture(mouse)
                self.assertEqual((view["current"], view["stages"], view["canSet"]), (expected[0], expected, True))
                self.assertEqual(target["identity"], cid)
                result = P.set_device_dpi([mouse.info], target, expected[1], expected[0], view["targetToken"])
                self.assertTrue(result["success"], result)
                self.assertEqual(result["confirmedDpi"], expected[1])
                self.assertEqual(len(mouse.writes()), 1)

    def test_shared_vxe_wired_pids_accept_catalog_cid_mid_variants(self):
        for pid, cid, row0, row1, stages in (
            (0xF58C, [2, 7], "07 07 00 47", "0f 0f 00 37", [400, 800]),
            (0xF58F, [2, 29], "09 09 00 43", "12 12 00 31", [400, 800]),
            (0xF58F, [2, 31], "09 09 00 43", "12 12 00 31", [400, 800]),
            (0xF58F, [2, 11], "07 07 00 47", "0f 0f 00 37", [400, 800]),
            (0xF58F, [2, 12], "07 07 00 47", "0f 0f 00 37", [400, 800]),
        ):
            with self.subTest(pid=pid, cid=cid), fake(Mouse(0x3554, pid)) as mouse:
                mouse.cid = cid
                mouse.rows = [list(bytes.fromhex(row0)), list(bytes.fromhex(row1))]
                view, target = capture(mouse)
                self.assertEqual((view["current"], view["stages"]), (stages[0], stages))
                self.assertEqual(target["identity"], cid)
                result = P.set_device_dpi([mouse.info], target, stages[1], stages[0], view["targetToken"])
                self.assertTrue(result["success"], result)

    def test_target_and_descriptor_guards_before_open(self):
        with fake(Mouse()) as mouse:
            view, target = capture(mouse)
            mouse.frames.clear()
            for changed in (dict(mouse.info, path=b"other"), dict(mouse.info, physical_id="other"),
                            dict(mouse.info, serial_number="other"), dict(mouse.info, modelSource="pack"),
                            dict(mouse.info, usage_page=0xFF04), dict(mouse.info, product_id=0x12A8)):
                result = P.set_device_dpi([changed], target, 800, 400, view["targetToken"])
                self.assertFalse(result["success"])
            self.assertFalse(P.set_device_dpi([mouse.info], target, 800, 400, "b" * 64)["success"])
            self.assertFalse(P.set_device_dpi([mouse.info], target, 900, 400, view["targetToken"])["success"])
            self.assertEqual(mouse.frames, [])
        for length in (None, 16, 64):
            with fake(Mouse(), length) as mouse:
                self.assertEqual(P._capture_dpi(mouse.info), (None, None))
                self.assertEqual(mouse.frames, [])

    def test_live_current_identity_or_stages_changed_prevents_mutation(self):
        for changed in ("current", "identity", "stages", "during_read", "before_write"):
            with self.subTest(changed=changed), fake(Mouse()) as mouse:
                view, target = capture(mouse)
                if changed == "current":
                    mouse.system[4:6] = [1, 0x54]
                elif changed == "identity":
                    mouse.cid = [1, 63]
                elif changed == "stages":
                    mouse.rows.reverse()
                else:
                    count = [0]
                    def drift(m, command, address):
                        if command == 8 and address == 0:
                            count[0] += 1
                            if count[0] == (2 if changed == "during_read" else 3):
                                m.system[4:6] = [1, 0x54]
                    mouse.before_reply = drift
                result = P.set_device_dpi([mouse.info], target, 800, 400, view["targetToken"])
                self.assertFalse(result["success"])
                self.assertFalse(result["outcomeUnknown"])
                self.assertEqual(mouse.writes(), [])

    def test_duplicate_or_asymmetric_axes_are_read_only(self):
        for duplicate in (True, False):
            with fake(Mouse()) as mouse:
                if duplicate:
                    mouse.rows[1] = list(mouse.rows[0])
                else:
                    mouse.rows[0] = [0x8F, 1, 0x1F, 3, 0, 0xA3]  # X=400, Y=800
                view, target = capture(mouse)
                self.assertTrue(view["supported"])
                self.assertFalse(view["canSet"])
                self.assertIsNone(target)
                self.assertEqual(mouse.writes(), [])


class CompXDpiCacheTests(unittest.TestCase):
    def test_hit_reads_only_system_and_expires_from_the_full_read(self):
        for vid, pid in REPRESENTATIVE_MODELS:
            identity = P.DPI_MODELS[(vid, pid)][0]
            full_cmds = [4, 0x10, 8, 8, 8, 8] if identity is not None else [4, 8, 8, 8, 8]
            with self.subTest(vid=vid, pid=pid), fake(Mouse(vid, pid)) as mouse, \
                    patch.object(P.time, "monotonic", return_value=100.0) as clock:
                provider = P.PulsarProvider()
                first, target = capture(mouse, provider)
                mouse.rows.reverse()  # Unchanged system bytes allow the cached table.
                mouse.frames.clear()
                clock.return_value = 699.0
                hit, cached_target = capture(mouse, provider)
                self.assertEqual(hit["current"], 400)
                self.assertEqual(hit["stages"], first["stages"])
                self.assertEqual(hit["targetToken"], first["targetToken"])
                self.assertIs(cached_target, target)
                self.assertIsNot(hit, first)
                self.assertEqual([f[1] for f in mouse.frames], [4, 8])
                self.assertEqual(mouse.frames[1], list(bytes.fromhex(
                    "08 08 00 00 00 06 00 00 00 00 00 00 00 00 00 00 3f")))
                mouse.frames.clear()
                clock.return_value = 700.0
                expired, _ = capture(mouse, provider)
                self.assertEqual(expired["current"], 800)
                self.assertNotEqual(expired["targetToken"], first["targetToken"])
                self.assertEqual([f[1] for f in mouse.frames], full_cmds)
                self.assertEqual(mouse.writes(), [])
                self.assertEqual(mouse.closed, len(mouse.opened))

    def test_every_system_pair_change_triggers_a_complete_refresh(self):
        for offset in (0, 2, 4):
            with self.subTest(offset=offset), fake(Mouse()) as mouse:
                provider = P.PulsarProvider()
                capture(mouse, provider)
                mouse.frames.clear()
                value = 2 if offset == 0 else 1
                mouse.system[offset:offset + 2] = [value, 0x55 - value]
                if offset == 2:
                    mouse.rows = mouse.rows[:1]
                else:
                    mouse.rows.reverse()
                fresh, _ = capture(mouse, provider)
                self.assertEqual(fresh["stages"], [400] if offset == 2 else [800, 400])
                self.assertEqual(fresh["current"], 800 if offset == 0 else 400)
                self.assertEqual([f[1] for f in mouse.frames],
                                 [4, 8, 0x10] + [8] * (len(mouse.rows) + 2))
                mouse.frames.clear()
                self.assertEqual(capture(mouse, provider)[0]["stages"], fresh["stages"])
                self.assertEqual([f[1] for f in mouse.frames], [4, 8])

    def test_failed_or_invalid_system_probe_falls_back_to_a_full_read(self):
        for fault in ("timeout", "driver", "short_write", "frame", "system_pair", "count", "active"):
            with self.subTest(fault=fault), fake(Mouse()) as mouse:
                provider = P.PulsarProvider()
                first, _ = capture(mouse, provider)
                mouse.rows.reverse()
                mouse.frames.clear()
                original_write = mouse.write
                failed = False

                def fail_probe(frame):
                    nonlocal failed
                    if not failed and frame[1] == 8:
                        failed = True
                        mouse.frames.append(list(frame))
                        if fault == "driver":
                            raise OSError("fake probe failure")
                        if fault in ("timeout", "short_write"):
                            return 17 if fault == "timeout" else 8
                        system = list(mouse.system)
                        if fault == "system_pair":
                            system[1] ^= 1
                        elif fault == "count":
                            system[2:4] = [7, 0x4E]
                        elif fault == "active":
                            system[4:6] = [2, 0x53]
                        reply = response(8, system)
                        if fault == "frame":
                            reply[-1] ^= 1
                        mouse.pending.append(reply)
                        return 17
                    return original_write(frame)

                with patch.object(mouse, "write", side_effect=fail_probe):
                    refreshed, _ = capture(mouse, provider)
                self.assertTrue(failed)
                self.assertEqual(refreshed["current"], 800)
                self.assertNotEqual(refreshed["targetToken"], first["targetToken"])
                self.assertEqual([f[1] for f in mouse.frames], [4, 8, 0x10, 8, 8, 8, 8])
                mouse.frames.clear()
                self.assertEqual(capture(mouse, provider)[0]["current"], 800)
                self.assertEqual([f[1] for f in mouse.frames], [4, 8])
                self.assertEqual(mouse.closed, len(mouse.opened))

    def test_failed_complete_refresh_drops_cache_but_preserves_battery(self):
        with fake(Mouse()) as mouse:
            provider = P.PulsarProvider()
            capture(mouse, provider)
            mouse.system[:2] = [2, 0x53]
            mouse.cid = [1, 63]
            status, = provider.poll()
            self.assertEqual(status.level, 95)
            self.assertTrue(status.online)
            self.assertIsNone(status.dpi)
            self.assertIsNone(status.dpi_target)
            self.assertEqual(provider._dpi_cache, {})
            mouse.cid = [1, 62]
            mouse.frames.clear()
            self.assertIsNotNone(capture(mouse, provider)[0])
            self.assertEqual([f[1] for f in mouse.frames], [4, 0x10, 8, 8, 8, 8])

    def test_cache_is_per_instance_and_exact_device(self):
        for field in ("physical_id", "path", "serial_number"):
            with self.subTest(field=field), fake(Mouse()) as mouse:
                provider = P.PulsarProvider()
                capture(mouse, provider)
                mouse.info[field] = b"other-path" if field == "path" else "other-identity"
                mouse.frames.clear()
                capture(mouse, provider)
                self.assertEqual([f[1] for f in mouse.frames], [4, 0x10, 8, 8, 8, 8])
                mouse.frames.clear()
                capture(mouse, P.PulsarProvider())
                self.assertEqual([f[1] for f in mouse.frames], [4, 0x10, 8, 8, 8, 8])
        provider = P.PulsarProvider()
        for vid, pid in REPRESENTATIVE_MODELS:
            identity = P.DPI_MODELS[(vid, pid)][0]
            full_cmds = [4, 0x10, 8, 8, 8, 8] if identity is not None else [4, 8, 8, 8, 8]
            with self.subTest(vid=vid, pid=pid), fake(Mouse(vid, pid)) as mouse:
                capture(mouse, provider)
                self.assertEqual([f[1] for f in mouse.frames], full_cmds)

    def test_cached_read_only_stages_remain_read_only(self):
        for duplicate in (True, False):
            with self.subTest(duplicate=duplicate), fake(Mouse()) as mouse:
                if duplicate:
                    mouse.rows[1] = list(mouse.rows[0])
                else:
                    mouse.rows[0] = [0x8F, 1, 0x1F, 3, 0, 0xA3]
                provider = P.PulsarProvider()
                first, _ = capture(mouse, provider)
                mouse.frames.clear()
                hit, target = capture(mouse, provider)
                self.assertEqual(hit["stages"], first["stages"])
                self.assertFalse(hit["canSet"])
                self.assertIsNone(hit["targetToken"])
                self.assertIsNone(target)
                self.assertEqual([f[1] for f in mouse.frames], [4, 8])

    def test_writes_bypass_cache_and_success_or_unknown_clears_it(self):
        for fault in (None, "mismatch", "timeout", "write_error", "short_write", "bad_pair", "no_op"):
            with self.subTest(fault=fault), fake(Mouse()) as mouse:
                provider = P.PulsarProvider()
                view, target = capture(mouse, provider)
                mouse.frames.clear()
                mouse.fault = fault
                value = 400 if fault == "no_op" else 800
                result = provider.set_device_dpi([mouse.info], target, value, 400, view["targetToken"])
                self.assertEqual(result["success"], fault in (None, "no_op"))
                self.assertEqual(result["outcomeUnknown"], fault not in (None, "no_op"))
                self.assertEqual([f[1] for f in mouse.frames[:5]], [0x10, 8, 8, 8, 8])
                if fault != "no_op":
                    self.assertEqual([f[1] for f in mouse.frames[5:8]], [0x10, 8, 7])
                self.assertEqual(len(mouse.writes()), 0 if fault == "no_op" else 1)
                self.assertEqual(provider._dpi_cache, {})
                mouse.fault, mouse.after_write = None, False
                mouse.frames.clear()
                self.assertIsNotNone(capture(mouse, provider)[0])
                self.assertEqual([f[1] for f in mouse.frames], [4, 0x10, 8, 8, 8, 8])

    def test_cached_stage_table_cannot_authorize_a_write(self):
        with fake(Mouse()) as mouse:
            provider = P.PulsarProvider()
            view, target = capture(mouse, provider)
            mouse.rows.reverse()
            mouse.frames.clear()
            result = provider.set_device_dpi([mouse.info], target, 800, 400, view["targetToken"])
            self.assertFalse(result["success"])
            self.assertFalse(result["outcomeUnknown"])
            self.assertEqual(result["error"], "dpi_target_changed")
            self.assertEqual(mouse.writes(), [])
            self.assertEqual([f[1] for f in mouse.frames], [0x10, 8, 8, 8, 8])


if __name__ == "__main__":
    unittest.main()
