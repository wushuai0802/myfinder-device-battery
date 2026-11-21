"""Read memory never abandons a device that was silent during its first trial."""
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from families import memory as mem


class RetryTests(unittest.TestCase):
    def test_unanswered_trial_retries_one_round_every_retry_polls(self):
        memory = mem.ReadMemory()
        for _ in range(mem.MAX_TRIAL_ROUNDS):
            self.assertEqual(memory.plan("f", "unit-a", ["battery"]), ["battery"])
            memory.record("f", "unit-a", "battery", "silent")
        sent = [bool(memory.plan("f", "unit-a", ["battery"])) for _ in range(mem.RETRY_POLLS * 2)]
        self.assertEqual(sent.count(True), 2)                 # one retry per window, not every poll
        self.assertEqual(sent.index(True), mem.RETRY_POLLS - 1)
        memory.record("f", "unit-a", "battery", "reading")
        self.assertEqual(memory.plan("f", "unit-a", ["battery"]), ["battery"])

    def test_retry_survives_persistence_and_never_beats_retirement_or_disable(self):
        memory = mem.ReadMemory()
        memory.restore({"f|unit-a": {"rounds": mem.MAX_TRIAL_ROUNDS, "silentPolls": mem.RETRY_POLLS - 1},
                        "f|unit-b": {"rounds": mem.MAX_TRIAL_ROUNDS, "silentPolls": mem.RETRY_POLLS - 1,
                                     "disabled": True},
                        "f|unit-c": {"rounds": mem.MAX_TRIAL_ROUNDS, "silentPolls": mem.RETRY_POLLS - 1,
                                     "retired": ["a", "b"]}})
        self.assertEqual(memory.plan("f", "unit-a", ["battery"]), ["battery"])
        self.assertEqual(memory.plan("f", "unit-b", ["battery"]), [])
        self.assertEqual(memory.plan("f", "unit-c", ["a", "b"]), [])


if __name__ == "__main__":
    unittest.main()
