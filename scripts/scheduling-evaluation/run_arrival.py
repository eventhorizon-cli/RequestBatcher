#!/usr/bin/env python3
"""Run open-loop scheduling diagnostics against an isolated source copy."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import random
import shutil
import subprocess
import tempfile


SNAPSHOT_ROOTS = (
    Path("src/RequestBatcher"),
    Path("src/RequestBatcher.Analyzers"),
    Path("tests/RequestBatcher.Benchmarks"),
)
SNAPSHOT_FILES = (Path("global.json"), Path("Directory.Build.props"), Path(".editorconfig"))


def snapshot_manifest(root):
    manifest = {}
    for relative_file in SNAPSHOT_FILES:
        path = root / relative_file
        if path.is_file():
            manifest[relative_file.as_posix()] = hashlib.sha256(path.read_bytes()).hexdigest()

    for relative_root in SNAPSHOT_ROOTS:
        base = root / relative_root
        for path in sorted(base.rglob("*")):
            if not path.is_file() or any(part in ("bin", "obj") for part in path.parts):
                continue
            relative = path.relative_to(root).as_posix()
            manifest[relative] = hashlib.sha256(path.read_bytes()).hexdigest()
    return manifest


def manifest_hash(manifest):
    digest = hashlib.sha256()
    for relative in sorted(manifest):
        digest.update(relative.encode())
        digest.update(b"\0")
        digest.update(manifest[relative].encode())
        digest.update(b"\n")
    return digest.hexdigest()


def manifest_diff_hash(before, after):
    digest = hashlib.sha256()
    for relative in sorted(set(before) | set(after)):
        old_hash = before.get(relative, "")
        new_hash = after.get(relative, "")
        if old_hash == new_hash:
            continue
        digest.update(relative.encode())
        digest.update(b"\0")
        digest.update(old_hash.encode())
        digest.update(b"\0")
        digest.update(new_hash.encode())
        digest.update(b"\n")
    return digest.hexdigest()


def replace_once(path, before, after):
    text = path.read_text()
    if text.count(before) != 1:
        raise RuntimeError(f"Expected exactly one patch location in {path}")
    path.write_text(text.replace(before, after))


def patch_evaluation_hooks(scratch):
    loop = scratch / "src/RequestBatcher/Scheduling/BatchDispatchLoop.cs"
    replace_once(
        loop,
        "    private readonly RequestBatchConsumer<TRequest> _batchConsumer;",
        """    internal static bool EvaluationOffload;
    internal static Action? EvaluationExecutionStarted;
    internal static Action? EvaluationExecutionFinished;
    internal static Action<int>? EvaluationBatchStarted;
    private readonly RequestBatchConsumer<TRequest> _batchConsumer;""",
    )
    replace_once(
        loop,
        """        TrackExecution();
        var processing = _batchConsumer.ExecuteAsync(pendingRequests, cancellationToken);""",
        """        TrackExecution();
        if (EvaluationOffload)
        {
            _ = ProcessOffloadedBatchAsync(pendingRequests, cancellationToken);
            return;
        }

        var processing = _batchConsumer.ExecuteAsync(pendingRequests, cancellationToken);""",
    )
    replace_once(
        loop,
        "    private async Task ProcessBatchAsync(ValueTask processing)",
        """    private async Task ProcessOffloadedBatchAsync(
        IReadOnlyList<PendingBatchRequest<TRequest>> pendingRequests,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(
                async () => await _batchConsumer.ExecuteAsync(pendingRequests, cancellationToken)
                    .ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (Interlocked.CompareExchange(ref _executionFailure, exception, null) is null)
            {
                _executionFailureCancellation.Cancel();
            }

        }
        finally
        {
            CompleteExecution();
            _executionSlots.Release();
        }
    }

    private async Task ProcessBatchAsync(ValueTask processing)""",
    )
    replace_once(
        loop,
        """            _inFlightCount++;
        }
    }

    private void CompleteExecution()""",
        """            _inFlightCount++;
        }

        EvaluationExecutionStarted?.Invoke();
    }

    private void CompleteExecution()""",
    )
    replace_once(
        loop,
        """        drained?.TrySetResult();
    }

    private static TaskCompletionSource CreateCompletedSource()""",
        """        drained?.TrySetResult();
        EvaluationExecutionFinished?.Invoke();
    }

    private static TaskCompletionSource CreateCompletedSource()""",
    )

    consumer = scratch / "src/RequestBatcher/Scheduling/RequestBatchConsumer.cs"
    replace_once(
        consumer,
        """            logger.BatchStarted(requestTypeName, requests.Length);
            await handler(requests, cancellationToken).ConfigureAwait(false);""",
        """            logger.BatchStarted(requestTypeName, requests.Length);
            BatchDispatchLoop<TRequest>.EvaluationBatchStarted?.Invoke(requests.Length);
            await handler(requests, cancellationToken).ConfigureAwait(false);""",
    )


def prepare_copy(repo):
    scratch = Path(tempfile.mkdtemp(prefix="requestbatcher-arrival-")).resolve()
    for name in ("global.json", "Directory.Build.props", ".editorconfig"):
        shutil.copy2(repo / name, scratch / name)
    ignore = shutil.ignore_patterns("bin", "obj")
    shutil.copytree(repo / "src/RequestBatcher", scratch / "src/RequestBatcher", ignore=ignore)
    shutil.copytree(repo / "src/RequestBatcher.Analyzers", scratch / "src/RequestBatcher.Analyzers", ignore=ignore)
    shutil.copytree(repo / "tests/RequestBatcher.Benchmarks", scratch / "tests/RequestBatcher.Benchmarks", ignore=ignore)
    patch_evaluation_hooks(scratch)
    return scratch


def run_case(dll, settings, scratch, env, timeout):
    result = subprocess.run(
        ["dotnet", str(dll), "--scheduling-probe", json.dumps(settings)],
        cwd=scratch,
        env=env,
        text=True,
        capture_output=True,
        timeout=timeout,
    )
    if result.returncode:
        raise RuntimeError(f"{settings}\n{result.stdout}\n{result.stderr}")
    lines = [line for line in result.stdout.splitlines() if line.strip()]
    if len(lines) != 1:
        raise RuntimeError(f"Expected one JSON result for {settings}, got:\n{result.stdout}")
    return json.loads(lines[0])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--prepare-only", action="store_true")
    parser.add_argument("--smoke", action="store_true")
    parser.add_argument("--workloads", nargs="+", default=["fast", "io", "cpu", "blocking"])
    parser.add_argument("--rates", nargs="+", type=float, default=[1_000, 10_000])
    parser.add_argument("--batch-sizes", nargs="+", type=int, default=[1, 64])
    parser.add_argument("--duration-ms", type=int, default=750)
    parser.add_argument("--max-items", type=int, default=20_000)
    parser.add_argument("--warmup-items", type=int, default=256)
    parser.add_argument("--repeats", type=int, default=1)
    parser.add_argument("--database", choices=["none", "postgres", "redis"], default="none")
    parser.add_argument("--database-operation", choices=["write", "read"], default="write")
    parser.add_argument("--timeout-seconds", type=int, default=180)
    args = parser.parse_args()
    if any(rate <= 0 for rate in args.rates):
        parser.error("--rates must be positive")
    if any(batch <= 0 for batch in args.batch_sizes):
        parser.error("--batch-sizes must be positive")
    if args.duration_ms < 0 or args.max_items <= 0 or args.warmup_items < 0:
        parser.error("duration, max-items, and warmup-items are invalid")
    if args.repeats <= 0:
        parser.error("--repeats must be positive")
    if args.smoke:
        args.workloads = ["fast"]
        args.rates = [1_000]
        args.batch_sizes = [1]
        args.duration_ms = 0
        args.max_items = 128
        args.warmup_items = 16
        args.repeats = 1
        args.database = "none"

    repo = Path(__file__).resolve().parents[2]
    source_manifest = snapshot_manifest(repo)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    scratch = prepare_copy(repo)
    scratch_manifest = snapshot_manifest(scratch)
    env = os.environ.copy()
    env["DOTNET_TieredCompilation"] = "0"
    project = scratch / "tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj"
    subprocess.run(["dotnet", "build", str(project), "-c", "Release", "--nologo"], cwd=scratch, env=env, check=True)
    working_tree_status = subprocess.check_output(
        ["git", "status", "--porcelain"], cwd=repo, text=True
    ).splitlines()
    environment = {
        "Scratch": str(scratch),
        "SourceCommit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repo, text=True).strip(),
        "SourceSnapshotSha256": manifest_hash(source_manifest),
        "ScratchSnapshotSha256": manifest_hash(scratch_manifest),
        "PrototypeDiffSha256": manifest_diff_hash(source_manifest, scratch_manifest),
        "RunnerSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "WorkingTreeDirty": bool(working_tree_status),
        "WorkingTreeStatus": working_tree_status,
        "TieredCompilation": False,
        "MaxConcurrency": 8,
        "OnePartition": True,
        "Database": args.database,
        "DatabaseOperation": args.database_operation,
        "Isolation": "Copied src, analyzer, and benchmark project; hooks patched only in scratch",
    }
    (output / "environment.json").write_text(json.dumps(environment, indent=2) + "\n")
    print(f"Isolated source: {scratch}", flush=True)
    if args.prepare_only:
        return

    workloads = [args.database] if args.database != "none" else args.workloads
    cases = [
        {
            "Workload": workload,
            "DatabaseOperation": args.database_operation,
            "IntendedRate": rate,
            "DurationMilliseconds": args.duration_ms,
            "MaxItems": args.max_items,
            "WarmupItems": args.warmup_items,
            "BatchSize": batch_size,
            "MaxConcurrency": 8,
            "Offload": offload,
        }
        for workload in workloads
        for rate in args.rates
        for batch_size in args.batch_sizes
        for offload in (False, True)
        for _ in range(args.repeats)
    ]
    random.Random(5187).shuffle(cases)
    dll = project.parent / "bin/Release/net8.0/RequestBatcher.Benchmarks.dll"
    with (output / "measurements.jsonl").open("w") as stream:
        for index, settings in enumerate(cases, start=1):
            row = run_case(dll, settings, scratch, env, args.timeout_seconds)
            stream.write(json.dumps(row) + "\n")
            stream.flush()
            print(
                f"{index}/{len(cases)} {settings['Workload']} batch={settings['BatchSize']} "
                f"offload={settings['Offload']} throughput={row['throughput']:.0f}/s",
                flush=True,
            )


if __name__ == "__main__":
    main()
