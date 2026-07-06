using FluentValidation;

namespace OrderService.Commands;

/// <summary>
/// The inbound command received by POST /api/orders.
/// This is validated here, then serialised and sent to Azure Service Bus.
/// The Worker Function receives this same payload and performs the SQL insert.
/// </summary>
public sealed class CreateOrderCommand
{
    public Guid   CorrelationId  { get; set; } = Guid.NewGuid();  // Caller can supply for idempotency
    public string CustomerName   { get; set; } = string.Empty;
    public string CustomerEmail  { get; set; } = string.Empty;

    /// <summary>
    /// Becomes the Service Bus SessionId (FIFO per customer) and
    /// the Cosmos DB partition key for global distribution.
    /// </summary>
    public string Region         { get; set; } = string.Empty;

    public List<OrderItemCommand> Items { get; set; } = [];
}

public sealed class OrderItemCommand
{
    public Guid    ProductId  { get; set; }
    public string  Sku        { get; set; } = string.Empty;
    public string  ProductName { get; set; } = string.Empty;
    public decimal UnitPrice  { get; set; }
    public int     Quantity   { get; set; }
}

// ── FluentValidation ─────────────────────────────────────────────────────────
public sealed class CreateOrderCommandValidator : AbstractValidator<CreateOrderCommand>
{
    private static readonly HashSet<string> ValidRegions = ["AMER", "EMEA", "APAC"];

    public CreateOrderCommandValidator()
    {
        RuleFor(x => x.CustomerName)
            .NotEmpty().WithMessage("Customer name is required.")
            .MaximumLength(256);

        RuleFor(x => x.CustomerEmail)
            .NotEmpty().WithMessage("Customer email is required.")
            .EmailAddress().WithMessage("A valid email address is required.")
            .MaximumLength(256);

        RuleFor(x => x.Region)
            .NotEmpty().WithMessage("Region is required.")
            .Must(r => ValidRegions.Contains(r))
            .WithMessage("Region must be one of: AMER, EMEA, APAC.");

        RuleFor(x => x.Items)
            .NotEmpty().WithMessage("At least one order item is required.")
            .Must(items => items.Count <= 100)
            .WithMessage("An order cannot exceed 100 line items.");

        RuleForEach(x => x.Items).SetValidator(new OrderItemCommandValidator());
    }
}

public sealed class OrderItemCommandValidator : AbstractValidator<OrderItemCommand>
{
    public OrderItemCommandValidator()
    {
        RuleFor(x => x.ProductId)
            .NotEmpty().WithMessage("ProductId is required.");

        RuleFor(x => x.Sku)
            .NotEmpty().WithMessage("SKU is required.")
            .MaximumLength(100);

        RuleFor(x => x.Quantity)
            .GreaterThan(0).WithMessage("Quantity must be at least 1.")
            .LessThanOrEqualTo(10_000).WithMessage("Quantity cannot exceed 10,000 per line.");

        RuleFor(x => x.UnitPrice)
            .GreaterThan(0).WithMessage("Unit price must be greater than zero.")
            .LessThan(1_000_000).WithMessage("Unit price exceeds maximum.");
    }
}