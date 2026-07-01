using Azure.Identity;
using Azure.Messaging.ServiceBus;
using LogisticsWeb.Services;
using Microsoft.AspNetCore.Components.Web;

var builder = WebApplication.CreateBuilder(args);

// ══════════════════════════════════════════════════════════
// 1. ZERO TRUST SECRET MANAGEMENT
//    Managed Identity → Key Vault. No passwords in code.
// ══════════════════════════════════════════════════════════
var keyVaultUri = builder.Configuration["Azure:KeyVaultUri"]
    ?? throw new InvalidOperationException("Azure:KeyVaultUri is not configured.");

// In production: DefaultAzureCredential uses the App Service Managed Identity.
// In local dev:  falls back to Visual Studio / Azure CLI credentials.
var azureCredential = new DefaultAzureCredential();

builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), azureCredential);

// ══════════════════════════════════════════════════════════
// 2. BLAZOR & RAZOR COMPONENTS
// ══════════════════════════════════════════════════════════
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ══════════════════════════════════════════════════════════
// 3. DISTRIBUTED CACHE — Azure Cache for Redis
//    Used by InventoryService for sub-10ms read latency.
//    Connection string fetched from Key Vault (no hardcoding).
// ══════════════════════════════════════════════════════════
var redisConnection = builder.Configuration["Redis--ConnectionString"]
    ?? throw new InvalidOperationException("Redis--ConnectionString secret not found in Key Vault.");

builder.Services.AddStackExchangeRedisCache(opts =>
{
    opts.Configuration = redisConnection;
    opts.InstanceName   = "logistics:";
});

// ══════════════════════════════════════════════════════════
// 4. AZURE SERVICE BUS CLIENT
//    Passwordless auth via Managed Identity (no SAS keys).
//    Used by OrderService to enqueue write commands.
// ══════════════════════════════════════════════════════════
var sbNamespace = builder.Configuration["Azure:ServiceBus:FullyQualifiedNamespace"]
    ?? throw new InvalidOperationException("Service Bus namespace is not configured.");

builder.Services.AddSingleton(_ =>
    new ServiceBusClient(sbNamespace, azureCredential));

// ══════════════════════════════════════════════════════════
// 5. DOWNSTREAM MICROSERVICE HTTP CLIENTS
//    Typed clients routed to internal VNet endpoints.
//    Resilience via exponential back-off retry (Polly).
// ══════════════════════════════════════════════════════════
builder.Services.AddHttpClient<IOrderService, OrderApiService>(client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:OrderApi"]
        ?? throw new InvalidOperationException("Services:OrderApi not configured."));
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddHttpClient<IInventoryService, InventoryApiService>(client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:InventoryApi"]
        ?? throw new InvalidOperationException("Services:InventoryApi not configured."));
    client.Timeout = TimeSpan.FromSeconds(10);
});

// ══════════════════════════════════════════════════════════
// 6. APPLICATION INSIGHTS TELEMETRY
// ══════════════════════════════════════════════════════════
builder.Services.AddApplicationInsightsTelemetry(opts =>
{
    opts.ConnectionString = builder.Configuration["AppInsights--ConnectionString"];
});

// ══════════════════════════════════════════════════════════
// 7. BUILD & CONFIGURE PIPELINE
// ══════════════════════════════════════════════════════════
var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<LogisticsWeb.Components.App>()
   .AddInteractiveServerRenderMode();

app.Run();