"""Rule-based, offline issue triage. No issue text is executed or echoed in replies.

Usage: triage.py --issue issue.json --comments comments.json --known-ids released.json --known-ids-next main.json
The comments file may be a flat list or gh api --paginate --slurp pages; without
--comments, an embedded issue["comments"] list is used. A null label means no-op.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re


MARKER = "<!-- myfinder-triage:v1 -->"
NEEDS_INFO = ("自动回复：这次反馈没有带上设备信息，我们没法判断是哪款设备。请在程序里打开‘反馈问题’，"
              "勾选‘附带诊断信息’后重新提交，或在这里粘贴完整的反馈报告。维护者会尽快查看。")
SUPPORTED = ("自动回复：报告里的 {models} 在支持列表中。如果还是没有电量，请依次试试："
             "1）唤醒设备后在程序里点重新同步；2）暂时关闭厂商驱动后再试；3）拔插接收器。"
             "仍不行的话，请在这里补充一次新的反馈报告。维护者会尽快查看。")
IN_NEXT_RELEASE = ("自动回复：报告里的 {models} 已在开发版中加入读取，会随下一个测试版提供，届时会在这里通知。"
                   "新加入的设备需要实机确认，到时欢迎帮忙对照官方软件的读数。维护者会尽快查看。")
PAIR = re.compile(r"(?<![\w:])([0-9A-Fa-f]{4}):([0-9A-Fa-f]{4})(?![\w:])")
TYPE = re.compile(r"^[ \t]*(?:\*\*)?类型(?:\*\*)?[ \t]*[：:][ \t]*([^\r\n]+?)[ \t\r]*$", re.MULTILINE)
DEVICE_TYPES = {"设备不识别", "电量或 DPI 不对"}
BUG_TYPES = {"界面或操作", "其他"}
HEADINGS = {"有卡片的设备": "card", "未取得电量的设备": "unadapted",
            "已隐藏的重复连接": "ignore", "读取器健康": "ignore"}
NOISE_NAME = re.compile(r"HIDI2C|Touchpad|Multitouch|Precision", re.IGNORECASE)


def comments_list(value):
    if not isinstance(value, list):
        return []
    return [comment for page in value for comment in (page if isinstance(page, list) else [page])
            if isinstance(comment, dict)]


def labels_of(issue):
    return sorted({item if isinstance(item, str) else item["name"] for item in issue.get("labels", [])})


def device_row(match, line, source):
    # Product names may contain ID-shaped text; collections follow the last quote.
    tail = line[match.end():]
    if '"' in tail:
        name, tail = tail.rsplit('"', 1)
        name = name.split('"', 1)[-1]
    else:
        collection = PAIR.search(tail)
        name = tail[:collection.start()] if collection else tail
    name = line[:match.start()] + name
    collections = tuple((int(m[1], 16), int(m[2], 16)) for m in PAIR.finditer(tail))
    return (int(match[1], 16), int(match[2], 16), collections, source, name.strip())


def report_devices(text):
    """Keep each device's collections on its own row, excluding quoted names.

    Recognised report sections take precedence over loose VID:PID mentions.
    This is important for card names, hidden duplicates and reader error text.
    """
    rows, section = [], None
    for raw in text.splitlines():
        line = raw.strip()
        heading = line.lstrip("# ")
        matched = next((kind for title, kind in HEADINGS.items()
                        if heading == title or heading.startswith(title + "（")), None)
        if matched:
            section = matched
            continue
        if line.startswith(("```", "~~~", "</details>", "### ")):
            section = None
            continue
        if section == "ignore":
            continue
        if section == "card":
            # FeedbackReport emits five fields even when VID:PID is unavailable.
            # Card presence matters, but neither its name nor identity is evidence.
            if len(line.split(" | ")) >= 5:
                rows.append((None, None, (), "card", ""))
            continue
        if section == "unadapted":
            match = PAIR.match(line)
            if match:
                rows.append(device_row(match, line, "unadapted"))
            continue
        matches = list(PAIR.finditer(line))
        if matches:
            # A loose inventory row follows the same first-ID, then-collections
            # convention. A bare usage list is not a USB device identity.
            first = matches[0]
            if int(first[1], 16) in (0x0001, 0x000C) or int(first[1], 16) >= 0xFF00:
                continue
            rows.append(device_row(first, line, "mention"))
    return rows


def decide(issue, known, comments=None, known_next=None):
    labels = labels_of(issue)
    result = {"label": None, "reply": None, "reason": "未命中设备或问题类型规则。", "remove_labels": []}
    if "triage:locked" in labels or "pull_request" in issue:
        result["reason"] = "维护者已锁定初筛。" if "triage:locked" in labels else "跳过 Pull Request。"
        return result
    comments = comments_list(issue.get("comments") if comments is None else comments)
    body = issue.get("body") or ""
    category_match = TYPE.search(body)
    category = category_match[1] if category_match else ""
    rows = report_devices(body)
    for comment in comments:
        user = comment.get("user") or {}
        if user.get("type") != "Bot" and not user.get("login", "").endswith("[bot]"):
            rows.extend(report_devices(comment.get("body") or ""))
    device_request = category in DEVICE_TYPES or (category not in BUG_TYPES and (
        "### 反馈报告" in body or any(s in (issue.get("title") or "") for s in ("设备适配", "设备反馈"))))
    index = {}
    for device in known["devices"]:
        index.setdefault((device["vendorId"], device["productId"]), []).append(device)
    next_index = {}
    for device in (known_next or {}).get("devices", []):
        next_index.setdefault((device["vendorId"], device["productId"]), []).append(device)
    vendors = {vid for ids in known["vendors"].values() for vid in ids}
    vendors.update(vid for ids in (known_next or {}).get("vendors", {}).values() for vid in ids)
    known_pairs = index.keys() | next_index.keys()
    reported = rows
    rows, ignored = [], []
    for row in reported:
        # Cards already have readings. Retain only their presence for needs-info
        # and the card-only incorrect-reading exception, never their identities.
        if row[3] == "card":
            continue
        if set(row[2]) == {(0x0001, 0x0002)} or NOISE_NAME.search(row[4]):
            ignored.append(row)
        else:
            rows.append(row)
    hits = [(row, index[row[:2]]) for row in rows if row[3] == "unadapted" and row[:2] in index]
    next_hits = [(row, next_index[row[:2]]) for row in rows
                 if row[3] == "unadapted" and row[:2] not in index and row[:2] in next_index]
    new_vendors = [row for row in rows if row[0] not in vendors]
    reply = None
    if device_request and not reported:
        label, reason, reply = "needs-info", "设备类反馈缺少 VID:PID。", NEEDS_INFO
    elif category == "电量或 DPI 不对" and reported and all(row[3] == "card" for row in reported):
        label, reason = "bug", "只有已取得电量的卡片设备，但反馈电量或 DPI 读数不对，需要人工检查。"
    elif hits or next_hits:
        label = "supported" if hits else "in-next-release"
        reason = ("设备 ID 命中已发布版本的内置支持列表，但本次未取得电量。" if hits
                  else "设备 ID 仅命中开发版支持列表，尚未包含在最新正式发布中。")
        # Only repository-owned model names enter the fixed reply, never names
        # supplied by the issue author. Neutralise mentions in future pack names.
        names = sorted({d["name"].replace("@", "＠").replace("\n", " ").replace("\r", " ")
                        for _, devices in (hits or next_hits) for d in devices})
        reply = (SUPPORTED if hits else IN_NEXT_RELEASE).format(models="、".join(names))
    elif any(row[:2] not in known_pairs and row[0] in vendors and any(page >= 0xFF00 for page, _ in row[2])
             for row in rows):
        label, reason = "pack-candidate", "已支持厂商的未知产品 ID，且同一设备带厂商自定义集合。"
    elif new_vendors:
        ids = "、".join(sorted({f"{row[0]:04X}:{row[1]:04X}" for row in new_vendors}))
        label, reason = "new-vendor", "设备厂商 ID 不在当前支持范围：" + ids + "。"
    elif (rows and all(row[2] and all(page < 0xFF00 for page, _ in row[2]) for row in rows)) or (ignored and not rows):
        label, reason = "insufficient-evidence", "排除噪声后只看到标准 HID 接口，无法确认私有电量协议。"
    elif category in BUG_TYPES and not rows:
        label, reason = "bug", "界面、操作或其他问题，未包含待分类设备 ID。"
    elif rows:
        label, reason = "insufficient-evidence", "缺少未取得电量设备的支持匹配或未知产品的厂商自定义集合证据。"
    elif reported:
        label, reason = "insufficient-evidence", "报告仅有已取得电量的卡片设备，缺少待分类设备的证据。"
    else:
        return result
    if ignored:
        ids = "、".join(sorted({f"{row[0]:04X}:{row[1]:04X}" for row in ignored}))
        reason += " 已忽略触摸板、虚拟设备名称或仅含 0001:0002 集合的行：" + ids + "。"
    result.update(label="triage:" + label, reason=reason)
    result["remove_labels"] = [old for old in labels if old.startswith("triage:") and old != result["label"]]
    if reply and not any((comment.get("user") or {}).get("login") == "github-actions[bot]"
                         and MARKER in (comment.get("body") or "") for comment in comments):
        result["reply"] = MARKER + "\n" + reply
    return result


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--issue", required=True)
    parser.add_argument("--comments")
    parser.add_argument("--known-ids", required=True)
    parser.add_argument("--known-ids-next", help="main index; --known-ids is the latest release")
    args = parser.parse_args()
    result = decide(read_json(args.issue), read_json(args.known_ids),
                    read_json(args.comments) if args.comments else None,
                    read_json(args.known_ids_next) if args.known_ids_next else None)
    print(json.dumps(result, ensure_ascii=True, sort_keys=True))


if __name__ == "__main__":
    main()
