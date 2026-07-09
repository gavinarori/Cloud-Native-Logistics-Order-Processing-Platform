namespace LogisticsWeb.Models;

/// <summary>
/// Product catalog entry. Read model served from Cosmos DB / Redis
/// to avoid hitting the primary Azure SQL write replica.
/// </summary>
public sealed class Product
{
    public Guid    Id          { get; init; } = Guid.NewGuid();
    public string  Name        { get; set; }  = string.Empty;
    public string  Sku         { get; set; }  = string.Empty;
    public string  Category    { get; set; }  = string.Empty;
    public decimal UnitPrice   { get; set; }
    public string  Description { get; set; }  = string.Empty;
    public string  ImageUrl    { get; set; }  = string.Empty;
}

/// <summary>
/// Inventory snapshot. This is a read model — it is the eventually-consistent
/// view synced from Azure SQL → Cosmos DB via Azure Data Factory / CDC.
/// Do NOT use this for stock reservation; that happens in the Order microservice
/// using a SELECT ... WITH (UPDLOCK) on Azure SQL.
/// </summary>
public sealed class InventoryItem
{
    public Guid    ProductId    { get; init; }
    public string  ProductName  { get; set; }  = string.Empty;
    public string  Sku          { get; set; }  = string.Empty;
    public string  Category     { get; set; }  = string.Empty;
    public int     StockLevel   { get; set; }
    public int     ReorderPoint { get; set; }
    public string  WarehouseLocation { get; set; } = string.Empty;
    public DateTime LastSyncedAt { get; set; }   // Tracks CDC sync freshness

    public StockStatus Status => StockLevel switch
    {
        0         => StockStatus.OutOfStock,
        var n when n <= ReorderPoint => StockStatus.LowStock,
        _         => StockStatus.InStock
    };
}

public enum StockStatus { InStock, LowStock, OutOfStock }

/// <summary>Summary metrics for the dashboard — served from Redis.</summary>
public sealed class InventorySummary
{
    public int    TotalProducts    { get; set; }
    public int    LowStockCount    { get; set; }
    public int    OutOfStockCount  { get; set; }
    public int    TotalUnits       { get; set; }
    public DateTime ComputedAt     { get; set; }
}