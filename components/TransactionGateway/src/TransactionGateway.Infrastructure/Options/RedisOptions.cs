namespace TransactionGateway.Infrastructure.Options;

public sealed class RedisOptions
{
    /// <summary>
    /// This service's own ElastiCache endpoint (TransactionGateway.md §6) -
    /// separate from TransactionService's Redis, since each guards a
    /// different retry boundary.
    /// </summary>
    public string ConnectionString { get; set; } = "localhost:6379";
}
