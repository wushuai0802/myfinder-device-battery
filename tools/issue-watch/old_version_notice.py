"""Create local upgrade-notice drafts from an existing T00 issue snapshot."""

from __future__ import annotations

import argparse
import csv
from html import escape
import json
from pathlib import Path
import re
import runpy


CUTOFF = (1, 3, 10)
NUMBER = r"(?:0|[1-9][0-9]*)"
IDENTIFIER = r"[0-9A-Za-z-]+"
VERSION = re.compile(
    rf"v?({NUMBER}\.{NUMBER}\.{NUMBER})"
    rf"(?:-({IDENTIFIER}(?:\.{IDENTIFIER})*))?"
    rf"(?:\+{IDENTIFIER}(?:\.{IDENTIFIER})*)?"
)
MEMBER_SECTION = re.compile(
    r"^##[ \t]+涉及的反馈(?=[ \t（(]|$)[^\n]*\n(.*?)(?=^#{1,2}[ \t]|\Z)",
    re.MULTILINE | re.DOTALL,
)
MEMBER_LINE = re.compile(r"^[ \t]*(?:[-*+]|\d+[.)])[ \t]+#([0-9]+)\b", re.MULTILINE)
COLUMNS = ("编号", "标题", "版本", "目标ID")
TEMPLATE = (
    "这条反馈来自 {版本}，之后已经新增了不少设备。"
    "请先升级到最新版再看看；如果还是读不到，"
    "回复这里并附上新的诊断信息即可。"
)
comment_kind = runpy.run_path(str(Path(__file__).with_name("snapshot.py")))["comment_kind"]


def older_than_supported(version):
    match = VERSION.fullmatch(version.strip())
    if not match:
        return False
    core = tuple(map(int, match[1].split(".")))
    prerelease = match[2]
    if prerelease and any(part.isdigit() and len(part) > 1 and part.startswith("0")
                          for part in prerelease.split(".")):
        return False
    return core < CUTOFF or (core == CUTOFF and prerelease is not None)


def rollup_members(issues):
    numbers = set()
    for issue in issues:
        if not any(label.get("name") == "device-rollup" for label in issue.get("labels", [])):
            continue
        numbers.add(issue["number"])
        body = (issue.get("body") or "").replace("\r\n", "\n")
        found = MEMBER_SECTION.search(body)
        if found is None:
            raise ValueError(f"汇总帖 #{issue['number']} 缺少明确的涉及反馈清单。")
        numbers.update(map(int, MEMBER_LINE.findall(found[1])))
    return numbers


def latest_comments(comments):
    latest = {}
    for comment in comments:
        raw_number = comment.get("issue_url", "").rstrip("/").rsplit("/", 1)[-1]
        if not raw_number.isascii() or not raw_number.isdecimal():
            continue
        number = int(raw_number)
        key = (comment["created_at"], comment["id"])
        previous = latest.get(number)
        if previous is None or key > (previous["created_at"], previous["id"]):
            latest[number] = comment
    return latest


def build_plan(snapshot_dir):
    with (snapshot_dir / "issues.csv").open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        if not set((*COLUMNS, "分组")).issubset(reader.fieldnames or ()):
            raise ValueError("快照 CSV 缺少所需列。")
        rows = list(reader)
    issues = json.loads((snapshot_dir / "issues-raw.json").read_text(encoding="utf-8"))
    comments = json.loads((snapshot_dir / "comments-raw.json").read_text(encoding="utf-8"))

    excluded = rollup_members(issues)
    latest = latest_comments(comments)
    notices, manual = [], []
    for row in sorted(rows, key=lambda entry: int(entry["编号"])):
        number = int(row["编号"])
        if row["分组"] != "C" or number in excluded or not older_than_supported(row["版本"]):
            continue
        entry = (number, row["标题"], row["版本"], row["目标ID"])
        speaker = comment_kind(latest[number]) if number in latest else "用户"
        (notices if speaker in {"维护者", "机器人"} else manual).append(entry)
    return notices, manual


def table(rows):
    lines = ["| 编号 | 标题 | 版本 | 目标 ID |", "| ---: | --- | --- | --- |"]
    for number, title, version, ids in rows:
        title = escape(" ".join(title.splitlines()), quote=False).replace("|", r"\|")
        lines.append(f"| #{number} | {title} | {version} | {ids} |")
    return lines


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--snapshot-dir", type=Path, default=Path("tmp/issue-snapshot"))
    args = parser.parse_args(argv)
    notices, manual = build_plan(args.snapshot_dir)
    out = args.snapshot_dir / "old-version"
    out.mkdir(parents=True, exist_ok=True)
    path = out / "notices.md"
    lines = [
        "# 旧版本反馈升级提示清单", "",
        f"候选 {len(notices)} 条。转人工 {len(manual)} 条。", "",
        "## 待升级提示", "", *table(notices), "",
        "## 转人工", "", *table(manual), "",
        "## 评论模板", "", f"> {TEMPLATE}", "",
    ]
    path.write_text("\n".join(lines), encoding="utf-8")
    print(f"候选 {len(notices)} 条，转人工 {len(manual)} 条。清单 {path}")


if __name__ == "__main__":
    main()
