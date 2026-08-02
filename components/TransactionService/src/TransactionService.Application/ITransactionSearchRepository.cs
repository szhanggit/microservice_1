namespace TransactionService.Application;

/// <summary>
/// Read-only DB access for search (TransactionService.md §5) - a second,
/// independent path from the SQS-forwarding write flow. Uses a distinct
/// read-only Postgres role from TransactionWorker's write-capable one
/// (TransactionService.md §2/§6).
/// </summary>
public interface ITransactionSearchRepository
{
    /// <summary>Single-shard point lookup - the caller has already resolved shard_id via components/ShardRouting.</summary>
    Task<TransactionRecord?> FindByTransactionNoAsync(string transactionNo, int shardId, CancellationToken cancellationToken);

    /// <summary>Queries the reporting instance, not the OLTP shards (database.md §8).</summary>
    Task<TransactionSearchPage> FindByDateRangeAsync(DateTimeOffset from, DateTimeOffset to, int limit, int offset, CancellationToken cancellationToken);
}
