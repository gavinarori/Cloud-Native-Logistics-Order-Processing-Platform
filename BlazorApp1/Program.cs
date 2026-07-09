using Azure.Identity;
using Azure.Messaging.ServiceBus;
using LogisticsWeb.Services;

var builder = WebApplication.CreateBuilder(args);

// ══════════════════════════════════════════════════════════
// 1. KEY VAULT  (skipped in local dev if URI not set)
//
//    In production on Azure: set Azure__KeyVaultUri in the
//    Container App environment variables.
//    Locally: leave it blank — the app runs without Azure.
// ══════════════════════════════════════════════════════════
var keyVaultUri = builder.Configuration["Azure:KeyVaultUri"];

if (!string.IsNullOrWhiteSpace(keyVaultUri) && !builder.Environment.IsDevelopment())
{
    var credential = new DefaultAzureCredential();
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), credential);
}

// ══════════════════════════════════════════════════════════
// 2. BLAZOR
// ══════════════════════════════════════════════════════════
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ══════════════════════════════════════════════════════════
// 3. REDIS  (only register if a connection string is found)
// ══════════════════════════════════════════════════════════
var redisConnection = builder.Configuration["Redis--ConnectionString"];

if (!string.IsNullOrWhiteSpace(redisConnection))
{
    builder.Services.AddStackExchangeRedisCache(opts =>
    {
        opts.Configuration = redisConnection;
        opts.InstanceName  = "logistics:";
    });
}
else
{
    // Fallback: in-memory cache for local development
    builder.Services.AddDistributedMemoryCache();
}

// ══════════════════════════════════════════════════════════
// 4. SERVICE BUS  (only if namespace configured)
// ══════════════════════════════════════════════════════════
var sbNamespace = builder.Configuration["Azure:ServiceBus:FullyQualifiedNamespace"];

if (!string.IsNullOrWhiteSpace(sbNamespace))
{
    builder.Services.AddSingleton(_ =>
        new ServiceBusClient(sbNamespace, new DefaultAzureCredential()));
}

// ══════════════════════════════════════════════════════════
// 5. HTTP CLIENTS FOR DOWNSTREAM SERVICES
// ══════════════════════════════════════════════════════════
var orderApiUrl     = builder.Configuration["Services:OrderApi"]     ?? "https://localhost:7001";
var inventoryApiUrl = builder.Configuration["Services:InventoryApi"] ?? "https://localhost:7002";

builder.Services.AddHttpClient<IOrderService, OrderApiService>(c =>
{
    c.BaseAddress = new Uri(orderApiUrl);
    c.Timeout     = TimeSpan.FromSeconds(10);
});

builder.Services.AddHttpClient<IInventoryService, InventoryApiService>(c =>
{
    c.BaseAddress = new Uri(inventoryApiUrl);
    c.Timeout     = TimeSpan.FromSeconds(10);
});

// ══════════════════════════════════════════════════════════
// 6. APP INSIGHTS  (optional in dev)
// ══════════════════════════════════════════════════════════
var aiConnString = builder.Configuration["AppInsights--ConnectionString"]
                ?? builder.Configuration["Azure:ApplicationInsights:ConnectionString"];

if (!string.IsNullOrWhiteSpace(aiConnString))
{
    builder.Services.AddApplicationInsightsTelemetry(o => o.ConnectionString = aiConnString);
}

// ══════════════════════════════════════════════════════════
// 7. BUILD + PIPELINE
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