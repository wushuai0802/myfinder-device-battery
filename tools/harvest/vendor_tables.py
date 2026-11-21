"""Collect explicit model declarations from public vendor bundles as text."""

from __future__ import annotations

import argparse
from collections import deque
from decimal import Decimal
from hashlib import sha256
from html.parser import HTMLParser
import json
from pathlib import Path
import re
from urllib.error import URLError
from urllib.parse import urljoin, urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener


ENTRYPOINTS = {
    "mchose": "https://www.mchose.com.cn/",
    "vgn": "https://drive.vgnlab.com.cn/",
    "aula": "https://hub.aulacn.com/",
    "epomaker": "https://hub.epomaker.com/",
    "zhuaudio": "https://drive.zhuaudio.cn/web/",
}

MAIN_SCRIPT = {
    "mchose": re.compile(r"^(?:main|index)-[\w-]+\.js$"),
    "vgn": re.compile(r"^index-[\w-]+\.js$"),
    "aula": re.compile(r"^(?:main|index|app)(?:[-.][\w-]+)?\.js$"),
    "epomaker": re.compile(r"^index-[\w-]+\.js$"),
    "zhuaudio": re.compile(r"^flutter_bootstrap\.js$"),
}
LINKED_SCRIPTS = {
    "mchose": re.compile(r"""["'](\./main-[\w-]+\.js)["']"""),
    "zhuaudio": re.compile(r"""["'](main\.dart\.js)["']"""),
}
MAX_BYTES = 12_000_000
MAX_SCRIPTS = 6
OBJECT_START = re.compile(r"""(?:^|[=(:,\[])\s*(\{)(?=\s*["']?(?:vendorId|vid|VID|modelKey|productId|pid|PID)["']?\s*:)""")
NAME = r"""(?:"(?P<dq>[^"\\\r\n]+)"|'(?P<sq>[^'\\\r\n]+)'|`(?P<tq>[^`\\$\r\n]+)`)"""
VALUE = re.compile(
    r"""\s*(?:["'](?P<quoted>[\w$]+)["']|(?P<key>[\w$]+))\s*:\s*(?:"""
    + NAME
    + r"""|(?P<bare>[A-Za-z_$][\w$.]*|0[xX][0-9a-fA-F]+|\d{1,5}(?:[eE]\+?\d{1,2})?|![01]))(?=\s*[,}])"""
)


class Scripts(HTMLParser):
    def __init__(self):
        super().__init__()
        self.sources = []

    def handle_starttag(self, tag, attrs):
        if tag == "script":
            url = dict(attrs).get("src")
            if url:
                self.sources.append(url)


def require_host(url, host):
    parts = urlsplit(url)
    if (parts.scheme != "https" or parts.hostname != host or parts.port not in (None, 443)
            or parts.username is not None or parts.password is not None):
        raise ValueError("source URL is outside the vendor HTTPS host")


class VendorRedirect(HTTPRedirectHandler):
    def __init__(self, host):
        self.host = host

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        require_host(newurl, self.host)
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def download(url, host):
    require_host(url, host)
    opener = build_opener(VendorRedirect(host))
    request = Request(url, headers={"User-Agent": "MyFinder-Model-Inventory/1.0"})
    with opener.open(request, timeout=15) as response:
        require_host(response.geturl(), host)
        body = response.read(MAX_BYTES + 1)
        if not body or len(body) > MAX_BYTES:
            raise ValueError("empty response or source too large")
        return response.geturl(), body


def usb_number(text):
    if text is None:
        return None
    if re.fullmatch(r"0[xX][0-9a-fA-F]{1,4}", text):
        value = int(text, 16)
    elif re.fullmatch(r"\d{1,5}(?:[eE]\+?\d{1,2})?", text, re.ASCII):
        exact = Decimal(text)
        if not 0 < exact <= 65535:
            return None
        value = int(exact)
    else:
        return None
    return value if 0 < value <= 65535 else None


def literal_rows(source):
    for start in OBJECT_START.finditer(source):
        offset = start.start(1)
        position = offset + 1
        fields = {}
        for _ in range(24):
            match = VALUE.match(source, position)
            if match is None:
                break
            key = match["key"] or match["quoted"]
            if key in fields:
                fields.clear()
                break
            quoted = match["dq"] or match["sq"] or match["tq"]
            fields[key] = quoted, match["bare"]
            position = match.end()
            while position < len(source) and source[position].isspace():
                position += 1
            if source[position:position + 1] != ",":
                break
            position += 1
        if fields:
            yield offset, fields


def quoted(fields, keys):
    for key in keys:
        value, _ = fields.get(key, (None, None))
        if value:
            return value
    return None


def parse_literal_records(source, name_keys, protocol_keys, *, mchose=False,
              vid_keys=("vendorId",), pid_keys=("productId",)):
    for offset, fields in literal_rows(source):
        vid = next((usb_number(fields[key][1] or fields[key][0]) for key in vid_keys if key in fields), None)
        pid = next((usb_number(fields[key][1] or fields[key][0]) for key in pid_keys if key in fields), None)
        if vid is None or pid is None:
            continue
        model = quoted(fields, name_keys)
        protocol = quoted(fields, protocol_keys)
        if mchose and protocol is None:
            symbol = fields.get("protocolProfile", (None, None))[1]
            prefix = "AUDIO_PROTOCOL_PROFILE."
            if symbol and symbol.startswith(prefix):
                protocol = symbol
        if model and protocol:
            yield offset, vid, pid, model, protocol


def parse_mchose(source):
    return parse_literal_records(source, ("productName", "modelKey"), ("protocolProfile", "protocolType"), mchose=True)


def parse_vgn(source):
    return parse_literal_records(source, ("productName",), ("customDeviceType",))


def parse_aula(source):
    return parse_literal_records(source, ("productName", "modelName", "name"),
                     ("protocolType", "driverType"), vid_keys=("vendorId", "vid", "VID"),
                     pid_keys=("productId", "pid", "PID"))


def parse_epomaker(source):
    for offset, fields in literal_rows(source):
        model = quoted(fields, ("name",))
        vendor = quoted(fields, ("vendor",))
        version = quoted(fields, ("protocolVersion",))
        vid = usb_number(fields.get("vendorId", (None, None))[1])
        pid = usb_number(fields.get("productId", (None, None))[1])
        if model and vendor and version and vid is not None and pid is not None:
            yield offset, vid, pid, model, f"{vendor} {version}"


def parse_zhuaudio(source):
    if "assets/zhulinniao/zg4/" not in source.lower():
        return
    # The published Flutter table binds this single USB identity to the ZG4
    # service. Both the constructor and its list consumer must be present.
    for match in re.finditer(r"(B\.\w+)\s*=\s*new A\.\w+\(13908,\s*20565\)\s*(?:;|\s)\s*B\.\w+\s*=\s*\w+\(\[\1\]", source):
        yield match.start(), 13908, 20565, "ZhuAudio ZG4", "ZG4 (FF05)"


PARSERS = {
    "mchose": parse_mchose,
    "vgn": parse_vgn,
    "aula": parse_aula,
    "epomaker": parse_epomaker,
    "zhuaudio": parse_zhuaudio,
}


def harvest(vendor, known, fetch=download):
    entry = ENTRYPOINTS[vendor]
    host = urlsplit(entry).hostname
    sources, errors, models = [], [], {}

    def read(url):
        try:
            final, raw = fetch(url, host)
            require_host(final, host)
            digest = sha256(raw).hexdigest()
            sources.append({"url": final, "sha256": digest})
            return final, digest, raw.decode("utf-8")
        except (OSError, URLError, ValueError) as error:
            errors.append({"url": url, "error": str(error)})
            return None

    html = read(entry)
    if html:
        parser = Scripts()
        parser.feed(html[2])
        pending = deque(
            urljoin(html[0], source) for source in parser.sources
            if MAIN_SCRIPT[vendor].fullmatch(urlsplit(source).path.rsplit("/", 1)[-1])
        )
        seen = set()
        while pending and len(seen) < MAX_SCRIPTS:
            url = pending.popleft()
            if url in seen:
                continue
            seen.add(url)
            loaded = read(url)
            if loaded is None:
                continue
            final, digest, content = loaded
            for offset, vid, pid, model, protocol in PARSERS[vendor](content):
                identity = f"{vid:04X}:{pid:04X}"
                models.setdefault((identity, model, protocol), {
                    "id": identity, "model": model, "protocol": protocol,
                    "url": final, "sha256": digest, "offset": offset,
                })
            links = LINKED_SCRIPTS.get(vendor)
            if links:
                pending.extend(urljoin(final, item) for item in links.findall(content))
        if pending:
            errors.append({"url": entry, "error": "script discovery limit reached"})
        if not models:
            errors.append({"url": entry, "error": "no supported literal model table found"})

    records = sorted(models.values(), key=lambda row: (row["id"], row["model"], row["protocol"]))
    identities = {row["id"] for row in records}
    status = "partial" if records and errors else "ok" if records else "failed"
    return {"status": status, "models": len(records), "uniqueIds": len(identities),
            "unknownIds": len(identities - known), "sources": sources, "records": records,
            "errors": errors}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--known-ids", required=True, type=Path)
    parser.add_argument("--out", type=Path, default=Path("tmp/harvest/results.json"))
    args = parser.parse_args()
    dataset = json.loads(args.known_ids.read_text(encoding="utf-8-sig"))
    known = {f"{row['vendorId']:04X}:{row['productId']:04X}" for row in dataset["devices"]}
    result = {vendor: harvest(vendor, known) for vendor in ENTRYPOINTS}
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        vendor: {key: data[key] for key in ("status", "models", "uniqueIds", "unknownIds", "errors")}
        for vendor, data in result.items()
    }, ensure_ascii=True))
    return 0 if all(data["status"] == "ok" for data in result.values()) else 1


if __name__ == "__main__":
    raise SystemExit(main())
