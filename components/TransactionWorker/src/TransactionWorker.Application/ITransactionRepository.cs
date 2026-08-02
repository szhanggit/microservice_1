namespace TransactionWorker.Application;

/// <summary>Single-shard insert (KEDA.md §4) - the caller has already resolved shard_id.</summary>
public interface ITransactionRepository
{
    Task<InsertOutcome> InsertAsync(TransactionRecord record, int shardId, CancellationToken cancellationToken);
}
