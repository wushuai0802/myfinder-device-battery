"""Build local rollup drafts only; this script never writes to GitHub."""
from __future__ import annotations

import argparse
import csv
import json
from pathlib import Path
import re


TARGETS = ["05AC:024F", "3837:1012", "10D6:4801", "25A7:FA70", "1A2C:7FFF"]
PAIR_LINE = re.compile(r'^([0-9A-F]{4}:[0-9A-F]{4}) "(.*?)"(.*)$', re.I)


def load(snapshot):
    with (snapshot / "issues.csv").open(encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream))
    raw = {str(issue["number"]): issue for issue in json.loads((snapshot / "issues-raw.json").read_text(encoding="utf-8"))}
    comments = {}
    for comment in json.loads((snapshot / "comments-raw.json").read_text(encoding="utf-8")):
        number = str(comment.get("issue_url", "")).rstrip("/").split("/")[-1]
        if number.isdigit() and (comment.get("user") or {}).get("type") != "Bot":
            comments.setdefault(number, []).append(comment.get("body") or "")
    return rows, raw, comments


def selected(rows, target):
    return [row for row in rows if target in row["目标ID"].split(";")]


def evidence(number, body, target, comments):
    found = []
    for line in (body + "\n" + "\n".join(comments)).splitlines():
        match = PAIR_LINE.match(line.strip())
        if match and match[1].upper() == target:
            found.append("#{}: 产品串 {}；集合 {}".format(number, match[2], match[3].strip()))
        elif re.search(r"官方.*(?:显示|软件|驱动)|(?:请求|回复|report|Report|字节|Feature|Output|Input|VID:PID)", line):
            found.append("#{}: {}".format(number, line.strip()))
        if len(found) >= 20:
            break
    return found


def render_rollup(target, rows, raw, comments):
    ordinary = [row for row in rows if not (target == "05AC:024F" and "F99 MAX" in (raw.get(row["编号"], {}).get("body") or ""))]
    entries = []
    for row in ordinary:
        entries.extend(evidence(row["编号"], raw[row["编号"]].get("body") or "", target, comments.get(row["编号"], [])))
    extra = ""
    if target == "05AC:024F":
        extra = "\n### F99 MAX 例外\nF99 MAX 已在 beta.20 以精确产品串、集合、报告长度及固件身份门禁加入 C 级候选；它不代表其他 05AC:024F 设备已支持。\n\n### INFI75\nINFI75 仍为未支持/D 级；同 PID 不能外推，当前不发送私有命令，等待 T20/T21 的只读资料。\n"
    lines = ["# [设备汇总] " + target, "", "## 设备", target, "", "## 当前状态（未支持 / 候选 / 等资料）", "等资料", extra,
             "## 涉及的反馈（列表，含编号和标题)"]
    lines += ["- #{} {}".format(row["编号"], row["标题"]) for row in ordinary]
    lines += ["", "## 已收集的资料（逐条注明来源 #编号）"] + (entries or ["- 暂无可核对的独有协议资料。"])
    lines += ["", "## 还缺什么", "完整接口导出、官方软件名称和版本、官方软件显示的电量对照。", "", "## 如何补充", "请在程序里的“我的反馈 → 补充资料”中补充，不需要重新提交。", ""]
    return "\n".join(lines)


def render_actions(target, rows):
    single = [row for row in rows if row["目标ID"] == target]
    multi = [row for row in rows if row["目标ID"] != target]
    lines = ["# {} 原 issue 处理清单".format(target), "", "此文件是待确认草稿，不执行任何 GitHub 操作。", "", "## 单设备 issue"]
    lines += ["- #{}：评论“已合并到 #<汇总编号>，后续进展会在那里更新。如果你的设备情况不同，回复这里即可重新打开。”；加 duplicate；关闭。".format(row["编号"]) for row in single]
    lines += ["", "## 多设备 issue"]
    lines += ["- #{}：仅评论“其中 {} 的进展请看 #<汇总编号>”；不关闭。".format(row["编号"], target) for row in multi]
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--snapshot", type=Path, default=Path("tmp/issue-snapshot"))
    parser.add_argument("--out", type=Path, default=Path("tmp/issue-snapshot/rollups"))
    args = parser.parse_args()
    rows, raw, comments = load(args.snapshot)
    args.out.mkdir(parents=True, exist_ok=True)
    for target in TARGETS:
        target_rows = selected(rows, target)
        stem = target.replace(":", "_")
        (args.out / (stem + ".md")).write_text(render_rollup(target, target_rows, raw, comments), encoding="utf-8")
        (args.out / (stem + "-actions.md")).write_text(render_actions(target, target_rows), encoding="utf-8")


if __name__ == "__main__":
    main()
