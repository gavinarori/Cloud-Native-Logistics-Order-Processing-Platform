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
    string   Status,            // "InStock" | "LowStock" | "OutOfStock"
    DateTime LastSyncedAt       // CDC sync timestamp — surface lag in UI
);

public sealed record InventorySummaryDto(
    int      TotalProducts,
    int      LowStockCount,
    int      OutOfStockCount,
    int      TotalUnits,
    DateTime ComputedAt
);