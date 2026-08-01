namespace ShardRouting;

/// <summary>
/// shard_id = murmur3_32(transaction_no) % 3 (database.md §4). Deterministic -
/// the same transaction_no always maps to the same shard, so inserts, updates,
/// and point lookups by transaction_no are single-shard operations.
///
/// This is a single shared library (database.md §10) rather than being owned
/// by any one service - TransactionWorker uses it to route inserts,
/// TransactionService uses it to route transaction_no lookups. A mismatch
/// between two copies of this logic would send writes and reads for the same
/// transaction_no to different shards.
/// </summary>
public sealed class Murmur3ShardRouter : IShardRouter
{
    public const int ShardCount = 3;

    public int ResolveShardId(string transactionNo)
    {
        if (string.IsNullOrEmpty(transactionNo))
        {
            throw new ArgumentException("transaction_no must not be empty.", nameof(transactionNo));
        }

        return (int)(MurmurHash3.Hash32(transactionNo) % ShardCount);
    }
}
