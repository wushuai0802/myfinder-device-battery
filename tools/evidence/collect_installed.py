"""Copy and hash static configuration files from an installed vendor tree."""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import stat
import sys
from typing import Iterable


DEFAULT_MAX_BYTES = 5 * 1024 * 1024
COPY_EXTENSIONS = frozenset({
    ".xml", ".json", ".ini", ".cfg", ".conf", ".txt", ".js", ".mjs",
    ".css", ".html", ".htm", ".csv", ".yaml", ".yml", ".toml", ".lua",
    ".qml", ".ts", ".map", ".md", ".plist", ".properties",
})
NEVER_COPY_EXTENSIONS = frozenset({
    ".exe", ".dll", ".sys", ".msi", ".bat", ".cmd", ".ps1", ".vbs",
    ".scr", ".com", ".cpl", ".drv", ".ocx", ".jar", ".py", ".sh",
    ".lnk", ".inf", ".cat",
})
SKIPPED_EXECUTABLE_EXTENSIONS = frozenset({".exe", ".dll", ".sys"})
SKIP_REASONS = frozenset({
    "extension", "executable-header", "too-large", "link", "unreadable",
})


class CollectError(ValueError):
    """An input or destination validation error."""


def _as_relative(path: Path, root: Path) -> str:
    return path.relative_to(root).as_posix()


def _is_link(path: Path) -> bool:
    if path.is_symlink():
        return True
    is_junction = getattr(path, "is_junction", None)
    return bool(is_junction and is_junction())


def classify(path: Path | str, max_bytes: int) -> tuple[bool, str | None]:
    """Return whether a path is safe to copy and its fixed skip reason."""
    candidate = Path(path)
    try:
        if _is_link(candidate):
            return False, "link"
        details = candidate.lstat()
        if not stat.S_ISREG(details.st_mode):
            return False, "unreadable"
        extension = candidate.suffix.casefold()
        if extension in NEVER_COPY_EXTENSIONS or extension not in COPY_EXTENSIONS:
            return False, "extension"
        if details.st_size > max_bytes:
            return False, "too-large"
        with candidate.open("rb") as handle:
            if handle.read(2) == b"MZ":
                return False, "executable-header"
    except OSError:
        return False, "unreadable"
    return True, None


def _hash_file(path: Path, chunk_size: int = 1024 * 1024) -> tuple[int, str]:
    digest = hashlib.sha256()
    size = 0
    with path.open("rb") as handle:
        while True:
            chunk = handle.read(chunk_size)
            if not chunk:
                break
            size += len(chunk)
            digest.update(chunk)
    return size, digest.hexdigest()


def _resolved_directory(path: str | os.PathLike[str], label: str) -> Path:
    try:
        resolved = Path(path).resolve(strict=True)
    except OSError as exc:
        raise CollectError(f"{label} does not exist or cannot be resolved: {path}") from exc
    if not resolved.is_dir():
        raise CollectError(f"{label} is not a directory: {path}")
    return resolved


def _validate_paths(
    source: str | os.PathLike[str],
    dest: str | os.PathLike[str],
) -> tuple[Path, Path]:
    source_dir = _resolved_directory(source, "source")
    try:
        dest_dir = Path(dest).resolve(strict=False)
    except OSError as exc:
        raise CollectError(f"destination cannot be resolved: {dest}") from exc

    if (
        dest_dir == source_dir
        or dest_dir.is_relative_to(source_dir)
        or source_dir.is_relative_to(dest_dir)
    ):
        raise CollectError("source and destination must not contain each other")

    raw_dest = Path(dest)
    if os.path.lexists(raw_dest):
        if not dest_dir.is_dir():
            raise CollectError(f"destination is not a directory: {dest}")
        try:
            if any(dest_dir.iterdir()):
                raise CollectError(f"destination is not empty: {dest}")
        except OSError as exc:
            raise CollectError(f"destination cannot be read: {dest}") from exc
    return source_dir, dest_dir


def _scan(
    source: Path,
    max_bytes: int,
) -> tuple[list[tuple[Path, str]], list[dict[str, object]], list[dict[str, object]]]:
    copy_items: list[tuple[Path, str]] = []
    skipped: list[dict[str, object]] = []
    skipped_executables: list[dict[str, object]] = []

    def record_skip(path: Path, reason: str) -> None:
        if reason not in SKIP_REASONS:
            raise AssertionError(f"unknown skip reason: {reason}")
        skipped.append({"path": _as_relative(path, source), "reason": reason})

    def on_walk_error(error: OSError) -> None:
        error_path = Path(error.filename) if error.filename else source
        try:
            error_path = error_path.resolve(strict=False)
            error_path.relative_to(source)
        except (OSError, ValueError):
            error_path = source
        record_skip(error_path, "unreadable")

    for root, directories, filenames in os.walk(
        source, topdown=True, onerror=on_walk_error, followlinks=False
    ):
        root_path = Path(root)
        kept_directories: list[str] = []
        for name in sorted(directories):
            path = root_path / name
            try:
                if _is_link(path):
                    record_skip(path, "link")
                    continue
            except OSError:
                record_skip(path, "unreadable")
                continue
            kept_directories.append(name)
        directories[:] = kept_directories

        for name in sorted(filenames):
            path = root_path / name
            relative = _as_relative(path, source)
            can_copy, reason = classify(path, max_bytes)
            if can_copy:
                copy_items.append((path, relative))
                continue
            record_skip(path, reason or "unreadable")

            if path.suffix.casefold() not in SKIPPED_EXECUTABLE_EXTENSIONS:
                continue
            try:
                if _is_link(path):
                    continue
                details = path.lstat()
                if not stat.S_ISREG(details.st_mode):
                    continue
                size, sha256 = _hash_file(path)
            except OSError:
                continue
            skipped_executables.append({
                "path": relative,
                "size": size,
                "sha256": sha256,
            })

    skipped.sort(key=lambda item: (str(item["path"]), str(item["reason"])))
    skipped_executables.sort(key=lambda item: str(item["path"]))
    copy_items.sort(key=lambda item: item[1])
    return copy_items, skipped, skipped_executables


def _copy_files(
    copy_items: Iterable[tuple[Path, str]],
    destination: Path,
    max_bytes: int,
) -> tuple[list[tuple[str, str]], int]:
    sums: list[tuple[str, str]] = []
    copied_bytes = 0
    for source_path, relative in copy_items:
        target_path = destination.joinpath(*relative.split("/"))
        target_path.parent.mkdir(parents=True, exist_ok=True)
        digest = hashlib.sha256()
        file_bytes = 0
        try:
            with source_path.open("rb") as source_handle, target_path.open("xb") as target_handle:
                while True:
                    chunk = source_handle.read(1024 * 1024)
                    if not chunk:
                        break
                    file_bytes += len(chunk)
                    if file_bytes > max_bytes:
                        raise CollectError(f"source file grew beyond --max-bytes: {source_path}")
                    digest.update(chunk)
                    target_handle.write(chunk)
        except CollectError:
            raise
        except OSError as exc:
            raise CollectError(f"cannot copy source file: {source_path}") from exc
        copied_bytes += file_bytes
        sums.append((relative, digest.hexdigest()))
    return sums, copied_bytes


def _write_text(path: Path, text: str) -> None:
    try:
        with path.open("w", encoding="utf-8", newline="\n") as handle:
            handle.write(text)
    except OSError as exc:
        raise CollectError(f"cannot write evidence file: {path}") from exc


def collect(
    source: str | os.PathLike[str],
    dest: str | os.PathLike[str],
    max_bytes: int = DEFAULT_MAX_BYTES,
    dry_run: bool = False,
) -> dict[str, object]:
    """Validate, scan, and optionally copy an installed vendor tree."""
    if not isinstance(max_bytes, int) or isinstance(max_bytes, bool) or max_bytes < 0:
        raise CollectError("--max-bytes must be a non-negative integer")

    source_dir, dest_dir = _validate_paths(source, dest)
    copy_items, skipped, skipped_executables = _scan(source_dir, max_bytes)
    source_text = os.fspath(source)

    if dry_run:
        return {
            "source": source_text,
            "generatedAt": "",
            "copied": len(copy_items),
            "copiedBytes": sum(path.lstat().st_size for path, _ in copy_items),
            "skipped": skipped,
            "skippedExecutables": skipped_executables,
        }

    try:
        dest_dir.mkdir(parents=True, exist_ok=True)
    except OSError as exc:
        raise CollectError(f"cannot create destination: {dest}") from exc

    sums, copied_bytes = _copy_files(copy_items, dest_dir, max_bytes)
    sums.sort(key=lambda item: item[0])
    sums_text = "".join(f"{digest}  {relative}\n" for relative, digest in sums)
    _write_text(dest_dir / "SHA256SUMS.txt", sums_text)

    manifest: dict[str, object] = {
        "source": source_text,
        "generatedAt": datetime.now(timezone.utc).replace(
            microsecond=0
        ).isoformat().replace("+00:00", "Z"),
        "copied": len(sums),
        "copiedBytes": copied_bytes,
        "skipped": skipped,
        "skippedExecutables": skipped_executables,
    }
    manifest_text = json.dumps(manifest, ensure_ascii=False, indent=2) + "\n"
    _write_text(dest_dir / "MANIFEST.json", manifest_text)
    return manifest


def _nonnegative_integer(value: str) -> int:
    try:
        number = int(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("must be an integer") from exc
    if number < 0:
        raise argparse.ArgumentTypeError("must be non-negative")
    return number


def _reason_counts(skipped: list[dict[str, object]]) -> dict[str, int]:
    counts = {reason: 0 for reason in sorted(SKIP_REASONS)}
    for item in skipped:
        counts[str(item["reason"])] += 1
    return counts


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Collect static configuration files from an installed vendor tree."
    )
    parser.add_argument("--source", required=True)
    parser.add_argument("--dest", required=True)
    parser.add_argument("--max-bytes", type=_nonnegative_integer, default=DEFAULT_MAX_BYTES)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args(argv)

    try:
        result = collect(args.source, args.dest, args.max_bytes, args.dry_run)
    except CollectError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2

    skipped_count = len(result["skipped"])
    if args.dry_run:
        counts = _reason_counts(result["skipped"])
        details = ", ".join(f"{reason} {count}" for reason, count in counts.items())
        print(
            f"dry-run: would collect {result['copied']} files "
            f"({result['copiedBytes']} bytes), skipped {skipped_count} "
            f"({details}) -> {args.dest}"
        )
    else:
        print(
            f"collected {result['copied']} files ({result['copiedBytes']} bytes), "
            f"skipped {skipped_count} -> {args.dest}"
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
