using Azure.Identity;
using Azure.Messaging.ServiceBus;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using OrderService.Commands;
using OrderService.Data;
using OrderService.Endpoints;
using OrderService.Messaging;
using OrderService.Middleware;
using OrderService.Queries;

var builder = WebApplication.CreateBuilder(args);

// ══════════════════════════════════════════════════════════
// 1. ZERO TRUST — Azure Key Vault via Managed Identity
//    No passwords in appsettings.json or environment vars.
// ══════════════════════════════════════════════════════════
var keyVaultUri = builder.Configuration["Azure:KeyVaultUri"]
    ?? throw new InvalidOperationException("Azure:KeyVaultUri must be configured.");

// DefaultAzureCredential:
//   Production  → App Service Managed Identity (assigned in Bicep)
//   Development → VS / Azure CLI / Environment credentials
var credential = new DefaultAzureCredential();

builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), credential);

// ══════════════════════════════════════════════════════════
// 2. AZURE SQL — WRITE DbContext (primary endpoint)
//    Connection string fetched from Key Vault.
//    Authentication=Active Directory Managed Identity (no password).
// ══════════════════════════════════════════════════════════
var writeConn = builder.Configuration["Sql--OrdersConnectionString"]
    ?? throw new InvalidOperationException("Sql--OrdersConnectionString not found in Key Vault.");

builder.Services.AddDbContext<OrderDbContext>(opts =>
    opts.UseSqlServer(writeConn, sql =>
    {
        sql.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(30),
            errorNumbersToAdd: null);
        sql.CommandTimeout(30);
    }));

// ══════════════════════════════════════════════════════════
// 3. AZURE SQL — READ DbContext (secondary/read replica)
//    Points at the Business Critical secondary endpoint.
//    Registered as scoped so EF can pool connections per request.
// ══════════════════════════════════════════════════════════
var readConn = builder.Configuration["Sql--OrdersReadConnectionString"]
    ?? throw new InvalidOperationException("Sql--OrdersReadConnectionString not found in Key Vault.");

builder.Services.AddDbContext<OrderReadDbContext>(opts =>
    opts.UseSqlServer(readConn, sql =>
    {
        sql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null);
        sql.CommandTimeout(60); // Read replica may run heavier queries
    }));

// ══════════════════════════════════════════════════════════
// 4. AZURE SERVICE BUS — WRITE PATH
//    Passwordless via Managed Identity (no SAS keys).
// ══════════════════════════════════════════════════════════
var sbNamespace = builder.Configuration["Azure:ServiceBus:FullyQualifiedNamespace"]
    ?? throw new InvalidOperationException("Service Bus namespace not configured.");

builder.Services.AddSingleton(_ => new ServiceBusClient(sbNamespace, credential));
builder.Services.AddSingleton<ServiceBusPublisher>();

// ══════════════════════════════════════════════════════════
// 5. DOMAIN SERVICES
// ══════════════════════════════════════════════════════════
builder.Services.AddScoped<OrderQueries>();
builder.Services.AddValidatorsFromAssemblyContaining<CreateOrderCommandValidator>();

// ══════════════════════════════════════════════════════════
// 6. API INFRASTRUCTURE
// ══════════════════════════════════════════════════════════
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(opts =>
{
    opts.SwaggerDoc("v1", new()
    {
        Title       = "Order Microservice API",
        Version     = "v1",
        Description = "CQRS-based order processing service. Write path → Service Bus. " +
                      "Read path → Azure SQL Business Critical (read replica)."
    });
});

// ══════════════════════════════════════════════════════════
// 7. HEALTH CHECKS
//    Exposed at /health — monitored by Azure Container Apps.
// ══════════════════════════════════════════════════════════
builder.Services.AddHealthChecks()
    .AddDbContextCheck<OrderDbContext>(
        name: "azure-sql-write",
        tags: ["db", "write"])
    .AddDbContextCheck<OrderReadDbContext>(
        name: "azure-sql-read",
        tags: ["db", "read"])
    .AddAzureServiceBusQueue(
        fullyQualifiedNamespace: sbNamespace,
        queueName: builder.Configuration["Azure:ServiceBus:OrderQueueName"]!,
        tokenCredential: credential,
        name: "service-bus",
        tags: ["messaging"]);

// ══════════════════════════════════════════════════════════
// 8. TELEMETRY
// ══════════════════════════════════════════════════════════
builder.Services.AddApplicationInsightsTelemetry(opts =>
{
    opts.ConnectionString = builder.Configuration["AppInsights--ConnectionString"];
});

// ══════════════════════════════════════════════════════════
// 9. BUILD & PIPELINE
// ══════════════════════════════════════════════════════════
var app = builder.Build();

// Auto-apply EF migrations on startup (safe for containerised deployments)
// In production you may prefer a separate migration job — this is fine for dev.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    await db.Database.MigrateAsync();
}

app.UseMiddleware<ExceptionMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(opts =>
    {
        opts.SwaggerEndpoint("/swagger/v1/swagger.json", "Order Service v1");
        opts.RoutePrefix = string.Empty; // Swagger at root in dev
    });
}

app.UseHttpsRedirection();

// ── Map endpoints ─────────────────────────────────────────
app.MapOrderWriteEndpoints();
app.MapOrderReadEndpoints();

app.MapHealthChecks("/health");
app.MapHealthChecks("/health/ready",  new() { Predicate = check => check.Tags.Contains("db") });
app.MapHealthChecks("/health/live",   new() { Predicate = _ => false });

app.Run();