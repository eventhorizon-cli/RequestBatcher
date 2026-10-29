# Paced explicit submissions and Handler fan-out

At 500 submissions/s, two Handler slots and batch size 120 reduced downstream call count by **28.6%** and average
downstream concurrency by **26.6–27.2%** relative to direct 20-item bulk queries. Submission P95 increased from
**20.87–21.02 ms to 40.80–40.91 ms**. At 50 submissions/s, every Handler batch contained 20 items, so there was no
downstream-call reduction. These are bounded, simulated-I/O measurements, not a database performance claim.

## Question and workload

This evaluation compares the README's two-layer concurrency example with direct bulk queries under uniform arrivals.
Each caller submits 20 unique items. At 500 submissions/s this is 10,000 items/s; the lower-rate control uses
50 submissions/s (1,000 items/s). All callers receive the same deterministic response for each item in input order.

The direct baseline already uses one bulk query for its 20 items. The RequestBatcher paths use one partition,
`MaxConcurrency = 2`, and `BatchSize = 120`. Each Handler splits its actual input into chunks of at most
30 items, starts their downstream calls in parallel, awaits them all, and assigns responses. There are no duplicate
keys, so no deduplication savings are assumed. The configured batch size is a maximum, not a collection threshold.

Each downstream call uses a real `Task.Delay(20)` and returns transformed item IDs. This exercises the current
RequestBatcher implementation and its public response API, but simulates downstream I/O: it does not measure a
database, network, connection pool, or the extra cost of larger queries. There is no downstream concurrency limiter.
At a full batch, 120 items produce four child calls; two Handler slots allow up to eight calls.

## Measurement contract

- A dedicated producer follows absolute Stopwatch deadlines and submits without waiting for previous responses.
  Late arrivals are recorded rather than silently lowering the target load.
- Submission latency runs from actual API invocation until the entire group's responses are available. A separate
  scheduled-arrival latency includes producer lateness. Percentiles are per submission, not per Handler or item.
- Preparation and warmup are outside measurement. Each measured phase drains all submitted work before validation.
  Time through final completion includes the drain; it is distinct from the configured arrival window.
  Completion timestamps are captured before checking response values, so latency and drain stop at response availability.
- Every returned item is checked against its originating request, including response order and exactly-once processing.
  Active Handler and downstream calls must return to zero. A correctness failure invalidates the run.
- Batch-size and query-size histograms, call counts, actual durations, peak concurrency, and time-weighted average
  downstream concurrency explain batching behavior. Average concurrency is total downstream call duration divided
  by measured elapsed time, including drain.
- First- and last-quarter submission P95 help distinguish a stable queue from accumulating backlog. Process-wide
  allocations and CPU include probe bookkeeping and are not isolated library overhead.

The probe is [`MeasurePacedBatchFanout.cs`](../../../scripts/MeasurePacedBatchFanout.cs). It requires the repository
.NET 10 SDK and targets `net10.0`, with C# 12. Tracing and metrics listeners are not enabled. Queue capacity is
65,536 items and Handler lifetime is singleton.

## Reproduction

Run from the repository root. Each command starts an independent process and writes a JSON summary plus
per-submission samples next to the output file:

The probe accepts `--submission-size` (default 20), `--downstream-batch-size` (30), `--delay-ms` (20), and
`--pending-capacity` (65,536), as well as the rate, duration, warmup, Handler batch size, concurrency, mode, and
output arguments below. Delay is fixed per simulated query. Keep the same downstream settings in both modes;
direct mode also splits a submission if it exceeds the downstream query-size limit, but never combines submissions.
The measurements in this report used the stated defaults before these four constants became CLI options;
the later parameter-validation smoke runs are not substituted for the recorded performance results.

Parameter validation subsequently covered the default configuration, direct 25-item submissions split into
10/10/5-item queries at 7 ms, batcher mode with those query sizes at 50 ms, and zero-delay submissions larger than
queue capacity. All four .NET 10 smoke runs passed response order and exactly-once checks. Their output is separate
from the performance measurements below.

```bash
dotnet run --file scripts/MeasurePacedBatchFanout.cs --configuration Release -- \
  --mode direct --rate 500 --seconds 20 --warmup-seconds 3 \
  --output /private/tmp/paced-fanout/direct-500.json

dotnet run --file scripts/MeasurePacedBatchFanout.cs --configuration Release -- \
  --mode batcher --rate 500 --seconds 20 --warmup-seconds 3 --batch-size 120 --concurrency 2 \
  --output /private/tmp/paced-fanout/batcher-120-500.json

```

Repeat with `--rate 50` for the lower-rate control and use distinct output names for repeated runs. Run cases
sequentially on the same machine; do not run other benchmarks concurrently. Use `--help` for argument validation.

## Results: 2026-10-28

Source: `a680f764e10d3dc59f6c48acc9cee9161b02b81a`, with the accompanying probe and shared statistics utility;
production source was unchanged. Host: macOS 27.2 Arm64, 12 reported logical processors. SDK: 10.0.100;
every measured process reported **.NET 10.0.0, Release**.

Each of the four cases ran twice in a fresh process, with a three-second warmup and 20 seconds of measured arrivals.
Round one ran direct-50, batcher-50, direct-500, batcher-500; round two reversed that order. No cases ran concurrently.
Each low-rate run completed 1,000 submissions / 20,000 items; each high-rate run completed 10,000 submissions /
200,000 items. All **44,000 measured submissions and 880,000 items** passed response, order, and exactly-once checks.

The table shows the range of the two run results; it does not average or pool percentiles. Counts include the final
drain. The direct path already sends 20 items per downstream call; it is not a one-call-per-item comparator.

| Submissions/s | Path | Submission mean (ms) | P95 (ms) | P99 (ms) | Mean Handler items | Downstream calls in 20 s + drain | Average downstream concurrency | Peak downstream concurrency |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 50 | Direct bulk | 21.16–21.30 | 21.64–21.81 | 21.68–21.86 | n/a | 1,000 | 1.058–1.065 | 2 |
| 50 | Batcher, C=2, B=120 | 20.68 | 21.22–21.36 | 21.53–21.76 | 20.00 | 1,000 | 1.032 | 2 |
| 500 | Direct bulk | 20.37–20.51 | 20.87–21.02 | 20.92–21.07 | n/a | 10,000 | 10.17–10.25 | 11–12 |
| 500 | Batcher, C=2, B=120 | 31.39–31.43 | 40.80–40.91 | 41.72–41.74 | 104.71–104.88 | 7,138–7,142 | 7.46–7.47 | 8 |

At high load, the Handler processed 1,907–1,910 batches. About half contained 120 items; the remainder were mostly
80 or 100 items. A 100-item batch still needs four calls (`30 + 30 + 30 + 10`), so the downstream work is not simply
`10,000 / 30` calls per second. Measured downstream call rate was about **356.5/s**, compared with **499.5/s** for
direct queries, using elapsed time through final completion. Both paths queried the same 200,000 items per run.
Fewer calls do not imply 28.6% less database work, since this model charges a fixed delay per query regardless of size.

The actual mean Handler duration at high load was **20.95–20.97 ms**, not exactly 20 ms. Actual downstream call means
were **20.93–20.95 ms** for batching and **20.37–20.51 ms** for direct calls. Timer and scheduling behavior therefore
matter even in this simple model. The small lower-rate latency difference favors batching numerically, but its actual
downstream delay was also lower (20.64 ms versus 21.16–21.30 ms). It is not evidence that queueing makes the same
downstream operation faster; at this rate there was no call-count saving.

### Arrival accuracy and queue stability

Actual submission rates were 50.0002–50.0004/s and 500.0021–500.0028/s. Producer lateness P95 was at most **0.0068 ms**,
and maximum lateness was **1.09 ms** across all runs. Scheduled-arrival P95 and actual-send P95 differed by less than
0.01 ms, so the high-load latency increase was not hidden by a slower producer.

| Batcher at 500 submissions/s | First-quarter P95 (ms) | Last-quarter P95 (ms) | Time from last submission to final response (ms) |
| --- | ---: | ---: | ---: |
| Round 1 | 40.84 | 40.89 | 30.48 |
| Round 2 | 40.77 | 40.83 | 25.80 |

These observations do not show accumulating delay during the measured 20-second windows. They do not mean there is
no queue: submissions wait behind active Handler batches, which accounts for much of the latency increase. No long-tail
faults were injected; this evaluation does not establish recovery time after a downstream stall or a production SLO.

### Allocation and validation

Measured process allocations per item were 55.30 bytes for direct and 481.19–481.21 bytes for batching at 50/s;
at 500/s they were 55.72–55.73 and 450.12–450.55 bytes respectively. These include response objects, diagnostic counters,
timers, and probe bookkeeping; prepared request arrays are excluded. They are not isolated RequestBatcher overhead.
CPU time also includes the dedicated producer's short spin waits and should not be used as a service CPU estimate.

The complete unit-test project passed 483 tests on each of .NET 8 and .NET 10. The probe itself and the shared
statistics checks ran on .NET 10. Statistics checks covered known nearest-rank results, empty and singleton samples,
input-order preservation, and rejection of non-finite values. Formatting and `git diff --check` passed.

## Evidence

The initial eight retained JSON summaries include runtime, configuration, correctness status, histograms, allocation counts,
and arrival/latency statistics:

| Rate | Direct | Batcher |
| ---: | --- | --- |
| 50 | [Round 1](results/measured-r1-direct-50.json), [Round 2](results/measured-r2-direct-50.json) | [Round 1](results/measured-r1-batcher-50.json), [Round 2](results/measured-r2-batcher-50.json) |
| 500 | [Round 1](results/measured-r1-direct-500.json), [Round 2](results/measured-r2-direct-500.json) | [Round 1](results/measured-r1-batcher-500.json), [Round 2](results/measured-r2-batcher-500.json) |

Per-submission CSVs were generated beside the original output files under `/private/tmp/paced-fanout/`; rerunning the
commands regenerates these samples. The compact JSON summaries are retained in the repository. The reusable
[`SampleSummary`](../../../scripts/Common/SampleSummary.cs) owns percentile calculation; the workload owns timing
boundaries and sample units. Script entry points and helpers remain under `scripts/`.

## Follow-up: four Handler slots (2026-10-29)

The same unchanged probe was rerun with `--concurrency 4`, keeping batch size 120, 20 items per submission,
30 items per child query, and the 20 ms simulated delay. High-load controls with two slots were rerun alongside it:
round one used C=2 then C=4; round two used C=4 then C=2. Two additional C=4 runs covered 50 submissions/s.
All six fresh processes reported .NET 10.0.0 Release, with three seconds of warmup and 20 seconds of measured arrivals.
All 42,000 submissions / 840,000 items passed correctness checks. No production or probe code changed between cases.

At **500 submissions/s**, the ranges below are from these fresh matched runs:

| Metric | C=2 | C=4 |
| --- | ---: | ---: |
| Submission mean latency (ms) | 31.45–31.48 | 27.54–27.65 |
| Submission P95 (ms) | 40.90–40.95 | 36.75–36.81 |
| Submission P99 (ms) | 41.79–41.84 | 37.41–38.11 |
| Actual mean Handler batch size | 104.82–104.88 | 52.31–52.34 |
| Downstream calls per run | 7,138–7,142 | 7,647–7,677 |
| Downstream calls/s, including drain | 356.35–356.53 | 381.78–383.51 |
| Average downstream concurrency | 7.458–7.463 | 7.924–7.947 |
| Peak downstream concurrency | 8 | 11 |
| Process allocated bytes/item, including probe | 450.19–450.55 | 462.33–463.35 |

Doubling the Handler limit lowered P95 by about **10% (4.1 ms)**, while increasing downstream call count by
**7.1–7.5%** and average downstream concurrency by **6.2–6.5%**. Relative to the earlier direct bulk-query runs,
C=4 still made about **23% fewer calls**, but did not restore direct-query latency (about 21 ms P95).
This is a measured tradeoff for this workload, not a general recommendation to use four slots.

The C=4 histogram is not a uniform sequence of 52-item batches: roughly half the batches contained 20 items and
about a quarter contained 120 items, with most others containing 40 or 60 items. More slots let some work start
earlier, reducing average batch occupancy, but the remaining larger batches show that queueing and opportunistic
merging still occurred. Mean batch size alone does not describe the tail latency.

First/last-quarter P95 was 36.77/36.71 ms and 36.66/36.57 ms for C=4, versus 40.86/40.94 ms and 40.90/41.03 ms
for C=2. These bounded windows again did not show sustained latency growth. High-load producer lateness P95 was
at most 0.0048 ms and maximum lateness was 2.36 ms; scheduled P95 differed from actual-send P95 by less than
0.01 ms. Actual arrival rate was 500.0029–500.0578 submissions/s.

At **50 submissions/s**, C=4 still processed exactly 20 items per Handler and made 1,000 downstream calls in each
run, with peak downstream concurrency two. P95 was 21.29–21.81 ms. As with C=2 in the original low-rate runs,
there was no additional merging or downstream-call saving. These low-rate C=4 runs were not interleaved with fresh
C=2 controls, so small timing differences should not be interpreted as a concurrency-related speedup or regression.

Reproduce by replacing `--concurrency 2` with `--concurrency 4` in the command above and choosing a fresh output name.
The additional evidence is retained separately from the initial results:

| Rate | Concurrency | Results |
| ---: | ---: | --- |
| 500 | 2 | [Round 1](results/concurrency-r1-c2-500.json), [Round 2](results/concurrency-r2-c2-500.json) |
| 500 | 4 | [Round 1](results/concurrency-r1-c4-500.json), [Round 2](results/concurrency-r2-c4-500.json) |
| 50 | 4 | [Round 1](results/concurrency-r1-c4-50.json), [Round 2](results/concurrency-r2-c4-50.json) |

## Follow-up: batch size 30 without Handler fan-out (2026-10-29)

This follow-up kept 500 submissions/s, 20 items per explicit submission, and the same 20 ms delay, but set
`BatchSize = 30` with `MaxConcurrency = 8` or `16`. The existing Handler fast path directly awaits one query whenever
the actual input contains at most 30 distinct items. Thus **none of these Handler invocations uses `Task.WhenAll`**
or starts parallel child queries. The load generator still submits independently and waits for all submissions at
the end; that is separate from Handler execution.

The unchanged probe ran C=8, C=16, C=16, C=8 sequentially in fresh .NET 10.0.0 Release processes, each with a
three-second warmup and 20-second measured arrival window. All 40,000 submissions / 800,000 items passed response,
order, and exactly-once validation. Handler and downstream call counts were identical in every run.

| Metric at 500 submissions/s | B=30, C=8 | B=30, C=16 |
| --- | ---: | ---: |
| Submission mean latency (ms) | 27.28–27.62 | 19.85–20.10 |
| Submission P95 (ms) | 36.60–36.79 | 20.32–20.58 |
| Submission P99 (ms) | 38.06–38.26 | 20.40–20.64 |
| Actual mean Handler batch size | 26.13–26.17 | 20.00 |
| Downstream calls per run | 7,643–7,655 | 10,000 |
| Downstream calls/s, including drain | 381.59–382.39 | 499.55 |
| Average downstream concurrency | 7.926–7.929 | 9.902–10.029 |
| Peak downstream concurrency | 8 | 12 |
| Process allocated bytes/item, including probe | 433.30–434.22 | 466.62–466.67 |

C=8 reduced call count by **23.45–23.57%** relative to the original direct bulk baseline, but P95 remained around
36.7 ms. C=16 returned latency close to direct bulk queries, while eliminating the additional call-count saving:
almost every 20-item submission ran as its own Handler batch. C=16 is an upper bound, not a claim that 16 calls
were active: measured average downstream concurrency was about ten and the peak was twelve.

Compared with the earlier B=120/C=4 measurements, B=30/C=8 produced similar P95 and downstream call counts while
bounding downstream concurrency at eight rather than the previously observed peak of eleven. This is a comparison
between sequential experiment sets, not a paired run isolating `Task.WhenAll` overhead. Both batch size and Handler
concurrency changed. The direct-await branch removes child-task aggregation, but the measurements do not establish
that removal as the cause of the latency change.

At B=30, an explicit 20-item submission can straddle two Handler batches: a 30-item pull may contain all of one
submission plus ten items of the next. The submission task waits for all twenty results. Reported P95 therefore
includes that completion boundary; it is not merely one Handler's duration.

### Arrival and timing qualifications

The first C=8 run had one notable producer stall: maximum lateness was **39.40 ms** and the largest interarrival gap
was 41.40 ms. It is retained rather than discarded. Producer lateness P95 was only 0.0075 ms and scheduled-arrival
P95 was 36.82 ms versus actual-send P95 of 36.79 ms. The second C=8 run had maximum lateness 2.61 ms and P95
36.60 ms. Their first/last-quarter P95 values were 36.67/36.68 ms and 35.75/36.79 ms. These short runs do not prove
long-term queue stability or resilience to downstream stalls.

C=16 producer lateness P95 was at most 0.0035 ms, maximum lateness was below 0.97 ms, and scheduled P95 differed
from actual-send P95 by less than 0.003 ms. Actual average downstream delays were **19.82–20.08 ms**, versus
**20.74–20.77 ms** for C=8 and **20.37–20.51 ms** in the earlier direct baseline. Runtime timer resolution and
scheduling can make `Task.Delay(20)` observations differ from exactly 20 ms; the lower C=16 result must not be read
as proof that batching speeds up the same downstream query. No real downstream saturation or connection pool was modeled.

Run the existing command with `--batch-size 30 --concurrency 8` or `--batch-size 30 --concurrency 16` and fresh output names.

| Configuration | Results |
| --- | --- |
| B=30, C=8 | [Round 1](results/smallbatch-r1-c8-b30-500.json), [Round 2](results/smallbatch-r2-c8-b30-500.json) |
| B=30, C=16 | [Round 1](results/smallbatch-r1-c16-b30-500.json), [Round 2](results/smallbatch-r2-c16-b30-500.json) |
