namespace InventoryService.Models;

public sealed record InventoryItemDto(
    Guid     ProductId,
    string   ProductName,
    string   Sku,
    string   Category,
    string   Region,
    int      StockLevel,
    int      ReorderPoint,
    decimal  UnitPrice,
    string   WarehouseLocation,
    string   Status,
    DateTime LastSyncedAt
);

public sealed record InventorySummaryDto(
    int      TotalProducts,
    int      LowStockCount,
    int      OutOfStockCount,
    int      TotalUnits,
    DateTime ComputedAt
);
