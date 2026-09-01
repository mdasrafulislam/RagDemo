namespace Rag.Repository.Models;

/// <summary>
/// A row to be written to <c>"RK"."PlanInfo"</c>.
/// </summary>
/// <remarks>
/// <c>recordid</c> and <c>create_at</c> are database-generated and deliberately absent.
/// </remarks>
public sealed class PlanInfoChunk
{
    public required string Category { get; init; }

    public required string ChunkText { get; init; }

    /// <summary>
    /// The embedding. Must contain exactly 1536 values to match the
    /// <c>chunk_embedding VECTOR(1536)</c> column; the business tier validates that before
    /// handing rows over.
    /// </summary>
    public required ReadOnlyMemory<float> Embedding { get; init; }
}

/// <summary>One row returned by a similarity search.</summary>
public sealed class ChunkSearchResult
{
    public required long RecordId { get; init; }

    public required string Category { get; init; }

    public required string ChunkText { get; init; }

    /// <summary>
    /// Cosine similarity on 0–1 where 1 is identical.
    /// </summary>
    /// <remarks>
    /// pgvector's <c>&lt;=&gt;</c> operator returns cosine <em>distance</em> (0 = identical).
    /// The repository inverts it here so that every layer above deals in "higher is better" —
    /// a score that shrinks as relevance rises is a dependable source of caller bugs.
    /// </remarks>
    public required double Similarity { get; init; }
}

/// <summary>Outcome of writing one document's chunks.</summary>
/// <param name="Inserted">Rows written.</param>
/// <param name="Replaced">
/// Rows deleted because they duplicated incoming chunk text. Non-zero means this was a
/// re-ingest rather than a first load.
/// </param>
public sealed record SaveChunksResult(int Inserted, int Replaced);
