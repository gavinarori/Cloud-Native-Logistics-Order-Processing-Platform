using Azure.Identity;
using Microsoft.Azure.Cosmos;
using InventoryService.Endpoints;
using InventoryService.Middleware;
using InventoryService.Queries;
using InventoryService.Services;

var builder = WebApplication.CreateBuilder(args);

// ── 1. Key Vault (skipped locally when URI is blank) ──────────────────────────
var keyVaultUri = builder.Configuration["Azure:KeyVaultUri"];
var credential  = new DefaultAzureCredential();

if (!string.IsNullOrWhiteSpace(keyVaultUri) && !builder.Environment.IsDevelopment())
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), credential);

// ── 2. Data source ────────────────────────────────────────────────────────────
//    UseInMemoryData = true  → realistic seed data, zero Azure setup
//    UseInMemoryData = false → Azure Cosmos DB (Managed Identity, no key)
var useInMemory = builder.Configuration.GetValue<bool>("UseInMemoryData");

if (useInMemory)
{
    // Local dev: in-memory seed data with 12 realistic SKUs
    builder.Services.AddSingleton<IInventoryDataSource, InMemoryInventoryDataSource>();
}
else
{
    // Production: Cosmos DB with Managed Identity auth
    var cosmosEndpoint = builder.Configuration["Azure:CosmosDb:AccountEndpoint"]
        ?? throw new InvalidOperationException("Azure:CosmosDb:AccountEndpoint not set.");

    builder.Services.AddSingleton(_ => new CosmosClient(
        cosmosEndpoint, credential,
        new CosmosClientOptions
        {
            SerializerOptions = new CosmosSerializationOptions
            {
                PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
            },
            ConnectionMode = ConnectionMode.Direct,
            MaxRetryAttemptsOnRateLimitedRequests = 9,
            MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(30)
        }));

    builder.Services.AddSingleton<IInventoryDataSource, CosmosInventoryDataSource>();
}

// ── 3. Cache — Redis (prod) or in-memory (local dev) ─────────────────────────
var redisConn = builder.Configuration["Azure:Redis:ConnectionString"];

if (!string.IsNullOrWhiteSpace(redisConn))
{
    builder.Services.AddStackExchangeRedisCache(o =>
    {
        o.Configuration = redisConn;
        o.InstanceName  = "inventory:";
    });
}
else
{
    // Local dev: in-memory distributed cache
    builder.Services.AddDistributedMemoryCache();
}

// ── 4. Domain services ────────────────────────────────────────────────────────
builder.Services.AddScoped<InventoryQueries>();

// ── 5. Swagger ────────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o => o.SwaggerDoc("v1", new()
{
    Title       = "Inventory Service API",
    Version     = "v1",
    Description = "Pure read service. Data: Redis → Cosmos DB (or in-memory locally).\n" +
                  "Never touches Azure SQL — completely isolated from write throughput."
}));

// ── 6. Health checks ──────────────────────────────────────────────────────────
builder.Services.AddHealthChecks();

// ── 7. CORS — allow Blazor web app ───────────────────────────────────────────
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("https://localhost:5001", "http://localhost:5000")
     .AllowAnyHeader().AllowAnyMethod()));

// ── 8. Telemetry (optional) ───────────────────────────────────────────────────
var aiConn = builder.Configuration["AppInsights--ConnectionString"];
if (!string.IsNullOrWhiteSpace(aiConn))
    builder.Services.AddApplicationInsightsTelemetry(o => o.ConnectionString = aiConn);

// ── Build ─────────────────────────────────────────────────────────────────────
var app = builder.Build();

app.UseMiddleware<ExceptionMiddleware>();
app.UseCors();
app.UseHttpsRedirection();

app.UseSwagger();
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/swagger/v1/swagger.json", "Inventory Service v1");
    o.RoutePrefix = string.Empty;
});

app.MapInventoryEndpoints();
app.MapHealthChecks("/health");

app.Run();
