namespace RequestBatcher.Tests.Diagnostics;

// ActivitySource listeners are process-wide; keep tracing probes away from unrelated context-sensitive tests.
[CollectionDefinition("Tracing", DisableParallelization = true)]
public sealed class TracingCollection;
