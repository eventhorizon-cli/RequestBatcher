using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using StackExchange.Redis;

namespace RequestBatcher.Benchmarks.SchedulingEvaluation;

internal static class SchedulingProbe
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(string[] args)
    {
        var options = args.Length == 0
            ? new SchedulingProbeOptions()
            : JsonSerializer.Deserialize<SchedulingProbeOptions>(args[0], _jsonOptions)
                ?? throw new InvalidOperationException("The scheduling probe options were empty.");
        options.Validate();

        await using var store = await ProbeStore.CreateAsync(options).ConfigureAwait(false);
        var runId = Guid.NewGuid();
        if (store is not null)
        {
            await store.PrepareAsync(runId).ConfigureAwait(false);
        }

        var telemetry = new ProbeTelemetry(options.MaxConcurrency);
        var hooks = EvaluationHooks.Configure<ProbeRequest>(options.Offload, telemetry);
        try
        {
            await RunWarmupAsync(options, store, telemetry).ConfigureAwait(false);
            await telemetry.WaitForIdleAsync().ConfigureAwait(false);
            if (store is not null)
            {
                await store.PrepareAsync(runId).ConfigureAwait(false);
                if (options.DatabaseOperation == "read")
                {
                    await store.SeedAsync(runId, options.MaxItems).ConfigureAwait(false);
                }
            }

            var result = await RunMeasuredAsync(options, runId, store, telemetry).ConfigureAwait(false);
            if (store is not null)
            {
                var expectedPersistedItems = options.DatabaseOperation == "read"
                    ? options.MaxItems
                    : result.IssuedItems;
                await store.VerifyAsync(runId, expectedPersistedItems).ConfigureAwait(false);
            }

            Console.WriteLine(JsonSerializer.Serialize(result, _jsonOptions));
        }
        finally
        {
            try
            {
                if (store is not null)
                {
                    await store.PrepareAsync(runId).ConfigureAwait(false);
                }
            }
            finally
            {
                hooks.Dispose();
            }
        }
    }

    private static async Task RunWarmupAsync(
        SchedulingProbeOptions options,
        ProbeStore? store,
        ProbeTelemetry telemetry)
    {
        var warmup = options with
        {
            DurationMilliseconds = 0,
            MaxItems = Math.Min(options.WarmupItems, options.MaxItems),
            IntendedRate = Math.Max(options.IntendedRate, 1),
        };
        if (warmup.MaxItems == 0)
        {
            return;
        }

        var warmupRunId = Guid.NewGuid();
        try
        {
            if (store is not null)
            {
                await store.PrepareAsync(warmupRunId).ConfigureAwait(false);
                if (options.DatabaseOperation == "read")
                {
                    await store.SeedAsync(warmupRunId, warmup.MaxItems).ConfigureAwait(false);
                }
            }

            await RunMeasuredAsync(warmup, warmupRunId, store, telemetry).ConfigureAwait(false);
        }
        finally
        {
            if (store is not null)
            {
                await store.PrepareAsync(warmupRunId).ConfigureAwait(false);
            }
        }
    }

    private static async Task<SchedulingProbeResult> RunMeasuredAsync(
        SchedulingProbeOptions options,
        Guid runId,
        ProbeStore? store,
        ProbeTelemetry telemetry)
    {
        var seen = new int[options.MaxItems];
        var services = new ServiceCollection();
        services.AddSingleton(new ProbeState(options, runId, store, seen));
        services.AddRequestBatcher<ProbeRequest, ProbeHandler>(
            ServiceLifetime.Singleton,
            requestOptions =>
            {
                requestOptions.BatchSize = options.BatchSize;
                requestOptions.MaxConcurrency = options.MaxConcurrency;
                requestOptions.MaxPendingRequests = Math.Max(
                    options.BatchSize * options.MaxConcurrency * 4,
                    options.MaxConcurrency * 16);
            });
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        var batcher = provider.GetRequiredService<IRequestBatcher<ProbeRequest>>();
        var plannedItems = PlannedItemCount(options);
        var completions = new List<Task>(plannedItems);
        telemetry.Reset();
        var issueStart = Stopwatch.GetTimestamp();
        var firstIssue = issueStart;
        var lastIssue = issueStart;
        var issued = 0;

        try
        {
            for (var id = 0; id < plannedItems; id++)
            {
                var due = options.DurationMilliseconds > 0
                    ? issueStart + (long)(id * (double)Stopwatch.Frequency / options.IntendedRate)
                    : Stopwatch.GetTimestamp();
                if (options.DurationMilliseconds > 0)
                {
                    await WaitUntilAsync(due).ConfigureAwait(false);
                }

                var actualIssue = Stopwatch.GetTimestamp();
                firstIssue = issued == 0 ? actualIssue : firstIssue;
                var completion = batcher.ExecuteAsync(new ProbeRequest(id));
                completions.Add(ObserveCompletionAsync(completion, due, telemetry));
                issued++;
                lastIssue = actualIssue;
            }

            await Task.WhenAll(completions).ConfigureAwait(false);
        }
        finally
        {
            await telemetry.WaitForIdleAsync().ConfigureAwait(false);
        }
        var finished = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(firstIssue, finished);
        var issueElapsed = Stopwatch.GetElapsedTime(firstIssue, lastIssue);
        var allocatedBytes = GC.GetTotalAllocatedBytes(false) - telemetry.AllocatedBytesAtStart;
        var gen0Collections = GC.CollectionCount(0) - telemetry.Gen0AtStart;
        var gen1Collections = GC.CollectionCount(1) - telemetry.Gen1AtStart;
        var gen2Collections = GC.CollectionCount(2) - telemetry.Gen2AtStart;
        var threadPoolPendingDelta = ReadThreadPoolDelta(telemetry.PendingAtStart, ThreadPool.PendingWorkItemCount);
        var threadPoolCompletedDelta = ReadThreadPoolDelta(telemetry.CompletedAtStart, ThreadPool.CompletedWorkItemCount);
        ValidateExactlyOnce(seen, issued);

        return new SchedulingProbeResult
        {
            Workload = options.Workload,
            DatabaseOperation = options.DatabaseOperation,
            Offload = options.Offload,
            BatchSize = options.BatchSize,
            MaxConcurrency = options.MaxConcurrency,
            IntendedRate = options.IntendedRate,
            IssuedItems = issued,
            IssueMilliseconds = issueElapsed.TotalMilliseconds,
            ActualIssueRate = issued <= 1
                ? 0
                : (issued - 1) / Math.Max(issueElapsed.TotalSeconds, 0.000001),
            ElapsedMilliseconds = elapsed.TotalMilliseconds,
            Throughput = issued / Math.Max(elapsed.TotalSeconds, 0.000001),
            CallerP50Milliseconds = telemetry.Percentile(0.50),
            CallerP95Milliseconds = telemetry.Percentile(0.95),
            CallerP99Milliseconds = telemetry.Percentile(0.99),
            ActiveSlotWallUtilization = telemetry.ActiveSlotUtilization(elapsed),
            PeakActiveSlots = telemetry.PeakActiveSlots,
            BatchCount = telemetry.BatchCount,
            MeanBatchSize = telemetry.MeanBatchSize,
            MaximumBatchSize = telemetry.MaximumBatchSize,
            AllocatedBytes = allocatedBytes,
            Gen0Collections = gen0Collections,
            Gen1Collections = gen1Collections,
            Gen2Collections = gen2Collections,
            ThreadPoolPendingDelta = threadPoolPendingDelta,
            ThreadPoolCompletedDelta = threadPoolCompletedDelta,
            ExactOnce = true,
        };
    }

    private static int PlannedItemCount(SchedulingProbeOptions options)
    {
        if (options.DurationMilliseconds == 0)
        {
            return options.MaxItems;
        }

        var target = options.IntendedRate * options.DurationMilliseconds / 1000d;
        return target >= options.MaxItems
            ? options.MaxItems
            : Math.Max(1, (int)Math.Ceiling(target));
    }

    private static async Task ObserveCompletionAsync(
        Task completion,
        long scheduledDue,
        ProbeTelemetry telemetry)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        finally
        {
            telemetry.RecordLatency(Stopwatch.GetElapsedTime(scheduledDue).TotalMilliseconds);
        }
    }

    private static async Task WaitUntilAsync(long due)
    {
        while (true)
        {
            var remaining = due - Stopwatch.GetTimestamp();
            if (remaining <= 0)
            {
                return;
            }

            var milliseconds = remaining * 1000.0 / Stopwatch.Frequency;
            if (milliseconds > 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(milliseconds - 1)).ConfigureAwait(false);
            }
            else
            {
                Thread.SpinWait(100);
            }
        }
    }

    private static long ReadThreadPoolDelta(long start, long end) => end - start;

    private static void ValidateExactlyOnce(int[] seen, int issued)
    {
        for (var id = 0; id < issued; id++)
        {
            if (Volatile.Read(ref seen[id]) != 1)
            {
                throw new InvalidOperationException(
                    $"Item {id} was observed {Volatile.Read(ref seen[id])} times; expected exactly once.");
            }
        }
    }

    private sealed record ProbeState(
        SchedulingProbeOptions Options,
        Guid RunId,
        ProbeStore? Store,
        int[] Seen);

    private sealed class ProbeHandler(ProbeState state) : IRequestBatchHandler<ProbeRequest>
    {
        public async ValueTask HandleAsync(
            IReadOnlyList<ProbeRequest> requests,
            CancellationToken cancellationToken = default)
        {
            switch (state.Options.Workload)
            {
                case "fast":
                    break;
                case "io":
                    await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                    break;
                case "cpu":
                    RunCpuPrefix(state.Options.CpuIterations);
                    break;
                case "blocking":
                    Thread.Sleep(state.Options.BlockingMilliseconds);
                    break;
                case "postgres":
                case "redis":
                    var database = state.Store ?? throw new InvalidOperationException("A database store is required.");
                    if (state.Options.DatabaseOperation == "read")
                    {
                        await database.ReadAsync(state.RunId, requests, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await database.WriteAsync(state.RunId, requests, cancellationToken).ConfigureAwait(false);
                    }
                    break;
                default:
                    throw new InvalidOperationException($"Unknown workload '{state.Options.Workload}'.");
            }

            foreach (var request in requests)
            {
                if (Interlocked.Increment(ref state.Seen[request.Id]) != 1)
                {
                    throw new InvalidOperationException($"Item {request.Id} was processed more than once.");
                }
            }

        }

        private static void RunCpuPrefix(int iterations)
        {
            var value = 17;
            for (var index = 0; index < iterations; index++)
            {
                value = unchecked(value * 31 + index);
            }

            GC.KeepAlive(value);
        }
    }

    private readonly record struct ProbeRequest(int Id);

    private sealed class ProbeTelemetry(int maxConcurrency)
    {
        private readonly object _lock = new();
        private readonly List<double> _latencies = [];
        private TaskCompletionSource? _idleWaiter;
        private long _activeSince;
        private long _activeSlotTicks;
        private int _active;
        private int _peakHandlers;
        private int _batchCount;
        private long _batchItems;
        private int _maximumBatchSize;

        public long AllocatedBytesAtStart { get; private set; }
        public int Gen0AtStart { get; private set; }
        public int Gen1AtStart { get; private set; }
        public int Gen2AtStart { get; private set; }
        public long PendingAtStart { get; private set; }
        public long CompletedAtStart { get; private set; }
        public int PeakActiveSlots => Volatile.Read(ref _peakHandlers);
        public int BatchCount => Volatile.Read(ref _batchCount);
        public double MeanBatchSize => BatchCount == 0 ? 0 : (double)Interlocked.Read(ref _batchItems) / BatchCount;
        public int MaximumBatchSize => Volatile.Read(ref _maximumBatchSize);

        public void Reset()
        {
            lock (_lock)
            {
                _latencies.Clear();
                _activeSince = 0;
                _activeSlotTicks = 0;
                _active = 0;
                _idleWaiter = null;
                _peakHandlers = 0;
                _batchCount = 0;
                _batchItems = 0;
                _maximumBatchSize = 0;
                AllocatedBytesAtStart = GC.GetTotalAllocatedBytes(true);
                Gen0AtStart = GC.CollectionCount(0);
                Gen1AtStart = GC.CollectionCount(1);
                Gen2AtStart = GC.CollectionCount(2);
                PendingAtStart = ThreadPool.PendingWorkItemCount;
                CompletedAtStart = ThreadPool.CompletedWorkItemCount;
            }
        }

        public void RecordLatency(double milliseconds)
        {
            lock (_lock)
            {
                _latencies.Add(milliseconds);
            }
        }

        public void ExecutionStarted()
        {
            lock (_lock)
            {
                var now = Stopwatch.GetTimestamp();
                AccumulateActiveSlots(now);
                _activeSince = now;
                _active++;
                _peakHandlers = Math.Max(_peakHandlers, _active);
            }
        }

        public void ExecutionFinished()
        {
            lock (_lock)
            {
                var now = Stopwatch.GetTimestamp();
                AccumulateActiveSlots(now);
                _active--;
                _activeSince = _active == 0 ? 0 : now;
                if (_active == 0)
                {
                    _idleWaiter?.TrySetResult();
                    _idleWaiter = null;
                }
            }
        }

        public Task WaitForIdleAsync()
        {
            lock (_lock)
            {
                return _active == 0
                    ? Task.CompletedTask
                    : (_idleWaiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
        }

        private void AccumulateActiveSlots(long now)
        {
            if (_active > 0)
            {
                _activeSlotTicks += (now - _activeSince) * (long)_active;
            }
        }

        public void RecordBatchStarted(int count)
        {
            Interlocked.Increment(ref _batchCount);
            Interlocked.Add(ref _batchItems, count);
            InterlockedMax(ref _maximumBatchSize, count);
        }

        public double Percentile(double percentile)
        {
            lock (_lock)
            {
                if (_latencies.Count == 0)
                {
                    return 0;
                }

                var sorted = _latencies.Order().ToArray();
                var index = Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
                return sorted[index];
            }
        }

        public double ActiveSlotUtilization(TimeSpan elapsed)
        {
            lock (_lock)
            {
                var denominator = elapsed.TotalSeconds * Stopwatch.Frequency * maxConcurrency;
                return denominator <= 0 ? 0 : _activeSlotTicks / denominator;
            }
        }

        private static void InterlockedMax(ref int location, int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref location);
                if (current >= value || Interlocked.CompareExchange(ref location, value, current) == current)
                {
                    return;
                }
            }
        }

    }

    private sealed class EvaluationHooks : IDisposable
    {
        private readonly FieldInfo _offload;
        private readonly FieldInfo _executionStarted;
        private readonly FieldInfo _executionFinished;
        private readonly FieldInfo _batchStarted;

        private EvaluationHooks(
            FieldInfo offload,
            FieldInfo executionStarted,
            FieldInfo executionFinished,
            FieldInfo batchStarted)
        {
            _offload = offload;
            _executionStarted = executionStarted;
            _executionFinished = executionFinished;
            _batchStarted = batchStarted;
        }

        public static EvaluationHooks Configure<TRequest>(bool offload, ProbeTelemetry telemetry)
        {
            var assembly = typeof(RequestBatchCoordinator<TRequest>).Assembly;
            var openType = assembly.GetType("RequestBatcher.Scheduling.BatchDispatchLoop`1")
                ?? throw new InvalidOperationException("The scheduling loop type was not found.");
            var dispatchType = openType.MakeGenericType(typeof(TRequest));
            var hooks = new EvaluationHooks(
                GetField(dispatchType, "EvaluationOffload"),
                GetField(dispatchType, "EvaluationExecutionStarted"),
                GetField(dispatchType, "EvaluationExecutionFinished"),
                GetField(dispatchType, "EvaluationBatchStarted"));
            hooks._offload.SetValue(null, offload);
            hooks._executionStarted.SetValue(null, (Action)telemetry.ExecutionStarted);
            hooks._executionFinished.SetValue(null, (Action)telemetry.ExecutionFinished);
            hooks._batchStarted.SetValue(null, (Action<int>)telemetry.RecordBatchStarted);
            return hooks;
        }

        public void Dispose()
        {
            _offload.SetValue(null, false);
            _executionStarted.SetValue(null, null);
            _executionFinished.SetValue(null, null);
            _batchStarted.SetValue(null, null);
        }

        private static FieldInfo GetField(Type type, string name) =>
            type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"The evaluation hook '{name}' was not found.");
    }

    private abstract class ProbeStore : IAsyncDisposable
    {
        public static async Task<ProbeStore?> CreateAsync(SchedulingProbeOptions options) =>
            options.Workload switch
            {
                "postgres" => await PostgreSqlProbeStore.CreateAsync(options).ConfigureAwait(false),
                "redis" => await RedisProbeStore.CreateAsync(options).ConfigureAwait(false),
                _ => null,
            };

        public abstract Task PrepareAsync(Guid runId);
        public abstract Task SeedAsync(Guid runId, int count);
        public abstract Task WriteAsync(Guid runId, IReadOnlyList<ProbeRequest> requests, CancellationToken cancellationToken);
        public abstract Task ReadAsync(Guid runId, IReadOnlyList<ProbeRequest> requests, CancellationToken cancellationToken);
        public abstract Task VerifyAsync(Guid runId, int expectedCount);
        public abstract ValueTask DisposeAsync();
    }

    private sealed class PostgreSqlProbeStore : ProbeStore
    {
        private const string DefaultConnection =
            "Host=127.0.0.1;Port=54329;Database=requestbatcher_sample;Username=postgres;Password=postgres";
        private readonly NpgsqlDataSource _dataSource;

        private PostgreSqlProbeStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

        public static new async Task<PostgreSqlProbeStore> CreateAsync(SchedulingProbeOptions options)
        {
            var connectionString = Environment.GetEnvironmentVariable("REQUEST_BATCHER_POSTGRES_CONNECTION")
                ?? DefaultConnection;
            var builder = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(connectionString)
            {
                MinPoolSize = options.MaxConcurrency,
                MaxPoolSize = options.MaxConcurrency,
                ApplicationName = "RequestBatcher.SchedulingProbe",
            }.ConnectionString);
            var store = new PostgreSqlProbeStore(builder.Build());
            await store.InitializeAsync(options.MaxConcurrency).ConfigureAwait(false);
            return store;
        }

        public override async Task PrepareAsync(Guid runId)
        {
            await using var command = _dataSource.CreateCommand("DELETE FROM scheduling_probe_writes WHERE run_id = $1");
            command.Parameters.AddWithValue(runId);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        public override async Task WriteAsync(
            Guid runId,
            IReadOnlyList<ProbeRequest> requests,
            CancellationToken cancellationToken)
        {
            var ids = requests.Select(request => request.Id).ToArray();
            var values = ids.Select(id => id * 17L + 3).ToArray();
            await using var command = _dataSource.CreateCommand("""
                INSERT INTO scheduling_probe_writes (run_id, item_id, value)
                SELECT $1, item_id, value
                FROM unnest($2::integer[], $3::bigint[]) AS rows(item_id, value)
                ON CONFLICT (run_id, item_id) DO UPDATE SET value = EXCLUDED.value;
                """);
            command.Parameters.AddWithValue(runId);
            command.Parameters.AddWithValue(ids);
            command.Parameters.AddWithValue(values);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        public override Task SeedAsync(Guid runId, int count) =>
            WriteAsync(
                runId,
                Enumerable.Range(0, count).Select(static id => new ProbeRequest(id)).ToArray(),
                CancellationToken.None);

        public override async Task ReadAsync(
            Guid runId,
            IReadOnlyList<ProbeRequest> requests,
            CancellationToken cancellationToken)
        {
            var ids = requests.Select(request => request.Id).ToArray();
            await using var command = _dataSource.CreateCommand(
                "SELECT item_id, value FROM scheduling_probe_writes WHERE run_id = $1 AND item_id = ANY($2::integer[])");
            command.Parameters.AddWithValue(runId);
            command.Parameters.AddWithValue(ids);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var found = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetInt32(0);
                var value = reader.GetInt64(1);
                if (value != id * 17L + 3)
                {
                    throw new InvalidOperationException($"PostgreSQL value mismatch for item {id}.");
                }

                found++;
            }

            if (found != ids.Length)
            {
                throw new InvalidOperationException($"PostgreSQL returned {found} rows; expected {ids.Length}.");
            }
        }

        public override async Task VerifyAsync(Guid runId, int expectedCount)
        {
            await using var command = _dataSource.CreateCommand(
                "SELECT item_id, value FROM scheduling_probe_writes WHERE run_id = $1 ORDER BY item_id");
            command.Parameters.AddWithValue(runId);
            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            var count = 0;
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var id = reader.GetInt32(0);
                var value = reader.GetInt64(1);
                if (value != id * 17L + 3)
                {
                    throw new InvalidOperationException($"PostgreSQL value mismatch for item {id}.");
                }

                count++;
            }

            if (count != expectedCount)
            {
                throw new InvalidOperationException($"PostgreSQL persisted {count} rows; expected {expectedCount}.");
            }
        }

        public override ValueTask DisposeAsync() => _dataSource.DisposeAsync();

        private async Task InitializeAsync(int maxConcurrency)
        {
            await using var command = _dataSource.CreateCommand("""
                CREATE TABLE IF NOT EXISTS scheduling_probe_writes
                (
                    run_id uuid NOT NULL,
                    item_id integer NOT NULL,
                    value bigint NOT NULL,
                    PRIMARY KEY (run_id, item_id)
                )
                """);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            var warmup = Enumerable.Range(0, maxConcurrency)
                .Select(_ => _dataSource.OpenConnectionAsync().AsTask())
                .ToArray();
            var connections = await Task.WhenAll(warmup).ConfigureAwait(false);
            foreach (var connection in connections)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class RedisProbeStore : ProbeStore
    {
        private readonly ConnectionMultiplexer _connection;
        private readonly IDatabase _database;

        private RedisProbeStore(ConnectionMultiplexer connection)
        {
            _connection = connection;
            _database = connection.GetDatabase();
        }

        public static new async Task<RedisProbeStore> CreateAsync(SchedulingProbeOptions options)
        {
            var connectionString = Environment.GetEnvironmentVariable("REQUEST_BATCHER_REDIS_CONNECTION")
                ?? "127.0.0.1:6379";
            var store = new RedisProbeStore(await ConnectionMultiplexer.ConnectAsync(connectionString).ConfigureAwait(false));
            await store._database.PingAsync().ConfigureAwait(false);
            return store;
        }

        public override async Task PrepareAsync(Guid runId)
        {
            var keys = FindKeys(runId);
            if (keys.Length != 0)
            {
                await _database.KeyDeleteAsync(keys).ConfigureAwait(false);
            }
        }

        public override async Task WriteAsync(
            Guid runId,
            IReadOnlyList<ProbeRequest> requests,
            CancellationToken cancellationToken)
        {
            var entries = requests.Select(request => new KeyValuePair<RedisKey, RedisValue>(
                Key(runId, request.Id),
                request.Id * 17L + 3)).ToArray();
            await _database.StringSetAsync(entries).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public override Task SeedAsync(Guid runId, int count)
        {
            var entries = Enumerable.Range(0, count)
                .Select(id => new KeyValuePair<RedisKey, RedisValue>(Key(runId, id), id * 17L + 3))
                .ToArray();
            return _database.StringSetAsync(entries);
        }

        public override async Task ReadAsync(
            Guid runId,
            IReadOnlyList<ProbeRequest> requests,
            CancellationToken cancellationToken)
        {
            var values = await _database.StringGetAsync(
                    requests.Select(request => (RedisKey)Key(runId, request.Id)).ToArray())
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (values.Length != requests.Count)
            {
                throw new InvalidOperationException($"Redis returned {values.Length} values; expected {requests.Count}.");
            }

            for (var index = 0; index < values.Length; index++)
            {
                var id = requests[index].Id;
                if (!long.TryParse(values[index], out var value) || value != id * 17L + 3)
                {
                    throw new InvalidOperationException($"Redis value mismatch for item {id}.");
                }
            }
        }

        public override async Task VerifyAsync(Guid runId, int expectedCount)
        {
            var keys = FindKeys(runId);
            if (keys.Length != expectedCount)
            {
                throw new InvalidOperationException($"Redis found {keys.Length} keys; expected {expectedCount}.");
            }

            var expectedKeys = new HashSet<string>(
                Enumerable.Range(0, expectedCount).Select(id => Key(runId, id)),
                StringComparer.Ordinal);
            foreach (var key in keys)
            {
                if (!expectedKeys.Remove(key.ToString()))
                {
                    throw new InvalidOperationException($"Redis found an unexpected key '{key}'.");
                }
            }

            var values = await _database.StringGetAsync(keys).ConfigureAwait(false);
            if (values.Length != expectedCount)
            {
                throw new InvalidOperationException($"Redis returned {values.Length} values; expected {expectedCount}.");
            }

            for (var index = 0; index < values.Length; index++)
            {
                var key = keys[index].ToString();
                var id = int.Parse(key[(key.LastIndexOf(':') + 1)..]);
                if (!long.TryParse(values[index], out var value) || value != id * 17L + 3)
                {
                    throw new InvalidOperationException($"Redis value mismatch for item {id}.");
                }
            }
        }

        public override ValueTask DisposeAsync() => _connection.DisposeAsync();

        private RedisKey[] FindKeys(Guid runId)
        {
            var endpoint = _connection.GetEndPoints().Single();
            var server = _connection.GetServer(endpoint);
            return server.Keys(pattern: $"requestbatcher:scheduling:{runId:N}:*").ToArray();
        }

        private static string Key(Guid runId, int id) => $"requestbatcher:scheduling:{runId:N}:{id}";
    }
}
