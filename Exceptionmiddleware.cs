using System.Text.Json;

namespace OrderService.Middleware;

/// <summary>
/// Catches unhandled exceptions and returns a consistent RFC 7807
/// <c>application/problem+json</c> response. Prevents stack traces leaking
/// to clients while still logging the full exception to Application Insights.
/// </summary>
public sealed class ExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionMiddleware> _logger;
    private readonly IHostEnvironment _env;

    private static readonly JsonSerializerOptions _json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ExceptionMiddleware(
        RequestDelegate next,
        ILogger<ExceptionMiddleware> logger,
        IHostEnvironment env)
    {
        _next   = next;
        _logger = logger;
        _env    = env;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await _next(ctx);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected — not an error
            ctx.Response.StatusCode = 499; // Nginx convention
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception processing {Method} {Path}",
                ctx.Request.Method, ctx.Request.Path);

            ctx.Response.StatusCode  = 500;
            ctx.Response.ContentType = "application/problem+json";

            var problem = new
            {
                type     = "https://tools.ietf.org/html/rfc7807",
                title    = "An unexpected error occurred.",
                status   = 500,
                traceId  = ctx.TraceIdentifier,
                // Only expose detail in development
                detail   = _env.IsDevelopment() ? ex.Message : null
            };

            await ctx.Response.WriteAsync(JsonSerializer.Serialize(problem, _json));
        }
    }
}