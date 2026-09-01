namespace Rag.Business.Models;

/// <summary>What is known about an available document without reading it.</summary>
public sealed record DocumentSummary(string FileName, long SizeBytes, DateTimeOffset LastModifiedUtc);

public sealed record DocumentListResult(int Count, IReadOnlyList<DocumentSummary> Documents);

/// <param name="ChunksReplaced">
/// Rows removed because they duplicated incoming chunk text. Non-zero means this was a
/// re-ingest, not a first load.
/// </param>
public sealed record IngestResult(
    string FileName,
    string Category,
    int CharacterCount,
    int ChunksInserted,
    int ChunksReplaced,
    long ElapsedMs);

/// <param name="Similarity">0–1, where 1 is identical. Higher is better.</param>
public sealed record SearchHit(long RecordId, string Category, string ChunkText, double Similarity);

/// <param name="Answer">
/// The synthesised answer, or an explanation of why the indexed documents cannot answer the
/// question. Null when the request asked to skip generation.
/// </param>
/// <param name="Answered">
/// True only when the model produced an answer grounded in the retrieved text. False when
/// retrieval found nothing, when the model reported the text does not contain the answer, or
/// when generation was skipped. **Branch on this before presenting the answer as fact.**
/// </param>
/// <param name="CitedRecordIds">
/// The <c>recordid</c> values the model cited, in first-mention order. An answer with
/// <c>Answered = true</c> and no citations is worth treating with suspicion.
/// </param>
public sealed record SearchResult(
    string Query,
    string? Answer,
    bool Answered,
    IReadOnlyList<long> CitedRecordIds,
    IReadOnlyList<SearchHit> Sources,
    SearchUsage Usage);

/// <param name="ChunksUsedAsContext">
/// How many retrieved chunks were actually sent to the model. Lower than
/// <paramref name="RetrievedChunks"/> when the context character budget dropped the weakest.
/// </param>
/// <param name="TopSimilarity">
/// The best retrieval score, or null when nothing was retrieved. A top score far below your
/// corpus norm usually means the answer is not indexed at all.
/// </param>
public sealed record SearchUsage(
    int RetrievedChunks,
    int ChunksUsedAsContext,
    double? TopSimilarity,
    int? InputTokens,
    int? OutputTokens,
    long ElapsedMs,
    string? FinishReason);

/// <summary>One retrieved chunk offered to the chat model as grounding.</summary>
public sealed record AnswerContextChunk(long RecordId, string Text);

/// <param name="AnsweredFromContext">
/// False when the supplied excerpts did not contain the answer.
/// </param>
public sealed record GeneratedAnswer(
    string Text,
    bool AnsweredFromContext,
    IReadOnlyList<long> CitedRecordIds,
    int? InputTokens,
    int? OutputTokens,
    string? FinishReason);

public sealed record HealthResult(bool Healthy, string Database, string Embeddings);
