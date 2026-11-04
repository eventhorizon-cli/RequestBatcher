using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Measures named batch tracing with synchronous handlers and single or explicit submissions.
/// </summary>
/// <remarks>
/// Runs with normal and suppressed submission ExecutionContext flow. Suppression covers only the synchronous public
/// submission calls and, for concurrent singles, Task.WhenAll creation; it is disposed before awaiting the returned
/// Task. The explicit-submission path returns its public Task directly, without Task.Run or a per-call async wrapper.
/// Each invocation validates 256 distinct request IDs, dispatch bounds, and the configured listener mode. Setup,
/// warmup calls, and provider disposal are outside measurement. Reported time and allocation are per wave.
/// No metrics provider, exporter, or downstream I/O is installed.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 2, warmupCount: 3, iterationCount: 10, id: "NamedTracingHotPath")]
public class NamedTracingHotPathBenchmarks
{
    private const int RequestCount = 256;
    private const int BatchSize = 32;
    private const int MaxConcurrency = 8;
    private const string BatcherName = "hot-path-orders";
    private const string CallName = "RequestBatcher.Execute " + BatcherName;
    private const string BatchName = "RequestBatcher.Handle " + BatcherName;

    private readonly int[] _requests = Enumerable.Range(0, RequestCount).ToArray();
    private ServiceProvider _provider = null!;
    private IRequestBatcher<int> _batcher = null!;
    private CountingHandler _handler = null!;
    private ActivityListener? _listener;
    private long _waves;
    private long _expectedCalls;
    private long _expectedBatches;
    private long _stoppedCalls;
    private long _stoppedBatches;
    private string _method = string.Empty;

    /// <summary>
    /// Gets or sets the ActivityListener configuration.
    /// </summary>
    [Params(ListenerMode.Disabled, ListenerMode.Propagation, ListenerMode.Recorded)]
    public ListenerMode TracingMode { get; set; }

    /// <summary>
    /// Gets or sets whether submission calls run while ExecutionContext flow is suppressed.
    /// </summary>
    /// <remarks>
    /// Suppression is applied only around synchronous public submission calls and Task.WhenAll creation, then restored
    /// before awaiting their returned task. Existing caller-owned suppression is preserved.
    /// </remarks>
    [Params(false, true)]
    public bool SuppressSubmissionContextFlow { get; set; }

    /// <summary>
    /// Creates the named singleton batcher and warms both submission paths.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        if (TracingMode != ListenerMode.Disabled)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = static source => source.Name == "RequestBatcher",
                Sample = Sample,
                ActivityStopped = OnActivityStopped,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        var services = new ServiceCollection();
        services.AddRequestBatcher<int, CountingHandler>(BatcherName, ServiceLifetime.Singleton, options =>
        {
            options.BatchSize = BatchSize;
            options.MaxConcurrency = MaxConcurrency;
            options.MaxPendingRequests = RequestCount;
        });
        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _handler = (CountingHandler)_provider.GetRequiredKeyedService<IRequestBatchHandler<int>>(BatcherName);
        _batcher = _provider.GetRequiredKeyedService<IRequestBatcher<int>>(BatcherName);

        RunWaveAsync(explicitSubmission: false).GetAwaiter().GetResult();
        RunWaveAsync(explicitSubmission: true).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Submits 256 concurrent single calls and validates each handled request.
    /// </summary>
    [Benchmark]
    public Task ConcurrentSinglesAsync() => RunWaveAsync(explicitSubmission: false);

    /// <summary>
    /// Submits one explicit group of 256 requests and validates each handled request.
    /// </summary>
    [Benchmark]
    public Task ExplicitSubmissionAsync() => RunWaveAsync(explicitSubmission: true);

    /// <summary>
    /// Drains the pipeline, validates stopped spans, and prints successful validation totals.
    /// </summary>
    [GlobalCleanup]
    public async Task GlobalCleanupAsync()
    {
        try
        {
            await _provider.DisposeAsync().ConfigureAwait(false);
            if (TracingMode == ListenerMode.Disabled)
            {
                if (_stoppedCalls != 0 || _stoppedBatches != 0)
                {
                    throw new InvalidOperationException("Disabled tracing unexpectedly recorded spans.");
                }
            }
            else if (_stoppedCalls != _expectedCalls || _stoppedBatches != _expectedBatches)
            {
                throw new InvalidOperationException(
                    $"Stopped spans mismatch: calls={_stoppedCalls}/{_expectedCalls}, batches={_stoppedBatches}/{_expectedBatches}.");
            }

            Console.WriteLine(
                $"Named tracing validation: Method={_method}, Mode={TracingMode}, " +
                $"SuppressSubmissionContextFlow={SuppressSubmissionContextFlow}, Waves={_waves}, " +
                $"RequestsPerWave={RequestCount}, Calls={_stoppedCalls}, Batches={_stoppedBatches}, " +
                "ExactlyOnce=True, DispatchBounds=True, SpanNames=True");
        }
        finally
        {
            _listener?.Dispose();
        }
    }

    private async Task RunWaveAsync(bool explicitSubmission)
    {
        _method = explicitSubmission ? nameof(ExplicitSubmissionAsync) : nameof(ConcurrentSinglesAsync);
        _handler.BeginWave();
        var submission = SubmitWave(explicitSubmission);
        await submission.ConfigureAwait(false);

        _expectedBatches += _handler.ValidateWave();
        _expectedCalls += explicitSubmission ? 1 : RequestCount;
        _waves++;
    }

    private Task SubmitWave(bool explicitSubmission)
    {
        var wasFlowSuppressed = ExecutionContext.IsFlowSuppressed();
        try
        {
            if (SuppressSubmissionContextFlow && !wasFlowSuppressed)
            {
                using (ExecutionContext.SuppressFlow())
                {
                    return CreateSubmissionTask(explicitSubmission);
                }
            }

            return CreateSubmissionTask(explicitSubmission);
        }
        finally
        {
            if (ExecutionContext.IsFlowSuppressed() != wasFlowSuppressed)
            {
                throw new InvalidOperationException("Submission changed caller-owned ExecutionContext flow suppression.");
            }
        }
    }

    private Task CreateSubmissionTask(bool explicitSubmission)
    {
        if (explicitSubmission)
        {
            return _batcher.ExecuteAsync(_requests);
        }

        var tasks = new Task[RequestCount];
        for (var index = 0; index < tasks.Length; index++)
        {
            tasks[index] = _batcher.ExecuteAsync(_requests[index]);
        }

        return Task.WhenAll(tasks);
    }

    private ActivitySamplingResult Sample(ref ActivityCreationOptions<ActivityContext> options)
    {
        if (options.Name != CallName && options.Name != BatchName)
        {
            throw new InvalidOperationException($"Unexpected sampled span name: {options.Name}.");
        }

        return TracingMode == ListenerMode.Recorded
            ? ActivitySamplingResult.AllDataAndRecorded
            : ActivitySamplingResult.PropagationData;
    }

    private void OnActivityStopped(Activity activity)
    {
        if (activity.Recorded != (TracingMode == ListenerMode.Recorded))
        {
            throw new InvalidOperationException("A stopped span has the wrong sampling flags.");
        }

        if (activity.OperationName == CallName)
        {
            Interlocked.Increment(ref _stoppedCalls);
        }
        else if (activity.OperationName == BatchName)
        {
            Interlocked.Increment(ref _stoppedBatches);
        }
        else
        {
            throw new InvalidOperationException($"Unexpected stopped span name: {activity.OperationName}.");
        }
    }

    /// <summary>
    /// Specifies whether Activities are disabled, propagation-only, or recorded.
    /// </summary>
    public enum ListenerMode
    {
        /// <summary>
        /// No listener is installed.
        /// </summary>
        Disabled,

        /// <summary>
        /// Activities propagate context without recording.
        /// </summary>
        Propagation,

        /// <summary>
        /// Activities record all data and sampled trace flags.
        /// </summary>
        Recorded,
    }

    private sealed class CountingHandler : IRequestBatchHandler<int>
    {
        private readonly int[] _processed = new int[RequestCount];
        private int _batches;
        private int _active;

        public void BeginWave()
        {
            if (Volatile.Read(ref _active) != 0)
            {
                throw new InvalidOperationException("A handler from the preceding wave is still active.");
            }

            Array.Clear(_processed);
            _batches = 0;
        }

        public ValueTask HandleAsync(IReadOnlyList<int> requests, CancellationToken cancellationToken = default)
        {
            if (requests.Count is < 1 or > BatchSize)
            {
                throw new InvalidOperationException("The handler received an invalid batch size.");
            }

            var active = Interlocked.Increment(ref _active);
            try
            {
                if (active > MaxConcurrency)
                {
                    throw new InvalidOperationException("The handler exceeded its concurrency limit.");
                }

                Interlocked.Increment(ref _batches);
                for (var index = 0; index < requests.Count; index++)
                {
                    var request = requests[index];
                    if ((uint)request >= RequestCount || Interlocked.Increment(ref _processed[request]) != 1)
                    {
                        throw new InvalidOperationException($"Unexpected or repeated request: {request}.");
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }

            return ValueTask.CompletedTask;
        }

        public int ValidateWave()
        {
            if (Volatile.Read(ref _active) != 0 || _batches == 0)
            {
                throw new InvalidOperationException("The wave did not finish all handler work.");
            }

            for (var index = 0; index < _processed.Length; index++)
            {
                if (Volatile.Read(ref _processed[index]) != 1)
                {
                    throw new InvalidOperationException($"Request {index} was not handled exactly once.");
                }
            }

            return _batches;
        }
    }
}
