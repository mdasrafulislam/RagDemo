using Rag.Business.Models;
using Rag.Business.Services;
using Rag.WebApi.Models;

namespace Rag.WebApi.Endpoints;

internal static class SearchEndpoints
{
    internal static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/search", SearchAsync)
            .WithTags("Search")
            .WithName("SearchChunks")
            .WithSummary("Answers a question from the indexed documents.")
            .WithDescription(
                "Embeds the question with the same model used at ingestion time, retrieves the " +
                "nearest chunks, then asks the chat model to answer using only those chunks. The " +
                "model is instructed not to draw on its own knowledge, so when the indexed text " +
                "does not contain the answer the response comes back with `answered: false` and " +
                "an explanation rather than an invented answer. " +
                "**Always branch on `answered` before presenting `answer` as fact.** " +
                "Set `generateAnswer: false` to get the raw chunks without a chat call.")
            .Produces<SearchResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static async Task<IResult> SearchAsync(
        SearchRequest? request,
        ISearchService searchService,
        CancellationToken cancellationToken)
    {
        var result = await searchService.SearchAsync(
                request?.Query,
                request?.TopK,
                request?.Category,
                request?.MinSimilarity,
                generateAnswer: request?.GenerateAnswer ?? true,
                includeSources: request?.IncludeSources ?? true,
                cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(result);
    }
}
