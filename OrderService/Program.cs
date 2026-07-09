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
using Microsoft.Extensions.Diagnostics.HealthChecks;  

var builder = WebApplication.CreateBuilder(args);

// ── 1. Key Vault (skipped locally when URI is blank) ──────────────────────────
var keyVaultUri = builder.Configuration["Azure:KeyVaultUri"];
var credential  = new DefaultAzureCredential();
if (!string.IsNullOrWhiteSpace(keyVaultUri) && !builder.Environment.IsDevelopment())
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), credential);

// ── 2. Database ───────────────────────────────────────────────────────────────
//    Dev  → SQLite (auto-created, no install needed)
//    Prod → Azure SQL Business Critical (Managed Identity, no password)
var useSqlite = builder.Configuration.GetValue<bool>("UseLocalSqlite");

var writeConn = builder.Configuration.GetConnectionString("OrdersDb")
    ?? throw new InvalidOperationException("ConnectionStrings:OrdersDb not set.");
var readConn  = builder.Configuration.GetConnectionString("OrdersReadDb") ?? writeConn;

if (useSqlite)
{
    builder.Services.AddDbContext<OrderDbContext>(o => o.UseSqlite(writeConn));
    builder.Services.AddDbContext<OrderReadDbContext>(o => o.UseSqlite(readConn));
}
else
{
    builder.Services.AddDbContext<OrderDbContext>(o =>
        o.UseSqlServer(writeConn, s => s.EnableRetryOnFailure(5)));
    builder.Services.AddDbContext<OrderReadDbContext>(o =>
        o.UseSqlServer(readConn, s => s.EnableRetryOnFailure(3)));
}

// ── 3. Service Bus (optional locally) ────────────────────────────────────────
var sbNamespace = builder.Configuration["Azure:ServiceBus:FullyQualifiedNamespace"];
if (!string.IsNullOrWhiteSpace(sbNamespace))
    builder.Services.AddSingleton(_ => new ServiceBusClient(sbNamespace, credential));
builder.Services.AddSingleton<ServiceBusPublisher>();

// ── 4. Domain services ────────────────────────────────────────────────────────
builder.Services.AddScoped<OrderQueries>();
builder.Services.AddValidatorsFromAssemblyContaining<CreateOrderCommandValidator>();

// ── 5. Swagger ────────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o => o.SwaggerDoc("v1", new()
{
    Title       = "Order Service API",
    Version     = "v1",
    Description = "Write: POST /api/orders → Service Bus → Worker → Azure SQL\n" +
                  "Read:  GET  /api/orders → Azure SQL read replica"
}));

// ── 6. Health checks ──────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddDbContextCheck<OrderDbContext>(name: "sql-write", tags: ["db"])
    .AddDbContextCheck<OrderReadDbContext>(name: "sql-read",  tags: ["db"]);

// ── 7. CORS — allow the Blazor web app ───────────────────────────────────────
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("https://localhost:5001", "http://localhost:5000")
     .AllowAnyHeader().AllowAnyMethod()));

// ── 8. App Insights (optional) ────────────────────────────────────────────────
var aiConn = builder.Configuration["AppInsights--ConnectionString"];
if (!string.IsNullOrWhiteSpace(aiConn))
    builder.Services.AddApplicationInsightsTelemetry(o => o.ConnectionString = aiConn);

// ── Build ─────────────────────────────────────────────────────────────────────
var app = builder.Build();

// Auto-create schema on first run
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    if (useSqlite)
        await db.Database.EnsureCreatedAsync();   // SQLite: create tables from model
    else
        await db.Database.MigrateAsync();          // Azure SQL: run EF migrations
}

app.UseMiddleware<ExceptionMiddleware>();
app.UseCors();
app.UseHttpsRedirection();

// Swagger always on — open http://localhost:{port} to test
app.UseSwagger();
app.UseSwaggerUI(o => { o.SwaggerEndpoint("/swagger/v1/swagger.json", "Order Service v1");
                         o.RoutePrefix = string.Empty; });

app.MapOrderWriteEndpoints();
app.MapOrderReadEndpoints();
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("db") });

app.Run();
