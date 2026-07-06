using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderWorkerFunction.Models;

namespace OrderWorkerFunction.Data;

/// <summary>
/// Performs the ACID write to Azure SQL (Business Critical, write primary).
///
/// Idempotency contract:
///   The Orders table has a UNIQUE index on CorrelationId (IX_Orders_CorrelationId).
///   If a duplicate message slips through Service Bus's 10-minute detection window,
///   the INSERT here will throw a SqlException with error 2627 (unique constraint
///   violation) — which we catch and treat as a successful no-op.
///
/// Stock reservation:
///   Uses SELECT … WITH (UPDLOCK, ROWLOCK) inside a SERIALIZABLE transaction to
///   lock the inventory row before decrementing stock. This prevents the double-
///   booking race where two concurrent orders both see stock = 1 and both succeed.
/// </summary>
public sealed class OrderRepository
{
    private readonly OrderWriteDbContext          _db;
    private readonly ILogger<OrderRepository>     _logger;

    public OrderRepository(OrderWriteDbContext db, ILogger<OrderRepository> logger)
    {
        _db     = db;
        _logger = logger;
    }

    /// <summary>
    /// Inserts the order and decrements stock inside a single serialisable transaction.
    /// Returns <c>true</c> if the order was created, <c>false</c> if it was a duplicate.
    /// Throws for any other error (triggers retry / dead-letter in the Function host).
    /// </summary>
    public async Task<bool> CreateOrderAsync(CreateOrderCommand command, CancellationToken ct)
    {
        // ── Idempotency pre-check (cheap read before acquiring a write lock) ──
        var alreadyExists = await _db.Orders
            .AsNoTracking()
            .AnyAsync(o => o.CorrelationId == command.CorrelationId, ct);

        if (alreadyExists)
        {
            _logger.LogWarning(
                "Duplicate message detected for CorrelationId={Id}. Skipping insert.",
                command.CorrelationId);
            return false;
        }

        // ── Begin SERIALIZABLE transaction ────────────────────────────────────
        await using var tx = await _db.Database
            .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);

        try
        {
            // ── 1. Reserve stock (UPDLOCK prevents concurrent decrement) ─────
            foreach (var item in command.Items)
            {
                // Raw SQL here for explicit lock hints — EF Core does not expose
                // UPDLOCK/ROWLOCK through LINQ, and this is too important to abstract.
                var rowsAffected = await _db.Database.ExecuteSqlRawAsync(
                    @"UPDATE Inventory WITH (UPDLOCK, ROWLOCK)
                      SET    StockLevel = StockLevel - {0},
                             UpdatedAt  = GETUTCDATE()
                      WHERE  Sku        = {1}
                        AND  StockLevel >= {0}",
                    item.Quantity, item.Sku, cancellationToken: ct);

                if (rowsAffected == 0)
                {
                    // Stock insufficient — roll back and signal the Worker to dead-letter
                    await tx.RollbackAsync(ct);
                    throw new InsufficientStockException(item.Sku, item.Quantity);
                }
            }

            // ── 2. Insert the order ───────────────────────────────────────────
            var order = new OrderEntity
            {
                Id            = Guid.NewGuid(),
                CorrelationId = command.CorrelationId,
                CustomerName  = command.CustomerName,
                CustomerEmail = command.CustomerEmail,
                Region        = command.Region,
                Status        = "Fulfilled",
                CreatedAt     = DateTime.UtcNow,
                FulfilledAt   = DateTime.UtcNow,
                Items         = command.Items.Select(i => new OrderItemEntity
                {
                    Id          = Guid.NewGuid(),
                    ProductId   = i.ProductId,
                    Sku         = i.Sku,
                    ProductName = i.ProductName,
                    UnitPrice   = i.UnitPrice,
                    Quantity    = i.Quantity
                }).ToList()
            };

            _db.Orders.Add(order);
            await _db.SaveChangesAsync(ct);

            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "Order {OrderId} created successfully. CorrelationId={CorrelationId}",
                order.Id, command.CorrelationId);

            return true;
        }
        catch (SqlException ex) when (ex.Number == 2627)
        {
            // Unique constraint on CorrelationId — true duplicate, commit was already
            // rolled back by SQL Server. Treat as success (idempotent no-op).
            await tx.RollbackAsync(ct);
            _logger.LogWarning(
                "CorrelationId={Id} violated unique constraint (race condition). Treating as duplicate.",
                command.CorrelationId);
            return false;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}

// ── Minimal EF Core write context ────────────────────────────────────────────
public sealed class OrderWriteDbContext : DbContext
{
    public OrderWriteDbContext(DbContextOptions<OrderWriteDbContext> options) : base(options) { }

    public DbSet<OrderEntity>     Orders     => Set<OrderEntity>();
    public DbSet<OrderItemEntity> OrderItems => Set<OrderItemEntity>();

    // Inventory table is owned by the InventoryService but we need it for
    // stock reservation. The Worker Function has SELECT+UPDATE rights only —
    // no schema changes originate from here.
    public DbSet<InventoryEntity> Inventory => Set<InventoryEntity>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<OrderEntity>(e =>
        {
            e.HasKey(o => o.Id);
            e.ToTable("Orders");
            e.HasIndex(o => o.CorrelationId).IsUnique();
            e.Property(o => o.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
        });

        model.Entity<OrderItemEntity>(e =>
        {
            e.HasKey(i => i.Id);
            e.ToTable("OrderItems");
            e.HasOne(i => i.Order).WithMany(o => o.Items)
             .HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.Property(i => i.UnitPrice).HasColumnType("decimal(18,2)");
        });

        model.Entity<InventoryEntity>(e =>
        {
            e.HasKey(i => i.Id);
            e.ToTable("Inventory");
            e.HasIndex(i => i.Sku).IsUnique();
        });
    }
}

// ── Entity classes local to the Worker ───────────────────────────────────────
public sealed class OrderEntity
{
    public Guid      Id            { get; set; }
    public Guid      CorrelationId { get; set; }
    public string    CustomerName  { get; set; } = string.Empty;
    public string    CustomerEmail { get; set; } = string.Empty;
    public string    Region        { get; set; } = string.Empty;
    public string    Status        { get; set; } = string.Empty;
    public DateTime  CreatedAt     { get; set; }
    public DateTime? FulfilledAt   { get; set; }
    public ICollection<OrderItemEntity> Items { get; set; } = [];
}

public sealed class OrderItemEntity
{
    public Guid    Id          { get; set; }
    public Guid    OrderId     { get; set; }
    public Guid    ProductId   { get; set; }
    public string  Sku         { get; set; } = string.Empty;
    public string  ProductName { get; set; } = string.Empty;
    public decimal UnitPrice   { get; set; }
    public int     Quantity    { get; set; }
    public OrderEntity Order   { get; set; } = null!;
}

public sealed class InventoryEntity
{
    public Guid     Id         { get; set; }
    public string   Sku        { get; set; } = string.Empty;
    public int      StockLevel { get; set; }
    public DateTime UpdatedAt  { get; set; }
}

public sealed class InsufficientStockException : Exception
{
    public string Sku      { get; }
    public int    Requested { get; }

    public InsufficientStockException(string sku, int requested)
        : base($"Insufficient stock for SKU '{sku}'. Requested: {requested}.")
    {
        Sku       = sku;
        Requested = requested;
    }
}