using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Pgvector;
using Rag.Repository.Models;
using Rag.Repository.Options;

namespace Rag.Repository;

/// <summary>
/// Npgsql + pgvector data access for <c>"RK"."PlanInfo"</c>.
/// </summary>
/// <remarks>
/// Both identifiers are quoted everywhere because the schema and table were created quoted.
/// Column names were created lowercase and need no quoting.
/// <para>
/// Every command is enlisted in the connection's active transaction. Npgsql requires this —
/// a command built from the connection alone while a transaction is open throws at execution.
/// </para>
/// </remarks>
public sealed class PlanInfoRepository(
    NpgsqlDataSource dataSource,
    IOptions<RepositoryOptions> options,
    ILogger<PlanInfoRepository> logger) : IPlanInfoRepository
{
    private const string TableName = @"""RK"".""PlanInfo""";

    private readonly RepositoryOptions _options = options.Value;

    public async Task<SaveChunksResult> ReplaceAndInsertAsync(
        string category,
        IReadOnlyList<PlanInfoChunk> chunks,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentNullException.ThrowIfNull(chunks);

        if (chunks.Count == 0)
        {
            return new SaveChunksResult(0, 0);
        }

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            var replaced = await DeleteMatchingAsync(connection, transaction, category, chunks, cancellationToken)
                .ConfigureAwait(false);
            var inserted = await InsertAsync(connection, transaction, category, chunks, cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            if (replaced > 0)
            {
                logger.LogInformation(
                    "Re-ingest of category {Category}: replaced {Replaced} existing chunks with {Inserted} new ones.",
                    category,
                    replaced,
                    inserted);
            }

            return new SaveChunksResult(inserted, replaced);
        }
        catch (PostgresException ex)
        {
            logger.LogError(ex, "Postgres rejected the write for category {Category}. SqlState={SqlState}",
                category, ex.SqlState);
            throw new RepositoryException(
                "persist.rejected",
                $"The database rejected the write: {ex.MessageText}",
                ex);
        }
        catch (NpgsqlException ex)
        {
            logger.LogError(ex, "Database unavailable while writing category {Category}.", category);
            throw new RepositoryException(
                "persist.unavailable",
                $"The database is unavailable: {ex.Message}",
                ex);
        }
    }

    public async Task<IReadOnlyList<ChunkSearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        int topK,
        string? category,
        CancellationToken cancellationToken)
    {
        // The `<=>` operator is cosine distance and must match the HNSW index's operator class
        // (vector_cosine_ops). If the index is ever rebuilt with vector_l2_ops or
        // vector_ip_ops, this operator AND the similarity conversion below must change with it
        // — otherwise the planner silently ignores the index and every search degrades to a
        // sequential scan. Confirm with EXPLAIN ANALYZE that this does an Index Scan.
        const string Sql = $"""
            SELECT recordid, category, chunk_text, (chunk_embedding <=> $1) AS distance
            FROM   {TableName}
            WHERE  ($2::varchar IS NULL OR category = $2)
            ORDER  BY chunk_embedding <=> $1
            LIMIT  $3;
            """;

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            // hnsw.ef_search is a per-session GUC. SET LOCAL scopes it to this transaction so a
            // pooled connection is not left mutated for whoever borrows it next.
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            await ApplyEfSearchAsync(connection, transaction, topK, cancellationToken).ConfigureAwait(false);

            await using var command = new NpgsqlCommand(Sql, connection, transaction);
            command.Parameters.Add(new NpgsqlParameter { Value = new Vector(queryEmbedding) });
            command.Parameters.Add(new NpgsqlParameter
            {
                Value = string.IsNullOrWhiteSpace(category) ? DBNull.Value : category,
                NpgsqlDbType = NpgsqlDbType.Varchar,
            });
            command.Parameters.Add(new NpgsqlParameter { Value = topK });

            var results = new List<ChunkSearchResult>(topK);

            await using (var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var distance = reader.GetDouble(3);

                    results.Add(new ChunkSearchResult
                    {
                        RecordId = reader.GetInt32(0),
                        Category = reader.IsDBNull(1)
                            ? string.Empty
                            : reader.GetString(1),

                        ChunkText = reader.IsDBNull(2)
                            ? string.Empty
                            : reader.GetString(2),

                        Similarity = Math.Clamp(1d - distance, 0d, 1d),
                    });
                }
            } 

            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);

            return results;
        }
        catch (PostgresException ex)
        {
            logger.LogError(ex, "Postgres rejected the search query. SqlState={SqlState}", ex.SqlState);
            throw new RepositoryException(
                "search.rejected",
                $"The database rejected the search: {ex.MessageText}",
                ex);
        }
        catch (NpgsqlException ex)
        {
            logger.LogError(ex, "Database unavailable during search.");
            throw new RepositoryException(
                "search.unavailable",
                $"The database is unavailable: {ex.Message}",
                ex);
        }
    }

    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1;");
            _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (NpgsqlException ex)
        {
            logger.LogError(ex, "Database health check failed.");
            return false;
        }
    }

    /// <remarks>
    /// The effective value is floored at <paramref name="topK"/>: pgvector's HNSW scan visits at
    /// most <c>ef_search</c> candidates, so a value below the requested LIMIT silently returns
    /// fewer rows than asked for. With the default of 40, a caller requesting topK=50 would get
    /// at most 40 results and no indication why.
    /// </remarks>
    private async Task ApplyEfSearchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int topK,
        CancellationToken cancellationToken)
    {
        var efSearch = Math.Max(_options.HnswEfSearch, topK);

        // SET does not accept parameters, so the value is interpolated. Both inputs are ints —
        // one range-validated by RepositoryOptions, one by the business tier — so there is no
        // injection surface here.
        var sql = string.Format(
            CultureInfo.InvariantCulture,
            "SET LOCAL hnsw.ef_search = {0};",
            efSearch);

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> DeleteMatchingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string category,
        IReadOnlyList<PlanInfoChunk> chunks,
        CancellationToken cancellationToken)
    {
        const string Sql = $"""
            DELETE FROM {TableName}
            WHERE category = $1 AND chunk_text = ANY($2);
            """;

        var texts = new string[chunks.Count];
        for (var i = 0; i < chunks.Count; i++)
        {
            texts[i] = chunks[i].ChunkText;
        }

        await using var command = new NpgsqlCommand(Sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = category,
            NpgsqlDbType = NpgsqlDbType.Varchar,
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = texts,
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
        });

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string category,
        IReadOnlyList<PlanInfoChunk> chunks,
        CancellationToken cancellationToken)
    {
        // A multi-row VALUES statement rather than binary COPY: a few dozen rows per document
        // does not justify COPY's extra machinery, and this keeps the write in the same
        // transaction as the delete with no special-casing.
        // recordid and create_at are database-generated and deliberately never written.
        var sql = new StringBuilder()
            .Append("INSERT INTO ").Append(TableName)
            .Append(" (category, chunk_text, chunk_embedding) VALUES ");

        await using var command = new NpgsqlCommand { Connection = connection, Transaction = transaction };

        // $1 is the category, reused by every row.
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = category,
            NpgsqlDbType = NpgsqlDbType.Varchar,
        });

        var parameterNumber = 1;
        for (var i = 0; i < chunks.Count; i++)
        {
            if (i > 0)
            {
                sql.Append(", ");
            }

            var textParameter = ++parameterNumber;
            var vectorParameter = ++parameterNumber;
            sql.Append(CultureInfo.InvariantCulture, $"($1, ${textParameter}, ${vectorParameter})");

            command.Parameters.Add(new NpgsqlParameter
            {
                Value = chunks[i].ChunkText,
                NpgsqlDbType = NpgsqlDbType.Varchar,
            });
            command.Parameters.Add(new NpgsqlParameter { Value = new Vector(chunks[i].Embedding) });
        }

        sql.Append(';');
        command.CommandText = sql.ToString();

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
