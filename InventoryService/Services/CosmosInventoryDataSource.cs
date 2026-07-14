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
