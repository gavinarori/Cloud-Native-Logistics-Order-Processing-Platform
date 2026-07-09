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
