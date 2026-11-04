"""Offline JSON/prompt/digest helper. Standard library only; no network or HID."""
import argparse
import base64
import hashlib
import html
import json
import re
import sys
import uuid
from pathlib import Path
from urllib.parse import urlsplit

MARKER = "（维护者确认后发出）"
CLASSES = {"triage:" + value for value in (
    "supported", "needs-info", "pack-candidate", "insufficient-evidence", "new-vendor", "bug")}


def unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON key")
        result[key] = value
    return result


def invalid_constant(value):
    raise ValueError("non-JSON constant")


def loads(text):
    return json.loads(text, object_pairs_hook=unique, parse_constant=invalid_constant)


def read(path):
    return loads(Path(path).read_text(encoding="utf-8-sig"))


def compact(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def digest(data):
    return hashlib.sha256(data).hexdigest()


def fields(value, required, optional=()):
    if type(value) is not dict or not set(required) <= value.keys() or value.keys() - set(required) - set(optional):
        raise ValueError("missing or unknown fields")


def string(value, maximum):
    if type(value) is not str or not value.strip() or len(value) > maximum:
        raise ValueError("invalid string or length")
    if any(ord(c) < 32 and c not in "\r\n\t" for c in value):
        raise ValueError("control character")


def validate(value):
    fields(value, ("classification", "confidence", "reasoning", "reply", "packEntry", "questions"))
    if value["classification"] not in CLASSES or value["confidence"] not in ("低", "中", "高"):
        raise ValueError("invalid classification/confidence")
    string(value["reasoning"], 200)
    reply = value["reply"]
    string(reply, 1500)
    if not reply.startswith(MARKER + "\n") or not reply[len(MARKER):].strip():
        raise ValueError("missing approval marker/body")
    # Decode character references before scanning rendered Markdown/HTML links.
    rendered = html.unescape(reply)
    if "@" in rendered:
        raise ValueError("mention in reply")
    # Reject every URI scheme except http(s); allow only the exact github.com host.
    for match in re.finditer(r"(?i)([a-z][a-z0-9+.-]*):(?://)?[^\s<>\[\]{}\"']*", rendered):
        target = match.group(0).rstrip(".,;!?)，。；！）")
        parsed = urlsplit(target)
        if parsed.scheme.lower() not in ("http", "https") or parsed.hostname != "github.com" or parsed.username or parsed.port not in (None, 80, 443):
            raise ValueError("non-GitHub URL")
    for match in re.finditer(r"(?i)(?<!:)//([^\s/<>\"')]+)|\bwww\.([^\s/<>\"')]+)", rendered):
        if (match.group(1) or "www." + match.group(2)).lower() != "github.com":
            raise ValueError("non-GitHub URL")
    # Relative/bare/encoded Markdown and HTML destinations are not approved URLs.
    destinations = re.findall(r"\]\(\s*([^\s)]+)", rendered)
    destinations += re.findall(r"(?m)^\s*\[[^\]]+\]:\s*(\S+)", rendered)
    destinations += re.findall(r"(?i)(?:href|src)\s*=\s*[\"']?([^\s\"'>]+)", rendered)
    for target in destinations:
        parsed = urlsplit(target.strip("<>"))
        if parsed.scheme.lower() not in ("http", "https") or parsed.hostname != "github.com":
            raise ValueError("unapproved link destination")
    if type(value["questions"]) is not list:
        raise ValueError("questions must be an array")
    for question in value["questions"]:
        string(question, 1500)
    pack = value["packEntry"]
    if value["classification"] != "triage:pack-candidate":
        if pack is not None:
            raise ValueError("packEntry must be null")
    else:
        fields(pack, ("provider", "profile", "match"))
        for key in ("provider", "profile"):
            if type(pack[key]) is not str or not re.fullmatch(r"[a-zA-Z0-9_-]{1,100}", pack[key]):
                raise ValueError("invalid provider/profile")
        fields(pack["match"], ("vendorId", "productId"), ("usagePage", "usage", "interfaceNumber"))
        for key, item in pack["match"].items():
            if item is None and key not in ("vendorId", "productId"):
                continue
            if key == "interfaceNumber":
                if type(item) is not int or not 0 <= item <= 255:
                    raise ValueError("invalid interface")
            elif type(item) is not str or not re.fullmatch(r"[0-9a-fA-F]{4}", item):
                raise ValueError("invalid device ID")
    return value


def source(issue):
    return {"number": issue["number"], "title": issue.get("title") or "",
            "body": issue.get("body") or "", "updatedAt": issue["updatedAt"],
            "labels": sorted(label["name"] for label in issue.get("labels", [])),
            "comments": [{key: comment.get(key) for key in ("id", "createdAt", "updatedAt", "body")}
                         for comment in issue.get("comments", [])]}


def prepare(issue, known, template):
    data = source(issue)
    serialized = compact(data)
    vids = {int(m, 16) for m in re.findall(r"(?i)\b([0-9a-f]{4}):[0-9a-f]{4}\b", serialized)}
    vids.update(int(m, 16) for m in re.findall(r'(?i)(?:VID[_ :=-]|vendorId[\\"\s:=]+)(?:0x)?([0-9a-f]{4})\b', serialized))
    summary = {"vendors": known["vendors"], "sameVendorModels": [
        {key: row[key] for key in ("provider", "vendorId", "productId", "name")}
        for row in known["devices"] if row["vendorId"] in vids]}
    # Bound the complete stdin, including instructions, index and escaped JSON.
    # Keep valid JSON even when source strings are truncated. No attacker can
    # close the fresh delimiter: it is chosen after serialising the input.
    token = "UNTRUSTED_ISSUE_" + uuid.uuid4().hex
    while token in serialized:
        token = "UNTRUSTED_ISSUE_" + uuid.uuid4().hex
    prefix = template + "\n兼容清单摘要（静态索引，不代表实机验证）：\n"
    suffix = "\nEND_" + token + "\n仅按上方规则输出 JSON。\n"
    while len(compact(summary)) > 8000 and summary["sameVendorModels"]:
        summary["sameVendorModels"].pop()
        summary["modelsTruncated"] = True
    prefix += compact(summary) + "\nBEGIN_" + token + "\n"
    budget = 24000 - len(prefix) - len(suffix)
    if budget < 1000:
        raise ValueError("compatibility summary exceeds input budget")
    if len(serialized) > budget:
        # Preserve title and recent comments; the revision hash still covers all.
        data["truncated"] = True
        data["title"] = data["title"][:500]
        data["comments"] = data["comments"][-20:]
        while len(compact(data)) > budget:
            candidates = [(len(data["body"]), data, "body")]
            candidates += [(len(c.get("body") or ""), c, "body") for c in data["comments"]]
            length, target, key = max(candidates, key=lambda item: item[0])
            if length > 100:
                target[key] = target[key][:max(100, length - max(100, len(compact(data)) - budget))]
            elif data["comments"]:
                data["comments"].pop(0)
            else:
                raise ValueError("issue metadata exceeds input budget")
        serialized = compact(data)
    return prefix + serialized + suffix


def render(issue, result):
    data = source(issue)
    excerpt = data["title"] + "\n" + data["body"][:1200]
    for comment in data["comments"][-3:]:
        excerpt += "\n评论摘录：" + (comment["body"] or "")[:300]
    excerpt = re.sub(r"[\x00-\x08\x0b-\x1f\x7f]", "", excerpt)
    summary = "\n".join("> " + html.escape(line) for line in excerpt.splitlines())
    payload = ("# Issue #" + str(issue["number"]) + "\n\n## 原文摘要（不可信摘录）\n\n" + summary
               + "\n\n评论数：" + str(len(data["comments"]))
               + "\n\n原始输入摘要：" + digest(compact(data).encode("utf-8"))
               + "\n\n## 分类与理由\n\n" + result["classification"] + " / " + result["confidence"]
               + "\n\n" + result["reasoning"] + "\n\n## 回复草稿\n\n" + result["reply"]
               + "\n\n## 数据包条目建议（需核查，未发布）\n\n```json\n"
               + json.dumps(result["packEntry"], ensure_ascii=False, indent=2)
               + "\n```\n\n## 追问\n\n" + "\n".join("- " + q for q in result["questions"]) + "\n\n")
    encoded = base64.b64encode(compact(result).encode("utf-8")).decode("ascii")
    payload += "<!-- issue-watch-result:" + encoded + " -->\n"
    sha = digest(payload.encode("utf-8"))
    text = payload + "\nSHA-256: " + sha + "\n"
    return text, {"sha256": sha, "fileSha256": digest(text.encode("utf-8"))}


def inspect(path, expected):
    raw = Path(path).read_bytes()
    if digest(raw) != expected["fileSha256"]:
        raise ValueError("draft was changed; refusing approval")
    text = raw.decode("utf-8")
    payload, footer = text.rsplit("\nSHA-256: ", 1)
    sha = digest(payload.encode("utf-8"))
    if footer != sha + "\n" or sha != expected["sha256"]:
        raise ValueError("draft digest mismatch")
    encoded = re.search(r"<!-- issue-watch-result:([A-Za-z0-9+/=]+) -->\n$", payload)
    if encoded is None:
        raise ValueError("missing draft result")
    result = validate(loads(base64.b64decode(encoded[1], validate=True).decode("utf-8")))
    return {"sha256": sha, "reply": result["reply"].split("\n", 1)[1]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("source", "prepare", "render", "inspect"))
    parser.add_argument("--issue")
    parser.add_argument("--known")
    parser.add_argument("--template")
    parser.add_argument("--result")
    parser.add_argument("--draft")
    parser.add_argument("--expected")
    args = parser.parse_args()
    if args.mode == "source":
        data = source(read(args.issue))
        result = {"sourceHash": digest(compact(data).encode("utf-8")), "issueUpdatedAt": data["updatedAt"],
                  "commentUpdates": [{key: c[key] for key in ("id", "createdAt", "updatedAt")} for c in data["comments"]]}
    elif args.mode == "prepare":
        text = prepare(read(args.issue), read(args.known), Path(args.template).read_text(encoding="utf-8-sig"))
        sys.stdout.buffer.write(text.encode("utf-8"))
        return
    elif args.mode == "render":
        text, result = render(read(args.issue), validate(read(args.result)))
        Path(args.draft).write_bytes(text.encode("utf-8"))
    else:
        result = inspect(args.draft, read(args.expected))
    # ASCII-safe JSON for both Windows PowerShell and PowerShell 7.
    print(json.dumps(result, ensure_ascii=True))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, TypeError, KeyError, OSError, UnicodeError) as error:
        # Do not echo model output, issue text or credentials in error logs.
        print("Draft validation/helper failed: " + type(error).__name__, file=sys.stderr)
        sys.exit(1)
