"""Synthetic Valkyrie VK replies and fixtures; no real HID access."""
import ast
from contextlib import contextmanager
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ENGINE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(ENGINE))
import _hidstub  # noqa: F401,E402
import battery_hid as H  # noqa: E402
import catalog  # noqa: E402
import device_pack  # noqa: E402
import known_ids  # noqa: E402
import server  # noqa: E402
from providers import valkyrie as V  # noqa: E402

P = V.ValkyrieProvider
REQUEST = [3, 0x22, 0x50, 0, 2, 0x4F, 0x81] + [0] * 57
PIDS = {0xA301: "Valkyrie M1 PRO", 0xA302: "Valkyrie M1 PRO", 0xA304: "Valkyrie M2",
        0xA305: "Valkyrie M2", 0xA307: "Valkyrie M2 Pro", 0xA308: "Valkyrie M2 Pro",
        0xA312: "Valkyrie M3", 0xA313: "Valkyrie M3", 0xA315: "Valkyrie M3 PRO",
        0xA316: "Valkyrie M3 PRO", 0xA324: "Valkyrie M5 PRO", 0xA325: "Valkyrie M5 PRO",
        0xA327: "Valkyrie M5 PRO"}


def parent(pid=0xA305, tail="synthetic"):
    return rf"pnp:usb\vid_374a&pid_{pid:04x}\{tail}"


def entry(**changes):
    pid = changes.get("product_id", 0xA305)
    info = dict(vendor_id=0x374A, product_id=pid, usage_page=0xFF00, usage=1,
                path=b"vk-ff00", physical_id=parent(pid), product_string="Valkyrie",
                feature_report_length=64, feature_report_sizes={"3": 64})
    info.update(changes)
    return info


def reply(level=73, charging=False):
    return [3, 0x7F, 0, 9, 2, 0x4F, level | (0x80 if charging else 0)] + [0] * 57


@contextmanager
def fixture(frames=(), inventory=None, *, short=False, error_at=None):
    bus = SimpleNamespace(frames=list(frames), opened=[], closed=[], writes=[], reads=[], events=[])

    class Handle:
        def open_path(self, path):
            self.path = path
            bus.opened.append(path)
            if error_at == "open":
                raise OSError("private-device-path")

        def send_feature_report(self, frame):
            # Strict allowlist: any other request (reset, config, pairing, OTA) fails the test.
            assert list(frame) == REQUEST, "unexpected device command"
            bus.writes.append(list(frame))
            bus.events.append("set")
            if error_at == "set":
                raise OSError("private-device-path")
            return len(frame) - int(short)

        def get_feature_report(self, report_id, length):
            assert (report_id, length) == (3, 64), "unexpected feature query"
            bus.reads.append((report_id, length))
            bus.events.append("get")
            if error_at == "get":
                raise OSError("private-device-path")
            return bus.frames.pop(0) if bus.frames else []

        def close(self):
            bus.closed.append(self.path)
            if error_at == "close":
                raise OSError("private-device-path")

        # No write/read/output APIs: attempted use fails the test.

    bus.device = Handle
    with patch.object(H, "hid", bus), \
            patch.object(H.hidlist, "enumerate", return_value=[entry()] if inventory is None else inventory), \
            patch.object(V, "time", SimpleNamespace(sleep=lambda delay: bus.events.append(delay))):
        yield bus


class ProtocolTests(unittest.TestCase):
    def test_request_bytes_and_checksum(self):
        self.assertEqual(V.make_request(), REQUEST)
        self.assertEqual(len(REQUEST), 64)
        self.assertEqual(REQUEST[1], sum(REQUEST[2:]) & 0xFF)
        self.assertEqual(REQUEST[1], (0x50 + 0x00 + 0x02 + 0x4F + 0x81) & 0xFF)
        self.assertIsNot(V.make_request(), V.make_request())

    def test_reply_battery_and_charging_bit(self):
        for level, charging in ((1, False), (73, False), (100, False), (0, True), (73, True), (100, True)):
            with self.subTest(level=level, charging=charging):
                self.assertEqual(V.parse_reply(reply(level, charging)), (level, charging, ""))
                self.assertEqual(V.parse_reply(bytes(reply(level, charging))), (level, charging, ""))
                self.assertEqual(V.parse_reply(tuple(reply(level, charging))), (level, charging, ""))

    def test_all_byte6_patterns(self):
        for value in range(256):
            frame = reply()
            frame[6] = value
            level, charging = value & 0x7F, bool(value & 0x80)
            expected = (level, charging, "") if level <= 100 and (level or charging) else None
            with self.subTest(value=value):
                self.assertEqual(V.parse_reply(frame), expected)

    def test_checksum_byte_and_unproven_bytes_are_not_validated(self):
        for index, value in ((1, 0), (1, 0xFF), (3, 0), (3, 0xFF), (5, 0), (7, 0xFF), (63, 0xFF)):
            frame = reply()
            frame[index] = value
            with self.subTest(index=index, value=value):
                self.assertEqual(V.parse_reply(frame), (73, False, ""))

    def test_header_rejections(self):
        for index, values in ((0, (0, 1, 2, 4, 0xFF)), (2, (1, 2, 0xFF)), (4, (0, 1, 3, 0xFF))):
            for value in values:
                frame = reply()
                frame[index] = value
                with self.subTest(index=index, value=value):
                    self.assertIsNone(V.parse_reply(frame))

    def test_ack_nonzero_byte4_not_two_and_echo_rejected(self):
        self.assertIsNone(V.parse_reply(REQUEST))
        self.assertIsNone(V.parse_reply(bytes(REQUEST)))
        # Even a request echo with ACK/command bytes patched to look valid is not telemetry.
        echo = list(REQUEST)
        echo[2], echo[4] = 0, 2
        self.assertEqual(echo, REQUEST[:2] + [0, 0, 2] + REQUEST[5:])
        self.assertIsNone(V.parse_reply(REQUEST))

    def test_level_range_and_zero_placeholder(self):
        for value in range(101, 128):
            frame = reply()
            frame[6] = value
            self.assertIsNone(V.parse_reply(frame))
            frame[6] = value | 0x80
            self.assertIsNone(V.parse_reply(frame))
        for value, expected in ((0, None), (0x80, (0, True, ""))):
            frame = reply()
            frame[6] = value
            self.assertEqual(V.parse_reply(frame), expected)

    def test_malformed_values_and_types(self):
        good = reply()
        bad = [None, [], b"", "x" * 64, 64, object(), [0] * 64, [3] + [0] * 63,
               good[:-1], good + [0], bytes(good[:-1]), bytes(good) + b"\0"]
        for case in (bad,):
            for frame in case:
                with self.subTest(frame=repr(frame)[:40]):
                    self.assertIsNone(V.parse_reply(frame))
        for index, value in ((0, True), (2, False), (4, 2.0), (6, True), (9, -1), (9, 256), (9, "0"),
                             (63, 300), (10, None)):
            frame = reply()
            frame[index] = value
            with self.subTest(index=index, value=value):
                self.assertIsNone(V.parse_reply(frame))
        frame = reply()
        frame[6] = 0x80 | 73
        frame[0] = True  # bool is not an int byte even though True == 1... and != 3
        self.assertIsNone(V.parse_reply(frame))

    def test_bool_bytes_are_rejected_even_when_equal(self):
        # 1 == True; any bool anywhere in the frame is refused.
        frame = reply()
        frame[10] = True
        self.assertIsNone(V.parse_reply(frame))
        frame = reply()
        frame[10] = False
        self.assertIsNone(V.parse_reply(frame))

    def test_static_constants(self):
        tree = ast.parse(Path(V.__file__).read_text(encoding="utf-8"))
        literals = {n.targets[0].id: ast.literal_eval(n.value) for n in tree.body
                    if isinstance(n, ast.Assign) and isinstance(n.targets[0], ast.Name)
                    and n.targets[0].id in ("VID", "PIDS", "COLLECTIONS", "REPORT_ID", "REPORT_BYTES")}
        self.assertEqual(literals["VID"], 0x374A)
        self.assertEqual(literals["PIDS"], PIDS)
        self.assertEqual(literals["COLLECTIONS"], {(0xFF00, 1), (0xFF06, 1)})
        self.assertEqual((literals["REPORT_ID"], literals["REPORT_BYTES"]), (3, 64))
        self.assertEqual((P.name, P.kind, P.charging_known, P.verification_level, P.unconfirmed),
                         ("valkyrie", "mouse", True, "C", True))
        self.assertEqual(len(PIDS), 13)


class ReaderTests(unittest.TestCase):
    def assert_no_io(self, bus):
        self.assertEqual((bus.opened, bus.writes, bus.reads), ([], [], []))

    def test_empty_inventory_and_missing_backend_have_reasons(self):
        for backend_missing in (False, True):
            with self.subTest(backend_missing=backend_missing), fixture(inventory=[]) as bus, \
                    patch.object(H, "hid", None if backend_missing else bus):
                provider = P()
                self.assertEqual(provider.poll(), [])
                self.assertIn("no eligible Valkyrie collection or HID backend", provider.diagnostics()[0])
                self.assertEqual(bus.opened, [])

    def test_exact_exchange_and_status_for_every_pid(self):
        for pid, name in PIDS.items():
            for level, charging in ((5, False), (73, True), (100, False)):
                with self.subTest(pid=pid, level=level, charging=charging), \
                        fixture([reply(level, charging)], [entry(product_id=pid)]) as bus:
                    status, = P().poll()
                self.assertEqual(bus.writes, [REQUEST])
                self.assertEqual(bus.reads, [(3, 64)])
                self.assertEqual(bus.events, ["set", 0.100, "get"])
                self.assertEqual(bus.opened, [b"vk-ff00"])
                self.assertEqual(bus.opened, bus.closed)
                self.assertEqual((status.name, status.kind, status.source, status.verification_level),
                                 (name, "mouse", "valkyrie", "C"))
                self.assertEqual((status.level, status.charging, status.charging_known), (level, charging, True))
                self.assertTrue(status.unconfirmed)
                self.assertTrue(status.online)
                self.assertTrue(status.connection_known)
                self.assertEqual(status.physical_id, parent(pid))
                self.assertEqual(status.model_source, "builtin")

    def test_either_page_selected_by_proof_not_inventory_order(self):
        for page, other_page in ((0xFF00, 0xFF06), (0xFF06, 0xFF00)):
            proven = entry(usage_page=page)
            other = entry(usage_page=other_page, path=b"other", feature_report_sizes={"4": 64})
            for infos in ([proven, other], [other, proven]):
                with self.subTest(page=page, order=infos[0]["path"]), fixture([reply()], infos) as bus:
                    self.assertEqual(len(P().poll()), 1)
                    self.assertEqual(bus.opened, [proven["path"]])
                    self.assertEqual(bus.writes, [REQUEST])

    def test_ambiguous_ff00_and_ff06_never_open(self):
        a, b = entry(), entry(usage_page=0xFF06, path=b"vk-ff06")
        cases = [[a, b], [b, a], [a, dict(a, path=b"duplicate")], [b, dict(b, path=b"dup2")]]
        for infos in cases:
            with self.subTest(infos=[i["path"] for i in infos]):
                provider = P()
                with fixture([reply()], infos) as bus:
                    self.assertEqual(provider.poll(), [])
                self.assert_no_io(bus)
                self.assertIn("ambiguous proven Feature 03", " ".join(provider.diagnostics()))

    def test_zero_proven_collections_never_open(self):
        a, b = entry(), entry(usage_page=0xFF06, path=b"vk-ff06")
        for infos in ([], [dict(a, feature_report_sizes=None), dict(b, feature_report_sizes=None)],
                      [dict(a, feature_report_sizes={"4": 64}), dict(b, feature_report_sizes={"3": 63})]):
            with self.subTest(infos=infos), fixture([reply()], infos) as bus:
                self.assertEqual(P().poll(), [])
                self.assert_no_io(bus)

    def test_unproven_feature_03_64_never_opens(self):
        changes = [dict(feature_report_length=n) for n in (None, True, 0, 63, 65, 4097, "64", 64.0)]
        changes += [dict(feature_report_sizes=sizes) for sizes in
                    (None, {}, {3: 64}, {"3": 63}, {"3": 65}, {"4": 64}, {"3": 64.0}, {"3": True},
                     {"3": 64, "4": True}, {"3": 64, "4": 65}, [("3", 64)], "3:64")]
        for change in changes:
            with self.subTest(change=change), fixture([reply()], [entry(**change)]) as bus:
                self.assertEqual(P().poll(), [])
                self.assert_no_io(bus)

    def test_refusal_identifies_feature_proof_without_private_text(self):
        for proof, error, expected in (("caps", None, "proof=caps"),
                                       ("failed", "long item", "descriptor=long item"),
                                       ("failed", "private-path", "descriptor=unknown")):
            info = entry(feature_report_sizes=None, feature_report_proof=proof, report_descriptor_error=error)
            with self.subTest(proof=proof, error=error), fixture(inventory=[info]) as bus:
                provider = P()
                self.assertEqual(provider.poll(), [])
                text = " ".join(provider.diagnostics())
                self.assertIn("FF00:0001 Feature 03 length unproven", text)
                self.assertIn(expected, text)
                self.assertIn("not opened; nothing sent", text)
                self.assertNotIn("private-path", text)
                self.assert_no_io(bus)

    def test_per_report_proof_allows_other_larger_feature_ids(self):
        info = entry(feature_report_length=128, feature_report_sizes={"3": 64, "9": 128})
        with fixture([reply()], [info]) as bus:
            self.assertEqual(len(P().poll()), 1)
            self.assertEqual(bus.reads, [(3, 64)])
            self.assertEqual(bus.writes, [REQUEST])

    def test_unresolved_or_foreign_parent_never_opens(self):
        for physical in ("", "receiver-a", "collection:unknown", "collection:abc",
                         r"pnp:usb\vid_374a&pid_a305", r"pnp:usb\vid_374a&pid_a305\\",
                         r"pnp:usb\vid_374a&pid_a305\x\y", r"usb\vid_374a&pid_a305\x",
                         r"pnp:bth\vid_374a&pid_a305\x", r"pnp:usb\vid_374b&pid_a305\x",
                         r"pnp:usb\vid_374a&pid_a304\x",   # a different PID than the collection
                         r"pnp:usb\vid_374a&pid_a305&mi_01\x"):
            with self.subTest(physical=physical), fixture([reply()], [entry(physical_id=physical)]) as bus:
                self.assertEqual(P().poll(), [])
                self.assert_no_io(bus)
        without_parent = {k: v for k, v in entry().items() if k != "physical_id"}
        with fixture([reply()], [without_parent]) as bus:
            self.assertEqual(P().poll(), [])
            self.assert_no_io(bus)
        for bad_type in (None, 5):
            self.assertIsNone(P()._pick([entry(physical_id=bad_type)]))

    def test_resolved_parent_variants_are_accepted(self):
        for physical in (r"pnp:usb\vid_374a&pid_a305\5&1a2b3c&0&2", r"PNP:USB\VID_374A&PID_A305\ABC",
                         r"pnp:usb\vid_374a&pid_a305&rev_0200\123"):
            with self.subTest(physical=physical), fixture([reply()], [entry(physical_id=physical)]) as bus:
                self.assertEqual(len(P().poll()), 1)
                self.assertEqual(bus.writes, [REQUEST])

    def test_missing_path_never_opens(self):
        for path in (None, b"", ""):
            with self.subTest(path=path), fixture([reply()], [entry(path=path)]) as bus:
                provider = P()
                self.assertEqual(provider.poll(), [])
                self.assert_no_io(bus)
                self.assertIn("unresolved", " ".join(provider.diagnostics()))

    def test_unresolved_collection_parent_is_skipped_and_other_receiver_still_reads(self):
        infos = [entry(physical_id="collection:unresolved", path=b"unresolved"), entry()]
        provider = P()
        with fixture([reply(61)], infos) as bus:
            status, = provider.poll()
        self.assertEqual(status.level, 61)
        self.assertEqual(bus.opened, [b"vk-ff00"])
        self.assertIn("physical parent unresolved", " ".join(provider.diagnostics()))

    def test_foreign_vid_pid_collection_and_usage_never_open(self):
        changes = [dict(vendor_id=0x372E), dict(vendor_id=0x374B), dict(vendor_id=0x3151)]
        changes += [dict(product_id=pid, physical_id=parent(pid)) for pid in
                    (0xA300, 0xA303, 0xA306, 0xA309, 0xA311, 0xA314, 0xA317, 0xA322, 0xA323, 0xA326,
                     0xA328, 0xA329, 0xA205, 0x1026, 0x0000)]
        changes += [dict(usage_page=1), dict(usage_page=0xFF01), dict(usage_page=0xFF60), dict(usage=2),
                    dict(usage=0), dict(usage_page=0xFF05, usage=1)]
        for change in changes:
            with self.subTest(change=change), fixture([reply()], [entry(**change)]) as bus:
                self.assertEqual(P().poll(), [])
                self.assert_no_io(bus)

    def test_unrelated_collections_are_ignored_and_do_not_create_ambiguity(self):
        infos = [entry(), entry(usage_page=1, usage=2, path=b"mouse"), entry(usage_page=1, usage=6, path=b"kbd"),
                 entry(usage_page=0xFF60, usage=0x61, path=b"other-vendor-page"),
                 entry(product_id=0xA329, path=b"rongyuan-mouse", physical_id=parent(0xA329))]
        with fixture([reply(88)], infos) as bus:
            status, = P().poll()
        self.assertEqual(status.level, 88)
        self.assertEqual(bus.opened, [b"vk-ff00"])

    def test_receivers_are_independent(self):
        infos = [entry(), entry(usage_page=0xFF06, path=b"second", physical_id=parent(0xA305, "second")),
                 entry(path=b"usb-cable", product_id=0xA304, physical_id=parent(0xA304))]
        with fixture([reply(73), reply(24), reply(55, True)], infos) as bus:
            statuses = P().poll()
        self.assertEqual([s.level for s in statuses], [73, 24, 55])
        self.assertEqual(len({s.key for s in statuses}), 3)
        self.assertEqual(bus.opened, [b"vk-ff00", b"second", b"usb-cable"])
        self.assertEqual(bus.writes, [REQUEST] * 3)

    def test_invalid_replies_and_silence_do_not_trigger_fallback_commands(self):
        good = reply()
        frames = [None, [], [0] * 64, REQUEST, good[:-1], good + [0]]
        for index, values in ((0, (0, 2, 9)), (2, (1, 255)), (4, (0, 1, 3)), (6, (0, 101, 127, 229, 255 - 128))):
            for value in values:
                frame = reply()
                frame[index] = value
                frames.append(frame)
        for frame in frames:
            expected = V.parse_reply(frame)
            with self.subTest(frame=repr(frame)[:40]), fixture([frame]) as bus:
                provider = P()
                self.assertEqual(len(provider.poll()), int(expected is not None))
                self.assertEqual(bus.writes, [REQUEST])
                self.assertEqual(bus.reads, [(3, 64)])
                self.assertEqual(bus.opened, bus.closed)
                if expected is None:
                    self.assertIn("no valid battery reply", " ".join(provider.diagnostics()))

    def test_zero_percent_not_charging_never_publishes(self):
        with fixture([reply(0, False)]) as bus:
            self.assertEqual(P().poll(), [])
        self.assertEqual(bus.writes, [REQUEST])
        with fixture([reply(0, True)]):
            status, = P().poll()
        self.assertEqual((status.level, status.charging), (0, True))

    def test_short_write_and_io_errors_close_without_retry_or_private_diagnostics(self):
        for options in [dict(short=True)] + [dict(error_at=s) for s in ("open", "set", "get", "close")]:
            provider = P()
            with self.subTest(options=options), fixture([reply()], **options) as bus:
                result = provider.poll()
                self.assertEqual(bus.opened, bus.closed if options.get("error_at") != "open" else bus.closed)
                self.assertLessEqual(len(bus.writes), 1)
                self.assertLessEqual(len(bus.reads), 1)
                if options.get("short") or options.get("error_at") in ("open", "set"):
                    self.assertEqual((bus.reads, result), ([], []))
                self.assertNotIn("private-device-path", " ".join(provider.diagnostics()))

    def test_provider_only_uses_the_two_feature_calls(self):
        source = Path(V.__file__).read_text(encoding="utf-8")
        attributes = {n.attr for n in ast.walk(ast.parse(source)) if isinstance(n, ast.Attribute)}
        for forbidden in ("write", "read", "set_nonblocking", "get_input_report", "send_output_report"):
            self.assertNotIn(forbidden, attributes)
        self.assertEqual({a for a in attributes if a.endswith("feature_report")},
                         {"send_feature_report", "get_feature_report"})


class RegistrationTests(unittest.TestCase):
    def test_server_registration_and_diagnostics(self):
        self.assertIn(("valkyrie", "ValkyrieProvider"), server.PROVIDERS)
        self.assertEqual(server.PROVIDER_VIDS["valkyrie"], (0x374A,))
        self.assertIn("valkyrie", server.READER_DIAGNOSTICS)
        self.assertNotIn("valkyrie", server.NO_CHARGING_EVIDENCE)
        self.assertNotIn("valkyrie", server.DPI_PROVIDERS)
        self.assertIs(server.provider_class("valkyrie"), P)
        self.assertIn("valkyrie", device_pack.BRAND_PROVIDERS)

    def test_recognized_candidate_count_requires_exact_vendor_and_pid(self):
        inventory = [entry(), entry(path=b"x"), entry(product_id=0xA304), entry(vendor_id=0x1234),
                     entry(vendor_id=0x1234, product_id=0xA305), entry(product_id=0xA329),
                     entry(product_id=0xA205)]
        self.assertEqual(server.recognized_candidate_count("valkyrie", inventory), 2)
        self.assertEqual(server.recognized_candidate_count("valkyrie", inventory[3:]), 0)

    def test_reader_diagnostics_are_exported(self):
        provider = P()
        with fixture(inventory=[entry(physical_id="collection:x")]):
            provider.poll()
        self.assertTrue(server.reader_diagnostics(provider))

    def test_known_ids_index(self):
        self.assertEqual(known_ids.TABLES["valkyrie"], ("PIDS",))
        index = known_ids.build_index()
        self.assertNotIn("valkyrie", index["unparsed"])
        rows = {(d["vendorId"], d["productId"]): (d["name"], d["kind"])
                for d in index["devices"] if d["provider"] == "valkyrie"}
        self.assertEqual(rows, {(0x374A, pid): (name, "mouse") for pid, name in PIDS.items()})
        self.assertEqual(index["vendors"]["valkyrie"], [0x374A])
        # No other reader or family claims these exact identities.
        owners = {(d["vendorId"], d["productId"]): d["provider"] for d in index["devices"]
                  if d["provider"] != "valkyrie"}
        for pid in PIDS:
            self.assertNotIn((0x374A, pid), owners)

    def test_topology_gate_is_scoped_to_valkyrie_collections(self):
        source = (ENGINE / "topology.py").read_text(encoding="utf-8")
        self.assertIn("from providers.valkyrie import matches_collection", source)
        tree = ast.parse(source)
        blocks = [n for n in ast.walk(tree) if isinstance(n, ast.If)
                  and any(isinstance(c, ast.ImportFrom) and c.module == "providers.valkyrie" for c in ast.walk(n))]
        block = min(blocks, key=lambda n: n.end_lineno - n.lineno)
        text = ast.unparse(block)
        self.assertTrue(any(isinstance(n, ast.Constant) and n.value == 0x374A for n in ast.walk(block.test)))
        self.assertIn("byte_report_sizes(hid, data, caps, 2)", text)
        self.assertIn("_descriptor_fallback(info, caps, ('feature',))", text)
        self.assertIn("feature_report_length=caps.feature", text)
        self.assertNotIn("battery_caps", text)

    def test_matches_collection_is_independent_of_parent_and_reports(self):
        for pid in PIDS:
            for page in (0xFF00, 0xFF06):
                self.assertTrue(V.matches_collection(entry(product_id=pid, usage_page=page)))
        self.assertTrue(V.matches_collection(entry(physical_id=None, feature_report_sizes=None)))
        for change in (dict(usage=2), dict(usage_page=0xFF01), dict(vendor_id=0x372E),
                       dict(product_id=0xA329), dict(product_id=0x1026)):
            self.assertFalse(V.matches_collection(entry(**change)))

    def test_catalog_row_and_generated_docs(self):
        rows = [r for r in catalog.load_catalog()["readers"] if r["provider"] == "valkyrie"]
        self.assertEqual(len(rows), 1)
        row = rows[0]
        self.assertEqual((row["verificationLevel"], row["kinds"]), ("C", ["mouse"]))
        for text in ("374A", "A305", "#1093", "FF00:0001", "FF06:0001", "03 22 50 00 02 4F 81"):
            self.assertIn(text, row["matchScope"])
        for pid in PIDS:
            self.assertIn(f"{pid:04X}", row["matchScope"])
        self.assertEqual(catalog.verification_for("valkyrie")[0], "C")
        self.assertEqual(catalog.validate_catalog()["errors"], [])
        markdown = catalog.markdown()
        self.assertIn("Valkyrie VK M1 PRO", markdown)
        docs = (ENGINE.parent / "docs" / "compatibility.md").read_text(encoding="utf-8")
        self.assertIn("Valkyrie VK M1 PRO", docs)
        notes = (ENGINE / "upstream-protocols.md").read_text(encoding="utf-8")
        self.assertIn("## Valkyrie VK wireless mice (374A)", notes)

    def test_engine_marks_readings_unconfirmed_grade_c(self):
        engine = server.BatteryEngine([P()], include_native=True)
        self.addCleanup(engine.close)
        with fixture([reply(64, True)]):
            device, = engine.poll()["devices"]
        self.assertEqual((device["provider"], device["kind"], device["percent"], device["charging"]),
                         ("valkyrie", "mouse", 64, True))
        self.assertEqual((device["verificationLevel"], device["unconfirmed"], device["name"]),
                         ("C", True, "Valkyrie M2"))
        self.assertTrue(device["chargingKnown"])
        self.assertEqual(device["measurementState"], "current")


if __name__ == "__main__":
    unittest.main()
