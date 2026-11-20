"""Prepare local confirmation notices without writing to GitHub."""
from __future__ import annotations

import argparse
import csv
import json
from pathlib import Path
import subprocess


def known_names():
    data = json.loads(subprocess.check_output(["python", "-I", "-B", "engine/known_ids.py", "--json"], text=True))
    names = {}
    for item in data.get("devices", []):
        names.setdefault(f"{item['vendorId']:04X}:{item['productId']:04X}", item.get("name", ""))
    return names


def classify(rows):
    notify, model_check, manual = [], [], []
    for row in rows:
        group, gated = row.get("分组", ""), row.get("含型号门禁ID", "否")
        if gated == "是" and group in {"A", "B"}:
            model_check.append(row)
        elif group == "A" and row.get("最后发言") == "用户":
            manual.append(row)
        elif group == "A" and row.get("最后发言") in {"维护者", "机器人"}:
            notify.append(row)
    return notify, model_check, manual


def table(rows, names):
    lines = ["| 编号 | 标题 | 目标 ID | 对应读取器名称 | 最后发言时间 |",
             "| ---: | --- | --- | --- | --- |"]
    for row in rows:
        ids = [item for item in row.get("目标ID", "").split(";") if item]
        readers = "、".join(dict.fromkeys(names.get(item, "") for item in ids if names.get(item, "")))
        lines.append(f"| {row.get('编号','')} | {row.get('标题','')} | {row.get('目标ID','')} | {readers} | {row.get('最后发言时间','')} |")
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--snapshot", type=Path, default=Path("tmp/issue-snapshot"))
    parser.add_argument("--out", type=Path, default=Path("tmp/issue-snapshot/confirm"))
    args = parser.parse_args()
    with (args.snapshot / "issues.csv").open(encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream))
    names = known_names()
    notify, model_check, manual = classify(rows)
    args.out.mkdir(parents=True, exist_ok=True)
    template = ("感谢反馈！你提到的设备（{型号}）在 1.3.10 正式版已经可以读取电量。"
                "请升级到最新版后看看是否正常显示；如果还是读不到，直接在这里回复即可。"
                "14 天内没有新回复的话，这条反馈会先自动关闭，之后回复仍可重新打开。")
    (args.out / "notify.md").write_text(table(notify, names) + "\n将要发送的评论模板：\n\n" + template + "\n", encoding="utf-8")
    (args.out / "model-check.md").write_text(table(model_check, names), encoding="utf-8")
    (args.out / "manual.md").write_text(table(manual, names), encoding="utf-8")
    print(f"notify={len(notify)} model-check={len(model_check)} manual={len(manual)} total={len(notify)+len(model_check)+len(manual)}")


if __name__ == "__main__":
    main()
