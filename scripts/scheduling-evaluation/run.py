#!/usr/bin/env python3
"""Compare scheduling policies in a temporary source copy; never patch the checkout."""
import argparse
import json
import os
from pathlib import Path
import random
import shutil
import statistics
import subprocess
import tempfile


def replace_once(path, before, after):
    text = path.read_text()
    if text.count(before) != 1:
        raise RuntimeError(f"Expected exactly one patch location in {path}")
    path.write_text(text.replace(before, after))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--prepare-only", action="store_true")
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[2]
    scratch = Path(tempfile.mkdtemp(prefix="requestbatcher-scheduling-"))
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    for name in ["global.json", "Directory.Build.props", ".editorconfig"]:
        shutil.copy2(repo / name, scratch / name)
    shutil.copytree(repo / "src/RequestBatcher", scratch / "src/RequestBatcher",
                    ignore=shutil.ignore_patterns("bin", "obj"))
    replace_once(scratch / "src/RequestBatcher/Scheduling/BatchDispatchLoop.cs",
                 "    private readonly RequestBatchConsumer<TRequest> _batchConsumer;",
                 "    internal static bool EvaluationOffload;\n    private readonly RequestBatchConsumer<TRequest> _batchConsumer;")
    replace_once(scratch / "src/RequestBatcher/Scheduling/BatchDispatchLoop.cs",
                 "        _ = ProcessBatchAsync(pendingRequests, cancellationToken);",
                 "        _ = EvaluationOffload\n"
                 "            ? Task.Run(() => ProcessBatchAsync(pendingRequests, cancellationToken))\n"
                 "            : ProcessBatchAsync(pendingRequests, cancellationToken);")
    replace_once(scratch / "src/RequestBatcher/RequestBatcherServiceCollectionExtensions.cs",
                 "public static class RequestBatcherServiceCollectionExtensions\n{",
                 "public static class RequestBatcherServiceCollectionExtensions\n{\n    internal static int? EvaluationPartitions;")
    replace_once(scratch / "src/RequestBatcher/RequestBatcherServiceCollectionExtensions.cs",
                 "topic.PartitionNumber = GetPartitionCount(",
                 "topic.PartitionNumber = EvaluationPartitions ?? GetPartitionCount(")
    probe = scratch / "Probe"
    probe.mkdir()
    shutil.copy2(Path(__file__).with_name("Program.cs"), probe / "Program.cs")
    (probe / "Probe.csproj").write_text('''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>RequestBatcher.Benchmarks</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../src/RequestBatcher/RequestBatcher.csproj" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.11" />
  </ItemGroup>
</Project>
''')
    env = os.environ.copy()
    # Apply the same JIT policy to all variants to reduce short-run tiering drift.
    env["DOTNET_TieredCompilation"] = "0"
    subprocess.run(["dotnet", "build", "Probe/Probe.csproj", "-c", "Release", "--nologo"],
                   cwd=scratch, env=env, check=True)
    (output / "environment.json").write_text(json.dumps({
        "Scratch": str(scratch), "TieredCompilation": False,
        "SourceCommit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repo, text=True).strip(),
        "SourceDiff": subprocess.check_output(["git", "diff", "--", "src/RequestBatcher"], cwd=repo, text=True),
        "Sdk": subprocess.check_output(["dotnet", "--version"], cwd=scratch, text=True).strip(),
    }, indent=2) + "\n")
    print(f"Isolated source: {scratch}", flush=True)
    if args.prepare_only:
        return
    cases = []
    for kind in ["fast", "io", "cpu", "blocking"]:
        for batch in [1, 64]:
            count = {"fast": 262144, "io": 512 * batch,
                     "cpu": 2048 * batch, "blocking": 512 * batch}[kind]
            for offload in [False, True]:
                cases.append([kind, batch, 8, 8, offload, 8, "none", count])
    for concurrency in [1, 64]:
        for kind in ["fast", "io", "blocking"]:
            for offload in [False, True]:
                cases.append([kind, 1, concurrency, min(concurrency, 8), offload, 8, "none",
                              262144 if kind == "fast" else 512])
    for keys, producers, kind in [("none", 1, "fast"), ("none", 8, "fast"),
                                  ("uniform", 8, "fast"), ("skew", 8, "fast"),
                                  ("none", 8, "io"), ("uniform", 8, "io"), ("skew", 8, "io")]:
        for partitions in [1, 8]:
            case = [kind, 64, 8, partitions, False, producers, keys,
                    262144 if kind == "fast" else 32768]
            if case not in cases:
                cases.append(case)
    random.Random(7824).shuffle(cases)
    dll = probe / "bin/Release/net8.0/RequestBatcher.Benchmarks.dll"
    with (output / "measurements.jsonl").open("w") as stream:
        for index, case in enumerate(cases):
            result = subprocess.run(["dotnet", str(dll), *map(str, case)],
                                    cwd=scratch, env=env, text=True, capture_output=True, timeout=180)
            if result.returncode:
                raise RuntimeError(f"{case}\n{result.stdout}\n{result.stderr}")
            row = json.loads(result.stdout)
            stream.write(json.dumps(row) + "\n")
            stream.flush()
            rate = statistics.median(x["RequestsPerSecond"] for x in row["Measurements"])
            print(f"{index + 1}/{len(cases)} {case}: {rate:.0f} requests/s", flush=True)
    with (output / "bursts.jsonl").open("w") as stream:
        for count in [64, 2048]:
            for keys in ["none", "uniform", "skew"]:
                for partitions in [1, 8]:
                    case = ["burst", 64, 8, partitions, False, 1, keys, count]
                    result = subprocess.run(["dotnet", str(dll), *map(str, case)],
                                            cwd=scratch, env=env, text=True, capture_output=True, timeout=30)
                    if result.returncode:
                        raise RuntimeError(f"{case}\n{result.stdout}\n{result.stderr}")
                    json.loads(result.stdout)
                    stream.write(result.stdout)
                    stream.flush()
                    print(result.stdout.strip(), flush=True)


if __name__ == "__main__":
    main()
