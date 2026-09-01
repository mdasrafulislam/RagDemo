using System.ComponentModel.DataAnnotations;

namespace Rag.Business.Options;

public sealed class DocumentsOptions
{
    public const string SectionName = "Documents";

    /// <summary>
    /// The fixed folder documents are read from. Relative paths resolve against the
    /// application's content root.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string RootPath { get; init; } = "Documents";

    [Required, MinLength(1)]
    public string[] AllowedExtensions { get; init; } = [".txt", ".md"];

    /// <summary>Cap on how much a single ingest call will read and embed. Default 5 MB.</summary>
    [Range(1, 100L * 1024 * 1024)]
    public long MaxFileSizeBytes { get; init; } = 5L * 1024 * 1024;
}

public sealed class ChunkingOptions
{
    public const string SectionName = "Chunking";

    [Range(1, 8_000)]
    public int MaxChars { get; init; } = 1_000;

    /// <summary>
    /// Characters of overlap between adjacent chunks. Across a 26,000-character document there
    /// are ~25 cut points, and a hard cut can sever the sentence that makes an answer findable.
    /// Set to 0 for strictly non-overlapping chunks.
    /// </summary>
    [Range(0, 4_000)]
    public int OverlapChars { get; init; } = 150;
}

public sealed class SearchOptions
{
    public const string SectionName = "Search";

    [Range(1, 50)]
    public int DefaultTopK { get; init; } = 5;

    [Range(1, 50)]
    public int MaxTopK { get; init; } = 50;

    /// <summary>Maximum length of a search question, in characters.</summary>
    [Range(10, 20_000)]
    public int MaxQueryLength { get; init; } = 2_000;
}
