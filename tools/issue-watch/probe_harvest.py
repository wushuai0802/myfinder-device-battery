"""Find confirmed experimental-family readings in a JSON export of issues."""

import argparse
import json
from pathlib import Path
import re
import sys


READING = re.compile(
    r"自动识别（实验）[ \t]*\|[ \t]*([0-9a-fA-F]{4}:[0-9a-fA-F]{4})[ \t]*\|"
    r"[ \t]*([^|\r\n]{1,126})[ \t]*\|[ \t]*([a-z0-9_]{1,64})[ \t]*\|"
    r"[ \t]*([a-zA-Z0-9_.-]{1,64})[ \t]*\|[ \t]*([^|\r\n]+)"
)
ANSWER = re.compile(r"MyFinder 显示的电量和官方软件一致吗[？?][ \t]*[：:]?[ \t]*(.+)")
FENCE = re.compile(r"(`{3,})(?:text)?")


def issue_rows(document):
    """Support issue arrays and paginated arrays from gh api --slurp."""
    if not isinstance(document, list):
        raise ValueError("Expected a JSON array of GitHub issues.")
    for page in document:
        if isinstance(page, list):
            yield from page
        else:
            yield page


def fenced_section(lines, heading, closing=None):
    starts = [index for index, line in enumerate(lines) if line == heading]
    if len(starts) != 1:
        return None
    start = starts[0] + 1
    if start >= len(lines):
        return None
    fence = FENCE.fullmatch(lines[start])
    if not fence:
        return None
    for end in range(start + 1, len(lines)):
        if lines[end] == fence[1]:
            if closing and (end + 1 >= len(lines) or lines[end + 1] != closing):
                return None
            return lines[start + 1 : end]
    return None


def feedback_sections(body):
    lines = [line.strip() for line in body.splitlines() if line.strip()]
    if "**描述**" in lines:
        diagnostic_heading = "<details><summary>诊断信息</summary>"
        if lines.count(diagnostic_heading) != 1:
            return None, None
        diagnostic_start = lines.index(diagnostic_heading)
        return (
            fenced_section(lines[:diagnostic_start], "**描述**"),
            fenced_section(lines[diagnostic_start:], diagnostic_heading, "</details>"),
        )

    # BrowserSubmission puts its description and diagnostic report in the
    # single "反馈报告" textarea of the GitHub device issue form.
    report = fenced_section(lines, "### 反馈报告")
    if not report or len(report) < 5 or not report[0].startswith("类型：") or report[1] != "描述：":
        return None, None
    boundaries = [
        index for index in range(3, len(report) - 1)
        if report[index].startswith("MyFinder Device Battery ") and report[index + 1].startswith("Windows ")
    ]
    if len(boundaries) != 1:
        return None, None
    boundary = boundaries[0]
    return report[2:boundary], report[boundary:]


def evidence(body):
    description, diagnostics = feedback_sections(body)
    if not description or not diagnostics:
        return None
    answer = ANSWER.fullmatch(description[-1])
    if not answer or answer[1].strip() != "一致":
        return None
    observations = set()
    for line in diagnostics:
        match = READING.fullmatch(line)
        if match:
            identity, product, family, read, outcome = match.groups()
            product = product.strip()
            if product and product.isprintable() and product.casefold() != "[redacted]":
                observations.add((identity.upper(), product, family, read, outcome.strip()))
    if len(observations) != 1:
        return None
    identity, product, family, _, outcome = observations.pop()
    if outcome == "读数":
        return identity, product, family
    return None


def harvest(document):
    observations = {}
    for issue in issue_rows(document):
        if not isinstance(issue, dict) or "pull_request" in issue:
            continue
        number = issue.get("number")
        body = issue.get("body")
        if type(number) is not int or number <= 0 or not isinstance(body, str):
            continue
        observations.setdefault(number, set()).add(evidence(body))
    candidates = []
    for number in sorted(observations):
        results = observations[number]
        if len(results) == 1 and None not in results:
            identity, product, family = next(iter(results))
            candidates.append({"issue": number, "vidPid": identity, "productString": product, "family": family})
    return candidates


def main(argv=None):
    parser = argparse.ArgumentParser(
        description=__doc__,
        epilog=(
            "Each qualifying issue contains its own official-tool confirmation and one "
            "diagnostic row in this format: 自动识别（实验） | VID:PID | USB product string "
            "| family ID | read name | 读数"
        ),
    )
    parser.add_argument("issues", type=Path, help="saved GitHub issues JSON with number and body fields")
    args = parser.parse_args(argv)
    try:
        candidates = harvest(json.loads(args.issues.read_text(encoding="utf-8-sig")))
    except (OSError, ValueError) as error:
        parser.error(str(error))
    print(json.dumps(candidates, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
