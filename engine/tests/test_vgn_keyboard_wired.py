"""320F:5055 wired keyboards: ATK HUB request/reply, exact-name gate and fail-closed I/O."""
from contextlib import contextmanager
import io
import json
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ENGINE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ENGINE))
sys.path.insert(0, str(ENGINE / "tests"))
import _hidstub  # noqa: F401 -- before importing any provider
import battery_hid as H
import catalog
import device_pack
import known_ids
import server
from isolation import wire_encode
from providers import vgn_keyboard as OLD, vgn_keyboard_wired as W
from test_new_brands import FakeHid

P = W.VgnKeyboardWiredProvider
PARENT = r"pnp:usb\vid_320f&pid_5055\fixture"
REQUEST = bytes.fromhex(
    "04 01 00 1A 00 00 00 00  00 00 00 00 00 00 00 00 "
    "00 00 00 00 00 00 00 00  00 00 00 00 00 00 00 00 "
    "00 00 00 00 00 00 00 00  00 00 00 00 00 00 00 00 "
    "00 00 00 00 00 00 00 00  00 00 00 00 00 00 00 00")
# ATK HUB DEVICE_PATHS["0x320f"]["0x5055"], written out independently of the provider.
ATK_HUB_NAMES = {
    "atkv75x", "vxev75x", "vgnvxe75v2", "vgnvxe75", "vxev87", "vxev87rgb",
    "kokomikeyboard108", "acheronkeyboard108", "acheronkeyboard87", "shenhekeyboard108",
    "kafka87", "kafka108", "vgnv98prov2", "vgnv98pro", "vxev75pro", "vgns99", "s99", "vgnn75pro"}


def entry(physical=PARENT, **changes):
    info = dict(vendor_id=0x320F, product_id=0x5055, usage_page=0xFF1C, usage=0x0092,
                interface_number=1, product_string="VGN S99",
                input_report_length=64, output_report_length=64,
                input_report_sizes={"4": 64}, output_report_sizes={"4": 64},
                physical_id=physical, path=(physical + "-vendor").encode(),
                serial_number="private-serial")
    info.update(changes)
    return info


def reply(level=57, charging=0, **changes):
    frame = [4, 1, 0, 0x1A, 0, 0, 0, 0, level, charging] + [0] * 54
    for offset, value in changes.items():
        frame[int(offset)] = value
    return frame


@contextmanager
def fixture(frames=(), inventory=None, **kwargs):
    bus = FakeHid(frames, **kwargs)
    with patch.object(H, "hid", bus), \
            patch.object(H.hidlist, "enumerate", return_value=[entry()] if inventory is None else inventory), \
            patch.object(H, "time", SimpleNamespace(monotonic=lambda: bus.now)):
        yield bus


class ProtocolTests(unittest.TestCase):
    def test_request_is_exactly_the_atk_hub_power_info_frame(self):
        self.assertEqual(bytes(W.make_request()), REQUEST)
        self.assertEqual(len(W.make_request()), 64)
        with fixture([reply()]) as bus:
            status, = P().poll()
        self.assertEqual(bus.writes, [(entry()["path"], list(REQUEST))])
        self.assertEqual(bus.reads[0][0], 64)
        self.assertEqual(bus.opened, bus.closed)
        self.assertEqual((status.level, status.kind, status.source, status.verification_level),
                         (57, "keyboard", "vgn_keyboard_wired", "C"))
        self.assertTrue(status.unconfirmed and status.charging_known and status.connection_known)
        self.assertTrue(status.online)
        self.assertFalse(status.charging)
        self.assertEqual(status.name, "VGN S99")
        self.assertEqual(status.physical_id, PARENT)

    def test_charging_state_is_byte_nine_equal_to_two(self):
        for value, expected in ((0, False), (1, False), (2, True), (3, False), (255, False)):
            with self.subTest(value=value):
                self.assertEqual(W.parse_reply(reply(80, value)), (80, expected, ""))
        with fixture([reply(80, 2)]):
            status, = P().poll()
        self.assertEqual((status.level, status.charging, status.charging_known), (80, True, True))

    def test_percentage_boundaries_and_zero_placeholder(self):
        for level in (1, 50, 100):
            self.assertEqual(W.parse_reply(reply(level))[0], level)
        self.assertIsNone(W.parse_reply(reply(0, 0)))        # uninitialised frame is never 0%
        self.assertIsNone(W.parse_reply(reply(0, 1)))
        self.assertEqual(W.parse_reply(reply(0, 2)), (0, True, ""))
        for level in (101, 200, 255):
            self.assertIsNone(W.parse_reply(reply(level)))
            self.assertIsNone(W.parse_reply(reply(level, 2)))

    def test_every_byte_container_parses(self):
        for container in (bytes, bytearray, list, tuple):
            self.assertEqual(W.parse_reply(container(reply(33, 2))), (33, True, ""))

    def test_malformed_wrong_header_and_echo_replies_are_rejected(self):
        good = reply()
        invalid = [None, True, 64, "x", object(), [], [0] * 64, good[:-1], good + [0],
                   W.make_request(), bytes(REQUEST)]
        invalid += [reply(**{"0": 3}), reply(**{"1": 0}), reply(**{"1": 2}), reply(**{"2": 1}),
                    reply(**{"3": 0x1B}), reply(**{"3": 0}), reply(**{"3": 0xFF}),
                    reply(**{"8": -1}), reply(**{"8": True}), reply(**{"9": True}),
                    reply(**{"9": -1}), reply(**{"9": 256})]
        for frame in invalid:
            with self.subTest(frame=frame):
                self.assertIsNone(W.parse_reply(frame))
        for frame in (good[:-1], reply(**{"0": 3}), reply(**{"1": 0}), W.make_request()):
            with self.subTest(wire=frame), fixture([frame]) as bus:
                self.assertEqual(P().poll(), [])
                self.assertEqual(len(bus.writes), 1)
                self.assertEqual(bus.opened, bus.closed)

    def test_echo_is_ignored_without_retrying_the_request(self):
        with fixture([W.make_request(), reply(61)]) as bus:
            self.assertEqual([s.level for s in P().poll()], [61])
            self.assertEqual((len(bus.writes), len(bus.reads)), (1, 2))

    def test_sleep_reply_has_no_reading_and_no_error(self):
        sleeping = reply(0, **{"3": 0xFF})
        self.assertIsNone(W.parse_reply(sleeping))
        self.assertTrue(W.no_reading(sleeping))
        for other in (reply(), reply(**{"0": 3, "3": 0xFF}), sleeping[:-1], None, True):
            self.assertFalse(W.no_reading(other))
        provider = P()
        with fixture([sleeping, reply(70)]) as bus:
            self.assertEqual(provider.poll(), [])
        self.assertEqual(provider.diagnostics(),
                         ["device asleep/offline; no new reading; connection unknown"])
        self.assertEqual((len(bus.writes), len(bus.reads)), (1, 1))
        self.assertEqual(bus.opened, bus.closed)

    def test_silence_and_short_write_and_io_errors(self):
        with fixture() as bus:
            self.assertEqual(P().poll(), [])
            self.assertEqual(len(bus.writes), 1)
            self.assertEqual(bus.opened, bus.closed)
        with fixture([reply()], write_result=63) as bus:
            self.assertEqual(P().poll(), [])
            self.assertEqual((len(bus.writes), len(bus.reads)), (1, 0))
        for stage in ("open", "write", "read"):
            with self.subTest(stage=stage), fixture([reply()], error_at=stage) as bus:
                provider = P()
                self.assertEqual(provider.poll(), [])
                self.assertEqual(bus.opened, bus.closed)
                self.assertNotIn("private", " ".join(provider.diagnostics()))

    def test_two_keyboards_keep_separate_cards(self):
        one, two = PARENT, r"pnp:usb\vid_320f&pid_5055\other"
        infos = [entry(one), entry(two, product_string="VXE V87")]
        with fixture([reply(40), reply(70)], infos) as bus:
            statuses = P().poll()
        self.assertEqual(sorted(s.level for s in statuses), [40, 70])
        self.assertEqual({s.name for s in statuses}, {"VGN S99", "VXE V87"})
        self.assertEqual(len({s.key for s in statuses}), 2)
        self.assertEqual(bus.opened, [infos[0]["path"], infos[1]["path"]])


class IdentityGateTests(unittest.TestCase):
    def assertNothingSent(self, inventory):
        with fixture([reply()], inventory) as bus:
            self.assertEqual(P().poll(), [])
            self.assertEqual((bus.opened, bus.writes, bus.reads), ([], [], []))

    def test_allow_list_is_exactly_atk_hub_320f_5055(self):
        self.assertEqual(set(W.PRODUCTS), ATK_HUB_NAMES)
        self.assertEqual(len(W.PRODUCTS), 18)
        self.assertEqual((W.VID, W.PIDS, W.COLLECTION), (0x320F, {0x5055: "VGN / VXE Wired Keyboard"}, (0xFF1C, 0x0092)))

    def test_every_listed_name_is_allowed_with_whitespace_and_case_variants(self):
        displays = ["ATK V75X", "VXE V75X", "VGN VXE75 V2", "VGN VXE75", "VXE V87", "VXE V87 RGB",
                    "Koko Mi Keyboard 108", "Acheron Keyboard 108", "Acheron Keyboard 87",
                    "Shenhe Keyboard 108", "Kafka 87", "Kafka 108", "VGN V98 PRO V2", "VGN V98 PRO",
                    "VXE V75 PRO", "VGN S99", "S99", "VGN N75 PRO"]
        self.assertEqual({"".join(n.split()).lower() for n in displays}, ATK_HUB_NAMES)
        for name in displays + [n.upper() for n in displays] + [" " + n + "\t" for n in displays]:
            with self.subTest(name=name), fixture([reply(44)], [entry(product_string=name)]) as bus:
                status, = P().poll()
                self.assertEqual(status.level, 44)
                self.assertEqual(status.name, name.strip())
                self.assertEqual(bus.writes, [(entry()["path"], list(REQUEST))])

    def test_unlisted_empty_or_missing_product_never_opens_or_sends(self):
        for name in ("Rainy75", "RAINY 75", "WOB", "Wireless Gaming Keyboard", "VGN S99 Pro", "S99x",
                     "VGN-S99", "VGN S99 2.4G Dongle", "VXE V87 Max", "", "   ", None, b"VGN S99",
                     99, ["VGN S99"], True):
            with self.subTest(name=name):
                self.assertNothingSent([entry(product_string=name)])
        self.assertNothingSent([{k: v for k, v in entry().items() if k != "product_string"}])

    def test_foreign_ids_and_collections_never_open_or_send(self):
        for changes in (dict(vendor_id=0x3554), dict(vendor_id=0), dict(product_id=0x5088),
                        dict(product_id=0x706E), dict(product_id=0x22BA), dict(product_id=0xFFFF),
                        dict(usage_page=1), dict(usage_page=0xFF1D), dict(usage=0x0091), dict(usage=0)):
            with self.subTest(changes=changes):
                self.assertNothingSent([entry(**changes)])

    def test_unresolved_wrong_or_ambiguous_parent_fails_closed(self):
        bad = [dict(physical_id=""), dict(physical_id=None), dict(physical_id="collection:private-path"),
               dict(physical_id=r"pnp:usb\vid_320f&pid_5088\fixture"),
               dict(physical_id=r"pnp:usb\vid_3554&pid_5055\fixture"),
               dict(physical_id=r"pnp:usb\vid_320f&pid_5055\a\b"),
               dict(physical_id=r"pnp:usb\vid_320f&pid_5055\ "),
               dict(physical_id=r"pnp:usb\vid_320f&pid_5055"),
               dict(physical_id=r"pnp:usb\vid_320f&pid_5055&mi_01\fixture"),
               dict(physical_id=r"device\vid_320f&pid_5055\fixture"),
               dict(path=b""), dict(path=None), dict(path=5)]
        for changes in bad:
            with self.subTest(changes=changes):
                self.assertNothingSent([entry(**changes)])

    def test_duplicate_collection_is_counted_before_any_other_gate(self):
        self.assertNothingSent([entry(), entry(path=b"second-collection")])
        # Even when one duplicate would fail the product or report gate by itself.
        self.assertNothingSent([entry(), entry(path=b"x", product_string="Rainy75")])
        self.assertNothingSent([entry(), entry(path=b"x", input_report_sizes=None)])
        # An ordinary other collection on the same parent does not disturb the target.
        with fixture([reply(52)], [entry(usage_page=1, usage=6, path=b"kbd"), entry()]) as bus:
            self.assertEqual([s.level for s in P().poll()], [52])
            self.assertEqual(bus.opened, [entry()["path"]])

    def test_a_bad_parent_does_not_hide_a_good_one(self):
        other = r"pnp:usb\vid_320f&pid_5055\good"
        infos = [entry("collection:private"), entry(other)]
        with fixture([reply(21)], infos) as bus:
            provider = P()
            self.assertEqual([s.level for s in provider.poll()], [21])
            self.assertEqual(bus.opened, [infos[1]["path"]])
        self.assertIn("physical parent unresolved; uniqueness unknown: nothing sent",
                      provider.diagnostics())

    def test_unproven_report_sizes_never_open_or_send(self):
        defects = [dict(input_report_length=None), dict(output_report_length=None),
                   dict(input_report_length=63), dict(input_report_length=65),
                   dict(output_report_length=63), dict(output_report_length=65),
                   dict(input_report_length=True), dict(output_report_length="64"),
                   dict(input_report_sizes=None), dict(output_report_sizes=None),
                   dict(input_report_sizes={}), dict(output_report_sizes={}),
                   dict(input_report_sizes={"3": 64}), dict(output_report_sizes={"3": 64}),
                   dict(input_report_sizes={"0": 64}), dict(output_report_sizes={"0": 64}),
                   dict(input_report_sizes={"4": 63}), dict(output_report_sizes={"4": 63}),
                   dict(input_report_sizes={"4": 64, "5": 64}),
                   dict(output_report_sizes={"4": 64, "5": 64}),
                   dict(input_report_sizes={"4": 64, "5": 2}),
                   dict(output_report_sizes={"4": 64, "99": 4097})]
        for changes in defects:
            with self.subTest(changes=changes):
                self.assertNothingSent([entry(**changes)])
        provider = P()
        with fixture([reply()], [entry(input_report_sizes=None)]):
            provider.poll()
        self.assertTrue(any("report IDs/lengths unknown or incompatible" in line
                            for line in provider.diagnostics()))

    def test_rainy75_style_shared_identity_leaves_no_card_and_diagnoses(self):
        provider = P()
        with fixture([reply()], [entry(product_string="Rainy75")]) as bus:
            self.assertEqual(provider.poll(), [])
            self.assertEqual((bus.opened, bus.writes), ([], []))
        self.assertEqual(provider.diagnostics(), ["unlisted keyboard product name: nothing sent"])

    def test_5088_reader_is_independent_and_still_grade_a(self):
        self.assertEqual(OLD.PIDS, {0x5088: "VGN 2.4G 键盘"})
        self.assertEqual(OLD.VgnKeyboardProvider.verification_level, "A")
        self.assertEqual(OLD.make_request(), [0x04, 0x00, 0x00, 0x1A, 0x06] + [0] * 59)
        self.assertEqual(catalog.verification_for("vgn_keyboard")[0], "A")
        self.assertNotEqual(OLD.make_request(), W.make_request())
        # The old reader never selects 5055 and this one never selects 5088.
        with H_fixture_for(OLD.VgnKeyboardProvider, [entry()]) as bus:
            self.assertEqual(OLD.VgnKeyboardProvider().poll(), [])
            self.assertEqual(bus.writes, [])
        self.assertNothingSent([entry(product_id=0x5088)])


@contextmanager
def H_fixture_for(factory, inventory):
    bus = FakeHid([reply()])
    with patch.object(H, "hid", bus), \
            patch.object(H.hidlist, "enumerate", return_value=inventory), \
            patch.object(H, "time", SimpleNamespace(monotonic=lambda: bus.now)):
        yield bus


class RegistrationTests(unittest.TestCase):
    def test_provider_contract(self):
        self.assertEqual((P.name, P.kind, P.verification_level, P.unconfirmed, P.charging_known),
                         ("vgn_keyboard_wired", "keyboard", "C", True, True))
        self.assertEqual((P.input_id, P.output_id, P.input_bounds, P.output_bytes),
                         (4, 4, (64, 64), 64))

    def test_server_registration(self):
        self.assertIn(("vgn_keyboard_wired", "VgnKeyboardWiredProvider"), server.PROVIDERS)
        self.assertIn(("vgn_keyboard", "VgnKeyboardProvider"), server.PROVIDERS)
        self.assertEqual(server.PROVIDER_VIDS["vgn_keyboard_wired"], (0x320F,))
        self.assertIn("vgn_keyboard_wired", server.READER_DIAGNOSTICS)
        self.assertNotIn("vgn_keyboard_wired", server.NO_CHARGING_EVIDENCE)
        self.assertIn("vgn_keyboard_wired", device_pack.BRAND_PROVIDERS)
        self.assertNotIn("vgn_keyboard_wired", server.DPI_PROVIDERS)
        self.assertIs(server.provider_class("vgn_keyboard_wired"), P)

    def test_candidate_count_counts_only_listed_product_names(self):
        count = lambda inventory: server.recognized_candidate_count("vgn_keyboard_wired", inventory)
        self.assertEqual(count([entry(), entry(path=b"x")]), 1)
        self.assertEqual(count([entry(product_string="Kafka 87")]), 1)
        self.assertEqual(count([entry(product_string="Rainy75")]), 0)
        self.assertEqual(count([entry(product_string="")]), 0)
        self.assertEqual(count([entry(product_string=None)]), 0)
        self.assertEqual(count([entry(vendor_id=0x1234)]), 0)
        self.assertEqual(count([entry(product_id=0x5088)]), 0)

    def test_known_id_index_and_model_gate_flag(self):
        index = known_ids.build_index()
        self.assertNotIn("vgn_keyboard_wired", index["unparsed"])
        row, = [r for r in index["devices"] if r["provider"] == "vgn_keyboard_wired"]
        self.assertEqual((row["vendorId"], row["productId"], row["kind"]), (0x320F, 0x5055, "keyboard"))
        self.assertTrue(row["requiresModelMatch"])
        old, = [r for r in index["devices"] if r["provider"] == "vgn_keyboard"]
        self.assertEqual(old["productId"], 0x5088)
        self.assertFalse(old.get("requiresModelMatch", False))

    def test_topology_collects_caps_for_5055(self):
        source = (ENGINE / "topology.py").read_text(encoding="utf-8")
        self.assertEqual(source.count("(0x320F, 0x5055, 0xFF1C, 0x0092)"), 1)
        self.assertEqual(source.count("(0x320F, 0x5088, 0xFF1C, 0x0092)"), 1)
        self.assertIn("from providers.vgn_keyboard_wired import product_allowed", source)
        self.assertIn('if product_allowed(info.get("product_string")):', source)

    def test_catalog_row_is_grade_c_and_scoped(self):
        rows = [r for r in catalog.load_catalog()["readers"] if r["provider"] == "vgn_keyboard_wired"]
        self.assertEqual(len(rows), 1)
        row = rows[0]
        self.assertEqual((row["verificationLevel"], row["kinds"]), ("C", ["keyboard"]))
        for text in ("320F:5055", "FF1C:0092", "产品名", "Rainy75", "04 01 00 1A", "待本项目实机验证"):
            self.assertIn(text, row["matchScope"])
        self.assertEqual(catalog.verification_for("vgn_keyboard_wired")[0], "C")
        self.assertEqual(catalog.validate_catalog()["errors"], [])
        protocols = (ENGINE / "upstream-protocols.md").read_text(encoding="utf-8")
        self.assertIn("### ATK / VGN / VXE wired keyboards (320F:5055)", protocols)
        self.assertEqual(row["reference"], "engine/upstream-protocols.md#atk--vgn--vxe-wired-keyboards-320f5055")


class EngineTests(unittest.TestCase):
    def test_card_carries_grade_c_unconfirmed_and_known_charging(self):
        engine = server.BatteryEngine([P()], include_native=True)
        self.addCleanup(engine.close)
        with fixture([reply(63, 2)]):
            device, = engine.poll()["devices"]
        self.assertEqual((device["percent"], device["verificationLevel"], device["kind"],
                          device["provider"]), (63, "C", "keyboard", "vgn_keyboard_wired"))
        self.assertTrue(device["unconfirmed"] and device["charging"] and device["chargingKnown"])
        self.assertTrue(device["fresh"])
        with fixture([reply(60, 1)]):
            device, = engine.poll()["devices"]
        self.assertEqual((device["percent"], device["charging"], device["chargingKnown"]),
                         (60, False, True))

    def test_sleep_keeps_history_without_error_and_never_creates_zero_card(self):
        engine = server.BatteryEngine([P()], include_native=True)
        self.addCleanup(engine.close)
        with fixture([reply(0, **{"3": 0xFF})]):
            first = engine.poll()
        self.assertEqual((first["devices"], first["errors"]), ([], []))
        with fixture([reply(75)]):
            awake, = engine.poll()["devices"]
        with fixture([reply(0, **{"3": 0xFF})]):
            snapshot = engine.poll()
        stale, = snapshot["devices"]
        self.assertEqual((stale["percent"], stale["fresh"], stale["connectionState"]),
                         (75, False, "unconfirmed"))
        self.assertEqual(stale["confirmedAtUtc"], awake["confirmedAtUtc"])
        self.assertEqual(snapshot["errors"], [])
        self.assertFalse(engine.failures)

    def test_scoped_child_query_exports_safe_diagnostics_and_candidate_count(self):
        infos = [entry(r"pnp:usb\vid_320f&pid_5055\one"), entry(r"pnp:usb\vid_320f&pid_5055\two")]
        request = dict(action="query", sequence=1, provider="vgn_keyboard_wired",
                       physicalId=r"pnp:usb\vid_320f&pid_5055\one", inventory=infos)
        data = (json.dumps(request, default=wire_encode) + "\n").encode()
        for frame, count in ((reply(57), 1), (reply(0, **{"3": 0xFF}), 0)):
            with self.subTest(count=count), fixture([frame], infos) as bus, \
                    patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                    patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
                server.reader_child()
            response = json.loads(output.getvalue())
            self.assertEqual(len(response["statuses"]), count)
            self.assertEqual(response["candidateCount"], 1)
            self.assertFalse(response.get("errorCode"))
            self.assertEqual(bus.opened, [infos[0]["path"]])
            self.assertEqual(bus.writes, [(infos[0]["path"], list(REQUEST))])
            if count:
                self.assertEqual(response["statuses"][0]["level"], 57)
                self.assertEqual(response["diagnostics"], [])
            else:
                self.assertEqual(response["diagnostics"],
                                 ["device asleep/offline; no new reading; connection unknown"])

    def test_shared_identity_is_not_dispatched_and_reports_no_candidate(self):
        request = dict(action="query", sequence=1, provider="vgn_keyboard_wired",
                       physicalId="private-one", inventory=[entry("private-one", product_string="Rainy75")])
        data = (json.dumps(request, default=wire_encode) + "\n").encode()
        with fixture([reply()], request["inventory"]) as bus, \
                patch.object(server.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data))), \
                patch.object(server.sys, "stdout", new_callable=io.StringIO) as output:
            server.reader_child()
        response = json.loads(output.getvalue())
        self.assertEqual((response["statuses"], response["candidateCount"]), ([], 0))
        self.assertEqual((bus.opened, bus.writes), ([], []))


if __name__ == "__main__":
    unittest.main()
