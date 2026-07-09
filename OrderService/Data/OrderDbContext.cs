using Microsoft.EntityFrameworkCore;
using OrderService.Data.Entities;

namespace OrderService.Data;

/// <summary>Write primary — INSERT and UPDATE only. Points at Azure SQL primary.</summary>
public sealed class OrderDbContext : DbContext
{
    public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options) { }

    public DbSet<OrderEntity>     Orders     => Set<OrderEntity>();
    public DbSet<OrderItemEntity> OrderItems => Set<OrderItemEntity>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<OrderEntity>(e =>
        {
            e.HasKey(o => o.Id);
            e.HasIndex(o => o.CorrelationId).IsUnique()
             .HasDatabaseName("IX_Orders_CorrelationId");
            e.HasIndex(o => o.CustomerEmail)
             .HasDatabaseName("IX_Orders_CustomerEmail");
            e.HasIndex(o => new { o.Region, o.CreatedAt })
             .HasDatabaseName("IX_Orders_Region_CreatedAt");
            e.HasIndex(o => o.Status)
             .HasDatabaseName("IX_Orders_Status");
            e.Property(o => o.Status).HasDefaultValue(OrderStatus.Queued);
            e.Property(o => o.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        model.Entity<OrderItemEntity>(e =>
        {
            e.HasKey(i => i.Id);
            e.HasOne(i => i.Order).WithMany(o => o.Items)
             .HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(i => i.OrderId).HasDatabaseName("IX_OrderItems_OrderId");
            e.Property(i => i.UnitPrice).HasColumnType("decimal(18,2)");
        });
    }
}

/// <summary>
/// Read replica — SELECT only. Routes to Azure SQL secondary via
/// ApplicationIntent=ReadOnly in the connection string.
/// In local dev: same SQLite file as the write context.
/// </summary>
public sealed class OrderReadDbContext : DbContext
{
    public OrderReadDbContext(DbContextOptions<OrderReadDbContext> options) : base(options) { }

    public DbSet<OrderEntity>     Orders     => Set<OrderEntity>();
    public DbSet<OrderItemEntity> OrderItems => Set<OrderItemEntity>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<OrderEntity>(e =>
        {
            e.HasKey(o => o.Id);
            e.Property(o => o.Status).HasDefaultValue(OrderStatus.Queued);
            e.Property(o => o.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });
        model.Entity<OrderItemEntity>(e =>
        {
            e.HasKey(i => i.Id);
            e.HasOne(i => i.Order).WithMany(o => o.Items)
             .HasForeignKey(i => i.OrderId);
            e.Property(i => i.UnitPrice).HasColumnType("decimal(18,2)");
        });
    }
}
