using Azure.Identity;
using Microsoft.Azure.Cosmos;
using InventoryService.Data;
using InventoryService.Endpoints;
using InventoryService.Queries;

var builder = WebApplication.CreateBuilder(args);

// ══════════════════════════════════════════════════════════
// 1. ZERO TRUST — Key Vault via Managed Identity
// ══════════════════════════════════════════════════════════
var keyVaultUri = builder.Configuration["Azure:KeyVaultUri"]
    ?? throw new InvalidOperationException("Azure:KeyVaultUri must be set.");

var credential = new DefaultAzureCredential();
builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), credential);

// ══════════════════════════════════════════════════════════
// 2. COSMOS DB — Managed Identity (no primary key)
//    ConsistencyLevel.Session = default; reads from nearest region.
// ══════════════════════════════════════════════════════════
var cosmosEndpoint = builder.Configuration["Azure:CosmosDb:AccountEndpoint"]
    ?? throw new InvalidOperationException("Cosmos DB endpoint not configured.");

builder.Services.AddSingleton(_ => new CosmosClient(
    cosmosEndpoint,
    credential,
    new CosmosClientOptions
    {
        SerializerOptions      = new CosmosSerializationOptions
        {
            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
        },
        // ApplicationPreferredRegions: route reads to the nearest replica.
        // In prod: populate from environment variable set per Container Apps region.
        ApplicationPreferredRegions = ["East US", "West Europe", "Southeast Asia"],
        ConnectionMode = ConnectionMode.Direct,
        MaxRetryAttemptsOnRateLimitedRequests = 9,
        MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(30)
    }));

builder.Services.AddSingleton<CosmosDbContext>();

// ══════════════════════════════════════════════════════════
// 3. REDIS — cache-aside, connection string from Key Vault
// ══════════════════════════════════════════════════════════
var redisConn = builder.Configuration["Redis--ConnectionString"]
    ?? throw new InvalidOperationException("Redis--ConnectionString not found in Key Vault.");

builder.Services.AddStackExchangeRedisCache(opts =>
{
    opts.Configuration = redisConn;
    opts.InstanceName  = "inventory:";
});

// ══════════════════════════════════════════════════════════
// 4. DOMAIN SERVICES
// ══════════════════════════════════════════════════════════
builder.Services.AddScoped<InventoryQueries>();

// ══════════════════════════════════════════════════════════
// 5. API INFRASTRUCTURE
// ══════════════════════════════════════════════════════════
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(opts =>
    opts.SwaggerDoc("v1", new()
    {
        Title       = "Inventory Service API",
        Version     = "v1",
        Description = "Pure read service. Data served from Redis → Cosmos DB. " +
                      "Never touches Azure SQL — eliminates Noisy Neighbor."
    }));

builder.Services.AddApplicationInsightsTelemetry(opts =>
    opts.ConnectionString = builder.Configuration["AppInsights--ConnectionString"]);

// ══════════════════════════════════════════════════════════
// 6. HEALTH CHECKS
// ══════════════════════════════════════════════════════════
builder.Services.AddHealthChecks()
    .AddCosmosDb(
        sp => sp.GetRequiredService<CosmosClient>(),
        name: "cosmos-db",
        tags: ["db"])
    .AddRedis(
        redisConn,
        name: "redis",
        tags: ["cache"]);

// ══════════════════════════════════════════════════════════
// 7. BUILD & PIPELINE
// ══════════════════════════════════════════════════════════
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(o => { o.RoutePrefix = string.Empty; });
}

app.UseHttpsRedirection();

app.MapInventoryEndpoints();

app.MapHealthChecks("/health");
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("db") });
app.MapHealthChecks("/health/live",  new() { Predicate = _ => false });

app.Run();