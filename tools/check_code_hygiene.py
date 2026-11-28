"""Lightweight static hygiene gate for CI (zero-regression baselines & version sync)."""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

# Zero-regression ceilings captured on 2026-11-28 (lower over time as debt is retired).
MAX_CS_EMPTY_CATCH = 151
MAX_PY_BROAD_EXCEPT_SILENT = 64

CS_EMPTY_CATCH_RE = re.compile(r"catch\s*(?:\([^)]*\))?\s*\{\s*\}")
PY_BROAD_EXCEPT_RE = re.compile(
    r"except\s+Exception\b[^\n:]*:\s*(?:pass|return\s+None|return\s+\[\])"
)
PY_PRINT_RE = re.compile(r"(?<!\w)print\s*\(")


def check_release_version(errors: list[str]) -> None:
    release_path = ROOT / "release.json"
    data = json.loads(release_path.read_text(encoding="utf-8"))
    version = str(data.get("version", ""))
    assembly = str(data.get("assemblyVersion", ""))
    base = version.split("-", 1)[0]
    expected_assembly = f"{base}.0"
    if not re.fullmatch(r"\d+\.\d+\.\d+", base):
        errors.append(f"release.json: invalid version '{version}'")
        return
    if assembly != expected_assembly:
        errors.append(
            f"release.json: assemblyVersion '{assembly}' does not match version base '{expected_assembly}'"
        )


def check_exception_baselines(errors: list[str]) -> tuple[int, int]:
    cs_count = 0
    for path in sorted((ROOT / "scripts").glob("*.cs")):
        text = path.read_text(encoding="utf-8", errors="replace")
        cs_count += len(CS_EMPTY_CATCH_RE.findall(text))
    if cs_count > MAX_CS_EMPTY_CATCH:
        errors.append(
            f"C# empty catch count regressed: {cs_count} > baseline {MAX_CS_EMPTY_CATCH}"
        )

    py_count = 0
    for path in sorted((ROOT / "engine").rglob("*.py")):
        if "tests" in path.parts:
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        py_count += len(PY_BROAD_EXCEPT_RE.findall(text))
    if py_count > MAX_PY_BROAD_EXCEPT_SILENT:
        errors.append(
            f"Python silent broad except count regressed: {py_count} > baseline {MAX_PY_BROAD_EXCEPT_SILENT}"
        )
    return cs_count, py_count


def check_provider_stdout_clean(errors: list[str]) -> None:
    providers_dir = ROOT / "engine" / "providers"
    for path in sorted(providers_dir.glob("*.py")):
        for lineno, line in enumerate(path.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
            stripped = line.lstrip()
            if stripped.startswith("#"):
                continue
            if PY_PRINT_RE.search(line):
                errors.append(f"{path.relative_to(ROOT)}:{lineno}: print() forbidden in engine/providers (pollutes IPC stdout)")


def main() -> int:
    errors: list[str] = []
    check_release_version(errors)
    cs_count, py_count = check_exception_baselines(errors)
    check_provider_stdout_clean(errors)
    if errors:
        for item in errors:
            print(f"HYGIENE FAIL: {item}", file=sys.stderr)
        return 1
    print(
        f"HYGIENE OK: release.json aligned, cs_empty_catch={cs_count}/{MAX_CS_EMPTY_CATCH}, "
        f"py_silent_except={py_count}/{MAX_PY_BROAD_EXCEPT_SILENT}, provider_prints=0"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
