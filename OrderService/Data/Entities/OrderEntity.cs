using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OrderService.Data.Entities;

[Table("Orders")]
public sealed class OrderEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(256)]
    public string CustomerEmail { get; set; } = string.Empty;

    [Required, MaxLength(256)]
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>AMER | EMEA | APAC — also the Cosmos DB partition key.</summary>
    [Required, MaxLength(10)]
    public string Region { get; set; } = string.Empty;

    [Required, MaxLength(50)]
    public string Status { get; set; } = OrderStatus.Queued;

    /// <summary>
    /// Stored for idempotency — a UNIQUE index on this column is the
    /// second line of defence against duplicate Service Bus deliveries.
    /// </summary>
    [Required]
    public Guid CorrelationId { get; set; }

    public DateTime  CreatedAt   { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt   { get; set; }
    public DateTime? FulfilledAt { get; set; }

    public ICollection<OrderItemEntity> Items { get; set; } = [];

    [NotMapped] public decimal TotalAmount => Items.Sum(i => i.UnitPrice * i.Quantity);
    [NotMapped] public int     TotalItems  => Items.Sum(i => i.Quantity);
}

[Table("OrderItems")]
public sealed class OrderItemEntity
{
    [Key]
    public Guid   Id          { get; set; } = Guid.NewGuid();
    public Guid   OrderId     { get; set; }
    public Guid   ProductId   { get; set; }

    [Required, MaxLength(100)]
    public string Sku         { get; set; } = string.Empty;

    [Required, MaxLength(256)]
    public string ProductName { get; set; } = string.Empty;

    [Column(TypeName = "decimal(18,2)")]
    public decimal UnitPrice  { get; set; }

    public int Quantity { get; set; }

    public OrderEntity Order { get; set; } = null!;
}

public static class OrderStatus
{
    public const string Queued     = "Queued";
    public const string Processing = "Processing";
    public const string Fulfilled  = "Fulfilled";
    public const string Failed     = "Failed";
    public const string Cancelled  = "Cancelled";
}
