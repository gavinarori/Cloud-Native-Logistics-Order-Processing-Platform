using InventoryService.Models;
using InventoryService.Queries;

namespace InventoryService.Endpoints;

/// <summary>
/// Pure read endpoints — no writes, no SQL.
/// All data flows: Redis (cache) → IInventoryDataSource (Cosmos or in-memory).
/// This service never talks to Azure SQL — that eliminates the Noisy Neighbor
/// problem where report queries caused checkout timeouts in the monolith.
/// </summary>
public static class InventoryReadEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/inventory")
            .WithTags("Inventory — Read");

        group.MapGet("/", GetInventoryAsync)
             .WithName("GetInventory")
             .WithSummary("List inventory items. Served from Redis → Cosmos DB (or in-memory locally).")
             .Produces<List<InventoryItemDto>>();

        group.MapGet("/summary", GetSummaryAsync)
             .WithName("GetInventorySummary")
             .WithSummary("KPI aggregates. Redis TTL 5 min.")
             .Produces<InventorySummaryDto>();

        group.MapGet("/categories", GetCategoriesAsync)
             .WithName("GetCategories")
             .WithSummary("Distinct product categories. Redis TTL 10 min.")
             .Produces<List<string>>();

        group.MapGet("/{sku}", GetBySkuAsync)
             .WithName("GetInventoryBySku")
             .WithSummary("Single item by SKU.")
             .Produces<InventoryItemDto>()
             .Produces(404);

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

        return Results.Ok(await queries.GetInventoryAsync(region, category, ct));
    }

    private static async Task<IResult> GetSummaryAsync(
        InventoryQueries queries,
        string? region = null,
        CancellationToken ct = default)
        => Results.Ok(await queries.GetSummaryAsync(region, ct));

    private static async Task<IResult> GetCategoriesAsync(
        InventoryQueries queries,
        CancellationToken ct = default)
        => Results.Ok(await queries.GetCategoriesAsync(ct));

    private static async Task<IResult> GetBySkuAsync(
        string sku,
        InventoryQueries queries,
        CancellationToken ct = default)
    {
        var item = await queries.GetBySkuAsync(sku, ct);
        return item is null ? Results.NotFound() : Results.Ok(item);
    }
}
