namespace LogisticsWeb.Models;

/// <summary>
/// Represents a customer order written to the Service Bus queue
/// and ultimately persisted in Azure SQL (ACID compliant).
/// </summary>
public sealed class Order
{
    public Guid   Id            { get; init; } = Guid.NewGuid();
    public string CustomerName  { get; set; }  = string.Empty;
    public string CustomerEmail { get; set; }  = string.Empty;
    public string Region        { get; set; }  = string.Empty;   // Used for Cosmos DB partition key
    public OrderStatus Status   { get; set; }  = OrderStatus.Pending;
    public DateTime CreatedAt   { get; init; } = DateTime.UtcNow;
    public DateTime? UpdatedAt  { get; set; }

    public List<OrderItem> Items { get; set; } = [];

    public decimal TotalAmount => Items.Sum(i => i.UnitPrice * i.Quantity);
    public int     TotalItems  => Items.Sum(i => i.Quantity);
}

public sealed class OrderItem
{
    public Guid    Id          { get; init; } = Guid.NewGuid();
    public Guid    ProductId   { get; set; }
    public string  ProductName { get; set; }  = string.Empty;
    public string  Sku         { get; set; }  = string.Empty;
    public int     Quantity    { get; set; }
    public decimal UnitPrice   { get; set; }
}

public enum OrderStatus
{
    Pending,
    Queued,       // Sent to Service Bus — awaiting Worker Function processing
    Processing,
    Fulfilled,
    Cancelled,
    Failed
}

/// <summary>
/// Lightweight DTO used when sending a command to the Service Bus queue.
/// Keeps the message payload small and serialization-friendly.
/// </summary>
public sealed class CreateOrderCommand
{
    public Guid   CorrelationId  { get; init; } = Guid.NewGuid(); // Service Bus duplicate-detection key
    public string CustomerName   { get; set; }  = string.Empty;
    public string CustomerEmail  { get; set; }  = string.Empty;
    public string Region         { get; set; }  = string.Empty;
    public List<OrderItemDto> Items { get; set; } = [];
}

public sealed class OrderItemDto
{
    public Guid    ProductId  { get; set; }
    public string  Sku        { get; set; } = string.Empty;
    public int     Quantity   { get; set; }
    public decimal UnitPrice  { get; set; }
}