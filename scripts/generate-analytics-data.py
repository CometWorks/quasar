#!/usr/bin/env python3
"""Generate synthetic Quasar analytics JSONL data for one or more servers."""

from __future__ import annotations

import argparse
import json
import math
import os
import random
import time
from datetime import datetime, timezone
from pathlib import Path


RAW_BUCKET = "r"
MINUTE_BUCKET = "m"
HOUR_BUCKET = "h"


def clamp(value: float, min_value: float, max_value: float) -> float:
    return max(min_value, min(max_value, value))


def get_quasar_root(install_dir: str | None) -> Path:
    if install_dir:
        return Path(install_dir).expanduser().resolve()

    env_override = os.getenv("QUASAR_INSTALL_DIR")
    if env_override:
        return Path(env_override).expanduser().resolve()

    return Path.cwd().resolve()


def list_servers(quasar_root: Path) -> list[str]:
    servers_dir = quasar_root / "Magnetars"
    if not servers_dir.is_dir():
        return []

    return sorted([p.name for p in servers_dir.iterdir() if p.is_dir()])


def create_line(bucket: str, t: int, sample: dict) -> str:
    payload = {
        "b": bucket,
        "T": t,
        "Ss": sample["Ss"],
        "Cpu": sample["Cpu"],
        "Mem": sample["Mem"],
        "Ft": sample["Ft"],
        "P": sample["P"],
        "Pcu": sample["Pcu"],
        "G": sample["G"],
        "E": sample["E"],
    }
    return json.dumps(payload, separators=(",", ":"))


def generate_sample(t: int, rng: random.Random) -> dict[str, float | int]:
    hour_ratio = (t % 86_400) / 86_400.0
    day_wave = math.sin(hour_ratio * 2.0 * math.pi)
    minute_ratio = (t % 3600) / 3600.0
    burst = (math.sin((t % 5_000) / 80.0) + 1.0) * 0.5

    players = int(clamp(
        6 + 10 * max(0, day_wave) + 4 * math.sin(hour_ratio * 6 * math.pi) + rng.gauss(0, 2),
        0,
        32,
    ))

    sim = clamp(0.65 + 0.25 * day_wave + rng.gauss(0, 0.05), 0.2, 1.35)

    cpu = clamp(
        22 + 28 * day_wave + 1.4 * players + 7 * burst + rng.uniform(-5, 6),
        1,
        99,
    )

    mem = clamp(
        4200 + 700 * day_wave + 18 * players + rng.uniform(-140, 180),
        2400,
        8600,
    )

    frame = clamp(
        12 + 2 * math.cos(minute_ratio * 2.0 * math.pi) + players * 0.65 + burst * 3 + rng.uniform(-2.0, 3.0),
        1,
        90,
    )
    if rng.random() < 0.02:
        frame = clamp(frame + rng.uniform(8, 45), 1, 130)

    used_pcu = int(clamp(2400 + 90 * players + 60 * burst + rng.gauss(0, 180), 300, 9000))
    active_grid = int(clamp(160 + 4 * players + rng.gauss(0, 10), 20, 9000))
    active_entity = int(clamp(3200 + 65 * players + 30 * burst + rng.gauss(0, 150), 200, 60000))

    return {
        "Ss": float(round(sim, 4)),
        "Cpu": float(round(cpu, 4)),
        "Mem": float(round(mem, 4)),
        "Ft": float(round(frame, 4)),
        "P": int(players),
        "Pcu": int(used_pcu),
        "G": int(active_grid),
        "E": int(active_entity),
    }


def write_bucket_lines(path: Path, lines: list[str]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="") as handle:
        for line in lines:
            handle.write(line)
            handle.write("\n")


def generate_series(
    start_ts: int,
    end_ts: int,
    interval_seconds: int,
    bucket: str,
    rng: random.Random,
) -> list[str]:
    if interval_seconds <= 0:
        raise ValueError("interval must be > 0")

    lines: list[str] = []
    t = start_ts
    while t < end_ts:
        lines.append(create_line(bucket, t, generate_sample(t, rng)))
        t += interval_seconds
    return lines


def build_args():
    parser = argparse.ArgumentParser(description="Generate Quasar analytics.jsonl test data.")
    parser.add_argument("--install-dir", default=None, help="Override Quasar install root directory")
    parser.add_argument("--days", type=int, default=30, help="How many days to generate (default 30)")
    parser.add_argument("--server", action="append", help="Server uniqueName; repeatable")
    parser.add_argument("--seed", type=int, default=1337, help="Random seed for repeatable output")
    parser.add_argument("--raw-hours", type=float, default=1.0, help="Hours of raw data to generate")
    parser.add_argument("--raw-interval", type=int, default=1, help="Raw sample interval in seconds")
    return parser.parse_args()


def main():
    args = build_args()
    if args.days <= 0:
        raise ValueError("--days must be > 0")
    if args.raw_interval <= 0:
        raise ValueError("--raw-interval must be > 0")
    if args.raw_hours <= 0:
        raise ValueError("--raw-hours must be > 0")

    quasar_root = get_quasar_root(args.install_dir)
    servers_dir = quasar_root / "Magnetars"

    if args.server:
        server_names = [s for s in args.server if s.strip()]
    else:
        server_names = list_servers(quasar_root)

    if not server_names:
        raise RuntimeError(f"No server folders found under {servers_dir}")

    now = int(time.time())
    range_start = now - args.days * 24 * 3600
    range_end = now
    start_minute = ((range_start + 59) // 60) * 60
    start_hour = ((range_start + 3599) // 3600) * 3600
    rng = random.Random(args.seed)

    for name in server_names:
        server_path = servers_dir / name
        analytics_path = server_path / "analytics.jsonl"

        minute_lines = generate_series(start_minute, range_end, 60, MINUTE_BUCKET, random.Random(rng.randint(0, 2**31 - 1)))
        hour_lines = generate_series(start_hour, range_end, 3600, HOUR_BUCKET, random.Random(rng.randint(0, 2**31 - 1)))

        raw_end = range_end
        raw_start = max(range_start, raw_end - int(args.raw_hours * 3600))
        raw_start = ((raw_start + args.raw_interval - 1) // args.raw_interval) * args.raw_interval
        raw_lines = generate_series(raw_start, raw_end, args.raw_interval, RAW_BUCKET, rng)

        lines = minute_lines + hour_lines + raw_lines
        write_bucket_lines(analytics_path, lines)

        from_ts = datetime.fromtimestamp(range_start, tz=timezone.utc).isoformat()
        to_ts = datetime.fromtimestamp(range_end, tz=timezone.utc).isoformat()
        print(
            f"{name}: wrote {len(lines)} lines to {analytics_path} "
            f"({len(minute_lines)} m / {len(hour_lines)} h / {len(raw_lines)} r) "
            f"({from_ts}..{to_ts})"
        )

        # Keep deterministic but not identical across servers.
        rng.seed(rng.randint(0, 2**31 - 1))


if __name__ == "__main__":
    main()
