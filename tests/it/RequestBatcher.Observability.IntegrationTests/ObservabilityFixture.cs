using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using Xunit;

namespace RequestBatcher.Observability.IntegrationTests;

/// <summary>
/// Owns the disposable Docker environment used by the live observability integration test.
/// </summary>
public sealed class ObservabilityFixture : IAsyncLifetime
{
    private const string PostgresImage = "postgres:17.6-alpine";
    private const string LgtmImage = "grafana/otel-lgtm:0.29.0";
    private const int LgtmGrafanaPort = 3000;
    private const int LgtmOtlpHttpPort = 4318;
    private const int ServicePort = 8080;

    private readonly string _repositoryRoot = FindRepositoryRoot();
    private INetwork? _network;
    private IFutureDockerImage? _cartImage;
    private IFutureDockerImage? _pricingImage;
    private IContainer? _postgres;
    private IContainer? _lgtm;
    private IContainer? _pricing;
    private IContainer? _cart;
    private int _disposed;

    /// <summary>
    /// Gets the Cart endpoint mapped to the test host.
    /// </summary>
    public Uri CartUrl { get; private set; } = null!;

    /// <summary>
    /// Gets the Grafana endpoint mapped to the test host.
    /// </summary>
    public Uri GrafanaUrl { get; private set; } = null!;

    /// <summary>
    /// Starts the environment when live integration tests are explicitly enabled.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (!IsEnabled())
        {
            return;
        }

        try
        {
            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var cancellationToken = startupTimeout.Token;
            _network = new NetworkBuilder().Build();
            await _network.CreateAsync(cancellationToken).ConfigureAwait(false);

            _cartImage = BuildSampleImage("cart");
            _pricingImage = BuildSampleImage("pricing");
            await _cartImage.CreateAsync(cancellationToken).ConfigureAwait(false);
            await _pricingImage.CreateAsync(cancellationToken).ConfigureAwait(false);

            _postgres = CreatePostgres();
            _lgtm = CreateLgtm();
            _pricing = CreateService(_pricingImage, "pricing", new Dictionary<string, string>
            {
                ["ConnectionStrings__Products"] =
                    "Host=postgres;Database=requestbatcher_observability;Username=postgres;Password=postgres",
                ["OTEL_SERVICE_NAME"] = "pricing",
                ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = $"http://lgtm:{LgtmOtlpHttpPort}",
                ["OTEL_METRIC_EXPORT_INTERVAL"] = "10000",
                ["OTEL_TRACES_SAMPLER"] = "always_on",
                ["OTEL_SPAN_LINK_COUNT_LIMIT"] = "256",
            });
            _cart = CreateService(_cartImage, "cart", new Dictionary<string, string>
            {
                ["Pricing__BaseAddress"] = $"http://pricing:{ServicePort}",
                ["OTEL_SERVICE_NAME"] = "cart",
                ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = $"http://lgtm:{LgtmOtlpHttpPort}",
                ["OTEL_METRIC_EXPORT_INTERVAL"] = "10000",
                ["OTEL_TRACES_SAMPLER"] = "always_on",
                ["OTEL_SPAN_LINK_COUNT_LIMIT"] = "256",
            });

            await _postgres.StartAsync(cancellationToken).ConfigureAwait(false);
            await _lgtm.StartAsync(cancellationToken).ConfigureAwait(false);
            await _pricing.StartAsync(cancellationToken).ConfigureAwait(false);
            await _cart.StartAsync(cancellationToken).ConfigureAwait(false);

            CartUrl = CreateMappedUrl(_cart, ServicePort);
            GrafanaUrl = CreateMappedUrl(_lgtm, LgtmGrafanaPort);
        }
        catch (Exception exception)
        {
            await ReportStartupFailureAsync(exception).ConfigureAwait(false);
            try
            {
                await DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "Observability Testcontainers startup and cleanup both failed.", exception, cleanupException);
            }

            throw;
        }
    }

    /// <summary>
    /// Stops and removes every resource created by the fixture.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var failures = new List<Exception>();
        await DisposeResourceAsync(_cart, failures).ConfigureAwait(false);
        await DisposeResourceAsync(_pricing, failures).ConfigureAwait(false);
        await DisposeResourceAsync(_lgtm, failures).ConfigureAwait(false);
        await DisposeResourceAsync(_postgres, failures).ConfigureAwait(false);
        await DeleteImageAsync(_cartImage, failures).ConfigureAwait(false);
        await DeleteImageAsync(_pricingImage, failures).ConfigureAwait(false);
        await DisposeResourceAsync(_network, failures).ConfigureAwait(false);
        if (failures.Count > 0)
        {
            throw new AggregateException("One or more observability Testcontainers resources could not be cleaned up.", failures);
        }
    }

    private static bool IsEnabled() =>
        string.Equals(Environment.GetEnvironmentVariable("REQUESTBATCHER_RUN_INTEGRATION_TESTS"), "1",
            StringComparison.Ordinal);

    private IFutureDockerImage BuildSampleImage(string target) =>
        new ImageFromDockerfileBuilder()
            .WithName($"requestbatcher-it-{target}:{Guid.NewGuid():N}")
            .WithContextDirectory(_repositoryRoot)
            .WithDockerfileDirectory(_repositoryRoot)
            .WithDockerfile("samples/RequestBatcher.Observability/Dockerfile")
            .WithTarget(target)
            .Build();

    private IContainer CreatePostgres()
    {
        var initSql = Path.Combine(_repositoryRoot, "samples", "RequestBatcher.Observability", "init.sql");
        return new ContainerBuilder(PostgresImage)
            .WithNetwork(_network!)
            .WithNetworkAliases("postgres")
            .WithEnvironment("POSTGRES_DB", "requestbatcher_observability")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_USER", "postgres")
            .WithResourceMapping(
                new FileInfo(initSql),
                new FileInfo("/docker-entrypoint-initdb.d/001-products.sql"))
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilInternalTcpPortIsAvailable(5432, wait => wait.WithTimeout(TimeSpan.FromMinutes(2)))
                .UntilCommandIsCompleted(
                    ["pg_isready", "-U", "postgres", "-d", "requestbatcher_observability"],
                    wait => wait.WithTimeout(TimeSpan.FromMinutes(2))))
            .Build();
    }

    private IContainer CreateLgtm()
    {
        var sampleRoot = Path.Combine(_repositoryRoot, "samples", "RequestBatcher.Observability");
        return new ContainerBuilder(LgtmImage)
            .WithNetwork(_network!)
            .WithNetworkAliases("lgtm")
            .WithPortBinding(LgtmGrafanaPort, true)
            .WithResourceMapping(
                new FileInfo(Path.Combine(sampleRoot, "grafana", "provisioning", "datasources", "requestbatcher.yaml")),
                new FileInfo("/otel-lgtm/grafana/conf/provisioning/datasources/requestbatcher.yaml"))
            .WithResourceMapping(
                new FileInfo(Path.Combine(sampleRoot, "grafana", "provisioning", "dashboards", "requestbatcher.yaml")),
                new FileInfo("/otel-lgtm/grafana/conf/provisioning/dashboards/requestbatcher.yaml"))
            .WithResourceMapping(
                new DirectoryInfo(Path.Combine(sampleRoot, "grafana", "dashboards")),
                new DirectoryInfo("/otel-lgtm/grafana/conf/provisioning/dashboards/requestbatcher"))
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request
                    .ForPort((ushort)LgtmGrafanaPort)
                    .ForPath("/api/health"),
                    wait => wait.WithTimeout(TimeSpan.FromMinutes(2))))
            .Build();
    }

    private IContainer CreateService(
        IImage image,
        string alias,
        IReadOnlyDictionary<string, string> environment) =>
        new ContainerBuilder(image)
            .WithNetwork(_network!)
            .WithNetworkAliases(alias)
            .WithEnvironment(environment)
            .WithPortBinding(ServicePort, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request
                    .ForPort((ushort)ServicePort)
                    .ForPath("/health"),
                    wait => wait.WithTimeout(TimeSpan.FromMinutes(2))))
            .Build();

    private static Uri CreateMappedUrl(IContainer container, int port) =>
        new UriBuilder(Uri.UriSchemeHttp, container.Hostname, container.GetMappedPublicPort(port)).Uri;

    private async Task ReportStartupFailureAsync(Exception exception)
    {
        Console.Error.WriteLine($"Observability Testcontainers startup failed: {exception}");
        foreach (var (name, container) in new[]
        {
            ("postgres", _postgres),
            ("lgtm", _lgtm),
            ("pricing", _pricing),
            ("cart", _cart),
        })
        {
            if (container is null)
            {
                continue;
            }

            try
            {
                var logs = await container.GetLogsAsync(DateTime.MinValue, DateTime.UtcNow, false).ConfigureAwait(false);
                Console.Error.WriteLine($"--- {name} stdout ---\n{logs.Stdout}\n--- {name} stderr ---\n{logs.Stderr}");
            }
            catch (Exception logException)
            {
                Console.Error.WriteLine($"Unable to read {name} logs: {logException.Message}");
            }
        }
    }

    private static async Task DisposeResourceAsync(IAsyncDisposable? resource, ICollection<Exception> failures)
    {
        if (resource is null)
        {
            return;
        }

        try
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Unable to clean up Testcontainers resource: {exception.Message}");
            failures.Add(exception);
        }
    }

    private static async Task DeleteImageAsync(IFutureDockerImage? image, ICollection<Exception> failures)
    {
        if (image is null)
        {
            return;
        }

        try
        {
            await image.DeleteAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Unable to delete Testcontainers image: {exception.Message}");
            failures.Add(exception);
        }

        await DisposeResourceAsync(image, failures).ConfigureAwait(false);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")) &&
                Directory.Exists(Path.Combine(directory.FullName, "samples")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate the RequestBatcher repository root.");
    }
}
