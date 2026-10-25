using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Tests;

public sealed class ResponseStressTests
{
    private const int RequestsPerRound = 10_000;
    private const int Rounds = 3;
    private const int ProducerCount = 8;
    private const int SmallExplicitBatchSize = 17;
    private const int LargeExplicitBatchSize = 129;
    private const int PartitionCount = 8;

    [Theory]
    [InlineData(SubmissionMode.Single, false)]
    [InlineData(SubmissionMode.Single, true)]
    [InlineData(SubmissionMode.ExplicitBatch, false)]
    [InlineData(SubmissionMode.ExplicitBatch, true)]
    [InlineData(SubmissionMode.Mixed, false)]
    [InlineData(SubmissionMode.Mixed, true)]
    public async Task ExecuteAsync_HighConcurrencyResponseModes_ReturnExactlyOnce(
        SubmissionMode submissionMode,
        bool usePartitionKey)
    {
        var state = new StressState(RequestsPerRound * Rounds);
        var services = new ServiceCollection();
        services.AddSingleton(state);
        services.AddRequestBatcher<StressRequest, StressResponse, StressHandler>(
            ServiceLifetime.Singleton,
            options =>
            {
                options.BatchSize = 32;
                options.MaxConcurrency = ProducerCount;
                options.MaxPendingRequests = 256;
                options.FullMode = RequestBatchFullMode.Wait;
                if (usePartitionKey)
                {
                    options.UsePartitionKey(
                        static request => request.Id % PartitionCount,
                        partitionCount: PartitionCount);
                }
            });

        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var batcher = provider.GetRequiredService<IRequestBatcher<StressRequest, StressResponse>>();

        for (var round = 0; round < Rounds; round++)
        {
            await RunRoundAsync(batcher, round, submissionMode);
        }

        state.AssertEveryRequestProcessedExactlyOnce();
    }

    private static async Task RunRoundAsync(
        IRequestBatcher<StressRequest, StressResponse> batcher,
        int round,
        SubmissionMode submissionMode)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var startGate = NewSource();
        var producers = Enumerable
            .Range(0, ProducerCount)
            .Select(producer => ProduceAsync(
                batcher,
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
        IRequestBatcher<StressRequest, StressResponse> batcher,
        int round,
        int producer,
        SubmissionMode submissionMode,
        Task startGate,
        CancellationToken cancellationToken)
    {
        await startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        await Task.Yield();

        var requestsPerProducer = RequestsPerRound / ProducerCount;
        var firstId = round * RequestsPerRound + producer * requestsPerProducer;
        var useExplicitBatches = submissionMode is SubmissionMode.ExplicitBatch ||
            (submissionMode is SubmissionMode.Mixed && producer % 2 == 1);

        if (useExplicitBatches)
        {
            var submissions = new List<Task>();
            var offset = 0;
            var groupIndex = 0;
            while (offset < requestsPerProducer)
            {
                var requestedCount = groupIndex % 2 == 0
                    ? SmallExplicitBatchSize
                    : LargeExplicitBatchSize;
                var count = Math.Min(requestedCount, requestsPerProducer - offset);
                submissions.Add(SubmitBatchAsync(
                    batcher,
                    firstId + offset,
                    count,
                    cancellationToken));
                offset += count;
                groupIndex++;
            }

            await Task.WhenAll(submissions).WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var singleSubmissions = new List<Task>(requestsPerProducer);
        for (var offset = 0; offset < requestsPerProducer; offset++)
        {
            singleSubmissions.Add(SubmitSingleAsync(batcher, firstId + offset, cancellationToken));
        }

        await Task.WhenAll(singleSubmissions).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SubmitSingleAsync(
        IRequestBatcher<StressRequest, StressResponse> batcher,
        int id,
        CancellationToken cancellationToken)
    {
        var request = CreateRequest(id);
        var response = await batcher
            .ExecuteAsync(request, cancellationToken)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        AssertResponse(request, response);
    }

    private static async Task SubmitBatchAsync(
        IRequestBatcher<StressRequest, StressResponse> batcher,
        int firstId,
        int count,
        CancellationToken cancellationToken)
    {
        var requests = new StressRequest[count];
        for (var index = 0; index < count; index++)
        {
            requests[index] = CreateRequest(firstId + index);
        }

        var responses = await batcher
            .ExecuteAsync(requests, cancellationToken)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        Assert.Equal(count, responses.Count);
        for (var index = 0; index < count; index++)
        {
            AssertResponse(requests[index], responses[index]);
        }
    }

    private static StressRequest CreateRequest(int id) =>
        new(id, unchecked(id * 101 + 7));

    private static void AssertResponse(StressRequest request, StressResponse response)
    {
        Assert.True(
            response.Id == request.Id,
            $"Response ID {response.Id} did not match request ID {request.Id}.");
        Assert.True(
            response.TransformedValue == Transform(request.Value),
            $"Response value for request ID {request.Id} did not match the expected transform.");
        Assert.NotEqual(request.Value, response.TransformedValue);
    }

    private static int Transform(int value) => unchecked(value * 17 + 11);

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public enum SubmissionMode
    {
        Single,
        ExplicitBatch,
        Mixed,
    }

    public readonly record struct StressRequest(int Id, int Value);

    public readonly record struct StressResponse(int Id, int TransformedValue);

    private sealed class StressState(int requestCount)
    {
        private readonly int[] _processedCounts = new int[requestCount];

        public void Record(int id)
        {
            if ((uint)id >= (uint)_processedCounts.Length)
            {
                throw new InvalidOperationException($"Unexpected request id {id}.");
            }

            Interlocked.Increment(ref _processedCounts[id]);
        }

        public void AssertEveryRequestProcessedExactlyOnce()
        {
            for (var id = 0; id < _processedCounts.Length; id++)
            {
                var count = Volatile.Read(ref _processedCounts[id]);
                Assert.True(count == 1, $"Request ID {id} was processed {count} times.");
            }
        }
    }

    private sealed class StressHandler(StressState state)
        : IRequestBatchHandler<StressRequest, StressResponse>
    {
        public async ValueTask HandleAsync(
            IReadOnlyList<RequestBatchEntry<StressRequest, StressResponse>> entries,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            for (var index = entries.Count - 1; index >= 0; index--)
            {
                var request = entries[index].Request;
                state.Record(request.Id);
                entries[index].SetResponse(new StressResponse(request.Id, Transform(request.Value)));
            }
        }
    }
}
