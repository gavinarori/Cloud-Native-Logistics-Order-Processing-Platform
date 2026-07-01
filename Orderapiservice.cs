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
/// </summary>
public sealed class OrderApiService : IOrderService
{
    private readonly HttpClient          _http;
    private readonly ServiceBusClient    _busClient;
    private readonly IDistributedCache   _cache;
    private readonly IConfiguration      _config;
    private readonly ILogger<OrderApiService> _logger;

    private static readonly JsonSerializerOptions _json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public OrderApiService(
        HttpClient http,
        ServiceBusClient busClient,
        IDistributedCache cache,
        IConfiguration config,
        ILogger<OrderApiService> logger)
    {
        _http      = http;
        _busClient = busClient;
        _cache     = cache;
        _config    = config;
        _logger    = logger;
    }

    // ── WRITE PATH ───────────────────────────────────────────────────────────
    /// <inheritdoc/>
    public async Task<Guid> EnqueueOrderAsync(CreateOrderCommand command, CancellationToken ct = default)
    {
        var queueName = _config["Azure:ServiceBus:OrderQueueName"]
            ?? throw new InvalidOperationException("Service Bus queue name not configured.");

        await using var sender = _busClient.CreateSender(queueName);

        var payload = JsonSerializer.Serialize(command, _json);

        var message = new ServiceBusMessage(payload)
        {
            // MessageId drives duplicate detection (10-min window configured in Bicep).
            MessageId   = command.CorrelationId.ToString(),

            // SessionId enforces FIFO ordering per customer — orders from the
            // same customer are processed in sequence by the Worker Function.
            SessionId   = command.CustomerEmail,

            ContentType = "application/json",
        };

        message.ApplicationProperties["Region"]  = command.Region;
        message.ApplicationProperties["Version"] = "1.0";

        await sender.SendMessageAsync(message, ct);

        _logger.LogInformation(
            "Order command {CorrelationId} enqueued for customer {Email} in region {Region}",
            command.CorrelationId, command.CustomerEmail, command.Region);

        return command.CorrelationId;
    }

    // ── READ PATH ─────────────────────────────────────────────────────────────
    /// <inheritdoc/>
    public async Task<List<Order>> GetOrdersAsync(int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var response = await _http.GetFromJsonAsync<List<Order>>(
            $"api/orders?page={page}&pageSize={pageSize}", ct);

        return response ?? [];
    }

    /// <inheritdoc/>
    public async Task<Order?> GetOrderByIdAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<Order>($"api/orders/{id}", ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // ── CACHE LAYER ───────────────────────────────────────────────────────────
    /// <inheritdoc/>
    public async Task<OrderSummary> GetOrderSummaryAsync(CancellationToken ct = default)
    {
        const string cacheKey = "order:summary";

        var cached = await _cache.GetStringAsync(cacheKey, ct);
        if (cached is not null)
        {
            _logger.LogDebug("Order summary served from Redis cache.");
            return JsonSerializer.Deserialize<OrderSummary>(cached, _json)!;
        }

        // Cache miss → hit the Order microservice
        var summary = await _http.GetFromJsonAsync<OrderSummary>("api/orders/summary", ct)
            ?? new OrderSummary { ComputedAt = DateTime.UtcNow };

        var cacheOpts = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60)
        };
        await _cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(summary, _json), cacheOpts, ct);

        return summary;
    }
}