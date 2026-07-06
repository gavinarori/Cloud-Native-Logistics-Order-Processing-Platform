using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using InventoryService.Data.Entities;

namespace InventoryService.Data;

/// <summary>
/// Thin wrapper around <see cref="CosmosClient"/> that exposes typed query methods
/// for the inventory container.
///
/// Connection uses Managed Identity via <c>DefaultAzureCredential</c> — no primary key.
///
/// Consistency level: Session (default) — appropriate for the read model.
/// The inventory container uses partition key /region, so all per-region
/// queries are served from a single logical partition — no cross-partition fanout.
/// </summary>
public sealed class CosmosDbContext
{
    private readonly Container _inventoryContainer;
    private readonly ILogger<CosmosDbContext> _logger;

    public CosmosDbContext(CosmosClient client, IConfiguration config, ILogger<CosmosDbContext> logger)
    {
        var db        = config["Azure:CosmosDb:DatabaseName"]          ?? "LogisticsDb";
        var container = config["Azure:CosmosDb:InventoryContainerName"] ?? "inventory";

        _inventoryContainer = client.GetContainer(db, container);
        _logger             = logger;
    }

    // ── Inventory items ───────────────────────────────────────────────────────
    public async Task<List<InventoryDocument>> GetInventoryAsync(
        string? region   = null,
        string? category = null,
        CancellationToken ct = default)
    {
        // Query with explicit partition key = zero cross-partition fanout.
        // Without a partition key filter this would scan all partitions (expensive).
        var query = _inventoryContainer.GetItemLinqQueryable<InventoryDocument>(
            requestOptions: region is not null
                ? new QueryRequestOptions { PartitionKey = new PartitionKey(region) }
                : null)
            .Where(d => !d.Id.StartsWith("summary:"))  // Exclude summary documents
            .AsQueryable();

        if (!string.IsNullOrEmpty(category))
            query = query.Where(d => d.Category == category);

        var results = new List<InventoryDocument>();
        var feed    = query.ToFeedIterator();

        while (feed.HasMoreResults)
        {
            var page = await feed.ReadNextAsync(ct);
            results.AddRange(page);
            _logger.LogDebug(
                "Cosmos query page consumed {RU} RU/s", page.RequestCharge);
        }

        return results;
    }

    public async Task<InventoryDocument?> GetBySkuAsync(string sku, CancellationToken ct = default)
    {
        // SKU is not the partition key — cross-partition point read required.
        // In a high-throughput scenario, index SKU and run a targeted query.
        var query = _inventoryContainer.GetItemLinqQueryable<InventoryDocument>()
            .Where(d => d.Sku == sku)
            .ToFeedIterator();

        if (query.HasMoreResults)
        {
            var page = await query.ReadNextAsync(ct);
            return page.FirstOrDefault();
        }
        return null;
    }

    public async Task<List<string>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var queryDef = new QueryDefinition(
            "SELECT DISTINCT VALUE c.category FROM c WHERE NOT STARTSWITH(c.id, 'summary:')");

        var results = new List<string>();
        var feed    = _inventoryContainer.GetItemQueryIterator<string>(queryDef);

        while (feed.HasMoreResults)
        {
            var page = await feed.ReadNextAsync(ct);
            results.AddRange(page);
        }

        return results.Where(c => !string.IsNullOrEmpty(c)).Distinct().Order().ToList();
    }

    // ── Summary ───────────────────────────────────────────────────────────────
    public async Task<InventorySummaryDocument?> GetSummaryAsync(
        string? region = null, CancellationToken ct = default)
    {
        if (region is not null)
        {
            // Fast point read — partition key + id known
            try
            {
                var response = await _inventoryContainer.ReadItemAsync<InventorySummaryDocument>(
                    id:           $"summary:{region}",
                    partitionKey: new PartitionKey(region),
                    cancellationToken: ct);
                return response.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        // Global summary — aggregate all per-region summaries in-memory (3 regions)
        var summaries = new List<InventorySummaryDocument>();
        foreach (var r in new[] { "AMER", "EMEA", "APAC" })
        {
            try
            {
                var response = await _inventoryContainer.ReadItemAsync<InventorySummaryDocument>(
                    id: $"summary:{r}", partitionKey: new PartitionKey(r), cancellationToken: ct);
                summaries.Add(response.Resource);
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogDebug("No summary document found for region {Region}.", r);
            }
        }

        if (summaries.Count == 0) return null;

        return new InventorySummaryDocument
        {
            Id              = "summary:global",
            Region          = "ALL",
            TotalProducts   = summaries.Sum(s => s.TotalProducts),
            LowStockCount   = summaries.Sum(s => s.LowStockCount),
            OutOfStockCount = summaries.Sum(s => s.OutOfStockCount),
            TotalUnits      = summaries.Sum(s => s.TotalUnits),
            ComputedAt      = summaries.Min(s => s.ComputedAt)  // Oldest = lag indicator
        };
    }
}