using LogisticsWeb.Models;

namespace LogisticsWeb.Services;

/// <summary>
/// READ-ONLY service. All data is served from the Cosmos DB / Redis read model.
/// This deliberately never touches Azure SQL — that is the whole point of CQRS.
/// </summary>
public interface IInventoryService
{
    Task<List<InventoryItem>> GetInventoryAsync(string? category = null, CancellationToken ct = default);
    Task<InventoryItem?>      GetItemBySkuAsync(string sku, CancellationToken ct = default);
    Task<InventorySummary>    GetSummaryAsync(CancellationToken ct = default);
    Task<List<string>>        GetCategoriesAsync(CancellationToken ct = default);
}