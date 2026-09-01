using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Rag.Business.Exceptions;

namespace Rag.WebApi.Infrastructure;

/// <summary>
/// Turns business-tier exceptions into <see cref="ProblemDetails"/> responses.
/// </summary>
/// <remarks>
/// The single place where failure classification becomes a status code. That is what keeps every
/// error response the same shape and keeps try/catch out of the endpoints entirely.
/// </remarks>
internal sealed class BusinessExceptionHandler(ILogger<BusinessExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>Advisory delay returned with a 503 when a dependency is throttling us.</summary>
    private const int UpstreamRetryAfterSeconds = 5;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not BusinessException businessException)
        {
            // Not ours: let the default handler produce a 500 and log it as unhandled.
            return false;
        }

        var statusCode = businessException switch
        {
            ValidationException => StatusCodes.Status400BadRequest,
            NotFoundException => StatusCodes.Status404NotFound,

            // Deliberately 503, not 500: the fault is a dependency we do not control
            // (throttling, an unreachable database), so the caller should retry rather than
            // treat it as a bug in this service.
            UpstreamException => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError,
        };

        if (statusCode >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Request failed with {Code}.", businessException.Code);
        }
        else
        {
            logger.LogInformation("Request rejected: {Code} — {Message}",
                businessException.Code, businessException.Message);
        }

        if (statusCode == StatusCodes.Status503ServiceUnavailable)
        {
            httpContext.Response.Headers.RetryAfter = UpstreamRetryAfterSeconds.ToString();
        }

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = TitleFor(statusCode),
            Detail = businessException.Message,
            Type = $"https://ragapi.invalid/errors/{businessException.Code}",
            Extensions = { ["code"] = businessException.Code },
        };

        httpContext.Response.StatusCode = statusCode;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken).ConfigureAwait(false);

        return true;
    }

    private static string TitleFor(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "The request was not valid.",
        StatusCodes.Status404NotFound => "The requested resource was not found.",
        StatusCodes.Status503ServiceUnavailable => "A dependency is unavailable. Retry shortly.",
        _ => "The service encountered an unexpected condition.",
    };
}
