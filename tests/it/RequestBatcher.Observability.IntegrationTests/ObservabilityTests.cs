using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace RequestBatcher.Observability.IntegrationTests;

public sealed class ObservabilityTests(ITestOutputHelper output, ObservabilityFixture fixture) : IClassFixture<ObservabilityFixture>
{
    private const string DelegateBatcherName = "pricing-queries";
    private const string OrdinaryBatcherName = "pricing-queries-ordinary";
    private static readonly TimeSpan _telemetryTimeout = TimeSpan.FromSeconds(120);

    [LiveFact]
    public async Task Quote_ConcurrentOrdinaryAndDelegateSubmissions_ExportsLinkedTracesAndMetrics()
    {
        using var cart = new HttpClient
        {
            BaseAddress = fixture.CartUrl,
            Timeout = TimeSpan.FromSeconds(30)
        };
        using var grafana = new HttpClient
        {
            BaseAddress = fixture.GrafanaUrl,
            Timeout = TimeSpan.FromSeconds(30)
        };
        const string credentials = "admin:admin";
        grafana.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        using var health = await cart.GetAsync("/health");
        health.EnsureSuccessStatusCode();
        var tempo = new TempoClient(grafana);
        await RunScenarioAsync(cart, grafana, tempo, "/cart/quote", DelegateBatcherName, "delegate");
        await RunScenarioAsync(cart, grafana, tempo, "/cart/quote/ordinary", OrdinaryBatcherName, "ordinary");
    }

    private async Task RunScenarioAsync(
        HttpClient cart,
        HttpClient grafana,
        TempoClient tempo,
        string endpoint,
        string batcherName,
        string apiStyle)
    {
        var baseline = await ReadMetricsAsync(grafana, batcherName);
        var submittedItems = 0;
        var submittedCalls = 0;
        var completedCallerIds = new HashSet<string>();

        // Fresh IDs in a second burst catch accidental reuse of an earlier caller's context.
        for (var round = 0; round < 2; round++)
        {
            var probes = Enumerable.Range(0, 32).Select(index => CreateProbe(index, round)).ToArray();
            submittedItems += probes.Sum(probe => probe.Items.Length);
            submittedCalls += probes.Length;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var submissions = probes.Select(async probe =>
            {
                await gate.Task;
                await SubmitAsync(cart, endpoint, probe);
            }).ToArray();
            gate.SetResult();
            await Task.WhenAll(submissions);

            await AssertEventuallyAsync(async cancellationToken =>
            {
                var callers = await Task.WhenAll(probes.Select(async probe =>
                    (Probe: probe, Spans: await tempo.GetTraceAsync(probe.TraceId, cancellationToken))));
                var executions = callers.Select(caller =>
                    AssertCaller(caller.Probe, caller.Spans, endpoint, batcherName)).ToArray();
                // Use exported timestamps to avoid host/container clock differences. Tempo excludes very recent
                // data from search, so leave enough lookback for its end-time clamp to preserve a valid range.
                var start = DateTimeOffset.FromUnixTimeMilliseconds(executions.Min(span => span.StartTimeUnixNano) / 1_000_000)
                    .AddMinutes(-2);
                var end = DateTimeOffset.FromUnixTimeMilliseconds(executions.Max(span => span.EndTimeUnixNano) / 1_000_000)
                    .AddSeconds(5);
                var batchIds = await tempo.FindBatchTracesAsync(
                    probes.Select(probe => probe.TraceId).ToArray(), batcherName, start, end, cancellationToken);
                Assert.NotEmpty(batchIds);
                var batches = await Task.WhenAll(batchIds.Select(id => tempo.GetTraceAsync(id, cancellationToken)));
                var roots = batches.Select(spans => AssertBatch(spans, batcherName)).ToArray();
                var batchContexts = roots
                    .Select(root => (root.TraceId, root.SpanId))
                    .ToHashSet();

                foreach (var execute in executions)
                {
                    var executeContext = (execute.TraceId, execute.SpanId);
                    var expectedBatchContexts = roots
                        .Where(root => root.Links.Contains(executeContext))
                        .Select(root => (root.TraceId, root.SpanId))
                        .ToHashSet();
                    Assert.NotEmpty(expectedBatchContexts);
                    Assert.Equal(execute.Links.Count, execute.Links.Distinct().Count());
                    Assert.True(expectedBatchContexts.SetEquals(execute.Links),
                        $"Execute {execute.TraceId}/{execute.SpanId} links to unrelated or missing batches. " +
                        $"Expected {string.Join(',', expectedBatchContexts.Select(context => $"{context.TraceId}/{context.SpanId}"))}; " +
                        $"actual {string.Join(',', execute.Links.Select(context => $"{context.TraceId}/{context.SpanId}"))}.");
                    Assert.All(execute.Links, link => Assert.Contains(link, batchContexts));
                    Assert.True(roots.Any(root => root.Links.Contains(executeContext)),
                        $"No Handle link to Execute {execute.TraceId}/{execute.SpanId}. " +
                        $"Searched {start:O} to {end:O}; found batches: {string.Join(',', batchIds)}.");
                }

                var callerIds = probes.Select(probe => probe.TraceId).ToHashSet();
                var callerContexts = executions.Select(execute => (execute.TraceId, execute.SpanId)).ToHashSet();
                Assert.True(roots.Any(root => root.Links.Where(link => callerIds.Contains(link.TraceId))
                        .Select(link => link.TraceId).Distinct().Count() > 1),
                    "The concurrent burst did not produce a batch linked to multiple Cart requests.");
                foreach (var root in roots)
                {
                    Assert.DoesNotContain(root.TraceId, callerIds);
                    Assert.DoesNotContain(root.Links, link => completedCallerIds.Contains(link.TraceId));
                    Assert.All(root.Links.Where(link => callerIds.Contains(link.TraceId)),
                        link => Assert.Contains(link, callerContexts));
                }

                output.WriteLine($"{apiStyle} round {round + 1}: {probes.Length} Cart traces, {roots.Length} linked batches; " +
                    $"batch sizes {string.Join(',', roots.Select(root => root.Attributes["requestbatcher.batch.size"]))}.");
                output.WriteLine($"Caller: {probes[0].TraceId}; batch: {roots[0].TraceId}");
            });
            completedCallerIds.UnionWith(probes.Select(probe => probe.TraceId));
        }

        await AssertEventuallyAsync(async cancellationToken =>
        {
            var current = await ReadMetricsAsync(grafana, batcherName, cancellationToken);
            foreach (var metric in new[] { "requestbatcher_batch_size_sum", "requestbatcher_submission_size_sum" })
            {
                var actual = current.GetValueOrDefault(metric) - baseline.GetValueOrDefault(metric);
                Assert.True(actual == submittedItems,
                    $"{metric} delta was {actual}; expected {submittedItems} submitted product items.");
            }

            foreach (var metric in new[]
                     {
                         "requestbatcher_submission_size_count",
                         "requestbatcher_execute_duration_milliseconds_count"
                     })
            {
                var actual = current.GetValueOrDefault(metric) - baseline.GetValueOrDefault(metric);
                Assert.True(actual == submittedCalls,
                    $"{metric} delta was {actual}; expected exactly {submittedCalls} calls for {batcherName}.");
            }

            foreach (var metric in new[] { "requestbatcher_batch_size_count",
                         "requestbatcher_handle_duration_milliseconds_count", "requestbatcher_handle_duration_milliseconds_sum",
                         "requestbatcher_execute_duration_milliseconds_sum" })
            {
                Assert.True(current.GetValueOrDefault(metric) > baseline.GetValueOrDefault(metric),
                    $"{metric} has not advanced after the requests.");
            }
        });
        output.WriteLine($"{apiStyle}: all four histograms exported; {submittedCalls} calls and " +
            $"{submittedItems} product items completed for {batcherName}.");
    }

    private static Probe CreateProbe(int index, int round)
    {
        var random = new Random(round * 100 + index);
        var items = Enumerable.Range(0, 2 + index % 9)
            .Select(_ => new CartItem(random.Next(1, 101), random.Next(1, 4))).ToArray();
        // Every request contains a duplicate, exercising response ordering and deduplication fan-out.
        items[^1] = new CartItem(items[0].ProductId, 3);
        return new Probe(ActivityTraceId.CreateRandom().ToHexString(), ActivitySpanId.CreateRandom().ToHexString(), items);
    }

    private static async Task SubmitAsync(HttpClient cart, string endpoint, Probe probe)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(probe.Items)
        };
        request.Headers.Add("traceparent", $"00-{probe.TraceId}-{probe.ParentSpanId}-01");
        using var response = await cart.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Cart returned {(int)response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        var lines = document.RootElement.GetProperty("lines").EnumerateArray().ToArray();
        Assert.Equal(probe.Items.Length, lines.Length);
        var total = 0m;
        for (var index = 0; index < lines.Length; index++)
        {
            var item = probe.Items[index];
            var line = lines[index];
            Assert.Equal(item.ProductId, line.GetProperty("productId").GetInt32());
            Assert.Equal(item.Quantity, line.GetProperty("quantity").GetInt32());
            Assert.Equal(item.ProductId * 1.25m, line.GetProperty("unitPrice").GetDecimal());
            var expected = item.ProductId * 1.25m * item.Quantity;
            Assert.Equal(expected, line.GetProperty("lineTotal").GetDecimal());
            total += expected;
        }

        Assert.Equal(total, document.RootElement.GetProperty("total").GetDecimal());
    }

    private static TempoSpan AssertCaller(
        Probe probe,
        IReadOnlyList<TempoSpan> spans,
        string endpoint,
        string batcherName)
    {
        Assert.True(spans.Count >= 2, $"Caller trace {probe.TraceId} has not been completely exported.");
        var http = Assert.Single(spans, span => span.Name == $"POST {endpoint}");
        var execute = Assert.Single(spans, span =>
            span.Name.StartsWith("RequestBatcher.Execute ", StringComparison.Ordinal));
        Assert.Equal($"RequestBatcher.Execute {batcherName}", execute.Name);
        Assert.Equal(probe.ParentSpanId, http.ParentSpanId);
        Assert.Equal(http.SpanId, execute.ParentSpanId);
        Assert.Equal(probe.TraceId, execute.TraceId);
        Assert.Equal("cart", execute.ServiceName);
        Assert.Equal(batcherName, execute.Attributes["requestbatcher.name"]);
        Assert.Equal(nameof(Int32), execute.Attributes["requestbatcher.request.type"]);
        Assert.Equal("200", http.Attributes["http.response.status_code"]);
        Assert.InRange(execute.StartTimeUnixNano, http.StartTimeUnixNano, http.EndTimeUnixNano);
        Assert.InRange(execute.EndTimeUnixNano, execute.StartTimeUnixNano + 1, http.EndTimeUnixNano);
        Assert.DoesNotContain(spans, span => span.Name == $"RequestBatcher.Handle {batcherName}" || span.IsError);
        return execute;
    }

    private static TempoSpan AssertBatch(IReadOnlyList<TempoSpan> spans, string batcherName)
    {
        var root = Assert.Single(spans, span => span.Name == $"RequestBatcher.Handle {batcherName}");
        Assert.Equal("cart", root.ServiceName);
        Assert.Equal("SPAN_KIND_CONSUMER", root.Kind);
        Assert.Empty(root.ParentSpanId);
        Assert.Equal(batcherName, root.Attributes["requestbatcher.name"]);
        Assert.Equal(nameof(Int32), root.Attributes["requestbatcher.request.type"]);
        var size = int.Parse(root.Attributes["requestbatcher.batch.size"], CultureInfo.InvariantCulture);
        Assert.InRange(size, 1, 20);
        Assert.InRange(root.Links.Count, 1, size);
        Assert.Equal(root.Links.Count, root.Links.Distinct().Count());
        Assert.All(root.Links, link => Assert.NotEqual(root.TraceId, link.TraceId));
        var client = Assert.Single(spans, span => span.ParentSpanId == root.SpanId &&
            span.ServiceName == "cart" && span.Kind == "SPAN_KIND_CLIENT");
        var server = Assert.Single(spans, span => span.ParentSpanId == client.SpanId &&
            span.ServiceName == "pricing" && span.Kind == "SPAN_KIND_SERVER");
        var database = Assert.Single(spans, span => span.ParentSpanId == server.SpanId && span.ScopeName == "Npgsql" &&
            span.Attributes.ContainsKey("db.query.text"));
        Assert.Equal("SPAN_KIND_CLIENT", database.Kind);
        Assert.False(string.IsNullOrWhiteSpace(database.Attributes["db.query.text"]));
        Assert.Equal("200", client.Attributes["http.response.status_code"]);
        Assert.Equal("200", server.Attributes["http.response.status_code"]);
        Assert.All(spans, span =>
        {
            Assert.Equal(root.TraceId, span.TraceId);
            Assert.False(span.IsError, $"Error span: {span.TraceId}/{span.SpanId} {span.Name}");
        });
        Assert.DoesNotContain(spans, span => span.ServiceName == "pricing" &&
            span.Name.StartsWith("RequestBatcher.", StringComparison.Ordinal));
        return root;
    }

    private static async Task<Dictionary<string, double>> ReadMetricsAsync(
        HttpClient grafana,
        string batcherName,
        CancellationToken cancellationToken = default)
    {
        var query = "sum by (__name__) ({__name__=~\"requestbatcher_(batch_size_(sum|count)|" +
            "submission_size_(sum|count)|handle_duration_milliseconds_(sum|count)|" +
            "execute_duration_milliseconds_(sum|count))\"," +
            $"requestbatcher_name=\"{batcherName}\",requestbatcher_request_type=\"Int32\",service_name=\"cart\"}})";
        using var response = await grafana.GetAsync("/api/datasources/proxy/uid/requestbatcher-prometheus/api/v1/query?query=" +
            Uri.EscapeDataString(query), cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal("success", document.RootElement.GetProperty("status").GetString());
        return document.RootElement.GetProperty("data").GetProperty("result").EnumerateArray().ToDictionary(
            series => series.GetProperty("metric").GetProperty("__name__").GetString()!,
            series => double.Parse(series.GetProperty("value")[1].GetString()!, CultureInfo.InvariantCulture));
    }

    private static async Task AssertEventuallyAsync(Func<CancellationToken, Task> assertion)
    {
        using var timeout = new CancellationTokenSource(_telemetryTimeout);
        Exception? lastFailure = null;
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                await assertion(timeout.Token);
                return;
            }
            catch (XunitException exception)
            {
                lastFailure = exception;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }
        }

        throw new XunitException($"Telemetry did not satisfy the contract within {_telemetryTimeout}. Last failure: {lastFailure}");
    }

    private sealed record CartItem(int ProductId, int Quantity);
    private sealed record Probe(string TraceId, string ParentSpanId, CartItem[] Items);
}
