using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TransactionService.Application;

namespace TransactionService.Infrastructure;

/// <summary>
/// Read-only search queries against the 3 shard instances + the reporting
/// instance (TransactionService.md §5) - a separate connection set from
/// TransactionWorker's write-capable NpgsqlDataSources, using a distinct
/// read-only Postgres role (TransactionService.md §2/§6). The 3 shard
/// NpgsqlDataSources are keyed singletons (key = shard_id), same pattern as
/// TransactionWorker.Infrastructure.PostgresTransactionRepository; the
/// reporting NpgsqlDataSource is unkeyed, since there's only one.
/// </summary>
public sealed class PostgresTransactionSearchRepository : ITransactionSearchRepository
{
    private const string SelectColumns = "id, transaction_no, transaction_datetime, amount, type, status, currency, system_datetime";

    private readonly NpgsqlDataSource[] _shards;
    private readonly NpgsqlDataSource _reporting;

    public PostgresTransactionSearchRepository(
        NpgsqlDataSource reporting,
        [FromKeyedServices(0)] NpgsqlDataSource shard0,
        [FromKeyedServices(1)] NpgsqlDataSource shard1,
        [FromKeyedServices(2)] NpgsqlDataSource shard2)
    {
        _reporting = reporting;
        _shards = [shard0, shard1, shard2];
    }

    public async Task<TransactionRecord?> FindByTransactionNoAsync(string transactionNo, int shardId, CancellationToken cancellationToken)
    {
        if (shardId < 0 || shardId >= _shards.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(shardId), shardId, $"No connection configured for shard {shardId}.");
        }

        await using var connection = await _shards[shardId].OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM transactions
            WHERE transaction_no = $1
            """;
        command.Parameters.Add(new NpgsqlParameter { Value = transactionNo });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
    }

    public async Task<TransactionSearchPage> FindByDateRangeAsync(DateTimeOffset from, DateTimeOffset to, int limit, int offset, CancellationToken cancellationToken)
    {
        await using var connection = await _reporting.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Fetches one row beyond `limit` to compute has_more without a
        // separate COUNT(*) query (TransactionService.md §5).
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM transactions_reporting
            WHERE transaction_datetime BETWEEN $1 AND $2
            ORDER BY transaction_datetime
            LIMIT $3 OFFSET $4
            """;
        command.Parameters.Add(new NpgsqlParameter { Value = from.UtcDateTime });
        command.Parameters.Add(new NpgsqlParameter { Value = to.UtcDateTime });
        command.Parameters.Add(new NpgsqlParameter { Value = limit + 1 });
        command.Parameters.Add(new NpgsqlParameter { Value = offset });

        var items = new List<TransactionRecord>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadRecord(reader));
        }

        var hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        return new TransactionSearchPage(items, hasMore);
    }

    private static TransactionRecord ReadRecord(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetDateTime(2).ToString("O", CultureInfo.InvariantCulture),
        reader.GetDecimal(3).ToString(CultureInfo.InvariantCulture),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetDateTime(7).ToString("O", CultureInfo.InvariantCulture));
}
