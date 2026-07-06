using FluentValidation;
using OrderService.Commands;
using OrderService.Messaging;
using OrderService.Models;

namespace OrderService.Endpoints;

/// <summary>
/// WRITE-SIDE endpoints (CQRS command path).
///
/// POST /api/orders
///   1. Validates the command (FluentValidation)
///   2. Publishes to Azure Service Bus FIFO queue
///   3. Returns HTTP 202 Accepted with a poll URL
///
/// The response is intentionally 202, not 201 — the order does not yet
/// exist in Azure SQL at this point. The Worker Function creates it.
/// Callers poll GET /api/orders/correlation/{id} to check progress.
/// </summary>
public static class OrderWriteEndpoints
{
    public static IEndpointRouteBuilder MapOrderWriteEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/orders")
            .WithTags("Orders — Write")
            .WithOpenApi();

        group.MapPost("/", CreateOrderAsync)
            .WithName("CreateOrder")
            .WithSummary("Enqueue a new order to the Service Bus FIFO queue.")
            .WithDescription("""
                Sends a CreateOrder command to Azure Service Bus (Premium FIFO queue).
                Returns 202 Accepted immediately. The Worker Function processes the message
                asynchronously and inserts the record into Azure SQL.

                Idempotency: Supply the same CorrelationId on retry — Service Bus will
                deduplicate within the 10-minute detection window.
                """)
            .Produces<EnqueuedOrderResponse>(StatusCodes.Status202Accepted)
            .Produces<ValidationProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static async Task<IResult> CreateOrderAsync(
        CreateOrderCommand           command,
        IValidator<CreateOrderCommand> validator,
        ServiceBusPublisher          publisher,
        ILogger<Program>             logger,
        CancellationToken            ct)
    {
        // ── 1. Validate ───────────────────────────────────────────────────────
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
        {
            var errors = validation.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(e => e.ErrorMessage).ToArray());

            return Results.ValidationProblem(errors);
        }

        // ── 2. Ensure CorrelationId is set (caller may omit it) ───────────────
        if (command.CorrelationId == Guid.Empty)
            command.CorrelationId = Guid.NewGuid();

        // ── 3. Publish to Service Bus ─────────────────────────────────────────
        try
        {
            await publisher.PublishAsync(command, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to publish order command to Service Bus. CorrelationId={Id}",
                command.CorrelationId);

            return Results.Problem(
                detail: "The order queue is temporarily unavailable. Please retry.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Service Bus Unavailable");
        }

        // ── 4. Return 202 Accepted with poll URL ──────────────────────────────
        return Results.Accepted(
            uri: $"/api/orders/correlation/{command.CorrelationId}",
            value: new EnqueuedOrderResponse(
                CorrelationId: command.CorrelationId,
                Status:        "Queued",
                Message:       "Your order has been queued for processing. " +
                               "The Worker Function will persist it to Azure SQL shortly.",
                PollUrl:       $"/api/orders/correlation/{command.CorrelationId}"
            ));
    }
}