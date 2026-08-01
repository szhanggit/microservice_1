using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TransactionWorker.Application;

namespace TransactionWorker.Infrastructure;

/// <summary>
/// Single-shard insert against one of the 3 RDS PostgreSQL instances
/// (database.md §6) - the 3 <see cref="NpgsqlDataSource"/> connection
/// factories are resolved as keyed singletons (key = shard_id), wired up in
/// <see cref="ServiceCollectionExtensions"/>.
/// </summary>
public sealed class PostgresTransactionRepository : ITransactionRepository
{
    private readonly NpgsqlDataSource[] _shards;

    public PostgresTransactionRepository(
        [FromKeyedServices(0)] NpgsqlDataSource shard0,
        [FromKeyedServices(1)] NpgsqlDataSource shard1,
        [FromKeyedServices(2)] NpgsqlDataSource shard2)
    {
        _shards = [shard0, shard1, shard2];
    }

    public async Task<InsertOutcome> InsertAsync(TransactionRecord record, int shardId, CancellationToken cancellationToken)
    {
        if (shardId < 0 || shardId >= _shards.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(shardId), shardId, $"No connection configured for shard {shardId}.");
        }

        try
        {
            await ResiliencePipelines.Postgres.ExecuteAsync(async ct =>
            {
                await using var connection = await _shards[shardId].OpenConnectionAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO transactions (id, transaction_no, transaction_datetime, amount, type, status, currency)
                    VALUES ($1, $2, $3, $4, $5, $6, $7)
                    """;
                command.Parameters.Add(new NpgsqlParameter { Value = record.Id });
                command.Parameters.Add(new NpgsqlParameter { Value = record.TransactionNo });
                command.Parameters.Add(new NpgsqlParameter
                {
                    Value = DateTime.Parse(record.TransactionDatetime, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                });
                command.Parameters.Add(new NpgsqlParameter { Value = decimal.Parse(record.Amount, CultureInfo.InvariantCulture) });
                command.Parameters.Add(new NpgsqlParameter { Value = record.Type });
                command.Parameters.Add(new NpgsqlParameter { Value = record.Status });
                command.Parameters.Add(new NpgsqlParameter { Value = record.Currency });

                await command.ExecuteNonQueryAsync(ct);
            }, cancellationToken);

            return InsertOutcome.Inserted;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // database.md §6's UNIQUE(transaction_no) constraint - treated as
            // "already processed", not an error (KEDA.md §5's defense-in-depth).
            return InsertOutcome.AlreadyExists;
        }
    }
}
