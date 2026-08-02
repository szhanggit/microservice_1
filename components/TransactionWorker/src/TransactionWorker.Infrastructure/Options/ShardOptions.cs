namespace TransactionWorker.Infrastructure.Options;

/// <summary>
/// Connection string per shard, ordinal-indexed (index 0 = shard 0, etc.) -
/// matches database.md §4's fixed 3-shard `mod 3` routing.
/// </summary>
public sealed class ShardOptions
{
    public string[] ConnectionStrings { get; set; } = [];
}
