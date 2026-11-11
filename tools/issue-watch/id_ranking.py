"""Rank unsupported device IDs across open issues using read-only GitHub access.

Run: python -I -B tools/issue-watch/id_ranking.py [--dir OUTPUT] [--min 2]
     [--seed-counts] [--repo owner/name]
"""
from __future__ import annotations

import argparse
import datetime as dt
import importlib.util
import json
from pathlib import Path


_spec = importlib.util.spec_from_file_location(
    "_id_ranking_issue_digest", Path(__file__).with_name("issue_digest.py"))
if _spec is None or _spec.loader is None:
    raise ImportError("Cannot load issue_digest.py")
_digest = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_digest)

gh = _digest.gh
known_ids = _digest.known_ids
parse_body = _digest.parse_body
annotate_unknown = _digest.annotate_unknown
BUILTIN_VENDORS = _digest.BUILTIN_VENDORS
BUILTIN_NAME = _digest.BUILTIN_NAME
HOT_THRESHOLD = _digest.HOT_THRESHOLD
REPO = _digest.REPO


def rank(issues, known):
    """Aggregate eligible IDs, counting each issue once per ID."""
    grouped = {}
    for issue in issues:
        if "pull_request" in issue:
            continue
        unknown = parse_body(issue.get("body"))["unknown"]
        annotate_unknown(unknown, known)
        for device in unknown:
            if (device["knownInMain"] or not device["vendor"]
                    or device["id"][:4] in BUILTIN_VENDORS
                    or BUILTIN_NAME.search(device["name"])):
                continue
            entry = grouped.setdefault(device["id"], {
                "id": device["id"], "names": [], "vendor": set(), "issues": set()})
            entry["issues"].add(issue["number"])
            entry["vendor"].update(device["vendor"])
            if device["name"] not in entry["names"] and len(entry["names"]) < 3:
                entry["names"].append(device["name"])

    rows = [{**entry, "vendor": sorted(entry["vendor"]), "issues": sorted(entry["issues"])}
            for entry in grouped.values()]
    return sorted(rows, key=lambda row: (-len(row["issues"]), row["id"]))


def _md_cell(value):
    return value.replace("|", "\\|").replace("\r", " ").replace("\n", " ")


def render_md(rows, generated_at, total, minimum):
    """Render the filtered table without truncating the underlying ranking."""
    lines = ["# 未支持设备 ID 需求排行", "", f"生成时间（UTC）：{generated_at}", "",
             f"统计的 open issue 数：{total}", "",
             "| 排名 | ID | 设备名 | 集合 | issue 数 | issue 编号 |",
             "| ---: | --- | --- | --- | ---: | --- |"]
    listed = [row for row in rows if len(row["issues"]) >= minimum]
    for position, row in enumerate(listed, 1):
        numbers = "、".join(f"#{number}" for number in row["issues"][:12])
        if len(row["issues"]) > 12:
            numbers += f" 等 {len(row['issues'])} 条"
        names = _md_cell("、".join(row["names"]))
        vendor = _md_cell("、".join(row["vendor"]))
        lines.append(f"| {position} | {row['id']} | {names} | {vendor} | "
                     f"{len(row['issues'])} | {numbers} |")
    return "\n".join(lines) + "\n"


def merge_counts(counts, rows, threshold):
    """Merge issue sets without mutating inputs; malformed counts start empty."""
    if not isinstance(counts, dict) or any(
            not isinstance(entry, dict)
            or not isinstance(entry.get("issues"), list)
            or any(type(number) is not int for number in entry["issues"])
            or not isinstance(entry.get("flagged"), bool)
            or not isinstance(entry.get("name"), str)
            for entry in counts.values()):
        counts = {}
    merged = {device_id: {"issues": sorted(set(entry["issues"])),
                          "flagged": entry["flagged"], "name": entry["name"]}
              for device_id, entry in counts.items()}
    for row in rows:
        name = row["names"][0] if row["names"] else ""
        entry = merged.setdefault(row["id"], {"issues": [], "flagged": False, "name": name})
        entry["issues"] = sorted(set(entry["issues"]) | set(row["issues"]))
        if not entry["name"]:
            entry["name"] = name
    for entry in merged.values():
        entry["flagged"] = entry["flagged"] or len(entry["issues"]) >= threshold
    return merged


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dir", default=r"E:\AIArchive\issue-digest")
    parser.add_argument("--repo", default=REPO)
    parser.add_argument("--min", type=int, default=2)
    parser.add_argument("--seed-counts", action="store_true")
    args = parser.parse_args(argv)

    issues = gh(f"repos/{args.repo}/issues?state=open&per_page=100", paginate=True)
    total = sum("pull_request" not in issue for issue in issues)
    rows = rank(issues, known_ids())
    generated_at = dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    output = Path(args.dir)
    output.mkdir(parents=True, exist_ok=True)
    (output / "id-ranking.md").write_text(
        render_md(rows, generated_at, total, args.min), encoding="utf-8")
    (output / "id-ranking.json").write_text(json.dumps(
        {"generatedAt": generated_at, "totalIssues": total, "rows": rows},
        ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    if args.seed_counts:
        counts_path = output / "id-counts.json"
        try:
            with counts_path.open(encoding="utf-8") as stream:
                counts = json.load(stream)
        except (OSError, ValueError):
            counts = {}
        counts_path.write_text(json.dumps(
            merge_counts(counts, rows, HOT_THRESHOLD), ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8")

    listed = sum(len(row["issues"]) >= args.min for row in rows)
    print(f"ranking: {total} issues, {len(rows)} ids, {listed} listed -> {args.dir}")


if __name__ == "__main__":
    main()
