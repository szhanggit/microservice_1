namespace TransactionService.Infrastructure.Options;

/// <summary>
/// Read-only connection string per shard, ordinal-indexed (index 0 = shard 0,
/// etc.) - matches database.md §4's fixed 3-shard `mod 3` routing. Uses a
/// separate read-only Postgres role from TransactionWorker's write-capable
/// credentials (TransactionService.md §2/§6).
/// </summary>
public sealed class ShardOptions
{
    public string[] ConnectionStrings { get; set; } = [];
}
