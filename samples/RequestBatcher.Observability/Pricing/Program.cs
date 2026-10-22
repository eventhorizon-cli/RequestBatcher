using Npgsql;
using RequestBatcher.Observability.Pricing;

var builder = WebApplication.CreateBuilder(args);
SampleTelemetry.Configure(builder, "pricing");
var connectionString = builder.Configuration.GetConnectionString("Products")
    ?? throw new InvalidOperationException("Set ConnectionStrings__Products to the sample PostgreSQL connection string.");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
app.MapPricingEndpoints();
await app.RunAsync();
