# Reusing the pending-request completion callback

The coordinator binds `OnRequestFinished` once during construction and shares
that immutable delegate with all of its pending requests. This replaces an
instance-method delegate conversion in both single admission and the explicit
submission loop. Callback ownership, per-item terminal guards, cancellation,
drain, and partition routing remain unchanged. Tasks and pending objects are
not pooled.

There is one retained delegate and one reference field per coordinator. The
benchmarks exclude coordinator construction; the reported savings concern
repeated submissions through a long-lived coordinator.

## Reproduction

The baseline is `5043545883a261f298759731dd1d05da44c251d9`, archived into a clean
directory with only the new explicit-submission benchmark copied in. The
measured candidate is `c4c9c80d7227ac3a3408a66da9f37724f26177f3`. Both versions use
identical benchmark source:

- `ExplicitSubmissionBenchmarks.cs` SHA-256:
  `82b81d93ce24cfb9f12271d52679d038ad919d7c7c27f13117784ba9ce65287f`.
- `ResponseCompletionBenchmarks.cs` SHA-256:
  `cea1504c8cdac7fe6700c1ae03dc3ed96a7e86894a523fd5dfa1fd5be8cb46ca`.

```bash
NuGetAudit=false dotnet run \
  --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*ResponseCompletionBenchmarks*' '*ExplicitSubmissionBenchmarks*' \
  --artifacts /tmp/completion-callback-results
```

Runs are sequential on ARM64 macOS, .NET SDK 10.0.100, .NET runtime 8.0.25,
workstation GC, and BenchmarkDotNet 0.15.8. Processor-model discovery and high
process priority were unavailable in the sandbox. The new .NET 10 package target
is validated separately; these measurements do not compare runtimes.

The explicit-submission suite uses 512 logical requests per operation, group
sizes 16 and 256, handler batch size 32, four partitions, concurrency 8, and
queue capacity 1,024. Mixed waves submit the first 256 requests singly and the
remaining 256 in groups. Every request's processing count must equal one. A
gate holds handlers until all submissions have been issued and the benchmark
has created `Task.WhenAll` for their returned tasks; it then releases the wave.
Input groups and DI setup are outside measurement; gate reset, submission arrays,
enqueueing, dispatch, waiting, and result validation are included. Each case
uses two processes, five warmups, twelve measured iterations, and a 300 ms
iteration target.

The response-completion suite uses 256 requests per wave, batch size 32,
concurrency 8, and queue capacity 512, without a partition key. Its cancellation
case uses concurrency 1 and two additional blocker/drain requests. Errors and
cancellations are observed. Each case uses two processes, eight warmups, twenty
measured iterations, and a 500 ms iteration target.

## Results

Each row compares the same workload before and after caching the delegate.
KB means 1,024 bytes; time and allocation are per complete wave, not per item.

### Explicit submissions: 512 requests per wave

| Workload | Before (us) | After (us) | Time decrease | Before (KB) | After (KB) | Allocation decrease |
|---|---:|---:|---:|---:|---:|---:|
| All explicit, group 256 | 143.40 | 136.60 | 4.7% | 124.70 | 92.76 | 25.6% |
| All explicit, group 16 | 193.20 | 187.00 | 3.2% | 142.39 | 110.43 | 22.4% |
| Half single, group 256 | 285.70 | 260.80 | 8.7% | 155.70 | 124.12 | 20.3% |
| Half single, group 16 | 291.70 | 278.90 | 4.4% | 161.48 | 129.45 | 19.8% |

### Response completion: 256 requests per wave

| Workload | Before (us) | After (us) | Time decrease | Before (KB) | After (KB) | Allocation decrease |
|---|---:|---:|---:|---:|---:|---:|
| Response explicit success | 71.50 | 66.07 | 7.6% | 76.38 | 60.38 | 20.9% |
| Request-only single success | 203.75 | 200.82 | 1.4% | 94.08 | 77.88 | 17.2% |
| Response single success | 211.64 | 202.36 | 4.4% | 111.31 | 95.62 | 14.1% |
| Response pre-dispatch cancellation | 6,365.37 | 6,308.79 | 0.9% | 253.09 | 236.96 | 6.4% |
| Response single failure | 7,425.80 | 7,420.46 | 0.1% | 571.95 | 555.56 | 2.9% |

The explicit suite saves 31.58–32.03 KB per 512 requests, approximately
64 bytes per pending item. Pure explicit submissions allocate 22.4–25.6% less;
mixed waves allocate about 20% less. The response suite saves roughly 16 KB per
256 requests, including request-only, failure, and cancellation paths. Small
variation around that amount comes from scheduling-dependent pipeline work.
The cancellation workload includes its two extra requests.

The stable finding is removal of a per-item delegate allocation. Observed time
decreases range from 0.1% to 8.7%. Failure timing is effectively unchanged, and
the request-only single success and group-16 cases have overlapping 99.9%
confidence intervals. These results do not establish a general throughput gain.

Both runs completed all nine cases with successful process exits and workload
validation. BenchmarkDotNet CSV and Markdown summaries, including confidence
intervals, GC counts, and runtime details, are retained under [before](before/)
and [after](after/).

## Correctness and package validation

After integrating the separate README and multi-targeting changes, the combined
code passes all 116 tests on .NET 8 and all 116 on .NET 10. Solution formatting
verification and the Release build pass with no warnings or errors. Package
inspection confirms DLL/XML assets for both targets, the package README, and
symbols for both targets in the symbol package. Existing regressions cover
success, failure, cancellation, consumer failure, mixed submission capacity,
and concurrent disposal.

## Limits

These are public-API, in-process workloads using singleton handlers. They do not
measure network I/O, scoped handler disposal, capacity-saturated admission,
retained heap size, or per-request tail latency. Gates deliberately make callers
wait; immediately completed submissions are not represented. Timing includes
scheduling and, where relevant, exception observation. Small timing differences
should not be generalized into throughput guarantees.
