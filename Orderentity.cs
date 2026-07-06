using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OrderService.Data.Entities;

/// <summary>
/// The canonical write-side record stored in Azure SQL (Business Critical).
/// This is the source-of-truth; Cosmos DB is the eventually-consistent read replica.
///
/// Row-level locking strategy:
///   Stock reservation uses SELECT ... WITH (UPDLOCK, ROWLOCK) on the inventory
///   rows inside a serialisable transaction, preventing the double-booking race.
///   This is why ACID on Azure SQL is non-negotiable for the write path.
/// </summary>
[Table("Orders")]
public sealed class OrderEntity
{
    [Key]
    public Guid   Id            { get; set; } = Guid.NewGuid();

    // Indexed for lookup by customer — Cosmos DB partitions by Region
    [Required, MaxLength(256)]
    public string CustomerEmail { get; set; } = string.Empty;

    [Required, MaxLength(256)]
    public string CustomerName  { get; set; } = string.Empty;

    /// <summary>
    /// Matches the Service Bus SessionId and the Cosmos DB partition key.
    /// Values: AMER | EMEA | APAC
    /// </summary>
    [Required, MaxLength(10)]
    public string Region        { get; set; } = string.Empty;

    [Required, MaxLength(50)]
    public string Status        { get; set; } = OrderStatus.Queued;

    /// <summary>
    /// The Service Bus MessageId that created this order.
    /// Stored for idempotency checks — the Worker Function rejects
    /// messages whose CorrelationId already exists in this column.
    /// </summary>
    [Required]
    public Guid   CorrelationId { get; set; }

    public DateTime CreatedAt   { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt  { get; set; }
    public DateTime? FulfilledAt { get; set; }

    // ── Navigation
    public ICollection<OrderItemEntity> Items { get; set; } = [];

    // ── Computed (not mapped to column, used in queries)
    [NotMapped]
    public decimal TotalAmount => Items.Sum(i => i.UnitPrice * i.Quantity);
}

[Table("OrderItems")]
public sealed class OrderItemEntity
{
    [Key]
    public Guid    Id          { get; set; } = Guid.NewGuid();

    // FK with cascade delete — if an order is cancelled, items go with it
    public Guid    OrderId     { get; set; }

    [Required]
    public Guid    ProductId   { get; set; }

    [Required, MaxLength(100)]
    public string  Sku         { get; set; } = string.Empty;

    [Required, MaxLength(256)]
    public string  ProductName { get; set; } = string.Empty;

    [Column(TypeName = "decimal(18,2)")]
    public decimal UnitPrice   { get; set; }

    public int     Quantity    { get; set; }

    // ── Navigation
    public OrderEntity Order   { get; set; } = null!;
}

/// <summary>
/// String constants for OrderStatus — avoids a separate enum table in SQL.
/// </summary>
public static class OrderStatus
{
    public const string Queued     = "Queued";
    public const string Processing = "Processing";
    public const string Fulfilled  = "Fulfilled";
    public const string Failed     = "Failed";
    public const string Cancelled  = "Cancelled";
}