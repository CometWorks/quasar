#!/usr/bin/env python3
"""Remove low-signal framework plumbing from graphify extraction JSON.

Run this after graphify extraction writes graphify-out/.graphify_extract.json and
before graph build/report generation. It removes generic .NET/C# plumbing nodes
and edges so graph centrality is driven by Quasar concepts instead of async and
collection primitives.
"""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path
from typing import Any


DEFAULT_INPUT = Path("graphify-out/.graphify_extract.json")

DROP_LABELS = {
    "action",
    "boolean",
    "bool",
    "cancellationtoken",
    "cancellationtokensource",
    "concurrentdictionary",
    "datetime",
    "datetimeoffset",
    "decimal",
    "dictionary",
    "double",
    "exception",
    "float",
    "func",
    "guid",
    "hashset",
    "iasyncenumerable",
    "icollection",
    "ienumerable",
    "ilogger",
    "int",
    "int32",
    "int64",
    "ireadonlycollection",
    "ireadonlylist",
    "list",
    "long",
    "object",
    "queue",
    "string",
    "task",
    "timespan",
    "valuetask",
}

# Exact endpoint IDs emitted by AST import extraction for framework namespaces.
# Most never have matching nodes, so they show up as dangling endpoint warnings.
DROP_ENDPOINT_IDS = {
    "collections",
    "compression",
    "concurrent",
    "diagnostics",
    "generic",
    "globalization",
    "io",
    "json",
    "linq",
    "net",
    "reflection",
    "regularexpressions",
    "runtime",
    "serialization",
    "system",
    "tasks",
    "text",
    "threading",
}


def normalize(value: Any) -> str:
    text = str(value or "")
    text = re.sub(r"([a-z0-9])([A-Z])", r"\1_\2", text)
    text = re.sub(r"[^A-Za-z0-9]+", "_", text)
    return text.strip("_").lower()


def node_is_plumbing(node: dict[str, Any]) -> bool:
    label = normalize(node.get("label"))
    node_id = normalize(node.get("id"))
    return label in DROP_LABELS or node_id in DROP_LABELS or node_id in DROP_ENDPOINT_IDS


def edge_hits_plumbing(edge: dict[str, Any], removed_ids: set[str]) -> bool:
    source = str(edge.get("source") or "")
    target = str(edge.get("target") or "")
    source_norm = normalize(source)
    target_norm = normalize(target)
    return (
        source in removed_ids
        or target in removed_ids
        or source_norm in DROP_ENDPOINT_IDS
        or target_norm in DROP_ENDPOINT_IDS
        or source_norm in DROP_LABELS
        or target_norm in DROP_LABELS
    )


def prune(payload: dict[str, Any]) -> tuple[dict[str, Any], dict[str, int]]:
    nodes = payload.get("nodes", [])
    edges = payload.get("edges", [])
    hyperedges = payload.get("hyperedges", [])

    kept_nodes: list[dict[str, Any]] = []
    removed_ids: set[str] = set()
    for node in nodes:
        if node_is_plumbing(node):
            removed_ids.add(str(node.get("id") or ""))
        else:
            kept_nodes.append(node)

    kept_edges = [
        edge for edge in edges
        if not edge_hits_plumbing(edge, removed_ids)
    ]

    kept_hyperedges: list[dict[str, Any]] = []
    for hyperedge in hyperedges:
        original_nodes = list(hyperedge.get("nodes", []))
        remaining_nodes = [
            node_id for node_id in original_nodes
            if node_id not in removed_ids
            and normalize(node_id) not in DROP_ENDPOINT_IDS
            and normalize(node_id) not in DROP_LABELS
        ]
        if len(remaining_nodes) >= 3:
            updated = dict(hyperedge)
            updated["nodes"] = remaining_nodes
            kept_hyperedges.append(updated)

    pruned = dict(payload)
    pruned["nodes"] = kept_nodes
    pruned["edges"] = kept_edges
    pruned["hyperedges"] = kept_hyperedges

    stats = {
        "nodes_removed": len(nodes) - len(kept_nodes),
        "edges_removed": len(edges) - len(kept_edges),
        "hyperedges_removed": len(hyperedges) - len(kept_hyperedges),
        "nodes_before": len(nodes),
        "nodes_after": len(kept_nodes),
        "edges_before": len(edges),
        "edges_after": len(kept_edges),
        "hyperedges_before": len(hyperedges),
        "hyperedges_after": len(kept_hyperedges),
    }
    return pruned, stats


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Prune generic framework plumbing from graphify extraction JSON.",
    )
    parser.add_argument(
        "input",
        nargs="?",
        default=DEFAULT_INPUT,
        type=Path,
        help=f"Extraction JSON to prune (default: {DEFAULT_INPUT})",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=None,
        help="Write pruned JSON to this path. Defaults to overwriting input.",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Print prune stats without writing output.",
    )
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    payload = json.loads(args.input.read_text(encoding="utf-8"))
    pruned, stats = prune(payload)

    for key, value in stats.items():
        print(f"{key}: {value}")

    if args.dry_run:
        return

    output = args.output or args.input
    output.write_text(json.dumps(pruned, indent=2, ensure_ascii=False), encoding="utf-8")


if __name__ == "__main__":
    main()
