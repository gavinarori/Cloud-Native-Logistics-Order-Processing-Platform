using FluentValidation;

namespace OrderService.Commands;

public sealed class CreateOrderCommand
{
    public Guid   CorrelationId { get; set; } = Guid.NewGuid();
    public string CustomerName  { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    /// <summary>AMER | EMEA | APAC — Service Bus SessionId + Cosmos DB partition key.</summary>
    public string Region        { get; set; } = string.Empty;
    public List<OrderItemCommand> Items { get; set; } = [];
}

public sealed class OrderItemCommand
{
    public Guid    ProductId   { get; set; }
    public string  Sku         { get; set; } = string.Empty;
    public string  ProductName { get; set; } = string.Empty;
    public decimal UnitPrice   { get; set; }
    public int     Quantity    { get; set; }
}

public sealed class CreateOrderCommandValidator : AbstractValidator<CreateOrderCommand>
{
    private static readonly HashSet<string> ValidRegions = ["AMER", "EMEA", "APAC"];

    public CreateOrderCommandValidator()
    {
        RuleFor(x => x.CustomerName).NotEmpty().MaximumLength(256);
        RuleFor(x => x.CustomerEmail).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Region)
            .NotEmpty()
            .Must(r => ValidRegions.Contains(r))
            .WithMessage("Region must be AMER, EMEA, or APAC.");
        RuleFor(x => x.Items)
            .NotEmpty().WithMessage("At least one item is required.")
            .Must(i => i.Count <= 100).WithMessage("Max 100 items per order.");
        RuleForEach(x => x.Items).SetValidator(new OrderItemCommandValidator());
    }
}

public sealed class OrderItemCommandValidator : AbstractValidator<OrderItemCommand>
{
    public OrderItemCommandValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.Sku).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Quantity).GreaterThan(0).LessThanOrEqualTo(10_000);
        RuleFor(x => x.UnitPrice).GreaterThan(0m);
    }
}
