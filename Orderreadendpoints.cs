using OrderService.Models;
using OrderService.Queries;

namespace OrderService.Endpoints;

/// <summary>
/// READ-SIDE endpoints (CQRS query path).
///
/// All queries route through <see cref="OrderQueries"/> which uses
/// <c>OrderReadDbContext</c> — this points at the Azure SQL Business Critical
/// secondary replica, so heavy read traffic never contends with checkout writes.
/// </summary>
public static class OrderReadEndpoints
{
    public static IEndpointRouteBuilder MapOrderReadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/orders")
            .WithTags("Orders — Read")
            .WithOpenApi();

        // ── GET /api/orders?page=1&pageSize=20&status=Fulfilled&region=EMEA
        group.MapGet("/", GetOrdersAsync)
            .WithName("GetOrders")
            .WithSummary("Paginated list of orders from the Azure SQL read replica.")
            .Produces<PagedResult<OrderDto>>()
            .Produces(StatusCodes.Status400BadRequest);

        // ── GET /api/orders/summary  (KPI aggregates — web app caches in Redis)
        group.MapGet("/summary", GetSummaryAsync)
            .WithName("GetOrderSummary")
            .WithSummary("Aggregated KPI metrics. Web app caches this in Redis (TTL 60s).")
            .Produces<OrderSummaryDto>();

        // ── GET /api/orders/{id}
        group.MapGet("/{id:guid}", GetByIdAsync)
            .WithName("GetOrderById")
            .WithSummary("Fetch a single order by its primary key.")
            .Produces<OrderDto>()
            .Produces(StatusCodes.Status404NotFound);

        // ── GET /api/orders/correlation/{correlationId}
        // Poll endpoint: caller uses this after POST /api/orders returns 202.
        group.MapGet("/correlation/{correlationId:guid}", GetByCorrelationIdAsync)
            .WithName("GetOrderByCorrelation")
            .WithSummary("Poll endpoint: find the order created by a given CorrelationId.")
            .WithDescription("""
                After POST /api/orders returns 202 Accepted, poll this endpoint with the
                returned CorrelationId to check whether the Worker Function has persisted
                the order to Azure SQL yet.

                Returns 404 while the message is still in the Service Bus queue.
                Returns 200 with status=Processing or status=Fulfilled once persisted.
                """)
            .Produces<OrderDto>()
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    // ─────────────────────────────────────────────────────────────────────────
    private static async Task<IResult> GetOrdersAsync(
        OrderQueries queries,
        int    page     = 1,
        int    pageSize = 20,
        string? status  = null,
        string? region  = null,
        string? email   = null,
        CancellationToken ct = default)
    {
        if (page < 1 || pageSize < 1 || pageSize > 100)
            return Results.BadRequest("page must be ≥1; pageSize must be between 1 and 100.");

        var result = await queries.GetOrdersAsync(page, pageSize, status, region, email, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetSummaryAsync(
        OrderQueries queries,
        CancellationToken ct = default)
    {
        var summary = await queries.GetSummaryAsync(ct);
        return Results.Ok(summary);
    }

    private static async Task<IResult> GetByIdAsync(
        Guid id,
        OrderQueries queries,
        CancellationToken ct = default)
    {
        var order = await queries.GetByIdAsync(id, ct);
        return order is null ? Results.NotFound() : Results.Ok(order);
    }

    private static async Task<IResult> GetByCorrelationIdAsync(
        Guid correlationId,
        OrderQueries queries,
        CancellationToken ct = default)
    {
        var order = await queries.GetByCorrelationIdAsync(correlationId, ct);
        return order is null ? Results.NotFound() : Results.Ok(order);
    }
}