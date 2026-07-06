namespace OrderService.Models;

/// <summary>
/// API response contract for a single order.
/// Deliberately separate from the EF entity — the entity is internal
/// to the data layer; this is what the outside world receives.
/// </summary>
public sealed record OrderDto(
    Guid            Id,
    string          CustomerName,
    string          CustomerEmail,
    string          Region,
    string          Status,
    DateTime        CreatedAt,
    DateTime?       UpdatedAt,
    DateTime?       FulfilledAt,
    decimal         TotalAmount,
    int             TotalItems,
    List<OrderItemDto> Items
);

public sealed record OrderItemDto(
    Guid    Id,
    Guid    ProductId,
    string  Sku,
    string  ProductName,
    decimal UnitPrice,
    int     Quantity,
    decimal LineTotal
);

/// <summary>
/// Paginated list wrapper — the envelope every list endpoint returns.
/// </summary>
public sealed record PagedResult<T>(
    List<T> Items,
    int     Page,
    int     PageSize,
    int     TotalCount,
    bool    HasNextPage
);

/// <summary>
/// KPI summary served by the dashboard — cached in Redis by the web app.
/// </summary>
public sealed record OrderSummaryDto(
    int     TotalOrders,
    int     PendingOrders,
    int     FulfilledToday,
    int     FailedOrders,
    decimal RevenueToday,
    decimal RevenueThisMonth,
    DateTime ComputedAt
);

/// <summary>
/// Response returned immediately after a POST /api/orders call.
/// The HTTP 202 Accepted body — tells the caller where to poll.
/// </summary>
public sealed record EnqueuedOrderResponse(
    Guid   CorrelationId,
    string Status,
    string Message,
    string PollUrl
);