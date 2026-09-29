#region MODULE_CONTRACT [DOMAIN(Middleware): Global exception handling; CONCEPT(GlobalExceptionMiddleware): catches all unhandled exceptions, returns JSON error; TECH(ASP.NET Core middleware, ADR-005)]
/**
 * [GREP_SUMMARY]: GlobalExceptionMiddleware, UseMiddleware, Exception, StatusCode, JSON, error response, ADR-005
 * [STRUCTURE]: GlobalExceptionMiddleware → InvokeAsync(next, ctx, logger) → try { await _next(ctx) } → catch ex → classify → log [IMP:CRITICAL] → write JSON → return (no rethrow)
 *
 * <summary>
 * [PURPOSE]: Catches all unhandled exceptions in the request pipeline and returns a structured JSON error response, preventing MCP connection breaks.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Never rethrows — always writes a response and returns. Guards against HasStarted to prevent ObjectDisposedException.
 *   ArgumentException → 400 Bad Request; InvalidOperationException → 503 Service Unavailable; all others → 500 InternalServerError.
 * [RATIONALE]: Per ADR-005 and SPEC §7, memory failures must not break the MCP connection. The middleware is the outermost safety net.
 * [CHANGES]: LAST_CHANGE: M10 creation.
 *   [CHANGES]: M10 @debug counter=1 (mem-038) — split middleware log severity per SPEC §7 / AC-10: statusCode==500 → LogCritical (with UnhandledException FATAL marker); statusCode==400|503 → LogError (same marker string preserved, downgraded severity). Previously LogCritical was used for ALL exception types, misrepresenting handled domain errors (bad_request/service_unavailable) as fatal-unhandled.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Middleware;

using McpMemoryService.Logging;
using Microsoft.Extensions.Logging;
using System.Text.Json;

/// <summary>
/// [PURPOSE]: Global exception middleware that catches all unhandled exceptions and returns a structured JSON error response.
/// </summary>
/// <remarks>
/// Implements the ASP.NET Core convention middleware pattern (InvokeAsync(RequestDelegate, HttpContext)).
/// Registered via <c>app.UseMiddleware&lt;GlobalExceptionMiddleware&gt;()</c> in Program.cs.
/// </remarks>
public sealed class GlobalExceptionMiddleware
{
    #region Fields

    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    #endregion Fields

    #region Constructors

    /// <summary>
    /// Creates a new <see cref="GlobalExceptionMiddleware"/>.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="logger">Logger for unhandled exception diagnostics.</param>
    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #endregion Constructors

    #region InvokeAsync

    /// <summary>
    /// [PURPOSE]: Invokes the middleware — wraps the rest of the pipeline in a try/catch.
    /// </summary>
    /// <param name="context">The HTTP context for the current request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// [INVARIANTS]: Never rethrows. Always writes a response if an exception occurs.
    ///   Guards <c>context.Response.HasStarted</c> to prevent ObjectDisposedException.
    /// </remarks>
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    #endregion InvokeAsync

    #region Private

    /// <summary>
    /// [PURPOSE]: Classifies an exception, logs it, and writes a JSON error response.
    /// </summary>
    /// <param name="context">HTTP context.</param>
    /// <param name="ex">The caught exception.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task HandleExceptionAsync(HttpContext context, Exception ex)
    {
        var (statusCode, code, message) = ClassifyException(ex);

        // BUG_FIX_CONTEXT: [HYPOTHESIS: GlobalExceptionMiddleware used LogCritical for ALL exception types (400/503/500) — violating SPEC §7 + the plan AC-10 requirement that non-500 results log at LogError (handled client/unavailable conditions) and only true 500 InternalServerError be LogCritical. Severity-flattening misrepresents handled-domain errors as fatal-unhandled.]
        // BUG_FIX_CONTEXT: [Why this fix: split severity by classified statusCode — 500 → LogCritical (with LddMarkers.UnhandledException FATAL marker), 400|503 → LogError (same marker string preserved for grep continuity, but downgraded severity to reflect the classified nature). No behavioral response change — same JSON body, same status, same marker text; only ILogger LogLevel adjusted per SPEC §7.]
        if (statusCode == 500)
        {
            _logger.LogCritical(ex, $"{LddMarkers.UnhandledException} {ex.GetType().Name}: {ex.Message}");
        }
        else
        {
            _logger.LogError(ex, $"{LddMarkers.UnhandledException} {ex.GetType().Name}: {ex.Message}");
        }

        if (context.Response.HasStarted)
        {
            _logger.LogWarning("Response has already started — cannot write error response. Type={Type}, Message={Message}",
                ex.GetType().Name, ex.Message);
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var body = JsonSerializer.Serialize(
            new { error = new { code, message } },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

        await context.Response.WriteAsync(body, CancellationToken.None);
    }

    /// <summary>
    /// [PURPOSE]: Classifies an exception into (statusCode, errorCode, message).
    /// </summary>
    /// <param name="ex">The exception to classify.</param>
    /// <returns>A tuple of (HTTP status code, error code string, human-readable message).</returns>
    private static (int StatusCode, string Code, string Message) ClassifyException(Exception ex)
    {
        return ex switch
        {
            ArgumentException => (400, "bad_request", ex.Message),
            InvalidOperationException => (503, "service_unavailable", ex.Message),
            _ => (500, "internal_error", ex.Message)
        };
    }

    #endregion Private
}
