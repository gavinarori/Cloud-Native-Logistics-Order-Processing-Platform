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
