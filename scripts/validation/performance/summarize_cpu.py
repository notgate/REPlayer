#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import sqlite3
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("--root", required=True)
parser.add_argument("--out", required=True)
args = parser.parse_args()
root = Path(args.root)
lanes = []
for cores in (2, 3, 4):
    db = root / f"finalbenchmark-{cores}vcpu" / "benchmark_database"
    if not db.exists():
        continue
    con = sqlite3.connect(db)
    row = con.execute(
        "select total_score,single_core_score,multi_core_score,detailed_results_json "
        "from benchmark_results where type='CPU' order by id desc limit 1"
    ).fetchone()
    if row is None:
        continue
    details = json.loads(row[3])
    lanes.append({
        "vcpus": cores,
        "normalized_score": row[0],
        "single_core_score": row[1],
        "multi_core_score": row[2],
        "invalid_tests": [item["name"] for item in details if not item["isValid"]],
        "valid_tests": sum(1 for item in details if item["isValid"]),
        "total_tests": len(details),
    })

for lane in lanes:
    base = next((x for x in lanes if x["vcpus"] == 2), None)
    if base:
        lane["multi_vs_2vcpu_percent"] = (lane["multi_core_score"] / base["multi_core_score"] - 1) * 100
        lane["normalized_vs_2vcpu_percent"] = (lane["normalized_score"] / base["normalized_score"] - 1) * 100
best = max(lanes, key=lambda item: item["normalized_score"]) if lanes else None
report = {
    "suite": "FinalBenchmark 2 CPU",
    "reference": {
        "device": "OnePlus Pad 2 / Snapdragon 8 Gen 3",
        "native_reference_score": 100.0,
        "source": "FinalBenchmark-Platform KotlinBenchmarkManager.kt lines 36-49 and README.md lines 90-92",
        "interpretation": "The suite's normalized score is relative to its native Snapdragon 8 Gen 3 reference. It is not a Geekbench score.",
    },
    "claimed_model": "REPlayer Virtual Device",
    "claimed_model_comparison": {
        "status": "not-measured",
        "reason": "The production persona is intentionally neutral and does not claim equivalence to commercial ARM hardware.",
    },
    "lanes": lanes,
    "recommended_vcpus": best["vcpus"] if best else None,
    "limitations": [
        "FinalBenchmark marks matrix multiplication and multi-core Fibonacci invalid in these runs; aggregate scores are retained with validity disclosed.",
        "One run per lane is sufficient for configuration selection, not a confidence interval.",
        "No commercial-device performance equivalence is claimed by the neutral REPlayer persona.",
    ],
}
out = Path(args.out)
out.parent.mkdir(parents=True, exist_ok=True)
out.with_suffix(".json").write_text(json.dumps(report, indent=2), encoding="utf-8")
lines = ["# REPlayer CPU benchmark", "", "| vCPU | Single | Multi | Normalized | vs 2-vCPU multi | Valid |", "|---:|---:|---:|---:|---:|---:|"]
for lane in lanes:
    lines.append(f"| {lane['vcpus']} | {lane['single_core_score']:.4f} | {lane['multi_core_score']:.4f} | {lane['normalized_score']:.4f} | {lane['multi_vs_2vcpu_percent']:+.1f}% | {lane['valid_tests']}/{lane['total_tests']} |")
lines += [
    "", f"**Selected:** {report['recommended_vcpus']} vCPU", "",
    "## Native reference", "",
    "FinalBenchmark 2 normalizes against a native **OnePlus Pad 2 / Snapdragon 8 Gen 3 = 100** reference in its source.",
    "The neutral REPlayer persona does not claim commercial-device equivalence; unrelated benchmark suites are not substituted.",
    "", "## Invalid subtests", "",
]
for lane in lanes:
    lines.append(f"- {lane['vcpus']} vCPU: " + ", ".join(lane["invalid_tests"]))
lines += ["", "## Limitations", ""] + [f"- {x}" for x in report["limitations"]]
out.with_suffix(".md").write_text("\n".join(lines) + "\n", encoding="utf-8")
print(json.dumps({"lanes": len(lanes), "recommended_vcpus": report["recommended_vcpus"]}))
