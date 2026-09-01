using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Rag.Business.Exceptions;
using Rag.Business.Models;
using Rag.Business.Options;
using Rag.Repository;
using Rag.Repository.Models;

namespace Rag.Business.Services;

public interface IIngestionService
{
    /// <summary>Reads, chunks, embeds, and stores one document from the fixed folder.</summary>
    Task<IngestResult> IngestAsync(string? fileName, string? category, CancellationToken cancellationToken);

    IReadOnlyList<DocumentSummary> ListDocuments();
}

/// <summary>
/// Ingest workflow. Coordinates the file service, chunker, embedder, and repository; the rules
/// each of those enforces stay with them.
/// </summary>
public sealed class IngestionService(
    IDocumentFileService fileService,
    IChunkingService chunkingService,
    IEmbeddingService embeddingService,
    IPlanInfoRepository repository,
    ILogger<IngestionService> logger) : IIngestionService
{
    public async Task<IngestResult> IngestAsync(
        string? fileName,
        string? category,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // 1. Read. The file service validates the name before touching the filesystem.
        var text = await fileService.ReadAsync(fileName, cancellationToken).ConfigureAwait(false);
        var validatedFileName = fileName!;   // ReadAsync threw if it was null or illegal.

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ValidationException(
                "document.empty",
                $"'{validatedFileName}' is empty or contains only whitespace, so there is nothing to ingest.");
        }

        // 2. Category: explicit if supplied, else the file name without its extension.
        var resolvedCategory = string.IsNullOrWhiteSpace(category)
            ? fileService.DeriveCategory(validatedFileName)
            : category.Trim();

        if (resolvedCategory.Length == 0)
        {
            throw new ValidationException("category.missing", "A category could not be determined.");
        }

        // 3. Chunk.
        var chunks = chunkingService.Split(text);
        if (chunks.Count == 0)
        {
            throw new UpstreamException(
                "document.no_chunks_produced",
                $"The splitter produced no chunks for '{validatedFileName}' despite " +
                $"{text.Length} characters of input.");
        }

        // 4. Embed. Order is load-bearing — the vectors are paired to chunks positionally below.
        var embeddings = await embeddingService.GenerateAsync(chunks, cancellationToken).ConfigureAwait(false);

        if (embeddings.Count != chunks.Count)
        {
            // Belt and braces: the embedding service already guarantees this. Pairing a
            // mismatched set positionally would associate text with the wrong vector and produce
            // a working API that returns subtly wrong results — the worst available failure mode.
            throw new UpstreamException(
                "embedding.count_mismatch",
                $"Received {embeddings.Count} embeddings for {chunks.Count} chunks. Refusing to pair them.");
        }

        // 5. Persist in one transaction.
        var rows = new List<PlanInfoChunk>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            rows.Add(new PlanInfoChunk
            {
                Category = resolvedCategory,
                ChunkText = chunks[i],
                Embedding = embeddings[i],
            });
        }

        SaveChunksResult saved;
        try
        {
            saved = await repository.ReplaceAndInsertAsync(resolvedCategory, rows, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RepositoryException ex)
        {
            // The repository has already translated and logged the driver-level failure; this
            // tier only re-labels it so the web tier answers 503 rather than 500.
            throw new UpstreamException(ex.Code, ex.Message, ex);
        }

        stopwatch.Stop();

        logger.LogInformation(
            "Ingested {FileName} into category {Category}: {Inserted} chunks written, {Replaced} replaced, {Elapsed}ms.",
            validatedFileName,
            resolvedCategory,
            saved.Inserted,
            saved.Replaced,
            stopwatch.ElapsedMilliseconds);

        return new IngestResult(
            validatedFileName,
            resolvedCategory,
            text.Length,
            saved.Inserted,
            saved.Replaced,
            stopwatch.ElapsedMilliseconds);
    }

    public IReadOnlyList<DocumentSummary> ListDocuments() => fileService.List();
}
