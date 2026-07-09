using LogisticsWeb.Models;

namespace LogisticsWeb.Services;

/// <summary>
/// Web app's contract for reading inventory data.
/// Implemented by <see cref="InventoryApiService"/>, which calls the
/// Inventory microservice over HTTP. That service reads from Redis → Cosmos DB.
///
/// This interface lives in the Blazor web app project.
/// It is NOT the same as the InventoryService microservice project.
/// </summary>
public interface IInventoryService
{
    /// <summary>
    /// Returns all inventory items, optionally filtered by category.
    /// Served from Redis (TTL 2 min) → Cosmos DB on cache miss.
    /// </summary>
    Task<List<InventoryItem>> GetInventoryAsync(
        string? category = null, CancellationToken ct = default);

    /// <summary>Single item lookup by SKU.</summary>
    Task<InventoryItem?> GetItemBySkuAsync(
        string sku, CancellationToken ct = default);

    /// <summary>
    /// Aggregated KPI counts: total products, low stock, out of stock.
    /// Cached in Redis (TTL 5 min) by the Inventory microservice.
    /// </summary>
    Task<InventorySummary> GetSummaryAsync(CancellationToken ct = default);

    /// <summary>Distinct category names — for filter dropdowns in the UI.</summary>
    Task<List<string>> GetCategoriesAsync(CancellationToken ct = default);
}