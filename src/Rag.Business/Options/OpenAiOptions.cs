using System.ComponentModel.DataAnnotations;

namespace Rag.Business.Options;

public sealed class OpenAiOptions
{
    public const string SectionName = "OpenAI";

    /// <summary>Fixed by the <c>chunk_embedding VECTOR(1536)</c> column.</summary>
    public const int RequiredEmbeddingDimensions = 1536;

    [Required(AllowEmptyStrings = false)]
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// The embedding model. Must produce 1536 dimensions:
    /// <list type="bullet">
    ///   <item><c>text-embedding-3-small</c> — 1536 natively. The default, and the cheapest.</item>
    ///   <item><c>text-embedding-ada-002</c> — 1536 natively (legacy).</item>
    ///   <item><c>text-embedding-3-large</c> — 3072 natively; set <see cref="Dimensions"/> to
    ///     1536 to use it.</item>
    /// </list>
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string EmbeddingModel { get; init; } = "text-embedding-3-small";

    /// <summary>
    /// Requested output width, sent as the API's <c>dimensions</c> parameter. Leave at 1536.
    /// </summary>
    [Range(1, 3072)]
    public int Dimensions { get; init; } = RequiredEmbeddingDimensions;

    /// <summary>Inputs per embedding request. A 26,000-character document needs one batch.</summary>
    [Range(1, 2048)]
    public int BatchSize { get; init; } = 64;

    /// <summary>
    /// The chat model that synthesises answers from retrieved chunks.
    /// </summary>
    /// <remarks>
    /// Grounded synthesis over supplied excerpts is a comparatively easy task, so a small cheap
    /// model usually suffices — retrieval quality matters far more here than model size. Set
    /// this to whatever your account has; the default is only a default.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    public string ChatModel { get; init; } = "gpt-4o-mini";

    /// <summary>Output-token ceiling for one answer. Hitting it truncates, which is reported.</summary>
    [Range(64, 32_000)]
    public int MaxOutputTokens { get; init; } = 800;

    /// <summary>
    /// Sampling temperature. 0 suits grounded extraction — the answer should come from the
    /// excerpts, not from creative variation.
    /// </summary>
    /// <remarks>
    /// Nullable on purpose: some newer OpenAI models reject any non-default temperature. Set
    /// this to null in configuration to omit the parameter entirely.
    /// </remarks>
    [Range(0d, 2d)]
    public double? Temperature { get; init; } = 0d;

    /// <summary>Maximum characters of retrieved text sent to the chat model per question.</summary>
    [Range(500, 500_000)]
    public int MaxContextChars { get; init; } = 12_000;

    /// <summary>
    /// Retries for throttling and transient faults. The client pipeline honours the
    /// <c>Retry-After</c> header on 429 responses.
    /// </summary>
    [Range(0, 10)]
    public int MaxRetries { get; init; } = 5;

    [Range(5, 600)]
    public int TimeoutSeconds { get; init; } = 100;

    /// <summary>
    /// Optional. Overrides the API base URL — only for an OpenAI-compatible gateway or proxy.
    /// Empty means api.openai.com.
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>Optional organisation ID, for keys belonging to multiple organisations.</summary>
    public string? Organization { get; init; }
}
