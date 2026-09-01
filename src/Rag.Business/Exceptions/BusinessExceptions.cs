namespace Rag.Business.Exceptions;

/// <summary>
/// Base for failures the business tier reports to the caller. The web tier maps each subtype to
/// a status code in one place, so no endpoint needs a try/catch.
/// </summary>
/// <param name="Code">
/// A stable machine-readable code, e.g. <c>document.not_found</c>. Clients should branch on this
/// rather than on message text, which is free to change.
/// </param>
public abstract class BusinessException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>The caller supplied something unusable. Maps to 400.</summary>
public sealed class ValidationException(string code, string message)
    : BusinessException(code, message);

/// <summary>The requested thing does not exist. Maps to 404.</summary>
public sealed class NotFoundException(string code, string message)
    : BusinessException(code, message);

/// <summary>
/// A dependency we do not control failed or throttled us — OpenAI, PostgreSQL. Maps to 503
/// rather than 500: the fault is not in this service, and the caller should retry.
/// </summary>
public sealed class UpstreamException(string code, string message, Exception? inner = null)
    : BusinessException(code, message, inner);
