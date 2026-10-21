using Npgsql;
using RequestBatcher;
using RequestBatcher.Observability.Pricing;

var builder = WebApplication.CreateBuilder(args);
SampleTelemetry.Configure(builder, "requestbatcher-pricing");
var connectionString = builder.Configuration.GetConnectionString("Products")
    ?? throw new InvalidOperationException("Set ConnectionStrings__Products to the sample PostgreSQL connection string.");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddRequestBatcher<int, Product?, ProductReadHandler>(
    "product-reads", ServiceLifetime.Scoped, options =>
    {
        options.BatchSize = 16;
        options.MaxConcurrency = 1;
        options.MaxPendingRequests = 1024;
    });
var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
app.MapPricingEndpoints();
await app.RunAsync();
