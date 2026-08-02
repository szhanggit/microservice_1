namespace TransactionService.Infrastructure.Options;

public sealed class RedisOptions
{
    /// <summary>
    /// This service's own ElastiCache endpoint - separate from the Gateway's
    /// Redis (TransactionService.md §2).
    /// </summary>
    public string ConnectionString { get; set; } = "localhost:6380";
}
