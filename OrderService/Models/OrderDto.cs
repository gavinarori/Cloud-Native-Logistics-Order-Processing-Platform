namespace OrderService.Models;

public sealed record OrderDto(
    Guid   Id, string CustomerName, string CustomerEmail,
    string Region, string Status,
    DateTime CreatedAt, DateTime? UpdatedAt, DateTime? FulfilledAt,
    decimal TotalAmount, int TotalItems,
    List<OrderItemDto> Items);

public sealed record OrderItemDto(
    Guid Id, Guid ProductId, string Sku, string ProductName,
    decimal UnitPrice, int Quantity, decimal LineTotal);

public sealed record PagedResult<T>(
    List<T> Items, int Page, int PageSize, int TotalCount, bool HasNextPage);

public sealed record OrderSummaryDto(
    int TotalOrders, int PendingOrders, int FulfilledToday,
    int FailedOrders, decimal RevenueToday, decimal RevenueThisMonth,
    DateTime ComputedAt);

public sealed record EnqueuedOrderResponse(
    Guid CorrelationId, string Status, string Message, string PollUrl);
