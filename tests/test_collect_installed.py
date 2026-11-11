"""Offline tests for static installed-vendor evidence collection."""

import contextlib
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


collector = load("collect_installed", ROOT / "tools/evidence/collect_installed.py")


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def run_main(*arguments):
    stdout = io.StringIO()
    stderr = io.StringIO()
    with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
        code = collector.main(list(arguments))
    return code, stdout.getvalue(), stderr.getvalue()


class CollectInstalledTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.source = self.root / "source"
        self.dest = self.root / "evidence"
        self.source.mkdir()

    def tearDown(self):
        self.temp.cleanup()

    def write(self, relative, data=b"value"):
        path = self.source / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        return path

    def test_whitelisted_files_are_copied_and_structure_is_preserved(self):
        self.write("config.JSON", b'{"enabled":true}')
        self.write("nested/settings.yaml", b"mode: safe\n")

        result = collector.collect(self.source, self.dest)

        self.assertEqual((self.dest / "config.JSON").read_bytes(), b'{"enabled":true}')
        self.assertEqual((self.dest / "nested/settings.yaml").read_bytes(), b"mode: safe\n")
        self.assertEqual(result["copied"], 2)
        self.assertEqual(result["copiedBytes"], 27)

    def test_blacklisted_extensions_are_skipped(self):
        self.write("setup.EXE", b"MZ safe fixture")
        self.write("helper.dll", b"DLL fixture")
        self.write("script.py", b"raise AssertionError('must not run')")
        self.write("allowed.txt", b"allowed")

        result = collector.collect(self.source, self.dest)

        skipped = {(item["path"], item["reason"]) for item in result["skipped"]}
        self.assertIn(("setup.EXE", "extension"), skipped)
        self.assertIn(("helper.dll", "extension"), skipped)
        self.assertIn(("script.py", "extension"), skipped)
        self.assertFalse((self.dest / "setup.EXE").exists())
        self.assertFalse((self.dest / "helper.dll").exists())
        self.assertFalse((self.dest / "script.py").exists())

    def test_mz_header_is_skipped_even_with_whitelisted_extension(self):
        self.write("disguised.json", b"MZnot-really-json")

        result = collector.collect(self.source, self.dest)

        self.assertEqual(result["copied"], 0)
        self.assertEqual(result["skipped"], [{
            "path": "disguised.json",
            "reason": "executable-header",
        }])
        self.assertFalse((self.dest / "disguised.json").exists())

    def test_file_over_size_limit_is_skipped(self):
        self.write("small.txt", b"1234")
        self.write("large.txt", b"12345")

        result = collector.collect(self.source, self.dest, max_bytes=4)

        self.assertEqual(result["copied"], 1)
        self.assertIn(
            {"path": "large.txt", "reason": "too-large"},
            result["skipped"],
        )
        self.assertFalse((self.dest / "large.txt").exists())

    def test_sha256sums_are_sorted_use_slashes_and_lf(self):
        self.write("z.txt", b"last")
        self.write("nested/a.JSON", b"first")

        collector.collect(self.source, self.dest)

        expected = (
            f"{sha256(b'first')}  nested/a.JSON\n"
            f"{sha256(b'last')}  z.txt\n"
        ).encode("utf-8")
        self.assertEqual((self.dest / "SHA256SUMS.txt").read_bytes(), expected)

    def test_manifest_counts_and_executable_hashes_are_correct(self):
        executable = b"MZ synthetic executable"
        self.write("bin/vendor.DLL", executable)
        self.write("data/config.json", b"{}")

        result = collector.collect(self.source, self.dest)
        manifest = json.loads((self.dest / "MANIFEST.json").read_text(encoding="utf-8"))

        self.assertEqual(manifest["source"], str(self.source))
        self.assertTrue(manifest["generatedAt"].endswith("Z"))
        self.assertEqual(manifest["copied"], 1)
        self.assertEqual(manifest["copiedBytes"], 2)
        self.assertEqual(manifest["skipped"], [{
            "path": "bin/vendor.DLL",
            "reason": "extension",
        }])
        self.assertEqual(manifest["skippedExecutables"], [{
            "path": "bin/vendor.DLL",
            "size": len(executable),
            "sha256": sha256(executable),
        }])
        self.assertEqual(result, manifest)

    def test_nonempty_destination_exits_two_without_changing_files(self):
        self.write("config.txt", b"new")
        self.dest.mkdir()
        sentinel = self.dest / "existing.txt"
        sentinel.write_bytes(b"keep me")

        code, stdout, stderr = run_main(
            "--source", str(self.source), "--dest", str(self.dest)
        )

        self.assertEqual(code, 2)
        self.assertEqual(stdout, "")
        self.assertIn("not empty", stderr)
        self.assertEqual(sentinel.read_bytes(), b"keep me")
        self.assertEqual(sorted(path.name for path in self.dest.iterdir()), ["existing.txt"])

    def test_destination_inside_source_is_rejected(self):
        self.write("config.txt", b"value")
        nested_dest = self.source / "evidence"

        code, _, stderr = run_main(
            "--source", str(self.source), "--dest", str(nested_dest)
        )

        self.assertEqual(code, 2)
        self.assertIn("must not contain", stderr)
        self.assertFalse(nested_dest.exists())

    def test_source_inside_destination_is_rejected(self):
        self.write("config.txt", b"value")

        code, _, stderr = run_main(
            "--source", str(self.source), "--dest", str(self.root)
        )

        self.assertEqual(code, 2)
        self.assertIn("must not contain", stderr)

    def test_dry_run_creates_nothing_and_reports_counts(self):
        self.write("keep.txt", b"value")
        self.write("skip.exe", b"MZ executable")
        self.write("skip.bin", b"not whitelisted")

        code, stdout, stderr = run_main(
            "--source", str(self.source),
            "--dest", str(self.dest),
            "--dry-run",
        )

        self.assertEqual(code, 0)
        self.assertEqual(stderr, "")
        self.assertIn("would collect 1 files", stdout)
        self.assertIn("extension 2", stdout)
        self.assertFalse(self.dest.exists())

    def test_source_files_contents_and_mtimes_are_unchanged(self):
        self.write("config.json", b"{}")
        self.write("driver.exe", b"MZ driver")
        self.write("unknown.bin", b"binary")
        (self.source / "empty-dir").mkdir()

        before = {
            path.relative_to(self.source).as_posix(): (
                "file",
                path.read_bytes() if path.is_file() else None,
                path.stat().st_mtime_ns,
            )
            for path in self.source.rglob("*")
        }

        collector.collect(self.source, self.dest)

        after = {
            path.relative_to(self.source).as_posix(): (
                "file",
                path.read_bytes() if path.is_file() else None,
                path.stat().st_mtime_ns,
            )
            for path in self.source.rglob("*")
        }
        self.assertEqual(after, before)

    def test_symbolic_links_and_linked_directory_contents_are_skipped(self):
        self.write("real.txt", b"real")
        outside = self.root / "outside"
        outside.mkdir()
        (outside / "secret.json").write_bytes(b"outside")
        file_link = self.source / "linked.json"
        directory_link = self.source / "linked-dir"
        try:
            file_link.symlink_to(outside / "secret.json")
            directory_link.symlink_to(outside, target_is_directory=True)
        except (OSError, NotImplementedError) as exc:
            self.skipTest(f"symbolic links are unavailable: {exc}")

        result = collector.collect(self.source, self.dest)

        skipped = {(item["path"], item["reason"]) for item in result["skipped"]}
        self.assertIn(("linked.json", "link"), skipped)
        self.assertIn(("linked-dir", "link"), skipped)
        self.assertFalse((self.dest / "linked.json").exists())
        self.assertFalse((self.dest / "linked-dir").exists())
        self.assertEqual((outside / "secret.json").read_bytes(), b"outside")


if __name__ == "__main__":
    unittest.main()
