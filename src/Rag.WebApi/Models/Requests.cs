namespace Rag.WebApi.Models;

/// <summary>Request body for <c>POST /api/ingest</c>.</summary>
/// <param name="FileName">
/// A bare file name inside the fixed documents folder — no path, no traversal. Call
/// <c>GET /api/documents</c> to list the valid names.
/// </param>
/// <param name="Category">
/// Optional. Defaults to the file name with its extension removed. This value also scopes the
/// idempotency check, so ingesting the same file under two categories stores it twice.
/// </param>
public sealed record IngestRequest(string? FileName, string? Category);

/// <summary>Request body for <c>POST /api/search</c>.</summary>
/// <param name="Query">The natural-language question. Required, at most 2,000 characters.</param>
/// <param name="TopK">How many chunks to retrieve as grounding, 1–50. Defaults to 5.</param>
/// <param name="Category">Optional exact-match category filter.</param>
/// <param name="MinSimilarity">
/// Optional floor on similarity, 0–1. Applied after ranking, so it never prevents the vector
/// index from serving the query. It filters and does not backfill — topK 5 with a floor can
/// return fewer than 5 results.
/// </param>
/// <param name="GenerateAnswer">
/// When false, retrieval runs but no chat call is made — the chunks come back with a null
/// answer. Useful for tuning retrieval without paying for generation. Defaults to true.
/// </param>
/// <param name="IncludeSources">
/// When true the chunks used as grounding are returned alongside the answer. Defaults to true.
/// </param>
public sealed record SearchRequest(
    string? Query,
    int? TopK,
    string? Category,
    double? MinSimilarity,
    bool? GenerateAnswer,
    bool? IncludeSources);
