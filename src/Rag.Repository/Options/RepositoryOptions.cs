using System.ComponentModel.DataAnnotations;

namespace Rag.Repository.Options;

public sealed class RepositoryOptions
{
    public const string SectionName = "Repository";

    /// <summary>
    /// pgvector's HNSW search breadth. Higher means better recall at higher latency.
    /// </summary>
    /// <remarks>
    /// This is a per-session GUC, not a property of the index, so it is applied on each search
    /// transaction with <c>SET LOCAL</c>. 40 is pgvector's default; the repository raises the
    /// effective value to at least <c>topK</c>, because an <c>ef_search</c> below the LIMIT
    /// silently returns fewer rows than asked for.
    /// </remarks>
    [Range(1, 1_000)]
    public int HnswEfSearch { get; init; } = 40;
}
