using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.Text;
using Rag.Business.Exceptions;
using Rag.Business.Options;

namespace Rag.Business.Services;

public interface IChunkingService
{
    /// <summary>Splits text into chunks of at most the configured character length.</summary>
    IReadOnlyList<string> Split(string text);
}

/// <summary>
/// Splits text with Semantic Kernel's <see cref="TextChunker"/>.
/// </summary>
/// <remarks>
/// <para>
/// The requirement is 1,000-<em>character</em> chunks, but <see cref="TextChunker"/> counts
/// <em>tokens</em>. The two reconcile through its optional token-counter delegate: supplying one
/// that returns the string's length turns the token budget into a character budget. No custom
/// splitter is needed, and the "chunk with Semantic Kernel" requirement is met as stated.
/// </para>
/// <para>
/// <see cref="TextChunker"/> respects paragraph and sentence boundaries, so real chunks land at
/// or below the limit rather than exactly on it. The result is re-checked here, so a future
/// Semantic Kernel release that changes this behaviour fails loudly instead of quietly sending
/// over-long text to the database.
/// </para>
/// </remarks>
public sealed class ChunkingService(IOptions<ChunkingOptions> options) : IChunkingService
{
    private readonly ChunkingOptions _options = options.Value;

    private static int CountCharacters(string text) => text.Length;

    public IReadOnlyList<string> Split(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var lines = TextChunker.SplitPlainTextLines(text, _options.MaxChars, CountCharacters);

        var chunks = TextChunker.SplitPlainTextParagraphs(
            lines,
            _options.MaxChars,
            overlapTokens: _options.OverlapChars,
            chunkHeader: null,
            tokenCounter: CountCharacters);

        // Verify the splitter honoured the budget rather than trusting it. A chunk longer than
        // the limit is a defect worth failing on: it would be stored and later retrieved, so a
        // silent pass here becomes bad data that is expensive to find.
        for (var i = 0; i < chunks.Count; i++)
        {
            if (chunks[i].Length > _options.MaxChars)
            {
                throw new UpstreamException(
                    "chunking.limit_exceeded",
                    $"Chunk {i} is {chunks[i].Length} characters, exceeding the configured " +
                    $"limit of {_options.MaxChars}. The text splitter did not honour its budget.");
            }
        }

        return chunks;
    }
}
