using System.Diagnostics;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
var app = builder.Build();
var calls = 0;
var items = 0;
var active = 0;
var peak = 0;
app.MapGet("/work", async (int delay, int count, HttpResponse response) =>
{
    var begin = Stopwatch.GetTimestamp();
    var current = Interlocked.Increment(ref active);
    var previous = Volatile.Read(ref peak);
    while (current > previous)
    {
        var observed = Interlocked.CompareExchange(ref peak, current, previous);
        if (observed == previous)
        {
            break;
        }
        previous = observed;
    }
    try
    {
        await Task.Delay(delay).ConfigureAwait(false);
        Interlocked.Increment(ref calls);
        Interlocked.Add(ref items, count);
        response.Headers["X-Service-Duration-Ms"] = Stopwatch.GetElapsedTime(begin).TotalMilliseconds
            .ToString("R", CultureInfo.InvariantCulture);
        return Results.Text(count.ToString(CultureInfo.InvariantCulture));
    }
    finally
    {
        Interlocked.Decrement(ref active);
    }
});
app.MapPost("/reset", () =>
{
    if (Volatile.Read(ref active) != 0)
    {
        return Results.Conflict();
    }
    calls = items = peak = 0;
    return Results.NoContent();
});
app.MapGet("/stats", () => new { Calls = calls, Items = items, Peak = peak });
await app.RunAsync();
