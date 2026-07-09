using System.Net.Http.Json;
using System.Text.Json;
using LogisticsWeb.Models;
using Microsoft.Extensions.Caching.Distributed;

namespace LogisticsWeb.Services;

/// <summary>
/// Calls the Inventory microservice over HTTP and caches results in Redis.
///
/// Cache key strategy (mirrors what the microservice uses):
///   inventory:list:{category}   — item lists
///   inventory:sku:{sku}         — single item
///   inventory:categories        — distinct categories
///   inventory:summary           — KPI aggregates
///
/// When the Inventory microservice is unreachable (local dev without Docker),
/// every method returns an empty/default result instead of crashing the UI.
/// </summary>
public sealed class InventoryApiService : IInventoryService
{
    private readonly HttpClient                    _http;
    private readonly IDistributedCache             _cache;
    private readonly ILogger<InventoryApiService>  _logger;

    private static readonly JsonSerializerOptions _json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public InventoryApiService(
        HttpClient http,
        IDistributedCache cache,
        ILogger<InventoryApiService> logger)
    {
        _http   = http;
        _cache  = cache;
        _logger = logger;
    }

    public async Task<List<InventoryItem>> GetInventoryAsync(
        string? category = null, CancellationToken ct = default)
    {
        var cacheKey = $"inventory:list:{category ?? "all"}";

        var cached = await TryGetCacheAsync<List<InventoryItem>>(cacheKey, ct);
        if (cached is not null) return cached;

        try
        {
            var url   = category is null ? "api/inventory" : $"api/inventory?category={Uri.EscapeDataString(category)}";
            var items = await _http.GetFromJsonAsync<List<InventoryItem>>(url, ct) ?? [];
            await TrySetCacheAsync(cacheKey, items, TimeSpan.FromMinutes(2), ct);
            return items;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inventory API unreachable — returning empty list.");
            return [];
        }
    }

    public async Task<InventoryItem?> GetItemBySkuAsync(
        string sku, CancellationToken ct = default)
    {
        var cacheKey = $"inventory:sku:{sku}";

        var cached = await TryGetCacheAsync<InventoryItem>(cacheKey, ct);
        if (cached is not null) return cached;

        try
        {
            var item = await _http.GetFromJsonAsync<InventoryItem>($"api/inventory/{sku}", ct);
            if (item is not null)
                await TrySetCacheAsync(cacheKey, item, TimeSpan.FromMinutes(2), ct);
            return item;
        }
        catch (HttpRequestException ex) when
            (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inventory API unreachable for SKU {Sku}.", sku);
            return null;
        }
    }

    public async Task<InventorySummary> GetSummaryAsync(CancellationToken ct = default)
    {
        const string cacheKey = "inventory:summary";

        var cached = await TryGetCacheAsync<InventorySummary>(cacheKey, ct);
        if (cached is not null) return cached;

        try
        {
            var summary = await _http.GetFromJsonAsync<InventorySummary>(
                "api/inventory/summary", ct) ?? new InventorySummary { ComputedAt = DateTime.UtcNow };
            await TrySetCacheAsync(cacheKey, summary, TimeSpan.FromMinutes(5), ct);
            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inventory summary API unreachable — using zeroed fallback.");
            return new InventorySummary { ComputedAt = DateTime.UtcNow };
        }
    }

    public async Task<List<string>> GetCategoriesAsync(CancellationToken ct = default)
    {
        const string cacheKey = "inventory:categories";

        var cached = await TryGetCacheAsync<List<string>>(cacheKey, ct);
        if (cached is not null) return cached;

        try
        {
            var cats = await _http.GetFromJsonAsync<List<string>>("api/inventory/categories", ct) ?? [];
            await TrySetCacheAsync(cacheKey, cats, TimeSpan.FromMinutes(10), ct);
            return cats;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inventory categories API unreachable.");
            return [];
        }
    }

    // ── Cache helpers ─────────────────────────────────────────────────────────
    private async Task<T?> TryGetCacheAsync<T>(string key, CancellationToken ct)
    {
        try
        {
            var raw = await _cache.GetStringAsync(key, ct);
            if (raw is not null)
            {
                _logger.LogDebug("Cache hit: {Key}", key);
                return JsonSerializer.Deserialize<T>(raw, _json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache read failed for key {Key}.", key);
        }
        return default;
    }

    private async Task TrySetCacheAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct)
    {
        try
        {
            var opts = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl
            };
            await _cache.SetStringAsync(key, JsonSerializer.Serialize(value, _json), opts, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache write failed for key {Key} — non-fatal.", key);
        }
    }
}