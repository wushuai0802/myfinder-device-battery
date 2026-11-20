"""Read the WOB Rainy75 V2 battery once (or on a loop); never writes to the device.

The 2.4G receiver pushes a BE BB battery frame every ~5 s on the FF60:0061
collection, so this script only listens. Run it with the engine's own
interpreter so hidapi resolves:

    engine\\runtime\\python.exe scripts\\read_rainy75.py
    engine\\runtime\\python.exe scripts\\read_rainy75.py --interval 30 --json
"""
import argparse
import json
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "engine"))
from providers import hidlist  # noqa: E402
from providers.rainyrt import RainyRTProvider  # noqa: E402
from server import NATIVE  # noqa: E402
from topology import discover  # noqa: E402

CHARGING = {0: "未充电", 1: "已充满", 2: "充电中"}


def install_inventory():
    """Feed the provider the descriptor-proven inventory instead of hidapi's.

    Inside the app the reader child receives this inventory from
    `topology.discover`; standalone we build it once and pin it, because the
    provider only opens a collection whose report length was proven there.
    """
    paths = hidlist.interface_paths()
    inventory = discover(sorted(paths) if paths is not None else None, NATIVE, full=True)
    hidlist.enumerate = lambda vid=0: [d for d in inventory if not vid or d.get("vendor_id") == vid]



def snapshot(provider):
    rows = provider.poll()
    for row in rows:
        yield {
            "name": row.name,
            "percent": row.level,
            "charging": row.charging,
            "chargingLabel": CHARGING.get(row.charging, "未知"),
            "measuredAtUtc": row.measured_at_utc,
            "physicalId": row.physical_id,
        }


def collect(provider, window):
    """Poll until a frame arrives or the listening window expires.

    With an already-open handle the provider drains at most 400 ms per call,
    which is shorter than the receiver's ~5 s push period, so a single poll
    can miss; keep polling inside one bounded window instead.
    """
    deadline = time.monotonic() + window
    while True:
        rows = list(snapshot(provider))
        if rows or time.monotonic() >= deadline:
            return rows
        time.sleep(0.35)


def render(rows):
    if not rows:
        return "未收到电量帧：键盘可能未连接、已休眠，或接收器未推送"
    lines = []
    for row in rows:
        lines.append(f"{row['name']}: {row['percent']}% {row['chargingLabel']}（{row['measuredAtUtc']}）")
    return "\n".join(lines)


def main(argv=None):
    parser = argparse.ArgumentParser(description="Passively read WOB Rainy75 V2 battery frames")
    parser.add_argument("--window", type=float, default=7.0,
                        help="seconds to listen for one battery push")
    parser.add_argument("--interval", type=float, default=0.0,
                        help="seconds between reads; 0 reads once and exits")
    parser.add_argument("--count", type=int, default=0, help="stop after N reads; 0 means forever")
    parser.add_argument("--json", action="store_true", help="print one JSON object per read")
    args = parser.parse_args(argv)
    if args.interval < 0 or args.count < 0 or args.window <= 0:
        parser.error("--window must be positive; --interval and --count must not be negative")

    provider = RainyRTProvider()
    reads, status = 0, 2
    try:
        install_inventory()
        while True:
            rows = collect(provider, args.window)
            status = 0 if rows else 2
            reads += 1
            if args.json:
                print(json.dumps({"read": reads, "devices": rows}, ensure_ascii=False), flush=True)
            else:
                print(f"[{time.strftime('%H:%M:%S')}] {render(rows)}", flush=True)
            if not args.interval or (args.count and reads >= args.count):
                break
            time.sleep(args.interval)
    except KeyboardInterrupt:
        status = 0
    finally:
        provider.close()
    return status


if __name__ == "__main__":
    sys.exit(main())
