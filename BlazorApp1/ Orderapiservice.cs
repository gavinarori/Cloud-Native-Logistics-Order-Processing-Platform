using System.Net.Http.Json;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using LogisticsWeb.Models;
using Microsoft.Extensions.Caching.Distributed;

namespace LogisticsWeb.Services;

/// <summary>
/// WRITE PATH  → Azure Service Bus (FIFO queue, duplicate detection via CorrelationId)
/// READ PATH   → Order microservice HTTP API (Azure SQL Business Critical read replica)
/// CACHE LAYER → Azure Cache for Redis (summary KPIs, TTL 60 s)
///
/// ServiceBusClient is nullable — when running locally without Azure configured,
/// EnqueueOrderAsync throws a clear error instead of crashing on startup.
/// </summary>
public sealed class OrderApiService : IOrderService
{
    private readonly HttpClient               _http;
    private readonly ServiceBusClient?        _busClient;   // nullable — optional in local dev
    private readonly IDistributedCache        _cache;
    private readonly IConfiguration           _config;
    private readonly ILogger<OrderApiService> _logger;

    private static readonly JsonSerializerOptions _json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // IServiceProvider lets us optionally resolve ServiceBusClient
    // without crashing DI when it isn't registered locally.
    public OrderApiService(
        HttpClient http,
        IServiceProvider services,
        IDistributedCache cache,
        IConfiguration config,
        ILogger<OrderApiService> logger)
    {
        _http      = http;
        _busClient = services.GetService<ServiceBusClient>();  // null if not registered
        _cache     = cache;
        _config    = config;
        _logger    = logger;
    }

    // ── WRITE PATH ────────────────────────────────────────────────────────────
    public async Task<Guid> EnqueueOrderAsync(
        CreateOrderCommand command, CancellationToken ct = default)
    {
        if (_busClient is null)
            throw new InvalidOperationException(
                "Service Bus is not configured. Set Azure:ServiceBus:FullyQualifiedNamespace " +
                "in appsettings.json or environment variables to enable order creation.");

        var queueName = _config["Azure:ServiceBus:OrderQueueName"]
            ?? throw new InvalidOperationException("Azure:ServiceBus:OrderQueueName not configured.");

        await using var sender = _busClient.CreateSender(queueName);

        var payload = JsonSerializer.Serialize(command, _json);

        var message = new ServiceBusMessage(payload)
        {
            MessageId   = command.CorrelationId.ToString(),  // duplicate detection key
            SessionId   = command.CustomerEmail,             // FIFO per customer
            ContentType = "application/json",
        };

        message.ApplicationProperties["Region"]  = command.Region;
        message.ApplicationProperties["Version"] = "1.0";

        await sender.SendMessageAsync(message, ct);

        _logger.LogInformation(
            "Order {CorrelationId} enqueued for {Email} in {Region}",
            command.CorrelationId, command.CustomerEmail, command.Region);

        return command.CorrelationId;
    }

    // ── READ PATH ─────────────────────────────────────────────────────────────
    public async Task<List<Order>> GetOrdersAsync(
        int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<List<Order>>(
                $"api/orders?page={page}&pageSize={pageSize}", ct);
            return result ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Order API unreachable — returning empty list for local dev.");
            return [];
        }
    }

    public async Task<Order?> GetOrderByIdAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<Order>($"api/orders/{id}", ct);
        }
        catch (HttpRequestException ex) when
            (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Order API unreachable for id {Id}.", id);
            return null;
        }
    }

    // ── CACHE LAYER ───────────────────────────────────────────────────────────
    public async Task<OrderSummary> GetOrderSummaryAsync(CancellationToken ct = default)
    {
        const string cacheKey = "order:summary";

        try
        {
            var cached = await _cache.GetStringAsync(cacheKey, ct);
            if (cached is not null)
            {
                _logger.LogDebug("Order summary served from Redis cache.");
                return JsonSerializer.Deserialize<OrderSummary>(cached, _json)!;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache read failed — falling back to API.");
        }

        // Cache miss → hit the Order microservice
        OrderSummary summary;
        try
        {
            summary = await _http.GetFromJsonAsync<OrderSummary>("api/orders/summary", ct)
                      ?? FallbackSummary();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Order summary API unreachable — using zeroed fallback.");
            return FallbackSummary();
        }

        // Write back to cache (best-effort — failure is not fatal)
        try
        {
            var opts = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60)
            };
            await _cache.SetStringAsync(
                cacheKey, JsonSerializer.Serialize(summary, _json), opts, ct);
        }
        catch { /* cache write failure is non-fatal */ }

        return summary;
    }

    private static OrderSummary FallbackSummary() => new()
    {
        ComputedAt = DateTime.UtcNow
    };
}