"""Validate device status and optionally refresh counts from an offline ranking."""
from __future__ import annotations

import argparse
from copy import deepcopy
import json
from pathlib import Path
import re
import sys


DEFAULT_STATUS = Path(".github/triage/device-status.json")
DEVICE_ID = re.compile(r"[0-9A-F]{4}:[0-9A-F]{4}")
BETA_VERSION = re.compile(r"[0-9]+\.[0-9]+\.[0-9]+-beta\.[0-9]+")
UNSAFE_NOTE = re.compile(r"[@`<>\r\n\v\f\x1c-\x1e\x85\u2028\u2029]|http", re.IGNORECASE)
STATUSES = {"beta", "researching", "no-evidence", "not-a-device"}
FIELDS = {"status", "version", "note", "reports", "rank"}


def validate(status):
    """Return the unchanged document, or identify its invalid ID and field."""
    if not isinstance(status, dict):
        raise ValueError("document: expected an object")
    if type(status.get("schema")) is not int or status["schema"] != 1:
        raise ValueError("document.schema: expected 1")
    if set(status) - {"schema", "devices"}:
        raise ValueError("document: unexpected field " + sorted(set(status) - {"schema", "devices"})[0])
    if not isinstance(status.get("devices"), dict):
        raise ValueError("document.devices: expected an object")
    for device_id, entry in status["devices"].items():
        if not isinstance(device_id, str) or not DEVICE_ID.fullmatch(device_id):
            raise ValueError(f"{device_id}.id: expected uppercase VID:PID")
        if not isinstance(entry, dict):
            raise ValueError(f"{device_id}.entry: expected an object")
        extra = set(entry) - FIELDS
        if extra:
            raise ValueError(f"{device_id}.{sorted(extra)[0]}: unexpected field")
        if not isinstance(entry.get("status"), str) or entry["status"] not in STATUSES:
            raise ValueError(f"{device_id}.status: unknown or missing status")
        if entry["status"] == "beta" or "version" in entry:
            if not isinstance(entry.get("version"), str) or not BETA_VERSION.fullmatch(entry["version"]):
                raise ValueError(f"{device_id}.version: expected 1.2.3-beta.4")
        if "note" in entry and (not isinstance(entry["note"], str)
                                or len(entry["note"]) > 60 or UNSAFE_NOTE.search(entry["note"])):
            raise ValueError(f"{device_id}.note: expected at most 60 safe, single-line characters")
        for field, minimum in (("reports", 0), ("rank", 1)):
            if field in entry and (type(entry[field]) is not int or entry[field] < minimum):
                raise ValueError(f"{device_id}.{field}: expected an integer >= {minimum}")
    return status


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"{key}: duplicate field or ID")
        result[key] = value
    return result


def load(path):
    try:
        value = json.loads(Path(path).read_text(encoding="utf-8-sig"), object_pairs_hook=_unique_object)
    except (OSError, ValueError) as error:
        raise ValueError(f"document ({path}): {error}") from error
    return validate(value)


def update_counts(status, ranking_rows):
    """Copy counts by ranking position; never alter membership or other fields."""
    validate(status)
    if not isinstance(ranking_rows, list):
        raise ValueError("ranking.rows: expected a list")
    counts = {}
    for position, row in enumerate(ranking_rows, 1):
        if not isinstance(row, dict) or not isinstance(row.get("id"), str) or not DEVICE_ID.fullmatch(row["id"]):
            raise ValueError(f"ranking.rows[{position}].id: expected uppercase VID:PID")
        device_id = row["id"]
        if device_id in counts:
            raise ValueError(f"{device_id}.ranking: duplicate ID")
        if not isinstance(row.get("issues"), list) or any(type(number) is not int or number < 1
                                                        for number in row["issues"]):
            raise ValueError(f"{device_id}.issues: expected issue numbers")
        counts[device_id] = (len(row["issues"]), position)
    updated = deepcopy(status)
    for device_id, entry in updated["devices"].items():
        entry["reports"] = counts.get(device_id, (0, None))[0]
        if device_id in counts:
            entry["rank"] = counts[device_id][1]
        else:
            entry.pop("rank", None)
    return updated


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--status", type=Path, default=DEFAULT_STATUS)
    parser.add_argument("--ranking", type=Path)
    parser.add_argument("--check", action="store_true", help="validate only; never write")
    args = parser.parse_args(argv)
    try:
        status = load(args.status)
        if args.ranking and not args.check:
            ranking = json.loads(args.ranking.read_text(encoding="utf-8-sig"))
            if not isinstance(ranking, dict):
                raise ValueError("ranking.rows: expected an object containing rows")
            status = update_counts(status, ranking.get("rows"))
            status["devices"] = dict(sorted(status["devices"].items()))
            with args.status.open("w", encoding="utf-8", newline="\n") as stream:
                stream.write(json.dumps(status, ensure_ascii=False, indent=2) + "\n")
    except (OSError, ValueError) as error:
        print(error, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
