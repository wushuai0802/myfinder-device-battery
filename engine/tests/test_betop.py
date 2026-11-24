"""Synthetic BETOP gamepad fixtures; no device or network access."""
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
import server  # noqa: E402
from providers import betop as B  # noqa: E402

P = B.BetopProvider
PATH = rb"\\?\hid#vid_20bc&pid_5158&mi_02&col03#synthetic"


def physical(vid=0x20BC, pid=0x5158, tail="synthetic"):
    return rf"pnp:usb\vid_{vid:04x}&pid_{pid:04x}\{tail}"


def entry(vid=0x20BC, pid=0x5158, size=64, tail="synthetic", **changes):
    info = dict(
        vendor_id=vid, product_id=pid, usage_page=0x00FF, usage=3,
        interface_number=2, path=PATH + tail.encode(), physical_id=physical(vid, pid, tail),
        input_report_length=size, output_report_length=size,
        input_report_sizes={"2": size}, output_report_sizes={"2": size},
        product_string="",
    )
    info.update(changes)
    return info


def plain(level=73, state=0x50, size=64, command=0x15, third=0, report=2):
    raw = [0] * size
    raw[0:5] = [report, command, level, third, state]
    return raw


def scrambled(level=73, charging=False, size=64, command=0x15, third=0, report=2,
              level_raw=None, state=None):
    raw = [0] * size
    raw[0] = report
    raw[1] = B.scramble(command)
    raw[2] = B.scramble(level) if level_raw is None else level_raw
    raw[3] = B.scramble(third)
    raw[4] = B.scramble(0x51 if charging else 0x50) if state is None else B.scramble(state)
    return raw


def plain_request(size=64):
    return bytes([2, 0x15]) + bytes(size - 2)


def scrambled_request(size=64):
    return bytes([2, 0xAD]) + bytes(size - 2)


class Clock:
    def __init__(self, step=0.0001):
        self.value = 0
        self.step = step

    def monotonic(self):
        self.value += self.step
        return self.value


class FakeHid:
    """Answers only the two whitelisted requests; anything else raises."""

    def __init__(self, scrambled_replies=(), plain_replies=(), write_result=None,
                 fail=None, endless=None, size=64):
        self.responses = {0xAD: list(scrambled_replies), 0x15: list(plain_replies)}
        self.write_result = write_result
        self.fail = fail
        self.endless = endless
        self.size = size
        self.opened, self.closed, self.writes, self.reads = [], [], [], []

    def device(self):
        bus = self

        class Handle:
            def open_path(self, path):
                self.path = path
                self.pending = []
                bus.opened.append(path)
                if bus.fail == "open":
                    raise OSError("private-path")

            def write(self, packet):
                if (type(packet) is not bytes or len(packet) != bus.size
                        or packet not in (plain_request(bus.size), scrambled_request(bus.size))):
                    raise AssertionError("output outside the fixed whitelist")
                bus.writes.append(packet)
                if bus.fail == "write":
                    raise OSError("private-path")
                self.pending = list(bus.responses[packet[1]])
                return len(packet) if bus.write_result is None else bus.write_result

            def read(self, length, timeout):
                if not 1 <= timeout <= B.READ_TIMEOUT_MS or length != bus.size:
                    raise AssertionError("unbounded or unexpected read")
                bus.reads.append((length, timeout))
                if bus.fail == "read":
                    raise OSError("private-path")
                if self.pending:
                    return self.pending.pop(0)
                return list(bus.endless) if bus.endless else []

            def send_feature_report(self, *args):
                raise AssertionError("Feature output forbidden")

            def get_feature_report(self, *args):
                raise AssertionError("Feature input forbidden")

            def set_nonblocking(self, *args):
                raise AssertionError("configuration forbidden")

            def close(self):
                bus.closed.append(self.path)

        return Handle()


class Env:
    def __init__(self, bus, inventory, clock):
        self.bus, self.inventory, self.clock = bus, inventory, clock
        self.enumerated = []
        self.error = None

    def enumerate(self, vid):
        self.enumerated.append(vid)
        if self.error:
            raise self.error
        return [dict(d) for d in self.inventory if d.get("vendor_id") == vid]


@contextmanager
def fixture(scrambled_replies=(), plain_replies=(), inventory=None, clock_step=0.0001, **kwargs):
    bus = FakeHid(scrambled_replies, plain_replies, **kwargs)
    clock = Clock(clock_step)
    env = Env(bus, [entry()] if inventory is None else inventory, clock)
    with patch.object(B, "hid", bus), patch.object(
        B.hidlist, "enumerate", side_effect=env.enumerate,
    ), patch.object(B, "time", SimpleNamespace(monotonic=clock.monotonic)):
        yield env


class BetopTests(unittest.TestCase):
    def assert_no_io(self, env):
        self.assertEqual(env.bus.opened, [])
        self.assertEqual(env.bus.writes, [])
        self.assertEqual(env.bus.reads, [])

    # -- protocol primitives -------------------------------------------------
    def test_scramble_is_a_bijection_and_known_vectors(self):
        self.assertEqual(sorted(B.scramble(i) for i in range(256)), list(range(256)))
        self.assertEqual(sorted(B.NIBBLES), list(range(16)))
        for value in range(256):
            self.assertEqual(B.unscramble(B.scramble(value)), value)
            self.assertEqual(B.scramble(B.unscramble(value)), value)
        self.assertEqual(B.scramble(0x15), 0xAD)
        self.assertEqual(B.unscramble(0xAD), 0x15)
        self.assertEqual(B.scramble(0x00), 0x08)
        self.assertEqual(B.unscramble(0x00), 0x08)

    def test_requests_are_fixed_and_padded_to_the_proven_length(self):
        for size in (5, 17, 32, 64):
            self.assertEqual(B.make_request("plain", size), plain_request(size))
            self.assertEqual(B.make_request("scrambled", size), scrambled_request(size))
        self.assertEqual(B.make_request("plain", 5), bytes([2, 0x15, 0, 0, 0]))
        self.assertEqual(B.make_request("scrambled", 5), bytes([2, 0xAD, 0, 0, 0]))

    def test_pid_tables_are_exact_and_disjoint(self):
        groups = (B.SCRAMBLED_ONLY, B.BOTH_MODES, B.PLAIN_ONLY)
        self.assertEqual(sum(len(g) for g in groups), len(set().union(*groups)))
        self.assertEqual(set().union(*groups), {pid for _, pid in B.PIDS})
        self.assertEqual({vid for vid, _ in B.PIDS}, {0x20BC, 0x20DD})
        self.assertEqual(len(B.PIDS), 2 * 36)
        self.assertEqual(B.BOTH_MODES, {0x5125, 0x5127})
        for vid, pid in ((0x20BC, 0x5073), (0x20BC, 0x5075), (0x20DD, 0x5073), (0x20BC, 0x5000)):
            self.assertNotIn((vid, pid), B.PIDS)
        self.assertEqual(B.modes_for(0x5126), ("scrambled",))
        self.assertEqual(B.modes_for(0x5125), ("scrambled", "plain"))
        self.assertEqual(B.modes_for(0x5158), ("plain",))
        self.assertEqual(B.modes_for(0x5073), ())

    # -- plain / scrambled readings -------------------------------------------
    def test_plain_reading_levels_and_charging(self):
        for level, state, charging in ((1, 0x50, False), (73, 0x50, False), (100, 0x51, True),
                                       (50, 0x5F, True), (20, 0x52, True)):
            with self.subTest(level=level, state=state), fixture(
                plain_replies=[plain(level, state)],
            ) as env:
                reading, = P().poll()
                self.assertEqual((reading.level, reading.charging), (level, charging))
                self.assertEqual((reading.kind, reading.source, reading.online), ("gamepad", "betop", True))
                self.assertTrue(reading.charging_known)
                self.assertTrue(reading.connection_known)
                self.assertTrue(reading.unconfirmed)
                self.assertEqual(reading.verification_level, "C")
                self.assertEqual(env.bus.writes, [plain_request()])
                self.assertEqual(env.bus.opened, env.bus.closed)

    def test_plain_report_sizes_follow_the_descriptor(self):
        for size in (5, 17, 32, 64):
            with self.subTest(size=size), fixture(
                plain_replies=[plain(66, size=size)], size=size,
                inventory=[entry(size=size)],
            ) as env:
                reading, = P().poll()
                self.assertEqual(reading.level, 66)
                self.assertEqual(env.bus.writes, [plain_request(size)])
                self.assertEqual({r[0] for r in env.bus.reads}, {size})

    def test_scrambled_only_pid_sends_only_the_scrambled_request(self):
        for pid in sorted(B.SCRAMBLED_ONLY):
            with self.subTest(pid=pid), fixture(
                scrambled_replies=[scrambled(61, True)], inventory=[entry(pid=pid)],
            ) as env:
                reading, = P().poll()
                self.assertEqual((reading.level, reading.charging), (61, True))
                self.assertEqual(env.bus.writes, [scrambled_request()])
        with fixture(plain_replies=[plain(61)], inventory=[entry(pid=0x5126)]) as env:
            self.assertEqual(P().poll(), [])      # a plain reply never answers a scrambled-only PID
            self.assertEqual(env.bus.writes, [scrambled_request()])

    def test_plain_only_pid_never_sends_the_scrambled_request(self):
        for pid in sorted(B.PLAIN_ONLY):
            with self.subTest(pid=pid), fixture(
                plain_replies=[plain(40)], inventory=[entry(pid=pid)],
            ) as env:
                self.assertEqual([s.level for s in P().poll()], [40])
                self.assertEqual(env.bus.writes, [plain_request()])
        with fixture(scrambled_replies=[scrambled(40)], inventory=[entry(pid=0x5158)]) as env:
            self.assertEqual(P().poll(), [])
            self.assertEqual(env.bus.writes, [plain_request()])

    def test_both_mode_pids_try_scrambled_first_then_plain_once(self):
        for pid in sorted(B.BOTH_MODES):
            with self.subTest(pid=pid, case="scrambled answers"), fixture(
                scrambled_replies=[scrambled(80)], plain_replies=[plain(11)],
                inventory=[entry(pid=pid)],
            ) as env:
                reading, = P().poll()
                self.assertEqual(reading.level, 80)
                self.assertEqual(env.bus.writes, [scrambled_request()])
            with self.subTest(pid=pid, case="silent then plain"), fixture(
                plain_replies=[plain(11, 0x51)], inventory=[entry(pid=pid)],
            ) as env:
                reading, = P().poll()
                self.assertEqual((reading.level, reading.charging), (11, True))
                self.assertEqual(env.bus.writes, [scrambled_request(), plain_request()])
            with self.subTest(pid=pid, case="both silent"), fixture(
                inventory=[entry(pid=pid)],
            ) as env:
                provider = P()
                self.assertEqual(provider.poll(), [])
                self.assertEqual(env.bus.writes, [scrambled_request(), plain_request()])
                self.assertIn("no valid battery reply", " ".join(provider.diagnostics()))
            with self.subTest(pid=pid, case="invalid scrambled then plain"), fixture(
                scrambled_replies=[scrambled(0)], plain_replies=[plain(22)],
                inventory=[entry(pid=pid)],
            ) as env:
                self.assertEqual([s.level for s in P().poll()], [22])
                self.assertEqual(len(env.bus.writes), 2)

    def test_both_vendors_and_both_collection_orders_are_accepted(self):
        for vid in (0x20BC, 0x20DD):
            for page in (0x00FF, 0xFF00):
                with self.subTest(vid=vid, page=page), fixture(
                    plain_replies=[plain(35)], inventory=[entry(vid=vid, usage_page=page)],
                ) as env:
                    reading, = P().poll()
                    self.assertEqual(reading.level, 35)
                    self.assertIn(f"vid_{vid:04x}", reading.physical_id)
                    self.assertEqual(sorted(env.enumerated), [0x20BC, 0x20DD])

    def test_product_string_is_preferred_when_safe(self):
        for text, expected in (("BETOP Kunpeng 40 Wireless", "BETOP Kunpeng 40 Wireless"),
                               ("  BTP-X  ", "BTP-X"), ("", "BETOP gamepad"),
                               ("   ", "BETOP gamepad"), ("x" * 65, "BETOP gamepad"),
                               ("bad\x00name", "BETOP gamepad"), (None, "BETOP gamepad")):
            with self.subTest(text=text), fixture(
                plain_replies=[plain()], inventory=[entry(product_string=text)],
            ):
                self.assertEqual(P().poll()[0].name, expected)
        with fixture(scrambled_replies=[scrambled()], inventory=[entry(pid=0x5126)]):
            self.assertEqual(P().poll()[0].name, "BETOP Kunpeng 20")

    # -- reply validation ------------------------------------------------------
    def test_invalid_plain_replies_are_rejected(self):
        bad = {
            "level zero": plain(0),
            "level over 100": plain(101),
            "level 255": plain(255),
            "echo": plain(0, 0),
            "request echo": [2, 0x15] + [0] * 62,
            "wrong state nibble 40": plain(50, 0x40),
            "wrong state nibble 60": plain(50, 0x60),
            "wrong state nibble 00": plain(50, 0x00),
            "wrong command": plain(50, command=0x16),
            "scrambled command": plain(50, command=0xAD),
            "nonzero byte 3": plain(50, third=1),
            "wrong report id": plain(50, report=1),
            "report id 3": plain(50, report=3),
            "short": plain(50)[:63],
            "long": plain(50) + [0],
            "empty-ish": [2],
            "non-int": plain(50)[:4] + ["x"] + plain(50)[5:],
            "out of byte": plain(50)[:5] + [256] + plain(50)[6:],
            "bool": [True] + plain(50)[1:],
        }
        for label, raw in bad.items():
            with self.subTest(label=label), fixture(plain_replies=[raw]) as env:
                provider = P()
                self.assertEqual(provider.poll(), [])
                self.assertEqual(env.bus.writes, [plain_request()])
                self.assertIn("no valid battery reply", " ".join(provider.diagnostics()))

    def test_invalid_scrambled_replies_are_rejected(self):
        bad = {
            "level zero": scrambled(0),
            "level over 100": scrambled(101),
            "level 255": scrambled(255),
            "request echo": [2, 0xAD] + [0] * 62,
            "plain frame": plain(50),
            "wrong command": scrambled(50, command=0x16),
            "plain command": [2, 0x15, B.scramble(50), B.scramble(0), B.scramble(0x50)] + [0] * 59,
            "nonzero byte 3": scrambled(50, third=1),
            "wrong report id": scrambled(50, report=1),
            "scrambled report id": scrambled(50, report=B.scramble(2)),
            "short": scrambled(50)[:63],
            "long": scrambled(50) + [0],
            "non-int": scrambled(50)[:4] + [None] + scrambled(50)[5:],
        }
        for label, raw in bad.items():
            with self.subTest(label=label), fixture(
                scrambled_replies=[raw], inventory=[entry(pid=0x5126)],
            ) as env:
                self.assertEqual(P().poll(), [])
                self.assertEqual(env.bus.writes, [scrambled_request()])

    def test_scrambled_charging_follows_the_low_state_nibble(self):
        for state, charging in ((0x50, False), (0x51, True), (0x5F, True), (0x00, False)):
            with self.subTest(state=state), fixture(
                scrambled_replies=[scrambled(70, state=state)], inventory=[entry(pid=0x5126)],
            ):
                reading, = P().poll()
                self.assertEqual((reading.level, reading.charging), (70, charging))

    def test_unrelated_and_invalid_reports_are_skipped_until_a_valid_one(self):
        junk = [plain(0), [2, 0x15] + [0] * 62, [1] + [0] * 63, plain(120), plain(50, 0x40)]
        with fixture(plain_replies=junk + [plain(57, 0x51)]) as env:
            reading, = P().poll()
            self.assertEqual((reading.level, reading.charging), (57, True))
            self.assertEqual(len(env.bus.writes), 1)
        with fixture(scrambled_replies=[scrambled(0), plain(9), scrambled(44)],
                     inventory=[entry(pid=0x5126)]):
            self.assertEqual(P().poll()[0].level, 44)

    def test_read_loop_is_bounded(self):
        with fixture(endless=plain(0)) as env:
            self.assertEqual(P().poll(), [])
        self.assertEqual(len(env.bus.reads), B.MAX_READS)
        self.assertEqual(len(env.bus.writes), 1)
        with fixture(endless=plain(0), inventory=[entry(pid=0x5125)]) as env:
            self.assertEqual(P().poll(), [])
        self.assertEqual(len(env.bus.writes), 2)
        self.assertLessEqual(len(env.bus.reads), 2 * B.MAX_READS)
        with fixture([plain(50)], plain_replies=[plain(50)], clock_step=B.READ_TIMEOUT_MS) as env:
            # Time is already exhausted: the write happened, but no read waits on it.
            self.assertEqual(P().poll(), [])
            self.assertEqual(env.bus.reads, [])

    def test_short_writes_and_io_failures_never_retry(self):
        for result in (0, 63, 65, -1, None, True, "64"):
            if result is None:
                continue
            with self.subTest(result=result), fixture(
                plain_replies=[plain()], write_result=result, inventory=[entry(pid=0x5125)],
            ) as env:
                provider = P()
                self.assertEqual(provider.poll(), [])
                self.assertEqual(len(env.bus.writes), 1)        # no retry, no plain fallback
                self.assertEqual(env.bus.reads, [])
                self.assertIn("short/failed", " ".join(provider.diagnostics()))
        for phase in ("open", "write", "read"):
            with self.subTest(phase=phase), fixture([scrambled()], [plain()], fail=phase) as env:
                provider = P()
                self.assertEqual(provider.poll(), [])
                self.assertNotIn("private-path", " ".join(provider.diagnostics()))
                self.assertLessEqual(len(env.bus.writes), 1)
                self.assertEqual(env.bus.opened, env.bus.closed)

    # -- identity and descriptor gates ----------------------------------------
    def test_other_collections_identities_and_usages_never_open(self):
        changes = [
            dict(usage=1), dict(usage=2), dict(usage=4), dict(usage=5), dict(usage=6),
            dict(usage=0x0103), dict(usage_page=0x0001), dict(usage_page=0x0001, usage=5),
            dict(usage_page=0x0001, usage=6), dict(usage_page=0xFF01), dict(usage_page=0x000C),
            dict(usage_page=0x00FE), dict(usage_page=0x0000, usage=0),
            dict(product_id=0x5073, physical_id=physical(pid=0x5073)),
            dict(product_id=0x5075, physical_id=physical(pid=0x5075)),
            dict(product_id=0x5000, physical_id=physical(pid=0x5000)),
            dict(vendor_id=0x258A), dict(vendor_id=0x045E),
        ]
        for change in changes:
            with self.subTest(change=change), fixture(
                [scrambled()], [plain()], inventory=[entry(**change)],
            ) as env:
                self.assertEqual(P().poll(), [])
                self.assert_no_io(env)

    def test_unlisted_pids_on_betop_vendors_never_open(self):
        for vid in (0x20BC, 0x20DD):
            for pid in (0x5073, 0x5075, 0x5124, 0x5129, 0x5171, 0x0001, 0xFFFF):
                with self.subTest(vid=vid, pid=pid), fixture(
                    [scrambled()], [plain()], inventory=[entry(vid=vid, pid=pid)],
                ) as env:
                    self.assertEqual(P().poll(), [])
                    self.assert_no_io(env)

    def test_exact_numbered_input_and_output_proof_required(self):
        for direction in ("input", "output"):
            changes = [
                {direction + "_report_length": length}
                for length in (None, 4, 65, 63, "64", True, 0)
            ] + [
                {direction + "_report_sizes": sizes}
                for sizes in (None, {}, {"0": 64}, {"1": 64}, {"3": 64}, {"2": 63}, {"2": 65},
                              {"2": 4}, {"2": "64"}, {2: 64}, {"2": True}, {"2": 64, "x": 1},
                              {"2": 64, "3": None}, [("2", 64)])
            ]
            for change in changes:
                with self.subTest(change=change), fixture(
                    [scrambled()], [plain()], inventory=[entry(**change)],
                ) as env:
                    self.assertEqual(P().poll(), [])
                    self.assert_no_io(env)
        for change in (dict(input_report_sizes={"2": 64, "1": 20}),
                       dict(output_report_sizes={"2": 64, "5": 8})):
            with self.subTest(change=change), fixture(plain_replies=[plain()],
                                                      inventory=[entry(**change)]):
                self.assertEqual(P().poll()[0].level, 73)   # extra IDs are fine; 02 must match the max

    def test_missing_or_bad_paths_never_open(self):
        for path in (None, b"", "", 0, [], {}):
            with self.subTest(path=path), fixture([scrambled()], [plain()],
                                                  inventory=[entry(path=path)]) as env:
                self.assertEqual(P().poll(), [])
                self.assert_no_io(env)

    def test_parent_must_be_a_matching_usb_parent(self):
        bad = (None, "", "receiver-a", "collection:unresolved",
               r"pnp:usb\vid_20bc&pid_5159\synthetic",           # other PID
               r"pnp:usb\vid_20dd&pid_5158\synthetic",           # other vendor
               r"pnp:usb\vid_20bc&pid_5158&mi_02\synthetic",
               r"pnp:usb\vid_20bc&pid_5158\\",
               r"pnp:usb\vid_20bc&pid_5158",
               r"pnp:hid\vid_20bc&pid_5158\synthetic",
               r"pnp:usb\vid_20bc&pid_5158\syn thetic",
               5, b"pnp:usb\\vid_20bc&pid_5158\\synthetic")
        for value in bad:
            for inventory in ([entry(physical_id=value)],
                              [entry(physical_id=value), entry(tail="other")]):
                with self.subTest(physical=value, count=len(inventory)), fixture(
                    [scrambled()], [plain()], inventory=inventory,
                ) as env:
                    self.assertEqual(P().poll(), [])      # unresolved target blocks resolved receivers too
                    self.assert_no_io(env)
        with fixture(plain_replies=[plain()], inventory=[
            entry(physical_id=r"pnp:usb\VID_20BC&PID_5158&REV_0100\Synthetic".replace("Synthetic", "synthetic"))
        ]):
            self.assertEqual(len(P().poll()), 1)

    def test_duplicates_are_counted_before_path_or_report_proof(self):
        for change in ({}, dict(path=None), dict(input_report_sizes=None), dict(output_report_length=65),
                       dict(usage_page=0xFF00), dict(physical_id=physical().upper())):
            with self.subTest(change=change), fixture(
                [scrambled()], [plain()], inventory=[entry(), entry(**change)],
            ) as env:
                provider = P()
                self.assertEqual(provider.poll(), [])
                self.assert_no_io(env)
                self.assertIn("ambiguous", " ".join(provider.diagnostics()))

    def test_non_target_collections_do_not_create_duplicates(self):
        with fixture(plain_replies=[plain(81)], inventory=[
            entry(), entry(usage_page=0x0001, usage=5), entry(usage_page=0x0001, usage=6),
            entry(usage=2), entry(pid=0x5073, physical_id="collection:foreign"),
            entry(vendor_id=0x258A, physical_id="collection:foreign"),
        ]) as env:
            reading, = P().poll()
        self.assertEqual(reading.level, 81)
        self.assertEqual(len(env.bus.writes), 1)

    def test_two_receivers_are_read_independently(self):
        inventory = [entry(tail="a"), entry(pid=0x5126, tail="b"), entry(vid=0x20DD, tail="c")]
        with fixture([scrambled(33)], [plain(44)], inventory=inventory) as env:
            statuses = P().poll()
        self.assertEqual(sorted(s.level for s in statuses), [33, 44, 44])
        self.assertEqual(len({s.key for s in statuses}), 3)
        self.assertEqual(len(env.bus.writes), 3)

    # -- history ----------------------------------------------------------------
    def test_history_survives_silence_until_the_receiver_leaves(self):
        with fixture(plain_replies=[plain(61, 0x51)]) as env:
            provider = P()
            first, = provider.poll()
            self.assertTrue(first.online and not first.cached)
            env.bus.responses[0x15] = []
            for changes in ({}, {"input_report_sizes": None}, {"usage": 5},
                            {"physical_id": "collection:temporarily-unresolved"},
                            {"usage_page": 0xFF00, "path": None}):
                env.inventory = [entry(**changes)]
                writes = len(env.bus.writes)
                stale, = provider.poll()
                self.assertEqual((stale.level, stale.online, stale.cached, stale.wake_on_input),
                                 (61, False, True, True))
                self.assertEqual(stale.key, first.key)
                self.assertEqual(stale.measured_at_utc, first.measured_at_utc)
                self.assertFalse(stale.connection_known)
                self.assertFalse(stale.charging_known)
                public = server.normalise(stale, server.normalise(first, None, "2026-11-05T01:00:00Z"),
                                          "2026-11-06T01:00:00Z")
                self.assertEqual(public["measurementState"], "cached")
                self.assertIsNone(public["charging"])
                if changes in ({"input_report_sizes": None}, {"usage": 5}):
                    self.assertEqual(len(env.bus.writes), writes)   # proof lost: nothing sent
            env.inventory = [entry(), entry(tail="dup")]
            self.assertTrue(provider.poll()[0].cached)             # ambiguous receiver keeps history
            env.inventory = []
            self.assertEqual(provider.poll(), [])                  # unplugged
            env.inventory = [entry()]
            self.assertEqual(provider.poll(), [])                  # replug cannot revive old history
            env.bus.responses[0x15] = [plain(70)]
            self.assertEqual(provider.poll()[0].level, 70)

    def test_history_is_per_receiver_and_survives_enumeration_failure(self):
        infos = [entry(tail="a"), entry(tail="b")]
        with fixture(plain_replies=[plain(12)], inventory=infos) as env:
            provider = P()
            first = provider.poll()
            self.assertEqual(len(first), 2)
            env.bus.responses[0x15] = []
            env.inventory = [infos[1]]
            only, = provider.poll()
            self.assertEqual(only.key, first[1].key)
            self.assertTrue(only.cached)
            env.inventory = infos
            env.error = OSError("private-path")
            writes = len(env.bus.writes)
            kept = provider.poll()
            self.assertEqual([s.key for s in kept], [first[1].key])
            self.assertTrue(kept[0].cached)
            self.assertEqual(len(env.bus.writes), writes)
            self.assertNotIn("private-path", " ".join(provider.diagnostics()))

    def test_stale_history_never_overrides_a_fresh_reading(self):
        with fixture(plain_replies=[plain(30)]) as env:
            provider = P()
            provider.poll()
            env.bus.responses[0x15] = [plain(29)]
            reading, = provider.poll()
            self.assertEqual((reading.level, reading.cached, reading.online), (29, False, True))

    # -- registration -----------------------------------------------------------
    def test_topology_proof_is_scoped_and_preserves_failed_candidates(self):
        tree = ast.parse((ENGINE / "topology.py").read_text(encoding="utf-8"))
        blocks = [
            node for node in ast.walk(tree)
            if isinstance(node, ast.If)
            and any(isinstance(child, ast.ImportFrom) and child.module == "providers.betop"
                    for child in ast.walk(node))
        ]
        block = min(blocks, key=lambda node: node.end_lineno - node.lineno)
        text = ast.unparse(block)
        constants = {node.value for node in ast.walk(block.test) if isinstance(node, ast.Constant)}
        self.assertTrue({0x20BC, 0x20DD, 0x00FF, 0xFF00, 0x0003} <= constants)
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
        tuples, mappings = [], []
        for node in ast.walk(tree):
            if isinstance(node, (ast.Tuple, ast.Dict)):
                try:
                    value = ast.literal_eval(node)
                except (ValueError, TypeError, SyntaxError):
                    continue
                (mappings if isinstance(value, dict) else tuples).append(value)
        self.assertIn(("betop", "BetopProvider"), tuples)
        self.assertTrue(any(m.get("betop") == (0x20BC, 0x20DD) for m in mappings))
        self.assertIn(("flydigi", "betop", "bluetooth", "xinput"), tuples)
        self.assertIn("betop", server.READER_DIAGNOSTICS)
        self.assertEqual(server.PROVIDER_VIDS["betop"], (0x20BC, 0x20DD))
        self.assertNotIn("betop", server.NO_CHARGING_EVIDENCE)
        self.assertEqual(known_ids.TABLES["betop"], ("PIDS",))
        index = known_ids.build_index()
        rows = {(d["vendorId"], d["productId"]) for d in index["devices"] if d["provider"] == "betop"}
        self.assertEqual(rows, set(B.PIDS))
        self.assertEqual(index["vendors"]["betop"], [0x20BC, 0x20DD])
        self.assertTrue(all(d["kind"] == "gamepad" for d in index["devices"] if d["provider"] == "betop"))
        self.assertNotIn("betop", index["unparsed"])
        self.assertNotIn((0x20BC, 0x5073), rows)
        self.assertEqual(server.recognized_candidate_count("betop", [entry(), entry(vid=0x20DD, pid=0x5126),
                                                                     entry(pid=0x5073), entry(vendor_id=1)]), 2)
        verification, _ = catalog.verification_for("betop")
        self.assertEqual(verification, "C")
        self.assertEqual(catalog.validate_catalog()["errors"], [])

    def test_gamepad_statuses_are_accepted_only_from_the_matching_reader(self):
        with fixture(plain_replies=[plain(64, 0x51)]) as env:
            provider = P()
            status, = provider.poll()
            engine = server.BatteryEngine(providers=[provider])
            engine._accept("reader", "betop", [status], server.utcnow(), status.physical_id, 1)
            device, = engine.by_provider["reader"].values()
            self.assertEqual((device["kind"], device["percent"], device["provider"]), ("gamepad", 64, "betop"))
            self.assertEqual(device["measurementState"], "current")
            self.assertTrue(device["chargingKnown"])
            self.assertTrue(device["charging"])
            self.assertEqual(device["verificationLevel"], "C")
            engine._accept("reader2", "razer", [status], server.utcnow(), "", 0)
            self.assertEqual(engine.by_provider["reader2"], {})   # foreign reader may not publish gamepads
            from dataclasses import replace
            engine._accept("reader3", "betop", [replace(status, source="flydigi")], server.utcnow(), "", 0)
            self.assertEqual(engine.by_provider["reader3"], {})   # source and reader must agree

    def test_provider_never_writes_anything_but_the_two_requests(self):
        source = Path(B.__file__).read_text(encoding="utf-8")
        tree = ast.parse(source)
        attributes = {n.attr for n in ast.walk(tree) if isinstance(n, ast.Attribute)}
        for forbidden in ("send_feature_report", "get_feature_report", "set_nonblocking",
                          "write_feature", "send_output_report"):
            self.assertNotIn(forbidden, attributes)
        writes = [n for n in ast.walk(tree) if isinstance(n, ast.Call)
                  and isinstance(n.func, ast.Attribute) and n.func.attr == "write"]
        self.assertEqual(len(writes), 1)


if __name__ == "__main__":
    unittest.main()
