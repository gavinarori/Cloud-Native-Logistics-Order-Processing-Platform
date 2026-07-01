using System.Net.Http.Json;
using System.Text.Json;
using LogisticsWeb.Models;
using Microsoft.Extensions.Caching.Distributed;

namespace LogisticsWeb.Services;

/// <summary>
/// All reads hit Redis first (TTL 2 min for inventory, 5 min for summary).
/// Cache miss falls through to the Inventory microservice, which reads from Cosmos DB.
///
/// Note on eventual consistency: The StockLevel shown here reflects the CDC-synced
/// read model and may lag the Azure SQL write replica by seconds.
/// For reservation decisions, the Order microservice re-checks SQL directly.
/// </summary>
public sealed class InventoryApiService : IInventoryService
{
    private readonly HttpClient        _http;
    private readonly IDistributedCache _cache;
    private readonly ILogger<InventoryApiService> _logger;

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

        var cached = await _cache.GetStringAsync(cacheKey, ct);
        if (cached is not null)
            return JsonSerializer.Deserialize<List<InventoryItem>>(cached, _json) ?? [];

        var url      = category is null ? "api/inventory" : $"api/inventory?category={Uri.EscapeDataString(category)}";
        var items    = await _http.GetFromJsonAsync<List<InventoryItem>>(url, ct) ?? [];

        await _cache.SetStringAsync(cacheKey,
            JsonSerializer.Serialize(items, _json),
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2)
            }, ct);

        return items;
    }

    public async Task<InventoryItem?> GetItemBySkuAsync(string sku, CancellationToken ct = default)
    {
        var cacheKey = $"inventory:sku:{sku}";

        var cached = await _cache.GetStringAsync(cacheKey, ct);
        if (cached is not null)
            return JsonSerializer.Deserialize<InventoryItem>(cached, _json);

        try
        {
            var item = await _http.GetFromJsonAsync<InventoryItem>($"api/inventory/{sku}", ct);
            if (item is not null)
                await _cache.SetStringAsync(cacheKey,
                    JsonSerializer.Serialize(item, _json),
                    new DistributedCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2)
                    }, ct);
            return item;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<InventorySummary> GetSummaryAsync(CancellationToken ct = default)
    {
        const string cacheKey = "inventory:summary";

        var cached = await _cache.GetStringAsync(cacheKey, ct);
        if (cached is not null)
        {
            _logger.LogDebug("Inventory summary served from Redis.");
            return JsonSerializer.Deserialize<InventorySummary>(cached, _json)!;
        }

        var summary = await _http.GetFromJsonAsync<InventorySummary>("api/inventory/summary", ct)
            ?? new InventorySummary { ComputedAt = DateTime.UtcNow };

        await _cache.SetStringAsync(cacheKey,
            JsonSerializer.Serialize(summary, _json),
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
            }, ct);

        return summary;
    }

    public async Task<List<string>> GetCategoriesAsync(CancellationToken ct = default)
    {
        const string cacheKey = "inventory:categories";

        var cached = await _cache.GetStringAsync(cacheKey, ct);
        if (cached is not null)
            return JsonSerializer.Deserialize<List<string>>(cached, _json) ?? [];

        var categories = await _http.GetFromJsonAsync<List<string>>("api/inventory/categories", ct) ?? [];

        await _cache.SetStringAsync(cacheKey,
            JsonSerializer.Serialize(categories, _json),
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
            }, ct);

        return categories;
    }
}