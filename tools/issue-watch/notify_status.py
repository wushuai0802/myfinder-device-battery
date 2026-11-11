"""Preview beta status notices; only --post authorizes GitHub comments."""
from __future__ import annotations

import argparse
import importlib.util
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile


ROOT = Path(__file__).resolve().parents[2]


def _load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise ImportError(f"Cannot load {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


_triage = _load("_notify_triage", ROOT / ".github/scripts/triage.py")
_status = _load("_notify_device_status", Path(__file__).with_name("device_status.py"))
_digest = _load("_notify_issue_digest", Path(__file__).with_name("issue_digest.py"))
report_devices = _triage.report_devices
REPO = _digest.REPO


def normalize_id(value):
    device_id = value.upper()
    if not _status.DEVICE_ID.fullmatch(device_id):
        raise ValueError("id: expected VID:PID")
    return device_id


def make_body(device_id, status, repo=REPO):
    """Build only repository-owned text and strictly validated status fields."""
    device_id = normalize_id(device_id)
    _status.validate(status)
    entry = status["devices"].get(device_id)
    if entry is None or entry["status"] != "beta":
        raise ValueError(f"{device_id}.status: ID must be present and beta")
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repo):
        raise ValueError("repo: expected owner/name")
    version, note = entry["version"], entry.get("note", "")
    extra = f"（{note}）" if note else ""
    return (f"<!-- myfinder-status:v1 {device_id} {version} -->\n"
            f"自动通知：测试版 {version} 已加入 {device_id} 的电量读取，待实机验证{extra}。"
            "可在程序设置里打开「提醒测试版更新」后升级，或到发布页下载："
            f"https://github.com/{repo}/releases/tag/v{version} 。"
            "读到后请对照官方软件的电量；读不到请在程序里用「我的反馈 → 补充资料」再提交一次诊断。")


def select_issues(issues, comments_by_issue, device_id, version, limit=30):
    """Return unique issue numbers to notify, and fixed reasons for every skip."""
    device_id = normalize_id(device_id)
    if type(limit) is not int or limit < 0:
        raise ValueError("limit: expected an integer >= 0")
    marker = f"<!-- myfinder-status:v1 {device_id} {version} -->"
    release = re.compile(re.escape(f"/releases/tag/v{version}") + r"(?![A-Za-z0-9.+-])")
    selected, skipped, seen = [], [], set()
    for issue in issues:
        number = issue["number"]
        if number in seen:
            continue
        seen.add(number)
        comments = _triage.comments_list(comments_by_issue.get(number, []))
        reason = None
        if "pull_request" in issue:
            reason = "Pull Request"
        elif issue.get("state", "open") != "open":
            reason = "非 open issue"
        elif "triage:locked" in _triage.labels_of(issue):
            reason = "triage:locked"
        elif any(marker in (comment.get("body") or "") for comment in comments):
            reason = "已有本版本通知标记"
        elif any(release.search(comment.get("body") or "") for comment in comments):
            reason = "已有本版本发布地址"
        else:
            texts = [issue.get("body") or ""]
            for comment in comments:
                user = comment.get("user") or {}
                if user.get("type") != "Bot" and not user.get("login", "").endswith("[bot]"):
                    texts.append(comment.get("body") or "")
            matched = any(row[3] == "unadapted" and f"{row[0]:04X}:{row[1]:04X}" == device_id
                          for text in texts for row in report_devices(text))
            if not matched:
                reason = "未在未取得电量设备行命中 ID"
            elif len(selected) >= limit:
                reason = f"超过 --limit {limit}"
        if reason:
            skipped.append({"number": number, "reason": reason})
        else:
            selected.append(number)
    return {"notify": selected, "skipped": skipped}


def gh_get(path):
    """The sole read transport, explicitly GET even when adding CLI flags."""
    result = subprocess.run(["gh", "api", "--method", "GET", "--paginate", "--slurp", path],
                            capture_output=True, text=True, encoding="utf-8", check=True)
    pages = json.loads(result.stdout)
    if not isinstance(pages, list):
        raise ValueError("gh api: expected list pages")
    return _triage.comments_list(pages)


def post_comment(repo, number, body):
    """The sole write transport; callers must explicitly opt in with --post."""
    with tempfile.TemporaryDirectory(prefix="myfinder-status-") as folder:
        path = Path(folder) / "comment.txt"
        path.write_text(body + "\n", encoding="utf-8", newline="\n")
        subprocess.run(["gh", "issue", "comment", str(number), "-R", repo, "--body-file", str(path)],
                       capture_output=True, text=True, encoding="utf-8", check=True)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--id", required=True)
    parser.add_argument("--status", type=Path, default=_status.DEFAULT_STATUS)
    parser.add_argument("--repo", default=REPO)
    parser.add_argument("--limit", type=int, default=30)
    parser.add_argument("--post", action="store_true")
    args = parser.parse_args(argv)
    try:
        device_id = normalize_id(args.id)
        if args.limit < 0:
            raise ValueError("limit: expected an integer >= 0")
        status = _status.load(args.status)
        body = make_body(device_id, status, args.repo)
        version = status["devices"][device_id]["version"]
        issues = gh_get(f"repos/{args.repo}/issues?state=open&per_page=100")
        comments = {}
        for issue in issues:
            number = issue["number"]
            if (number not in comments and "pull_request" not in issue
                    and issue.get("state", "open") == "open"
                    and "triage:locked" not in _triage.labels_of(issue)):
                comments[number] = gh_get(f"repos/{args.repo}/issues/{number}/comments?per_page=100")
        plan = select_issues(issues, comments, device_id, version, args.limit)
        print(body)
        for number in plan["notify"]:
            print(f"{'发送' if args.post else '预览'}：将通知 #{number}")
        for item in plan["skipped"]:
            print(f"跳过 #{item['number']}：{item['reason']}")
        if args.post:
            for number in plan["notify"]:
                try:
                    post_comment(args.repo, number, body)
                except (OSError, subprocess.SubprocessError) as error:
                    print(f"通知 #{number} 失败，已停止：{error}", file=sys.stderr)
                    return 1
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        print(error, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
