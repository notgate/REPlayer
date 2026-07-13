#!/usr/bin/env python3
"""Fail-closed release-tree audit for secrets, private keys, and oversized Git blobs."""
from __future__ import annotations

import argparse
import json
import re
import subprocess
from dataclasses import asdict, dataclass
from pathlib import Path

MAX_SOURCE_BYTES = 10 * 1024 * 1024
FORBIDDEN_SUFFIXES = {".apk", ".idsig", ".iso", ".keystore", ".p12", ".pfx", ".qcow2", ".vhd", ".vhdx", ".dmp"}
FORBIDDEN_NAMES = {".env", "id_dsa", "id_ecdsa", "id_ed25519", "id_rsa"}
PATTERNS = {
    "private-key": re.compile(rb"-----BEGIN (?:RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY-----"),
    "github-token": re.compile(rb"\bgh[psour]_[A-Za-z0-9]{30,}\b"),
    "aws-access-key": re.compile(rb"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b"),
    "google-api-key": re.compile(rb"\bAIza[0-9A-Za-z_-]{35}\b"),
    "slack-token": re.compile(rb"\bxox[baprs]-[A-Za-z0-9-]{10,}\b"),
    "generic-secret": re.compile(rb"(?i)(?:api[_-]?key|client[_-]?secret|access[_-]?token|password)\s*[:=]\s*[\"']?[A-Za-z0-9_./+\-=]{20,}"),
}


@dataclass(frozen=True)
class Finding:
    scope: str
    path: str
    check: str
    detail: str
    object_id: str | None = None
    line: int | None = None


def run(root: Path, *arguments: str, binary: bool = False) -> str | bytes:
    completed = subprocess.run(arguments, cwd=root, capture_output=True, check=True)
    return completed.stdout if binary else completed.stdout.decode(errors="replace")


def scan_content(scope: str, path: str, data: bytes, object_id: str | None = None) -> list[Finding]:
    if b"\0" in data[:4096]:
        return []
    findings: list[Finding] = []
    for name, pattern in PATTERNS.items():
        for match in pattern.finditer(data):
            findings.append(Finding(scope, path, name, "credential-like material", object_id, data.count(b"\n", 0, match.start()) + 1))
    return findings


def candidate_paths(root: Path) -> list[Path]:
    output = run(root, "git", "ls-files", "--cached", "--others", "--exclude-standard", "-z", binary=True)
    assert isinstance(output, bytes)
    return sorted({root / raw.decode(errors="surrogateescape") for raw in output.split(b"\0") if raw})


def audit_tree(root: Path) -> tuple[list[Finding], dict[str, int]]:
    findings: list[Finding] = []
    scanned = 0
    for path in candidate_paths(root):
        if not path.is_file():
            continue
        scanned += 1
        relative = path.relative_to(root).as_posix()
        size = path.stat().st_size
        if path.name.lower() in FORBIDDEN_NAMES or path.suffix.lower() in FORBIDDEN_SUFFIXES:
            findings.append(Finding("tree", relative, "forbidden-artifact", f"{size} bytes"))
        if size > MAX_SOURCE_BYTES:
            findings.append(Finding("tree", relative, "large-file", f"{size} bytes exceeds {MAX_SOURCE_BYTES}"))
            continue
        findings.extend(scan_content("tree", relative, path.read_bytes()))
    return findings, {"files": scanned}


def history_objects(root: Path) -> list[tuple[str, int, str]]:
    listing = run(root, "git", "rev-list", "--objects", "--all")
    assert isinstance(listing, str)
    rows: list[tuple[str, str]] = []
    for line in listing.splitlines():
        object_id, _, path = line.partition(" ")
        rows.append((object_id, path))
    batch = "".join(object_id + "\n" for object_id, _ in rows).encode()
    completed = subprocess.run(
        ["git", "cat-file", "--batch-check=%(objecttype) %(objectname) %(objectsize)"],
        cwd=root,
        input=batch,
        capture_output=True,
        check=True,
    )
    metadata = completed.stdout.decode().splitlines()
    blobs: list[tuple[str, int, str]] = []
    for (_, path), line in zip(rows, metadata, strict=True):
        kind, object_id, size = line.split()
        if kind == "blob":
            blobs.append((object_id, int(size), path or "<unnamed>"))
    return blobs


def audit_history(root: Path) -> tuple[list[Finding], dict[str, int]]:
    findings: list[Finding] = []
    blobs = history_objects(root)
    scanned_content = 0
    for object_id, size, path in blobs:
        suffix = Path(path).suffix.lower()
        if Path(path).name.lower() in FORBIDDEN_NAMES or suffix in FORBIDDEN_SUFFIXES:
            findings.append(Finding("history", path, "forbidden-artifact", f"{size} bytes", object_id))
        if size > MAX_SOURCE_BYTES:
            findings.append(Finding("history", path, "large-blob", f"{size} bytes exceeds {MAX_SOURCE_BYTES}", object_id))
            continue
        data = run(root, "git", "cat-file", "blob", object_id, binary=True)
        assert isinstance(data, bytes)
        scanned_content += 1
        findings.extend(scan_content("history", path, data, object_id))
    return findings, {"blobs": len(blobs), "contentScanned": scanned_content}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[3])
    parser.add_argument("--history", action="store_true")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    root = args.root.resolve()
    findings, tree_counts = audit_tree(root)
    history_counts: dict[str, int] = {}
    if args.history:
        history_findings, history_counts = audit_history(root)
        findings.extend(history_findings)
    report = {
        "schema": 1,
        "verdict": "PASS" if not findings else "FAIL",
        "maximumSourceBytes": MAX_SOURCE_BYTES,
        "tree": tree_counts,
        "history": history_counts,
        "findings": [asdict(finding) for finding in findings],
    }
    rendered = json.dumps(report, indent=2) + "\n"
    if args.output:
        output = args.output if args.output.is_absolute() else root / args.output
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(rendered, encoding="utf-8")
    print(rendered, end="")
    return 0 if not findings else 1


if __name__ == "__main__":
    raise SystemExit(main())
