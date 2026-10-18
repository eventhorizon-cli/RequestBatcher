# Complete error-accounting results

Times are BenchmarkDotNet mean ± its reported Error column. Allocation includes
the full measured operation. Before and after use the same units in each row.

## Primitive, before then after

| Case | Items | Before | After | Allocated before / after |
| --- | ---: | ---: | ---: | ---: |
| Success | 10 | 59.84 ns ± 0.843 ns | 52.36 ns ± 0.543 ns | 144 B / 120 B |
| Repeated same exception | 10 | 422.39 ns ± 6.588 ns | 228.26 ns ± 1.079 ns | 920 B / 552 B |
| Alternating exception references | 10 | 472.69 ns ± 4.948 ns | 405.99 ns ± 1.997 ns | 1032 B / 1008 B |
| Distinct exception references | 10 | 936.25 ns ± 24.360 ns | 896.26 ns ± 6.713 ns | 2728 B / 2704 B |
| Concurrent repeated same exception | 10 | 3,273.09 ns ± 455.490 ns | 2,884.29 ns ± 126.574 ns | 3322 B / 2915 B |
| Concurrent distinct exception references | 10 | 3,823.64 ns ± 83.708 ns | 4,113.07 ns ± 494.638 ns | 5222 B / 5194 B |
| Success | 1000 | 3,947.77 ns ± 41.717 ns | 3,840.44 ns ± 33.165 ns | 144 B / 120 B |
| Alternating exception references | 1000 | 18,549.26 ns ± 128.920 ns | 12,674.99 ns ± 52.704 ns | 1032 B / 1008 B |
| Repeated same exception | 1000 | 18,664.59 ns ± 216.529 ns | 6,505.96 ns ± 53.913 ns | 920 B / 552 B |
| Distinct exception references | 1000 | 61,936.70 ns ± 877.186 ns | 63,993.12 ns ± 1,665.335 ns | 178944 B / 178920 B |
| Concurrent repeated same exception | 1000 | 349,805.78 ns ± 52,017.248 ns | 72,995.75 ns ± 8,721.270 ns | 5337 B / 4646 B |
| Concurrent distinct exception references | 1000 | 384,414.34 ns ± 44,050.194 ns | 398,748.37 ns ± 69,196.036 ns | 183384 B / 183814 B |

## Public pipeline, before then after

| Case | Items | Before | After | Allocated before / after |
| --- | ---: | ---: | ---: | ---: |
| Explicit group success | 10 | 9.111 μs ± 0.2140 μs | 9.043 μs ± 0.5847 μs | 4.71 KB / 4.69 KB |
| Explicit groups with capacity waiting | 10 | 22.034 μs ± 0.3420 μs | 23.044 μs ± 1.7391 μs | 8.05 KB / 7.98 KB |
| Mixed single requests and one explicit group | 10 | 26.904 μs ± 1.1164 μs | 28.309 μs ± 1.6762 μs | 10.49 KB / 10.28 KB |
| Explicit group partial caller cancellation | 10 | 44.515 μs ± 0.5251 μs | 44.176 μs ± 0.4179 μs | 8.69 KB / 8.67 KB |
| Explicit group success | 1000 | 173.695 μs ± 7.8214 μs | 164.406 μs ± 5.4118 μs | 164.18 KB / 164.17 KB |
| Explicit group observed handler failure | 10 | 236.378 μs ± 0.6937 μs | 234.227 μs ± 0.9097 μs | 10.21 KB / 9.85 KB |
| Explicit groups with capacity waiting | 1000 | 358.564 μs ± 17.9490 μs | 351.072 μs ± 6.0914 μs | 331.08 KB / 331.03 KB |
| Explicit group partial caller cancellation | 1000 | 575.103 μs ± 5.2879 μs | 669.879 μs ± 106.0438 μs | 526.5 KB / 526.5 KB |
| Mixed single requests and one explicit group | 1000 | 866.625 μs ± 28.9081 μs | 919.949 μs ± 34.8885 μs | 431.99 KB / 432.1 KB |
| Explicit group observed handler failure | 1000 | 1,729.552 μs ± 17.9831 μs | 1,720.174 μs ± 12.5714 μs | 197.62 KB / 197.26 KB |

## Public pipeline, reverse-order repeat

| Case | Items | Before | After | Allocated before / after |
| --- | ---: | ---: | ---: | ---: |
| Mixed single requests and one explicit group | 10 | 26.95 μs ± 1.376 μs | 27.39 μs ± 1.551 μs | 10.7 KB / 10.83 KB |
| Explicit group partial caller cancellation | 10 | 44.55 μs ± 0.576 μs | 45.40 μs ± 0.634 μs | 8.69 KB / 8.67 KB |
| Explicit group partial caller cancellation | 1000 | 573.57 μs ± 4.836 μs | 578.03 μs ± 7.473 μs | 526.48 KB / 526.48 KB |
| Mixed single requests and one explicit group | 1000 | 861.13 μs ± 24.155 μs | 858.88 μs ± 30.719 μs | 431.57 KB / 431.41 KB |
