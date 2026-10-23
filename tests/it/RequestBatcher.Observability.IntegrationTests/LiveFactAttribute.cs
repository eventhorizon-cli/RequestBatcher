using Xunit;

namespace RequestBatcher.Observability.IntegrationTests;

public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("REQUESTBATCHER_RUN_INTEGRATION_TESTS") != "1")
        {
            Skip = "Set REQUESTBATCHER_RUN_INTEGRATION_TESTS=1 to start the disposable Testcontainers environment; see tests/it/README.md.";
        }
    }
}
