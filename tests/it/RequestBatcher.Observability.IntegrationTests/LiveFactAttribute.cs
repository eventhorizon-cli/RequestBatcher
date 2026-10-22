using Xunit;

namespace RequestBatcher.Observability.IntegrationTests;

public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("REQUESTBATCHER_RUN_INTEGRATION_TESTS") != "1")
        {
            Skip = "Start the observability Compose sample and set REQUESTBATCHER_RUN_INTEGRATION_TESTS=1; see tests/it/README.md.";
        }
    }
}
