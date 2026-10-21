# Single-call latency and dispatch scheduling

Measured 2026-10-10 with SDK 10.0.100 / .NET 8 on Arm64, using a separate loopback HTTP server.
Both dispatch modes use one unkeyed partition, full 32-item handler batches and explicit
1,024-item windows. Each scenario has two launches, 1,024 warmup items and 3 × 2,048 measured
items per launch: 384 HTTP calls per mode/scenario. Tiering is disabled equally.

`HttpCall` includes connection-pool wait, transport, server work and reading/validating the
response. `ServerResidence` measures endpoint time, excluding pool wait and transport.
`HandlerDuration` adds deliberate synchronous computation but excludes pre-handler queueing.
They are not interchangeable with whole-submission completion latency.

## Measured single-call durations

Direct-dispatch results pool 384 calls; throughput is the median of launch medians.
`H/C` means handler slots / connection limit. All durations are milliseconds.

| Requested delay | H/C | HTTP mean | HTTP P50 | HTTP P95 | HTTP P99 | Server mean | Calls/second |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 10 | 8/8 | 11.94 | 12.32 | 13.22 | 13.85 | 11.46 | 656.85 |
| 10 | 32/8 | 30.68 | 26.08 | 51.13 | 51.77 | 11.87 | 644.41 |
| 10 | 32/32 | 12.30 | 12.43 | 13.03 | 13.17 | 11.73 | 2475.68 |
| 100 | 8/8 | 103.21 | 103.34 | 104.56 | 105.29 | 101.80 | 76.97 |
| 100 | 32/8 | 259.40 | 210.85 | 417.97 | 418.63 | 102.16 | 76.63 |
| 100 | 32/32 | 105.05 | 104.18 | 111.13 | 112.74 | 101.99 | 300.65 |
| 500 | 8/8 | 503.26 | 503.40 | 504.32 | 504.65 | 501.86 | 15.87 |
| 500 | 32/8 | 1258.93 | 1008.68 | 2016.08 | 2017.36 | 502.08 | 15.87 |
| 500 | 32/32 | 503.75 | 503.93 | 505.17 | 505.89 | 501.75 | 63.23 |

More handler slots cannot enlarge a connection pool: at H/C=32/8 and 500 ms delay, mean
client call time is 1,258.93 ms while server time remains about 500 ms. Pure asynchronous
cases have offload/direct throughput ratios of 0.97–1.02; no substantial benefit is established.

## Synchronous prefix versus I/O duration

H/C=32/32, a fixed 2,000,000-operation prefix, and full 32-item batches:

| Requested delay | Direct calls/s | Offload calls/s | Offload / direct | HTTP mean, direct / offload | Handler mean, direct / offload |
| --- | ---: | ---: | ---: | ---: | ---: |
| 10 ms | 461.56 | 1813.75 | 3.93x | 10.50 / 12.52 ms | 12.32 / 14.72 ms |
| 100 ms | 194.30 | 262.82 | 1.35x | 100.68 / 104.52 ms | 102.66 / 108.43 ms |
| 500 ms | 54.89 | 61.46 | 1.12x | 501.03 / 504.34 ms | 503.50 / 508.41 ms |

The offload benefit falls as the external wait dominates. These finite windows repeatedly
pay startup/drain costs, so ratios are not steady-state capacity estimates. Keep direct
dispatch for asynchronous I/O; evaluate synchronous prefix cost, actual call duration,
connection limits and batch occupancy together.

## Limits and reproduction

All 48 launches / 144 measured iterations validated 9,216 HTTP calls and 294,912 requests.
Setup, warmup, pre-iteration GC, validation counters and shutdown are outside timing.
Per-iteration P99 is the maximum of only 64 calls; even pooled P99 has only 384 samples.
These controlled loopback results do not predict rare production tails, variable latency,
TLS, retries, rate limits or database locks. One call per handler batch is assumed.

```bash
python3 scripts/scheduling-evaluation/run_io.py \
  --latency-sweep --delays 10 100 500 --launches 2 \
  --measured-requests 2048 --warmup-requests 1024 --iterations 3 \
  --output /private/tmp/http-latency-evaluation
```

See the [probe instructions](../../../scripts/scheduling-evaluation/README.io.md) for timing
boundaries and cache settings, and the [previous comparison](../external-io-scheduling/README.md)
for the individual-submission workloads. The local server stops automatically.

## Observability

This historical HTTP latency evaluation does not measure the optional `RequestBatcher.OpenTelemetry` instrumentation.
See the [observability design](../../observability-design.md) for caller spans, batch links, and downstream children.
