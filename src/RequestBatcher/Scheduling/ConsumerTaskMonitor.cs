namespace RequestBatcher.Scheduling;

internal sealed class ConsumerTaskMonitor
{
    public ConsumerTaskMonitor(
        IEnumerable<Task> consumers,
        CancellationToken stoppingToken,
        string requestTypeName,
        Action<Exception> onFailure)
    {
        ArgumentNullException.ThrowIfNull(consumers);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestTypeName);
        ArgumentNullException.ThrowIfNull(onFailure);

        Completion = MonitorAsync(
            consumers.ToArray(),
            stoppingToken,
            requestTypeName,
            onFailure);
    }

    public Task Completion { get; }

    private static async Task MonitorAsync(
        IReadOnlyCollection<Task> consumers,
        CancellationToken stoppingToken,
        string requestTypeName,
        Action<Exception> onFailure)
    {
        var remainingConsumers = new HashSet<Task>(consumers);
        while (remainingConsumers.Count > 0)
        {
            var completedConsumer = await Task.WhenAny(remainingConsumers).ConfigureAwait(false);
            remainingConsumers.Remove(completedConsumer);

            if (stoppingToken.IsCancellationRequested)
            {
                continue;
            }

            var exception = completedConsumer.Exception?.GetBaseException() ??
                new InvalidOperationException(
                    $"A request batch consumer for '{requestTypeName}' stopped unexpectedly.");
            onFailure(exception);
            return;
        }
    }
}
