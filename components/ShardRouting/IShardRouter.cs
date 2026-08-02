namespace ShardRouting;

/// <summary>Resolves which of the 3 shards a transaction_no routes to (database.md §4).</summary>
public interface IShardRouter
{
    int ResolveShardId(string transactionNo);
}
