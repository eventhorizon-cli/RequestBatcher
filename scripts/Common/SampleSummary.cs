namespace RequestBatcher.ScriptUtilities;

/// <summary>
/// Summarizes finite measurement samples using nearest-rank percentiles.
/// </summary>
/// <param name="Count">The number of samples; zero identifies an empty summary.</param>
/// <param name="Mean">The arithmetic mean, or zero for no samples.</param>
/// <param name="P50">The nearest-rank 50th percentile, or zero for no samples.</param>
/// <param name="P95">The nearest-rank 95th percentile, or zero for no samples.</param>
/// <param name="P99">The nearest-rank 99th percentile, or zero for no samples.</param>
/// <param name="Max">The maximum sample, or zero for no samples.</param>
public sealed record SampleSummary(int Count, double Mean, double P50, double P95, double P99, double Max)
{
    /// <summary>
    /// Copies and summarizes the samples without changing their original order.
    /// A percentile selects sorted index <c>ceil(sampleCount * percentile) - 1</c> without interpolation.
    /// </summary>
    /// <param name="samples">Finite values expressed in a common unit.</param>
    /// <returns>A summary in the same unit. Empty input returns zero fields with <see cref="Count"/> equal to zero.</returns>
    /// <exception cref="ArgumentNullException">The input is null.</exception>
    /// <exception cref="ArgumentException">A sample is NaN or infinity.</exception>
    public static SampleSummary Calculate(IEnumerable<double> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var sorted = samples.ToArray();
        if (sorted.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException("Samples must be finite.", nameof(samples));
        }

        if (sorted.Length == 0)
        {
            return new SampleSummary(0, 0, 0, 0, 0, 0);
        }

        Array.Sort(sorted);
        double Percentile(double fraction) => sorted[(int)Math.Ceiling(sorted.Length * fraction) - 1];
        return new SampleSummary(
            sorted.Length, sorted.Average(), Percentile(0.50), Percentile(0.95), Percentile(0.99), sorted[^1]);
    }
}
