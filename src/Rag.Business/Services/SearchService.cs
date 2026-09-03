using System.Diagnostics;
using Microsoft.Extensions.Options;
using Rag.Business.Exceptions;
using Rag.Business.Models;
using Rag.Business.Options;
using Rag.Repository;

namespace Rag.Business.Services;

public interface ISearchService
{
    /// <summary>
    /// Embeds the question, retrieves the nearest chunks, and answers from them.
    /// </summary>
    /// <param name="generateAnswer">
    /// When false, retrieval runs and the chunks come back but no chat call is made.
    /// </param>
    Task<SearchResult> SearchAsync(
        string? query,
        int? topK,
        string? category,
        double? minSimilarity,
        CancellationToken cancellationToken);
}

/// <summary>
/// The full retrieval-augmented-generation workflow behind <c>POST /api/search</c>.
/// </summary>
public sealed class SearchService(
    IEmbeddingService embeddingService,
    IAnswerService answerService,
    IPlanInfoRepository repository,
    IOptions<SearchOptions> searchOptions,
    IOptions<OpenAiOptions> openAiOptions) : ISearchService
{
    private readonly SearchOptions _searchOptions = searchOptions.Value;
    private readonly OpenAiOptions _openAiOptions = openAiOptions.Value;

    public async Task<SearchResult> SearchAsync(
        string? query,
        int? topK,
        string? category,
        double? minSimilarity,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        var trimmedQuery = ValidateQuery(query);
        var effectiveTopK = ValidateTopK(topK);
        var floor = ValidateMinSimilarity(minSimilarity);
        var effectiveCategory = string.IsNullOrWhiteSpace(category) ? null : category.Trim();

        // 1. Embed the question with the SAME model used at ingestion time. If those ever
        //    diverge, retrieval quality collapses silently — there is no error to point at.
        var embeddings = await embeddingService
            .GenerateAsync([trimmedQuery], cancellationToken)
            .ConfigureAwait(false);

        if (embeddings.Count != 1)
        {
            throw new UpstreamException(
                "search.embedding_count_unexpected",
                $"Expected exactly one query embedding but received {embeddings.Count}.");
        }

        // 2. Retrieve.
        IReadOnlyList<SearchHit> hits;
        try
        {
            var rows = await repository
                .SearchAsync(embeddings[0], effectiveTopK, effectiveCategory, cancellationToken)
                .ConfigureAwait(false);

            // The floor is applied here rather than in SQL: a WHERE on the computed distance
            // would stop the HNSW index from serving the ORDER BY ... LIMIT, turning every
            // search into a sequential scan. Note it filters and does not backfill — topK 5 with
            // a floor can return fewer than 5.
            hits =
            [
                .. rows
                    .Where(r => r.Similarity >= floor)
                    .Select(r => new SearchHit(r.RecordId, r.Category, r.ChunkText, r.Similarity))
            ];
        }
        catch (RepositoryException ex)
        {
            // The repository has already translated and logged the driver-level failure; this
            // tier only re-labels it so the web tier answers 503 rather than 500.
            throw new UpstreamException(ex.Code, ex.Message, ex);
        }

        double? topSimilarity = hits.Count > 0 ? hits[0].Similarity : null;

        // 3. Nothing retrieved: do NOT call the chat model. With no context it has nothing to be
        //    grounded in, so it would either refuse — a wasted call — or answer from its own
        //    training data, which is worse: an ungrounded answer the caller cannot distinguish
        //    from a real one. This branch is the cheapest and most reliable hallucination guard
        //    in the pipeline, because it removes the opportunity entirely.
        if (hits.Count == 0)
        {
            stopwatch.Stop();
            return new SearchResult(
                trimmedQuery,
                "The indexed documents do not contain anything relevant to that question.",
                Answered: false
                );
        }


        // 5. Fit the context to its character budget by dropping whole low-ranked chunks. Never
        //    truncate mid-chunk: cutting a chunk in half can sever the sentence holding the
        //    answer, converting a retrievable answer into a confident "not found".
        var (context, used) = SelectWithinBudget(hits);

        // 6. Generate.
        var answer = await answerService
            .GenerateAsync(trimmedQuery, context, cancellationToken)
            .ConfigureAwait(false);

        stopwatch.Stop();

        return new SearchResult(
            trimmedQuery,
            answer.Text,
            answer.AnsweredFromContext
            );
    }

    private string ValidateQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ValidationException("query.missing", "A search query is required.");
        }

        var trimmed = query.Trim();
        if (trimmed.Length > _searchOptions.MaxQueryLength)
        {
            throw new ValidationException(
                "query.too_long",
                $"A search query must be at most {_searchOptions.MaxQueryLength} characters.");
        }

        return trimmed;
    }

    private int ValidateTopK(int? topK)
    {
        if (!topK.HasValue)
        {
            return _searchOptions.DefaultTopK;
        }

        if (topK.Value < 1 || topK.Value > _searchOptions.MaxTopK)
        {
            throw new ValidationException(
                "top_k.out_of_range",
                $"topK must be between 1 and {_searchOptions.MaxTopK}; got {topK.Value}.");
        }

        return topK.Value;
    }

    private static double ValidateMinSimilarity(double? minSimilarity)
    {
        if (!minSimilarity.HasValue)
        {
            return 0d;
        }

        if (double.IsNaN(minSimilarity.Value) || minSimilarity.Value < 0d || minSimilarity.Value > 1d)
        {
            throw new ValidationException(
                "min_similarity.out_of_range",
                $"minSimilarity must be within 0..1; got {minSimilarity.Value}.");
        }

        return minSimilarity.Value;
    }

    private (IReadOnlyList<AnswerContextChunk> Context, IReadOnlyList<SearchHit> Used) SelectWithinBudget(
        IReadOnlyList<SearchHit> hits)
    {
        var context = new List<AnswerContextChunk>(hits.Count);
        var used = new List<SearchHit>(hits.Count);
        var budget = _openAiOptions.MaxContextChars;
        var consumed = 0;

        // hits arrive most-similar-first, so taking greedily keeps the best matches.
        foreach (var hit in hits)
        {
            var cost = hit.ChunkText.Length;

            // Always include the top match even if it alone exceeds the budget: sending no
            // context would be a worse outcome than one oversized chunk.
            if (context.Count > 0 && consumed + cost > budget)
            {
                continue;
            }

            context.Add(new AnswerContextChunk(hit.RecordId, hit.ChunkText));
            used.Add(hit);
            consumed += cost;
        }

        return (context, used);
    }
}
