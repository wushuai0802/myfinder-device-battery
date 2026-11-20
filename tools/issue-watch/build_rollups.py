"""Build local device rollup drafts from a T00 snapshot. Does not contact GitHub."""
from __future__ import annotations

import argparse
import csv
import json
import re
import runpy
from collections import defaultdict
from pathlib import Path


WATCH = Path(__file__).resolve().parent
ROOT = WATCH.parents[1]
TARGETS = {
    "05AC:024F": "共享键盘接收器（按产品串区分）",
    "320F:5055": "VGN V98 Pro / ATK V75X 等键盘",
    "10D6:4801": "西伯利亚 K03S / K02BS 等耳机",
    "25A7:FA70": "前行者 MT108 等键盘",
    "1A2C:7FFF": "SEMICO K98 等键盘",
}
PAIR_LINE = re.compile(r'^\s*([0-9a-f]{4}:[0-9a-f]{4})\s+"([^"\r\n]+)"([^\r\n]*)', re.I | re.M)
USAGE = re.compile(r"\b[0-9A-Fa-f]{4}:[0-9A-Fa-f]{4}\b")
F99 = re.compile(r"\bF99[\s_-]*MAX(?:\s+Dongle)?\b", re.I)
MAINTAINERS = {"OWNER", "MEMBER", "COLLABORATOR"}
MISSING_NAME = "尚无可归属此 ID 的产品串"
MISSING = "完整接口导出、官方软件名称和版本、官方软件显示的电量对照。"
# Use the same accessory list as the digest. The extra ID is the V9 Turbo+ lighting interface.
STATUS = json.loads((ROOT / ".github/triage/device-status.json").read_text(encoding="utf-8"))["devices"]
IGNORED_IDS = (runpy.run_path(str(WATCH / "issue_digest.py"))["NO_BATTERY_IDS"] | {"3837:6008"} |
               {identity.upper() for identity, item in STATUS.items() if item["status"] == "not-a-device"})


def load(snapshot):
    with (snapshot / "issues.csv").open(encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream))
    issues = json.loads((snapshot / "issues-raw.json").read_text(encoding="utf-8"))
    raw = {str(issue["number"]): issue for issue in issues if "pull_request" not in issue}
    comments = defaultdict(list)
    for comment in json.loads((snapshot / "comments-raw.json").read_text(encoding="utf-8")):
        number = comment["issue_url"].rsplit("/", 1)[-1]
        if number in raw:
            comments[number].append(comment)
    for thread in comments.values():
        thread.sort(key=lambda comment: (comment["created_at"], comment["id"]))
    return rows, raw, comments


def role(comment):
    body = (comment.get("body") or "").lstrip()
    if (comment.get("user") or {}).get("type") == "Bot" or body.startswith("<!-- myfinder-triage"):
        return "bot"
    if body.startswith("<!-- myfinder-supplement"):
        return "user"
    return "maintainer" if comment.get("author_association") in MAINTAINERS else "user"


def device_ids(row):
    return {v.strip().upper() for v in row.get("目标ID", "").split(";") if v.strip()} - IGNORED_IDS


def describes_multiple_devices(row, issue):
    # A second problem may be mentioned in ordinary prose without a second USB ID.
    body = (issue.get("body") or "").replace("\r", "")
    description = re.search(r"\*\*描述\*\*\s*\n\x60{3}[^\n]*\n(.*?)\n\x60{3}", body, re.S)
    prose = description[1] if description else body.partition("### 反馈报告")[0].partition("<details>")[0]
    text = row["标题"] + "\n" + prose.split("未取得电量的设备")[0]
    return sum(word in text for word in ("鼠标", "键盘", "耳机")) >= 2


def is_rollup(issue):
    return ((issue.get("title") or "").startswith("[设备汇总]") or
            any(label["name"] == "device-rollup" for label in issue.get("labels", [])))


def existing_rollup(target, raw):
    pattern = re.compile(r"(?<![0-9a-f])" + re.escape(target) + r"(?![0-9a-f])", re.I)
    matches = [number for number, issue in raw.items() if issue.get("state") == "open"
               and "pull_request" not in issue and is_rollup(issue) and pattern.search(issue.get("title", ""))
               and (issue.get("author_association") in MAINTAINERS or
                    any(label["name"] == "device-rollup" for label in issue.get("labels", [])))]
    if len(matches) > 1:
        raise ValueError(f"{target} 对应多个开放汇总，请核对 {matches}")
    return matches[0] if matches else None


def pointer_status(thread, target, existing):
    if not existing:
        return ""
    ref = rf"#{re.escape(str(existing))}(?!\d)"
    pattern = re.compile(rf"^(?:已合并到\s*{ref}|其中\s*{re.escape(target)}\s*的进展请看\s*{ref})")
    for index, comment in reversed(list(enumerate(thread))):
        if role(comment) == "maintainer" and pattern.match((comment.get("body") or "").strip()):
            if any(role(later) == "user" for later in thread[index + 1:]):
                return "已有指向，后有用户回复，留待人工复核"
            return "已有指向，跳过"
    return ""


def diagnostics(target, texts):
    for text in texts:
        for device, product, tail in PAIR_LINE.findall(text):
            if device.upper() == target:
                yield product.strip(), " ".join(usage.upper() for usage in USAGE.findall(tail))


def user_texts(issue, thread):
    return [issue.get("body") or ""] + [c.get("body") or "" for c in thread if role(c) == "user"]


def product_names(target, texts):
    return set(name for name, _ in diagnostics(target, texts) if name and name != "[redacted]")


def selected(rows, target, raw, comments):
    result = []
    for row in rows:
        number = row["编号"]
        issue = raw.get(number, {})
        if (issue.get("state", "open") != "open" or "pull_request" in issue or is_rollup(issue)
                or row["标题"].startswith("[设备汇总]") or ("目标ID" in row and target not in device_ids(row))):
            continue
        if target == "05AC:024F":
            texts = user_texts(issue, comments.get(number, []))
            names = product_names(target, texts)
            if (names and all(F99.fullmatch(name) for name in names)) or (not names and F99.fullmatch(texts[0].strip())):
                continue
        result.append(row)
    return result


def evidence(number, body, target, comments, multi=False):
    texts = [body, *comments]
    result = dict.fromkeys(
        f"#{number}: 产品串 {name}；集合 {usages or '未注明'}"
        for name, usages in diagnostics(target, texts)
        if not (target == "05AC:024F" and F99.fullmatch(name))
    )
    # Keep pasted query/reply samples in order, including repeated zero-filled lines.
    quotes = []
    for text in texts:
        for line in text.splitlines():
            text_line = line.strip()
            if (not text_line or PAIR_LINE.match(text_line) or re.search(
                    r"<!--|</?\w+>|[a-z]:[/\\]|\\\\|https?://\S*[?]|(?:sha256|serial|序列号)\s*[:=：]|@[\w-]", text_line, re.I)):
                continue
            bound = target in text_line.upper()
            if multi and not bound:
                continue
            if re.search(r"官方(?:软件|驱动)|官网驱动|抓包|报文|被动监听|(?:feature|input|output|report)\b|"
                         r"^(?:[0-9a-f]{2} ){3,}[0-9a-f]{2}$|^HID Data:\s*[a-f0-9]{8,}", text_line, re.I):
                if len(text_line) <= 360:
                    quotes.append(f"#{number}: {text_line}")
                    if len(quotes) == 20:
                        return list(result) + quotes
    return list(result) + quotes


def render_rollup(target, rows, raw, comments, reviewed=()):
    active = selected(rows, target, raw, comments)
    name = TARGETS[target]
    groups = defaultdict(list)
    for row in active:
        names = product_names(target, user_texts(raw.get(row["编号"], {}), comments.get(row["编号"], [])))
        visible = [n for n in sorted(names) if not (target == "05AC:024F" and F99.fullmatch(n))]
        group = (" / ".join(visible) or MISSING_NAME) if target == "05AC:024F" else ""
        groups[group].append(row)
    lines = [f"# [设备汇总] {name}（{target}）", "", "## 设备", "", f"{name}，USB ID {target}。", "",
             "## 当前状态（未支持 / 候选 / 等资料）", "", "等资料。需按具体产品串及连接方式核对电量接口。"]
    if target == "05AC:024F":
        lines += ["", "### INFI75", "", "INFI75 当前为 D 级，尚未支持。有线记录为 MI_03 / FF13:0001（Feature 65 字节候选）"
                  "及 MI_02 / FF68:0061。FFB5:0001 的 2.4G 接收器归属仍待核实。[仓库记录](https://github.com/"
                  "wushuai0802/myfinder-device-battery/blob/main/docs/infi75-driver.md)", "",
                  "F99 MAX Dongle 已在 1.3.11-beta.20 加入有精确身份门禁的候选，本次排除该产品。"]
    lines += ["", "## 涉及的反馈（列表，含编号和标题）"]
    for group, sources in groups.items():
        if group:
            lines += ["", f"### {group}"]
        lines += [""] + [f"- #{row['编号']} {row['标题']}" for row in sources]
    existing = existing_rollup(target, raw)
    historical = [n for n, issue in raw.items() if issue.get("state") == "closed" and not is_rollup(issue)
                  and pointer_status(comments.get(n, []), target, existing)]
    lines += ["", "## 已收集的资料（逐条注明来源 #编号）", ""]
    entries = []
    for row in active:
        number = row["编号"]
        body, *supplements = user_texts(raw.get(number, {}), comments.get(number, []))
        entries += evidence(number, body, target, supplements, multi=len(device_ids(row)) > 1)
    if historical:
        lines += ["已归档的原帖只作资料来源：" + "、".join(f"#{n}" for n in sorted(historical, key=int)) + "。", ""]
        for number in sorted(historical, key=int):
            body, *supplements = user_texts(raw[number], comments.get(number, []))
            entries += evidence(number, body, target, supplements, multi=True)
    lines += [f"- {entry}" for entry in entries] or ["暂未取得可归属此 ID 的具体资料。"]
    if reviewed:
        lines += ["", "### 按型号核对的读数与协议资料"]
        for paragraph in reviewed:
            lines.extend(["", paragraph])
    lines += ["", "## 还缺什么", "", MISSING, "", "## 如何补充", "",
              "请在程序里的“我的反馈 → 补充资料”中附上完整型号、连接方式及同次对照。", ""]
    return "\n".join(lines)


def render_actions(target, rows, raw=None, comments=None):
    raw, comments = raw or {}, comments or {}
    existing = existing_rollup(target, raw)
    ref = f"#{existing}" if existing else "#<汇总编号>"
    single, multi, handled = [], [], []
    for row in selected(rows, target, raw, comments):
        number = row["编号"]
        thread = comments.get(number, [])
        status = pointer_status(thread, target, existing)
        if status:
            handled.append(f"- #{number}：{status}（{ref}）。")
            continue
        names = product_names(target, user_texts(raw.get(number, {}), thread))
        if device_ids(row) == {target} and len(names) <= 1 and not describes_multiple_devices(row, raw.get(number, {})):
            single.append(f"- #{number}：评论“已合并到 {ref}，后续进展会在那里更新。如果你的设备情况不同，回复这里即可重新打开。”；加 duplicate；关闭。")
        else:
            multi.append(f"- #{number}：仅评论“其中 {target} 的进展请看 {ref}”；保留开放。")
    lines = [f"# {target} 原 Issue 处理清单", "", f"汇总安排：{'复用 ' + ref if existing else '拟新建'}。",
             "本版待用户本人确认。执行前重读状态和最新评论，逐条记录对应的确认消息、实际动作及回执。",
             "", "## 单设备 issue", "", *(single or ["无。"]), "", "## 多设备 issue", "", *(multi or ["无。"]),
             "", "## 已处理或需复核", "", *(handled or ["无。"]), ""]
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--snapshot", type=Path, default=Path("tmp/issue-snapshot"))
    parser.add_argument("--out", type=Path, default=Path("tmp/issue-snapshot/rollups"))
    parser.add_argument("--reviewed-notes", type=Path, help="按设备 ID 整理且注明原帖的本地 Markdown 段落（JSON）")
    args = parser.parse_args()
    rows, raw, comments = load(args.snapshot)
    notes = json.loads(args.reviewed_notes.read_text(encoding="utf-8")) if args.reviewed_notes else {}
    args.out.mkdir(parents=True, exist_ok=True)
    for target in TARGETS:
        stem = target.replace(":", "_")
        draft = render_rollup(target, rows, raw, comments, notes.get(target, []))
        (args.out / (stem + ".md")).write_text(draft, encoding="utf-8")
        (args.out / (stem + "-actions.md")).write_text(render_actions(target, rows, raw, comments), encoding="utf-8")


if __name__ == "__main__":
    main()
