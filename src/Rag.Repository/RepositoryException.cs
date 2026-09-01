namespace Rag.Repository;

/// <summary>
/// A database failure, expressed without exposing the driver.
/// </summary>
/// <remarks>
/// The repository translates <c>NpgsqlException</c> and <c>PostgresException</c> into this so the
/// business tier never has to reference Npgsql types to handle a failure. Without it, "swap the
/// database" would mean editing every service that catches a database error.
/// </remarks>
public sealed class RepositoryException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>Stable machine-readable code, e.g. <c>persist.rejected</c>.</summary>
    public string Code { get; } = code;
}
