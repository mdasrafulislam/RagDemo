using Rag.Business.Models;
using Rag.Business.Services;
using Rag.WebApi.Models;

namespace Rag.WebApi.Endpoints;

/// <summary>
/// Document listing and ingestion. These map HTTP to and from business-tier calls and do nothing
/// else — no validation, no orchestration, no data access.
/// </summary>
internal static class DocumentEndpoints
{
    internal static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Documents");

        group.MapGet("/documents", ListDocuments)
            .WithName("ListDocuments")
            .WithSummary("Lists the documents available to ingest.")
            .WithDescription(
                "Returns the file names in the fixed documents folder. Use this to discover the " +
                "exact name to pass to POST /api/ingest rather than guessing it.")
            .Produces<DocumentListResult>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/ingest", IngestAsync)
            .WithName("IngestDocument")
            .WithSummary("Chunks, embeds, and stores one document by file name.")
            .WithDescription(
                "Reads the named file from the fixed documents folder, splits it into " +
                "1,000-character chunks, embeds each chunk, and writes them to RK.PlanInfo. " +
                "Re-posting the same file is idempotent: chunks whose text is unchanged are " +
                "replaced rather than duplicated, and chunksReplaced reports how many were. " +
                "Editing a document is NOT fully supported — chunks whose text changed cannot be " +
                "located for removal and will remain in the table.")
            .Produces<IngestResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static IResult ListDocuments(IIngestionService ingestionService) =>
        TypedResults.Ok(BuildList(ingestionService.ListDocuments()));

    private static async Task<IResult> IngestAsync(
        IngestRequest? request,
        IIngestionService ingestionService,
        CancellationToken cancellationToken)
    {
        // No validation here on purpose. Every rule lives in the business tier, so there is one
        // definition of a legal file name — a second check at the transport boundary could drift
        // from it, which is worse than having none.
        var result = await ingestionService
            .IngestAsync(request?.FileName, request?.Category, cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(result);
    }

    private static DocumentListResult BuildList(IReadOnlyList<DocumentSummary> documents) =>
        new(documents.Count, documents);
}
