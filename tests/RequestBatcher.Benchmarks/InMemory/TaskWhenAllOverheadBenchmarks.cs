using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Compares <see cref="Task.WhenAll(Task[])"/> with awaiting the same tasks one by one.
/// Completed cases reuse a prebuilt task array. Pending cases intentionally include creating
/// and completing the same task-source harness in each invocation, so their difference includes
/// harness overhead and should not be attributed entirely to <see cref="Task.WhenAll(Task[])"/>.
/// The generic <see cref="Task.WhenAll(Task{TResult}[])"/> path also includes its results-array allocation.
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 1,
    warmupCount: 3,
    iterationCount: 8,
    id: "TaskWhenAllOverhead")]
[IterationTime(200)]
public class TaskWhenAllOverheadBenchmarks
{
    private Task<int>[] _completedTasks = [];
    private long _expectedChecksum;

    /// <summary>
    /// Gets or sets the number of tasks in one aggregation operation.
    /// </summary>
    [Params(32, 256, 4_096)]
    public int TaskCount { get; set; }

    /// <summary>
    /// Builds the completed task set, warms every benchmark path, and validates its result.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        _completedTasks = new Task<int>[TaskCount];
        for (var index = 0; index < _completedTasks.Length; index++)
        {
            _completedTasks[index] = Task.FromResult(index);
        }

        _expectedChecksum = (long)TaskCount * (TaskCount - 1) / 2;
        ValidateResult(nameof(CompletedWhenAll), CompletedWhenAll().GetAwaiter().GetResult());
        ValidateResult(nameof(CompletedAwaitEach), CompletedAwaitEach().GetAwaiter().GetResult());
        ValidateResult(nameof(PendingWhenAll), PendingWhenAll().GetAwaiter().GetResult());
        ValidateResult(nameof(PendingAwaitEach), PendingAwaitEach().GetAwaiter().GetResult());
    }

    /// <summary>
    /// Measures aggregation of an already-completed task array with <see cref="Task.WhenAll(Task[])"/>.
    /// </summary>
    [Benchmark(Baseline = true, Description = "Completed: Task.WhenAll"), BenchmarkCategory("Completed")]
    public Task<long> CompletedWhenAll() => SumWithWhenAllAsync(_completedTasks);

    /// <summary>
    /// Measures awaiting the same already-completed task array one task at a time.
    /// </summary>
    [Benchmark(Description = "Completed: await each"), BenchmarkCategory("Completed")]
    public Task<long> CompletedAwaitEach() => SumWithAwaitEachAsync(_completedTasks);

    /// <summary>
    /// Measures pending task aggregation with <see cref="Task.WhenAll(Task[])"/>.
    /// </summary>
    [Benchmark(Baseline = true, Description = "Pending: Task.WhenAll"), BenchmarkCategory("Pending")]
    public async Task<long> PendingWhenAll()
    {
        var harness = PendingTaskHarness.Create(TaskCount);
        // Start aggregation while sources are pending, then release the identical harness for both pending cases.
        var aggregation = SumWithWhenAllAsync(harness.Tasks);
        EnsurePending(aggregation, nameof(PendingWhenAll));
        harness.Complete();
        return await aggregation.ConfigureAwait(false);
    }

    /// <summary>
    /// Measures pending task aggregation by awaiting each task after all tasks have been created.
    /// </summary>
    [Benchmark(Description = "Pending: await each"), BenchmarkCategory("Pending")]
    public async Task<long> PendingAwaitEach()
    {
        var harness = PendingTaskHarness.Create(TaskCount);
        // All tasks exist before the first await; completion is shared with the WhenAll case.
        var aggregation = SumWithAwaitEachAsync(harness.Tasks);
        EnsurePending(aggregation, nameof(PendingAwaitEach));
        harness.Complete();
        return await aggregation.ConfigureAwait(false);
    }

    private static async Task<long> SumWithWhenAllAsync(Task<int>[] tasks)
    {
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return Sum(results);
    }

    private static async Task<long> SumWithAwaitEachAsync(Task<int>[] tasks)
    {
        long sum = 0;
        foreach (var task in tasks)
        {
            sum += await task.ConfigureAwait(false);
        }

        return sum;
    }

    private static long Sum(IReadOnlyList<int> values)
    {
        long sum = 0;
        for (var index = 0; index < values.Count; index++)
        {
            sum += values[index];
        }

        return sum;
    }

    private static void EnsurePending(Task<long> aggregation, string benchmarkName)
    {
        if (aggregation.IsCompleted)
        {
            throw new InvalidOperationException(
                $"{benchmarkName} aggregation completed before pending sources were released.");
        }
    }

    private void ValidateResult(string benchmarkName, long result)
    {
        if (result != _expectedChecksum)
        {
            throw new InvalidOperationException(
                $"{benchmarkName} returned {result}; expected {_expectedChecksum}.");
        }
    }

    private sealed class PendingTaskHarness
    {
        private readonly TaskCompletionSource<int>[] _sources;

        private PendingTaskHarness(TaskCompletionSource<int>[] sources, Task<int>[] tasks)
        {
            _sources = sources;
            Tasks = tasks;
        }

        public Task<int>[] Tasks { get; }

        public static PendingTaskHarness Create(int taskCount)
        {
            var sources = new TaskCompletionSource<int>[taskCount];
            var tasks = new Task<int>[taskCount];
            for (var index = 0; index < taskCount; index++)
            {
                var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                sources[index] = source;
                tasks[index] = source.Task;
            }

            return new PendingTaskHarness(sources, tasks);
        }

        public void Complete()
        {
            for (var index = 0; index < _sources.Length; index++)
            {
                _sources[index].SetResult(index);
            }
        }
    }
}
