# Typed versus delegate handlers

This historical evaluation compared typed handlers with delegate registration and `ExecuteAsync` delegate extensions using equivalent integer transformations in gated in-memory 256-request waves. The original recorded comparison ran on .NET 8.0.25 / Arm64 macOS with batch size 32 and concurrency eight. Results include submission, scheduling, completion, and validation; setup and first delegate binding/compilation were outside measurement. No external service or instrumentation was used.

For explicit-list submissions, the delegate path measured 63.36-63.43 microseconds per wave versus 66.37-71.78 microseconds for typed handlers. Individual submissions were effectively similar: 210.04-212.59 versus 209.75-212.09 microseconds. Allocation was equal without services and slightly lower for scoped service injection in this run. Variability, batch formation, and the limited original matrix mean these measurements do not establish a general speedup or equivalent performance for every workload. The expanded benchmark matrix was not represented in the recorded results.

The reusable source is [DelegateHandlerBenchmarks](../../../tests/RequestBatcher.Benchmarks/InMemory/DelegateHandlerBenchmarks.cs). The separate [batch scope cost](batch-scope/README.md) summary measures the per-batch scope used for service injection.
