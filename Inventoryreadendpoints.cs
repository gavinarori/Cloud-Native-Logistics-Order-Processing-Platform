using InventoryService.Models;
using InventoryService.Queries;

namespace InventoryService.Endpoints;

/// <summary>
/// All endpoints are read-only (GET). This service is a pure query service —
/// it never writes to Cosmos DB or Redis directly. Writes come from Azure SQL
/// via Change Data Capture → Azure Data Factory → Cosmos DB upsert.
/// </summary>
public static class InventoryReadEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/inventory")
            .WithTags("Inventory — Read")
            .WithOpenApi();

        // GET /api/inventory?region=EMEA&category=Electronics
        group.MapGet("/", GetInventoryAsync)
            .WithName("GetInventory")
            .WithSummary("List inventory items. Served from Redis (TTL 2 min) → Cosmos DB.")
            .Produces<List<InventoryItemDto>>()
            .Produces(StatusCodes.Status400BadRequest);

        // GET /api/inventory/summary?region=EMEA
        group.MapGet("/summary", GetSummaryAsync)
            .WithName("GetInventorySummary")
            .WithSummary("KPI aggregates. Redis TTL 5 min. Used by the dashboard.")
            .Produces<InventorySummaryDto>();

        // GET /api/inventory/categories
        group.MapGet("/categories", GetCategoriesAsync)
            .WithName("GetCategories")
            .WithSummary("Distinct product categories. Redis TTL 10 min.")
            .Produces<List<string>>();

        // GET /api/inventory/{sku}
        group.MapGet("/{sku}", GetBySkuAsync)
            .WithName("GetInventoryBySku")
            .WithSummary("Single inventory item by SKU.")
            .Produces<InventoryItemDto>()
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> GetInventoryAsync(
        InventoryQueries queries,
        string? region   = null,
        string? category = null,
        CancellationToken ct = default)
    {
        if (region is not null && region is not ("AMER" or "EMEA" or "APAC"))
            return Results.BadRequest("Region must be AMER, EMEA, or APAC.");

        var items = await queries.GetInventoryAsync(region, category, ct);
        return Results.Ok(items);
    }

    private static async Task<IResult> GetSummaryAsync(
        InventoryQueries queries,
        string? region = null,
        CancellationToken ct = default)
    {
        var summary = await queries.GetSummaryAsync(region, ct);
        return Results.Ok(summary);
    }

    private static async Task<IResult> GetCategoriesAsync(
        InventoryQueries queries,
        CancellationToken ct = default)
    {
        var categories = await queries.GetCategoriesAsync(ct);
        return Results.Ok(categories);
    }

    private static async Task<IResult> GetBySkuAsync(
        string sku,
        InventoryQueries queries,
        CancellationToken ct = default)
    {
        var item = await queries.GetBySkuAsync(sku, ct);
        return item is null ? Results.NotFound() : Results.Ok(item);
    }
}