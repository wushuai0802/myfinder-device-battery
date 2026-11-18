"""AULA (hec.aulacn.com web driver) direct HID battery read, C / unverified.

Protocol facts are independently implemented from the vendor web driver bundle
app.c35ca532.js: buildPkt_final frames 0xAA output reports, Hy/us parse 0x55
echoes, getElectricityInfo polls the keyboard 0x10 device-info command and
reads deviceArray[17] as the percentage, and the mouse/receiver stack Ry uses
0xA0 with payload[17] battery and payload[18] charge. The 55 FB 02 push carries
the same fields but arrives unprompted and is ignored here.
Evidence and limits: engine/upstream-protocols.md.
"""
import battery_hid as H

VID = 0x0C45

# Exact (vendor, product) pairs from the official web driver's requestDevice
# filters, never a cross product. 0x28A6/0x17EF are borrowed vendor IDs the
# driver itself binds, so they stay candidate-only identities. The driver's
# borrowed `05AC:024F` entry is deliberately NOT enrolled: the same identity is
# registered by the EPOMAKER HUB for TH108/TH87 keyboards on FF68:0061 and is
# the maintainer's INFI75 identity (docs/epomaker-hub-rongyuan.md), so brand
# cannot be proven before any query is sent.
PIDS = {
    (0x0C45, 0x8031): "AULA（0C45:8031）",
    (0x0C45, 0x8032): "AULA（0C45:8032）",
    (0x0C45, 0xFEFE): "AULA（0C45:FEFE）",
    (0x0C45, 0xFEFC): "AULA（0C45:FEFC）",
    (0x0C45, 0x80A1): "AULA（0C45:80A1）",
    (0x0C45, 0x80A2): "AULA（0C45:80A2）",
    (0x0C45, 0x8801): "AULA（0C45:8801）",
    (0x0C45, 0x80B1): "AULA（0C45:80B1）",
    (0x0C45, 0x80B2): "AULA（0C45:80B2）",
    (0x0C45, 0x800A): "AULA（0C45:800A）",
    (0x0C45, 0x8803): "AULA（0C45:8803）",
    (0x38A6, 0x2710): "AULA（38A6:2710）",
    (0x38A6, 0x2711): "AULA（38A6:2711）",
    (0x38A6, 0x2712): "AULA（38A6:2712）",
    (0x38A6, 0x2722): "AULA（38A6:2722）",
    (0x28A6, 0x2722): "AULA（28A6:2722）",
    (0x17EF, 0x62F0): "AULA（17EF:62F0）",
}

# Collections the driver declares in its requestDevice filters. The remaining
# identities match any collection in WebHID (a human picks the interface); the
# engine has no human in the loop and can only use vendor-defined collections,
# so those PIDs require usage_page >= 0xFF00 instead of an exact pair.
DECLARED_COLLECTIONS = {
    (0x0C45, 0x8031): (0xFF08, 0x61),
    (0x0C45, 0x8032): (0xFF08, 0x61),
    (0x0C45, 0x80A2): (0xFF08, 0x61),
    (0x0C45, 0x80B2): (0xFF08, 0x61),
    (0x0C45, 0xFEFE): (0xFF60, 0x61),
    (0x0C45, 0xFEFC): (0xFF80, 0x01),
}

KB_INFO, MS_INFO = 0x10, 0xA0   # GET_DEVICE_INFO: keyboard vs mouse/receiver stack.
INFO_LEN_FIELD = 0x18           # One 24-byte chunk covers payload[0..18]: battery[17], charge[18].
REPORT_LENGTHS = (33, 65)       # Official packLen 32/64 plus the Windows report-ID slot.
ASLEEP = object()               # _exchange marker: the dongle's all-zero placeholder frame.


def make_request(command, output_length):
    """Unnumbered output report: hidapi's leading 0x00 slot, then the AA frame."""
    request = bytearray(output_length)
    request[1], request[2], request[3] = 0xAA, command, INFO_LEN_FIELD
    return bytes(request)


def parse_reply(raw, command):
    """One 55 echo frame. Battery is payload[17]; payload[18] is only a proven
    charging field on the mouse/receiver stack. Returns (level, charging, approx)."""
    if (len(raw) not in (32, 64) or raw[0] != 0x55 or raw[1] != command
            or raw[2] != INFO_LEN_FIELD or raw[3] or raw[4]):
        return None
    level = raw[8 + 17]
    # 0 is only observed as the asleep receiver's all-zero placeholder frame
    # (live MINI 60 HE Pro dongle), so it never becomes a reading - the same
    # trade-off as the VGN/MCHOSE keyboard dongles.
    if not 1 <= level <= 100:
        return None
    charging = bool(raw[8 + 18]) if command == MS_INFO else False
    return level, charging, ""


def no_reading_reply(raw, command):
    """The asleep dongle echoes the query with an all-zero payload."""
    return (len(raw) in (32, 64) and raw[0] == 0x55 and raw[1] == command
            and raw[2] == INFO_LEN_FIELD and not raw[3] and not raw[4]
            and not any(raw[8:32]))


def eligible_collection(info):
    """The driver's declared collection, else any vendor-defined usage page."""
    declared = DECLARED_COLLECTIONS.get((info.get("vendor_id"), info.get("product_id")))
    if declared is not None:
        return (info.get("usage_page"), info.get("usage")) == declared
    page = info.get("usage_page")
    return type(page) is int and 0xFF00 <= page <= 0xFFFF


def matches_collection(info):
    """Topology gate: an enrolled identity on its eligible collection."""
    return ((info.get("vendor_id"), info.get("product_id")) in PIDS
            and eligible_collection(info))


class AulaProvider(H.BatteryHidProvider):
    name = "aula"
    vid = VID
    pids = {pid: name for (vendor, pid), name in PIDS.items() if vendor == VID}
    input_id = output_id = 0        # The driver always sends report ID 0.
    kind = "keyboard"               # Replaced per reading by the stack that answers.
    charging_known = False          # Only the mouse/receiver stack carries charge.
    verification_level = "C"
    read_timeout_ms = 500           # Official per-packet budget (timeout: 500, 3 retries).

    def poll(self):
        result, diagnostics = [], []
        original = self.vid, self.pids
        try:
            # One official filter table spans several vendor IDs; reuse the same
            # descriptor gate per exact vendor's PID table (MCHOSE BY pattern).
            for vid in sorted({vid for vid, _ in PIDS}):
                self.vid = vid
                self.pids = {pid: name for (vendor, pid), name in PIDS.items() if vendor == vid}
                result.extend(super().poll())
                diagnostics.extend(self._diag)
        finally:
            self.vid, self.pids = original
            self._diag = diagnostics
        return result

    def device_name(self, info):
        return info.get("product_string") or super().device_name(info)

    def _pick(self, infos):
        candidates = [info for info in infos
                      if info.get("vendor_id") == self.vid
                      and info.get("product_id") in self.pids
                      and eligible_collection(info)]
        if len(candidates) != 1 or not candidates[0].get("path"):
            self._diag.append("missing or ambiguous eligible collection: nothing sent")
            return None
        info = candidates[0]
        for direction in ("input", "output"):
            length = info.get(direction + "_report_length")
            sizes = info.get(direction + "_report_sizes")
            if (type(length) is not int or length not in REPORT_LENGTHS
                    or not isinstance(sizes, dict) or sizes != {"0": length}):
                self._diag.append("report IDs/lengths unknown or incompatible: nothing sent"
                                  + H.report_proof_summary(info, direction, include_declared=True))
                return None
        return info

    def _exchange(self, dev, info, command):
        request = make_request(command, info["output_report_length"])
        if dev.write(request) != len(request):
            self._diag.append("short/failed battery query write; no retry")
            return None
        read_length = info["input_report_length"] - 1   # hidapi omits the unnumbered slot.
        deadline = H.time.monotonic() + self.read_timeout_ms / 1000
        for _ in range(H.MAX_READS):
            remaining = deadline - H.time.monotonic()
            if remaining <= 0:
                break
            raw = dev.read(read_length, max(1, H.math.ceil(remaining * 1000)))
            if not raw:
                break
            if no_reading_reply(raw, command):
                self._diag.append("device asleep/offline; no new reading; connection unknown")
                return ASLEEP
            parsed = parse_reply(raw, command)
            if parsed is not None:
                return parsed
            self._diag.append("rejected battery reply")
        return None

    def _query(self, dev, info):
        # Both commands are audited read-only device-info queries; the stack
        # that answers decides kind and charging evidence. A mouse behind this
        # identity costs one silent keyboard timeout before 0xA0 answers. The
        # all-zero placeholder frame means the paired unit is asleep: stop
        # instead of asking the other stack too.
        for command in (KB_INFO, MS_INFO):
            result = self._exchange(dev, info, command)
            if result is None:
                continue
            if result is ASLEEP:
                return None
            level, charging, approx = result
            self.kind = "mouse" if command == MS_INFO else "keyboard"
            self.charging_known = command == MS_INFO
            return level, charging, approx
        self._diag.append("no valid reply within deadline/read limit; connection unknown")
        return None
