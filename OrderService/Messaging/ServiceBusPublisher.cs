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
