using LogisticsWeb.Models;

namespace LogisticsWeb.Services;

/// <summary>
/// Abstracts write operations (via Service Bus) and read operations
/// (via Order microservice HTTP API backed by Azure SQL read replicas).
/// </summary>
public interface IOrderService
{
    /// <summary>
    /// Enqueues a CreateOrderCommand onto the Azure Service Bus FIFO queue.
    /// Returns the CorrelationId to track the command through the pipeline.
    /// This is the CQRS write path — the actual SQL insert is done by the Worker Function.
    /// </summary>
    Task<Guid> EnqueueOrderAsync(CreateOrderCommand command, CancellationToken ct = default);

    /// <summary>Read path — queries the Order microservice (Azure SQL read replica).</summary>
    Task<List<Order>> GetOrdersAsync(int page = 1, int pageSize = 20, CancellationToken ct = default);

    /// <summary>Read path — single order detail.</summary>
    Task<Order?> GetOrderByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Dashboard KPI aggregates (cached in Redis).</summary>
    Task<OrderSummary> GetOrderSummaryAsync(CancellationToken ct = default);
}

public sealed class OrderSummary
{
    public int     TotalOrders     { get; set; }
    public int     PendingOrders   { get; set; }
    public int     FulfilledToday  { get; set; }
    public int     FailedOrders    { get; set; }
    public decimal RevenueToday    { get; set; }
    public decimal RevenueThisMonth { get; set; }
    public DateTime ComputedAt     { get; set; }
}