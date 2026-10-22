using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.InteropServices;

internal static class GenerateTraffic
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--help"])
        {
            Console.WriteLine("Continuously query Cart. Configure CART_URL, TRAFFIC_CONCURRENCY (1–64, default 32), and TRAFFIC_DELAY_SECONDS (at least 0.05, default 0.25). Ctrl+C stops the generator.");
            return 0;
        }

        if (args.Length != 0)
        {
            Console.Error.WriteLine("Unknown arguments. Use --help.");
            return 2;
        }

        if (!int.TryParse(Environment.GetEnvironmentVariable("TRAFFIC_CONCURRENCY") ?? "32", out var concurrency)
            || concurrency is < 1 or > 64
            || !double.TryParse(Environment.GetEnvironmentVariable("TRAFFIC_DELAY_SECONDS") ?? "0.25",
                NumberStyles.Float, CultureInfo.InvariantCulture, out var delaySeconds)
            || !double.IsFinite(delaySeconds) || delaySeconds < 0.05 || delaySeconds > 86400)
        {
            Console.Error.WriteLine("TRAFFIC_CONCURRENCY must be 1–64; TRAFFIC_DELAY_SECONDS must be 0.05–86400.");
            return 2;
        }

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopping.Cancel();
        };
        using var termination = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(
            PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                stopping.Cancel();
            });
        using var client = new HttpClient
        {
            BaseAddress = new Uri((Environment.GetEnvironmentVariable("CART_URL") ?? "http://localhost:5081").TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(10)
        };
        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(worker =>
            RunWorkerAsync(client, worker, TimeSpan.FromSeconds(delaySeconds), stopping.Token))).ConfigureAwait(false);
        return 0;
    }

    private static async Task RunWorkerAsync(HttpClient client, int worker, TimeSpan delay, CancellationToken stopping)
    {
        var iteration = 0;
        while (!stopping.IsCancellationRequested)
        {
            var count = Random.Shared.Next(2, 11);
            var items = Enumerable.Range(0, count)
                .Select(_ => new CartItem(Random.Shared.Next(1, 101), Random.Shared.Next(1, 4)))
                .ToArray();
            var started = Stopwatch.GetTimestamp();
            try
            {
                using var response = await client.PostAsJsonAsync("cart/quote", items, stopping).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var quote = await response.Content.ReadFromJsonAsync<CartQuote>(cancellationToken: stopping).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The quote is empty.");
                ValidateQuote(items, quote);
                Console.WriteLine(FormattableString.Invariant(
                    $"worker={worker} items={items.Length} total={quote.Total:F2} duration_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2}"));
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Cart quote failed: worker={worker} items={count} {exception.Message}");
            }

            iteration = (iteration + 1) % 100;
            try
            {
                await Task.Delay(delay, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static void ValidateQuote(CartItem[] items, CartQuote quote)
    {
        if (quote.Lines is null || quote.Lines.Length != items.Length)
        {
            throw new InvalidOperationException("The quote has an unexpected number of lines.");
        }

        var expectedTotal = 0m;
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            var line = quote.Lines[index];
            // The deterministic database seed lets the generator detect incorrect prices as well as dropped items.
            var price = item.ProductId * 1.25m;
            var total = price * item.Quantity;
            if (line is null || line.ProductId != item.ProductId || line.Quantity != item.Quantity
                || line.UnitPrice != price || line.LineTotal != total)
            {
                throw new InvalidOperationException("The quote does not match the requested cart.");
            }

            expectedTotal += total;
        }

        if (quote.Total != expectedTotal)
        {
            throw new InvalidOperationException("The quote total does not match its lines.");
        }
    }

    private sealed record CartItem(int ProductId, int Quantity);
    private sealed record QuoteLine(int ProductId, decimal UnitPrice, int Quantity, decimal LineTotal);
    private sealed record CartQuote(QuoteLine?[]? Lines, decimal Total);
}
