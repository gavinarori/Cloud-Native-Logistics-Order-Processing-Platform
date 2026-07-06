using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using OrderWorkerFunction.Data;
using OrderWorkerFunction.Models;

namespace OrderWorkerFunction.Functions;

/// <summary>
/// Azure Functions Service Bus trigger — FIFO session-based processing.
///
/// How FIFO ordering is enforced:
///   The Service Bus queue uses Message Sessions (requiresSession: true in Bicep).
///   Each message carries SessionId = CustomerEmail. The Functions runtime acquires
///   an exclusive session lock, then calls this function for each message in the
///   session in arrival order. maxConcurrentCallsPerSession = 1 (in host.json)
///   ensures no two messages from the same customer run simultaneously.
///
/// Retry / dead-letter flow:
///   Transient failures (SQL timeout, network blip) → automatic retry by the
///   Functions host up to maxDeliveryCount (5, set in Bicep). After 5 failures
///   the message is moved to the dead-letter sub-queue, where
///   <see cref="DeadLetterProcessor"/> picks it up for alerting.
///
///   Business failures (InsufficientStockException) → message is explicitly
///   dead-lettered with a reason code so ops can inspect and replay.
/// </summary>
public sealed class ProcessOrderFunction
{
    private readonly OrderRepository _repo;
    private readonly ILogger<ProcessOrderFunction> _logger;

    private static readonly JsonSerializerOptions _json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ProcessOrderFunction(
        OrderRepository repo,
        ILogger<ProcessOrderFunction> logger)
    {
        _repo   = repo;
        _logger = logger;
    }

    [Function(nameof(ProcessOrderFunction))]
    [FixedDelayRetry(maxRetryCount: 3, delayInterval: "00:00:05")]
    public async Task RunAsync(
        [ServiceBusTrigger(
            queueName:          "%Azure:ServiceBus:OrderQueueName%",
            Connection:         "AzureWebJobsServiceBus",
            IsSessionsEnabled:  true)]          // Session lock = FIFO per customer
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions  messageActions,
        FunctionContext           context,
        CancellationToken         ct)
    {
        var correlationId = message.MessageId;
        var sessionId     = message.SessionId;    // CustomerEmail
        var region        = message.ApplicationProperties.TryGetValue("Region", out var r)
                            ? r?.ToString() ?? "Unknown"
                            : "Unknown";

        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["MessageId"]     = correlationId,
            ["SessionId"]     = sessionId,
            ["Region"]        = region,
            ["DeliveryCount"] = message.DeliveryCount
        });

        _logger.LogInformation(
            "Processing order message. MessageId={MessageId} Session={Session} Delivery={Delivery}",
            correlationId, sessionId, message.DeliveryCount);

        // ── Deserialise ───────────────────────────────────────────────────────
        CreateOrderCommand? command;
        try
        {
            var body = message.Body.ToString();
            command = JsonSerializer.Deserialize<CreateOrderCommand>(body, _json);

            if (command is null)
                throw new InvalidOperationException("Message body deserialised to null.");
        }
        catch (Exception ex)
        {
            // Poison message — cannot deserialise at all, dead-letter immediately
            _logger.LogError(ex,
                "Poison message: failed to deserialise. MessageId={MessageId}", correlationId);

            await messageActions.DeadLetterMessageAsync(
                message,
                deadLetterReason:            "DeserializationFailure",
                deadLetterErrorDescription:  ex.Message,
                cancellationToken:            ct);
            return;
        }

        // ── Process ───────────────────────────────────────────────────────────
        try
        {
            var created = await _repo.CreateOrderAsync(command, ct);

            if (created)
                _logger.LogInformation(
                    "Order successfully written to Azure SQL. CorrelationId={Id}", command.CorrelationId);
            else
                _logger.LogInformation(
                    "Duplicate order skipped (idempotent). CorrelationId={Id}", command.CorrelationId);

            // Complete the message — removes it from the queue
            await messageActions.CompleteMessageAsync(message, ct);
        }
        catch (InsufficientStockException stockEx)
        {
            // Business failure — don't retry, dead-letter with structured reason
            _logger.LogWarning(
                "Insufficient stock for SKU={Sku} Qty={Qty}. Dead-lettering order {Id}.",
                stockEx.Sku, stockEx.Requested, command.CorrelationId);

            await messageActions.DeadLetterMessageAsync(
                message,
                deadLetterReason:           "InsufficientStock",
                deadLetterErrorDescription: stockEx.Message,
                cancellationToken:          ct);
        }
        catch (Exception ex)
        {
            // Transient failure — abandon back to queue so the host can retry.
            // After maxDeliveryCount attempts the host dead-letters automatically.
            _logger.LogError(ex,
                "Transient failure processing order {Id}. Abandoning for retry. Delivery={Delivery}",
                command?.CorrelationId, message.DeliveryCount);

            await messageActions.AbandonMessageAsync(message, cancellationToken: ct);
        }
    }
}