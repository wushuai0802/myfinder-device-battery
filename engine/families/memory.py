"""Per-device read memory: the trial policy for families with several reads.

Adopted from BrimDeck's device-read policy (Apache-2.0, MoFeng2223/BrimDeck,
docs/zh-CN/devices.md "读取方式与安全"), reimplemented here:

* a device seen for the first time tries its family's reads in order, for at
  most ``MAX_TRIAL_ROUNDS`` polls; the first read that yields a reading is
  remembered and from then on is the only one sent;
* a read that ends in an I/O anomaly is retired for that device; once
  ``MAX_ANOMALIES`` reads are retired, the device is not read again;
* a remembered read that stays silent for ``STALE_POLLS`` polls is forgotten
  once, so the trial can run again (firmware updates change answers);
* a device that never answered its trial is not abandoned: every
  ``RETRY_POLLS`` polls it gets one more trial round;
* the user can switch reading off per device (``disable``).

``snapshot``/``restore`` carry the whole state: the engine manager keeps the
authoritative copy, hands it to the reader child with every families query and
merges the child's result back; the host persists it as family-memory.json.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Dict, List, Optional, Set, Tuple

MAX_TRIAL_ROUNDS = 3
MAX_ANOMALIES = 2
STALE_POLLS = 40
RETRY_POLLS = 3   # after an unanswered trial, wait this many polls and try one more round


@dataclass
class DeviceMemory:
    working: Optional[str] = None
    rounds: int = 0
    retired: Set[str] = field(default_factory=set)
    silent_polls: int = 0
    relearned: bool = False
    disabled: bool = False


def _bounded(value, limit: int) -> int:
    # Persisted by the host: anything that is not a small non-negative int is 0.
    return min(limit, value) if isinstance(value, int) and not isinstance(value, bool) and value > 0 else 0


class ReadMemory:
    def __init__(self):
        self._devices: Dict[Tuple[str, str], DeviceMemory] = {}

    def entry(self, family_id: str, device_key: str) -> DeviceMemory:
        return self._devices.setdefault((family_id, device_key), DeviceMemory())

    def plan(self, family_id: str, device_key: str, read_names: List[str], force: bool = False) -> List[str]:
        """Reads to try this poll, in order. Empty: send nothing."""
        memory = self.entry(family_id, device_key)
        # Persisted state can name reads a newer family file no longer has.
        memory.retired &= set(read_names)
        if memory.working not in read_names:
            memory.working = None
        if memory.disabled or len(memory.retired) >= MAX_ANOMALIES:
            return []
        if memory.working is not None and memory.working not in memory.retired:
            return [memory.working]
        if memory.rounds >= MAX_TRIAL_ROUNDS:
            # Never give up for good: a receiver whose mouse was off, or a passive
            # read of a device that only reports while in use, gets another trial.
            if force:
                memory.rounds, memory.silent_polls = MAX_TRIAL_ROUNDS - 1, 0
            else:
                memory.silent_polls += 1
                if memory.silent_polls < RETRY_POLLS:
                    return []
                memory.rounds, memory.silent_polls = MAX_TRIAL_ROUNDS - 1, 0
        memory.rounds += 1
        return [name for name in read_names if name not in memory.retired]

    def record(self, family_id: str, device_key: str, read_name: str, outcome_kind: str):
        memory = self.entry(family_id, device_key)
        if outcome_kind == "anomaly":
            memory.retired.add(read_name)
            if memory.working == read_name:
                memory.working = None
            return
        if outcome_kind in ("reading", "asleep"):
            memory.working = read_name
            memory.silent_polls = 0
            return
        if memory.working == read_name:
            memory.silent_polls += 1
            if memory.silent_polls >= STALE_POLLS and not memory.relearned:
                memory.working, memory.rounds, memory.silent_polls, memory.relearned = None, 0, 0, True

    def disable(self, family_id: str, device_key: str, disabled: bool = True):
        self.entry(family_id, device_key).disabled = disabled

    def snapshot(self) -> dict:
        return {f"{f}|{d}": {"working": m.working, "rounds": m.rounds, "retired": sorted(m.retired),
                             "silentPolls": m.silent_polls, "relearned": m.relearned, "disabled": m.disabled}
                for (f, d), m in self._devices.items()}

    def restore(self, data: dict):
        if not isinstance(data, dict):
            return
        for key, value in data.items():
            family_id, _, device_key = str(key).partition("|")
            if not family_id or not device_key or not isinstance(value, dict):
                continue
            retired = value.get("retired")
            self._devices[(family_id, device_key)] = DeviceMemory(
                working=value.get("working") if isinstance(value.get("working"), str) else None,
                rounds=_bounded(value.get("rounds"), MAX_TRIAL_ROUNDS),
                silent_polls=_bounded(value.get("silentPolls"), STALE_POLLS),
                retired={r for r in retired if isinstance(r, str)} if isinstance(retired, list) else set(),
                relearned=value.get("relearned") is True, disabled=value.get("disabled") is True)
