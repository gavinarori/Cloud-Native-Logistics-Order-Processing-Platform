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
