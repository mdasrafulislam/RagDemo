using Rag.Repository.Models;

namespace Rag.Repository;

/// <summary>
/// Data access for <c>"RK"."PlanInfo"</c>. The only type in the solution that knows SQL.
/// </summary>
public interface IPlanInfoRepository
{
    /// <summary>
    /// Writes a document's chunks in one transaction, first deleting any existing rows in the
    /// same category whose text is byte-identical to an incoming chunk.
    /// </summary>
    /// <remarks>
    /// The delete is how re-ingesting an unchanged document avoids duplicating it. The table
    /// has no column identifying the source document, so text equality is the only handle
    /// available — see the README for what that cannot cover.
    /// </remarks>
    Task<SaveChunksResult> ReplaceAndInsertAsync(
        string category,
        IReadOnlyList<PlanInfoChunk> chunks,
        CancellationToken cancellationToken);

    /// <summary>Returns the chunks nearest <paramref name="queryEmbedding"/>, most similar first.</summary>
    /// <param name="category">Optional exact-match filter. Null means all categories.</param>
    Task<IReadOnlyList<ChunkSearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        int topK,
        string? category,
        CancellationToken cancellationToken);

    /// <summary>Cheap round trip, for the health endpoint.</summary>
    Task<bool> CanConnectAsync(CancellationToken cancellationToken);
}
