#!/usr/bin/env python3
"""Compare immutable Core DLLs without building or replacing the engine/public host."""
import argparse
import hashlib
import json
import math
import pathlib
import shutil
import statistics
import subprocess
import time


def summarize(items):
    latencies = sorted(item["elapsed"] for item in items)
    return {
        "meanMilliseconds": statistics.mean(latencies),
        "medianMilliseconds": statistics.median(latencies),
        "p95Milliseconds": latencies[math.ceil(0.95 * len(latencies)) - 1],
        "meanAllocatedBytes": statistics.mean(item["bytes"] for item in items),
        "meanNodes": statistics.mean(item["Nodes"] for item in items),
        "minimumDepth": min(item["CompletedDepth"] for item in items),
        "maximumDepth": max(item["CompletedDepth"] for item in items),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--before-core", type=pathlib.Path, required=True)
    parser.add_argument("--after-core", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--samples", type=int, default=5)
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    if args.samples < 1:
        parser.error("--samples must be positive")
    project = pathlib.Path(__file__).resolve().with_name("SearchBenchmark.csproj")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    engines = {}
    hashes = {}
    for name, source in [("before", args.before_core), ("after", args.after_core)]:
        source = source.resolve()
        if not source.is_file():
            parser.error(f"Core DLL does not exist: {source}")
        directory = output / name
        directory.mkdir(exist_ok=True)
        copied = directory / "StratJamAI.Core.dll"
        if source != copied:
            shutil.copy2(source, copied)
        pdb = source.with_suffix(".pdb")
        if pdb.is_file() and pdb != copied.with_suffix(".pdb"):
            shutil.copy2(pdb, copied.with_suffix(".pdb"))
        hashes[name] = hashlib.sha256(copied.read_bytes()).hexdigest()
        artifacts = directory / "artifacts"
        subprocess.run([
            args.dotnet, "build", str(project), "-c", "Release", "--nologo",
            "--artifacts-path", str(artifacts), f"-p:CoreAssemblyPath={copied}",
            "-p:AllowMissingPrunePackageData=true",
        ], check=True)
        engines[name] = artifacts / "bin" / "SearchBenchmark" / "release" / "SearchBenchmark.dll"

    rows = []
    started = time.monotonic()
    for trial in range(args.samples):
        order = ["before", "after"] if trial % 2 == 0 else ["after", "before"]
        for engine in order:
            for mode in ["fixed", "benchmark"]:
                run = subprocess.run(
                    [args.dotnet, str(engines[engine]), mode, "1"],
                    capture_output=True, text=True, check=True,
                )
                (output / f"{trial}-{engine}-{mode}.log").write_text(run.stderr)
                for row in json.loads(run.stdout):
                    row.update(engine=engine, trial=trial)
                    rows.append(row)
                (output / "rows.json").write_text(json.dumps(rows, indent=2) + "\n")
                print(f"trial={trial} engine={engine} mode={mode} "
                      f"elapsed={time.monotonic() - started:.1f}s", flush=True)

    fixed = []
    timed = []
    for position in ["opening", "middle", "late"]:
        def select(engine, kind, budget=None):
            return [row for row in rows if row["engine"] == engine
                    and row["position"] == position and row["kind"] == kind
                    and (budget is None or row["budget"] == budget)]

        before, after = select("before", "fixed"), select("after", "fixed")
        for row in before + after:
            if (row["CompletedDepth"] != before[0]["CompletedDepth"]
                    or row["Action"] != before[0]["Action"]
                    or abs(row["Value"] - before[0]["Value"]) >= 1e-12):
                raise RuntimeError(f"Fixed-depth result changed for {position}: {row}")
            if row["CompletedDepth"] != row["requestedDepth"]:
                raise RuntimeError(f"Fixed-depth search failed to finish: {row}")
        original, optimized = summarize(before), summarize(after)
        fixed.append({
            "position": position, "depth": before[0]["CompletedDepth"],
            "before": original, "after": optimized,
            "meanSpeedup": original["meanMilliseconds"] / optimized["meanMilliseconds"],
            "action": before[0]["Action"], "value": before[0]["Value"],
            "beforePv": before[0]["PV"], "afterPv": after[0]["PV"],
        })
        for budget in [5, 50, 1000]:
            timed.append({
                "position": position, "budgetMilliseconds": budget,
                "before": summarize(select("before", "budget", budget)),
                "after": summarize(select("after", "budget", budget)),
            })
    report = {
        "samples": args.samples, "seed": 72891, "elapsedSeconds": time.monotonic() - started,
        "hashes": hashes, "fixedDepth": fixed, "timedSearch": timed, "rawSamples": rows,
    }
    (output / "report.json").write_text(json.dumps(report, indent=2) + "\n")
    for item in fixed:
        print(f"{item['position']}: {item['meanSpeedup']:.2f}x same-depth speedup")
    print(f"Report: {output / 'report.json'}")


if __name__ == "__main__":
    main()
