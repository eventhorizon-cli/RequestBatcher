# Open-loop scheduling diagnostic

`run_arrival.py` builds an isolated copy of the current source, analyzer, and
benchmark project. It patches only that temporary copy with an experimental
bounded offload path and telemetry hooks, then runs one JSON-emitting probe
process per case. The checkout's production runtime is not changed.
The temporary checkout is retained so its exact patched source and build output
can be inspected. The runner prints its path and records it in `environment.json`;
remove that directory after inspection, including after a failed campaign.
`--prepare-only` retains the compiled copy without running measurements.

The probe compares direct dispatch with the isolated offload path for `fast`,
`io` (`Task.Delay(1)`), fixed CPU-prefix, and blocking-prefix handlers. The
default matrix uses one partition, eight execution slots, batch limits `1` and
`64`, and two open-loop arrival rates. Each caller submits without awaiting the
previous item. Caller latency includes admission, queueing, dispatch, and
handler completion.
Delay, CPU-prefix, and blocking-prefix work happens once per handler batch.
Direct/offload pairs use the same work, but changing batch size or formation
changes its amortized cost per logical item.

```bash
python3 scripts/scheduling-evaluation/run_arrival.py \
  --output /private/tmp/requestbatcher-arrival --smoke
```

The default matrix is bounded by 32 short cases:

```bash
python3 scripts/scheduling-evaluation/run_arrival.py \
  --output /private/tmp/requestbatcher-arrival
```

Each result records intended and actual issue rate, throughput, caller p50/p95/p99,
active execution-slot wall utilization, peak active slots, batch count and sizes,
allocation and GC deltas, ThreadPool pending/completed deltas, and exactly-once
validation. Database modes are opt-in and use the connection strings in
`REQUEST_BATCHER_POSTGRES_CONNECTION` or `REQUEST_BATCHER_REDIS_CONNECTION`.
Use `--database-operation write|read`; read cases seed their IDs before the
measurement and batch asynchronous reads during the measured interval.
Database schema creation, pool warmup, read seeding, per-run reset, and
persisted-value verification are outside the measured interval. PostgreSQL uses
the sample database on port `54329` by default; Redis defaults to
`127.0.0.1:6379`. Redis verification and cleanup require a single standalone
endpoint; this probe does not support Redis Cluster or multi-endpoint setups.

```bash
python3 scripts/scheduling-evaluation/run_arrival.py \
  --database postgres --rates 100 --batch-sizes 1 8 \
  --output /private/tmp/requestbatcher-arrival-postgres

python3 scripts/scheduling-evaluation/run_arrival.py \
  --database redis --database-operation read --rates 100 \
  --batch-sizes 1 8 --output /private/tmp/requestbatcher-arrival-redis-read
```

The offload path is diagnostic only. It is not a production implementation and
does not establish cancellation or shutdown semantics for a future scheduling
policy. Results are sensitive to runtime, CPU topology, ThreadPool state, and
database service conditions; compare direct/offload pairs from the same host
and source copy. For paced cases, the runner plans
`min(MaxItems, ceil(IntendedRate * DurationMilliseconds / 1000))` items and
submits every planned arrival even when the generator is late. Caller latency
starts at each scheduled due time and therefore includes generator lag;
`ActualIssueRate` uses the actual first and last submission timestamps (and is
zero when only one item is submitted).
Slot utilization includes asynchronous waiting and, in the offload variant,
time waiting for a ThreadPool worker. It is neither CPU utilization nor a count
of handler bodies running simultaneously. ThreadPool deltas are endpoint
counters, not a queue-length trace. Process allocation includes probe overhead;
both variants use the same instrumentation.
Percentiles select sorted latency at zero-based index `ceil(count * p) - 1`.
`ExactOnce` verifies that each logical ID reaches the in-process handler once.
Database checks verify final key/row counts and values; they do not prove
exactly-once execution of external side effects.

## Observability

This historical diagnostic uses temporary probe hooks; its results do not represent runs with the optional
`RequestBatcher.OpenTelemetry` instrumentation enabled. For production tracing, use
`AddRequestBatcherInstrumentation()` and follow the [observability design](../../docs/observability-design.md).
