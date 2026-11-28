"""Single-source-of-truth consistency checks for server provider tables and wakeup."""
from __future__ import annotations

import json
import sys
import threading
import time
import unittest
from pathlib import Path

ENGINE_DIR = Path(__file__).resolve().parents[1]
if str(ENGINE_DIR) not in sys.path:
    sys.path.insert(0, str(ENGINE_DIR))

import diagnostics_projection  # noqa: E402
import isolation  # noqa: E402
import server  # noqa: E402


class ProviderConsistencyTests(unittest.TestCase):
    def test_provider_registration_tables_stay_aligned(self):
        provider_names = [name for name, _ in server.PROVIDERS]
        self.assertEqual(len(provider_names), len(set(provider_names)), "Duplicate entries in server.PROVIDERS")
        all_names = set(provider_names) | {"standard_hid"}

        vid_keys = set(server.PROVIDER_VIDS)
        expected_vid_keys = set(provider_names) - set(server.SYSTEM_PROVIDERS)
        self.assertEqual(vid_keys, expected_vid_keys, "PROVIDER_VIDS drifted from PROVIDERS")

        self.assertTrue(
            server.READER_DIAGNOSTICS.issubset(all_names),
            f"Unknown providers in READER_DIAGNOSTICS: {server.READER_DIAGNOSTICS - all_names}",
        )
        self.assertTrue(
            diagnostics_projection.PROJECTED_DIAGNOSTIC_PROVIDERS.issubset(server.READER_DIAGNOSTICS),
            "Every projected diagnostic provider must also be registered in READER_DIAGNOSTICS",
        )
        self.assertTrue(
            diagnostics_projection.FIXED_FIRST_DIAGNOSTIC_PROVIDERS.issubset(server.READER_DIAGNOSTICS),
            "Every fixed-first diagnostic provider must be registered in READER_DIAGNOSTICS",
        )
        self.assertTrue(
            server.IDLE_BACKOFF_EXEMPT.issubset(all_names),
            f"Unknown providers in IDLE_BACKOFF_EXEMPT: {server.IDLE_BACKOFF_EXEMPT - all_names}",
        )
        self.assertTrue(
            set(server.DPI_PROVIDERS).issubset(all_names),
            f"Unknown providers in DPI_PROVIDERS: {set(server.DPI_PROVIDERS) - all_names}",
        )

    def test_builtin_family_ids_are_unique(self):
        builtin_dir = ENGINE_DIR / "families" / "builtin"
        seen = set()
        for path in sorted(builtin_dir.glob("*.json")):
            doc = json.loads(path.read_text(encoding="utf-8"))
            family_id = doc.get("id")
            self.assertTrue(family_id, f"Missing family id in {path.name}")
            self.assertNotIn(family_id, seen, f"Duplicate family id {family_id}")
            seen.add(family_id)

    def test_reader_process_event_wakeup_fires_immediately(self):
        wakeup = threading.Event()
        proc = isolation.ReaderProcess(
            command=[sys.executable, "-I", "-B", "-c",
                     "import sys,json; line=sys.stdin.readline(); req=json.loads(line); "
                     "print(json.dumps({'sequence': req['sequence'], 'ok': True}), flush=True)"],
            wakeup=wakeup,
        )
        try:
            wakeup.clear()
            proc.start({"action": "ping"}, 5.0)
            signaled = wakeup.wait(timeout=3.0)
            self.assertTrue(signaled, "ReaderProcess._pump must signal wakeup event when a line arrives")
            deadline = time.monotonic() + 2.0
            result = None
            while result is None and time.monotonic() < deadline:
                result = proc.result()
                if result is None:
                    wakeup.wait(0.05)
            self.assertEqual(result, {"sequence": 1, "ok": True})
        finally:
            proc.close()


if __name__ == "__main__":
    unittest.main()
