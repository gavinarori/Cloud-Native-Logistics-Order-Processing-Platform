#!/usr/bin/env bash
# =============================================================================
# setup-inventory-service.sh
#
# Creates the InventoryService — read-only ASP.NET Core Minimal API
# that serves inventory data from Redis → Cosmos DB.
#
# Local dev: runs entirely on in-memory seed data.
# No Cosmos DB, no Redis, no Azure account needed to run locally.
#
# Run from ~/Desktop/learn/BlazorApp1/:
#   chmod +x setup-inventory-service.sh
#   ./setup-inventory-service.sh
# =============================================================================
set -e

PROJECT_NAME="InventoryService"
PROJECT_DIR="$(pwd)/$PROJECT_NAME"

echo ""
echo "======================================================"
echo "  Setting up $PROJECT_NAME"
echo "======================================================"

# ── 1. Scaffold ───────────────────────────────────────────────────────────────
dotnet new web -n "$PROJECT_NAME" --framework net10.0 --force
cd "$PROJECT_DIR"

rm -f Program.cs appsettings.json appsettings.Development.json

echo "✓ Project scaffolded, boilerplate removed"

# ── 2. Create folder structure ────────────────────────────────────────────────
mkdir -p Data/Entities
mkdir -p Endpoints
mkdir -p Middleware
mkdir -p Models
mkdir -p Queries
mkdir -p Services

echo "✓ Folders created"

# ── 3. Project file ───────────────────────────────────────────────────────────
cat > "$PROJECT_NAME.csproj" << 'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>InventoryService</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <!-- Zero Trust auth -->
    <PackageReference Include="Azure.Identity"                                     Version="1.13.2" />
    <PackageReference Include="Azure.Extensions.AspNetCore.Configuration.Secrets" Version="1.3.2" />

    <!-- Cosmos DB — production read model -->
    <PackageReference Include="Microsoft.Azure.Cosmos"                            Version="3.46.0" />

    <!-- Redis — cache-aside layer -->
    <PackageReference Include="Microsoft.Extensions.Caching.StackExchangeRedis"   Version="9.0.5" />

    <!-- Swagger -->
    <PackageReference Include="Swashbuckle.AspNetCore"                            Version="9.0.1" />

    <!-- Telemetry -->
    <PackageReference Include="Microsoft.ApplicationInsights.AspNetCore"          Version="2.22.0" />
  </ItemGroup>

</Project>
CSPROJ

echo "✓ InventoryService.csproj written"

# ── 4. appsettings.json ───────────────────────────────────────────────────────
cat > appsettings.json << 'JSON'
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Azure": {
    "KeyVaultUri": "",
    "CosmosDb": {
      "AccountEndpoint": "",
      "DatabaseName": "LogisticsDb",
      "InventoryContainerName": "inventory"
    },
    "Redis": {
      "ConnectionString": ""
    }
  },
  "UseInMemoryData": false
}
JSON

# ── 5. appsettings.Development.json ──────────────────────────────────────────
cat > appsettings.Development.json << 'JSON'
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug"
    }
  },
  "UseInMemoryData": true
}
JSON

echo "✓ appsettings files written"

# ── 6. Models/InventoryDto.cs ─────────────────────────────────────────────────
cat > Models/InventoryDto.cs << 'CS'
namespace InventoryService.Models;

public sealed record InventoryItemDto(
    Guid     ProductId,
    string   ProductName,
    string   Sku,
    string   Category,
    string   Region,
    int      StockLevel,
    int      ReorderPoint,
    decimal  UnitPrice,
    string   WarehouseLocation,
    string   Status,
    DateTime LastSyncedAt
);

public sealed record InventorySummaryDto(
    int      TotalProducts,
    int      LowStockCount,
    int      OutOfStockCount,
    int      TotalUnits,
    DateTime ComputedAt
);
CS

echo "✓ Models/InventoryDto.cs written"

# ── 7. Data/Entities/InventoryDocument.cs ────────────────────────────────────
cat > Data/Entities/InventoryDocument.cs << 'CS'
using Newtonsoft.Json;

namespace InventoryService.Data.Entities;

/// <summary>
/// Cosmos DB document in the "inventory" container.
/// Partition key: /region  (AMER | EMEA | APAC)
///
/// Populated by Azure Data Factory CDC from Azure SQL.
/// Typical sync lag: 1–5 seconds (eventual consistency).
/// The LastSyncedAt field lets the UI surface how stale the data is.
/// </summary>
public sealed class InventoryDocument
{
    [JsonProperty("id")]
    public string  Id               { get; set; } = string.Empty;

    [JsonProperty("productId")]
    public Guid    ProductId        { get; set; }

    [JsonProperty("productName")]
    public string  ProductName      { get; set; } = string.Empty;

    [JsonProperty("sku")]
    public string  Sku              { get; set; } = string.Empty;

    [JsonProperty("category")]
    public string  Category         { get; set; } = string.Empty;

    [JsonProperty("region")]
    public string  Region           { get; set; } = string.Empty;

    [JsonProperty("stockLevel")]
    public int     StockLevel       { get; set; }

    [JsonProperty("reorderPoint")]
    public int     ReorderPoint     { get; set; }

    [JsonProperty("unitPrice")]
    public decimal UnitPrice        { get; set; }

    [JsonProperty("warehouseLocation")]
    public string  WarehouseLocation { get; set; } = string.Empty;

    [JsonProperty("lastSyncedAt")]
    public DateTime LastSyncedAt    { get; set; }
}
CS

echo "✓ Data/Entities/InventoryDocument.cs written"

# ── 8. Services/IInventoryDataSource.cs ───────────────────────────────────────
cat > Services/IInventoryDataSource.cs << 'CS'
using InventoryService.Models;

namespace InventoryService.Services;

/// <summary>
/// Abstraction over the data source so we can swap:
///   Production → CosmosInventoryDataSource (Cosmos DB)
///   Local dev  → InMemoryInventoryDataSource (seed data, no Azure needed)
/// </summary>
public interface IInventoryDataSource
{
    Task<List<InventoryItemDto>> GetInventoryAsync(
        string? region = null, string? category = null, CancellationToken ct = default);

    Task<InventoryItemDto?> GetBySkuAsync(string sku, CancellationToken ct = default);

    Task<List<string>> GetCategoriesAsync(CancellationToken ct = default);

    Task<InventorySummaryDto> GetSummaryAsync(
        string? region = null, CancellationToken ct = default);
}
CS

echo "✓ Services/IInventoryDataSource.cs written"

# ── 9. Services/InMemoryInventoryDataSource.cs ────────────────────────────────
cat > Services/InMemoryInventoryDataSource.cs << 'CS'
using InventoryService.Models;

namespace InventoryService.Services;

/// <summary>
/// In-memory data source with realistic seed data.
/// Used in local development — no Azure services required.
/// Swap for CosmosInventoryDataSource in production.
/// </summary>
public sealed class InMemoryInventoryDataSource : IInventoryDataSource
{
    private static readonly List<InventoryItemDto> _seed =
    [
        new(Guid.NewGuid(), "Pro Laptop 15\"",       "LAPTOP-001",  "Electronics", "EMEA", 45,  10, 1299.99m, "Warehouse-A-Shelf-3", "InStock",    DateTime.UtcNow),
        new(Guid.NewGuid(), "Wireless Mouse",         "MOUSE-001",   "Peripherals", "EMEA",  8,  20,   29.99m, "Warehouse-A-Shelf-7", "LowStock",   DateTime.UtcNow),
        new(Guid.NewGuid(), "USB-C Hub 7-in-1",      "USBHUB-001",  "Peripherals", "EMEA",  0,  15,   49.99m, "Warehouse-B-Shelf-1", "OutOfStock", DateTime.UtcNow),
        new(Guid.NewGuid(), "Mechanical Keyboard",    "KB-001",      "Peripherals", "EMEA", 32,  10,   89.99m, "Warehouse-A-Shelf-2", "InStock",    DateTime.UtcNow),
        new(Guid.NewGuid(), "4K Monitor 27\"",        "MON-001",     "Electronics", "AMER", 12,  5,  499.99m,  "Warehouse-C-Shelf-1", "InStock",    DateTime.UtcNow),
        new(Guid.NewGuid(), "Standing Desk",          "DESK-001",    "Furniture",   "AMER",  3,  5,  649.99m,  "Warehouse-C-Shelf-4", "LowStock",   DateTime.UtcNow),
        new(Guid.NewGuid(), "Ergonomic Chair",        "CHAIR-001",   "Furniture",   "AMER", 18,  8,  399.99m,  "Warehouse-C-Shelf-5", "InStock",    DateTime.UtcNow),
        new(Guid.NewGuid(), "Noise-Cancel Headphones","HEADPH-001",  "Electronics", "APAC", 27, 10,  249.99m,  "Warehouse-D-Shelf-2", "InStock",    DateTime.UtcNow),
        new(Guid.NewGuid(), "Webcam HD 1080p",        "CAM-001",     "Peripherals", "APAC",  5, 15,   79.99m,  "Warehouse-D-Shelf-3", "LowStock",   DateTime.UtcNow),
        new(Guid.NewGuid(), "Laptop Stand",           "STAND-001",   "Peripherals", "APAC",  0, 10,   39.99m,  "Warehouse-D-Shelf-6", "OutOfStock", DateTime.UtcNow),
        new(Guid.NewGuid(), "SSD 1TB External",       "SSD-001",     "Storage",     "EMEA", 55,  20,  109.99m, "Warehouse-A-Shelf-9", "InStock",    DateTime.UtcNow),
        new(Guid.NewGuid(), "NAS Drive 4TB",          "NAS-001",     "Storage",     "AMER",  7,  10,  189.99m, "Warehouse-C-Shelf-8", "LowStock",   DateTime.UtcNow),
    ];

    public Task<List<InventoryItemDto>> GetInventoryAsync(
        string? region = null, string? category = null, CancellationToken ct = default)
    {
        var result = _seed.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(region))
            result = result.Where(i => i.Region == region);
        if (!string.IsNullOrWhiteSpace(category))
            result = result.Where(i => i.Category == category);
        return Task.FromResult(result.ToList());
    }

    public Task<InventoryItemDto?> GetBySkuAsync(string sku, CancellationToken ct = default)
        => Task.FromResult(_seed.FirstOrDefault(i => i.Sku == sku));

    public Task<List<string>> GetCategoriesAsync(CancellationToken ct = default)
        => Task.FromResult(_seed.Select(i => i.Category).Distinct().Order().ToList());

    public Task<InventorySummaryDto> GetSummaryAsync(
        string? region = null, CancellationToken ct = default)
    {
        var items = string.IsNullOrWhiteSpace(region)
            ? _seed
            : _seed.Where(i => i.Region == region).ToList();

        return Task.FromResult(new InventorySummaryDto(
            TotalProducts:   items.Count,
            LowStockCount:   items.Count(i => i.Status == "LowStock"),
            OutOfStockCount: items.Count(i => i.Status == "OutOfStock"),
            TotalUnits:      items.Sum(i => i.StockLevel),
            ComputedAt:      DateTime.UtcNow));
    }
}
CS

echo "✓ Services/InMemoryInventoryDataSource.cs written"

# ── 10. Services/CosmosInventoryDataSource.cs ─────────────────────────────────
cat > Services/CosmosInventoryDataSource.cs << 'CS'
using InventoryService.Data.Entities;
using InventoryService.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;

namespace InventoryService.Services;

/// <summary>
/// Production data source — reads from Azure Cosmos DB (NoSQL API).
/// Partition key: /region — all per-region queries are single-partition (fast + cheap).
/// Managed Identity auth: no primary keys in config.
/// </summary>
public sealed class CosmosInventoryDataSource : IInventoryDataSource
{
    private readonly Container _container;
    private readonly ILogger<CosmosInventoryDataSource> _logger;

    public CosmosInventoryDataSource(
        CosmosClient client,
        IConfiguration config,
        ILogger<CosmosInventoryDataSource> logger)
    {
        var db        = config["Azure:CosmosDb:DatabaseName"]          ?? "LogisticsDb";
        var container = config["Azure:CosmosDb:InventoryContainerName"] ?? "inventory";
        _container    = client.GetContainer(db, container);
        _logger       = logger;
    }

    public async Task<List<InventoryItemDto>> GetInventoryAsync(
        string? region = null, string? category = null, CancellationToken ct = default)
    {
        var queryOpts = region is not null
            ? new QueryRequestOptions { PartitionKey = new PartitionKey(region) }
            : null;

        var query = _container
            .GetItemLinqQueryable<InventoryDocument>(requestOptions: queryOpts)
            .Where(d => !d.Id.StartsWith("summary:"))
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(d => d.Category == category);

        var results = new List<InventoryItemDto>();
        var feed    = query.ToFeedIterator();

        while (feed.HasMoreResults)
        {
            var page = await feed.ReadNextAsync(ct);
            _logger.LogDebug("Cosmos query consumed {RU} RU/s", page.RequestCharge);
            results.AddRange(page.Select(ToDto));
        }

        return results;
    }

    public async Task<InventoryItemDto?> GetBySkuAsync(string sku, CancellationToken ct = default)
    {
        var feed = _container
            .GetItemLinqQueryable<InventoryDocument>()
            .Where(d => d.Sku == sku)
            .ToFeedIterator();

        if (feed.HasMoreResults)
        {
            var page = await feed.ReadNextAsync(ct);
            var doc  = page.FirstOrDefault();
            return doc is null ? null : ToDto(doc);
        }

        return null;
    }

    public async Task<List<string>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var queryDef = new QueryDefinition(
            "SELECT DISTINCT VALUE c.category FROM c WHERE NOT STARTSWITH(c.id, 'summary:')");

        var results = new List<string>();
        var feed    = _container.GetItemQueryIterator<string>(queryDef);

        while (feed.HasMoreResults)
        {
            var page = await feed.ReadNextAsync(ct);
            results.AddRange(page);
        }

        return results.Where(c => !string.IsNullOrEmpty(c)).Distinct().Order().ToList();
    }

    public async Task<InventorySummaryDto> GetSummaryAsync(
        string? region = null, CancellationToken ct = default)
    {
        var regions = region is not null
            ? [region]
            : new[] { "AMER", "EMEA", "APAC" };

        int total = 0, lowStock = 0, outOfStock = 0, units = 0;
        var oldest = DateTime.UtcNow;

        foreach (var r in regions)
        {
            try
            {
                var items = await GetInventoryAsync(r, null, ct);
                total      += items.Count;
                lowStock   += items.Count(i => i.Status == "LowStock");
                outOfStock += items.Count(i => i.Status == "OutOfStock");
                units      += items.Sum(i => i.StockLevel);
                var syncTime = items.Min(i => (DateTime?)i.LastSyncedAt) ?? DateTime.UtcNow;
                if (syncTime < oldest) oldest = syncTime;
            }
            catch (CosmosException ex) when
                (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { }
        }

        return new InventorySummaryDto(total, lowStock, outOfStock, units, oldest);
    }

    private static InventoryItemDto ToDto(InventoryDocument d)
    {
        var status = d.StockLevel == 0          ? "OutOfStock"
                   : d.StockLevel <= d.ReorderPoint ? "LowStock"
                   : "InStock";

        return new InventoryItemDto(
            d.ProductId, d.ProductName, d.Sku, d.Category, d.Region,
            d.StockLevel, d.ReorderPoint, d.UnitPrice,
            d.WarehouseLocation, status, d.LastSyncedAt);
    }
}
CS

echo "✓ Services/CosmosInventoryDataSource.cs written"

# ── 11. Queries/InventoryQueries.cs ───────────────────────────────────────────
cat > Queries/InventoryQueries.cs << 'CS'
using System.Text.Json;
using InventoryService.Models;
using InventoryService.Services;
using Microsoft.Extensions.Caching.Distributed;

namespace InventoryService.Queries;

/// <summary>
/// Cache-aside layer in front of the data source.
///
/// Cache key strategy:
///   inventory:list:{region}:{category}  TTL 2 min
///   inventory:sku:{sku}                 TTL 2 min
///   inventory:categories                TTL 10 min
///   inventory:summary:{region}          TTL 5 min
///
/// Redis outage is non-fatal — falls through to Cosmos DB.
/// Cosmos DB outage returns empty/fallback — UI shows stale badge.
/// </summary>
public sealed class InventoryQueries
{
    private readonly IInventoryDataSource        _source;
    private readonly IDistributedCache           _cache;
    private readonly IConfiguration              _config;
    private readonly ILogger<InventoryQueries>   _logger;

    private static readonly JsonSerializerOptions _json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public InventoryQueries(
        IInventoryDataSource source,
        IDistributedCache cache,
        IConfiguration config,
        ILogger<InventoryQueries> logger)
    {
        _source = source;
        _cache  = cache;
        _config = config;
        _logger = logger;
    }

    public async Task<List<InventoryItemDto>> GetInventoryAsync(
        string? region = null, string? category = null, CancellationToken ct = default)
        => await CacheAsync(
            $"inventory:list:{region ?? "all"}:{category ?? "all"}",
            TimeSpan.FromMinutes(2),
            () => _source.GetInventoryAsync(region, category, ct),
            ct);

    public async Task<InventoryItemDto?> GetBySkuAsync(
        string sku, CancellationToken ct = default)
        => await CacheAsync<InventoryItemDto?>(
            $"inventory:sku:{sku}",
            TimeSpan.FromMinutes(2),
            () => _source.GetBySkuAsync(sku, ct),
            ct);

    public async Task<List<string>> GetCategoriesAsync(CancellationToken ct = default)
        => await CacheAsync(
            "inventory:categories",
            TimeSpan.FromMinutes(10),
            () => _source.GetCategoriesAsync(ct),
            ct);

    public async Task<InventorySummaryDto> GetSummaryAsync(
        string? region = null, CancellationToken ct = default)
        => await CacheAsync(
            $"inventory:summary:{region ?? "global"}",
            TimeSpan.FromMinutes(5),
            () => _source.GetSummaryAsync(region, ct),
            ct);

    // ── Generic cache-aside ───────────────────────────────────────────────────
    private async Task<T> CacheAsync<T>(
        string cacheKey, TimeSpan ttl,
        Func<Task<T>> factory, CancellationToken ct)
    {
        try
        {
            var raw = await _cache.GetStringAsync(cacheKey, ct);
            if (raw is not null)
            {
                _logger.LogDebug("Cache HIT: {Key}", cacheKey);
                return JsonSerializer.Deserialize<T>(raw, _json)!;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache read failed for {Key} — falling back to source.", cacheKey);
        }

        _logger.LogDebug("Cache MISS: {Key}", cacheKey);
        var value = await factory();

        try
        {
            await _cache.SetStringAsync(cacheKey,
                JsonSerializer.Serialize(value, _json),
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = ttl
                }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache write failed for {Key} — non-fatal.", cacheKey);
        }

        return value;
    }
}
CS

echo "✓ Queries/InventoryQueries.cs written"

# ── 12. Endpoints/InventoryReadEndpoints.cs ───────────────────────────────────
cat > Endpoints/InventoryReadEndpoints.cs << 'CS'
using InventoryService.Models;
using InventoryService.Queries;

namespace InventoryService.Endpoints;

/// <summary>
/// Pure read endpoints — no writes, no SQL.
/// All data flows: Redis (cache) → IInventoryDataSource (Cosmos or in-memory).
/// This service never talks to Azure SQL — that eliminates the Noisy Neighbor
/// problem where report queries caused checkout timeouts in the monolith.
/// </summary>
public static class InventoryReadEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/inventory")
            .WithTags("Inventory — Read");

        group.MapGet("/", GetInventoryAsync)
             .WithName("GetInventory")
             .WithSummary("List inventory items. Served from Redis → Cosmos DB (or in-memory locally).")
             .Produces<List<InventoryItemDto>>();

        group.MapGet("/summary", GetSummaryAsync)
             .WithName("GetInventorySummary")
             .WithSummary("KPI aggregates. Redis TTL 5 min.")
             .Produces<InventorySummaryDto>();

        group.MapGet("/categories", GetCategoriesAsync)
             .WithName("GetCategories")
             .WithSummary("Distinct product categories. Redis TTL 10 min.")
             .Produces<List<string>>();

        group.MapGet("/{sku}", GetBySkuAsync)
             .WithName("GetInventoryBySku")
             .WithSummary("Single item by SKU.")
             .Produces<InventoryItemDto>()
             .Produces(404);

        return app;
    }

    private static async Task<IResult> GetInventoryAsync(
        InventoryQueries queries,
        string? region   = null,
        string? category = null,
        CancellationToken ct = default)
    {
        if (region is not null && region is not ("AMER" or "EMEA" or "APAC"))
            return Results.BadRequest("Region must be AMER, EMEA, or APAC.");

        return Results.Ok(await queries.GetInventoryAsync(region, category, ct));
    }

    private static async Task<IResult> GetSummaryAsync(
        InventoryQueries queries,
        string? region = null,
        CancellationToken ct = default)
        => Results.Ok(await queries.GetSummaryAsync(region, ct));

    private static async Task<IResult> GetCategoriesAsync(
        InventoryQueries queries,
        CancellationToken ct = default)
        => Results.Ok(await queries.GetCategoriesAsync(ct));

    private static async Task<IResult> GetBySkuAsync(
        string sku,
        InventoryQueries queries,
        CancellationToken ct = default)
    {
        var item = await queries.GetBySkuAsync(sku, ct);
        return item is null ? Results.NotFound() : Results.Ok(item);
    }
}
CS

echo "✓ Endpoints/InventoryReadEndpoints.cs written"

# ── 13. Middleware/ExceptionMiddleware.cs ─────────────────────────────────────
cat > Middleware/ExceptionMiddleware.cs << 'CS'
using System.Text.Json;

namespace InventoryService.Middleware;

public sealed class ExceptionMiddleware
{
    private readonly RequestDelegate              _next;
    private readonly ILogger<ExceptionMiddleware>  _logger;
    private readonly IHostEnvironment             _env;

    private static readonly JsonSerializerOptions _json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ExceptionMiddleware(RequestDelegate next,
        ILogger<ExceptionMiddleware> logger, IHostEnvironment env)
    {
        _next = next; _logger = logger; _env = env;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        try { await _next(ctx); }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        { ctx.Response.StatusCode = 499; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception on {Method} {Path}",
                ctx.Request.Method, ctx.Request.Path);
            ctx.Response.StatusCode  = 500;
            ctx.Response.ContentType = "application/problem+json";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                type    = "https://tools.ietf.org/html/rfc7807",
                title   = "An unexpected error occurred.",
                status  = 500,
                traceId = ctx.TraceIdentifier,
                detail  = _env.IsDevelopment() ? ex.Message : null
            }, _json));
        }
    }
}
CS

echo "✓ Middleware/ExceptionMiddleware.cs written"

# ── 14. Program.cs ────────────────────────────────────────────────────────────
cat > Program.cs << 'CS'
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
CS

echo "✓ Program.cs written"

# ── 15. Restore + build ────────────────────────────────────────────────────────
echo ""
echo "Running dotnet restore..."
dotnet restore

echo ""
echo "Running dotnet build..."
dotnet build --no-restore

echo ""
echo "======================================================"
echo "  ✅  InventoryService is ready!"
echo ""
echo "  To run:  cd $PROJECT_DIR && dotnet run"
echo "  Then open: http://localhost:{port} for Swagger"
echo ""
echo "  Local dev uses in-memory seed data — no Azure needed."
echo "  12 products across EMEA / AMER / APAC with realistic stock levels."
echo "======================================================"