#!/usr/bin/env bash
# =============================================================================
# setup-order-service.sh
#
# Run this once from your learn/ folder:
#   chmod +x setup-order-service.sh
#   ./setup-order-service.sh
#
# What it does:
#   1. Creates a minimal ASP.NET Core Web project via dotnet new
#   2. Deletes the boilerplate files we don't need
#   3. Adds the NuGet packages
#   4. Writes every source file for the OrderService
#   5. Runs dotnet build to confirm everything compiles
# =============================================================================
set -e  # stop immediately if any command fails

# ── Where to create the project ───────────────────────────────────────────────
PARENT_DIR="$(pwd)"
PROJECT_NAME="OrderService"
PROJECT_DIR="$PARENT_DIR/$PROJECT_NAME"

echo ""
echo "======================================================"
echo "  Creating $PROJECT_NAME in $PARENT_DIR"
echo "======================================================"

# ── 1. Scaffold from the minimal web template ─────────────────────────────────
dotnet new web -n "$PROJECT_NAME" --framework net10.0 --force
cd "$PROJECT_DIR"

# ── 2. Delete boilerplate we replace ─────────────────────────────────────────
rm -f Program.cs
rm -f appsettings.json
rm -f appsettings.Development.json

echo "✓ Template created, boilerplate removed"

# ── 3. Create folder structure ────────────────────────────────────────────────
mkdir -p Commands
mkdir -p Data/Entities
mkdir -p Endpoints
mkdir -p Messaging
mkdir -p Middleware
mkdir -p Models
mkdir -p Queries

echo "✓ Folders created"

# ── 4. Write the project file ─────────────────────────────────────────────────
cat > "$PROJECT_NAME.csproj" << 'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>OrderService</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Azure.Identity"                                     Version="1.13.2" />
    <PackageReference Include="Azure.Extensions.AspNetCore.Configuration.Secrets" Version="1.3.2" />
    <PackageReference Include="Azure.Messaging.ServiceBus"                        Version="7.18.4" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer"           Version="9.0.5" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite"              Version="9.0.5" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design"              Version="9.0.5">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="FluentValidation.AspNetCore"                       Version="11.3.0" />
    <PackageReference Include="Swashbuckle.AspNetCore"                            Version="9.0.1" />
    <PackageReference Include="Microsoft.ApplicationInsights.AspNetCore"          Version="2.22.0" />
  </ItemGroup>

</Project>
CSPROJ

echo "✓ OrderService.csproj written"

# ── 5. appsettings.json ───────────────────────────────────────────────────────
cat > appsettings.json << 'JSON'
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Azure": {
    "KeyVaultUri": "",
    "ServiceBus": {
      "FullyQualifiedNamespace": "",
      "OrderQueueName": "orders-fifo-queue"
    }
  },
  "ConnectionStrings": {
    "OrdersDb":    "",
    "OrdersReadDb": ""
  },
  "Api": {
    "DefaultPageSize": 20,
    "MaxPageSize": 100
  }
}
JSON

# ── 6. appsettings.Development.json ──────────────────────────────────────────
cat > appsettings.Development.json << 'JSON'
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "Microsoft.EntityFrameworkCore.Database.Command": "Information"
    }
  },
  "ConnectionStrings": {
    "OrdersDb":    "Data Source=orders_dev.db",
    "OrdersReadDb": "Data Source=orders_dev.db"
  },
  "UseLocalSqlite": true
}
JSON

echo "✓ appsettings files written"

# ── 7. Data/Entities/OrderEntity.cs ──────────────────────────────────────────
cat > Data/Entities/OrderEntity.cs << 'CS'
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
CS

echo "✓ Data/Entities/OrderEntity.cs written"

# ── 8. Data/OrderDbContext.cs ─────────────────────────────────────────────────
cat > Data/OrderDbContext.cs << 'CS'
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
CS

echo "✓ Data/OrderDbContext.cs written"

# ── 9. Commands/CreateOrderCommand.cs ────────────────────────────────────────
cat > Commands/CreateOrderCommand.cs << 'CS'
using FluentValidation;

namespace OrderService.Commands;

public sealed class CreateOrderCommand
{
    public Guid   CorrelationId { get; set; } = Guid.NewGuid();
    public string CustomerName  { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    /// <summary>AMER | EMEA | APAC — Service Bus SessionId + Cosmos DB partition key.</summary>
    public string Region        { get; set; } = string.Empty;
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

public sealed class CreateOrderCommandValidator : AbstractValidator<CreateOrderCommand>
{
    private static readonly HashSet<string> ValidRegions = ["AMER", "EMEA", "APAC"];

    public CreateOrderCommandValidator()
    {
        RuleFor(x => x.CustomerName).NotEmpty().MaximumLength(256);
        RuleFor(x => x.CustomerEmail).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Region)
            .NotEmpty()
            .Must(r => ValidRegions.Contains(r))
            .WithMessage("Region must be AMER, EMEA, or APAC.");
        RuleFor(x => x.Items)
            .NotEmpty().WithMessage("At least one item is required.")
            .Must(i => i.Count <= 100).WithMessage("Max 100 items per order.");
        RuleForEach(x => x.Items).SetValidator(new OrderItemCommandValidator());
    }
}

public sealed class OrderItemCommandValidator : AbstractValidator<OrderItemCommand>
{
    public OrderItemCommandValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.Sku).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Quantity).GreaterThan(0).LessThanOrEqualTo(10_000);
        RuleFor(x => x.UnitPrice).GreaterThan(0m);
    }
}
CS

echo "✓ Commands/CreateOrderCommand.cs written"

# ── 10. Models/OrderDto.cs ────────────────────────────────────────────────────
cat > Models/OrderDto.cs << 'CS'
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
CS

echo "✓ Models/OrderDto.cs written"

# ── 11. Messaging/ServiceBusPublisher.cs ──────────────────────────────────────
cat > Messaging/ServiceBusPublisher.cs << 'CS'
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using OrderService.Commands;

namespace OrderService.Messaging;

/// <summary>
/// Publishes a CreateOrderCommand to the Service Bus FIFO queue.
/// Gracefully disabled when no namespace is configured (local dev).
/// </summary>
public sealed class ServiceBusPublisher : IAsyncDisposable
{
    private readonly ServiceBusSender?            _sender;
    private readonly ILogger<ServiceBusPublisher>  _logger;
    private readonly bool                          _enabled;

    private static readonly JsonSerializerOptions _json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ServiceBusPublisher(
        IServiceProvider services,
        IConfiguration config,
        ILogger<ServiceBusPublisher> logger)
    {
        _logger = logger;
        var client    = services.GetService<ServiceBusClient>();
        var queueName = config["Azure:ServiceBus:OrderQueueName"];

        if (client is not null && !string.IsNullOrWhiteSpace(queueName))
        {
            _sender  = client.CreateSender(queueName);
            _enabled = true;
        }
        else
        {
            _logger.LogWarning(
                "Service Bus not configured — orders will NOT be queued. " +
                "Set Azure:ServiceBus:FullyQualifiedNamespace to enable.");
        }
    }

    public async Task PublishAsync(CreateOrderCommand command, CancellationToken ct = default)
    {
        if (!_enabled || _sender is null)
        {
            _logger.LogWarning("Service Bus disabled. Skipping publish for {Id}.", command.CorrelationId);
            return;
        }

        var message = new ServiceBusMessage(JsonSerializer.Serialize(command, _json))
        {
            MessageId   = command.CorrelationId.ToString(),  // duplicate-detection key
            SessionId   = command.CustomerEmail,             // FIFO per customer
            Subject     = "CreateOrder/v1",
            ContentType = "application/json",
        };
        message.ApplicationProperties["Region"] = command.Region;

        await _sender.SendMessageAsync(message, ct);
        _logger.LogInformation(
            "Published CreateOrder {Id} for {Email}", command.CorrelationId, command.CustomerEmail);
    }

    public async ValueTask DisposeAsync()
    {
        if (_sender is not null) await _sender.DisposeAsync();
    }
}
CS

echo "✓ Messaging/ServiceBusPublisher.cs written"

# ── 12. Queries/OrderQueries.cs ───────────────────────────────────────────────
cat > Queries/OrderQueries.cs << 'CS'
using Microsoft.EntityFrameworkCore;
using OrderService.Data;
using OrderService.Data.Entities;
using OrderService.Models;

namespace OrderService.Queries;

/// <summary>
/// All reads go through OrderReadDbContext (Azure SQL secondary replica).
/// Heavy dashboard queries never touch the write primary — this eliminates
/// the Noisy Neighbor problem that caused checkout timeouts in the monolith.
/// </summary>
public sealed class OrderQueries
{
    private readonly OrderReadDbContext    _db;
    private readonly ILogger<OrderQueries> _logger;

    public OrderQueries(OrderReadDbContext db, ILogger<OrderQueries> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<PagedResult<OrderDto>> GetOrdersAsync(
        int page = 1, int pageSize = 20,
        string? status = null, string? region = null, string? email = null,
        CancellationToken ct = default)
    {
        page     = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var q = _db.Orders.AsNoTracking().AsSplitQuery().Include(o => o.Items).AsQueryable();

        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(o => o.Status == status);
        if (!string.IsNullOrWhiteSpace(region)) q = q.Where(o => o.Region == region);
        if (!string.IsNullOrWhiteSpace(email))  q = q.Where(o => o.CustomerEmail == email);

        var total = await q.CountAsync(ct);
        var rows  = await q.OrderByDescending(o => o.CreatedAt)
                           .Skip((page - 1) * pageSize).Take(pageSize)
                           .ToListAsync(ct);

        return new PagedResult<OrderDto>(rows.Select(ToDto).ToList(),
            page, pageSize, total, page * pageSize < total);
    }

    public async Task<OrderDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var e = await _db.Orders.AsNoTracking().AsSplitQuery()
                         .Include(o => o.Items)
                         .FirstOrDefaultAsync(o => o.Id == id, ct);
        return e is null ? null : ToDto(e);
    }

    public async Task<OrderDto?> GetByCorrelationIdAsync(Guid correlationId, CancellationToken ct = default)
    {
        var e = await _db.Orders.AsNoTracking().AsSplitQuery()
                         .Include(o => o.Items)
                         .FirstOrDefaultAsync(o => o.CorrelationId == correlationId, ct);
        return e is null ? null : ToDto(e);
    }

    public async Task<OrderSummaryDto> GetSummaryAsync(CancellationToken ct = default)
    {
        var today      = DateTime.UtcNow.Date;
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);

        var counts = await _db.Orders.AsNoTracking().GroupBy(_ => 1).Select(g => new
        {
            Total     = g.Count(),
            Pending   = g.Count(o => o.Status == OrderStatus.Queued || o.Status == OrderStatus.Processing),
            Failed    = g.Count(o => o.Status == OrderStatus.Failed),
            DoneToday = g.Count(o => o.Status == OrderStatus.Fulfilled && o.FulfilledAt >= today)
        }).FirstOrDefaultAsync(ct);

        var revenueToday = await _db.OrderItems.AsNoTracking()
            .Where(i => i.Order.Status == OrderStatus.Fulfilled && i.Order.FulfilledAt >= today)
            .SumAsync(i => (decimal?)i.UnitPrice * i.Quantity, ct) ?? 0m;

        var revenueMonth = await _db.OrderItems.AsNoTracking()
            .Where(i => i.Order.Status == OrderStatus.Fulfilled && i.Order.FulfilledAt >= monthStart)
            .SumAsync(i => (decimal?)i.UnitPrice * i.Quantity, ct) ?? 0m;

        return new OrderSummaryDto(counts?.Total ?? 0, counts?.Pending ?? 0,
            counts?.DoneToday ?? 0, counts?.Failed ?? 0,
            revenueToday, revenueMonth, DateTime.UtcNow);
    }

    private static OrderDto ToDto(OrderEntity e) => new(
        e.Id, e.CustomerName, e.CustomerEmail, e.Region, e.Status,
        e.CreatedAt, e.UpdatedAt, e.FulfilledAt,
        e.Items.Sum(i => i.UnitPrice * i.Quantity),
        e.Items.Sum(i => i.Quantity),
        e.Items.Select(i => new OrderItemDto(
            i.Id, i.ProductId, i.Sku, i.ProductName,
            i.UnitPrice, i.Quantity, i.UnitPrice * i.Quantity)).ToList());
}
CS

echo "✓ Queries/OrderQueries.cs written"

# ── 13. Endpoints/OrderWriteEndpoints.cs ──────────────────────────────────────
cat > Endpoints/OrderWriteEndpoints.cs << 'CS'
using FluentValidation;
using OrderService.Commands;
using OrderService.Messaging;
using OrderService.Models;

namespace OrderService.Endpoints;

/// <summary>
/// CQRS write path.
/// POST /api/orders → validate → publish to Service Bus → 202 Accepted
/// The order does not exist in SQL yet at this point.
/// The Worker Function creates it after dequeuing the message.
/// </summary>
public static class OrderWriteEndpoints
{
    public static IEndpointRouteBuilder MapOrderWriteEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/orders")
           .WithTags("Orders — Write")
           .WithOpenApi()
           .MapPost("/", CreateOrderAsync)
           .WithName("CreateOrder")
           .WithSummary("Queue a new order via Service Bus FIFO.")
           .Produces<EnqueuedOrderResponse>(StatusCodes.Status202Accepted)
           .ProducesValidationProblem()
           .Produces(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static async Task<IResult> CreateOrderAsync(
        CreateOrderCommand             command,
        IValidator<CreateOrderCommand> validator,
        ServiceBusPublisher            publisher,
        ILogger<Program>               logger,
        CancellationToken              ct)
    {
        // Validate
        var result = await validator.ValidateAsync(command, ct);
        if (!result.IsValid)
            return Results.ValidationProblem(
                result.Errors.GroupBy(e => e.PropertyName)
                             .ToDictionary(g => g.Key,
                                           g => g.Select(e => e.ErrorMessage).ToArray()));

        if (command.CorrelationId == Guid.Empty)
            command.CorrelationId = Guid.NewGuid();

        // Publish
        try   { await publisher.PublishAsync(command, ct); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Service Bus publish failed. CorrelationId={Id}", command.CorrelationId);
            return Results.Problem(
                title: "Service Unavailable",
                detail: "Order queue temporarily unavailable. Please retry.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Accepted(
            $"/api/orders/correlation/{command.CorrelationId}",
            new EnqueuedOrderResponse(
                command.CorrelationId, "Queued",
                "Order queued. Worker Function will persist it to Azure SQL shortly.",
                $"/api/orders/correlation/{command.CorrelationId}"));
    }
}
CS

echo "✓ Endpoints/OrderWriteEndpoints.cs written"

# ── 14. Endpoints/OrderReadEndpoints.cs ───────────────────────────────────────
cat > Endpoints/OrderReadEndpoints.cs << 'CS'
using OrderService.Models;
using OrderService.Queries;

namespace OrderService.Endpoints;

/// <summary>
/// CQRS read path — all queries hit the Azure SQL read replica.
/// </summary>
public static class OrderReadEndpoints
{
    public static IEndpointRouteBuilder MapOrderReadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/orders").WithTags("Orders — Read").WithOpenApi();

        group.MapGet("/", GetOrdersAsync)
             .WithName("GetOrders")
             .WithSummary("Paginated order list from the read replica.")
             .Produces<PagedResult<OrderDto>>();

        group.MapGet("/summary", GetSummaryAsync)
             .WithName("GetOrderSummary")
             .WithSummary("KPI aggregates — cached in Redis by the web app (TTL 60s).")
             .Produces<OrderSummaryDto>();

        group.MapGet("/{id:guid}", GetByIdAsync)
             .WithName("GetOrderById")
             .Produces<OrderDto>().Produces(404);

        group.MapGet("/correlation/{correlationId:guid}", GetByCorrelationIdAsync)
             .WithName("GetOrderByCorrelationId")
             .WithSummary("Poll after POST /api/orders returns 202.")
             .Produces<OrderDto>().Produces(404);

        return app;
    }

    private static async Task<IResult> GetOrdersAsync(
        OrderQueries q, int page = 1, int pageSize = 20,
        string? status = null, string? region = null, string? email = null,
        CancellationToken ct = default)
        => Results.Ok(await q.GetOrdersAsync(page, pageSize, status, region, email, ct));

    private static async Task<IResult> GetSummaryAsync(
        OrderQueries q, CancellationToken ct = default)
        => Results.Ok(await q.GetSummaryAsync(ct));

    private static async Task<IResult> GetByIdAsync(
        Guid id, OrderQueries q, CancellationToken ct = default)
    {
        var order = await q.GetByIdAsync(id, ct);
        return order is null ? Results.NotFound() : Results.Ok(order);
    }

    private static async Task<IResult> GetByCorrelationIdAsync(
        Guid correlationId, OrderQueries q, CancellationToken ct = default)
    {
        var order = await q.GetByCorrelationIdAsync(correlationId, ct);
        return order is null ? Results.NotFound() : Results.Ok(order);
    }
}
CS

echo "✓ Endpoints/OrderReadEndpoints.cs written"

# ── 15. Middleware/ExceptionMiddleware.cs ─────────────────────────────────────
cat > Middleware/ExceptionMiddleware.cs << 'CS'
using System.Text.Json;

namespace OrderService.Middleware;

/// <summary>
/// Returns RFC 7807 problem+json for every unhandled exception.
/// Exposes the exception message only in Development.
/// </summary>
public sealed class ExceptionMiddleware
{
    private readonly RequestDelegate              _next;
    private readonly ILogger<ExceptionMiddleware>  _logger;
    private readonly IHostEnvironment             _env;

    private static readonly JsonSerializerOptions _json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ExceptionMiddleware(RequestDelegate next,
        ILogger<ExceptionMiddleware> logger, IHostEnvironment env)
    {
        _next = next; _logger = logger; _env = env;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        try { await _next(ctx); }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            ctx.Response.StatusCode = 499;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception on {Method} {Path}",
                ctx.Request.Method, ctx.Request.Path);

            ctx.Response.StatusCode  = 500;
            ctx.Response.ContentType = "application/problem+json";

            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                type    = "https://tools.ietf.org/html/rfc7807",
                title   = "An unexpected error occurred.",
                status  = 500,
                traceId = ctx.TraceIdentifier,
                detail  = _env.IsDevelopment() ? ex.Message : null
            }, _json));
        }
    }
}
CS

echo "✓ Middleware/ExceptionMiddleware.cs written"

# ── 16. Program.cs ────────────────────────────────────────────────────────────
cat > Program.cs << 'CS'
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using OrderService.Commands;
using OrderService.Data;
using OrderService.Endpoints;
using OrderService.Messaging;
using OrderService.Middleware;
using OrderService.Queries;

var builder = WebApplication.CreateBuilder(args);

// ── 1. Key Vault (skipped locally when URI is blank) ──────────────────────────
var keyVaultUri = builder.Configuration["Azure:KeyVaultUri"];
var credential  = new DefaultAzureCredential();
if (!string.IsNullOrWhiteSpace(keyVaultUri) && !builder.Environment.IsDevelopment())
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), credential);

// ── 2. Database ───────────────────────────────────────────────────────────────
//    Dev  → SQLite (auto-created, no install needed)
//    Prod → Azure SQL Business Critical (Managed Identity, no password)
var useSqlite = builder.Configuration.GetValue<bool>("UseLocalSqlite");

var writeConn = builder.Configuration.GetConnectionString("OrdersDb")
    ?? throw new InvalidOperationException("ConnectionStrings:OrdersDb not set.");
var readConn  = builder.Configuration.GetConnectionString("OrdersReadDb") ?? writeConn;

if (useSqlite)
{
    builder.Services.AddDbContext<OrderDbContext>(o => o.UseSqlite(writeConn));
    builder.Services.AddDbContext<OrderReadDbContext>(o => o.UseSqlite(readConn));
}
else
{
    builder.Services.AddDbContext<OrderDbContext>(o =>
        o.UseSqlServer(writeConn, s => s.EnableRetryOnFailure(5)));
    builder.Services.AddDbContext<OrderReadDbContext>(o =>
        o.UseSqlServer(readConn, s => s.EnableRetryOnFailure(3)));
}

// ── 3. Service Bus (optional locally) ────────────────────────────────────────
var sbNamespace = builder.Configuration["Azure:ServiceBus:FullyQualifiedNamespace"];
if (!string.IsNullOrWhiteSpace(sbNamespace))
    builder.Services.AddSingleton(_ => new ServiceBusClient(sbNamespace, credential));
builder.Services.AddSingleton<ServiceBusPublisher>();

// ── 4. Domain services ────────────────────────────────────────────────────────
builder.Services.AddScoped<OrderQueries>();
builder.Services.AddValidatorsFromAssemblyContaining<CreateOrderCommandValidator>();

// ── 5. Swagger ────────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o => o.SwaggerDoc("v1", new()
{
    Title       = "Order Service API",
    Version     = "v1",
    Description = "Write: POST /api/orders → Service Bus → Worker → Azure SQL\n" +
                  "Read:  GET  /api/orders → Azure SQL read replica"
}));

// ── 6. Health checks ──────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddDbContextCheck<OrderDbContext>(name: "sql-write", tags: ["db"])
    .AddDbContextCheck<OrderReadDbContext>(name: "sql-read",  tags: ["db"]);

// ── 7. CORS — allow the Blazor web app ───────────────────────────────────────
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("https://localhost:5001", "http://localhost:5000")
     .AllowAnyHeader().AllowAnyMethod()));

// ── 8. App Insights (optional) ────────────────────────────────────────────────
var aiConn = builder.Configuration["AppInsights--ConnectionString"];
if (!string.IsNullOrWhiteSpace(aiConn))
    builder.Services.AddApplicationInsightsTelemetry(o => o.ConnectionString = aiConn);

// ── Build ─────────────────────────────────────────────────────────────────────
var app = builder.Build();

// Auto-create schema on first run
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    if (useSqlite)
        await db.Database.EnsureCreatedAsync();   // SQLite: create tables from model
    else
        await db.Database.MigrateAsync();          // Azure SQL: run EF migrations
}

app.UseMiddleware<ExceptionMiddleware>();
app.UseCors();
app.UseHttpsRedirection();

// Swagger always on — open http://localhost:{port} to test
app.UseSwagger();
app.UseSwaggerUI(o => { o.SwaggerEndpoint("/swagger/v1/swagger.json", "Order Service v1");
                         o.RoutePrefix = string.Empty; });

app.MapOrderWriteEndpoints();
app.MapOrderReadEndpoints();
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("db") });

app.Run();
CS

echo "✓ Program.cs written"

# ── 17. Restore packages ──────────────────────────────────────────────────────
echo ""
echo "Running dotnet restore..."
dotnet restore

# ── 18. Build to verify ───────────────────────────────────────────────────────
echo ""
echo "Running dotnet build..."
dotnet build --no-restore

echo ""
echo "======================================================"
echo "  ✅  OrderService is ready!"
echo ""
echo "  To run:  cd $PROJECT_DIR && dotnet run"
echo "  Then open: http://localhost:{port} for Swagger"
echo "======================================================"
