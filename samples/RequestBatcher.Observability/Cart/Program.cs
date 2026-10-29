using RequestBatcher;
using RequestBatcher.Observability.Cart;

var builder = WebApplication.CreateBuilder(args);
SampleTelemetry.Configure(builder);
builder.Services.AddHttpClient("pricing", client =>
{
    var baseAddress = builder.Configuration["Pricing:BaseAddress"] ?? "http://localhost:5082";
    client.BaseAddress = new Uri(baseAddress.EndsWith('/') ? baseAddress : $"{baseAddress}/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddDelegateRequestBatcher<int, Product?>(
    "pricing-queries",
    options =>
    {
        options.BatchSize = 20;
        options.MaxConcurrency = 2;
        options.MaxPendingRequests = 1024;
    });
builder.Services.AddRequestBatcher<int, Product?, PricingQueryHandler>(
    "pricing-queries-ordinary",
    ServiceLifetime.Scoped,
    options =>
    {
        options.BatchSize = 20;
        options.MaxConcurrency = 2;
        options.MaxPendingRequests = 1024;
    });

var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
app.MapCartEndpoints();
await app.RunAsync();
