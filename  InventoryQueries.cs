using System.Text.Json;
using InventoryService.Data;
using InventoryService.Data.Entities;
using InventoryService.Models;
using Microsoft.Extensions.Caching.Distributed;

namespace InventoryService.Queries;

/// <summary>
/// All reads go through a two-level stack:
///   L1: Azure Cache for Redis    (TTL 2–10 min, sub-millisecond)
///   L2: Azure Cosmos DB          (globally distributed, ~5ms p99)
///
/// This isolates reporting and browsing traffic from the Azure SQL write primary
/// entirely — the SQL database is never queried from this service.
///
/// Cache key strategy:
///   inventory:list:{region}:{category}   — list queries
///   inventory:sku:{sku}                  — single item
///   inventory:categories                 — distinct categories
///   inventory:summary:{region}           — KPI aggregates
/// </summary>
public sealed class InventoryQueries
{
    private readonly CosmosDbContext             _cosmos;
    private readonly IDistributedCache           _cache;
    private readonly IConfiguration              _config;
    private readonly ILogger<InventoryQueries>   _logger;

    private static readonly JsonSerializerOptions _json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public InventoryQueries(
        CosmosDbContext cosmos,
        IDistributedCache cache,
        IConfiguration config,
        ILogger<InventoryQueries> logger)
    {
        _cosmos = cosmos;
        _cache  = cache;
        _config = config;
        _logger = logger;
    }

    public async Task<List<InventoryItemDto>> GetInventoryAsync(
        string? region   = null,
        string? category = null,
        CancellationToken ct = default)
    {
        var cacheKey = $"inventory:list:{region ?? "all"}:{category ?? "all"}";
        var ttl      = TimeSpan.FromMinutes(_config.GetValue("Azure:Redis:InventoryTtlMinutes", 2));

        return await GetCachedAsync(
            cacheKey, ttl,
            async () =>
            {
                var docs = await _cosmos.GetInventoryAsync(region, category, ct);
                return docs.Select(ToDto).ToList();
            }, ct);
    }

    public async Task<InventoryItemDto?> GetBySkuAsync(string sku, CancellationToken ct = default)
    {
        var cacheKey = $"inventory:sku:{sku}";
        var ttl      = TimeSpan.FromMinutes(_config.GetValue("Azure:Redis:InventoryTtlMinutes", 2));

        return await GetCachedAsync<InventoryItemDto?>(
            cacheKey, ttl,
            async () =>
            {
                var doc = await _cosmos.GetBySkuAsync(sku, ct);
                return doc is null ? null : ToDto(doc);
            }, ct);
    }

    public async Task<List<string>> GetCategoriesAsync(CancellationToken ct = default)
    {
        const string cacheKey = "inventory:categories";
        var ttl = TimeSpan.FromMinutes(_config.GetValue("Azure:Redis:CategoriesTtlMinutes", 10));

        return await GetCachedAsync(
            cacheKey, ttl,
            () => _cosmos.GetCategoriesAsync(ct),
            ct);
    }

    public async Task<InventorySummaryDto> GetSummaryAsync(
        string? region = null, CancellationToken ct = default)
    {
        var cacheKey = $"inventory:summary:{region ?? "global"}";
        var ttl      = TimeSpan.FromMinutes(_config.GetValue("Azure:Redis:SummaryTtlMinutes", 5));

        return await GetCachedAsync(
            cacheKey, ttl,
            async () =>
            {
                var doc = await _cosmos.GetSummaryAsync(region, ct);
                return doc is null
                    ? new InventorySummaryDto(0, 0, 0, 0, DateTime.UtcNow)
                    : new InventorySummaryDto(
                        doc.TotalProducts, doc.LowStockCount,
                        doc.OutOfStockCount, doc.TotalUnits, doc.ComputedAt);
            }, ct);
    }

    // ── Generic cache-aside helper ────────────────────────────────────────────
    private async Task<T> GetCachedAsync<T>(
        string cacheKey,
        TimeSpan ttl,
        Func<Task<T>> factory,
        CancellationToken ct)
    {
        try
        {
            var cached = await _cache.GetStringAsync(cacheKey, ct);
            if (cached is not null)
            {
                _logger.LogDebug("Cache hit: {Key}", cacheKey);
                return JsonSerializer.Deserialize<T>(cached, _json)!;
            }
        }
        catch (Exception ex)
        {
            // Redis outage — fall through to Cosmos DB (cache failure is not fatal)
            _logger.LogWarning(ex, "Redis cache read failed for key {Key}. Falling back to Cosmos DB.", cacheKey);
        }

        _logger.LogDebug("Cache miss: {Key} — querying Cosmos DB", cacheKey);
        var value = await factory();

        try
        {
            var opts = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl
            };
            await _cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(value, _json), opts, ct);
        }
        catch (Exception ex)
        {
            // Redis outage on write — serve fresh value from Cosmos, don't fail the request
            _logger.LogWarning(ex, "Redis cache write failed for key {Key}.", cacheKey);
        }

        return value;
    }

    // ── Projection ────────────────────────────────────────────────────────────
    private static InventoryItemDto ToDto(InventoryDocument d) => new(
        ProductId:        d.ProductId,
        ProductName:      d.ProductName,
        Sku:              d.Sku,
        Category:         d.Category,
        Region:           d.Region,
        StockLevel:       d.StockLevel,
        ReorderPoint:     d.ReorderPoint,
        UnitPrice:        d.UnitPrice,
        WarehouseLocation:d.WarehouseLocation,
        Status:           d.StockLevel == 0          ? "OutOfStock"
                        : d.StockLevel <= d.ReorderPoint ? "LowStock"
                        : "InStock",
        LastSyncedAt:     d.LastSyncedAt
    );
}