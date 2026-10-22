namespace RequestBatcher.Observability.IntegrationTests;

internal sealed record TempoSpan(
    string ServiceName,
    string ScopeName,
    string Name,
    string TraceId,
    string SpanId,
    string ParentSpanId,
    string Kind,
    long StartTimeUnixNano,
    long EndTimeUnixNano,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<(string TraceId, string SpanId)> Links,
    bool IsError);
