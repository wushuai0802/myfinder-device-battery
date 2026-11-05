"""M HUB BY keyboard receiver: model-gated, read-only 3554:FA09 support.

Protocol facts independently implemented from main-DCRwBIvm.js, not vendor
code. See upstream-protocols.md for framing, identification and untested limits.
Only generic receiver names take this wireless path; no substring brand guesses.
"""
import battery_hid as H

VID = 0x3554
PIDS = {0xFA09: "MCHOSE BY keyboard (model identification required)"}
RECEIVER_NAMES = frozenset(("2.4G Wireless Receiver", "MCHOSE 2.4G Wireless"))

# Six-byte model identifiers from M HUB's keyboardPwds table, not credentials.
# The official generic-receiver path matches this entire table, not just the
# PID-dependent aliases used for already-named wired keyboards by getDevName.
MODEL_CODES = {
    "03000000021c": "G87", "030000000077": "K99",
    "0300000001df": "K99_V2", "030000000132": "G98",
    "0300000000d6": "X75", "0a0000000003": "G98_Pro",
    "0300000001c6": "X75_V2", "030000000115": "G75_Pro",
    "0a0000000018": "G98_V2", "0a000000002b": "UT98",
    "0a0000000001": "Z75", "0a0000000009": "Z75S",
    "0300000001c7": "KX75", "030000000174": "GX87",
    "0300000001ed": "GX87_V2", "030000000135": "K87",
    "030000000335": "G75_V2", "0a0000000034": "G87_V2",
}


def identity_request():
    return [0x13, 0x05, 0x01] + [0] * 16 + [0x19]


def make_request():
    return [0x13, 0x4A, 0x01] + [0] * 16 + [0x5E]


def _payload(reply, command):
    # M HUB strips the four-byte packet header and last byte, and accepts
    # command + total + sequence. Restrict these tiny reads to one packet.
    # Its input handler does NOT verify the last-byte checksum; do not invent
    # a response algorithm from the request's additive checksum.
    if (len(reply) != 20 or any(type(b) is not int or not 0 <= b <= 255 for b in reply)
            or list(reply[:4]) != [0x13, command, 1, 0]
            or list(reply) in (identity_request(), make_request())):
        return None
    return reply[5:19]


def parse_model(reply):
    payload = _payload(reply, 0x05)
    return MODEL_CODES.get(bytes(payload[:6]).hex()) if payload is not None else None


def parse_reply(reply):
    payload = _payload(reply, 0x4A)
    if payload is None:
        return None
    level, flags = payload[:2]
    full, charge = flags & 15, flags >> 4
    # The bundled little-endian bit parser places fullStatus in the LOW nibble.
    # M HUB treats chargeStatus == 1 as charging, only below 100 percent.
    # Other nibble values have no established meaning: reject, never coerce.
    if level > 100 or full not in (0, 1) or charge not in (0, 1):
        return None
    return level, charge == 1 and level < 100, ""


class MchoseByKeyboardProvider(H.BatteryHidProvider):
    name = "mchose_by_keyboard"
    vid, pids = VID, PIDS
    collection = (0xFF04, 0x0002)
    input_id = output_id = 0x13
    input_bounds = (20, 20)
    output_bytes = 20
    kind = "keyboard"
    charging_known = True
    read_timeout_ms = 2200  # M HUB's retryTime:2 response budget, per query.

    def _pick(self, infos):
        info = super()._pick(infos)
        if info is None:
            return None
        if info.get("product_string") not in RECEIVER_NAMES:
            self._diag.append("unrecognized wireless receiver name: nothing sent")
            return None
        if any(info.get(direction + "_report_sizes") != {"19": 20}
               for direction in ("input", "output")):
            self._diag.append("single byte-array report not proven: nothing sent")
            return None
        return info

    def device_name(self, info):
        return "MCHOSE " + self._model.replace("_", " ")

    def _exchange(self, dev, request, parse):
        if dev.write(request) != len(request):
            self._diag.append("short/failed read query write; no retry")
            return None
        deadline = H.time.monotonic() + self.read_timeout_ms / 1000
        for _ in range(H.MAX_READS):
            remaining = deadline - H.time.monotonic()
            if remaining <= 0:
                break
            raw = dev.read(20, max(1, H.math.ceil(remaining * 1000)))
            if not raw:
                break
            parsed = parse(raw)
            if parsed is not None:
                return parsed
            # A complete unknown identity is a terminal refusal, even if a
            # previously queued recognized identity follows it.
            if request == identity_request() and _payload(raw, 0x05) is not None:
                self._diag.append("model not in M HUB table: no battery query")
                return None
        self._diag.append("no valid reply; no new reading; connection unknown")
        return None

    def _query(self, dev, info):
        # Re-identify on every poll: neither a cached model nor a browser's
        # synthetic realName is trusted across receiver re-pairing/sleep.
        self._model = self._exchange(dev, identity_request(), parse_model)
        if self._model is None:
            return None
        return self._exchange(dev, make_request(), parse_reply)
