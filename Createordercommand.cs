namespace OrderWorkerFunction.Models;

/// <summary>
/// Deserialised from the Azure Service Bus message body.
/// Must stay byte-for-byte compatible with OrderService.Commands.CreateOrderCommand
/// — both sides own a copy of this DTO, kept in sync manually or via a shared NuGet.
/// </summary>
public sealed class CreateOrderCommand
{
    public Guid   CorrelationId  { get; set; }  // Maps to MessageId — duplicate detection key
    public string CustomerName   { get; set; }  = string.Empty;
    public string CustomerEmail  { get; set; }  = string.Empty;
    public string Region         { get; set; }  = string.Empty;  // Cosmos DB partition key
    public List<OrderItemCommand> Items { get; set; } = [];
}

public sealed class OrderItemCommand
{
    public Guid    ProductId   { get; set; }
    public string  Sku         { get; set; } = string.Empty;
    public string  ProductName { get; set; } = string.Empty;
    public decimal UnitPrice   { get; set; }
    public int     Quantity    { get; set; }
}

/// <summary>
/// Written back to the Orders table by <see cref="Data.OrderRepository"/>
/// after a successful SQL insert.
/// </summary>
public sealed class OrderRecord
{
    public Guid     Id            { get; set; } = Guid.NewGuid();
    public Guid     CorrelationId { get; set; }
    public string   CustomerName  { get; set; } = string.Empty;
    public string   CustomerEmail { get; set; } = string.Empty;
    public string   Region        { get; set; } = string.Empty;
    public string   Status        { get; set; } = "Processing";
    public DateTime CreatedAt     { get; set; } = DateTime.UtcNow;
    public List<OrderItemCommand> Items { get; set; } = [];
}