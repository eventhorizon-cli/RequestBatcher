using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using RequestBatcher.Diagnostics.Tracing;

namespace RequestBatcher.Tests.Diagnostics;

[Collection("Tracing")]
public sealed class OpenTelemetryInstrumentationTests
{
    [Fact]
    public async Task AddSource_RealSdk_ExportsCallAndLinkedBatch()
    {
        var processor = new CaptureProcessor();
        var sampler = new LinkSampler();
        var builder = Sdk.CreateTracerProviderBuilder();
        builder.AddSource(RequestBatcherTracing.SourceName);
        using var tracing = builder.SetSampler(sampler).AddProcessor(processor).Build();
        var services = new ServiceCollection();
        services.AddRequestBatcher<int, Handler>(ServiceLifetime.Singleton);
        await using var provider = services.BuildServiceProvider();
        using var parent = new Activity("HTTP request").Start();
        await provider.GetRequiredService<IRequestBatcher<int>>().ExecuteAsync(new[] { 1, 2 });

        Assert.Same(parent, Activity.Current);
        var call = Assert.Single(processor.Spans, span => span.OperationName == RequestBatcherTracing.CallName);
        var batch = Assert.Single(processor.Spans, span => span.OperationName == RequestBatcherTracing.BatchName);
        Assert.Equal(parent.SpanId, call.ParentSpanId);
        Assert.Equal(call.Context, Assert.Single(batch.Links).Context);
        Assert.Equal(call.Context, Assert.Single(sampler.BatchLinks).Context);
        Assert.Equal(batch.Context, Assert.Single(processor.CallLinks).Context);
        Assert.Empty(parent.Links);
        Assert.Equal(default, batch.ParentSpanId);
    }

    [Fact]
    public async Task AddRequestBatcherInstrumentation_DropSampler_PreservesResultsWithoutRecordedSpans()
    {
        var processor = new CaptureProcessor();
        using var tracing = Sdk.CreateTracerProviderBuilder()
            .AddRequestBatcherInstrumentation()
            .SetSampler(new AlwaysOffSampler())
            .AddProcessor(processor)
            .Build();
        var services = new ServiceCollection();
        services.AddRequestBatcher<int, Handler>(ServiceLifetime.Singleton);
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IRequestBatcher<int>>().ExecuteAsync(1);
        Assert.Empty(processor.Spans);
    }

    [Fact]
    public void AddRequestBatcherInstrumentation_NullBuilder_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            RequestBatcherTracerProviderBuilderExtensions.AddRequestBatcherInstrumentation(null!));
        Assert.Equal("builder", exception.ParamName);
    }

    private sealed class Handler : IRequestBatchHandler<int>
    {
        public ValueTask HandleAsync(IReadOnlyList<int> requests, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class CaptureProcessor : BaseProcessor<Activity>
    {
        public ConcurrentQueue<Activity> Spans { get; } = new();
        public ActivityLink[] CallLinks { get; private set; } = [];

        public override void OnEnd(Activity data)
        {
            if (data.OperationName == RequestBatcherTracing.CallName)
            {
                CallLinks = data.Links.ToArray();
            }

            Spans.Enqueue(data);
        }
    }

    private sealed class LinkSampler : Sampler
    {
        public List<ActivityLink> BatchLinks { get; } = [];

        public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
        {
            if (samplingParameters.Name == RequestBatcherTracing.BatchName)
            {
                BatchLinks.AddRange(samplingParameters.Links ?? []);
            }

            return new SamplingResult(SamplingDecision.RecordAndSample);
        }
    }
}
