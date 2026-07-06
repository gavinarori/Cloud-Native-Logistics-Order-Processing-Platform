using Microsoft.EntityFrameworkCore;
using OrderService.Data;
using OrderService.Data.Entities;
using OrderService.Models;

namespace OrderService.Queries;

/// <summary>
/// All read operations run through <see cref="OrderReadDbContext"/> which points
/// at the Azure SQL Business Critical <em>secondary (read) replica</em>.
/// This completely isolates reporting and dashboard reads from the write primary,
/// eliminating the Noisy Neighbor problem.
///
/// Query performance notes:
///   - AsNoTracking() on every query — we never materialise change-tracked entities
///     on the read side, saving both memory and round-trips to the state manager.
///   - AsSplitQuery() on includes — avoids cartesian explosion when loading Items.
///   - Projections to DTOs happen in SQL (not in-memory) wherever possible.
/// </summary>
public sealed class OrderQueries
{
    private readonly OrderReadDbContext _db;
    private readonly ILogger<OrderQueries> _logger;

    public OrderQueries(OrderReadDbContext db, ILogger<OrderQueries> logger)
    {
        _db     = db;
        _logger = logger;
    }

    // ── List / paged ──────────────────────────────────────────────────────────
    public async Task<PagedResult<OrderDto>> GetOrdersAsync(
        int    page        = 1,
        int    pageSize    = 20,
        string? status     = null,
        string? region     = null,
        string? email      = null,
        CancellationToken ct = default)
    {
        page     = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _db.Orders
            .AsNoTracking()
            .AsSplitQuery()
            .Include(o => o.Items)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(o => o.Status == status);

        if (!string.IsNullOrWhiteSpace(region))
            query = query.Where(o => o.Region == region);

        if (!string.IsNullOrWhiteSpace(email))
            query = query.Where(o => o.CustomerEmail == email);

        var totalCount = await query.CountAsync(ct);

        var entities = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<OrderDto>(
            Items:       entities.Select(ToDto).ToList(),
            Page:        page,
            PageSize:    pageSize,
            TotalCount:  totalCount,
            HasNextPage: page * pageSize < totalCount
        );
    }

    // ── Single order ──────────────────────────────────────────────────────────
    public async Task<OrderDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _db.Orders
            .AsNoTracking()
            .AsSplitQuery()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

        return entity is null ? null : ToDto(entity);
    }

    public async Task<OrderDto?> GetByCorrelationIdAsync(Guid correlationId, CancellationToken ct = default)
    {
        var entity = await _db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.CorrelationId == correlationId, ct);

        return entity is null ? null : ToDto(entity);
    }

    // ── Summary KPIs (expensive — cached in Redis by the web app) ─────────────
    public async Task<OrderSummaryDto> GetSummaryAsync(CancellationToken ct = default)
    {
        var today      = DateTime.UtcNow.Date;
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);

        // Run aggregations as a single round-trip to SQL via GroupBy projection
        var counts = await _db.Orders
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total      = g.Count(),
                Pending    = g.Count(o => o.Status == OrderStatus.Queued || o.Status == OrderStatus.Processing),
                Failed     = g.Count(o => o.Status == OrderStatus.Failed),
                DoneToday  = g.Count(o => o.Status == OrderStatus.Fulfilled && o.FulfilledAt >= today),
            })
            .FirstOrDefaultAsync(ct);

        // Revenue requires joining Items — separate query avoids cartesian product in GroupBy
        var revenueToday = await _db.OrderItems
            .AsNoTracking()
            .Where(i => i.Order.Status == OrderStatus.Fulfilled && i.Order.FulfilledAt >= today)
            .SumAsync(i => (decimal?)i.UnitPrice * i.Quantity, ct) ?? 0m;

        var revenueMonth = await _db.OrderItems
            .AsNoTracking()
            .Where(i => i.Order.Status == OrderStatus.Fulfilled && i.Order.FulfilledAt >= monthStart)
            .SumAsync(i => (decimal?)i.UnitPrice * i.Quantity, ct) ?? 0m;

        return new OrderSummaryDto(
            TotalOrders:      counts?.Total ?? 0,
            PendingOrders:    counts?.Pending ?? 0,
            FulfilledToday:   counts?.DoneToday ?? 0,
            FailedOrders:     counts?.Failed ?? 0,
            RevenueToday:     revenueToday,
            RevenueThisMonth: revenueMonth,
            ComputedAt:       DateTime.UtcNow
        );
    }

    // ── Projection helper ────────────────────────────────────────────────────
    private static OrderDto ToDto(OrderEntity e) => new(
        Id:           e.Id,
        CustomerName: e.CustomerName,
        CustomerEmail:e.CustomerEmail,
        Region:       e.Region,
        Status:       e.Status,
        CreatedAt:    e.CreatedAt,
        UpdatedAt:    e.UpdatedAt,
        FulfilledAt:  e.FulfilledAt,
        TotalAmount:  e.Items.Sum(i => i.UnitPrice * i.Quantity),
        TotalItems:   e.Items.Sum(i => i.Quantity),
        Items:        e.Items.Select(i => new OrderItemDto(
            Id:          i.Id,
            ProductId:   i.ProductId,
            Sku:         i.Sku,
            ProductName: i.ProductName,
            UnitPrice:   i.UnitPrice,
            Quantity:    i.Quantity,
            LineTotal:   i.UnitPrice * i.Quantity
        )).ToList()
    );
}