"""Read-only GitHub issue snapshot for MyFinder triage."""
from __future__ import annotations

import argparse
import csv
import json
from collections import Counter, defaultdict
from pathlib import Path
import re
import subprocess


PAIR = re.compile(r"(?<![0-9A-Fa-f])([0-9A-Fa-f]{4})[:/]([0-9A-Fa-f]{4})(?![0-9A-Fa-f])")
VERSION = re.compile(r"来自程序内反馈 · 版本 (\S+)")
CATEGORY = re.compile(r"\*\*类型\*\*：([^\r\n]+)")
DESCRIPTION = re.compile(r"\*\*描述\*\*\s*\n```[^\n]*\n(.*?)\n```", re.DOTALL)
DIAGNOSTICS = re.compile(r"<summary>诊断信息</summary>.*?```+\n(.*?)\n```+", re.DOTALL)
TEMPLATE_DESCRIPTION = re.compile(r"^### 品牌与完整型号\s*\n(.*?)(?=^### 反馈报告\s*$)", re.DOTALL | re.MULTILINE)
TEMPLATE_REPORT = re.compile(r"^### 反馈报告\s*\n```text\n(.*?)\n```", re.DOTALL | re.MULTILINE)
UNADAPTED = "未取得电量的设备"
SYSTEM_NAME = re.compile(r"HIDI2C|ITE Device|GIGABYTE|HID VHF|N-KEY|MYSTIC LIGHT|extscreen|HUB|Touch", re.I)
SYSTEM_VIDS = {"8087", "06CB", "04F3", "27C6", "2808", "093A", "048D", "0DB0", "5E2A", "345F", "C019"}
NOT_DEVICE_VIDS = {"0000", "0001", "000B", "000C", "000D", "000F"}
MAINTAINERS = {"OWNER", "MEMBER", "COLLABORATOR"}


def gh_get_pages(repo, endpoint):
    command = ["gh", "api", f"repos/{repo}/{endpoint}", "--paginate", "--slurp"]
    return json.loads(subprocess.check_output(command, text=True, encoding="utf-8"))


def flatten_pages(value):
    if not isinstance(value, list):
        return []
    return [row for page in value for row in (page if isinstance(page, list) else [page]) if isinstance(row, dict)]


def id_text(pair):
    return f"{pair[0]:04X}:{pair[1]:04X}"


def pairs(text):
    result = []
    for match in PAIR.finditer(text or ""):
        vid, pid = match[1].upper(), match[2].upper()
        if vid not in NOT_DEVICE_VIDS and int(vid, 16) < 0xFF00:
            pair = (int(vid, 16), int(pid, 16))
            if pair not in result:
                result.append(pair)
    return result


def parse_body(body):
    body = body or ""
    version = VERSION.search(body)
    category = CATEGORY.search(body)
    description = DESCRIPTION.search(body) or TEMPLATE_DESCRIPTION.search(body)
    return (version.group(1) if version else "", category.group(1).strip() if category else "",
            description.group(1) if description else "")


def system_device(vid, name):
    return bool(SYSTEM_NAME.search(name or "")) or (vid in SYSTEM_VIDS and not re.search(r"Receiver|Mouse|Keyboard", name or "", re.I))


def diagnostic_devices(body):
    details = DIAGNOSTICS.search(body or "")
    template = TEMPLATE_REPORT.search(body or "")
    text = details.group(1) if details else template.group(1) if template else ""
    if not text:
        return []
    rows, inside = [], False
    for raw in text.splitlines():
        line = raw.strip()
        if line == UNADAPTED:
            inside = True
            continue
        if inside and line in {"有卡片的设备", "已隐藏的重复连接", "读取器健康"}:
            break
        if inside:
            found = re.match(r'^([0-9A-F]{4}):([0-9A-F]{4}) "(.*?)"', line, re.I)
            if found:
                vid, pid, name = found[1].upper(), found[2].upper(), found[3]
                if not system_device(vid, name):
                    rows.append((int(vid, 16), int(pid, 16), name))
    return rows


def target_ids(body):
    _version, _category, description = parse_body(body)
    described = pairs(description)
    return described or [(vid, pid) for vid, pid, _name in diagnostic_devices(body)]


def is_template_issue(body):
    return bool(TEMPLATE_REPORT.search(body or ""))


def load_known(engine_root=None):
    command = ["python", "-I", "-B", "engine/known_ids.py", "--json"]
    if engine_root:
        command += ["--engine-root", str(engine_root)]
    value = json.loads(subprocess.check_output(command, text=True, encoding="utf-8"))
    ids, names, gated = set(), {}, set()
    for item in value.get("devices", []):
        pair = (item["vendorId"], item["productId"])
        ids.add(pair)
        names.setdefault(pair, item.get("name", ""))
        if item.get("requiresModelMatch") is True:
            gated.add(pair)
    return ids, names, gated


def comment_kind(comment):
    user = comment.get("user") or {}
    if user.get("type") == "Bot":
        return "机器人"
    if (comment.get("body") or "").lstrip().startswith("<!-- myfinder-supplement"):
        return "用户"
    return "维护者" if comment.get("author_association") in MAINTAINERS else "用户"


def last_speaker(issue, comments):
    comments = [row for row in comments if comment_kind(row) != "机器人"]
    if not comments:
        return "用户", issue.get("created_at") or ""
    latest = max(comments, key=lambda row: row.get("created_at") or "")
    return comment_kind(latest), latest.get("created_at") or ""


def group_for(ids, released, main):
    if not ids:
        return "D"
    if all(pair in released for pair in ids):
        return "A"
    return "B" if all(pair in main for pair in ids) else "C"


def rows_for(issues, comments, released, main, gated):
    comments_by_issue = defaultdict(list)
    for comment in comments:
        number = str(comment.get("issue_url", "")).rstrip("/").split("/")[-1]
        if number.isdigit():
            comments_by_issue[int(number)].append(comment)
    rows = []
    for issue in issues:
        if issue.get("state", "").lower() != "open" or not isinstance(issue.get("number"), int):
            continue
        version, category, _description = parse_body(issue.get("body"))
        ids = target_ids(issue.get("body"))
        speaker, at = last_speaker(issue, comments_by_issue[issue["number"]])
        diagnostic_names = defaultdict(list)
        for vid, pid, name in diagnostic_devices(issue.get("body")):
            if name and name != "[redacted]":
                diagnostic_names[(vid, pid)].append(name)
        rows.append({"编号": issue["number"], "日期": issue.get("created_at", ""), "版本": version, "类型": category,
                     "分组": group_for(ids, released, main), "目标ID": ";".join(map(id_text, ids)),
                     "含型号门禁ID": "是" if any(pair in gated for pair in ids) else "否",
                     "最后发言": speaker, "最后发言时间": at, "标题": issue.get("title", ""),
                     "模板格式": is_template_issue(issue.get("body")), "诊断名称": diagnostic_names})
    return sorted(rows, key=lambda row: row["编号"])


def write_outputs(out, issues, comments, rows, names):
    out.mkdir(parents=True, exist_ok=True)
    (out / "issues-raw.json").write_text(json.dumps(issues, ensure_ascii=False, indent=2), encoding="utf-8")
    (out / "comments-raw.json").write_text(json.dumps(comments, ensure_ascii=False, indent=2), encoding="utf-8")
    fields = ["编号", "日期", "版本", "类型", "分组", "目标ID", "含型号门禁ID", "最后发言", "最后发言时间", "标题"]
    with (out / "issues.csv").open("w", newline="", encoding="utf-8-sig") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        writer.writerows({field: row[field] for field in fields} for row in rows)
    groups = Counter(row["分组"] for row in rows)
    speakers = Counter(row["最后发言"] for row in rows)
    unknown, unknown_names = defaultdict(list), defaultdict(Counter)
    for row in rows:
        if row["分组"] == "C":
            for value in filter(None, row["目标ID"].split(";")):
                unknown[value].append(row["编号"])
                pair = tuple(int(part, 16) for part in value.split(":"))
                unknown_names[value].update(row["诊断名称"].get(pair, []))
    ranked = sorted(unknown.items(), key=lambda item: (-len(set(item[1])), item[0]))[:50]
    lines = ["# Issue 快照汇总", "", "## 分组", "", "| 组 | 数量 |", "| --- | ---: |"]
    lines += [f"| {key} | {groups[key]} |" for key in "ABCD"]
    lines += ["", "## 最后发言", "", "| 类型 | 数量 |", "| --- | ---: |"]
    lines += [f"| {key} | {speakers[key]} |" for key in ("用户", "维护者", "机器人")]
    template_d = sum(row["分组"] == "D" and row["模板格式"] for row in rows)
    lines += [f"", f"D 组中模板格式 issue：{template_d}", "", "## 未知 ID 前 50", "", "| ID | 涉及 issue 数 | 常见名称 | issue |", "| --- | ---: | --- | --- |"]
    for identity, numbers in ranked:
        common = "、".join(name for name, _count in unknown_names[identity].most_common(2))
        lines.append(f"| {identity} | {len(set(numbers))} | {common} | {', '.join(map(str, sorted(set(numbers))))} |")
    (out / "summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def release_index(root):
    path = root / "tmp" / "v1310"
    subprocess.run(["git", "worktree", "add", str(path), "v1.3.10"], check=True)
    try:
        return load_known(path / "engine")
    finally:
        subprocess.run(["git", "worktree", "remove", "--force", str(path)], check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", default="wushuai0802/myfinder-device-battery")
    parser.add_argument("--out", type=Path, default=Path("tmp/issue-snapshot"))
    args = parser.parse_args()
    issues = [row for row in flatten_pages(gh_get_pages(args.repo, "issues?state=all&per_page=100")) if "pull_request" not in row]
    comments = flatten_pages(gh_get_pages(args.repo, "issues/comments?per_page=100"))
    released, released_names, released_gated = release_index(Path.cwd())
    main_ids, main_names, main_gated = load_known()
    names = dict(released_names)
    names.update(main_names)
    write_outputs(args.out, issues, comments, rows_for(issues, comments, released, main_ids, released_gated | main_gated), names)


if __name__ == "__main__":
    main()
