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

public sealed class ObservabilityTests(ITestOutputHelper output)
{
    private const string ExecuteName = "RequestBatcher.Execute pricing-queries";
    private const string BatchName = "RequestBatcher.HandleBatch pricing-queries";
    private static readonly TimeSpan _telemetryTimeout = TimeSpan.FromSeconds(120);

    [LiveFact]
    public async Task Quote_ConcurrentNativeSubmissions_ExportsLinkedTracesAndMetrics()
    {
        using var cart = new HttpClient
        {
            BaseAddress = new Uri(GetSetting("REQUESTBATCHER_CART_URL", "http://127.0.0.1:5081")),
            Timeout = TimeSpan.FromSeconds(30)
        };
        using var grafana = new HttpClient
        {
            BaseAddress = new Uri(GetSetting("REQUESTBATCHER_GRAFANA_URL", "http://127.0.0.1:3000")),
            Timeout = TimeSpan.FromSeconds(30)
        };
        var credentials = GetSetting("REQUESTBATCHER_GRAFANA_USER", "admin") + ":" +
            GetSetting("REQUESTBATCHER_GRAFANA_PASSWORD", "admin");
        grafana.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        using var health = await cart.GetAsync("/health");
        health.EnsureSuccessStatusCode();
        var tempo = new TempoClient(grafana);
        var baseline = await ReadMetricsAsync(grafana);
        var submittedItems = 0;
        var completedCallerIds = new HashSet<string>();

        // Fresh IDs in a second burst catch accidental reuse of an earlier caller's context.
        for (var round = 0; round < 2; round++)
        {
            var probes = Enumerable.Range(0, 32).Select(index => CreateProbe(index, round)).ToArray();
            submittedItems += probes.Sum(probe => probe.Items.Length);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var submissions = probes.Select(async probe =>
            {
                await gate.Task;
                await SubmitAsync(cart, probe);
            }).ToArray();
            gate.SetResult();
            await Task.WhenAll(submissions);

            await AssertEventuallyAsync(async cancellationToken =>
            {
                var callers = await Task.WhenAll(probes.Select(async probe =>
                    (Probe: probe, Spans: await tempo.GetTraceAsync(probe.TraceId, cancellationToken))));
                var executions = callers.Select(caller => AssertCaller(caller.Probe, caller.Spans)).ToArray();
                // Use exported timestamps to avoid host/container clock differences. Tempo excludes very recent
                // data from search, so leave enough lookback for its end-time clamp to preserve a valid range.
                var start = DateTimeOffset.FromUnixTimeMilliseconds(executions.Min(span => span.StartTimeUnixNano) / 1_000_000)
                    .AddMinutes(-2);
                var end = DateTimeOffset.FromUnixTimeMilliseconds(executions.Max(span => span.EndTimeUnixNano) / 1_000_000)
                    .AddSeconds(5);
                var batchIds = await tempo.FindBatchTracesAsync(
                    probes.Select(probe => probe.TraceId).ToArray(), start, end, cancellationToken);
                Assert.NotEmpty(batchIds);
                var batches = await Task.WhenAll(batchIds.Select(id => tempo.GetTraceAsync(id, cancellationToken)));
                var roots = batches.Select(AssertBatch).ToArray();
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
                        $"No HandleBatch link to Execute {execute.TraceId}/{execute.SpanId}. " +
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

                output.WriteLine($"Round {round + 1}: {probes.Length} Cart traces, {roots.Length} linked batches; " +
                    $"batch sizes {string.Join(',', roots.Select(root => root.Attributes["requestbatcher.batch.size"]))}.");
                output.WriteLine($"Caller: {probes[0].TraceId}; batch: {roots[0].TraceId}");
            });
            completedCallerIds.UnionWith(probes.Select(probe => probe.TraceId));
        }

        await AssertEventuallyAsync(async cancellationToken =>
        {
            var current = await ReadMetricsAsync(grafana, cancellationToken);
            foreach (var metric in new[] { "requestbatcher_requests_submitted_total", "requestbatcher_requests_processed_total",
                         "requestbatcher_batch_size_sum" })
            {
                Assert.True(current.GetValueOrDefault(metric) - baseline.GetValueOrDefault(metric) >= submittedItems,
                    $"{metric} has not increased by the {submittedItems} submitted product items.");
            }

            foreach (var metric in new[] { "requestbatcher_batches_processed_total", "requestbatcher_batch_size_count",
                         "requestbatcher_batch_duration_milliseconds_count", "requestbatcher_batch_duration_milliseconds_sum" })
            {
                Assert.True(current.GetValueOrDefault(metric) > baseline.GetValueOrDefault(metric),
                    $"{metric} has not advanced after the requests.");
            }
        });
        output.WriteLine($"All five RequestBatcher instruments exported; {submittedItems} probe product items completed.");
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

    private static async Task SubmitAsync(HttpClient cart, Probe probe)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/cart/quote")
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

    private static TempoSpan AssertCaller(Probe probe, IReadOnlyList<TempoSpan> spans)
    {
        Assert.True(spans.Count >= 2, $"Caller trace {probe.TraceId} has not been completely exported.");
        var http = Assert.Single(spans, span => span.Name == "POST /cart/quote");
        var execute = Assert.Single(spans, span => span.Name == ExecuteName);
        Assert.Equal(probe.ParentSpanId, http.ParentSpanId);
        Assert.Equal(http.SpanId, execute.ParentSpanId);
        Assert.Equal(probe.TraceId, execute.TraceId);
        Assert.Equal("cart", execute.ServiceName);
        Assert.Equal("System.Int32", execute.Attributes["requestbatcher.request.type"]);
        Assert.Equal("pricing-queries", execute.Attributes["requestbatcher.name"]);
        Assert.Equal("200", http.Attributes["http.response.status_code"]);
        Assert.InRange(execute.StartTimeUnixNano, http.StartTimeUnixNano, http.EndTimeUnixNano);
        Assert.InRange(execute.EndTimeUnixNano, execute.StartTimeUnixNano + 1, http.EndTimeUnixNano);
        Assert.DoesNotContain(spans, span => span.Name == BatchName || span.IsError);
        return execute;
    }

    private static TempoSpan AssertBatch(IReadOnlyList<TempoSpan> spans)
    {
        var root = Assert.Single(spans, span => span.Name == BatchName);
        Assert.Equal("cart", root.ServiceName);
        Assert.Equal("SPAN_KIND_CONSUMER", root.Kind);
        Assert.Empty(root.ParentSpanId);
        Assert.Equal("pricing-queries", root.Attributes["requestbatcher.name"]);
        var size = int.Parse(root.Attributes["requestbatcher.batch.size"], CultureInfo.InvariantCulture);
        Assert.InRange(size, 1, 20);
        Assert.InRange(root.Links.Count, 1, size);
        Assert.Equal(root.Links.Count, root.Links.Distinct().Count());
        Assert.All(root.Links, link => Assert.NotEqual(root.TraceId, link.TraceId));
        var client = Assert.Single(spans, span => span.ParentSpanId == root.SpanId &&
            span.ServiceName == "cart" && span.Kind == "SPAN_KIND_CLIENT");
        var server = Assert.Single(spans, span => span.ParentSpanId == client.SpanId &&
            span.ServiceName == "pricing" && span.Kind == "SPAN_KIND_SERVER");
        var database = Assert.Single(spans, span => span.ParentSpanId == server.SpanId && span.ScopeName == "Npgsql");
        Assert.Equal("SPAN_KIND_CLIENT", database.Kind);
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
        HttpClient grafana, CancellationToken cancellationToken = default)
    {
        const string query = "sum by (__name__) ({__name__=~\"requestbatcher_(requests_submitted_total|requests_processed_total|" +
            "batches_processed_total|batch_size_(sum|count)|batch_duration_milliseconds_(sum|count))\"," +
            "requestbatcher_name=\"pricing-queries\",requestbatcher_request_type=\"Int32\",service_name=\"cart\"})";
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

    private static string GetSetting(string name, string fallback) => Environment.GetEnvironmentVariable(name) ?? fallback;

    private sealed record CartItem(int ProductId, int Quantity);
    private sealed record Probe(string TraceId, string ParentSpanId, CartItem[] Items);
}
