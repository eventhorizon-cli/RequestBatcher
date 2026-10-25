using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Tests;

public sealed class DelegateRequestOnlyStressTests
{
    private const int RequestsPerWave = 2_048;
    private const int Rounds = 4;
    private const int ProducerCount = 8;
    private const int ExplicitBatchSize = 32;
    private const int RequestsPerProducer = RequestsPerWave / ProducerCount;

    [Theory]
    [InlineData(SubmissionMode.Single)]
    [InlineData(SubmissionMode.Explicit)]
    [InlineData(SubmissionMode.Mixed)]
    public async Task ExecuteAsync_RequestOnlyDelegateStress_ProcessesEveryModeExactlyOnce(
        SubmissionMode submissionMode)
    {
        var state = new StressState(RequestsPerWave * Rounds);
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<StressRequest>(options =>
        {
            options.BatchSize = 32;
            options.MaxConcurrency = ProducerCount;
            options.MaxPendingRequests = 256;
            options.FullMode = RequestBatchFullMode.Wait;
        });

        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var batcher = provider.GetRequiredService<IRequestBatcher<StressRequest>>();

        for (var round = 0; round < Rounds; round++)
        {
            await RunRoundAsync(batcher, state, round, submissionMode);
        }

        state.AssertEveryRequestProcessedExactlyOnce();
    }

    private static async Task RunRoundAsync(
        IRequestBatcher<StressRequest> batcher,
        StressState state,
        int round,
        SubmissionMode submissionMode)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var startGate = NewSource();
        var producers = Enumerable
            .Range(0, ProducerCount)
            .Select(producer => ProduceAsync(
                batcher,
                state,
                round,
                producer,
                submissionMode,
                startGate.Task,
                timeout.Token))
            .ToArray();

        try
        {
            startGate.SetResult();
            await Task.WhenAll(producers).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            startGate.TrySetResult();
        }
    }

    private static async Task ProduceAsync(
        IRequestBatcher<StressRequest> batcher,
        StressState state,
        int round,
        int producer,
        SubmissionMode submissionMode,
        Task startGate,
        CancellationToken cancellationToken)
    {
        await startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        await Task.Yield();

        var firstId = round * RequestsPerWave + producer * RequestsPerProducer;
        var useExplicitBatches = submissionMode is SubmissionMode.Explicit ||
            (submissionMode is SubmissionMode.Mixed && producer % 2 == 1);

        if (useExplicitBatches)
        {
            var submissions = new List<Task>();
            for (var offset = 0; offset < RequestsPerProducer; offset += ExplicitBatchSize)
            {
                var count = Math.Min(ExplicitBatchSize, RequestsPerProducer - offset);
                submissions.Add(SubmitBatchAsync(
                    batcher,
                    state,
                    firstId + offset,
                    count,
                    cancellationToken));
            }

            await Task.WhenAll(submissions).WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var singleSubmissions = new List<Task>(RequestsPerProducer);
        for (var offset = 0; offset < RequestsPerProducer; offset++)
        {
            singleSubmissions.Add(SubmitSingleAsync(
                batcher,
                state,
                firstId + offset,
                cancellationToken));
        }

        await Task.WhenAll(singleSubmissions).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SubmitSingleAsync(
        IRequestBatcher<StressRequest> batcher,
        StressState state,
        int id,
        CancellationToken cancellationToken)
    {
        var request = state.CreateRequest(id);
        await batcher
            .ExecuteAsync(request, HandleAsync, cancellationToken)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        Assert.True(
            request.Processed.Task.IsCompletedSuccessfully,
            $"Caller completed before request {id} was processed.");
    }

    private static async Task SubmitBatchAsync(
        IRequestBatcher<StressRequest> batcher,
        StressState state,
        int firstId,
        int count,
        CancellationToken cancellationToken)
    {
        var requests = new StressRequest[count];
        for (var index = 0; index < count; index++)
        {
            requests[index] = state.CreateRequest(firstId + index);
        }

        await batcher
            .ExecuteAsync(requests, HandleAsync, cancellationToken)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        Assert.All(
            requests,
            static request => Assert.True(
                request.Processed.Task.IsCompletedSuccessfully,
                $"Caller completed before request {request.Id} was processed."));
    }

    private static async Task HandleAsync(
        IReadOnlyList<StressRequest> requests,
        CancellationToken cancellationToken)
    {
        Assert.True(cancellationToken.CanBeCanceled);
        await Task.Yield();

        foreach (var request in requests)
        {
            var transformed = Transform(request.Value);
            Assert.Equal(request.ExpectedValue, transformed);
            request.State.Record(request.Id, transformed);
            request.Processed.TrySetResult();
        }
    }

    private static int Transform(int value) => unchecked(value * 17 + 11);

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public enum SubmissionMode
    {
        Single,
        Explicit,
        Mixed,
    }

    private sealed class StressRequest(
        StressState state,
        int id,
        int value,
        int expectedValue)
    {
        public int Id { get; } = id;

        public int Value { get; } = value;

        public int ExpectedValue { get; } = expectedValue;

        public StressState State { get; } = state;

        public TaskCompletionSource Processed { get; } = NewSource();
    }

    private sealed class StressState(int requestCount)
    {
        private readonly int[] _processedCounts = new int[requestCount];

        public StressRequest CreateRequest(int id)
        {
            var value = unchecked(id * 101 + 7);
            return new StressRequest(this, id, value, Transform(value));
        }

        public void Record(int id, int transformed)
        {
            if ((uint)id >= (uint)_processedCounts.Length)
            {
                throw new InvalidOperationException($"Unexpected request ID {id}.");
            }

            var count = Interlocked.Increment(ref _processedCounts[id]);
            if (count != 1)
            {
                throw new InvalidOperationException(
                    $"Request ID {id} was processed {count} times; transformed value was {transformed}.");
            }
        }

        public void AssertEveryRequestProcessedExactlyOnce()
        {
            for (var id = 0; id < _processedCounts.Length; id++)
            {
                var count = Volatile.Read(ref _processedCounts[id]);
                Assert.Equal(1, count);
            }
        }
    }
}
