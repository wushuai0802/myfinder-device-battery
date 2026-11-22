"""Collect what changed on the issue tracker since the last run into a digest.

Read-only: uses the logged-in GitHub CLI (`gh api`) and the repository's own
offline ID index (engine/known_ids.py). Never comments, labels or closes.

Writes into the digest directory:
  digest-<UTC>.md / .json   what is new since the previous run
  latest.md / latest.json   copies of the newest digest
  watermark.json            the time this run started (next run's lower bound)

Run: python -I -B tools/issue-watch/issue_digest.py [--dir E:\\AIArchive\\issue-digest]
     [--repo owner/name] [--since 2026-11-05T00:00:00Z]
"""
from __future__ import annotations

import argparse
import base64
import datetime as dt
import json
import os
import re
import secrets
import subprocess
import sys
import urllib.error
import urllib.request
import uuid

REPO = "wushuai0802/myfinder-device-battery"
MAINTAINER = "wushuai0802"
BOT = "github-actions[bot]"
DAILY_CAP = 200
FEEDBACK_PROBE_URL = "https://myfinder-feedback-relay.wushuai591459380.workers.dev/feedback/supplement"
FEEDBACK_PROBE_ISSUE = 999999
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
NOISE = re.compile(r"HIDI2C|VHF|LED|Touch|Controller|ITE Device|Virtual|OrayVHID|Tecent", re.I)
ID_LINE = re.compile(r'^\s*([0-9A-F]{4}):([0-9A-F]{4}) "([^"]*)"(.*)$')
# Priority filter: only these need the maintainer; everything else is covered
# by the automatic replies. Keep the patterns cheap and conservative.
PROTOCOL = re.compile(r"抓包|描述符|descriptor|Report ?ID|协议|报文|帧|官方(?:软件|驱动).{0,40}(?:https?://|版本)|下载地址|\.exe|"
                      r"(?:[0-9A-Fa-f]{2} ){4,}[0-9A-Fa-f]{2}", re.I)
BUG = re.compile(r"崩溃|闪退|报错|卡死|无响应|打不开|启动不了|更新失败|安装失败|bug|异常|错误", re.I)
PAST_WORKING = re.compile(
    r"(?:之前|以前|此前|原来|曾经|[旧老]版本?|上(?:上)?(?:一)?个版本)"
    r"[^不没无未，,。；;！？!?\n]{0,16}?"
    r"(?:(?:能|能够|可以)(?:正常)?(?:显示|识别|读[取到出])|正常(?:显示|识别|读取)|是可以的)"
)
CURRENT_LOSS = re.compile(
    r"(?:(?:更新|升级)(?:之?后|以后)|这次更新|新版(?:本)?|"
    r"最新(?:的这?[一两二三\d]+个)?版本?|这[两二]个版本|现在|目前|突然)"
    r"[，,]?[^，,。；;！？!?\n]{0,30}?"
    r"(?:(?:无法|不能|不再?|没有)(?:正常)?(?:显示|识别|读取)|"
    r"(?:识别|读|看|检测)不到|(?:识别|显示)不了|没有电量|没电量|失效)"
)
SPECULATIVE = re.compile(r"如果|假如|假设|要是|万一|请保证|请确保|千万不要|不要不|请别不|会不会")

HOT_THRESHOLD = 3
# Laptop/motherboard internals (Intel Bluetooth, Lenovo, ASUS keyboard/audio).
BUILTIN_VENDORS = {"8087", "17EF", "048D", "1462"}
BUILTIN_NAME = re.compile(r"USB Audio|N-KEY|Idea\d|Bluetooth|Realtek|Webcam|Camera", re.I)
# Accessories that enumerate with a vendor collection but have no battery of
# their own (MCHOSE MagDock is a charging dock; the mouse reports through its
# own receiver, see engine/research/batch2-protocols.md section 4).
NO_BATTERY_IDS = {"3837:1012", "187C:0551"}  # MagDock charging dock; Alienware AW-ELC light controller


class NoProbeRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def probe_feedback_relay(opener=None):
    """Test the relay's GitHub read path with a missing issue and an ephemeral key."""
    opener = opener or urllib.request.build_opener(NoProbeRedirect())
    payload = {
        "schema": 1,
        "number": FEEDBACK_PROBE_ISSUE,
        "replyKey": base64.urlsafe_b64encode(secrets.token_bytes(32)).rstrip(b"=").decode("ascii"),
        "requestId": str(uuid.uuid4()),
        "description": "relay token probe",
        "diagnostics": None,
        "appVersion": "1.3.11",
    }
    request = urllib.request.Request(
        FEEDBACK_PROBE_URL, data=json.dumps(payload).encode("utf-8"),
        headers={
            "Content-Type": "application/json",
            "User-Agent": "MyFinderDeviceBattery-RelayHealth/1",
        },
        method="POST",
    )
    status = None
    try:
        try:
            response = opener.open(request, timeout=10)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            status = response.getcode()
            data = json.loads(response.read(1025))
        allowed_errors = ("denied", "disabled", "unavailable", "rate_limited", "invalid")
        reason = data.get("error") if isinstance(data, dict) else None
        error_code = reason if reason in allowed_errors else "unexpected_response"
    except (OSError, ValueError, UnicodeError):
        error_code = "network_or_invalid_response"
    return {"ok": status == 403 and error_code == "denied", "status": status, "error": error_code}


def feedback_relay_summary(result):
    if result["ok"]:
        return "反馈中转可读取 GitHub（403 denied）。创建权限需用测试反馈回执验证。"
    return f"[告警] 反馈中转异常（HTTP {result['status'] or '无响应'}，{result['error']}）"


def gh(path, paginate=False):
    args = ["gh", "api", path] + (["--paginate"] if paginate else [])
    out = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", check=True).stdout
    if paginate:
        # --paginate concatenates JSON arrays: "[...][...]"
        out = "[" + out.strip().replace("]\n[", ",").replace("][", ",").strip("[]") + "]" if out.strip() else "[]"
    return json.loads(out)


def known_ids():
    py = os.path.join(ROOT, "engine", "runtime", "python.exe")
    py = py if os.path.exists(py) else sys.executable
    out = subprocess.run([py, "-I", "-B", os.path.join(ROOT, "engine", "known_ids.py"), "--json"],
                         capture_output=True, text=True, encoding="utf-8", check=True).stdout
    index = {}
    for device in json.loads(out).get("devices", []):
        key = (device["vendorId"], device["productId"])
        # Preserve a gate if multiple providers/sources share the same USB ID.
        gated = device.get("requiresModelMatch", False) or index.get(key, {}).get("requiresModelMatch", False)
        index[key] = {"name": device.get("name", ""), "requiresModelMatch": bool(gated)}
    return index


def annotate_unknown(unknown, known):
    for device in unknown:
        key = tuple(int(value, 16) for value in device["id"].split(":"))
        match = known.get(key, {})
        device["knownInMain"] = match.get("name")
        device["requiresModelMatch"] = match.get("requiresModelMatch", False)


def support_tag(device):
    if not device["knownInMain"]:
        return "未支持"
    label = "仅部分型号" if device["requiresModelMatch"] else "已支持"
    return f"{label}（{device['knownInMain']}）"


def parse_body(body):
    body = body or ""
    version = re.search(r"版本 ([\w.\-]+)", body)
    desc = re.search(r"\*\*描述\*\*\s*```\s*(.*?)```", body, re.S)
    cards, unknown = [], []
    if "有卡片的设备" in body:
        block = body.split("有卡片的设备")[-1].split("已隐藏")[0]
        cards = [line.strip()[:100] for line in block.splitlines() if "|" in line]
    if "未取得电量的设备" in body:
        for line in body.split("未取得电量的设备")[-1].splitlines():
            m = ID_LINE.match(line)
            if m and not NOISE.search(m.group(3)) and f"{m.group(1)}:{m.group(2)}" not in NO_BATTERY_IDS:
                usages = sorted(set(re.findall(r"FF[0-9A-F]{2}:[0-9A-F]{4}", m.group(4))))
                unknown.append({"id": f"{m.group(1)}:{m.group(2)}", "name": m.group(3), "vendor": usages})
    return {"version": version.group(1) if version else None,
            "description": (desc.group(1).strip() if desc else body[:300]).replace("\r", "")[:400],
            "hasReplyKey": "myfinder-reply:v1" in body, "inApp": "来自程序内反馈" in body,
            "cards": cards, "unknown": unknown}


def is_device_regression(text):
    # Drop quoted feedback. Match one short sentence or two adjacent ones.
    unquoted = "\n".join(line for line in text.splitlines()
                         if not line.lstrip().startswith((">", "&gt;")))
    sentences = [part.strip() for part in re.split(r"[。！？!?\n]+", unquoted) if part.strip()]
    pairs = [left + "。" + right for left, right in zip(sentences, sentences[1:])]
    return any(
        len(window) <= 120 and not SPECULATIVE.search(window)
        and PAST_WORKING.search(window) and CURRENT_LOSS.search(window)
        for window in sentences + pairs
    )


def priority_items(new_issues, user_comments, supplements, counts_path):
    """Device regressions, protocol contributions, bugs and frequently reported unknown IDs."""
    items, seen = [], set()

    def add(kind, number, why):
        if (kind, number) not in seen:
            seen.add((kind, number))
            items.append({"kind": kind, "number": number, "why": why[:160].replace("\n", " / ")})

    texts = [(n["number"], n["title"] + "\n" + n["description"], "issue") for n in new_issues]
    texts += [(c["number"], c["text"], "comment") for c in user_comments]
    texts += [(s["number"], s["text"], "supplement") for s in supplements]
    for number, text, _ in texts:
        if is_device_regression(text):
            add("设备回归", number, text)
    for number, text, source in texts:
        if source == "issue":
            text = text.replace("\n", " ", 1)
        if PROTOCOL.search(text):
            add("协议/驱动线索", number, text)
        elif BUG.search(text):
            add("程序问题", number, text)
    for n in new_issues:
        if any(t in n["title"] for t in ("界面或操作", "其他：")) and not n["unknown"]:
            add("程序问题", n["number"], n["title"])
    try:
        counts = json.load(open(counts_path, encoding="utf-8"))
    except (OSError, ValueError):
        counts = {}
    for n in new_issues:
        for u in n["unknown"]:
            # Without a vendor-defined collection (FFxx) there is no private
            # battery channel to study; built-in audio/Bluetooth/laptop IDs drop out.
            if (u["knownInMain"] or not u["vendor"] or u["id"][:4] in BUILTIN_VENDORS
                    or BUILTIN_NAME.search(u["name"])):
                continue
            entry = counts.setdefault(u["id"], {"issues": [], "flagged": False, "name": u["name"]})
            if n["number"] not in entry["issues"]:
                entry["issues"].append(n["number"])
            if len(entry["issues"]) >= HOT_THRESHOLD and not entry["flagged"]:
                entry["flagged"] = True
                add("高频未支持 ID", n["number"], f"{u['id']} \"{u['name']}\" 已在 {len(entry['issues'])} 条 issue 出现："
                    + "、".join(f"#{x}" for x in entry["issues"][-8:]))
    json.dump(counts, open(counts_path, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    return items


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", default=r"E:\AIArchive\issue-digest")
    ap.add_argument("--repo", default=REPO)
    ap.add_argument("--since")
    probe_mode = ap.add_mutually_exclusive_group()
    probe_mode.add_argument("--check-feedback-relay", action="store_true")
    probe_mode.add_argument("--feedback-relay-only", action="store_true")
    a = ap.parse_args()
    relay_probe = None
    if a.check_feedback_relay or a.feedback_relay_only:
        if a.repo != REPO:
            ap.error("反馈中转检查只适用于 MyFinder Device Battery 主仓库")
        relay_probe = probe_feedback_relay()
        if a.feedback_relay_only:
            print(feedback_relay_summary(relay_probe))
            return 0 if relay_probe["ok"] else 1
    os.makedirs(a.dir, exist_ok=True)
    mark = os.path.join(a.dir, "watermark.json")
    now = dt.datetime.now(dt.timezone.utc).replace(microsecond=0)
    since = a.since
    if not since and os.path.exists(mark):
        since = json.load(open(mark, encoding="utf-8")).get("since")
    since = since or (now - dt.timedelta(hours=24)).isoformat().replace("+00:00", "Z")
    repo = a.repo
    known = known_ids()

    issues = gh(f"repos/{repo}/issues?state=all&since={since}&per_page=100", paginate=True)
    comments = gh(f"repos/{repo}/issues/comments?since={since}&per_page=100", paginate=True)
    created = [i for i in issues if not i.get("pull_request") and i["created_at"] >= since]

    new_issues = []
    for i in sorted(created, key=lambda x: x["number"]):
        info = parse_body(i.get("body"))
        annotate_unknown(info["unknown"], known)
        new_issues.append({"number": i["number"], "title": i["title"], "state": i["state"],
                           "labels": [l["name"] for l in i.get("labels", [])], "url": i["html_url"], **info})

    user_comments, supplements = [], []
    for c in comments:
        num = int(c["issue_url"].rsplit("/", 1)[1])
        login = c["user"]["login"]
        text = re.sub(r"<!--.*?-->", "", c.get("body") or "", flags=re.S).strip()
        if "myfinder-supplement:v1" in (c.get("body") or ""):
            supplements.append({"number": num, "at": c["created_at"], "text": text[:600], "url": c["html_url"]})
        elif login not in (MAINTAINER, BOT):
            user_comments.append({"number": num, "user": login, "at": c["created_at"], "text": text[:600],
                                  "url": c["html_url"]})

    day_ago = (now - dt.timedelta(hours=24)).isoformat().replace("+00:00", "Z")
    q = f"repo:{repo} is:issue label:in-app-feedback created:>={day_ago}"
    count = subprocess.run(["gh", "api", "-X", "GET", "search/issues", "-f", f"q={q}", "--jq", ".total_count"],
                           capture_output=True, text=True, encoding="utf-8").stdout.strip()

    digest = {"generatedAt": now.isoformat().replace("+00:00", "Z"), "since": since, "repo": repo,
              "inAppLast24h": int(count) if count.isdigit() else None, "dailyCap": DAILY_CAP,
              "newIssues": new_issues, "userComments": user_comments, "supplements": supplements}

    priority = priority_items(new_issues, user_comments, supplements, os.path.join(a.dir, "id-counts.json"))
    digest["priority"] = priority
    lines = [f"# Issue 汇总 {digest['generatedAt']}", "", f"范围：{since} 起。程序内反馈 24 小时 {count}/{DAILY_CAP}。", ""]
    if relay_probe is not None:
        digest["feedbackRelay"] = relay_probe
        lines.extend([feedback_relay_summary(relay_probe), ""])
    lines.append(f"## 需要人工处理（{len(priority)}）")
    lines += [f"- [{p['kind']}] #{p['number']}: {p['why']}" for p in priority] or ["- 无。其余由自动回复覆盖，可不逐条处理。"]
    lines.append("")
    lines.append(f"## 新 issue（{len(new_issues)}）")
    for n in new_issues:
        flags = ("程序内" if n["inApp"] else "网页") + (" 有凭证" if n["hasReplyKey"] else "") + f" v{n['version'] or '?'}"
        lines.append(f"- #{n['number']} [{flags}] {n['title']}  ")
        lines.append(f"  描述：{n['description'][:200].replace(chr(10), ' / ')}")
        for c in n["cards"]:
            lines.append(f"  - 卡片：{c}")
        for u in n["unknown"][:8]:
            tag = support_tag(u)
            lines.append(f"  - {u['id']} \"{u['name']}\" {' '.join(u['vendor'])} — {tag}")
    lines += ["", f"## 用户评论（{len(user_comments)}）"]
    lines += [f"- #{c['number']} @{c['user']}: {c['text'][:300].replace(chr(10), ' / ')}" for c in user_comments]
    lines += ["", f"## 程序内补充资料（{len(supplements)}）"]
    lines += [f"- #{s['number']}: {s['text'][:300].replace(chr(10), ' / ')}" for s in supplements]
    md = "\n".join(lines) + "\n"

    stamp = now.strftime("%Y%m%dT%H%M%SZ")
    for name, content in ((f"digest-{stamp}.md", md), ("latest.md", md)):
        open(os.path.join(a.dir, name), "w", encoding="utf-8").write(content)
    for name in (f"digest-{stamp}.json", "latest.json"):
        json.dump(digest, open(os.path.join(a.dir, name), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    json.dump({"since": digest["generatedAt"]}, open(mark, "w", encoding="utf-8"))
    print(f"digest {stamp}: {len(new_issues)} issues, {len(user_comments)} comments, "
          f"{len(supplements)} supplements, priority {len(priority)} -> {a.dir}")


if __name__ == "__main__":
    sys.exit(main())
