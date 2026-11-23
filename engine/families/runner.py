"""Interpret one declarative read on an already opened hidapi device.

Only the four read transports of ``spec.TRANSPORTS`` exist. Every reply is
checked against the family's rules before a percentage is accepted; anything
else is "no reading". I/O errors are reported as an anomaly so the caller's
memory can retire the read (``memory.py``).

Frames are indexed as hidapi returns them: input reports with report ID 0 come
without the ID byte; feature reports always start with their report ID.
"""
from __future__ import annotations

import math
import time
from dataclasses import dataclass
from typing import Optional

from .spec import DpiSpec, DpiValue, Family, Gate, Read, Reply


@dataclass(frozen=True)
class Outcome:
    kind: str                 # "reading" | "asleep" | "silent" | "rejected" | "anomaly"
    level: Optional[int] = None
    charging: bool = False
    charging_known: bool = False
    note: str = ""


def _holds(data, rule):
    return data[rule.at] & rule.mask == rule.equals


def _checksum_ok(data, checksum):
    return checksum is None or sum(data[checksum.start:checksum.end]) & 0xFF == checksum.equals


def _envelope(data, match, spans=(), checksum=None, length=None):
    """None when the frame is this reply's shape, else a rejection note."""
    if length is not None and len(data) != length:
        return "reply length"
    needed = [r.at for r in match] + [s.at + s.count - 1 for s in spans] + ([checksum.end - 1] if checksum else [])
    if needed and len(data) <= max(needed):
        return "reply too short"
    if not all(_holds(data, r) for r in match):
        return "reply header"
    if not all(s.lo <= b <= s.hi for s in spans for b in data[s.at:s.at + s.count]):
        return "reply field range"
    if not _checksum_ok(data, checksum):
        return "reply checksum"
    return None


def parse(reply: Reply, frame) -> Outcome:
    data = bytes(frame)
    note = _envelope(data, reply.match, reply.spans, reply.checksum, reply.length)
    if note:
        return Outcome("rejected", note=note)
    if any(len(data) > r.at and _holds(data, r) for r in reply.asleep):
        return Outcome("asleep", note="device reports asleep/offline")
    for variant in reply.variants:
        positions = [r.at for r in variant.when] + [variant.percent_at] + [r.at for r in variant.asleep]
        if variant.charging is not None:
            positions.append(variant.charging.at)
        if len(data) <= max(positions):
            continue
        if not all(_holds(data, r) for r in variant.when):
            continue
        if any(_holds(data, r) for r in variant.asleep):
            return Outcome("asleep", note="device reports asleep/offline")
        if not all(len(data) > r.at and _holds(data, r) for r in variant.require):
            return Outcome("rejected", note="reply state")
        level = data[variant.percent_at] & variant.percent_mask
        if not variant.percent_min <= level <= variant.percent_max:
            return Outcome("rejected", note="percent out of range")
        if variant.charging_always:
            return Outcome("reading", level=level, charging=True, charging_known=True)
        if variant.charging is None:
            return Outcome("reading", level=level)
        return Outcome("reading", level=level, charging=_holds(data, variant.charging), charging_known=True)
    return Outcome("rejected", note="no reply layout applies")


def parse_gate(gate: Gate, frame) -> Optional[bool]:
    """True online, False offline (asleep), None not the gate's answer."""
    data = bytes(frame)
    if _envelope(data, gate.match, (), gate.checksum, gate.length) is not None:
        return None
    if len(data) > gate.online.at and _holds(data, gate.online):
        return True
    if gate.offline is not None and len(data) > gate.offline.at and _holds(data, gate.offline):
        return False
    return None


def _padded(request: bytes, length: int) -> bytes:
    return request + bytes(max(0, length - len(request)))


def _echo(request: bytes, report_id: int) -> bytes:
    """The request as it would come back on the input side (no ID byte for report 0)."""
    return request[1:] if report_id == 0 else request


def _read_inputs(dev, family: Family, read: Read, clock, request: bytes, accept):
    length = family.input_report.length - (family.input_report.report_id == 0)
    echo = _echo(request, family.input_report.report_id) if request else b""
    deadline = clock() + read.timeout_ms / 1000
    last = None
    for _ in range(read.max_reads):
        remaining = deadline - clock()
        if remaining <= 0:
            break
        raw = dev.read(length, max(1, math.ceil(remaining * 1000)))
        if not raw:
            break
        frame = bytes(raw)
        if echo and frame[:len(echo)] == echo[:len(frame)]:
            continue  # the request read back is never an answer
        result, final = accept(frame)
        if final:
            return result
        last = result
    return last


def _exchange(dev, family: Family, read: Read, request: bytes, accept, clock):
    """Send one request on the read's transport; returns (result, sent_ok)."""
    if read.transport == "output":
        padded = _padded(request, family.output_report.length)
        if dev.write(list(padded)) != len(padded):
            return None, False
        return _read_inputs(dev, family, read, clock, padded, accept), True
    length = family.feature_report.length
    padded = _padded(request, length)
    if read.transport == "feature" and dev.send_feature_report(list(padded)) != len(padded):
        return None, False
    raw = dev.get_feature_report(request[0], length)
    if not raw:
        return None, True
    result, _ = accept(bytes(raw))
    return result, True


def run(dev, family: Family, read: Read, clock=time.monotonic) -> Outcome:
    try:
        if read.transport == "listen":
            def take(frame):
                outcome = parse(read.reply, frame)
                return outcome, outcome.kind in ("reading", "asleep")
            return _read_inputs(dev, family, read, clock, b"", take) or Outcome("silent", note="no report within deadline")
        if read.gate is not None:
            def gate_answer(frame):
                answer = parse_gate(read.gate, frame)
                return answer, answer is not None
            answer, sent = _exchange(dev, family, read, read.gate.request, gate_answer, clock)
            if not sent:
                return Outcome("anomaly", note="short gate write")
            if answer is False:
                return Outcome("asleep", note="gate: device offline")
            if answer is not True:
                return Outcome("silent", note="gate unanswered; read not sent")
        def take(frame):
            outcome = parse(read.reply, frame)
            return outcome, outcome.kind in ("reading", "asleep")
        outcome, sent = _exchange(dev, family, read, read.request, take, clock)
        if not sent:
            return Outcome("anomaly", note="short write")
        return outcome or Outcome("silent", note="no reply within deadline")
    except (OSError, ValueError):
        # Backend text can carry device paths; keep only the category.
        return Outcome("anomaly", note="I/O error")


def _value(frames, field: DpiValue, offset=0):
    data = frames[field.exchange]
    at = field.at + offset
    if len(data) < at + field.width:
        raise ValueError("dpi field outside reply")
    raw = int.from_bytes(data[at:at + field.width], "big" if field.big_endian else "little")
    return (raw & field.mask) * field.scale


def run_dpi(dev, family: Family, clock=time.monotonic):
    """Read-only DPI evidence: (dict(current=, stages=, stageSource=), note).

    Each exchange is sent once in order on the open handle; any rejected,
    silent or short exchange stops the sequence and yields no DPI at all.
    """
    spec: DpiSpec = family.dpi
    frames = {}
    try:
        for exchange in spec.exchanges:
            def take(frame, exchange=exchange):
                ok = _envelope(frame, exchange.match, exchange.spans, exchange.checksum, exchange.length) is None
                return (frame if ok else None), ok
            frame, sent = _exchange(dev, family, exchange, exchange.request, take, clock)
            if not sent:
                return None, f"dpi {exchange.name}: short write"
            if frame is None:
                return None, f"dpi {exchange.name}: no valid reply"
            frames[exchange.name] = bytes(frame)
        count = _value(frames, spec.stage_count) if spec.stage_count else spec.fixed_count
        if not 1 <= count <= 8:
            return None, "dpi stage count out of range"
        stages = [_value(frames, spec.stage_first, i * spec.stride) for i in range(count)]
        if not all(50 <= v <= 65535 for v in stages):
            return None, "dpi stage out of range"
        if spec.current_stage is not None:
            index = _value(frames, spec.current_stage) - spec.stage_base
            if not 0 <= index < count:
                return None, "dpi current stage out of range"
            current = stages[index]
        else:
            current = _value(frames, spec.current)
            if not 50 <= current <= 65535:
                return None, "dpi current out of range"
        return {"current": current, "stages": stages, "stageSource": spec.stage_source}, ""
    except (OSError, ValueError):
        return None, "dpi I/O error"
