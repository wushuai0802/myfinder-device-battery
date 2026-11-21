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


def issue_rows(document):
    """Support issue arrays and paginated arrays from gh api --slurp."""
    if not isinstance(document, list):
        raise ValueError("Expected a JSON array of GitHub issues.")
    for page in document:
        if isinstance(page, list):
            yield from page
        else:
            yield page


def evidence(body):
    answers = set()
    observations = set()
    fenced = False
    for raw_line in body.splitlines():
        line = raw_line.strip()
        if line.startswith("```"):
            fenced = not fenced
            continue
        answer = ANSWER.fullmatch(line) if not fenced else None
        if answer:
            answers.add(answer[1].strip())
        match = READING.fullmatch(line)
        if match:
            identity, product, family, read, outcome = match.groups()
            product = product.strip()
            if product and product.isprintable() and product.casefold() != "[redacted]":
                observations.add((identity.upper(), product, family, read, outcome.strip()))
    if answers != {"一致"} or len(observations) != 1:
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
