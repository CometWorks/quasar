#!/usr/bin/env python3
"""Generate low-poly armor fallback geometry from Space Engineers data."""

from __future__ import annotations

import argparse
import csv
import json
import os
import re
import struct
from collections import OrderedDict
from pathlib import Path

from scipy.spatial import ConvexHull


GRID_SIZE = {"Large": 2.5, "Small": 0.5}
EDGE_QUANTUM = 0.05
SILHOUETTE_QUANTUM = 0.25


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--csv", required=True, help="Path to the armor block metadata CSV.")
    parser.add_argument("--content", required=True, help="Path to the Space Engineers Content directory.")
    parser.add_argument("--topology-source", required=True, help="Path to Sandbox.Game/.../MyBlockVerticesCache.cs from decompiled game sources.")
    parser.add_argument("--topology-enum", required=True, help="Path to VRage.Game/.../MyCubeTopology.cs from decompiled game sources.")
    parser.add_argument("--output", default="Quasar/wwwroot/viewer/armor-fallback-models.js")
    args = parser.parse_args()

    rows = list(csv.DictReader(open(args.csv, newline="")))
    topology_vertices = read_topology_vertices(Path(args.topology_source), Path(args.topology_enum))
    models: OrderedDict[str, dict] = OrderedDict()
    signatures: dict[str, str] = {}
    subtype_to_key: dict[str, str] = {}
    model_key_to_key: dict[str, str] = {}
    missing: list[str] = []

    for row in rows:
        if row["blockTopology"] == "Cube":
            topology = sharp_topology(row["cubeTopology"] or "Box")
            key = f"cube_{snake(topology)}"
            if key not in models:
                vertices = [[round(c / 2, 4) for c in point] for point in topology_vertices[topology]]
                vertices, triangles = convex_hull_model(vertices)
                models[key] = {"description": f"Armor cube topology {topology}.", "vertices": vertices, "triangles": triangles, "is_box": topology == "Box"}
            subtype_to_key[row["subtypeId"].lower()] = key
            continue

        target_model_key = round_panel_target_model_key(row)
        if target_model_key:
            subtype_to_key[row["subtypeId"].lower()] = model_key_to_key[target_model_key]
            continue

        source_model = row["sourceModel"]
        path = case_insensitive_path(Path(args.content) / source_model)
        if not path.exists():
            missing.append(source_model)
            continue
        model = read_mwm_model(path, GRID_SIZE[row["cubeSize"]], Path(args.content))
        signature = json.dumps([model["vertices"], model["triangles"]], separators=(",", ":"))
        key = signatures.get(signature)
        if key is None:
            key = f"mesh_{len(signatures) + 1:02d}"
            signatures[signature] = key
            model["description"] = f"Armor mesh fallback {key}."
            model["is_box"] = False
            models[key] = model
        subtype_to_key[row["subtypeId"].lower()] = key
        model_key_to_key.setdefault(row["modelKey"], key)

    if missing:
        raise SystemExit("Missing source models:\n" + "\n".join(sorted(set(missing))))

    output = render_js(models, subtype_to_key, model_key_to_key)
    Path(args.output).write_text(output, encoding="utf-8")


def snake(value: str) -> str:
    value = re.sub(r"(.)([A-Z][a-z]+)", r"\1_\2", value)
    value = re.sub(r"([a-z0-9])([A-Z])", r"\1_\2", value)
    return value.lower()


def sharp_topology(topology: str) -> str:
    return {
        "RoundedSlope": "Slope",
        "RoundSlope": "Slope",
        "RoundCorner": "Corner",
        "RoundInvCorner": "InvCorner",
    }.get(topology, topology)


def round_panel_target_model_key(row: dict[str, str]) -> str | None:
    if "roundarmorpanel" not in row["subtypeId"].lower():
        return None
    return {
        "thin_side_panel": "thin_side_panel",
        "thin_center_panel": "thin_center_panel",
        "thin_corner_panel": "thin_quarter_panel",
    }[row["modelKey"]]


def read_topology_vertices(path: Path, enum_path: Path) -> dict[str, list[list[float]]]:
    enum_text = enum_path.read_text(encoding="utf-8")
    enum_names = re.findall(r"^\s*([A-Za-z][A-Za-z0-9]*),?\s*$", enum_text, re.MULTILINE)

    text = path.read_text(encoding="utf-8")
    result: dict[str, list[list[float]]] = {}
    for name in enum_names:
        if name == "CornerSquareInverted":
            pattern = r"else\s*\{(?P<body>.*?)\n\s*\}\s*\n\s*}\s*$"
        else:
            pattern = rf"case MyCubeTopology\.{name}:\s*(?P<body>.*?)\s*break;"
        match = re.search(pattern, text, re.DOTALL)
        if not match:
            continue
        result[name] = parse_vector_adds(match.group("body"))
    if "StandaloneBox" not in result:
        result["StandaloneBox"] = result["Box"]
    return result


def parse_vector_adds(body: str) -> list[list[float]]:
    points = []
    for x, y, z in re.findall(r"new Vector3\(([-0-9.]+)f?, ([-0-9.]+)f?, ([-0-9.]+)f?\)", body):
        point = [float(x), float(y), float(z)]
        if point not in points:
            points.append(point)
    return points


def convex_hull_model(points: list[list[float]]) -> tuple[list[list[float]], list[list[int]]]:
    vertices = unique_points([[quantize(c) for c in point] for point in points])
    if len(vertices) < 4:
        return vertices, []
    hull = ConvexHull(vertices)
    triangles = sorted(sorted_triangle(simplex) for simplex in hull.simplices.tolist())
    triangles = orient_triangles_outward(vertices, triangles)
    return vertices, triangles


def read_mwm_model(path: Path, grid_size: float, content: Path) -> dict:
    with path.open("rb") as stream:
        read_string(stream)
        version_tags = read_string_array(stream)
        version = int(version_tags[0].replace("Version:", "")) if version_tags else 0
        index = read_index_dictionary(stream)
        if "Vertices" not in index and "GeometryDataAsset" in index:
            stream.seek(index["GeometryDataAsset"])
            assert read_string(stream) == "GeometryDataAsset"
            target = read_string(stream).replace("\\", "/")
            if not target.lower().endswith(".mwm"):
                target += ".mwm"
            return read_mwm_model(case_insensitive_path(content / target), grid_size, content)
        vertices = read_vertices_at(stream, index["Vertices"], grid_size)
        triangles = read_mesh_parts_at(stream, index["MeshParts"], version)

    remap: OrderedDict[tuple[float, float, float], int] = OrderedDict()
    out_triangles: list[list[int]] = []
    for tri in triangles:
        out = []
        for index in tri:
            point = vertices[index]
            if point not in remap:
                remap[point] = len(remap)
            out.append(remap[point])
        if len(set(out)) == 3:
            out_triangles.append(out)
    out_vertices = [list(point) for point in remap.keys()]
    out_triangles = orient_triangles_outward(out_vertices, out_triangles)
    return {"vertices": out_vertices, "triangles": out_triangles}


def orient_triangles_outward(vertices: list[list[float]], triangles: list[list[int]]) -> list[list[int]]:
    if not vertices:
        return triangles

    center = [sum(point[axis] for point in vertices) / len(vertices) for axis in range(3)]
    oriented = []
    for triangle in triangles:
        a, b, c = (vertices[index] for index in triangle)
        ab = [b[axis] - a[axis] for axis in range(3)]
        ac = [c[axis] - a[axis] for axis in range(3)]
        normal = [
            ab[1] * ac[2] - ab[2] * ac[1],
            ab[2] * ac[0] - ab[0] * ac[2],
            ab[0] * ac[1] - ab[1] * ac[0],
        ]
        centroid = [(a[axis] + b[axis] + c[axis]) / 3 for axis in range(3)]
        outward = sum(normal[axis] * (centroid[axis] - center[axis]) for axis in range(3))
        oriented.append([triangle[0], triangle[2], triangle[1]] if outward < 0 else triangle)
    return oriented


def read_vertices_at(stream, offset: int, grid_size: float) -> list[tuple[float, float, float]]:
    stream.seek(offset)
    assert read_string(stream) == "Vertices"
    count = read_i32(stream)
    vertices = []
    for _ in range(count):
        x, y, z, scale = struct.unpack("<eeee", stream.read(8))
        vertices.append((quantize(x * scale / grid_size), quantize(y * scale / grid_size), quantize(z * scale / grid_size)))
    return vertices


def read_mesh_parts_at(stream, offset: int, version: int) -> list[list[int]]:
    stream.seek(offset)
    assert read_string(stream) == "MeshParts"
    triangles = []
    for _ in range(read_i32(stream)):
        stream.read(4)
        count = read_i32(stream)
        indices = [read_i32(stream) for _ in range(count)]
        for i in range(0, len(indices) - 2, 3):
            triangles.append([indices[i], indices[i + 2], indices[i + 1]])
        if read_bool(stream):
            skip_material(stream, version)
    return triangles


def skip_material(stream, version: int) -> None:
    read_string(stream)
    if version < 1052002:
        read_string(stream); read_string(stream)
    else:
        for _ in range(read_i32(stream)):
            read_string(stream); read_string(stream)
    if version >= 1068001:
        for _ in range(read_i32(stream)):
            read_string(stream); read_string(stream)
    if version < 1157001:
        stream.read(28)
    technique = read_string(stream) if version >= 1052001 else str(read_i32(stream))
    if technique == "GLASS":
        read_string(stream); read_string(stream); stream.read(1)


def read_index_dictionary(stream) -> dict[str, int]:
    return {read_string(stream): read_i32(stream) for _ in range(read_i32(stream))}


def read_string_array(stream) -> list[str]:
    return [read_string(stream) for _ in range(read_i32(stream))]


def read_string(stream) -> str:
    count = 0
    shift = 0
    while True:
        value = stream.read(1)[0]
        count |= (value & 0x7F) << shift
        if (value & 0x80) == 0:
            break
        shift += 7
    return stream.read(count).decode("utf-8")


def read_i32(stream) -> int:
    return struct.unpack("<i", stream.read(4))[0]


def read_bool(stream) -> bool:
    return struct.unpack("<?", stream.read(1))[0]


def case_insensitive_path(path: Path) -> Path:
    current = Path(path.anchor) if path.is_absolute() else Path(".")
    for part in path.parts[1 if path.is_absolute() else 0:]:
        try:
            names = os.listdir(current)
        except OSError:
            return current / part
        match = next((name for name in names if name.lower() == part.lower()), part)
        current /= match
    return current


def quantize(value: float) -> float:
    value = round(value / EDGE_QUANTUM) * EDGE_QUANTUM
    if abs(value) < 0.00001:
        return 0
    if abs(abs(value) - 0.5) <= EDGE_QUANTUM:
        return round(value, 4)
    if abs(value) <= EDGE_QUANTUM:
        return round(value, 4)
    value = round(value / SILHOUETTE_QUANTUM) * SILHOUETTE_QUANTUM
    return 0 if abs(value) < 0.00001 else round(value, 4)


def unique_points(points: list[list[float]]) -> list[list[float]]:
    result = []
    for point in points:
        if point not in result:
            result.append(point)
    return result


def sorted_triangle(values: list[int]) -> list[int]:
    return values


def render_js(models: OrderedDict[str, dict], subtype_to_key: dict[str, str], model_key_to_key: dict[str, str]) -> str:
    lines = [
        "// Generated by viewer/tools/generate-armor-fallback-models.py.",
        "// Low-poly vertices are quantized and texture/material data is intentionally omitted.",
        "const ARMOR_FALLBACK_MODELS = {",
    ]
    for key, item in models.items():
        box_flag = ", true" if item.get("is_box") else ""
        lines.append(f"    {key}: model({json.dumps(item['description'])}, {json.dumps(item['vertices'], separators=(',', ':'))}, {json.dumps(item['triangles'], separators=(',', ':'))}{box_flag}),")
    lines.extend(["};", "", "const ARMOR_FALLBACK_MODEL_BY_SUBTYPE = {"])
    for subtype, key in sorted(subtype_to_key.items()):
        lines.append(f"    {json.dumps(subtype)}: {json.dumps(key)},")
    lines.extend([
        "};",
        "",
        "export function armorFallbackModelForDefinition(definition) {",
        "    const subtype = definitionSubtypeId(definition);",
        "    if (!subtype || !isArmorSubtype(subtype)) return null;",
        "    const key = ARMOR_FALLBACK_MODEL_BY_SUBTYPE[subtype.toLowerCase()] || armorFallbackModelKey(subtype.toLowerCase());",
        "    return ARMOR_FALLBACK_MODELS[key] || null;",
        "}",
        "",
        "export function armorFallbackModelCount() {",
        "    return Object.keys(ARMOR_FALLBACK_MODELS).length;",
        "}",
        "",
    ])
    lines.extend(render_fallback_helpers(model_key_to_key).strip().splitlines())
    return "\n".join(lines) + "\n"


def render_fallback_helpers(model_key_to_key: dict[str, str]) -> str:
    return (r'''
function armorFallbackModelKey(subtype) {
    if (subtype.includes("panel")) return panelFallbackModelKey(subtype);
    if (subtype.includes("inv")) return "cube_inv_corner";
    if (subtype.includes("slope2base")) return "cube_slope2_base";
    if (subtype.includes("slope2tip")) return "cube_slope2_tip";
    if (subtype.includes("corner2base")) return "cube_corner2_base";
    if (subtype.includes("corner2tip")) return "cube_corner2_tip";
    if (subtype.includes("cornersquare") || subtype.includes("raisedslopedcorner") || subtype.includes("slopedcornerbase") || subtype.includes("halfslopecorner") || subtype.includes("halfslopedcorner") || subtype.includes("squareslopedcornerbase")) return "cube_sloped_corner";
    if (subtype.includes("halfslopearmorblock") || subtype.includes("halfslope") || subtype.includes("halfsloped")) return "cube_half_slope_box";
    if (subtype.includes("halfarmorblock")) return "cube_half_box";
    if (subtype.includes("halfcorn")) return "cube_box";
    if (subtype.includes("corner") || subtype.includes("slopedcornertip") || subtype.includes("squareslopedcornertip")) return "cube_corner";
    if (subtype.includes("slope")) return "cube_slope";
    return "cube_box";
}

function panelFallbackModelKey(subtype) {
    if (subtype.includes("round") && subtype.includes("corner")) return "__THIN_QUARTER_PANEL__";
    if (subtype.includes("slopedside") || subtype.includes("slopeside")) return "__THIN_SIDE_SLOPE__";
    if (subtype.includes("halfsloped")) return "__THIN_HALF_WEDGE__";
    if (subtype.includes("sloped") || subtype.includes("slope")) return "__THIN_WEDGE__";
    if (subtype.includes("quarter")) return "__THIN_QUARTER_PANEL__";
    if (subtype.includes("halfcenter")) return "__THIN_HALF_CENTER_PANEL__";
    if (subtype.includes("half")) return "__THIN_HALF_PANEL__";
    if (subtype.includes("center") || subtype.includes("face")) return "__THIN_CENTER_PANEL__";
    if (subtype.includes("corner")) return "__THIN_QUARTER_PANEL__";
    return "__THIN_SIDE_PANEL__";
}

function isArmorSubtype(subtype) {
    const lower = subtype.toLowerCase();
    return lower.includes("armor") && !lower.includes("armory");
}

function definitionSubtypeId(definition) {
    const id = String(definition && definition.id || "");
    if (!id) return "";
    const slash = id.lastIndexOf("/");
    return slash >= 0 ? id.slice(slash + 1) : id;
}

function model(description, vertices, triangles, isBoxPlaceholder = false) {
    return { description, vertices, triangles, isBoxPlaceholder };
}
'''
        .replace("__THIN_QUARTER_PANEL__", model_key_to_key["thin_quarter_panel"])
        .replace("__THIN_SIDE_SLOPE__", model_key_to_key["thin_side_slope"])
        .replace("__THIN_HALF_WEDGE__", model_key_to_key["thin_half_wedge"])
        .replace("__THIN_WEDGE__", model_key_to_key["thin_wedge"])
        .replace("__THIN_HALF_CENTER_PANEL__", model_key_to_key["thin_half_center_panel"])
        .replace("__THIN_HALF_PANEL__", model_key_to_key["thin_half_panel"])
        .replace("__THIN_CENTER_PANEL__", model_key_to_key["thin_center_panel"])
        .replace("__THIN_SIDE_PANEL__", model_key_to_key["thin_side_panel"])
    )


if __name__ == "__main__":
    main()
