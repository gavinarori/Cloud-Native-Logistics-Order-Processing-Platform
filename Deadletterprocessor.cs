using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using OrderWorkerFunction.Models;

namespace OrderWorkerFunction.Functions;

/// <summary>
/// Timer-triggered function that drains the Service Bus dead-letter sub-queue
/// every 5 minutes, classifies each dead-lettered message by reason, and emits
/// structured telemetry to Application Insights for alerting.
///
/// Dead-letter reasons produced by <see cref="ProcessOrderFunction"/>:
///   "DeserializationFailure" — poison message, requires manual inspection
///   "InsufficientStock"      — business failure, requires stock replenishment / customer notification
///   (anything else)          — exhausted retries on transient error
///
/// In production: hook an Azure Monitor alert on the custom event
/// "OrderDeadLettered" with severity based on Reason.
/// For InsufficientStock: trigger a Logic App to email the customer.
/// </summary>
public sealed class DeadLetterProcessor
{
    private readonly ServiceBusClient _busClient;
    private readonly IConfiguration   _config;
    private readonly ILogger<DeadLetterProcessor> _logger;

    private static readonly JsonSerializerOptions _json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public DeadLetterProcessor(
        ServiceBusClient busClient,
        IConfiguration config,
        ILogger<DeadLetterProcessor> logger)
    {
        _busClient = busClient;
        _config    = config;
        _logger    = logger;
    }

    [Function(nameof(DeadLetterProcessor))]
    public async Task RunAsync(
        [TimerTrigger("0 */5 * * * *")] TimerInfo timer,   // Every 5 minutes
        FunctionContext context,
        CancellationToken ct)
    {
        var queueName = _config["Azure:ServiceBus:OrderQueueName"]
            ?? throw new InvalidOperationException("Queue name not configured.");

        var dlqPath = $"{queueName}/$DeadLetterQueue";

        await using var receiver = _busClient.CreateReceiver(
            dlqPath,
            new ServiceBusReceiverOptions
            {
                ReceiveMode = ServiceBusReceiveMode.PeekLock,
                // Dead-letter queue does NOT use sessions even if main queue does
                SubQueue    = SubQueue.DeadLetter
            });

        // Drain up to 50 dead-lettered messages per invocation
        var messages = await receiver.ReceiveMessagesAsync(maxMessages: 50, maxWaitTime: TimeSpan.FromSeconds(5), ct);

        if (messages.Count == 0)
        {
            _logger.LogDebug("Dead-letter queue is empty.");
            return;
        }

        _logger.LogWarning("Processing {Count} dead-lettered messages.", messages.Count);

        foreach (var msg in messages)
        {
            var reason = msg.DeadLetterReason    ?? "Unknown";
            var detail = msg.DeadLetterErrorDescription ?? string.Empty;

            // ── Classify and log ──────────────────────────────────────────────
            _logger.LogError(
                "Dead-lettered order. MessageId={MessageId} Session={Session} " +
                "Reason={Reason} Detail={Detail} DeliveryCount={Delivery}",
                msg.MessageId, msg.SessionId, reason, detail, msg.DeliveryCount);

            // ── Deserialise for context (best-effort) ─────────────────────────
            CreateOrderCommand? command = null;
            try
            {
                command = JsonSerializer.Deserialize<CreateOrderCommand>(msg.Body.ToString(), _json);
            }
            catch { /* Poison message — body unreadable, log what we have */ }

            // ── Emit structured custom event to Application Insights ──────────
            // Hook an Azure Monitor alert on this event name.
            using (_logger.BeginScope(new Dictionary<string, object>
            {
                ["EventName"]      = "OrderDeadLettered",
                ["MessageId"]      = msg.MessageId ?? string.Empty,
                ["SessionId"]      = msg.SessionId ?? string.Empty,
                ["DeadLetterReason"] = reason,
                ["CustomerEmail"]  = command?.CustomerEmail ?? "unknown",
                ["Region"]         = command?.Region        ?? "unknown",
                ["CorrelationId"]  = command?.CorrelationId.ToString() ?? "unknown",
                ["Severity"]       = reason == "InsufficientStock" ? "Warning" : "Error"
            }))
            {
                _logger.LogError("OrderDeadLettered: {Reason}", reason);
            }

            // ── Complete (remove from DLQ) so we don't process it again ──────
            // In a real system: archive to Azure Blob / Table Storage first.
            await receiver.CompleteMessageAsync(msg, ct);
        }
    }
}