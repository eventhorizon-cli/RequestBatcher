#:property TargetFramework=net10.0
#:property PublishAot=false
#:property LangVersion=12.0
#:project ../tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj

using System.Diagnostics.Tracing;
using RequestBatcher.Benchmarks.InMemory;

internal static class ProfileTracingAllocations
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--help"])
        {
            Console.WriteLine("ProfileTracingAllocations <Disabled|Sample0|Sample100> [waves=500]\nRuns ten warmup waves, then marked allocation diagnostics using the 1,024-single-submission, 20 ms tracing benchmark. Run each mode in a separate process; diagnostic timing is not a performance result.");
            return 0;
        }

        if (args.Length is < 1 or > 2 ||
            !Enum.TryParse<TracingIoOverheadBenchmarks.ListenerMode>(args[0], out var mode) ||
            mode is not (TracingIoOverheadBenchmarks.ListenerMode.Disabled or
                TracingIoOverheadBenchmarks.ListenerMode.Sample0 or
                TracingIoOverheadBenchmarks.ListenerMode.Sample100) ||
            (args.Length == 2 && (!int.TryParse(args[1], out var parsedWaves) || parsedWaves <= 0)))
        {
            Console.Error.WriteLine("Invalid arguments. Use --help.");
            return 2;
        }

        var waves = args.Length == 2 ? int.Parse(args[1]) : 500;
        var benchmark = new TracingIoOverheadBenchmarks { TracingMode = mode };
        benchmark.GlobalSetup();
        try
        {
            for (var index = 0; index < 10; index++)
            {
                await benchmark.SingleSubmissionsAsync().ConfigureAwait(false);
            }

            using var markers = new AllocationProbeEvents();
            // Ensure marker metadata is initialized before measuring process-wide allocations.
            markers.Ready();
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            markers.Start(waves);
            for (var index = 0; index < waves; index++)
            {
                await benchmark.SingleSubmissionsAsync().ConfigureAwait(false);
            }
            markers.Stop(waves);
            var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            Console.WriteLine(
                $"Allocation probe: Mode={mode}, Waves={waves}, Requests={(long)waves * 1_024}, " +
                $"AllocatedBytes={allocatedBytes}, BytesPerWave={(double)allocatedBytes / waves:F2}, " +
                $"BytesPerRequest={(double)allocatedBytes / waves / 1_024:F2}");
        }
        finally
        {
            await benchmark.GlobalCleanupAsync().ConfigureAwait(false);
        }

        return 0;
    }

    [EventSource(Name = "RequestBatcher-AllocationProbe")]
    private sealed class AllocationProbeEvents : EventSource
    {
        [Event(1, Level = EventLevel.Informational, Opcode = EventOpcode.Start, Task = (EventTask)1)]
        public void Start(int waveCount) => WriteEvent(1, waveCount);

        [Event(2, Level = EventLevel.Informational, Opcode = EventOpcode.Stop, Task = (EventTask)1)]
        public void Stop(int waveCount) => WriteEvent(2, waveCount);

        [Event(3, Level = EventLevel.Informational)]
        public void Ready() => WriteEvent(3);
    }
}
