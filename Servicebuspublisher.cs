using System.Text.Json;
using Azure.Messaging.ServiceBus;
using OrderService.Commands;

namespace OrderService.Messaging;

/// <summary>
/// Publishes <see cref="CreateOrderCommand"/> messages to the Azure Service Bus
/// FIFO queue (Premium tier). This is the only write-path component in the
/// Order Microservice — actual SQL persistence is done by the Worker Function.
///
/// Key Service Bus properties set per message:
///   MessageId  → CorrelationId (drives 10-min duplicate detection window)
///   SessionId  → CustomerEmail (enforces FIFO ordering per customer)
///   Subject    → "CreateOrder" (allows future message routing by type)
/// </summary>
public sealed class ServiceBusPublisher : IAsyncDisposable
{
    private readonly ServiceBusSender _sender;
    private readonly ILogger<ServiceBusPublisher> _logger;

    private static readonly JsonSerializerOptions _json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ServiceBusPublisher(
        ServiceBusClient client,
        IConfiguration config,
        ILogger<ServiceBusPublisher> logger)
    {
        var queueName = config["Azure:ServiceBus:OrderQueueName"]
            ?? throw new InvalidOperationException("Service Bus queue name not configured.");

        _sender = client.CreateSender(queueName);
        _logger = logger;
    }

    /// <summary>
    /// Serialises and sends the command to the Service Bus queue.
    /// Returns immediately after the send — the Worker Function processes asynchronously.
    /// </summary>
    public async Task PublishAsync(CreateOrderCommand command, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(command, _json);

        var message = new ServiceBusMessage(payload)
        {
            // ── Duplicate detection ────────────────────────────────────────
            // If the caller retries with the same CorrelationId within the
            // 10-minute window (configured in Bicep), Service Bus silently
            // discards the duplicate without forwarding to the Worker Function.
            MessageId = command.CorrelationId.ToString(),

            // ── FIFO ordering ──────────────────────────────────────────────
            // All messages for the same customer share a SessionId. The Worker
            // Function acquires an exclusive session lock and processes them
            // in exactly the order they arrived.
            SessionId = command.CustomerEmail,

            // ── Routing metadata ───────────────────────────────────────────
            Subject     = "CreateOrder/v1",
            ContentType = "application/json",
        };

        // Application properties for filtering / observability
        message.ApplicationProperties["Region"]        = command.Region;
        message.ApplicationProperties["CorrelationId"] = command.CorrelationId.ToString();
        message.ApplicationProperties["SchemaVersion"] = "1.0";

        await _sender.SendMessageAsync(message, ct);

        _logger.LogInformation(
            "Published CreateOrder command. CorrelationId={CorrelationId} Customer={Email} Region={Region}",
            command.CorrelationId, command.CustomerEmail, command.Region);
    }

    public async ValueTask DisposeAsync() => await _sender.DisposeAsync();
}