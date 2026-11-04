"""Build a complete snapshot and reject even engine row-level rejections."""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "engine"))
from device_pack import MAX_BYTES, _constant, _object, load_pack, parse_pack


def build(source, version):
    raw = Path(source).read_bytes()
    if len(raw) > MAX_BYTES:
        raise ValueError("source_too_large")
    data = json.loads(raw.decode("utf-8"), object_pairs_hook=_object, parse_constant=_constant)
    if type(data) is not dict or set(data) != {"entries", "disabled"}:
        raise ValueError("source_fields_must_be_entries_and_disabled")
    document = dict(format="myfinder-device-pack", loader=1, packVersion=version,
                    keyId="pack-2026a", createdAt=datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
                    **data)
    raw = (json.dumps(document, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
    check(parse_pack(raw))
    return raw


def check(pack):
    if not pack.loaded or pack.rejected:
        raise ValueError("; ".join(pack.rejected) or "pack_not_loaded")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=ROOT / "devices/pack/source.json")
    parser.add_argument("--version", type=int, default=int(datetime.now(timezone.utc).strftime("%Y%m%d") + "01"))
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    try:
        raw = build(args.source, args.version)
        if args.output:
            args.output.write_bytes(raw)
            check(load_pack(args.output))
        else:
            # CI needs no key, but exercises the same on-disk loader as publication.
            with tempfile.TemporaryDirectory(prefix="device-pack-check-") as work:
                path = Path(work) / "pack.json"
                path.write_bytes(raw)
                check(load_pack(path))
        print("Device pack source and complete snapshot validated.")
    except (ValueError, OSError, UnicodeError) as error:
        print("Device pack validation failed: " + str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
