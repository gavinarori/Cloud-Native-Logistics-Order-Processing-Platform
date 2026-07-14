using InventoryService.Models;

namespace InventoryService.Services;

/// <summary>
/// In-memory data source with realistic seed data.
/// Used in local development — no Azure services required.
/// Swap for CosmosInventoryDataSource in production.
/// </summary>
public sealed class InMemoryInventoryDataSource : IInventoryDataSource
{
    private static readonly List<InventoryItemDto> _seed =
    [
        new(Guid.NewGuid(), "Pro Laptop 15\"",       "LAPTOP-001",  "Electronics", "EMEA", 45,  10, 1299.99m, "Warehouse-A-Shelf-3", "InStock",    DateTime.UtcNow),
        new(Guid.NewGuid(), "Wireless Mouse",         "MOUSE-001",   "Peripherals", "EMEA",  8,  20,   29.99m, "Warehouse-A-Shelf-7", "LowStock",   DateTime.UtcNow),
        new(Guid.NewGuid(), "USB-C Hub 7-in-1",      "USBHUB-001",  "Peripherals", "EMEA",  0,  15,   49.99m, "Warehouse-B-Shelf-1", "OutOfStock", DateTime.UtcNow),
        new(Guid.NewGuid(), "Mechanical Keyboard",    "KB-001",      "Peripherals", "EMEA", 32,  10,   89.99m, "Warehouse-A-Shelf-2", "InStock",    DateTime.UtcNow),
        new(Guid.NewGuid(), "4K Monitor 27\"",        "MON-001",     "Electronics", "AMER", 12,  5,  499.99m,  "Warehouse-C-Shelf-1", "InStock",    DateTime.UtcNow),
        new(Guid.NewGuid(), "Standing Desk",          "DESK-001",    "Furniture",   "AMER",  3,  5,  649.99m,  "Warehouse-C-Shelf-4", "LowStock",   DateTime.UtcNow),
        new(Guid.NewGuid(), "Ergonomic Chair",        "CHAIR-001",   "Furniture",   "AMER", 18,  8,  399.99m,  "Warehouse-C-Shelf-5", "InStock",    DateTime.UtcNow),
        new(Guid.NewGuid(), "Noise-Cancel Headphones","HEADPH-001",  "Electronics", "APAC", 27, 10,  249.99m,  "Warehouse-D-Shelf-2", "InStock",    DateTime.UtcNow),
        new(Guid.NewGuid(), "Webcam HD 1080p",        "CAM-001",     "Peripherals", "APAC",  5, 15,   79.99m,  "Warehouse-D-Shelf-3", "LowStock",   DateTime.UtcNow),
        new(Guid.NewGuid(), "Laptop Stand",           "STAND-001",   "Peripherals", "APAC",  0, 10,   39.99m,  "Warehouse-D-Shelf-6", "OutOfStock", DateTime.UtcNow),
        new(Guid.NewGuid(), "SSD 1TB External",       "SSD-001",     "Storage",     "EMEA", 55,  20,  109.99m, "Warehouse-A-Shelf-9", "InStock",    DateTime.UtcNow),
        new(Guid.NewGuid(), "NAS Drive 4TB",          "NAS-001",     "Storage",     "AMER",  7,  10,  189.99m, "Warehouse-C-Shelf-8", "LowStock",   DateTime.UtcNow),
    ];

    public Task<List<InventoryItemDto>> GetInventoryAsync(
        string? region = null, string? category = null, CancellationToken ct = default)
    {
        var result = _seed.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(region))
            result = result.Where(i => i.Region == region);
        if (!string.IsNullOrWhiteSpace(category))
            result = result.Where(i => i.Category == category);
        return Task.FromResult(result.ToList());
    }

    public Task<InventoryItemDto?> GetBySkuAsync(string sku, CancellationToken ct = default)
        => Task.FromResult(_seed.FirstOrDefault(i => i.Sku == sku));

    public Task<List<string>> GetCategoriesAsync(CancellationToken ct = default)
        => Task.FromResult(_seed.Select(i => i.Category).Distinct().Order().ToList());

    public Task<InventorySummaryDto> GetSummaryAsync(
        string? region = null, CancellationToken ct = default)
    {
        var items = string.IsNullOrWhiteSpace(region)
            ? _seed
            : _seed.Where(i => i.Region == region).ToList();

        return Task.FromResult(new InventorySummaryDto(
            TotalProducts:   items.Count,
            LowStockCount:   items.Count(i => i.Status == "LowStock"),
            OutOfStockCount: items.Count(i => i.Status == "OutOfStock"),
            TotalUnits:      items.Sum(i => i.StockLevel),
            ComputedAt:      DateTime.UtcNow));
    }
}
