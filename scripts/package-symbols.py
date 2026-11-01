#!/usr/bin/env python3
"""Archive matching portable PDBs and managed modules from the exact release build."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import uuid
import zipfile


def u32(data, offset):
    return struct.unpack_from("<I", data, offset)[0]


def portable_id(path):
    data = path.read_bytes()
    if data[:4] != b"BSJB":
        raise ValueError(f"Not a portable PDB: {path}")
    offset = 16 + u32(data, 12)
    count = struct.unpack_from("<H", data, offset + 2)[0]
    offset += 4
    for _ in range(count):
        position = u32(data, offset)
        end = data.index(b"\0", offset + 8)
        name = data[offset + 8:end]
        offset = (end + 4) & ~3
        if name == b"#Pdb":
            return data[position:position + 20]
    raise ValueError(f"Missing portable PDB identifier: {path}")


def matches_module(path, identifier):
    data = path.read_bytes()
    pe = u32(data, 0x3C)
    if data[pe:pe + 4] != b"PE\0\0":
        return False
    optional = pe + 24
    magic = struct.unpack_from("<H", data, optional)[0]
    directories = optional + (112 if magic == 0x20B else 96)
    debug_rva, debug_size = struct.unpack_from("<II", data, directories + 6 * 8)
    sections = optional + struct.unpack_from("<H", data, pe + 20)[0]
    for index in range(struct.unpack_from("<H", data, pe + 6)[0]):
        section = sections + index * 40
        virtual_size, address, raw_size, raw_offset = struct.unpack_from("<IIII", data, section + 8)
        if address <= debug_rva < address + max(virtual_size, raw_size):
            debug_offset = raw_offset + debug_rva - address
            for entry in range(debug_offset, debug_offset + debug_size, 28):
                if u32(data, entry + 12) != 2:
                    continue
                codeview = u32(data, entry + 24)
                if (data[codeview:codeview + 4] == b"RSDS"
                        and data[codeview + 4:codeview + 20] == identifier[:16]
                        and data[entry + 4:entry + 8] == identifier[16:20]):
                    return True
    return False


def description(path, name):
    with path.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    return {"path": name, "bytes": path.stat().st_size, "sha256": digest}


def package(repo, publish, output, runtime, configuration, version):
    commit = subprocess.check_output(["git", "-C", str(repo), "rev-parse", "HEAD"], text=True).strip()
    dirty = bool(subprocess.check_output(["git", "-C", str(repo), "status", "--porcelain", "--untracked-files=no"], text=True).strip())
    suffix = ".exe" if runtime.startswith("win-") else ""
    targets = [("bootstrap", "Quasar.Bootstrap", "Quasar" + suffix),
               ("worker", "Quasar", "WebService/Quasar" + suffix),
               ("host", "Quasar.Host", "Host/Quasar.Host" + suffix)]
    manifest = {"schemaVersion": 1, "version": version, "runtime": runtime,
                "configuration": configuration, "optimized": True,
                "source": {"repository": "https://github.com/CometWorks/quasar", "commit": commit, "dirty": dirty},
                "components": []}
    temporary = output.with_suffix(output.suffix + ".tmp")
    try:
        with zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED) as archive:
            for source, destination in [("LICENSE", "LICENSE"), ("NOTICE", "NOTICE"),
                                        ("Docs/Licensing.md", "LICENSING.md")]:
                archive.write(repo / source, destination)
            for name, project, executable in targets:
                build = repo / project / "bin" / configuration / "net10.0" / runtime
                if not (build / (project + ".pdb")).is_file():
                    raise ValueError(f"Release symbols missing: {build / (project + '.pdb')}")
                component = {"name": name, "executable": description(publish / executable, executable), "modules": []}
                for pdb in sorted(build.glob("*.pdb")):
                    module = pdb.with_suffix(".dll")
                    if not module.is_file():
                        continue
                    identifier = portable_id(pdb)
                    if not matches_module(module, identifier):
                        raise ValueError(f"PDB does not match its release module: {pdb}")
                    module_name, pdb_name = f"{name}/{module.name}", f"{name}/{pdb.name}"
                    archive.write(module, module_name)
                    archive.write(pdb, pdb_name)
                    component["modules"].append({"assembly": description(module, module_name),
                        "symbols": description(pdb, pdb_name), "pdbId": str(uuid.UUID(bytes_le=identifier[:16]))
                            + "-" + identifier[16:20].hex()})
                if not any(item["assembly"]["path"] == f"{name}/{project}.dll" for item in component["modules"]):
                    raise ValueError(f"Matching release module missing: {project}")
                manifest["components"].append(component)
            archive.writestr("manifest.json", json.dumps(manifest, indent=2) + "\n")
        temporary.replace(output)
    finally:
        temporary.unlink(missing_ok=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for argument in ("repo", "publish", "output", "runtime", "configuration", "version"):
        parser.add_argument("--" + argument, required=True, type=Path if argument in ("repo", "publish", "output") else str)
    package(**vars(parser.parse_args()))
