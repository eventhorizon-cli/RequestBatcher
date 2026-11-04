# Dispatch under paced arrivals

This historical prototype compared direct handler invocation with bounded `Task.Run` offloading. It changed no production scheduling behavior. On Arm64 macOS 27.0.1, SDK 10.0.100, and .NET 8.0.25, the comparison covered finite two-second arrival windows, batch limits 1/64, eight execution slots, in-memory handlers, and local PostgreSQL/Redis workloads. Cases validated every response and exactly-once handling; database cases also checked persisted values and counts.

Offloading did not consistently improve time for asynchronous delay or database handlers and added allocation; the results do not support a general latency or throughput claim. It did improve the tested synchronous CPU and blocking prefixes, especially at batch size one, while changing batch formation and downstream call counts. Those specific cases do not establish that offloading is generally beneficial. Generator lag, telemetry, two-run percentiles, local databases, and simulated work limit interpretation; slot utilization includes asynchronous waits and is not CPU utilization. The study does not establish production capacity or tail latency.

The reusable [arrival probe](../../../scripts/scheduling-evaluation/EvaluateArrivalScheduling.cs) and [instructions](../../../scripts/scheduling-evaluation/README.arrival.md) retain reproduction guidance.
