"""Idle back-off for device readers that find their device but no reading (#588)."""
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import _hidstub  # noqa: F401
import server
from providers.base import DeviceStatus


def status(level=None, online=False, cached=True):
    return DeviceStatus("razer:x", "Razer Viper Ultimate", level, False, online, "razer", cached=cached)


class IdleBackoffTests(unittest.TestCase):
    def setUp(self):
        self.engine = server.BatteryEngine(providers=[])
        self.clock = 1000.0
        patcher = patch.object(server.time, "monotonic", side_effect=lambda: self.clock)
        patcher.start()
        self.addCleanup(patcher.stop)

    def idle(self, key="razer|pnp:usb\\receiver", name="razer", physical="pnp:usb\\receiver", candidates=1, statuses=None):
        self.engine._idle_backoff(key, name, physical, candidates, statuses if statuses is not None else [status()])
        return self.engine.idle_until.get(key, 0) - self.clock

    def test_progressive_backoff_and_reset_on_reading(self):
        self.assertLessEqual(self.idle(), 0)          # first silent round: no back-off yet
        self.assertEqual(self.idle(), 60)
        self.assertEqual(self.idle(), 120)
        self.assertEqual(self.idle(), 300)
        self.assertEqual(self.idle(), 300)            # capped
        self.assertLessEqual(self.idle(statuses=[status(57, True, False)]), 0)
        self.assertNotIn("razer|pnp:usb\\receiver", self.engine.idle_misses)

    def test_exempt_scopes_never_back_off(self):
        for kwargs in (dict(name="rapoo"), dict(name="fa6x_passive"), dict(name="bluetooth"),
                       dict(physical=""), dict(candidates=0)):
            with self.subTest(kwargs=kwargs):
                key = "k-" + str(sorted(kwargs.items()))
                for _ in range(4):
                    self.assertLessEqual(self.idle(key=key, **kwargs), 0)

    def test_cached_history_is_not_a_reading(self):
        self.idle()
        self.assertEqual(self.idle(statuses=[status(57, False, True)]), 60)


if __name__ == "__main__":
    unittest.main()
