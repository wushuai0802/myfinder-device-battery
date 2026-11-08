"""DELUX GH8K / M900 Ultra receiver battery reader.

Only the proven 3554:FB26 receiver collection and report-08 battery query are
used. The reply's charging flag is deliberately left unknown pending validation.
"""
from __future__ import annotations

from typing import List, Optional, Tuple

try:
    import hid
except ImportError:  # pragma: no cover
    hid = None

from battery_hid import BatteryHidProvider
from providers.base import DeviceStatus
from providers import hidlist

VID = 0x3554
PID = 0xFB26
COLLECTION = (0xFF02, 0x0002)
REPORT_ID = 0x08
REPORT_LENGTH = 17
HEADER = 0x08
CMD_POWER = 0x04
CHECKSUM_BASE = 0x55

PIDS = {PID: "DELUX GH8K/M900 Ultra"}


def make_request() -> List[int]:
    """Build the ordinary, zero-argument battery query only."""
    frame = [HEADER, CMD_POWER] + [0] * (REPORT_LENGTH - 3)
    frame.append((CHECKSUM_BASE - sum(frame)) % 256)
    return frame


def parse_reply(reply) -> Optional[Tuple[int, bool, str]]:
    """Return (percent, charging, detail), rejecting unproven/invalid frames."""
    if reply is None:
        return None
    frame = list(reply)
    if len(frame) != REPORT_LENGTH:
        return None
    if frame[0] != HEADER or frame[1] != CMD_POWER or frame[2] != 0:
        return None
    if any(type(value) is not int or not 0 <= value <= 255 for value in frame):
        return None
    if sum(frame) % 256 != CHECKSUM_BASE:
        return None
    # The query echo (08 04, zero-filled, request checksum 49) is not telemetry.
    if frame == make_request():
        return None
    level = frame[6]
    millivolts = int.from_bytes(bytes(frame[8:10]), "big")
    # Conservative single-cell plausibility gate, not a calibrated voltage curve.
    if not 0 <= level <= 100 or not 2500 <= millivolts <= 5000:
        return None
    # Byte 7's meaning has not been established on this receiver.
    return level, False, ""


class DeluxProvider(BatteryHidProvider):
    name = "delux"
    vid = VID
    pids = PIDS
    collection = COLLECTION
    output_id = REPORT_ID
    output_bytes = REPORT_LENGTH
    input_id = REPORT_ID
    input_bounds = (REPORT_LENGTH, REPORT_LENGTH)
    charging_known = False
    unconfirmed = False
    verification_level = "A"
    kind = "mouse"
    make_request = staticmethod(make_request)

    def _pick(self, infos):
        # Restrict inherited physical grouping to the one exact receiver identity.
        infos = [info for info in infos
                 if (info.get("vendor_id"), info.get("product_id")) == (VID, PID)]
        return super()._pick(infos)

    def device_name(self, info):
        return PIDS[info["product_id"]]

    def parse_reply(self, reply):
        return parse_reply(reply)

    def poll(self):
        self._diag = []
        if hid is None:
            return []
        try:
            inventory = hidlist.enumerate(VID)
        except OSError:
            self._diag.append("enumeration failed: nothing sent")
            return []

        # The common provider groups receiver collections by physical_id. Validate
        # every identity/usage/length before that base class can open a handle.
        exact = [info for info in inventory
                 if (info.get("vendor_id"), info.get("product_id")) == (VID, PID)]
        eligible = [info for info in exact
                    if (info.get("usage_page"), info.get("usage")) == COLLECTION]
        if len(eligible) != 1 or not eligible[0].get("path"):
            self._diag.append("missing or ambiguous eligible collection: nothing sent")
            return []
        info = self._pick(exact)
        if info is None:
            return []
        physical = info.get("physical_id", "")
        if not isinstance(physical, str) or physical.startswith("collection:"):
            self._diag.append("physical parent unresolved; nothing sent")
            return []
        for direction in ("input", "output"):
            if (info.get(direction + "_report_length") != REPORT_LENGTH
                    or info.get(direction + "_report_sizes") != {str(REPORT_ID): REPORT_LENGTH}):
                self._diag.append("report-8 input/output length unproven or incompatible: nothing sent")
                return []
        # Avoid inherited fallback or secondary-collection selection: only this
        # descriptor-proven exact collection reaches open_path/write.
        return self._poll_one(info)

    def _poll_one(self, info):
        dev = hid.device()
        try:
            dev.open_path(info["path"])
            # Start the Windows input read before this fast receiver replies.
            # Discard pre-query input so an old report never becomes a new reading.
            dev.read(REPORT_LENGTH, 1)
            reading = self._query(dev, info)
            if reading is None:
                self._diag = ["no valid battery reply; connection unknown"]
                return []
            level, _charging, approx = reading
            physical = info.get("physical_id", "")
            return [DeviceStatus(
                f"{self.name}:{physical or repr(info['path'])}", self.device_name(info),
                level, False, True, self.name, approx=approx, kind=self.kind,
                physical_id=physical, connection_known=True, charging_known=False,
                verification_level=self.verification_level, unconfirmed=False)]
        except (OSError, ValueError):
            self._diag.append("battery I/O failed; connection unknown")
            return []
        finally:
            try:
                dev.close()
            except OSError:
                self._diag.append("HID handle close failed")

    def diagnostics(self):
        return list(self._diag)
