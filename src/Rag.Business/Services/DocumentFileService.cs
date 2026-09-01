using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rag.Business.Exceptions;
using Rag.Business.Models;
using Rag.Business.Options;

namespace Rag.Business.Services;

public interface IDocumentFileService
{
    /// <summary>Validates the name, then reads the file. Throws if either step fails.</summary>
    Task<string> ReadAsync(string? fileName, CancellationToken cancellationToken);

    IReadOnlyList<DocumentSummary> List();

    /// <summary>The file name with its extension removed — the default category.</summary>
    string DeriveCategory(string fileName);
}

/// <summary>
/// Reads documents from the fixed folder, and is the single gatekeeper for file names.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Security-critical.</b> <see cref="ReadAsync"/> takes a caller-supplied string and opens
/// a file with it. Unguarded, <c>../../appsettings.json</c> turns the ingest endpoint into an
/// arbitrary-file-read primitive whose contents leak back out through search results.
/// </para>
/// <para>
/// Every path that reaches the filesystem must go through <see cref="ResolveWithinRoot"/>.
/// Nothing in the type system enforces that — it is a convention this class must uphold — so if
/// you add another method here that touches a caller-supplied name, call the guard from it.
/// </para>
/// </remarks>
public sealed class DocumentFileService : IDocumentFileService
{
    private readonly DocumentsOptions _options;
    private readonly ILogger<DocumentFileService> _logger;
    private readonly string _root;
    private readonly HashSet<string> _allowedExtensions;

    public DocumentFileService(
        IOptions<DocumentsOptions> options,
        DocumentRootPath rootPath,
        ILogger<DocumentFileService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rootPath);

        _options = options.Value;
        _logger = logger;

        _allowedExtensions = _options.AllowedExtensions
            .Select(static e => e.StartsWith('.') ? e : '.' + e)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Canonicalise once. The per-request guard compares resolved file paths against this
        // value, and that comparison is only sound if the root is already absolute and
        // normalised.
        var configured = _options.RootPath;
        var combined = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(rootPath.BaseDirectory, configured);

        _root = Path.GetFullPath(combined)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public async Task<string> ReadAsync(string? fileName, CancellationToken cancellationToken)
    {
        var validated = ValidateFileName(fileName);
        var path = ResolveWithinRoot(validated);

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new NotFoundException(
                "document.not_found",
                $"'{validated}' was not found in the documents folder. " +
                "Call GET /api/documents to list the available names.");
        }

        if (info.Length > _options.MaxFileSizeBytes)
        {
            throw new ValidationException(
                "document.too_large",
                $"'{validated}' is {info.Length} bytes, over the {_options.MaxFileSizeBytes}-byte limit.");
        }

        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to read document {FileName}.", validated);
            throw new UpstreamException(
                "document.read_failed",
                $"'{validated}' could not be read: {ex.Message}",
                ex);
        }
    }

    public IReadOnlyList<DocumentSummary> List()
    {
        if (!Directory.Exists(_root))
        {
            throw new UpstreamException(
                "documents.root_missing",
                $"The configured documents folder does not exist: {_root}");
        }

        try
        {
            // Top level only. Nested files could never be ingested anyway — the name guard
            // rejects any name containing a directory separator — so listing them would
            // advertise names that are guaranteed to fail.
            return
            [
                .. new DirectoryInfo(_root)
                    .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                    .Where(f => _allowedExtensions.Contains(f.Extension))
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(f => new DocumentSummary(f.Name, f.Length, f.LastWriteTimeUtc))
            ];
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Failed to list the documents folder {Root}.", _root);
            throw new UpstreamException(
                "documents.list_failed",
                $"The documents folder could not be listed: {ex.Message}",
                ex);
        }
    }

    public string DeriveCategory(string fileName) => Path.GetFileNameWithoutExtension(fileName);

    /// <summary>
    /// First half of the path guard: is this a legal bare file name?
    /// </summary>
    private string ValidateFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ValidationException("file_name.missing", "A file name is required.");
        }

        // Do not trim: a name that needs trimming to become legal is not a name we accept,
        // because the stored identity would then differ from what the caller sent.
        // Path.GetFileName strips everything up to the last separator, so equality here proves
        // the input carried no directory, drive qualifier, or traversal segment at all.
        if (Path.GetFileName(fileName) != fileName)
        {
            throw new ValidationException(
                "file_name.not_a_bare_name",
                "The file name must be a bare name with no directory, drive, or traversal component.");
        }

        if (fileName.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ValidationException(
                "file_name.invalid_characters",
                "The file name contains characters that are not allowed in a file name.");
        }

        if (fileName is "." or "..")
        {
            throw new ValidationException(
                "file_name.not_a_bare_name",
                "The file name must not be a path segment.");
        }

        if (Path.GetFileNameWithoutExtension(fileName).Length == 0)
        {
            throw new ValidationException(
                "file_name.stem_missing",
                "The file name must have a name before its extension.");
        }

        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension) || !_allowedExtensions.Contains(extension))
        {
            throw new ValidationException(
                "file_name.extension_not_allowed",
                $"'{extension}' is not an allowed extension. " +
                $"Allowed: {string.Join(", ", _allowedExtensions)}.");
        }

        return fileName;
    }

    /// <summary>
    /// Second half of the path guard: does the resolved path still sit inside the root?
    /// </summary>
    /// <remarks>
    /// Both halves are required. The name check cannot see symlinks or 8.3 short-name aliases;
    /// canonicalisation alone would accept odd-but-legal names better refused outright.
    /// </remarks>
    private string ResolveWithinRoot(string validatedFileName)
    {
        var candidate = Path.GetFullPath(Path.Combine(_root, validatedFileName));

        // Compare against the root WITH a trailing separator, so a sibling directory whose name
        // merely starts with the root's name (e.g. "docs-evil" vs "docs") cannot pass as being
        // inside it.
        var rootWithSeparator = _root + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            // Reaching here means a name passed validation yet still escaped the root — a link
            // or alias. Log it: this is either a misconfiguration or an attack.
            _logger.LogWarning(
                "Rejected document path escaping the configured root. Name={FileName} Resolved={Resolved} Root={Root}",
                validatedFileName,
                candidate,
                _root);

            throw new ValidationException(
                "document.outside_root",
                $"'{validatedFileName}' resolves outside the documents folder and was rejected.");
        }

        return candidate;
    }
}

/// <summary>
/// The base directory a relative <see cref="DocumentsOptions.RootPath"/> resolves against.
/// </summary>
/// <remarks>
/// Supplied by the web tier at startup (its content root) so the business tier does not need a
/// dependency on ASP.NET hosting types.
/// </remarks>
public sealed class DocumentRootPath(string baseDirectory)
{
    public string BaseDirectory { get; } = baseDirectory;
}
