using Microsoft.EntityFrameworkCore;
using OrderService.Data.Entities;

namespace OrderService.Data;

/// <summary>
/// EF Core DbContext for the Azure SQL write database (Business Critical tier).
///
/// Connection uses Managed Identity — no password in the connection string.
/// The read replica is handled by a separate <see cref="OrderReadDbContext"/>
/// that points at the secondary endpoint.
/// </summary>
public sealed class OrderDbContext : DbContext
{
    public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options) { }

    public DbSet<OrderEntity>     Orders     => Set<OrderEntity>();
    public DbSet<OrderItemEntity> OrderItems => Set<OrderItemEntity>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        // ── Orders table ──────────────────────────────────────────────────────
        model.Entity<OrderEntity>(e =>
        {
            e.HasKey(o => o.Id);

            // Unique constraint: one CorrelationId per order (idempotency gate)
            e.HasIndex(o => o.CorrelationId)
             .IsUnique()
             .HasDatabaseName("IX_Orders_CorrelationId");

            // Supports customer history queries on the read replica
            e.HasIndex(o => o.CustomerEmail)
             .HasDatabaseName("IX_Orders_CustomerEmail");

            // Supports Cosmos DB partition-key queries by region
            e.HasIndex(o => new { o.Region, o.CreatedAt })
             .HasDatabaseName("IX_Orders_Region_CreatedAt");

            // Status index — Worker Function updates this frequently
            e.HasIndex(o => o.Status)
             .HasDatabaseName("IX_Orders_Status");

            e.Property(o => o.Status)
             .HasDefaultValue(OrderStatus.Queued);

            e.Property(o => o.CreatedAt)
             .HasDefaultValueSql("GETUTCDATE()");
        });

        // ── OrderItems table ──────────────────────────────────────────────────
        model.Entity<OrderItemEntity>(e =>
        {
            e.HasKey(i => i.Id);

            e.HasOne(i => i.Order)
             .WithMany(o => o.Items)
             .HasForeignKey(i => i.OrderId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(i => i.OrderId)
             .HasDatabaseName("IX_OrderItems_OrderId");

            e.HasIndex(i => i.Sku)
             .HasDatabaseName("IX_OrderItems_Sku");

            e.Property(i => i.UnitPrice)
             .HasColumnType("decimal(18,2)");
        });
    }
}

/// <summary>
/// Read-only DbContext pointing at the Azure SQL Business Critical
/// secondary (read) replica. Registered with a different connection string
/// so EF never sends reads to the write primary during reporting.
/// </summary>
public sealed class OrderReadDbContext : DbContext
{
    public OrderReadDbContext(DbContextOptions<OrderReadDbContext> options) : base(options) { }

    public DbSet<OrderEntity>     Orders     => Set<OrderEntity>();
    public DbSet<OrderItemEntity> OrderItems => Set<OrderItemEntity>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        // Same schema, read-only — no migrations run against this context
        model.Entity<OrderEntity>().HasKey(o => o.Id);
        model.Entity<OrderItemEntity>().HasKey(i => i.Id);
        model.Entity<OrderItemEntity>()
             .HasOne(i => i.Order)
             .WithMany(o => o.Items)
             .HasForeignKey(i => i.OrderId);
    }
}