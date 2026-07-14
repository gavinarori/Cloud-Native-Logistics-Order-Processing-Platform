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
