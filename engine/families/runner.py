"""Interpret one declarative read on an already opened hidapi device.

Only the four read transports of ``spec.TRANSPORTS`` exist. Every reply is
checked against the family's rules before a percentage is accepted; anything
else is "no reading". I/O errors are reported as an anomaly so the caller's
memory can retire the read (``memory.py``).
"""
from __future__ import annotations

import math
import time
from dataclasses import dataclass
from typing import Optional

from .spec import Family, Read, Reply


@dataclass(frozen=True)
class Outcome:
    kind: str                 # "reading" | "asleep" | "silent" | "rejected" | "anomaly"
    level: Optional[int] = None
    charging: bool = False
    charging_known: bool = False
    note: str = ""


def parse(reply: Reply, frame) -> Outcome:
    data = bytes(frame)
    if reply.length is not None and len(data) != reply.length:
        return Outcome("rejected", note="reply length")
    needed = max([r.at for r in reply.match] + [reply.percent_at]
                 + [r.at for r in (reply.charging, reply.asleep) if r is not None])
    if len(data) <= needed:
        return Outcome("rejected", note="reply too short")
    if any(data[r.at] & r.mask != r.equals for r in reply.match):
        return Outcome("rejected", note="reply header")
    if reply.asleep is not None and data[reply.asleep.at] & reply.asleep.mask == reply.asleep.equals:
        return Outcome("asleep", note="device reports asleep/offline")
    level = data[reply.percent_at] & reply.percent_mask
    if not reply.percent_min <= level <= reply.percent_max:
        return Outcome("rejected", note="percent out of range")
    if reply.charging is None:
        return Outcome("reading", level=level)
    charging = data[reply.charging.at] & reply.charging.mask == reply.charging.equals
    return Outcome("reading", level=level, charging=charging, charging_known=True)


def _padded(request: bytes, length: int) -> bytes:
    return request + bytes(max(0, length - len(request)))


def _read_inputs(dev, family: Family, read: Read, clock, request: bytes) -> Outcome:
    length = family.input_report.length - (family.input_report.report_id == 0)
    deadline = clock() + read.timeout_ms / 1000
    last = Outcome("silent", note="no reply within deadline")
    for _ in range(read.max_reads):
        remaining = deadline - clock()
        if remaining <= 0:
            break
        raw = dev.read(length, max(1, math.ceil(remaining * 1000)))
        if not raw:
            break
        frame = bytes(raw)
        if request and frame == request[:len(frame)]:
            continue  # the request echoed back is never a reading
        outcome = parse(read.reply, frame)
        if outcome.kind in ("reading", "asleep"):
            return outcome
        last = outcome
    return last


def run(dev, family: Family, read: Read, clock=time.monotonic) -> Outcome:
    try:
        if read.transport == "listen":
            return _read_inputs(dev, family, read, clock, b"")
        if read.transport == "output":
            request = _padded(read.request, family.output_report.length)
            if dev.write(list(request)) != len(request):
                return Outcome("anomaly", note="short write")
            return _read_inputs(dev, family, read, clock, request)
        length = family.feature_report.length
        if read.transport == "feature":
            request = _padded(read.request, length)
            if dev.send_feature_report(list(request)) != len(request):
                return Outcome("anomaly", note="short feature set")
        raw = dev.get_feature_report(read.request[0], length)
        if not raw:
            return Outcome("silent", note="empty feature report")
        return parse(read.reply, raw)
    except (OSError, ValueError):
        # Backend text can carry device paths; keep only the category.
        return Outcome("anomaly", note="I/O error")
