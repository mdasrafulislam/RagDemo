using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rag.Business.Exceptions;
using Rag.Business.Options;

namespace Rag.Business.Services;

public interface IEmbeddingService
{
    /// <summary>
    /// Embeds every input, preserving order. Order is load-bearing — callers pair the results to
    /// chunks positionally.
    /// </summary>
    Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken);
}

/// <summary>
/// Produces embeddings via the OpenAI API.
/// </summary>
/// <remarks>
/// Throttling is expected, not exceptional: OpenAI enforces per-project request and token rate
/// limits. The client pipeline honours <c>Retry-After</c> on 429, so retry is configured on the
/// client rather than layered on with a second policy.
/// </remarks>
public sealed class EmbeddingService(
    IEmbeddingGenerator<string, Embedding<float>> generator,
    IOptions<OpenAiOptions> options,
    ILogger<EmbeddingService> logger) : IEmbeddingService
{
    private readonly OpenAiOptions _options = options.Value;

    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        if (inputs.Count == 0)
        {
            return [];
        }

        var results = new List<ReadOnlyMemory<float>>(inputs.Count);

        foreach (var batch in Batch(inputs, _options.BatchSize))
        {
            results.AddRange(await GenerateBatchAsync(batch, cancellationToken).ConfigureAwait(false));
        }

        if (results.Count != inputs.Count)
        {
            throw new UpstreamException(
                "embedding.count_mismatch",
                $"Requested {inputs.Count} embeddings but assembled {results.Count}.");
        }

        return results;
    }

    private async Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateBatchAsync(
        IReadOnlyList<string> batch,
        CancellationToken cancellationToken)
    {
        // Sending `dimensions` explicitly is what allows text-embedding-3-large against a
        // VECTOR(1536) column; for the 1536-native models it is a no-op.
        var generationOptions = new EmbeddingGenerationOptions { Dimensions = _options.Dimensions };

        GeneratedEmbeddings<Embedding<float>> generated;
        try
        {
            generated = await generator
                .GenerateAsync(batch, generationOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ClientResultException ex) when (ex.Status == 429)
        {
            logger.LogWarning(ex, "OpenAI throttled the embedding request for {Count} inputs.", batch.Count);
            throw new UpstreamException(
                "embedding.throttled",
                "The OpenAI embedding endpoint is rate limited. Retry shortly.",
                ex);
        }
        catch (ClientResultException ex) when (ex.Status is 401 or 403)
        {
            logger.LogError(ex, "OpenAI rejected the API key ({Status}).", ex.Status);
            throw new UpstreamException(
                "embedding.unauthorized",
                "OpenAI rejected the API key. Check OpenAI:ApiKey and that it has access to " +
                $"'{_options.EmbeddingModel}'.",
                ex);
        }
        catch (ClientResultException ex) when (ex.Status == 404)
        {
            throw new UpstreamException(
                "embedding.model_not_found",
                $"OpenAI does not recognise the model '{_options.EmbeddingModel}'.",
                ex);
        }
        catch (ClientResultException ex)
        {
            logger.LogError(ex, "OpenAI returned {Status} for an embedding request.", ex.Status);
            throw new UpstreamException(
                "embedding.request_failed",
                $"The OpenAI embedding endpoint returned HTTP {ex.Status}.",
                ex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected failure calling the OpenAI embedding endpoint.");
            throw new UpstreamException("embedding.unavailable", $"OpenAI is unavailable: {ex.Message}", ex);
        }

        if (generated.Count != batch.Count)
        {
            throw new UpstreamException(
                "embedding.batch_count_mismatch",
                $"Sent {batch.Count} inputs but received {generated.Count} embeddings. Refusing to " +
                "continue, because pairing them positionally would associate text with the wrong vector.");
        }

        var vectors = new List<ReadOnlyMemory<float>>(generated.Count);
        for (var i = 0; i < generated.Count; i++)
        {
            var vector = generated[i].Vector;

            // Validated here, at the boundary. Without this check a misconfigured model surfaces
            // as an opaque Postgres type error during INSERT, after the work is already done.
            if (vector.Length != OpenAiOptions.RequiredEmbeddingDimensions)
            {
                throw new UpstreamException(
                    "embedding.wrong_dimensions",
                    $"Model '{_options.EmbeddingModel}' returned {vector.Length} dimensions but the " +
                    $"chunk_embedding column requires {OpenAiOptions.RequiredEmbeddingDimensions}. " +
                    "Use text-embedding-3-small, or text-embedding-3-large with Dimensions=1536.");
            }

            vectors.Add(vector);
        }

        return vectors;
    }

    private static IEnumerable<IReadOnlyList<string>> Batch(IReadOnlyList<string> source, int size)
    {
        for (var offset = 0; offset < source.Count; offset += size)
        {
            var length = Math.Min(size, source.Count - offset);
            var slice = new string[length];
            for (var i = 0; i < length; i++)
            {
                slice[i] = source[offset + i];
            }

            yield return slice;
        }
    }
}
