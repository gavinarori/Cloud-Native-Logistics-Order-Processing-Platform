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
