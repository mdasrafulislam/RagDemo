using Rag.Business.Models;
using Rag.Business.Services;

namespace Rag.WebApi.Endpoints;

internal static class HealthEndpoints
{
    internal static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", CheckAsync)
            .WithTags("Health")
            .WithName("Health")
            .WithSummary("Reports whether dependencies are reachable.")
            .WithDescription(
                "Always round-trips PostgreSQL. The embedding endpoint is only called when " +
                "`deep=true`, because every embedding call consumes OpenAI quota and a " +
                "frequently-polled probe would spend real money to report nothing new. Use " +
                "deep=true once after deployment, and the shallow check for liveness.")
            .Produces<HealthResult>()
            .Produces<HealthResult>(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static async Task<IResult> CheckAsync(
        IHealthService healthService,
        bool deep = false,
        CancellationToken cancellationToken = default)
    {
        var result = await healthService.CheckAsync(deep, cancellationToken).ConfigureAwait(false);

        return result.Healthy
            ? TypedResults.Ok(result)
            : TypedResults.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
