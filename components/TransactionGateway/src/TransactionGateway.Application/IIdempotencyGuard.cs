namespace TransactionGateway.Application;

/// <summary>
/// Best-effort dedup on transaction_no (TransactionGateway.md §2). Callers
/// must treat any exception as "fail open" - correctness never depends on
/// this cache, it's a latency/cost optimization layered on top of the real
/// idempotency guarantees further downstream (the DB's unique constraint,
/// DynamoDB claim).
/// </summary>
public interface IIdempotencyGuard
{
    Task<bool> HasBeenProcessedAsync(string transactionNo, CancellationToken cancellationToken);

    Task MarkProcessedAsync(string transactionNo, CancellationToken cancellationToken);
}
