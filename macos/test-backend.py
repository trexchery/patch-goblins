"""Run a deterministic shard of the real shared backend suite on a native runner."""

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--shard", type=int, required=True)
    parser.add_argument("--count", type=int, default=4)
    parser.add_argument("--list-only", action="store_true")
    args = parser.parse_args()
    if not 0 <= args.shard < args.count:
        parser.error("shard must be between zero and count minus one")
    root = Path(__file__).resolve().parent.parent
    project = root / "dock/PatchGoblins.Backend.Tests/PatchGoblins.Backend.Tests.csproj"
    subprocess.run(["dotnet", "build", str(project), "--nologo"], cwd=root, check=True)
    listing = subprocess.check_output([
        "dotnet", "test", str(project), "--no-build", "--list-tests", "--nologo",
    ], cwd=root, text=True)
    methods = sorted({line.strip().split("(")[0] for line in listing.splitlines()
                      if line.startswith("    PatchGoblins.Tests.")})
    if not methods or any(not re.fullmatch(r"[A-Za-z0-9_.+]+", name) for name in methods):
        raise RuntimeError("Backend discovery did not return valid fully qualified test methods")
    selected = methods[args.shard::args.count]
    if not selected:
        raise RuntimeError("The selected backend test shard is empty")
    results = root / "TestResults"
    results.mkdir(exist_ok=True)
    (results / f"shard-{args.shard}.json").write_text(json.dumps({
        "commit": os.environ.get("GITHUB_SHA"), "shard": args.shard, "count": args.count,
        "discovered": len(methods), "selected": selected,
    }, indent=2) + "\n")
    print(f"Shard {args.shard + 1}/{args.count}: {len(selected)} of {len(methods)} methods", flush=True)
    if args.list_only:
        return 0
    return subprocess.run([
        "dotnet", "test", str(project), "--no-build", "--nologo",
        "--filter", "|".join("FullyQualifiedName=" + name for name in selected),
        "--logger", f"trx;LogFileName=macos-backend-{args.shard}.trx",
        "--results-directory", str(results),
    ], cwd=root).returncode


if __name__ == "__main__":
    sys.exit(main())
