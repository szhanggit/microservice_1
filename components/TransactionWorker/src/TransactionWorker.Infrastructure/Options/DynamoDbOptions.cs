namespace TransactionWorker.Infrastructure.Options;

public sealed class DynamoDbOptions
{
    /// <summary>The `transaction-claims` table (KEDA.md §5).</summary>
    public string TableName { get; set; } = "transaction-claims";

    public int LeaseDurationSeconds { get; set; } = 30;

    /// <summary>DynamoDB TTL for COMPLETED items, so the demo table stays visibly clean (KEDA.md §5).</summary>
    public int CompletedTtlSeconds { get; set; } = 300;
}
