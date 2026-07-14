using System.Text.Json;

namespace InventoryService.Middleware;

public sealed class ExceptionMiddleware
{
    private readonly RequestDelegate              _next;
    private readonly ILogger<ExceptionMiddleware>  _logger;
    private readonly IHostEnvironment             _env;

    private static readonly JsonSerializerOptions _json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ExceptionMiddleware(RequestDelegate next,
        ILogger<ExceptionMiddleware> logger, IHostEnvironment env)
    {
        _next = next; _logger = logger; _env = env;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        try { await _next(ctx); }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        { ctx.Response.StatusCode = 499; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception on {Method} {Path}",
                ctx.Request.Method, ctx.Request.Path);
            ctx.Response.StatusCode  = 500;
            ctx.Response.ContentType = "application/problem+json";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                type    = "https://tools.ietf.org/html/rfc7807",
                title   = "An unexpected error occurred.",
                status  = 500,
                traceId = ctx.TraceIdentifier,
                detail  = _env.IsDevelopment() ? ex.Message : null
            }, _json));
        }
    }
}
