using OrderService.Models;
using OrderService.Queries;

namespace OrderService.Endpoints;

/// <summary>
/// CQRS read path — all queries hit the Azure SQL read replica.
/// </summary>
public static class OrderReadEndpoints
{
    public static IEndpointRouteBuilder MapOrderReadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/orders").WithTags("Orders — Read");

        group.MapGet("/", GetOrdersAsync)
             .WithName("GetOrders")
             .WithSummary("Paginated order list from the read replica.")
             .Produces<PagedResult<OrderDto>>();

        group.MapGet("/summary", GetSummaryAsync)
             .WithName("GetOrderSummary")
             .WithSummary("KPI aggregates — cached in Redis by the web app (TTL 60s).")
             .Produces<OrderSummaryDto>();

        group.MapGet("/{id:guid}", GetByIdAsync)
             .WithName("GetOrderById")
             .Produces<OrderDto>().Produces(404);

        group.MapGet("/correlation/{correlationId:guid}", GetByCorrelationIdAsync)
             .WithName("GetOrderByCorrelationId")
             .WithSummary("Poll after POST /api/orders returns 202.")
             .Produces<OrderDto>().Produces(404);

        return app;
    }

    private static async Task<IResult> GetOrdersAsync(
        OrderQueries q, int page = 1, int pageSize = 20,
        string? status = null, string? region = null, string? email = null,
        CancellationToken ct = default)
        => Results.Ok(await q.GetOrdersAsync(page, pageSize, status, region, email, ct));

    private static async Task<IResult> GetSummaryAsync(
        OrderQueries q, CancellationToken ct = default)
        => Results.Ok(await q.GetSummaryAsync(ct));

    private static async Task<IResult> GetByIdAsync(
        Guid id, OrderQueries q, CancellationToken ct = default)
    {
        var order = await q.GetByIdAsync(id, ct);
        return order is null ? Results.NotFound() : Results.Ok(order);
    }

    private static async Task<IResult> GetByCorrelationIdAsync(
        Guid correlationId, OrderQueries q, CancellationToken ct = default)
    {
        var order = await q.GetByCorrelationIdAsync(correlationId, ct);
        return order is null ? Results.NotFound() : Results.Ok(order);
    }
}
