#!/usr/bin/env python3
"""Patch API 34 partition build.prop files into a coherent analysis persona.

This tool operates on files pulled from a writable-system scratch AVD. It does
not modify an SDK system image directly. The publisher is responsible for
pushing the results back through adb remount and for all reboot/verification
gates.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

FILES = ("system", "product", "system_ext", "vendor", "odm")


def replace_properties(path: Path, values: dict[str, str]) -> None:
    lines = path.read_text(encoding="utf-8").splitlines()
    pending = dict(values)
    output: list[str] = []
    for line in lines:
        if "=" in line and not line.lstrip().startswith("#"):
            key = line.split("=", 1)[0]
            if key in pending:
                output.append(f"{key}={pending.pop(key)}")
                continue
        output.append(line)
    if pending:
        output.append("####################################")
        output.append("# REPlayer maintained persona overlay")
        output.extend(f"{key}={value}" for key, value in sorted(pending.items()))
    path.write_text("\n".join(output) + "\n", encoding="utf-8")


def build_values(persona: dict, mode: str) -> dict[str, dict[str, str]]:
    brand = persona["brand"]
    maker = persona["manufacturer"]
    model = persona["model"]
    device = persona["device"]
    product = persona["product"]
    build_id = persona["buildId"]
    incremental = persona["incremental"]
    fingerprint = persona["fingerprint"]
    description = persona["description"]
    release_type = "user"
    release_tags = "release-keys"
    arm_first = bool(persona.get("advertiseArm64First"))
    abi_list = "arm64-v8a,x86_64" if arm_first else "x86_64,arm64-v8a"

    system = {
        "ro.product.system.brand": brand,
        "ro.product.system.device": device,
        "ro.product.system.manufacturer": maker,
        "ro.product.system.model": model,
        "ro.product.system.name": product,
        "ro.system.product.cpu.abilist": abi_list,
        "ro.system.product.cpu.abilist64": abi_list,
        "ro.system.build.fingerprint": fingerprint,
        "ro.system.build.id": build_id,
        "ro.system.build.tags": release_tags,
        "ro.system.build.type": release_type,
        "ro.system.build.version.incremental": incremental,
        "ro.build.id": build_id,
        "ro.build.display.id": description,
        "ro.build.version.incremental": incremental,
        "ro.build.type": release_type,
        "ro.build.tags": release_tags,
        "ro.build.flavor": f"{product}-user",
        "ro.product.cpu.abi": "arm64-v8a" if arm_first else "x86_64",
        "ro.build.product": device,
        "ro.build.description": description,
        "ro.debuggable": "0" if mode == "stealth" else "1",
        # Emulator ADB is the host control plane; keep the transport while
        # release-style ro.debuggable=0 prevents adbd root.
        "persist.sys.usb.config": "adb",
    }

    product_values = {
        "ro.product.product.brand": brand,
        "ro.product.product.device": device,
        "ro.product.product.manufacturer": maker,
        "ro.product.product.model": model,
        "ro.product.product.name": product,
        "ro.product.build.fingerprint": fingerprint,
        "ro.product.build.id": build_id,
        "ro.product.build.tags": release_tags,
        "ro.product.build.type": release_type,
        "ro.product.build.version.incremental": incremental,
        "ro.build.characteristics": persona["characteristics"],
    }

    system_ext = {
        "ro.product.system_ext.brand": brand,
        "ro.product.system_ext.device": device,
        "ro.product.system_ext.manufacturer": maker,
        "ro.product.system_ext.model": model,
        "ro.product.system_ext.name": product,
        "ro.system_ext.build.fingerprint": fingerprint,
        "ro.system_ext.build.id": build_id,
        "ro.system_ext.build.tags": release_tags,
        "ro.system_ext.build.type": release_type,
        "ro.system_ext.build.version.incremental": incremental,
        # Keep authenticated ADB in both lanes. Compatibility mode remains
        # root-capable because ro.debuggable=1, but it does not disable transport auth.
        "ro.adb.secure": "1",
    }

    vendor = {
        "ro.product.vendor.brand": brand,
        "ro.product.vendor.device": device,
        "ro.product.vendor.manufacturer": maker,
        "ro.product.vendor.model": model,
        "ro.product.vendor.name": product,
        "ro.vendor.product.cpu.abilist": abi_list,
        "ro.vendor.product.cpu.abilist64": abi_list,
        "ro.vendor.build.fingerprint": fingerprint,
        "ro.vendor.build.id": build_id,
        "ro.vendor.build.tags": release_tags,
        "ro.vendor.build.type": release_type,
        "ro.vendor.build.version.incremental": incremental,
        "ro.product.first_api_level": persona["firstApiLevel"],
        "ro.product.board": persona["board"],
        "ro.board.platform": persona["boardPlatform"],
        "ro.soc.manufacturer": persona["socManufacturer"],
        "ro.soc.model": persona["socModel"],
    }

    odm = {
        "ro.product.odm.brand": brand,
        "ro.product.odm.device": device,
        "ro.product.odm.manufacturer": maker,
        "ro.product.odm.model": model,
        "ro.product.odm.name": product,
        "ro.odm.product.cpu.abilist": abi_list,
        "ro.odm.product.cpu.abilist64": abi_list,
        "ro.odm.build.fingerprint": fingerprint,
        "ro.odm.build.id": build_id,
        "ro.odm.build.tags": release_tags,
        "ro.odm.build.type": release_type,
        "ro.odm.build.version.incremental": incremental,
    }
    return {"system": system, "product": product_values, "system_ext": system_ext, "vendor": vendor, "odm": odm}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--persona", required=True, type=Path)
    parser.add_argument("--input-dir", required=True, type=Path)
    parser.add_argument("--output-dir", required=True, type=Path)
    parser.add_argument("--mode", choices=("compatibility", "stealth"), default="compatibility")
    args = parser.parse_args()

    persona = json.loads(args.persona.read_text(encoding="utf-8"))
    if persona.get("schema") != 1:
        raise SystemExit("Unsupported persona schema")
    values = build_values(persona, args.mode)
    args.output_dir.mkdir(parents=True, exist_ok=True)
    manifest = {"persona": persona["id"], "mode": args.mode, "files": {}}
    for name in FILES:
        source = args.input_dir / f"{name}.build.prop"
        target = args.output_dir / f"{name}.build.prop"
        if not source.is_file():
            raise SystemExit(f"Missing input: {source}")
        target.write_bytes(source.read_bytes())
        replace_properties(target, values[name])
        manifest["files"][name] = {"path": str(target), "properties": values[name]}
    (args.output_dir / "persona-patch-manifest.json").write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8"
    )
    print(json.dumps({"persona": persona["id"], "mode": args.mode, "files": len(FILES)}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
