# Historical Parallel Dispatch Benchmark

These measurements predate the unkeyed single-partition policy. They compare
the previous consumer topology and do not define the current runtime contract.

## Benchmark Method

The rate benchmark fixes `BatchSize = 100`, uses paced single-request
submissions for three seconds, and varies `TargetQps` over 100, 1,000, and
5,000 and `MaxConcurrency` over 1, 4, 16, and 64. Direct PostgreSQL SELECTs
use the same request stream, connection-pool limit, and result validation.

Both runs use:

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \\
  --configuration Release --no-build --no-restore -- \\
  --filter '*PostgreSqlPriceQueryRateBenchmarks*' \\
  --warmupCount 1 --iterationCount 3 --invocationCount 1 --unrollFactor 1 \\
  --stopOnFirstError --disableLogFile
```

The mean is elapsed time for the fixed three-second workload, and `Allocated`
is managed allocation for one benchmark invocation. It is not process RSS.

### Before Implementation

Environment: macOS Sequoia 15.7.9, Apple M2 Max (12 logical cores), .NET 8.0.25
host, PostgreSQL 17.6 container, BenchmarkDotNet 0.15.8. The baseline was
re-run from an isolated clone of the pre-implementation commit. It used the
old manual-commit topology where partitions and pull consumers both equalled
`MaxConcurrency`.

| Target QPS | Max concurrency | Direct mean | Direct allocated | Direct GC (G0/G1/G2) | Batcher mean | Batcher allocated | Batcher GC (G0/G1/G2) |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 100 | 1 | 3.008 s | 770.16 KB | 0 / 0 / 0 | 3.010 s | 1,586.59 KB | 0 / 0 / 0 |
| 100 | 4 | 3.004 s | 771.18 KB | 0 / 0 / 0 | 3.010 s | 1,717.61 KB | 0 / 0 / 0 |
| 100 | 16 | 3.008 s | 769.69 KB | 0 / 0 / 0 | 3.009 s | 1,718.78 KB | 0 / 0 / 0 |
| 100 | 64 | 3.007 s | 768.27 KB | 0 / 0 / 0 | 3.007 s | 1,711.16 KB | 0 / 0 / 0 |
| 1,000 | 1 | 3.006 s | 7,474.01 KB | 0 / 0 / 0 | 3.005 s | 2,522.81 KB | 0 / 0 / 0 |
| 1,000 | 4 | 3.008 s | 7,622.41 KB | 0 / 0 / 0 | 3.005 s | 6,745.22 KB | 0 / 0 / 0 |
| 1,000 | 16 | 3.011 s | 8,115.38 KB | 1,000 / 0 / 0 | 3.009 s | 16,906.44 KB | 2,000 / 1,000 / 0 |
| 1,000 | 64 | 3.008 s | 8,134.66 KB | 1,000 / 0 / 0 | 3.008 s | 16,929.46 KB | 2,000 / 1,000 / 0 |
| 5,000 | 1 | 3.009 s | 36,944.58 KB | 4,000 / 1,000 / 0 | 3.005 s | 7,612.33 KB | 0 / 0 / 0 |
| 5,000 | 4 | 3.007 s | 37,165.25 KB | 4,000 / 1,000 / 0 | 3.008 s | 12,513.35 KB | 1,000 / 0 / 0 |
| 5,000 | 16 | 3.011 s | 39,320.76 KB | 4,000 / 1,000 / 0 | 3.007 s | 30,191.54 KB | 3,000 / 1,000 / 0 |
| 5,000 | 64 | 3.058 s | 41,144.11 KB | 5,000 / 1,000 / 0 | 3.114 s | 82,641.92 KB | 10,000 / 3,000 / 0 |

GC cells record G0/G1/G2 collections per 1,000 benchmark operations in that
order. BenchmarkDotNet omits a generation column when every measurement is
zero, so omitted values are recorded explicitly as `0` above. At 5,000 QPS and
`MaxConcurrency = 64`, the batcher allocated approximately 83 MB per
invocation and collected G0/G1 10,000/3,000 times. This is the primary
memory-pressure case the redesigned topology must remeasure.

### After Implementation

The identical command completed all 24 direct and RequestBatcher benchmark
cases. Every benchmark invocation validated its query results, and all
temporary PostgreSQL containers were removed after the run.

| Target QPS | Max concurrency | Direct mean | Direct allocated | Direct GC (G0/G1/G2) | Batcher mean | Batcher allocated | Batcher GC (G0/G1/G2) |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 100 | 1 | 3.008 s | 759.14 KB | 0 / 0 / 0 | 3.008 s | 1,449.26 KB | 0 / 0 / 0 |
| 100 | 4 | 3.009 s | 760.14 KB | 0 / 0 / 0 | 3.006 s | 1,532.16 KB | 0 / 0 / 0 |
| 100 | 16 | 3.006 s | 760.08 KB | 0 / 0 / 0 | 3.007 s | 1,548.80 KB | 0 / 0 / 0 |
| 100 | 64 | 3.008 s | 769.38 KB | 0 / 0 / 0 | 3.009 s | 1,543.40 KB | 0 / 0 / 0 |
| 1,000 | 1 | 3.011 s | 7,375.47 KB | 0 / 0 / 0 | 3.006 s | 2,474.04 KB | 0 / 0 / 0 |
| 1,000 | 4 | 3.009 s | 7,558.59 KB | 0 / 0 / 0 | 3.006 s | 5,767.06 KB | 0 / 0 / 0 |
| 1,000 | 16 | 3.007 s | 8,167.21 KB | 1,000 / 0 / 0 | 3.007 s | 14,698.63 KB | 1,000 / 0 / 0 |
| 1,000 | 64 | 3.009 s | 8,163.22 KB | 1,000 / 0 / 0 | 3.005 s | 14,738.99 KB | 1,000 / 0 / 0 |
| 5,000 | 1 | 3.011 s | 37,074.77 KB | 4,000 / 1,000 / 0 | 3.010 s | 7,686.80 KB | 0 / 0 / 0 |
| 5,000 | 4 | 3.010 s | 37,096.82 KB | 4,000 / 1,000 / 0 | 3.005 s | 10,824.47 KB | 1,000 / 0 / 0 |
| 5,000 | 16 | 3.007 s | 38,161.20 KB | 4,000 / 1,000 / 0 | 3.010 s | 20,384.21 KB | 2,000 / 1,000 / 0 |
| 5,000 | 64 | 3.041 s | 40,934.16 KB | 5,000 / 1,000 / 0 | 3.011 s | 20,354.11 KB | 2,000 / 1,000 / 0 |

The fixed three-second workload remains throughput-bound, so elapsed means are
all approximately three seconds. The redesign substantially lowers managed
allocation and GC pressure at high concurrency: at 5,000 QPS and
`MaxConcurrency = 64`, the batcher drops from 82,641.92 KB before the change
to 20,354.11 KB after it (-75.4%), while G0/G1 falls from 10,000/3,000 to
2,000/1,000. At 5,000 QPS and `MaxConcurrency = 16`, allocation drops from
30,191.54 KB to 20,384.21 KB (-32.5%) and G0 falls from 3,000 to 2,000.
