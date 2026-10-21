# Dispatch scheduling with external asynchronous I/O

Evaluated 2026-10-09 with BufferQueue 2.0.3, SDK 10.0.100, .NET 8.0.25, Arm64,
12 visible processors. A separate loopback Kestrel process supplies HTTP/1.1 operations;
the configured delays below are server waits, not measured client call durations.

Both variants use one unkeyed partition, batch size 32, capacity 8,192 and a 1,024-item
producer window. Direct execution is compared with `Task.Run` after acquiring an execution
slot. Reused HTTP connections are prewarmed; tiering is disabled and ThreadPool minimums unchanged.
Each scenario has three client launches, each warming 4,096 items then measuring 3 × 8,192.
Tables report the median of launch medians; all requests and server item/call counts are validated.

## Individual-submission results

`H/C` means handler slots / HTTP connection limit. Throughput counts logical requests.
CPU variants use 10 ms HTTP delay and 250,000 (small) or 2,000,000 (large) dependent operations.

| Handler | H/C | Direct req/s | Offload req/s | Ratio | End-to-end p99 ms, direct / offload | Mean batch, direct / offload |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Async HTTP 1 ms | 8/8 | 158,758 | 141,111 | 0.89x | 6.0 / 7.1 | 31.0 / 25.9 |
| Async HTTP 10 ms | 8/8 | 17,711 | 17,178 | 0.97x | 56.8 / 62.3 | 31.3 / 26.9 |
| Async HTTP 10 ms | 32/8 | 17,649 | 11,445 | 0.65x | 57.4 / 100.1 | 31.0 / 18.2 |
| Async HTTP 10 ms | 32/32 | 44,709 | 42,760 | 0.96x | 22.4 / 24.7 | 31.0 / 17.0 |
| Async HTTP 50 ms | 8/8 | 4,179 | 3,880 | 0.93x | 258.7 / 268.2 | 31.4 / 27.7 |
| Synchronous HTTP 10 ms | 8/8 | 2,547 | 16,953 | 6.66x | 406.8 / 63.4 | 31.3 / 27.3 |
| Synchronous HTTP 10 ms | 32/32 | 2,537 | 15,811 | 6.23x | 401.4 / 66.7 | 31.3 / 18.2 |
| Small CPU before HTTP | 8/8 | 17,683 | 16,443 | 0.93x | 57.3 / 65.0 | 31.3 / 26.8 |
| Large CPU before HTTP | 8/8 | 14,176 | 13,576 | 0.96x | 71.9 / 77.2 | 31.3 / 26.0 |
| Large CPU before HTTP | 32/32 | 14,185 | 30,894 | 2.18x | 71.8 / 34.8 | 31.3 / 18.4 |
| Large CPU after HTTP | 8/8 | 14,008 | 13,937 | 0.99x | 73.9 / 75.3 | 31.1 / 26.0 |
| Large CPU after HTTP | 32/32 | 33,946 | 29,295 | 0.86x | 31.0 / 35.6 | 31.0 / 17.1 |

The large synchronous prefix can prevent filling available execution slots: at 32 slots,
offloading improves this workload 2.18×. Moving the same computation after the incomplete
HTTP await permits overlap already, and offloading is slower. Blocking HTTP benefits from
leaving the dispatch path but still occupies ThreadPool workers.

**Batch formation differs between modes.** These are pipeline comparisons, not isolated
`Task.Run` costs; smaller batches mean more downstream calls and more per-batch CPU work.

## Explicit-submission control

Another 30 launches submit each 1,024-item window through the batch API. Both modes produce
full 32-item handler batches in this experiment; explicit submission does not guarantee that boundary.

| HTTP delay | H/C | Direct req/s | Offload req/s | Ratio | Direct launch range | Offload launch range |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 ms | 8/8 | 175,995 | 176,806 | 1.00x | 175,229–176,348 | 176,790–180,036 |
| 10 ms | 8/8 | 21,440 | 21,484 | 1.00x | 21,383–21,554 | 21,151–21,744 |
| 10 ms | 32/8 | 21,370 | 21,532 | 1.01x | 21,312–21,426 | 21,054–21,616 |
| 10 ms | 32/32 | 87,278 | 85,716 | 0.98x | 86,619–89,195 | 85,435–86,203 |
| 50 ms | 8/8 | 4,865 | 4,857 | 1.00x | 4,836–4,870 | 4,840–4,859 |

With equal full batches, offload/direct ratios are 0.98–1.01: **no substantial pure-I/O gain**.
The connection-limited ratio recovers from 0.65 to 1.01, so the earlier regression must not
be presented as scheduling-hop cost alone. Whole-submission completion times from this
control are not comparable to individual-request p99 above.

## Limits and reproduction

Keep direct dispatch for asynchronous I/O; tune batch size, concurrency and downstream limits
together. The performance-only offload prototype does not validate production cancellation,
scope lifetime or shutdown semantics. Synchronous work before the first incomplete await
requires separate measurement before choosing a scheduling policy.

All 102 launches / 306 measured iterations validated 2,506,752 logical requests. These are
finite producer windows over local HTTP, not saturation or production p99 tests; they omit
TLS, remote jitter, database locks, retries and rate limits. See the
[single-call follow-up](../external-io-latency/README.md) for measured HTTP durations.

```bash
dotnet run --file scripts/scheduling-evaluation/EvaluateHttpScheduling.cs -- --output /private/tmp/http-scheduling-evaluation
dotnet run --file scripts/scheduling-evaluation/EvaluateHttpScheduling.cs -- --batch-control --output /private/tmp/http-batch-control
```

The [probe instructions](../../../scripts/scheduling-evaluation/README.io.md) define metrics
and setup. A local HTTP listener is required; the runner stops it automatically.

## Observability

The recorded HTTP scheduling comparison does not include the optional `RequestBatcher.OpenTelemetry` instrumentation.
See the [observability design](../../observability-design.md) for the production trace model.
